using System;
using System.Collections.Generic;
using System.Linq;
using Core.Primitives.DataStructures.MechanicalGraph;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.MechanicalGraph
{
    internal static class LoadMechanicalScopes
    {
        internal static IReadOnlyList<MechanicalScope> Load(JObject doc, GraphBuildResult topology, List<GraphDiagnostic> diagnostics, string snapshotId)
        {
            var scopes = new List<MechanicalScope>();
            if (doc["mechanicalScopes"] == null) return scopes.AsReadOnly();
            if (!(doc["mechanicalScopes"] is JArray entries) || entries.Count > 128)
            { diagnostics.Add(new GraphDiagnostic("INVALID_MECHANICAL_SCOPES", "/mechanicalScopes", "Expected at most 128 explicit scopes.")); return scopes.AsReadOnly(); }
            var objects = ((JArray)doc["objects"]!).OfType<JObject>().ToDictionary(o => (string)o["id"]!, StringComparer.Ordinal);
            var mates = topology.State != GraphDataState.Invalid && doc["mates"] is JArray suppliedMates
                ? suppliedMates.OfType<JObject>().ToDictionary(m => (string)m["id"]!, StringComparer.Ordinal) : null;
            var seen = new HashSet<(string, string)>();
            foreach (var entry in entries)
            {
                var errors = new List<GraphDiagnostic>();
                void Error(string message) => errors.Add(new GraphDiagnostic("INVALID_MECHANICAL_SCOPE", "/mechanicalScopes/" + scopes.Count, message));
                string? Text(JToken? t) => t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)t) && ((string)t!).Length <= 512 ? (string)t! : null;
                var s = entry as JObject;
                string assembly = Text(s?["scopeAssemblyId"]) ?? "", config = Text(s?["configuration"]) ?? "";
                string source = Text(s?["source"]) ?? "";
                if (assembly.Length == 0 || !objects.TryGetValue(assembly, out var root) || (string?)root["type"] != "assembly" || config.Length == 0 || source.Length == 0)
                    Error("Scope requires an existing assembly ID, configuration and exporter source.");
                if (!seen.Add((assembly, config))) Error("Duplicate assembly/configuration scope.");
                MechanicalCoverage Coverage(JToken? t)
                {
                    if (t?.Type == JTokenType.String && Enum.TryParse<MechanicalCoverage>((string)t!, false, out var c) && Enum.IsDefined(typeof(MechanicalCoverage), c) && c.ToString() == (string)t!) return c;
                    Error("Coverage must be Complete, Partial, Unavailable or Invalid."); return MechanicalCoverage.Invalid;
                }
                var membership = Coverage(s?["membershipCoverage"]); var coverage = Coverage(s?["mateCoverage"]);
                if (membership == MechanicalCoverage.Invalid || coverage == MechanicalCoverage.Invalid) Error("Exporter reports invalid membership or mate coverage.");
                string[] Ids(JToken? t, int maximum)
                {
                    if (!(t is JArray a) || a.Count > maximum || a.Any(v => Text(v) == null)) { Error("Expected a bounded array of occurrence/mate IDs."); return Array.Empty<string>(); }
                    var ids = a.Select(v => (string)v!).ToArray(); if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) Error("Duplicate scope member ID."); return ids;
                }
                var ids = Ids(s?["occurrenceIds"], 10000); var mateIds = Ids(s?["mateIds"], 50000);
                var members = new HashSet<string>(ids, StringComparer.Ordinal);
                if (ids.Any(id => !objects.ContainsKey(id))) Error("Scope references unknown occurrence.");
                if (topology.State != GraphDataState.Invalid && mateIds.Any(id => mates == null || !mates.ContainsKey(id) || !members.Contains((string)mates[id]["componentIds"]![0]!) || !members.Contains((string)mates[id]["componentIds"]![1]!))) Error("Scoped mate must exist and have both endpoints in explicit occurrence membership.");
                if (s?["reason"] != null && s["reason"]!.Type != JTokenType.Null && Text(s["reason"]) == null) Error("Scope reason must be bounded text.");
                GraphDataState state = topology.State;
                DataStructures.MechanicalGraph.MechanicalGraph? graph = null;
                if (errors.Count > 0 || membership == MechanicalCoverage.Invalid || coverage == MechanicalCoverage.Invalid) state = GraphDataState.Invalid;
                else if (membership == MechanicalCoverage.Unavailable || coverage == MechanicalCoverage.Unavailable) state = GraphDataState.Unavailable;
                else if (state == GraphDataState.Available)
                {
                    var selected = new HashSet<string>(mateIds, StringComparer.Ordinal);
                    var projection = new JObject { ["schemaVersion"] = "1.0", ["project"] = doc["project"]!.DeepClone(),
                        ["objects"] = new JArray(ids.Select(id => objects[id].DeepClone())),
                        ["mates"] = new JArray(((JArray)doc["mates"]!).Where(m => selected.Contains((string)m["id"]!)).Select(m => m.DeepClone())) };
                    var built = BuildMechanicalGraph.BuildParsed(projection, GraphDataState.Available, snapshotId);
                    state = built.State; graph = built.Graph; errors.AddRange(built.Errors);
                }
                diagnostics.AddRange(errors);
                scopes.Add(new MechanicalScope(assembly, config, membership, coverage, source, Text(s?["reason"]), state, graph, errors.Concat(topology.Errors)));
            }
            // Duplicate scope keys are ambiguous: no scope from this catalog is usable.
            if (seen.Count != scopes.Count) return scopes.Select(s => new MechanicalScope(s.ScopeAssemblyId, s.Configuration, s.MembershipCoverage, s.MateCoverage, s.Source, s.Reason, GraphDataState.Invalid, null, s.Diagnostics)).ToList().AsReadOnly();
            return scopes.AsReadOnly();
        }
    }
}
