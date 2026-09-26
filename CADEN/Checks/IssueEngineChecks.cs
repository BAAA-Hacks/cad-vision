using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Issues.Checkers;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;

internal static class IssueEngineChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Document() => JObject.Parse("""
    {"schemaVersion":"1.0","project":{"id":"P","name":"Checks","rootObjectId":"R"},
     "objects":[
       {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B","C","D","E"],"suppressed":false,"definitionStatus":"fully_defined"},
       {"id":"A","name":"A","type":"part","parentId":"R","childIds":[],"suppressed":false,"definitionStatus":"under_defined","material":{"assigned":false}},
       {"id":"B","name":"B","type":"part","parentId":"R","childIds":[],"suppressed":false,"definitionStatus":"over_defined","material":{"assigned":true,"name":"Steel"}},
       {"id":"C","name":"C","type":"part","parentId":"R","childIds":[],"suppressed":true,"definitionStatus":"no_solution","material":{"assigned":false}},
       {"id":"D","name":"D","type":"part","parentId":"R","childIds":[],"definitionStatus":"under_defined","material":{"assigned":false}},
       {"id":"E","name":"E","type":"part","parentId":"R","childIds":[],"suppressed":false,"definitionStatus":"no_solution","material":{"assigned":true,"name":"Steel"}}
     ],"mates":[{"id":"M","componentIds":["A","B"],"suppressed":false}]}
    """);
    private static ProjectSnapshot Load(JObject? document = null)
    {
        var load = LoadProject.Load((document ?? Document()).ToString(), new ProjectLoadOptions { MateExportState = CapabilityState.Available });
        Require(load.Success, "Engine fixture failed to load."); return load.Snapshot!;
    }
    private static IssueSubject Object(string id) => new(IssueSubjectKind.Object, id);
    private static IssueFinding Finding(ProjectSnapshot snapshot, IssueSubject subject, string checker, string type = "test", int value = 1, string version = "1") =>
        new(subject.Key(checker, type), version, snapshot.ProjectId, snapshot.SnapshotId, IssueSeverity.Warning,
            subject.Kind == IssueSubjectKind.Object ? IssueScope.Object : IssueScope.Mate,
            subject.Kind == IssueSubjectKind.Object ? new[] { subject.Id } : new[] { "A", "B" },
            subject.Kind == IssueSubjectKind.Mate ? new[] { subject.Id } : Array.Empty<string>(), new JObject { ["value"] = value });
    private sealed class Checker : ISubjectIssueChecker
    {
        public string Id { get; }
        public string Version => "1";
        public IssueSubjectKind SubjectKind { get; }
        public IssueCapabilities RequiredCapabilities { get; set; } = IssueCapabilities.Properties;
        public int Calls;
        public Func<ProjectSnapshot, IssueSubject, Task<SubjectCheckResult>> Evaluate { get; set; }
        public Checker(string id, IssueSubjectKind kind = IssueSubjectKind.Object)
        { Id = id; SubjectKind = kind; Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, Id))); }
        public Task<SubjectCheckResult> EvaluateAsync(ProjectSnapshot snapshot, IssueSubject subject, CancellationToken cancellationToken)
        { Calls++; return Evaluate(snapshot, subject); }
    }
    private static SubjectCheckResult Complete(params IssueFinding[] findings) => new(SubjectEvaluationStatus.Complete, findings);
    private static SubjectCheckResult Unable() => new(SubjectEvaluationStatus.UnableToEvaluate, reason: "data missing");

    public static async Task RunAsync()
    {
        var snapshot = Load(); var store = new IssueStore(snapshot); var engine = InitialIssueCheckers.CreateEngine();
        var result = await engine.ScanAsync(store);
        Require((await engine.ScanAsync(store, Array.Empty<IssueSubject>())).Value!.Status == IssueScanStatus.NotEvaluated, "Empty scan implied successful coverage.");
        Require(result.Success && result.Value!.Status == IssueScanStatus.Partial && result.Value.Evaluations.Count == 24, "Discovery did not report subject-level coverage.");
        Require(store.Count == 4 && store.ByObject("A").Count == 2 && store.ByObject("B").Count == 1 && store.ByObject("E").Count == 1, "Initial checker triggers wrong.");
        Require(store.ByObject("C").Count == 0 && store.ByObject("D").Count == 0, "Suppressed or unknown-suppression object treated as active.");
        Require(store.GetEvaluation("material.missing", Object("C"))!.Status == SubjectEvaluationStatus.Complete && store.GetEvaluation("material.missing", Object("D"))!.Status == SubjectEvaluationStatus.UnableToEvaluate, "Suppressed applicability and unknown suppression conflated.");
        var under = store.GetFinding(Object("A").Key("constraint.under_defined", "under_defined")).Value!;
        Require(under.Severity == IssueSeverity.Question && under.RequiresDesignIntent && under.Evidence["activeMates"]!["state"]!.Value<string>() == "Unavailable", "Under-defined semantics invented mate coverage.");
        Require(store.GetFinding(Object("E").Key("constraint.unsolvable", "unsolvable")).Value!.Severity == IssueSeverity.Error, "Unsolvable native status not Error.");
        Require(store.GetPresentation().All(p => p.IsVerified && p.IsPresented), "Initial presentation freshness incorrect.");
        store.SetDisposition(under.Key, IssueDispositionState.Ignored, "shaft rotates", under.EvidenceHash);
        Require((await engine.RevalidateAsync(store, under.Key)).Success && store.GetDisposition(under.Key).Value!.State == IssueDispositionState.Ignored, "Same evidence lost acceptance.");
        Require((await RevalidateIssue.RunAsync(store, under.Key, new IssueCheckerRegistry(Array.Empty<IIssueChecker>()))).ErrorCode == "USE_ISSUE_ENGINE", "Legacy revalidation bypassed scan coverage.");
        var native = Document(); native["objects"]![0]!["definitionStatus"] = "invalid_solution";
        native["mates"]![0]!["componentIds"] = new JArray("A", "missing");
        var degraded = new IssueStore(Load(native)); await engine.ScanAsync(degraded);
        Require(degraded.Snapshot.Capabilities.MechanicalGraph == CapabilityState.Invalid && degraded.ByObject("A").Count == 2 && degraded.GetFinding(Object("R").Key("constraint.unsolvable", "unsolvable")).Value!.Scope == IssueScope.Assembly, "Invalid graph blocked properties or assembly scope wrong.");
        native = Document(); native["objects"]![1]!["definitionStatus"] = "unrecognized"; native["objects"]![1]!["material"] = new JObject { ["assigned"] = "false" };
        var malformed = new IssueStore(Load(native)); await engine.ScanAsync(malformed);
        Require(malformed.ByObject("A").Count == 0 && malformed.GetEvaluation("material.missing", Object("A"))!.Status == SubjectEvaluationStatus.UnableToEvaluate, "Malformed data created a physical finding.");
        native = Document(); native["sampleInfo"] = new JObject { ["fixture"] = true };
        var fixture = new IssueStore(Load(native)); await engine.ScanAsync(fixture); Require(fixture.Count == 0, "Fixture placeholders produced findings.");

        // Exact subject reconciliation: complete absence clears A, unknown B is retained.
        var checker = new Checker("coverage"); var coverageEngine = new IssueEngine(new[] { checker }); var coverageStore = new IssueStore(snapshot);
        var a = Object("A"); var b = Object("B");
        await coverageEngine.ScanAsync(coverageStore, new[] { a, b, a }); Require(checker.Calls == 2 && coverageStore.Count == 2, "Duplicate targets executed twice.");
        var bFinding = coverageStore.GetFinding(b.Key("coverage", "test")).Value!;
        coverageStore.SetDisposition(bFinding.Key, IssueDispositionState.Ignored, "intentional", bFinding.EvidenceHash);
        checker.Evaluate = (_, target) => Task.FromResult(target.Id == "A" ? Complete() : Unable());
        var partial = await coverageEngine.ScanAsync(coverageStore, new[] { a, b });
        Require(partial.Value!.Status == IssueScanStatus.Partial && coverageStore.Count == 1 && coverageStore.GetFinding(bFinding.Key).Success && coverageStore.GetDisposition(bFinding.Key).Value!.State == IssueDispositionState.Ignored && !coverageStore.GetPresentation().Single().IsVerified, "Partial run removed or changed unevaluable subject.");
        var lastB = coverageStore.GetEvaluation("coverage", b);
        await coverageEngine.ScanAsync(coverageStore, new[] { a });
        Require(ReferenceEquals(lastB, coverageStore.GetEvaluation("coverage", b)), "Unreported subject coverage changed.");
        checker.Evaluate = (_, _) => throw new InvalidOperationException("not exported");
        var failed = await coverageEngine.ScanAsync(coverageStore, new[] { b });
        Require(failed.Value!.Status == IssueScanStatus.Failed && coverageStore.Count == 1 && coverageStore.GetEvaluation("coverage", b)!.Reason.Contains("InvalidOperationException"), "Failure cleared finding or lost subject diagnostics.");
        checker.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, "coverage", value: 2)));
        await coverageEngine.ScanAsync(coverageStore, new[] { b });
        Require(coverageStore.GetDisposition(bFinding.Key).Value!.State == IssueDispositionState.Open && coverageStore.GetPresentation().Single().IsVerified, "Changed evidence did not reopen/reverify.");
        var before = coverageStore.GetFinding(bFinding.Key).Value!;
        checker.Evaluate = (s, target) => Task.FromResult(target.Id == "A" ? Complete(Finding(s, target, "coverage")) : Complete(Finding(s, a, "coverage")));
        Require((await coverageEngine.ScanAsync(coverageStore, new[] { a, b })).ErrorCode == "INVALID_CHECK_RESULT" && coverageStore.Count == 1 && coverageStore.GetFinding(bFinding.Key).Value!.EvidenceHash == before.EvidenceHash, "Invalid subject batch partially committed.");
        checker.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, "coverage"), Finding(s, target, "coverage")));
        Require((await coverageEngine.ScanAsync(coverageStore, new[] { b })).ErrorCode == "INVALID_CHECK_RESULT", "Duplicate candidates accepted.");
        Require((await coverageEngine.ScanAsync(coverageStore, new[] { Object("missing") })).ErrorCode == "INVALID_SUBJECT", "Unknown target accepted.");
        var unknown = await coverageEngine.ScanAsync(coverageStore, new[] { b }, new[] { "not.registered" });
        Require(unknown.Value!.Evaluations.Single().Status == SubjectEvaluationStatus.UnableToEvaluate, "Unknown checker implied absence.");

        // Precedence retains candidates/dispositions, and only verified causes may suppress.
        var mate = new IssueSubject(IssueSubjectKind.Mate, "M");
        var error = new Checker("mate.error", IssueSubjectKind.Mate);
        var dangling = new Checker("mate.dangling", IssueSubjectKind.Mate);
        error.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, error.Id, "error")));
        dangling.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, dangling.Id, "dangling")));
        var mateEngine = new IssueEngine(new[] { error, dangling }); var mates = new IssueStore(snapshot);
        await mateEngine.ScanAsync(mates, new[] { mate }, new[] { error.Id });
        Require(error.Calls == 1 && dangling.Calls == 1 && mates.Count == 2, "Scan did not expand precedence group.");
        var generic = mate.Key(error.Id, "error"); var specific = mate.Key(dangling.Id, "dangling");
        var genericFinding = mates.GetFinding(generic).Value!;
        mates.SetDisposition(generic, IssueDispositionState.Ignored, "accepted", genericFinding.EvidenceHash);
        Require(!mates.GetPresentation().Single(p => p.Finding.Key == generic).IsPresented && mates.ByMate("M").Count == 2 && mates.ByDisposition(IssueDispositionState.Ignored).Single() == generic, "Precedence deleted or ignored candidates.");
        dangling.Evaluate = (_, _) => Task.FromResult(Unable());
        await mateEngine.RevalidateAsync(mates, generic);
        Require(error.Calls == 2 && dangling.Calls == 2 && mates.Count == 2 && mates.GetPresentation().All(p => p.IsPresented) && !mates.GetPresentation().Single(p => p.Finding.Key == specific).IsVerified, "Stale cause suppressed a fresh finding.");
        Require(mates.GetDisposition(generic).Value!.State == IssueDispositionState.Ignored, "Presentation changed disposition.");
        dangling.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, dangling.Id, "dangling")));
        await mateEngine.RevalidateAsync(mates, generic);
        Require(!mates.GetPresentation().Single(p => p.Finding.Key == generic).IsPresented, "Fresh cause failed to restore precedence.");
        var missingPeerEngine = new IssueEngine(new[] { error });
        await missingPeerEngine.RevalidateAsync(mates, generic);
        Require(mates.GetPresentation().All(p => p.IsPresented) && mates.GetEvaluation(dangling.Id, mate)!.Reason == "CHECKER_UNAVAILABLE", "Unavailable group member retained suppression power.");
        dangling.Evaluate = (_, _) => Task.FromResult(Complete());
        await mateEngine.RevalidateAsync(mates, generic);
        Require(!mates.GetFinding(specific).Success && mates.GetPresentation().Single().IsPresented, "Generic finding failed to reappear after specific absence.");

        // A failed group member does not discard a successful peer, and targets stay local.
        var unsolvable = new Checker("constraint.unsolvable"); var over = new Checker("constraint.over_defined");
        unsolvable.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, unsolvable.Id, "unsolvable")));
        over.Evaluate = (s, target) => Task.FromResult(Complete(Finding(s, target, over.Id, "over_defined")));
        var groupEngine = new IssueEngine(new[] { unsolvable, over }); var groupStore = new IssueStore(snapshot);
        await groupEngine.ScanAsync(groupStore, new[] { a, b });
        var bEval = groupStore.GetEvaluation(unsolvable.Id, b);
        unsolvable.Evaluate = (_, _) => throw new InvalidOperationException();
        await groupEngine.RevalidateAsync(groupStore, a.Key(over.Id, "over_defined"));
        Require(groupStore.GetPresentation("A").All(p => p.IsPresented) && groupStore.GetPresentation("B").Count(p => p.IsPresented) == 1 && ReferenceEquals(bEval, groupStore.GetEvaluation(unsolvable.Id, b)), "Targeted group revalidation affected another subject.");

        // No late commits after concurrent decisions or cancellation.
        var pending = new TaskCompletionSource<SubjectCheckResult>(); checker.Evaluate = (_, _) => pending.Task;
        var running = coverageEngine.ScanAsync(coverageStore, new[] { b });
        coverageStore.SetDisposition(before.Key, IssueDispositionState.Resolved, "decision", before.EvidenceHash);
        pending.SetResult(Complete());
        Require((await running).ErrorCode == "STORE_CHANGED" && coverageStore.GetFinding(before.Key).Success && coverageStore.GetDisposition(before.Key).Value!.State == IssueDispositionState.Resolved, "Late scan overwrote decision.");
        using var cancellation = new CancellationTokenSource(); pending = new TaskCompletionSource<SubjectCheckResult>();
        running = coverageEngine.ScanAsync(coverageStore, new[] { b }, cancellationToken: cancellation.Token);
        cancellation.Cancel(); pending.SetResult(Complete());
        try { await running; throw new Exception("Canceled scan committed."); } catch (OperationCanceledException) { }
        Require(coverageStore.GetFinding(before.Key).Success, "Cancellation removed candidate.");
        var gated = new Checker("graph.only") { RequiredCapabilities = IssueCapabilities.MechanicalGraph };
        var gateEngine = new IssueEngine(new[] { gated });
        Require((await gateEngine.ScanAsync(degraded, new[] { a })).Value!.Status == IssueScanStatus.UnableToEvaluate && gated.Calls == 0, "Unavailable capability executed checker.");
        Console.WriteLine("PASS: initial issue discovery, per-subject coverage, suppression/fixture safeguards, candidate precedence/freshness, grouped revalidation, atomic scans, concurrency and cancellation.");
    }
}
