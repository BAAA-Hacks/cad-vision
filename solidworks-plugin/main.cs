// CADVision SolidWorks GLB + metadata exporter â€” single-file edition.
// Target: Windows x64, .NET Framework 4.8, SolidWorks 2020+.
// References: System.Core, System.Web.Extensions, System.Xml, System.Xml.Linq,
// SolidWorks.Interop.sldworks.dll, SolidWorks.Interop.swconst.dll.
// Compile as a console application. Do not include the separate source files too.
// Usage: CADVision.Export.exe [new-output-directory] [--skip-interferences] [--shipper CadenShipper.exe]
// Requires a running SolidWorks instance and a saved active part or assembly.
// Data flow: attach -> native GLB -> current metadata -> publish pair -> optional shipper.
// Console runner; add-in button/registration and Quest networking are not implemented here.
// Read IMPLEMENTATION_STATUS.md for capabilities, limitations, and test evidence.
// Repeated namespace and partial-class blocks are valid together in one .cs file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Threading;
using System.Diagnostics;
namespace CADVision.SolidWorks
{
    // Names deliberately match the interchange contract, including null-valued fields.
    // PART 1: JSON DATA MODELS
    // Plain data containers: public fields become JSON properties. They do not call SolidWorks.
    public sealed class Metadata
    {
        public string schemaVersion = "1.0";
        public Project project;
        public List<CadObject> objects = new List<CadObject>();
        public List<Mate> mates = new List<Mate>();
        public List<InterferenceRecord> interferences = null;
        public Dictionary<string, string> extractionStatus = new Dictionary<string, string>();
        public List<string> warnings = new List<string>();
        public List<string> notices = new List<string>();
        public string mappingStatus = "not_correlated_to_glb";
        public GlbAsset glbAsset;
        public MappingReport glbMapping;
        public string coordinateSystem = "SolidWorks root assembly; SI; inertia about center of mass aligned with root axes";
        public string valuePolicy = "Prefer authoritative SolidWorks API values; only normalize representation or derive unavailable fields with explicit provenance.";

        // Check IDs, parent/child links, tree connectivity, and mate targets.
        // This validates the exported structure, not the correctness of the CAD design.
        public void Validate()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in objects)
                if (String.IsNullOrEmpty(o.id) || !ids.Add(o.id)) throw new InvalidDataException("Duplicate or empty object ID.");
            var byId = objects.ToDictionary(o => o.id);
            if (project == null || !byId.ContainsKey(project.rootObjectId)) throw new InvalidDataException("Missing root.");
            var root = byId[project.rootObjectId];
            if (root.parentId != null) throw new InvalidDataException("Root has a parent.");
            foreach (var o in objects)
            {
                if (o != root && (o.parentId == null || !byId.ContainsKey(o.parentId) || !byId[o.parentId].childIds.Contains(o.id)))
                    throw new InvalidDataException("Broken parent link: " + o.id);
                if (o.childIds.Distinct().Count() != o.childIds.Count) throw new InvalidDataException("Duplicate child.");
                foreach (var child in o.childIds)
                    if (!byId.ContainsKey(child) || byId[child].parentId != o.id) throw new InvalidDataException("Broken child link.");
            }
            var visited = new HashSet<string>();
            Action<string> walk = null;
            walk = id => { if (!visited.Add(id)) throw new InvalidDataException("Hierarchy cycle."); foreach (var child in byId[id].childIds) walk(child); };
            walk(root.id);
            if (visited.Count != objects.Count) throw new InvalidDataException("Disconnected hierarchy.");
            var mateIds = new HashSet<string>();
            foreach (var m in mates)
            {
                if (!mateIds.Add(m.id)) throw new InvalidDataException("Duplicate mate ID.");
                if (m.componentIds.Any(id => !ids.Contains(id)) || m.references.Any(r => r.componentId != null && !ids.Contains(r.componentId)))
                    throw new InvalidDataException("Mate references an unknown object.");
            }
        }

        // Validate, then serialize as UTF-8 JSON. Null values mean unavailable/not assessed;
        // they must not be confused with measured zero or a completed empty result.
        public void Write(string path)
        {
            Validate();
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 };
            // CreateNew prevents accidentally replacing another design's metadata.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(serializer.Serialize(this));
        }
    }
    // SI during extraction; declared document units replace these defaults before export.
    public sealed class Project
    {
        public string id, name, rootObjectId;
        public DocumentUnits documentUnits;
        public Dictionary<string, string> units = new Dictionary<string, string> {
            {"length", "m"}, {"mass", "kg"}, {"angle", "rad"}, {"volume", "m^3"}, {"density", "kg/m^3"}, {"inertia", "kg*m^2"}
        };
    }
    // One record per occurrence: two copies of a wheel have distinct IDs and transforms.
    // Nullable fields (bool?, double?) preserve unknown values. @fixed escapes a C# keyword.
    // transform uses SolidWorks' native array layout, not a glTF matrix.
    public sealed class CadObject
    {
        public string id, name, type, parentId, sourceDocument, configuration, partNumber, description;
        public string occurrencePath, identityBasis;
        public List<string> childIds = new List<string>();
        public bool? suppressed, @fixed;
        public Material material = new Material();
        public Material documentMaterial;
        public double? mass, volume;
        public double? effectiveDensity;
        public string effectiveDensitySource, partNumberSource;
        public double[] centerOfMass, transform;
        public Inertia inertia;
        public string definitionStatus = "unknown";
        // Preserve the actual native enum alongside its normalized schema value.
        public NativeStatus nativeConstrainedStatus;
        public string fixedState = "unknown";
        public object remainingDOF = null;
        public ReferenceGeometry referenceGeometry;
        public List<DimensionRecord> dimensions;
        public List<BodyMaterial> bodyMaterials;
        public Dictionary<string, string> extractionStatus = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> featureProperties = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, string> customProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> documentProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> configurationProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class Material { public bool? assigned; public string name; public double? density; public string database, densitySource; }
    public sealed class BodyMaterial { public string id, name; public Material material; }
    public sealed class ReferenceGeometry
    {
        public List<AxisRecord> axes = new List<AxisRecord>();
        public List<PlaneRecord> planes = new List<PlaneRecord>();
        public List<PointRecord> points = new List<PointRecord>();
    }
    // Identifies the exact visualization file in this pair, not a node-to-CAD mapping.
    public sealed class GlbAsset
    {
        public string file = "model.glb", sha256, sourceMode, originalFileName;
        public long byteLength;
        public string correspondenceStatus = "unverified";
        public string lengthUnitConvention = "m", scaleVerification = "not_verified";
    }
    public sealed class MappingReport
    {
        public string method = "exact_parent_hierarchy_name_and_world_transform";
        public string glbSha256, status;
        public double positionToleranceMeters = 0.00001, matrixTolerance = 0.00001;
        public string verificationScope = "node correspondence only; geometry, CAD revision and Unity axis conversion are not verified";
        public List<NodeMapping> objects = new List<NodeMapping>();
        public List<int> unmappedMeshNodeIndices = new List<int>();
    }
    public sealed class NodeMapping
    {
        public string objectId, status = "unmatched", reason;
        public int? glbNodeIndex;
        public string glbNodeName, glbNodePath;
        public List<int> candidateNodeIndices = new List<int>();
        public double? positionErrorMeters, matrixError;
    }
    // Version 2 uses the active root document's units for every numeric field.
    // ToSI factors are for metadata only, not an instruction to scale a GLB mesh.
    public sealed class DocumentUnits
    {
        public string system, length, mass, angle;
        public int nativeSystemCode;
        public double lengthToMeters, massToKilograms, angleToRadians;
        public string scope = "active_root_document_for_entire_export";
        public string source = "SolidWorks document preferences and IUserUnit; exact mass unit definitions";
        public string glbScaleStatus = "unverified_GLTF_specifies_meters_do_not_apply_metadata_scale_to_mesh";
        public Dictionary<string, double> toSI;
    }
    public static class DocumentUnitConversion
    {
        public static DocumentUnits Create(int system, bool radians)
        {
            var u = new DocumentUnits { nativeSystemCode = system, angle = radians ? "rad" : "deg", angleToRadians = radians ? 1 : Math.PI / 180 };
            switch ((swUnitSystem_e)system)
            {
                case swUnitSystem_e.swUnitSystem_IPS: u.system = "IPS"; u.length = "in"; u.mass = "lb"; u.lengthToMeters = .0254; u.massToKilograms = .45359237; break;
                case swUnitSystem_e.swUnitSystem_MMGS: u.system = "MMGS"; u.length = "mm"; u.mass = "g"; u.lengthToMeters = .001; u.massToKilograms = .001; break;
                case swUnitSystem_e.swUnitSystem_MKS: u.system = "MKS"; u.length = "m"; u.mass = "kg"; u.lengthToMeters = 1; u.massToKilograms = 1; break;
                case swUnitSystem_e.swUnitSystem_CGS: u.system = "CGS"; u.length = "cm"; u.mass = "g"; u.lengthToMeters = .01; u.massToKilograms = .001; break;
                default: throw new InvalidDataException("Custom/unknown document unit system is not supported yet; refusing to label values with guessed units.");
            }
            return u;
        }
        // This changes representation only: engineering values were read from native APIs.
        // All transforms/reference positions are converted after native coordinate operations.
        public static void Apply(Metadata data, DocumentUnits u)
        {
            if (data.schemaVersion != "1.0") throw new InvalidDataException("Unit conversion may run only once on SI metadata.");
            var factors = new[] { u.lengthToMeters, u.massToKilograms, u.angleToRadians };
            if (factors.Any(x => Double.IsNaN(x) || Double.IsInfinity(x) || x <= 0)) throw new InvalidDataException("Invalid document unit factors.");
            double l = u.lengthToMeters, m = u.massToKilograms, a = u.angleToRadians, v = l*l*l, inertia = m*l*l;
            u.toSI = new Dictionary<string, double> { {"length",l}, {"mass",m}, {"angle",a}, {"volume",v}, {"density",m/v}, {"inertia",inertia} };
            data.project.documentUnits = u;
            data.project.units = new Dictionary<string, string> { {"length",u.length}, {"mass",u.mass}, {"angle",u.angle}, {"volume",u.length+"^3"}, {"density",u.mass+"/"+u.length+"^3"}, {"inertia",u.mass+"*"+u.length+"^2"} };
            var materials = new HashSet<Material>();
            Action<Material> convertMaterial = material => {
                if (material != null && materials.Add(material)) material.density /= m/v;
            };
            foreach (var o in data.objects)
            {
                o.mass /= m; o.volume /= v; o.effectiveDensity /= m/v;
                o.centerOfMass = Scale(o.centerOfMass, l);
                if (o.transform != null)
                {
                    o.transform = (double[])o.transform.Clone();
                    for (int i = 9; i < 12; i++) o.transform[i] /= l;
                }
                if (o.inertia != null)
                {
                    o.inertia.ixx /= inertia; o.inertia.iyy /= inertia; o.inertia.izz /= inertia;
                    o.inertia.ixy /= inertia; o.inertia.ixz /= inertia; o.inertia.iyz /= inertia;
                }
                convertMaterial(o.material); convertMaterial(o.documentMaterial);
                if (o.bodyMaterials != null) foreach (var body in o.bodyMaterials) convertMaterial(body.material);
                if (o.referenceGeometry != null)
                {
                    foreach (var axis in o.referenceGeometry.axes) axis.origin = Scale(axis.origin,l);
                    foreach (var plane in o.referenceGeometry.planes) plane.origin = Scale(plane.origin,l);
                    foreach (var point in o.referenceGeometry.points) point.position = Scale(point.position,l);
                }
                if (o.dimensions != null) foreach (var d in o.dimensions)
                {
                    if (d.unit == "m") { d.value /= l; d.unit = u.length; }
                    else if (d.unit == "rad") { d.value /= a; d.unit = u.angle; }
                    var tolerance = d.tolerance;
                    if (tolerance != null && tolerance.unit == "m")
                    {
                        tolerance.lowerDeviation /= l; tolerance.upperDeviation /= l; tolerance.unit = u.length;
                    }
                    else if (tolerance != null && tolerance.unit == "rad")
                    {
                        tolerance.lowerDeviation /= a; tolerance.upperDeviation /= a; tolerance.unit = u.angle;
                    }
                }
            }
            foreach (var mate in data.mates)
            {
                var limits = mate.limits;
                if (limits == null) continue;
                if (limits.unit == "m") { limits.minimum /= l; limits.maximum /= l; limits.unit = u.length; }
                else if (limits.unit == "rad") { limits.minimum /= a; limits.maximum /= a; limits.unit = u.angle; }
            }
            if (data.interferences != null) foreach (var hit in data.interferences) hit.volume /= v;
            data.coordinateSystem = "SolidWorks root document axes; positions/transform translations in project.units.length; inertia about center of mass; directions and rotations dimensionless";
            data.extractionStatus["units"] = "converted_to_active_root_document_units";
            data.schemaVersion = "2.1";
        }
        private static double[] Scale(double[] values, double divisor) { return values == null ? null : values.Select(x => x/divisor).ToArray(); }
    }
    public sealed class AxisRecord { public string id, name; public double[] origin, direction; }
    public sealed class PlaneRecord { public string id, name; public double[] origin, normal; }
    public sealed class PointRecord { public string id, name; public double[] position; }
    public sealed class DimensionRecord
    {
        public string id, name, type, unit;
        public int? nativeDisplayType, nativeParameterType;
        public double? value;
        // Schema support only: not_read must never be interpreted as no tolerance.
        public DimensionTolerance tolerance = new DimensionTolerance();
        public List<MateReference> references = new List<MateReference>();
    }
    public sealed class DimensionTolerance
    {
        // extractionStatus: not_read, complete, partial, unavailable, not_applicable.
        // type: none, bilateral, symmetric, limit, fit, basic, other, unknown.
        public string extractionStatus = "not_read", type = "unknown";
        public int? nativeTypeCode;
        public string nativeTypeName, source, unit;
        // Signed offsets from nominal, never absolute minimum/maximum sizes.
        // Null means unknown; zero is a known zero deviation.
        public double? lowerDeviation, upperDeviation;
        // Independent field states: not_read, read, unavailable, not_applicable, invalid.
        // Only read carries a finite numeric value; all other states carry null.
        // A failed bound must not erase a successfully read bound or tolerance type.
        public string lowerDeviationStatus = "not_read", upperDeviationStatus = "not_read";
        public int? lowerNativeStatusCode, upperNativeStatusCode;
        // Preserve a native fit designation such as H7 when numeric bounds are absent.
        public string fitDesignation;
    }
    public sealed class MateLimits { public bool? enabled; public double? minimum, maximum; public string unit; }
    public sealed class InterferenceRecord
    {
        public string id;
        public List<string> componentIds = new List<string>();
        public double volume;
        public bool possible;
    }
    public sealed class Inertia { public double ixx, iyy, izz, ixy, ixz, iyz; }
    // A mate relates CAD objects. References identify the participating geometry.
    // Unknown solver status remains unknown; no feature error does not prove a solved mate.
    public sealed class Mate
    {
        public string id, name, type, ownerId, status = "unknown", alignment;
        public int? nativeErrorCode;
        public bool? nativeWarning;
        public NativeStatus nativeStatus;
        public string satisfaction = "unknown";
        public bool? lockRotation;
        public string lockRotationStatus = "not_applicable";
        public List<string> componentIds = new List<string>();
        public List<MateReference> references = new List<MateReference>();
        public double[] axis;
        public MateLimits limits;
    }
    public sealed class MateReference { public string componentId, entityType = "unknown", entityId; }
    public sealed class NativeStatus
    {
        public string source, name;
        public int code;
        public bool? isWarning;
    }
    // PART 2: ID AND VALUE CONVERSION HELPERS
    // These functions can run without SolidWorks.
    public static class Identity
    {
        // Hash identity inputs into a deterministic ID. This is identification, not encryption.
        // Repeat exports retain IDs only while their underlying identity inputs remain stable.
        public static string Make(string prefix, params string[] values)
        {
            // Length prefixes avoid delimiter collisions in user-controlled file/component names.
            var key = String.Concat(values.Select(v => (v ?? "").Length + ":" + (v ?? "")));
            using (var hash = SHA256.Create())
                return prefix + "_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
        }
        // Convert native constraint enum values into the metadata vocabulary.
        // Unrecognized values (including autosolve-off) stay unknown.
        public static string Definition(int state)
        {
            switch (state) { case 2: return "under_defined"; case 3: return "fully_defined"; case 4: return "over_defined"; case 5: return "no_solution"; case 6: return "invalid_solution"; default: return "unknown"; }
        }
        // Native tensor layout: [xx, xy, xz, yx, yy, yz, zx, zy, zz].
        // Keep the native off-diagonal sign convention and reject invalid values.
        public static Inertia Tensor(double[] v)
        {
            if (v == null || v.Length != 9 || v.Any(x => Double.IsNaN(x) || Double.IsInfinity(x))) throw new InvalidDataException("Invalid inertia tensor.");
            return new Inertia { ixx = v[0], ixy = v[1], ixz = v[2], iyy = v[4], iyz = v[5], izz = v[8] };
        }
    }
}


