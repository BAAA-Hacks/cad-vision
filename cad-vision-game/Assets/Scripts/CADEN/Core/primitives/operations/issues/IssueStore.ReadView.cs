#nullable enable
using System.Collections.Generic;
using System.Linq;
using Core.Primitives.DataStructures.Issues;

namespace Core.Primitives.Operations.Issues
{
    public sealed partial class IssueStore
    {
        internal (long Revision, IReadOnlyList<IssuePresentation> Findings, IReadOnlyList<SubjectEvaluation> Evaluations) ReadView()
        { lock (gate) return (revision, GetPresentation(), evaluations.Values.OrderBy(e => e.CheckerId, System.StringComparer.Ordinal).ThenBy(e => e.Subject.Kind).ThenBy(e => e.Subject.Id, System.StringComparer.Ordinal).ToList().AsReadOnly()); }
        internal void SetAnalysisRevision(long value) { lock (gate) revision = value; }
        // Isolated analysis transaction: findings/evaluations are immutable; mutable indexes are rebuilt.
        internal IssueStore CloneForAnalysis()
        {
            lock (gate)
            {
                var copy = new IssueStore(Snapshot);
                foreach (var p in dispositions) copy.dispositions.Add(p.Key, p.Value);
                foreach (var p in history) copy.history.Add(p.Key, new List<IssueDisposition>(p.Value));
                foreach (var p in findings) { copy.findings.Add(p.Key, p.Value); copy.IndexFinding(p.Value, true); }
                foreach (var p in evaluations) copy.evaluations.Add(p.Key, p.Value);
                copy.verified.UnionWith(verified); copy.revision = revision;
                return copy;
            }
        }
    }
}
