using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    internal sealed class MateQueryTool : IAsyncCadenTool, ICapabilityCadenTool
    {
        private readonly ProjectSnapshot? snapshot;
        private readonly ToolLimits limits;
        private readonly QueryCursors cursors = new QueryCursors();
        public string Name => "get_mates";
        public bool Available => snapshot != null && !snapshot.IsFixture && (snapshot.MatesById.Count > 0 || snapshot.MechanicalScopes.Any(s => s.State == Core.Primitives.DataStructures.MechanicalGraph.GraphDataState.Available));
        internal MateQueryTool(ProjectSnapshot? snapshot, ToolLimits limits) { this.snapshot = snapshot; this.limits = limits; }
        private static JObject Text() => new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 };
        public JObject Declaration => new JObject { ["name"] = Name,
            ["description"] = "Read exported mates incident to ANY requested object, deduplicated by mate ID. objectIds is required without active scope; omit it with active scope to list all incident internal/boundary mates. External explicit targets fail OUT_OF_SCOPE. Does not traverse or prove valid constraints. Explicitly suppressed mates excluded by default; unknown suppression retained and labeled unknown, never assumed active. Includes availability/provenance for optional fields. Missing export coverage is partial even when the list is empty. Raw spatial fields are not usable geometry without explicit frame/units.",
            ["parameters"] = new JObject { ["type"] = "object", ["required"] = new JArray("projectId", "snapshotId"), ["properties"] = new JObject {
                ["projectId"] = Text(), ["snapshotId"] = Text(), ["objectIds"] = new JObject { ["type"] = "array", ["items"] = Text(), ["minItems"] = 1, ["maxItems"] = limits.MaxObjectIds },
                ["includeSuppressed"] = new JObject { ["type"] = "boolean" }, ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = limits.MaxResults }, ["cursor"] = Text() } } };
        public JObject Execute(JObject args) => ExecuteAsync(args, CancellationToken.None).GetAwaiter().GetResult();
        public Task<JObject> ExecuteAsync(JObject args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Available) throw new ToolInputException("CAPABILITY_UNAVAILABLE", "Usable mate records/coverage are unavailable. Empty data does not establish zero mates.");
            var s = snapshot!; var activeScope = ScopeContext.From(args);
            if (!(args["objectIds"] is JArray) && activeScope == null) throw new ToolInputException("INVALID_ARGUMENT", "objectIds is required without active scope.");
            var ids = args["objectIds"] is JArray requested ? requested.Select(v => (string)v!).ToHashSet(StringComparer.Ordinal) : activeScope!.ObjectIds.ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids) if (!s.ComponentsById.ContainsKey(id)) throw new ToolInputException("UNKNOWN_OBJECT_ID", "Unknown object: " + id);
            var matches = s.Indexes.Incident(ids).Select(id => s.MatesById[id]).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
            bool? Suppressed(MateMetadata m) { var value = m.CopyRawRecord()["suppressed"]; return value?.Type == JTokenType.Boolean ? (bool?)value : null; }
            var visible = matches.Where(m => (bool?)args["includeSuppressed"] == true || Suppressed(m) != true).ToArray();
            int limit = (int?)args["limit"] ?? Math.Min(20, limits.MaxResults); args["limit"] = limit;
            int offset = cursors.Resolve(Name, (string)args["projectId"]!, s.SnapshotId, args);
            // An unscoped record lookup cannot certify assembly/configuration coverage.
            return Task.FromResult(new JObject { ["items"] = new JArray(visible.Skip(offset).Take(limit).Select(m => Serialize(m, s, activeScope))),
                ["interpretation"] = "Native error code zero is not proof of satisfaction or absence of dangling/conflicting references. lockRotation=false is a mate setting, not proof of any remaining component DOF. Coverage is partial.",
                ["coverage"] = new JObject { ["status"] = "partial", ["countUnit"] = "mates", ["countScope"] = "exported_incident_records_before_suppression_filter", ["requestedCount"] = matches.Length, ["evaluatedCount"] = matches.Length,
                    ["unknownSuppressionCount"] = matches.Count(m => Suppressed(m) == null), ["reasonCodes"] = new JArray("UNSCOPED_MATE_COVERAGE_NOT_ESTABLISHED") },
                ["pagination"] = new JObject { ["limit"] = limit, ["total"] = visible.Length, ["nextCursor"] = offset + limit < visible.Length ? cursors.Issue(Name, (string)args["projectId"]!, s.SnapshotId, args, offset + limit) : null } });
        }
        internal static JObject Serialize(MateMetadata mate, ProjectSnapshot s, ScopeContext? activeScope)
            {
                bool? Suppressed(MateMetadata m) { var v = m.CopyRawRecord()["suppressed"]; return v?.Type == JTokenType.Boolean ? (bool?)v : null; }
                var raw = mate.CopyRawRecord(); var fields = new JObject();
                foreach (string field in new[] { "name", "type", "status", "suppressed", "references", "axis", "limits", "lockRotation", "alignment", "nativeStatus", "satisfaction", "nativeErrorCode", "lockRotationStatus" })
                {
                    var value = raw[field]; bool missing = value == null || value.Type == JTokenType.Null;
                    bool valid = !missing && (field == "suppressed" || field == "lockRotation" ? value!.Type == JTokenType.Boolean
                        : field == "nativeErrorCode" ? value!.Type == JTokenType.Integer
                        : field == "references" || field == "axis" ? value is JArray
                        : field == "limits" || field == "nativeStatus" ? value is JObject : value!.Type == JTokenType.String);
                    bool spatial = field == "axis" || field == "limits";
                    if (field == "references" && valid) valid = ((JArray)value!).All(r => r is JObject && r["componentId"]?.Type == JTokenType.String && r["entityType"]?.Type == JTokenType.String);
                    if (field == "axis" && valid) valid = ((JArray)value!).Count == 3 && ((JArray)value!).All(v => (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) && !double.IsNaN((double)v) && !double.IsInfinity((double)v));
                    bool unknown = value?.Type == JTokenType.String && (string?)value == "unknown";
                    bool notApplicable = missing && (string?)raw[field + "Status"] == "not_applicable";
                    fields[field] = new JObject { ["status"] = notApplicable ? "not_applicable" : missing || unknown ? "missing" : !valid ? "invalid" : spatial ? "missing" : "available",
                        ["value"] = valid && !spatial && !unknown ? value!.DeepClone() : null,
                        ["reason"] = notApplicable ? "NOT_APPLICABLE" : missing ? "NOT_EXPORTED" : unknown ? "UNESTABLISHED" : !valid ? "INVALID_FORMAT" : spatial ? "SPATIAL_REFERENCE_NOT_ESTABLISHED" : null,
                        ["rawValue"] = spatial && valid ? value!.DeepClone() : null, ["sourceField"] = mate.Provenance.SourceField + "." + field };
                }
                return new JObject { ["mateId"] = mate.Id, ["endpointObjectIds"] = new JArray(mate.ObjectAId, mate.ObjectBId),
                    ["endpoints"] = new JArray(new[] { mate.ObjectAId, mate.ObjectBId }.Select(id => new JObject { ["id"] = id, ["name"] = s.ComponentsById[id].Name })),
                    ["relationScope"] = activeScope?.Classify(mate.ObjectAId, mate.ObjectBId),
                    ["properties"] = fields, ["suppressionState"] = Suppressed(mate) == null ? "Unknown" : Suppressed(mate) == true ? "Suppressed" : "Active",
                    ["provenance"] = new JObject { ["sourceProjectId"] = mate.Provenance.ProjectId, ["snapshotId"] = mate.Provenance.SnapshotId, ["identityScope"] = mate.Provenance.IdentityScope.ToString(), ["sourceField"] = mate.Provenance.SourceField, ["isFixture"] = mate.Provenance.IsFixture } };
            }
    }
}
