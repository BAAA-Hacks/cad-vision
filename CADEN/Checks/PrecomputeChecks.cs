using Core.Diagnostics;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Project;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class PrecomputeChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task RunAsync()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scope_assembly_metadata.json"));
        var cached = LoadProject.Load(json, new ProjectLoadOptions { Precompute = new PrecomputeOptions { MaxScopeMemberships = 1000, OptionalStartupMilliseconds = 10000 } }).Snapshot!;
        var fallback = LoadProject.Load(json, new ProjectLoadOptions { Precompute = new PrecomputeOptions { MaxScopeMemberships = 0, OptionalStartupMilliseconds = 0 } }).Snapshot!;
        Require(cached.Indexes.CachedScopeCount == 5 && fallback.Indexes.CachedScopeCount == 0, "Optional scope cache budgets ignored.");
        var tiny = LoadProject.Load(json, new ProjectLoadOptions { Precompute = new PrecomputeOptions { MaxScopeMemberships = 3, OptionalStartupMilliseconds = 10000 } }).Snapshot!;
        Require(tiny.Indexes.CachedMembershipCount <= 3, "Membership budget exceeded.");
        Require(!LoadProject.Load(json, new ProjectLoadOptions { Precompute = new PrecomputeOptions { MaxScopeMemberships = -1 } }).Success, "Invalid budget accepted.");
        var association = new ProjectAssociation("precompute");
        var a = SemanticQueryTools.Create(cached, association); var b = SemanticQueryTools.Create(fallback, association);
        JObject Args(JObject? fields = null)
        { var args = fields == null ? new JObject() : (JObject)fields.DeepClone(); args["projectId"] = association.ProjectId; args["snapshotId"] = cached.SnapshotId; return args; }
        JObject Normalize(JObject result) { var copy = (JObject)result.DeepClone(); copy.Remove("scope"); return copy; }
        foreach (var id in new[] { "R", "DRIVE", "GEAR", "SHAFT", "SUPPORT" })
        {
            await a.ExecuteAsync("set_scope", Args(new JObject { ["objectId"] = id }));
            await b.ExecuteAsync("set_scope", Args(new JObject { ["objectId"] = id }));
            Require(a.Scopes!.Active!.ObjectCount == b.Scopes!.Active!.ObjectCount, "Cached scope count changed.");
            foreach (var query in new[] {
                ("find_connections", new JObject { ["objectId"] = id, ["relation"] = "all", ["includeSuppressed"] = true }),
                ("find_objects", new JObject { ["query"] = "a" }),
                ("get_mates", new JObject()),
                ("get_object_details", new JObject { ["objectIds"] = new JArray(id) }),
                ("query_hierarchy", new JObject { ["objectId"] = id, ["direction"] = "descendants", ["maxDepth"] = 20 }) })
                Require(JToken.DeepEquals(Normalize(await a.ExecuteAsync(query.Item1, Args(query.Item2))), Normalize(await b.ExecuteAsync(query.Item1, Args(query.Item2)))),
                    "Cached/fallback result differs: " + query.Item1 + "/" + id);
        }
        var decl = a.Declarations; decl.Clear(); Require(a.Declarations.Count > 0, "Caller mutated cached declarations.");
        await a.ExecuteAsync("clear_scope", Args());
        var detailsArgs = Args(new JObject { ["objectIds"] = new JArray("SHAFT"), ["fields"] = new JArray("mass") });
        var details = await a.ExecuteAsync("get_object_details", detailsArgs);
        details["data"]!["items"]![0]!["properties"]!["mass"]!["value"] = 999;
        Require((double)(await a.ExecuteAsync("get_object_details", detailsArgs))["data"]!["items"]![0]!["properties"]!["mass"]!["value"]! == 50, "Caller mutated cached property DTO.");
        var metrics = new List<JObject>(); ToolPerformanceLog.Configure(metrics.Add);
        try
        {
            await a.ExecuteAsync("get_object_details", detailsArgs);
            Require(metrics.Count == 1 && (int)metrics[0]["serializedBytes"]! > 0 && (double)metrics[0]["executionDurationMs"]! >= 0, "Tool timing missing.");
            Require(!metrics[0].ToString().Contains("SHAFT"), "Performance log leaked argument content.");
            ToolPerformanceLog.Configure(_ => throw new IOException("test sink"));
            Require((bool)(await a.ExecuteAsync("get_object_details", detailsArgs))["success"]!, "Telemetry failure broke successful query.");
        }
        finally { ToolPerformanceLog.Configure(null); }
        var storage = new IssueAccessChecks.Storage();
        var access = await Core.Tools.Issues.IssueAccess.OpenAsync(cached, association, storage);
        var issueTools = SemanticQueryTools.Create(cached, association, issues: access);
        var summary = await issueTools.ExecuteAsync("get_issue_summary", Args());
        var listed = await issueTools.ExecuteAsync("list_issues", Args());
        var finding = listed["data"]!["items"]!.First(i => (string?)i["checkerId"] == "constraint.under_defined");
        var change = Args(new JObject { ["issueId"] = finding["issueId"], ["disposition"] = "Ignored",
            ["expectedEvidenceHash"] = finding["evidenceHash"], ["expectedRevision"] = summary["data"]!["revision"], ["operationId"] = "summary-change" });
        storage.Fail = true;
        Require(!(bool)(await issueTools.ExecuteAsync("set_issue_disposition", change))["success"]!, "Expected persistence failure.");
        Require(JToken.DeepEquals(summary, await issueTools.ExecuteAsync("get_issue_summary", Args())), "Failed commit invalidated committed summary.");
        storage.Fail = false;
        Require((bool)(await issueTools.ExecuteAsync("set_issue_disposition", change))["success"]!, "Disposition commit failed.");
        var changed = await issueTools.ExecuteAsync("get_issue_summary", Args());
        Require((int)changed["data"]!["byDisposition"]!["Ignored"]! == 1 && (long)changed["data"]!["revision"]! > (long)summary["data"]!["revision"]!,
            "Summary cache survived committed revision.");
        Console.WriteLine("PASS: cached/fallback scope equivalence, budget limits, declaration/property clone isolation, tool performance metrics and sink-failure isolation.");
    }
}
