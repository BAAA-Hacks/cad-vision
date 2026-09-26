#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.Operations.MechanicalGraph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.Issues.Checkers
{
    // A checker registration addresses exactly one explicit assembly/configuration scope.
    public sealed class MechanicalIssueChecker : ISubjectIssueChecker
    {
        private readonly string assembly, configuration;
        private readonly bool islands;
        private sealed class IslandIndex
        {
            public bool Complete { get; }
            public IReadOnlyList<string> Reference { get; }
            public Dictionary<string, IReadOnlyList<string>> Others { get; }
            public IslandIndex(MechanicalScope scope, CancellationToken token)
            {
                Complete = MechanicalQueries.Complete(scope);
                var components = Complete ? MechanicalQueries.Islands(scope, token) : Array.Empty<IReadOnlyList<string>>();
                Reference = components.Count > 0 ? components[0] : Array.Empty<string>();
                Others = components.Skip(1).ToDictionary(c => c[0], StringComparer.Ordinal);
            }
        }
        private readonly ConditionalWeakTable<MechanicalScope, IslandIndex> islandIndexes = new ConditionalWeakTable<MechanicalScope, IslandIndex>();
        public string Id { get; }
        public string Version => "1";
        public IssueSubjectKind SubjectKind => IssueSubjectKind.Object;
        public IssueCapabilities RequiredCapabilities => IssueCapabilities.MechanicalGraph;
        public MechanicalIssueChecker(string scopeAssemblyId, string configuration, bool islands)
        {
            assembly = scopeAssemblyId; this.configuration = configuration; this.islands = islands;
            using var hash = SHA256.Create();
            var key = new JArray(assembly, configuration).ToString(Formatting.None);
            Id = (islands ? "mechanical.island." : "mechanical.unmated.") + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
        }
        public Task<SubjectCheckResult> EvaluateAsync(ProjectSnapshot snapshot, IssueSubject subject, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubjectCheckResult Empty(string reason) => new SubjectCheckResult(SubjectEvaluationStatus.Complete, reason: reason);
            SubjectCheckResult Unable(string reason) => new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: reason);
            var scope = snapshot.MechanicalScopes.FirstOrDefault(s => s.ScopeAssemblyId == assembly && s.Configuration == configuration);
            if (scope?.State != GraphDataState.Available || scope.Graph == null) return Task.FromResult(Unable("GRAPH_CAPABILITY_UNAVAILABLE_OR_INVALID"));
            var graph = scope.Graph;
            if (!graph.NodesById.TryGetValue(subject.Id, out var node)) return Task.FromResult(scope.MembershipCoverage == MechanicalCoverage.Complete ? Empty("OUTSIDE_MECHANICAL_SCOPE") : Unable("MEMBERSHIP_INCOMPLETE"));
            if (node.Suppressed == true) return Task.FromResult(Empty("EXPLICITLY_SUPPRESSED"));
            if (node.Suppressed == null) return Task.FromResult(Unable("SUPPRESSION_UNKNOWN"));
            if (scope.MateCoverage != MechanicalCoverage.Complete || scope.MembershipCoverage != MechanicalCoverage.Complete) return Task.FromResult(Unable("CONNECTIVITY_COVERAGE_INCOMPLETE"));
            string[] affected;
            var evidence = new JObject { ["scopeAssemblyId"] = assembly, ["configuration"] = configuration, ["source"] = scope.Source,
                ["mateCoverage"] = scope.MateCoverage.ToString(), ["membershipCoverage"] = scope.MembershipCoverage.ToString() };
            if (islands)
            {
                var index = islandIndexes.GetValue(scope, s => new IslandIndex(s, cancellationToken));
                if (!index.Complete) return Task.FromResult(Unable("ACTIVE_SET_UNKNOWN"));
                if (!index.Others.TryGetValue(subject.Id, out var island)) return Task.FromResult(Empty("NOT_NONREFERENCE_ISLAND_REPRESENTATIVE"));
                affected = island.ToArray(); evidence["memberIds"] = new JArray(affected); evidence["referenceIslandIds"] = new JArray(index.Reference);
            }
            else
            {
                if (node.Type != "part" || node.Fixed == true) return Task.FromResult(Empty("NOT_UNMATED_CANDIDATE"));
                if (node.Fixed == null) return Task.FromResult(Unable("FIXED_STATE_UNKNOWN"));
                if (node.Edges.Any(e => MechanicalQueries.Unknown(e, graph))) return Task.FromResult(Unable("INCIDENT_ACTIVE_SET_UNKNOWN"));
                int degree = node.Edges.Count(e => MechanicalQueries.Active(e, graph));
                if (degree != 0) return Task.FromResult(Empty("HAS_CONFIRMED_ACTIVE_MATES"));
                affected = new[] { subject.Id }; evidence["fixed"] = false; evidence["suppressed"] = false; evidence["activeIncidentMateCount"] = 0;
            }
            var finding = new IssueFinding(subject.Key(Id, islands ? "disconnected_island" : "unmated"), Version, snapshot.ProjectId, snapshot.SnapshotId,
                IssueSeverity.Question, affected.Length == 1 ? IssueScope.Object : IssueScope.MultiObject, affected, Array.Empty<string>(), evidence, requiresDesignIntent: true);
            return Task.FromResult(new SubjectCheckResult(SubjectEvaluationStatus.Complete, new[] { finding }));
        }
    }
}
