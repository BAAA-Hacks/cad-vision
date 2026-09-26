using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Core;
using Core.Diagnostics;
using Core.Speech;
using Desktop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class StreamingChecks
{
    private const string First = "The mounting plate has one broken mate.";
    private const string Last = " Its other connection is fully defined.";
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Frame(string? text = null, string? finish = null, int? output = null, JArray? parts = null)
    {
        var candidate = new JObject { ["index"] = 0, ["content"] = new JObject { ["role"] = "model", ["parts"] = parts ?? new JArray(new JObject { ["text"] = text ?? "" }) } };
        if (finish != null) candidate["finishReason"] = finish;
        var value = new JObject { ["candidates"] = new JArray(candidate) };
        if (output != null) value["usageMetadata"] = new JObject { ["promptTokenCount"] = 100, ["candidatesTokenCount"] = output, ["cachedContentTokenCount"] = 20 };
        return "data: " + value.ToString(Formatting.None) + "\n\n";
    }
    private static HttpResponseMessage Response(params string[] frames)
        => Response(new FramesStream(frames));
    private static HttpResponseMessage Response(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream"); return response;
    }
    public static async Task RunAsync()
    {
        var settings = new GeminiSettings("offline-stream-key", "test-model", "Test prompt");
        var synthesized = new List<string>(); var played = new List<byte[]>(); var errors = new List<Exception>();
        var firstSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TurnTokenUsage? usage = null; TokenUsageLog.Configure(value => usage = value);
        try
        {
            using var http = new HttpClient(new Handler(async (request, _) =>
            {
                Require(request.RequestUri!.AbsoluteUri.EndsWith(":streamGenerateContent?alt=sse"), "SSE endpoint not used");
                Require(request.Headers.GetValues("x-goog-api-key").Single() == "offline-stream-key", "Stream authentication missing");
                var payload = JObject.Parse(await request.Content!.ReadAsStringAsync());
                Require(payload["systemInstruction"] != null, "Stream system instructions missing");
                return Response(new FramesStream(new[]
                {
                    Frame(parts: new JArray(new JObject { ["text"] = "Do not speak this reasoning.", ["thought"] = true })),
                    Frame(First + " ", output: 8),
                    Frame(Last.TrimStart(), "STOP", 20)
                }, async index => { if (index == 2) await firstSpeech.Task.WaitAsync(TimeSpan.FromSeconds(3)); }));
            }));
            var chat = new ChatSession(new GeminiClient(http, settings));
            var turn = new StreamingSpeechTurn(new Speech((text, _) =>
            {
                synthesized.Add(text); firstSpeech.TrySetResult(); return Task.FromResult(new byte[] { 0, 0 });
            }), (pcm, _) => { played.Add(pcm); return Task.CompletedTask; }, errors.Add, default);
            string answer = await chat.SendAsync("Check mount", observer: turn.Receive);
            turn.Complete(answer); await turn.Completion;
            Require(answer == First + Last && chat.Messages.Count == 2, "Streaming final answer/history mismatch");
            Require(synthesized.SequenceEqual(new[] { First, Last.TrimStart() }) && played.Count == 2 && errors.Count == 0,
                "Speech did not start early, duplicated text, or leaked thought text");
            Require(usage?.ApiRequests == 1 && usage.InputTokens == 100 && usage.OutputTokens == 20 && usage.CachedInputTokens == 20,
                "Cumulative chunk usage double counted");
        }
        finally { TokenUsageLog.Configure(null); }

        // Function-call rounds are retained exactly but never sent to speech.
        int round = 0; var updates = new List<ChatStreamUpdate>();
        using (var http = new HttpClient(new Handler(async (request, _) =>
        {
            if (round++ == 0) return Response(Frame(finish: "STOP", parts: new JArray(
                new JObject { ["text"] = "This tool preamble must not be spoken." },
                new JObject { ["functionCall"] = new JObject { ["name"] = "unavailable_tool", ["args"] = new JObject(), ["id"] = "call-1" }, ["thoughtSignature"] = "opaque-signature" })));
            var payload = JObject.Parse(await request.Content!.ReadAsStringAsync());
            Require(payload["contents"]!.ToString().Contains("opaque-signature") && payload["contents"]!.ToString().Contains("call-1"), "Tool continuation lost identity/signature");
            return Response(Frame("The requested tool is unavailable.", "STOP"));
        })))
        {
            var gemini = new GeminiClient(http, settings);
            var chat = new ChatSession(gemini);
            await chat.SendAsync("test", observer: updates.Add);
            Require(gemini.LastToolCallCount == 1 && updates.Where(u => u.Kind == ChatStreamEvent.Text).Select(u => u.Text).SequenceEqual(new[] { "The requested tool is unavailable." }), "Tool text escaped to speech");
            Require(updates.Any(u => u.Kind == ChatStreamEvent.DiscardRound), "Tool round was not discarded");
        }

        // Truncation, provider errors, safety endings and malformed SSE never commit partial history.
        foreach (var frames in new[]
        {
            new[] { Frame(First + " ") },
            new[] { Frame(First + " "), "data: {\"error\":{\"code\":500}}\n\n" },
            new[] { Frame(First, "SAFETY") },
            new[] { "data: {broken\n\n" }
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(frames))));
            var chat = new ChatSession(new GeminiClient(http, settings));
            try { await chat.SendAsync("test", observer: _ => { }); throw new Exception("Expected stream failure"); }
            catch (ChatException) { Require(chat.Messages.Count == 0, "Partial stream committed to history"); }
        }

        // Speech failures are independent of successful Gemini text; stop prevents queued speech.
        errors.Clear();
        var failedTurn = new StreamingSpeechTurn(new Speech((_, _) => throw new HttpRequestException("simulated quota failure")),
            (_, _) => throw new Exception("Unexpected playback"), errors.Add, default);
        failedTurn.Receive(new ChatStreamUpdate(ChatStreamEvent.BeginRound));
        failedTurn.Receive(new ChatStreamUpdate(ChatStreamEvent.Text, First + " "));
        failedTurn.Complete(First + " "); await failedTurn.Completion; failedTurn.Cancel();
        Require(errors.Count == 1, "Speech failure not independently reported");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int playbackCount = 0;
        var stoppedTurn = new StreamingSpeechTurn(new Speech(async (_, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Array.Empty<byte>();
        }), (_, _) => { playbackCount++; return Task.CompletedTask; }, errors.Add, default);
        stoppedTurn.Receive(new ChatStreamUpdate(ChatStreamEvent.BeginRound));
        stoppedTurn.Receive(new ChatStreamUpdate(ChatStreamEvent.Text, First + " "));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stoppedTurn.Cancel();
        stoppedTurn.Complete(First); await stoppedTurn.Completion;
        Require(playbackCount == 0, "Cancelled synthesis played late audio");

        using (var cancellation = new CancellationTokenSource())
        using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new FramesStream(
            new[] { Frame(First), Frame(Last, "STOP") }, index => { if (index == 1) cancellation.Cancel(); return Task.CompletedTask; }))))))
        {
            var chat = new ChatSession(new GeminiClient(http, settings));
            try { await chat.SendAsync("cancel", cancellation.Token, _ => { }); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Require(chat.Messages.Count == 0, "Cancelled stream committed"); }
        }
        var buffer = new SpeechTextBuffer();
        Require(buffer.Append("The component mass is approximately 247505.").Count == 0, "Split decimal at chunk boundary");
        var chunks = buffer.Append("93 grams. The next item has no material");
        Require(chunks.Single().Contains("247505.93"), "Decimal was corrupted");
        Require(buffer.Append("", true).Single() == "The next item has no material", "Trailing text lost");
        Console.WriteLine("PASS: early sentence speech, SSE/thought/tool isolation, signatures, cumulative usage, partial-stream rollback, cancellation, speech failure isolation, and decimal boundaries (offline).");
    }
    private sealed class Speech(Func<string, CancellationToken, Task<byte[]>> action) : ISpeechClient
    { public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellation = default) => action(text, cancellation); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken); }
    private sealed class FramesStream(string[] frames, Func<int, Task>? before = null) : Stream
    {
        private int index, offset;
        private byte[]? current;
        public override async Task<int> ReadAsync(byte[] buffer, int bufferOffset, int count, CancellationToken cancellationToken)
        {
            if (current == null)
            {
                if (index == frames.Length) return 0;
                if (before != null) await before(index);
                current = Encoding.UTF8.GetBytes(frames[index++]); offset = 0;
            }
            int amount = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, bufferOffset, amount); offset += amount;
            if (offset == current.Length) current = null;
            return amount;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
