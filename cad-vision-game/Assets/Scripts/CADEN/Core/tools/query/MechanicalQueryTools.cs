#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.MechanicalGraph;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    internal sealed class MechanicalQueryTool : IAsyncCadenTool, ICapabilityCadenTool
    {
        private readonly ProjectSnapshot? snapshot;
        private readonly ToolLimits limits;
        public string Name { get; }
        public bool Available => snapshot?.MechanicalScopes.Any(s => s.State == GraphDataState.Available) == true;
        public MechanicalQueryTool(string name, ProjectSnapshot? snapshot, ToolLimits limits) { Name = name; this.snapshot = snapshot; this.limits = limits; }
        private static JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 };
        public JObject Declaration
        {
            get
            {
                var p = new JObject { ["projectId"] = Text(), ["snapshotId"] = Text(), ["scopeAssemblyId"] = Text(), ["configuration"] = Text(),
                    ["maxHops"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = limits.MaxDepth } };
                var required = new JArray("projectId", "snapshotId", "scopeAssemblyId", "configuration");
                bool neighborhood = Name == "get_mechanical_neighborhood";
                if (neighborhood) { p["startObjectIds"] = new JObject { ["type"] = "array", ["items"] = Text(), ["minItems"] = 1, ["maxItems"] = limits.MaxObjectIds }; required.Add("startObjectIds"); required.Add("maxHops"); }
                else { p["startObjectId"] = Text(); p["endObjectId"] = Text(); required.Add("startObjectId"); required.Add("endObjectId"); }
                return new JObject { ["name"] = Name, ["description"] = neighborhood
                    ? "Return confirmed-active exported mate neighbors with minimum hops from simultaneous starts and every eligible mate between reached objects. Explicit scope/configuration from summary required. maxHops is required. Coverage and depth boundaries limit claims. No rigidity or motion inference."
                    : "Return one deterministic minimum-hop exported mate path, or ConfirmedDisconnected only with complete evidence and exhaustive search; otherwise NotEstablished. Optional maxHops bounds the search. Error/dangling active mates remain relationships. Shortest path is only among known eligible relationships unless shortestPathComplete=true. Same eligible endpoints give zero-hop identity, not a mechanical relationship.",
                    ["parameters"] = new JObject { ["type"] = "object", ["properties"] = p, ["required"] = required } };
            }
        }
        public JObject Execute(JObject args) => Run(args, CancellationToken.None);
        public Task<JObject> ExecuteAsync(JObject args, CancellationToken token) => Task.FromResult(Run(args, token));
        private JObject Run(JObject args, CancellationToken token)
        {
            var store = snapshot ?? throw new ToolInputException("CAPABILITY_UNAVAILABLE", "No metadata loaded.");
            var scope = store.MechanicalScopes.FirstOrDefault(s => s.ScopeAssemblyId == (string?)args["scopeAssemblyId"] && s.Configuration == (string?)args["configuration"]);
            if (scope == null) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "No explicit mechanical scope for this assembly/configuration. Refresh get_model_summary.");
            string[] ids = Name == "get_mechanical_neighborhood" ? ((JArray)args["startObjectIds"]!).Select(v => (string)v!).ToArray() : new[] { (string)args["startObjectId"]!, (string)args["endObjectId"]! };
            foreach (string id in ids) if (!store.ComponentsById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown occurrence: " + id);
            try
            {
                var graph = MechanicalQueries.RequireGraph(scope);
                var activeScope = ScopeContext.From(args);
                Func<string, bool>? includes = activeScope == null ? null : new Func<string, bool>(activeScope.Includes);
                JObject Object(string id) => new JObject { ["id"] = id, ["name"] = graph.NodesById[id].Name, ["type"] = graph.NodesById[id].Type, ["suppressed"] = false };
                JObject Edge(MateEdge e) => new JObject { ["id"] = e.Id, ["componentIds"] = new JArray(e.ObjectAId, e.ObjectBId), ["type"] = e.Type, ["status"] = e.Status, ["suppressed"] = e.Suppressed };
                JObject result; bool bounded;
                if (Name == "get_mechanical_neighborhood")
                {
                    var found = MechanicalQueries.Neighborhood(scope, ids, (int)args["maxHops"]!, token, includes); bounded = found.DepthBoundReached;
                    var objects = found.Depths.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => { var o = Object(p.Key); o["hopDepth"] = p.Value; return o; });
                    result = new JObject { ["objects"] = new JArray(objects), ["mates"] = new JArray(found.Mates.Select(Edge)), ["excludedStartIds"] = new JArray(found.ExcludedStartIds) };
                }
                else
                {
                    var path = MechanicalQueries.Path(scope, ids[0], ids[1], (int?)args["maxHops"], token, includes); bounded = path.DepthBoundReached;
                    result = new JObject { ["pathStatus"] = path.Status.ToString(), ["objects"] = new JArray(path.ObjectIds.Select(Object)), ["mates"] = new JArray(path.Mates.Select(Edge)),
                        ["hopCount"] = path.Status == MechanicalPathStatus.Found ? new JValue(path.Mates.Count) : JValue.CreateNull(),
                        ["shortestPathComplete"] = path.ShortestPathComplete, ["pathCertainty"] = path.Status == MechanicalPathStatus.Found ? "confirmed" : null,
                        ["reasonCode"] = path.Reason,
                        ["parallelMates"] = new JArray(path.Mates.Select(e => new JObject { ["representativeMateId"] = e.Id,
                            ["eligibleMateIds"] = new JArray(graph.NodesById[e.ObjectAId].Edges.Where(p => p.GetOtherEndpoint(e.ObjectAId) == e.ObjectBId && MechanicalQueries.Active(p, graph)).Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal)) })) };
                }
                result["limitations"] = new JArray("MATE_PATH_DOES_NOT_ESTABLISH_RIGIDITY", "MATE_RELATIONSHIP_DOES_NOT_ESTABLISH_VALID_CONSTRAINT");
                result["coverage"] = Coverage(scope, bounded, (int?)args["maxHops"]);
                if (activeScope != null)
                {
                    result["coverage"]!["activeScopeBoundaryApplied"] = true;
                    result["coverage"]!["globalConnectivityEstablished"] = false;
                    ((JArray)result["limitations"]!).Add("TRAVERSAL_STAYS_INSIDE_ACTIVE_SCOPE_NO_GLOBAL_DISCONNECTION_CLAIM");
                }
                result["interpretation"] = new JObject {
                    ["evidenceScope"] = "exported_mate_relationships_only",
                    ["reportCoverageLimitation"] = activeScope != null || !MechanicalQueries.Complete(scope) || bounded,
                    ["summary"] = activeScope != null ? "Traversal is restricted to active query scope. Export coverage remains that of the original explicit mechanical scope; no global disconnection or global shortest-path completeness follows. Inspect source coverage as well."
                        : !MechanicalQueries.Complete(scope) ? "Export coverage is partial; report this limitation. No solved DOF, rigidity, satisfaction or exhaustive absence claim follows." : bounded ? "Search is depth-bounded; report the boundary." : "Coverage is complete for this scoped exported relationship query, not solver validity." };
                return result;
            }
            catch (MechanicalQueryException ex) { throw new ToolInputException(ex.Code, ex.Message); }
        }
        private static JObject Coverage(MechanicalScope scope, bool bounded, int? maxHops)
        {
            var graph = scope.Graph!; var reasons = new JArray();
            if (scope.MateCoverage != MechanicalCoverage.Complete) reasons.Add("MATE_COVERAGE_" + scope.MateCoverage.ToString().ToUpperInvariant());
            if (scope.MembershipCoverage != MechanicalCoverage.Complete) reasons.Add("MEMBERSHIP_COVERAGE_" + scope.MembershipCoverage.ToString().ToUpperInvariant());
            int unknownNodes = graph.NodesById.Values.Count(n => n.Suppressed == null), unknownMates = graph.MatesById.Values.Count(e => MechanicalQueries.Unknown(e, graph));
            if (unknownNodes + unknownMates > 0) reasons.Add("UNKNOWN_SUPPRESSION_EXCLUDED");
            if (bounded) reasons.Add("SEARCH_DEPTH_BOUNDED");
            return new JObject { ["status"] = MechanicalQueries.Complete(scope) ? "complete" : "partial", ["countUnit"] = "objects", ["countScope"] = "entire_explicit_scope",
                ["requestedCount"] = graph.NodesById.Count, ["evaluatedCount"] = graph.NodesById.Values.Count(n => n.Suppressed == false),
                ["excludedUnknownCount"] = unknownNodes, ["excludedSuppressedCount"] = graph.NodesById.Values.Count(n => n.Suppressed == true),
                ["mates"] = new JObject { ["countUnit"] = "mates", ["requestedCount"] = graph.MatesById.Count, ["evaluatedCount"] = graph.MatesById.Values.Count(e => MechanicalQueries.Active(e, graph)),
                    ["excludedUnknownSuppressionCount"] = unknownMates,
                    ["excludedSuppressedCount"] = graph.MatesById.Values.Count(e => e.Suppressed == true || graph.NodesById[e.ObjectAId].Suppressed == true || graph.NodesById[e.ObjectBId].Suppressed == true) },
                ["mateCoverageStatus"] = scope.MateCoverage.ToString(), ["membershipCoverageStatus"] = scope.MembershipCoverage.ToString(),
                ["scopeAssemblyId"] = scope.ScopeAssemblyId, ["configuration"] = scope.Configuration, ["source"] = scope.Source, ["sourceReason"] = scope.Reason,
                ["maxHops"] = maxHops, ["depthBoundReached"] = bounded, ["reasonCodes"] = reasons };
        }
    }
}
