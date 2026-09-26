using System.Net;
using Core;
using Desktop.Configuration;
using Newtonsoft.Json.Linq;
using Core.Tools.Query;
using Core.Primitives.Operations.Project;

if (args.Contains("--assess-orchestration"))
{
    int start = Array.IndexOf(args, "--assess-orchestration") + 1;
    if (args.Length < start + 3) throw new ArgumentException("Usage: --assess-orchestration <results.json> <cases.json> <assessment.json>");
    var report = JObject.Parse(File.ReadAllText(args[start]));
    var cases = JArray.Parse(File.ReadAllText(args[start + 1]));
    var assessments = new JArray();
    foreach (var turn in report["turns"]!)
    {
        var testCase = cases[(int)turn["number"]! - 1];
        if ((string?)turn["prompt"] != (string?)testCase["prompt"]) throw new ArgumentException("Results do not match case prompts.");
        var assessment = OrchestrationAssessment.Assess(turn, testCase);
        assessment["number"] = turn["number"]; assessment["caseId"] = testCase["id"]; assessments.Add(assessment);
    }
    File.WriteAllText(args[start + 2], assessments.ToString());
    Console.WriteLine($"Structural checks: {assessments.Count(a => (bool)a["passed"]!)}/{cases.Count}. Semantic review still required.");
    return;
}

if (args.Contains("--orchestration"))
{
    if (!args.Contains("--live")) throw new ArgumentException("Live orchestration requires explicit --live authorization.");
    int start = Array.IndexOf(args, "--orchestration") + 1;
    if (args.Length < start + 4) throw new ArgumentException("Usage: --live --orchestration <new-run-directory> <metadata.json> <config-directory> <cases.json>");
    await LiveOrchestrationRunner.RunAsync(args.Skip(start).Take(4).ToArray());
    return;
}

static void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}

