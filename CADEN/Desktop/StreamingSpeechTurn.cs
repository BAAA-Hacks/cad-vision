using System.Threading.Channels;
using Core;
using Core.Speech;

namespace Desktop;

// A per-turn queue. Gemini never waits for speech; speech errors never roll back a chat turn.
internal sealed class StreamingSpeechTurn
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopped;
    private readonly List<CancellationTokenSource> rounds = new();
    private readonly Channel<(string Text, CancellationToken Token)> queue = Channel.CreateUnbounded<(string, CancellationToken)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Exception> report;
    private CancellationTokenSource? round;
    private SpeechTextBuffer buffer = new();
    private string received = "";
    private bool ended;
    private bool finished;
    public Task Completion { get; }

    public StreamingSpeechTurn(ISpeechClient client, Func<byte[], CancellationToken, Task> play,
        Action<Exception> report, CancellationToken cancellation)
    {
        this.report = report;
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
                    foreach (var sentence in buffer.Append(update.Text)) queue.Writer.TryWrite((sentence, round.Token));
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
                foreach (var sentence in buffer.Append(answer.Substring(received.Length), complete: true)) queue.Writer.TryWrite((sentence, round.Token));
                ended = true; queue.Writer.TryComplete();
            }
            catch (Exception ex) { CancelLocked(); report(ex); }
        }
    }

    public void Cancel() { lock (gate) { CancelLocked(); } }
    private void CancelLocked()
    {
        if (finished) return;
        ended = true; stopped.Cancel(); queue.Writer.TryComplete();
    }

    private async Task RunAsync(ISpeechClient client, Func<byte[], CancellationToken, Task> play)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stopped.Token))
            {
                try
                {
                    item.Token.ThrowIfCancellationRequested();
                    var pcm = await client.SynthesizeAsync(item.Text, item.Token);
                    item.Token.ThrowIfCancellationRequested();
                    await play(pcm, item.Token);
                }
                catch (OperationCanceledException) when (item.Token.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (Exception ex) { report(ex); }
        finally
        {
            lock (gate)
            {
                ended = true; queue.Writer.TryComplete();
                finished = true;
                foreach (var source in rounds) source.Dispose();
                stopped.Dispose();
            }
        }
    }
}
