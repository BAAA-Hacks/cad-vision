using System.Net;
using Core.Speech;

internal static class TranscriptionChecks
{
    public static async Task RunAsync()
    {
        static void Require(bool ok, string text) { if (!ok) throw new Exception(text); }
        byte[] wav = SpeechWave.Encode(new float[] { -1, 0, 1, 0.5f }, 4, 1, 16000);
        Require(System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && wav.Length == 52, "WAV size/header");
        Require(BitConverter.ToInt32(wav, 24) == 16000 && BitConverter.ToInt32(wav, 40) == 8, "WAV rate/data size");
        Require(BitConverter.ToInt16(wav, 44) == -32767 && BitConverter.ToInt16(wav, 48) == 32767, "PCM conversion");
        try { SpeechWave.Encode(new[] { float.NaN }, 1, 1, 16000); throw new Exception("Accepted NaN"); }
        catch (ArgumentException) { }
        int calls = 0;
        using var http = new HttpClient(new Wire(async (request, token) =>
        {
            calls++;
            Require(request.RequestUri!.AbsolutePath == "/v1/speech-to-text", "STT endpoint");
            Require(request.Headers.GetValues("xi-api-key").Single() == "fake-test-key", "STT auth");
            var parts = ((MultipartFormDataContent)request.Content!).ToArray();
            Require(await parts[0].ReadAsStringAsync(token) == "scribe_v2", "STT model");
            Require((await parts.Last().ReadAsByteArrayAsync(token)).SequenceEqual(wav), "Audio sent unchanged");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"  Isolate the base.  \"}") };
        }));
        var settings = new TranscriptionSettings("fake-test-key");
        Require(await new ElevenLabsTranscriptionClient(http, settings).TranscribeAsync(wav, default) == "Isolate the base.", "Transcript");
        Require(calls == 1, "Single STT request");
        foreach (string body in new[] { "not JSON", "{}", "{\"text\":42}" })
        {
            using var invalid = new HttpClient(new Wire((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            try { await new ElevenLabsTranscriptionClient(invalid, settings).TranscribeAsync(wav, default); throw new Exception("Accepted malformed transcript"); }
            catch (InvalidDataException ex) { Require(ex.Message.StartsWith("STT_INVALID_RESPONSE"), "Malformed response code"); }
        }
        using var empty = new HttpClient(new Wire((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\" \"}") })));
        Require(await new ElevenLabsTranscriptionClient(empty, settings).TranscribeAsync(wav, default) == "", "Empty transcript stays empty");
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            using var bad = new HttpClient(new Wire((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("private provider body") })));
            try { await new ElevenLabsTranscriptionClient(bad, settings).TranscribeAsync(wav, default); throw new Exception("Accepted HTTP failure"); }
            catch (HttpRequestException ex) { Require(ex.Message.Contains("STT_HTTP_" + (int)code) && !ex.Message.Contains("private"), "Safe specific error"); }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new ElevenLabsTranscriptionClient(http, settings).TranscribeAsync(wav, cancelled.Token); throw new Exception("Accepted cancellation"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Transcription checks passed (mock HTTP; no paid calls).");
    }
    private sealed class Wire(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return respond(request, cancellationToken); }
    }
}