namespace CADVision.SolidWorks
{
    /// <summary>Call on the SolidWorks STA thread. Does not save, resolve, or switch configurations.</summary>
    // PART 3: SOLIDWORKS API EXTRACTION
    // COM is the Windows interface used to communicate with SolidWorks.
    // Store an occurrence lookup for mate resolution and native component handles for traversal.
    public sealed partial class Extractor
    {
        private Metadata result;
        private ModelDoc2 model;
        private string projectId;
        private SldWorks application;
        private ExtractionOptions options;
        private MathUtility nativeMath;
        private readonly Dictionary<string, CadObject> occurrences = new Dictionary<string, CadObject>(StringComparer.Ordinal);
        private readonly List<Tuple<Component2, CadObject>> components = new List<Tuple<Component2, CadObject>>();

        // Entry point for extraction: require an STA thread and a saved active part/assembly,
        // build the root and component tree, then read mates once all object IDs are known.
        // The saved project path participates in identity; moving the assembly changes its ID.
        // This method reads the current state without forcing configuration switches or saves.
        public Metadata Extract(SldWorks app, ExtractionOptions extractionOptions = null)
        {
            application = app;
            options = extractionOptions ?? new ExtractionOptions();
            materialDatabases.Clear(); nativeMath = null;
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                throw new InvalidOperationException("SolidWorks API calls require an STA thread.");
            occurrences.Clear(); components.Clear();
            model = app.ActiveDoc as ModelDoc2;
            if (model == null || (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY && model.GetType() != (int)swDocumentTypes_e.swDocPART))
                throw new InvalidOperationException("Open and activate a SolidWorks part or assembly first. Drawings are not supported.");
            bool isPart = model.GetType() == (int)swDocumentTypes_e.swDocPART;
            // Read document preferences, never application-wide defaults. Fail early
            // when units cannot be established, rather than exporting mislabeled numbers.
            var outputUnits = ReadDocumentUnits(model);
            Report("Output units: " + outputUnits.system + " (" + outputUnits.length + ", " + outputUnits.mass + ", " + outputUnits.angle + ")");
            var path = model.GetPathName();
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Save the part or assembly first to establish its project identity.");
            projectId = Identity.Make("PROJECT", Path.GetFullPath(path).ToUpperInvariant());
            var config = model.ConfigurationManager.ActiveConfiguration;
            var root = new CadObject { id = Identity.Make(isPart ? "PART" : "ASSY", projectId, config.Name), name = model.GetTitle(), type = isPart ? "part" : "assembly", sourceDocument = path,
                configuration = config.Name, suppressed = false, fixedState = "not_applicable_root_document", occurrencePath = "", identityBasis = "project_path_and_configuration" };
            result = new Metadata { project = new Project { id = projectId, name = model.GetTitle(), rootObjectId = root.id } };
            result.objects.Add(root);
            Report("Root properties and features: " + root.name);
            ReadProperties(model, root);
            Try(root.id, "feature metadata", () => ReadFeatures(model, root, null));
            if (isPart)
            {
                ReadStandalonePart(root);
            }
            else
            {
            var rootComponent = config.GetRootComponent3(false);
            if (rootComponent == null) throw new InvalidOperationException("Assembly tree unavailable. Open the assembly in resolved mode.");
            var children = Items(rootComponent.GetChildren()).Cast<Component2>().ToArray();
            foreach (var child in children) Visit(child, root);
            Try(root.id, "mass properties", () => ReadMass(root, children.Where(c => !c.IsSuppressed()).Cast<object>().ToArray()));
            Report("Assembly mates");
            ReadMateFeatures(model.FirstFeature() as Feature, root, null);
            foreach (var pair in components.Where(p => p.Item2.type == "assembly" && p.Item2.suppressed == false))
                Try(pair.Item2.id, "subassembly mates", () => {
                    var doc = pair.Item1.GetModelDoc2() as ModelDoc2;
                    if (doc == null || doc.ConfigurationManager.ActiveConfiguration.Name != pair.Item2.configuration)
                        throw new InvalidDataException("Source configuration is not active; subassembly mate features were not read from a different configuration.");
                    ReadMateFeatures(doc.FirstFeature() as Feature, pair.Item2, pair.Item1);
                });
            }
            result.warnings.Add("GLB mapping is pending: occurrencePath and transform are correlation inputs, not verified GLB node IDs.");
            if (!isPart)
            {
            result.extractionStatus["mates"] = "attempted_requires_live_verification";
            result.extractionStatus["remainingDOF"] = "not_implemented_stretch";
            result.extractionStatus["interferences"] = options.RunInterferenceDetection ? "pending" : "disabled";
            if (options.RunInterferenceDetection) { Report("Native interference detection"); Try(root.id, "interference detection", ReadInterferences); }
            if (result.extractionStatus["interferences"] == "pending") result.extractionStatus["interferences"] = "unavailable";
            result.warnings.Add("Exact remaining DOF vectors are not implemented. Mate solved/conflicting classifications remain unknown unless supported by explicit native evidence; no solver state is guessed.");
            }
            result.warnings.Add("Custom properties use cached values or raw expressions. Rebuild/configuration changes are not forced. See extractionStatus and warnings for incomplete reads.");
            DocumentUnitConversion.Apply(result, outputUnits);
            result.Validate();
            return result;
        }

        private static DocumentUnits ReadDocumentUnits(ModelDoc2 doc)
        {
            int system = doc.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            var length = doc.GetUserUnit((int)swUserUnitsType_e.swLengthUnit) as UserUnit;
            var angle = doc.GetUserUnit((int)swUserUnitsType_e.swAngleUnit) as UserUnit;
            if (length == null || angle == null) throw new InvalidDataException("Native document units unavailable.");
            bool radians = angle.SpecificUnitType == (int)swAngleUnit_e.swRADIANS;
            var units = DocumentUnitConversion.Create(system, radians);
            double nativeLength = length.ConvertDoubleToSystemValue(1.0);
            // Composite angle formatting is represented as decimal degrees in JSON.
            double nativeAngle = angle.ConvertDoubleToSystemValue(1.0);
            if (!Finite(nativeLength) || Math.Abs(nativeLength - units.lengthToMeters) > units.lengthToMeters * 1e-8 ||
                !Finite(nativeAngle) || Math.Abs(nativeAngle - units.angleToRadians) > units.angleToRadians * 1e-8)
                throw new InvalidDataException("Document unit settings do not match the native unit preset.");
            units.lengthToMeters = nativeLength;
            units.angleToRadians = nativeAngle;
            return units;
        }

