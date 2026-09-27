#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Core.Speech
{
    public sealed class TranscriptionSettings
    {
        internal string ApiKey { get; }
        public string Model { get; }
        public TranscriptionSettings(string apiKey, string model = "scribe_v2")
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("STT_KEY_MISSING: Set ELEVENLABS_API_KEY in this device's CADEN/.env.");
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("STT_MODEL_MISSING: Set ELEVENLABS_STT_MODEL.");
            ApiKey = apiKey.Trim(); Model = model.Trim(); Core.Diagnostics.DiagnosticLog.RegisterSecret(ApiKey);
        }
    }

    public sealed class ElevenLabsTranscriptionClient
    {
        private readonly HttpClient http;
        private readonly TranscriptionSettings settings;
        public ElevenLabsTranscriptionClient(HttpClient http, TranscriptionSettings settings)
        { this.http = http; this.settings = settings; }

        public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellation)
        {
            if (wav == null || wav.Length <= 44 || wav.Length > 12000000) throw new ArgumentException("STT_INVALID_AUDIO: Empty or oversized recording.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
                request.Headers.Add("xi-api-key", settings.ApiKey);
                var form = new MultipartFormDataContent(); request.Content = form;
                form.Add(new StringContent(settings.Model), "model_id");
                form.Add(new StringContent("false"), "tag_audio_events");
                form.Add(new StringContent("false"), "diarize");
                var audio = new ByteArrayContent(wav); audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(audio, "file", "command.wav");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string hint = (int)response.StatusCode == 401 ? "Check ELEVENLABS_API_KEY on this device." :
                        (int)response.StatusCode == 403 ? "Check the key's speech-to-text permission." :
                        (int)response.StatusCode == 429 ? "Quota or concurrency limit reached; try again later." : "Check the STT model and ElevenLabs service.";
                    throw new HttpRequestException("STT_HTTP_" + (int)response.StatusCode + ": " + hint);
                }
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var bytes = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > 1048576) throw new InvalidDataException("STT_RESPONSE_TOO_LARGE");
                    buffer.Write(bytes, 0, read);
                }
                timeout.Token.ThrowIfCancellationRequested();
                JObject payload;
                try { payload = JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray())); }
                catch (Newtonsoft.Json.JsonException) { throw new InvalidDataException("STT_INVALID_RESPONSE: Expected JSON transcript."); }
                if (payload["text"]?.Type != JTokenType.String) throw new InvalidDataException("STT_INVALID_RESPONSE: Missing text field.");
                string text = ((string)payload["text"]!).Trim();
                if (text.Length > 8000) throw new InvalidDataException("STT_TRANSCRIPT_TOO_LONG");
                return text;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException("STT_TIMEOUT: Transcription exceeded 60 seconds."); }
        }
    }

    public static class SpeechWave
    {
        // Unity float samples are interleaved. Preserve the actual capture rate/channels.
        public static byte[] Encode(float[] samples, int frames, int channels, int sampleRate)
        {
            if (frames <= 0 || channels < 1 || channels > 8 || sampleRate < 8000 || sampleRate > 192000 ||
                frames > sampleRate * 31 || samples == null || (long)frames * channels > samples.Length)
                throw new ArgumentException("STT_INVALID_CAPTURE: Invalid recording dimensions.");
            int count = checked(frames * channels);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
            writer.Write((short)channels); writer.Write(sampleRate); writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (int i = 0; i < count; i++)
            {
                float value = samples[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("STT_INVALID_CAPTURE: Nonfinite sample.");
                writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767));
            }
            return stream.ToArray();
        }
    }
}
