using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.Operations.Project;
using Core.Primitives.Operations.MechanicalGraph;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Issues.Checkers;
using Core.Tools.Query;
using Newtonsoft.Json.Linq;

internal static class Schema21Checks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Document() => JObject.Parse("""
    {"schemaVersion":"2.1","project":{"id":"export","name":"Real-shaped test","rootObjectId":"R","units":{"mass":"g","length":"mm","volume":"cm^3","density":"g/cm^3","inertia":"g*mm^2"}},
     "coordinateSystem":"Root axes declared as text only","extractionStatus":{"mates":"attempted_requires_live_verification"},
     "objects":[
      {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A"],"suppressed":false,"fixed":null,"fixedState":"not_applicable_root_document","material":{"assigned":null,"name":null,"density":null},"mass":2,"volume":3,"dimensions":[],"extractionStatus":{"dimensions":"complete","massProperties":"complete","massPropertiesMethod":"IMassProperty2"}},
      {"id":"A","name":"Part","type":"part","parentId":"R","childIds":[],"suppressed":false,"fixed":false,"material":{"assigned":true,"name":"Steel","density":7.8},"mass":2,"volume":3,"definitionStatus":"under_defined","nativeConstrainedStatus":{"code":1,"name":"native-test"},"centerOfMass":[1,2,3],"extractionStatus":{"massProperties":"complete"}}
     ],"mates":[]}
    """);
    private static ProjectSnapshot Load(JObject d) { var result = LoadProject.Load(d.ToString()); Require(result.Success, "2.1 load failed: " + string.Join(",", result.Diagnostics.Select(x => x.Code))); return result.Snapshot!; }
    public static async Task RunAsync()
    {
        var doc = Document(); var s = Load(doc); var root = s.ComponentsById["R"].Properties; var part = s.ComponentsById["A"].Properties;
        Require(s.SchemaVersion == "2.1" && s.Capabilities.Hierarchy == CapabilityState.Available && s.Capabilities.MechanicalGraph == CapabilityState.Unavailable, "2.1 capabilities misreported.");
        Require(root["material"].State == AvailabilityState.Missing && root["fixed"].State == AvailabilityState.NotApplicable && root["dimensions"].State == AvailabilityState.Available && ((JArray)root["dimensions"].Value!).Count == 0, "Unknown/inapplicable/confirmed-empty mapping failed.");
        Require(part["volume"].Unit == "cm^3" && part["centerOfMass"].State == AvailabilityState.Missing, "Explicit volume unit ignored or text frame guessed.");
        Require((string?)part["material"].Value!["densityUnit"] == "g/cm^3" && (double)part["material"].Value!["density"]! == 7.8, "Explicit density lost.");
        Require((int?)part["definitionStatus"].SourceEvidence!["nativeConstrainedStatus"]!["code"] == 1, "Native provenance lost.");
        var evidence = part["definitionStatus"].SourceEvidence!; evidence["nativeConstrainedStatus"]!["code"] = 99;
        Require((int?)part["definitionStatus"].SourceEvidence!["nativeConstrainedStatus"]!["code"] == 1, "Evidence is mutable.");
        var context = s.CopyExportContext(); context["coordinateSystemDeclaration"] = "changed";
        Require((string?)s.CopyExportContext()["coordinateSystemDeclaration"] == "Root axes declared as text only", "Export context mutable.");
        var graph = BuildMechanicalGraph.Build(doc.ToString(), GraphDataState.Available).Graph!;
        Require(Math.Abs(graph.NodesById["A"].Volume.Value!.Value - 3e-6) < 1e-15 && Math.Abs(graph.NodesById["A"].Mass.Value!.Value - .002) < 1e-15, "Graph quantities disagree with 2.1 explicit units.");
        doc["mechanicalScopes"] = new JArray(new JObject { ["scopeAssemblyId"] = "R", ["configuration"] = "Default", ["source"] = "test", ["membershipCoverage"] = "Complete", ["mateCoverage"] = "Complete", ["occurrenceIds"] = new JArray("A"), ["mateIds"] = new JArray() });
        Require(Math.Abs(Load(doc).MechanicalScopes[0].Graph!.NodesById["A"].Volume.Value!.Value - 3e-6) < 1e-15, "Scope projection reverted schema units.");
        doc = Document(); doc["objects"]![1]!["extractionStatus"]!["massProperties"] = "failed";
        Require(Load(doc).ComponentsById["A"].Properties["mass"].State == AvailabilityState.Missing && BuildMechanicalGraph.Build(doc.ToString(), GraphDataState.Available).Graph!.NodesById["A"].Mass.State == ValueState.Missing, "Failed extraction exposed numeric evidence.");
        doc = Document(); doc["objects"]![0]!["extractionStatus"]!["dimensions"] = "partial";
        Require(Load(doc).ComponentsById["R"].Properties["dimensions"].State == AvailabilityState.Missing, "Partial empty dimensions became confirmed empty.");
        doc = Document(); doc["objects"]![0]!["fixed"] = false;
        Require(Load(doc).ComponentsById["R"].Properties["fixed"].State == AvailabilityState.Invalid, "Contradictory inapplicability accepted.");
        doc = Document(); doc["objects"]![1]!["fixedState"] = "not_applicable_root_document"; doc["objects"]![1]!["fixed"] = null;
        Require(Load(doc).ComponentsById["A"].Properties["fixed"].State == AvailabilityState.Invalid, "Root-only applicability leaked to part.");
        doc = Document(); doc["project"]!["units"]!["volume"] = "mm";
        Require(Load(doc).ComponentsById["A"].Properties["volume"].State == AvailabilityState.Invalid, "Malformed explicit volume unit accepted.");
        doc = Document(); doc["objects"]![1]!["extractionStatus"] = 7;
        Require(Load(doc).ComponentsById["A"].Properties["mass"].State == AvailabilityState.Invalid, "Malformed extraction status accepted.");
        var registry = SemanticQueryTools.Create(s, new ProjectAssociation("test"));
        var response = await registry.ExecuteAsync("get_object_details", new JObject { ["projectId"] = "test", ["snapshotId"] = s.SnapshotId, ["objectIds"] = new JArray("R"), ["fields"] = new JArray("fixed", "material", "dimensions") });
        Require((bool)response["success"]! && (string?)response["data"]!["items"]![0]!["properties"]!["fixed"]!["status"] == "not_applicable", "Tool lost inapplicability.");
        Require(!registry.Declarations.Any(t => (string?)t["name"] == "find_mechanical_path"), "Unscoped 2.1 export advertised traversal.");
        doc = Document();
        doc["objects"]![1]!["inertia"] = new JObject { ["ixx"] = 2, ["iyy"] = 3, ["izz"] = 4, ["ixy"] = .1, ["ixz"] = -.2, ["iyz"] = .3 };
        var spatialSnapshot = Load(doc); var spatialRegistry = SemanticQueryTools.Create(spatialSnapshot, new ProjectAssociation("test"));
        var spatialArgs = new JObject { ["projectId"] = "test", ["snapshotId"] = spatialSnapshot.SnapshotId, ["objectIds"] = new JArray("A", "R"), ["fields"] = new JArray("centerOfMass", "inertia") };
        var spatial = await spatialRegistry.ExecuteAsync("get_object_details", spatialArgs);
        foreach (var field in new[] { "centerOfMass", "inertia" })
        {
            var present = spatial["data"]!["items"]![0]!["properties"]![field]!;
            var absent = spatial["data"]!["items"]![1]!["properties"]![field]!;
            Require((string?)present["reasonCode"] == "SPATIAL_REFERENCE_UNMAPPED" && (bool)present["exportedValuePresent"]! && present["value"]!.Type == JTokenType.Null && ((string)present["reason"]!).Contains("importer limitation"), "Exported spatial data misreported as absent.");
            Require((string?)absent["reasonCode"] == null && !(bool)absent["exportedValuePresent"]!, "Absent spatial field mislabeled as importer limitation: " + absent);
        }
        doc["coordinateSystem"] = "SolidWorks root document axes; positions/transform translations in project.units.length; inertia about center of mass; directions and rotations dimensionless";
        var mapped = Load(doc); var mappedPart = mapped.ComponentsById["A"].Properties;
        Require(mappedPart["centerOfMass"].State == AvailabilityState.Available && mappedPart["centerOfMass"].Unit == "mm" && mappedPart["centerOfMass"].CoordinateFrame == "solidworks_root_document_axes", "Known center-of-mass frame did not map.");
        Require(mappedPart["inertia"].State == AvailabilityState.Available && mappedPart["inertia"].Unit == "g*mm^2" && (string?)mappedPart["inertia"].SpatialReference!["referencePoint"] == "object_center_of_mass", "Known inertia frame/units did not map.");
        Require(JToken.DeepEquals(mappedPart["centerOfMass"].Value, doc["objects"]![1]!["centerOfMass"]) && JToken.DeepEquals(mappedPart["inertia"].Value, doc["objects"]![1]!["inertia"]), "Spatial mapping changed source values.");
        var frameCopy = mappedPart["inertia"].SpatialReference!; frameCopy["referencePoint"] = "mutated";
        Require((string?)mappedPart["inertia"].SpatialReference!["referencePoint"] == "object_center_of_mass", "Spatial reference was mutable.");
        spatialArgs["snapshotId"] = mapped.SnapshotId;
        var mappedReply = await SemanticQueryTools.Create(mapped, new ProjectAssociation("test")).ExecuteAsync("get_object_details", spatialArgs);
        Require((string?)mappedReply["data"]!["items"]![0]!["properties"]!["inertia"]!["spatialReference"]!["referencePoint"] == "object_center_of_mass", "Tool lost inertia reference point.");
        var inertiaReply = mappedReply["data"]!["items"]![0]!["properties"]!["inertia"]!;
        Require(((JObject)inertiaReply["value"]!).Count == 9 && (double)inertiaReply["value"]!["Lxx"]! == 2 && (double)inertiaReply["value"]!["Lyy"]! == 3 && (double)inertiaReply["value"]!["Lzz"]! == 4
            && (double)inertiaReply["value"]!["Lxz"]! == -.2 && (double)inertiaReply["value"]!["Lyz"]! == .3
            && inertiaReply["value"]!["iyy"] == null && inertiaReply["value"]!["Py"] == null
            && JToken.DeepEquals(inertiaReply["value"]!["Lxy"], inertiaReply["value"]!["Lyx"])
            && (string?)inertiaReply["componentSourceFields"]!["Lyy"] == "/objects/1/inertia/iyy", "SolidWorks display labels lost values/source mapping or confused L/P/I sections.");
        doc["project"]!["units"]!["inertia"] = "g";
        Require(Load(doc).ComponentsById["A"].Properties["inertia"].State == AvailabilityState.Invalid, "Invalid inertia unit admitted.");
        doc["project"]!["units"]!["inertia"] = "g*mm^2"; doc["objects"]![1]!["extractionStatus"]!["massProperties"] = "partial";
        Require(Load(doc).ComponentsById["A"].Properties["inertia"].State == AvailabilityState.Missing, "Partial extraction overrode spatial safeguards.");
        Console.WriteLine("PASS: schema 2.1, unknown/inapplicable values, extraction coverage, explicit units, immutable provenance, versioned spatial mapping, graph projection and tool responses.");
    }
    public static async Task AuditAsync(string path)
    {
        var loaded = LoadProject.Load(File.ReadAllText(path));
        foreach (var d in loaded.Diagnostics) Console.WriteLine($"{d.Code} {d.Path}: {d.Message}");
        if (!loaded.Success) throw new Exception("Metadata audit failed to load; see diagnostics above.");
        var s = loaded.Snapshot!; var association = new ProjectAssociation("offline-audit");
        var issues = await Core.Tools.Issues.IssueAccess.OpenAsync(s, association, new IssueAccessChecks.Storage());
        var registry = SemanticQueryTools.Create(s, association, issues: issues);
        Console.WriteLine($"LOADED: schema {s.SchemaVersion}, {s.ComponentsById.Count} objects, {s.MatesById.Count} mate records; hierarchy={s.Capabilities.Hierarchy}, graph={s.Capabilities.MechanicalGraph}, declared tools={registry.Declarations.Count}");
        var details = await registry.ExecuteAsync("get_object_details", new JObject { ["projectId"] = "offline-audit", ["snapshotId"] = s.SnapshotId, ["objectIds"] = new JArray(s.ComponentsById.Keys.Take(32)), ["fields"] = new JArray("mass", "volume", "fixed", "material", "dimensions", "centerOfMass", "inertia") });
        Require((bool)details["success"]!, "Offline detail query failed: " + details);
        foreach (var row in (JArray)details["data"]!["items"]!) Console.WriteLine(row["name"] + ": " + string.Join(", ", ((JObject)row["properties"]!).Properties().Select(p => p.Name + "=" + p.Value["status"] + (p.Name == "mass" || p.Name == "volume" ? " (" + p.Value["value"] + " " + p.Value["unit"] + ")" : ""))));
        var report = await registry.ExecuteAsync("list_issues", new JObject { ["projectId"] = association.ProjectId, ["snapshotId"] = s.SnapshotId });
        Require((bool)report["success"]!, "Offline issue access failed.");
        foreach (var issue in (JArray)report["data"]!["items"]!) Console.WriteLine("FINDING: " + issue["checkerId"] + " " + issue["severity"] + " " + issue["issueId"]);
        Console.WriteLine("PASS: offline metadata load, semantic detail query and explicit issue scan. No Gemini, persistence or UI changes.");
    }
}
