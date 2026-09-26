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

    [Test]
    public void RejectsTruncatedGlbAndExternalResourcesAndMissingNodes()
    {
        var metadata = new CadMetadata(Valid().ToString());
        var good = Glb("{\"nodes\":[{},{}]}");
        Assert.DoesNotThrow(() => CadGlbPackage.Validate(good, metadata));
        Array.Resize(ref good, good.Length - 1);
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(good, metadata));
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(Glb("{\"nodes\":[{}]}"), metadata));
        Assert.Throws<InvalidDataException>(() => CadGlbPackage.Validate(
            Glb("{\"nodes\":[{},{}],\"buffers\":[{\"uri\":\"https://example.com/buffer\"}]}"), metadata));
    }

    [Test]
    public void RegistryRejectsBadMappingAndInvalidatesOldViewsOnReplacement()
    {
        var host = new GameObject("test runtime");
        var runtime = host.AddComponent<CADVisionRuntime>();
        var first = new GameObject("first");
        var child = new GameObject("Repeated");
        child.transform.SetParent(first.transform);
        var second = new GameObject("second");
        var secondChild = new GameObject("Repeated");
        secondChild.transform.SetParent(second.transform);
        var metadata = new CadMetadata(Valid().ToString());
        try
        {
            runtime.PublishImportedModel(metadata, first, new Dictionary<int, GameObject> { [0] = first, [1] = child });
            var oldView = runtime.GetAllObjects();
            Assert.That(runtime.GetObject("B").GetComponent<CadVisionObject>().Id, Is.EqualTo("B"));
            Assert.Throws<InvalidDataException>(() => runtime.PublishImportedModel(metadata, second,
                new Dictionary<int, GameObject> { [0] = second, [1] = second }));
            Assert.That(runtime.GetObject("B"), Is.SameAs(child));
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
