using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.DataStructures.Project
{
    public enum CapabilityState { Available, Unavailable, Invalid }
    public enum AvailabilityState { Available, Missing, Invalid, NotApplicable }
    public enum IdentityScope { SnapshotOnly, ProjectStable }
    public enum DiagnosticScope { Project, Properties, Hierarchy, MechanicalGraph }

    public sealed class LoadDiagnostic
    {
        public string Code { get; }
        public string Path { get; }
        public string Message { get; }
        public DiagnosticScope Scope { get; }
        public bool IsError { get; }
        public bool Fatal { get; }
        internal LoadDiagnostic(string code, string path, string message, DiagnosticScope scope, bool error, bool fatal = false)
        { Code = code; Path = path; Message = message; Scope = scope; IsError = error; Fatal = fatal; }
    }

    public sealed class ValueProvenance
    {
        public string ProjectId { get; }
        public string SnapshotId { get; }
        public string SourceField { get; }
        public bool IsFixture { get; }
        public IdentityScope IdentityScope { get; }
        internal ValueProvenance(string project, string snapshot, string source, bool fixture, IdentityScope identity)
        { ProjectId = project; SnapshotId = snapshot; SourceField = source; IsFixture = fixture; IdentityScope = identity; }
    }

    public sealed class MetadataValue
    {
        private readonly JToken? value, rawValue;
        public AvailabilityState State { get; }
        public bool WasPresent { get; }
        public string Reason { get; }
        public string ExpectedFormat { get; }
        public string? Unit { get; }
        public string? CoordinateFrame { get; }
        public ValueProvenance Provenance { get; }
        public JToken? Value => value?.DeepClone();
        public JToken? RawValue => rawValue?.DeepClone();
        private readonly JObject? sourceEvidence;
        public JObject? SourceEvidence => (JObject?)sourceEvidence?.DeepClone();
        public string? ReasonCode { get; }
        private readonly JObject? spatialReference;
        public JObject? SpatialReference => (JObject?)spatialReference?.DeepClone();
        internal MetadataValue(AvailabilityState state, JToken? value, JToken? raw, bool present, string reason,
            string format, string? unit, string? frame, ValueProvenance provenance, JObject? sourceEvidence = null, string? reasonCode = null, JObject? spatialReference = null)
        { State = state; this.value = value?.DeepClone(); rawValue = raw?.DeepClone(); WasPresent = present;
            Reason = reason; ReasonCode = reasonCode; ExpectedFormat = format; Unit = unit; CoordinateFrame = frame; Provenance = provenance; this.sourceEvidence = (JObject?)sourceEvidence?.DeepClone(); this.spatialReference = (JObject?)spatialReference?.DeepClone(); }
    }

    public sealed class ComponentMetadata
    {
        private readonly JObject raw;
        public string Id { get; }
        public string Name { get; }
        public string Type { get; }
        public IReadOnlyDictionary<string, MetadataValue> Properties { get; }
        public JObject CopyRawRecord() => (JObject)raw.DeepClone();
        internal ComponentMetadata(string id, string name, string type, JObject raw, Dictionary<string, MetadataValue> properties)
        { Id = id; Name = name; Type = type; this.raw = (JObject)raw.DeepClone(); Properties = new ReadOnlyDictionary<string, MetadataValue>(properties); }
    }

    public sealed class MateMetadata
    {
        private readonly JObject raw;
        public string Id { get; }
        public string ObjectAId { get; }
        public string ObjectBId { get; }
        public ValueProvenance Provenance { get; }
        public JObject CopyRawRecord() => (JObject)raw.DeepClone();
        internal MateMetadata(string id, string a, string b, JObject raw, ValueProvenance provenance)
        { Id = id; ObjectAId = a; ObjectBId = b; this.raw = (JObject)raw.DeepClone(); Provenance = provenance; }
    }

    public sealed class ProjectCapabilities
    {
        public CapabilityState Properties => CapabilityState.Available;
        public CapabilityState Hierarchy { get; }
        public CapabilityState MechanicalGraph { get; }
        // Readiness of input data, not a claim that the deferred issue engine exists.
        public CapabilityState MateAnalysisInput => MechanicalGraph;
        internal ProjectCapabilities(CapabilityState hierarchy, CapabilityState graph) { Hierarchy = hierarchy; MechanicalGraph = graph; }
    }

    public sealed class ProjectSnapshot
    {
        private readonly JObject document;
        public ProjectIndexes Indexes { get; }
        public string ProjectId { get; }
        public string SnapshotId { get; }
        public string Name { get; }
        public string? RevisionId { get; }
        public bool IsFixture { get; }
        public IdentityScope IdentityScope { get; }
        public ProjectCapabilities Capabilities { get; }
        public IReadOnlyDictionary<string, ComponentMetadata> ComponentsById { get; }
        // Empty when mate topology is invalid; raw rejected records remain in CopyRawExport.
        public IReadOnlyDictionary<string, MateMetadata> MatesById { get; }
        public IReadOnlyList<LoadDiagnostic> LoadDiagnostics { get; }
        public IReadOnlyList<Core.Primitives.DataStructures.MechanicalGraph.MechanicalScope> MechanicalScopes { get; }
        public JObject CopyProjectMetadata() => (JObject)document["project"]!.DeepClone();
        public JObject CopyRawExport() => (JObject)document.DeepClone();
        public string SchemaVersion => (string)document["schemaVersion"]!;
        public JObject CopyExportContext() => new JObject { ["schemaVersion"] = SchemaVersion,
            ["extractionStatus"] = document["extractionStatus"]?.DeepClone(), ["coordinateSystemDeclaration"] = document["coordinateSystem"]?.DeepClone(),
            ["documentUnits"] = document["project"]?["documentUnits"]?.DeepClone(), ["units"] = document["project"]?["units"]?.DeepClone(),
            ["mappingStatus"] = document["mappingStatus"]?.DeepClone(), ["warnings"] = document["warnings"]?.DeepClone() };
        internal ProjectSnapshot(JObject document, string projectId, string snapshotId, string name, string? revisionId, bool fixture,
            IdentityScope identityScope, ProjectCapabilities capabilities, Dictionary<string, ComponentMetadata> components,
            Dictionary<string, MateMetadata> mates, List<LoadDiagnostic> diagnostics,
            IReadOnlyList<Core.Primitives.DataStructures.MechanicalGraph.MechanicalScope> mechanicalScopes, PrecomputeOptions options)
        {
            this.document = (JObject)document.DeepClone(); ProjectId = projectId; SnapshotId = snapshotId; Name = name;
            RevisionId = revisionId; IsFixture = fixture; IdentityScope = identityScope; Capabilities = capabilities;
            ComponentsById = new ReadOnlyDictionary<string, ComponentMetadata>(components);
            MatesById = new ReadOnlyDictionary<string, MateMetadata>(mates); LoadDiagnostics = diagnostics.AsReadOnly();
            MechanicalScopes = mechanicalScopes;
            Indexes = new ProjectIndexes(this, options);
            foreach (var scope in mechanicalScopes) if (scope.Graph != null) Core.Primitives.Operations.MechanicalGraph.MechanicalQueries.Prepare(scope.Graph);
        }
    }

    public sealed class ProjectLoadResult
    {
        public bool Success => Snapshot != null;
        public ProjectSnapshot? Snapshot { get; }
        public IReadOnlyList<LoadDiagnostic> Diagnostics { get; }
        internal ProjectLoadResult(ProjectSnapshot? snapshot, List<LoadDiagnostic> diagnostics) { Snapshot = snapshot; Diagnostics = diagnostics.AsReadOnly(); }
    }
}
