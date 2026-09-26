using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Core.Primitives.DataStructures.MechanicalGraph
{
    public enum GraphDataState { Available, Unavailable, Invalid }
    public enum ValueState { Available, Missing, Invalid }

    public sealed class GraphDiagnostic
    {
        public string Code { get; }
        public string Path { get; }
        public string Message { get; }
        public GraphDiagnostic(string code, string path, string message) { Code = code; Path = path; Message = message; }
    }

    public sealed class GraphQuantity
    {
        public double? Value { get; }
        public ValueState State { get; }
        public string? Unit { get; }
        public string? Reason { get; }
        internal GraphQuantity(double? value, ValueState state, string? unit, string? reason = null)
        { Value = value; State = state; Unit = unit; Reason = reason; }
    }

    public sealed class GraphVector3
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public string? CoordinateFrame { get; }
        public string? Unit { get; }
        internal GraphVector3(double x, double y, double z, string? frame, string? unit)
        { X = x; Y = y; Z = z; CoordinateFrame = frame; Unit = unit; }
    }

    public sealed class MateReference
    {
        public string ComponentId { get; }
        public string? EntityType { get; }
        public string? EntityId { get; }
        internal MateReference(string componentId, string? entityType, string? entityId)
        { ComponentId = componentId; EntityType = entityType; EntityId = entityId; }
    }

    public sealed class MateLimits
    {
        public bool? Enabled { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public string? Unit { get; }
        internal MateLimits(bool? enabled, double? minimum, double? maximum, string? unit)
        { Enabled = enabled; Minimum = minimum; Maximum = maximum; Unit = unit; }
    }

    public sealed class ComponentNode
    {
        private readonly List<MateEdge> edges = new List<MateEdge>();
        public string Id { get; }
        public string Name { get; }
        public string Type { get; }
        public string? ParentId { get; }
        public GraphQuantity Mass { get; }
        public GraphQuantity Volume { get; }
        public string? Material { get; }
        public string? ConstraintStatus { get; }
        public bool? Fixed { get; }
        public bool? Suppressed { get; }
        public IReadOnlyList<string> IssueIds { get; }
        public IReadOnlyList<MateEdge> Edges { get; }
        internal ComponentNode(string id, string name, string type, string? parentId, GraphQuantity mass,
            GraphQuantity volume, string? material, string? constraintStatus, bool? fixedState, bool? suppressed, List<string> issues)
        {
            Id = id; Name = name; Type = type; ParentId = parentId; Mass = mass; Volume = volume;
            Material = material; ConstraintStatus = constraintStatus; Fixed = fixedState; Suppressed = suppressed;
            IssueIds = issues.AsReadOnly(); Edges = edges.AsReadOnly();
        }
        internal void Attach(MateEdge edge) => edges.Add(edge);
    }

    public sealed class MateEdge
    {
        public string Id { get; }
        public string ObjectAId { get; }
        public string ObjectBId { get; }
        public string? Type { get; }
        public string? Status { get; }
        public bool? Suppressed { get; }
        public IReadOnlyList<MateReference> References { get; }
        public GraphVector3? Axis { get; }
        public bool? LockRotation { get; }
        public MateLimits? Limits { get; }
        public IReadOnlyList<string> IssueIds { get; }
        internal MateEdge(string id, string a, string b, string? type, string? status, bool? suppressed,
            List<MateReference> references, GraphVector3? axis, bool? lockRotation, MateLimits? limits, List<string> issues)
        {
            Id = id; ObjectAId = a; ObjectBId = b; Type = type; Status = status; Suppressed = suppressed;
            References = references.AsReadOnly(); Axis = axis; LockRotation = lockRotation; Limits = limits; IssueIds = issues.AsReadOnly();
        }
        public string GetOtherEndpoint(string currentObjectId)
        {
            if (currentObjectId == ObjectAId) return ObjectBId;
            if (currentObjectId == ObjectBId) return ObjectAId;
            throw new ArgumentException("The component is not an endpoint of this mate.", nameof(currentObjectId));
        }
    }

    public sealed class MechanicalGraph
    {
        public string SnapshotId { get; } = Guid.NewGuid().ToString("N");
        public IReadOnlyDictionary<string, ComponentNode> NodesById { get; }
        public IReadOnlyDictionary<string, MateEdge> MatesById { get; }
        internal MechanicalGraph(Dictionary<string, ComponentNode> nodes, Dictionary<string, MateEdge> mates)
        { NodesById = new ReadOnlyDictionary<string, ComponentNode>(nodes); MatesById = new ReadOnlyDictionary<string, MateEdge>(mates); }
    }

    public sealed class GraphBuildResult
    {
        public GraphDataState State { get; }
        public MechanicalGraph? Graph { get; }
        public IReadOnlyList<GraphDiagnostic> Errors { get; }
        public IReadOnlyList<GraphDiagnostic> Warnings { get; }
        internal GraphBuildResult(GraphDataState state, MechanicalGraph? graph, List<GraphDiagnostic> errors, List<GraphDiagnostic> warnings)
        { State = state; Graph = graph; Errors = errors.AsReadOnly(); Warnings = warnings.AsReadOnly(); }
    }
}
