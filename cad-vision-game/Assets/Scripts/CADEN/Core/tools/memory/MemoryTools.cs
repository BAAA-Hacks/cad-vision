#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Memory
{
    internal class MemoryReadTool : IAsyncCadenTool
    {
        protected readonly MemoryAccess access;
        private readonly QueryCursors cursors = new QueryCursors();
        protected readonly ToolLimits limits;
        public virtual string Name => "get_project_memory";
        internal MemoryReadTool(MemoryAccess access, ToolLimits limits) { this.access = access; this.limits = limits; }
        protected static JObject Text(params string[] values) => values.Length == 0 ? new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 } : new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        private static JObject Strings() => new JObject { ["type"] = "array", ["items"] = Text(), ["maxItems"] = 32 };
        public JObject Declaration
        {
            get
            {
                var fields = new JObject { ["projectId"] = Text(), ["snapshotId"] = Text() }; var required = new JArray("projectId", "snapshotId");
                bool write = Name == "write_project_memory";
                if (write)
                {
                    fields["targetObjectIds"] = Strings(); fields["kind"] = Text("memory", "requirement"); fields["type"] = Text(); fields["key"] = Text();
                    fields["value"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32000 };
                    fields["valueJson"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32000 };
                    fields["context"] = Text();
                    fields["lifecycle"] = Text("Active", "Retired"); fields["operationId"] = Text();
                    fields["expectedRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9007199254740991L };
                    foreach (var key in new[] { "targetObjectIds", "kind", "type", "key", "operationId", "expectedRevision" }) required.Add(key);
                }
                else
                {
                    fields["objectIds"] = Strings(); fields["keys"] = Strings(); fields["types"] = Strings(); fields["kind"] = Text("memory", "requirement");
                    fields["allObjectScopes"] = new JObject { ["type"] = "boolean", ["description"] = "Search exact keys across all object attachments. Requires nonempty keys; cannot combine with objectIds. Use when the memory key is known but the attached object is not." };
                    foreach (var key in new[] { "includeProjectScope", "includeStale", "includeRetired" }) fields[key] = new JObject { ["type"] = "boolean" };
                    fields["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = System.Math.Min(100, limits.MaxResults) }; fields["cursor"] = Text();
                }
                return new JObject { ["name"] = Name, ["description"] = write
                    ? "Persist CADEN knowledge separately from CAD metadata and issue disposition. Supply value (text) OR valueJson (encoded structured JSON), never both/null. Empty targetObjectIds is project scope. Memory writes the same type/key slot atomically for every target; requirement writes one multi-object requirement with key as its stable ID. Lifecycle Active/Retired preserves history. Host labels Gemini writes AssistantInferred; cannot overwrite UserEstablished knowledge. Use memory revision from get_project_memory; exact retries reuse operationId and all arguments."
                    : "Read stored intent/requirements; this does not establish CAD facts. For a known key with unknown attachment use keys plus allObjectScopes=true directly; no object discovery is needed. Otherwise omitted objectIds means project only; explicit IDs are exact scopes. Project scope included by default. Requirement key is its ID. Stale/retired records excluded unless requested. Inspect provenance/reference validity. Pagination binds query and memory revision.",
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = fields, ["required"] = required } };
            }
        }
        public JObject Execute(JObject args) => ExecuteAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        public virtual Task<JObject> ExecuteAsync(JObject args, CancellationToken token) => access.ReadAsync(args, cursors, token);
    }
    internal sealed class MemoryWriteTool : MemoryReadTool, IActionCadenTool
    {
        public override string Name => "write_project_memory";
        internal MemoryWriteTool(MemoryAccess access, ToolLimits limits) : base(access, limits) { }
        public override Task<JObject> ExecuteAsync(JObject args, CancellationToken token) => access.WriteAsync(args, token);
        public Task<JObject?> RecoverCommittedAsync(JObject args) => access.RecoverAsync(args);
    }
    internal static class MemoryTools
    {
        internal static IEnumerable<ICadenTool> Create(MemoryAccess? access, ToolLimits limits)
        { if (access == null) yield break; yield return new MemoryReadTool(access, limits); yield return new MemoryWriteTool(access, limits); }
    }
}
