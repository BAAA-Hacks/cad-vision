using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;

namespace Core.Primitives.Operations.Issues
{
    public sealed partial class IssueStore
    {
        private readonly object gate = new object();
        private long revision;
        private readonly Dictionary<IssueKey, IssueFinding> findings = new Dictionary<IssueKey, IssueFinding>();
        private readonly Dictionary<IssueKey, IssueDisposition> dispositions = new Dictionary<IssueKey, IssueDisposition>();
        private readonly Dictionary<IssueKey, List<IssueDisposition>> history = new Dictionary<IssueKey, List<IssueDisposition>>();
        private readonly Dictionary<string, HashSet<IssueKey>> objects = new Dictionary<string, HashSet<IssueKey>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<IssueKey>> mates = new Dictionary<string, HashSet<IssueKey>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<IssueKey>> types = new Dictionary<string, HashSet<IssueKey>>(StringComparer.Ordinal);
        private readonly Dictionary<IssueSeverity, HashSet<IssueKey>> severities = new Dictionary<IssueSeverity, HashSet<IssueKey>>();
        private readonly Dictionary<IssueDispositionState, HashSet<IssueKey>> states = new Dictionary<IssueDispositionState, HashSet<IssueKey>>();
        public ProjectSnapshot Snapshot { get; }
        public IssueStore(ProjectSnapshot snapshot) { Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)); }
        public int Count { get { lock (gate) return findings.Count; } }
        public IssueResult<IssueFinding> GetFinding(IssueKey key)
        { lock (gate) return findings.TryGetValue(key, out var f) ? IssueResult<IssueFinding>.Ok(f) : IssueResult<IssueFinding>.Fail("NOT_FOUND", "No current finding for this key."); }
        // Retained after removal, but never included in active disposition indexes.
        public IssueResult<IssueDisposition> GetDisposition(IssueKey key)
        { lock (gate) return dispositions.TryGetValue(key, out var d) ? IssueResult<IssueDisposition>.Ok(d) : IssueResult<IssueDisposition>.Fail("NOT_FOUND", "No disposition for this key."); }
        public IReadOnlyList<IssueDisposition> GetDispositionHistory(IssueKey key)
        { lock (gate) return history.TryGetValue(key, out var h) ? h.ToList().AsReadOnly() : Array.AsReadOnly(Array.Empty<IssueDisposition>()); }
        private IReadOnlyList<IssueKey> Query<T>(Dictionary<T, HashSet<IssueKey>> index, T value) where T : notnull
        { lock (gate) return index.TryGetValue(value, out var keys) ? keys.OrderBy(k => k.CheckerId, StringComparer.Ordinal).ThenBy(k => k.IssueType, StringComparer.Ordinal).ThenBy(k => k.SubjectKey, StringComparer.Ordinal).ToList().AsReadOnly() : Array.AsReadOnly(Array.Empty<IssueKey>()); }
        public IReadOnlyList<IssueKey> ByObject(string id) => Query(objects, id);
        public IReadOnlyList<IssueKey> ByMate(string id) => Query(mates, id);
        public IReadOnlyList<IssueKey> ByType(string type) => Query(types, type);
        public IReadOnlyList<IssueKey> BySeverity(IssueSeverity severity) => Query(severities, severity);
        public IReadOnlyList<IssueKey> ByDisposition(IssueDispositionState state) => Query(states, state);

        private string? Validate(IssueFinding f)
        {
            if (f.ProjectId != Snapshot.ProjectId || f.SnapshotId != Snapshot.SnapshotId) return "Finding belongs to a different project or snapshot.";
            if (f.AffectedObjectIds.Any(id => !Snapshot.ComponentsById.ContainsKey(id)) || f.RelatedMateIds.Any(id => !Snapshot.MatesById.ContainsKey(id))) return "Finding references an unknown object or mate.";
            if (f.Scope == IssueScope.Assembly && Snapshot.ComponentsById[f.AffectedObjectIds[0]].Type != "assembly") return "Assembly scope requires an assembly object.";
            return null;
        }
        public IssueResult<IssueFinding> AddFinding(IssueFinding finding)
        {
            if (finding == null) throw new ArgumentNullException(nameof(finding));
            lock (gate)
            {
                var error = Validate(finding); if (error != null) return IssueResult<IssueFinding>.Fail("INVALID_FINDING", error);
                if (findings.TryGetValue(finding.Key, out var old)) return old.EvidenceHash == finding.EvidenceHash ? IssueResult<IssueFinding>.Ok(old) : IssueResult<IssueFinding>.Fail("ALREADY_EXISTS", "Use replacement or revalidation for changed evidence.");
                Upsert(finding); revision++; return IssueResult<IssueFinding>.Ok(finding);
            }
        }
        public IssueResult<IssueFinding> ReplaceFindingEvidence(IssueFinding finding, string expectedEvidenceHash)
        {
            if (finding == null) throw new ArgumentNullException(nameof(finding));
            lock (gate)
            {
                if (!findings.TryGetValue(finding.Key, out var old)) return IssueResult<IssueFinding>.Fail("NOT_FOUND", "No current finding.");
                if (old.EvidenceHash != expectedEvidenceHash) return IssueResult<IssueFinding>.Fail("EVIDENCE_CHANGED", "Refresh the finding before replacing it.");
                var error = Validate(finding); if (error != null) return IssueResult<IssueFinding>.Fail("INVALID_FINDING", error);
                Upsert(finding); revision++; return IssueResult<IssueFinding>.Ok(finding);
            }
        }
        public IssueResult<IssueFinding> RemoveFinding(IssueKey key, string expectedEvidenceHash)
        {
            lock (gate)
            {
                if (!findings.TryGetValue(key, out var old)) return IssueResult<IssueFinding>.Fail("NOT_FOUND", "No current finding.");
                if (old.EvidenceHash != expectedEvidenceHash) return IssueResult<IssueFinding>.Fail("EVIDENCE_CHANGED", "Refresh the finding before removing it.");
                Remove(old); revision++; return IssueResult<IssueFinding>.Ok(old);
            }
        }
        public IssueResult<IssueDisposition> SetDisposition(IssueKey key, IssueDispositionState state, string? reason, string expectedEvidenceHash)
        {
            lock (gate)
            {
                if (!Enum.IsDefined(typeof(IssueDispositionState), state)) return IssueResult<IssueDisposition>.Fail("INVALID_DISPOSITION", "Unknown disposition.");
                if (!findings.TryGetValue(key, out var f)) return IssueResult<IssueDisposition>.Fail("NOT_FOUND", "No current finding.");
                if (f.EvidenceHash != expectedEvidenceHash) return IssueResult<IssueDisposition>.Fail("EVIDENCE_CHANGED", "Refresh the evidence before accepting it.");
                var d = new IssueDisposition(key, state, reason, state == IssueDispositionState.Open ? null : f.EvidenceHash, state == IssueDispositionState.Open ? null : f.SnapshotId, DateTimeOffset.UtcNow);
                ChangeDisposition(d); revision++; return IssueResult<IssueDisposition>.Ok(d);
            }
        }
        private static void Index<T>(Dictionary<T, HashSet<IssueKey>> index, T value, IssueKey key, bool add) where T : notnull
        {
            if (add) { if (!index.TryGetValue(value, out var set)) index[value] = set = new HashSet<IssueKey>(); set.Add(key); }
            else if (index.TryGetValue(value, out var set)) { set.Remove(key); if (set.Count == 0) index.Remove(value); }
        }
        private void IndexFinding(IssueFinding f, bool add)
        {
            Index(subjects, f.Key.SubjectKey, f.Key, add);
            Index(evaluationKeys, f.Key.EvaluationKey, f.Key, add);
            foreach (var id in f.AffectedObjectIds) Index(objects, id, f.Key, add);
            foreach (var id in f.RelatedMateIds) Index(mates, id, f.Key, add);
            Index(types, f.Type, f.Key, add); Index(severities, f.Severity, f.Key, add);
            Index(states, dispositions[f.Key].State, f.Key, add);
        }
        private void ChangeDisposition(IssueDisposition d)
        {
            bool active = findings.ContainsKey(d.Key);
            if (active && dispositions.TryGetValue(d.Key, out var old)) Index(states, old.State, d.Key, false);
            dispositions[d.Key] = d;
            if (!history.TryGetValue(d.Key, out var h)) history[d.Key] = h = new List<IssueDisposition>();
            h.Add(d); if (active) Index(states, d.State, d.Key, true);
        }
        private void Upsert(IssueFinding f)
        {
            verified.Remove(f.Key);
            if (findings.TryGetValue(f.Key, out var old)) Remove(old);
            if (!dispositions.TryGetValue(f.Key, out var d) || d.State != IssueDispositionState.Open && d.AcceptedEvidenceHash != f.EvidenceHash)
                ChangeDisposition(new IssueDisposition(f.Key, IssueDispositionState.Open, null, null, null, DateTimeOffset.UtcNow));
            findings[f.Key] = f; IndexFinding(f, true);
        }
        private void Remove(IssueFinding f) { IndexFinding(f, false); findings.Remove(f.Key); verified.Remove(f.Key); }

        internal (IssueFinding? Finding, long Revision) Capture(IssueKey key)
        { lock (gate) return (findings.TryGetValue(key, out var f) ? f : null, revision); }
        internal IssueResult<IssueCheckResult> Apply(IssueKey key, long expectedRevision, IssueCheckResult result, string checkerVersion)
        {
            lock (gate)
            {
                if (revision != expectedRevision) return IssueResult<IssueCheckResult>.Fail("STORE_CHANGED", "The store changed during evaluation; retry against current state.");
                if (result.Outcome == RevalidationOutcome.UnableToEvaluate) return IssueResult<IssueCheckResult>.Ok(result);
                var original = findings[key];
                bool containsOriginal = result.Findings.Any(f => f.Key == key);
                if ((result.Outcome == RevalidationOutcome.Present) != containsOriginal || result.Findings.Select(f => f.Key).Distinct().Count() != result.Findings.Count)
                    return IssueResult<IssueCheckResult>.Fail("INVALID_CHECK_RESULT", "Outcome and findings disagree, or duplicate keys were returned.");
                foreach (var f in result.Findings)
                {
                    var error = Validate(f);
                    if (error != null || f.CheckerId != key.CheckerId || f.CheckerVersion != checkerVersion ||
                        original.Scope != IssueScope.Project && !f.AffectedObjectIds.Intersect(original.AffectedObjectIds).Any() && !f.RelatedMateIds.Intersect(original.RelatedMateIds).Any())
                        return IssueResult<IssueCheckResult>.Fail("INVALID_CHECK_RESULT", error ?? "Checker returned findings outside its version or target scope.");
                }
                // All validation completes before any index or disposition changes.
                if (!containsOriginal) Remove(original);
                foreach (var f in result.Findings) Upsert(f);
                revision++; return IssueResult<IssueCheckResult>.Ok(result);
            }
        }

        public IssueResult<IssueDisposition> CarryDispositionFrom(IssueStore previous, IssueKey key, bool mateIdsStableAcrossSnapshots = false)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            IssueFinding? source; IssueDisposition? accepted;
            lock (previous.gate) { previous.findings.TryGetValue(key, out source); previous.dispositions.TryGetValue(key, out accepted); }
            lock (gate)
            {
                if (source == null || accepted == null || !findings.TryGetValue(key, out var current)) return IssueResult<IssueDisposition>.Fail("NOT_FOUND", "Both snapshots must contain the finding.");
                if (Snapshot.ProjectId != previous.Snapshot.ProjectId) return IssueResult<IssueDisposition>.Fail("PROJECT_MISMATCH", "Dispositions cannot cross projects.");
                if (Snapshot.SnapshotId != previous.Snapshot.SnapshotId &&
                    (Snapshot.IdentityScope != IdentityScope.ProjectStable || previous.Snapshot.IdentityScope != IdentityScope.ProjectStable || current.RelatedMateIds.Count > 0 && !mateIdsStableAcrossSnapshots))
                    return IssueResult<IssueDisposition>.Fail("UNTRUSTED_IDENTITY", "Stable component and, where used, mate identity must be guaranteed.");
                if (source.EvidenceHash != current.EvidenceHash) return IssueResult<IssueDisposition>.Fail("EVIDENCE_CHANGED", "Acceptance does not apply to changed evidence.");
                if (accepted.State == IssueDispositionState.Open) return IssueResult<IssueDisposition>.Fail("NO_ACCEPTANCE", "The previous finding has no accepted disposition.");
                if (history[key].Count != 1 || dispositions[key].State != IssueDispositionState.Open) return IssueResult<IssueDisposition>.Fail("DISPOSITION_EXISTS", "Carry-over cannot overwrite a decision in this store.");
                ChangeDisposition(accepted); revision++; return IssueResult<IssueDisposition>.Ok(accepted);
            }
        }
    }
}
