using System;
using System.Collections.Generic;
using System.Linq;
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

    public sealed class ToolRegistry
    {
        public const string ContractVersion = "1.0";
        private readonly Dictionary<string, ICadenTool> tools = new Dictionary<string, ICadenTool>(StringComparer.Ordinal);
        public ToolRegistry(IEnumerable<ICadenTool> handlers)
        {
            foreach (var tool in handlers) tools.Add(tool.Name, tool);
        }
        public JArray Declarations => new JArray(tools.Values.Select(t => t.Declaration.DeepClone()));
        public static JObject Error(string code, string message) => new JObject
        {
            ["contractVersion"] = ContractVersion, ["ok"] = false,
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        public JObject Execute(string name, JToken? arguments)
        {
            if (!tools.TryGetValue(name, out var tool)) return Error("UNKNOWN_TOOL", "That tool is not available.");
            if (!(arguments is JObject args)) return Error("INVALID_ARGUMENTS", "Arguments must be a JSON object.");
            try
            {
                var schema = tool.Declaration["parameters"]!;
                var properties = (JObject)schema["properties"]!;
                foreach (var required in (JArray?)schema["required"] ?? new JArray())
                    if (args[(string)required!] == null) throw new ToolInputException("INVALID_ARGUMENTS", "Missing argument: " + required);
                foreach (var p in args.Properties())
                {
                    var rule = properties[p.Name] ?? throw new ToolInputException("INVALID_ARGUMENTS", "Unexpected argument: " + p.Name);
                    string type = (string)rule["type"]!;
                    bool valid = type == "string" ? p.Value.Type == JTokenType.String && ((string)p.Value!).Length <= 512
                        : type == "integer" ? p.Value.Type == JTokenType.Integer && p.Value.Value<double>() >= 0 && p.Value.Value<double>() <= 100000
                        : type == "array" && p.Value is JArray a && a.Count <= 32 && a.All(v => v.Type == JTokenType.String && ((string)v!).Length <= 128);
                    if (!valid) throw new ToolInputException("INVALID_ARGUMENTS", "Unexpected format for argument: " + p.Name);
                    if (rule["enum"] is JArray allowed && !allowed.Any(v => JToken.DeepEquals(v, p.Value)))
                        throw new ToolInputException("INVALID_ARGUMENTS", "Unsupported value for argument: " + p.Name);
                }
                var result = tool.Execute(args);
                var envelope = new JObject { ["contractVersion"] = ContractVersion, ["ok"] = true, ["data"] = result };
                if (envelope.ToString(Formatting.None).Length > 64000)
                    return Error("RESULT_TOO_LARGE", "Request fewer fields or a smaller page. Maximum tool response is 64,000 characters.");
                return envelope;
            }
            catch (ToolInputException ex) { return Error(ex.Code, ex.Message); }
            catch (Exception) { return Error("TOOL_EXECUTION_FAILED", "The query could not be completed. No result should be inferred; inspect the metadata contract or report the failure."); }
        }
    }
}
