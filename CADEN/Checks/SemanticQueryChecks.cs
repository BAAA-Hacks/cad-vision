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
        var registry = SemanticQueryTools.Create(snapshot);
        async Task<JObject> Call(string name, JObject? args = null)
        {
            args ??= new JObject(); if (name != "get_model_summary") args["snapshotId"] = snapshot.SnapshotId;
            return await registry.ExecuteAsync(name, args);
        }
        JObject Filter(string property, string op, JObject value) => new() { ["property"] = property, ["operator"] = op, ["value"] = value };
        var summary = await Call("get_model_summary");
        Require((string?)summary["contractVersion"] == "2.0" && (string?)summary["context"]!["snapshotId"] == snapshot.SnapshotId && registry.Declarations.Count == 4, "Canonical envelope/declarations missing.");
        var stale = await registry.ExecuteAsync("get_object_details", new JObject { ["snapshotId"] = "old", ["objectIds"] = new JArray("A") });
        Require((string?)stale["error"]!["code"] == "STALE_SNAPSHOT" && stale["context"] != null, "Stale references accepted or error lacks context.");
        Require((string?)(await registry.ExecuteAsync("get_object_details", new JObject { ["objectIds"] = new JArray("A") }))["error"]!["code"] == "INVALID_ARGUMENTS", "Snapshot scope not required.");
        var details = await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A", "B"), ["fields"] = new JArray("mass", "fixed", "constraintStatus") });
        Require((bool)details["ok"]! && (string?)details["data"]!["items"]![0]!["properties"]!["constraintStatus"]!["value"] == "under_defined" && (string?)details["data"]!["items"]![1]!["properties"]!["mass"]!["status"] == "invalid", "Canonical values/alias lost.");
        var defaults = await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A") });
        Require(((JObject)defaults["data"]!["items"]![0]!["properties"]!).Count == 5 && ((JArray)defaults["data"]!["items"]![0]!["path"]!).Count == 3, "Defaults not compact or path missing.");
        var search = await Call("find_objects", new JObject { ["query"] = "shaft", ["limit"] = 1 });
        Require((int)search["data"]!["total"]! == 2 && (bool)search["data"]!["truncated"]!, "Substring matching/pagination failed.");
        var next = await Call("find_objects", new JObject { ["query"] = "shaft", ["offset"] = 1, ["limit"] = 1 });
        Require((string?)search["data"]!["items"]![0]!["id"] != (string?)next["data"]!["items"]![0]!["id"], "Pagination duplicated names/instances.");
        var mass = await Call("find_objects", Filter("mass", "greater_than", new JObject { ["number"] = 2, ["unit"] = "kg" }));
        Require((int)mass["data"]!["total"]! == 1 && (string?)mass["data"]!["items"]![0]!["id"] == "A" && (int)mass["data"]!["coverage"]!["unknownCount"]! == 3, "Units or partial coverage wrong.");
        var volume = await Call("find_objects", Filter("volume", "equals", new JObject { ["number"] = 1, ["unit"] = "m^3" }));
        Require((int)volume["data"]!["total"]! == 1, "Volume unit conversion failed.");
        var fixedFalse = await Call("find_objects", Filter("fixed", "equals", new JObject { ["boolean"] = false }));
        Require((int)fixedFalse["data"]!["total"]! == 1, "Unknown boolean became false.");
        var notFalse = await Call("find_objects", Filter("fixed", "not_equals", new JObject { ["boolean"] = false }));
        Require((int)notFalse["data"]!["total"]! == 1 && (int)notFalse["data"]!["coverage"]!["unknownCount"]! == 2, "Unknown boolean satisfied not_equals.");
        var material = await Call("find_objects", Filter("material.name", "equals", new JObject { ["text"] = "Steel" }));
        Require((int)material["data"]!["total"]! == 1, "Nested material lookup failed.");
        var insensitive = await Call("find_objects", Filter("material.name", "equals", new JObject { ["text"] = "steel" }));
        Require((int)insensitive["data"]!["total"]! == 0, "Exact equality unexpectedly case-insensitive.");
        var membership = await Call("find_objects", new JObject { ["property"] = "mass", ["operator"] = "in", ["values"] = new JArray(new JObject { ["number"] = 2.5, ["unit"] = "kg" }) });
        Require((int)membership["data"]!["total"]! == 1, "Unit-aware membership failed.");
        var emptyScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = new JArray() });
        Require((int)emptyScope["data"]!["total"]! == 0 && (int)emptyScope["data"]!["coverage"]!["scopeCount"]! == 0, "Empty scope became whole model.");
        var exactScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = new JArray("S") });
        Require((int)exactScope["data"]!["total"]! == 0, "Exact scope expanded descendants.");
        var nullScope = await Call("find_objects", new JObject { ["query"] = "shaft", ["scopeObjectIds"] = null });
        Require((int)nullScope["data"]!["total"]! == 2, "Null scope failed.");
        var hierarchy = await Call("query_hierarchy", new JObject { ["objectId"] = "R", ["direction"] = "descendants", ["maxDepth"] = 1 });
        Require((int)hierarchy["data"]!["total"]! == 1 && (bool)hierarchy["data"]!["depthLimited"]! && !(bool)hierarchy["data"]!["truncated"]!, "Depth and page coverage conflated.");
        var ancestors = await Call("query_hierarchy", new JObject { ["objectId"] = "A", ["direction"] = "ancestors", ["maxDepth"] = 5 });
        Require((int)ancestors["data"]!["total"]! == 2 && (string?)ancestors["data"]!["items"]![0]!["id"] == "S" && !(bool)ancestors["data"]!["depthLimited"]!, "Ancestor order wrong.");
        var zeroDepth = await Call("query_hierarchy", new JObject { ["objectId"] = "R", ["direction"] = "descendants", ["maxDepth"] = 0 });
        Require((int)zeroDepth["data"]!["total"]! == 0 && (bool)zeroDepth["data"]!["depthLimited"]!, "Zero depth wrong.");
        foreach (var bad in new[]
        {
            Filter("mass", "greater_than", new JObject { ["number"] = 2 }),
            Filter("fixed", "equals", new JObject { ["text"] = "false" }),
            Filter("mass", "equals", new JObject { ["number"] = "2", ["unit"] = "kg" }),
            Filter("mass", "equals", new JObject { ["number"] = 2, ["boolean"] = false, ["unit"] = "kg" }),
            new JObject { ["query"] = "a", ["property"] = "mass" },
            new JObject { ["query"] = "a", ["limit"] = 51 },
            new JObject { ["query"] = "a", ["scopeObjectIds"] = new JArray(true) },
            new JObject { ["query"] = "a", ["extra"] = true }
        }) Require((string?)(await Call("find_objects", bad))["error"]?["code"] == "INVALID_ARGUMENTS", "Malformed tool arguments accepted: " + bad);
        Require((string?)(await Call("get_object_details", new JObject { ["objectIds"] = new JArray("A", "unknown") }))["error"]!["code"] == "OBJECT_NOT_FOUND", "Unknown batch ID partially accepted.");
        var degradedDocument = Document(); degradedDocument["objects"]![2]!["parentId"] = "missing";
        var degraded = LoadProject.Load(degradedDocument.ToString()).Snapshot!; var degradedTools = SemanticQueryTools.Create(degraded);
        var unavailable = await degradedTools.ExecuteAsync("query_hierarchy", new JObject { ["snapshotId"] = degraded.SnapshotId, ["objectId"] = "R", ["direction"] = "children" });
        Require((bool)unavailable["ok"]! && unavailable["data"]!["items"]!.Type == JTokenType.Null && (string?)unavailable["data"]!["coverage"]!["status"] == "Invalid", "Invalid hierarchy became an empty tree.");
        var healthyProperties = await degradedTools.ExecuteAsync("get_object_details", new JObject { ["snapshotId"] = degraded.SnapshotId, ["objectIds"] = new JArray("A") });
        Require((bool)healthyProperties["ok"]!, "Invalid hierarchy blocked usable properties.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await registry.ExecuteAsync("get_model_summary", new JObject(), cancelled.Token); throw new Exception("Cancelled query ran."); } catch (OperationCanceledException) { }
        var asyncTool = new DelayedTool(); var asyncRegistry = new ToolRegistry(new[] { asyncTool });
        using var cancellation = new CancellationTokenSource(); var pending = asyncRegistry.ExecuteAsync("delayed", new JObject(), cancellation.Token);
        await asyncTool.Started.Task; cancellation.Cancel();
        try { await pending; throw new Exception("In-flight tool cancellation swallowed."); } catch (OperationCanceledException) { }
        using var wire = new WireHandler(); using var http = new HttpClient(wire);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("dummy", "test-model", "System"), registry));
        await chat.SendAsync("Inspect model");
        Require(wire.Bodies.Count == 2 && wire.Bodies[1].Contains("\"contractVersion\":\"2.0\"") && wire.Bodies[1].Contains("get_object_details"), "Async Gemini dispatch lost new envelope/declarations.");
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
