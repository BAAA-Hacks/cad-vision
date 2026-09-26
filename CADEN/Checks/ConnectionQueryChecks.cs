using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class ConnectionQueryChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task RunAsync()
    {
        var raw = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scope_assembly_metadata.json")));
        var snapshot = LoadProject.Load(raw.ToString()).Snapshot!;
        var association = new ProjectAssociation("connections-check");
        var tools = SemanticQueryTools.Create(snapshot, association);
        async Task<JObject> Call(JObject fields, string name = "find_connections")
        {
            fields["projectId"] = association.ProjectId; fields["snapshotId"] = snapshot.SnapshotId;
            return await tools.ExecuteAsync(name, fields);
        }
        JObject Target(string id, string relation = "boundary") => new() { ["objectId"] = id, ["relation"] = relation };
        var result = await Call(new JObject { ["query"] = "Drivetrain", ["otherQuery"] = "drone base" });
        Require((bool)result["success"]! && (int)result["pagination"]!["total"]! == 2, "Assembly boundary discovery failed: " + result);
        Require((int)result["data"]!["candidates"]![0]!["includedObjectCount"]! == 6 && (int)result["data"]!["otherQuery"]!["nameMatchCount"]! == 0, "Descendant expansion or unmatched hint failed.");
        Require(tools.Scopes!.Active == null, "Connection query changed scope.");
        Require((string?)result["data"]!["answerCoverage"]!["candidateDiscovery"]!["status"] == "supplied"
            && result["data"]!["answerCoverage"]!["retrievalGaps"]!.Count() == 0
            && !(bool)result["data"]!["answerCoverage"]!["exportedConnections"]!["absenceProven"]!,
            "Partial export confused with a repairable retrieval gap.");
        var all = await Call(Target("DRIVE", "all"));
        Require((int)all["pagination"]!["total"]! == 3, "Internal connections lost.");
        var inside = await Call(Target("DRIVE", "internal"));
        Require((int)inside["pagination"]!["total"]! == 1 && JToken.DeepEquals(inside["coverage"]!["evaluatedCount"], all["coverage"]!["evaluatedCount"]), "Relation filter changed underlying evaluation count.");
        Require((int)(await Call(Target("SHAFT")))["pagination"]!["total"]! == 2, "Part incident connections failed.");
        Require((int)(await Call(Target("R")))["pagination"]!["total"]! == 0, "Root boundary incorrectly includes internal mates.");
        var bolts = await Call(new JObject { ["query"] = "Bolt", ["candidateLimit"] = 1 });
        Require((int)bolts["data"]!["candidateCount"]! == 2 && (bool)bolts["data"]!["candidatesTruncated"]!, "Repeated occurrence truncation concealed.");
        Require(bolts["data"]!["answerCoverage"]!["retrievalGaps"]!.Values<string>().Contains("NARROW_TRUNCATED_CANDIDATES"), "Truncated candidates reported ready.");
        var page = Target("DRIVE"); page["limit"] = 1;
        var first = await Call(page); string cursor = (string)first["pagination"]!["nextCursor"]!;
        Require(first["data"]!["answerCoverage"]!["retrievalGaps"]!.Values<string>().Contains("PAGE_REMAINING_CONNECTIONS_IF_NEEDED"), "Paging evidence gap hidden.");
        var nextArgs = Target("DRIVE"); nextArgs["limit"] = 1; nextArgs["cursor"] = cursor;
        var next = await Call(nextArgs);
        Require((bool)next["success"]! && (string?)first["data"]!["items"]![0]!["mateId"] != (string?)next["data"]!["items"]![0]!["mateId"], "Cursor repeats connection.");
        await Call(new JObject { ["objectId"] = "DRIVE" }, "set_scope");
        Require((string?)(await Call(nextArgs))["errors"]?[0]?["code"] == "INVALID_CURSOR", "Connection cursor survived scope change.");
        await Call(new JObject { ["objectId"] = "GEAR" }, "set_scope");
        var scoped = await Call(Target("GEAR"));
        Require(scoped["data"]!["items"]!.Any(m => m["endpoints"]!.Any(e => (string?)e["id"] == "BRACKET")), "Boundary endpoint context hidden.");
        Require((string?)(await Call(Target("BRACKET")))["errors"]?[0]?["code"] == "OUT_OF_SCOPE", "Explicit target escaped scope.");
        await Call(new JObject(), "clear_scope");
        Require((string?)(await Call(new JObject { ["query"] = "Bolt", ["objectId"] = "SHAFT" }))["errors"]?[0]?["code"] == "INVALID_ARGUMENT", "Ambiguous arguments accepted.");
        // Optional malformed values must not become usable engineering facts.
        raw["mates"]![0]!["alignment"] = 123;
        raw["mates"]![0]!["nativeErrorCode"] = "zero";
        raw["objects"]!.First(o => (string?)o["id"] == "DRIVE")["suppressed"] = true;
        snapshot = LoadProject.Load(raw.ToString()).Snapshot!;
        tools = SemanticQueryTools.Create(snapshot, association);
        Require((int)(await Call(Target("SHAFT")))["pagination"]!["total"]! == 0, "Suppressed ancestor ignored.");
        var included = Target("SHAFT"); included["includeSuppressed"] = true;
        var malformed = await Call(included);
        var m1 = malformed["data"]!["items"]!.First(m => (string?)m["mateId"] == "M1");
        Require((string?)m1["properties"]!["alignment"]!["status"] == "invalid" && (string?)m1["properties"]!["nativeErrorCode"]!["status"] == "invalid", "Malformed optional mate data trusted.");
        raw["objects"]!.First(o => (string?)o["id"] == "SHAFT")["definitionStatus"] = null;
        snapshot = LoadProject.Load(raw.ToString()).Snapshot!;
        tools = SemanticQueryTools.Create(snapshot, association);
        var missing = await Call(included);
        Require((int)missing["data"]!["answerCoverage"]!["definitionStates"]!["unestablishedCount"]! > 0, "Missing definition state reported as established.");
        var testCase = JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OrchestrationCases.RedstartConnections.json")))[0];
        JObject Trace(params string[] names) => new() { ["completed"] = true, ["exchanges"] = new JArray(names.Select(n =>
            new JObject { ["calls"] = new JArray(new JObject { ["name"] = n }), ["sentToolResults"] = new JArray() })) };
        Require((bool)OrchestrationAssessment.Assess(Trace("find_connections"), testCase)["passed"]!, "Expected single connection call rejected.");
        Require(!(bool)OrchestrationAssessment.Assess(Trace("find_connections", "find_objects"), testCase)["passed"]!
            && !(bool)OrchestrationAssessment.Assess(Trace("find_connections", "find_connections"), testCase)["passed"]!,
            "Redundant orchestration calls passed regression.");
        Console.WriteLine("PASS: one-call assembly/part connections, unmatched hints, boundary evidence, deduplication, pagination/scope guards, ancestor suppression and malformed evidence.");
    }
}
