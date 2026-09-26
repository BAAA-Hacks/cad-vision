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
        internal Func<JObject, string>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(JObject.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Respond?.Invoke(Requests.Last()) ?? Replies.Dequeue()) };
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
        await WithinTurn();
    }

    static async Task WithinTurn()
    {
        var raw = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scope_assembly_metadata.json")));
        // Inflate only the unselected discovery row, to verify it is not resent as a summary.
        raw["objects"]!.First(o => (string?)o["id"] == "BOLT_B")["name"] = "Bolt_UNSELECTED_" + new string('x', 300);
        for (int i = 0; i < 18; i++)
        {
            var extra = (JObject)raw["objects"]!.First(o => (string?)o["id"] == "BOLT_B").DeepClone();
            extra["id"] = "EXTRA_" + i; extra["name"] = "Bolt_Z_" + i + new string('x', 300);
            ((JArray)raw["objects"]!).Add(extra);
            ((JArray)raw["objects"]!.First(o => (string?)o["id"] == "GEAR")["childIds"]!).Add((string)extra["id"]!);
        }
        var load = Core.Primitives.Operations.Project.LoadProject.Load(raw.ToString());
        Require(load.Success, "Pruning fixture import failed.");
        var snapshot = load.Snapshot!;
        var association = new Core.Primitives.DataStructures.Memory.ProjectAssociation("pruning");
        var tools = Core.Tools.Query.SemanticQueryTools.Create(snapshot, association);
        JObject Args(JObject fields) { fields["projectId"] = association.ProjectId; fields["snapshotId"] = snapshot.SnapshotId; return fields; }
        using var wire = new Wire(); using var http = new HttpClient(wire);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("offline", "test", "System"), tools));
        string id = ""; JObject? original = null;
        wire.Respond = request =>
        {
            switch (wire.Requests.Count)
            {
                case 1: return Call("find_objects", Args(new JObject { ["query"] = "Bolt" }));
                case 2:
                    original = (JObject)Returned(wire).DeepClone(); id = (string)original["sessionResultId"]!;
                    Require(original["data"]!["items"]!.Count() == 20, "Discovery pruned before model could select.");
                    return Call("get_object_details", Args(new JObject { ["objectIds"] = new JArray("BOLT_A"), ["fields"] = new JArray("mass") }));
                case 3:
                    var older = (JObject)request["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!;
                    Require(older["data"]!["items"]!.Count() == 1 && (string?)older["data"]!["items"]![0]!["id"] == "BOLT_A", "Unfollowed discovery rows not removed.");
                    Require(!request.ToString().Contains("Bolt_UNSELECTED_"), "Unselected details still in request.");
                    Require(JToken.DeepEquals(older["coverage"], original!["coverage"]) && JToken.DeepEquals(older["pagination"], original["pagination"]), "Pruning changed original coverage or pagination.");
                    Require((int)older["contextPruning"]!["references"]![0]!["omittedCount"]! == 19, "Missing reference/count safeguard.");
                    Require(request.ToString().Contains("preserve-within-turn"), "Pruning lost model signatures.");
                    return Call("recall_result", new JObject { ["resultId"] = id, ["path"] = "/data/items", ["offset"] = 1, ["limit"] = 1 });
                case 4:
                    Require(Returned(wire)["data"]!["value"]!.ToString().Contains("Bolt_UNSELECTED_"), "Pruned evidence not recoverable from original cache.");
                    return Call("get_object_details", Args(new JObject { ["objectIds"] = new JArray("BOLT_B"), ["fields"] = new JArray("mass") }));
                case 5:
                    Require(request["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["data"]!["items"]!.Count() == 2,
                        "Following a previously pruned ID did not restore original evidence.");
                    return FakeHandler.Success;
                default: throw new Exception("Unexpected pruning request.");
            }
        };
        await chat.SendAsync("Find bolts, inspect A, then recover the alternative.");
        int full = original!.ToString(Formatting.None).Length;
        int compact = wire.Requests[2]["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!.ToString(Formatting.None).Length;
        Require(compact < full / 2, "Discovery pruning did not materially reduce context.");
        Console.WriteLine($"PASS: within-turn selection pruning, reference-only alternatives, unchanged coverage/pagination/signatures and exact recall. Discovery response {full:N0} -> {compact:N0} characters; no live API.");
        chat.Clear(); wire.Requests.Clear();
        JObject? connections = null;
        wire.Respond = request =>
        {
            switch (wire.Requests.Count)
            {
                case 1: return Call("find_connections", Args(new JObject { ["objectId"] = "DRIVE", ["relation"] = "all" }));
                case 2:
                    connections = (JObject)Returned(wire).DeepClone();
                    return Call("get_object_details", Args(new JObject { ["objectIds"] = new JArray("SHAFT"), ["fields"] = new JArray("mass") }));
                case 3:
                    var retained = request["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!["data"]!["items"]!;
                    Require(retained.Count() == 2, "Selected endpoint failed to retain connecting mate evidence.");
                    foreach (var mate in retained)
                        Require(JToken.DeepEquals(mate, connections!["data"]!["items"]!.First(m => (string?)m["mateId"] == (string?)mate["mateId"])),
                            "Mate endpoints/status/provenance were stripped.");
                    return FakeHandler.Success;
                default: throw new Exception("Unexpected mate-pruning request.");
            }
        };
        await chat.SendAsync("Inspect drivetrain connections, then shaft mass.");
    }
}
