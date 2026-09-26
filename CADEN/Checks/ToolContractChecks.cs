using Core.Tools;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Desktop.Configuration;
using Newtonsoft.Json.Linq;

internal static class ToolContractChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class ActionTool : IActionCadenTool
    {
        public string Name => "act";
        public JObject Declaration => JObject.Parse("""
        {"parameters":{"type":"object","required":["projectId","snapshotId","operationId"],"properties":{"projectId":{"type":"string"},"snapshotId":{"type":"string"},"operationId":{"type":"string"}}}}
        """);
        public CancellationTokenSource? Cancel;
        public bool ThrowAfterCommit;
        public bool UnexpectedAfterCommit;
        public bool Large;
        public int Commits;
        private JObject? saved;
        public JObject Execute(JObject arguments) => throw new Exception("Expected async.");
        public Task<JObject> ExecuteAsync(JObject arguments, CancellationToken token)
        {
            if (saved != null) return Task.FromResult(Replay());
            token.ThrowIfCancellationRequested(); Commits++;
            saved = new JObject { ["receipt"] = new JObject { ["operationId"] = arguments["operationId"]!.DeepClone(), ["subsystem"] = "memory", ["revision"] = 1, ["applied"] = true, ["replayed"] = false }, ["value"] = Large ? new string('x', 70000) : "committed" };
            Cancel?.Cancel(); if (ThrowAfterCommit) throw new OperationCanceledException(token);
            if (UnexpectedAfterCommit) throw new InvalidOperationException("Synthetic post-commit failure.");
            return Task.FromResult((JObject)saved.DeepClone());
        }
        private JObject Replay() { var result = (JObject)saved!.DeepClone(); result["receipt"]!["replayed"] = true; return result; }
        public Task<JObject?> RecoverCommittedAsync(JObject args) => Task.FromResult(saved == null || (string?)saved["receipt"]!["operationId"] != (string?)args["operationId"] ? null : (JObject?)Replay());
    }
    public static async Task RunAsync()
    {
        var snapshot = LoadProject.Load("""
        {"schemaVersion":"1.0","project":{"id":"export-id","name":"Test","rootObjectId":"R"},"objects":[{"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":[]}],"mates":[]}
        """).Snapshot!;
        var association = new ProjectAssociation("durable-caden-id");
        var registry = Core.Tools.Query.SemanticQueryTools.Create(snapshot, association);
        var summary = await registry.ExecuteAsync("get_model_summary", new JObject());
        Require((string?)summary["projectId"] == association.ProjectId && (string?)summary["provenance"]!["sourceProjectId"] == "export-id" && summary["ok"] == null && summary["context"] == null, "Identity/envelope migration incomplete.");
        Require(summary["data"]!["coverage"] == null && (string?)summary["coverage"]!["countUnit"] == "objects" && (string?)summary["coverage"]!["status"] == "complete" && ((JArray)summary["errors"]!).Count == 0, "Coverage not centralized.");
        JObject Args() => new() { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId };
        var wrong = Args(); wrong["projectId"] = "export-id"; wrong["objectIds"] = new JArray("R");
        Require((string?)(await registry.ExecuteAsync("get_object_details", wrong))["errors"]![0]!["code"] == "WRONG_PROJECT", "Exporter ID accepted as CADEN ID.");
        var depth = Args(); depth["objectId"] = "R"; depth["direction"] = "descendants"; depth["maxDepth"] = 33;
        Require((string?)(await registry.ExecuteAsync("query_hierarchy", depth))["errors"]![0]!["code"] == "MAX_DEPTH_EXCEEDED", "Depth error mapping wrong.");
        var unit = Args(); unit["property"] = "mass"; unit["operator"] = "equals"; unit["value"] = new JObject { ["number"] = 1, ["unit"] = "bananas" };
        Require((string?)(await registry.ExecuteAsync("find_objects", unit))["errors"]![0]!["code"] == "UNIT_MISMATCH", "Unit error mapping wrong.");
        var cursors = new QueryCursors(); var query = new JObject { ["query"] = "shaft", ["limit"] = 1 };
        string cursor = cursors.Issue("issues", association.ProjectId, snapshot.SnapshotId, query, 1, "issues", 7);
        query["cursor"] = cursor; Require(cursors.Resolve("issues", association.ProjectId, snapshot.SnapshotId, query, "issues", 7) == 1, "Cursor failed valid resume.");
        foreach (var variant in new[] { 0, 1, 2, 3 })
        {
            var altered = (JObject)query.DeepClone(); if (variant == 0) altered["query"] = "bolt";
            try { cursors.Resolve("issues", variant == 1 ? "wrong" : association.ProjectId, variant == 2 ? "wrong" : snapshot.SnapshotId, altered, "issues", variant == 3 ? 8 : 7); throw new Exception("Cursor binding bypassed."); }
            catch (ToolInputException ex) { Require(ex.Code == "INVALID_CURSOR", "Unexpected cursor error."); }
        }
        foreach (bool throwAfter in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource(); var action = new ActionTool { Cancel = cancel, ThrowAfterCommit = throwAfter };
            var actionRegistry = new ToolRegistry(new[] { action }, snapshot, true, association);
            var arguments = Args(); arguments["operationId"] = "op";
            var committed = await actionRegistry.ExecuteAsync("act", arguments, cancel.Token);
            Require((bool)committed["success"]! && (bool)committed["receipt"]!["applied"]! && (string?)committed["receipt"]!["subsystem"] == "memory", "Post-commit cancellation hid receipt.");
            var replay = await actionRegistry.ExecuteAsync("act", arguments, cancel.Token);
            Require((bool)replay["receipt"]!["replayed"]! && action.Commits == 1 && (long)replay["receipt"]!["revision"]! == 1, "Recovery repeated action or changed receipt revision.");
        }
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        var unexpected = new ActionTool { UnexpectedAfterCommit = true };
        var unexpectedRegistry = new ToolRegistry(new[] { unexpected }, snapshot, true, association);
        var unexpectedArgs = Args(); unexpectedArgs["operationId"] = "unexpected";
        var recoveredUnexpected = await unexpectedRegistry.ExecuteAsync("act", unexpectedArgs);
        Require((bool)recoveredUnexpected["success"]! && (bool)recoveredUnexpected["receipt"]!["replayed"]! && unexpected.Commits == 1, "Post-commit exception hid durable receipt.");
        var uncommitted = new ActionTool(); var beforeRegistry = new ToolRegistry(new[] { uncommitted }, snapshot, true, association);
        var beforeArgs = Args(); beforeArgs["operationId"] = "never";
        var cancelled = await beforeRegistry.ExecuteAsync("act", beforeArgs, preCancelled.Token);
        Require(!(bool)cancelled["success"]! && (string?)cancelled["errors"]![0]!["code"] == "CANCELLED" && uncommitted.Commits == 0, "Pre-commit cancellation ran mutation.");
        var large = new ActionTool { Large = true }; var largeRegistry = new ToolRegistry(new[] { large }, snapshot, true, association);
        var oversized = await largeRegistry.ExecuteAsync("act", beforeArgs);
        Require((bool)oversized["success"]! && oversized["receipt"] != null && (bool)oversized["data"]!["detailsOmitted"]!, "Oversized action result hid committed receipt.");
        string dir = Path.Combine(Path.GetTempPath(), "caden-association-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var first = ProjectAssociationFile.LoadOrCreate(dir); var again = ProjectAssociationFile.LoadOrCreate(dir);
            Require(first.ProjectId == again.ProjectId, "Host association changed across reload.");
            File.WriteAllText(Path.Combine(dir, "caden-project.json"), "broken");
            try { ProjectAssociationFile.LoadOrCreate(dir); throw new Exception("Corrupt association replaced."); } catch (Newtonsoft.Json.JsonException) { }
            Require(File.ReadAllText(Path.Combine(dir, "caden-project.json")) == "broken", "Corrupt association overwritten.");
        }
        finally { foreach (var file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); }
        Console.WriteLine("PASS: v3 envelope/identity, public errors, query/revision-bound cursors, cancellation/committed receipt recovery, oversized acknowledgements and persistent host association.");
    }
}
