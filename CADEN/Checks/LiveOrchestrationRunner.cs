using Core;
using Core.Tools;
using Core.Tools.Query;
using Core.Tools.Issues;
using Core.Tools.Memory;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.Operations.Project;
using Desktop.Configuration;
using Desktop.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Security.Cryptography;


internal static class LiveOrchestrationRunner { internal static async Task RunAsync(string[] args) {
var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var metadataPath = args[1]; var configDir = args[2];
if (File.Exists(Path.Combine(root, "results.json"))) throw new Exception("Use a new isolated run directory; do not overwrite a previous test.");
var settings = LocalConfiguration.Load(configDir);
var snapshot = LoadProject.Load(File.ReadAllText(metadataPath)).Snapshot ?? throw new Exception("Metadata did not load.");
var association = new ProjectAssociation("live-stress-" + Guid.NewGuid().ToString("N"));
ToolRegistry? registry = null;
using var capture = new OrchestrationCapture(); using var http = new HttpClient(capture) { Timeout = Timeout.InfiniteTimeSpan };
async Task<ChatSession> Open()
{
    var issues = await IssueAccess.OpenAsync(snapshot, association, new ProjectMemoryFile(Path.Combine(root, "issues.caden.json")));
    var memory = MemoryAccess.Open(snapshot, association, new ProjectMemoryFile(Path.Combine(root, "memory.caden.json")));
    registry = SemanticQueryTools.Create(snapshot, association, issues: issues, memory: memory);
    var client = new GeminiClient(http, settings, registry); await client.InitializeSessionAsync(); return new ChatSession(client);
}
var session = await Open();
var cases = JArray.Parse(File.ReadAllText(args[3])); string[] prompts = cases.Select(c => (string)c["prompt"]!).ToArray();
File.WriteAllText(Path.Combine(root, "cases.json"), cases.ToString(Formatting.Indented));
var turns = new JArray(); var report = new JObject { ["model"] = settings.Model, ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
    ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId, ["metadataSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metadataPath))),
    ["promptSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(configDir, "prompts", "system.md")))), ["turns"] = turns };
for (int index = 0; index < prompts.Length; index++)
{
    if (index > 0 && (bool?)cases[index]["newSession"] == true) { session = await Open(); Console.WriteLine("REOPENED: new chat plus issue/memory stores from disk."); }
    capture.Exchanges = new JArray(); var timer = Stopwatch.StartNew();
    var turn = new JObject { ["number"] = index + 1, ["caseId"] = cases[index]["id"], ["prompt"] = prompts[index], ["newSession"] = index == 0 || (bool?)cases[index]["newSession"] == true };
    try { turn["answer"] = await session.SendAsync(prompts[index]); turn["completed"] = true; }
    catch (Exception ex)
    {
        turn["completed"] = false; turn["error"] = ex.Message;
        bool transport = false; for (Exception? e = ex; e != null; e = e.InnerException) if (e is HttpRequestException) transport = true;
        turn["transportFailure"] = transport;
    }
    turn["elapsedSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2); turn["exchanges"] = capture.Exchanges.DeepClone();
    turn["structuralAssessment"] = OrchestrationAssessment.Assess(turn, cases[index]);
    // Local state readback, no Gemini calls; captures independent durable state evidence.
    var readArgs = new JObject { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId };
    if (index >= 20)
    {
        turn["issueState"] = await registry!.ExecuteAsync("list_issues", readArgs);
        var memoryArgs = (JObject)readArgs.DeepClone(); memoryArgs["objectIds"] = new JArray(snapshot.ComponentsById.Keys); memoryArgs["includeRetired"] = true;
        turn["memoryState"] = await registry.ExecuteAsync("get_project_memory", memoryArgs);
    }
    turns.Add(turn); File.WriteAllText(Path.Combine(root, "results.json"), report.ToString(Formatting.Indented));
    Console.WriteLine($"TURN {index + 1}: completed={turn["completed"]}; requests={capture.Exchanges.Count}; seconds={turn["elapsedSeconds"]}");
    if ((bool?)turn["transportFailure"] == true) { Console.WriteLine("STOPPED after transport failure; no automatic retries."); break; }
}
// Reopen once more to prove final cleanup is durable, not just an in-memory read.
if (turns.Count == prompts.Length)
{
    session = await Open();
    var a = new JObject { ["projectId"] = association.ProjectId, ["snapshotId"] = snapshot.SnapshotId };
    report["finalIssuesAfterReopen"] = await registry!.ExecuteAsync("list_issues", a);
    a["objectIds"] = new JArray(snapshot.ComponentsById.Keys); a["includeRetired"] = true;
    report["finalMemoryAfterReopen"] = await registry.ExecuteAsync("get_project_memory", a);
}
report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"); File.WriteAllText(Path.Combine(root, "results.json"), report.ToString(Formatting.Indented));
Console.WriteLine("RESULTS=" + Path.Combine(root, "results.json"));


} }
internal static class OrchestrationAssessment
{
    internal static JObject Assess(JToken turn, JToken testCase)
    {
        var actions = new[] { "write_project_memory", "set_issue_disposition", "revalidate_issue", "revalidate_object_issues" };
        var allowed = testCase["allowedMutationTools"]?.Values<string>().ToArray() ?? Array.Empty<string>();
        var forbidden = testCase["forbiddenTools"]?.Values<string>().ToArray() ?? Array.Empty<string>();
        var calls = turn["exchanges"]!.SelectMany(e => e["calls"] ?? new JArray()).Select(c => (string)c["name"]!).ToArray();
        var unexpected = calls.Where(c => (actions.Contains(c) && !allowed.Contains(c)) || forbidden.Contains(c)).Distinct().ToArray();
        var missing = (testCase["requiredTools"]?.Values<string>() ?? Enumerable.Empty<string>()).Where(t => !calls.Contains(t)).Select(t => t!).ToArray();
        var errors = turn["exchanges"]!.SelectMany(e => e["sentToolResults"] ?? new JArray())
            .Where(r => (bool?)r["response"]?["success"] == false).SelectMany(r => r["response"]?["errors"] ?? new JArray()).Select(e => (string?)e["code"] ?? "UNSPECIFIED_ERROR").ToArray();
        return new JObject { ["passed"] = (bool?)turn["completed"] == true && unexpected.Length == 0 && missing.Length == 0 && errors.Length == 0,
            ["unexpectedTools"] = new JArray(unexpected), ["missingRequiredTools"] = new JArray(missing), ["toolErrors"] = new JArray(errors),
            ["semanticReviewRequired"] = true };
    }
}
sealed class OrchestrationCapture : DelegatingHandler
{
    public JArray Exchanges = new();
    public OrchestrationCapture() : base(new HttpClientHandler()) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var payload = JObject.Parse(await request.Content!.ReadAsStringAsync(token));
        var entry = new JObject { ["sentToolResults"] = new JArray(payload["contents"]?.Last?["parts"]?.OfType<JObject>().Where(p => p["functionResponse"] != null).Select(p => p["functionResponse"]!.DeepClone()) ?? Enumerable.Empty<JToken>()) };
        Exchanges.Add(entry); // Never log headers, keys, URLs, or model thought text.
        var response = await base.SendAsync(request, token);
        entry["httpStatus"] = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(token);
        try
        {
            var body = JObject.Parse(text); entry["usage"] = body["usageMetadata"]?.DeepClone();
            entry["calls"] = new JArray(body["candidates"]?[0]?["content"]?["parts"]?.OfType<JObject>().Where(p => p["functionCall"] != null).Select(p => p["functionCall"]!.DeepClone()) ?? Enumerable.Empty<JToken>());
            entry["finishReason"] = body["candidates"]?[0]?["finishReason"]?.DeepClone();
        }
        catch (JsonException) { entry["nonJsonResponse"] = true; }
        return response;
    }
}

