#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Core.Speech;

namespace Core.Speech
{

// A per-turn queue. Gemini never waits for speech; speech errors never roll back a chat turn.
public sealed class StreamingSpeechTurn
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopped;
    private readonly List<CancellationTokenSource> rounds = new();
    private readonly Queue<(string Text, CancellationToken Token)> queue = new Queue<(string, CancellationToken)>();
    private readonly SemaphoreSlim wake = new SemaphoreSlim(0);
    private readonly Action<Exception> report;
    private readonly Func<string, CancellationToken, Task>? streamAndPlay;
    private CancellationTokenSource? round;
    private SpeechTextBuffer buffer = new();
    private string received = "";
    private bool ended;
    private bool finished;
    public Task Completion { get; }

    public StreamingSpeechTurn(ISpeechClient client, Func<byte[], CancellationToken, Task> play,
        Action<Exception> report, CancellationToken cancellation, Func<string, CancellationToken, Task>? streamAndPlay = null)
    {
        this.report = report;
        this.streamAndPlay = streamAndPlay;
        stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Completion = RunAsync(client, play);
    }

    public void Receive(ChatStreamUpdate update)
    {
        lock (gate)
        {
            if (ended) return;
            try
            {
                if (update.Kind == ChatStreamEvent.BeginRound)
                {
                    round?.Cancel();
                    round = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token); rounds.Add(round);
                    buffer = new SpeechTextBuffer(); received = "";
                }
                else if (update.Kind == ChatStreamEvent.DiscardRound) round?.Cancel();
                else if (round != null && !round.IsCancellationRequested)
                {
                    received += update.Text;
                    foreach (var sentence in buffer.Append(update.Text)) Enqueue(sentence, round.Token);
                }
            }
            catch (Exception ex) { CancelLocked(); report(ex); }
        }
    }

    public void Complete(string answer)
    {
        lock (gate)
        {
            if (ended) return;
            try
            {
                // Completion may add the existing output-limit warning. Do not duplicate streamed text.
                if (round == null || !answer.StartsWith(received, StringComparison.Ordinal))
                    throw new InvalidDataException("SPEECH_STREAM_MISMATCH: final answer differs from streamed text; speech stopped.");
                foreach (var sentence in buffer.Append(answer.Substring(received.Length), complete: true)) Enqueue(sentence, round.Token);
                ended = true; wake.Release();
            }
            catch (Exception ex) { CancelLocked(); report(ex); }
        }
    }

    public void Cancel() { lock (gate) { CancelLocked(); } }
    private void CancelLocked()
    {
        if (finished) return;
        ended = true; stopped.Cancel(); wake.Release();
    }

    private async Task RunAsync(ISpeechClient client, Func<byte[], CancellationToken, Task> play)
    {
        try
        {
            while (true)
            {
                await wake.WaitAsync(stopped.Token);
                (string Text, CancellationToken Token) item;
                lock (gate)
                {
                    if (queue.Count == 0) { if (ended) break; else continue; }
                    item = queue.Dequeue();
                }
                try
                {
                    item.Token.ThrowIfCancellationRequested();
                    if (streamAndPlay != null) await streamAndPlay(item.Text, item.Token);
                    else
                    {
                        var pcm = await client.SynthesizeAsync(item.Text, item.Token);
                        item.Token.ThrowIfCancellationRequested();
                        await play(pcm, item.Token);
                    }
                }
                catch (OperationCanceledException) when (item.Token.IsCancellationRequested) { }
                // A dropped or slow connection loses one sentence, not the rest of the answer.
                catch (Exception ex) when (IsTransient(ex)) { report(ex); }
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (Exception ex) { report(ex); }
        finally
        {
            lock (gate)
            {
                ended = true; queue.Clear();
                finished = true;
                foreach (var source in rounds) source.Dispose();
                stopped.Dispose();
                wake.Dispose();
            }
        }
    }
    /// <summary>Network failures worth skipping past; configuration errors (bad key, voice, model) still stop speech.</summary>
    public static bool IsTransient(Exception ex) => ex is TimeoutException || ex is IOException ||
        ex is System.Net.WebException || ex is System.Net.Sockets.SocketException || ex is System.Net.WebSockets.WebSocketException ||
        (ex is System.Net.Http.HttpRequestException http && (!http.Message.StartsWith("ELEVENLABS_HTTP_", StringComparison.Ordinal) ||
            http.Message.StartsWith("ELEVENLABS_HTTP_5", StringComparison.Ordinal) || http.Message.StartsWith("ELEVENLABS_HTTP_429", StringComparison.Ordinal)));
    private void Enqueue(string text, CancellationToken token) { queue.Enqueue((text, token)); wake.Release(); }
}
}
