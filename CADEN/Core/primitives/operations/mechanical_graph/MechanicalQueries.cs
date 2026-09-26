using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Core.Primitives.DataStructures.MechanicalGraph;

namespace Core.Primitives.Operations.MechanicalGraph
{
    public enum MechanicalPathStatus { Found, ConfirmedDisconnected, NotEstablished }
    public sealed class MechanicalQueryException : Exception
    {
        public string Code { get; }
        public MechanicalQueryException(string code, string message) : base(message) { Code = code; }
    }
    public sealed class MechanicalReach
    {
        public IReadOnlyDictionary<string, int> Depths { get; }
        public IReadOnlyList<MateEdge> Mates { get; }
        public IReadOnlyList<string> ExcludedStartIds { get; }
        public bool DepthBoundReached { get; }
        internal MechanicalReach(Dictionary<string, int> depths, IEnumerable<MateEdge> mates, IEnumerable<string> excluded, bool bounded)
        { Depths = new ReadOnlyDictionary<string, int>(depths); Mates = mates.ToList().AsReadOnly(); ExcludedStartIds = excluded.ToList().AsReadOnly(); DepthBoundReached = bounded; }
    }
    public sealed class MechanicalPath
    {
        public MechanicalPathStatus Status { get; }
        public IReadOnlyList<string> ObjectIds { get; }
        public IReadOnlyList<MateEdge> Mates { get; }
        public bool ShortestPathComplete { get; }
        public bool DepthBoundReached { get; }
        public string? Reason { get; }
        internal MechanicalPath(MechanicalPathStatus status, IEnumerable<string> objects, IEnumerable<MateEdge> mates, bool complete, bool bounded, string? reason)
        { Status = status; ObjectIds = objects.ToList().AsReadOnly(); Mates = mates.ToList().AsReadOnly(); ShortestPathComplete = complete; DepthBoundReached = bounded; Reason = reason; }
    }

    public static class MechanicalQueries
    {
        public static DataStructures.MechanicalGraph.MechanicalGraph RequireGraph(MechanicalScope scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (scope.State != GraphDataState.Available || scope.Graph == null)
                throw new MechanicalQueryException("CAPABILITY_UNAVAILABLE", "GRAPH_CAPABILITY_" + scope.State.ToString().ToUpperInvariant() + ": " + scope.Reason + " " + string.Join("; ", scope.Diagnostics.Take(8).Select(d => d.Path + " " + d.Code + ": " + d.Message)));
            return scope.Graph;
        }
        public static bool Active(MateEdge edge, DataStructures.MechanicalGraph.MechanicalGraph graph) => edge.Suppressed == false
            && graph.NodesById[edge.ObjectAId].Suppressed == false && graph.NodesById[edge.ObjectBId].Suppressed == false;
        // An explicitly suppressed endpoint makes an edge irrelevant to the active design.
        public static bool Unknown(MateEdge edge, DataStructures.MechanicalGraph.MechanicalGraph graph) => edge.Suppressed != true
            && graph.NodesById[edge.ObjectAId].Suppressed != true && graph.NodesById[edge.ObjectBId].Suppressed != true
            && (edge.Suppressed == null || graph.NodesById[edge.ObjectAId].Suppressed == null || graph.NodesById[edge.ObjectBId].Suppressed == null);
        public static bool Complete(MechanicalScope scope)
        {
            var graph = RequireGraph(scope);
            return scope.MembershipCoverage == MechanicalCoverage.Complete && scope.MateCoverage == MechanicalCoverage.Complete
                && graph.NodesById.Values.All(n => n.Suppressed.HasValue) && !graph.MatesById.Values.Any(e => Unknown(e, graph));
        }
        private static void Validate(DataStructures.MechanicalGraph.MechanicalGraph graph, IEnumerable<string> ids, int? hops)
        {
            if (hops < 0) throw new MechanicalQueryException("INVALID_ARGUMENT", "maxHops must be non-negative.");
            foreach (var id in ids) if (id == null || !graph.NodesById.ContainsKey(id)) throw new MechanicalQueryException("OBJECT_OUTSIDE_SCOPE", "Occurrence is outside explicit mechanical membership: " + id);
        }
        private static IEnumerable<MateEdge> Adjacent(DataStructures.MechanicalGraph.MechanicalGraph graph, string id) => graph.NodesById[id].Edges
            .Where(e => Active(e, graph)).OrderBy(e => e.GetOtherEndpoint(id), StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal);