if (args.Contains("--metadata"))
{
    try
    {
        int index = Array.IndexOf(args, "--metadata");
        if (index + 1 >= args.Length) throw new ArgumentException("--metadata requires a JSON path.");
        await Schema21Checks.AuditAsync(args[index + 1]);
    }
    catch (Exception ex) { Console.Error.WriteLine("METADATA_AUDIT_FAILED: " + ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--live-query"))
{
    try
    {
        string directory = LocalConfiguration.FindDirectory();
        var loaded = LoadProject.Load(File.ReadAllText(Path.Combine(directory, "data", "metadata.json")));
        if (!loaded.Success) throw new ArgumentException("Canonical metadata could not load.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var gemini = new GeminiClient(http, LocalConfiguration.Load(directory), SemanticQueryTools.Create(loaded.Snapshot, ProjectAssociationFile.LoadOrCreate(directory)));
        var chat = new ChatSession(gemini);
        string answer = await chat.SendAsync("Use get_model_summary, then get_object_details with its snapshotId for the root's mass and material. Report the object count and whether root mass/material data is available, with IDs. Keep it brief.");
        Require(gemini.LastToolCallCount >= 2, "Expected at least two live query calls.");
        Console.WriteLine("PASS: live query loop (" + gemini.LastToolCallCount + " calls).\n" + answer);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex is ChatException || ex is ArgumentException ? ex.Message : "Live query check failed (" + ex.GetType().Name + ").");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--live"))
{
    try
    {
        var settings = LocalConfiguration.Load(LocalConfiguration.FindDirectory());
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var chat = new ChatSession(new GeminiClient(http, settings));
        string first = await chat.SendAsync("Remember the test word copper. Reply with one short sentence.");
        string second = await chat.SendAsync("What test word did I just give you? Reply with only that word.");
        Require(second.Contains("copper", StringComparison.OrdinalIgnoreCase), "Live response did not recall the test word.");
        Console.WriteLine("PASS: live Gemini response and follow-up memory (" + settings.Model + ").");
        chat.Clear();
        Require(chat.Messages.Count == 0, "Reset failed.");
        Console.WriteLine("PASS: reset.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex is ChatException || ex is ArgumentException ? ex.Message : "Live check failed (" + ex.GetType().Name + ").");
        Environment.ExitCode = 1;
    }
    return;
}

Core.Diagnostics.DiagnosticLog.Configure(_ => { }, "offline check in-memory diagnostics");
var handler = new FakeHandler();
using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
var config = new GeminiSettings("private/key+value", "test-model", "Hidden CADEN instructions");
var session = new ChatSession(new GeminiClient(client, config));
await session.SendAsync("Remember copper");
await session.SendAsync("Which word?");
var sent = JObject.Parse(handler.LastBody);
Require((string?)sent["systemInstruction"]?["parts"]?[0]?["text"] == "Hidden CADEN instructions", "System instructions missing.");
Require(((JArray)sent["contents"]!).Count == 3, "Follow-up history missing.");
Require((string?)sent["contents"]?[1]?["role"] == "model", "Assistant role incorrect.");
Require(session.Messages.Count == 4 && session.Messages.All(m => !m.Text.Contains("Hidden CADEN")), "System instructions leaked into transcript.");
Require(handler.LastKey == "private/key+value" && !handler.LastBody.Contains("private/key"), "Key must be a header, not body data.");
Console.WriteLine("PASS: REST payload, hidden prompt, history, key header.");

handler.Code = HttpStatusCode.BadRequest;
handler.Body = new JObject { ["error"] = new JObject { ["status"] = "INVALID_ARGUMENT", ["message"] = "API key not valid: private/key+value private%2Fkey%2Bvalue AIzaOtherSecret123" } }.ToString();
try { await session.SendAsync("Failed turn"); throw new Exception("Expected API failure."); }
catch (ChatException ex)
{
    Require(ex.Message.Contains("HTTP 400 / INVALID_ARGUMENT"), "Missing error code/status.");
    Require(!ex.Message.Contains("private") && !ex.Message.Contains("AIza"), "Secret leaked.");
}
Require(session.Messages.Count == 4, "Failed turn entered history.");
handler.Code = HttpStatusCode.OK; handler.Body = FakeHandler.Success;
await session.SendAsync("Retry turn");
Require(session.Messages.Count == 6, "Retry history failed.");
session.Clear(); Require(session.Messages.Count == 0, "New chat failed.");
Console.WriteLine("PASS: detailed error redaction, failed-turn rollback, retry, reset.");

handler.Body = "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}";
try { await session.SendAsync("Blocked"); throw new Exception("Expected empty response failure."); }
catch (ChatException ex) { Require(ex.Message.Contains("SAFETY"), "Missing block reason."); }
Require(session.Messages.Count == 0, "Blocked turn entered history.");
handler.Body = "not JSON";
try { await session.SendAsync("Malformed"); throw new Exception("Expected malformed response failure."); }
catch (ChatException ex) { Require(ex.Message.Contains("non-JSON"), "Missing malformed response explanation."); }
handler.Body = "{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"Internal thought\"},{\"text\":\"Visible answer\"}]},\"finishReason\":\"MAX_TOKENS\"}]}";
string limited = await session.SendAsync("Partial");
Require(!limited.Contains("Internal thought") && limited.Contains("output limit"), "Thought filtering/truncation failed.");
Console.WriteLine("PASS: missing text, malformed response, thought filtering, output limit.");

using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try { await session.SendAsync("Cancelled", cancellation.Token); throw new Exception("Expected cancellation."); }
catch (OperationCanceledException) { }
Require(session.Messages.Count == 2, "Cancelled turn entered history.");
Console.WriteLine("PASS: cancellation leaves history intact.");

// Temporary fixtures contain dummy credentials only. No real .env is read by offline checks.
string temp = Path.Combine(Path.GetTempPath(), "caden-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp, "prompts"));
string[] names = { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GEMINI_MODEL", "GEMINI_TIMEOUT_SECONDS", "GEMINI_MAX_OUTPUT_TOKENS", "GEMINI_MAX_TOOL_ROUNDS", "GEMINI_MAX_TOOL_CALLS" };
var old = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
try
{
    foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
    File.WriteAllText(Path.Combine(temp, "prompts", "system.md"), "Hidden prompt");
    File.WriteAllText(Path.Combine(temp, ".env"), "export GEMINI_API_KEY='dummy-key' # comment\nGEMINI_MODEL=test-model\nGEMINI_TIMEOUT_SECONDS=22 # timeout\n");
    var loaded = LocalConfiguration.Load(temp);
    Require(loaded.TimeoutSeconds == 22 && loaded.Model == "test-model", "Local config parsing failed.");
    Require(loaded.MaxToolRounds == 12 && loaded.MaxToolCalls == 48, "Default tool budgets missing.");
    File.AppendAllText(Path.Combine(temp, ".env"), "GEMINI_MAX_TOOL_ROUNDS=20\nGEMINI_MAX_TOOL_CALLS=80\n");
    Require(LocalConfiguration.Load(temp).MaxToolRounds == 20 && LocalConfiguration.Load(temp).MaxToolCalls == 80, "Tool budget config ignored.");
    Environment.SetEnvironmentVariable("GEMINI_MAX_TOOL_CALLS", "96");
    Require(LocalConfiguration.Load(temp).MaxToolCalls == 96, "Tool budget environment precedence failed.");
    Environment.SetEnvironmentVariable("GEMINI_MAX_TOOL_CALLS", "0");
    try { LocalConfiguration.Load(temp); throw new Exception("Invalid tool budget accepted."); } catch (ArgumentException) { }
    Environment.SetEnvironmentVariable("GEMINI_MAX_TOOL_CALLS", null);
    Environment.SetEnvironmentVariable("GEMINI_MODEL", "environment-model");
    Require(LocalConfiguration.Load(temp).Model == "environment-model", "Environment precedence failed.");
    File.WriteAllText(Path.Combine(temp, ".env"), "GEMINI_API_KEY='dummy-key\n");
    try { LocalConfiguration.Load(temp); throw new Exception("Expected bad quote failure."); }
    catch (ArgumentException) { }
    Console.WriteLine("PASS: local config quotes/comments, environment precedence, malformed config.");
}
finally
{
    foreach (var pair in old) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    File.Delete(Path.Combine(temp, ".env")); File.Delete(Path.Combine(temp, "prompts", "system.md"));
    Directory.Delete(Path.Combine(temp, "prompts")); Directory.Delete(temp);
}

await QueryChecks.RunAsync();
await SemanticQueryChecks.RunAsync();
await ToolContractChecks.RunAsync();
await IssueAccessChecks.RunAsync();
await MemoryToolChecks.RunAsync();
await CapabilityChecks.RunAsync();
await OrchestrationChecks.RunAsync();
await ContextCacheChecks.RunAsync();
await ActiveScopeChecks.RunAsync();
await ConnectionQueryChecks.RunAsync();
await TokenUsageChecks.RunAsync();
await MechanicalQueryChecks.RunAsync();
await Schema21Checks.RunAsync();
await DiagnosticChecks.RunAsync();
MechanicalGraphChecks.Run();
ProjectLoaderChecks.Run();
await IssueStoreChecks.RunAsync();
await IssueEngineChecks.RunAsync();
MemoryChecks.Run();

sealed class FakeHandler : HttpMessageHandler
{
    public const string Success = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Copper.\"}]},\"finishReason\":\"STOP\"}]}";
    public string Body = Success;
    public HttpStatusCode Code = HttpStatusCode.OK;
    public string LastBody = "";
    public string LastKey = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        LastKey = request.Headers.GetValues("x-goog-api-key").Single();
        return new HttpResponseMessage(Code) { Content = new StringContent(Body) };
    }
}
