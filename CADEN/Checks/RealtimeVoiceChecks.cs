using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Core.Speech;
using Newtonsoft.Json.Linq;

internal static class RealtimeVoiceChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var fifo = new PcmStreamBuffer();
        await fifo.AppendAsync(new byte[] { 0 }, default);
        Require(fifo.Count == 0, "Odd-byte fragment retained");
        await fifo.AppendAsync(new byte[] { 64, 0, 128 }, default);
        var samples = new float[3]; fifo.Read(samples);
        Require(samples.SequenceEqual(new[] { .5f, -1f, 0f }), "PCM fragments reconstructed; underflow padded");
        await fifo.AppendAsync(new byte[] { 0, 32 }, default); fifo.Complete();
        Require(!fifo.Drained, "EOF must not discard tail"); fifo.Read(samples);
        Require(samples[0] == .25f && fifo.Drained, "Final PCM tail preserved");
        var odd = new PcmStreamBuffer(); await odd.AppendAsync(new byte[] { 1 }, default);
        try { odd.Complete(); throw new Exception("Truncated PCM accepted"); } catch (InvalidDataException) { }
        var bounded = new PcmStreamBuffer(); await bounded.AppendAsync(new byte[480000], default);
        using (var cancellation = new CancellationTokenSource())
        {
            var blocked = bounded.AppendAsync(new byte[2], cancellation.Token);
            Require(!blocked.IsCompleted, "Buffer backpressure"); cancellation.Cancel();
            try { await blocked; throw new Exception("Backpressure cancellation ignored"); } catch (OperationCanceledException) { }
        }

        using var socket = new FakeSocket();
        string partial = "";
        using (var session = new RealtimeTranscription(socket, text => partial = text, default))
        {
            socket.Push("{\"message_type\":\"session_started\"}"); await session.WaitReadyAsync(default);
            socket.Push("{\"message_type\":\"partial_transcript\",\"text\":\"isolate base\"}");
            await session.SendAsync(new byte[] { 1, 0, 2, 0 }, false, default);
            Require(socket.Sent.Count == 1 && !(bool)socket.Sent[0]["commit"]!, "Audio streamed before commit");
            await session.SendAsync(new byte[3200], true, default);
            Require(await session.FinishAsync(default) == "Isolate the base.", "Final transcript returned");
            Require(partial == "isolate base", "Partial observed before final");
            Require(socket.Sent.Count == 2 && (bool)socket.Sent[1]["commit"]!, "Single explicit commit");
            try { await session.SendAsync(new byte[2], true, default); throw new Exception("Double commit accepted"); } catch (InvalidOperationException) { }
        }
        using (var badSocket = new FakeSocket())
        using (var bad = new RealtimeTranscription(badSocket, null, default))
        {
            badSocket.Push("{\"message_type\":\"auth_error\",\"error\":\"sensitive provider details\"}");
            try { await bad.WaitReadyAsync(default); throw new Exception("Provider error accepted"); }
            catch (IOException ex) { Require(ex.Message.Contains("STT_REALTIME_ERROR") && !ex.Message.Contains("sensitive"), "Safe provider error"); }
        }
        using (var cancelSocket = new FakeSocket())
        using (var cancelled = new RealtimeTranscription(cancelSocket, null, default))
        using (var cancellation = new CancellationTokenSource())
        {
            cancelSocket.Push("{\"message_type\":\"session_started\"}"); await cancelled.WaitReadyAsync(default);
            var result = cancelled.FinishAsync(cancellation.Token); cancellation.Cancel();
            try { await result; throw new Exception("Commit cancellation ignored"); } catch (OperationCanceledException) { }
        }
        // The first PCM chunk must reach playback while the HTTP body is still unfinished.
        var stream = new GatedStream(); var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(request =>
        {
            Require(request.RequestUri!.AbsolutePath.EndsWith("/voice/stream"), "Streaming TTS endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }));
        var tts = new ElevenLabsSpeechClient(http, new ElevenLabsSettings("offline-test", "voice"));
        int bytes = 0;
        var download = tts.StreamAsync("A complete sentence for testing.", (chunk, token) => { bytes += chunk.Length; firstChunk.TrySetResult(); return Task.CompletedTask; }, default);
        await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!download.IsCompleted, "TTS did not wait for entire response before delivering PCM");
        stream.Release.TrySetResult(); await download;
        Require(bytes == 4, "All streamed PCM delivered");
        bool streamed = false;
        var turn = new StreamingSpeechTurn(new BufferedMustNotRun(), (_, _) => throw new Exception("Buffered playback used"),
            ex => throw new Exception("Speech queue error", ex), default,
            (text, token) => { streamed = true; return Task.CompletedTask; });
        turn.Receive(new Core.ChatStreamUpdate(Core.ChatStreamEvent.BeginRound, ""));
        turn.Receive(new Core.ChatStreamUpdate(Core.ChatStreamEvent.Text, "The mounting plate is fully constrained. "));
        turn.Complete("The mounting plate is fully constrained. ");
        await turn.Completion;
        Require(streamed, "Unity streaming delegate bypasses full-sentence buffering");
        Console.WriteLine("Realtime voice checks passed (fake WebSocket/HTTP; no API calls).");
    }
    private sealed class BufferedMustNotRun : ISpeechClient
    { public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellation = default) => throw new Exception("Buffered synthesis used"); }
    private sealed class FakeSocket : WebSocket
    {
        readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        byte[]? frame; int offset;
        public List<JObject> Sent { get; } = new();
        WebSocketState state = WebSocketState.Open;
        public void Push(string text) => incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) => CloseAsync(closeStatus, description, token);
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var data = JObject.Parse(Encoding.UTF8.GetString(bytes)); Sent.Add(data);
            if ((bool)data["commit"]!) Push("{\"message_type\":\"committed_transcript\",\"text\":\"Isolate the base.\"}");
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken token)
        {
            if (frame == null) { frame = await incoming.Reader.ReadAsync(token); offset = 0; }
            int take = Math.Min(7, frame.Length - offset); // Fragment every JSON message.
            Array.Copy(frame, offset, bytes.Array!, bytes.Offset, take); offset += take;
            bool end = offset == frame.Length; if (end) frame = null;
            return new WebSocketReceiveResult(take, WebSocketMessageType.Text, end);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
    private sealed class GatedStream : MemoryStream
    {
        int step;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (step++ == 0) { buffer[offset] = 0; buffer[offset + 1] = 64; return 2; }
            if (step == 2) { await Release.Task.WaitAsync(token); buffer[offset] = 0; buffer[offset + 1] = 32; return 2; }
            return 0;
        }
    }
}
