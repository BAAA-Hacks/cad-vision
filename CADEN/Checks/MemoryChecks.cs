using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.Operations.Memory;
using Core.Primitives.Operations.Project;
using Core.Primitives.Operations.Issues;
using Desktop.Persistence;
using Newtonsoft.Json.Linq;

internal static class MemoryChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Storage : IMemoryPersistence
    {
        public string? Content;
        public bool Fail;
        public int Writes;
        public string? Read() => Content;
        public void Commit(string? expectedContent, string newContent)
        {
            if (Fail) throw new IOException("Injected persistence failure.");
            if (Content != expectedContent) throw new MemoryPersistenceConflictException("Injected external edit.");
            Content = newContent; Writes++;
        }
    }
    private static ProjectSnapshot Snapshot(int revision = 1, bool stable = true, bool removeB = false, bool fixture = false, string project = "source")
    {
        var doc = JObject.Parse("""
        {"schemaVersion":"1.0","project":{"id":"source","name":"Test","rootObjectId":"R"},"objects":[
        {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"]},
        {"id":"A","name":"A","type":"part","parentId":"R","childIds":[]},
        {"id":"B","name":"B","type":"part","parentId":"R","childIds":[]}],"mates":[{"id":"M","componentIds":["A","B"]}]}
        """);
        doc["project"]!["revision"] = revision; doc["project"]!["id"] = project;
        if (removeB) { ((JArray)doc["objects"]!).RemoveAt(2); doc["objects"]![0]!["childIds"] = new JArray("A"); doc["mates"] = new JArray(); }
        if (fixture) doc["sampleInfo"] = new JObject { ["fixture"] = true };
        return LoadProject.Load(doc.ToString(), new ProjectLoadOptions { ComponentIdsStableAcrossSnapshots = stable, MateExportState = CapabilityState.Available }).Snapshot!;
    }
    private static ProjectMemoryStore Open(IMemoryPersistence storage, ProjectSnapshot snapshot, IssueStore? issues = null, bool stableMates = false)
    {
        var result = ProjectMemoryStore.Open(storage, new ProjectAssociation("caden-project"), snapshot, issues, stableMates);
        Require(result.Success, "Memory open failed: " + result.ErrorCode + " " + result.Message + (result.Success ? "" : "\n" + Core.Diagnostics.DiagnosticLog.GetRecent().LastOrDefault()?.ExceptionDetails)); return result.Value!;
    }
    private static MemoryWriteContext Write(ProjectMemoryStore store, string op, MemoryProvenance provenance = MemoryProvenance.UserEstablished, bool allowOverride = false) => new(op, store.Revision, provenance, "test-host", allowOverride);
    private static IssueFinding Finding(ProjectSnapshot s, int evidence = 1, bool mate = false) => new(
        mate ? IssueKey.ForEntities("test", "mate", Array.Empty<string>(), new[] { "M" }) : IssueKey.ForEntities("test", "object", new[] { "A" }),
        "1", s.ProjectId, s.SnapshotId, IssueSeverity.Question, mate ? IssueScope.Mate : IssueScope.Object,
        mate ? new[] { "A", "B" } : new[] { "A" }, mate ? new[] { "M" } : Array.Empty<string>(), new JObject { ["value"] = evidence });
    public static void Run()
    {
        var snapshot = Snapshot(); var storage = new Storage(); var issues = new IssueStore(snapshot); var f = Finding(snapshot); issues.AddFinding(f);
        var store = Open(storage, snapshot, issues); Require(storage.Content == null && store.Revision == 0, "Opening missing memory wrote unexpectedly.");
        var intent = new MemoryMutation("intended_dof", "rotation", new JValue("allowed"), "A"); var firstContext = Write(store, "intent-1");
        var first = store.UpsertMemory(intent, firstContext); Require(first.Success && store.Revision == 1 && storage.Writes == 1, "Memory commit failed: " + first.Message);
        string saved = storage.Content!;
        Require(store.GetProjectMemory().Total == 0 && store.GetObjectMemory(new[] { "A" }).Total == 1 && store.GetObjectMemory(new[] { "R" }).Total == 0, "Memory scope leaked or expanded subtree.");
        var copy = store.GetObjectMemory(new[] { "A" }).Records; copy[0]!["value"] = "mutated";
        Require((string?)store.GetObjectMemory(new[] { "A" }).Records[0]!["value"] == "allowed", "Read mutated stored value.");
        Require(store.GetObjectMemory(new[] { "A" }, keys: new[] { "intended_dof" }).Total == 0 && store.GetObjectMemory(new[] { "A" }, types: new[] { "intended_dof" }, keys: new[] { "rotation" }).Total == 1, "Type/key filters conflated.");
        var replay = store.UpsertMemory(intent, firstContext); Require(replay.Value!.Replayed && replay.Value.RecordId == first.Value!.RecordId && storage.Writes == 1, "Retry duplicated memory/history.");
        var reopened = Open(storage, snapshot, issues); Require(reopened.UpsertMemory(intent, firstContext).Value!.Replayed, "Receipt not durable across restart.");
        Require(store.UpsertMemory(new MemoryMutation("intended_dof", "rotation", new JValue("changed"), "A"), firstContext).ErrorCode == "IDEMPOTENCY_CONFLICT", "Operation ID reused for different content.");
        Require(store.UpsertMemory(intent, new MemoryWriteContext("stale-write", 0, MemoryProvenance.UserEstablished, "host")).ErrorCode == "REVISION_CONFLICT", "Stale writer overwrote memory.");
        Require(store.UpsertMemory(intent, Write(store, "infer", MemoryProvenance.AssistantInferred)).ErrorCode == "AUTHORITY_CONFLICT", "Inference overwrote user intent.");
        Require(store.UpsertMemory(intent, Write(store, "system", MemoryProvenance.System)).ErrorCode == "AUTHORITY_CONFLICT", "System bypassed provenance authority.");
        storage.Fail = true;
        Require(store.UpsertMemory(new MemoryMutation("goal", "primary", new JValue("light")), Write(store, "disk-fail")).ErrorCode == "PERSISTENCE_FAILED" && storage.Content == saved && store.Revision == 1 && store.GetProjectMemory().Total == 0, "Failed save changed runtime or disk.");
        Require(!store.SetIssueDisposition(f.Key, f.EvidenceHash, IssueDispositionState.Ignored, "intent", Write(store, "issue-fail")).Success && issues.GetDisposition(f.Key).Value!.State == IssueDispositionState.Open, "Failed issue save changed runtime disposition.");
        storage.Fail = false;
        Require(store.UpsertMemory(new MemoryMutation("goal", "primary", new JValue("light")), Write(store, "goal")).Success, "Project memory failed.");
        Require(store.GetProjectMemory(types: Array.Empty<string>()).Total == 0 && store.GetProjectMemory().Total == 1, "Empty filters ignored.");
        long beforeReads = store.Revision; int writes = storage.Writes; store.GetRequirements(); store.GetIssueDisposition(f.Key);
        Require(store.Revision == beforeReads && storage.Writes == writes, "Reads rewrote sidecar.");
        var retirement = new MemoryMutation("intended_dof", "rotation", new JValue("allowed"), "A", MemoryLifecycle.Retired);
        Require(store.UpsertMemory(retirement, Write(store, "retire")).Success && store.GetObjectMemory(new[] { "A" }).Total == 0 && store.GetObjectMemory(new[] { "A" }, includeRetired: true).Total == 1, "Retirement deleted history or stayed active.");
        Require(store.UpsertMemory(intent, Write(store, "reactivate")).Success && store.GetObjectMemory(new[] { "A" }).Total == 1, "Trusted reactivation failed.");
        Require(store.UpsertMemory(new MemoryMutation("goal", "bad", JValue.CreateNull()), Write(store, "null")).ErrorCode == "INVALID_VALUE", "Null accepted as deletion/value.");
        Require(store.UpsertMemory(new MemoryMutation("goal", "bad", new JValue((string?)null)), Write(store, "typed-null")).ErrorCode == "INVALID_VALUE", "Typed null bypassed validation.");
        Require(store.UpsertMemory(new MemoryMutation("goal", "bad", new JValue(double.NaN)), Write(store, "nonfinite")).ErrorCode == "INVALID_VALUE", "Nonfinite value persisted.");
        var req = new RequirementMutation("req", "max_mass", new JObject { ["value"] = 0.5, ["unit"] = "kg" }, new[] { "B", "A" });
        Require(store.UpsertRequirement(req, Write(store, "requirement")).Success, "Structured requirement failed.");
        Require(store.GetRequirements().Total == 0 && store.GetRequirements(new[] { "A" }).Total == 1 && ((JArray)store.GetRequirements(new[] { "A" }).Records[0]!["references"]!).Count == 2, "Requirement intersection/scope incorrect.");
        Require(store.UpsertRequirement(new RequirementMutation("global", "goal", new JValue("portable"), Array.Empty<string>()), Write(store, "global")).Success && store.GetRequirements().Total == 1 && store.GetRequirements(new[] { "A" }).Total == 2 && store.GetRequirements(new[] { "A" }, includeProjectScope: false).Total == 1, "Project requirements filter incorrect.");
        Require(store.UpsertRequirement(new RequirementMutation("global", "goal", new JValue("portable"), Array.Empty<string>(), MemoryLifecycle.Retired), Write(store, "retire-requirement")).Success && store.GetRequirements().Total == 0 && store.GetRequirements(includeRetired: true).Total == 1, "Requirement retirement failed.");
        var acceptedContext = Write(store, "accept");
        Require(store.SetIssueDisposition(f.Key, f.EvidenceHash, IssueDispositionState.Ignored, "intent", acceptedContext).Success && issues.GetDisposition(f.Key).Value!.State == IssueDispositionState.Ignored, "Durable disposition not coordinated.");
        Require(store.SetIssueDisposition(f.Key, f.EvidenceHash, IssueDispositionState.Ignored, "intent", acceptedContext).Value!.Replayed, "Disposition retry not idempotent.");
        Require(store.SetIssueDisposition(f.Key, "stale", IssueDispositionState.Resolved, "done", Write(store, "stale-evidence")).ErrorCode == "EVIDENCE_CHANGED", "Stale evidence accepted.");
        var regenerated = new IssueStore(snapshot); regenerated.AddFinding(f); Open(storage, snapshot, regenerated);
        Require(regenerated.GetDisposition(f.Key).Value!.State == IssueDispositionState.Ignored, "Restart failed to restore eligible judgment.");
        var next = Snapshot(2); var nextIssues = new IssueStore(next); nextIssues.AddFinding(Finding(next)); var nextStore = Open(storage, next, nextIssues);
        Require(nextStore.GetObjectMemory(new[] { "A" }).Total == 1 && nextIssues.GetDisposition(f.Key).Value!.State == IssueDispositionState.Ignored, "Stable identity did not reconcile.");
        var untrusted = Snapshot(3, stable: false); var untrustedIssues = new IssueStore(untrusted); untrustedIssues.AddFinding(Finding(untrusted)); var untrustedStore = Open(storage, untrusted, untrustedIssues);
        Require(untrustedStore.GetObjectMemory(new[] { "A" }).Total == 0 && (string?)untrustedStore.GetObjectMemory(new[] { "A" }, includeStale: true).Records[0]!["references"]![0]!["referenceState"] == "UntrustedIdentity" && untrustedIssues.GetDisposition(f.Key).Value!.State == IssueDispositionState.Open, "Same string ID silently trusted across snapshots.");
        Require(untrustedStore.UpsertMemory(intent, Write(untrustedStore, "unsafe-rebind")).ErrorCode == "STALE_REFERENCE", "Stale logical slot implicitly rebound.");
        var missing = Open(storage, Snapshot(4, removeB: true));
        Require(missing.GetRequirements(new[] { "A" }, includeProjectScope: false).Total == 0 && (string?)missing.GetRequirements(new[] { "A" }, includeProjectScope: false, includeStale: true).Records[0]!["references"]![1]!["referenceState"] == "StaleReference", "Partially stale requirement applied silently.");
        var changedIssues = new IssueStore(next); changedIssues.AddFinding(Finding(next, 9)); var changedStore = Open(storage, next, changedIssues);
        Require(changedIssues.GetDisposition(f.Key).Value!.State == IssueDispositionState.Open && (string?)changedStore.GetIssueDisposition(f.Key)!["applicability"] == "EvidenceChanged", "Changed evidence inherited acceptance.");
        var noIssues = new IssueStore(next); var historical = Open(storage, next, noIssues);
        Require((string?)historical.GetIssueDisposition(f.Key)!["applicability"] == "Historical", "Absent finding deleted disposition history.");
        Require(!ProjectMemoryStore.Open(storage, new ProjectAssociation("other-caden-project"), snapshot).Success, "Wrong CADEN project bound sidecar.");
        Require(!ProjectMemoryStore.Open(new Storage(), new ProjectAssociation("caden-project", "solidworks", "different-source"), snapshot).Success, "Trusted external source mismatch accepted.");
        var explicitAssociation = Open(storage, Snapshot(2, project: "changed-untrusted-export-id"));
        Require(explicitAssociation.GetProjectMemory().Total == 1, "CADEN durable association depended on untrusted exporter ID.");
        var fixtureStorage = new Storage(); var fixtureStore = Open(fixtureStorage, Snapshot(fixture: true));
        Require(fixtureStore.UpsertMemory(intent, Write(fixtureStore, "fixture-intent")).Success && Open(fixtureStorage, Snapshot(2, fixture: true)).GetObjectMemory(new[] { "A" }).Total == 0, "Fixture IDs treated as project-stable.");
        var authorityStorage = new Storage(); var authorityStore = Open(authorityStorage, snapshot);
        authorityStore.UpsertMemory(intent, Write(authorityStore, "user"));
        Require(authorityStore.UpsertMemory(intent, Write(authorityStore, "approved-override", MemoryProvenance.AssistantInferred, allowOverride: true)).Success, "Explicit host override blocked.");
        Require(authorityStore.GetObjectMemory(new[] { "A" }, limit: 1).Records.Count == 1, "Bounded read failed.");

        // Mate acceptance needs a separate identity guarantee on both sides.
        var mateStorage = new Storage(); var mateIssues = new IssueStore(snapshot); var mf = Finding(snapshot, mate: true); mateIssues.AddFinding(mf);
        var mateStore = Open(mateStorage, snapshot, mateIssues, true); Require(mateStore.SetIssueDisposition(mf.Key, mf.EvidenceHash, IssueDispositionState.Resolved, "fixed", Write(mateStore, "mate")).Success, "Mate disposition save failed.");
        var futureMateIssues = new IssueStore(next); futureMateIssues.AddFinding(Finding(next, mate: true)); Open(mateStorage, next, futureMateIssues);
        Require(futureMateIssues.GetDisposition(mf.Key).Value!.State == IssueDispositionState.Open, "Mate identity guessed.");
        var trustedMateIssues = new IssueStore(next); trustedMateIssues.AddFinding(Finding(next, mate: true)); Open(mateStorage, next, trustedMateIssues, true);
        Require(trustedMateIssues.GetDisposition(mf.Key).Value!.State == IssueDispositionState.Resolved, "Guaranteed mate identity not restored.");

        // Corruption is fail-closed and never rewritten.
        string valid = storage.Content!;
        foreach (string corrupt in new[] { "", "{}", valid + "{}", valid.Replace("\"schemaVersion\":\"1\"", "\"schemaVersion\":\"2\""), valid.Replace("\"revision\":1", "\"revision\":1,\"revision\":1") })
        { var broken = new Storage { Content = corrupt }; Require(!ProjectMemoryStore.Open(broken, new ProjectAssociation("caden-project"), snapshot).Success && broken.Content == corrupt && broken.Writes == 0, "Corrupt memory loaded/reset."); }
        storage.Content = valid + " "; Require(store.UpsertMemory(intent, Write(store, "external-edit")).ErrorCode == "STORAGE_CONFLICT", "External edit overwritten."); storage.Content = valid;

        // Exercise the actual file adapter, including atomic replacement and stale-writer rejection.
        string dir = Path.Combine(Path.GetTempPath(), "caden-memory-" + Guid.NewGuid().ToString("N")); string path = Path.Combine(dir, "project.caden.json");
        try
        {
            var file = new ProjectMemoryFile(path); var fileStore = Open(file, snapshot); var competing = Open(new ProjectMemoryFile(path), snapshot);
            Require(fileStore.UpsertMemory(intent, Write(fileStore, "file-first")).Success && File.Exists(path), "File commit failed.");
            Require(competing.UpsertMemory(intent, Write(competing, "competing")).ErrorCode == "STORAGE_CONFLICT", "Competing store overwrote file.");
            Require(fileStore.UpsertMemory(retirement, Write(fileStore, "file-replace")).Success && Open(file, snapshot).GetObjectMemory(new[] { "A" }, includeRetired: true).Total == 1, "File replacement/reopen failed.");
            Require(!Directory.GetFiles(dir).Any(p => p.EndsWith(".tmp")), "Temporary file leaked.");
        }
        finally { if (Directory.Exists(dir)) { foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); } }
        Console.WriteLine("PASS: durable memory/requirements, provenance, retirement, selective reads, revision/idempotency, identity reconciliation, atomic disposition persistence, corruption and file conflict safeguards.");
    }
}
