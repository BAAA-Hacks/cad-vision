#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Core.Diagnostics;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Issues;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.Memory
{
    public sealed partial class ProjectMemoryStore
    {
        private readonly object gate = new object();
        private readonly IMemoryPersistence persistence;
        private readonly ProjectAssociation association;
        private readonly ProjectSnapshot snapshot;
        private readonly IssueStore? issues;
        private readonly bool stableMates;
        private string? committedText;
        private State state;
        public long Revision { get { lock (gate) return (long)state.Document["revision"]!; } }
        public string ProjectId => association.ProjectId;
        // Includes historical attachments, not just IDs in the current snapshot.
        internal IReadOnlyList<string> ReferencedObjectIds()
        { lock (gate) return state.ObjectMemory.Keys.Concat(state.ObjectRequirements.Keys).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(); }

        private sealed class State
        {
            internal JObject Document { get; }
            internal Dictionary<string, JObject> Memory { get; } = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal Dictionary<string, JObject> Requirements { get; } = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal Dictionary<string, JObject> Dispositions { get; } = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal Dictionary<string, JObject> Receipts { get; } = new Dictionary<string, JObject>(StringComparer.Ordinal);
            internal Dictionary<string, HashSet<string>> ObjectMemory { get; } = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            internal Dictionary<string, HashSet<string>> ObjectRequirements { get; } = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            internal HashSet<string> ProjectMemory { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal HashSet<string> ProjectRequirements { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal State(JObject document)
            {
                Document = document;
                foreach (JObject record in ((JArray)document["memory"]!["project"]!).Concat((JArray)document["memory"]!["objects"]!))
                {
                    string slot = Slot((string?)record["objectId"], (string)record["type"]!, (string)record["key"]!); Memory.Add(slot, record);
                    if (record["objectId"]!.Type == JTokenType.Null) ProjectMemory.Add(slot); else Index(ObjectMemory, (string)record["objectId"]!, slot);
                }
                foreach (JObject record in (JArray)document["memory"]!["requirements"]!)
                {
                    string id = (string)record["id"]!; Requirements.Add(id, record);
                    var refs = (JArray)record["references"]!;
                    if (refs.Count == 0) ProjectRequirements.Add(id);
                    foreach (var r in refs) Index(ObjectRequirements, (string)r["id"]!, id);
                }
                foreach (JObject d in (JArray)document["issueState"]!["dispositions"]!) Dispositions.Add(KeyText(ReadKey((JObject)d["issueKey"]!)), d);
                foreach (JObject r in (JArray)document["receipts"]!) Receipts.Add((string)r["operationId"]!, r);
            }
            private static void Index(Dictionary<string, HashSet<string>> index, string id, string key)
            { if (!index.TryGetValue(id, out var set)) index[id] = set = new HashSet<string>(StringComparer.Ordinal); set.Add(key); }
        }
        private ProjectMemoryStore(IMemoryPersistence persistence, ProjectAssociation association, ProjectSnapshot snapshot, IssueStore? issues, bool stableMates, string? text, State state)
        { this.persistence = persistence; this.association = association; this.snapshot = snapshot; this.issues = issues; this.stableMates = stableMates; committedText = text; this.state = state; }

        public static MemoryResult<ProjectMemoryStore> Open(IMemoryPersistence persistence, ProjectAssociation association, ProjectSnapshot snapshot, IssueStore? issues = null, bool mateIdsStableAcrossSnapshots = false)
        {
            if (persistence == null || association == null || snapshot == null) throw new ArgumentNullException("Persistence, association and snapshot are required.");
            try
            {
                if (issues != null && (issues.Snapshot.ProjectId != snapshot.ProjectId || issues.Snapshot.SnapshotId != snapshot.SnapshotId)) throw new ContractException("SNAPSHOT_MISMATCH", "IssueStore must belong to the supplied snapshot.");
                if (association.TrustedSourceProjectId != null && association.TrustedSourceProjectId != snapshot.ProjectId) throw new ContractException("PROJECT_MISMATCH", "Trusted source project does not match this export.");
                string? text = persistence.Read();
                var document = text == null ? NewDocument(association, snapshot) : Parse(text);
                ValidateDocument(document);
                var project = document["project"]!;
                if ((string?)project["projectId"] != association.ProjectId || (string?)project["sourceSystem"] != association.SourceSystem || (string?)project["trustedSourceProjectId"] != association.TrustedSourceProjectId)
                    throw new ContractException("PROJECT_MISMATCH", "Sidecar does not match the host's explicit project association.");
                var store = new ProjectMemoryStore(persistence, association, snapshot, issues, mateIdsStableAcrossSnapshots, text, new State(document));
                // Findings must already have been regenerated. Restore only eligible acceptance;
                // unknown/missing/evidence-changed records remain historical in the sidecar.
                if (issues != null) foreach (var record in store.state.Dispositions.Values)
                {
                    var key = ReadKey((JObject)record["issueKey"]!);
                    if (store.DispositionEligibility(record, key) == "Applicable")
                        issues.RestoreDisposition(key, (string)record["expectedEvidenceHash"]!, Enum.Parse<IssueDispositionState>((string)record["state"]!),
                            (string?)record["reason"], (string)record["originSnapshotId"]!, DateTimeOffset.Parse((string)record["updatedAt"]!, System.Globalization.CultureInfo.InvariantCulture));
                }
                return MemoryResult<ProjectMemoryStore>.Ok(store);
            }
            catch (ContractException ex) { return MemoryResult<ProjectMemoryStore>.Fail(ex.Code, ex.Message); }
            catch (Exception ex)
            {
                var diagnostic = DiagnosticLog.Report(ex, "memory.open", association.ProjectId, snapshot.SnapshotId);
                return MemoryResult<ProjectMemoryStore>.Fail("MEMORY_LOAD_FAILED", "Sidecar was not loaded or overwritten. Diagnostic ID: " + diagnostic.Entry.CorrelationId);
            }
        }
        private static JObject NewDocument(ProjectAssociation association, ProjectSnapshot snapshot)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            return new JObject { ["schemaVersion"] = "1", ["revision"] = 0,
                ["project"] = new JObject { ["projectId"] = association.ProjectId, ["projectName"] = snapshot.Name, ["sourceSystem"] = Optional(association.SourceSystem),
                    ["trustedSourceProjectId"] = Optional(association.TrustedSourceProjectId), ["lastKnownSnapshotId"] = snapshot.SnapshotId, ["createdAt"] = now, ["updatedAt"] = now },
                ["memory"] = new JObject { ["project"] = new JArray(), ["objects"] = new JArray(), ["requirements"] = new JArray() },
                ["issueState"] = new JObject { ["dispositions"] = new JArray() },
                ["history"] = new JObject { ["memoryChanges"] = new JArray(), ["requirementChanges"] = new JArray(), ["issueDispositionChanges"] = new JArray() }, ["receipts"] = new JArray() };
        }
        private static string Slot(string? objectId, string type, string key) => new JArray(objectId == null ? JValue.CreateNull() : new JValue(objectId), new JValue(type), new JValue(key)).ToString(Formatting.None);
        private static JToken Optional(string? value) => value == null ? JValue.CreateNull() : new JValue(value);
        private static string KeyText(IssueKey key) => KeyJson(key).ToString(Formatting.None);
        private static JObject KeyJson(IssueKey key) => new JObject { ["checkerId"] = key.CheckerId, ["issueType"] = key.IssueType, ["subjectKey"] = key.SubjectKey };
        private static string Hash(string text) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static string Canon(JToken value) => IssueValidation.Canonical(value);
        private bool Stable(string kind) => snapshot.IdentityScope == IdentityScope.ProjectStable && !snapshot.IsFixture && (kind != "mate" || stableMates);
        private JObject Reference(string id, string kind) => new JObject { ["id"] = id, ["kind"] = kind, ["originSnapshotId"] = snapshot.SnapshotId, ["stable"] = Stable(kind) };
        private MemoryReferenceState ReferenceState(JToken reference)
        {
            string id = (string)reference["id"]!, kind = (string)reference["kind"]!;
            if (kind == "mate" ? !snapshot.MatesById.ContainsKey(id) : !snapshot.ComponentsById.ContainsKey(id)) return MemoryReferenceState.StaleReference;
            if ((string?)reference["originSnapshotId"] != snapshot.SnapshotId && (!(bool)reference["stable"]! || !Stable(kind))) return MemoryReferenceState.UntrustedIdentity;
            return MemoryReferenceState.Active;
        }
        private bool ActiveReferences(JObject record) => ((JArray)record["references"]!).All(r => ReferenceState(r) == MemoryReferenceState.Active);
        private JObject View(JObject record)
        {
            var copy = (JObject)record.DeepClone(); foreach (JObject reference in (JArray)copy["references"]!) reference["referenceState"] = ReferenceState(reference).ToString(); return copy;
        }
        private static HashSet<string>? Filter(IEnumerable<string>? values) => values == null ? null : new HashSet<string>(values.Select(ProjectAssociation.Text), StringComparer.Ordinal);
        private MemoryPage Page(IEnumerable<JObject> records, IEnumerable<string>? types, IEnumerable<string>? keys, bool includeRetired, bool includeStale, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("Expected non-negative offset and limit 1-100.");
            var typeSet = Filter(types); var keySet = Filter(keys);
            var matches = records.Where(r => (includeRetired || (string?)r["lifecycle"] == "Active") && (includeStale || ActiveReferences(r))
                && (typeSet == null || typeSet.Contains((string)r["type"]!)) && (keySet == null || keySet.Contains((string)r["key"]!))).OrderBy(r => (string)r["id"]!, StringComparer.Ordinal).ToList();
            return new MemoryPage(new JArray(matches.Skip(offset).Take(limit).Select(View)), matches.Count, offset);
        }
        public MemoryPage GetProjectMemory(IEnumerable<string>? types = null, IEnumerable<string>? keys = null, bool includeRetired = false, int offset = 0, int limit = 50)
        { lock (gate) return Page(state.ProjectMemory.Select(k => state.Memory[k]), types, keys, includeRetired, false, offset, limit); }
        public MemoryPage GetObjectMemory(IEnumerable<string> objectIds, IEnumerable<string>? types = null, IEnumerable<string>? keys = null, bool includeStale = false, bool includeRetired = false, int offset = 0, int limit = 50)
        {
            lock (gate)
            {
                var ids = Filter(objectIds) ?? throw new ArgumentNullException(nameof(objectIds));
                var slots = ids.Where(state.ObjectMemory.ContainsKey).SelectMany(id => state.ObjectMemory[id]).Distinct();
                return Page(slots.Select(k => state.Memory[k]), types, keys, includeRetired, includeStale, offset, limit);
            }
        }
        public MemoryPage GetRequirements(IEnumerable<string>? scopeObjectIds = null, IEnumerable<string>? types = null, bool includeProjectScope = true, bool includeStale = false, bool includeRetired = false, int offset = 0, int limit = 50)
        {
            lock (gate)
            {
                var ids = Filter(scopeObjectIds); var keys = new HashSet<string>(StringComparer.Ordinal);
                if (includeProjectScope) keys.UnionWith(state.ProjectRequirements);
                if (ids != null) foreach (var id in ids) if (state.ObjectRequirements.TryGetValue(id, out var found)) keys.UnionWith(found);
                return Page(keys.Select(k => state.Requirements[k]), types, null, includeRetired, includeStale, offset, limit);
            }
        }
        private string DispositionEligibility(JObject record, IssueKey key)
        {
            if (!ActiveReferences(record)) return "StaleReference";
            if (issues == null) return "NotEvaluated";
            var finding = issues.GetFinding(key);
            if (!finding.Success) return "Historical";
            return finding.Value!.EvidenceHash == (string?)record["expectedEvidenceHash"] ? "Applicable" : "EvidenceChanged";
        }
        public JObject? GetIssueDisposition(IssueKey issueIdentity)
        {
            lock (gate)
            {
                if (!issueIdentity.Valid) throw new ArgumentException("Valid structured issue identity required.");
                if (!state.Dispositions.TryGetValue(KeyText(issueIdentity), out var record)) return null;
                var view = View(record); view["applicability"] = DispositionEligibility(record, issueIdentity); return view;
            }
        }

        private MemoryResult<MemoryReceipt> Mutate(MemoryWriteContext context, JObject payload, Func<string, long, MemoryReceipt> change)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            lock (gate)
            {
                try
                {
                    var request = new JObject { ["snapshotId"] = snapshot.SnapshotId, ["payload"] = payload, ["expectedRevision"] = context.ExpectedRevision,
                        ["provenance"] = context.Provenance.ToString(), ["actor"] = context.Actor, ["override"] = context.AllowUserEstablishedOverride };
                    string fingerprint;
                    try { fingerprint = Hash(Canon(request)); }
                    catch (ArgumentException ex) { throw new ContractException("INVALID_VALUE", ex.Message); }
                    if (state.Receipts.TryGetValue(context.OperationId, out var prior))
                    {
                        if ((string?)prior["fingerprint"] != fingerprint) throw new ContractException("IDEMPOTENCY_CONFLICT", "Operation ID was already used with different content or context.");
                        return MemoryResult<MemoryReceipt>.Ok(new MemoryReceipt(context.OperationId, (string)prior["recordId"]!, (long)prior["revision"]!, true));
                    }
                    if (context.ExpectedRevision != Revision) throw new ContractException("REVISION_CONFLICT", "Refresh the committed memory revision.");
                    if (Revision == long.MaxValue) throw new ContractException("STORE_CAPACITY", "Revision limit reached.");
                    return MemoryResult<MemoryReceipt>.Ok(change(fingerprint, Revision + 1));
                }
                catch (ContractException ex) { return MemoryResult<MemoryReceipt>.Fail(ex.Code, ex.Message); }
                catch (MemoryPersistenceConflictException ex) { return MemoryResult<MemoryReceipt>.Fail("STORAGE_CONFLICT", ex.Message); }
                catch (Exception ex)
                {
                    var diagnostic = DiagnosticLog.Report(ex, "memory.commit", association.ProjectId, snapshot.SnapshotId);
                    return MemoryResult<MemoryReceipt>.Fail("PERSISTENCE_FAILED", "Mutation was not committed. Diagnostic ID: " + diagnostic.Entry.CorrelationId);
                }
            }
        }
        private void Authorize(JObject? old, MemoryWriteContext context)
        {
            if (old != null && (string?)old["provenance"]!["type"] == "UserEstablished" && context.Provenance != MemoryProvenance.UserEstablished && !context.AllowUserEstablishedOverride)
                throw new ContractException("AUTHORITY_CONFLICT", "Host authorization is required to overwrite user-established knowledge.");
        }
        private static void ValidateValue(JToken value)
        {
            if (value.Type == JTokenType.Null || value is JValue scalar && scalar.Value == null) throw new ContractException("INVALID_VALUE", "Null is not a deletion or memory value; use lifecycle Retired.");
            try { if (Encoding.UTF8.GetByteCount(Canon(value)) > 32000) throw new ContractException("INVALID_VALUE", "Memory value exceeds 32 KB."); }
            catch (ArgumentException ex) { throw new ContractException("INVALID_VALUE", ex.Message); }
        }
        private JObject Record(string id, string type, JToken value, JArray references, MemoryLifecycle lifecycle, MemoryWriteContext context, JObject? old, long revision)
        {
            ValidateValue(value); Authorize(old, context);
            if (old != null && lifecycle == MemoryLifecycle.Active && !ActiveReferences(old)) throw new ContractException("STALE_REFERENCE", "Existing stale references require explicit reconciliation; they cannot be silently rebound.");
            string now = DateTimeOffset.UtcNow.ToString("O");
            return new JObject { ["id"] = id, ["type"] = type, ["value"] = value.DeepClone(), ["references"] = references.DeepClone(), ["lifecycle"] = lifecycle.ToString(),
                ["provenance"] = new JObject { ["type"] = context.Provenance.ToString(), ["actor"] = context.Actor }, ["originSnapshotId"] = old?["originSnapshotId"] ?? new JValue(snapshot.SnapshotId),
                ["createdAt"] = old?["createdAt"] ?? new JValue(now), ["updatedAt"] = now, ["revision"] = revision };
        }
        private MemoryReceipt Save(JObject? old, JObject record, string bucket, string historyBucket, MemoryWriteContext context, string fingerprint, long revision)
        {
            var next = (JObject)state.Document.DeepClone();
            var target = bucket == "dispositions" ? (JArray)next["issueState"]![bucket]! : (JArray)next["memory"]![bucket]!;
            var existing = target.OfType<JObject>().SingleOrDefault(r => (string?)r["id"] == (string?)record["id"]);
            if (existing != null) existing.Replace(record.DeepClone()); else target.Add(record.DeepClone());
            ((JArray)next["history"]![historyBucket]!).Add(new JObject { ["operationId"] = context.OperationId, ["revision"] = revision, ["before"] = old?.DeepClone(), ["after"] = record.DeepClone() });
            ((JArray)next["receipts"]!).Add(new JObject { ["operationId"] = context.OperationId, ["recordId"] = record["id"]!.DeepClone(), ["revision"] = revision, ["fingerprint"] = fingerprint });
            next["revision"] = revision; next["project"]!["updatedAt"] = record["updatedAt"]!.DeepClone(); next["project"]!["lastKnownSnapshotId"] = snapshot.SnapshotId;
            string serialized = next.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(serialized) > 10000000 || ((JArray)next["receipts"]!).Count > 10000) throw new ContractException("STORE_CAPACITY", "Sidecar limit reached; history/receipts were not discarded.");
            try { ValidateDocument(next); }
            catch (InvalidDataException ex) { throw new ContractException("INVALID_RECORD", ex.Message); }
            var prepared = new State(next);
            var receipt = new MemoryReceipt(context.OperationId, (string)record["id"]!, revision, false);
            persistence.Commit(committedText, serialized);
            state = prepared; committedText = serialized; return receipt;
        }
        public MemoryResult<MemoryReceipt> UpsertMemory(MemoryMutation mutation, MemoryWriteContext context)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            var payload = new JObject { ["operation"] = "memory", ["objectId"] = mutation.ObjectId, ["type"] = mutation.Type, ["key"] = mutation.Key, ["value"] = mutation.Value, ["lifecycle"] = mutation.Lifecycle.ToString() };
            return Mutate(context, payload, (fingerprint, revision) =>
            {
                state.Memory.TryGetValue(Slot(mutation.ObjectId, mutation.Type, mutation.Key), out var old);
                if (mutation.ObjectId != null && !snapshot.ComponentsById.ContainsKey(mutation.ObjectId) && !(old != null && mutation.Lifecycle == MemoryLifecycle.Retired)) throw new ContractException("OBJECT_NOT_FOUND", "Object is not in the bound snapshot.");
                var refs = old != null ? (JArray)old["references"]! : mutation.ObjectId == null ? new JArray() : new JArray(Reference(mutation.ObjectId, "object"));
                var record = Record((string?)old?["id"] ?? Guid.NewGuid().ToString("N"), mutation.Type, mutation.Value, refs, mutation.Lifecycle, context, old, revision);
                record["key"] = mutation.Key; record["objectId"] = Optional(mutation.ObjectId);
                return Save(old, record, mutation.ObjectId == null ? "project" : "objects", "memoryChanges", context, fingerprint, revision);
            });
        }
        public MemoryResult<MemoryReceipt> UpsertRequirement(RequirementMutation mutation, MemoryWriteContext context)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            var payload = new JObject { ["operation"] = "requirement", ["id"] = mutation.RequirementId, ["type"] = mutation.Type, ["objectIds"] = new JArray(mutation.ObjectIds), ["value"] = mutation.Value, ["lifecycle"] = mutation.Lifecycle.ToString() };
            return Mutate(context, payload, (fingerprint, revision) =>
            {
                state.Requirements.TryGetValue(mutation.RequirementId, out var old);
                var oldIds = old == null ? Array.Empty<string>() : ((JArray)old["references"]!).Select(r => (string)r["id"]!).ToArray();
                bool sameScope = old != null && oldIds.SequenceEqual(mutation.ObjectIds);
                if (mutation.ObjectIds.Any(id => !snapshot.ComponentsById.ContainsKey(id)) && !(sameScope && mutation.Lifecycle == MemoryLifecycle.Retired)) throw new ContractException("OBJECT_NOT_FOUND", "Requirement scope contains unknown objects.");
                var refs = sameScope ? (JArray)old!["references"]! : new JArray(mutation.ObjectIds.Select(id => Reference(id, "object")));
                var record = Record(mutation.RequirementId, mutation.Type, mutation.Value, refs, mutation.Lifecycle, context, old, revision);
                return Save(old, record, "requirements", "requirementChanges", context, fingerprint, revision);
            });
        }
        public MemoryResult<MemoryReceipt> SetIssueDisposition(IssueKey key, string expectedEvidenceHash, IssueDispositionState disposition, string? reason, MemoryWriteContext context)
        {
            if (!key.Valid) throw new ArgumentException("Valid structured issue identity required.");
            if (string.IsNullOrWhiteSpace(reason)) reason = null;
            var payload = new JObject { ["operation"] = "disposition", ["key"] = KeyJson(key), ["expectedEvidenceHash"] = expectedEvidenceHash, ["state"] = disposition.ToString(), ["reason"] = reason };
            return Mutate(context, payload, (fingerprint, revision) =>
            {
                if (issues == null) throw new ContractException("ISSUES_UNAVAILABLE", "Bind the regenerated IssueStore before accepting evidence.");
                state.Dispositions.TryGetValue(KeyText(key), out var old); Authorize(old, context); MemoryReceipt? receipt = null;
                var result = issues.PersistDisposition(key, expectedEvidenceHash, disposition, reason, (finding, runtimeDisposition) =>
                {
                    var refs = new JArray(finding.AffectedObjectIds.Select(id => Reference(id, "object")).Concat(finding.RelatedMateIds.Select(id => Reference(id, "mate"))));
                    var record = new JObject { ["id"] = Hash(KeyText(key)), ["issueKey"] = KeyJson(key), ["references"] = refs, ["state"] = disposition.ToString(),
                        ["reason"] = Optional(reason), ["expectedEvidenceHash"] = expectedEvidenceHash, ["acceptedEvidenceHash"] = Optional(runtimeDisposition.AcceptedEvidenceHash),
                        ["originSnapshotId"] = snapshot.SnapshotId, ["updatedAt"] = runtimeDisposition.UpdatedAt.ToString("O"), ["revision"] = revision,
                        ["provenance"] = new JObject { ["type"] = context.Provenance.ToString(), ["actor"] = context.Actor } };
                    receipt = Save(old, record, "dispositions", "issueDispositionChanges", context, fingerprint, revision);
                });
                if (!result.Success) throw new ContractException(result.ErrorCode!, result.Message!);
                return receipt!;
            });
        }
        private sealed class ContractException : Exception
        { internal string Code { get; } internal ContractException(string code, string message) : base(message) { Code = code; } }
    }
}