        public static MechanicalReach Neighborhood(MechanicalScope scope, IEnumerable<string> startIds, int maxHops, CancellationToken token = default)
        {
            var graph = RequireGraph(scope); var starts = startIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (starts.Length == 0) throw new MechanicalQueryException("INVALID_ARGUMENT", "At least one start occurrence is required.");
            Validate(graph, starts, maxHops);
            var depths = new Dictionary<string, int>(StringComparer.Ordinal); var queue = new Queue<string>(); var excluded = new List<string>();
            foreach (var id in starts) { if (graph.NodesById[id].Suppressed != false) excluded.Add(id); else { depths.Add(id, 0); queue.Enqueue(id); } }
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var id = queue.Dequeue();
                if (depths[id] == maxHops) continue;
                foreach (var edge in Adjacent(graph, id)) { var next = edge.GetOtherEndpoint(id); if (!depths.ContainsKey(next)) { depths.Add(next, depths[id] + 1); queue.Enqueue(next); } }
            }
            token.ThrowIfCancellationRequested();
            var edges = graph.MatesById.Values.Where(e => Active(e, graph) && depths.ContainsKey(e.ObjectAId) && depths.ContainsKey(e.ObjectBId)).OrderBy(e => e.Id, StringComparer.Ordinal);
            bool bounded = depths.Keys.Any(id => Adjacent(graph, id).Any(e => !depths.ContainsKey(e.GetOtherEndpoint(id))));
            return new MechanicalReach(depths, edges, excluded, bounded);
        }
        public static MechanicalPath Path(MechanicalScope scope, string start, string end, int? maxHops = null, CancellationToken token = default)
        {
            var graph = RequireGraph(scope); Validate(graph, new[] { start, end }, maxHops); token.ThrowIfCancellationRequested();
            MechanicalPath None(MechanicalPathStatus status, bool bounded, string? reason) => new MechanicalPath(status, Array.Empty<string>(), Array.Empty<MateEdge>(), false, bounded, reason);
            if (graph.NodesById[start].Suppressed != false || graph.NodesById[end].Suppressed != false)
                return None(MechanicalPathStatus.NotEstablished, false, "ENDPOINT_NOT_CONFIRMED_ACTIVE");
            var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [start] = 0 };
            var parent = new Dictionary<string, (string Id, MateEdge Edge)>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(start);
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var id = queue.Dequeue();
                if (id == end)
                {
                    var objects = new List<string> { end }; var edges = new List<MateEdge>();
                    while (id != start) { var step = parent[id]; edges.Add(step.Edge); id = step.Id; objects.Add(id); }
                    objects.Reverse(); edges.Reverse();
                    return new MechanicalPath(MechanicalPathStatus.Found, objects, edges, start == end || Complete(scope), false, start == end ? "ZERO_HOP_IDENTITY" : null);
                }
                if (maxHops != null && depths[id] >= maxHops) continue;
                foreach (var edge in Adjacent(graph, id))
                {
                    var next = edge.GetOtherEndpoint(id); if (depths.ContainsKey(next)) continue;
                    depths.Add(next, depths[id] + 1); parent.Add(next, (id, edge)); queue.Enqueue(next);
                }
            }
            bool bounded = depths.Keys.Any(id => Adjacent(graph, id).Any(e => !depths.ContainsKey(e.GetOtherEndpoint(id))));
            bool complete = Complete(scope);
            return None(complete && !bounded ? MechanicalPathStatus.ConfirmedDisconnected : MechanicalPathStatus.NotEstablished, bounded,
                bounded ? "SEARCH_DEPTH_BOUNDED" : !complete ? "INCOMPLETE_CONNECTIVITY_EVIDENCE" : null);
        }
        // Ordered reference island first; lexicographic member sequence breaks equal-size ties.
        public static IReadOnlyList<IReadOnlyList<string>> Islands(MechanicalScope scope, CancellationToken token = default)
        {
            var graph = RequireGraph(scope); var remaining = new SortedSet<string>(graph.NodesById.Values.Where(n => n.Suppressed == false).Select(n => n.Id), StringComparer.Ordinal);
            var islands = new List<IReadOnlyList<string>>();
            while (remaining.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var members = new List<string>(); var queue = new Queue<string>(); var start = remaining.Min!; remaining.Remove(start); queue.Enqueue(start);
                while (queue.Count > 0) { token.ThrowIfCancellationRequested(); var id = queue.Dequeue(); members.Add(id); foreach (var edge in Adjacent(graph, id)) { var next = edge.GetOtherEndpoint(id); if (remaining.Remove(next)) queue.Enqueue(next); } }
                members.Sort(StringComparer.Ordinal); islands.Add(members.AsReadOnly());
            }
            islands.Sort((a, b) => { int count = b.Count.CompareTo(a.Count); if (count != 0) return count; for (int i = 0; i < a.Count; i++) { int c = StringComparer.Ordinal.Compare(a[i], b[i]); if (c != 0) return c; } return 0; });
            return islands.AsReadOnly();
        }
    }
}
