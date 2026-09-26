using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Core.Primitives.DataStructures.MechanicalGraph;
using Graph = Core.Primitives.DataStructures.MechanicalGraph.MechanicalGraph;

namespace Core.Primitives.Operations.MechanicalGraph
{
    public sealed class GraphEdgeFilter
    {
        public bool IncludeSuppressed { get; set; }
        public HashSet<string>? AllowedMateTypes { get; set; }
        public HashSet<string>? AllowedMateStatuses { get; set; }
    }
    public sealed class GraphReturnSpec
    {
        public bool IncludeNodes { get; set; }
        public bool IncludeEdges { get; set; }
        public bool IncludeDepths { get; set; }
        public bool IncludeIssueIds { get; set; }
        public bool CountComponents { get; set; }
        public bool CountMates { get; set; }
        public bool SumMass { get; set; }
        public bool SumVolume { get; set; }
        public bool GroupMaterials { get; set; }
        public bool GroupMateTypes { get; set; }
        public bool GroupConstraintStates { get; set; }
        internal GraphReturnSpec Snapshot() => (GraphReturnSpec)MemberwiseClone();
    }
    public sealed class GraphQuery
    {
        public List<string> StartIds { get; set; } = new List<string>();
        public int? MaxDepth { get; set; }
        public bool IncludeSuppressedComponents { get; set; }
        public GraphEdgeFilter EdgeFilter { get; set; } = new GraphEdgeFilter();
        public GraphReturnSpec Return { get; set; } = new GraphReturnSpec();
    }

    public sealed class QuantityAggregate
    {
        private double sum;
        public string Unit { get; }
        public int KnownCount { get; private set; }
        public int MissingCount { get; private set; }
        public int InvalidCount { get; private set; }
        public int ExcludedAssemblyCount { get; internal set; }
        public bool Overflowed { get; private set; }
        public double? TotalKnown => Overflowed || KnownCount == 0 && MissingCount + InvalidCount > 0 ? (double?)null : sum;
        public bool Complete => MissingCount == 0 && InvalidCount == 0 && !Overflowed;
        internal QuantityAggregate(string unit) { Unit = unit; }
        internal void Add(GraphQuantity quantity)
        {
            if (quantity.State == ValueState.Missing) { MissingCount++; return; }
            if (quantity.State == ValueState.Invalid || quantity.Value == null || quantity.Unit != Unit) { InvalidCount++; return; }
            KnownCount++; sum += quantity.Value.Value; if (double.IsInfinity(sum) || double.IsNaN(sum)) Overflowed = true;
        }
    }
    public sealed class GroupCounts
    {
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> Counts { get; }
        public int UnknownCount { get; private set; }
        internal GroupCounts() { Counts = new ReadOnlyDictionary<string, int>(counts); }
        internal void Add(string? key)
        {
            if (key == null) { UnknownCount++; return; }
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }
    }
    public sealed class GraphAggregates
    {
        public int? ComponentCount { get; internal set; }
        public int? MateCount { get; internal set; }
        public QuantityAggregate? Mass { get; internal set; }
        public QuantityAggregate? Volume { get; internal set; }
        public GroupCounts? MaterialCounts { get; internal set; }
        public GroupCounts? MateTypeCounts { get; internal set; }
        public GroupCounts? ConstraintStatusCounts { get; internal set; }
    }
    public sealed class GraphQueryResult
    {
        public bool Success => Errors.Count == 0;
        public string? SnapshotId { get; internal set; }
        public IReadOnlyList<GraphDiagnostic> Errors { get; internal set; } = Array.Empty<GraphDiagnostic>();
        public IReadOnlyList<ComponentNode>? Nodes { get; internal set; }
        public IReadOnlyList<MateEdge>? Edges { get; internal set; }
        public IReadOnlyDictionary<string, int>? DepthByNodeId { get; internal set; }
        public IReadOnlyCollection<string>? IssueIds { get; internal set; }
        public IReadOnlyList<string> ExcludedStartIds { get; internal set; } = Array.Empty<string>();
        public int UnknownSuppressionNodeCount { get; internal set; }
        public int UnknownSuppressionMateCount { get; internal set; }
        public bool SuppressionInformationComplete => UnknownSuppressionNodeCount == 0 && UnknownSuppressionMateCount == 0;
        public GraphAggregates Aggregates { get; internal set; } = new GraphAggregates();
    }

