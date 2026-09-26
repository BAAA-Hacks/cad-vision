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
        private static string ExtractionKey(string field) => new[] { "mass", "volume", "centerOfMass", "inertia" }.Contains(field) ? "massProperties" : field;
        internal static JObject SourceEvidence(JObject component, string field)
        {
            var evidence = new JObject { ["extractionStatus"] = (component["extractionStatus"] as JObject)?[ExtractionKey(field)]?.DeepClone() };
            foreach (string key in field switch {
                "definitionStatus" => new[] { "nativeConstrainedStatus" }, "fixed" => new[] { "fixedState" },
                "mass" or "volume" or "centerOfMass" or "inertia" => new[] { "extractionStatus" },
                "material" => new[] { "documentMaterial", "bodyMaterials" }, "partNumber" => new[] { "partNumberSource" }, _ => Array.Empty<string>() })
                evidence[key] = component[key]?.DeepClone();
            return evidence;
        }
        internal static JObject Read(JObject component, JObject project, bool fixture, string sourceField, string field, string schemaVersion = "1.0", JObject? export = null)
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
            bool modern = schemaVersion == "2.1";
            if (modern && field == "fixed" && component["fixedState"]?.Type == JTokenType.String && (string?)component["fixedState"] == "not_applicable_root_document")
            {
                bool root = component["id"]?.Type == JTokenType.String && JToken.DeepEquals(component["id"], project["rootObjectId"])
                    && (string?)component["type"] == "assembly" && component["parentId"]?.Type == JTokenType.Null;
                return root && (raw == null || raw.Type == JTokenType.Null) ? Result("not_applicable", "Exporter explicitly marks fixed state inapplicable to the root document.")
                    : Result("invalid", "not_applicable_root_document contradicts object identity or a supplied fixed boolean.");
            }
            if (modern && field == "material" && raw is JObject materialUnknown && (materialUnknown["assigned"] == null || materialUnknown["assigned"]!.Type == JTokenType.Null))
                return Optional(materialUnknown["name"], Text) && Optional(materialUnknown["density"], Nonnegative)
                    ? Result("missing", "Material assignment is unknown; it is not an explicit unassigned material.")
                    : Result("invalid", "Unknown material assignment contains malformed material fields.");
            if (raw == null || raw.Type == JTokenType.Null || raw.Type == JTokenType.String && (string.IsNullOrWhiteSpace((string?)raw) || (string?)raw == "unknown"))
                return Result("missing", "Field is absent, null, empty, or explicitly unknown.");
            if (!Valid(field, raw)) return Result("invalid", "Present value does not match the expected format. It is unavailable for reasoning.");
            JToken? extraction = (component["extractionStatus"] as JObject)?[ExtractionKey(field)];
            if (modern && component["extractionStatus"] != null && component["extractionStatus"]!.Type != JTokenType.Null && !(component["extractionStatus"] is JObject))
                return Result("invalid", "Malformed extractionStatus; reported values cannot be certified.");
            if (modern && extraction != null && extraction.Type != JTokenType.Null)
            {
                if (extraction.Type != JTokenType.String) return Result("invalid", "Extraction status must be a string.");
                if ((string?)extraction != "complete") return Result("missing", "Extraction is not complete: " + (string?)extraction + ". Raw values are retained as source evidence.");
            }
            bool empty = raw is JArray a && a.Count == 0 || raw is JObject o && !o.HasValues || field == "referenceGeometry" && ((JObject)raw).Properties().All(p => p.Value is JArray list && list.Count == 0);
            if (empty && modern && (string?)extraction == "complete") return Result("available", "Exporter explicitly reports complete extraction with no entries.", raw);
            if (empty)
                return Result("missing", "No entries were reported; export coverage is not established. This is not proof that none exist.");
            string? unit = null;
            var units = project["units"] as JObject;
            if (field == "mass" || field == "volume" || field == "centerOfMass")
            {
                string key = field == "mass" ? "mass" : modern && field == "volume" ? "volume" : "length";
                JToken? declared = units?[key];
                string[] accepted = key == "mass" ? new[] { "kg", "g", "lb" } : key == "volume" ? new[] { "mm^3", "cm^3", "m^3", "in^3", "ft^3" } : new[] { "mm", "cm", "m", "in", "ft" };
                if (declared?.Type != JTokenType.String || !accepted.Contains((string)declared!))
                    return Result("invalid", "A supported project unit is required before interpreting this numeric value.");
                unit = (string)declared! + (field == "volume" && !modern ? "^3" : "");
            }
            // Map only the exact, versioned exporter declaration. Arbitrary prose is not a frame contract.
            const string rootFrameDeclaration = "SolidWorks root document axes; positions/transform translations in project.units.length; inertia about center of mass; directions and rotations dimensionless";
            if (modern && (field == "centerOfMass" || field == "inertia") && export?["coordinateSystem"]?.Type == JTokenType.String
                && (string?)export["coordinateSystem"] == rootFrameDeclaration && Text(project["rootObjectId"]) && (string?)extraction == "complete")
            {
                if (field == "inertia")
                {
                    JToken? declared = units?["inertia"];
                    var supported = new[] { "kg", "g", "lb" }.SelectMany(m => new[] { "mm", "cm", "m", "in", "ft" }.Select(l => m + "*" + l + "^2"));
                    if (declared?.Type != JTokenType.String || !supported.Contains((string)declared!))
                        return Result("invalid", "A supported explicit inertia unit (mass*length^2) is required.");
                    unit = (string)declared!;
                }
                var mapped = Result("available", "Exported value interpreted using the schema 2.1 root-document-axis declaration; source coordinates and units are preserved without conversion or independent verification.", raw, unit);
                mapped["coordinateFrame"] = "solidworks_root_document_axes";
                mapped["spatialReference"] = new JObject {
                    ["frameObjectId"] = project["rootObjectId"]!.DeepClone(),
                    ["axes"] = "root_document_axes",
                    ["referencePoint"] = field == "inertia" ? "object_center_of_mass" : "root_document_origin",
                    ["sourceField"] = "/coordinateSystem", ["mapping"] = "solidworks_schema_2.1_root_axes_v1",
                    ["verification"] = "exporter_declared",
                    ["componentConvention"] = field == "inertia" ? "exported_ixx_iyy_izz_ixy_ixz_iyz_preserved" : "xyz" };
                return mapped;
            }
            if (new[] { "centerOfMass", "inertia", "remainingDOF", "referenceGeometry" }.Contains(field))
            {
                var result = Result("missing", "Values are present in the export, but CADEN has not mapped their spatial reference semantics. This is an importer limitation, not evidence that the exported values or units are absent.", unit: unit);
                result["reasonCode"] = "SPATIAL_REFERENCE_UNMAPPED";
                return result;
            }
            if (field == "material")
            {
                var material = (JObject)raw.DeepClone();
                bool densityPresent = material["density"] != null && material["density"]!.Type != JTokenType.Null;
                if (modern && densityPresent && units?["density"]?.Type == JTokenType.String && new[] { "kg/m^3", "g/cm^3", "g/mm^3", "lb/in^3", "lb/ft^3" }.Contains((string)units["density"]!))
                {
                    material["densityStatus"] = "available"; material["densityUnit"] = units["density"]!.DeepClone();
                    material["densityReason"] = "Reported material density with explicit project density unit; not inferred from effectiveDensity.";
                    return Result("available", "Explicit material assignment and density units.", material);
                }
                material["density"] = JValue.CreateNull();
                material["densityStatus"] = "missing";
                material["densityReason"] = densityPresent ? "Density reported without an explicit density-unit contract; unavailable for calculations." : "Density was not reported.";
                return Result("available", "Material assignment is explicitly reported; density availability is described separately.", material);
            }
            return Result("available", "Present in the export and valid for this query contract; not independently verified.", raw, unit);
        }
    }
}
