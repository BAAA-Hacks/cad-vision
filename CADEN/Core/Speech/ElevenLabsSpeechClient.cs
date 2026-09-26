using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Diagnostics;
using Newtonsoft.Json;

namespace Core.Speech
{
    public sealed class ElevenLabsSettings
    {
        internal string ApiKey { get; }
        public string VoiceId { get; }
        public string Model { get; }
        public int TimeoutSeconds { get; }
        public ElevenLabsSettings(string apiKey, string voiceId, string model = "eleven_flash_v2_5", int timeoutSeconds = 30)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("ELEVENLABS_API_KEY is required when speech is enabled.");
            DiagnosticLog.RegisterSecret(apiKey.Trim());
            if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("ELEVENLABS_VOICE_ID is required when speech is enabled.");
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("ELEVENLABS_MODEL is required.");
            if (timeoutSeconds < 1 || timeoutSeconds > 300) throw new ArgumentException("ELEVENLABS_TIMEOUT_SECONDS must be between 1 and 300.");
            ApiKey = apiKey.Trim(); VoiceId = voiceId.Trim(); Model = model.Trim(); TimeoutSeconds = timeoutSeconds;
        }
    }

    public interface ISpeechClient
    {
        Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellation = default);
    }

    // Returns mono signed 16-bit little-endian PCM at 24 kHz. Playback belongs to the host.
    public sealed class ElevenLabsSpeechClient : ISpeechClient
    {
        private readonly HttpClient http;
        private readonly ElevenLabsSettings settings;
        public ElevenLabsSpeechClient(HttpClient http, ElevenLabsSettings settings)
        { this.http = http; this.settings = settings; }

        public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Speech text is empty.");
            if (text.Length > 5000) throw new ArgumentException("Speech exceeds the 5,000-character desktop limit; the full answer remains in chat.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(settings.VoiceId) + "?output_format=pcm_24000");
                request.Headers.Add("xi-api-key", settings.ApiKey);
                request.Content = new StringContent(JsonConvert.SerializeObject(new { text, model_id = settings.Model }), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Never copy provider bodies: they can echo speech text or credentials.
                    string guidance = (int)response.StatusCode switch
                    {
                        401 => "Check ELEVENLABS_API_KEY.",
                        403 => "Check key permissions and voice/model access.",
                        404 => "Check ELEVENLABS_VOICE_ID and model availability.",
                        429 => "Quota or concurrency limit reached; retry speech later.",
                        _ => "Check ElevenLabs configuration/service status; the chat answer is retained."
                    };
                    throw new HttpRequestException("ELEVENLABS_HTTP_" + (int)response.StatusCode + ": " + guidance);
                }
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var audio = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (audio.Length + count > 24000 * 2 * 300) throw new InvalidDataException("ELEVENLABS_AUDIO_TOO_LARGE: audio exceeds five minutes.");
                    audio.Write(buffer, 0, count);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (audio.Length == 0 || audio.Length % 2 != 0 || response.Content.Headers.ContentType?.MediaType == "application/json")
                    throw new InvalidDataException("ELEVENLABS_INVALID_AUDIO: expected nonempty 16-bit PCM audio.");
                return audio.ToArray();
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException("ELEVENLABS_TIMEOUT: speech generation exceeded " + settings.TimeoutSeconds + " seconds."); }
        }
    }
}