        // A standalone part is one root object, even when it has many bodies.
        // Reuse property/feature/material readers, but select bodies for mass instead
        // of assembly occurrences. Never cast a part document to AssemblyDoc.
        private void ReadStandalonePart(CadObject root)
        {
            result.coordinateSystem = "SolidWorks root part; SI; inertia about center of mass aligned with part axes";
            root.@fixed = null;
            root.definitionStatus = "unknown";
            root.extractionStatus["fixed"] = "not_applicable_standalone_part";
            root.extractionStatus["definitionStatus"] = "not_applicable_assembly_constraint_state";
            result.extractionStatus["mates"] = "not_applicable_standalone_part";
            result.extractionStatus["remainingDOF"] = "not_applicable_assembly_motion";
            // Multibody overlap is possible, but the current detector requires an
            // assembly. Null/unsupported must not be presented as zero overlaps.
            result.extractionStatus["interferences"] = "not_implemented_standalone_part";
            result.warnings.Add("Standalone part: assembly mates and fixed/constraint/DOF states do not apply. Part body-to-body interference detection is not implemented.");
            Try(root.id, "material", () => ReadMaterials(model, root));
            root.extractionStatus["massProperties"] = "unavailable";
            Try(root.id, "mass properties", () => {
                var bodies = Items(((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, false)).ToArray();
                ReadMass(root, bodies);
            });
        }

        // Recursively record this occurrence, link it to its parent, read available fields,
        // and visit its children. Suppressed components stay represented, but unavailable
        // descendants/properties are not invented or automatically resolved.
        private void Visit(Component2 component, CadObject parent)
        {
            var path = component.Name2;
            Report("Component: " + path); 
            string basis;
            var id = PersistentId(component, "COMP", parent.id, path, out basis);
            var source = component.GetPathName();
            var doc = component.GetModelDoc2() as ModelDoc2;
            var type = doc != null ? (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "part") :
                (String.Equals(Path.GetExtension(source), ".sldasm", StringComparison.OrdinalIgnoreCase) ? "assembly" : "part");
            var o = new CadObject { id = id, parentId = parent.id, name = path.Split('/').Last(), occurrencePath = path, identityBasis = basis,
                type = type, sourceDocument = source, configuration = component.ReferencedConfiguration };
            if (occurrences.ContainsKey(path)) throw new InvalidDataException("Duplicate occurrence path: " + path);
            occurrences.Add(path, o); components.Add(Tuple.Create(component, o)); result.objects.Add(o); parent.childIds.Add(id);
            Try(id, "suppression", () => o.suppressed = component.IsSuppressed());
            Try(id, "fixed state", () => { o.@fixed = component.IsFixed(); o.fixedState = o.@fixed.Value ? "fixed" : "floating"; });
            Try(id, "transform", () => o.transform = component.Transform2 == null ? null : component.Transform2.ArrayData as double[]);
            if (o.suppressed != false) { result.warnings.Add(id + ": suppressed/unavailable occurrence; descendants and engineering properties not assessed."); return; }
            Try(id, "definition status", () => {
                int native = component.GetConstrainedStatus();
                o.nativeConstrainedStatus = MetadataMath.ComponentStatus(native);
                o.definitionStatus = Identity.Definition(native);
            });
            if (doc != null)
            {
                ReadProperties(doc, o);
                if (type == "part") Try(id, "material", () => ReadMaterials(doc, o));
                Try(id, "feature metadata", () => ReadFeatures(doc, o, component));
            }
            else result.warnings.Add(id + ": source document is unloaded/lightweight; custom properties and material unavailable.");
            // Explicit occurrence selection uses the referenced configuration in assembly context.
            Try(id, "mass properties", () => ReadMass(o, new object[] { component }));
            if (type == "assembly") foreach (var child in Items(component.GetChildren()).Cast<Component2>()) Visit(child, o);
        }

        private void Report(string message) { if (options.Progress != null) options.Progress(message); }

        // Calculate through the active root document. Select occurrences for assemblies
        // or solid bodies for standalone parts, including hidden bodies.
        // Suspend the user selection, preselect native objects, and restore in finally.
        // Request SI units, include hidden geometry, and validate before assigning results.
        private void ReadMass(CadObject o, object[] selected)
        {
            o.extractionStatus["massProperties"] = "unavailable";
            if (selected.Length == 0) { result.warnings.Add(o.id + ": no solid bodies/resolved components for mass calculation; physical properties remain null."); return; }
            int where = (int)swMassPropertyMoment_e.swMassPropertyMomentAboutCenterOfMass;
            Report("Native mass properties: " + o.name);
            var selection = model.SelectionManager as SelectionMgr;
            if (selection == null) throw new InvalidOperationException("Selection manager unavailable.");
            selection.SuspendSelectionList();
            try
            {
            // Use each object's typed selection API. AddSelectionListObject rejected
            // component dispatch objects in the user's live assembly.
            var selectData = selection.CreateSelectData();
            selectData.Mark = 0;
            foreach (var item in selected)
            {
                var component = item as Component2;
                var body = item as Body2;
                bool accepted = component != null ? component.Select4(true, selectData, false) :
                    body != null && body.Select2(true, selectData);
                if (!accepted)
                    throw new InvalidOperationException("Typed mass selection rejected (" +
                        (component != null ? component.Name2 : "solid body") + "); refusing a whole-document calculation.");
            }
            if (selection.GetSelectedObjectCount2(-1) != selected.Length)
                throw new InvalidOperationException("Mass selection count mismatch; calculation skipped.");
            string stage = "CreateMassProperty2";
            Try(o.id, "IMassProperty2", () => {
                try
                {
                    var mass = model.Extension.CreateMassProperty2() as MassProperty2;
                    if (mass == null) throw new InvalidOperationException("No mass-property object returned.");
                    stage = "UseSystemUnits"; mass.UseSystemUnits = true;
                    stage = "IncludeHiddenBodiesOrComponents"; mass.IncludeHiddenBodiesOrComponents = true;
                    // CreateMassProperty2 consumes the preselected bodies/components.
                    stage = "Recalculate";
                    if (!mass.Recalculate()) throw new InvalidOperationException("Recalculate returned false.");
                    MetadataMath.ReadPhysicalFields(o, () => mass.Mass, () => mass.Volume,
                        () => mass.CenterOfMass as double[], () => mass.GetMomentOfInertia(where) as double[],
                        (field, read) => Try(o.id, "IMassProperty2." + field, read));
                    Try(o.id, "IMassProperty2.Density", () => SetNativeDensity(o, mass.Density, "IMassProperty2.Density"));
                }
                catch (COMException e) { throw new InvalidOperationException(stage + " failed (0x" + e.ErrorCode.ToString("X8") + "): " + e.Message, e); }
            });
            o.extractionStatus["massPropertiesMethod"] = "IMassProperty2";
            // Legacy native mass properties also consume selected assembly components.
            // AddBodies is used only for standalone parts, never assembly occurrences.
            if (MetadataMath.PhysicalStatus(o) != "complete" || o.effectiveDensity == null)
            {
                o.extractionStatus["massPropertiesMethod"] = "IMassProperty2_then_IMassProperty_fallback";
                stage = "CreateMassProperty";
                Try(o.id, "IMassProperty fallback", () => {
                    try
                    {
                        var legacy = model.Extension.CreateMassProperty() as MassProperty;
                        if (legacy == null) throw new InvalidOperationException("No legacy mass-property object returned.");
                        stage = "UseSystemUnits"; legacy.UseSystemUnits = true;
                        if (model.GetType() == (int)swDocumentTypes_e.swDocPART)
                        {
                            stage = "AddBodies";
                            var dispatchBodies = selected.Select(item => new DispatchWrapper(item)).ToArray();
                            if (!legacy.AddBodies(dispatchBodies)) throw new InvalidOperationException("AddBodies rejected the selected solid bodies.");
                        }
                        MetadataMath.ReadPhysicalFields(o, () => legacy.Mass, () => legacy.Volume,
                            () => legacy.CenterOfMass as double[], () => legacy.GetMomentOfInertia(where) as double[],
                            (field, read) => Try(o.id, "IMassProperty." + field, read));
                        if (o.effectiveDensity == null) Try(o.id, "IMassProperty.Density", () => SetNativeDensity(o, legacy.Density, "IMassProperty.Density"));
                    }
                    catch (COMException e) { throw new InvalidOperationException(stage + " failed (0x" + e.ErrorCode.ToString("X8") + "): " + e.Message, e); }
                });
            }
            }
            finally { selection.ResumeSelectionList(); }
            o.extractionStatus["massProperties"] = MetadataMath.PhysicalStatus(o);
            // Material density is read independently from the assigned material database.
            // Do not substitute mass/volume: native mass overrides could make it misleading.
        }

        // Preserve both document-wide and configuration-specific custom properties.
        // Merge with configuration values taking precedence; common labels also populate
        // convenience fields, while every discovered property remains in the dictionaries.
        private void ReadProperties(ModelDoc2 doc, CadObject o)
        {
            Try(o.id, "document properties", () => ReadPropertyManager(doc.Extension.CustomPropertyManager[""], o.documentProperties));
            Try(o.id, "configuration properties", () => ReadPropertyManager(doc.Extension.CustomPropertyManager[o.configuration], o.configurationProperties));
            foreach (var p in o.documentProperties) o.customProperties[p.Key] = p.Value;
            foreach (var p in o.configurationProperties) o.customProperties[p.Key] = p.Value;
            string value;
            if (o.customProperties.TryGetValue("PartNumber", out value) || o.customProperties.TryGetValue("Part Number", out value)) { o.partNumber = value; o.partNumberSource = "custom_property_fallback"; }
            if (o.customProperties.TryGetValue("Description", out value)) o.description = value;
            Try(o.id, "BOM source", () => ReadBomSource(doc, o));
        }
        // Enumerate all names and read cached values without activating configurations.
        // Use resolved text when evaluated; otherwise retain the raw expression.
        // Cached values may be stale, so this is not a forced property refresh.
        private static void ReadPropertyManager(CustomPropertyManager manager, Dictionary<string, string> target)
        {
            foreach (var entry in Items(manager.GetNames()))
            {
                string raw, resolved; bool evaluated, linked;
                manager.Get6((string)entry, true, out raw, out resolved, out evaluated, out linked);
                // Use cached values to avoid activating configurations; unevaluated expressions stay raw.
                target[(string)entry] = evaluated ? resolved : raw;
            }
        }

        // Walk MateGroup features and their mate subfeatures. Extract type/alignment,
        // available feature errors, and geometry references linked to occurrence IDs.
        // Limits and supported reference directions are read separately. Solved/conflicting
        // solver states and nested/flexible assembly behavior still need live verification.
        private void ReadMateFeatures(Feature first, CadObject owner, Component2 ownerComponent)
        {
            for (var f = first; f != null; f = f.GetNextFeature() as Feature)
            {
                if (f.GetTypeName2() != "MateGroup") continue;
                foreach (var sub in MateSubfeatures(f, new HashSet<string>(StringComparer.Ordinal)))
                {
                    var feature = sub;
                    Try(owner.id, "mate " + feature.Name, () => {
                        var mate = feature.GetSpecificFeature2() as Mate2;
                        if (mate == null) return;
                        string basis;
                        var record = new Mate { id = PersistentId(feature, "MATE", owner.id, feature.Name, out basis), name = feature.Name, ownerId = owner.id,
                            type = EnumName(typeof(swMateType_e), mate.Type, "swMate"), alignment = MetadataMath.Alignment(mate.Alignment) };
                        // Add before optional reads so one broken reference cannot erase
                        // the entire mate from the export.
                        if (result.mates.Any(m => m.id == record.id)) return;
                        result.mates.Add(record);
                        bool warning;
                        record.nativeErrorCode = feature.GetErrorCode2(out warning);
                        record.nativeWarning = record.nativeErrorCode == 0 ? (bool?)null : warning;
                        record.nativeStatus = new NativeStatus { source = "IFeature.GetErrorCode2", code = record.nativeErrorCode.Value,
                            name = Enum.GetName(typeof(swFeatureError_e), record.nativeErrorCode.Value), isWarning = record.nativeWarning };
                        record.status = MetadataMath.MateStatus(feature.IsSuppressed(), record.nativeErrorCode.Value, warning);
                        record.satisfaction = MetadataMath.MateSatisfaction(record.status == "suppressed", record.nativeErrorCode.Value, warning);
                        Try(record.id, "mate limits", () => record.limits = ReadLimits(feature, mate.Type));
                        if (mate.Type == (int)swMateType_e.swMateCONCENTRIC)
                        {
                            record.lockRotationStatus = "unavailable";
                            Try(record.id, "lock rotation", () => {
                                var definition = feature.GetDefinition() as ConcentricMateFeatureData;
                                if (definition == null) throw new InvalidDataException("Concentric mate definition unavailable.");
                                record.lockRotation = definition.LockRotation;
                                record.lockRotationStatus = "read_from_IConcentricMateFeatureData.LockRotation";
                            });
                        }
                        // Zero feature error is not evidence that the mate solver reports 'solved'.
                        for (int i = 0; i < mate.GetMateEntityCount(); i++)
                        {
                            var entity = mate.MateEntity(i);
                            var reference = new MateReference();
                            var native = entity.Reference;
                            var component = entity.ReferenceComponent;
                            reference.componentId = native == null && component == null ? null : ResolveMateComponent(component, owner, ownerComponent);
                            if (reference.componentId == null) result.warnings.Add(record.id + ": mate reference could not be mapped; it is not assumed to refer to the root.");
                            if (native is Face2) { var surface = ((Face2)native).GetSurface() as Surface; reference.entityType = surface != null && surface.IsCylinder() ? "cylindrical_face" : surface != null && surface.IsPlane() ? "planar_face" : "face"; }
                            else if (native is Edge) reference.entityType = "edge";
                            else if (native is Vertex) reference.entityType = "vertex";
                            else reference.entityType = EntityKind(native);
                            // EntityParams vectors are in the owning assembly frame. Convert
                            // once to root coordinates for nested subassembly mates.
                            if (record.axis == null &&
                                ((mate.Type == (int)swMateType_e.swMateCONCENTRIC && (reference.entityType == "cylindrical_face" || reference.entityType == "axis")) ||
                                 ((mate.Type == (int)swMateType_e.swMateDISTANCE || mate.Type == (int)swMateType_e.swMateCOINCIDENT || mate.Type == (int)swMateType_e.swMatePARALLEL) &&
                                  (reference.entityType == "planar_face" || reference.entityType == "plane"))))
                                Try(record.id, "mate axis", () => record.axis = ReadMateAxis(entity, ownerComponent));
                            if (native != null) reference.entityId = PersistentOnly(native, "ENTITY", reference.componentId ?? owner.id);
                            record.references.Add(reference);
                            if (reference.componentId != null && !record.componentIds.Contains(reference.componentId)) record.componentIds.Add(reference.componentId);
                        }
                    });
                }
            }
        }
        // Prefer a match inside the owning subassembly occurrence so repeated assemblies
        // cannot accidentally link to each other's parts. Unresolved matches produce null
        // and a warning rather than a guessed ID.
        private string ResolveMateComponent(Component2 c, CadObject owner, Component2 ownerComponent)
        {
            if (c == null) return owner.id; // Mate reference to the owning assembly itself.
            var name = c.Name2;
            if (ownerComponent != null && name == owner.occurrencePath) return owner.id;
            CadObject target;
            if (ownerComponent != null)
            {
                // Prefer the owning occurrence, because a source subassembly may have many instances.
                var prefixed = owner.occurrencePath + "/" + name;
                if (occurrences.TryGetValue(prefixed, out target)) return target.id;
                if (name.StartsWith(owner.occurrencePath + "/", StringComparison.Ordinal) && occurrences.TryGetValue(name, out target)) return target.id;
            }
            else if (occurrences.TryGetValue(name, out target)) return target.id;
            result.warnings.Add(owner.id + ": unresolved mate component " + name);
            return null;
        }
        // Request native persistent-reference bytes and hash them with project/owner scope.
        // Return null if SolidWorks cannot supply the reference; topology changes can still
        // invalidate native persistent identity.
        private string PersistentOnly(object value, string prefix, string scope)
        {
            try { var bytes = model.Extension.GetPersistReference3(value) as byte[]; return bytes == null || bytes.Length == 0 ? null : Identity.Make(prefix, projectId, scope, Convert.ToBase64String(bytes)); }
            catch (COMException) { return null; }
        }
        // Prefer persistent identity; otherwise use a scoped name/path and warn that
        // rename or reparent operations can change the fallback ID.
        private string PersistentId(object value, string prefix, string scope, string fallback, out string basis)
        {
            var id = PersistentOnly(value, prefix, scope);
            basis = id == null ? "occurrence_path_fallback" : "solidworks_persistent_reference";
            if (id == null) result.warnings.Add(scope + ": persistent reference unavailable; rename/reparent can change ID for " + fallback);
            return id ?? Identity.Make(prefix, projectId, scope, fallback);
        }
        private static IEnumerable<object> Items(object values) { return values is Array ? ((Array)values).Cast<object>() : Enumerable.Empty<object>(); }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static string EnumName(Type type, int value, string prefix) { var name = Enum.GetName(type, value); return name == null ? "unknown" : (name.StartsWith(prefix) ? name.Substring(prefix.Length) : name).ToLowerInvariant(); }
        // Isolate expected failures while reading individual fields and record warnings.
        // Unhandled programming errors still propagate instead of silently hiding them.
        private void Try(string id, string field, Action read)
        {
            try { read(); }
            catch (Exception e) { if (!(e is COMException || e is InvalidOperationException || e is IOException || e is System.Xml.XmlException || e is UnauthorizedAccessException)) throw; result.warnings.Add(id + ": " + field + " unavailable: " + e.Message); }
        }
    }
}





namespace CADVision.SolidWorks
{
    public sealed class ExtractionOptions
    {
        // Native interference calculation can be expensive for large assemblies.
        public bool RunInterferenceDetection = true;
        public Action<string> Progress;
    }

