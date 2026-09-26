using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Core.Primitives.DataStructures.MechanicalGraph;

namespace Core.Primitives.Operations.MechanicalGraph
{
    public static class BuildMechanicalGraph
    {
        // Availability must come from the export pipeline, not from observing an empty array.
        public static GraphBuildResult Build(string json, GraphDataState mateExportState = GraphDataState.Unavailable)
        {
            try
            {
                if (json == null || json.Length > 10000000) return Failure("INVALID_JSON", "", "Expected metadata JSON up to 10 MB.");
                var document = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                return new Builder(document, mateExportState).Run();
            }
            catch (JsonException) { return Failure("INVALID_JSON", "", "Expected a JSON object without duplicate property names."); }
        }

        private static GraphBuildResult Failure(string code, string path, string message) => new GraphBuildResult(GraphDataState.Invalid, null,
            new List<GraphDiagnostic> { new GraphDiagnostic(code, path, message) }, new List<GraphDiagnostic>());

        // Reuse validation on the canonical loader's parsed document without reparsing JSON.
        internal static GraphBuildResult BuildParsed(JObject document, GraphDataState state, string? snapshotId = null) => new Builder(document, state, snapshotId).Run();

        private sealed class Builder
        {
            private readonly JObject doc;
            private readonly GraphDataState state;
            private readonly string? snapshotId;
            private readonly List<GraphDiagnostic> errors = new List<GraphDiagnostic>();
            private readonly List<GraphDiagnostic> warnings = new List<GraphDiagnostic>();
            private readonly Dictionary<string, ComponentNode> nodes = new Dictionary<string, ComponentNode>(StringComparer.Ordinal);
            private readonly Dictionary<string, MateEdge> mates = new Dictionary<string, MateEdge>(StringComparer.Ordinal);
            private bool fixture;
            public Builder(JObject doc, GraphDataState state, string? snapshotId = null) { this.doc = doc; this.state = state; this.snapshotId = snapshotId; }
            private void Error(string code, string path, string message) => errors.Add(new GraphDiagnostic(code, path, message));
            private void Warn(string path, string message) => warnings.Add(new GraphDiagnostic("INVALID_PROPERTY", path, message));
            private static string? String(JToken? token) => token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)token) ? (string?)token : null;
            private static bool Finite(JToken? token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                && !double.IsNaN((double)token) && !double.IsInfinity((double)token);
            private string? OptionalText(JToken? token, string path)
            {
                if (token == null || token.Type == JTokenType.Null) return null;
                if (token.Type != JTokenType.String) { Warn(path, "Expected a string; treated as unknown."); return null; }
                string? value = String(token); return value == "unknown" ? null : value;
            }
            private bool? Boolean(JToken? token, string path)
            {
                if (token == null || token.Type == JTokenType.Null) return null;
                if (token.Type == JTokenType.Boolean) return (bool)token;
                Warn(path, "Expected boolean; treated as unknown, not false."); return null;
            }
            private List<string> Issues(JToken? token, string path)
            {
                if (token == null || token.Type == JTokenType.Null) return new List<string>();
                if (!(token is JArray array) || array.Any(v => String(v) == null)) { Warn(path, "Expected string issue IDs; references unavailable."); return new List<string>(); }
                return array.Select(v => (string)v!).Distinct(StringComparer.Ordinal).ToList();
            }
            private GraphQuantity Quantity(JToken? token, string? unit, bool mass, string path)
            {
                string canonical = mass ? "kg" : "m^3";
                if (fixture || token == null || token.Type == JTokenType.Null)
                    return new GraphQuantity(null, ValueState.Missing, canonical, fixture ? "Synthetic fixture engineering evidence is unavailable." : "Value not reported.");
                if (!Finite(token) || (double)token! < 0)
                { Warn(path, "Expected finite non-negative number."); return new GraphQuantity(null, ValueState.Invalid, canonical, "Malformed value."); }
                var factors = mass ? new Dictionary<string, double> { ["kg"] = 1, ["g"] = 0.001, ["lb"] = 0.45359237 }
                    : new Dictionary<string, double> { ["m"] = 1, ["cm"] = 0.000001, ["mm"] = 0.000000001, ["in"] = 0.000016387064, ["ft"] = 0.028316846592 };
                if (unit == null) return new GraphQuantity(null, ValueState.Missing, canonical, "Project unit missing; value cannot be aggregated.");
                if (!factors.TryGetValue(unit, out double factor))
                { Warn(path, "Unsupported project unit."); return new GraphQuantity(null, ValueState.Invalid, canonical, "Unsupported unit."); }
                double value = (double)token! * factor;
                if (double.IsInfinity(value)) return new GraphQuantity(null, ValueState.Invalid, canonical, "Unit conversion overflow.");
                return new GraphQuantity(value, ValueState.Available, canonical);
            }
            public GraphBuildResult Run()
            {
                if (!Enum.IsDefined(typeof(GraphDataState), state)) Error("INVALID_EXPORT_STATE", "", "Unknown export state.");
                if (state == GraphDataState.Invalid) Error("INVALID_EXPORT_STATE", "/mates", "Caller reports invalid mate export.");
                if (String(doc["schemaVersion"]) != "1.0") Error("UNSUPPORTED_SCHEMA", "/schemaVersion", "Expected metadata schema 1.0.");
                if (!(doc["project"] is JObject)) Error("INVALID_PROJECT", "/project", "Expected project metadata.");
                var sample = doc["sampleInfo"] as JObject;
                if (doc["sampleInfo"] != null && doc["sampleInfo"]!.Type != JTokenType.Null && sample == null)
                    Error("INVALID_FIXTURE_STATE", "/sampleInfo", "Expected object.");
                if (sample?["fixture"] != null && sample["fixture"]!.Type != JTokenType.Boolean)
                    Error("INVALID_FIXTURE_STATE", "/sampleInfo/fixture", "Expected boolean.");
                fixture = sample?["fixture"]?.Type == JTokenType.Boolean && (bool)sample["fixture"]!;
                var units = (doc["project"] as JObject)?["units"] as JObject;
                string? massUnit = String(units?["mass"]), lengthUnit = String(units?["length"]);
                if (!(doc["objects"] is JArray objects) || objects.Count > 10000)
                    Error("INVALID_COMPONENTS", "/objects", "Expected an array with at most 10,000 components.");
                else for (int i = 0; i < objects.Count; i++)
                {
                    string path = "/objects/" + i;
                    if (!(objects[i] is JObject o)) { Error("INVALID_COMPONENT", path, "Expected component object."); continue; }
                    string? id = String(o["id"]), name = String(o["name"]), type = String(o["type"]);
                    if (id == null || id.Length > 128 || name == null || name.Length > 512 || !(type == "part" || type == "assembly"))
                    { Error("INVALID_COMPONENT", path, "Expected id (up to 128 characters), name (up to 512), and type part/assembly."); continue; }
                    if (nodes.ContainsKey(id)) { Error("DUPLICATE_COMPONENT_ID", path + "/id", "Duplicate component ID: " + id); continue; }
                    string? material = null;
                    if (!fixture && o["material"] is JObject mat)
                    {
                        bool? assigned = Boolean(mat["assigned"], path + "/material/assigned");
                        if (assigned == true) material = OptionalText(mat["name"], path + "/material/name");
                    }
                    else if (!fixture && o["material"] != null && o["material"]!.Type != JTokenType.Null) Warn(path + "/material", "Expected material object; treated as unknown.");
                    var node = new ComponentNode(id, name, type, OptionalText(o["parentId"], path + "/parentId"),
                        Quantity(o["mass"], massUnit, true, path + "/mass"), Quantity(o["volume"], lengthUnit, false, path + "/volume"), material,
                        fixture ? null : OptionalText(o["definitionStatus"], path + "/definitionStatus"),
                        fixture ? null : Boolean(o["fixed"], path + "/fixed"), fixture ? null : Boolean(o["suppressed"], path + "/suppressed"), Issues(o["issueIds"], path + "/issueIds"));
                    nodes.Add(id, node);
                }
                var entries = doc["mates"] as JArray;
                if (entries == null)
                {
                    if (state == GraphDataState.Available || doc["mates"] != null && doc["mates"]!.Type != JTokenType.Null)
                        Error("INVALID_MATES", "/mates", "Confirmed available export requires a mates array; other non-array values are invalid.");
                }
                else if (entries.Count > 50000) Error("INVALID_MATES", "/mates", "Maximum 50,000 mates supported.");
                else
                {
                    var mateIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        string path = "/mates/" + i;
                        if (!(entries[i] is JObject m)) { Error("INVALID_MATE", path, "Expected mate object."); continue; }
                        string? id = String(m["id"]);
                        if (id == null || id.Length > 128) { Error("INVALID_MATE_ID", path + "/id", "Expected non-empty ID up to 128 characters."); continue; }
                        if (!mateIds.Add(id)) { Error("DUPLICATE_MATE_ID", path + "/id", "Duplicate mate ID: " + id); continue; }
                        if (!(m["componentIds"] is JArray ends) || ends.Count != 2 || ends.Any(e => String(e) == null) || (string?)ends[0] == (string?)ends[1])
                        { Error("INVALID_ENDPOINTS", path + "/componentIds", "Exactly two distinct component IDs are required; self-mates and N-way mates are unsupported."); continue; }
                        string a = (string)ends[0]!, b = (string)ends[1]!;
                        if (!nodes.ContainsKey(a) || !nodes.ContainsKey(b)) { Error("MISSING_COMPONENT", path + "/componentIds", "Mate references an unknown component."); continue; }
                        var references = new List<MateReference>();
                        if (m["references"] != null && m["references"]!.Type != JTokenType.Null)
                        {
                            if (!(m["references"] is JArray refs)) Error("INVALID_REFERENCES", path + "/references", "Expected reference array.");
                            else foreach (var r in refs)
                            {
                                string? owner = r is JObject rObject ? String(rObject["componentId"]) : null;
                                if (owner == null || !(owner == a || owner == b)) { Error("INVALID_REFERENCE_COMPONENT", path + "/references", "Reference must belong to one of this mate's two endpoints."); continue; }
                                references.Add(new MateReference(owner, OptionalText(r["entityType"], path + "/references/entityType"), OptionalText(r["entityId"], path + "/references/entityId")));
                            }
                        }
                        string? status = OptionalText(m["status"], path + "/status");
                        bool? suppressed = Boolean(m["suppressed"], path + "/suppressed");
                        if (string.Equals(status, "suppressed", StringComparison.OrdinalIgnoreCase))
                        {
                            if (suppressed == false) Error("INCONSISTENT_SUPPRESSION", path, "Mate status says suppressed but suppressed is false.");
                            suppressed = true;
                        }
                        GraphVector3? axis = null;
                        if (m["axis"] != null && m["axis"]!.Type != JTokenType.Null)
                        {
                            if (m["axis"] is JArray vector && vector.Count == 3 && vector.All(Finite) && vector.Any(v => (double)v != 0))
                                axis = new GraphVector3((double)vector[0], (double)vector[1], (double)vector[2], OptionalText(m["axisFrame"], path + "/axisFrame"), OptionalText(m["axisUnit"], path + "/axisUnit"));
                            else Warn(path + "/axis", "Expected three finite numbers forming a nonzero vector; axis unavailable.");
                        }
                        MateLimits? limits = null;
                        if (m["limits"] is JObject l)
                        {
                            bool? enabled = Boolean(l["enabled"], path + "/limits/enabled");
                            bool valid = (l["minimum"] == null || l["minimum"]!.Type == JTokenType.Null || Finite(l["minimum"])) && (l["maximum"] == null || l["maximum"]!.Type == JTokenType.Null || Finite(l["maximum"]));
                            double? min = Finite(l["minimum"]) ? (double?)l["minimum"] : null, max = Finite(l["maximum"]) ? (double?)l["maximum"] : null;
                            if (valid && !(min > max)) limits = new MateLimits(enabled, min, max, OptionalText(l["unit"], path + "/limits/unit"));
                            else Warn(path + "/limits", "Malformed or inverted bounds; limits unavailable.");
                        }
                        else if (m["limits"] != null && m["limits"]!.Type != JTokenType.Null) Warn(path + "/limits", "Expected limits object.");
                        mates.Add(id, new MateEdge(id, a, b, OptionalText(m["type"], path + "/type"), status, suppressed,
                            references, axis, Boolean(m["lockRotation"], path + "/lockRotation"), limits, Issues(m["issueIds"], path + "/issueIds")));
                    }
                }
                if (errors.Count > 0) return new GraphBuildResult(GraphDataState.Invalid, null, errors, warnings);
                if (fixture || state != GraphDataState.Available)
                {
                    warnings.Add(new GraphDiagnostic("MATE_DATA_UNAVAILABLE", "/mates", fixture ? "Synthetic fixture does not establish mate relationships." : "Mate extraction availability has not been established."));
                    return new GraphBuildResult(GraphDataState.Unavailable, null, errors, warnings);
                }
                foreach (var edge in mates.Values) { nodes[edge.ObjectAId].Attach(edge); nodes[edge.ObjectBId].Attach(edge); }
                return new GraphBuildResult(GraphDataState.Available, new DataStructures.MechanicalGraph.MechanicalGraph(nodes, mates, snapshotId), errors, warnings);
            }
        }
    }
}
