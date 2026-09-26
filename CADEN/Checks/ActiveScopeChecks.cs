using Core;
using Core.Tools;
using Core.Tools.Query;
using Core.Tools.Issues;
using Core.Tools.Memory;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;

internal static class ActiveScopeChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static string? Error(JObject r) => (string?)r["errors"]?[0]?["code"];
    internal static async Task RunAsync()
    {
        var raw = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scope_assembly_metadata.json")));
        var loaded = LoadProject.Load(raw.ToString()); Require(loaded.Success, "Scope fixture failed import."); var snapshot = loaded.Snapshot!;
        var association = new ProjectAssociation("scope-check");
        var issues = await IssueAccess.OpenAsync(snapshot, association, new IssueAccessChecks.Storage());
        var memory = MemoryAccess.Open(snapshot, association, new IssueAccessChecks.Storage());
        var tools = SemanticQueryTools.Create(snapshot, association, issues: issues, memory: memory);
        JObject Args() => new() { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId };
        async Task<JObject> Call(string name, JObject? extra = null)
        { var args = Args(); if (extra != null) foreach (var p in extra.Properties()) args[p.Name] = p.Value.DeepClone(); return await tools.ExecuteAsync(name, args); }
        async Task<JObject> Set(string id) => await Call("set_scope", new JObject { ["objectId"] = id });
        var all = await Call("find_objects", new JObject { ["query"] = "Bolt", ["limit"] = 1 }); string cursor = (string)all["pagination"]!["nextCursor"]!;
        Require((int)all["pagination"]!["total"]! == 2 && !(bool)all["scope"]!["active"]!, "No-scope semantics changed.");
        var scope = await Set("DRIVE"); Require((int)scope["data"]!["objectCount"]! == 6, "Assembly did not resolve root plus all descendants.");
        string scopeId = (string)scope["scope"]!["scopeId"]!;
        Require((string?)scope["scope"]!["type"] == "Subassembly", "Nested assembly scope type lost.");
        var cancelledArgs = Args(); cancelledArgs["objectId"] = "FRAME";
        using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); Require(Error(await tools.ExecuteAsync("set_scope", cancelledArgs, cancellation.Token)) == "CANCELLED", "Cancelled scope resolution reported success."); }
        Require((string?)tools.Scopes!.Describe()["scopeId"] == scopeId, "Cancelled scope change discarded previous state.");
        Require(Error(await Set("missing")) == "UNKNOWN_OBJECT_ID" && (string?)tools.Scopes!.Describe()["scopeId"] == scopeId, "Failed scope change replaced previous scope.");
        var outside = await Call("get_object_details", new JObject { ["objectIds"] = new JArray("BRACKET") });
        Require(Error(outside) == "OUT_OF_SCOPE", "Explicit external target silently accepted.");
        Require(Error(await Call("find_objects", new JObject { ["query"] = "Bolt", ["limit"] = 1, ["cursor"] = cursor })) == "INVALID_CURSOR", "Old unscoped cursor accepted after scope change.");
        var parts = await Call("find_objects", new JObject { ["property"] = "type", ["operator"] = "equals", ["value"] = new JObject { ["text"] = "part" }, ["limit"] = 1 });
        Require((int)parts["pagination"]!["total"]! == 4 && ((JArray)parts["data"]!["items"]!).Count == 1 && (int)parts["coverage"]!["scopeCount"]! == 6, "Scope applied after pagination or coverage changed with property filter.");
        var nested = await Call("query_hierarchy", new JObject { ["objectId"] = "DRIVE", ["direction"] = "descendants", ["maxDepth"] = 8 });
        Require((int)nested["pagination"]!["total"]! == 5, "Nested assembly descendants missing.");
        var ancestors = await Call("query_hierarchy", new JObject { ["objectId"] = "DRIVE", ["direction"] = "ancestors", ["maxDepth"] = 8 });
        Require((int)ancestors["pagination"]!["total"]! == 0 && (bool)ancestors["coverage"]!["activeScopeBoundaryApplied"]!, "Ancestor query escaped scope.");
        var mates = await Call("get_mates");
        Require((int)mates["pagination"]!["total"]! == 3 && mates["data"]!["items"]!.Count(m => (string?)m["relationScope"] == "Internal") == 1
            && mates["data"]!["items"]!.Count(m => (string?)m["relationScope"] == "Boundary") == 2, "Boundary/internal classification failed.");
        var graphArgs = new JObject { ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["startObjectId"] = "MOTOR", ["endObjectId"] = "BOLT_A" };
        var path = await Call("find_mechanical_path", graphArgs);
        Require((string?)path["data"]!["pathStatus"] == "NotEstablished" && (string?)path["data"]!["reasonCode"] == "ACTIVE_SCOPE_RESTRICTED", "Path escaped through external bracket or claimed global disconnection.");
        var neighborhood = await Call("get_mechanical_neighborhood", new JObject { ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["startObjectIds"] = new JArray("MOTOR"), ["maxHops"] = 8 });
        Require(neighborhood["data"]!["objects"]!.Count() == 2 && neighborhood["data"]!["objects"]!.All(o => (string?)o["id"] != "BRACKET"), "Neighborhood escaped scope.");
        var scopedIssues = await Call("list_issues");
        Require(scopedIssues["data"]!["items"]!.All(i => i["objects"]!.Any(o => tools.Scopes!.Active!.Includes((string)o["id"]!))), "Unrelated findings returned.");
        var filtered = await Call("list_issues", new JObject { ["filters"] = new JObject { ["severity"] = "Error" } });
        Require(JToken.DeepEquals(filtered["coverage"], scopedIssues["coverage"]), "Issue filtering changed evaluation coverage.");
        Require(Error(await Call("get_project_memory")) == "SCOPE_NOT_SUPPORTED" && Error(await Call("get_diagnostics")) == "SCOPE_NOT_SUPPORTED", "Unmigrated tool silently returned global data.");
        var summary = await tools.ExecuteAsync("get_model_summary", new JObject());
        Require((int)summary["data"]!["objectCount"]! == 11 && (string?)summary["scopeBehavior"] == "global_discovery", "Model summary no longer explicit global discovery.");
        await Call("clear_scope"); var globalPath = await Call("find_mechanical_path", graphArgs);
        Require((string?)globalPath["data"]!["pathStatus"] == "Found" && (int)globalPath["data"]!["hopCount"]! == 3, "Clearing scope did not restore cross-boundary path.");
        Require(Error(await Call("find_objects", new JObject { ["query"] = "Bolt", ["limit"] = 1, ["cursor"] = cursor })) == "INVALID_CURSOR", "Clear scope revived pre-change cursor.");
        var globalIssues = await Call("list_issues"); var bracketIssue = globalIssues["data"]!["items"]!.First(i => i["objects"]!.Any(o => (string?)o["id"] == "BRACKET"));
        await Set("DRIVE"); Require(Error(await Call("get_issue", new JObject { ["issueId"] = bracketIssue["issueId"] })) == "OUT_OF_SCOPE", "Issue ID bypassed scope.");
        Require(Error(await Call("set_issue_disposition", new JObject { ["issueId"] = bracketIssue["issueId"], ["disposition"] = "Ignored", ["expectedEvidenceHash"] = bracketIssue["evidenceHash"], ["expectedRevision"] = globalIssues["data"]!["revision"], ["operationId"] = "outside" })) == "OUT_OF_SCOPE", "Issue mutation bypassed scope.");
        await Set("SHAFT"); Require(tools.Scopes!.Active!.ObjectCount == 1, "Part scope expanded ancestors.");
        var partMates = await Call("get_mates"); Require(partMates["data"]!["items"]!.All(m => (string?)m["relationScope"] == "Boundary"), "Part relationship classification failed.");
        var shaftIssues = await Call("list_issues"); var shaftIssue = shaftIssues["data"]!["items"]!.First(i => (string?)i["checkerId"] == "constraint.under_defined");
        var mutation = new JObject { ["issueId"] = shaftIssue["issueId"], ["disposition"] = "Ignored", ["expectedEvidenceHash"] = shaftIssue["evidenceHash"], ["expectedRevision"] = shaftIssues["data"]!["revision"], ["operationId"] = "scoped-commit" };
        var committed = await Call("set_issue_disposition", mutation);
        Require((bool)committed["success"]! && (string?)committed["data"]!["scopeAtCommit"]!["rootId"] == "SHAFT", "Scoped action lost original scope provenance.");
        await Set("FRAME"); var replay = await Call("set_issue_disposition", mutation);
        Require((bool)replay["receipt"]!["replayed"]! && (string?)replay["data"]!["scopeAtCommit"]!["rootId"] == "SHAFT" && ((string)replay["scopeBehavior"]!).StartsWith("historical"), "Scope switch prevented committed receipt recovery or relabelled original action scope.");
        var client = new GeminiClient(new HttpClient(), new GeminiSettings("offline", "test", "test"), tools); new ChatSession(client).Clear();
        Require(tools.Scopes.Active == null, "New chat failed to reset scope.");
        var invalid = (JObject)raw.DeepClone(); invalid["objects"]![1]!["childIds"] = new JArray("UNKNOWN");
        var bad = LoadProject.Load(invalid.ToString());
        if (bad.Success)
        {
            var broken = SemanticQueryTools.Create(bad.Snapshot, association);
            var args = new JObject { ["projectId"] = association.ProjectId, ["snapshotId"] = bad.Snapshot!.SnapshotId, ["objectId"] = "DRIVE" };
            Require(Error(await broken.ExecuteAsync("set_scope", args)) == "CAPABILITY_UNAVAILABLE", "Malformed hierarchy accepted as complete scope.");
        }
        Console.WriteLine("PASS: assembly/part scope resolution, exact target guards, boundary mates, internal traversal, pre-pagination filtering, scoped issue coverage, action guards, cursor invalidation, reset and unsupported-tool failures.");
    }
}
