using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives.DataStructures.Issues;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Issues
{
    internal static class IssueToolSerialization
    {
        internal static JObject Evaluation(SubjectEvaluation e) => new JObject { ["checkerId"] = e.CheckerId, ["checkerVersion"] = e.CheckerVersion,
            ["subjectKind"] = e.Subject.Kind.ToString(), ["subjectId"] = e.Subject.Id, ["status"] = e.Status.ToString(), ["reason"] = e.Reason,
            ["snapshotId"] = e.SnapshotId, ["evaluatedAt"] = e.EvaluatedAt.ToString("O") };
        internal static JObject Coverage(IEnumerable<SubjectEvaluation> source, string scope)
        {
            var entries = source.ToArray(); int complete = entries.Count(e => e.Status == SubjectEvaluationStatus.Complete), failed = entries.Count(e => e.Status == SubjectEvaluationStatus.Failed);
            var reasons = entries.Where(e => e.Status != SubjectEvaluationStatus.Complete).GroupBy(e => e.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            return new JObject { ["status"] = entries.Length > 0 && complete == entries.Length ? "complete" : complete > 0 ? "partial" : "unavailable",
                ["countUnit"] = "checker_subjects", ["countScope"] = scope, ["requestedCount"] = entries.Length, ["evaluatedCount"] = complete,
                ["excludedUnknownCount"] = entries.Length - complete - failed, ["failedCount"] = failed,
                ["reasonCodes"] = entries.Length == 0 ? new JArray("NO_CHECKER_EVALUATIONS_FOR_SCOPE") : new JArray(new[] { complete < entries.Length ? "ISSUE_EVALUATION_INCOMPLETE" : null, failed > 0 ? "CHECKER_FAILED" : null }.Where(s => s != null)),
                ["reasonCounts"] = new JArray(reasons.Take(32).Select(g => new JObject { ["reason"] = g.Key, ["count"] = g.Count() })),
                ["subjectEvaluations"] = new JArray(entries.Take(32).Select(Evaluation)), ["subjectEvaluationsTruncated"] = entries.Length > 32,
                ["reasonCountsTruncated"] = reasons.Length > 32,
                ["limitation"] = "Coverage describes registered checkers only; it is not proof that all possible engineering defects were checked." };
        }
        internal static JObject Finding(IssueAccess access, IssuePresentation p, bool evidence = false)
        {
            var f = p.Finding;
            var result = new JObject { ["issueId"] = access.Id(f.Key), ["checkerId"] = f.CheckerId, ["checkerVersion"] = f.CheckerVersion,
                ["issueType"] = f.Type, ["severity"] = f.Severity.ToString(), ["scope"] = f.Scope.ToString(),
                ["objects"] = new JArray(f.AffectedObjectIds.Select(id => new JObject { ["id"] = id, ["name"] = access.Snapshot.ComponentsById[id].Name })),
                ["mateIds"] = new JArray(f.RelatedMateIds), ["requiresDesignIntent"] = f.RequiresDesignIntent, ["isHeuristic"] = f.IsHeuristic,
                ["presented"] = p.IsPresented, ["suppressedBy"] = new JArray(p.SuppressedBy.Select(access.Id)),
                ["disposition"] = p.Disposition.State.ToString(), ["currentlyPresent"] = p.IsVerified ? new JValue(true) : JValue.CreateNull(),
                ["verification"] = p.IsVerified ? "verified" : "unverified", ["freshness"] = p.IsVerified ? "current_snapshot" : "requires_revalidation",
                ["detectedAtSnapshot"] = f.SnapshotId, ["evidenceHash"] = f.EvidenceHash,
                ["latestEvaluation"] = p.LatestEvaluation == null ? null : Evaluation(p.LatestEvaluation) };
            if (evidence) result["evidence"] = f.Evidence;
            return result;
        }
    }
}
