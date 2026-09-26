using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Intent = CADPointerStateMachine.Intent;

/// <summary>
/// Multi-selection: service-owned selection set, pointer toggling, the selection menu,
/// hierarchy normalization, isolate/reset of the set, outlines and model replacement.
/// Model (registered like a Task 2 import): Root / A / { P1, P2, P3, S / { S1 } }.
/// </summary>
public class CADMultiSelectTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    private readonly List<Object> created = new();
    private CADVisionManipulationService svc;
    private CADPointerInteraction pointer;
    private CADContextMenu menu;
    private Transform root;
    private Dictionary<string, Transform> t;
    private int menuRequests;

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
        pointer.ContextMenuRequested += _ => menuRequests++;
        pointer.ContextMenuRequested += r => typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { r });

        (root, t) = BuildModel("");
        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));
        menuRequests = 0;
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

    // 1
    [Test]
    public void EnterMultiSelectWithOneObjectSelected()
    {
        svc.EnterScope("A");
        Click("P1");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1" }));
        Click("P1"); // Second click opens the object menu.
        Assert.That(menu.IsOpen, Is.True);

        MenuButton("Multi-Select").onClick.Invoke();

        Assert.That(svc.IsMultiSelectActive, Is.True);
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1" }), "existing selection kept as first member");
    }

    // 2, 3
    [Test]
    public void ClickSiblingAddsAndClickingSelectedAgainRemoves()
    {
        svc.EnterScope("A");
        Click("P1");
        svc.BeginMultiSelect();

        Click("P2");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }), "2: sibling added");

        Click("P1");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P2" }), "3: selected object removed");
        Assert.That(menuRequests, Is.Zero, "clicks in multi-select never open the object menu");
    }

    // 4
    [Test]
    public void DetachedPartCanBeAddedAtModelScope()
    {
        svc.Detach("P3");
        Assert.That(svc.CurrentScopeId, Is.Null);
        svc.BeginMultiSelect();

        Click("P3");

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P3" }));
    }

    // 5
    [Test]
    public void NonDetachedPartStillResolvesThroughScope()
    {
        svc.BeginMultiSelect();

        Click("P1");

        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }), "model scope: part resolves to its assembly");
        Assert.That(svc.CurrentScopeId, Is.Null, "multi-select never moves the scope");
    }

    // 6
    [Test]
    public void ClearSelectionClearsAllAndExitsMultiSelect()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Tick();

        MenuButton("Clear Selection").onClick.Invoke();
        Tick();

        Assert.That(Selected(), Is.Empty);
        Assert.That(svc.IsMultiSelectActive, Is.False);
        Assert.That(menu.IsOpen, Is.False);
    }

    // 7
    [Test]
    public void DoneExitsModeAndKeepsTheSelection()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Tick();

        MenuButton("Done").onClick.Invoke();
        Tick();

        Assert.That(svc.IsMultiSelectActive, Is.False);
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
        Assert.That(menu.IsOpen, Is.False);

        // Back to normal: clicking an unselected object replaces the set.
        Click("P3");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P3" }));
    }

    // 8
    [Test]
    public void ResetSelectedRestoresTwoSiblings()
    {
        Vector3 p1 = t["P1"].localPosition, p2 = t["P2"].localPosition;
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        svc.MoveObject("P1", Vector3.one);
        svc.MoveObject("P2", Vector3.one);
        Tick();

        MenuButton("Reset Selected").onClick.Invoke();

        Assert.That(Vector3.Distance(t["P1"].localPosition, p1), Is.LessThan(1e-6f));
        Assert.That(Vector3.Distance(t["P2"].localPosition, p2), Is.LessThan(1e-6f));
        Assert.That(svc.IsMultiSelectActive, Is.True, "reset keeps multi-select going");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    // 9
    [Test]
    public void AssemblyPlusDescendantIsNormalized()
    {
        Vector3 p1 = t["P1"].localPosition; // Original local pose under A.
        svc.AddToSelection("A");
        svc.AddToSelection("P1");
        svc.AddToSelection("S1");

        Assert.That(svc.GetSelectedLogicalRoots(), Is.EquivalentTo(new[] { "A" }), "reset roots: A covers P1 and S1");
        Assert.That(svc.GetSelectedTransformRoots(), Is.EquivalentTo(new[] { "A" }), "move roots: children follow A");

        // Detached P1 no longer moves with A (transform root) but A's reset still covers it.
        svc.Detach("P1");
        Assert.That(svc.GetSelectedTransformRoots(), Is.EquivalentTo(new[] { "A", "P1" }));
        Assert.That(svc.GetSelectedLogicalRoots(), Is.EquivalentTo(new[] { "A" }));

        svc.SetObjectWorldPose("P1", t["P1"].position + Vector3.up, t["P1"].rotation);
        svc.ResetSelected();

        Assert.That(svc.IsDetached("P1"), Is.False, "A's reset reattached P1");
        Assert.That(t["P1"].parent, Is.SameAs(t["A"]));
        Assert.That(Vector3.Distance(t["P1"].localPosition, p1), Is.LessThan(1e-6f));
    }

    // 10, 11, 12
    [Test]
    public void IsolateSelectedKeepsSelectionAndAncestorsVisibleAndShowAllRestores()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        svc.AddToSelection("S1"); // Nested one level deeper, under S.
        Tick();

        MenuButton("Isolate Selected").onClick.Invoke();

        Assert.That(t["P1"].gameObject.activeInHierarchy, Is.True, "10: selected P1 visible");
        Assert.That(t["S1"].gameObject.activeInHierarchy, Is.True, "10: selected S1 visible");
        Assert.That(t["A"].gameObject.activeSelf && t["S"].gameObject.activeSelf, Is.True, "11: ancestors kept active");
        Assert.That(t["P2"].gameObject.activeInHierarchy, Is.False, "unselected sibling hidden");
        Assert.That(t["P3"].gameObject.activeInHierarchy, Is.False);

        MenuButton("Show All").onClick.Invoke();

        Assert.That(t.Values.All(x => x.gameObject.activeInHierarchy), Is.True, "12: everything visible again");
    }

    // 13
    [Test]
    public void MultipleSelectedObjectsAreAllOutlined()
    {
        var outline = svc.gameObject.AddComponent<CADSelectionOutline>();
        Call(outline, "Awake");
        if (!outline.enabled)
            Assert.Ignore("Outline shader unsupported in this editor (e.g. -nographics).");
        Call(outline, "OnEnable");

        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Call(outline, "LateUpdate");
        Assert.That(ShellCount("P1"), Is.EqualTo(1));
        Assert.That(ShellCount("P2"), Is.EqualTo(1));
        Assert.That(svc.UseSelectionTint, Is.False, "materials untouched: tint off while outlining");

        Click("P1"); // Remove P1.
        Call(outline, "LateUpdate");
        Assert.That(ShellCount("P1"), Is.Zero, "deselected object loses its outline");
        Assert.That(ShellCount("P2"), Is.EqualTo(1));
    }

    // 14
    [Test]
    public void ModelReplacementDuringMultiSelectClearsModeAndState()
    {
        var outline = svc.gameObject.AddComponent<CADSelectionOutline>();
        Call(outline, "Awake");
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Tick();
        if (outline.enabled) Call(outline, "LateUpdate");
        Assert.That(menu.IsOpen, Is.True);

        var (root2, t2) = BuildModel("_B");
        svc.ReplaceImportedModel(root2, t2.ToDictionary(kv => kv.Key + "_B", kv => kv.Value.gameObject));
        Object.DestroyImmediate(root.gameObject);
        Tick();
        if (outline.enabled) Call(outline, "LateUpdate");

        Assert.That(svc.IsMultiSelectActive, Is.False);
        Assert.That(Selected(), Is.Empty);
        Assert.That(menu.IsOpen, Is.False);
        Assert.That(svc.CurrentScopeId, Is.Null);

        // Model B works normally.
        Click("P1_B");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A_B" }));
    }

    // 15
    [Test]
    public void UiClicksDoNotDeselect()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Tick();

        // Pointer press/release on the menu (classified as UI).
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.Ui, null, Vector3.zero, Pose.identity, 0f);
        Handle(machine.Up(0.1f));
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));

        // Empty-space click during multi-select keeps the set too.
        machine.Down(CADPointerTargetKind.None, null, null, Pose.identity, 0f);
        Handle(machine.Up(0.1f));
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));

        MenuButton("Show All").onClick.Invoke();
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    // 16
    [Test]
    public void SingleSelectBehaviorUnchangedWhenMultiSelectIsOff()
    {
        Click("P1");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }), "click selects the resolved assembly");

        Click("P2");
        Assert.That(menuRequests, Is.EqualTo(1), "click on the selected object opens its menu");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }));

        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.None, null, null, Pose.identity, 0f);
        Handle(machine.Up(0.1f));
        Assert.That(Selected(), Is.Empty, "empty click clears");
    }

    [Test]
    public void DragWhilePickingMovesTheWholeSelectionAndKeepsTheSet()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        Vector3 p1 = t["P1"].position, p2 = t["P2"].position, p3 = t["P3"].position;

        Drag("P2", new Vector3(0.2f, 0.1f, 0f));

        AssertMovedTogether("P1", p1, "P2", p2);
        Assert.That(Vector3.Distance(t["P3"].position, p3), Is.LessThan(1e-6f), "unselected sibling did not move");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }), "selection not collapsed");
        Assert.That(svc.IsMultiSelectActive, Is.True);
    }

    [Test]
    public void AfterDoneDraggingASelectedObjectMovesTheWholeSelection()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        svc.EndMultiSelect(); // Done.
        Vector3 p1 = t["P1"].position, p2 = t["P2"].position;

        Drag("P1", new Vector3(-0.15f, 0.05f, 0.1f));

        AssertMovedTogether("P1", p1, "P2", p2);
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }), "Done selection survives a drag");
    }

    [Test]
    public void AfterDoneClickingASelectedObjectOpensSelectionMenuWithoutCollapsing()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Click("P2");
        svc.EndMultiSelect();

        Click("P1");

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
        Assert.That(menuRequests, Is.EqualTo(1));
        Assert.That(menu.IsOpen, Is.True);
        Assert.That(MenuButton("Edit Selection"), Is.Not.Null, "selection menu, not the object menu");

        MenuButton("Edit Selection").onClick.Invoke();
        Assert.That(svc.IsMultiSelectActive, Is.True, "Edit Selection resumes picking");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    [Test]
    public void DraggingAnUnselectedObjectWhilePickingAddsItAndMovesTheGroup()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Click("P1");
        Vector3 p1 = t["P1"].position, p3 = t["P3"].position;

        Drag("P3", new Vector3(0.1f, 0f, 0.2f));

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P3" }));
        AssertMovedTogether("P1", p1, "P3", p3);
    }

    [Test]
    public void GroupDragMovesADescendantOnlyOnceWithItsSelectedAssembly()
    {
        svc.AddToSelection("A");
        svc.AddToSelection("P1");
        Vector3 localP1 = t["P1"].localPosition;
        Quaternion localRot = t["P1"].localRotation;
        Vector3 a = t["A"].position;

        Drag("S1", new Vector3(0.3f, 0.1f, 0f)); // S1 resolves to A (selected) at model scope.

        Assert.That(Vector3.Distance(t["A"].position, a), Is.GreaterThan(1e-3f), "assembly moved");
        Assert.That(Vector3.Distance(t["P1"].localPosition, localP1), Is.LessThan(1e-6f),
            "P1 kept its pose relative to A (moved once, via A)");
        Assert.That(Quaternion.Angle(t["P1"].localRotation, localRot), Is.LessThan(1e-3f));
    }

    // ---- helpers ----

    // A full pointer click on a CAD collider owned by hitId, through the real coordinator logic.
    private void Click(string hitId)
    {
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.Cad, hitId, null, Pose.identity, 0f);

        SimulatePress(hitId);
        Handle(machine.Up(0.1f));
    }

    // Same rule as CADPointerInteraction.Press (which needs a live ISDK ray).
    private void SimulatePress(string hitId)
    {
        string resolved = svc.ResolveHitTarget(hitId);
        typeof(CADPointerInteraction).GetField("pressedResolvedId", Any).SetValue(pointer, resolved);
        typeof(CADPointerInteraction).GetField("pressedSelectedTarget", Any)
            .SetValue(pointer, resolved != null && !svc.IsMultiSelectActive && svc.IsSelected(resolved));
    }

    // Press on hitId's geometry, hold, move the pointer by delta, release.
    private void Drag(string hitId, Vector3 delta)
    {
        var machine = (CADPointerStateMachine)Get(pointer, "machine");
        machine.Down(CADPointerTargetKind.Cad, hitId, t[hitId].position, Pose.identity, 0f);
        SimulatePress(hitId);
        var start = new Pose(new Vector3(0.05f, 0f, 0f), Quaternion.identity);
        Assert.That(machine.Move(start, 0.3f), Is.EqualTo(Intent.BeginDrag));
        Handle(Intent.BeginDrag, start);
        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.IsActive, Is.True, "drag started");
        session.Update(new Pose(start.position + delta, Quaternion.identity));
        Handle(machine.Up(0.5f));
    }

    private void AssertMovedTogether(string a, Vector3 aBefore, string b, Vector3 bBefore)
    {
        Vector3 moveA = t[a].position - aBefore, moveB = t[b].position - bBefore;
        Assert.That(moveA.magnitude, Is.GreaterThan(1e-3f), $"{a} moved");
        Assert.That(Vector3.Distance(moveA, moveB), Is.LessThan(1e-5f), $"{a} and {b} moved as one rigid group");
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

    private static void Call(object o, string name) => o.GetType().GetMethod(name, Any).Invoke(o, null);
    private static object Get(object o, string name) => o.GetType().GetField(name, Any).GetValue(o);

    private T Track<T>(T obj) where T : Object
    {
        created.Add(obj);
        return obj;
    }
}
