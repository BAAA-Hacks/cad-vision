using System.Media;
using System.Text;
using Core.Diagnostics;
using Core.Speech;
using Desktop.Configuration;

namespace Desktop;

internal sealed partial class ChatWindow
{
    private ISpeechClient? speechClient;
    private CancellationTokenSource? speechRequest;
    private SoundPlayer? activePlayer;
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private StreamingSpeechTurn? streamingSpeech;
    private string lastSpokenAnswer = "";
    private readonly CheckBox speakReplies = new() { Text = "Speak", AutoSize = true, Enabled = false };
    private readonly Button stopSpeech = new() { Text = "Stop voice", AutoSize = true, Enabled = false };
    private readonly Button replaySpeech = new() { Text = "Speak again", AutoSize = true, Enabled = false };

    private void ConfigureSpeech()
    {
        speechClient = null; lastSpokenAnswer = ""; replaySpeech.Enabled = false;
        speakReplies.Checked = false; speakReplies.Enabled = false;
        try
        {
            var settings = LocalConfiguration.LoadSpeech(directory);
            if (settings == null) return;
            speechClient = new ElevenLabsSpeechClient(http, settings);
            speakReplies.Enabled = speakReplies.Checked = true;
        }
        catch (Exception ex) { DiagnosticLog.Report(ex, "speech.configuration"); }
    }

    private void StopSpeech()
    {
        streamingSpeech?.Cancel();
        speechRequest?.Cancel();
        activePlayer?.Stop();
        stopSpeech.Enabled = false;
    }

    private StreamingSpeechTurn? StartStreamingSpeech(CancellationToken token)
    {
        if (!speakReplies.Checked || speechClient == null) return null;
        var turn = new StreamingSpeechTurn(speechClient, PlayPcmAsync,
            ex => DiagnosticLog.Report(ex, "speech.streaming"), token);
        streamingSpeech = turn; stopSpeech.Enabled = true; replaySpeech.Enabled = false;
        _ = ObserveSpeechCompletionAsync(turn);
        return turn;
    }

    private async Task ObserveSpeechCompletionAsync(StreamingSpeechTurn turn)
    {
        await turn.Completion;
        if (!ReferenceEquals(streamingSpeech, turn)) return;
        streamingSpeech = null;
        if (IsDisposed || Disposing) return;
        stopSpeech.Enabled = false;
        replaySpeech.Enabled = request == null && speechRequest == null && lastSpokenAnswer.Length > 0;
    }

    private async Task PlayPcmAsync(byte[] pcm, CancellationToken cancellation)
    {
        using var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(24000); writer.Write(48000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        }
        wave.Position = 0;
        await playbackGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var player = new SoundPlayer(wave);
            activePlayer = player;
            try
            {
                using var registration = cancellation.Register(player.Stop);
                await Task.Run(() =>
                {
                    player.Load(); cancellation.ThrowIfCancellationRequested();
                    player.PlaySync();
                }, cancellation);
            }
            finally { if (ReferenceEquals(activePlayer, player)) activePlayer = null; }
        }
        finally { playbackGate.Release(); }
    }

    private async Task SpeakAsync(string answer)
    {
        if (speechClient == null || string.IsNullOrWhiteSpace(answer)) return;
        StopSpeech();
        var client = speechClient;
        using var cancellation = new CancellationTokenSource();
        speechRequest = cancellation;
        stopSpeech.Enabled = true; replaySpeech.Enabled = false;
        try
        {
            byte[] pcm = await client.SynthesizeAsync(answer, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || Disposing) return;
            await PlayPcmAsync(pcm, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (!IsDisposed && !Disposing)
        { DiagnosticLog.Report(ex, "speech.synthesis_or_playback"); }
        catch (Exception) when (IsDisposed || Disposing) { /* Window has closed. */ }
        finally
        {
            if (ReferenceEquals(speechRequest, cancellation))
            {
                speechRequest = null;
                if (!IsDisposed && !Disposing)
                {
                    stopSpeech.Enabled = false;
                    replaySpeech.Enabled = request == null && streamingSpeech == null && lastSpokenAnswer.Length > 0;
                }
            }
        }
    }
}
