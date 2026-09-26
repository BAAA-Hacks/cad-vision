#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    // One semantic operation: lexical discovery + containment expansion + incident mate evidence.
    // No geometry inference, graph path search, alias learning, or mutation of active scope.
    internal sealed class ConnectionQueryTool : IAsyncCadenTool, ICapabilityCadenTool
    {
        private readonly ProjectSnapshot? snapshot;
        private readonly ToolLimits limits;
        private readonly QueryCursors cursors = new QueryCursors();
        public string Name => "find_connections";
        public bool Available => snapshot?.Capabilities.Hierarchy == CapabilityState.Available && new MateQueryTool(snapshot, limits).Available;
        internal ConnectionQueryTool(ProjectSnapshot? snapshot, ToolLimits limits) { this.snapshot = snapshot; this.limits = limits; }
        private static JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 };
        public JObject Declaration => new JObject { ["name"] = Name,
            ["description"] = "First choice for whether parts/assemblies are mated or connected. Supply query (name/ID words) OR exact objectId. Resolves matching occurrences and assembly descendants locally; returns connected endpoint names, mate types/references and native fixed/constraint states in one call. Exact IDs/names preferred; otherwise matching descendants under matching assemblies are folded into those assembly candidates. otherQuery is an optional name hint, NEVER a hard filter: unmatched informal names still return connected alternatives, not confirmed aliases. Default relation=boundary includes mates crossing each candidate's subtree; all also includes internal mates. Active scope limits primary candidates, not visibility of their boundary endpoints. No scope change or graph traversal. Empty results never prove no connection. Results are bounded; if candidatesTruncated, narrow query or use objectId. Repeat unchanged query with cursor for remaining connections.",
            ["parameters"] = new JObject { ["type"] = "object", ["required"] = new JArray("projectId", "snapshotId"), ["properties"] = new JObject {
                ["projectId"] = Text(), ["snapshotId"] = Text(), ["query"] = Text(), ["objectId"] = Text(), ["otherQuery"] = Text(),
                ["relation"] = new JObject { ["type"] = "string", ["enum"] = new JArray("boundary", "internal", "all") },
                ["includeSuppressed"] = new JObject { ["type"] = "boolean" },
                ["candidateLimit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 16 },
                ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = limits.MaxResults }, ["cursor"] = Text() } } };
        public JObject Execute(JObject args) => ExecuteAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        public Task<JObject> ExecuteAsync(JObject args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Available) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "Valid hierarchy and usable exported mate records are required. This query does not require a traversable graph.");
            var s = snapshot!; var scope = ScopeContext.From(args);
            bool byId = args["objectId"] != null;
            if (byId == (args["query"] != null)) throw new ToolInputException("INVALID_ARGUMENT", "Supply exactly one of query or objectId.");
            string? query = (string?)args["query"], otherQuery = (string?)args["otherQuery"];
            if (query != null && string.IsNullOrWhiteSpace(query) || otherQuery != null && string.IsNullOrWhiteSpace(otherQuery)) throw new ToolInputException("INVALID_ARGUMENT", "Search text must contain a word.");
            string? Parent(string id) => s.Indexes.Parent(id);
            IEnumerable<string> Ancestors(string id) => s.Indexes.Ancestors(id);
            HashSet<string> Expand(string id) => new HashSet<string>(s.Indexes.Scope(id, token), StringComparer.Ordinal);
            bool Matches(ComponentMetadata c, string text) => s.Indexes.Matches(c.Id, text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            ComponentMetadata[] Discover(string text, bool primary)
            {
                var matches = s.ComponentsById.Values.Where(c => (!primary || scope == null || scope.Includes(c.Id)) && Matches(c, text)).ToArray();
                var exact = matches.Where(c => string.Equals(c.Id, text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 0) exact = matches.Where(c => string.Equals(c.Name, text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length > 0) return exact;
                var matchingAssemblies = matches.Where(c => c.Type == "assembly").Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                return matches.Where(c => !Ancestors(c.Id).Any(matchingAssemblies.Contains)).ToArray();
            }
            ComponentMetadata[] discovered;
            if (byId)
            {
                if (!s.ComponentsById.TryGetValue((string)args["objectId"]!, out var c)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown connection target.");
                discovered = new[] { c };
            }
            else discovered = Discover(query!, true);
            int candidateLimit = (int?)args["candidateLimit"] ?? 8, limit = (int?)args["limit"] ?? Math.Min(10, limits.MaxResults);
            string relation = (string?)args["relation"] ?? "boundary";
            args["candidateLimit"] = candidateLimit; args["limit"] = limit; args["relation"] = relation;
            var candidates = discovered.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).Take(candidateLimit).ToArray();
            var memberships = candidates.ToDictionary(c => c.Id, c => Expand(c.Id), StringComparer.Ordinal);
            var hintCandidates = otherQuery == null ? Array.Empty<ComponentMetadata>() : Discover(otherQuery, false);
            var hintIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in hintCandidates) hintIds.UnionWith(Expand(c.Id));
            JObject Value(ComponentMetadata c, string key)
            {
                var v = c.Properties[key];
                return new JObject { ["status"] = v.State == AvailabilityState.NotApplicable ? "not_applicable" : v.State.ToString().ToLowerInvariant(),
                    ["value"] = v.State == AvailabilityState.Available ? v.Value : null, ["sourceField"] = v.Provenance.SourceField, ["reasonCode"] = v.ReasonCode };
            }
            JObject Object(string id)
            {
                var c = s.ComponentsById[id]; return new JObject { ["id"] = id, ["name"] = c.Name, ["type"] = c.Type,
                    ["parentId"] = Parent(id), ["parentName"] = Parent(id) is string p ? s.ComponentsById[p].Name : null,
                    ["fixed"] = Value(c, "fixed"), ["suppressed"] = Value(c, "suppressed"), ["constraintStatus"] = Value(c, "definitionStatus") };
            }
            bool? Suppression(string id)
            {
                bool unknown = false;
                foreach (var member in new[] { id }.Concat(Ancestors(id)))
                { var value = s.ComponentsById[member].Properties["suppressed"]; if (value.State != AvailabilityState.Available) unknown = true; else if ((bool?)value.Value == true) return true; }
                return unknown ? (bool?)null : false;
            }
            var suppressed = new Dictionary<string, bool?>(StringComparer.Ordinal);
            bool? Suppressed(string id) { if (!suppressed.TryGetValue(id, out var v)) { v = Suppression(id); suppressed.Add(id, v); } return v; }
            var matchesByMate = new Dictionary<string, List<(string Candidate, string Relation, bool Hint)>>(StringComparer.Ordinal);
            var incidentIds = new HashSet<string>(StringComparer.Ordinal); int excludedSuppressed = 0, unknownSuppression = 0;
            foreach (var mate in s.Indexes.Incident(memberships.Values.SelectMany(v => v)).OrderBy(id => id, StringComparer.Ordinal).Select(id => s.MatesById[id]))
            {
                token.ThrowIfCancellationRequested(); var matched = new List<(string Candidate, string Relation, bool Hint)>();
                foreach (var c in candidates)
                {
                    var membership = memberships[c.Id]; bool a = membership.Contains(mate.ObjectAId), b = membership.Contains(mate.ObjectBId);
                    if (!a && !b) continue;
                    incidentIds.Add(mate.Id);
                    bool boundary = a != b;
                    if (relation == "boundary" && !boundary || relation == "internal" && boundary) continue;
                    matched.Add((c.Id, boundary ? "Boundary" : "Internal", otherQuery != null && (boundary ? hintIds.Contains(a ? mate.ObjectBId : mate.ObjectAId) : hintIds.Contains(mate.ObjectAId) || hintIds.Contains(mate.ObjectBId))));
                }
                if (matched.Count == 0) continue;
                var record = mate.CopyRawRecord(); bool? mateSuppressed = record["suppressed"]?.Type == JTokenType.Boolean ? (bool?)record["suppressed"] : null;
                bool isSuppressed = mateSuppressed == true || Suppressed(mate.ObjectAId) == true || Suppressed(mate.ObjectBId) == true;
                if (isSuppressed && (bool?)args["includeSuppressed"] != true) { excludedSuppressed++; continue; }
                if (!isSuppressed && (mateSuppressed == null || Suppressed(mate.ObjectAId) == null || Suppressed(mate.ObjectBId) == null)) unknownSuppression++;
                matchesByMate.Add(mate.Id, matched);
            }
            var summaries = new JArray(candidates.Select(c => { var o = Object(c.Id); o["includedObjectCount"] = memberships[c.Id].Count;
                o["matchingExportedMateCount"] = matchesByMate.Values.Count(v => v.Any(m => m.Candidate == c.Id)); return o; }));
            var ordered = matchesByMate.Keys.OrderByDescending(id => matchesByMate[id].Any(m => m.Hint)).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            int offset = cursors.Resolve(Name, (string)args["projectId"]!, s.SnapshotId, args);
            var items = new JArray();
            var result = new JObject { ["candidates"] = summaries, ["candidateCount"] = discovered.Length, ["candidatesTruncated"] = discovered.Length > candidates.Length,
                ["resolution"] = byId ? "exact_object_id" : "lexical_candidates_not_confirmed_identity",
                ["otherQuery"] = new JObject { ["text"] = otherQuery, ["nameMatchCount"] = hintCandidates.Length,
                    ["status"] = otherQuery == null ? "not_requested" : hintCandidates.Length == 0 ? "no_name_match_connected_alternatives_retained" : "name_hint_only_not_a_filter" },
                ["items"] = items, ["limitations"] = new JArray("EXPORTED_MATE_RELATIONSHIPS_NOT_SOLVER_VALIDITY", "ENTITY_REFERENCES_DO_NOT_IDENTIFY_NAMED_MOUNTING_HOLES", "NATIVE_ERROR_ZERO_IS_NOT_PROOF_OF_SATISFACTION", "EMPTY_RESULTS_DO_NOT_PROVE_NO_CONNECTION"),
                ["coverage"] = new JObject { ["status"] = "partial", ["countUnit"] = "mate_records", ["countScope"] = "incident_exported_records_for_selected_candidates_before_relation_and_suppression_filters",
                    ["requestedCount"] = incidentIds.Count, ["evaluatedCount"] = incidentIds.Count, ["excludedSuppressedAfterRelationFilterCount"] = excludedSuppressed, ["unknownSuppressionAfterRelationFilterCount"] = unknownSuppression,
                    ["reasonCodes"] = new JArray("SCOPED_EXPORT_COMPLETENESS_NOT_ESTABLISHED"), ["candidateSelectionComplete"] = discovered.Length == candidates.Length } };
            foreach (string id in ordered.Skip(offset).Take(limit))
            {
                token.ThrowIfCancellationRequested(); var mate = s.MatesById[id]; var r = mate.CopyRawRecord();
                var row = MateQueryTool.Serialize(mate, s, scope);
                row["endpoints"] = new JArray(Object(mate.ObjectAId), Object(mate.ObjectBId));
                row["effectiveEndpointSuppression"] = new JArray(new JValue(Suppressed(mate.ObjectAId)), new JValue(Suppressed(mate.ObjectBId)));
                row["candidateRelations"] = new JArray(matchesByMate[id].Select(m => new JObject { ["candidateId"] = m.Candidate, ["relation"] = m.Relation, ["otherQueryNameMatch"] = m.Hint }));
                items.Add(row);
                if (result.ToString(Formatting.None).Length > Math.Max(0, limits.MaxResponseCharacters - 4000))
                { items.RemoveAt(items.Count - 1); if (items.Count == 0) throw new ToolInputException("QUERY_TOO_LARGE", "Connection evidence exceeds response budget; narrow query/use objectId or reduce candidateLimit."); break; }
            }
            int next = offset + items.Count;
            result["pagination"] = new JObject { ["limit"] = limit, ["total"] = ordered.Length, ["returnedCount"] = items.Count,
                ["nextCursor"] = next < ordered.Length ? cursors.Issue(Name, (string)args["projectId"]!, s.SnapshotId, args, next) : null };
            var definitionSubjects = candidates.Select(c => c.Id).Concat(items.SelectMany(m => m["endpoints"]!).Select(e => (string)e["id"]!))
                .Distinct(StringComparer.Ordinal).ToArray();
            int knownDefinitions = definitionSubjects.Count(id => s.ComponentsById[id].Properties["definitionStatus"].State == AvailabilityState.Available);
            int knownTypes = items.Count(m => (string?)m["properties"]?["type"]?["status"] == "available");
            var retrievalGaps = new JArray();
            if (discovered.Length > candidates.Length) retrievalGaps.Add("NARROW_TRUNCATED_CANDIDATES");
            if (next < ordered.Length) retrievalGaps.Add("PAGE_REMAINING_CONNECTIONS_IF_NEEDED");
            result["answerCoverage"] = new JObject {
                ["candidateDiscovery"] = new JObject { ["status"] = discovered.Length > candidates.Length ? "truncated" : discovered.Length == 0 ? "no_lexical_match" : "supplied",
                    ["basis"] = "exact_ID_then_exact_name_else_outermost_lexical_matches_with_descendants", ["count"] = discovered.Length },
                ["exportedConnections"] = new JObject { ["status"] = next < ordered.Length || offset > 0 ? "paged" : "supplied",
                    ["matchingCount"] = ordered.Length, ["returnedCount"] = items.Count, ["absenceProven"] = false },
                ["mateTypes"] = new JObject { ["basis"] = "returned_mates", ["availableCount"] = knownTypes,
                    ["unestablishedCount"] = items.Count - knownTypes },
                ["definitionStates"] = new JObject { ["basis"] = "selected_candidates_and_returned_endpoints",
                    ["availableCount"] = knownDefinitions, ["unestablishedCount"] = definitionSubjects.Length - knownDefinitions,
                    ["sameSnapshotPropertyRereadAddsEvidence"] = false },
                ["retrievalGaps"] = retrievalGaps,
                ["evidenceBoundaries"] = new JArray("INFORMAL_COUNTERPART_IDENTITY_NOT_CONFIRMED_BY_NAME_HINT",
                    "NAMED_MOUNTING_HOLES_NOT_ESTABLISHED_BY_ENTITY_REFERENCES",
                    "SOLVER_VALIDITY_AND_REMAINING_DOF_NOT_ESTABLISHED_BY_MATES",
                    "EXPORT_COMPLETENESS_NOT_CERTIFIED_BY_THIS_QUERY"),
                ["followUpPolicy"] = "Answer exported connections, mate types and native definition states from supplied evidence, qualifying identity/coverage. Additional calls need a specific requested fact missing here and a tool capable of supplying it. Partial export coverage alone is not a retrieval gap; repeated discovery or mate reads do not repair it."
            };
            return Task.FromResult(result);
        }
    }
}
