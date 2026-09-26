using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.Operations.MechanicalGraph;

namespace Core.Primitives.Operations.Project
{
    public sealed class ProjectLoadOptions
    {
        public CapabilityState MateExportState { get; set; } = CapabilityState.Unavailable;
        // The host must establish this guarantee; never infer stability from matching ID strings.
        public bool ComponentIdsStableAcrossSnapshots { get; set; }
    }

    public static class LoadProject
    {
        public static ProjectLoadResult Load(string json, ProjectLoadOptions? options = null)
        {
            options ??= new ProjectLoadOptions();
            var diagnostics = new List<LoadDiagnostic>();
            void Fatal(string code, string path, string message) => diagnostics.Add(new LoadDiagnostic(code, path, message, DiagnosticScope.Project, true, true));
            if (!Enum.IsDefined(typeof(CapabilityState), options.MateExportState)) Fatal("INVALID_OPTIONS", "", "Unknown mate export state.");
            if (json == null || Encoding.UTF8.GetByteCount(json) > 10000000) Fatal("INVALID_JSON", "", "Expected at most 10 MB of UTF-8 metadata.");
            if (diagnostics.Count > 0) return new ProjectLoadResult(null, diagnostics);
            JObject doc;
            try
            {
                using var text = new StringReader(json!);
                using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 128 };
                doc = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException("Trailing content.");
            }
            catch (JsonException) { Fatal("INVALID_JSON", "", "Expected one JSON object without duplicate keys or excessive nesting."); return new ProjectLoadResult(null, diagnostics); }
            string? schema = Text(doc["schemaVersion"]);
            if (schema != "1.0" && schema != "2.1") Fatal("UNSUPPORTED_SCHEMA", "/schemaVersion", "Supported metadata schema versions: 1.0, 2.1.");
            var project = doc["project"] as JObject;
            if (project == null || Text(project["id"], 128) == null || Text(project["name"]) == null)
                Fatal("INVALID_PROJECT_IDENTITY", "/project", "Project requires a non-empty ID and name.");
            var sample = doc["sampleInfo"] as JObject;
            if (doc["sampleInfo"] != null && sample == null || sample?["fixture"] != null && sample["fixture"]!.Type != JTokenType.Boolean)
                Fatal("INVALID_PROVENANCE", "/sampleInfo", "sampleInfo must be an object and fixture must be boolean when provided.");
            bool fixture = sample?["fixture"]?.Type == JTokenType.Boolean && (bool)sample["fixture"]!;
            var entries = doc["objects"] as JArray;
            if (entries == null || entries.Count == 0 || entries.Count > 10000) Fatal("INVALID_COMPONENTS", "/objects", "Expected 1 to 10,000 component records.");
            var records = new Dictionary<string, (JObject record, int index)>(StringComparer.Ordinal);
            if (entries != null && entries.Count <= 10000)
                for (int i = 0; i < entries.Count; i++)
                {
                    var o = entries[i] as JObject; string? id = Text(o?["id"], 128);
                    if (o == null || id == null || Text(o["name"]) == null || !(Text(o["type"]) == "part" || Text(o["type"]) == "assembly"))
                    { Fatal("INVALID_COMPONENT_IDENTITY", "/objects/" + i, "Expected bounded ID/name and type part or assembly."); continue; }
                    if (records.ContainsKey(id)) Fatal("DUPLICATE_COMPONENT_ID", "/objects/" + i + "/id", "Duplicate component ID: " + id);
                    else records.Add(id, (o, i));
                }
            if (diagnostics.Any(d => d.Fatal)) return new ProjectLoadResult(null, diagnostics);
            string projectId = (string)project!["id"]!;
            string snapshotId;
            using (var hash = SHA256.Create()) snapshotId = "sha256:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(json!))).Replace("-", "").ToLowerInvariant();
            var identity = !fixture && options.ComponentIdsStableAcrossSnapshots ? IdentityScope.ProjectStable : IdentityScope.SnapshotOnly;
            ValueProvenance Provenance(string path) => new ValueProvenance(projectId, snapshotId, path, fixture, identity);
            var components = new Dictionary<string, ComponentMetadata>(StringComparer.Ordinal);
            foreach (var pair in records)
            {
                var o = pair.Value.record; string prefix = "/objects/" + pair.Value.index + "/";
                var values = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
                foreach (string field in MetadataPropertyRules.Fields)
                {
                    var result = MetadataPropertyRules.Read(o, project, fixture, prefix + field, field, schema!, doc);
                    var state = (string?)result["status"] == "available" ? AvailabilityState.Available : (string?)result["status"] == "invalid" ? AvailabilityState.Invalid : (string?)result["status"] == "not_applicable" ? AvailabilityState.NotApplicable : AvailabilityState.Missing;
                    values[field] = new MetadataValue(state, state == AvailabilityState.Available ? result["value"] : null, o[field], o.Property(field) != null,
                        (string)result["reason"]!, (string)result["expectedFormat"]!, (string?)result["unit"], (string?)result["coordinateFrame"], Provenance(prefix + field),
                        schema == "2.1" ? MetadataPropertyRules.SourceEvidence(o, field) : null, (string?)result["reasonCode"], result["spatialReference"] as JObject);
                    if (state == AvailabilityState.Invalid) diagnostics.Add(new LoadDiagnostic("INVALID_PROPERTY", prefix + field, (string)result["reason"]!, DiagnosticScope.Properties, false));
                }
                // Structural identity remains usable even if hierarchy is unavailable or invalid.
                foreach (string field in new[] { "id", "name", "type", "parentId", "childIds" })
                {
                    var raw = o[field]; bool present = o.Property(field) != null;
                    bool valid = field == "childIds" ? raw is JArray a && a.All(v => Text(v, 128) != null)
                        : field == "parentId" ? raw?.Type == JTokenType.Null || Text(raw, 128) != null : Text(raw) != null;
                    var state = !present || field == "childIds" && raw?.Type == JTokenType.Null ? AvailabilityState.Missing : valid ? AvailabilityState.Available : AvailabilityState.Invalid;
                    values[field] = new MetadataValue(state, valid ? raw : null, raw, present, valid ? "Exported structural field; consult hierarchy capability before traversal." : "Structural field absent or malformed.",
                        field == "childIds" ? "string ID array" : field == "parentId" ? "string ID or null root parent" : "non-empty string", null, null, Provenance(prefix + field));
                }
                components.Add(pair.Key, new ComponentMetadata(pair.Key, (string)o["name"]!, (string)o["type"]!, o, values));
            }
            var hierarchy = ValidateHierarchy(project, records, diagnostics);
            // Validate topology once, then build explicit assembly/configuration projections.
            var graphState = options.MateExportState == CapabilityState.Available ? GraphDataState.Available : options.MateExportState == CapabilityState.Invalid ? GraphDataState.Invalid : GraphDataState.Unavailable;
            if (doc["mechanicalScopes"] is JArray declaredScopes && graphState != GraphDataState.Invalid &&
                declaredScopes.OfType<JObject>().Any(s => s["mateCoverage"]?.Type == JTokenType.String && ((string?)s["mateCoverage"] == "Complete" || (string?)s["mateCoverage"] == "Partial"))) graphState = GraphDataState.Available;
            var graphResult = BuildMechanicalGraph.BuildParsed(doc, graphState, snapshotId);
            foreach (var d in graphResult.Errors) diagnostics.Add(new LoadDiagnostic(d.Code, d.Path, d.Message, DiagnosticScope.MechanicalGraph, true));
            foreach (var d in graphResult.Warnings) diagnostics.Add(new LoadDiagnostic(d.Code, d.Path, d.Message, DiagnosticScope.MechanicalGraph, false));
            var graphCapability = graphResult.State == GraphDataState.Available ? CapabilityState.Available : graphResult.State == GraphDataState.Invalid ? CapabilityState.Invalid : CapabilityState.Unavailable;
            var scopeDiagnostics = new List<GraphDiagnostic>();
            var mechanicalScopes = LoadMechanicalScopes.Load(doc, graphResult, scopeDiagnostics, snapshotId);
            foreach (var d in scopeDiagnostics) diagnostics.Add(new LoadDiagnostic(d.Code, d.Path, d.Message, DiagnosticScope.MechanicalGraph, true));
            if (doc["mechanicalScopes"] != null)
                graphCapability = mechanicalScopes.Any(s => s.State == GraphDataState.Available) ? CapabilityState.Available
                    : scopeDiagnostics.Count > 0 || mechanicalScopes.Any(s => s.State == GraphDataState.Invalid) || graphResult.State == GraphDataState.Invalid ? CapabilityState.Invalid : CapabilityState.Unavailable;
            var mates = new Dictionary<string, MateMetadata>(StringComparer.Ordinal);
            if (graphCapability != CapabilityState.Invalid && !fixture && doc["mates"] is JArray mateRecords)
                for (int i = 0; i < mateRecords.Count; i++)
                {
                    var m = (JObject)mateRecords[i]!;
                    string id = (string)m["id"]!;
                    mates.Add(id, new MateMetadata(id, (string)m["componentIds"]![0]!, (string)m["componentIds"]![1]!, m, Provenance("/mates/" + i)));
                }
            string? revision = Text(project["revisionId"]);
            if (project["revisionId"] != null && project["revisionId"]!.Type != JTokenType.Null && revision == null)
                diagnostics.Add(new LoadDiagnostic("INVALID_REVISION", "/project/revisionId", "Revision label is malformed; content-derived SnapshotId remains authoritative.", DiagnosticScope.Project, false));
            var snapshot = new ProjectSnapshot(doc, projectId, snapshotId, (string)project["name"]!, revision, fixture, identity,
                new ProjectCapabilities(hierarchy, graphCapability), components, mates, diagnostics, mechanicalScopes);
            return new ProjectLoadResult(snapshot, diagnostics);
        }

