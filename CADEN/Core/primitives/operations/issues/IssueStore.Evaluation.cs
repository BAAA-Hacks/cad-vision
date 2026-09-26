using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives.DataStructures.Issues;

namespace Core.Primitives.Operations.Issues
{
    public sealed partial class IssueStore
    {
        private readonly Dictionary<string, HashSet<IssueKey>> subjects = new Dictionary<string, HashSet<IssueKey>>(StringComparer.Ordinal);
        private readonly Dictionary<IssueKey, HashSet<IssueKey>> evaluationKeys = new Dictionary<IssueKey, HashSet<IssueKey>>();
        private readonly Dictionary<IssueKey, SubjectEvaluation> evaluations = new Dictionary<IssueKey, SubjectEvaluation>();
        private readonly HashSet<IssueKey> verified = new HashSet<IssueKey>();
        internal long CaptureRevision() { lock (gate) return revision; }
        internal bool IsScanManaged(IssueKey key) { lock (gate) return evaluations.ContainsKey(key.EvaluationKey); }

        public SubjectEvaluation? GetEvaluation(string checkerId, IssueSubject subject)
        { lock (gate) return evaluations.TryGetValue(subject.Key(checkerId, "$evaluation"), out var evaluation) ? evaluation : null; }

        // Existing By* queries expose candidates. This view adds presentation and freshness.
        public IReadOnlyList<IssuePresentation> GetPresentation(string? objectId = null)
        {
            lock (gate)
            {
                IEnumerable<IssueKey> keys = objectId == null ? findings.Keys
                    : objects.TryGetValue(objectId, out var matching) ? matching : Enumerable.Empty<IssueKey>();
                var result = new List<IssuePresentation>();
                foreach (var key in keys.OrderBy(k => k.CheckerId, StringComparer.Ordinal).ThenBy(k => k.IssueType, StringComparer.Ordinal).ThenBy(k => k.SubjectKey, StringComparer.Ordinal))
                {
                    evaluations.TryGetValue(key.EvaluationKey, out var evaluation);
                    var suppressors = subjects[key.SubjectKey].Where(k => verified.Contains(k) && IssuePrecedence.Suppresses(k, key))
                        .OrderBy(k => k.CheckerId, StringComparer.Ordinal).ThenBy(k => k.IssueType, StringComparer.Ordinal);
                    result.Add(new IssuePresentation(findings[key], dispositions[key], evaluation, verified.Contains(key), suppressors));
                }
                return result.AsReadOnly();
            }
        }

        internal IssueResult<IssueScanReport> ApplyScan(long expectedRevision, IReadOnlyList<EvaluatedSubject> batch)
        {
            lock (gate)
            {
                if (revision != expectedRevision) return IssueResult<IssueScanReport>.Fail("STORE_CHANGED", "The store changed during evaluation; retry against current state.");
                var addresses = new HashSet<IssueKey>();
                foreach (var entry in batch)
                {
                    var evaluation = entry.Evaluation;
                    if (!addresses.Add(evaluation.Address)) return IssueResult<IssueScanReport>.Fail("INVALID_CHECK_RESULT", "Duplicate subject evaluation.");
                    var keys = new HashSet<IssueKey>();
                    foreach (var f in entry.Result.Findings)
                    {
                        var error = Validate(f);
                        if (error != null || f.Key.EvaluationKey != evaluation.Address || f.CheckerVersion != evaluation.CheckerVersion || !keys.Add(f.Key))
                            return IssueResult<IssueScanReport>.Fail("INVALID_CHECK_RESULT", error ?? "Finding is outside the evaluated checker/version/subject or duplicates a key.");
                    }
                }
                foreach (var entry in batch)
                {
                    var address = entry.Evaluation.Address;
                    var prior = evaluationKeys.TryGetValue(address, out var set) ? set.ToArray() : Array.Empty<IssueKey>();
                    if (entry.Result.Status == SubjectEvaluationStatus.Complete)
                    {
                        var returned = new HashSet<IssueKey>(entry.Result.Findings.Select(f => f.Key));
                        foreach (var key in prior) if (!returned.Contains(key)) Remove(findings[key]);
                        foreach (var f in entry.Result.Findings) { Upsert(f); verified.Add(f.Key); }
                    }
                    else foreach (var key in prior) verified.Remove(key);
                    evaluations[address] = entry.Evaluation;
                }
                if (batch.Count > 0) revision++;
                return IssueResult<IssueScanReport>.Ok(new IssueScanReport(batch.Select(b => b.Evaluation)));
            }
        }
    }

    internal sealed class EvaluatedSubject
    {
        public SubjectEvaluation Evaluation { get; }
        public SubjectCheckResult Result { get; }
        public EvaluatedSubject(SubjectEvaluation evaluation, SubjectCheckResult result) { Evaluation = evaluation; Result = result; }
    }

    // One policy shared by presentation and targeted group expansion.
    internal static class IssuePrecedence
    {
        private static readonly (string Winner, string WinnerType, string Loser, string LoserType)[] Rules =
        {
            ("mate.dangling", "dangling", "mate.error", "error"),
            ("constraint.unsolvable", "unsolvable", "constraint.over_defined", "over_defined")
        };
        internal static bool Suppresses(IssueKey winner, IssueKey loser) => Rules.Any(r => r.Winner == winner.CheckerId && r.WinnerType == winner.IssueType && r.Loser == loser.CheckerId && r.LoserType == loser.IssueType);
        internal static IReadOnlyList<string> Group(string id)
        {
            var group = new HashSet<string>(StringComparer.Ordinal) { id };
            bool changed;
            do { changed = false; foreach (var r in Rules) if (group.Contains(r.Winner) || group.Contains(r.Loser)) { changed |= group.Add(r.Winner); changed |= group.Add(r.Loser); } } while (changed);
            return group.OrderBy(s => s, StringComparer.Ordinal).ToList().AsReadOnly();
        }
    }
}
