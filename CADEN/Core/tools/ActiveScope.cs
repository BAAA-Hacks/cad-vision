using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;

namespace Core.Tools
{
    public sealed class ScopeContext
    {
        private readonly HashSet<string> ids;
        public string RootObjectId { get; }
        public string DisplayName { get; }
        public string Type { get; }
        public string ScopeId { get; } = Guid.NewGuid().ToString("N");
        public long Revision { get; }
        public string ProjectId { get; }
        public string SnapshotId { get; }
        public int ObjectCount => ids.Count;
        internal IEnumerable<string> ObjectIds => ids;
        public bool Includes(string id) => ids.Contains(id);
        internal ScopeContext(string root, string name, string type, IEnumerable<string> ids, long revision, string project, string snapshot)
        { RootObjectId = root; DisplayName = name; Type = type; this.ids = new HashSet<string>(ids, StringComparer.Ordinal); Revision = revision; ProjectId = project; SnapshotId = snapshot; }
        public JObject Describe() => new JObject { ["active"] = true, ["scopeId"] = ScopeId, ["revision"] = Revision, ["rootId"] = RootObjectId,
            ["displayName"] = DisplayName, ["type"] = Type, ["objectCount"] = ObjectCount, ["projectId"] = ProjectId, ["snapshotId"] = SnapshotId };
        public string Classify(string a, string b) => Includes(a) && Includes(b) ? "Internal" : Includes(a) || Includes(b) ? "Boundary" : "External";
        internal static ScopeContext? From(JObject args) => args.Annotation<ScopeContext>();
    }

    public sealed class ScopeManager
    {
        private static readonly HashSet<string> ScopedTools = new HashSet<string>(new[] {
            "get_object_details", "find_objects", "query_hierarchy", "get_mates", "find_connections", "get_mechanical_neighborhood", "find_mechanical_path",
            "get_issue", "list_issues", "get_issues_for_object", "get_issues_for_mate", "get_issue_summary", "get_issue_evidence",
            "revalidate_issue", "revalidate_object_issues", "set_issue_disposition" }, StringComparer.Ordinal);
        internal static string Behavior(string name) => name == "get_model_summary" ? "global_discovery" : name == "get_scope" || name == "set_scope" || name == "clear_scope" ? "scope_control" : ScopedTools.Contains(name) ? "scoped" : "unsupported";
        private readonly ProjectSnapshot? snapshot;
        private readonly string? project;
        private long revision;
        public ScopeContext? Active { get; private set; }
        internal ScopeManager(ProjectSnapshot? snapshot, string? project) { this.snapshot = snapshot; this.project = project; }
        public JObject Describe() => Active?.Describe() ?? new JObject { ["active"] = false, ["scopeId"] = null, ["revision"] = revision,
            ["type"] = "WholeModel", ["projectId"] = project, ["snapshotId"] = snapshot?.SnapshotId, ["objectCount"] = snapshot?.ComponentsById.Count };
        internal void Clear() { Active = null; revision++; }
        internal void Set(string id, CancellationToken token)
        {
            if (snapshot == null) throw new ToolInputException("MODEL_NOT_LOADED", "Load metadata first.");
            if (!snapshot.ComponentsById.TryGetValue(id, out var root)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Resolve an exact object ID before setting scope.");
            if (snapshot.Capabilities.Hierarchy != CapabilityState.Available) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "Valid authoritative hierarchy is required to resolve scope.");
            var members = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(id);
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string current = queue.Dequeue();
                if (!members.Add(current)) throw new ToolInputException("INVALID_SCOPE_HIERARCHY", "Repeated or cyclic hierarchy reference; previous scope retained.");
                foreach (var child in (JArray)snapshot.ComponentsById[current].CopyRawRecord()["childIds"]!) queue.Enqueue((string)child!);
            }
            token.ThrowIfCancellationRequested();
            string type = root.Type != "assembly" ? "Component" : root.CopyRawRecord()["parentId"]?.Type == JTokenType.String ? "Subassembly" : "WholeAssembly";
            Active = new ScopeContext(id, root.Name, type, members, ++revision, project!, snapshot.SnapshotId);
        }
        internal void Bind(string name, JObject args)
        {
            // Also bind unscoped cursors, preventing clear/reselect from reviving old cursors.
            if (new[] { "find_objects", "find_connections", "query_hierarchy", "get_mates", "list_issues", "get_issues_for_object", "get_issues_for_mate", "get_project_memory", "get_diagnostics" }.Contains(name)) args["__scopeRevision"] = revision;
            var scope = Active; if (scope == null) return;
            args.AddAnnotation(scope);
            if (name == "get_model_summary" || name == "get_scope" || name == "set_scope" || name == "clear_scope") return;
            if (!ScopedTools.Contains(name))
                throw new ToolInputException("SCOPE_NOT_SUPPORTED", "This tool has not adopted active scope. Clear scope explicitly before using its existing global/explicit-target behavior.");
            void Check(string id) { if (snapshot == null || !snapshot.ComponentsById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown object: " + id);
                if (!scope.Includes(id)) throw new ToolInputException("OUT_OF_SCOPE", "Explicit target lies outside active scope: " + id + ". Change or clear scope."); }
            void Mate(string id) { if (!snapshot!.MatesById.TryGetValue(id, out var mate)) throw new ToolInputException("UNKNOWN_MATE_ID", "Unknown mate: " + id);
                if (scope.Classify(mate.ObjectAId, mate.ObjectBId) == "External") throw new ToolInputException("OUT_OF_SCOPE", "Mate is external to active scope. Change or clear scope."); }
            foreach (string key in new[] { "objectId", "startObjectId", "endObjectId" }) if (args[key]?.Type == JTokenType.String) Check((string)args[key]!);
            foreach (string key in new[] { "objectIds", "startObjectIds", "scopeObjectIds", "targetObjectIds" }) if (args[key] is JArray values) foreach (var id in values.Values<string>()) Check(id!);
            if (args["mateId"] != null) Mate((string)args["mateId"]!);
            if (args["filters"] is JObject f) { if (f["objectId"] != null) Check((string)f["objectId"]!); if (f["mateId"] != null) Mate((string)f["mateId"]!); }
        }
    }

