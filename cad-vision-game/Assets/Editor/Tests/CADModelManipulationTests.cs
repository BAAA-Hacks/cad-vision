using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Intent = CADPointerStateMachine.Intent;

/// <summary>
/// Whole-model manipulation: model mode lifecycle, model-root drag/rotate/scale through the
/// service, pointer routing (model mode wins over object/group drag), preservation of
/// selection, multi-selection and detach state, resets and model replacement.
/// Model (registered like a Task 2 import, root scaled ×10 and offset so the CAD origin is
/// far from the geometry): Root / A / { P1, P2, P3, S / { S1 } }.
/// </summary>
public class CADModelManipulationTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private const float Eps = 1e-4f;

    private readonly List<Object> created = new();
    private CADVisionManipulationService svc;
    private CADPointerInteraction pointer;
    private CADContextMenu menu;
    private Transform root;
    private Dictionary<string, Transform> t;
    private Vector3 rootStartPosition;
    private Quaternion rootStartRotation;
    private Vector3 rootStartScale;

    [SetUp]
    public void SetUp()
    {
        var cam = Track(new GameObject("Eye", typeof(Camera)));
        cam.tag = "MainCamera";
        cam.transform.position = new Vector3(0f, 1.6f, 0f);

        var host = Track(new GameObject("ManipulationManager"));
        svc = host.AddComponent<CADVisionManipulationService>();
        pointer = host.AddComponent<CADPointerInteraction>();
        menu = host.AddComponent<CADContextMenu>();
        Call(pointer, "Awake");
        Call(menu, "Awake");
        pointer.ContextMenuRequested += r => typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { r });

        (root, t) = BuildModel("");
        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));
        rootStartPosition = root.localPosition;
        rootStartRotation = root.localRotation;
        rootStartScale = root.localScale;
    }

    [TearDown]
    public void TearDown()
    {
        if (menu != null && Get(menu, "panelRoot") is GameObject panel)
            Object.DestroyImmediate(panel);
        foreach (EventSystem es in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include))
        {
            if (es.name == "CAD Vision EventSystem")
                Object.DestroyImmediate(es.gameObject);
        }

        foreach (Object o in created)
        {
            if (o != null)
                Object.DestroyImmediate(o);
        }
        created.Clear();
    }

    // 1, 3
    [Test]
    public void ModelTranslationMovesAttachedPartsAndKeepsRelativeTransforms()
    {
        var locals = LocalPoses();
        Vector3 p1 = t["P1"].position, p2 = t["P2"].position, s1 = t["S1"].position;
        svc.BeginModelManipulation();

        DragModel("P1", new Vector3(0.2f, 0.1f, -0.1f));

        Assert.That(Vector3.Distance(root.position, rootStartPosition), Is.GreaterThan(1e-3f), "root moved");
        Vector3 move = t["P1"].position - p1;
        Assert.That(Vector3.Distance(t["P2"].position - p2, move), Is.LessThan(Eps), "P2 moved with P1");
        Assert.That(Vector3.Distance(t["S1"].position - s1, move), Is.LessThan(Eps), "nested S1 moved with P1");
        AssertLocalPosesUnchanged(locals);
    }

    // 2, 9
    [Test]
    public void ModelTranslationMovesDetachedPartsAndKeepsDetachState()
    {
        Assert.That(svc.Detach("P3"), Is.True);
        svc.SetObjectWorldPose("P3", t["P3"].position + new Vector3(0.5f, 0f, 0f), t["P3"].rotation);
        Transform detachedParent = t["P3"].parent;
        Vector3 inRoot = root.InverseTransformPoint(t["P3"].position);
        Vector3 p1 = t["P1"].position, p3 = t["P3"].position;
        svc.BeginModelManipulation();

        DragModel("P1", new Vector3(-0.3f, 0.2f, 0.1f));

        Assert.That(Vector3.Distance(t["P3"].position - p3, t["P1"].position - p1), Is.LessThan(Eps),
            "detached P3 moved with the whole model");
        Assert.That(Vector3.Distance(root.InverseTransformPoint(t["P3"].position), inRoot), Is.LessThan(Eps),
            "P3 kept its pose relative to the model root");
        Assert.That(svc.IsDetached("P3"), Is.True, "detach state survives");
        Assert.That(t["P3"].parent, Is.SameAs(detachedParent), "not reparented");
        Assert.That(svc.GetLogicalParentId("P3"), Is.EqualTo("A"), "logical hierarchy unchanged");
    }

    // 4
    [Test]
    public void ModelRotationFollowsThePointerAroundTheHeldPointAndPreservesHierarchy()
    {
        var locals = LocalPoses();
        float p1p2 = Vector3.Distance(t["P1"].position, t["P2"].position);
        svc.BeginModelManipulation();
        CADGrabSession session = PressModel("P2", out Pose start);
        Vector3 heldOffset = Quaternion.Inverse(start.rotation) * (session.GrabPointWorld - start.position);

        var turned = new Pose(start.position, Quaternion.Euler(0f, 30f, 0f));
        Assert.That(session.Update(turned), Is.True);

        Assert.That(Quaternion.Angle(root.rotation, rootStartRotation), Is.EqualTo(30f).Within(0.05f), "root rotated with the pointer");
        Vector3 expectedHeld = turned.position + turned.rotation * heldOffset;
        Assert.That(Vector3.Distance(session.GrabPointWorld, expectedHeld), Is.LessThan(Eps),
            "held point stays rigid to the pointer (not the root origin)");
        Assert.That(Vector3.Distance(t["P1"].position, t["P2"].position), Is.EqualTo(p1p2).Within(Eps));
        AssertLocalPosesUnchanged(locals);
    }

    // 5
    [Test]
    public void UniformScaleAroundAPointKeepsThePivotAndHierarchy()
    {
        var locals = LocalPoses();
        float p1p2 = Vector3.Distance(t["P1"].position, t["P2"].position);
        Vector3 pivotWorld = t["P2"].position;
        Vector3 pivotLocal = root.InverseTransformPoint(pivotWorld);

        float applied = svc.SetModelScaleAroundPoint(2f, pivotLocal, pivotWorld);

        Assert.That(applied, Is.EqualTo(2f).Within(1e-5f));
        Assert.That(root.localScale.x, Is.EqualTo(rootStartScale.x * 2f).Within(1e-4f), "scale is relative to the adopted scale (not 1)");
        Assert.That(svc.ModelScaleRatio, Is.EqualTo(2f).Within(1e-5f));
        Assert.That(Vector3.Distance(t["P2"].position, pivotWorld), Is.LessThan(Eps), "pivot on the geometry stays put");
        Assert.That(Vector3.Distance(t["P1"].position, t["P2"].position), Is.EqualTo(p1p2 * 2f).Within(Eps));
        AssertLocalPosesUnchanged(locals);
    }

    // 6
    [Test]
    public void ModelScaleIsClampedAndRejectsInvalidValues()
    {
        Vector3 pivotWorld = t["P1"].position;
        Vector3 pivotLocal = root.InverseTransformPoint(pivotWorld);

        float max = svc.SetModelScaleAroundPoint(1e6f, pivotLocal, pivotWorld);
        Assert.That(max, Is.LessThan(1e6f), "upper clamp");
        Assert.That(svc.ModelScaleRatio, Is.EqualTo(max).Within(1e-3f));

        float min = svc.SetModelScaleAroundPoint(1e-6f, pivotLocal, pivotWorld);
        Assert.That(min, Is.GreaterThan(1e-6f), "lower clamp");
        Assert.That(svc.ModelScaleRatio, Is.EqualTo(min).Within(1e-5f));

        Vector3 scale = root.localScale;
        svc.SetModelScaleAroundPoint(float.NaN, pivotLocal, pivotWorld);
        svc.SetModelScaleAroundPoint(0f, pivotLocal, pivotWorld);
        svc.SetModelScaleAroundPoint(-1f, pivotLocal, pivotWorld);
        Assert.That(root.localScale, Is.EqualTo(scale), "NaN / zero / negative ignored");
        Assert.That(Vector3.Distance(t["P1"].position, pivotWorld), Is.LessThan(Eps), "pivot held through clamps");
    }

    // Phase 2: one-hand → two-hand → one-hand never snaps.
    [Test]
    public void ScalingDuringAModelDragDoesNotSnapWhenTheDragResumes()
    {
        svc.BeginModelManipulation();
        CADGrabSession session = PressModel("P1", out Pose start);
        Vector3 pivotWorld = session.GrabPointWorld;

        // Two-hand scaling around the held point while the one-hand drag is held.
        svc.SetModelScaleAroundPoint(1.5f, root.InverseTransformPoint(pivotWorld), pivotWorld);
        session.Rebase(start);
        Vector3 scaledPosition = root.position;
        Vector3 scaledScale = root.localScale;

        Assert.That(session.Update(start), Is.True);
        Assert.That(Vector3.Distance(root.position, scaledPosition), Is.LessThan(Eps), "no snap back after scaling");
        Assert.That(root.localScale, Is.EqualTo(scaledScale));
        Assert.That(Vector3.Distance(session.GrabPointWorld, pivotWorld), Is.LessThan(Eps), "held point unchanged");
    }

    // 7, outlines
    [Test]
    public void SelectionAndOutlinesSurviveModelManipulation()
    {
        var outline = svc.gameObject.AddComponent<CADSelectionOutline>();
        Call(outline, "Awake");
        svc.EnterScope("A");
        Click("P1");
        Call(outline, "LateUpdate");
        Assert.That(ShellCount("P1"), Is.EqualTo(1));

        OpenObjectMenu("P1");
        Assert.That(ActiveLabels(), Has.No.Member("Manipulate Model"), "global action lives in the Main Menu");
        svc.BeginModelManipulation(); // Main Menu → Manipulate model.
        Assert.That(svc.IsModelManipulationActive, Is.True);
        DragModel("P2", new Vector3(0.1f, 0.2f, 0f));
        Call(outline, "LateUpdate");

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1" }));
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "scope unchanged");
        Assert.That(ShellCount("P1"), Is.EqualTo(1), "outline shell still on (and moving with) P1");
    }

    // 8
    [Test]
    public void MultiSelectionSurvivesModelManipulation()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");

        svc.BeginModelManipulation(); // Main Menu → Manipulate model.
        Assert.That(svc.IsModelManipulationActive, Is.True);
        Assert.That(svc.IsMultiSelectActive, Is.False, "picking ends; the set is kept");

        DragModel("P3", new Vector3(0f, 0.1f, 0.2f));
        svc.SetModelScaleAroundPoint(0.5f, Vector3.zero, root.position);

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    // 10
    [Test]
    public void ResetModelTransformRestoresTheAdoptedReviewPose()
    {
        svc.BeginModelManipulation();
        DragModel("P1", new Vector3(0.3f, -0.1f, 0.2f), Quaternion.Euler(10f, 40f, 0f));
        svc.SetModelScaleAroundPoint(3f, Vector3.zero, root.position);

        svc.ResetModelTransform();

        Assert.That(Vector3.Distance(root.localPosition, rootStartPosition), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(root.localRotation, rootStartRotation), Is.LessThan(1e-3f));
        Assert.That(Vector3.Distance(root.localScale, rootStartScale), Is.LessThan(Eps), "scale ×10, not 1");
    }

    // 11
    [Test]
    public void ResetModelRestoresObjectsDetachStateAndRoot()
    {
        var locals = LocalPoses();
        Transform p3Parent = t["P3"].parent;
        svc.Detach("P3");
        svc.MoveObject("P1", new Vector3(0.05f, 0f, 0f));
        svc.BeginModelManipulation();
        DragModel("P2", new Vector3(0.2f, 0.2f, 0.2f), Quaternion.Euler(0f, -25f, 0f));
        svc.SetModelScaleAroundPoint(0.4f, Vector3.zero, root.position);

        MenuButton("Reset Model").onClick.Invoke();

        Assert.That(svc.IsDetached("P3"), Is.False);
        Assert.That(t["P3"].parent, Is.SameAs(p3Parent), "reattached");
        AssertLocalPosesUnchanged(locals);
        Assert.That(Vector3.Distance(root.localPosition, rootStartPosition), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(root.localRotation, rootStartRotation), Is.LessThan(1e-3f));
        Assert.That(Vector3.Distance(root.localScale, rootStartScale), Is.LessThan(Eps));
        Assert.That(svc.IsModelManipulationActive, Is.True, "Reset Model keeps model mode");
        Assert.That(menu.IsOpen, Is.True, "model menu stays open");
    }

    // 12
    [Test]
    public void ModelModeSuppressesObjectAndGroupDrag()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        svc.EndMultiSelect();
        var locals = LocalPoses();
        svc.BeginModelManipulation();

        bool heldModel = DragModel("P1", new Vector3(0.1f, 0f, 0.1f));

        Assert.That(heldModel, Is.True, "the drag held the model, not the group");
        AssertLocalPosesUnchanged(locals);
        Assert.That(Vector3.Distance(root.localPosition, rootStartPosition), Is.GreaterThan(1e-3f));

        // Clicks change nothing and open no object menu.
        Click("P3");
        Click("P1");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
        Tick();
        Assert.That(ActiveLabels(), Is.EquivalentTo(new[] { "Done", "Reset Scale", "Reset Model" }), "only the model menu");
    }

    // 13
    [Test]
    public void DoneRestoresOrdinaryObjectDrag()
    {
        svc.EnterScope("A");
        Click("P1");
        svc.BeginModelManipulation();
        Tick();
        Assert.That(menu.IsOpen, Is.True);

        MenuButton("Done").onClick.Invoke();
        Tick();
        Assert.That(svc.IsModelManipulationActive, Is.False);
        Assert.That(menu.IsOpen, Is.False);

        Vector3 rootPosition = root.position, p1Local = t["P1"].localPosition;
        Assert.That(DragModel("P1", new Vector3(0.1f, 0f, 0f)), Is.False, "object drag, not model");
        Assert.That(Vector3.Distance(t["P1"].localPosition, p1Local), Is.GreaterThan(1e-4f), "P1 itself moved");
        Assert.That(root.position, Is.EqualTo(rootPosition), "model root untouched");
    }

    // 14, 15, 16
    [Test]
    public void RuntimeReplacementExitsModelModeAndLeavesNoStaleState()
    {
        svc.BeginModelManipulation();
        Tick();
        Assert.That(menu.IsOpen, Is.True);
        CADGrabSession session = PressModel("P1", out Pose start);
        Assert.That(pointer.IsManipulatingModel, Is.True);

        var (root2, t2) = BuildModel("_B");
        svc.ReplaceImportedModel(root2, t2.ToDictionary(kv => kv.Key + "_B", kv => kv.Value.gameObject));
        Object.DestroyImmediate(root.gameObject);

        Assert.That(svc.IsModelManipulationActive, Is.False, "14: model mode ended");
        Assert.That(svc.ModelRoot, Is.SameAs(root2));
        Assert.That(session.Update(start), Is.False, "16: the held (destroyed) root is not driven");
        Call(pointer, "EndManipulation", "test");
        Assert.That(pointer.IsManipulating, Is.False);
        Assert.That(pointer.TryGetModelGrabPoint(out _), Is.False);
        Tick();
        Assert.That(menu.IsOpen, Is.False, "15: model menu closed");
        Assert.That(Get(menu, "modelMode"), Is.False);

        // The new model enters model mode normally.
        Assert.That(svc.BeginModelManipulation(), Is.True);
        Vector3 start2 = root2.position;
        t = t2.ToDictionary(kv => kv.Key + "_B", kv => kv.Value);
        DragModel("P1_B", new Vector3(0.1f, 0.1f, 0f));
        Assert.That(Vector3.Distance(root2.position, start2), Is.GreaterThan(1e-3f));
    }

    [Test]
    public void ModelModeAndMultiSelectAreExclusive()
    {
        svc.BeginModelManipulation();
        svc.BeginMultiSelect();
        Assert.That(svc.IsModelManipulationActive, Is.False);
        Assert.That(svc.IsMultiSelectActive, Is.True);

        svc.BeginModelManipulation();
        Assert.That(svc.IsMultiSelectActive, Is.False);
        Assert.That(svc.IsModelManipulationActive, Is.True);
    }

    // ---- helpers ----

    private void Click(string hitId)
    {
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.Cad, hitId, t[hitId].position, Pose.identity, 0f);
        SimulatePress(hitId);
        Handle(machine.Up(0.1f));
    }

    private void OpenObjectMenu(string hitId)
    {
        Click(hitId);
        Assert.That(menu.IsOpen, Is.True, "object menu open");
    }

    private void SimulatePress(string hitId)
    {
        string resolved = svc.ResolveHitTarget(hitId);
        typeof(CADPointerInteraction).GetField("pressedResolvedId", Any).SetValue(pointer, resolved);
        typeof(CADPointerInteraction).GetField("pressedSelectedTarget", Any)
            .SetValue(pointer, resolved != null && !svc.IsMultiSelectActive && svc.IsSelected(resolved));
    }

    // Press on hitId's geometry and cross the drag threshold; the session is left active.
    private CADGrabSession PressModel(string hitId, out Pose start)
    {
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.Cad, hitId, t[hitId].position, Pose.identity, 0f);
        SimulatePress(hitId);
        start = new Pose(new Vector3(0.05f, 0f, 0f), Quaternion.identity);
        Assert.That(machine.Move(start, 0.3f), Is.EqualTo(Intent.BeginDrag));
        Handle(Intent.BeginDrag, start);
        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.IsActive, Is.True, "drag started");
        return session;
    }

    // Full press → drag by delta (and optional rotation) → release through the coordinator.
    // Returns whether the drag held the model root (vs objects).
    private bool DragModel(string hitId, Vector3 delta, Quaternion? rotation = null)
    {
        CADGrabSession session = PressModel(hitId, out Pose start);
        bool isModel = session.IsModel;
        Assert.That(session.Update(new Pose(start.position + delta, rotation ?? Quaternion.identity)), Is.True);
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        Handle(machine.Up(0.5f));
        Assert.That(session.IsActive, Is.False, "released");
        return isModel;
    }

    private Dictionary<string, (Vector3 p, Quaternion r, Vector3 s)> LocalPoses() =>
        t.ToDictionary(kv => kv.Key, kv => (kv.Value.localPosition, kv.Value.localRotation, kv.Value.localScale));

    private void AssertLocalPosesUnchanged(Dictionary<string, (Vector3 p, Quaternion r, Vector3 s)> before)
    {
        foreach (var entry in before)
        {
            Transform node = t[entry.Key];
            Assert.That(Vector3.Distance(node.localPosition, entry.Value.p), Is.LessThan(1e-5f), $"{entry.Key} local position");
            Assert.That(Quaternion.Angle(node.localRotation, entry.Value.r), Is.LessThan(1e-3f), $"{entry.Key} local rotation");
            Assert.That(Vector3.Distance(node.localScale, entry.Value.s), Is.LessThan(1e-6f), $"{entry.Key} local scale");
        }
    }

    private void Handle(Intent intent, Pose pose = default) =>
        typeof(CADPointerInteraction).GetMethod("Handle", Any).Invoke(pointer, new object[] { intent, null, pose });

    private void Tick() => Call(menu, "LateUpdate");

    private Button MenuButton(string label)
    {
        Tick();
        var panel = (GameObject)Get(menu, "panelRoot");
        return panel.GetComponentsInChildren<Button>(true)
            .First(b => b.gameObject.activeSelf && b.GetComponentInChildren<Text>().text == label);
    }

    private string[] ActiveLabels()
    {
        var panel = (GameObject)Get(menu, "panelRoot");
        return panel.GetComponentsInChildren<Button>(true)
            .Where(b => b.gameObject.activeSelf)
            .Select(b => b.GetComponentInChildren<Text>().text).ToArray();
    }

    private int ShellCount(string id) =>
        t[id].GetComponentsInChildren<CADVisualOverlay>(true).Length;

    private string[] Selected() => svc.GetSelectedIds().ToArray();

    private (Transform root, Dictionary<string, Transform> t) BuildModel(string suffix)
    {
        var modelRoot = Track(new GameObject("CADVisionModelRoot" + suffix)).transform;
        modelRoot.localScale = Vector3.one * 10f;
        modelRoot.position = new Vector3(4f, -2f, 7f);
        var nodes = new Dictionary<string, Transform>();
        nodes["A"] = Node("A", modelRoot, new Vector3(-0.3f, 0.35f, -0.5f), false);
        nodes["P1"] = Node("P1", nodes["A"], new Vector3(0.02f, 0f, 0f), true);
        nodes["P2"] = Node("P2", nodes["A"], new Vector3(0.04f, 0f, 0f), true);
        nodes["P3"] = Node("P3", nodes["A"], new Vector3(0.06f, 0f, 0f), true);
        nodes["S"] = Node("S", nodes["A"], new Vector3(-0.02f, 0.01f, 0f), false);
        nodes["S1"] = Node("S1", nodes["S"], new Vector3(0f, 0.02f, 0f), true);
        return (modelRoot, nodes);
    }

    private Transform Node(string name, Transform parent, Vector3 local, bool mesh)
    {
        GameObject go = mesh ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject();
        Track(go);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = local;
        if (mesh)
            go.transform.localScale = Vector3.one * 0.01f;
        return go.transform;
    }

    private static void Call(object o, string name, params object[] args) =>
        o.GetType().GetMethod(name, Any).Invoke(o, args.Length == 0 ? null : args);
    private static object Get(object o, string name) => o.GetType().GetField(name, Any).GetValue(o);

    private T Track<T>(T obj) where T : Object
    {
        created.Add(obj);
        return obj;
    }
}
