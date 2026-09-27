using System;
using System.Net.Http;
using System.Threading;
using Core.Diagnostics;
using Core.Speech;
using Desktop.Configuration;
using UnityEngine;

namespace CADEN.Unity
{
    public sealed class CadenVoiceInput : MonoBehaviour
    {
        public event Action<string> Message;
        public bool Recording => clip != null;
        public bool Busy => clip != null || pending != null;
        public string Status { get; private set; } = "Y: record, Y again: send";
        private readonly HttpClient http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private AudioClip clip;
        private string device;
        private float started;
        private CadenUnityHost host;
        private Core.Tools.ToolRegistry registry;
        private ElevenLabsTranscriptionClient client;
        private CancellationTokenSource pending;
        private bool submitted;

        public void Toggle(CadenUnityHost target)
        {
            if (Recording) { Finish(); return; }
            if (Busy) return;
            try
            {
                if (target == null || !target.Ready || target.IsBusy) throw new InvalidOperationException("Wait for CADEN ready before recording.");
                client = new ElevenLabsTranscriptionClient(http, LocalConfiguration.LoadTranscription(target.ConfigurationPath));
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                    Notify("Allow microphone access, then press Y again. If denied, enable Microphone in the app permissions.");
                    return;
                }
#else
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                {
                    Application.RequestUserAuthorization(UserAuthorization.Microphone);
                    Notify("Allow microphone access, then press Record again."); return;
                }
#endif
                if (Microphone.devices.Length == 0) throw new InvalidOperationException("STT_MIC_MISSING: No microphone detected.");
                host = target; registry = target.Registry; device = Microphone.devices[0];
                if (Microphone.IsRecording(device)) throw new InvalidOperationException("STT_MIC_BUSY: Microphone is already recording.");
                host.StopSpeech();
                clip = Microphone.Start(device, false, 31, 16000);
                if (clip == null) throw new InvalidOperationException("STT_MIC_START_FAILED");
                started = Time.unscaledTime;
                Status = "Recording — Y again to send (30 seconds max)";
                Notify(Status);
            }
            catch (Exception ex) { ReleaseMicrophone(); Fail(ex); }
        }

        private void Update()
        {
            if (!Busy) return;
            if (host == null || !host.Ready || !ReferenceEquals(registry, host.Registry))
            { Cancel(); Notify("Voice cancelled: model or chat session changed."); return; }
            if (!Recording) return;
            float elapsed = Time.unscaledTime - started;
            if (elapsed > 3 && Microphone.GetPosition(device) <= 0)
            { ReleaseMicrophone(); Fail(new InvalidOperationException("STT_MIC_NO_DATA: Microphone delivered no samples.")); return; }
            if (elapsed >= 30) Finish();
        }

        private async void Finish()
        {
            if (clip == null || pending != null) return;
            var operation = new CancellationTokenSource(); pending = operation;
            var token = operation.Token;
            try
            {
                int frames = Microphone.GetPosition(device);
                if (frames <= 0 || frames < clip.frequency / 4) throw new InvalidOperationException("STT_TOO_SHORT: Record at least a quarter second.");
                int channels = clip.channels, rate = clip.frequency;
                var samples = new float[frames * channels];
                if (!clip.GetData(samples, 0)) throw new InvalidOperationException("STT_MIC_READ_FAILED");
                ReleaseMicrophone();
                double energy = 0;
                foreach (float sample in samples) energy += sample * sample;
                if (Math.Sqrt(energy / samples.Length) < 0.0001)
                { Notify("No audible speech captured. Nothing sent."); return; }
                byte[] wav = SpeechWave.Encode(samples, frames, channels, rate);
                Status = "Transcribing with ElevenLabs..."; Notify(Status);
                string transcript = await client.TranscribeAsync(wav, token);
                token.ThrowIfCancellationRequested();
                if (this == null || host == null || !host.Ready || !ReferenceEquals(registry, host.Registry))
                    throw new OperationCanceledException();
                if (string.IsNullOrWhiteSpace(transcript)) { Notify("No speech recognized. Nothing sent to CADEN."); return; }
                Status = "CADEN is thinking...";
                Notify("YOU: " + transcript + "\n\n" + Status);
                submitted = true;
                string answer = await host.SendAsync(transcript);
                token.ThrowIfCancellationRequested();
                if (this != null) Notify("YOU: " + transcript + "\n\nCADEN: " + answer);
            }
            catch (OperationCanceledException) { if (this != null) Notify("Voice request cancelled."); }
            catch (Exception ex) { if (this != null) Fail(ex); }
            finally
            {
                ReleaseMicrophone(); submitted = false;
                if (ReferenceEquals(pending, operation)) pending = null;
                operation.Dispose(); Status = "Y: record, Y again: send";
            }
        }

        public void Cancel()
        {
            ReleaseMicrophone(); pending?.Cancel();
            if (submitted && host != null) host.CancelTurn();
            Status = "Y: record, Y again: send"; Notify("Voice cancelled.");
        }
        private void ReleaseMicrophone()
        {
            if (clip == null) return;
            Microphone.End(device); Destroy(clip); clip = null;
        }
        private void Notify(string message) => Message?.Invoke(message);
        private void Fail(Exception ex)
        {
            var receipt = DiagnosticLog.Report(ex, "unity.speech_to_text");
            Debug.LogError(receipt.UserMessage); Status = "Voice error; see panel"; Notify(receipt.UserMessage);
        }
        private void OnApplicationPause(bool paused) { if (paused && Busy) Cancel(); }
        private void OnApplicationFocus(bool focused) { if (!focused && Busy) Cancel(); }
        private void OnDisable() { if (Busy) Cancel(); }
        private void OnDestroy() { Cancel(); http.Dispose(); }
    }
}
