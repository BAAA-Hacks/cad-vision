#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Core.Primitives.DataStructures.Issues
{
    public enum IssueSubjectKind { Object, Mate }
    public enum SubjectEvaluationStatus { Complete, UnableToEvaluate, Failed }
    public enum IssueScanStatus { Complete, Partial, UnableToEvaluate, Failed, NotEvaluated }

    // Occurrence IDs are scoped by the owning store's immutable project/snapshot.
    public sealed class IssueSubject : IEquatable<IssueSubject>
    {
        public IssueSubjectKind Kind { get; }
        public string Id { get; }
        public IssueSubject(IssueSubjectKind kind, string id)
        {
            if (!Enum.IsDefined(typeof(IssueSubjectKind), kind)) throw new ArgumentException("Unknown subject kind.");
            Kind = kind; Id = IssueValidation.Text(id, "subject ID");
        }
        public IssueKey Key(string checkerId, string issueType) => IssueKey.ForEntities(checkerId, issueType,
            Kind == IssueSubjectKind.Object ? new[] { Id } : Array.Empty<string>(),
            Kind == IssueSubjectKind.Mate ? new[] { Id } : Array.Empty<string>());
        public bool Equals(IssueSubject? other) => other != null && Kind == other.Kind && Id == other.Id;
        public override bool Equals(object? obj) => obj is IssueSubject s && Equals(s);
        public override int GetHashCode() => ((int)Kind * 397) ^ Id.GetHashCode();
    }

    public sealed class SubjectCheckResult
    {
        public SubjectEvaluationStatus Status { get; }
        public string Reason { get; }
        public IReadOnlyList<IssueFinding> Findings { get; }
        public SubjectCheckResult(SubjectEvaluationStatus status, IEnumerable<IssueFinding>? findings = null, string reason = "")
        {
            if (!Enum.IsDefined(typeof(SubjectEvaluationStatus), status)) throw new ArgumentException("Unknown evaluation status.");
            Status = status; Reason = reason ?? "";
            Findings = (findings ?? Array.Empty<IssueFinding>()).ToList().AsReadOnly();
            if (Findings.Any(f => f == null) || status != SubjectEvaluationStatus.Complete && Findings.Count != 0)
                throw new ArgumentException("Only Complete evaluations may return findings.");
        }
    }

    public sealed class SubjectEvaluation
    {
        public string ProjectId { get; }
        public string SnapshotId { get; }
        public string CheckerId { get; }
        public string? CheckerVersion { get; }
        public IssueSubject Subject { get; }
        public SubjectEvaluationStatus Status { get; }
        public string Reason { get; }
        public DateTimeOffset EvaluatedAt { get; }
        internal IssueKey Address => Subject.Key(CheckerId, "$evaluation");
        internal SubjectEvaluation(string project, string snapshot, string checker, string? version, IssueSubject subject, SubjectCheckResult result)
        {
            ProjectId = project; SnapshotId = snapshot; CheckerId = checker; CheckerVersion = version;
            Subject = subject; Status = result.Status; Reason = result.Reason; EvaluatedAt = DateTimeOffset.UtcNow;
        }
    }

    public sealed class IssueScanReport
    {
        public IReadOnlyList<SubjectEvaluation> Evaluations { get; }
        public IssueScanStatus Status { get; }
        internal IssueScanReport(IEnumerable<SubjectEvaluation> evaluations)
        {
            Evaluations = evaluations.ToList().AsReadOnly();
            Status = Evaluations.Count == 0 ? IssueScanStatus.NotEvaluated
                : Evaluations.All(e => e.Status == SubjectEvaluationStatus.Complete) ? IssueScanStatus.Complete
                : Evaluations.Any(e => e.Status == SubjectEvaluationStatus.Complete) ? IssueScanStatus.Partial
                : Evaluations.Any(e => e.Status == SubjectEvaluationStatus.Failed) ? IssueScanStatus.Failed : IssueScanStatus.UnableToEvaluate;
        }
    }

    public sealed class IssuePresentation
    {
        public IssueFinding Finding { get; }
        public IssueDisposition Disposition { get; }
        public SubjectEvaluation? LatestEvaluation { get; }
        public bool IsVerified { get; }
        public IReadOnlyList<IssueKey> SuppressedBy { get; }
        public bool IsPresented => SuppressedBy.Count == 0;
        internal IssuePresentation(IssueFinding finding, IssueDisposition disposition, SubjectEvaluation? evaluation, bool verified, IEnumerable<IssueKey> suppressedBy)
        { Finding = finding; Disposition = disposition; LatestEvaluation = evaluation; IsVerified = verified; SuppressedBy = suppressedBy.ToList().AsReadOnly(); }
    }
}
