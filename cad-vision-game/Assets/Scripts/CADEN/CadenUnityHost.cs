using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using Core;
using Core.Diagnostics;
using Core.Speech;
using Core.Tools;
using Core.Tools.Query;
using Core.Tools.View;
using Core.Tools.Issues;
using Core.Tools.Memory;
using Core.Primitives.Operations.Project;
using Desktop.Configuration;
using Desktop.Persistence;
using UnityEngine;
using UnityEngine.Events;

namespace CADEN.Unity
{
    [DefaultExecutionOrder(220)]
    [DisallowMultipleComponent]
    public sealed class CadenUnityHost : MonoBehaviour
    {
        [Tooltip("Optional external CADEN configuration directory. Never put API keys in Assets.")]
        public string ConfigurationDirectory;
        public TextAsset SystemPrompt;
        public UnityEvent<string> AnswerReceived = new UnityEvent<string>();
        public UnityEvent<string> InputReceived = new UnityEvent<string>();
        public UnityEvent<string> StatusChanged = new UnityEvent<string>();
        public UnityEvent<string> ErrorReceived = new UnityEvent<string>();
        public string Status { get; private set; } = "Waiting for model";
        public ToolRegistry Registry { get; private set; }
        public bool Ready => session != null;
        public ChatSession Session => session;
        public bool IsBusy => turn != null;
        public bool IsSpeaking => audioSource != null && audioSource.isPlaying;
        private readonly float[] levelSamples = new float[256];
        private const int SpeechStallMilliseconds = 10000;
        private const double SpeechTailPaddingSeconds = 0.3;
        public string ConfigurationPath => ResolveConfigurationDirectory();
        public CADVisionRuntime ModelRuntime => runtime;
        private readonly HttpClient http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private readonly object logGate = new object();
        private readonly SemaphoreSlim audioGate = new SemaphoreSlim(1, 1);
        private CADVisionRuntime runtime;
        private CADVisionManipulationService manipulation;
        private UnityViewHost view;
        private ChatSession session;
        private ISpeechClient speech;
        private StreamingSpeechTurn speaking;
        private AudioSource audioSource;
        private CancellationTokenSource modelLifetime;
        private CancellationTokenSource turn;
        private SynchronizationContext mainContext;
        private float nextSearch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CadenUnityHost>() != null) return;
            var service = FindAnyObjectByType<CADVisionManipulationService>();
            if (service == null) return;
            if (service.GetComponent<CADRuntimeBridge>() == null) service.gameObject.AddComponent<CADRuntimeBridge>();
            service.gameObject.AddComponent<CadenUnityHost>();
        }

        private void Awake()
        {
            mainContext = SynchronizationContext.Current;
            manipulation = GetComponent<CADVisionManipulationService>();
            if (manipulation == null) manipulation = FindAnyObjectByType<CADVisionManipulationService>();
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false; audioSource.spatialBlend = 0;
            if (SystemPrompt == null) SystemPrompt = Resources.Load<TextAsset>("CadenSystemPrompt");
            string logs = Path.Combine(Application.persistentDataPath, "CADEN", "logs");
            Directory.CreateDirectory(logs);
            void Write(string prefix, string json)
            { lock (logGate) File.AppendAllText(Path.Combine(logs, prefix + "-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"), json + Environment.NewLine); }
            DiagnosticLog.Configure(entry => Write("diagnostics", entry.ToJson()), logs);
            TokenUsageLog.Configure(entry => Write("tokens", entry.ToJson()));
        }
        private void Start() => Connect();
        private void Update()
        {
            if (runtime != null || Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 0.5f; Connect();
        }
        private void Connect()
        {
            if (runtime != null) return;
            runtime = FindAnyObjectByType<CADVisionRuntime>();
            if (runtime == null) return;
            runtime.ModelChanged += ModelChanged;
            ModelChanged();
        }
        private void ModelChanged() { _ = ReloadAsync(); }
        private void Revoke()
        {
            view?.Invalidate(); view = null; session = null; Registry = null;
            turn?.Cancel(); StopSpeech(); modelLifetime?.Cancel(); modelLifetime?.Dispose(); modelLifetime = null;
        }
        public async Task ReloadAsync()
        {
            Revoke();
            var lifetime = new CancellationTokenSource(); modelLifetime = lifetime;
            var token = lifetime.Token;
            SetStatus("Loading CADEN metadata and issues");
            try
            {
                // All ModelChanged subscribers (including the Task 3 bridge) finish before capture.
                await Task.Yield(); token.ThrowIfCancellationRequested();
                if (this == null || !isActiveAndEnabled || runtime == null || runtime.Metadata == null)
                { SetStatus("Waiting for model"); return; }
                int revision = runtime.Revision;
                string json = runtime.Metadata.RawJson;
                string directory = ResolveConfigurationDirectory();
                Directory.CreateDirectory(directory);
                var association = ProjectAssociationFile.LoadOrCreate(directory);
                var loaded = await Task.Run(() => LoadProject.Load(json), token);
                if (!loaded.Success) throw new InvalidDataException(string.Join("; ", loaded.Diagnostics.Where(d => d.Fatal).Select(d => d.Code + ": " + d.Message)));
                var snapshot = loaded.Snapshot;
                var failures = new Dictionary<string, HostCapabilityFailure>();
                IssueAccess issues = null; MemoryAccess memory = null;
                string data = Path.Combine(directory, "data");
                try { issues = await IssueAccess.OpenAsync(snapshot, association, new ProjectMemoryFile(Path.Combine(data, "issues.caden.json")), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { var d = Report(ex, "unity.issues_load"); failures["issues"] = HostCapabilityFailure.FromException("issues", ex, d.Entry.CorrelationId); }
                try { memory = await Task.Run(() => MemoryAccess.Open(snapshot, association, new ProjectMemoryFile(Path.Combine(data, "memory.caden.json"))), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { var d = Report(ex, "unity.memory_load"); failures["memory"] = HostCapabilityFailure.FromException("memory", ex, d.Entry.CorrelationId); }
                token.ThrowIfCancellationRequested();
                if (runtime == null || runtime.Revision != revision) throw new OperationCanceledException(token);
                view = new UnityViewHost(runtime, manipulation, snapshot);
                Registry = SemanticQueryTools.Create(snapshot, association, issues: issues, memory: memory,
                    initializationFailures: failures, hostTools: ViewTools.Create(view));
                // Editor uses the existing authoritative prompt. Players use the packaged TextAsset.
                string promptOverride = File.Exists(Path.Combine(directory, "prompts", "system.md")) ? null : SystemPrompt == null ? null : SystemPrompt.text;
                var settings = LocalConfiguration.Load(directory, promptOverride);
                var client = new GeminiClient(http, settings, Registry, view.ReadAsync);
                await client.InitializeSessionAsync(token); token.ThrowIfCancellationRequested();
                session = new ChatSession(client);
                speech = null;
                try { var speechSettings = LocalConfiguration.LoadSpeech(directory); if (speechSettings != null) speech = new ElevenLabsSpeechClient(http, speechSettings); }
                catch (Exception ex) { Report(ex, "unity.speech_configuration"); }
                SetStatus(view.Available ? "CADEN ready" : "CADEN ready; object manipulation unavailable");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (this != null && !token.IsCancellationRequested) { Report(ex, "unity.reload"); SetStatus("CADEN configuration/load failed"); } }
            finally
            {
                // Keep the successful model token alive until the next replacement/disable.
                if (!ReferenceEquals(modelLifetime, lifetime)) lifetime.Dispose();
            }
        }
        private string ResolveConfigurationDirectory()
        {
            if (!string.IsNullOrWhiteSpace(ConfigurationDirectory)) return Path.GetFullPath(ConfigurationDirectory);
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--caden-config-dir") return Path.GetFullPath(args[i + 1]);
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "CADEN"));
#else
            return Path.Combine(Application.persistentDataPath, "CADEN");
#endif
        }

        // Called by Unity's chat/STT UI on the main thread. This is the only path making paid calls.
        public async Task<string> SendAsync(string prompt)
        {
            if (session == null) throw new InvalidOperationException("CADEN is not ready: " + Status);
            if (turn != null) throw new InvalidOperationException("A CADEN turn is already running.");
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a message.");
            StopSpeech();
            var current = session;
            var pending = CancellationTokenSource.CreateLinkedTokenSource(modelLifetime.Token);
            turn = pending;
            var voice = speech == null ? null : new StreamingSpeechTurn(speech, PlayPcmAsync,
                ex => mainContext.Post(_ => { if (this != null) Report(ex, "unity.speech"); }, null), pending.Token,
                speech is IStreamingSpeechClient streaming ? (text, token) => SpeakStreamedAsync(streaming, text, token) : null);
            speaking = voice;
            SetStatus("CADEN is thinking");
            try
            {
                InputReceived.Invoke(prompt);
                string answer = await current.SendAsync(prompt, pending.Token, voice == null ? null : voice.Receive);
                pending.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(session, current)) throw new OperationCanceledException();
                voice?.Complete(answer); SetStatus("CADEN ready"); AnswerReceived.Invoke(answer); return answer;
            }
            catch (Exception ex)
            {
                voice?.Cancel();
                if (!(ex is OperationCanceledException) && this != null) Report(ex, "unity.send");
                throw;
            }
            finally
            {
                if (ReferenceEquals(turn, pending))
                {
                    turn = null;
                    if (this != null && ReferenceEquals(session, current)) SetStatus("CADEN ready");
                }
                pending.Dispose();
            }
        }
        public async void SendMessageToCaden(string prompt)
        {
            try { await SendAsync(prompt); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (this != null) ErrorReceived.Invoke(DiagnosticLog.Redact(ex.Message)); }
        }
        public void CancelTurn() { turn?.Cancel(); StopSpeech(); }
        /// <summary>RMS loudness (0..1) of the speech currently playing.</summary>
        public float SpeechLevel()
        {
            if (!IsSpeaking) return 0;
            audioSource.GetOutputData(levelSamples, 0);
            float sum = 0; foreach (float s in levelSamples) sum += s * s;
            return Mathf.Sqrt(sum / levelSamples.Length);
        }
        public void StopSpeech() { speaking?.Cancel(); if (audioSource != null) audioSource.Stop(); }
        private async Task PlayPcmAsync(byte[] pcm, CancellationToken token)
        {
            await audioGate.WaitAsync(token);
            AudioClip clip = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (this == null || audioSource == null) throw new OperationCanceledException();
                var samples = new float[pcm.Length / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;
                clip = AudioClip.Create("CADEN speech", samples.Length, 1, 24000, false); clip.SetData(samples, 0);
                audioSource.clip = clip; audioSource.Play();
                while (audioSource != null && audioSource.isPlaying) { token.ThrowIfCancellationRequested(); await Task.Delay(20, token); }
            }
            finally { if (audioSource != null) { audioSource.Stop(); audioSource.clip = null; } if (clip != null) Destroy(clip); audioGate.Release(); }
        }
        // One retry for a sentence that failed before any of it was heard; replaying after that would repeat words.
        private async Task SpeakStreamedAsync(IStreamingSpeechClient client, string text, CancellationToken token)
        {
            bool started = false;
            try { await StreamPcmAsync(client, text, token, () => started = true); }
            catch (Exception ex) when (!started && !token.IsCancellationRequested && StreamingSpeechTurn.IsTransient(ex))
            {
                Debug.LogWarning("[CADEN] Speech failed before playback (" + DiagnosticLog.Redact(ex.Message) + "); retrying once.");
                await StreamPcmAsync(client, text, token, null);
            }
        }
        private async Task StreamPcmAsync(IStreamingSpeechClient client, string text, CancellationToken token, Action started)
        {
            await audioGate.WaitAsync(token);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var fifo = new PcmStreamBuffer();
            AudioClip clip = null;
            Task download = null;
            // Written by the download thread; a stall watchdog ends the sentence instead of waiting out the full request timeout.
            int lastArrival = Environment.TickCount;
            void CheckStall()
            {
                if (!download.IsCompleted && Environment.TickCount - lastArrival > SpeechStallMilliseconds)
                    throw new TimeoutException("ELEVENLABS_STALLED: no speech audio received for " + SpeechStallMilliseconds / 1000 + " seconds.");
            }
            try
            {
                async Task Produce()
                {
                    await client.StreamAsync(text, async (bytes, t) =>
                    {
                        lastArrival = Environment.TickCount;
                        await fifo.AppendAsync(bytes, t).ConfigureAwait(false);
                        lastArrival = Environment.TickCount;
                    }, lifetime.Token).ConfigureAwait(false);
                    fifo.Complete();
                }
                download = Produce();
                // Start after 500ms (12,000 samples at 24kHz), or immediately for a completed shorter utterance.
                while (fifo.Count < 12000 && !download.IsCompleted) { CheckStall(); await Task.Delay(10, lifetime.Token); }
                if (download.IsCompleted) await download;
                token.ThrowIfCancellationRequested();
                if (this == null || audioSource == null) throw new OperationCanceledException();
                const int clipSamples = 1024;
                clip = AudioClip.Create("CADEN streaming speech", clipSamples, 1, 24000, true, fifo.Read);
                audioSource.clip = clip; audioSource.loop = true; audioSource.Play();
                started?.Invoke();
                while (!fifo.Drained)
                {
                    token.ThrowIfCancellationRequested();
                    if (download.IsCompleted) await download;
                    // Only a starved buffer counts as a stall; a full one is just backpressure.
                    if (fifo.Count == 0) CheckStall();
                    await Task.Delay(10, lifetime.Token);
                }
                await download;
                // The audio callback can consume ahead of the physical speaker. Drain Unity's DSP queue, plus the
                // device output latency Unity doesn't report (Quest/Android), or the last syllable is cut off.
                AudioSettings.GetDSPBufferSize(out int length, out int buffers);
                double end = AudioSettings.dspTime + (double)length * buffers / AudioSettings.outputSampleRate + (double)clipSamples / 24000 + SpeechTailPaddingSeconds;
                while (AudioSettings.dspTime < end) await Task.Delay(10, lifetime.Token);
            }
            finally
            {
                lifetime.Cancel();
                if (audioSource != null) { audioSource.Stop(); audioSource.loop = false; audioSource.clip = null; }
                if (clip != null) Destroy(clip);
                if (download != null) { try { await download; } catch { /* Original failure/cancellation is reported by the speech queue. */ } }
                audioGate.Release();
            }
        }
        private DiagnosticReceipt Report(Exception ex, string operation)
        { var receipt = DiagnosticLog.Report(ex, operation); Debug.LogError(receipt.UserMessage); ErrorReceived.Invoke(receipt.UserMessage); return receipt; }
        private void SetStatus(string value) { if (this == null) return; Status = value; StatusChanged.Invoke(value); }
        private void OnEnable() { if (runtime != null) { runtime.ModelChanged -= ModelChanged; runtime.ModelChanged += ModelChanged; ModelChanged(); } }
        private void OnDisable() { if (runtime != null) runtime.ModelChanged -= ModelChanged; Revoke(); }
        private void OnDestroy() { Revoke(); http.Dispose(); modelLifetime?.Dispose(); }
    }
}
