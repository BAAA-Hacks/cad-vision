using Core.Primitives.DataStructures.Project;
using Core.Primitives.Operations.Project;
using Newtonsoft.Json.Linq;

internal static class ProjectLoaderChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JObject Document() => JObject.Parse("""
    {
      "schemaVersion":"1.0",
      "project":{"id":"P","name":"Project","rootObjectId":"R","units":{"mass":"kg","length":"mm"}},
      "objects":[
        {"id":"R","name":"Root","type":"assembly","parentId":null,"childIds":["A","B"]},
        {"id":"A","name":"Part A","type":"part","parentId":"R","childIds":[],"mass":0,"fixed":false,"volume":null,"customProperties":{"Built":"2026-09-26T00:00:00Z"}},
        {"id":"B","name":"Part B","type":"part","parentId":"R","childIds":[],"mass":"heavy","material":{"assigned":true,"name":"Steel","density":7850}}
      ],
      "mates":[{"id":"M","componentIds":["A","B"],"type":"concentric","status":"solved","suppressed":false}]
    }
    """);
    private static ProjectLoadResult Load(JObject doc, CapabilityState state = CapabilityState.Available) => LoadProject.Load(doc.ToString(), new ProjectLoadOptions { MateExportState = state });
    public static void Run()
    {
        var doc = Document(); var load = Load(doc); var snapshot = load.Snapshot!;
        Require(load.Success && snapshot.Capabilities.Hierarchy == CapabilityState.Available && snapshot.Capabilities.MechanicalGraph == CapabilityState.Available, "Valid project failed.");
        Require(snapshot.ComponentsById.Count == 3 && snapshot.MatesById.Count == 1, "Canonical ID indexes missing.");
        var fields = snapshot.ComponentsById["A"].Properties;
        Require(fields["mass"].State == AvailabilityState.Available && (double)fields["mass"].Value! == 0 && fields["mass"].Unit == "kg", "Zero/unit lost.");
        Require(fields["fixed"].State == AvailabilityState.Available && !(bool)fields["fixed"].Value!, "Explicit false lost.");
        Require(fields["suppressed"].State == AvailabilityState.Missing && !fields["suppressed"].WasPresent, "Missing boolean became false.");
        Require(fields["volume"].WasPresent && fields["volume"].RawValue!.Type == JTokenType.Null && fields["volume"].State == AvailabilityState.Missing, "Explicit null vs absence lost.");
        Require(fields["mass"].Provenance.SourceField == "/objects/1/mass" && fields["mass"].Provenance.SnapshotId == snapshot.SnapshotId, "Provenance lost.");
        Require(snapshot.ComponentsById["B"].Properties["mass"].State == AvailabilityState.Invalid && (string?)snapshot.ComponentsById["B"].Properties["mass"].RawValue == "heavy", "Invalid raw value lost.");
        Require(fields["customProperties"].State == AvailabilityState.Available, "Date-like property text was converted into a date token.");
        Require(snapshot.IdentityScope == IdentityScope.SnapshotOnly, "Stable identity was assumed.");
        Require(Load(doc).Snapshot!.SnapshotId == snapshot.SnapshotId, "Identical input received different snapshot IDs.");
        doc["objects"]![1]!["mass"] = 20;
        Require(Load(doc).Snapshot!.SnapshotId != snapshot.SnapshotId, "Changed export retained snapshot ID.");
        var raw = snapshot.CopyRawExport(); raw["objects"]![1]!["mass"] = 99;
        var record = snapshot.ComponentsById["A"].CopyRawRecord(); record["mass"] = 88;
        var custom = fields["customProperties"].Value!; custom["Built"] = "mutated";
        Require((double)snapshot.ComponentsById["A"].CopyRawRecord()["mass"]! == 0 && (string?)fields["customProperties"].Value!["Built"] != "mutated", "Caller mutated the canonical snapshot.");
        Require(((IDictionary<string, ComponentMetadata>)snapshot.ComponentsById).IsReadOnly, "Component index mutable.");
        doc = Document(); ((JArray)doc["objects"]!).Add(doc["objects"]![1]!.DeepClone());
        Require(!Load(doc).Success && Load(doc).Diagnostics.Any(d => d.Fatal && d.Code == "DUPLICATE_COMPONENT_ID"), "Duplicate component identity did not fail atomically.");
        foreach (string json in new[] { "not json", "[]", "{} {}", "{\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"}" })
            Require(!LoadProject.Load(json).Success, "Malformed JSON accepted.");
        doc = Document(); doc["schemaVersion"] = "2.0"; Require(!Load(doc).Success, "Unsupported schema accepted.");
        doc = Document(); doc["mates"]![0]!["componentIds"] = new JArray("A", "missing");
        var badMate = Load(doc);
        Require(badMate.Success && badMate.Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Invalid && badMate.Snapshot.Capabilities.Hierarchy == CapabilityState.Available && badMate.Snapshot.Capabilities.Properties == CapabilityState.Available, "Invalid graph killed healthy capabilities.");
        Require(badMate.Snapshot!.MatesById.Count == 0 && badMate.Diagnostics.Any(d => d.Scope == DiagnosticScope.MechanicalGraph && d.IsError && !d.Fatal), "Invalid mate capability exposed a partial index or lost diagnostics.");
        doc = Document(); ((JArray)doc["mates"]!).Add(doc["mates"]![0]!.DeepClone()); Require(Load(doc).Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Invalid, "Duplicate mate not degraded.");
        doc = Document(); doc["mates"] = new JArray();
        Require(Load(doc).Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Available && Load(doc, CapabilityState.Unavailable).Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Unavailable, "Empty mates inferred coverage.");
        doc.Remove("mates"); Require(Load(doc, CapabilityState.Unavailable).Success && Load(doc).Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Invalid, "Missing mate data mishandled.");
        doc = Document(); doc["objects"]![1]!["parentId"] = "missing";
        Require(Load(doc).Snapshot!.Capabilities.Hierarchy == CapabilityState.Invalid && Load(doc).Snapshot!.Capabilities.MechanicalGraph == CapabilityState.Available, "Invalid hierarchy killed graph/properties.");
        doc = Document(); foreach (JObject o in (JArray)doc["objects"]!) { o.Remove("parentId"); o.Remove("childIds"); }
        Require(Load(doc).Snapshot!.Capabilities.Hierarchy == CapabilityState.Unavailable, "Missing hierarchy inferred empty tree.");
        doc = Document(); doc["objects"]![0]!["childIds"] = new JArray(); doc["objects"]![1]!["type"] = "assembly"; doc["objects"]![2]!["type"] = "assembly";
        doc["objects"]![1]!["parentId"] = "B"; doc["objects"]![1]!["childIds"] = new JArray("B"); doc["objects"]![2]!["parentId"] = "A"; doc["objects"]![2]!["childIds"] = new JArray("A");
        Require(Load(doc).Snapshot!.Capabilities.Hierarchy == CapabilityState.Invalid, "Disconnected cycle accepted.");
        doc = Document(); doc["sampleInfo"] = new JObject { ["fixture"] = true };
        var fixture = LoadProject.Load(doc.ToString(), new ProjectLoadOptions { MateExportState = CapabilityState.Available, ComponentIdsStableAcrossSnapshots = true }).Snapshot!;
        Require(fixture.IdentityScope == IdentityScope.SnapshotOnly && fixture.Capabilities.MechanicalGraph == CapabilityState.Unavailable && fixture.ComponentsById["A"].Properties["mass"].State == AvailabilityState.Missing, "Fixture became stable engineering evidence.");
        doc.Remove("sampleInfo"); Require(LoadProject.Load(doc.ToString(), new ProjectLoadOptions { ComponentIdsStableAcrossSnapshots = true }).Snapshot!.IdentityScope == IdentityScope.ProjectStable, "Host stability guarantee ignored.");
        doc = Document(); doc["objects"]![1]!["mass"] = JToken.Parse("9223372036854775808");
        doc["mates"]![0]!["axis"] = new JArray(JToken.Parse("9223372036854775808"), 0, 1);
        Require(Load(doc).Success, "BigInteger JSON value crashed loading.");
        string oversizedIntegerJson = Document().ToString(Newtonsoft.Json.Formatting.None).Replace("\"mass\":0", "\"mass\":" + "1" + new string('0', 400));
        Require(!LoadProject.Load(oversizedIntegerJson).Success, "Integer beyond the JSON parser's supported range should return diagnostics, not throw.");
        doc = Document(); doc["objects"]![1]!["childIds"] = JValue.CreateNull();
        Require(Load(doc).Snapshot!.Capabilities.Hierarchy == CapabilityState.Unavailable, "Null optional hierarchy should be unavailable.");
        doc["objects"]![2]!["parentId"] = "A";
        Require(Load(doc).Snapshot!.Capabilities.Hierarchy == CapabilityState.Invalid, "Incomplete hierarchy masked contradictory exported links.");
        Console.WriteLine("PASS: canonical snapshot immutability/provenance, fatal identity validation, graph/hierarchy degradation, nullable fields, snapshot identity, fixture safeguards.");
        string path = Path.Combine(Desktop.Configuration.LocalConfiguration.FindDirectory(), "data", "metadata.json");
        if (File.Exists(path))
        {
            var real = LoadProject.Load(File.ReadAllText(path)); Require(real.Success, "Local metadata failed canonical load.");
            Console.WriteLine("PASS: local fixture canonical load (" + real.Snapshot!.ComponentsById.Count + " objects, graph " + real.Snapshot.Capabilities.MechanicalGraph + ").");
        }
    }
}
