using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// CADDisplayModeController: Shaded / Edges / Wireframe rendering state, material restore,
/// overlay lifetime (visibility, detach, transforms, model replacement), edge extraction and
/// caching, and coexistence with selection and the selection outline.
/// Model (registered like a Task 2 import): Root / A / { P1, P2 (same mesh as P1),
/// P3 (two material slots), P4 (MeshRenderer without a mesh) }.
/// </summary>
public class CADDisplayModeTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    private readonly List<Object> created = new();
    private CADVisionManipulationService svc;
    private CADUISettings settings;
    private CADSelectionOutline outline;
    private CADDisplayModeController display;
    private Transform root;
    private Dictionary<string, Transform> t;
    private Material red, green, blue;

    [SetUp]
    public void SetUp()
    {
        red = Track(new Material(Shader.Find("Standard")) { name = "Red" });
        green = Track(new Material(Shader.Find("Standard")) { name = "Green" });
        blue = Track(new Material(Shader.Find("Standard")) { name = "Blue" });

        var host = Track(new GameObject("ManipulationManager"));
        svc = host.AddComponent<CADVisionManipulationService>();
        settings = host.AddComponent<CADUISettings>();
        outline = host.AddComponent<CADSelectionOutline>();
        display = host.AddComponent<CADDisplayModeController>();
        Call(outline, "Awake");
        Call(outline, "OnEnable");
        Call(display, "Awake");
        Call(display, "OnEnable");

        (root, t) = BuildModel("");
        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));
        Call(display, "Start");
    }

    [TearDown]
    public void TearDown()
    {
        Call(display, "OnDestroy");
        Call(outline, "OnDestroy");
        foreach (Object o in created)
        {
            if (o != null)
                Object.DestroyImmediate(o);
        }
        created.Clear();
    }

    // 1, 2
    [Test]
    public void DefaultIsEdgesAndTheFirstModelLoadsWithEdges()
    {
        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Edges), "shaded + edges by default");
        Assert.That(display.AppliedMode, Is.EqualTo(CADDisplayMode.Edges));
        Assert.That(Overlay("P1"), Is.Not.Null);
        Assert.That(Overlay("P1").activeInHierarchy, Is.True, "the first model shows its edges");
        Assert.That(Materials("P1"), Is.EqualTo(new[] { red }), "surfaces keep their materials");
        Assert.That(Materials("P3"), Is.EqualTo(new[] { green, blue }));
        var fresh = Track(new GameObject("Fresh Session")).AddComponent<CADUISettings>();
        Assert.That(fresh.DisplayMode, Is.EqualTo(CADDisplayMode.Edges), "a new session starts in Edges");
    }

    [Test]
    public void ShadedShowsSurfacesOnlyAndReplacementKeepsTheChoice()
    {
        settings.SetDisplayMode(CADDisplayMode.Shaded);
        Assert.That(Overlay("P1").activeSelf, Is.False, "no edges in Shaded");
        Assert.That(Materials("P1"), Is.EqualTo(new[] { red }));

        (Transform newRoot, Dictionary<string, Transform> nodes) = BuildModel("2");
        Object.DestroyImmediate(root.gameObject);
        svc.ReplaceImportedModel(newRoot, nodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));
        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Shaded), "the user's choice, not the default");
        Assert.That(nodes["P1"].Find(CADDisplayModeController.OverlayName) == null ||
            !nodes["P1"].Find(CADDisplayModeController.OverlayName).gameObject.activeSelf, Is.True);
    }

    // 3
    [Test]
    public void EdgesAddsActiveOverlaysAndKeepsMaterials()
    {
        settings.SetDisplayMode(CADDisplayMode.Edges);

        foreach (string id in new[] { "P1", "P2", "P3" })
        {
            GameObject overlay = Overlay(id);
            Assert.That(overlay, Is.Not.Null, id);
            Assert.That(overlay.activeInHierarchy, Is.True, id);
            Assert.That(overlay.GetComponent<CADVisualOverlay>(), Is.Not.Null, "marked as overlay");
            Assert.That(overlay.GetComponent<Collider>(), Is.Null, "no interaction collider");
            Assert.That(overlay.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(1));
        }
        Assert.That(Materials("P1"), Is.EqualTo(new[] { red }), "surfaces keep imported materials");
        Assert.That(Materials("P3"), Is.EqualTo(new[] { green, blue }));
    }

    // 4, 5
    [Test]
    public void WireframeShowsEdgesAndPaleSurfaces()
    {
        settings.SetDisplayMode(CADDisplayMode.Wireframe);

        Assert.That(Overlay("P1").activeInHierarchy, Is.True);
        Assert.That(Overlay("P1").GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(2),
            "visible + faint hidden edges");
        foreach (string id in new[] { "P1", "P3", "P4" })
        {
            Assert.That(Materials(id).All(m => m != null && m.name == "CAD Wireframe Surface"), Is.True, id);
            Assert.That(t[id].GetComponent<MeshRenderer>().enabled, Is.True, "renderer stays enabled");
        }
        Assert.That(Materials("P3").Length, Is.EqualTo(2), "one surface material per slot");
        Assert.That(t["P1"].GetComponent<Collider>().enabled, Is.True, "colliders untouched");
    }

    [Test]
    public void WireframeFacesAreSeeThroughAndDrawOrderKeepsOutlineAndMenus()
    {
        settings.SetDisplayMode(CADDisplayMode.Wireframe);

        Material surface = Materials("P1")[0];
        Assert.That(surface.color.a, Is.InRange(0.01f, 0.5f), "faces mostly see-through");
        Assert.That(surface.renderQueue, Is.InRange(2501, 2999), "after the sky, before UI (3000)");

        int outlineQueue = outline.RenderQueue;
        Material[] edges = Overlay("P1").GetComponent<MeshRenderer>().sharedMaterials;
        Assert.That(outlineQueue, Is.GreaterThan(surface.renderQueue), "outline hull after the surfaces' depth");
        Assert.That(edges.All(e => e.renderQueue > outlineQueue && e.renderQueue < 3000), Is.True,
            "edges after surfaces and outline, before UI");

        settings.SetDisplayMode(CADDisplayMode.Edges);
        Assert.That(outline.RenderQueue, Is.EqualTo(2010), "outline back at its shader queue (Geometry+10)");
    }

    // 6, 17
    [Test]
    public void ShadedRestoresTheExactOriginalMaterials()
    {
        settings.SetDisplayMode(CADDisplayMode.Wireframe);
        settings.SetDisplayMode(CADDisplayMode.Edges);
        settings.SetDisplayMode(CADDisplayMode.Wireframe);
        settings.SetDisplayMode(CADDisplayMode.Shaded);

        Assert.That(Materials("P1"), Is.EqualTo(new[] { red }));
        Assert.That(Materials("P2"), Is.EqualTo(new[] { red }));
        Assert.That(Materials("P3"), Is.EqualTo(new[] { green, blue }), "multiple slots, same order");
        Assert.That(Materials("P4"), Is.EqualTo(new[] { red }));
        Assert.That(Overlay("P1").activeSelf, Is.False, "no edge overlay in Shaded");
        Assert.That(red.name, Is.EqualTo("Red"), "shared imported material not modified");
    }

    // 7
    [Test]
    public void RepeatedSwitchingCreatesNoDuplicatesOrMaterials()
    {
        settings.SetDisplayMode(CADDisplayMode.Edges);
        int overlays = display.OverlayCount;
        int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
        int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;

        for (int i = 0; i < 5; i++)
        {
            settings.SetDisplayMode(CADDisplayMode.Wireframe);
            settings.SetDisplayMode(CADDisplayMode.Shaded);
            settings.SetDisplayMode(CADDisplayMode.Edges);
        }

        Assert.That(display.OverlayCount, Is.EqualTo(overlays));
        Assert.That(t["P1"].GetComponentsInChildren<CADVisualOverlay>(true)
            .Count(o => o.name == CADDisplayModeController.OverlayName), Is.EqualTo(1));
        Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials), "no new materials");
        Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Length, Is.EqualTo(meshes), "no new meshes");
    }

    // 8, 9
    [Test]
    public void SelectionAndOutlineSurviveModeChanges()
    {
        svc.Select("P1");
        Call(outline, "LateUpdate");
        foreach (CADDisplayMode mode in new[] { CADDisplayMode.Edges, CADDisplayMode.Wireframe, CADDisplayMode.Shaded })
        {
            settings.SetDisplayMode(mode);
            Call(outline, "LateUpdate");
            Assert.That(svc.IsSelected("P1"), Is.True, $"{mode}: selection kept");
            Assert.That(outline.ShowOutlines, Is.True);
            Assert.That(OutlineShells("P1"), Is.GreaterThan(0), $"{mode}: outline shell present");
            Assert.That(OutlineShells("P2"), Is.EqualTo(0), "only the selection is outlined");
        }
    }

    // 10, 11
    [Test]
    public void OverlaysFollowHideIsolateAndShowAll()
    {
        settings.SetDisplayMode(CADDisplayMode.Edges);

        svc.Hide("P1");
        Assert.That(Overlay("P1").activeInHierarchy, Is.False, "no ghost edges for a hidden part");
        svc.Show("P1");
        Assert.That(Overlay("P1").activeInHierarchy, Is.True);

        svc.Isolate("P2");
        Assert.That(Overlay("P2").activeInHierarchy, Is.True);
        Assert.That(Overlay("P1").activeInHierarchy, Is.False);
        Assert.That(Overlay("P3").activeInHierarchy, Is.False);

        svc.ShowAll();
        foreach (string id in new[] { "P1", "P2", "P3" })
            Assert.That(Overlay(id).activeInHierarchy, Is.True, id);

        // A part hidden in Shaded gets its (inactive) overlay when a mode builds them.
        settings.SetDisplayMode(CADDisplayMode.Shaded);
        svc.Hide("P3");
        settings.SetDisplayMode(CADDisplayMode.Wireframe);
        Assert.That(Overlay("P3").activeInHierarchy, Is.False);
        svc.Show("P3");
        Assert.That(Overlay("P3").activeInHierarchy, Is.True);
    }

    // 12, 13
    [Test]
    public void OverlaysStayAlignedThroughDetachAndTransforms()
    {
        settings.SetDisplayMode(CADDisplayMode.Edges);

        Assert.That(svc.Detach("P1"), Is.True);
        svc.Select("P1");
        svc.SetObjectWorldPose("P1", new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 40f, 0f));
        AssertAligned("P1");

        root.position += new Vector3(0.5f, 0f, -1f);
        root.rotation = Quaternion.Euler(0f, 30f, 0f);
        root.localScale *= 2f;
        foreach (string id in new[] { "P1", "P2", "P3" })
            AssertAligned(id);
    }

    // 14, 15, 16
    [Test]
    public void ModelReplacementKeepsTheModeAndDropsOldEntries()
    {
        settings.SetDisplayMode(CADDisplayMode.Wireframe);
        Assert.That(display.ManagedRendererCount, Is.EqualTo(4));

        (Transform newRoot, Dictionary<string, Transform> nodes) = BuildModel("2");
        Object.DestroyImmediate(root.gameObject); // Task 2 destroys the old model.
        svc.ReplaceImportedModel(newRoot, nodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));

        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Wireframe), "preference kept");
        Assert.That(display.AppliedMode, Is.EqualTo(CADDisplayMode.Wireframe));
        Assert.That(display.ManagedRendererCount, Is.EqualTo(4), "old renderers dropped, new ones managed");
        GameObject overlay = nodes["P1"].Find(CADDisplayModeController.OverlayName)?.gameObject;
        Assert.That(overlay, Is.Not.Null);
        Assert.That(overlay.activeInHierarchy, Is.True, "new model shows edges immediately");
        Assert.That(nodes["P1"].GetComponent<MeshRenderer>().sharedMaterials[0].name, Is.EqualTo("CAD Wireframe Surface"));

        settings.SetDisplayMode(CADDisplayMode.Shaded);
        Assert.That(nodes["P3"].GetComponent<MeshRenderer>().sharedMaterials, Is.EqualTo(new[] { green, blue }));
    }

    // 18
    [Test]
    public void RenderersSharingAMeshShareEdgeGeometry()
    {
        settings.SetDisplayMode(CADDisplayMode.Edges);
        Mesh p1 = Overlay("P1").GetComponent<MeshFilter>().sharedMesh;
        Assert.That(Overlay("P2").GetComponent<MeshFilter>().sharedMesh, Is.SameAs(p1));
        Assert.That(CADDisplayModeController.CachedEdgeMesh(t["P1"].GetComponent<MeshFilter>().sharedMesh), Is.SameAs(p1));
    }

    // 19
    [Test]
    public void RendererWithoutMeshIsHandledSafely()
    {
        Assert.DoesNotThrow(() => settings.SetDisplayMode(CADDisplayMode.Edges));
        Assert.That(Overlay("P4"), Is.Null, "no mesh, no edges");
        Assert.DoesNotThrow(() => settings.SetDisplayMode(CADDisplayMode.Wireframe));
        Assert.DoesNotThrow(() => settings.SetDisplayMode(CADDisplayMode.Shaded));
        Assert.That(Materials("P4"), Is.EqualTo(new[] { red }));
    }

    // ---------------- Edge extraction ----------------

    [Test]
    public void CubeHasTwelveEdgesAndNoFaceDiagonals()
    {
        Mesh cube = t["P1"].GetComponent<MeshFilter>().sharedMesh;
        CADDisplayModeController.ExtractFeatureEdges(cube, 30f, out Vector3[] positions, out int[] lines);
        Assert.That(lines.Length / 2, Is.EqualTo(12), "12 box edges; the 6 face diagonals are coplanar");
        Assert.That(positions.Length, Is.EqualTo(8), "split vertices welded to 8 corners");
        for (int i = 0; i < lines.Length; i += 2)
        {
            Vector3 d = positions[lines[i + 1]] - positions[lines[i]];
            int axes = (Mathf.Abs(d.x) > 1e-4f ? 1 : 0) + (Mathf.Abs(d.y) > 1e-4f ? 1 : 0) + (Mathf.Abs(d.z) > 1e-4f ? 1 : 0);
            Assert.That(axes, Is.EqualTo(1), "axis-aligned box edge, not a diagonal");
        }
    }

    [Test]
    public void SmoothTessellationIsNotAnEdgeButCapCreasesAre()
    {
        var cylinder = Track(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
        Mesh mesh = cylinder.GetComponent<MeshFilter>().sharedMesh;
        CADDisplayModeController.ExtractFeatureEdges(mesh, 30f, out Vector3[] positions, out int[] lines);

        Assert.That(lines.Length, Is.GreaterThan(0));
        for (int i = 0; i < lines.Length; i++)
            Assert.That(Mathf.Abs(Mathf.Abs(positions[lines[i]].y) - 1f), Is.LessThan(1e-4f),
                "only the two cap rims; no vertical facet seams on the smooth side");
    }

    // ---------------- Helpers ----------------

    private void AssertAligned(string id)
    {
        Matrix4x4 source = t[id].localToWorldMatrix;
        Matrix4x4 overlay = Overlay(id).transform.localToWorldMatrix;
        for (int i = 0; i < 16; i++)
            Assert.That(overlay[i], Is.EqualTo(source[i]).Within(1e-4f), $"{id} overlay aligned");
    }

    private Material[] Materials(string id) => t[id].GetComponent<MeshRenderer>().sharedMaterials;

    private GameObject Overlay(string id) => t[id].Find(CADDisplayModeController.OverlayName)?.gameObject;

    private int OutlineShells(string id) =>
        t[id].GetComponentsInChildren<CADVisualOverlay>(true).Count(o => o.name == "__CADOutline");

    private (Transform root, Dictionary<string, Transform> t) BuildModel(string suffix)
    {
        var modelRoot = Track(new GameObject("CADVisionModelRoot" + suffix)).transform;
        var nodes = new Dictionary<string, Transform>();
        nodes["A"] = Track(new GameObject("A" + suffix)).transform;
        nodes["A"].SetParent(modelRoot, false);
        nodes["P1"] = Part("P1" + suffix, nodes["A"], new Vector3(0f, 0f, 1f), red);
        nodes["P2"] = Part("P2" + suffix, nodes["A"], new Vector3(0.5f, 0f, 1f), red);
        nodes["P3"] = Part("P3" + suffix, nodes["A"], new Vector3(1f, 0f, 1f), green, blue);

        var empty = Track(new GameObject("P4" + suffix)).transform;
        empty.SetParent(nodes["A"], false);
        empty.gameObject.AddComponent<MeshRenderer>().sharedMaterial = red; // No MeshFilter/mesh.
        nodes["P4"] = empty;
        return (modelRoot, nodes);
    }

    private Transform Part(string name, Transform parent, Vector3 position, params Material[] materials)
    {
        GameObject go = Track(GameObject.CreatePrimitive(PrimitiveType.Cube)); // Shared built-in mesh.
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localScale = Vector3.one * 0.2f;
        go.GetComponent<MeshRenderer>().sharedMaterials = materials;
        return go.transform;
    }

    private static void Call(object o, string name) => o.GetType().GetMethod(name, Any)?.Invoke(o, null);

    private T Track<T>(T obj) where T : Object
    {
        created.Add(obj);
        return obj;
    }
}
