using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Core.Tools.Memory;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class MemoryToolChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var doc = JObject.Parse("""
        {"schemaVersion":"2.1","project":{"id":"export","name":"Test","rootObjectId":"R"},"objects":[
        {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"]},
        {"id":"A","name":"Shaft","type":"part","parentId":"R","childIds":[]},
        {"id":"B","name":"Base","type":"part","parentId":"R","childIds":[]}],"mates":[
        {"id":"M1","type":"coincident","componentIds":["A","B"],"suppressed":false},
        {"id":"M2","type":"parallel","componentIds":["A","B"],"suppressed":true},
        {"id":"M3","type":"concentric","componentIds":["A","B"],"suppressed":null}]}
        """);
        var snapshot = LoadProject.Load(doc.ToString()).Snapshot!; var association = new ProjectAssociation("test");
        var disk = new IssueAccessChecks.Storage(); var memory = MemoryAccess.Open(snapshot, association, disk);
        var tools = SemanticQueryTools.Create(snapshot, association, memory: memory);
        JObject Args() => new() { ["projectId"] = "test", ["snapshotId"] = snapshot.SnapshotId };
        async Task<JObject> Read() { var a = Args(); a["objectIds"] = new JArray("A", "B"); return await tools.ExecuteAsync("get_project_memory", a); }
        Require(tools.Declarations.Any(t => (string?)t["name"] == "get_mates") && tools.Declarations.Any(t => (string?)t["name"] == "write_project_memory"), "New tools not declared.");
        var matesArgs = Args(); matesArgs["objectIds"] = new JArray("A", "B"); matesArgs["limit"] = 1;
        var mates = await tools.ExecuteAsync("get_mates", matesArgs);
        Require((bool)mates["success"]! && (int)mates["pagination"]!["total"]! == 2 && (int)mates["coverage"]!["unknownSuppressionCount"]! == 1, "Mate filtering lost unknowns or duplicated shared mates.");
        matesArgs["cursor"] = mates["pagination"]!["nextCursor"]!.DeepClone();
        var secondMate = await tools.ExecuteAsync("get_mates", matesArgs);
        Require((string?)secondMate["data"]!["items"]![0]!["suppressionState"] == "Unknown", "Unknown suppression became active.");
        matesArgs.Remove("cursor"); matesArgs["includeSuppressed"] = true;
        Require((int)(await tools.ExecuteAsync("get_mates", matesArgs))["pagination"]!["total"]! == 3, "Suppressed mate opt-in failed.");
        matesArgs["objectIds"] = new JArray("missing"); Require(!(bool)(await tools.ExecuteAsync("get_mates", matesArgs))["success"]!, "Unknown mate target accepted.");
        var write = Args(); write["targetObjectIds"] = new JArray("A", "B"); write["kind"] = "memory"; write["type"] = "intent"; write["key"] = "purpose";
        write["value"] = "Rotating assembly"; write["operationId"] = "remember"; write["expectedRevision"] = 0;
        var bad = (JObject)write.DeepClone(); bad["targetObjectIds"] = new JArray("A", "missing");
        Require(!(bool)(await tools.ExecuteAsync("write_project_memory", bad))["success"]! && disk.Writes == 0 && (int)(await Read())["pagination"]!["total"]! == 0, "Multi-object validation partially committed.");
        disk.Fail = true; Require((string?)(await tools.ExecuteAsync("write_project_memory", write))["errors"]![0]!["code"] == "PERSISTENCE_FAILED", "Disk failure hidden."); disk.Fail = false;
        Require((int)(await Read())["pagination"]!["total"]! == 0, "Failed write published runtime memory.");
        using var cancellation = new CancellationTokenSource(); disk.AfterCommit = cancellation.Cancel;
        var saved = await tools.ExecuteAsync("write_project_memory", write, cancellation.Token); disk.AfterCommit = null;
        Require((bool)saved["success"]! && disk.Writes == 1 && ((JArray)saved["data"]!["recordIds"]!).Count == 2, "Atomic multi-object save/cancel failed.");
        tools = SemanticQueryTools.Create(snapshot, association, memory: MemoryAccess.Open(snapshot, association, disk));
        var keyRecall = Args(); keyRecall["keys"] = new JArray("purpose"); keyRecall["allObjectScopes"] = true;
        var recalled = await tools.ExecuteAsync("get_project_memory", keyRecall);
        Require((bool)recalled["success"]! && (int)recalled["pagination"]!["total"]! == 2, "Cross-object exact-key recall lost persisted attachments.");
        var invalidRecall = Args(); invalidRecall["allObjectScopes"] = true;
        Require((string?)(await tools.ExecuteAsync("get_project_memory", invalidRecall))["errors"]![0]!["code"] == "INVALID_ARGUMENT", "Unbounded cross-object recall accepted.");
        invalidRecall = (JObject)keyRecall.DeepClone(); invalidRecall["objectIds"] = new JArray();
        Require(!(bool)(await tools.ExecuteAsync("get_project_memory", invalidRecall))["success"]!, "Conflicting recall scopes accepted.");
        Require((int)(await Read())["pagination"]!["total"]! == 2 && (bool)(await tools.ExecuteAsync("write_project_memory", write, cancellation.Token))["receipt"]!["replayed"]! && disk.Writes == 1, "Memory/replay did not survive restart.");
        bad = (JObject)write.DeepClone(); bad["value"] = "Changed";
        Require((string?)(await tools.ExecuteAsync("write_project_memory", bad))["errors"]![0]!["code"] == "IDEMPOTENCY_CONFLICT", "Conflicting retry accepted.");
        var read = Args(); read["objectIds"] = new JArray("A", "B"); read["limit"] = 1;
        var page = await tools.ExecuteAsync("get_project_memory", read);
        var requirement = (JObject)write.DeepClone(); requirement.Remove("value"); requirement["valueJson"] = "{\"mass\":2,\"unit\":\"kg\"}";
        requirement["kind"] = "requirement"; requirement["key"] = "REQ-1"; requirement["operationId"] = "requirement"; requirement["expectedRevision"] = 1;
        Require((bool)(await tools.ExecuteAsync("write_project_memory", requirement))["success"]!, "Structured multi-object requirement failed.");
        read["cursor"] = page["pagination"]!["nextCursor"]!.DeepClone();
        Require((string?)(await tools.ExecuteAsync("get_project_memory", read))["errors"]![0]!["code"] == "INVALID_CURSOR", "Old memory cursor survived mutation.");
        var rows = (JArray)(await Read())["data"]!["items"]!;
        Require(rows.Count == 3 && rows.All(r => (string?)r["provenance"]!["type"] == "AssistantInferred"), "Wrong memory provenance/count.");
        var retire = (JObject)write.DeepClone(); retire["lifecycle"] = "Retired"; retire["expectedRevision"] = 2; retire["operationId"] = "retire";
        Require((bool)(await tools.ExecuteAsync("write_project_memory", retire))["success"]! && (int)(await Read())["pagination"]!["total"]! == 1, "Retirement did not hide records.");
        doc["objects"]![1]!["name"] = "New shaft export";
        var changed = LoadProject.Load(doc.ToString()).Snapshot!;
        var changedTools = SemanticQueryTools.Create(changed, association, memory: MemoryAccess.Open(changed, association, disk));
        var changedArgs = Args(); changedArgs["snapshotId"] = changed.SnapshotId; changedArgs["objectIds"] = new JArray("A", "B");
        Require((int)(await changedTools.ExecuteAsync("get_project_memory", changedArgs))["pagination"]!["total"]! == 0, "Snapshot-only references silently carried over.");
        changedArgs["includeStale"] = true;
        Require((int)(await changedTools.ExecuteAsync("get_project_memory", changedArgs))["pagination"]!["total"]! == 1, "Stale requirement could not be inspected.");
        var corrupt = new IssueAccessChecks.Storage { Content = "broken" };
        try { MemoryAccess.Open(snapshot, association, corrupt); throw new Exception("Corrupt journal accepted."); } catch (Newtonsoft.Json.JsonException) { }
        Require(corrupt.Writes == 0, "Corrupt journal overwritten.");
        Console.WriteLine("PASS: mate lookup/suppression/pagination, memory atomic scopes, requirements, provenance, rollback, durable replay, cancellation, retirement and stale references.");
    }
}
