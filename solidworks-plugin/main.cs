// CADVision SolidWorks metadata extractor — single-file edition.
// Target: Windows x64, .NET Framework 4.8, SolidWorks 2020+.
// References: System.Core, System.Web.Extensions, System.Xml, System.Xml.Linq,
// SolidWorks.Interop.sldworks.dll, SolidWorks.Interop.swconst.dll.
// Compile as a console application. Do not include the separate source files too.
// Usage: CADVision.Metadata.exe <new-output-directory> [--skip-interferences]
// Requires a running SolidWorks instance and a saved active part or assembly.
// Data flow: attach -> traverse occurrences -> read fields/mates -> interference check -> JSON.
// No GLB export, shipper, or add-in registration in this metadata-only step.
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
    // Assembly identity and declared SI units, independent of SolidWorks display units.
    public sealed class Project
    {
        public string id, name, rootObjectId;
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
    public sealed class AxisRecord { public string id, name; public double[] origin, direction; }
    public sealed class PlaneRecord { public string id, name; public double[] origin, normal; }
    public sealed class PointRecord { public string id, name; public double[] position; }
    public sealed class DimensionRecord
    {
        public string id, name, type, unit;
        public int? nativeDisplayType, nativeParameterType;
        public double? value;
        public List<MateReference> references = new List<MateReference>();
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
            materialDatabases.Clear();
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                throw new InvalidOperationException("SolidWorks API calls require an STA thread.");
            occurrences.Clear(); components.Clear();
            model = app.ActiveDoc as ModelDoc2;
            if (model == null || (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY && model.GetType() != (int)swDocumentTypes_e.swDocPART))
                throw new InvalidOperationException("Open and activate a SolidWorks part or assembly first. Drawings are not supported.");
            bool isPart = model.GetType() == (int)swDocumentTypes_e.swDocPART;
            var path = model.GetPathName();
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Save the part or assembly first to establish its project identity.");
            projectId = Identity.Make("PROJECT", Path.GetFullPath(path).ToUpperInvariant());
            var config = model.ConfigurationManager.ActiveConfiguration;
            var root = new CadObject { id = Identity.Make(isPart ? "PART" : "ASSY", projectId, config.Name), name = model.GetTitle(), type = isPart ? "part" : "assembly", sourceDocument = path,
                configuration = config.Name, suppressed = false, fixedState = "not_applicable_root_document", occurrencePath = "", identityBasis = "project_path_and_configuration" };
            result = new Metadata { project = new Project { id = projectId, name = model.GetTitle(), rootObjectId = root.id } };
            result.objects.Add(root);
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
            if (options.RunInterferenceDetection) Try(root.id, "interference detection", ReadInterferences);
            if (result.extractionStatus["interferences"] == "pending") result.extractionStatus["interferences"] = "unavailable";
            result.warnings.Add("Exact remaining DOF vectors are not implemented. Mate solved/conflicting classifications remain unknown unless supported by explicit native evidence; no solver state is guessed.");
            }
            result.warnings.Add("Custom properties use cached values or raw expressions. Rebuild/configuration changes are not forced. See extractionStatus and warnings for incomplete reads.");
            result.Validate();
            return result;
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

        // Calculate through the active root document. Select occurrences for assemblies
        // or solid bodies for standalone parts, including hidden bodies.
        // SelectedItems belongs to the mass calculation object, not the UI selection list.
        // Request SI units, include hidden geometry, and validate before assigning results.
        private void ReadMass(CadObject o, object[] selected)
        {
            o.extractionStatus["massProperties"] = "unavailable";
            if (selected.Length == 0) { result.warnings.Add(o.id + ": no solid bodies/resolved components for mass calculation; physical properties remain null."); return; }
            int where = (int)swMassPropertyMoment_e.swMassPropertyMomentAboutCenterOfMass;
            string stage = "CreateMassProperty2";
            Try(o.id, "IMassProperty2", () => {
                try
                {
                    var mass = model.Extension.CreateMassProperty2() as MassProperty2;
                    if (mass == null) throw new InvalidOperationException("No mass-property object returned.");
                    stage = "UseSystemUnits"; mass.UseSystemUnits = true;
                    stage = "IncludeHiddenBodiesOrComponents"; mass.IncludeHiddenBodiesOrComponents = true;
                    stage = "SelectedItems"; mass.SelectedItems = selected;
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
            // The older supported API can recover part-body calculations when the newer
            // COM object fails. Restrict fallback to standalone parts: AddBodies has
            // different assembly semantics and must not select the whole assembly by mistake.
            if ((MetadataMath.PhysicalStatus(o) != "complete" || o.effectiveDensity == null) && model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                o.extractionStatus["massPropertiesMethod"] = "IMassProperty2_then_IMassProperty_fallback";
                stage = "CreateMassProperty";
                Try(o.id, "IMassProperty fallback", () => {
                    try
                    {
                        var legacy = model.Extension.CreateMassProperty() as MassProperty;
                        if (legacy == null) throw new InvalidOperationException("No legacy mass-property object returned.");
                        stage = "UseSystemUnits"; legacy.UseSystemUnits = true;
                        stage = "AddBodies";
                        if (!legacy.AddBodies(selected)) throw new InvalidOperationException("AddBodies rejected the selected solid bodies.");
                        MetadataMath.ReadPhysicalFields(o, () => legacy.Mass, () => legacy.Volume,
                            () => legacy.CenterOfMass as double[], () => legacy.GetMomentOfInertia(where) as double[],
                            (field, read) => Try(o.id, "IMassProperty." + field, read));
                        if (o.effectiveDensity == null) Try(o.id, "IMassProperty.Density", () => SetNativeDensity(o, legacy.Density, "IMassProperty.Density"));
                    }
                    catch (COMException e) { throw new InvalidOperationException(stage + " failed (0x" + e.ErrorCode.ToString("X8") + "): " + e.Message, e); }
                });
            }
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
    }

    // Pure conversions are kept separate so unit/coordinate mistakes can be tested
    // without a running SolidWorks instance. All numeric output stays in SI units.
    public static class MetadataMath
    {
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
            var math = application.GetMathUtility() as MathUtility;
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

        private void ReadFeatureTree(Feature feature, ModelDoc2 doc, CadObject owner, Component2 component, bool sameConfig, HashSet<string> visited, HashSet<string> dimensions)
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
                Field(owner, "dimensions", () => {
                    for (var display = feature.GetFirstDisplayDimension() as DisplayDimension; display != null; display = feature.GetNextDisplayDimension(display) as DisplayDimension)
                    {
                        var dim = display.GetDimension2(0);
                        if (dim == null) continue;
                        var name = dim.FullName;
                        if (!dimensions.Add(name)) continue;
                        var record = new DimensionRecord { id = SourceId(doc, dim, "DIM", owner.id, name), name = name, nativeDisplayType = display.Type2, nativeParameterType = dim.GetType(), type = MetadataMath.DimensionType(display.Type2) };
                        owner.dimensions.Add(record);
                        // Angular radians and linear meters match project.units.
                        if (record.type == "unknown") { owner.extractionStatus["dimensions"] = "partial"; result.warnings.Add(record.id + ": unsupported dimension " + name + " (display type " + record.nativeDisplayType + ", parameter type " + record.nativeParameterType + "); numeric value left null."); }
                        else
                        {
                            record.unit = record.type == "angle" ? "rad" : "m";
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
                });
            }
            for (var child = feature.GetFirstSubFeature() as Feature; child != null; child = child.GetNextSubFeature() as Feature)
                ReadFeatureTree(child, doc, owner, component, sameConfig, visited, dimensions);
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
    // PART 4: CONSOLE RUNNER
    // Main attaches to an existing SolidWorks instance, extracts metadata, and writes it.
    // It does not launch SolidWorks, register an add-in, export GLB, or invoke the shipper.
    internal static class Program
    {
        // SolidWorks COM calls run on a single-threaded apartment (STA).
        // Exit codes: 0 = wrote output (possibly with warnings), 1 = failure, 2 = bad arguments.
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] != "--skip-interferences")) { Console.Error.WriteLine("Usage: CADVision.Metadata.exe <new-output-directory> [--skip-interferences]"); return 2; }
            try
            {
                var folder = Path.GetFullPath(args[0]);
                var path = Path.Combine(folder, "metadata.json");
                if (File.Exists(path)) throw new IOException("metadata.json already exists. Choose a new export directory.");
                var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                var metadata = new Extractor().Extract(app, new ExtractionOptions { RunInterferenceDetection = args.Length == 1 });
                Directory.CreateDirectory(folder);
                metadata.Write(path);
                Console.WriteLine("Wrote " + path + " (" + metadata.objects.Count + " objects, " + metadata.mates.Count + " mates)");
                foreach (var warning in metadata.warnings) Console.Error.WriteLine("Warning: " + warning);
                if (metadata.notices.Count > 0) Console.WriteLine("Info: " + metadata.notices.Count + " identity notices recorded in metadata.json; these do not mean values are missing.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
    }
}

