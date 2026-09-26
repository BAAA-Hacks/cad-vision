using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;
using Core.Diagnostics;

namespace Core.Primitives.Operations.Issues
{
    public interface ISubjectIssueChecker
    {
        string Id { get; }
        string Version { get; }
        IssueSubjectKind SubjectKind { get; }
        IssueCapabilities RequiredCapabilities { get; }
        Task<SubjectCheckResult> EvaluateAsync(ProjectSnapshot snapshot, IssueSubject subject, CancellationToken cancellationToken);
    }

    public sealed class IssueEngine
    {
        private sealed class Registration
        {
            public ISubjectIssueChecker Checker { get; }
            public string Version { get; }
            public IssueSubjectKind Kind { get; }
            public IssueCapabilities Capabilities { get; }
            public Registration(ISubjectIssueChecker checker)
            { Checker = checker; Version = IssueValidation.Text(checker.Version, "version"); Kind = checker.SubjectKind; Capabilities = checker.RequiredCapabilities; }
        }
        private readonly Dictionary<string, Registration> checkers = new Dictionary<string, Registration>(StringComparer.Ordinal);
        public IReadOnlyList<string> CheckerIds => checkers.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        public IssueEngine(IEnumerable<ISubjectIssueChecker> checkers)
        {
            foreach (var checker in checkers ?? throw new ArgumentNullException(nameof(checkers)))
            {
                if (checker == null) throw new ArgumentException("Null checker.");
                var id = IssueValidation.Text(checker.Id, "checker ID"); var registration = new Registration(checker);
                if (!Enum.IsDefined(typeof(IssueSubjectKind), registration.Kind) ||
                    (registration.Capabilities & ~(IssueCapabilities.Properties | IssueCapabilities.Hierarchy | IssueCapabilities.MechanicalGraph)) != 0)
                    throw new ArgumentException("Unknown subject kind or capability.");
                if (this.checkers.ContainsKey(id)) throw new ArgumentException("Duplicate checker ID: " + id);
                this.checkers.Add(id, registration);
            }
            foreach (var pair in this.checkers)
                foreach (var peer in IssuePrecedence.Group(pair.Key))
                    if (this.checkers.TryGetValue(peer, out var registration) && registration.Kind != pair.Value.Kind)
                        throw new ArgumentException("Precedence peers must evaluate the same subject kind.");
        }

        public Task<IssueResult<IssueScanReport>> RevalidateAsync(IssueStore store, IssueKey key, CancellationToken cancellationToken = default)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            var finding = store.GetFinding(key);
            if (!finding.Success) return Task.FromResult(IssueResult<IssueScanReport>.Fail("NOT_FOUND", "No current candidate for this key."));
            var objects = key.Subjects(false).ToArray(); var mates = key.Subjects(true).ToArray();
            IssueSubject? subject = objects.Length == 1 && mates.Length == 0 ? new IssueSubject(IssueSubjectKind.Object, objects[0])
                : mates.Length == 1 && objects.Length == 0 ? new IssueSubject(IssueSubjectKind.Mate, mates[0]) : null;
            if (subject == null || subject.Key(key.CheckerId, key.IssueType) != key)
                return Task.FromResult(IssueResult<IssueScanReport>.Fail("UNSUPPORTED_SUBJECT", "This milestone supports canonical single-object and single-mate subjects."));
            return ScanAsync(store, new[] { subject }, new[] { key.CheckerId }, cancellationToken);
        }

        public async Task<IssueResult<IssueScanReport>> ScanAsync(IssueStore store, IEnumerable<IssueSubject>? subjects = null,
            IEnumerable<string>? checkerIds = null, CancellationToken cancellationToken = default)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = store.Snapshot; long revision = store.CaptureRevision();
            var requestedSubjects = (subjects ?? snapshot.ComponentsById.Keys.Select(id => new IssueSubject(IssueSubjectKind.Object, id))
                .Concat(snapshot.MatesById.Keys.Select(id => new IssueSubject(IssueSubjectKind.Mate, id)))).ToArray();
            if (requestedSubjects.Any(s => s == null || (s.Kind == IssueSubjectKind.Object ? !snapshot.ComponentsById.ContainsKey(s.Id) : !snapshot.MatesById.ContainsKey(s.Id))))
                return IssueResult<IssueScanReport>.Fail("INVALID_SUBJECT", "Subject does not exist in this snapshot.");
            var ids = (checkerIds ?? checkers.Keys).ToArray();
            if (ids.Any(string.IsNullOrWhiteSpace)) return IssueResult<IssueScanReport>.Fail("INVALID_CHECKER", "Checker IDs must be non-empty.");
            var targets = new Dictionary<IssueKey, (string Checker, IssueSubject Subject)>();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
                foreach (var subject in requestedSubjects.Distinct())
                {
                    if (checkers.TryGetValue(id, out var registration) && registration.Kind != subject.Kind) continue;
                    foreach (var peer in IssuePrecedence.Group(id)) targets[subject.Key(peer, "$evaluation")] = (peer, subject);
                }
            var batch = new List<EvaluatedSubject>();
            foreach (var target in targets.Values.OrderBy(t => t.Checker, StringComparer.Ordinal).ThenBy(t => t.Subject.Kind).ThenBy(t => t.Subject.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SubjectCheckResult result; string? version = null;
                if (!checkers.TryGetValue(target.Checker, out var registration)) result = new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: "CHECKER_UNAVAILABLE");
                else
                {
                    version = registration.Version;
                    if (registration.Kind != target.Subject.Kind) result = new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: "UNSUPPORTED_SUBJECT_KIND");
                    else if (!CapabilitiesAvailable(snapshot, registration.Capabilities)) result = new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: "CAPABILITY_UNAVAILABLE_OR_INVALID");
                    else
                    {
                        try
                        {
                            result = await registration.Checker.EvaluateAsync(snapshot, target.Subject, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidOperationException("Checker returned null instead of subject evaluation coverage.");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            var diagnostic = DiagnosticLog.Report(ex, "checker." + target.Checker, snapshot.ProjectId, snapshot.SnapshotId, target.Subject.Id);
                            result = new SubjectCheckResult(SubjectEvaluationStatus.Failed, reason: "CHECKER_FAILED: " + ex.GetType().Name + "; diagnosticId=" + diagnostic.Entry.CorrelationId);
                        }
                    }
                }
                batch.Add(new EvaluatedSubject(new SubjectEvaluation(snapshot.ProjectId, snapshot.SnapshotId, target.Checker, version, target.Subject, result), result));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return store.ApplyScan(revision, batch);
        }

        private static bool CapabilitiesAvailable(ProjectSnapshot snapshot, IssueCapabilities required)
        {
            var caps = snapshot.Capabilities;
            return (!required.HasFlag(IssueCapabilities.Properties) || caps.Properties == CapabilityState.Available)
                && (!required.HasFlag(IssueCapabilities.Hierarchy) || caps.Hierarchy == CapabilityState.Available)
                && (!required.HasFlag(IssueCapabilities.MechanicalGraph) || caps.MechanicalGraph == CapabilityState.Available);
        }
    }
}
