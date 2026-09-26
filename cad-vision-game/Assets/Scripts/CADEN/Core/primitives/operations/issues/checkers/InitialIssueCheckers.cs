#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.Issues.Checkers
{
    public static class InitialIssueCheckers
    {
        public static IssueEngine CreateEngine(ProjectSnapshot? snapshot = null) => new IssueEngine(new ISubjectIssueChecker[]
        { new MissingMaterialChecker(), new UnsolvableConstraintChecker(), new OverDefinedConstraintChecker(), new UnderDefinedComponentChecker() }
            .Concat((snapshot?.MechanicalScopes ?? Array.Empty<Core.Primitives.DataStructures.MechanicalGraph.MechanicalScope>())
                .GroupBy(s => (s.ScopeAssemblyId, s.Configuration)).SelectMany(g => new ISubjectIssueChecker[] {
                    new MechanicalIssueChecker(g.Key.ScopeAssemblyId, g.Key.Configuration, false), new MechanicalIssueChecker(g.Key.ScopeAssemblyId, g.Key.Configuration, true) })));
    }

    public abstract class ComponentIssueChecker : ISubjectIssueChecker
    {
        public abstract string Id { get; }
        public string Version => "1";
        public IssueSubjectKind SubjectKind => IssueSubjectKind.Object;
        public IssueCapabilities RequiredCapabilities => IssueCapabilities.Properties;
        protected abstract string IssueType { get; }
        protected abstract IssueSeverity Severity { get; }
        protected virtual bool PartsOnly => false;
        protected virtual bool RequiresDesignIntent => false;
        protected abstract string RequiredField { get; }
        protected abstract bool Matches(JToken value);
        public Task<SubjectCheckResult> EvaluateAsync(ProjectSnapshot snapshot, IssueSubject subject, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubjectCheckResult Empty(string reason) => new SubjectCheckResult(SubjectEvaluationStatus.Complete, reason: reason);
            SubjectCheckResult Unable(string reason) => new SubjectCheckResult(SubjectEvaluationStatus.UnableToEvaluate, reason: reason);
            if (subject.Kind != IssueSubjectKind.Object || !snapshot.ComponentsById.TryGetValue(subject.Id, out var component))
                return Task.FromResult(Unable("UNKNOWN_COMPONENT"));
            if (PartsOnly && component.Type != "part") return Task.FromResult(Empty("NOT_APPLICABLE_TO_OBJECT_TYPE"));
            var suppression = component.Properties["suppressed"];
            if (suppression.State != AvailabilityState.Available) return Task.FromResult(Unable("SUPPRESSION_UNKNOWN: " + suppression.State));
            if ((bool)suppression.Value!) return Task.FromResult(Empty("EXPLICITLY_SUPPRESSED"));
            var field = component.Properties[RequiredField];
            if (field.State != AvailabilityState.Available) return Task.FromResult(Unable(RequiredField + ": " + field.State + "; " + field.Reason));
            if (!Matches(field.Value!)) return Task.FromResult(Empty("CONDITION_NOT_PRESENT"));
            var evidence = new JObject
            {
                [RequiredField] = Evidence(field), ["suppressed"] = Evidence(suppression),
                ["sourceDocument"] = Evidence(component.Properties["sourceDocument"]),
                ["configuration"] = Evidence(component.Properties["configuration"])
            };
            if (RequiredField == "definitionStatus")
            {
                evidence["fixed"] = Evidence(component.Properties["fixed"]);
                evidence["remainingDOF"] = Evidence(component.Properties["remainingDOF"]);
                // Graph availability does not prove coverage or an exact active-mate set.
                evidence["activeMates"] = new JObject { ["state"] = "Unavailable", ["reason"] = "Scoped mate coverage and active-set contract are not established." };
            }
            var finding = new IssueFinding(subject.Key(Id, IssueType), Version, snapshot.ProjectId, snapshot.SnapshotId,
                Severity, component.Type == "assembly" ? IssueScope.Assembly : IssueScope.Object,
                new[] { component.Id }, Array.Empty<string>(), evidence, RequiresDesignIntent);
            return Task.FromResult(new SubjectCheckResult(SubjectEvaluationStatus.Complete, new[] { finding }));
        }
        private static JObject Evidence(MetadataValue value) => new JObject
        {
            ["state"] = value.State.ToString(), ["value"] = value.Value, ["unit"] = value.Unit,
            ["coordinateFrame"] = value.CoordinateFrame, ["reason"] = value.Reason,
            // Snapshot provenance lives on the finding; array-position source paths are not material evidence.
            ["fixture"] = value.Provenance.IsFixture
        };
    }

    public sealed class MissingMaterialChecker : ComponentIssueChecker
    {
        public override string Id => "material.missing";
        protected override string IssueType => "missing";
        protected override IssueSeverity Severity => IssueSeverity.Warning;
        protected override bool PartsOnly => true;
        protected override string RequiredField => "material";
        protected override bool Matches(JToken value) => value["assigned"]?.Type == JTokenType.Boolean && !(bool)value["assigned"]!;
    }
    public sealed class UnsolvableConstraintChecker : ComponentIssueChecker
    {
        public override string Id => "constraint.unsolvable";
        protected override string IssueType => "unsolvable";
        protected override IssueSeverity Severity => IssueSeverity.Error;
        protected override string RequiredField => "definitionStatus";
        protected override bool Matches(JToken value) => (string?)value == "no_solution" || (string?)value == "invalid_solution";
    }
    public sealed class OverDefinedConstraintChecker : ComponentIssueChecker
    {
        public override string Id => "constraint.over_defined";
        protected override string IssueType => "over_defined";
        protected override IssueSeverity Severity => IssueSeverity.Warning;
        protected override string RequiredField => "definitionStatus";
        protected override bool Matches(JToken value) => (string?)value == "over_defined";
    }
    public sealed class UnderDefinedComponentChecker : ComponentIssueChecker
    {
        public override string Id => "constraint.under_defined";
        protected override string IssueType => "under_defined";
        protected override IssueSeverity Severity => IssueSeverity.Question;
        protected override bool RequiresDesignIntent => true;
        protected override string RequiredField => "definitionStatus";
        protected override bool Matches(JToken value) => (string?)value == "under_defined";
    }
}