    // Pure conversions are kept separate so unit/coordinate mistakes can be tested
    // without a running SolidWorks instance. Internal reads use SI; the final export
    // converts all dimensional values to the declared root document units.
    public static class MetadataMath
    {
        public static string ToleranceType(int code)
        {
            switch (code)
            {
                case 0: return "none";
                case 1: return "basic";
                case 2: return "bilateral";
                case 3: return "limit";
                case 4: return "symmetric";
                case 7: case 8: case 9: return "fit";
                case 5: case 6: case 10: case 11: return "other";
                default: return "unknown";
            }
        }
        // Native warning 1 means this bound is not meaningful for the tolerance type.
        // Never serialize the out-value returned alongside a non-success warning.
        public static void SetToleranceBound(DimensionTolerance tolerance, bool lower, int status, double value)
        {
            string state = status == 1 ? "not_applicable" : status != 0 ? "unavailable" : !Finite(value) ? "invalid" : "read";
            double? number = state == "read" ? (double?)value : null;
            if (lower) { tolerance.lowerNativeStatusCode = status; tolerance.lowerDeviationStatus = state; tolerance.lowerDeviation = number; }
            else { tolerance.upperNativeStatusCode = status; tolerance.upperDeviationStatus = state; tolerance.upperDeviation = number; }
        }
        public static NativeStatus ComponentStatus(int code)
        {
            return new NativeStatus { source = "IComponent2.GetConstrainedStatus", code = code,
                name = Enum.GetName(typeof(swConstrainedStatus_e), code) };
        }
        public static string MateSatisfaction(bool suppressed, int error, bool warning)
        {
            if (suppressed) return "not_applicable";
            if (warning && error != 0) return "unknown";
            // Explicit native errors mean tangent not satisfied / mate cannot be solved.
            // Zero error, redundancy, missing data, or fixed components do not prove satisfied.
            return error == (int)swFeatureError_e.swFeatureErrorMateUnknownTangent || error == (int)swFeatureError_e.swFeatureErrorMateIlldefined
                ? "not_satisfied" : "unknown";
        }
        // Read independently: a failure fetching inertia must not discard a valid volume.
        // A second native API may fill missing fields without overwriting successful reads.
        public static void ReadPhysicalFields(CadObject o, Func<double> mass, Func<double> volume,
            Func<double[]> center, Func<double[]> inertia, Action<string, Action> attempt)
        {
            if (o.mass == null) attempt("Mass", () => { var n = mass(); if (!Finite(n) || n < 0) throw new InvalidDataException("Invalid mass."); o.mass = n; });
            if (o.volume == null) attempt("Volume", () => { var n = volume(); if (!Finite(n) || n <= 0) throw new InvalidDataException("Invalid solid volume."); o.volume = n; });
            if (o.centerOfMass == null) attempt("CenterOfMass", () => { var p = center(); if (p == null || p.Length != 3 || p.Any(n => !Finite(n))) throw new InvalidDataException("Invalid center of mass."); o.centerOfMass = p; });
            if (o.inertia == null) attempt("GetMomentOfInertia", () => o.inertia = Identity.Tensor(inertia()));
        }
        public static string PhysicalStatus(CadObject o)
        {
            int available = (o.mass.HasValue ? 1 : 0) + (o.volume.HasValue ? 1 : 0) + (o.centerOfMass != null ? 1 : 0) + (o.inertia != null ? 1 : 0);
            return available == 4 ? "complete" : available == 0 ? "unavailable" : "partial";
        }
        public static bool Finite(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n); }
        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length != 3 || v.Any(n => !Finite(n))) throw new InvalidDataException("Invalid direction.");
            double length = Math.Sqrt(v.Sum(n => n * n));
            if (!Finite(length) || length < 1e-12) throw new InvalidDataException("Degenerate direction.");
            return v.Select(n => n / length).ToArray();
        }
        public static double[] Transform(double[] v, double[] t, bool direction)
        {
            if (v == null || v.Length != 3 || v.Any(n => !Finite(n))) throw new InvalidDataException("Invalid coordinate.");
            if (t == null) return direction ? Unit(v) : (double[])v.Clone();
            if (t.Length < 13 || t.Take(13).Any(n => !Finite(n)) || Math.Abs(t[12]) < 1e-12) throw new InvalidDataException("Invalid component transform.");
            // SolidWorks layout: rotation [0..8], translation [9..11], scale [12].
            var transformed = new[] {
                (v[0]*t[0] + v[1]*t[3] + v[2]*t[6])*t[12] + (direction ? 0 : t[9]),
                (v[0]*t[1] + v[1]*t[4] + v[2]*t[7])*t[12] + (direction ? 0 : t[10]),
                (v[0]*t[2] + v[1]*t[5] + v[2]*t[8])*t[12] + (direction ? 0 : t[11]) };
            if (transformed.Any(n => !Finite(n))) throw new InvalidDataException("Invalid transformed coordinate.");
            return direction ? Unit(transformed) : transformed;
        }
        public static string Alignment(int value)
        {
            // Enum aliases share numeric values; Enum.GetName can pick the wrong alias.
            switch (value) { case 0: return "aligned"; case 1: return "anti_aligned"; case 2: return "closest"; default: return "unknown"; }
        }
        public static string MateStatus(bool suppressed, int error, bool warning)
        {
            if (suppressed) return "suppressed";
            // A warning code does not prove the mate solver failed.
            if (warning && error != 0) return "unknown";
            if (error == (int)swFeatureError_e.swFeatureErrorMateDanglingGeometry) return "dangling";
            if (error == (int)swFeatureError_e.swFeatureErrorMateOverdefined) return "over_defined";
            return error == 0 ? "unknown" : "error";
        }
        public static string DimensionType(int type)
        {
            switch ((swDimensionType_e)type)
            {
                case swDimensionType_e.swScalarDimension: return "scalar";
                case swDimensionType_e.swAngularDimension:
                case swDimensionType_e.swAngularOrdinateDimension: return "angle";
                case swDimensionType_e.swDiameterDimension:
                case swDimensionType_e.swDiametricLinearDimension: return "diameter";
                case swDimensionType_e.swRadialDimension:
                case swDimensionType_e.swRadialLinearDimension: return "radius";
                case swDimensionType_e.swLinearDimension:
                case swDimensionType_e.swHorLinearDimension:
                case swDimensionType_e.swVertLinearDimension:
                case swDimensionType_e.swOrdinateDimension:
                case swDimensionType_e.swHorOrdinateDimension:
                case swDimensionType_e.swVertOrdinateDimension:
                case swDimensionType_e.swZAxisDimension:
                case swDimensionType_e.swArcLengthDimension: return "linear";
                default: return "unknown";
            }
        }
        public static string DimensionUnit(string type) { return type == "scalar" ? "1" : type == "angle" ? "rad" : type == "unknown" ? null : "m"; }
        public static MateLimits Limits(bool enabled, double minimum, double maximum, string unit)
        {
            if (enabled && (!Finite(minimum) || !Finite(maximum) || minimum > maximum)) throw new InvalidDataException("Invalid mate limits.");
            return new MateLimits { enabled = enabled, minimum = enabled ? (double?)minimum : null, maximum = enabled ? (double?)maximum : null, unit = unit };
        }
        public static Material SummarizeMaterials(Material document, IEnumerable<BodyMaterial> bodies, bool complete)
        {
            var materials = bodies == null ? new Material[0] : bodies.Select(b => b.material).ToArray();
            if (!complete) return new Material { assigned = document.assigned == true || materials.Any(m => m.assigned == true) ? (bool?)true : null };
            if (materials.Length == 0) return document;
            if (materials.All(m => m.assigned == false)) return new Material { assigned = false };
            if (materials.All(m => m.assigned == true) && materials.All(m => m.name == materials[0].name && m.database == materials[0].database)) return materials[0];
            // Mixed materials/partial assignment cannot be represented by one name/density.
            return new Material { assigned = materials.Any(m => m.assigned == true) ? (bool?)true : null };
        }
    }

    public sealed partial class Extractor
    {
        public static void SetBomValue(CadObject o, string value, string source)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            o.partNumber = value; o.partNumberSource = source;
        }
        public static void SetNativeDensity(CadObject o, double value, string source)
        {
            if (!MetadataMath.Finite(value) || value <= 0) throw new InvalidDataException("Native density is invalid.");
            o.effectiveDensity = value; o.effectiveDensitySource = source;
        }
        // Let SolidWorks apply its own transform convention and vector normalization.
        // We still derive an axis direction from its two native endpoints because that
        // API returns endpoints, not a ready-made direction vector.
        private double[] NativeTransform(double[] values, double[] transform, bool direction)
        {
            if (values == null || values.Length != 3 || values.Any(v => !Finite(v))) throw new InvalidDataException("Invalid native coordinate input.");
            var math = nativeMath ?? (nativeMath = application.GetMathUtility() as MathUtility);
            if (math == null) throw new InvalidOperationException("SolidWorks MathUtility unavailable.");
            MathTransform nativeTransform = null;
            if (transform != null)
            {
                nativeTransform = math.CreateTransform(transform) as MathTransform;
                if (nativeTransform == null) throw new InvalidDataException("SolidWorks rejected the transform.");
            }
            double[] output;
            if (direction)
            {
                var vector = math.CreateVector(values) as MathVector;
                if (vector == null) throw new InvalidDataException("SolidWorks rejected the vector.");
                if (nativeTransform != null) vector = vector.MultiplyTransform(nativeTransform) as MathVector;
                if (vector == null) throw new InvalidDataException("Native vector transform failed.");
                vector = vector.Normalise();
                output = vector == null ? null : vector.ArrayData as double[];
            }
            else
            {
                var point = math.CreatePoint(values) as MathPoint;
                if (point == null) throw new InvalidDataException("SolidWorks rejected the point.");
                if (nativeTransform != null) point = point.MultiplyTransform(nativeTransform) as MathPoint;
                output = point == null ? null : point.ArrayData as double[];
            }
            if (output == null || output.Length != 3 || output.Any(v => !Finite(v)) || (direction && output.All(v => v == 0)))
                throw new InvalidDataException("Native coordinate conversion returned invalid data.");
            return output;
        }
        private readonly Dictionary<string, XDocument> materialDatabases = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);

        // Reference features are read from their source document, then transformed
        // exactly once into root assembly coordinates. Never read another configuration's
        // geometry as though it belonged to this occurrence.
        private void ReadFeatures(ModelDoc2 doc, CadObject owner, Component2 component)
        {
            bool sameConfig = String.Equals(doc.ConfigurationManager.ActiveConfiguration.Name, owner.configuration, StringComparison.Ordinal);
            owner.referenceGeometry = sameConfig ? new ReferenceGeometry() : null;
            owner.dimensions = new List<DimensionRecord>();
            owner.extractionStatus["referenceGeometry"] = sameConfig ? "partial" : "unavailable_configuration_not_active";
            owner.extractionStatus["dimensions"] = sameConfig ? "partial" : "partial_configuration_not_active";
            owner.extractionStatus["featureProperties"] = sameConfig ? "partial" : "unavailable_configuration_not_active";
            if (!sameConfig) result.warnings.Add(owner.id + ": geometry/feature properties skipped for inactive source configuration; dimension enumeration may be incomplete, but values request the referenced configuration explicitly.");
            var dimensions = new HashSet<string>(StringComparer.Ordinal);
            var features = new HashSet<string>(StringComparer.Ordinal);
            // The API requires display dimensions enabled for feature enumeration.
            // Restore the previous display preference even if a COM call fails.
            int setting = (int)swUserPreferenceToggle_e.swDisplayFeatureDimensions;
            bool previous = doc.GetUserPreferenceToggle(setting);
            try
            {
                if (!previous && !doc.SetUserPreferenceToggle(setting, true)) throw new InvalidOperationException("Cannot enable dimension enumeration.");
                if (sameConfig)
                    foreach (var category in new[] { "referenceGeometry", "dimensions", "featureProperties" }) owner.extractionStatus[category] = "complete";
                for (var feature = doc.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                    ReadFeatureTree(feature, doc, owner, component, sameConfig, features, dimensions);
            }
            catch
            {
                foreach (var category in new[] { "referenceGeometry", "dimensions", "featureProperties" })
                    if (owner.extractionStatus[category] == "complete") owner.extractionStatus[category] = "partial";
                throw;
            }
            finally
            {
                if (!previous && !doc.SetUserPreferenceToggle(setting, previous))
                    result.warnings.Add(owner.id + ": could not restore feature-dimension display preference.");
            }
        }

        private void ReadFeatureTree(Feature feature, ModelDoc2 doc, CadObject owner, Component2 component, bool sameConfig, HashSet<string> visited, HashSet<string> dimensions, bool readDimensions = true)
        {
            string key = SourceId(doc, feature, "FEATURE", owner.id, feature.Name);
            if (!visited.Add(key)) return;
            // Do not include dimensions belonging to suppressed features in this configuration.
            bool? suppressed = null;
            Try(owner.id, "feature suppression", () => {
                var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { owner.configuration }) as Array;
                if (states == null || states.Length != 1) throw new InvalidDataException("Feature suppression unavailable.");
                suppressed = Convert.ToBoolean(states.GetValue(0));
            });
            if (suppressed == null)
                foreach (var category in new[] { "referenceGeometry", "dimensions", "featureProperties" })
                    if (owner.extractionStatus[category] == "complete") owner.extractionStatus[category] = "partial";
            bool dimensionsRead = !readDimensions;
            if (suppressed == false)
            {
                if (sameConfig)
                {
                    Field(owner, "referenceGeometry", () => ReadReference(feature, doc, owner, component));
                    Field(owner, "featureProperties", () => {
                        var manager = feature.CustomPropertyManager;
                        if (manager == null) return;
                        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ReadPropertyManager(manager, properties);
                        if (properties.Count == 0) return;
                        owner.featureProperties[key] = properties;
                        // Scoped keys keep identical cut-list property names from overwriting
                        // each other while fulfilling the flat customProperties contract.
                        foreach (var p in properties) owner.customProperties["feature/" + key + "/" + p.Key] = p.Value;
                    });
                }
                // Parent enumeration already includes its subfeature dimensions.
                if (readDimensions) Field(owner, "dimensions", () => {
                    for (var display = feature.GetFirstDisplayDimension() as DisplayDimension; display != null; display = feature.GetNextDisplayDimension(display) as DisplayDimension)
                    {
                        var dim = display.GetDimension2(0);
                        if (dim == null) continue;
                        var name = dim.FullName;
                        if (!dimensions.Add(name)) continue;
                        var record = new DimensionRecord { id = SourceId(doc, dim, "DIM", owner.id, name), name = name, nativeDisplayType = display.Type2, nativeParameterType = dim.GetType(), type = MetadataMath.DimensionType(display.Type2) };
                        owner.dimensions.Add(record);
                        // Tolerance methods read the source document's active configuration.
                        // Do not infer them from a different configuration or annotation text.
                        ReadTolerance(dim, record, sameConfig);
                        // Angular radians and linear meters match project.units.
                        if (record.type == "unknown") { owner.extractionStatus["dimensions"] = "partial"; result.warnings.Add(record.id + ": unsupported dimension " + name + " (display type " + record.nativeDisplayType + ", parameter type " + record.nativeParameterType + "); numeric value left null."); }
                        else
                        {
                            record.unit = MetadataMath.DimensionUnit(record.type);
                            var values = dim.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { owner.configuration }) as double[];
                            if (values == null || values.Length != 1 || !Finite(values[0])) throw new InvalidDataException("Dimension value unavailable.");
                            record.value = values[0];
                        }
                        // Dimension values can explicitly request another configuration;
                        // attachment geometry cannot be assumed to follow that request.
                        if (!sameConfig) { record.references = null; continue; }
                        var annotation = display.GetAnnotation() as Annotation;
                        if (annotation != null) foreach (var entity in Items(annotation.GetAttachedEntities3()))
                        {
                            if (entity == null) continue;
                            // These references belong to source features. Assembly annotation
                            // targets require occurrence mapping, so do not assume owner there.
                            string target = owner.type == "part" ? owner.id : null;
                            var nativeEntity = entity as Entity;
                            if (owner.type == "assembly" && nativeEntity != null)
                                target = ResolveMateComponent(nativeEntity.GetComponent() as Component2, owner, component);
                            record.references.Add(new MateReference { componentId = target, entityType = EntityKind(entity), entityId = SourcePersistent(doc, entity, "ENTITY", target ?? owner.id) });
                        }
                    }
                                    dimensionsRead = true;
                });
            }
            for (var child = feature.GetFirstSubFeature() as Feature; child != null; child = child.GetNextSubFeature() as Feature)
                ReadFeatureTree(child, doc, owner, component, sameConfig, visited, dimensions, !dimensionsRead);
        }

        private void ReadTolerance(Dimension dimension, DimensionRecord record, bool sameConfiguration)
        {
            var t = record.tolerance;
            t.source = "IDimension.Tolerance / IDimensionTolerance.Type / GetMinValue2 / GetMaxValue2";
            t.extractionStatus = "unavailable";
            t.lowerDeviationStatus = t.upperDeviationStatus = "unavailable";
            if (!sameConfiguration)
            {
                result.warnings.Add(record.id + ": tolerance unavailable because the referenced configuration is not active.");
                return;
            }
            global::SolidWorks.Interop.sldworks.DimensionTolerance native = null;
            Try(record.id, "tolerance type", () => {
                native = dimension.Tolerance;
                if (native == null) throw new InvalidDataException("Native tolerance object unavailable.");
                int code = native.Type;
                t.nativeTypeCode = code;
                t.nativeTypeName = Enum.GetName(typeof(swTolType_e), code);
                t.type = MetadataMath.ToleranceType(code);
            });
            if (t.nativeTypeCode == null) return;
            // Confirmed absence/basic dimension is not a failed numeric read.
            if (t.type == "none" || t.type == "basic")
            {
                t.lowerDeviationStatus = t.upperDeviationStatus = "not_applicable";
                t.extractionStatus = "complete";
                return;
            }
            t.extractionStatus = "partial";
            t.unit = MetadataMath.DimensionUnit(record.type);
            if (t.type == "unknown" || t.unit == null)
            {
                result.warnings.Add(record.id + ": native tolerance type retained, but numeric tolerance interpretation is unsupported.");
                return;
            }
            Try(record.id, "lower tolerance", () => {
                double value; int status = native.GetMinValue2(out value);
                MetadataMath.SetToleranceBound(t, true, status, value);
            });
            Try(record.id, "upper tolerance", () => {
                double value; int status = native.GetMaxValue2(out value);
                MetadataMath.SetToleranceBound(t, false, status, value);
            });
            bool fitRead = true;
            if (t.type == "fit")
            {
                fitRead = false;
                Try(record.id, "fit designation", () => {
                    string hole = native.GetHoleFitValue(), shaft = native.GetShaftFitValue();
                    t.fitDesignation = String.Join("/", new[] { hole, shaft }.Where(s => !String.IsNullOrWhiteSpace(s)));
                    if (String.IsNullOrEmpty(t.fitDesignation)) t.fitDesignation = null;
                    fitRead = t.fitDesignation != null;
                });
            }
            bool lowerOk = t.lowerDeviationStatus == "read" || t.lowerDeviationStatus == "not_applicable";
            bool upperOk = t.upperDeviationStatus == "read" || t.upperDeviationStatus == "not_applicable";
            if (lowerOk && upperOk && fitRead) t.extractionStatus = "complete";
            else result.warnings.Add(record.id + ": tolerance partially read; lower=" + t.lowerDeviationStatus + ", upper=" + t.upperDeviationStatus + ".");
        }

        private void ReadReference(Feature f, ModelDoc2 doc, CadObject o, Component2 component)
        {
            var type = f.GetTypeName2();
            if (type != "RefAxis" && type != "RefPlane" && type != "RefPoint") return;
            var t = ComponentTransform(component);
            var specific = f.GetSpecificFeature2();
            string id = SourceId(doc, f, type.ToUpperInvariant(), o.id, f.Name);
            if (specific is RefAxis)
            {
                var a = ((RefAxis)specific).GetRefAxisParams() as double[];
                if (a == null || a.Length != 6) throw new InvalidDataException("Invalid reference axis.");
                o.referenceGeometry.axes.Add(new AxisRecord { id = id, name = f.Name,
                    origin = NativeTransform(a.Take(3).ToArray(), t, false),
                    direction = NativeTransform(new[] { a[3]-a[0], a[4]-a[1], a[5]-a[2] }, t, true) });
            }
            else if (specific is RefPlane)
            {
                var planeTransform = ((RefPlane)specific).Transform;
                if (planeTransform == null) throw new InvalidDataException("Reference plane transform unavailable.");
                var p = planeTransform.ArrayData as double[];
                o.referenceGeometry.planes.Add(new PlaneRecord { id = id, name = f.Name,
                    origin = NativeTransform(NativeTransform(new double[] {0,0,0}, p, false), t, false),
                    normal = NativeTransform(NativeTransform(new double[] {0,0,1}, p, true), t, true) });
            }
            else if (specific is RefPoint)
            {
                var point = ((RefPoint)specific).GetRefPoint();
                if (point == null) throw new InvalidDataException("Reference point unavailable.");
                o.referenceGeometry.points.Add(new PointRecord { id = id, name = f.Name, position = NativeTransform(point.ArrayData as double[], t, false) });
            }
            else throw new InvalidDataException("Unsupported reference feature interface.");
        }

        private static double[] ComponentTransform(Component2 component)
        {
            if (component == null) return null; // Root document coordinates already are root coordinates.
            var transform = component.Transform2;
            if (transform == null) throw new InvalidDataException("Component transform unavailable.");
            var values = transform.ArrayData as double[];
            if (values == null) throw new InvalidDataException("Component transform data unavailable.");
            return values;
        }
        // Mate folders can contain nested organizational folders. Traverse those too,
        // de-duplicating features that SolidWorks exposes through more than one route.
        private static IEnumerable<Feature> MateSubfeatures(Feature parent, HashSet<string> seen)
        {
            for (var feature = parent.GetFirstSubFeature() as Feature; feature != null; feature = feature.GetNextSubFeature() as Feature)
            {
                if (!seen.Add(feature.GetTypeName2() + ":" + feature.Name)) continue;
                yield return feature;
                foreach (var nested in MateSubfeatures(feature, seen)) yield return nested;
            }
        }
        private static string EntityKind(object value)
        {
            if (value is Face2)
            {
                var surface = ((Face2)value).GetSurface() as Surface;
                return surface != null && surface.IsCylinder() ? "cylindrical_face" : surface != null && surface.IsPlane() ? "planar_face" : "face";
            }
            if (value is Edge) return "edge";
            if (value is Vertex) return "vertex";
            if (value is RefAxis) return "axis";
            if (value is RefPlane) return "plane";
            if (value is RefPoint || value is SketchPoint) return "point";
            if (value is Feature)
            {
                switch (((Feature)value).GetTypeName2()) { case "RefAxis": return "axis"; case "RefPlane": return "plane"; case "RefPoint": return "point"; }
            }
            return "unknown";
        }
        private double[] ReadMateAxis(MateEntity2 entity, Component2 owner)
        {
            // This helper is called only for supported mate/reference combinations.
            // Avoid treating selection-type enum values as mate-geometry enum values.
            var p = entity.EntityParams as double[];
            if (p == null || p.Length < 6) return null;
            return NativeTransform(p.Skip(3).Take(3).ToArray(), ComponentTransform(owner), true);
        }
        private static MateLimits ReadLimits(Feature f, int type)
        {
            if (type == (int)swMateType_e.swMateDISTANCE)
            {
                var data = f.GetDefinition() as DistanceMateFeatureData;
                if (data == null) throw new InvalidDataException("Distance mate definition unavailable.");
                return data.IsAdvancedMate ? MetadataMath.Limits(true, data.MinimumDistance, data.MaximumDistance, "m") : MetadataMath.Limits(false, 0, 0, "m");
            }
            if (type == (int)swMateType_e.swMateANGLE)
            {
                var data = f.GetDefinition() as AngleMateFeatureData;
                if (data == null) throw new InvalidDataException("Angle mate definition unavailable.");
                return data.IsAdvancedMate ? MetadataMath.Limits(true, data.MinimumAngle, data.MaximumAngle, "rad") : MetadataMath.Limits(false, 0, 0, "rad");
            }
            return null; // No generic range is claimed for other mate types.
        }

        // BOM configuration settings are distinct from an arbitrary custom PartNumber.
        // Honor native BOM settings first. Custom properties are retained as fallback
        // only if the native configuration does not provide a usable part number.
        private static void ReadBomSource(ModelDoc2 doc, CadObject o)
        {
            var config = doc.GetConfigurationByName(o.configuration) as Configuration;
            if (config == null) return;
            if (String.IsNullOrEmpty(o.description)) o.description = String.IsNullOrEmpty(config.Description) ? null : config.Description;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (config != null && seen.Add(config.Name))
            {
                switch ((swBOMPartNumberSource_e)config.BOMPartNoSource)
                {
                    case swBOMPartNumberSource_e.swBOMPartNumber_DocumentName: SetBomValue(o, Path.GetFileNameWithoutExtension(doc.GetPathName()), "IConfiguration.BOMPartNoSource:document_name"); return;
                    case swBOMPartNumberSource_e.swBOMPartNumber_ConfigurationName: SetBomValue(o, config.Name, "IConfiguration.BOMPartNoSource:configuration_name"); return;
                    case swBOMPartNumberSource_e.swBOMPartNumber_UserSpecified: SetBomValue(o, config.AlternateName, "IConfiguration.AlternateName"); return;
                    case swBOMPartNumberSource_e.swBOMPartNumber_ParentName: config = config.GetParent() as Configuration; break;
                    default: return;
                }
            }
        }

        private void ReadMaterials(ModelDoc2 doc, CadObject o)
        {
            string database;
            string name = ((PartDoc)doc).GetMaterialPropertyName2(o.configuration, out database);
            o.documentMaterial = MaterialAssignment(name, database, o.id);
            o.material = MetadataMath.SummarizeMaterials(o.documentMaterial, null, false);
            o.extractionStatus["bodyMaterials"] = "unavailable_configuration_not_active";
            if (doc.ConfigurationManager.ActiveConfiguration.Name != o.configuration) return;
            o.bodyMaterials = new List<BodyMaterial>();
            o.extractionStatus["bodyMaterials"] = "complete";
            foreach (var body in new[] { swBodyType_e.swSolidBody, swBodyType_e.swSheetBody }
                .SelectMany(type => Items(((PartDoc)doc).GetBodies2((int)type, false))).Cast<Body2>())
            {
                Field(o, "bodyMaterials", () => {
                    string bodyDb;
                    string bodyName = body.GetMaterialPropertyName(o.configuration, out bodyDb);
                    var assignment = String.IsNullOrEmpty(bodyName) ? o.documentMaterial : MaterialAssignment(bodyName, bodyDb, o.id);
                    o.bodyMaterials.Add(new BodyMaterial { id = SourceId(doc, body, "BODY", o.id, body.Name), name = body.Name, material = assignment });
                });
            }
            o.material = MetadataMath.SummarizeMaterials(o.documentMaterial, o.bodyMaterials, o.extractionStatus["bodyMaterials"] == "complete");
            if (o.material.assigned == true && o.material.name == null)
                result.warnings.Add(o.id + ": mixed/partially known body materials; consult documentMaterial and bodyMaterials. No single name/density is claimed.");
        }
        private Material MaterialAssignment(string name, string database, string owner)
        {
            var material = new Material { assigned = !String.IsNullOrEmpty(name), name = String.IsNullOrEmpty(name) ? null : name, database = database };
            if (material.assigned == false) return material;
            Try(owner, "material density", () => {
                string path = ResolveMaterialDatabase(database);
                if (path == null) throw new InvalidDataException("Assigned material database cannot be located: " + database);
                XDocument xml;
                if (!materialDatabases.TryGetValue(path, out xml))
                {
                    // Local database XML is data. Disable external entities/network resolution.
                    using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml = XDocument.Load(reader);
                    materialDatabases[path] = xml;
                }
                material.density = ReadDensity(xml, name);
                if (material.density == null) throw new InvalidDataException("No unambiguous finite density in the assigned material entry.");
                material.densitySource = "assigned_material_database_kg_per_m3";
            });
            return material;
        }
        public static double? ReadDensity(XDocument xml, string name)
        {
            var entries = xml.Descendants().Where(e => e.Name.LocalName == "material" && (string)e.Attribute("name") == name).ToArray();
            if (entries.Length != 1) return null;
            var density = entries[0].Elements().Where(e => e.Name.LocalName == "physicalproperties").Elements().FirstOrDefault(e => e.Name.LocalName == "DENS");
            double value;
            return density != null && Double.TryParse((string)density.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Finite(value) && value > 0 ? (double?)value : null;
        }
        private string ResolveMaterialDatabase(string database)
        {
            if (String.IsNullOrEmpty(database)) return null;
            if (Path.IsPathRooted(database) && File.Exists(database)) return database;
            var candidates = Items(application.GetMaterialDatabases()).Cast<string>().Where(p =>
                String.Equals(Path.GetFileName(p), Path.GetFileName(database), StringComparison.OrdinalIgnoreCase) ||
                String.Equals(Path.GetFileNameWithoutExtension(p), Path.GetFileNameWithoutExtension(database), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return candidates.Length == 1 && File.Exists(candidates[0]) ? candidates[0] : null;
        }

        // Native interference belongs in the SolidWorks layer. Store IDs/volumes only;
        // transfer, packaging, and CADEN-side interpretation remain shipper responsibilities.
        private void ReadInterferences()
        {
            var manager = ((AssemblyDoc)model).InterferenceDetectionManager;
            if (manager == null) throw new InvalidOperationException("Interference manager unavailable.");
            bool coincidence = manager.TreatCoincidenceAsInterference, subassemblies = manager.TreatSubAssembliesAsComponents;
            bool hidden = manager.IgnoreHiddenBodies, multibody = manager.IncludeMultibodyPartInterferences;
            bool transparent = manager.MakeInterferingPartsTransparent;
            bool useTransform = manager.UseTransform, showIgnored = manager.ShowIgnoredInterferences;
            try
            {
                manager.TreatCoincidenceAsInterference = false;
                manager.TreatSubAssembliesAsComponents = false;
                manager.IgnoreHiddenBodies = false;
                manager.IncludeMultibodyPartInterferences = true;
                manager.MakeInterferingPartsTransparent = false;
                manager.UseTransform = false;
                manager.ShowIgnoredInterferences = true;
                var records = new List<InterferenceRecord>();
                var groupedCounts = new Dictionary<string, int>();
                var nativeResults = manager.GetInterferences();
                if (nativeResults == null && manager.GetInterferenceCount() != 0) throw new InvalidDataException("Interference results unavailable despite nonzero count.");
                foreach (var native in Items(nativeResults).Cast<Interference>())
                {
                    var record = new InterferenceRecord { volume = native.Volume, possible = native.IsPossibleInterference };
                    if (!Finite(record.volume) || record.volume < 0) throw new InvalidDataException("Invalid interference volume.");
                    foreach (var c in Items(native.Components).Cast<Component2>())
                    {
                        CadObject target;
                        if (!occurrences.TryGetValue(c.Name2, out target)) throw new InvalidDataException("Interference component not found: " + c.Name2);
                        if (!record.componentIds.Contains(target.id)) record.componentIds.Add(target.id);
                    }
                    if (record.componentIds.Count == 0) throw new InvalidDataException("Interference has no mapped components.");
                    record.componentIds.Sort(StringComparer.Ordinal);
                    var key = String.Join("|", record.componentIds);
                    int count; groupedCounts.TryGetValue(key, out count); groupedCounts[key] = count + 1;
                    // Multiple regions between the same occurrences are distinguished by
                    // native result order; stability across geometry changes is not promised.
                    record.id = Identity.Make("INTERFERENCE", projectId, key, count.ToString(CultureInfo.InvariantCulture));
                    records.Add(record);
                }
                result.interferences = records;
                result.extractionStatus["interferences"] = components.Any(p => p.Item2.suppressed == false && p.Item1.GetModelDoc2() == null) ? "partial_unloaded_components" : "complete";
            }
            finally
            {
                try
                {
                    manager.TreatCoincidenceAsInterference = coincidence;
                    manager.TreatSubAssembliesAsComponents = subassemblies;
                    manager.IgnoreHiddenBodies = hidden;
                    manager.IncludeMultibodyPartInterferences = multibody;
                    manager.MakeInterferingPartsTransparent = transparent;
                    manager.UseTransform = useTransform;
                    manager.ShowIgnoredInterferences = showIgnored;
                }
                finally { manager.Done(); }
            }
        }
        private string SourcePersistent(ModelDoc2 doc, object value, string prefix, string scope)
        {
            try { var data = doc.Extension.GetPersistReference3(value) as byte[]; return data == null || data.Length == 0 ? null : Identity.Make(prefix, projectId, scope, Convert.ToBase64String(data)); }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }
        private string SourceId(ModelDoc2 doc, object value, string prefix, string scope, string fallback)
        {
            var id = SourcePersistent(doc, value, prefix, scope);
            if (id != null) return id;
            result.notices.Add(scope + ": source identity fallback used for " + fallback);
            return Identity.Make(prefix, projectId, scope, fallback);
        }
        private void Field(CadObject owner, string name, Action read)
        {
            int warnings = result.warnings.Count;
            Try(owner.id, name, read);
            if (result.warnings.Count > warnings) owner.extractionStatus[name] = "partial";
        }
    }
}





namespace CADVision.SolidWorks
{
    /// <summary>Native SolidWorks export; call only from the SolidWorks STA thread.</summary>
    public static class GlbExporter
    {
        // Isolate translator failures from metadata extraction. Never open dialogs,
        // overwrite an existing export, or treat a diagnostic file as a completed pair.
        public static bool Probe(SldWorks app, string directory, Action<string> log)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Native export diagnostics require STA.");
            directory = Path.GetFullPath(directory);
            if (Directory.Exists(directory) || File.Exists(directory))
                throw new IOException("Choose a new diagnostic directory.");
            var model = app.ActiveDoc as ModelDoc2;
            if (model == null || (model.GetType() != (int)swDocumentTypes_e.swDocPART && model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY))
                throw new InvalidOperationException("Open a saved part or assembly first.");
            string source = model.GetPathName();
            if (String.IsNullOrEmpty(source)) throw new InvalidOperationException("Save the CAD document first.");
            string configuration = model.ConfigurationManager.ActiveConfiguration.Name;
            int stamp = model.GetUpdateStamp();
            Directory.CreateDirectory(directory);
            bool glbPassed = false;
            using (var report = new StreamWriter(Path.Combine(directory, "native-export-diagnostic.txt")))
            {
                report.AutoFlush = true;
                Action<string> record = message => { report.WriteLine(message); if (log != null) log(message); };
                record("UTC: " + DateTime.UtcNow.ToString("o"));
                record("SolidWorks: " + app.RevisionNumber());
                record("Document: " + source + "; configuration: " + configuration);
                record("API: IModelDocExtension.SaveAs3; options: Silent; no UI automation.");
                var selection = (SelectionMgr)model.SelectionManager;
                selection.SuspendSelectionList();
                try
                {
                    model.ClearSelection2(true);
                    foreach (string extension in new[] { ".glb", ".gltf" })
                    {
                        if (!Object.Equals(app.ActiveDoc, model) || model.GetPathName() != source ||
                            model.ConfigurationManager.ActiveConfiguration.Name != configuration || model.GetUpdateStamp() != stamp)
                            throw new InvalidOperationException("CAD state changed; diagnostic stopped.");
                        string path = Path.Combine(directory, "probe" + extension);
                        int errors = 0, warnings = 0;
                        record("Attempt: " + path);
                        try
                        {
                            bool saved = model.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
                            bool exists = File.Exists(path);
                            record("saved=" + saved + "; errors=" + errors + " (" + (swFileSaveError_e)errors +
                                "); warnings=" + warnings + "; bytes=" + (exists ? new FileInfo(path).Length : 0));
                            if (saved && errors == 0 && exists)
                            {
                                if (extension == ".glb") { CheckContainer(path); glbPassed = true; record("GLB container check passed."); }
                                else record("GLTF file produced; dependencies and geometry still require validation.");
                            }
                        }
                        catch (Exception ex) { record("Failure: " + ex.GetType().Name + ": " + ex.Message); }
                        if (!Object.Equals(app.ActiveDoc, model) || model.GetPathName() != source ||
                            model.ConfigurationManager.ActiveConfiguration.Name != configuration || model.GetUpdateStamp() != stamp)
                            throw new InvalidOperationException("CAD state changed during native export; diagnostic stopped.");
                    }
                }
                finally { selection.ResumeSelectionList2(false); }
                record("Diagnostic only: no metadata pair was published. GLB success=" + glbPassed);
            }
            return glbPassed;
        }

        public static int Export(SldWorks app, ModelDoc2 model, string path)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("GLB export requires an STA thread.");
            if (model == null || !Object.Equals(app.ActiveDoc, model))
                throw new InvalidOperationException("The export document must remain active in SolidWorks.");
            if (model.GetType() != (int)swDocumentTypes_e.swDocPART && model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("GLB export requires a part or assembly.");
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Export filename must end in .glb.");
            if (File.Exists(path)) throw new IOException("Refusing to overwrite " + path);
            var selection = (SelectionMgr)model.SelectionManager;
            // Preserve the user's selection while ensuring SaveAs exports the whole model.
            selection.SuspendSelectionList();
            int errors = 0, warnings = 0;
            try
            {
                model.ClearSelection2(true);
                // Use the current extension API, not the obsolete SaveAs method.
                // Translator availability through this API still requires a live test.
                bool saved = model.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
                if (!saved || errors != 0)
                    throw new IOException("SolidWorks GLB export via IModelDocExtension.SaveAs3 failed: errors=" + errors + " (" + (swFileSaveError_e)errors +
                        "), warnings=" + warnings + ". Check whether Extended Reality (*.GLB) is listed in File > Save As. " +
                        "If it is listed, this may be an API export limitation; this error does not prove the translator is missing.");
                CheckContainer(path);
                return warnings;
            }
            finally { selection.ResumeSelectionList2(false); }
        }

        // Basic exporter-output check only. Semantic GLB validation/mapping belongs to the shipper.
        public static void CheckContainer(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 20 || reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 ||
                    reader.ReadUInt32() != stream.Length)
                    throw new InvalidDataException("Native exporter did not produce a complete GLB 2.0 container.");
                bool first = true, seenBinary = false;
                while (stream.Position < stream.Length)
                {
                    if (stream.Length - stream.Position < 8) throw new InvalidDataException("Truncated GLB chunk header.");
                    uint length = reader.ReadUInt32(), type = reader.ReadUInt32();
                    if (length % 4 != 0 || length > stream.Length - stream.Position || (first && (type != 0x4E4F534A || length == 0)))
                        throw new InvalidDataException("Invalid GLB chunk layout.");
                    if (!first && type == 0x4E4F534A) throw new InvalidDataException("Duplicate GLB JSON chunk.");
                    if (type == 0x004E4942)
                    {
                        if (seenBinary) throw new InvalidDataException("Duplicate GLB binary chunk.");
                        seenBinary = true;
                    }
                    stream.Seek(length, SeekOrigin.Current);
                    first = false;
                }
            }
        }
    }

    public static class ExportPipeline
    {
        public static Metadata Export(SldWorks app, string directory, ExtractionOptions options, Action<string> progress, string existingGlb = null)
        {
            string destination = Path.GetFullPath(directory);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("Choose a new export directory; existing exports are never overwritten.");
            if (existingGlb != null) GlbExporter.CheckContainer(existingGlb);
            var model = app.ActiveDoc as ModelDoc2;
            if (model == null) throw new InvalidOperationException("Open a saved part or assembly in SolidWorks.");
            string source = model.GetPathName();
            if (String.IsNullOrEmpty(source)) throw new InvalidOperationException("Save the active model first to establish its identity.");
            string configuration = model.ConfigurationManager.ActiveConfiguration.Name;
            int updateStamp = model.GetUpdateStamp();
            string stage = destination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                int warnings = 0;
                string glbPath = Path.Combine(stage, "model.glb");
                if (existingGlb == null)
                {
                    if (progress != null) progress("Exporting native model.glb...");
                    warnings = GlbExporter.Export(app, model, glbPath);
                }
                else
                {
                    if (progress != null) progress("Copying supplied GLB unchanged; CAD correspondence remains unverified...");
                    CopySuppliedGlb(existingGlb, glbPath);
                }
                EnsureSameModel(app, model, source, configuration, updateStamp);
                if (progress != null) progress("Extracting metadata from the same model/configuration...");
                // Pass detailed metadata progress through the combined runner without
                // mutating the caller's options or dropping its existing callback.
                var metadataOptions = new ExtractionOptions {
                    RunInterferenceDetection = options == null || options.RunInterferenceDetection,
                    Progress = message => {
                        if (progress != null) progress(message);
                        if (options != null && options.Progress != null && options.Progress != progress) options.Progress(message);
                    }
                };
                var metadata = new Extractor().Extract(app, metadataOptions);
                EnsureSameModel(app, model, source, configuration, updateStamp);
                var root = metadata.objects.Find(o => o.id == metadata.project.rootObjectId);
                if (root == null || !String.Equals(root.sourceDocument, source, StringComparison.OrdinalIgnoreCase) || root.configuration != configuration)
                    throw new InvalidOperationException("Metadata source differs from the captured active CAD document; export was not published.");
                metadata.glbAsset = DescribeGlb(glbPath, existingGlb);
                metadata.extractionStatus["glb"] = existingGlb == null ? "native_export_container_checked_mapping_unverified" : "supplied_glb_container_checked_correspondence_unverified";
                // The native GLB is never rescaled to the JSON display units. GLB
                // specifies meters; its actual geometry/axis mapping awaits pair validation.
                metadata.extractionStatus["exportPair"] = existingGlb == null ? "same_document_configuration_root_update_stamp_checked" : "supplied_glb_plus_active_CAD_metadata_correspondence_unverified";
                if (existingGlb != null) metadata.warnings.Add("Supplied GLB: matching CAD document, configuration, revision, scale and node mapping have not been verified. A matching filename is not proof of correspondence.");
                if (warnings != 0) metadata.warnings.Add("Native GLB export warnings=" + warnings + " (" + (swFileSaveWarning_e)warnings + ").");
                if (progress != null) progress("Preprocessing GLB hierarchy and transform mapping...");
                GlbMapping.Apply(glbPath, metadata);
                if (progress != null) progress("Mapping: " + metadata.mappingStatus);
                if (progress != null) progress("Writing metadata.json (schema " + metadata.schemaVersion + ")...");
                metadata.Write(Path.Combine(stage, "metadata.json"));
                // Publish both files together, only after both exporters have succeeded.
                Directory.Move(stage, destination);
                if (progress != null) progress("Published model.glb + metadata.json.");
                return metadata;
            }
            catch (Exception ex)
            {
                // Preserve partial output for diagnosis; never label it a completed export.
                throw new IOException("Export failed. Unpublished diagnostic files: " + stage + ". " + ex.Message, ex);
            }
        }

        public static void CopySuppliedGlb(string source, string destination)
        {
            if (!String.Equals(Path.GetExtension(source), ".glb", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Supplied visualization must be a .glb file.");
            // Copy with the source locked against writes; never modify or rescale it.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            GlbExporter.CheckContainer(destination);
        }
        public static GlbAsset DescribeGlb(string path, string suppliedPath)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return new GlbAsset { byteLength = stream.Length, sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(),
                    sourceMode = suppliedPath == null ? "native_solidworks_export" : "supplied_glb", originalFileName = suppliedPath == null ? null : Path.GetFileName(suppliedPath) };
        }

        private static void EnsureSameModel(SldWorks app, ModelDoc2 model, string source, string configuration, int updateStamp)
        {
            if (!Object.Equals(app.ActiveDoc, model) || model.GetPathName() != source || model.ConfigurationManager.ActiveConfiguration.Name != configuration || model.GetUpdateStamp() != updateStamp)
                throw new InvalidOperationException("Active model, configuration, or root update stamp changed during export. Retry without changing the model.");
        }
    }
}

