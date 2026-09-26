using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;

internal static class IssueStoreChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ProjectSnapshot Snapshot(int revision = 1, bool stable = true, bool badGraph = false, string project = "P")
    {
        var doc = JObject.Parse("""
        {"schemaVersion":"1.0","project":{"id":"P","name":"Test","rootObjectId":"R"},
         "objects":[{"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"]},
                    {"id":"A","name":"A","type":"part","parentId":"R","childIds":[]},
                    {"id":"B","name":"B","type":"part","parentId":"R","childIds":[]}],
         "mates":[{"id":"M","componentIds":["A","B"]}]}
        """);
        doc["project"]!["id"] = project; doc["project"]!["revision"] = revision;
        if (badGraph) doc["mates"]![0]!["componentIds"] = new JArray("A", "missing");
        var loaded = LoadProject.Load(doc.ToString(), new ProjectLoadOptions { ComponentIdsStableAcrossSnapshots = stable, MateExportState = CapabilityState.Available });
        Require(loaded.Success, "Issue fixture did not load."); return loaded.Snapshot!;
    }
    private static IssueFinding Finding(ProjectSnapshot s, int value = 1, string type = "free", string[]? ids = null, string[]? mates = null, IssueSeverity severity = IssueSeverity.Question, JObject? evidence = null, string version = "1")
    {
        ids ??= new[] { "A" }; mates ??= Array.Empty<string>();
        return new IssueFinding(IssueKey.ForEntities("test", type, ids, mates), version, s.ProjectId, s.SnapshotId, severity,
            ids.Length > 1 ? IssueScope.MultiObject : IssueScope.Object, ids, mates, evidence ?? new JObject { ["dof"] = value });
    }
    private sealed class Checker : IIssueChecker
    {
        public string Id => "test";
        public string Version => "1";
        public IssueCapabilities RequiredCapabilities { get; set; } = IssueCapabilities.Properties;
        public Func<IssueFinding, Task<IssueCheckResult>> Evaluate { get; set; } = _ => throw new InvalidOperationException("test failure");
        public int Calls;
        public Task<IssueCheckResult> EvaluateAsync(IssueFinding previous, ProjectSnapshot snapshot, CancellationToken cancellationToken) { Calls++; return Evaluate(previous); }
    }
    public static async Task RunAsync()
    {
        var s = Snapshot(); var f = Finding(s); var store = new IssueStore(s);
        Require(IssueKey.ForEntities("c", "t", new[] { "B", "A", "A" }) == IssueKey.ForEntities("c", "t", new[] { "A", "B" }), "Set key not canonical.");
        Require(IssueKey.ForEntities("c", "t", new[] { "A|B" }) != IssueKey.ForEntities("c", "t", new[] { "A", "B" }), "Delimited IDs collided.");
        Require(IssueKey.ForEntities("c", "t", new[] { "M" }) != IssueKey.ForEntities("c", "t", Array.Empty<string>(), new[] { "M" }), "Mate/object namespaces collided.");
        Require(IssueKey.ForRoles("c", "t", new Dictionary<string, string> { ["from"] = "A", ["to"] = "B" }) != IssueKey.ForRoles("c", "t", new Dictionary<string, string> { ["from"] = "B", ["to"] = "A" }), "Directional subjects lost roles.");
        var evidence = new JObject { ["z"] = 1, ["a"] = new JObject { ["y"] = false, ["b"] = "Missing" } };
        var immutable = Finding(s, evidence: evidence);
        var equivalent = Finding(s, evidence: JObject.Parse("{\"a\":{\"b\":\"Missing\",\"y\":false},\"z\":1}"));
        Require(immutable.EvidenceHash == equivalent.EvidenceHash, "Evidence hash depends on property order.");
        evidence["z"] = 8; var copy = immutable.Evidence; copy["z"] = 9;
        Require((int)immutable.Evidence["z"]! == 1, "Evidence mutable from outside.");
        Require(Finding(s, version: "2").EvidenceHash != f.EvidenceHash, "Checker version excluded from hash.");
        Require(Finding(s, evidence: new JObject { ["a"] = new JArray(1, 2) }).EvidenceHash != Finding(s, evidence: new JObject { ["a"] = new JArray(2, 1) }).EvidenceHash, "Ordered evidence array sorted.");
        try { _ = new IssueFinding(f.Key, "1", s.ProjectId, s.SnapshotId, IssueSeverity.Error, IssueScope.Object, new[] { "A" }, Array.Empty<string>(), new JObject(), isHeuristic: true); throw new Exception("Heuristic error accepted."); } catch (ArgumentException) { }
        Require(store.AddFinding(f).Success && store.AddFinding(f).Success && store.Count == 1, "Duplicate finding not idempotent.");
        Require(!store.AddFinding(Finding(s, 2)).Success && !store.AddFinding(Finding(s, ids: new[] { "missing" })).Success && !store.AddFinding(Finding(Snapshot(2))).Success, "Invalid/changed finding accepted.");
        Require(store.ByObject("A").Single() == f.Key && store.ByType("free").Single() == f.Key && store.BySeverity(IssueSeverity.Question).Single() == f.Key && store.ByDisposition(IssueDispositionState.Open).Single() == f.Key, "Initial indexes incorrect.");
        Require(store.SetDisposition(f.Key, IssueDispositionState.Ignored, "Intentional rotation", f.EvidenceHash).Success && store.ByDisposition(IssueDispositionState.Open).Count == 0, "Disposition indexes incorrect.");
        Require(!store.SetDisposition(f.Key, IssueDispositionState.Resolved, null, "stale").Success, "Stale acceptance allowed.");
        Require(store.RemoveFinding(f.Key, f.EvidenceHash).Success && store.ByObject("A").Count == 0 && store.ByDisposition(IssueDispositionState.Ignored).Count == 0 && store.GetDispositionHistory(f.Key).Count == 2, "Removal lost history or retained indexes.");
        store.AddFinding(f); Require(store.GetDisposition(f.Key).Value!.State == IssueDispositionState.Ignored, "Same evidence reappearance lost acceptance.");
        var changed = Finding(s, 2, severity: IssueSeverity.Warning);
        Require(store.ReplaceFindingEvidence(changed, f.EvidenceHash).Success && store.GetDisposition(f.Key).Value!.State == IssueDispositionState.Open && store.BySeverity(IssueSeverity.Question).Count == 0 && store.BySeverity(IssueSeverity.Warning).Count == 1, "Changed evidence did not reopen/reindex.");
        var multi = Finding(s, type: "multi", ids: new[] { "A", "B" }, mates: new[] { "M" });
        store.AddFinding(multi); Require(store.ByObject("B").Single() == multi.Key && store.ByMate("M").Single() == multi.Key, "Multi-object/mate index incomplete.");
        store.RemoveFinding(multi.Key, multi.EvidenceHash); Require(store.ByObject("B").Count == 0 && store.ByMate("M").Count == 0, "Secondary index removal failed.");

        var checker = new Checker(); var registry = new IssueCheckerRegistry(new[] { checker });
        store.SetDisposition(changed.Key, IssueDispositionState.Ignored, "accepted", changed.EvidenceHash);
        checker.Evaluate = old => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Present, new[] { old }));
        Require((await RevalidateIssue.RunAsync(store, changed.Key, registry)).Success && store.GetDisposition(changed.Key).Value!.State == IssueDispositionState.Ignored, "Unchanged revalidation lost acceptance.");
        checker.Evaluate = _ => throw new InvalidOperationException();
        Require((await RevalidateIssue.RunAsync(store, changed.Key, registry)).Value!.Outcome == RevalidationOutcome.UnableToEvaluate && store.Count == 1, "Failed checker removed finding.");
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.UnableToEvaluate, reason: "missing input"));
        Require((await RevalidateIssue.RunAsync(store, changed.Key, registry)).Value!.Outcome == RevalidationOutcome.UnableToEvaluate && store.GetDisposition(changed.Key).Value!.State == IssueDispositionState.Ignored, "Unavailable input changed disposition.");
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Present, new[] { f, Finding(s, type: "bad", ids: new[] { "missing" }) }));
        Require(!(await RevalidateIssue.RunAsync(store, changed.Key, registry)).Success && store.GetFinding(f.Key).Value!.EvidenceHash == changed.EvidenceHash && store.GetDisposition(f.Key).Value!.State == IssueDispositionState.Ignored, "Invalid batch partially committed.");
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Present, new[] { f }));
        Require((await RevalidateIssue.RunAsync(store, f.Key, registry)).Success && store.GetDisposition(f.Key).Value!.State == IssueDispositionState.Open, "Revalidation evidence change did not reopen.");
        var pending = new TaskCompletionSource<IssueCheckResult>(); checker.Evaluate = _ => pending.Task;
        var running = RevalidateIssue.RunAsync(store, f.Key, registry);
        store.SetDisposition(f.Key, IssueDispositionState.Resolved, "user decision", f.EvidenceHash);
        pending.SetResult(new IssueCheckResult(RevalidationOutcome.Absent));
        Require((await running).ErrorCode == "STORE_CHANGED" && store.Count == 1 && store.GetDisposition(f.Key).Value!.State == IssueDispositionState.Resolved, "Late checker overwrote user decision.");
        var unrelated = Finding(s, type: "unrelated", ids: new[] { "B" }); store.AddFinding(unrelated);
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Absent, new[] { unrelated }));
        Require((await RevalidateIssue.RunAsync(store, f.Key, registry)).ErrorCode == "INVALID_CHECK_RESULT" && store.GetFinding(f.Key).Success, "Unrelated output accepted.");
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Present, new[] { f, f }));
        Require((await RevalidateIssue.RunAsync(store, f.Key, registry)).ErrorCode == "INVALID_CHECK_RESULT", "Duplicate batch keys accepted.");
        checker.Evaluate = _ => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Absent, new[] { multi }));
        Require((await RevalidateIssue.RunAsync(store, f.Key, registry)).Success && !store.GetFinding(f.Key).Success && store.GetFinding(unrelated.Key).Success && store.GetFinding(multi.Key).Success, "Targeted absence affected unrelated findings.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await RevalidateIssue.RunAsync(store, multi.Key, registry, cancellation.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        using var lateCancellation = new CancellationTokenSource();
        var pendingCancel = new TaskCompletionSource<IssueCheckResult>(); checker.Evaluate = _ => pendingCancel.Task;
        var cancelRun = RevalidateIssue.RunAsync(store, multi.Key, registry, lateCancellation.Token);
        lateCancellation.Cancel(); pendingCancel.SetResult(new IssueCheckResult(RevalidationOutcome.Absent));
        try { await cancelRun; throw new Exception("Late cancellation ignored."); } catch (OperationCanceledException) { }
        Require(store.GetFinding(multi.Key).Success, "Canceled result committed.");

        var degraded = Snapshot(badGraph: true); var degradedStore = new IssueStore(degraded); var propertyFinding = Finding(degraded); degradedStore.AddFinding(propertyFinding);
        checker.RequiredCapabilities = IssueCapabilities.MechanicalGraph; int calls = checker.Calls;
        Require((await RevalidateIssue.RunAsync(degradedStore, propertyFinding.Key, registry)).Value!.Outcome == RevalidationOutcome.UnableToEvaluate && checker.Calls == calls, "Invalid graph checker ran.");
        checker.RequiredCapabilities = IssueCapabilities.Properties; checker.Evaluate = old => Task.FromResult(new IssueCheckResult(RevalidationOutcome.Present, new[] { old }));
        Require((await RevalidateIssue.RunAsync(degradedStore, propertyFinding.Key, registry)).Success && checker.Calls == calls + 1, "Invalid graph blocked property checks.");

        var previous = new IssueStore(s); previous.AddFinding(f); previous.SetDisposition(f.Key, IssueDispositionState.Ignored, "intent", f.EvidenceHash);
        var nextSnapshot = Snapshot(2); var next = new IssueStore(nextSnapshot); next.AddFinding(Finding(nextSnapshot));
        Require(next.CarryDispositionFrom(previous, f.Key).Success && next.GetDisposition(f.Key).Value!.AcceptedSnapshotId == s.SnapshotId, "Stable matching acceptance did not carry with provenance.");
        Require(!next.CarryDispositionFrom(previous, f.Key).Success, "Carry-over overwrote current decision.");
        var untrustedSnapshot = Snapshot(2, stable: false); var untrusted = new IssueStore(untrustedSnapshot); untrusted.AddFinding(Finding(untrustedSnapshot));
        Require(untrusted.CarryDispositionFrom(previous, f.Key).ErrorCode == "UNTRUSTED_IDENTITY", "Snapshot-only IDs remapped silently.");
        var altered = new IssueStore(nextSnapshot); altered.AddFinding(Finding(nextSnapshot, 3));
        Require(altered.CarryDispositionFrom(previous, f.Key).ErrorCode == "EVIDENCE_CHANGED", "Changed evidence inherited acceptance.");
        var otherProjectSnapshot = Snapshot(project: "other"); var otherProject = new IssueStore(otherProjectSnapshot); otherProject.AddFinding(Finding(otherProjectSnapshot));
        Require(otherProject.CarryDispositionFrom(previous, f.Key).ErrorCode == "PROJECT_MISMATCH", "Acceptance crossed projects.");
        previous.AddFinding(multi); previous.SetDisposition(multi.Key, IssueDispositionState.Resolved, "fixed", multi.EvidenceHash);
        next.AddFinding(Finding(nextSnapshot, type: "multi", ids: new[] { "A", "B" }, mates: new[] { "M" }));
        Require(next.CarryDispositionFrom(previous, multi.Key).ErrorCode == "UNTRUSTED_IDENTITY" && next.CarryDispositionFrom(previous, multi.Key, mateIdsStableAcrossSnapshots: true).Success, "Mate identity guarantee not required/accepted.");
        Console.WriteLine("PASS: issue identity/evidence, immutable findings, indexes, dispositions/history, atomic targeted revalidation, capability degradation, concurrency, cancellation, snapshot carry-over.");
    }
}
