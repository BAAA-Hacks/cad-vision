using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using Core.Diagnostics;
using Core.Speech;
using Desktop.Configuration;
using UnityEngine;

namespace CADEN.Unity
{
    public sealed class CadenVoiceInput : MonoBehaviour
    {
        public event Action<string> Message;
        public bool Recording => clip != null && !stopRequested;
        public bool Busy => pending != null;
        public string Status { get; private set; } = "Y: record, Y again: send";
        public string LastError { get; private set; } = "";
        public string Transcript { get; private set; } = "";
        private AudioClip clip;
        private string device;
        private float started;
        private CadenUnityHost host;
        private Core.Tools.ToolRegistry registry;
        private CancellationTokenSource pending;
        private bool submitted, stopRequested;
        private int stoppedFrames;

        public void Toggle(CadenUnityHost target)
        {
            if (Recording) { StopCapture(); return; }
            if (Busy) return;
            LastError = "";
            Transcript = "";
            try
            {
                if (target == null || !target.Ready || target.IsBusy) throw new InvalidOperationException("Wait for CADEN ready before recording.");
                var settings = LocalConfiguration.LoadTranscription(target.ConfigurationPath);
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                    Notify("Allow microphone access, then press Y again. If denied, enable Microphone in app permissions."); return;
                }
#else
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                {
                    Application.RequestUserAuthorization(UserAuthorization.Microphone);
                    Notify("Allow microphone access, then press Y again."); return;
                }
#endif
                if (Microphone.devices.Length == 0) throw new InvalidOperationException("STT_MIC_MISSING");
                device = Microphone.devices[0];
                if (Microphone.IsRecording(device)) throw new InvalidOperationException("STT_MIC_BUSY");
                host = target; registry = target.Registry; host.StopSpeech();
                clip = Microphone.Start(device, false, 31, 16000);
                if (clip == null) throw new InvalidOperationException("STT_MIC_START_FAILED");
                started = Time.unscaledTime; stoppedFrames = 0; stopRequested = false;
                pending = new CancellationTokenSource();
                Status = "Recording / connecting realtime STT — Y to send"; Notify(Status);
                RunAsync(settings, pending);
            }
            catch (Exception ex) { ReleaseMicrophone(); Fail(ex); }
        }
        private void Update()
        {
            if (!Busy) return;
            if (host == null || !host.Ready || !ReferenceEquals(registry, host.Registry))
            { Cancel(); return; }
            if (!Recording) return;
            if (Time.unscaledTime - started >= 30) StopCapture();
        }
        private void StopCapture()
        {
            if (!Recording) return;
            stoppedFrames = Microphone.GetPosition(device); Microphone.End(device);
            stopRequested = true; Status = "Finalizing transcript..."; Notify(Status);
        }
        private async void RunAsync(TranscriptionSettings settings, CancellationTokenSource operation)
        {
            var token = operation.Token;
            var context = SynchronizationContext.Current;
            RealtimeTranscription realtime = null;
            int sent = 0; double energy = 0;
            try
            {
                // Microphone is already capturing, so connection setup cannot drop the first words.
                realtime = await RealtimeTranscription.ConnectAsync(settings, clip.frequency, partial =>
                    context.Post(_ => { if (this != null && !token.IsCancellationRequested && ReferenceEquals(pending, operation) && !stopRequested) { Transcript = partial; Status = "Listening — Y to send"; } }, null), token);
                token.ThrowIfCancellationRequested();
                int rate = clip.frequency, channels = clip.channels;
                if (!stopRequested) { Status = "Recording / streaming to ElevenLabs — Y to send"; Notify(Status); }
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (realtime.Completion.IsCompleted) await realtime.Completion;
                    int available = stopRequested ? stoppedFrames : Microphone.GetPosition(device);
                    if (available < sent) throw new InvalidOperationException("STT_MIC_CURSOR_RESET");
                    if (!stopRequested && available == 0 && Time.unscaledTime - started > 3) throw new InvalidOperationException("STT_MIC_NO_DATA");
                    // At most 100 ms per message; never read samples that have not been recorded yet.
                    int frames = Math.Min(rate / 10, available - sent);
                    if (frames > 0 && (frames == rate / 10 || stopRequested))
                    {
                        var samples = new float[frames * channels];
                        if (!clip.GetData(samples, sent)) throw new InvalidOperationException("STT_MIC_READ_FAILED");
                        var bytes = new byte[frames * 2];
                        for (int i = 0; i < frames; i++)
                        {
                            float mono = 0;
                            for (int c = 0; c < channels; c++) mono += samples[i * channels + c] / channels;
                            if (float.IsNaN(mono) || float.IsInfinity(mono)) throw new InvalidOperationException("STT_INVALID_SAMPLE");
                            energy += mono * mono;
                            short value = (short)Math.Round(Math.Max(-1, Math.Min(1, mono)) * 32767);
                            bytes[i * 2] = (byte)value; bytes[i * 2 + 1] = (byte)(value >> 8);
                        }
                        await realtime.SendAsync(bytes, false, token); sent += frames;
                    }
                    else if (stopRequested && sent == available) break;
                    else await Task.Delay(20, token);
                }
                ReleaseMicrophone();
                if (sent < rate / 4 || Math.Sqrt(energy / Math.Max(1, sent)) < 0.0001)
                { Notify("No audible speech captured. Nothing sent to CADEN."); return; }
                // Commit a short silence tail after all real samples; this produces one final transcript.
                await realtime.SendAsync(new byte[rate / 10 * 2], true, token);
                string transcript = await realtime.FinishAsync(token);
                Transcript = transcript;
                realtime.Dispose(); realtime = null;
                token.ThrowIfCancellationRequested();
                if (this == null || host == null || !host.Ready || !ReferenceEquals(registry, host.Registry)) throw new OperationCanceledException();
                if (string.IsNullOrWhiteSpace(transcript)) { Notify("No speech recognized. Nothing sent to CADEN."); return; }
                Status = "CADEN is thinking..."; Notify("YOU: " + transcript + "\n\n" + Status);
                submitted = true;
                string answer = await host.SendAsync(transcript);
                token.ThrowIfCancellationRequested();
                if (this != null) Notify("YOU: " + transcript + "\n\nCADEN: " + answer);
            }
            catch (OperationCanceledException) { if (this != null) Notify("Voice request cancelled."); }
            catch (Exception ex) { if (this != null) Fail(ex); }
            finally
            {
                realtime?.Dispose(); ReleaseMicrophone(); submitted = false;
                if (ReferenceEquals(pending, operation)) pending = null;
                operation.Dispose();
                if (LastError.Length == 0) Status = "Y: record, Y again: send";
            }
        }
        public void Cancel()
        {
            pending?.Cancel(); ReleaseMicrophone();
            if (submitted && host != null) host.CancelTurn();
            if (LastError.Length == 0) Status = "Y: record, Y again: send";
        }
        private void ReleaseMicrophone()
        {
            if (clip == null) return;
            Microphone.End(device); Destroy(clip); clip = null;
        }
        private void Notify(string message) => Message?.Invoke(message);
        private void Fail(Exception ex)
        {
            string summary = "VOICE_FAILED: " + DiagnosticLog.Redact(ex.Message);
            for (Exception cause = ex; cause != null; cause = cause.InnerException)
            {
                if (cause is SocketException socket &&
                    (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.TryAgain || socket.SocketErrorCode == SocketError.NoData))
                {
                    summary = "STT_DNS_FAILED: Quest could not resolve api.elevenlabs.io. Check headset Wi-Fi, internet access and network sign-in, then press Y to retry.";
                    break;
                }
            }
            var receipt = DiagnosticLog.Report(new InvalidOperationException(summary, ex), "unity.speech_to_text");
            Debug.LogError(receipt.UserMessage);
            LastError = summary + "\nDiagnostic ID: " + receipt.Entry.CorrelationId;
            Status = "VOICE FAILED — see error in chat";
            Notify(LastError);
        }
        private void OnApplicationPause(bool paused) { if (paused && Busy) Cancel(); }
        private void OnApplicationFocus(bool focused) { if (!focused && Busy) Cancel(); }
        private void OnDisable() { if (Busy) Cancel(); }
        private void OnDestroy() { Cancel(); }
    }
}
