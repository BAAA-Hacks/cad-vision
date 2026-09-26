using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Project;
using Core.Diagnostics;
using Core.Primitives.DataStructures.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools
{
    public sealed class ToolInputException : Exception
    {
        public string Code { get; }
        public ToolInputException(string code, string message) : base(message) { Code = code; }
    }

    public interface ICadenTool
    {
        string Name { get; }
        JObject Declaration { get; }
        JObject Execute(JObject arguments);
    }

    public interface IAsyncCadenTool : ICadenTool
    {
        Task<JObject> ExecuteAsync(JObject arguments, CancellationToken cancellationToken);
    }
    public interface ICapabilityCadenTool : ICadenTool { bool Available { get; } }
    public interface IActionCadenTool : IAsyncCadenTool
    {
        // Must validate operation ID + original request content, and return the committed result
        // with receipt.replayed=true. Recovery must not execute an uncommitted action.
        Task<JObject?> RecoverCommittedAsync(JObject arguments);
    }

    public sealed class ToolRegistry
    {
        public const string ContractVersion = "1.0";
        private readonly Dictionary<string, ICadenTool> tools = new Dictionary<string, ICadenTool>(StringComparer.Ordinal);
        private readonly ProjectSnapshot? snapshot;
        private readonly bool semantic;
        private readonly ProjectAssociation? association;
        private readonly ToolLimits limits;
        private readonly ToolCapabilities? capabilities;
        public string? ProjectId => semantic ? association?.ProjectId : snapshot?.ProjectId;
        public string? SnapshotId => snapshot?.SnapshotId;
        public bool SupportsStartupContext => capabilities != null;
        public ToolRegistry(IEnumerable<ICadenTool> handlers, ProjectSnapshot? snapshot = null, bool semantic = false, ProjectAssociation? association = null, ToolLimits? limits = null, ToolCapabilities? capabilities = null)
        {
            if (semantic && snapshot != null && association == null) throw new ArgumentException("Semantic tools require an explicit host ProjectAssociation.");
            if (association?.TrustedSourceProjectId != null && snapshot != null && association.TrustedSourceProjectId != snapshot.ProjectId) throw new ArgumentException("Trusted source project does not match snapshot.");
            this.association = association;
            this.capabilities = capabilities;
            this.limits = limits ?? new ToolLimits();
            this.snapshot = snapshot; this.semantic = semantic;
            foreach (var tool in handlers) tools.Add(tool.Name, tool);
        }
        public JArray Declarations => new JArray(tools.Values.Where(t => (capabilities?.IsAvailable(t.Name) ?? true) && (!(t is ICapabilityCadenTool c) || c.Available)).Select(t => t.Declaration.DeepClone()));
        public static JObject Error(string code, string message) => new JObject
        {
            ["contractVersion"] = ContractVersion, ["ok"] = false,
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        private JObject Envelope(JObject result)
        {
            return semantic ? ToolResponseContract.Wrap(result, association, snapshot) : result;
        }
        public JObject Execute(string name, JToken? arguments) => ExecuteAsync(name, arguments, CancellationToken.None).GetAwaiter().GetResult();
        public async Task<JObject> ExecuteAsync(string name, JToken? arguments, CancellationToken cancellationToken = default)
        {
            if (!semantic) cancellationToken.ThrowIfCancellationRequested();
            if (!tools.TryGetValue(name, out var tool)) return Envelope(capabilities?.Failure(name) ?? Error("UNKNOWN_TOOL", "That tool is not available."));
            if (!(arguments is JObject args)) return Envelope(Error("INVALID_ARGUMENTS", "Arguments must be a JSON object."));
            try
            {
                if (args.ToString(Formatting.None).Length > 64000) throw new ToolInputException("INVALID_ARGUMENTS", "Arguments exceed 64,000 characters.");
                Validate(args, (JObject)tool.Declaration["parameters"]!, "arguments", 0);
                if (tool is IActionCadenTool && (args["operationId"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)args["operationId"])))
                    throw new ToolInputException("INVALID_ARGUMENT", "Actions require a non-empty operationId.");
                if (semantic && snapshot == null && name != "get_model_summary") return Envelope(capabilities?.Failure(name) ?? Error("MODEL_NOT_LOADED", "Load metadata before querying the model."));
                if (semantic && name != "get_model_summary" && (string?)args["projectId"] != association!.ProjectId)
                    throw new ToolInputException("WRONG_PROJECT", "Request does not reference the active CADEN project.");
                if (semantic && name != "get_model_summary" && (string?)args["snapshotId"] != snapshot!.SnapshotId)
                    throw new ToolInputException("STALE_SNAPSHOT", "Refresh get_model_summary and resolve IDs against the current snapshot.");
                var unavailable = capabilities?.Failure(name, args);
                if (unavailable != null) return Envelope(unavailable);
                var copied = (JObject)args.DeepClone();
                if (cancellationToken.IsCancellationRequested && tool is IActionCadenTool recovering)
                {
                    var recovered = await recovering.RecoverCommittedAsync(copied).ConfigureAwait(false);
                    if (recovered != null) return ActionEnvelope(recovered, (string)args["operationId"]!);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = name == "get_model_summary" && snapshot == null && capabilities != null ? new JObject { ["modelLoaded"] = false }
                    : tool is IAsyncCadenTool asyncTool ? await asyncTool.ExecuteAsync(copied, cancellationToken).ConfigureAwait(false) : tool.Execute(copied);
                if (name == "get_model_summary" && capabilities != null)
                {
                    result["modelLoaded"] = snapshot != null; result["toolCapabilities"] = capabilities.Describe();
                    result["capabilityPolicy"] = "Usable means the handler can run, not complete evidence. Inspect response coverage and property availability. Unavailable requirements need host/data changes; do not retry unchanged requests.";
                }
                if (tool is IActionCadenTool) return ActionEnvelope(result, (string)args["operationId"]!);
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = Envelope(new JObject { ["contractVersion"] = ContractVersion, ["ok"] = true, ["data"] = result });
                if (envelope.ToString(Formatting.None).Length > limits.MaxResponseCharacters)
                    return Envelope(Error("RESULT_TOO_LARGE", "Request fewer fields or a smaller page. Response exceeds configured size limit."));
                return envelope;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!semantic) throw;
                if (tool is IActionCadenTool recovering)
                {
                    try { var recovered = await recovering.RecoverCommittedAsync((JObject)args.DeepClone()).ConfigureAwait(false); if (recovered != null) return ActionEnvelope(recovered, (string)args["operationId"]!); }
                    catch (Exception ex)
                    {
                        var d = DiagnosticLog.Report(ex, "action.recover." + name, ProjectId, SnapshotId);
                        var failure = Error("INTERNAL_ERROR", "Could not establish commit status. Recover using the same operationId; do not invent a new ID.");
                        failure["error"]!["correlationId"] = d.Entry.CorrelationId; return Envelope(failure);
                    }
                }
                return Envelope(Error("CANCELLED", "Execution was cancelled before a committed result was returned."));
            }
            catch (ToolInputException ex) { return Envelope(Error(ex.Code, ex.Message)); }
            catch (Exception ex)
            {
                var diagnostic = DiagnosticLog.Report(ex, "tool." + name, ProjectId, snapshot?.SnapshotId);
                if (tool is IActionCadenTool action)
                {
                    try { var recovered = await action.RecoverCommittedAsync((JObject)args.DeepClone()).ConfigureAwait(false); if (recovered != null) return ActionEnvelope(recovered, (string)args["operationId"]!); }
                    catch (Exception recoveryError) { DiagnosticLog.Report(recoveryError, "action.recover." + name, ProjectId, SnapshotId); }
                }
                var error = Error("TOOL_EXECUTION_FAILED", "Unexpected " + diagnostic.Entry.ExceptionType + " in tool " + name + ". No result should be inferred. Diagnostic ID: " + diagnostic.Entry.CorrelationId);
                error["error"]!["correlationId"] = diagnostic.Entry.CorrelationId;
                return Envelope(error);
            }
        }
        private JObject ActionEnvelope(JObject result, string expectedOperationId)
        {
            if (!(result["receipt"] is JObject r) || r["operationId"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)r["operationId"]) || (string?)r["operationId"] != expectedOperationId || ((string)r["operationId"]!).Length > 512
                || r["subsystem"]?.Type != JTokenType.String || !new[] { "memory", "issues", "view" }.Contains((string)r["subsystem"]!)
                || r["revision"]?.Type != JTokenType.Integer || !long.TryParse(r["revision"]!.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) || r["applied"]?.Type != JTokenType.Boolean || !(bool)r["applied"]!
                || r["replayed"]?.Type != JTokenType.Boolean || r.Count != 5)
                throw new InvalidOperationException("Committed actions require a bounded receipt: operationId, subsystem, revision, applied=true, replayed.");
            var envelope = Envelope(new JObject { ["ok"] = true, ["data"] = result });
            if (envelope.ToString(Formatting.None).Length > limits.MaxResponseCharacters)
            {
                // Preserve the durable acknowledgement even if optional result details are oversized.
                envelope["data"] = new JObject { ["detailsOmitted"] = true, ["reasonCode"] = "QUERY_TOO_LARGE" };
                envelope["coverage"] = null; envelope["pagination"] = null;
            }
            return envelope;
        }
        private static void Validate(JToken value, JObject schema, string path, int depth)
        {
            void Invalid(string reason) => throw new ToolInputException("INVALID_ARGUMENTS", path + ": " + reason);
            if (depth > 12) Invalid("Excessive nesting.");
            if (value.Type == JTokenType.Null && (bool?)schema["nullable"] == true) return;
            string? type = (string?)schema["type"];
            if (type == "object" && value is JObject obj)
            {
                var properties = (JObject?)schema["properties"] ?? new JObject();
                foreach (var required in (JArray?)schema["required"] ?? new JArray())
                    if (obj[(string)required!] == null) Invalid("Missing " + required);
                foreach (var p in obj.Properties())
                {
                    if (!(properties[p.Name] is JObject rule)) { Invalid("Unexpected field " + p.Name); return; }
                    Validate(p.Value, rule, path + "." + p.Name, depth + 1);
                }
            }
            else if (type == "array" && value is JArray array)
            {
                if (array.Count > ((int?)schema["maxItems"] ?? 32)) throw new ToolInputException("QUERY_TOO_LARGE", path + ": array exceeds safety bound.");
                if (array.Count < ((int?)schema["minItems"] ?? 0)) Invalid("Array size out of range.");
                foreach (var item in array) Validate(item, (JObject?)schema["items"] ?? new JObject { ["type"] = "string", ["maxLength"] = 128 }, path + "[]", depth + 1);
            }
            else if (type == "string" && value.Type == JTokenType.String)
            {
                if (((string)value!).Length > ((int?)schema["maxLength"] ?? 512) || ((string)value!).Length < ((int?)schema["minLength"] ?? 0)) Invalid("String length out of range.");
            }
            else if ((type == "number" || type == "integer") && (value.Type == JTokenType.Integer || type == "number" && value.Type == JTokenType.Float))
            {
                double number = (double)value;
                if ((path == "arguments.maxDepth" || path == "arguments.maxHops") && number > ((double?)schema["maximum"] ?? 128)) throw new ToolInputException("MAX_DEPTH_EXCEEDED", "Requested depth exceeds the configured bound.");
                if (double.IsNaN(number) || double.IsInfinity(number) || number < ((double?)schema["minimum"] ?? (type == "integer" ? 0 : -double.MaxValue)) || number > ((double?)schema["maximum"] ?? (type == "integer" ? 100000 : double.MaxValue))) Invalid("Number out of range.");
            }
            else if (!(type == "boolean" && value.Type == JTokenType.Boolean)) Invalid("Expected " + type);
            if (schema["enum"] is JArray allowed && !allowed.Any(v => JToken.DeepEquals(v, value))) Invalid("Unsupported value.");
        }
    }
}
