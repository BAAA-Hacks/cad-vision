using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    public static class SemanticQueryTools
    {
        public static ToolRegistry Create(ProjectSnapshot? snapshot)
        {
            var hierarchy = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (snapshot != null) foreach (var component in snapshot.ComponentsById.Values)
            {
                var raw = component.CopyRawRecord();
                hierarchy.Add(component.Id, new JObject { ["parentId"] = raw["parentId"], ["childIds"] = raw["childIds"] });
            }
            return new ToolRegistry(new[] { "get_model_summary", "get_object_details", "find_objects", "query_hierarchy" }
                .Select(name => (ICadenTool)new SemanticQueryTool(name, snapshot, hierarchy)), snapshot, semantic: true);
        }
    }

    internal sealed class SemanticQueryTool : IAsyncCadenTool
    {
        private readonly ProjectSnapshot? snapshot;
        private readonly Dictionary<string, JObject> records;
        private ProjectSnapshot Store => snapshot ?? throw new ToolInputException("MODEL_NOT_LOADED", "Load metadata first.");
        public string Name { get; }
        private static readonly string[] Fields = { "name", "type", "parentId", "sourceDocument", "configuration", "partNumber", "description", "suppressed", "fixed", "material", "mass", "volume", "centerOfMass", "inertia", "constraintStatus", "remainingDOF", "dimensions", "referenceGeometry", "customProperties" };
        private static readonly string[] Defaults = { "suppressed", "fixed", "material", "mass", "constraintStatus" };
        public SemanticQueryTool(string name, ProjectSnapshot? snapshot, Dictionary<string, JObject> hierarchy)
        {
            Name = name; this.snapshot = snapshot; records = hierarchy;
        }
        private static JObject String(params string[] values) => values.Length == 0 ? new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }
            : new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        private static JObject Array(JObject items, int max = 32, int min = 0) => new JObject { ["type"] = "array", ["items"] = items, ["maxItems"] = max, ["minItems"] = min };
        private static JObject Integer(int min, int max) => new JObject { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max };
        private static JObject Operand() => new JObject { ["type"] = "object", ["properties"] = new JObject
        { ["text"] = String(), ["number"] = new JObject { ["type"] = "number" }, ["boolean"] = new JObject { ["type"] = "boolean" }, ["unit"] = String() } };
        public JObject Declaration
        {
            get
            {
                var p = new JObject(); var required = new JArray(); string description;
                if (Name != "get_model_summary") { p["snapshotId"] = String(); required.Add("snapshotId"); }
                if (Name == "get_model_summary") description = "Discover the loaded project's snapshot ID, identity scope, counts and capability states. Call first; use its snapshotId in all other queries. Counts describe exported occurrences, not verified BOM quantities.";
                else if (Name == "get_object_details")
                {
                    description = "Read known object IDs with compact identity and requested availability-wrapped properties. Unknown fields are errors; missing values are not false or zero. Default fields: suppressed, fixed, material, mass, constraintStatus.";
                    p["objectIds"] = Array(String(), min: 1); required.Add("objectIds"); p["fields"] = Array(String(Fields), min: 1);
                }
                else if (Name == "find_objects")
                {
                    description = "Find objects by query (case-insensitive ID/name substrings; every word matches), OR one property/operator filter. Use value={text:...}, {boolean:...}, or {number:...,unit:...}; in uses values=[...]. Numeric mass/volume operands require units. Text equality is ordinal case-sensitive. Unknown values never match, including not_equals. scopeObjectIds limits to exact IDs; omitted/null means all; [] means none. Inspect coverage and pagination.";
                    p["query"] = String(); p["property"] = String(Fields.Where(f => new[] { "name", "type", "sourceDocument", "configuration", "partNumber", "description", "suppressed", "fixed", "mass", "volume", "constraintStatus", "parentId" }.Contains(f)).Concat(new[] { "material.name", "material.assigned" }).ToArray());
                    p["operator"] = String("equals", "not_equals", "greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal", "in");
                    p["value"] = Operand(); p["values"] = Array(Operand(), min: 1);
                    p["scopeObjectIds"] = Array(String(), 256); p["scopeObjectIds"]!["nullable"] = true;
                    p["limit"] = Integer(1, 50); p["offset"] = Integer(0, 10000);
                }
                else
                {
                    description = "Read authoritative metadata containment, never mechanical connectivity. parent/children return depth 1; ancestors/descendants allow maxDepth 0-32 (default 1). Start object excluded. Depth 0 returns no relatives. Inspect depthLimited separately from pagination; invalid/unavailable hierarchy returns unavailable items, not a confirmed empty tree.";
                    p["objectId"] = String(); required.Add("objectId"); p["direction"] = String("parent", "children", "ancestors", "descendants"); required.Add("direction");
                    p["maxDepth"] = Integer(0, 32); p["limit"] = Integer(1, 50); p["offset"] = Integer(0, 10000);
                }
                return new JObject { ["name"] = Name, ["description"] = description, ["parameters"] = new JObject { ["type"] = "object", ["properties"] = p, ["required"] = required } };
            }
        }
        public JObject Execute(JObject arguments) => Run(arguments, CancellationToken.None);
        public Task<JObject> ExecuteAsync(JObject arguments, CancellationToken cancellationToken) => Task.FromResult(Run(arguments, cancellationToken));
        private JObject Run(JObject args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (Name)
            {
                case "get_model_summary": return Summary();
                case "get_object_details": return Details(args, token);
                case "find_objects": return Find(args, token);
                default: return Hierarchy(args, token);
            }
        }
        private ComponentMetadata Get(string id) => Store.ComponentsById.TryGetValue(id, out var component) ? component : throw new ToolInputException("OBJECT_NOT_FOUND", "Unknown object ID: " + id);
        private static JObject Coverage(string status, int evaluated, int unknown) => new JObject { ["status"] = status, ["evaluatedCount"] = evaluated, ["unknownCount"] = unknown };
        private JObject Summary() => new JObject
        {
            ["name"] = Store.Name, ["rootObjectId"] = Store.Capabilities.Hierarchy == CapabilityState.Available ? Store.CopyProjectMetadata()["rootObjectId"] : null,
            ["objectCount"] = Store.ComponentsById.Count, ["partCount"] = Store.ComponentsById.Values.Count(c => c.Type == "part"),
            ["assemblyCount"] = Store.ComponentsById.Values.Count(c => c.Type == "assembly"),
            ["capabilities"] = new JObject { ["properties"] = Store.Capabilities.Properties.ToString(), ["hierarchy"] = Store.Capabilities.Hierarchy.ToString(), ["mechanicalGraph"] = Store.Capabilities.MechanicalGraph.ToString(), ["issueTools"] = "Unavailable" },
            ["loadDiagnostics"] = new JObject { ["count"] = Store.LoadDiagnostics.Count, ["codes"] = new JArray(Store.LoadDiagnostics.Select(d => d.Code).Distinct().Take(32)) },
            ["coverage"] = Coverage("Complete", Store.ComponentsById.Count, 0)
        };
        private JObject Identity(string id)
        {
            var c = Get(id); var result = new JObject { ["id"] = c.Id, ["name"] = c.Name, ["type"] = c.Type, ["parentId"] = Property(c, "parentId") };
            if (Store.Capabilities.Hierarchy == CapabilityState.Available)
            {
                var path = new List<JObject>(); string? current = id;
                while (current != null) { var ancestor = Get(current); path.Add(new JObject { ["id"] = current, ["name"] = ancestor.Name }); current = (string?)records[current]["parentId"]; }
                path.Reverse(); result["path"] = new JArray(path); result["childCount"] = ((JArray)records[id]["childIds"]!).Count;
            }
            else { result["path"] = null; result["childCount"] = null; }
            return result;
        }
        private JObject Property(ComponentMetadata c, string field)
        {
            if (!Fields.Contains(field) && field != "material.name" && field != "material.assigned") throw new ToolInputException("INVALID_ARGUMENTS", "Unknown public property: " + field);
            if (field == "name" || field == "type" || field == "parentId")
            {
                bool usable = field != "parentId" || Store.Capabilities.Hierarchy == CapabilityState.Available;
                return new JObject { ["status"] = usable ? "available" : Store.Capabilities.Hierarchy == CapabilityState.Invalid ? "invalid" : "missing",
                    ["value"] = usable ? field == "name" ? new JValue(c.Name) : field == "type" ? new JValue(c.Type) : records[c.Id]["parentId"]?.DeepClone() : null,
                    ["reason"] = usable ? "Validated metadata identity/hierarchy." : "Hierarchy is " + Store.Capabilities.Hierarchy,
                    ["sourceField"] = c.Properties["mass"].Provenance.SourceField.Replace("/mass", "/" + field), ["unit"] = null };
            }
            string source = field == "constraintStatus" ? "definitionStatus" : field.StartsWith("material.", StringComparison.Ordinal) ? "material" : field;
            var value = c.Properties[source]; var data = value.Value;
            if (field.StartsWith("material.", StringComparison.Ordinal)) data = data?[field.Substring(9)];
            string state = value.State == AvailabilityState.NotApplicable ? "not_applicable" : value.State.ToString().ToLowerInvariant();
            if (state == "available" && (data == null || data.Type == JTokenType.Null)) state = "missing";
            return new JObject { ["status"] = state, ["value"] = state == "available" ? data : null,
                ["reason"] = state == "missing" && value.State == AvailabilityState.Available ? "Nested field was not reported." : value.Reason,
                ["unit"] = value.Unit, ["coordinateFrame"] = value.CoordinateFrame, ["sourceField"] = value.Provenance.SourceField + (field.StartsWith("material.", StringComparison.Ordinal) ? "/" + field.Substring(9) : ""),
                ["expectedFormat"] = value.ExpectedFormat };
        }
        private JObject Details(JObject args, CancellationToken token)
        {
            var ids = ((JArray)args["objectIds"]!).Select(v => (string)v!).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var id in ids) Get(id); // Reject whole request if any ID is stale/unknown.
            var fields = args["fields"] is JArray requested ? requested.Select(v => (string)v!).Distinct().ToArray() : Defaults;
            var items = new JArray();
            foreach (var id in ids) { token.ThrowIfCancellationRequested(); var item = Identity(id); var props = new JObject(); foreach (var field in fields) props[field] = Property(Get(id), field); item["properties"] = props; items.Add(item); }
            return new JObject { ["items"] = items, ["coverage"] = Coverage("Complete", ids.Length, 0) };
        }
        private static JObject Page(List<JObject> rows, JObject args)
        {
            int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 20;
            var page = rows.Skip(offset).Take(limit).ToArray(); bool more = offset + page.Length < rows.Count;
            return new JObject { ["items"] = new JArray(page), ["total"] = rows.Count, ["offset"] = offset, ["limit"] = limit,
                ["truncated"] = more, ["nextOffset"] = more ? new JValue(offset + page.Length) : JValue.CreateNull() };
        }
        private static string PropertyType(string field) => field == "mass" || field == "volume" ? "number" : field == "suppressed" || field == "fixed" || field == "material.assigned" ? "boolean" : "text";
        private static double UnitFactor(string property, string? unit)
        {
            var factors = property == "mass" ? new Dictionary<string, double> { ["kg"] = 1, ["g"] = 0.001, ["lb"] = 0.45359237 }
                : new Dictionary<string, double> { ["m^3"] = 1, ["cm^3"] = 1e-6, ["mm^3"] = 1e-9, ["in^3"] = 0.000016387064, ["ft^3"] = 0.028316846592 };
            return unit != null && factors.TryGetValue(unit, out var factor) ? factor : throw new ToolInputException("INVALID_ARGUMENTS", "A supported " + property + " unit is required.");
        }
        private static JToken OperandValue(JObject operand, string property)
        {
            string type = PropertyType(property);
            if (operand[type] == null || operand.Properties().Count(p => p.Name != "unit") != 1 || type != "number" && operand["unit"] != null)
                throw new ToolInputException("INVALID_ARGUMENTS", "Supply exactly one " + type + " operand for " + property + ".");
            if (type != "number") return operand[type]!;
            double number = (double)operand[type]! * UnitFactor(property, (string?)operand["unit"]);
            if (double.IsInfinity(number) || double.IsNaN(number)) throw new ToolInputException("INVALID_ARGUMENTS", "Normalized operand overflowed.");
            return new JValue(number);
        }
        private JObject Find(JObject args, CancellationToken token)
        {
            string? query = (string?)args["query"], property = (string?)args["property"], op = (string?)args["operator"];
            bool lexical = query != null;
            if (lexical && (string.IsNullOrWhiteSpace(query) || property != null || op != null || args["value"] != null || args["values"] != null)
                || !lexical && (property == null || op == null)) throw new ToolInputException("INVALID_ARGUMENTS", "Choose a nonblank query OR a property/operator filter.");
            var operands = new List<JToken>();
            if (!lexical)
            {
                if (op == "in")
                {
                    if (!(args["values"] is JArray values) || args["value"] != null) throw new ToolInputException("INVALID_ARGUMENTS", "in requires values, not value.");
                    operands.AddRange(values.Cast<JObject>().Select(v => OperandValue(v, property!)));
                }
                else
                {
                    if (!(args["value"] is JObject value) || args["values"] != null) throw new ToolInputException("INVALID_ARGUMENTS", "This operator requires value, not values.");
                    operands.Add(OperandValue(value, property!));
                }
                if (op != "in" && op != "equals" && op != "not_equals" && PropertyType(property!) != "number") throw new ToolInputException("INVALID_ARGUMENTS", "Ordered comparisons require a numeric property.");
            }
            var scoped = args["scopeObjectIds"] is JArray scope ? scope.Select(v => Get((string)v!)).Distinct().ToArray() : Store.ComponentsById.Values.ToArray();
            var rows = new List<JObject>(); int unknown = 0; var unknownIds = new List<string>();
            string[] words = query?.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? System.Array.Empty<string>();
            foreach (var c in scoped.OrderBy(c => lexical && string.Equals(c.Id, query?.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); bool match;
                if (lexical) match = words.All(w => (c.Id + " " + c.Name).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                else
                {
                    var wrapped = Property(c, property!); var actual = wrapped["value"];
                    if ((string?)wrapped["status"] != "available" || actual == null || actual.Type == JTokenType.Null)
                    { unknown++; if (unknownIds.Count < 32) unknownIds.Add(c.Id); continue; }
                    if (PropertyType(property!) == "number")
                    {
                        double normalized = (double)actual * UnitFactor(property!, (string?)wrapped["unit"]);
                        if (double.IsInfinity(normalized) || double.IsNaN(normalized)) { unknown++; if (unknownIds.Count < 32) unknownIds.Add(c.Id); continue; }
                        actual = new JValue(normalized);
                    }
                    int Compare(JToken operand) => PropertyType(property!) == "number" ? ((double)actual).CompareTo((double)operand) : PropertyType(property!) == "boolean" ? ((bool)actual).CompareTo((bool)operand) : StringComparer.Ordinal.Compare((string?)actual, (string?)operand);
                    match = op == "in" ? operands.Any(v => Compare(v) == 0) : op switch
                    { "equals" => Compare(operands[0]) == 0, "not_equals" => Compare(operands[0]) != 0, "greater_than" => Compare(operands[0]) > 0, "greater_than_or_equal" => Compare(operands[0]) >= 0, "less_than" => Compare(operands[0]) < 0, _ => Compare(operands[0]) <= 0 };
                }
                if (match) rows.Add(Identity(c.Id));
            }
            var result = Page(rows, args); result["coverage"] = Coverage(unknown == 0 ? "Complete" : "Partial", scoped.Length - unknown, unknown);
            result["coverage"]!["scopeCount"] = scoped.Length; result["coverage"]!["unknownObjectIds"] = new JArray(unknownIds); result["coverage"]!["unknownIdsTruncated"] = unknown > unknownIds.Count;
            return result;
        }
        private JObject Hierarchy(JObject args, CancellationToken token)
        {
            string id = (string)args["objectId"]!, direction = (string)args["direction"]!; Get(id);
            int depth = (int?)args["maxDepth"] ?? 1;
            if ((direction == "parent" || direction == "children") && depth != 1) throw new ToolInputException("INVALID_ARGUMENTS", "parent/children require maxDepth=1; use ancestors/descendants for other depths.");
            if (Store.Capabilities.Hierarchy != CapabilityState.Available) return new JObject { ["items"] = null, ["coverage"] = new JObject { ["status"] = Store.Capabilities.Hierarchy.ToString(), ["reason"] = "Metadata hierarchy is not usable; no empty-tree conclusion is supported." } };
            bool down = direction == "children" || direction == "descendants", limited = false;
            IEnumerable<string> Adjacent(string current) => down ? ((JArray)records[current]["childIds"]!).Select(v => (string)v!) : records[current]["parentId"]?.Type == JTokenType.String ? new[] { (string)records[current]["parentId"]! } : System.Array.Empty<string>();
            var queue = new Queue<(string Id, int Depth)>(); queue.Enqueue((id, 0)); var rows = new List<JObject>();
            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var current = queue.Dequeue();
                if (current.Depth > 0) { var row = Identity(current.Id); row["depth"] = current.Depth; rows.Add(row); }
                var adjacent = Adjacent(current.Id).ToArray();
                if (current.Depth == depth) { if (adjacent.Length > 0) limited = true; }
                else foreach (var next in adjacent) queue.Enqueue((next, current.Depth + 1));
            }
            var result = Page(rows, args); result["root"] = Identity(id); result["direction"] = direction; result["maxDepth"] = depth;
            result["depthLimited"] = limited; result["coverage"] = Coverage("Complete", rows.Count, 0); return result;
        }
    }
}
