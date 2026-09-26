using System.Net;
using Core.Speech;
using Core.Diagnostics;
using Newtonsoft.Json.Linq;

internal static class SpeechChecks
{
    public static async Task RunAsync()
    {
        const string secret = "test-speech-secret";
        var settings = new ElevenLabsSettings(secret, "voice", timeoutSeconds: 1);
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Require(request.RequestUri!.ToString().EndsWith("/voice?output_format=pcm_24000"), "Speech endpoint/format");
            Require(request.Headers.GetValues("xi-api-key").Single() == secret, "Speech authentication");
            var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
            Require((string?)payload["text"] == "The mate is broken." && (string?)payload["model_id"] == "eleven_flash_v2_5", "Speech payload");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0, 0, 1, 0 }) };
        }));
        var client = new ElevenLabsSpeechClient(http, settings);
        Require((await client.SynthesizeAsync("The mate is broken.")).Length == 4 && calls == 1, "Speech audio returned");
        Require(!DiagnosticLog.Redact(secret).Contains(secret), "Speech key redaction");
        try { await client.SynthesizeAsync(new string('a', 5001)); throw new Exception("Expected text limit"); }
        catch (ArgumentException) { Require(calls == 1, "Oversized speech must not call API"); }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            int attempts = 0;
            using var failing = new HttpClient(new Handler((_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("sensitive provider body") }); }));
            try { await new ElevenLabsSpeechClient(failing, settings).SynthesizeAsync("test"); throw new Exception("Expected HTTP failure"); }
            catch (HttpRequestException ex) { Require(ex.Message.Contains("ELEVENLABS_HTTP_" + (int)status) && !ex.Message.Contains("sensitive") && attempts == 1, "Traceable failure without automatic billing retry"); }
        }
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1 } })
        {
            using var invalid = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            try { await new ElevenLabsSpeechClient(invalid, settings).SynthesizeAsync("test"); throw new Exception("Expected invalid audio"); }
            catch (InvalidDataException) { }
        }
        using var slow = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); }));
        var slowClient = new ElevenLabsSpeechClient(slow, settings);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await slowClient.SynthesizeAsync("test", cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        try { await slowClient.SynthesizeAsync("test"); throw new Exception("Expected timeout"); }
        catch (TimeoutException ex) { Require(ex.Message.Contains("ELEVENLABS_TIMEOUT"), "Timeout diagnostic"); }
        Console.WriteLine("Speech checks passed (offline; no ElevenLabs calls).");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
