using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Project;
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

    public sealed class ToolRegistry
    {
        public const string ContractVersion = "1.0";
        private readonly Dictionary<string, ICadenTool> tools = new Dictionary<string, ICadenTool>(StringComparer.Ordinal);
        private readonly ProjectSnapshot? snapshot;
        private readonly bool semantic;
        public ToolRegistry(IEnumerable<ICadenTool> handlers, ProjectSnapshot? snapshot = null, bool semantic = false)
        {
            this.snapshot = snapshot; this.semantic = semantic;
            foreach (var tool in handlers) tools.Add(tool.Name, tool);
        }
        public JArray Declarations => new JArray(tools.Values.Select(t => t.Declaration.DeepClone()));
        public static JObject Error(string code, string message) => new JObject
        {
            ["contractVersion"] = ContractVersion, ["ok"] = false,
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        private JObject Envelope(JObject result)
        {
            if (semantic)
            {
                result["contractVersion"] = "2.0";
                result["context"] = new JObject { ["projectId"] = snapshot?.ProjectId, ["snapshotId"] = snapshot?.SnapshotId,
                    ["identityScope"] = snapshot?.IdentityScope.ToString(), ["fixture"] = snapshot == null ? JValue.CreateNull() : new JValue(snapshot.IsFixture) };
            }
            return result;
        }
        public JObject Execute(string name, JToken? arguments) => ExecuteAsync(name, arguments, CancellationToken.None).GetAwaiter().GetResult();
        public async Task<JObject> ExecuteAsync(string name, JToken? arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tools.TryGetValue(name, out var tool)) return Envelope(Error("UNKNOWN_TOOL", "That tool is not available."));
            if (!(arguments is JObject args)) return Envelope(Error("INVALID_ARGUMENTS", "Arguments must be a JSON object."));
            try
            {
                if (args.ToString(Formatting.None).Length > 64000) throw new ToolInputException("INVALID_ARGUMENTS", "Arguments exceed 64,000 characters.");
                Validate(args, (JObject)tool.Declaration["parameters"]!, "arguments", 0);
                if (semantic && snapshot == null) throw new ToolInputException("MODEL_NOT_LOADED", "Load metadata before querying the model.");
                if (semantic && name != "get_model_summary" && (string?)args["snapshotId"] != snapshot!.SnapshotId)
                    throw new ToolInputException("STALE_SNAPSHOT", "Refresh get_model_summary and resolve IDs against the current snapshot.");
                var copied = (JObject)args.DeepClone();
                var result = tool is IAsyncCadenTool asyncTool ? await asyncTool.ExecuteAsync(copied, cancellationToken).ConfigureAwait(false) : tool.Execute(copied);
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = Envelope(new JObject { ["contractVersion"] = ContractVersion, ["ok"] = true, ["data"] = result });
                if (envelope.ToString(Formatting.None).Length > 64000)
                    return Envelope(Error("RESULT_TOO_LARGE", "Request fewer fields or a smaller page. Maximum tool response is 64,000 characters."));
                return envelope;
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolInputException ex) { return Envelope(Error(ex.Code, ex.Message)); }
            catch (Exception) { return Envelope(Error("TOOL_EXECUTION_FAILED", "The query could not be completed. No result should be inferred; inspect the metadata contract or report the failure.")); }
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
                if (array.Count < ((int?)schema["minItems"] ?? 0) || array.Count > ((int?)schema["maxItems"] ?? 32)) Invalid("Array size out of range.");
                foreach (var item in array) Validate(item, (JObject?)schema["items"] ?? new JObject { ["type"] = "string", ["maxLength"] = 128 }, path + "[]", depth + 1);
            }
            else if (type == "string" && value.Type == JTokenType.String)
            {
                if (((string)value!).Length > ((int?)schema["maxLength"] ?? 512) || ((string)value!).Length < ((int?)schema["minLength"] ?? 0)) Invalid("String length out of range.");
            }
            else if ((type == "number" || type == "integer") && (value.Type == JTokenType.Integer || type == "number" && value.Type == JTokenType.Float))
            {
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number) || number < ((double?)schema["minimum"] ?? (type == "integer" ? 0 : -double.MaxValue)) || number > ((double?)schema["maximum"] ?? (type == "integer" ? 100000 : double.MaxValue))) Invalid("Number out of range.");
            }
            else if (!(type == "boolean" && value.Type == JTokenType.Boolean)) Invalid("Expected " + type);
            if (schema["enum"] is JArray allowed && !allowed.Any(v => JToken.DeepEquals(v, value))) Invalid("Unsupported value.");
        }
    }
}
