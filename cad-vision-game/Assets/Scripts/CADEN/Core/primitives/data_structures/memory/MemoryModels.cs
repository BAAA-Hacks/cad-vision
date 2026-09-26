#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.DataStructures.Memory
{
    public enum MemoryLifecycle { Active, Retired }
    public enum MemoryProvenance { UserEstablished, AssistantInferred, Imported, System }
    public enum MemoryReferenceState { Active, StaleReference, UntrustedIdentity }

    // Constructed by the trusted host, never deserialized from model arguments.
    public sealed class ProjectAssociation
    {
        public string ProjectId { get; }
        public string? SourceSystem { get; }
        public string? TrustedSourceProjectId { get; }
        public ProjectAssociation(string projectId, string? sourceSystem = null, string? trustedSourceProjectId = null)
        {
            ProjectId = Text(projectId);
            if ((sourceSystem == null) != (trustedSourceProjectId == null)) throw new ArgumentException("Source system and trusted source ID must be supplied together.");
            SourceSystem = sourceSystem == null ? null : Text(sourceSystem); TrustedSourceProjectId = trustedSourceProjectId == null ? null : Text(trustedSourceProjectId);
        }
        internal static string Text(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 ? value : throw new ArgumentException("Expected non-empty text of at most 512 characters.");
    }
    public sealed class MemoryWriteContext
    {
        public string OperationId { get; }
        public long ExpectedRevision { get; }
        public MemoryProvenance Provenance { get; }
        public string Actor { get; }
        public bool AllowUserEstablishedOverride { get; }
        public MemoryWriteContext(string operationId, long expectedRevision, MemoryProvenance provenance, string actor, bool allowUserEstablishedOverride = false)
        {
            OperationId = ProjectAssociation.Text(operationId); Actor = ProjectAssociation.Text(actor);
            if (expectedRevision < 0 || !Enum.IsDefined(typeof(MemoryProvenance), provenance)) throw new ArgumentException("Invalid mutation context.");
            ExpectedRevision = expectedRevision; Provenance = provenance; AllowUserEstablishedOverride = allowUserEstablishedOverride;
        }
    }
    public sealed class MemoryMutation
    {
        private readonly JToken value;
        public string Type { get; }
        public string Key { get; }
        public string? ObjectId { get; }
        public MemoryLifecycle Lifecycle { get; }
        public JToken Value => value.DeepClone();
        public MemoryMutation(string type, string key, JToken value, string? objectId = null, MemoryLifecycle lifecycle = MemoryLifecycle.Active)
        {
            Type = ProjectAssociation.Text(type); Key = ProjectAssociation.Text(key); ObjectId = objectId == null ? null : ProjectAssociation.Text(objectId);
            if (!Enum.IsDefined(typeof(MemoryLifecycle), lifecycle)) throw new ArgumentException("Unknown lifecycle.");
            this.value = (value ?? throw new ArgumentNullException(nameof(value))).DeepClone(); Lifecycle = lifecycle;
        }
    }
    public sealed class RequirementMutation
    {
        private readonly JToken value;
        public string RequirementId { get; }
        public string Type { get; }
        public IReadOnlyList<string> ObjectIds { get; }
        public MemoryLifecycle Lifecycle { get; }
        public JToken Value => value.DeepClone();
        // Empty objectIds is explicitly project scope.
        public RequirementMutation(string requirementId, string type, JToken value, IEnumerable<string> objectIds, MemoryLifecycle lifecycle = MemoryLifecycle.Active)
        {
            RequirementId = ProjectAssociation.Text(requirementId); Type = ProjectAssociation.Text(type);
            ObjectIds = Core.Primitives.DataStructures.Issues.IssueValidation.Ids(objectIds);
            if (!Enum.IsDefined(typeof(MemoryLifecycle), lifecycle)) throw new ArgumentException("Unknown lifecycle.");
            this.value = (value ?? throw new ArgumentNullException(nameof(value))).DeepClone(); Lifecycle = lifecycle;
        }
    }
    public sealed class MemoryPage
    {
        private readonly JArray records;
        public JArray Records => (JArray)records.DeepClone();
        public int Total { get; }
        public int Offset { get; }
        public int? NextOffset { get; }
        internal MemoryPage(JArray records, int total, int offset) { this.records = (JArray)records.DeepClone(); Total = total; Offset = offset; NextOffset = offset + records.Count < total ? offset + records.Count : (int?)null; }
    }
    public sealed class MemoryReceipt
    {
        public string OperationId { get; }
        public string RecordId { get; }
        public long Revision { get; }
        public bool Replayed { get; }
        internal MemoryReceipt(string operationId, string recordId, long revision, bool replayed) { OperationId = operationId; RecordId = recordId; Revision = revision; Replayed = replayed; }
    }
    public sealed class MemoryResult<T> where T : class
    {
        public bool Success => ErrorCode == null;
        public T? Value { get; }
        public string? ErrorCode { get; }
        public string? Message { get; }
        private MemoryResult(T? value, string? code, string? message) { Value = value; ErrorCode = code; Message = message; }
        public static MemoryResult<T> Ok(T? value) => new MemoryResult<T>(value, null, null);
        public static MemoryResult<T> Fail(string code, string message) => new MemoryResult<T>(null, code, message);
    }
}
