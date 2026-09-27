#nullable enable
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Speech
{
    // One manual-commit utterance per socket; one serialized sender, one receiver.
    public sealed class RealtimeTranscription : IDisposable
    {
        private readonly WebSocket socket;
        private readonly CancellationTokenSource lifetime;
        private readonly Action<string>? partial;
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> final = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task reader;
        private volatile bool committed;
        private int disposed;
        public Task Completion => reader;
        public RealtimeTranscription(WebSocket socket, Action<string>? partial, CancellationToken token)
        {
            this.socket = socket; this.partial = partial;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            reader = ReadAsync();
        }
        public static async Task<RealtimeTranscription> ConnectAsync(TranscriptionSettings settings, int rate, Action<string>? partial, CancellationToken token)
        {
            if (rate != 8000 && rate != 16000 && rate != 22050 && rate != 24000 && rate != 44100 && rate != 48000)
                throw new ArgumentException("STT_SAMPLE_RATE_UNSUPPORTED: " + rate);
            var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("xi-api-key", settings.ApiKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            RealtimeTranscription? session = null;
            try
            {
                await socket.ConnectAsync(new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime?model_id=scribe_v2_realtime&audio_format=pcm_" + rate + "&commit_strategy=manual"), deadline.Token).ConfigureAwait(false);
                session = new RealtimeTranscription(socket, partial, token);
                await session.WaitReadyAsync(deadline.Token).ConfigureAwait(false);
                return session;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { session?.Dispose(); socket.Dispose(); throw new TimeoutException("STT_CONNECT_TIMEOUT: Could not start realtime transcription in 15 seconds."); }
            catch { session?.Dispose(); socket.Dispose(); throw; }
        }
        public async Task WaitReadyAsync(CancellationToken token)
        {
            using var registration = token.Register(() => ready.TrySetCanceled());
            await ready.Task.ConfigureAwait(false);
        }
        public async Task SendAsync(byte[] pcm, bool commit, CancellationToken token)
        {
            if (committed) throw new InvalidOperationException("STT_ALREADY_COMMITTED");
            if (pcm.Length % 2 != 0) throw new ArgumentException("STT_INVALID_PCM");
            if (reader.IsCompleted) await reader.ConfigureAwait(false);
            committed = commit;
            byte[] json = Encoding.UTF8.GetBytes(new JObject
            {
                ["message_type"] = "input_audio_chunk", ["audio_base_64"] = Convert.ToBase64String(pcm), ["commit"] = commit
            }.ToString(Formatting.None));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { await socket.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("STT_SEND_TIMEOUT: Audio upload stalled."); }
        }
        public async Task<string> FinishAsync(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var registration = deadline.Token.Register(() => final.TrySetCanceled());
            try { return await final.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("STT_COMMIT_TIMEOUT: No final transcript after 15 seconds."); }
        }
        private async Task ReadAsync()
        {
            try
            {
                var buffer = new byte[8192];
                while (!lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream(); WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token).ConfigureAwait(false);
                        if (received.MessageType != WebSocketMessageType.Text) throw new IOException("STT_SOCKET_CLOSED: Connection ended before a final transcript.");
                        if (message.Length + received.Count > 65536) throw new InvalidDataException("STT_MESSAGE_TOO_LARGE");
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    JObject data;
                    try { data = JObject.Parse(Encoding.UTF8.GetString(message.ToArray())); }
                    catch (JsonException) { throw new InvalidDataException("STT_INVALID_RESPONSE"); }
                    string kind = (string?)data["message_type"] ?? "";
                    if (kind == "session_started") ready.TrySetResult(true);
                    else if (kind == "partial_transcript" || kind == "committed_transcript")
                    {
                        if (data["text"]?.Type != JTokenType.String) throw new InvalidDataException("STT_INVALID_TRANSCRIPT");
                        string text = ((string)data["text"]!).Trim();
                        if (text.Length > 8000) throw new InvalidDataException("STT_TRANSCRIPT_TOO_LONG");
                        if (kind == "partial_transcript") partial?.Invoke(text);
                        else
                        {
                            if (!committed) throw new InvalidDataException("STT_UNEXPECTED_COMMIT: Refusing an unsolicited final transcript.");
                            final.TrySetResult(text); return;
                        }
                    }
                    else if (kind != "warning")
                    {
                        // Never echo arbitrary provider error text, which could include input or credentials.
                        throw new IOException("STT_REALTIME_ERROR: " + (kind == "rate_limited" ? "Rate limited; check quota." : kind == "auth_error" ? "Authentication failed; check key." : "Session failed; check key permissions, quota, and connectivity."));
                    }
                }
            }
            catch (Exception ex) { ready.TrySetException(ex); final.TrySetException(ex); throw; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); socket.Abort(); socket.Dispose();
            // Cancellation can finish a socket read after the owner exits. Observe all pending task failures.
            _ = reader.ContinueWith(task => { _ = task.Exception; lifetime.Dispose(); }, TaskScheduler.Default);
            _ = ready.Task.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
            _ = final.Task.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
        }
    }
}
