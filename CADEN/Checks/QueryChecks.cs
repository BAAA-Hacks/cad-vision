using System.Net;
using Core;
using Core.Tools;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class QueryChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Fixture() => JObject.Parse("""
    {
      "schemaVersion":"1.0",
      "project":{"id":"P","name":"Test model","rootObjectId":"ROOT","units":{"mass":"kg","length":"mm"}},
      "objects":[
        {"id":"ROOT","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"],"glbNodeIndex":0},
        {"id":"A","name":"Shaft","type":"part","parentId":"ROOT","childIds":[],"glbNodeIndex":1,"mass":0,"material":{"assigned":false,"name":null,"density":null}},
        {"id":"B","name":"Shaft","type":"part","parentId":"ROOT","childIds":[],"glbNodeIndex":2,"mass":"heavy","fixed":"false"}
      ],
      "mates":[],"interferences":[]
    }
    """);
    private static ToolRegistry Registry(JObject doc) => QueryTools.Create(new MetadataStore(doc.ToString()));
    private static JObject Object(ToolRegistry tools, string id) => tools.Execute("get_object", new JObject { ["id"] = id });
    private static string? Status(JObject result, string field) => (string?)result["data"]?["properties"]?[field]?["status"];
    private static void Invalid(Action<JObject> mutate)
    {
        var doc = Fixture(); mutate(doc);
        try { new MetadataStore(doc.ToString()); throw new Exception("Invalid metadata accepted."); }
        catch (ToolInputException ex) { Require(ex.Code == "INVALID_METADATA", "Wrong metadata error."); }
    }
    public static async Task RunAsync()
    {
        var doc = Fixture(); var tools = Registry(doc);
        var summary = tools.Execute("get_model_summary", new JObject());
        Require((int)summary["data"]!["objectCount"]! == 3 && tools.Declarations.Count == 4, "Wrong summary/tool count.");
        var page = tools.Execute("search_objects", new JObject { ["query"] = "shaft", ["limit"] = 1 });
        Require((int)page["data"]!["total"]! == 2 && (bool)page["data"]!["truncated"]!, "Repeated names lost/pagination failed.");
        var next = tools.Execute("search_objects", new JObject { ["query"] = "shaft", ["limit"] = 1, ["offset"] = page["data"]!["nextOffset"]!.DeepClone() });
        Require((string?)page["data"]!["items"]![0]!["id"] != (string?)next["data"]!["items"]![0]!["id"], "Page repeated the same instance.");
        Require(((JArray)page["data"]!["items"]![0]!["path"]!).Count == 2, "Context path missing.");
        var hierarchy = tools.Execute("get_hierarchy", new JObject());
        Require((int)hierarchy["data"]!["total"]! == 2, "Direct children missing.");
        Require((int)tools.Execute("get_hierarchy", new JObject { ["id"] = "A" })["data"]!["total"]! == 0, "Leaf hierarchy should be empty.");
        Require(Status(Object(tools, "A"), "mass") == "available" && (double)Object(tools, "A")["data"]!["properties"]!["mass"]!["value"]! == 0, "Zero mass became missing.");
        Require(Status(Object(tools, "A"), "material") == "available", "Explicit false material state lost.");
        Require(Status(Object(tools, "B"), "mass") == "invalid" && Status(Object(tools, "B"), "fixed") == "invalid", "Malformed engineering values were trusted.");
        Require(Status(Object(tools, "A"), "volume") == "missing", "Absent property not marked missing.");
        Require((string?)Object(tools, "A")["data"]!["properties"]!["mass"]!["sourceField"] == "/objects/1/mass", "Invalid JSON pointer.");
        doc["sampleInfo"] = new JObject { ["fixture"] = true }; var fixtureTools = Registry(doc);
        Require(Status(Object(fixtureTools, "A"), "material") == "missing" && Status(Object(fixtureTools, "A"), "mass") == "missing", "Fixture placeholders became engineering evidence.");
        doc = Fixture(); doc["project"]!["units"]!["mass"] = "bananas";
        Require(Status(Object(Registry(doc), "A"), "mass") == "invalid", "Unsupported unit was trusted.");
        doc = Fixture();
        doc["objects"]![1]!["dimensions"] = new JArray(new JObject { ["id"] = "D", ["name"] = "Diameter", ["type"] = "diameter", ["value"] = 5, ["unit"] = "bananas", ["references"] = new JArray() });
        Require(Status(Object(Registry(doc), "A"), "dimensions") == "invalid", "Malformed dimension units trusted.");
        doc["objects"]![1]!["dimensions"]![0]!["unit"] = "mm";
        Require(Status(Object(Registry(doc), "A"), "dimensions") == "available", "Valid dimension rejected.");
        doc = Fixture(); doc["objects"]![1]!["type"] = "assembly"; doc["objects"]![1]!["childIds"] = new JArray("B"); doc["objects"]![2]!["parentId"] = "A"; doc["objects"]![0]!["childIds"] = new JArray("A");
        var nested = Registry(doc);
        Require((bool)nested.Execute("get_hierarchy", new JObject())["data"]!["deeperLevelsOmitted"]!, "Depth-limited hierarchy presented as complete.");
        Require((int)nested.Execute("get_hierarchy", new JObject { ["depth"] = 2 })["data"]!["total"]! == 2, "Nested hierarchy traversal failed.");
        var invalidCases = new[] {
            ("get_object", new JObject(), "INVALID_ARGUMENTS"),
            ("get_object", new JObject { ["id"] = "NOPE" }, "OBJECT_NOT_FOUND"),
            ("search_objects", new JObject { ["query"] = "" }, "INVALID_ARGUMENTS"),
            ("search_objects", new JObject { ["query"] = "shaft", ["limit"] = "5" }, "INVALID_ARGUMENTS"),
            ("search_objects", new JObject { ["query"] = "shaft", ["limit"] = 51 }, "INVALID_ARGUMENTS"),
            ("search_objects", new JObject { ["query"] = "shaft", ["parent_id"] = "NOPE" }, "OBJECT_NOT_FOUND"),
            ("get_hierarchy", new JObject { ["depth"] = 6 }, "INVALID_ARGUMENTS"),
            ("get_object", new JObject { ["id"] = "A", ["fields"] = new JArray("nonexistent") }, "INVALID_ARGUMENTS"),
            ("get_model_summary", new JObject { ["extra"] = true }, "INVALID_ARGUMENTS"),
            ("delete_model", new JObject(), "UNKNOWN_TOOL")
        };
        foreach (var c in invalidCases) Require((string?)tools.Execute(c.Item1, c.Item2)["error"]?["code"] == c.Item3, "Wrong error: " + c.Item1);
        Require((string?)QueryTools.Create(null).Execute("get_model_summary", new JObject())["error"]?["code"] == "MODEL_NOT_LOADED", "Missing-model error absent.");
        Invalid(d => d["objects"]![1]!["id"] = "ROOT");
        Invalid(d => d["objects"]![1]!["parentId"] = "NOPE");
        Invalid(d => d["objects"]![0]!["childIds"] = new JArray("A", "A", "B"));
        Invalid(d => d["objects"]![2]!["glbNodeIndex"] = 1);
        Invalid(d => d["schemaVersion"] = "2.0");
        Invalid(d => d["objects"]![0]!["type"] = "part");
        Invalid(d => d["sampleInfo"] = new JObject { ["fixture"] = "true" });
        try { new MetadataStore("{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"}"); throw new Exception("Duplicate JSON key accepted."); }
        catch (ToolInputException) { }
        Console.WriteLine("PASS: four queries, duplicate-name context, pagination, hierarchy, field contract, IDs/arguments, invalid metadata.");

        var queued = new QueueHandler(); using var http = new HttpClient(queued);
        var client = new GeminiClient(http, new GeminiSettings("dummy", "test-model", "System"), tools);
        var chat = new ChatSession(client);
        string calls = """
        {"candidates":[{"content":{"role":"model","parts":[
          {"thoughtSignature":"opaque-signature","functionCall":{"name":"get_model_summary","args":{},"id":"call-1"}},
          {"functionCall":{"name":"get_object","args":{"id":"NOPE"},"id":"call-2"}}
        ]},"finishReason":"STOP"}]}
        """;
        queued.Responses.Enqueue(calls); queued.Responses.Enqueue(FakeHandler.Success);
        await chat.SendAsync("Inspect model");
        Require(client.LastToolCallCount == 2, "Calls were not dispatched.");
        var wire = JObject.Parse(queued.Requests[1]);
        Require(((JArray)wire["tools"]![0]!["functionDeclarations"]!).Count == 5, "Missing query or session recall declarations.");
        Require((string?)wire["contents"]![1]!["parts"]![0]!["thoughtSignature"] == "opaque-signature", "Thought signature lost.");
        Require((string?)wire["contents"]![2]!["parts"]![0]!["functionResponse"]!["id"] == "call-1", "Call/result ID association lost.");
        Require((string?)wire["contents"]![2]!["parts"]![1]!["functionResponse"]!["response"]!["error"]!["code"] == "OBJECT_NOT_FOUND", "Tool error did not reach Gemini.");
        Require(chat.Messages.Count(m => m.Text.Length > 0) == 2, "Tool exchanges leaked into visible transcript.");
        queued.Responses.Enqueue(FakeHandler.Success); await chat.SendAsync("Follow-up");
        Require(!queued.Requests.Last().Contains("opaque-signature") && !queued.Requests.Last().Contains("functionResponse") && queued.Requests.Last().Contains("result_"), "Follow-up did not replace old raw exchanges with cache references.");
        chat.Clear(); queued.Responses.Enqueue(FakeHandler.Success); await chat.SendAsync("Fresh");
        Require(!queued.Requests.Last().Contains("opaque-signature"), "Reset retained old tool state.");
        chat.Clear(); for (int i = 0; i < 9; i++) queued.Responses.Enqueue(calls);
        queued.Responses.Enqueue(FakeHandler.Success); await chat.SendAsync("Long valid sequence");
        Require(client.LastToolCallCount == 18, "Expanded defaults still reject more than six rounds/sixteen calls.");
        chat.Clear(); for (int i = 0; i < 13; i++) queued.Responses.Enqueue(calls);
        try { await chat.SendAsync("Loop"); throw new Exception("Unbounded tool loop."); }
        catch (ChatException ex) { Require(ex.Message.Contains("query limit"), "Wrong loop-limit error."); }
        Require(chat.Messages.Count == 0, "Failed tool loop entered history.");
        var limitedClient = new GeminiClient(http, new GeminiSettings("dummy", "test-model", "System", maxToolRounds: 2, maxToolCalls: 1), tools);
        var limitedChat = new ChatSession(limitedClient); queued.Responses.Enqueue(calls);
        try { await limitedChat.SendAsync("Batch exceeds call cap"); throw new Exception("Call budget ignored."); }
        catch (ChatException ex) { Require(ex.Message.Contains("2 rounds / 1 calls") && limitedClient.LastToolCallCount == 0, "Configured budget or atomic batch rejection failed."); }
        Console.WriteLine("PASS: Gemini dispatch, multiple calls, call IDs/signatures, tool errors, follow-up/reset, bounded loops, failed-turn rollback.");

        string path = Path.Combine(Desktop.Configuration.LocalConfiguration.FindDirectory(), "data", "metadata.json");
        if (File.Exists(path))
        {
            var sample = new MetadataStore(File.ReadAllText(path)); var registry = QueryTools.Create(sample);
            var info = registry.Execute("get_model_summary", new JObject());
            Require((bool)info["ok"]!, "Sample summary failed.");
            if (sample.IsFixture && sample.Name == "FRED_P1_Assembly")
            {
                Require(sample.Count == 261, "Unexpected FRED object count.");
                Require((int)info["data"]!["propertyAvailability"]!["mass"]!["missing"]! == 261, "FRED mass evidence incorrectly classified.");
            }
            Console.WriteLine("PASS: local metadata sample loaded and queried (" + sample.Count + " objects).");
        }
    }
    private sealed class QueueHandler : HttpMessageHandler
    {
        public Queue<string> Responses { get; } = new();
        public List<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Responses.Dequeue()) };
        }
    }
}