        private static string? Text(JToken? value, int max = 512) => value?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)value) && ((string)value!).Length <= max ? (string)value! : null;

        private static CapabilityState ValidateHierarchy(JObject project, Dictionary<string, (JObject record, int index)> records, List<LoadDiagnostic> diagnostics)
        {
            bool invalid = false, incomplete = false;
            void Error(string code, string path, string message) { invalid = true; diagnostics.Add(new LoadDiagnostic(code, path, message, DiagnosticScope.Hierarchy, true)); }
            string? root = Text(project["rootObjectId"], 128);
            if (project["rootObjectId"] == null || project["rootObjectId"]!.Type == JTokenType.Null) incomplete = true;
            else if (root == null || !records.ContainsKey(root)) Error("INVALID_ROOT", "/project/rootObjectId", "Root must identify an existing component.");
            foreach (var pair in records)
            {
                var o = pair.Value.record; string path = "/objects/" + pair.Value.index;
                if (o["parentId"] == null || o["childIds"] == null || o["childIds"]!.Type == JTokenType.Null) incomplete = true;
                if (o["parentId"] != null && o["parentId"]!.Type != JTokenType.Null && (Text(o["parentId"], 128) == null || !records.ContainsKey((string)o["parentId"]!)))
                    Error("INVALID_PARENT", path + "/parentId", "Parent must reference an existing component or be null for the root.");
                if (o["childIds"] != null && o["childIds"]!.Type != JTokenType.Null)
                {
                    if (!(o["childIds"] is JArray children) || children.Any(c => Text(c, 128) == null)) { Error("INVALID_CHILDREN", path + "/childIds", "Expected string child IDs."); continue; }
                    var ids = children.Select(c => (string)c!).ToArray();
                    if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !records.ContainsKey(id))) Error("INVALID_CHILDREN", path + "/childIds", "Duplicate or nonexistent child IDs.");
                    if ((string?)o["type"] == "part" && ids.Length > 0) Error("PART_HAS_CHILDREN", path, "Parts cannot own hierarchy children.");
                }
            }
            if (invalid) return CapabilityState.Invalid;
            // Reject known contradictions even when other hierarchy fields were not exported.
            foreach (var pair in records)
            {
                var o = pair.Value.record; string path = "/objects/" + pair.Value.index;
                string? parent = Text(o["parentId"], 128);
                if (parent != null && records[parent].record["childIds"] is JArray siblings && !siblings.Any(c => (string)c! == pair.Key))
                    Error("INCONSISTENT_PARENT", path, "Supplied parent and child lists disagree.");
                if (o["childIds"] is JArray children)
                    foreach (string child in children.Select(c => (string)c!))
                        if (records[child].record.Property("parentId") != null && (string?)records[child].record["parentId"] != pair.Key)
                            Error("INCONSISTENT_CHILD", path, "Supplied child and parent fields disagree.");
            }
            if (invalid) return CapabilityState.Invalid;
            if (incomplete) { diagnostics.Add(new LoadDiagnostic("HIERARCHY_UNAVAILABLE", "/objects", "Hierarchy fields are incomplete; do not infer an empty hierarchy.", DiagnosticScope.Hierarchy, false)); return CapabilityState.Unavailable; }
            if ((string?)records[root!].record["type"] != "assembly" || records[root!].record["parentId"]!.Type != JTokenType.Null) Error("INVALID_ROOT", "/project/rootObjectId", "Root must be an assembly with null parent.");
            foreach (var pair in records)
            {
                var o = pair.Value.record; string path = "/objects/" + pair.Value.index;
                string? parent = (string?)o["parentId"];
                if (pair.Key != root && (parent == null || !((JArray)records[parent].record["childIds"]!).Any(c => (string)c! == pair.Key)))
                    Error("INCONSISTENT_PARENT", path, "Non-root parent must list this component as a child.");
                foreach (string child in ((JArray)o["childIds"]!).Select(c => (string)c!))
                    if ((string?)records[child].record["parentId"] != pair.Key) Error("INCONSISTENT_CHILD", path, "Child and parent relationships disagree.");
            }
            if (!invalid)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<(string id, int depth)>(); queue.Enqueue((root!, 0));
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    if (!visited.Add(item.id) || item.depth > 128) { Error("INVALID_HIERARCHY", "/objects", "Hierarchy has a cycle or exceeds 128 levels."); break; }
                    foreach (string child in ((JArray)records[item.id].record["childIds"]!).Select(c => (string)c!)) queue.Enqueue((child, item.depth + 1));
                }
                if (visited.Count != records.Count) Error("DISCONNECTED_HIERARCHY", "/objects", "Every component must be reachable from the root.");
            }
            return invalid ? CapabilityState.Invalid : CapabilityState.Available;
        }
    }
}
