using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Core.Tools;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class CapabilityChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var empty = SemanticQueryTools.Create(null, null);
        Require(empty.Declarations.Count == 1 && (string?)empty.Declarations[0]!["name"] == "get_model_summary", "No-model session advertises unusable tools.");
        var summary = await empty.ExecuteAsync("get_model_summary", new JObject());
        var catalogue = (JArray)summary["data"]!["toolCapabilities"]!;
        Require((bool)summary["success"]! && !(bool)summary["data"]!["modelLoaded"]! && catalogue.Count == 24 && catalogue.Count(c => (bool)c["usable"]!) == 1, "No-model discovery missing.");
        var missing = await empty.ExecuteAsync("write_project_memory", new JObject());
        Require((string?)missing["errors"]![0]!["details"]!["reasonCode"] == "MODEL_NOT_LOADED" && !(bool)missing["errors"]![0]!["details"]!["retryable"]!, "Missing handler lost capability reason.");
        Require((string?)(await empty.ExecuteAsync("invented_tool", new JObject()))["errors"]![0]!["code"] == "UNKNOWN_TOOL", "Unknown tool confused with unavailable capability.");
        var doc = JObject.Parse("""
        {"schemaVersion":"2.1","project":{"id":"source","name":"Test","rootObjectId":"R"},"objects":[
        {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"]},
        {"id":"A","name":"A","type":"part","parentId":"R","childIds":[]},
        {"id":"B","name":"B","type":"part","parentId":"R","childIds":[]}],
        "mates":[{"id":"M","componentIds":["A","B"],"type":"coincident","suppressed":null}]}
        """);
        var snapshot = LoadProject.Load(doc.ToString()).Snapshot!; var association = new ProjectAssociation("host");
        var failures = new Dictionary<string, HostCapabilityFailure> { ["memory"] = HostCapabilityFailure.FromException("memory", new InvalidDataException("secret host path"), "diag-1") };
        var tools = SemanticQueryTools.Create(snapshot, association, initializationFailures: failures);
        summary = await tools.ExecuteAsync("get_model_summary", new JObject()); catalogue = (JArray)summary["data"]!["toolCapabilities"]!;
        foreach (var entry in catalogue) Require(tools.Declarations.Any(t => (string?)t["name"] == (string?)entry["tool"]) == (bool)entry["usable"]!, "Catalogue and declarations disagree.");
        Require(!summary.ToString().Contains("secret host path"), "Summary exposed raw host exception.");
        var unavailableMemory = await tools.ExecuteAsync("get_project_memory", new JObject());
        Require((string?)unavailableMemory["errors"]![0]!["details"]!["reasonCode"] == "MEMORY_STORAGE_INVALID" && (string?)unavailableMemory["errors"]![0]!["correlationId"] == "diag-1", "Storage reason/trace was lost.");
        var args = new JObject { ["projectId"] = "host", ["snapshotId"] = snapshot.SnapshotId, ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["startObjectId"] = "A", ["endObjectId"] = "B" };
        var traversal = await tools.ExecuteAsync("find_mechanical_path", args);
        Require((string?)traversal["errors"]![0]!["details"]!["reasonCode"] == "MECHANICAL_SCOPE_MISSING" && ((JArray)traversal["errors"]![0]!["details"]!["alternativeTools"]!).Any(v => (string?)v == "get_mates"), "Traversal lacks usable fallback.");
        var mateArgs = new JObject { ["projectId"] = "host", ["snapshotId"] = snapshot.SnapshotId, ["objectIds"] = new JArray("A") };
        var mate = await tools.ExecuteAsync("get_mates", mateArgs);
        Require((bool)mate["success"]! && (string?)mate["coverage"]!["status"] == "partial", "Fallback erased usable partial evidence.");
        Console.WriteLine("PASS: 24-tool capability discovery, no-model summary, declaration gating, structured recovery, host diagnostics and partial-evidence fallback.");
    }
}
