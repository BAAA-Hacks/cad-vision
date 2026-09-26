using Core;
using Core.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;

internal static class ContextCacheChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    sealed class Bulk : ICadenTool
    {
        public int Executions;
        public string Name => "bulk";
        public JObject Declaration => JObject.Parse("{'name':'bulk','description':'Fixture','parameters':{'type':'object','properties':{}}}");
        public JObject Execute(JObject args)
        {
            Executions++;
            return new JObject { ["items"] = new JArray(Enumerable.Range(0, 100).Select(i => new JObject { ["id"] = i, ["value"] = "PRIVATE_DETAIL_" + new string('x', 400) })),
                ["mass"] = new JObject { ["status"] = "missing", ["unit"] = "kg" }, ["a/b"] = new JObject { ["~value"] = 42 } };
        }
    }
    sealed class Wire : HttpMessageHandler
    {
        internal Queue<string> Replies = new(); internal List<JObject> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JObject.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Replies.Dequeue()) };
        }
    }
    static string Call(string name, JObject args) => new JObject { ["candidates"] = new JArray(new JObject { ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray(new JObject {
        ["thoughtSignature"] = "preserve-within-turn", ["functionCall"] = new JObject { ["name"] = name, ["args"] = args, ["id"] = "call" } }) }, ["finishReason"] = "STOP" }) }.ToString();
    static JObject Returned(Wire wire) => (JObject)wire.Requests.Last()["contents"]!.Last!["parts"]![0]!["functionResponse"]!["response"]!;
    internal static async Task RunAsync()
    {
        var bulk = new Bulk(); var tools = new ToolRegistry(new[] { bulk });
        using var wire = new Wire(); using var http = new HttpClient(wire);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("offline", "test", "System"), tools));
        async Task<JObject> Execute(string name, JObject args)
        {
            wire.Replies.Enqueue(Call(name, args)); wire.Replies.Enqueue(FakeHandler.Success);
            await chat.SendAsync("Inspect fixture"); return Returned(wire);
        }
        var first = await Execute("bulk", new JObject()); string id = (string)first["sessionResultId"]!;
        int fullSize = wire.Requests.Last().ToString(Formatting.None).Length;
        Require(wire.Requests.Last().ToString().Contains("preserve-within-turn"), "Active continuation signature lost.");
        wire.Replies.Enqueue(FakeHandler.Success); await chat.SendAsync("Follow up");
        int compactSize = wire.Requests.Last().ToString(Formatting.None).Length;
        Require(compactSize < fullSize / 5 && !wire.Requests.Last().ToString().Contains("PRIVATE_DETAIL_"), "Old raw evidence still sent.");
        Require(chat.Messages.Any(m => m.Text == "Inspect fixture"), "Local transcript lost.");
        var page = await Execute("recall_result", new JObject { ["resultId"] = id, ["path"] = "/data/items", ["offset"] = 1, ["limit"] = 2 });
        Require((bool)page["success"]! && (int)page["data"]!["value"]![0]!["id"]! == 1 && (int)page["pagination"]!["nextOffset"]! == 3 && bulk.Executions == 1, "Recall did not page exact cached evidence or re-executed source.");
        var mass = await Execute("recall_result", new JObject { ["resultId"] = id, ["path"] = "/data/mass" });
        Require((string?)mass["data"]!["value"]!["status"] == "missing" && (string?)mass["data"]!["value"]!["unit"] == "kg", "Recall changed missing evidence/units.");
        var escaped = await Execute("recall_result", new JObject { ["resultId"] = id, ["path"] = "/data/a~1b/~0value" });
        Require((int)escaped["data"]!["value"]! == 42, "JSON Pointer escaping failed.");
        foreach (var test in new[] {
            (new JObject { ["resultId"] = id }, "RESULT_TOO_LARGE"),
            (new JObject { ["resultId"] = id, ["path"] = "/absent" }, "RESULT_PATH_NOT_FOUND"),
            (new JObject { ["resultId"] = id, ["path"] = "/~2" }, "INVALID_ARGUMENT"),
            (new JObject { ["resultId"] = id, ["limit"] = 21 }, "INVALID_ARGUMENTS"),
            (new JObject { ["resultId"] = id, ["extra"] = true }, "INVALID_ARGUMENTS") })
        {
            var result = await Execute("recall_result", test.Item1);
            Require((bool?)result["success"] == false && (string?)result["errors"]![0]!["code"] == test.Item2, "Recall validation failed: " + test.Item2);
        }
        for (int i = 0; i < 12; i++) { wire.Replies.Enqueue(FakeHandler.Success); await chat.SendAsync("Recent " + i); }
        var outgoing = wire.Requests.Last();
        Require(((JArray)outgoing["contents"]!).Count <= 13 && !outgoing["contents"]!.ToString().Contains("Inspect fixture"), "Recent conversation window unbounded.");
        for (int i = 0; i < 128; i++) await Execute("bulk", new JObject());
        var evicted = await Execute("recall_result", new JObject { ["resultId"] = id });
        Require((string?)evicted["errors"]![0]!["code"] == "RESULT_NOT_CACHED", "Cache retention unbounded.");
        var directory = await Execute("recall_result", new JObject { ["offset"] = 12, ["limit"] = 2 });
        Require((int)directory["pagination"]!["total"]! == 128 && ((JArray)directory["data"]!["items"]!).Count == 2, "Older cache directory cannot be paged.");
        string lastId = (string)directory["data"]!["items"]![0]!["resultId"]!;
        chat.Clear();
        var cleared = await Execute("recall_result", new JObject { ["resultId"] = lastId });
        Require((string?)cleared["errors"]![0]!["code"] == "RESULT_NOT_CACHED", "Reset retained old cache.");
        var fresh = await Execute("bulk", new JObject()); Require((string?)fresh["sessionResultId"] != id, "Reset reused an old result identity.");
        int beforeFailure = chat.Messages.Count;
        wire.Replies.Enqueue(Call("bulk", new JObject())); wire.Replies.Enqueue("{'candidates':[]}");
        try { await chat.SendAsync("Fail after tool result"); throw new Exception("Missing answer accepted."); }
        catch (ChatException) { }
        Require(chat.Messages.Count == beforeFailure, "Failed turn entered local transcript.");
        string failedTurnId = (string)Returned(wire)["sessionResultId"]!;
        var retained = await Execute("recall_result", new JObject { ["resultId"] = failedTurnId, ["path"] = "/data/mass" });
        Require((bool)retained["success"]!, "Actual tool result lost after subsequent model failure.");
        for (int i = 0; i < 3; i++) { wire.Replies.Enqueue(FakeHandler.Success); await chat.SendAsync(new string('z', 9000)); }
        Require(wire.Requests.Last()["contents"]!.Count() == 3, "Character budget did not bound long completed turns.");
        Console.WriteLine($"PASS: context cache, exact/paged recall, strict arguments, missing units/state, active signatures, bounded history/eviction/reset. Synthetic follow-up request: {fullSize:N0} -> {compactSize:N0} JSON characters (not measured tokens); no live API.");
    }
}
