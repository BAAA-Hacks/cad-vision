using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Diagnostics;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Issues.Checkers;
using Core.Primitives.Operations.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Issues
{
    // Host-owned, exclusive issue session. Reads never run checkers. Evidence remains read-only.
    public sealed class IssueAccess
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly IMemoryPersistence storage;
        private readonly IssueEngine engine;
        private IssueStore store;
        private JObject journal;
        private string? committed;
        public ProjectSnapshot Snapshot { get; }
        public ProjectAssociation Association { get; }
        public int InitialFindingCount { get; private set; }
        public string InitialScanStatus { get; private set; } = "NotEvaluated";
        public IReadOnlyList<string> CheckerIds => engine.CheckerIds;
        private IssueAccess(ProjectSnapshot snapshot, ProjectAssociation association, IMemoryPersistence storage, IssueEngine engine)
        { Snapshot = snapshot; Association = association; this.storage = storage; this.engine = engine; store = new IssueStore(snapshot); journal = new JObject(); }
        public static async Task<IssueAccess> OpenAsync(ProjectSnapshot snapshot, ProjectAssociation association, IMemoryPersistence storage, CancellationToken token = default, IssueEngine? engine = null)
        {
            if (snapshot == null || association == null || storage == null) throw new ArgumentNullException("Issue access requires snapshot, association and receipt storage.");
            if (association.TrustedSourceProjectId != null && association.TrustedSourceProjectId != snapshot.ProjectId) throw new ArgumentException("Source project does not match host association.");
            var access = new IssueAccess(snapshot, association, storage, engine ?? InitialIssueCheckers.CreateEngine(snapshot));
            token.ThrowIfCancellationRequested(); access.committed = storage.Read();
            access.journal = access.committed == null ? new JObject { ["schemaVersion"] = "1", ["projectId"] = association.ProjectId, ["revision"] = 0L, ["operations"] = new JArray() } : ParseJournal(access.committed, association.ProjectId);
            var scan = await access.engine.ScanAsync(access.store, cancellationToken: token).ConfigureAwait(false);
            if (!scan.Success) throw new InvalidOperationException("Initial issue scan failed: " + scan.ErrorCode + ": " + scan.Message);
            access.RestoreDispositions();
            var next = (JObject)access.journal.DeepClone(); long revision = checked((long)next["revision"]! + 1); next["revision"] = revision;
            token.ThrowIfCancellationRequested(); access.Commit(next);
            access.store.SetAnalysisRevision(revision); access.InitialFindingCount = access.store.GetPresentation().Count(p => p.IsPresented); access.InitialScanStatus = scan.Value!.Status.ToString();
            return access;
        }
        private static JObject ParseJournal(string text, string project)
        {
            if (Encoding.UTF8.GetByteCount(text) > 10000000) throw new InvalidDataException("Issue receipt journal exceeds 10 MB.");
            using var input = new StringReader(text); using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var doc = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || doc.Count != 4 || (string?)doc["schemaVersion"] != "1" || (string?)doc["projectId"] != project || doc["revision"]?.Type != JTokenType.Integer || (long)doc["revision"]! < 0 || !(doc["operations"] is JArray ops) || ops.Count > 10000)
                throw new InvalidDataException("Invalid, unsupported or wrong-project issue receipt journal. It was not replaced.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in ops)
            {
                if (!(row is JObject op) || op.Count != 4 || op["operationId"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)op["operationId"]) || !ids.Add((string)op["operationId"]!)
                    || !(op["request"] is JObject request) || !(op["result"] is JObject result) || op["fingerprint"]?.Type != JTokenType.String || (string?)op["fingerprint"] != IssueValidation.Canonical(request)
                    || (string?)request["arguments"]?["projectId"] != project || (string?)request["arguments"]?["operationId"] != (string?)op["operationId"]
                    || !new[] { "revalidate_issue", "revalidate_object_issues", "set_issue_disposition" }.Contains((string?)request["tool"]) || !(result["receipt"] is JObject receipt) || receipt.Count != 5
                    || (string?)receipt["operationId"] != (string?)op["operationId"] || (string?)receipt["subsystem"] != "issues" || receipt["revision"]?.Type != JTokenType.Integer
                    || (long)receipt["revision"]! < 1 || (long)receipt["revision"]! > (long)doc["revision"]! || receipt["applied"]?.Type != JTokenType.Boolean || !(bool)receipt["applied"]!
                    || receipt["replayed"]?.Type != JTokenType.Boolean || (bool)receipt["replayed"]!) throw new InvalidDataException("Corrupt issue receipt. Journal was not replaced.");
                IssueValidation.Canonical(result); // Reject non-JSON/nonfinite receipt data.
                if ((string?)request["tool"] == "set_issue_disposition")
                {
                    var a = request["arguments"] as JObject;
                    if (a == null || a["issueId"]?.Type != JTokenType.String || a["snapshotId"]?.Type != JTokenType.String
                        || a["expectedEvidenceHash"]?.Type != JTokenType.String || !new[] { "Open", "Resolved", "Ignored" }.Contains((string?)a["disposition"])
                        || (a["reason"] != null && a["reason"]!.Type != JTokenType.String)
                        || result["updatedAt"]?.Type != JTokenType.String || !DateTimeOffset.TryParse((string?)result["updatedAt"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                        throw new InvalidDataException("Corrupt issue disposition journal entry. Journal was not replaced.");
                }
            }
            return doc;
        }
        private void Commit(JObject next)
        {
            string content = next.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(content) > 10000000 || ((JArray)next["operations"]!).Count > 10000) throw new ToolInputException("QUERY_TOO_LARGE", "Issue receipt journal is full; no action committed.");
            try { storage.Commit(committed, content); }
            catch (MemoryPersistenceConflictException ex) { throw new ToolInputException("REVISION_CONFLICT", ex.Message); }
            catch (Exception ex)
            {
                var diagnostic = DiagnosticLog.Report(ex, "issues.receipt_commit", Association.ProjectId, Snapshot.SnapshotId);
                throw new ToolInputException("PERSISTENCE_FAILED", "Issue action was not published. Diagnostic ID: " + diagnostic.Entry.CorrelationId);
            }
            committed = content; journal = next;
        }
        internal string Id(IssueKey key)
        {
            using var sha = SHA256.Create(); var text = new JArray(Association.ProjectId, Snapshot.SnapshotId, key.CheckerId, key.IssueType, key.SubjectKey).ToString(Formatting.None);
            return "ISSUE_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        internal async Task<JObject> ReadAsync(Func<IssueStore, JObject> read, CancellationToken token)
        { await gate.WaitAsync(token).ConfigureAwait(false); try { token.ThrowIfCancellationRequested(); return read(store); } finally { gate.Release(); } }
        private JObject? Replay(string name, JObject args)
        {
            var op = ((JArray)journal["operations"]!).OfType<JObject>().FirstOrDefault(o => (string?)o["operationId"] == (string?)args["operationId"]);
            if (op == null) return null;
            string fingerprint = IssueValidation.Canonical(new JObject { ["tool"] = name, ["arguments"] = args.DeepClone() });
            if ((string?)op["fingerprint"] != fingerprint) throw new ToolInputException("IDEMPOTENCY_CONFLICT", "operationId was already used for a different request. Exact retries must retain the original arguments.");
            var result = (JObject)op["result"]!.DeepClone(); result["receipt"]!["replayed"] = true; return result;
        }
        internal async Task<JObject?> RecoverAsync(string name, JObject args)
        { await gate.WaitAsync().ConfigureAwait(false); try { return Replay(name, args); } finally { gate.Release(); } }
        private void RestoreDispositions()
        {
            var latest = ((JArray)journal["operations"]!).OfType<JObject>()
                .Where(o => (string?)o["request"]?["tool"] == "set_issue_disposition" && (string?)o["request"]?["arguments"]?["snapshotId"] == Snapshot.SnapshotId)
                .GroupBy(o => (string)o["request"]!["arguments"]!["issueId"]!, StringComparer.Ordinal)
                .Select(g => g.OrderBy(o => (long)o["result"]!["receipt"]!["revision"]!).Last());
            var findings = store.GetPresentation().ToDictionary(p => Id(p.Finding.Key), StringComparer.Ordinal);
            foreach (var op in latest)
            {
                var a = op["request"]!["arguments"]!;
                if (!findings.TryGetValue((string)a["issueId"]!, out var finding)) continue;
                store.RestoreDisposition(finding.Finding.Key, (string)a["expectedEvidenceHash"]!, Enum.Parse<IssueDispositionState>((string)a["disposition"]!),
                    (string?)a["reason"], Snapshot.SnapshotId, DateTimeOffset.Parse((string)op["result"]!["updatedAt"]!, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        internal async Task<JObject> SetDispositionAsync(JObject args, CancellationToken token)
        {
            const string name = "set_issue_disposition";
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var replay = Replay(name, args); if (replay != null) return replay;
                if ((long)args["expectedRevision"]! != (long)journal["revision"]!) throw new ToolInputException("REVISION_CONFLICT", "Refresh issue reads for the current issues revision.");
                var nextStore = store.CloneForAnalysis();
                var finding = nextStore.GetPresentation().FirstOrDefault(p => Id(p.Finding.Key) == (string?)args["issueId"]);
                if (finding == null) throw new ToolInputException("UNKNOWN_ISSUE_ID", "No current issue with this snapshot-scoped ID.");
                var change = nextStore.SetDisposition(finding.Finding.Key, Enum.Parse<IssueDispositionState>((string)args["disposition"]!), (string?)args["reason"], (string)args["expectedEvidenceHash"]!);
                if (!change.Success) throw new ToolInputException(change.ErrorCode!, change.Message!);
                long revision = checked((long)journal["revision"]! + 1); nextStore.SetAnalysisRevision(revision);
                var result = new JObject { ["issueId"] = args["issueId"]!.DeepClone(), ["disposition"] = args["disposition"]!.DeepClone(),
                    ["updatedAt"] = change.Value!.UpdatedAt.ToString("O"), ["revision"] = revision,
                    ["receipt"] = new JObject { ["operationId"] = args["operationId"]!.DeepClone(), ["subsystem"] = "issues", ["revision"] = revision, ["applied"] = true, ["replayed"] = false } };
                var request = new JObject { ["tool"] = name, ["arguments"] = args.DeepClone() };
                result["scopeAtCommit"] = ScopeContext.From(args)?.Describe();
                var next = (JObject)journal.DeepClone(); next["revision"] = revision;
                ((JArray)next["operations"]!).Add(new JObject { ["operationId"] = args["operationId"]!.DeepClone(), ["request"] = request, ["fingerprint"] = IssueValidation.Canonical(request), ["result"] = result.DeepClone() });
                token.ThrowIfCancellationRequested(); Commit(next); store = nextStore;
                return result;
            }
            finally { gate.Release(); }
        }
        internal async Task<JObject> RevalidateAsync(string name, JObject args, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var replay = Replay(name, args); if (replay != null) return replay;
                if ((long)args["expectedRevision"]! != (long)journal["revision"]!) throw new ToolInputException("REVISION_CONFLICT", "Refresh issue reads for the current issues revision.");
                var nextStore = store.CloneForAnalysis(); IssueResult<IssueScanReport> scan;
                if (name == "revalidate_issue")
                {
                    var finding = store.GetPresentation().FirstOrDefault(p => Id(p.Finding.Key) == (string?)args["issueId"]);
                    if (finding == null) throw new ToolInputException("UNKNOWN_ISSUE_ID", "No current issue with this snapshot-scoped ID.");
                    scan = await engine.RevalidateAsync(nextStore, finding.Finding.Key, token).ConfigureAwait(false);
                }
                else
                {
                    string id = (string)args["objectId"]!;
                    if (!Snapshot.ComponentsById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown object: " + id);
                    var subjects = new HashSet<IssueSubject> { new IssueSubject(IssueSubjectKind.Object, id) };
                    foreach (var p in store.GetPresentation(id))
                    {
                        foreach (var objectId in p.Finding.Key.Subjects(false)) subjects.Add(new IssueSubject(IssueSubjectKind.Object, objectId));
                        foreach (var mateId in p.Finding.Key.Subjects(true)) subjects.Add(new IssueSubject(IssueSubjectKind.Mate, mateId));
                    }
                    scan = await engine.ScanAsync(nextStore, subjects, cancellationToken: token).ConfigureAwait(false);
                }
                if (!scan.Success) throw new ToolInputException(scan.ErrorCode!, scan.Message!);
                long revision = checked((long)journal["revision"]! + 1); nextStore.SetAnalysisRevision(revision);
                var result = new JObject { ["evaluationStatus"] = scan.Value!.Status.ToString(), ["revision"] = revision,
                    ["coverage"] = IssueToolSerialization.Coverage(scan.Value.Evaluations, "revalidated_subjects"),
                    ["receipt"] = new JObject { ["operationId"] = args["operationId"]!.DeepClone(), ["subsystem"] = "issues", ["revision"] = revision, ["applied"] = true, ["replayed"] = false } };
                if (name == "revalidate_issue")
                {
                    var current = nextStore.GetPresentation().FirstOrDefault(p => Id(p.Finding.Key) == (string?)args["issueId"]);
                    result["issueId"] = args["issueId"]!.DeepClone();
                    result["outcome"] = current?.IsVerified == true ? "Present" : scan.Value.Status == IssueScanStatus.Complete && current == null ? "Absent" : "UnableToEvaluate";
                }
                else { result["objectId"] = args["objectId"]!.DeepClone(); result["presentedCount"] = nextStore.GetPresentation((string)args["objectId"]!).Count(p => p.IsPresented); }
                result["scopeAtCommit"] = ScopeContext.From(args)?.Describe();
                var request = new JObject { ["tool"] = name, ["arguments"] = args.DeepClone() };
                var next = (JObject)journal.DeepClone(); next["revision"] = revision;
                ((JArray)next["operations"]!).Add(new JObject { ["operationId"] = args["operationId"]!.DeepClone(), ["request"] = request, ["fingerprint"] = IssueValidation.Canonical(request), ["result"] = result.DeepClone() });
                token.ThrowIfCancellationRequested(); Commit(next); store = nextStore; // No cancellable interval after durable acknowledgement.
                return result;
            }
            finally { gate.Release(); }
        }
    }
}
