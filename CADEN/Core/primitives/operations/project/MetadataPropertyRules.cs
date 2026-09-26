using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.Project
{
    internal static class MetadataPropertyRules
    {
        public static readonly string[] Fields = { "sourceDocument", "configuration", "partNumber", "description", "suppressed", "fixed", "material", "mass", "volume", "centerOfMass", "inertia", "definitionStatus", "remainingDOF", "referenceGeometry", "dimensions", "customProperties" };
        private static bool Number(JToken? t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && !double.IsNaN((double)t) && !double.IsInfinity((double)t);
        private static bool Nonnegative(JToken? t) => Number(t) && (double)t! >= 0;
        private static bool Text(JToken? t) => t?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)t);
        private static bool Boolean(JToken? t) => t?.Type == JTokenType.Boolean;
        private static bool Vector(JToken? t) => t is JArray a && a.Count == 3 && a.All(Number);
        private static bool Optional(JToken? t, Func<JToken?, bool> test) => t == null || t.Type == JTokenType.Null || test(t);
        private static bool Direction(JToken? t) => Vector(t) && Math.Abs(((JArray)t!).Sum(v => Math.Pow((double)v, 2)) - 1) < 0.001;
        private static bool Dimension(JToken d)
        {
            if (!(d is JObject) || !Text(d["id"]) || !Text(d["name"]) || !Text(d["type"]) || !Number(d["value"]) || !Text(d["unit"]) || !(d["references"] is JArray)) return false;
            string type = (string)d["type"]!, unit = (string)d["unit"]!;
            if (type == "angle") return new[] { "deg", "rad" }.Contains(unit);
            return new[] { "linear", "diameter", "radius", "distance" }.Contains(type) && Nonnegative(d["value"]) && new[] { "mm", "cm", "m", "in", "ft" }.Contains(unit);
        }
        private static bool ReferenceItems(JToken? t, string vectorName) => t is JArray a && a.All(v => v is JObject && Text(v["id"]) && Text(v["name"]) && Vector(v["origin"]) && Direction(v[vectorName]));
        private static bool Valid(string field, JToken v)
        {
            switch (field)
            {
                case "sourceDocument": case "configuration": case "partNumber": case "description": return Text(v);
                case "fixed": case "suppressed": return Boolean(v);
                case "mass": case "volume": return Nonnegative(v);
                case "centerOfMass": return Vector(v);
                case "inertia": return v is JObject && new[] { "ixx", "iyy", "izz", "ixy", "ixz", "iyz" }.All(k => Number(v[k]));
                case "definitionStatus": return v.Type == JTokenType.String && new[] { "fully_defined", "under_defined", "over_defined", "no_solution", "invalid_solution" }.Contains((string)v!);
                case "material": return v is JObject && Boolean(v["assigned"]) && Optional(v["name"], Text) && Optional(v["density"], Nonnegative)
                    && ((bool)v["assigned"]! ? Text(v["name"]) : (v["name"] == null || v["name"]!.Type == JTokenType.Null) && (v["density"] == null || v["density"]!.Type == JTokenType.Null));
                case "remainingDOF": return v is JArray dofs && dofs.All(d => d is JObject && Text(d["type"]) && new[] { "translation", "rotation" }.Contains((string?)d["type"]) && Direction(d["axis"]));
                case "dimensions": return v is JArray dims && dims.All(Dimension);
                case "referenceGeometry": return v is JObject && ReferenceItems(v["axes"], "direction") && ReferenceItems(v["planes"], "normal") && v["points"] is JArray points && points.All(p => p is JObject && Text(p["id"]) && Text(p["name"]) && Vector(p["position"]));
                case "customProperties": return v is JObject props && props.Properties().All(p => Text(p.Value));
                default: return false;
            }
        }
        private static string Expected(string field) => field switch
        {
            "mass" or "volume" => "finite non-negative number with declared units",
            "centerOfMass" => "three finite numbers with declared length units and coordinate frame",
            "inertia" => "object containing six finite tensor components, explicit unit and coordinate frame",
            "suppressed" or "fixed" => "boolean",
            "material" => "object with boolean assigned, nullable string name, nullable non-negative density; assigned=true requires name",
            "definitionStatus" => "fully_defined | under_defined | over_defined | no_solution | invalid_solution | unknown",
            "remainingDOF" => "array of translation/rotation entries with normalized three-number axis vectors",
            "dimensions" => "array with id/name/references; type angle uses deg/rad, linear/diameter/radius/distance uses non-negative values in mm/cm/m/in/ft",
            "referenceGeometry" => "axes, planes, points arrays with IDs, names, three-number positions and normalized directions",
            "customProperties" => "object mapping property names to non-empty strings",
            _ => "non-empty string"
        };
        internal static JObject Read(JObject component, JObject project, bool fixture, string sourceField, string field)
        {
            if (!Fields.Contains(field)) throw new ArgumentException("Unknown property field: " + field);
            JToken? raw = component[field];
            JObject Result(string status, string reason, JToken? value = null, string? unit = null) => new JObject
            {
                ["status"] = status, ["value"] = value?.DeepClone(), ["reason"] = reason,
                ["sourceField"] = sourceField, ["expectedFormat"] = Expected(field), ["unit"] = unit
            };
            // This fixture's engineering placeholders are never evidence, including material.assigned=false.
            if (fixture) return Result("missing", "Engineering data is unavailable in this GLB-derived fixture.");
            if (raw == null || raw.Type == JTokenType.Null || raw.Type == JTokenType.String && (string.IsNullOrWhiteSpace((string?)raw) || (string?)raw == "unknown"))
                return Result("missing", "Field is absent, null, empty, or explicitly unknown.");
            if (!Valid(field, raw)) return Result("invalid", "Present value does not match the expected format. It is unavailable for reasoning.");
            if (raw is JArray a && a.Count == 0 || raw is JObject o && !o.HasValues || field == "referenceGeometry" && ((JObject)raw).Properties().All(p => p.Value is JArray list && list.Count == 0))
                return Result("missing", "No entries were reported; export coverage is not established. This is not proof that none exist.");
            string? unit = null;
            var units = project["units"] as JObject;
            if (field == "mass" || field == "volume" || field == "centerOfMass")
            {
                string key = field == "mass" ? "mass" : "length";
                JToken? declared = units?[key];
                string[] accepted = key == "mass" ? new[] { "kg", "g", "lb" } : new[] { "mm", "cm", "m", "in", "ft" };
                if (declared?.Type != JTokenType.String || !accepted.Contains((string)declared!))
                    return Result("invalid", "A supported project unit is required before interpreting this numeric value.");
                unit = (string)declared! + (field == "volume" ? "^3" : "");
            }
            // The current export schema does not establish frames/tensor or density units reliably.
            if (new[] { "centerOfMass", "inertia", "remainingDOF", "referenceGeometry" }.Contains(field))
                return Result("missing", "Values were reported, but the current schema does not establish the coordinate frame (and inertia unit). Spatial interpretation is unavailable.");
            if (field == "material")
            {
                var material = (JObject)raw.DeepClone();
                bool densityPresent = material["density"] != null && material["density"]!.Type != JTokenType.Null;
                material["density"] = JValue.CreateNull();
                material["densityStatus"] = "missing";
                material["densityReason"] = densityPresent ? "Density reported without an explicit density-unit contract; unavailable for calculations." : "Density was not reported.";
                return Result("available", "Material assignment is explicitly reported; density availability is described separately.", material);
            }
            return Result("available", "Present in the export and valid for this query contract; not independently verified.", raw, unit);
        }
    }
}
