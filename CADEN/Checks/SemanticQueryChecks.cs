using Core;
using Core.Tools;
using Core.Tools.Query;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;
using System.Net;

internal static class SemanticQueryChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Document() => JObject.Parse("""
    {"schemaVersion":"1.0","project":{"id":"P","name":"Test","rootObjectId":"R","units":{"mass":"g","length":"mm"}},
     "objects":[
       {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["S"]},
       {"id":"S","name":"Sub","type":"assembly","parentId":"R","childIds":["A","B"]},
       {"id":"A","name":"Shaft","type":"part","parentId":"S","childIds":[],"mass":2500,"volume":1000000000,"fixed":false,"definitionStatus":"under_defined","material":{"assigned":false}},
       {"id":"B","name":"Shaft","type":"part","parentId":"S","childIds":[],"mass":"heavy","fixed":true,"material":{"assigned":true,"name":"Steel"}}
     ],"mates":[]}
    """);
    public static async Task RunAsync()
    {
        var snapshot = LoadProject.Load(Document().ToString()).Snapshot!;
        var registry = SemanticQueryTools.Create(snapshot, new Core.Primitives.DataStructures.Memory.ProjectAssociation("caden-test"));
        async Task<JObject> Call(string name, JObject? args = null)
        {
            args ??= new JObject(); if (name != "get_model_summary") { args["snapshotId"] = snapshot.SnapshotId; args["projectId"] = "caden-test"; }
            return await registry.ExecuteAsync(name, args);
        }
        JObject Filter(string property, string op, JObject value) => new() { ["property"] = property, ["operator"] = op, ["value"] = value };
        var summary = await Call("get_model_summary");
        Require((string?)summary["contractVersion"] == "3.0" && (string?)summary["snapshotId"] == snapshot.SnapshotId && registry.Declarations.Count == 4, "Canonical envelope/declarations missing.");
        var stale = await registry.ExecuteAsync("get_object_details", new JObject { ["projectId"] = "caden-test", ["snapshotId"] = "old", ["objectIds"] = new JArray("A") });
        Require((string?)stale["errors"]![0]!["code"] == "STALE_SNAPSHOT_REFERENCE" && stale["projectId"] != null, "Stale references accepted or error lacks context.");
        Require((string?)(await registry.ExecuteAsync("get_object_details", new JObject { ["objectIds"] = new JArray("A") }))["errors"]![0]!["code"] == "INVALID_ARGUMENT", "Snapshot scope not required.");
        var details = await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A", "B"), ["fields"] = new JArray("mass", "fixed", "constraintStatus") });
        Require((bool)details["success"]! && (string?)details["data"]!["items"]![0]!["properties"]!["constraintStatus"]!["value"] == "under_defined" && (string?)details["data"]!["items"]![1]!["properties"]!["mass"]!["status"] == "invalid", "Canonical values/alias lost.");
        var defaults = await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A") });
        Require(((JObject)defaults["data"]!["items"]![0]!["properties"]!).Count == 5 && ((JArray)defaults["data"]!["items"]![0]!["path"]!).Count == 3, "Defaults not compact or path missing.");
        var search = await Call("find_objects", new JObject { ["query"] = "shaft", ["limit"] = 1 });
        Require((int)search["pagination"]!["total"]! == 2 && search["pagination"]!["nextCursor"]!.Type == JTokenType.String, "Substring matching/pagination failed.");
        var next = await Call("find_objects", new JObject { ["query"] = "shaft", ["cursor"] = search["pagination"]!["nextCursor"]!.DeepClone(), ["limit"] = 1 });
        Require((string?)search["data"]!["items"]![0]!["id"] != (string?)next["data"]!["items"]![0]!["id"], "Pagination duplicated names/instances.");
        var mass = await Call("find_objects", Filter("mass", "greater_than", new JObject { ["number"] = 2, ["unit"] = "kg" }));
        Require((int)mass["pagination"]!["total"]! == 1 && (string?)mass["data"]!["items"]![0]!["id"] == "A" && (int)mass["coverage"]!["excludedUnknownCount"]! == 3, "Units or partial coverage wrong.");
        var volume = await Call("find_objects", Filter("volume", "equals", new JObject { ["number"] = 1, ["unit"] = "m^3" }));
        Require((int)volume["pagination"]!["total"]! == 1, "Volume unit conversion failed.");
        var fixedFalse = await Call("find_objects", Filter("fixed", "equals", new JObject { ["boolean"] = false }));
        Require((int)fixedFalse["pagination"]!["total"]! == 1, "Unknown boolean became false.");
        var notFalse = await Call("find_objects", Filter("fixed", "not_equals", new JObject { ["boolean"] = false }));
        Require((int)notFalse["pagination"]!["total"]! == 1 && (int)notFalse["coverage"]!["excludedUnknownCount"]! == 2, "Unknown boolean satisfied not_equals.");
        var material = await Call("find_objects", Filter("material.name", "equals", new JObject { ["text"] = "Steel" }));
        Require((int)material["pagination"]!["total"]! == 1, "Nested material lookup failed.");
        var insensitive = await Call("find_objects", Filter("material.name", "equals", new JObject { ["text"] = "steel" }));
        Require((int)insensitive["pagination"]!["total"]! == 0, "Exact equality unexpectedly case-insensitive.");
        var membership = await Call("find_objects", new JObject { ["property"] = "mass", ["operator"] = "in", ["values"] = new JArray(new JObject { ["number"] = 2.5, ["unit"] = "kg" }) });
        Require((int)membership["pagination"]!["total"]! == 1, "Unit-aware membership failed.");
        var emptyScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = new JArray() });
        Require((int)emptyScope["pagination"]!["total"]! == 0 && (int)emptyScope["coverage"]!["scopeCount"]! == 0, "Empty scope became whole model.");
        var exactScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = new JArray("S") });
        Require((int)exactScope["pagination"]!["total"]! == 0, "Exact scope expanded descendants.");
        var nullScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = null });
        Require((int)nullScope["pagination"]!["total"]! == 2, "Null scope failed.");
        var hierarchy = await Call("query_hierarchy", new JObject { ["objectId"] = "R", ["direction"] = "descendants", ["maxDepth"] = 1 });
        Require((int)hierarchy["pagination"]!["total"]! == 1 && (bool)hierarchy["coverage"]!["depthLimited"]! && hierarchy["pagination"]!["nextCursor"]!.Type == JTokenType.Null, "Depth and page coverage conflated.");
        var ancestors = await Call("query_hierarchy", new JObject { ["objectId"] = "A", ["direction"] = "ancestors", ["maxDepth"] = 5 });
        Require((int)ancestors["pagination"]!["total"]! == 2 && (string?)ancestors["data"]!["items"]![0]!["id"] == "S" && !(bool)ancestors["coverage"]!["depthLimited"]!, "Ancestor order wrong.");
        var zeroDepth = await Call("query_hierarchy", new JObject { ["objectId"] = "R", ["direction"] = "descendants", ["maxDepth"] = 0 });
        Require((int)zeroDepth["pagination"]!["total"]! == 0 && (bool)zeroDepth["coverage"]!["depthLimited"]!, "Zero depth wrong.");
        foreach (var bad in new[]
        {

            Filter("fixed", "equals", new JObject { ["text"] = "false" }),
            Filter("mass", "equals", new JObject { ["number"] = "2", ["unit"] = "kg" }),
            Filter("mass", "equals", new JObject { ["number"] = 2, ["boolean"] = false, ["unit"] = "kg" }),
            new JObject { ["query"] = "a", ["property"] = "mass" },
            new JObject { ["query"] = "a", ["limit"] = 51 },
            new JObject { ["query"] = "a", ["scopeObjectIds"] = new JArray(true) },
            new JObject { ["query"] = "a", ["extra"] = true }
        }) Require((string?)(await Call("find_objects", bad))["errors"]?[0]?["code"] == "INVALID_ARGUMENT", "Malformed tool arguments accepted: " + bad);
        Require((string?)(await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A", "unknown") }))["errors"]![0]!["code"] == "UNKNOWN_OBJECT_ID", "Unknown batch ID partially accepted.");
        var degradedDocument = Document(); degradedDocument["objects"]![2]!["parentId"] = "missing";
        var degraded = LoadProject.Load(degradedDocument.ToString()).Snapshot!; var degradedTools = SemanticQueryTools.Create(degraded, new Core.Primitives.DataStructures.Memory.ProjectAssociation("caden-test"));
        var unavailable = await degradedTools.ExecuteAsync("query_hierarchy", new JObject { ["projectId"] = "caden-test", ["snapshotId"] = degraded.SnapshotId, ["objectId"] = "R", ["direction"] = "children" });
        Require((bool)unavailable["success"]! && unavailable["data"]!["items"]!.Type == JTokenType.Null && (string?)unavailable["coverage"]!["status"] == "unavailable", "Invalid hierarchy became an empty tree.");
        var healthyProperties = await degradedTools.ExecuteAsync("get_object_details", new JObject { ["projectId"] = "caden-test", ["snapshotId"] = degraded.SnapshotId, ["objectIds"] = new JArray("A") });
        Require((bool)healthyProperties["success"]!, "Invalid hierarchy blocked usable properties.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Require((string?)(await registry.ExecuteAsync("get_model_summary", new JObject(), cancelled.Token))["errors"]![0]!["code"] == "CANCELLED", "Cancellation envelope missing.");
        var asyncTool = new DelayedTool(); var asyncRegistry = new ToolRegistry(new[] { asyncTool });
        using var cancellation = new CancellationTokenSource(); var pending = asyncRegistry.ExecuteAsync("delayed", new JObject(), cancellation.Token);
        await asyncTool.Started.Task; cancellation.Cancel();
        try { await pending; throw new Exception("In-flight tool cancellation swallowed."); } catch (OperationCanceledException) { }
        using var wire = new WireHandler(); using var http = new HttpClient(wire);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("dummy", "test-model", "System"), registry));
        await chat.SendAsync("Inspect model");
        Require(wire.Bodies.Count == 2 && wire.Bodies[1].Contains("\"contractVersion\":\"3.0\"") && wire.Bodies[1].Contains("get_object_details"), "Async Gemini dispatch lost new envelope/declarations.");
        Console.WriteLine("PASS: semantic query envelope/snapshot guard, canonical properties, typed unit filters, partial coverage, scope/pagination/hierarchy, degraded loading and async cancellation/dispatch.");
    }
    private sealed class DelayedTool : IAsyncCadenTool
    {
        public TaskCompletionSource<bool> Started { get; } = new();
        public string Name => "delayed";
        public JObject Declaration => JObject.Parse("{\"parameters\":{\"type\":\"object\",\"properties\":{}}}");
        public JObject Execute(JObject arguments) => throw new Exception("Sync dispatch used.");
        public async Task<JObject> ExecuteAsync(JObject arguments, CancellationToken cancellationToken)
        { Started.SetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return new JObject(); }
    }
    private sealed class WireHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Bodies.Count == 1
                ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"get_model_summary\",\"args\":{},\"id\":\"s\"}}]},\"finishReason\":\"STOP\"}]}"
                : FakeHandler.Success) };
        }
    }
}
