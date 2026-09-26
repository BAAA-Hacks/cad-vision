using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CADVision;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class CadIngestionTests
{
    private static JObject Valid() => JObject.Parse(@"{
      'schemaVersion':'1.0', 'project':{'rootObjectId':'A'},
      'objects':[
        {'id':'A','name':'Repeated','glbNodeIndex':0,'parentId':null,'childIds':['B']},
        {'id':'B','name':'Repeated','glbNodeIndex':1,'parentId':'A','childIds':[], 'mass':null,
         'future':{'evidence':'retained'}}]}");

    private static JObject Exported()
    {
        var doc = Valid();
        doc["schemaVersion"] = "2.1";
        doc["mappingStatus"] = "assigned_by_exporter";
        foreach (JObject obj in doc["objects"]) obj.Remove("glbNodeIndex");
        // Deliberately reversed: mapping order and display names are not identity.
        doc["glbMapping"] = JObject.Parse(@"{'status':'assigned_by_exporter','objects':[
            {'objectId':'B','status':'matched','glbNodeIndex':1,'candidateNodeIndices':[99]},
            {'objectId':'A','status':'matched','glbNodeIndex':0}]}");
        return doc;
    }

    [Test]
    public void ExporterMappingJoinsByIdAndPreservesSourceMetadata()
    {
        var doc = Exported();
        string json = doc.ToString();
        var metadata = new CadMetadata(json);
        Assert.That(metadata.HasVerifiedNodeMapping, Is.True);
        Assert.That(metadata.NodeIndices.Count, Is.EqualTo(2));
        Assert.That(metadata.NodeIndices["A"], Is.EqualTo(0));
        Assert.That(metadata.NodeIndices["B"], Is.EqualTo(1));
        Assert.That(metadata.RawJson, Is.EqualTo(json));
        Assert.That(JToken.DeepEquals(metadata.Document, doc), Is.True);
        Assert.That(JToken.DeepEquals(metadata.GetObject("B"), doc["objects"][1]), Is.True);
        Assert.That(metadata.GetObject("B")["glbNodeIndex"], Is.Null);
        var copy = metadata.Document;
        copy["glbMapping"]["objects"][0]["glbNodeIndex"] = 99;
        var objCopy = metadata.GetObject("B");
        objCopy["future"] = null;
        Assert.That(metadata.NodeIndices["B"], Is.EqualTo(1));
        Assert.That(JToken.DeepEquals(metadata.Document, doc), Is.True);
        Assert.That(metadata.GetObject("B")["future"].Type, Is.EqualTo(JTokenType.Object));
    }

    [TestCase("1")]
    [TestCase("1.0")]
    [TestCase("2.1")]
    public void InlineMappingRemainsCompatible(string version)
    {
        var doc = Valid();
        doc["schemaVersion"] = version;
        Assert.That(new CadMetadata(doc.ToString()).NodeIndices["B"], Is.EqualTo(1));
        if (version == "1")
        {
            doc["schemaVersion"] = 1;
            Assert.That(new CadMetadata(doc.ToString()).NodeIndices["B"], Is.EqualTo(1));
        }
        if (version == "2.1")
        {
            doc["mappingStatus"] = "assigned_by_exporter";
            Assert.That(new CadMetadata(doc.ToString()).NodeIndices["B"], Is.EqualTo(1));
            doc["glbMapping"] = JValue.CreateNull();
            Assert.That(new CadMetadata(doc.ToString()).NodeIndices["B"], Is.EqualTo(1));
        }
    }

    [Test]
    public void MatchingInlineAndNestedIndicesAreAccepted()
    {
        var doc = Exported();
        doc["objects"][0]["glbNodeIndex"] = 0;
        doc["objects"][1]["glbNodeIndex"] = 1;
        ((JObject)doc["glbMapping"]).Remove("status"); // Optional container status.
        Assert.That(new CadMetadata(doc.ToString()).NodeIndices.Count, Is.EqualTo(2));
    }

    [TestCase("duplicate-engineering-id")]
    [TestCase("duplicate-mapping-id")]
    [TestCase("unknown-id")]
    [TestCase("id-case")]
    [TestCase("missing-id")]
    [TestCase("blank-id")]
    [TestCase("missing-root")]
    [TestCase("missing-child")]
    [TestCase("partial-with-inline")]
    [TestCase("duplicate-node")]
    [TestCase("missing-index")]
    [TestCase("null-index")]
    [TestCase("negative-index")]
    [TestCase("fractional-index")]
    [TestCase("string-index")]
    [TestCase("overflow-index")]
    [TestCase("boolean-index")]
    [TestCase("inline-conflict")]
    [TestCase("inline-malformed")]
    [TestCase("inline-null")]
    [TestCase("container-array")]
    [TestCase("container-string")]
    [TestCase("rows-missing")]
    [TestCase("rows-null")]
    [TestCase("rows-object")]
    [TestCase("row-null")]
    [TestCase("row-array")]
    [TestCase("status-missing")]
    [TestCase("status-ambiguous")]
    [TestCase("status-unmatched")]
    [TestCase("status-case")]
    [TestCase("candidate-only")]
    [TestCase("container-status")]
    [TestCase("container-status-null")]
    [TestCase("top-status-unknown")]
    [TestCase("top-status-missing")]
    [TestCase("top-status-malformed")]
    public void RejectsInvalidExporterMappings(string defect)
    {
        var doc = Exported();
        var mapping = (JObject)doc["glbMapping"];
        var rows = (JArray)mapping["objects"];
        var row = (JObject)rows[0]; // B
        switch (defect)
        {
            case "duplicate-engineering-id": doc["objects"][1]["id"] = "A"; break;
            case "duplicate-mapping-id": rows.Add(row.DeepClone()); break;
            case "unknown-id": rows.Add(JObject.Parse(@"{'objectId':'C','status':'matched','glbNodeIndex':2}")); break;
            case "id-case": row["objectId"] = "b"; break;
            case "missing-id": row.Remove("objectId"); break;
            case "blank-id": row["objectId"] = " "; break;
            case "missing-root": rows.RemoveAt(1); break;
            case "missing-child": rows.RemoveAt(0); break;
            case "partial-with-inline": rows.RemoveAt(0); doc["objects"][1]["glbNodeIndex"] = 1; break;
            case "duplicate-node": row["glbNodeIndex"] = 0; break;
            case "missing-index": row.Remove("glbNodeIndex"); break;
            case "null-index": row["glbNodeIndex"] = JValue.CreateNull(); break;
            case "negative-index": row["glbNodeIndex"] = -1; break;
            case "fractional-index": row["glbNodeIndex"] = 1.5; break;
            case "string-index": row["glbNodeIndex"] = "1"; break;
            case "overflow-index": row["glbNodeIndex"] = (long)int.MaxValue + 1; break;
            case "boolean-index": row["glbNodeIndex"] = true; break;
            case "inline-conflict": doc["objects"][1]["glbNodeIndex"] = 2; break;
            case "inline-malformed": doc["objects"][1]["glbNodeIndex"] = "1"; break;
            case "inline-null": doc["objects"][1]["glbNodeIndex"] = JValue.CreateNull(); break;
            case "container-array": doc["glbMapping"] = new JArray(); break;
            case "container-string": doc["glbMapping"] = "invalid"; break;
            case "rows-missing": mapping.Remove("objects"); break;
            case "rows-null": mapping["objects"] = JValue.CreateNull(); break;
            case "rows-object": mapping["objects"] = new JObject(); break;
            case "row-null": rows[0] = JValue.CreateNull(); break;
            case "row-array": rows[0] = new JArray(); break;
            case "status-missing": row.Remove("status"); break;
            case "status-ambiguous": row["status"] = "ambiguous"; break;
            case "status-unmatched": row["status"] = "unmatched"; break;
            case "status-case": row["status"] = "Matched"; break;
            case "candidate-only": row.Remove("glbNodeIndex"); row["candidateNodeIndices"] = new JArray(1); break;
            case "container-status": mapping["status"] = "not_correlated_to_glb"; break;
            case "container-status-null": mapping["status"] = JValue.CreateNull(); break;
            case "top-status-unknown": doc["mappingStatus"] = "unknown"; break;
            case "top-status-missing": doc.Remove("mappingStatus"); break;
            case "top-status-malformed": doc["mappingStatus"] = new JObject(); break;
        }
        Assert.Throws<InvalidDataException>(() => new CadMetadata(doc.ToString()));
    }

    [Test]
    public void PreservesUnknownFieldsAndProtectsParsedMetadata()
    {
        var metadata = new CadMetadata(Valid().ToString());
        var copy = metadata.GetObject("B");
        Assert.That((string)copy["future"]["evidence"], Is.EqualTo("retained"));
        Assert.That(copy["mass"].Type, Is.EqualTo(JTokenType.Null));
        copy["future"] = null;
        Assert.That(metadata.GetObject("B")["future"].Type, Is.EqualTo(JTokenType.Object));
        Assert.That(metadata.NodeIndices.Count, Is.EqualTo(2));
    }

    [Test]
    public void Schema21RetainsUncorrelatedMetadataWithoutInventingNodeIds()
    {
        var doc = Valid();
        doc["schemaVersion"] = "2.1";
        doc["mappingStatus"] = "not_correlated_to_glb";
        doc["glbMapping"] = JValue.CreateNull();
        foreach (JObject obj in doc["objects"]) obj.Remove("glbNodeIndex");
        var metadata = new CadMetadata(doc.ToString());
        Assert.That(metadata.HasVerifiedNodeMapping, Is.False);
        Assert.That(metadata.NodeIndices, Is.Empty);
        Assert.That((string)metadata.GetObject("B")["future"]["evidence"], Is.EqualTo("retained"));
        doc["objects"][0]["glbNodeIndex"] = 0;
        Assert.Throws<InvalidDataException>(() => new CadMetadata(doc.ToString()));
        ((JObject)doc["objects"][0]).Remove("glbNodeIndex");
        doc["glbMapping"] = Exported()["glbMapping"].DeepClone();
        Assert.Throws<InvalidDataException>(() => new CadMetadata(doc.ToString()));
    }

    [UnityTest]
    public IEnumerator CurrentSchema21PairLoadsGeometryAndRetainsMetadata()
    {
        var folder = CadFilesPackage.SourceFolder(Application.dataPath);
        string glb = Path.Combine(folder, "model.glb");
        string json = File.ReadAllText(Path.Combine(folder, "metadata.json"));
        var source = JObject.Parse(json);
        Assert.That((string)source["schemaVersion"], Is.EqualTo("2.1"));
        Assert.That((string)source["mappingStatus"], Is.EqualTo("assigned_by_exporter"));
        var host = new GameObject("Schema21Test", typeof(CadModelLoader));
        try
        {
            var load = host.GetComponent<CadModelLoader>().LoadFilesAsync(glb, Path.Combine(folder, "metadata.json"));
            while (!load.IsCompleted) yield return null;
            if (load.IsFaulted) throw load.Exception.GetBaseException();
            var runtime = host.GetComponent<CADVisionRuntime>();
            Assert.That(runtime.RootGameObject.GetComponentsInChildren<MeshFilter>().Length, Is.GreaterThan(0));
            Assert.That(runtime.Metadata.RawJson, Is.EqualTo(json));
            Assert.That(JToken.DeepEquals(runtime.Metadata.Document, source), Is.True);
            Assert.That(runtime.Metadata.HasVerifiedNodeMapping, Is.True);
            Assert.That(runtime.GetAllObjects().Count, Is.EqualTo(3));
            foreach (JObject obj in source["objects"])
            {
                string id = (string)obj["id"];
                var instance = runtime.GetObject(id);
                Assert.That(instance.GetComponent<CadVisionObject>().Id, Is.EqualTo(id));
                Assert.That(JToken.DeepEquals(runtime.GetMetadata(id), obj), Is.True);
                if (obj["parentId"].Type != JTokenType.Null)
                    Assert.That(instance.transform.IsChildOf(runtime.GetObject((string)obj["parentId"]).transform), Is.True);
            }
        }
        finally
        {
            host.GetComponent<CADVisionRuntime>().Clear();
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    [TestCase("version")]
    [TestCase("root")]
    [TestCase("duplicate-id")]
    [TestCase("duplicate-node")]
    [TestCase("negative-node")]
    [TestCase("missing-node")]
    [TestCase("missing-parent")]
    [TestCase("cycle")]
    [TestCase("child-list")]
    public void RejectsInvalidContracts(string defect)
    {
        var doc = Valid();
        switch (defect)
        {
            case "version": doc["schemaVersion"] = "99"; break;
            case "root": doc["project"]["rootObjectId"] = "absent"; break;
            case "duplicate-id": doc["objects"][1]["id"] = "A"; break;
            case "duplicate-node": doc["objects"][1]["glbNodeIndex"] = 0; break;
            case "negative-node": doc["objects"][1]["glbNodeIndex"] = -1; break;
            case "missing-node": ((JObject)doc["objects"][1]).Remove("glbNodeIndex"); break;
            case "missing-parent": doc["objects"][1]["parentId"] = "absent"; break;
            case "cycle": doc["objects"][1]["parentId"] = "B"; break;
            case "child-list": doc["objects"][0]["childIds"] = new JArray(); break;
        }
        Assert.Throws<InvalidDataException>(() => new CadMetadata(doc.ToString()));
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{'schemaVersion':'1.0','schemaVersion':'1.0'}")]
    public void RejectsMalformedJson(string json) => Assert.Throws<InvalidDataException>(() => new CadMetadata(json));

    private static byte[] Glb(string json)
    {
        byte[] chunk = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(20 + chunk.Length));
        writer.Write((uint)chunk.Length); writer.Write(0x4E4F534Au); writer.Write(chunk);
        return stream.ToArray();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsTruncatedGlbAndExternalResourcesAndMissingNodes(bool nested)
    {
        var metadata = new CadMetadata((nested ? Exported() : Valid()).ToString());
        var good = Glb("{\"nodes\":[{},{}]}");
        Assert.DoesNotThrow(() => CadGlbPackage.Validate(good, metadata));
        Array.Resize(ref good, good.Length - 1);
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(good, metadata));
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(Glb("{\"nodes\":[{}]}"), metadata));
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(
            Glb("{\"nodes\":[{},{}],\"buffers\":[{\"uri\":\"https://example.com/buffer\"}]}"), metadata));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RegistryRejectsBadMappingAndInvalidatesOldViewsOnReplacement(bool nested)
    {
        var host = new GameObject("test runtime");
        var runtime = host.AddComponent<CADVisionRuntime>();
        var first = new GameObject("first");
        var child = new GameObject("Repeated");
        child.transform.SetParent(first.transform);
        var second = new GameObject("second");
        var secondChild = new GameObject("Repeated");
        secondChild.transform.SetParent(second.transform);
        var metadata = new CadMetadata((nested ? Exported() : Valid()).ToString());
        try
        {
            runtime.PublishImportedModel(metadata, first, new Dictionary<int, GameObject> { [0] = first, [1] = child });
            var oldView = runtime.GetAllObjects();
            Assert.That(runtime.GetObject("B").GetComponent<CadVisionObject>().Id, Is.EqualTo("B"));
            Assert.Throws<InvalidDataException>(() => runtime.PublishImportedModel(metadata, second,
                new Dictionary<int, GameObject> { [0] = second, [1] = second }));
            Assert.That(runtime.GetObject("B"), Is.SameAs(child));
            secondChild.transform.SetParent(null);
            Assert.Throws<InvalidDataException>(() => runtime.PublishImportedModel(metadata, second,
                new Dictionary<int, GameObject> { [0] = second, [1] = secondChild }));
            secondChild.transform.SetParent(second.transform);
            Assert.Throws<InvalidDataException>(() => runtime.PublishImportedModel(metadata, second,
                new Dictionary<int, GameObject> { [0] = secondChild, [1] = second }));
            runtime.PublishImportedModel(metadata, second, new Dictionary<int, GameObject> { [0] = second, [1] = secondChild });
            Assert.That(oldView.Count, Is.Zero);
            Assert.That(first == null, Is.True);
            runtime.Clear();
            Assert.That(runtime.Metadata, Is.Null);
            Assert.Throws<KeyNotFoundException>(() => runtime.GetObject("B"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            if (first != null) UnityEngine.Object.DestroyImmediate(first);
            if (second != null) UnityEngine.Object.DestroyImmediate(second);
        }
    }

    [UnityTest]
    public IEnumerator FredImportsReloadsAndRetainsCurrentModelOnFailure()
    {
        string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        string glb = Path.Combine(repo, "FRED_P1_Assembly.glb");
        string json = Path.Combine(repo, "FRED_P1_Assembly_metadata_sample.json");
        if (!File.Exists(glb) || !File.Exists(json)) Assert.Ignore("Generate the local FRED fixture first; see docs/task-2-ingestion.md.");
        var host = new GameObject("FRED import test");
        var loader = host.AddComponent<CadModelLoader>();
        var runtime = host.GetComponent<CADVisionRuntime>();
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var loading = loader.LoadFilesAsync(glb, json);
                while (!loading.IsCompleted) yield return null;
                if (loading.IsFaulted) throw loading.Exception;
                Assert.That(runtime.GetAllObjects().Count, Is.EqualTo(261));
                Assert.That(runtime.GetObject("NODE_0001").name, Is.EqualTo("FRED_P1_Assembly"));
                Assert.That(runtime.RootGameObject.GetComponentsInChildren<BoxCollider>().Length, Is.GreaterThan(0));
            }
            var loadedRoot = runtime.RootGameObject;
            var bad = loader.LoadPackageAsync(new byte[1], File.ReadAllText(json));
            while (!bad.IsCompleted) yield return null;
            Assert.That(bad.IsFaulted, Is.True);
            Assert.That(runtime.RootGameObject, Is.SameAs(loadedRoot));
            runtime.Clear();
            Assert.That(runtime.GetAllObjects().Count, Is.Zero);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
}
