#nullable enable
using System;
using Core.Primitives.DataStructures.Issues;

namespace Core.Primitives.Operations.Issues
{
    public sealed partial class IssueStore
    {
        // Called by the memory coordinator while holding its own store lock. Storage must
        // not call back into another store. Holding gate prevents a stale post-save acceptance.
        internal IssueResult<IssueDisposition> PersistDisposition(IssueKey key, string expectedHash, IssueDispositionState state,
            string? reason, Action<IssueFinding, IssueDisposition> persist)
        {
            lock (gate)
            {
                if (!Enum.IsDefined(typeof(IssueDispositionState), state)) return IssueResult<IssueDisposition>.Fail("INVALID_DISPOSITION", "Unknown disposition.");
                if (!findings.TryGetValue(key, out var finding)) return IssueResult<IssueDisposition>.Fail("NOT_FOUND", "No current finding.");
                if (finding.EvidenceHash != expectedHash) return IssueResult<IssueDisposition>.Fail("EVIDENCE_CHANGED", "Refresh evidence before accepting it.");
                var disposition = new IssueDisposition(key, state, reason, state == IssueDispositionState.Open ? null : expectedHash,
                    state == IssueDispositionState.Open ? null : finding.SnapshotId, DateTimeOffset.UtcNow);
                persist(finding, disposition); // A failure leaves runtime disposition/indexes unchanged.
                ChangeDisposition(disposition); revision++; return IssueResult<IssueDisposition>.Ok(disposition);
            }
        }
        internal bool RestoreDisposition(IssueKey key, string evidenceHash, IssueDispositionState state, string? reason, string originSnapshot, DateTimeOffset updated)
        {
            lock (gate)
            {
                if (!findings.TryGetValue(key, out var f) || f.EvidenceHash != evidenceHash || history[key].Count != 1 || dispositions[key].State != IssueDispositionState.Open) return false;
                if (state == IssueDispositionState.Open) return true;
                ChangeDisposition(new IssueDisposition(key, state, reason, evidenceHash, originSnapshot, updated)); revision++; return true;
            }
        }
    }
}
