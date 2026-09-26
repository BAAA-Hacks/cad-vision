#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Memory
{
    // A host-owned transaction around the memory primitive: multi-object writes publish once.
    public sealed class MemoryAccess
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly IMemoryPersistence disk;
        private string? committed;
        private JObject document;
        private ProjectMemoryStore store;
        public ProjectSnapshot Snapshot { get; }
        public ProjectAssociation Association { get; }
        private sealed class Staged : IMemoryPersistence
        {
            internal string? Content;
            public string? Read() => Content;
            public void Commit(string? expected, string next) { if (expected != Content) throw new MemoryPersistenceConflictException("Staging conflict."); Content = next; }
        }
        private MemoryAccess(ProjectSnapshot snapshot, ProjectAssociation association, IMemoryPersistence disk)
        {
            Snapshot = snapshot; Association = association; this.disk = disk; committed = disk.Read();
            document = committed == null ? new JObject { ["schemaVersion"] = "1", ["projectId"] = association.ProjectId, ["revision"] = 0L, ["memory"] = null, ["operations"] = new JArray() } : Parse(committed);
            if (document.Count != 5 || (string?)document["schemaVersion"] != "1" || (string?)document["projectId"] != association.ProjectId || document["revision"]?.Type != JTokenType.Integer
                || !(document["operations"] is JArray ops) || ops.Count > 10000 || (long)document["revision"]! != ops.Count
                || document["memory"] == null || (document["memory"]!.Type != JTokenType.Null && !(document["memory"] is JObject))) throw new InvalidDataException("Invalid or wrong-project memory tool journal; not overwritten.");
            var ids = new HashSet<string>(StringComparer.Ordinal); long revision = 0;
            foreach (var op in ops)
            {
                if (!(op is JObject row) || row.Count != 3 || !(op["request"] is JObject request) || !(op["result"] is JObject result)
                    || (string?)request["projectId"] != association.ProjectId || request["operationId"]?.Type != JTokenType.String || !ids.Add((string)request["operationId"]!)
                    || (string?)op["fingerprint"] != Canon(request) || (long?)result["receipt"]?["revision"] != ++revision
                    || (string?)result["receipt"]?["operationId"] != (string?)request["operationId"] || (string?)result["receipt"]?["subsystem"] != "memory"
                    || (bool?)result["receipt"]?["applied"] != true || (bool?)result["receipt"]?["replayed"] != false) throw new InvalidDataException("Corrupt memory action receipt; not overwritten.");
            }
            store = OpenStore(new Staged { Content = document["memory"]!.Type == JTokenType.Null ? null : document["memory"]!.ToString(Formatting.None) });
        }
        public static MemoryAccess Open(ProjectSnapshot snapshot, ProjectAssociation association, IMemoryPersistence storage) => new MemoryAccess(snapshot, association, storage);
        private static JObject Parse(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > 10000000) throw new InvalidDataException("Memory tool journal exceeds 10 MB.");
            using var input = new StringReader(text); using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Trailing memory journal content."); return value;
        }
        private ProjectMemoryStore OpenStore(Staged staging)
        {
            var result = ProjectMemoryStore.Open(staging, Association, Snapshot);
            if (!result.Success) throw new ToolInputException(result.ErrorCode!, result.Message!); return result.Value!;
        }
        private static string Canon(JToken token) => IssueValidation.Canonical(token);
        private JObject? Replay(JObject args)
        {
            var op = ((JArray)document["operations"]!).FirstOrDefault(o => (string?)o["request"]?["operationId"] == (string?)args["operationId"]);
            if (op == null) return null;
            if ((string?)op["fingerprint"] != Canon(args)) throw new ToolInputException("IDEMPOTENCY_CONFLICT", "operationId already belongs to different arguments.");
            var result = (JObject)op["result"]!.DeepClone(); result["receipt"]!["replayed"] = true; return result;
        }
        internal async Task<JObject?> RecoverAsync(JObject args)
        { await gate.WaitAsync().ConfigureAwait(false); try { return Replay(args); } finally { gate.Release(); } }
        internal async Task<JObject> WriteAsync(JObject args, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var replay = Replay(args); if (replay != null) return replay;
                if ((long)args["expectedRevision"]! != (long)document["revision"]!) throw new ToolInputException("REVISION_CONFLICT", "Refresh get_project_memory for the memory revision.");
                if ((args["value"] == null) == (args["valueJson"] == null)) throw new ToolInputException("INVALID_ARGUMENT", "Supply exactly one of value (text) or valueJson (encoded JSON).");
                JToken value;
                try
                {
                    if (args["valueJson"] == null) value = args["value"]!.DeepClone();
                    else
                    {
                        using var input = new StringReader((string)args["valueJson"]!); using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 };
                        value = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        if (reader.Read()) throw new JsonReaderException("Trailing JSON.");
                    }
                }
                catch (JsonException ex) { throw new ToolInputException("INVALID_ARGUMENT", "valueJson is malformed: " + ex.Message); }
                if (value.Type == JTokenType.Null) throw new ToolInputException("INVALID_ARGUMENT", "Null is not a memory value or deletion; use lifecycle Retired.");
                if (args["context"] != null) value = new JObject { ["content"] = value, ["context"] = args["context"]!.DeepClone() };
                var ids = ((JArray)args["targetObjectIds"]!).Select(v => (string)v!).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray();
                var lifecycle = Enum.Parse<MemoryLifecycle>((string?)args["lifecycle"] ?? "Active");
                var staged = new Staged { Content = document["memory"]!.Type == JTokenType.Null ? null : document["memory"]!.ToString(Formatting.None) };
                var nextStore = OpenStore(staged); var recordIds = new JArray(); int index = 0;
                MemoryWriteContext Context()
                {
                    using var sha = SHA256.Create(); string id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes((string)args["operationId"]! + ":" + index++))).Replace("-", "");
                    // Model calls cannot grant themselves user-established authority.
                    return new MemoryWriteContext(id, nextStore.Revision, MemoryProvenance.AssistantInferred, "gemini");
                }
                void Accept(MemoryResult<MemoryReceipt> result) { if (!result.Success) throw new ToolInputException(result.ErrorCode!, result.Message!); recordIds.Add(result.Value!.RecordId); }
                if ((string?)args["kind"] == "requirement") Accept(nextStore.UpsertRequirement(new RequirementMutation((string)args["key"]!, (string)args["type"]!, value, ids, lifecycle), Context()));
                else foreach (string? id in ids.Length == 0 ? new string?[] { null } : ids)
                { token.ThrowIfCancellationRequested(); Accept(nextStore.UpsertMemory(new MemoryMutation((string)args["type"]!, (string)args["key"]!, value, id, lifecycle), Context())); }
                long revision = checked((long)document["revision"]! + 1);
                var result = new JObject { ["recordIds"] = recordIds, ["revision"] = revision, ["provenance"] = "AssistantInferred",
                    ["receipt"] = new JObject { ["operationId"] = args["operationId"]!.DeepClone(), ["subsystem"] = "memory", ["revision"] = revision, ["applied"] = true, ["replayed"] = false } };
                var next = (JObject)document.DeepClone(); next["revision"] = revision; next["memory"] = Parse(staged.Content!);
                ((JArray)next["operations"]!).Add(new JObject { ["request"] = args.DeepClone(), ["fingerprint"] = Canon(args), ["result"] = result.DeepClone() });
                string serialized = next.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(serialized) > 10000000 || ((JArray)next["operations"]!).Count > 10000) throw new ToolInputException("QUERY_TOO_LARGE", "Memory tool journal is full; no write committed.");
                token.ThrowIfCancellationRequested();
                try { disk.Commit(committed, serialized); }
                catch (MemoryPersistenceConflictException ex) { throw new ToolInputException("REVISION_CONFLICT", ex.Message); }
                catch (Exception ex)
                {
                    var diagnostic = Core.Diagnostics.DiagnosticLog.Report(ex, "memory.tools.commit", Association.ProjectId, Snapshot.SnapshotId);
                    throw new ToolInputException("PERSISTENCE_FAILED", "Memory was not saved. Diagnostic ID: " + diagnostic.Entry.CorrelationId);
                }
                committed = serialized; document = next; store = nextStore; return result;
            }
            finally { gate.Release(); }
        }
        internal async Task<JObject> ReadAsync(JObject args, QueryCursors cursors, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var ids = ((JArray?)args["objectIds"] ?? new JArray()).Select(v => (string)v!).ToArray();
                if ((bool?)args["allObjectScopes"] == true)
                {
                    if (args["objectIds"] != null || !(args["keys"] is JArray keys) || keys.Count == 0 || keys.Any(k => string.IsNullOrWhiteSpace((string?)k)))
                        throw new ToolInputException("INVALID_ARGUMENT", "allObjectScopes requires nonempty exact keys and cannot be combined with objectIds.");
                    ids = store.ReferencedObjectIds().ToArray();
                }
                bool project = (bool?)args["includeProjectScope"] ?? true, stale = (bool?)args["includeStale"] ?? false, retired = (bool?)args["includeRetired"] ?? false;
                var records = new List<JObject>();
                void Collect(Func<int, MemoryPage> fetch, string kind)
                {
                    int offset = 0; do { token.ThrowIfCancellationRequested(); var page = fetch(offset); foreach (JObject row in page.Records) { row["kind"] = kind; records.Add(row); } if (page.NextOffset == null) break; offset = page.NextOffset.Value; } while (true);
                }
                // Read complete selected stored scope before filtering; coverage never claims CAD completeness.
                if (project) Collect(o => store.GetProjectMemory(includeRetired: true, offset: o, limit: 100), "memory");
                Collect(o => store.GetObjectMemory(ids, includeStale: true, includeRetired: true, offset: o, limit: 100), "memory");
                Collect(o => store.GetRequirements(ids, includeProjectScope: project, includeStale: true, includeRetired: true, offset: o, limit: 100), "requirement");
                bool Match(JObject r) => (retired || (string?)r["lifecycle"] == "Active") && (stale || ((JArray)r["references"]!).All(v => (string?)v["referenceState"] == "Active"))
                    && (args["types"] == null || ((JArray)args["types"]!).Any(v => (string?)v == (string?)r["type"]))
                    && (args["keys"] == null || ((JArray)args["keys"]!).Any(v => (string?)v == (string?)(r["key"] ?? r["id"])))
                    && (args["kind"] == null || (string?)args["kind"] == (string?)r["kind"]);
                var rows = records.Where(Match).OrderBy(r => (string)r["id"]!, StringComparer.Ordinal).ToArray(); long revision = (long)document["revision"]!;
                int limit = (int?)args["limit"] ?? 20, start = cursors.Resolve("get_project_memory", Association.ProjectId, Snapshot.SnapshotId, args, "memory", revision);
                return new JObject { ["revision"] = revision, ["items"] = new JArray(rows.Skip(start).Take(limit)),
                    ["coverage"] = new JObject { ["status"] = "complete", ["countUnit"] = "memory_records", ["countScope"] = "selected_stored_scope_before_filters", ["requestedCount"] = records.Count, ["evaluatedCount"] = records.Count, ["limitation"] = "Stored CADEN knowledge only, not complete design intent or exported CAD facts." },
                    ["pagination"] = new JObject { ["limit"] = limit, ["total"] = rows.Length, ["nextCursor"] = start + limit < rows.Length ? cursors.Issue("get_project_memory", Association.ProjectId, Snapshot.SnapshotId, args, start + limit, "memory", revision) : null } };
            }
            finally { gate.Release(); }
        }
    }
}