    internal sealed class ScopeTool : IAsyncCadenTool
    {
        private readonly ScopeManager manager;
        public string Name { get; }
        internal ScopeTool(string name, ScopeManager manager) { Name = name; this.manager = manager; }
        public JObject Declaration
        {
            get
            {
                JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 };
                var p = new JObject { ["projectId"] = Text(), ["snapshotId"] = Text() }; var required = new JArray("projectId", "snapshotId");
                if (Name == "set_scope") { p["objectId"] = Text(); required.Add("objectId"); }
                return new JObject { ["name"] = Name, ["description"] = Name == "set_scope" ? "Set session query scope to an exact object ID and all descendants. Discover names before calling; resolve ambiguity. Does not mutate CAD or durable memory. Explicit targets outside scope fail. Does not select a mechanical configuration."
                    : Name == "clear_scope" ? "Clear active session scope, restoring original tool behavior. Invalidates scope-bound cursors; does not clear project memory." : "Read current active query scope and its revision. No active scope means original tool defaults.",
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = p, ["required"] = required } };
            }
        }
        public JObject Execute(JObject args) => Run(args, CancellationToken.None);
        public System.Threading.Tasks.Task<JObject> ExecuteAsync(JObject args, CancellationToken token) => System.Threading.Tasks.Task.FromResult(Run(args, token));
        private JObject Run(JObject args, CancellationToken token) { token.ThrowIfCancellationRequested(); if (Name == "set_scope") manager.Set((string)args["objectId"]!, token); else if (Name == "clear_scope") manager.Clear(); return manager.Describe(); }
    }
}
