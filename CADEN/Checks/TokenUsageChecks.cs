using Core;
using Core.Diagnostics;
using Core.Tools;
using Desktop;
using Newtonsoft.Json.Linq;
using System.Net;

internal static class TokenUsageChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Tool : ICadenTool
    {
        public string Name => "inspect";
        public JObject Declaration => JObject.Parse("{'name':'inspect','description':'test','parameters':{'type':'object','properties':{}}}");
        public JObject Execute(JObject args) => new() { ["value"] = "private CAD value" };
    }
    sealed class Wire : HttpMessageHandler
    {
        internal readonly Queue<(HttpStatusCode Status, string Body)> Replies = new();
        internal Action? BeforeSend;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            BeforeSend?.Invoke(); token.ThrowIfCancellationRequested();
            var next = Replies.Dequeue(); return Task.FromResult(new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body) });
        }
    }
    internal static async Task RunAsync()
    {
        var entries = new List<TurnTokenUsage>(); TokenUsageLog.Configure(entries.Add);
        using var wire = new Wire(); using var http = new HttpClient(wire);
        var chat = new ChatSession(new GeminiClient(http, new GeminiSettings("private-test-key", "test", "private prompt"), new ToolRegistry(new[] { new Tool() })));
        string Call = "{'candidates':[{'content':{'role':'model','parts':[{'functionCall':{'name':'inspect','args':{}}}]}}], 'usageMetadata':{'promptTokenCount':100,'candidatesTokenCount':8,'cachedContentTokenCount':40}}";
        string Answer = "{'candidates':[{'content':{'role':'model','parts':[{'text':'Done.'}]}}], 'usageMetadata':{'promptTokenCount':150,'candidatesTokenCount':12}}";
        try
        {
            wire.Replies.Enqueue((HttpStatusCode.OK, Call)); wire.Replies.Enqueue((HttpStatusCode.OK, Answer));
            await chat.SendAsync("private user question");
            var e = entries.Single();
            Require(e.InputTokens == 250 && e.OutputTokens == 20 && e.CachedInputTokens == 40 && e.ApiRequests == 2 && e.UsageComplete && e.Outcome == "completed", "Multi-request usage not summed correctly.");
            Require(!e.ToJson().Contains("private"), "Token log contains prompt, tool result or credential.");
            wire.Replies.Enqueue((HttpStatusCode.OK, Call)); wire.Replies.Enqueue((HttpStatusCode.ServiceUnavailable, "{'error':{'message':'Synthetic unavailable'}}"));
            try { await chat.SendAsync("Fail after tool"); throw new Exception("Expected failure"); } catch (ChatException) { }
            e = entries.Last(); Require(e.Outcome == "failed" && e.InputTokens == 100 && e.OutputTokens == 8 && e.CachedInputTokens == 40 && e.ApiRequests == 2 && !e.UsageComplete, "Partial failure usage was lost or represented as complete.");
            wire.Replies.Enqueue((HttpStatusCode.OK, FakeHandler.Success)); await chat.SendAsync("No usage supplied");
            e = entries.Last(); Require(e.InputTokens == null && e.OutputTokens == null && e.CachedInputTokens == null && !e.UsageComplete, "Absent usage became zero.");
            string oldSession = e.SessionId; chat.Clear();
            wire.Replies.Enqueue((HttpStatusCode.OK, Answer)); await chat.SendAsync("New session");
            Require(entries.Last().SessionId != oldSession && entries.Last().InputTokens == 150, "Reset carried usage/session identity forward.");
            using (var cancel = new CancellationTokenSource())
            {
                int count = 0; wire.BeforeSend = () => { if (++count == 2) cancel.Cancel(); };
                wire.Replies.Enqueue((HttpStatusCode.OK, Call));
                try { await chat.SendAsync("Cancelled continuation", cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
                wire.BeforeSend = null;
                e = entries.Last(); Require(e.Outcome == "cancelled" && e.InputTokens == 100 && e.ApiRequests == 2 && !e.UsageComplete, "Cancellation discarded observed usage.");
            }
            wire.Replies.Enqueue((HttpStatusCode.OK, Answer.Replace("150", "-1")));
            await chat.SendAsync("Invalid usage"); Require(entries.Last().InputTokens == null && !entries.Last().UsageComplete, "Negative usage accepted.");
            string directory = Path.Combine(Path.GetTempPath(), "caden-token-usage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = new FileTokenUsage(directory); file.Write(entries[0]); file.Write(entries[1]);
                var lines = File.ReadAllLines(Directory.GetFiles(directory).Single());
                Require(lines.Length == 2 && (long)JObject.Parse(lines[0])["InputTokens"]! == 250 && (string?)JObject.Parse(lines[1])["Outcome"] == "failed", "Usage log was not append-only JSONL.");
            }
            finally { if (Directory.Exists(directory)) { foreach (var path in Directory.GetFiles(directory)) File.Delete(path); Directory.Delete(directory); } }
            TokenUsageLog.Configure(_ => throw new IOException("Synthetic usage writer failure"));
            wire.Replies.Enqueue((HttpStatusCode.OK, Answer)); await chat.SendAsync("Writer failure should not fail chat");
            Require(DiagnosticLog.GetRecent().Any(d => d.Operation == "usage.log_write"), "Usage writer failure was silent.");
            Console.WriteLine("PASS: per-turn input/output/cached usage sums, partial failures/cancellation, missing/malformed usage, session reset, private-content exclusion, JSONL append and visible sink-failure diagnostics. No live API.");
        }
        finally { TokenUsageLog.Configure(null); }
    }
}
