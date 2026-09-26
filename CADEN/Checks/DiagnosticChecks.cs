using Core;
using Core.Diagnostics;
using Core.Tools;
using Desktop;
using Newtonsoft.Json.Linq;

internal static class DiagnosticChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class BrokenTool : ICadenTool
    {
        public string Name => "broken";
        public JObject Declaration => JObject.Parse("{\"parameters\":{\"type\":\"object\",\"properties\":{}}}");
        public JObject Execute(JObject args) => throw new InvalidOperationException("outer diagnostic-secret/123", new ArgumentException("inner diagnostic-secret%2F123"));
    }
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "caden-diagnostics-" + Guid.NewGuid().ToString("N"));
        var writer = new FileDiagnostics(directory);
        DiagnosticLog.RegisterSecret("diagnostic-secret/123");
        Require(!DiagnosticLog.Redact("{\"password\":\"unregistered-value\"} Bearer abc-token").Contains("unregistered-value") && !DiagnosticLog.Redact("Bearer abc-token").Contains("abc-token"), "Structured credential/header redaction failed.");
        DiagnosticLog.Configure(writer.Write, directory);
        DiagnosticReceipt? shown = null;
        void OnReported(DiagnosticReceipt receipt) => shown = receipt;
        DiagnosticLog.Reported += OnReported;
        try
        {
            var result = await new ToolRegistry(new[] { new BrokenTool() }).ExecuteAsync("broken", new JObject());
            string id = (string)result["error"]!["correlationId"]!;
            Require(id.Length == 32 && shown?.Entry.CorrelationId == id && shown.UserMessage.Contains(directory), "Tool failure not linked to visible diagnostic.");
            string file = Directory.GetFiles(directory).Single(); string logged = File.ReadAllText(file);
            Require(logged.Contains(id) && logged.Contains("BrokenTool.Execute") && logged.Contains("System.ArgumentException") && logged.Contains("System.InvalidOperationException"), "Stack or inner exception missing.");
            Require(!logged.Contains("diagnostic-secret") && !shown!.UserMessage.Contains("diagnostic-secret") && logged.Contains("[REDACTED]"), "Credentials leaked to log or UI.");
            Require(!result.ToString().Contains("BrokenTool.Execute") && !result.ToString().Contains(directory), "Developer stack/path exposed to model.");
            Exception original;
            try { throw new IOException("write test"); } catch (Exception ex) { original = ex; }
            var scoped = DiagnosticLog.Report(original, "checker.example", "project", "snapshot", "part");
            Require(scoped.Entry.ProjectId == "project" && scoped.Entry.SnapshotId == "snapshot" && scoped.Entry.SubjectId == "part", "Scope missing from diagnostic.");
            var stderr = Console.Error; using var fallback = new StringWriter(); Console.SetError(fallback);
            try
            {
                DiagnosticLog.Configure(_ => throw new UnauthorizedAccessException("disk denied"), "unwritable test log");
                var failedLog = DiagnosticLog.Report(original, "test.sink_failure");
                Require(failedLog.LogWriteFailed && failedLog.UserMessage.Contains("WARNING") && fallback.ToString().Contains(failedLog.Entry.CorrelationId) && DiagnosticLog.GetRecent().Any(e => e.CorrelationId == failedLog.Entry.CorrelationId), "Log failure silently swallowed.");
            }
            finally { Console.SetError(stderr); }
            DiagnosticLog.Configure(writer.Write, directory);
            using var handler = new FakeHandler { Body = "not json" }; using var http = new HttpClient(handler);
            var gemini = new GeminiClient(http, new GeminiSettings("diagnostic-secret/123", "test-model", "test"));
            try { await new ChatSession(gemini).SendAsync("test"); throw new Exception("Expected Gemini failure."); }
            catch (ChatException ex)
            {
                Require(ex.DiagnosticId != null && ex.InnerException != null && DiagnosticLog.GetRecent().Any(e => e.CorrelationId == ex.DiagnosticId && e.ExceptionDetails.Contains("JsonReaderException")), "Gemini parser failure lost original exception/diagnostic ID.");
            }
            int before = DiagnosticLog.GetRecent().Count;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await new ToolRegistry(new[] { new BrokenTool() }).ExecuteAsync("broken", new JObject(), cancelled.Token); } catch (OperationCanceledException) { }
            Require(DiagnosticLog.GetRecent().Count == before, "User cancellation logged as a crash.");
            foreach (var line in File.ReadAllLines(file)) Require(JObject.Parse(line)["CorrelationId"] != null, "Invalid JSONL log.");
        }
        finally
        {
            DiagnosticLog.Reported -= OnReported;
            DiagnosticLog.Configure(_ => { }, "offline check in-memory diagnostics");
            if (Directory.Exists(directory)) { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
        Console.WriteLine("PASS: correlated tool/Gemini diagnostics, stack/inner exceptions, file logs, UI notifications, credential redaction, scope, sink failure fallback and cancellation.");
    }
}