namespace CADVision.SolidWorks
{
    // File-only preprocessing: no SolidWorks COM calls. Kept separate from extraction
    // so the shipper can run it on a completed pair without a CAD installation.
    public static class GlbMapping
    {
        public sealed class Document { public int? scene; public Scene[] scenes; public Node[] nodes; }
        public sealed class Scene { public int[] nodes; }
        public sealed class Node
        {
            public string name;
            public int[] children;
            public int? mesh, camera;
            public double[] matrix, translation, rotation, scale;
        }
        private sealed class Located { public double[] world; public string path; }

        public static void Apply(string file, Metadata metadata)
        {
            Document glb;
            string sha;
            using (var stream = File.OpenRead(file))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 20 || reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != stream.Length)
                    throw new InvalidDataException("Invalid GLB header for mapping.");
                uint length = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x4E4F534A || length > 64*1024*1024 || length > stream.Length-20 || length%4 != 0)
                    throw new InvalidDataException("Invalid or oversized GLB JSON chunk.");
                glb = new JavaScriptSerializer { MaxJsonLength=64*1024*1024, RecursionLimit=256 }.Deserialize<Document>(Encoding.UTF8.GetString(reader.ReadBytes((int)length)));
                stream.Position = 0;
                using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            if (metadata.glbAsset != null && metadata.glbAsset.sha256 != sha) throw new InvalidDataException("GLB hash does not match metadata; refusing stale mapping.");
            metadata.glbMapping = Match(glb, metadata, sha);
            metadata.mappingStatus = metadata.glbMapping.status;
            metadata.extractionStatus["glbMapping"] = metadata.glbMapping.status;
            metadata.warnings.RemoveAll(w=>w.StartsWith("GLB mapping is pending:",StringComparison.Ordinal) || w.StartsWith("GLB mapping check:",StringComparison.Ordinal));
            metadata.warnings.Add("GLB mapping check: " + metadata.mappingStatus + ". Checks hierarchy and transforms only; geometry/revision and Unity axis conversion still require validation.");
            metadata.warnings.RemoveAll(w=>w.StartsWith("Supplied GLB: matching CAD document, configuration, revision, scale and node mapping",StringComparison.Ordinal));
            if(metadata.glbAsset != null && metadata.glbAsset.sourceMode == "supplied_glb" && !metadata.warnings.Contains("Supplied GLB: geometry, revision and physical mesh size remain unverified despite node correspondence checks."))
                metadata.warnings.Add("Supplied GLB: geometry, revision and physical mesh size remain unverified despite node correspondence checks.");
            // Matching hierarchy/transforms is evidence of node correspondence, not
            // proof that two designs have the same dimensions, material or revision.
            if (metadata.glbAsset != null) metadata.glbAsset.correspondenceStatus = metadata.glbMapping.status;
        }

        public static MappingReport Match(Document glb, Metadata metadata, string sha)
        {
            metadata.Validate();
            if (glb == null || glb.nodes == null || glb.scenes == null || glb.scenes.Length == 0) throw new InvalidDataException("GLB scene/nodes unavailable.");
            int scene = glb.scene ?? (glb.scenes.Length == 1 ? 0 : -1);
            if (scene < 0 || scene >= glb.scenes.Length || glb.scenes[scene] == null) throw new InvalidDataException("No unambiguous default GLB scene.");
            double meters;
            if (metadata.project.documentUnits != null) meters = metadata.project.documentUnits.lengthToMeters;
            else if (metadata.project.units["length"] == "m") meters = 1;
            else throw new InvalidDataException("Missing metadata length conversion.");
            if (!Finite(meters) || meters <= 0) throw new InvalidDataException("Invalid metadata length conversion.");
            var roots = glb.scenes[scene].nodes ?? new int[0];
            var locations = new Dictionary<int,Located>();
            Action<int,double[],string,int> walk = null;
            walk = (index,parent,path,depth) => {
                if (depth > 256 || index < 0 || index >= glb.nodes.Length || locations.ContainsKey(index) || glb.nodes[index] == null)
                    throw new InvalidDataException("Invalid GLB hierarchy: cycle, shared node or bad index.");
                var n = glb.nodes[index];
                var location = new Located { world=Multiply(parent,Local(n)), path=path+"/"+(n.name ?? "")+"["+index+"]" };
                locations.Add(index,location);
                foreach (int child in n.children ?? new int[0]) walk(child,location.world,location.path,depth+1);
            };
            foreach (int root in roots) walk(root,IdentityMatrix(),"",0);
            var report = new MappingReport { glbSha256=sha };
            var objects = metadata.objects.ToDictionary(o=>o.id);
            var records = new Dictionary<string,NodeMapping>();
            Action<CadObject,int[]> match = null;
            match = (obj,pool) => {
                var record = new NodeMapping { objectId=obj.id };
                report.objects.Add(record); records.Add(obj.id,record);
                string expectedName = obj.parentId == null ? Path.GetFileNameWithoutExtension(obj.name) : obj.name;
                var named = pool.Where(i=>glb.nodes[i].camera == null && String.Equals(glb.nodes[i].name,expectedName,StringComparison.Ordinal)).ToArray();
                double[] expected = obj.parentId == null ? IdentityMatrix() : CadMatrix(obj.transform,meters);
                if (obj.suppressed != false) record.reason = "suppressed_or_unknown_suppression";
                else if (expected == null) record.reason = "missing_CAD_transform";
                else
                {
                    foreach (var i in named)
                    {
                        double pe, me; Errors(expected,locations[i].world,out pe,out me);
                        if (pe <= report.positionToleranceMeters && me <= report.matrixTolerance) record.candidateNodeIndices.Add(i);
                    }
                    if (record.candidateNodeIndices.Count == 1)
                    {
                        int i=record.candidateNodeIndices[0]; double pe,me; Errors(expected,locations[i].world,out pe,out me);
                        record.status="matched"; record.glbNodeIndex=i; record.glbNodeName=glb.nodes[i].name; record.glbNodePath=locations[i].path;
                        record.positionErrorMeters=pe; record.matrixError=me; record.reason="parent_hierarchy_name_and_transform_agree";
                    }
                    else { record.status=record.candidateNodeIndices.Count>1 ? "ambiguous" : "unmatched"; record.reason=named.Length==0 ? "no_exact_name_under_matched_parent" : "transform_mismatch_or_multiple_candidates"; }
                }
                foreach (string child in obj.childIds) match(objects[child],record.glbNodeIndex.HasValue ? glb.nodes[record.glbNodeIndex.Value].children ?? new int[0] : new int[0]);
            };
            match(objects[metadata.project.rootObjectId],roots);
            // Do not allow two CAD occurrences to silently claim the same GLB node.
            foreach (var collision in report.objects.Where(r=>r.glbNodeIndex.HasValue).GroupBy(r=>r.glbNodeIndex.Value).Where(g=>g.Count()>1))
                foreach (var r in collision) { r.glbNodeIndex=null; r.status="ambiguous"; r.reason="node_claimed_by_multiple_CAD_objects"; }
            foreach (var obj in report.objects.Select(r=>objects[r.objectId]).Where(o=>o.parentId!=null))
                if (records[obj.parentId].status!="matched") { var r=records[obj.id]; r.glbNodeIndex=null; r.status="unmatched"; r.reason="parent_not_matched"; }
            var claimed=new HashSet<int>(report.objects.Where(r=>r.glbNodeIndex.HasValue).Select(r=>r.glbNodeIndex.Value));
            report.unmappedMeshNodeIndices=locations.Keys.Where(i=>glb.nodes[i].mesh.HasValue && !claimed.Contains(i)).OrderBy(i=>i).ToList();
            report.status=report.objects.All(r=>r.status=="matched") && report.unmappedMeshNodeIndices.Count==0 ? "hierarchy_transform_matched" : "partial_or_unmatched";
            return report;
        }
        public static double[] IdentityMatrix() { return new double[] {1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1}; }
        public static double[] Multiply(double[] a,double[] b)
        {
            var r=new double[16];
            for(int c=0;c<4;c++) for(int row=0;row<4;row++) for(int k=0;k<4;k++) r[c*4+row]+=a[k*4+row]*b[c*4+k];
            return r;
        }
        private static double[] CadMatrix(double[] t,double meters)
        {
            if(t==null || t.Length<13 || t.Take(13).Any(v=>!Finite(v))) return null;
            var m=IdentityMatrix();
            for(int c=0;c<3;c++) for(int r=0;r<3;r++) m[c*4+r]=t[c*3+r]*t[12];
            for(int r=0;r<3;r++) m[12+r]=t[9+r]*meters;
            return m;
        }
        public static double[] Local(Node n)
        {
            if(n.matrix!=null)
            {
                if(n.matrix.Length!=16 || n.matrix.Any(v=>!Finite(v)) || n.translation!=null || n.rotation!=null || n.scale!=null || n.matrix[3]!=0 || n.matrix[7]!=0 || n.matrix[11]!=0 || n.matrix[15]!=1) throw new InvalidDataException("Invalid GLB node matrix.");
                return n.matrix;
            }
            var t=n.translation??new double[3]; var s=n.scale??new double[]{1,1,1}; var q=n.rotation??new double[]{0,0,0,1};
            if(t.Length!=3 || s.Length!=3 || q.Length!=4 || t.Concat(s).Concat(q).Any(v=>!Finite(v)) || Math.Abs(q.Sum(v=>v*v)-1)>1e-5) throw new InvalidDataException("Invalid GLB TRS.");
            double x=q[0],y=q[1],z=q[2],w=q[3];
            return new double[]{(1-2*y*y-2*z*z)*s[0],(2*x*y+2*z*w)*s[0],(2*x*z-2*y*w)*s[0],0,
                (2*x*y-2*z*w)*s[1],(1-2*x*x-2*z*z)*s[1],(2*y*z+2*x*w)*s[1],0,
                (2*x*z+2*y*w)*s[2],(2*y*z-2*x*w)*s[2],(1-2*x*x-2*y*y)*s[2],0,t[0],t[1],t[2],1};
        }
        private static void Errors(double[] a,double[] b,out double position,out double matrix)
        {
            position=0; matrix=0;
            for(int i=0;i<16;i++) if(i>=12 && i<=14) position+=(a[i]-b[i])*(a[i]-b[i]); else matrix=Math.Max(matrix,Math.Abs(a[i]-b[i]));
            position=Math.Sqrt(position);
        }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        public static Metadata ProcessPair(string input,string output)
        {
            if(Directory.Exists(output) || File.Exists(output)) throw new IOException("Choose a new output directory.");
            var serializer=new JavaScriptSerializer { MaxJsonLength=Int32.MaxValue,RecursionLimit=256 };
            var metadata=serializer.Deserialize<Metadata>(File.ReadAllText(Path.Combine(input,"metadata.json")));
            string stage=Path.GetFullPath(output)+".partial-"+Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            string glb=Path.Combine(stage,"model.glb");
            File.Copy(Path.Combine(input,"model.glb"),glb,false);
            Apply(glb,metadata);
            metadata.Write(Path.Combine(stage,"metadata.json"));
            Directory.Move(stage,Path.GetFullPath(output));
            return metadata;
        }
    }
}

