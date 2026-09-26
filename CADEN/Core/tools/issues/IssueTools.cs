using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.Operations.Issues;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Issues
{
    internal class IssueReadTool : IAsyncCadenTool
    {
        protected readonly IssueAccess access;
        private readonly ToolLimits limits;
        private readonly QueryCursors cursors;
        public string Name { get; }
        internal IssueReadTool(string name, IssueAccess access, ToolLimits limits, QueryCursors cursors)
        { Name = name; this.access = access; this.limits = limits; this.cursors = cursors; }
        private static JObject Text(params string[] values) => values.Length == 0 ? new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 } : new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        public JObject Declaration
        {
            get
            {
                var fields = new JObject { ["projectId"] = Text(), ["snapshotId"] = Text() }; var required = new JArray("projectId", "snapshotId");
                bool action = Name.StartsWith("revalidate_", StringComparison.Ordinal) || Name == "set_issue_disposition";
                string? target = Name == "get_issue" || Name == "get_issue_evidence" || Name == "revalidate_issue" || Name == "set_issue_disposition" ? "issueId"
                    : Name == "get_issues_for_object" || Name == "revalidate_object_issues" ? "objectId" : Name == "get_issues_for_mate" ? "mateId" : null;
                if (target != null) { fields[target] = Text(); required.Add(target); }
                if (action)
                {
                    fields["operationId"] = Text(); fields["expectedRevision"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 9007199254740991L };
                    required.Add("operationId"); required.Add("expectedRevision");
                }
                else fields["includeSuppressedCandidates"] = new JObject { ["type"] = "boolean" };
                if (Name == "set_issue_disposition")
                {
                    fields["disposition"] = Text("Open", "Resolved", "Ignored"); fields["expectedEvidenceHash"] = Text(); fields["reason"] = Text();
                    required.Add("disposition"); required.Add("expectedEvidenceHash");
                }
                if (Name == "list_issues" || Name == "get_issues_for_object" || Name == "get_issues_for_mate")
                {
                    fields["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = limits.MaxResults }; fields["cursor"] = Text();
                    fields["filters"] = new JObject { ["type"] = "object", ["properties"] = new JObject {
                        ["severity"] = Text("Error", "Warning", "Question", "Info"), ["disposition"] = Text("Open", "Resolved", "Ignored"),
                        ["checkerId"] = Text(), ["issueType"] = Text(), ["objectId"] = Text(), ["mateId"] = Text() } };
                }
                string description = Name switch {
                    "set_issue_disposition" => "Set Open, Resolved or Ignored only when requested by the user. Requires issueId, expectedEvidenceHash from an issue read, expectedRevision and operationId; optional reason. Saves disposition durably without changing finding evidence or proving a CAD fix. Exact retries reuse all original arguments.",
                    "get_issue" => "Read one current snapshot-scoped finding by issueId, including verification and disposition; does not run checks.",
                    "get_issue_evidence" => "Read a finding and its structured evidence; evidence is read-only.",
                    "get_issue_summary" => "Read presented issue counts, registered checker IDs, current issues revision, and evaluation coverage. Zero findings is not proof of a defect-free design.",
                    "revalidate_issue" => "Rerun the finding's checker precedence group against the loaded immutable snapshot. Requires operationId and expectedRevision from issue reads. Does not resolve/ignore findings or query live CAD. Successful commit may still yield UnableToEvaluate.",
                    "revalidate_object_issues" => "Rerun registered checks for an object and existing related finding subjects, including island representatives. Requires operationId and expectedRevision. Does not mutate CAD/evidence directly or mark findings resolved or ignored.",
                    _ => "List current findings with bounded pagination and optional exact-match AND filters. Presented findings by default; includeSuppressedCandidates includes precedence-suppressed candidates, not suppressed CAD components. Coverage remains independent of filtering. Repeat identical arguments with nextCursor." };
                return new JObject { ["name"] = Name, ["description"] = description, ["parameters"] = new JObject { ["type"] = "object", ["properties"] = fields, ["required"] = required } };
            }
        }
        public JObject Execute(JObject args) => ExecuteAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        public virtual Task<JObject> ExecuteAsync(JObject args, CancellationToken token) => access.ReadAsync(store => Read(store, args), token);
        private JObject Read(IssueStore store, JObject args)
        {
            var view = store.ReadView(); var all = view.Findings;
            var scope = ScopeContext.From(args);
            bool Relevant(IssuePresentation p) => scope == null || p.Finding.AffectedObjectIds.Any(scope.Includes) || p.Finding.RelatedMateIds.Any(id => access.Snapshot.MatesById.TryGetValue(id, out var m) && scope.Classify(m.ObjectAId, m.ObjectBId) != "External");
            if (scope != null && args["issueId"] != null)
            {
                var target = all.FirstOrDefault(p => access.Id(p.Finding.Key) == (string?)args["issueId"]);
                if (target != null && !Relevant(target)) throw new ToolInputException("OUT_OF_SCOPE", "Finding does not affect active scope. Change or clear scope.");
            }
            all = all.Where(Relevant).ToArray();
            var evaluations = view.Evaluations.AsEnumerable(); IEnumerable<IssuePresentation> selected = all;
            if (scope != null)
            {
                var relatedSubjects = new HashSet<IssueSubject>();
                foreach (var p in all)
                {
                    foreach (var id in p.Finding.Key.Subjects(false)) relatedSubjects.Add(new IssueSubject(IssueSubjectKind.Object, id));
                    foreach (var id in p.Finding.Key.Subjects(true)) relatedSubjects.Add(new IssueSubject(IssueSubjectKind.Mate, id));
                }
                evaluations = evaluations.Where(e => relatedSubjects.Contains(e.Subject) || (e.Subject.Kind == IssueSubjectKind.Object ? scope.Includes(e.Subject.Id)
                    : access.Snapshot.MatesById.TryGetValue(e.Subject.Id, out var m) && scope.Classify(m.ObjectAId, m.ObjectBId) != "External"));
            }
            string? objectId = (string?)args["objectId"], mateId = (string?)args["mateId"];
            void CheckObject(string id) { if (!access.Snapshot.ComponentsById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown object: " + id); }
            void CheckMate(string id) { if (!access.Snapshot.MatesById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_MATE_ID", "Unknown or unavailable mate: " + id); }
            if (objectId != null)
            {
                CheckObject(objectId); selected = selected.Where(p => p.Finding.AffectedObjectIds.Contains(objectId));
                var subjects = new HashSet<IssueSubject> { new IssueSubject(IssueSubjectKind.Object, objectId) };
                foreach (var p in selected) { foreach (var id in p.Finding.Key.Subjects(false)) subjects.Add(new IssueSubject(IssueSubjectKind.Object, id)); foreach (var id in p.Finding.Key.Subjects(true)) subjects.Add(new IssueSubject(IssueSubjectKind.Mate, id)); }
                evaluations = evaluations.Where(e => subjects.Contains(e.Subject));
            }
            if (mateId != null) { CheckMate(mateId); selected = selected.Where(p => p.Finding.RelatedMateIds.Contains(mateId)); evaluations = evaluations.Where(e => e.Subject.Kind == IssueSubjectKind.Mate && e.Subject.Id == mateId); }
            if ((bool?)args["includeSuppressedCandidates"] != true) selected = selected.Where(p => p.IsPresented);
            var coverage = IssueToolSerialization.Coverage(evaluations, objectId != null ? "object_and_related_finding_subjects" : mateId != null ? "mate" : "snapshot_registered_checks");
            var result = new JObject { ["revision"] = view.Revision, ["coverage"] = coverage };
            if (scope != null) coverage["countScope"] = "active_scope_and_related_finding_subjects_before_filters";
            JObject Serialize(IssuePresentation p, bool evidence = false)
            {
                var row = IssueToolSerialization.Finding(access, p, evidence);
                if (scope != null) row["outsideScopeObjectIds"] = new JArray(p.Finding.AffectedObjectIds.Where(id => !scope.Includes(id)));
                return row;
            }
            if (Name == "get_issue" || Name == "get_issue_evidence")
            {
                var found = selected.FirstOrDefault(p => access.Id(p.Finding.Key) == (string?)args["issueId"]);
                if (found == null) throw new ToolInputException("UNKNOWN_ISSUE_ID", "Issue is absent from the requested view. IDs are snapshot-scoped; includeSuppressedCandidates is required for precedence-suppressed findings.");
                result["issue"] = Serialize(found, Name == "get_issue_evidence"); return result;
            }
            if (Name == "get_issue_summary")
            {
                var rows = selected.ToArray(); result["count"] = rows.Length; result["candidateCount"] = all.Count; result["presentedCount"] = all.Count(p => p.IsPresented);
                result["precedenceSuppressedCount"] = all.Count(p => !p.IsPresented); result["registeredCheckerIds"] = new JArray(access.CheckerIds);
                result["bySeverity"] = new JObject(Enum.GetNames(typeof(IssueSeverity)).Select(s => new JProperty(s, rows.Count(p => p.Finding.Severity.ToString() == s))));
                result["byDisposition"] = new JObject(Enum.GetNames(typeof(IssueDispositionState)).Select(s => new JProperty(s, rows.Count(p => p.Disposition.State.ToString() == s))));
                result["persistence"] = "Findings are rescanned on load; action receipts and explicit dispositions are durable. Dispositions restore only for matching snapshot and evidence."; return result;
            }
            if (args["filters"] is JObject f)
            {
                if (f["objectId"] != null) { CheckObject((string)f["objectId"]!); selected = selected.Where(p => p.Finding.AffectedObjectIds.Contains((string)f["objectId"]!)); }
                if (f["mateId"] != null) { CheckMate((string)f["mateId"]!); selected = selected.Where(p => p.Finding.RelatedMateIds.Contains((string)f["mateId"]!)); }
                if (f["severity"] != null) selected = selected.Where(p => p.Finding.Severity.ToString() == (string?)f["severity"]);
                if (f["disposition"] != null) selected = selected.Where(p => p.Disposition.State.ToString() == (string?)f["disposition"]);
                if (f["checkerId"] != null) selected = selected.Where(p => p.Finding.CheckerId == (string?)f["checkerId"]);
                if (f["issueType"] != null) selected = selected.Where(p => p.Finding.Type == (string?)f["issueType"]);
            }
            if (args["limit"] == null) args["limit"] = Math.Min(20, limits.MaxResults);
            int limit = (int)args["limit"]!, offset = cursors.Resolve(Name, access.Association.ProjectId, access.Snapshot.SnapshotId, args, "issues", view.Revision);
            var ordered = selected.OrderByDescending(p => p.Finding.Severity).ThenBy(p => access.Id(p.Finding.Key), StringComparer.Ordinal).ToArray();
            var page = ordered.Skip(offset).Take(limit).ToArray(); result["items"] = new JArray(page.Select(p => Serialize(p)));
            result["pagination"] = new JObject { ["limit"] = limit, ["total"] = ordered.Length, ["nextCursor"] = offset + page.Length < ordered.Length ? cursors.Issue(Name, access.Association.ProjectId, access.Snapshot.SnapshotId, args, offset + page.Length, "issues", view.Revision) : null };
            return result;
        }
    }
    internal sealed class IssueActionTool : IssueReadTool, IActionCadenTool
    {
        internal IssueActionTool(string name, IssueAccess access, ToolLimits limits, QueryCursors cursors) : base(name, access, limits, cursors) { }
        public override async Task<JObject> ExecuteAsync(JObject args, CancellationToken token)
        {
            var scope = ScopeContext.From(args);
            if (scope != null && args["issueId"] != null) await access.ReadAsync(store => {
                var p = store.ReadView().Findings.FirstOrDefault(f => access.Id(f.Finding.Key) == (string?)args["issueId"]);
                if (p != null && !p.Finding.AffectedObjectIds.Any(scope.Includes) && !p.Finding.RelatedMateIds.Any(id => access.Snapshot.MatesById.TryGetValue(id, out var m) && scope.Classify(m.ObjectAId, m.ObjectBId) != "External"))
                    throw new ToolInputException("OUT_OF_SCOPE", "Finding is external to active scope. Change or clear scope.");
                return new JObject();
            }, token).ConfigureAwait(false);
            return Name == "set_issue_disposition" ? await access.SetDispositionAsync(args, token).ConfigureAwait(false) : await access.RevalidateAsync(Name, args, token).ConfigureAwait(false);
        }
        public Task<JObject?> RecoverCommittedAsync(JObject args) => access.RecoverAsync(Name, args);
    }
    internal static class IssueTools
    {
        internal static IEnumerable<ICadenTool> Create(IssueAccess? access, ToolLimits limits)
        {
            if (access == null) yield break;
            var cursors = new QueryCursors();
            foreach (var name in new[] { "get_issue", "list_issues", "get_issues_for_object", "get_issues_for_mate", "get_issue_summary", "get_issue_evidence" }) yield return new IssueReadTool(name, access, limits, cursors);
            foreach (var name in new[] { "revalidate_issue", "revalidate_object_issues", "set_issue_disposition" }) yield return new IssueActionTool(name, access, limits, cursors);
        }
    }
}