    public static class TraverseMechanicalGraph
    {
        public static GraphQueryResult Traverse(Graph graph, GraphQuery query, CancellationToken cancellation = default)
        {
            var errors = new List<GraphDiagnostic>();
            if (graph == null) errors.Add(new GraphDiagnostic("GRAPH_NOT_AVAILABLE", "", "A successfully constructed available graph is required."));
            if (query == null || query.StartIds == null || query.StartIds.Count == 0)
                errors.Add(new GraphDiagnostic("INVALID_START_IDS", "/startIds", "At least one start component ID is required."));
            if (query != null && (query.MaxDepth < 0 || query.EdgeFilter == null || query.Return == null))
                errors.Add(new GraphDiagnostic("INVALID_QUERY", "", "Depth must be non-negative or null; filter and return specification are required."));
            if (errors.Count > 0) return new GraphQueryResult { Errors = errors.AsReadOnly() };
            var validQuery = query!;
            var starts = validQuery.StartIds!.ToArray();
            foreach (string start in starts)
                if (string.IsNullOrWhiteSpace(start) || !graph!.NodesById.ContainsKey(start))
                    errors.Add(new GraphDiagnostic("START_NOT_FOUND", "/startIds", "Unknown traversal start ID: " + start));
            var filter = validQuery.EdgeFilter!;
            if (filter.AllowedMateTypes?.Any(string.IsNullOrWhiteSpace) == true || filter.AllowedMateStatuses?.Any(string.IsNullOrWhiteSpace) == true)
                errors.Add(new GraphDiagnostic("INVALID_FILTER", "/edgeFilter", "Filter sets must contain non-empty strings."));
            if (errors.Count > 0) return new GraphQueryResult { Errors = errors.AsReadOnly() };
            var types = filter.AllowedMateTypes == null ? null : new HashSet<string>(filter.AllowedMateTypes, StringComparer.OrdinalIgnoreCase);
            var statuses = filter.AllowedMateStatuses == null ? null : new HashSet<string>(filter.AllowedMateStatuses, StringComparer.OrdinalIgnoreCase);
            bool includeNodesSuppressed = validQuery.IncludeSuppressedComponents, includeEdgesSuppressed = filter.IncludeSuppressed;
            int? maxDepth = validQuery.MaxDepth; var spec = validQuery.Return!.Snapshot();
            cancellation.ThrowIfCancellationRequested();
            bool EdgeAllowed(MateEdge edge) => edge.Suppressed.HasValue && (includeEdgesSuppressed || edge.Suppressed == false)
                && (types == null || edge.Type != null && types.Contains(edge.Type))
                && (statuses == null || edge.Status != null && statuses.Contains(edge.Status));

            var result = new GraphQueryResult { SnapshotId = graph!.SnapshotId };
            result.UnknownSuppressionNodeCount = graph.NodesById.Values.Count(n => n.Suppressed == null);
            result.UnknownSuppressionMateCount = graph.MatesById.Values.Count(e => e.Suppressed == null);
            var aggregate = result.Aggregates;
            if (spec.SumMass) aggregate.Mass = new QuantityAggregate("kg");
            if (spec.SumVolume) aggregate.Volume = new QuantityAggregate("m^3");
            if (spec.GroupMaterials) aggregate.MaterialCounts = new GroupCounts();
            if (spec.GroupMateTypes) aggregate.MateTypeCounts = new GroupCounts();
            if (spec.GroupConstraintStates) aggregate.ConstraintStatusCounts = new GroupCounts();
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            var visitedMates = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(); var excluded = new List<string>();
            var collectedNodes = spec.IncludeNodes ? new List<ComponentNode>() : null;
            var collectedEdges = spec.IncludeEdges ? new List<MateEdge>() : null;
            var issues = spec.IncludeIssueIds ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (string start in starts.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (graph.NodesById[start].Suppressed == null || !includeNodesSuppressed && graph.NodesById[start].Suppressed == true) { excluded.Add(start); continue; }
                depths.Add(start, 0); queue.Enqueue(start);
            }
            while (queue.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string id = queue.Dequeue(); var node = graph.NodesById[id]; int depth = depths[id];
                collectedNodes?.Add(node);
                if (issues != null) issues.UnionWith(node.IssueIds);
                aggregate.MaterialCounts?.Add(node.Material); aggregate.ConstraintStatusCounts?.Add(node.ConstraintStatus);
                if (node.Type == "part") { aggregate.Mass?.Add(node.Mass); aggregate.Volume?.Add(node.Volume); }
                else
                {
                    if (aggregate.Mass != null) aggregate.Mass.ExcludedAssemblyCount++;
                    if (aggregate.Volume != null) aggregate.Volume.ExcludedAssemblyCount++;
                }
                foreach (var edge in node.Edges.OrderBy(e => e.GetOtherEndpoint(id), StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (visitedMates.Contains(edge.Id) || !EdgeAllowed(edge)) continue;
                    string otherId = edge.GetOtherEndpoint(id); var other = graph.NodesById[otherId];
                    if (other.Suppressed == null || !includeNodesSuppressed && other.Suppressed == true) continue;
                    if (!depths.ContainsKey(otherId) && (maxDepth == null || depth < maxDepth))
                    { depths.Add(otherId, depth + 1); queue.Enqueue(otherId); }
                    // All nodes at a boundary depth are discovered before any boundary node is expanded.
                    // Include every eligible edge between reached vertices, not just BFS tree edges.
                    if (!depths.ContainsKey(otherId)) continue;
                    visitedMates.Add(edge.Id); collectedEdges?.Add(edge);
                    if (issues != null) issues.UnionWith(edge.IssueIds);
                    aggregate.MateTypeCounts?.Add(edge.Type);
                }
            }
            if (spec.CountComponents) aggregate.ComponentCount = depths.Count;
            if (spec.CountMates) aggregate.MateCount = visitedMates.Count;
            result.Nodes = collectedNodes?.AsReadOnly(); result.Edges = collectedEdges?.AsReadOnly();
            result.DepthByNodeId = spec.IncludeDepths ? new ReadOnlyDictionary<string, int>(depths) : null;
            result.IssueIds = issues?.OrderBy(s => s, StringComparer.Ordinal).ToList().AsReadOnly();
            result.ExcludedStartIds = excluded.AsReadOnly();
            return result;
        }
    }
}
