using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Memory;
using Core.Primitives.Operations.Project;
using Core.Tools.Issues;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;
using Core;

internal static class IssueAccessChecks
{
    private sealed class ChatHandler : HttpMessageHandler
    {
        private readonly JObject args; internal int Calls;
        internal ChatHandler(JObject args) { this.args = args; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); Calls++;
            JObject content;
            if (Calls == 1)
            {
                Require(((JArray)payload["tools"]![0]!["functionDeclarations"]!).Any(d => (string?)d["name"] == "get_issue_summary"), "Gemini payload omitted issue tools.");
                content = new JObject { ["role"] = "model", ["parts"] = new JArray(new JObject { ["functionCall"] = new JObject { ["name"] = "get_issue_summary", ["args"] = args.DeepClone() } }) };
            }
            else
            {
                var response = payload["contents"]!.Last!["parts"]![0]!["functionResponse"]!["response"]!;
                Require((bool)response["success"]! && (int)response["data"]!["count"]! == 4, "Issue result was not delivered to chat.");
                content = new JObject { ["role"] = "model", ["parts"] = new JArray(new JObject { ["text"] = "Four findings; evaluation coverage is partial." }) };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new JObject { ["candidates"] = new JArray(new JObject { ["content"] = content, ["finishReason"] = "STOP" }) }.ToString()) };
        }
    }
    internal sealed class Storage : IMemoryPersistence
    {
        internal string? Content; internal int Writes; internal bool Fail; internal Action? AfterCommit;
        public string? Read() => Content;
        public void Commit(string? expected, string next)
        {
            if (Fail) throw new IOException("Synthetic storage failure.");
            if (Content != expected) throw new MemoryPersistenceConflictException("Synthetic external change.");
            Content = next; Writes++; AfterCommit?.Invoke();
        }
    }
    private sealed class Checker : ISubjectIssueChecker
    {
        public string Id { get; }
        public string Version => "1";
        public IssueSubjectKind SubjectKind => IssueSubjectKind.Object;
        public IssueCapabilities RequiredCapabilities => IssueCapabilities.Properties;
        internal bool Present = true; internal bool Unable; internal int Calls;
        internal Checker(string id) { Id = id; }
        public Task<SubjectCheckResult> EvaluateAsync(Core.Primitives.DataStructures.Project.ProjectSnapshot s, IssueSubject subject, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (Unable) return Task.FromResult(new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: "TEST_UNAVAILABLE"));
            string type = Id == "constraint.unsolvable" ? "unsolvable" : "over_defined";
            return Task.FromResult(new SubjectCheckResult(SubjectEvaluationStatus.Complete, Present && subject.Id == "A" ? new[] {
                new IssueFinding(subject.Key(Id, type), Version, s.ProjectId, s.SnapshotId, IssueSeverity.Warning, IssueScope.Object, new[] { subject.Id }, Array.Empty<string>(), new JObject { ["test"] = true }) } : Array.Empty<IssueFinding>()));
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var snapshot = LoadProject.Load("""
        {"schemaVersion":"2.1","project":{"id":"export","name":"Issue test","rootObjectId":"R"},"objects":[
        {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"],"suppressed":false},
        {"id":"A","name":"Shaft","type":"part","parentId":"R","childIds":[],"suppressed":false,"material":{"assigned":false},"definitionStatus":"under_defined"},
        {"id":"B","name":"Base","type":"part","parentId":"R","childIds":[],"suppressed":false,"material":{"assigned":false},"definitionStatus":"over_defined"}],
        "mates":[{"id":"M","componentIds":["A","B"],"suppressed":false}]}
        """).Snapshot!;
        var association = new ProjectAssociation("caden-test"); var storage = new Storage();
        var access = await IssueAccess.OpenAsync(snapshot, association, storage); var tools = SemanticQueryTools.Create(snapshot, association, issues: access);
        JObject Args() => new() { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId };
        async Task<JObject> Call(string name, JObject? extra = null) { var args = Args(); if (extra != null) args.Merge(extra); return await tools.ExecuteAsync(name, args); }
        Require(tools.Declarations.Count == 16 && storage.Writes == 1 && access.InitialFindingCount == 4, "Initial scan/registration did not run before chat.");
        Require(!tools.Declarations.Any(d => new[] { "create_issue", "delete_issue", "overwrite_issue" }.Contains((string?)d["name"])), "Forbidden mutation declared.");
        var summary = await Call("get_issue_summary"); long revision = (long)summary["data"]!["revision"]!;
        Require((int)summary["data"]!["count"]! == 4 && (string?)summary["coverage"]!["countUnit"] == "checker_subjects" && (string?)summary["coverage"]!["status"] == "partial", "Summary hides incomplete evaluation.");
        var page = await Call("list_issues", new JObject { ["limit"] = 1 }); string id = (string)page["data"]!["items"]![0]!["issueId"]!;
        var handler = new ChatHandler(Args()); using var http = new HttpClient(handler);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("offline-dummy-key", "test-model", "Test prompt"), tools));
        Require((await chat.SendAsync("What issues were found?")).Contains("partial") && handler.Calls == 2 && chat.Messages.Count == 4, "Offline issue-to-chat dispatch failed.");
        string cursor = (string)page["pagination"]!["nextCursor"]!;
        var filtered = await Call("list_issues", new JObject { ["filters"] = new JObject { ["severity"] = "Info" } });
        Require((int)filtered["pagination"]!["total"]! == 0 && JToken.DeepEquals(summary["coverage"], filtered["coverage"]), "Finding filter changed evaluation coverage.");
        var detail = await Call("get_issue", new JObject { ["issueId"] = id });
        Require((bool)detail["data"]!["issue"]!["currentlyPresent"]! && (string?)detail["data"]!["issue"]!["verification"] == "verified", "Verification fields missing.");
        var evidence = await Call("get_issue_evidence", new JObject { ["issueId"] = id }); evidence["data"]!["issue"]!["evidence"]!["injected"] = true;
        Require((await Call("get_issue_evidence", new JObject { ["issueId"] = id }))["data"]!["issue"]!["evidence"]!["injected"] == null, "Read mutated evidence.");
        var mate = await Call("get_issues_for_mate", new JObject { ["mateId"] = "M" });
        Require((int)mate["pagination"]!["total"]! == 0 && (string?)mate["coverage"]!["status"] == "unavailable", "No mate checker falsely reported complete/clean.");
        Require((int)(await Call("get_issues_for_object", new JObject { ["objectId"] = "A" }))["pagination"]!["total"]! == 2, "Object issue lookup failed.");
        var action = Args(); action["issueId"] = id; action["operationId"] = "operation-1"; action["expectedRevision"] = revision;
        using var cancellation = new CancellationTokenSource(); storage.AfterCommit = cancellation.Cancel;
        var committed = await tools.ExecuteAsync("revalidate_issue", action, cancellation.Token); storage.AfterCommit = null;
        Require((bool)committed["success"]! && (string?)committed["receipt"]!["subsystem"] == "issues" && (string?)committed["data"]!["outcome"] == "Present", "Cancellation hid committed revalidation.");
        int writes = storage.Writes;
        var replay = await tools.ExecuteAsync("revalidate_issue", action, cancellation.Token);
        Require((bool)replay["receipt"]!["replayed"]! && storage.Writes == writes, "Retry reran action.");
        var conflict = (JObject)action.DeepClone(); conflict["issueId"] = "another";
        Require((string?)(await tools.ExecuteAsync("revalidate_issue", conflict))["errors"]![0]!["code"] == "IDEMPOTENCY_CONFLICT", "Reused operationId changed content.");
        Require((string?)(await Call("list_issues", new JObject { ["limit"] = 1, ["cursor"] = cursor }))["errors"]![0]!["code"] == "INVALID_CURSOR", "Stale issues cursor resumed after revalidation.");
        var failing = Args(); failing["objectId"] = "A"; failing["operationId"] = "fail"; failing["expectedRevision"] = (long)committed["receipt"]!["revision"]!;
        var before = await Call("get_issue_summary"); string? beforeDisk = storage.Content; storage.Fail = true;
        Require((string?)(await tools.ExecuteAsync("revalidate_object_issues", failing))["errors"]![0]!["code"] == "PERSISTENCE_FAILED", "Persistence failure not surfaced."); storage.Fail = false;
        Require(storage.Content == beforeDisk && JToken.DeepEquals(before, await Call("get_issue_summary")), "Failed save changed runtime evaluation/revision.");
        var resumed = await IssueAccess.OpenAsync(snapshot, association, storage); var reopened = SemanticQueryTools.Create(snapshot, association, issues: resumed);
        var durableReplay = await reopened.ExecuteAsync("revalidate_issue", action);
        Require((bool)durableReplay["receipt"]!["replayed"]! && (long)durableReplay["receipt"]!["revision"]! == (long)committed["receipt"]!["revision"]!, "Receipt lost across reopen.");
        var changed = Args(); changed["snapshotId"] = "stale"; changed["issueId"] = id;
        Require((string?)(await reopened.ExecuteAsync("get_issue", changed))["errors"]![0]!["code"] == "STALE_SNAPSHOT_REFERENCE", "Stale issue identity accepted.");
        var corrupt = new Storage { Content = "broken" }; try { await IssueAccess.OpenAsync(snapshot, association, corrupt); throw new Exception("Corrupt journal accepted."); } catch (Newtonsoft.Json.JsonException) { }
        Require(corrupt.Writes == 0 && corrupt.Content == "broken", "Corrupt journal overwritten.");
        // Disposition is durable user state, independent of evidence and finding presence.
        var dispositionArgs = Args(); dispositionArgs["issueId"] = id; dispositionArgs["disposition"] = "Ignored";
        dispositionArgs["expectedEvidenceHash"] = detail["data"]!["issue"]!["evidenceHash"]!.DeepClone();
        dispositionArgs["expectedRevision"] = (await reopened.ExecuteAsync("get_issue_summary", Args()))["data"]!["revision"]!.DeepClone();
        dispositionArgs["operationId"] = "ignore"; dispositionArgs["reason"] = "Intentional design condition.";
        var invalidState = (JObject)dispositionArgs.DeepClone(); invalidState["disposition"] = "Deleted";
        Require(!(bool)(await reopened.ExecuteAsync("set_issue_disposition", invalidState))["success"]!, "Invalid disposition accepted.");
        var staleEvidence = (JObject)dispositionArgs.DeepClone(); staleEvidence["expectedEvidenceHash"] = "stale";
        Require((string?)(await reopened.ExecuteAsync("set_issue_disposition", staleEvidence))["errors"]![0]!["code"] == "EVIDENCE_CHANGED", "Stale evidence accepted.");
        var diskBeforeDisposition = storage.Content; storage.Fail = true;
        Require((string?)(await reopened.ExecuteAsync("set_issue_disposition", dispositionArgs))["errors"]![0]!["code"] == "PERSISTENCE_FAILED", "Disposition persistence failure hidden.");
        storage.Fail = false;
        var readArgs = Args(); readArgs["issueId"] = id;
        Require(storage.Content == diskBeforeDisposition && (string?)(await reopened.ExecuteAsync("get_issue", readArgs))["data"]!["issue"]!["disposition"] == "Open", "Failed disposition save changed state.");
        using var dispositionCancellation = new CancellationTokenSource(); storage.AfterCommit = dispositionCancellation.Cancel;
        var ignored = await reopened.ExecuteAsync("set_issue_disposition", dispositionArgs, dispositionCancellation.Token); storage.AfterCommit = null;
        Require((bool)ignored["success"]! && (string?)ignored["data"]!["disposition"] == "Ignored", "Committed disposition lost to cancellation.");
        var reopenedDisposition = SemanticQueryTools.Create(snapshot, association, issues: await IssueAccess.OpenAsync(snapshot, association, storage));
        var restored = await reopenedDisposition.ExecuteAsync("get_issue", readArgs);
        Require((string?)restored["data"]!["issue"]!["disposition"] == "Ignored" && (bool)restored["data"]!["issue"]!["currentlyPresent"]! && JToken.DeepEquals(restored["data"]!["issue"]!["evidenceHash"], detail["data"]!["issue"]!["evidenceHash"]), "Disposition did not survive restart or changed evidence.");
        Require((bool)(await reopenedDisposition.ExecuteAsync("set_issue_disposition", dispositionArgs))["receipt"]!["replayed"]!, "Disposition receipt did not survive restart.");
        foreach (string state in new[] { "Resolved", "Open" })
        {
            dispositionArgs["disposition"] = state; dispositionArgs["operationId"] = "state-" + state;
            dispositionArgs["expectedRevision"] = (await reopenedDisposition.ExecuteAsync("get_issue_summary", Args()))["data"]!["revision"]!.DeepClone();
            Require((bool)(await reopenedDisposition.ExecuteAsync("set_issue_disposition", dispositionArgs))["success"]!, "Disposition transition failed.");
            reopenedDisposition = SemanticQueryTools.Create(snapshot, association, issues: await IssueAccess.OpenAsync(snapshot, association, storage));
            Require((string?)(await reopenedDisposition.ExecuteAsync("get_issue", readArgs))["data"]!["issue"]!["disposition"] == state, "Latest disposition was not restored.");
        }
        // Subject-local precedence peers must be rerun together, with candidates retained.
        var winner = new Checker("constraint.unsolvable"); var loser = new Checker("constraint.over_defined");
        var custom = await IssueAccess.OpenAsync(snapshot, association, new Storage(), engine: new IssueEngine(new[] { winner, loser }));
        var customTools = SemanticQueryTools.Create(snapshot, association, issues: custom);
        var allArgs = Args(); allArgs["includeSuppressedCandidates"] = true;
        var candidates = await customTools.ExecuteAsync("list_issues", allArgs);
        Require((int)candidates["pagination"]!["total"]! == 2 && ((JArray)candidates["data"]!["items"]!).Count(p => (bool)p["presented"]!) == 1, "Precedence candidates lost.");
        var winnerRow = ((JArray)candidates["data"]!["items"]!).Single(p => (string?)p["checkerId"] == winner.Id);
        var customAction = Args(); customAction["issueId"] = winnerRow["issueId"]!.DeepClone(); customAction["expectedRevision"] = candidates["data"]!["revision"]!.DeepClone(); customAction["operationId"] = "remove-winner";
        winner.Present = false; int loserCalls = loser.Calls;
        var removed = await customTools.ExecuteAsync("revalidate_issue", customAction);
        Require((bool)removed["success"]! && (string?)removed["data"]!["outcome"] == "Absent" && loser.Calls == loserCalls + 1, "Revalidation failed to rerun precedence group.");
        var remaining = await customTools.ExecuteAsync("list_issues", Args()); Require((int)remaining["pagination"]!["total"]! == 1 && (bool)remaining["data"]!["items"]![0]!["presented"]!, "Generic candidate did not reappear.");
        winner.Unable = loser.Unable = true;
        var unavailable = Args(); unavailable["objectId"] = "A"; unavailable["operationId"] = "unable"; unavailable["expectedRevision"] = removed["receipt"]!["revision"]!.DeepClone();
        var unable = await customTools.ExecuteAsync("revalidate_object_issues", unavailable);
        Require((bool)unable["success"]! && (string?)unable["data"]!["evaluationStatus"] == "UnableToEvaluate", "Committed UnableToEvaluate was called execution failure.");
        Console.WriteLine("PASS: initial issue scan, six reads/three actions, durable dispositions, evidence isolation, coverage/filtering, precedence, durable replay, cancellation, persistence rollback and revision-bound cursors.");
    }
}
