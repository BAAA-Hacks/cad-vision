using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;

namespace Core.Primitives.Operations.Issues
{
    public interface IIssueChecker
    {
        string Id { get; }
        string Version { get; }
        IssueCapabilities RequiredCapabilities { get; }
        Task<IssueCheckResult> EvaluateAsync(IssueFinding previous, ProjectSnapshot snapshot, CancellationToken cancellationToken);
    }
    public sealed class IssueCheckerRegistry
    {
        private readonly Dictionary<string, IIssueChecker> checkers = new Dictionary<string, IIssueChecker>(StringComparer.Ordinal);
        public IssueCheckerRegistry(IEnumerable<IIssueChecker> checkers)
        {
            foreach (var checker in checkers ?? throw new ArgumentNullException(nameof(checkers)))
            {
                if (checker == null) throw new ArgumentException("Null checker.");
                IssueValidation.Text(checker.Id, "checker ID"); IssueValidation.Text(checker.Version, "checker version");
                if ((checker.RequiredCapabilities & ~(IssueCapabilities.Properties | IssueCapabilities.Hierarchy | IssueCapabilities.MechanicalGraph)) != 0) throw new ArgumentException("Unknown checker capability.");
                if (this.checkers.ContainsKey(checker.Id)) throw new ArgumentException("Duplicate checker ID: " + checker.Id);
                this.checkers.Add(checker.Id, checker);
            }
        }
        internal bool TryGet(string id, out IIssueChecker checker) => checkers.TryGetValue(id, out checker!);
    }
    public static class RevalidateIssue
    {
        public static async Task<IssueResult<IssueCheckResult>> RunAsync(IssueStore store, IssueKey key, IssueCheckerRegistry registry, CancellationToken cancellationToken = default)
        {
            if (store == null || registry == null) throw new ArgumentNullException(store == null ? nameof(store) : nameof(registry));
            cancellationToken.ThrowIfCancellationRequested();
            var captured = store.Capture(key);
            if (captured.Finding == null) return IssueResult<IssueCheckResult>.Fail("NOT_FOUND", "No current finding.");
            if (store.IsScanManaged(key) || IssuePrecedence.Group(key.CheckerId).Count > 1)
                return IssueResult<IssueCheckResult>.Fail("USE_ISSUE_ENGINE", "Use IssueEngine.RevalidateAsync for subject coverage and precedence-group revalidation.");
            IssueResult<IssueCheckResult> Unable(string reason) => IssueResult<IssueCheckResult>.Ok(new IssueCheckResult(RevalidationOutcome.UnableToEvaluate, reason: reason));
            if (!registry.TryGet(key.CheckerId, out var checker)) return Unable("CHECKER_UNAVAILABLE");
            var caps = store.Snapshot.Capabilities; var required = checker.RequiredCapabilities;
            if (required.HasFlag(IssueCapabilities.Properties) && caps.Properties != CapabilityState.Available ||
                required.HasFlag(IssueCapabilities.Hierarchy) && caps.Hierarchy != CapabilityState.Available ||
                required.HasFlag(IssueCapabilities.MechanicalGraph) && caps.MechanicalGraph != CapabilityState.Available) return Unable("CAPABILITY_UNAVAILABLE_OR_INVALID");
            string version = checker.Version;
            IssueCheckResult result;
            try { result = await checker.EvaluateAsync(captured.Finding, store.Snapshot, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Unable("CHECKER_FAILED: " + ex.GetType().Name); }
            cancellationToken.ThrowIfCancellationRequested();
            if (result == null) return Unable("CHECKER_RETURNED_NULL");
            return store.Apply(key, captured.Revision, result, version);
        }
    }
}