namespace CADVision.SolidWorks
{
    internal static class ExportProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ExportCommand command;
            try { command = ExportCommand.Parse(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("Usage: CADVision.Export.exe [new-output-directory] [--glb existing.glb] [--skip-interferences] [--shipper CadenShipper.exe]");
                Console.Error.WriteLine("Offline: CADVision.Export.exe [new-output-directory] --map-pair existing-pair-directory");
                Console.Error.WriteLine("Diagnostic: CADVision.Export.exe [new-output-directory] --probe-native-glb");
                return 2;
            }
            try
            {
                string folder = command.Directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                if (command.MapPair != null)
                {
                    var mapped = GlbMapping.ProcessPair(command.MapPair, folder);
                    PrintMapping(mapped);
                    Console.WriteLine("Preprocessed pair saved to " + Path.GetFullPath(folder));
                    return 0;
                }
                // Check requested handoff configuration before an expensive CAD export.
                if (command.Shipper != null && !File.Exists(command.Shipper)) throw new FileNotFoundException("Requested shipper executable not found.", command.Shipper);
                var clock = Stopwatch.StartNew();
                Action<string> progress = message => Console.WriteLine("[{0:F1}s] {1}", clock.Elapsed.TotalSeconds, message);
                progress("Connecting to SolidWorks");
                var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                if (command.ProbeNativeGlb)
                {
                    bool passed = GlbExporter.Probe(app, folder, progress);
                    Console.WriteLine("Diagnostic report: " + Path.GetFullPath(Path.Combine(folder, "native-export-diagnostic.txt")));
                    return passed ? 0 : 1;
                }
                progress("SolidWorks revision: " + app.RevisionNumber() + (command.Glb == null ? "; GLB API: IModelDocExtension.SaveAs3" : "; using supplied GLB: " + command.Glb));
                Console.WriteLine("Keep the active SolidWorks model unchanged until export completes.");
                var metadata = ExportPipeline.Export(app, folder,
                    new ExtractionOptions { RunInterferenceDetection = !command.SkipInterferences }, progress, command.Glb);
                Console.WriteLine("Exported model.glb + metadata.json to " + Path.GetFullPath(folder));
                Console.WriteLine(metadata.objects.Count + " objects; " + metadata.mates.Count + " mates; " + clock.Elapsed.TotalSeconds.ToString("F1") + " seconds.");
                PrintMapping(metadata);
                foreach (string warning in metadata.warnings) Console.Error.WriteLine("Warning: " + warning);
                if (metadata.notices.Count > 0) Console.WriteLine("Info: " + metadata.notices.Count + " identity notices recorded in metadata.json.");
                // Show present or unresolved tolerances; omit confirmed 'none' entries.
                foreach (var obj in metadata.objects)
                {
                    if (obj.dimensions == null) continue;
                    foreach (var dimension in obj.dimensions)
                    {
                        var t = dimension.tolerance;
                        if (t == null || t.type == "none") continue;
                        Console.WriteLine("Tolerance: {0} / {1}: {2}; lower={3} ({4}); upper={5} ({6}); unit={7}; extraction={8}",
                            obj.name, dimension.name, t.type, t.lowerDeviation.HasValue ? t.lowerDeviation.Value.ToString("G17") : "null", t.lowerDeviationStatus,
                            t.upperDeviation.HasValue ? t.upperDeviation.Value.ToString("G17") : "null", t.upperDeviationStatus, t.unit ?? "not_applicable", t.extractionStatus);
                    }
                }
                if (command.Shipper != null)
                {
                    progress("Starting CADEN Shipper with the completed pair");
                    try
                    {
                        using (var process = Process.Start(ShipperHandoff.CreateStartInfo(command.Shipper, folder)))
                        {
                            if (process == null) throw new IOException("Shipper process did not start.");
                            process.WaitForExit();
                            if (process.ExitCode != 0) throw new IOException("Shipper reported exit code " + process.ExitCode + ".");
                        }
                        progress("Shipper completed successfully");
                    }
                    catch (Exception ex) { Console.Error.WriteLine("Exported files remain at " + Path.GetFullPath(folder) + ". Handoff failed: " + ex.Message); return 3; }
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        private static void PrintMapping(Metadata metadata)
        {
            if (metadata.glbMapping == null) return;
            Console.WriteLine("GLB mapping: " + metadata.glbMapping.status);
            foreach (var m in metadata.glbMapping.objects)
                Console.WriteLine("  " + m.objectId + " -> " + (m.glbNodeIndex.HasValue ? "node " + m.glbNodeIndex.Value : "no node") + ": " + m.status + " (" + m.reason + ")");
        }
    }
    // CLI parsing has no SolidWorks dependency. No model/source-file path is required.
    public sealed class ExportCommand
    {
        public string Directory, Shipper, Glb, MapPair;
        public bool SkipInterferences, ProbeNativeGlb;
        public static ExportCommand Parse(string[] args)
        {
            var result = new ExportCommand();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--skip-interferences" && !result.SkipInterferences) result.SkipInterferences = true;
                else if (args[i] == "--probe-native-glb" && !result.ProbeNativeGlb) result.ProbeNativeGlb = true;
                else if (args[i] == "--map-pair" && result.MapPair == null && i+1 < args.Length && !args[i+1].StartsWith("--"))
                    result.MapPair = Path.GetFullPath(args[++i]);
                else if (args[i] == "--glb" && result.Glb == null && i+1 < args.Length && !args[i+1].StartsWith("--"))
                    result.Glb = Path.GetFullPath(args[++i]);
                else if (args[i] == "--shipper" && result.Shipper == null && i+1 < args.Length && !args[i+1].StartsWith("--"))
                    result.Shipper = Path.GetFullPath(args[++i]);
                else if (!args[i].StartsWith("--") && result.Directory == null) result.Directory = Path.GetFullPath(args[i]);
                else throw new ArgumentException("Unknown, duplicate, or incomplete argument: " + args[i]);
            }
            if (result.MapPair != null && (result.Glb != null || result.Shipper != null || result.SkipInterferences)) throw new ArgumentException("--map-pair is an offline-only mode; do not combine it with CAD export/shipper options.");
            if (result.ProbeNativeGlb && (result.MapPair != null || result.Glb != null || result.Shipper != null || result.SkipInterferences))
                throw new ArgumentException("--probe-native-glb cannot be combined with other modes.");
            return result;
        }
    }
    // Launch only the caller-specified shipper; networking/Quest logic belongs there.
    public static class ShipperHandoff
    {
        public static ProcessStartInfo CreateStartInfo(string executable, string directory)
        {
            string glb = Path.GetFullPath(Path.Combine(directory, "model.glb"));
            string metadata = Path.GetFullPath(Path.Combine(directory, "metadata.json"));
            if (!File.Exists(glb) || !File.Exists(metadata)) throw new IOException("Both completed export files are required for shipper handoff.");
            return new ProcessStartInfo { FileName = Path.GetFullPath(executable),
                Arguments = "--glb " + QuoteFile(glb) + " --metadata " + QuoteFile(metadata),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)), UseShellExecute = false };
        }
        // These are Windows file paths ending in filenames, not shell commands.
        private static string QuoteFile(string path)
        {
            if (path.IndexOf('"') >= 0 || path.EndsWith("\\")) throw new ArgumentException("Invalid handoff filename.");
            return "\"" + path + "\"";
        }
    }
}
