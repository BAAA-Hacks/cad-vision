using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Interaction polish pass: left/right parity through the one semantic pointer layer,
/// two-pointer scaling with controllers and hands (logical targets, no jumps), reset scale,
/// focus / ghost, context-aware menus, above-bounds menu placement, hover tooltips and model
/// replacement. Fake pointer sources stand in for the SDK rays (no device); these tests cannot
/// prove physical Quest input or rendering.
/// Model near the user (root at 0, 1.2, 1): Root / A / { P1, P2, P3, S / { S1 }, P4 (origin 5 m
/// away from its geometry) }.
/// </summary>
public class CADInteractionPolishTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private const float Eps = 1e-4f;

    private sealed class FakeSource : ICADPointerSource
    {
        public FakeSource(CADPointerSourceKind kind, CADPointerHandedness handedness)
        {
            Kind = kind;
            Handedness = handedness;
            SourceId = $"{handedness} {kind} (fake)";
        }

        public string SourceId { get; }
        public CADPointerSourceKind Kind { get; }
        public CADPointerHandedness Handedness { get; }
        public bool IsAvailable { get; set; } = true;
        public Pose Pose { get; set; } = Pose.identity;
        public bool IsSelecting { get; set; }
        public CADPointerTargetKind Target = CADPointerTargetKind.None;
        public string CadId;
        public Vector3? Hit;

        public CADPointerTargetKind Classify(out string cadId, out Vector3? hitPoint)
        {
            cadId = Target == CADPointerTargetKind.Cad ? CadId : null;
            hitPoint = Hit;
            return Target;
        }
    }

    private readonly List<Object> created = new();
    private readonly List<GameObject> untaggedCameras = new();
    private CADVisionManipulationService svc;
    private CADPointerInteraction pointer;
    private CADContextMenu menu;
    private CADMainMenu mainMenu;
    private CADUISettings settings;
    private CADSelectionOutline outline;
    private CADDisplayModeController display;
    private Transform head;
    private Transform root;
    private Dictionary<string, Transform> t;
    private FakeSource leftController, rightController, leftHand, rightHand;
    private float now;

    [SetUp]
    public void SetUp()
    {
        foreach (Camera other in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            if (other.CompareTag("MainCamera"))
            {
                other.tag = "Untagged";
                untaggedCameras.Add(other.gameObject);
            }
        }

        var cam = Track(new GameObject("Eye", typeof(Camera)));
        cam.tag = "MainCamera";
        head = cam.transform;
        head.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.identity);

        var host = Track(new GameObject("ManipulationManager"));
        svc = host.AddComponent<CADVisionManipulationService>();
        pointer = host.AddComponent<CADPointerInteraction>();
        settings = host.AddComponent<CADUISettings>();
        outline = host.AddComponent<CADSelectionOutline>();
        display = host.AddComponent<CADDisplayModeController>();
        mainMenu = host.AddComponent<CADMainMenu>();
        menu = host.AddComponent<CADContextMenu>();
        Call(pointer, "Awake");
        Call(outline, "Awake");
        Call(outline, "OnEnable");
        Call(display, "Awake");
        Call(display, "OnEnable");
        Call(mainMenu, "Awake");
        Call(mainMenu, "OnEnable");
        Call(menu, "Awake");
        pointer.ContextMenuRequested += r => typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { r });

        (root, t) = BuildModel("");
        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));
        Call(display, "Start");

        leftController = new FakeSource(CADPointerSourceKind.Controller, CADPointerHandedness.Left);
        rightController = new FakeSource(CADPointerSourceKind.Controller, CADPointerHandedness.Right);
        leftHand = new FakeSource(CADPointerSourceKind.Hand, CADPointerHandedness.Left);
        rightHand = new FakeSource(CADPointerSourceKind.Hand, CADPointerHandedness.Right);
        foreach (FakeSource source in new[] { leftController, rightController, leftHand, rightHand })
            pointer.RegisterSource(source);
        now = 0f;
        Frame();
    }

    [TearDown]
    public void TearDown()
    {
        Call(display, "OnDestroy");
        Call(outline, "OnDestroy");
        foreach (object owner in new object[] { menu, mainMenu })
        {
            if (owner != null && Get(owner, "panelRoot") is GameObject panel)
                Object.DestroyImmediate(panel);
        }
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
        foreach (GameObject other in untaggedCameras)
        {
            if (other != null)
                other.tag = "MainCamera";
        }
        untaggedCameras.Clear();
    }

    // ================= Input parity =================

    [Test]
    public void LeftControllerIsAPointerSourceByDefault()
    {
        Assert.That(typeof(CADPointerInteraction).GetField("useLeftController", Any).GetValue(pointer), Is.True);
        Assert.That(typeof(CADPointerInteraction).GetField("useRightController", Any).GetValue(pointer), Is.True);
        Assert.That(typeof(CADPointerInteraction).GetField("useLeftHand", Any).GetValue(pointer), Is.True);
        Assert.That(typeof(CADPointerInteraction).GetField("useRightHand", Any).GetValue(pointer), Is.True);
    }

    // 1, 3, 5
    [Test]
    public void LeftControllerSelectsAndOpensTheMenuLikeTheRight()
    {
        svc.EnterScope("A");
        foreach (FakeSource source in new[] { leftController, rightController })
        {
            svc.ClearSelection();
            Tap(source, "P1");
            Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), $"{source.SourceId}: click selects");

            Tap(source, "P1");
            Tick();
            Assert.That(menu.IsOpen, Is.True, $"{source.SourceId}: click on the selection opens the menu");

            Tap(source, null);
            Assert.That(Selected(), Is.Empty, $"{source.SourceId}: empty click deselects");
            Tick();
            Assert.That(menu.IsOpen, Is.False);
        }
    }

    // 2
    [Test]
    public void LeftControllerDragsAndRotates()
    {
        svc.EnterScope("A");
        Vector3 start = t["P1"].position;
        StartDrag(leftController, "P1");
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(leftController.SourceId));
        Assert.That(pointer.IsManipulating, Is.True);

        leftController.Pose = new Pose(new Vector3(0.2f, 0.1f, 0f), Quaternion.Euler(0f, 30f, 0f));
        Frame();
        Assert.That(Vector3.Distance(t["P1"].position, start), Is.GreaterThan(0.05f), "moves");
        Assert.That(Quaternion.Angle(t["P1"].rotation, Quaternion.identity), Is.GreaterThan(1f), "rotates");
        Release(leftController);
        Assert.That(pointer.IsManipulating, Is.False);
    }

    // 4 (semantic side: the Meta canvas path itself needs the headset)
    [Test]
    public void LeftHandUiPressKeepsSelectionAndDragsTheMainMenuTitleBar()
    {
        svc.EnterScope("A");
        Tap(rightHand, "P1");
        mainMenu.ShowMainMenu();
        var title = (RectTransform)((Button)Get(mainMenu, "titleBar")).transform;

        PressUi(leftHand, title.TransformPoint(title.rect.center));
        Call(mainMenu, "LateUpdate");
        Assert.That(mainMenu.IsDragging, Is.True, "left hand grabs the title bar");
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "UI press keeps the selection");
        Release(leftHand);
        Call(mainMenu, "LateUpdate");
        Assert.That(mainMenu.IsDragging, Is.False);
    }

    // 6
    [Test]
    public void SecondPointerCannotStealAnActiveDrag()
    {
        svc.EnterScope("A");
        StartDrag(rightController, "P1");
        Aim(leftController, "P3");
        Press(leftController, new Vector3(0.4f, 0f, 0f));

        Assert.That(pointer.OwnerSourceId, Is.EqualTo(rightController.SourceId));
        Assert.That(pointer.IsScaling, Is.False, "P3 is not the held target");
        Release(leftController);
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "the ignored press never clicks");
        Assert.That(pointer.IsManipulating, Is.True, "the right drag continues");
    }

    // 7, 8, 10, 11
    [Test]
    public void BothControllerTriggersScaleWithoutJumps()
    {
        svc.EnterScope("A");
        StartDrag(rightController, "P1");
        Vector3 scale0 = t["P1"].localScale;
        Vector3 position0 = t["P1"].position;

        Aim(leftController, "P1");
        Press(leftController, new Vector3(0.3f, 0f, 0f)); // 0.25 m from the right controller.
        Assert.That(pointer.IsScaling, Is.True, "7: second trigger joins as scaling");
        Assert.That(t["P1"].localScale, Is.EqualTo(scale0), "10: joining doesn't jump");
        Assert.That(Vector3.Distance(t["P1"].position, position0), Is.LessThan(Eps));

        MoveTo(leftController, new Vector3(0.55f, 0f, 0f), 0.02f); // Separation doubles.
        Assert.That(t["P1"].localScale.x, Is.EqualTo(scale0.x * 2f).Within(1e-4f), "8: uniform ×2");

        Vector3 scaled = t["P1"].position;
        Release(leftController);
        Assert.That(pointer.IsScaling, Is.False);
        Assert.That(Vector3.Distance(t["P1"].position, scaled), Is.LessThan(Eps), "11: leaving doesn't jump");
        Frame();
        Assert.That(Vector3.Distance(t["P1"].position, scaled), Is.LessThan(Eps), "first pointer continues smoothly");
        Assert.That(pointer.IsManipulating, Is.True);
    }

    // 9
    [Test]
    public void HandPinchesScale()
    {
        svc.EnterScope("A");
        StartDrag(leftHand, "P2");
        Vector3 scale0 = t["P2"].localScale;
        Aim(rightHand, "P2");
        Press(rightHand, new Vector3(0.3f, 0f, 0f));
        MoveTo(rightHand, new Vector3(0.175f, 0f, 0f), 0.02f); // Separation halves.
        Assert.That(t["P2"].localScale.x, Is.EqualTo(scale0.x * 0.5f).Within(1e-4f));
    }

    // 12
    [Test]
    public void GroupScalingAcceptsAnyMemberOfTheGroup()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        svc.EndMultiSelect();
        Vector3 s1 = t["P1"].localScale, s2 = t["P2"].localScale;

        StartDrag(rightController, "P1");
        Aim(leftController, "P2"); // A different collider of the same held group.
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
        MoveTo(leftController, new Vector3(0.55f, 0f, 0f), 0.02f);
        Assert.That(t["P1"].localScale.x, Is.EqualTo(s1.x * 2f).Within(1e-4f));
        Assert.That(t["P2"].localScale.x, Is.EqualTo(s2.x * 2f).Within(1e-4f));
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    [Test]
    public void AssemblyScalingAcceptsAnyGeometryOfTheAssembly()
    {
        StartDrag(rightController, "P1"); // Model scope: P1 resolves to assembly A.
        Assert.That(Selected(), Is.EqualTo(new[] { "A" }));
        Aim(leftController, "S1"); // Nested deeper in A.
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
    }

    // 13
    [Test]
    public void ModelScalingAcceptsAnyModelGeometry()
    {
        svc.BeginModelManipulation();
        Vector3 scale0 = root.localScale;
        StartDrag(rightController, "P1");
        Assert.That(pointer.IsManipulatingModel, Is.True);
        Aim(leftController, "P3");
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
        MoveTo(leftController, new Vector3(0.55f, 0f, 0f), 0.02f);
        Assert.That(root.localScale.x, Is.EqualTo(scale0.x * 2f).Within(1e-4f));
    }

    // ================= Reset scale =================

    // 14
    [Test]
    public void ResetObjectScaleKeepsPoseAndVisibleCenter()
    {
        Vector3 original = t["P1"].localScale;
        svc.SetObjectWorldPose("P1", new Vector3(0.3f, 1.4f, 0.9f), Quaternion.Euler(0f, 35f, 10f));
        svc.SetObjectScaleAroundPoint("P1", original * 3f, Vector3.zero, t["P1"].position);
        Quaternion rotation = t["P1"].rotation;
        svc.TryGetObjectBounds("P1", out Bounds before);

        Assert.That(svc.ResetObjectScale("P1"), Is.True);
        Assert.That(Vector3.Distance(t["P1"].localScale, original), Is.LessThan(Eps), "original, not 1");
        Assert.That(Quaternion.Angle(t["P1"].rotation, rotation), Is.LessThan(1e-3f), "rotation kept");
        svc.TryGetObjectBounds("P1", out Bounds after);
        Assert.That(Vector3.Distance(after.center, before.center), Is.LessThan(1e-3f), "stays where it is");
    }

    // 15
    [Test]
    public void ResetAssemblyScaleKeepsPose()
    {
        Vector3 original = t["A"].localScale;
        svc.SetObjectWorldPose("A", t["A"].position + Vector3.right * 0.2f, Quaternion.Euler(0f, -20f, 0f));
        svc.SetObjectScaleAroundPoint("A", original * 0.5f, Vector3.zero, t["A"].position);
        Quaternion rotation = t["A"].rotation;
        svc.TryGetObjectBounds("A", out Bounds before);

        svc.ResetObjectScale("A");
        Assert.That(Vector3.Distance(t["A"].localScale, original), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(t["A"].rotation, rotation), Is.LessThan(1e-3f));
        svc.TryGetObjectBounds("A", out Bounds after);
        Assert.That(Vector3.Distance(after.center, before.center), Is.LessThan(1e-3f));
    }

    [Test]
    public void DetachedPartResetsToItsAssemblyScale()
    {
        float worldScale = t["P1"].lossyScale.x;
        svc.Detach("P1");
        svc.SetObjectScaleAroundPoint("P1", t["P1"].localScale * 2f, Vector3.zero, t["P1"].position);
        svc.ResetObjectScale("P1");
        Assert.That(t["P1"].lossyScale.x, Is.EqualTo(worldScale).Within(Eps));
        Assert.That(svc.IsDetached("P1"), Is.True, "still detached");
    }

    // 16
    [Test]
    public void ResetModelScaleRestoresTheReviewScaleOnly()
    {
        Vector3 reviewScale = root.localScale;
        svc.BeginModelManipulation();
        svc.SetModelWorldPose(root.position + new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Euler(0f, 50f, 0f));
        svc.SetModelScaleAroundPoint(3f, Vector3.zero, root.position);
        Quaternion rotation = root.rotation;
        svc.TryGetModelBounds(out Bounds before);

        MainButton("Reset ▼").onClick.Invoke();
        MainButton("Reset scale").onClick.Invoke(); // Model mode: the model root.
        Assert.That(Vector3.Distance(root.localScale, reviewScale), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(root.rotation, rotation), Is.LessThan(1e-3f));
        svc.TryGetModelBounds(out Bounds after);
        Assert.That(Vector3.Distance(after.center, before.center), Is.LessThan(1e-3f));
    }

    // 17
    [Test]
    public void MainMenuResetScaleResetsTheSelectedRoots()
    {
        svc.EnterScope("A");
        Vector3 s1 = t["P1"].localScale, s2 = t["P2"].localScale;
        svc.SetObjectScaleAroundPoint("P1", s1 * 2f, Vector3.zero, t["P1"].position);
        svc.SetObjectScaleAroundPoint("P2", s2 * 4f, Vector3.zero, t["P2"].position);
        Vector3 p1 = t["P1"].position;
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");

        mainMenu.ShowMainMenu();
        MainButton("Reset ▼").onClick.Invoke();
        Assert.That(MainButton("Reset scale").interactable, Is.True);
        MainButton("Reset scale").onClick.Invoke();
        Assert.That(Vector3.Distance(t["P1"].localScale, s1), Is.LessThan(Eps));
        Assert.That(Vector3.Distance(t["P2"].localScale, s2), Is.LessThan(Eps));
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }), "selection kept");

        svc.EndMultiSelect(clearSelection: true);
        mainMenu.Refresh();
        Assert.That(MainButton("Reset scale", includeInactive: true).interactable, Is.False, "nothing to reset");
    }

    // ================= Focus =================

    // 18, 19, 20, 21, 22
    [Test]
    public void FocusGhostsEverythingElseAndClearRestores()
    {
        Material p1 = Materials("P1")[0], p2 = Materials("P2")[0];
        svc.Detach("P3");
        svc.EnterScope("A");
        svc.Select("P1");

        OpenObjectMenu("P1");
        ContextButton("Focus").onClick.Invoke();
        Assert.That(svc.IsFocusActive, Is.True);
        Assert.That(Materials("P1")[0], Is.SameAs(p1), "18: target keeps its normal look");
        Assert.That(Materials("P2")[0].name, Is.EqualTo("CAD Ghost Surface"), "19: others ghosted");
        Assert.That(Materials("P3")[0].name, Is.EqualTo("CAD Ghost Surface"), "detached parts too");
        Assert.That(t["P2"].gameObject.activeInHierarchy, Is.True, "ghosted, not hidden");
        Assert.That(Overlay("P2").activeInHierarchy, Is.True, "ghost edges");
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "20: selection kept");
        Assert.That(svc.IsDetached("P3"), Is.True, "21: detach state kept");
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "scope kept");

        OpenObjectMenu("P1");
        Assert.That(ContextLabels(), Does.Contain("Clear Focus").And.Not.Contain("Focus"), "one context-aware action");
        ContextButton("Clear Focus").onClick.Invoke();
        Assert.That(svc.IsFocusActive, Is.False);
        Assert.That(Materials("P2")[0], Is.SameAs(p2), "22: rendering restored");
        Assert.That(Overlay("P2").activeSelf, Is.False, "no edges in Shaded");
    }

    [Test]
    public void FocusOnAnAssemblyKeepsItsDescendantsNormal()
    {
        Material s1 = Materials("S1")[0];
        svc.Focus("S");
        Assert.That(Materials("S1")[0], Is.SameAs(s1));
        Assert.That(Materials("P1")[0].name, Is.EqualTo("CAD Ghost Surface"));
    }

    // 23
    [Test]
    public void MultiSelectionFocus()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        Tick();
        ContextButton("Focus Selection").onClick.Invoke();

        Assert.That(svc.IsInFocus("P1") && svc.IsInFocus("P2"), Is.True);
        Assert.That(Materials("P3")[0].name, Is.EqualTo("CAD Ghost Surface"));
        Assert.That(Materials("P1")[0].name, Is.Not.EqualTo("CAD Ghost Surface"));
        Tick();
        Assert.That(ContextLabels(), Does.Contain("Clear Focus"));
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    // 24
    [Test]
    public void HiddenStateAndFocusDoNotConflict()
    {
        svc.Focus("P1");
        svc.Hide("P2");
        Assert.That(Overlay("P2").activeInHierarchy, Is.False, "hidden ghost leaves no edges");
        svc.Show("P2");
        Assert.That(Overlay("P2").activeInHierarchy, Is.True);
        Assert.That(Materials("P2")[0].name, Is.EqualTo("CAD Ghost Surface"));

        svc.ClearFocus();
        svc.Hide("P2");
        svc.Focus("P1");
        svc.Show("P2");
        Assert.That(Materials("P2")[0].name, Is.EqualTo("CAD Ghost Surface"), "focus applied while hidden");
    }

    [Test]
    public void GhostingKeepsTheSelectionOutlineRim()
    {
        svc.EnterScope("A");
        svc.Select("P2");
        svc.Focus("P1");
        Call(outline, "LateUpdate");
        Assert.That(t["P2"].GetComponentsInChildren<CADVisualOverlay>(true).Any(o => o.name == "__CADOutline"), Is.True);
        Assert.That(outline.RenderQueue, Is.GreaterThan(2500), "outline after the see-through ghosts");
        svc.ClearFocus();
        Assert.That(outline.RenderQueue, Is.EqualTo(2010));
    }

    // ================= Context-aware menus =================

    // 25, 26
    [Test]
    public void PartMenuShowsDetachOrReattachNeverBoth()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Focus", "Detach", "Reset Object", "Multi-Select", "Main Menu", "Close" }));

        svc.Detach("P1");
        OpenObjectMenu("P1");
        Assert.That(ContextLabels(), Does.Contain("Reattach").And.Not.Contain("Detach"));
    }

    // 27
    [Test]
    public void AssemblyMenuShowsAssemblyActions()
    {
        OpenObjectMenu("A");
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Enter Assembly", "Focus", "Reset Assembly", "Multi-Select", "Main Menu", "Close" }),
            "top-level assembly: no parent to detach from");

        svc.EnterScope("A");
        OpenObjectMenu("S");
        Assert.That(ContextLabels(), Does.Contain("Enter Assembly").And.Contain("Detach"));

        svc.EnterScope("S");
        svc.Select("S"); // The current scope itself: nothing to enter.
        OpenObjectMenu("S");
        Assert.That(ContextLabels(), Does.Not.Contain("Enter Assembly"));
    }

    // 28
    [Test]
    public void SelectionMenusShowSelectionActions()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        Tick();
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Done", "Focus Selection", "Reset Selected", "Clear Selection" }));

        ContextButton("Done").onClick.Invoke();
        Tick();
        typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { new CADContextMenuRequest("P1", t["P1"].position, true) });
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Focus Selection", "Reset Selected", "Edit Selection", "Clear Selection", "Close" }));
    }

    // 29
    [Test]
    public void GlobalActionsAreNotInObjectMenus()
    {
        string[] global = { "Reset Model", "Manipulate Model", "Show All", "Isolate", "Exit Assembly", "Isolate Selected" };
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Assert.That(ContextLabels().Intersect(global), Is.Empty);
        OpenObjectMenu("S");
        Assert.That(ContextLabels().Intersect(global), Is.Empty);

        mainMenu.ShowMainMenu();
        Assert.That(MainButton("Manipulate model"), Is.Not.Null, "in the Main Menu instead");
    }

    // ================= Placement =================

    // 30, 31, 34
    [Test]
    public void ObjectAndAssemblyMenusSitAboveTheirVisibleBounds()
    {
        svc.EnterScope("A");
        foreach (string id in new[] { "P1", "S", "P4" })
        {
            OpenObjectMenu(id);
            svc.TryGetObjectBounds(id, out Bounds bounds);
            AssertAbove(bounds, id);
        }
        Assert.That(Vector3.Distance(t["P4"].position, PanelOf(menu).position), Is.GreaterThan(3f),
            "34: P4's far-away origin is ignored");
    }

    // 32
    [Test]
    public void SelectionMenuUsesCombinedBounds()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P3");
        Tick();
        svc.TryGetSelectionBounds(out Bounds bounds);
        AssertAbove(bounds, "selection");
    }

    // 33
    [Test]
    public void ModelMenuUsesModelBounds()
    {
        svc.BeginModelManipulation();
        Tick();
        svc.TryGetModelBounds(out Bounds bounds);
        AssertAbove(bounds, "model");
    }

    // 35
    [Test]
    public void TinyAndHugeTargetsClampComfortably()
    {
        var panel = menu.Panel;
        panel.PlaceAboveBounds(new Bounds(new Vector3(0f, 1.2f, 0.8f), Vector3.one * 0.002f), head);
        float bottom = PanelBottom(panel);
        Assert.That(bottom, Is.GreaterThanOrEqualTo(1.2f + 0.1f - 1e-3f), "tiny: minimum rise above the part");

        // A 4 m assembly: its top is far above the eyes.
        var huge = new Bounds(new Vector3(0f, 2f, 3f), new Vector3(4f, 4f, 4f));
        panel.PlaceAboveBounds(huge, head);
        Vector3 position = panel.Root.transform.position;
        Assert.That(PanelBottom(panel), Is.LessThanOrEqualTo(head.position.y + 0.25f + 1e-3f), "not absurdly high");
        Assert.That(Vector3.Distance(position, head.position), Is.InRange(0.45f - 1e-3f, 1.4f + 1e-3f), "comfortable distance");
        Assert.That(Vector3.Dot(panel.Root.transform.forward, (position - head.position).normalized), Is.GreaterThan(0.99f), "faces the user");
    }

    // ================= Tooltips =================

    // 36, 37, 38, 39, 40 (controller and hand hover arrive as the same uGUI enter/exit)
    [Test]
    public void TooltipAppearsAfterTheDelayFollowsTheHoverAndHides()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        CADMenuTooltip tooltip = menu.Panel.Tooltip;
        var focus = ContextButton("Focus").GetComponent<CADMenuTooltipTrigger>();
        var reset = ContextButton("Reset Object").GetComponent<CADMenuTooltipTrigger>();

        tooltip.Enter(focus, 10f);
        tooltip.Tick(10.2f);
        Assert.That(tooltip.IsShowing, Is.False, "36: not on a transient crossing");
        tooltip.Tick(10.6f);
        Assert.That(tooltip.ShownText, Is.EqualTo("Emphasize this selection and ghost the rest of the model."));

        tooltip.Enter(reset, 11f);
        Assert.That(tooltip.IsShowing, Is.False, "38: new target restarts the delay");
        tooltip.Tick(11.6f);
        Assert.That(tooltip.ShownText, Is.EqualTo("Restore this object to its original assembly transform."));

        tooltip.Exit(focus); // Stale exit from the previous button.
        Assert.That(tooltip.IsShowing, Is.True);
        tooltip.Exit(reset);
        Assert.That(tooltip.IsShowing, Is.False, "37: hover ended");

        reset.OnPointerEnter(null); // The uGUI path both rays use.
        Assert.That(tooltip.Hovered, Is.SameAs(reset));
        reset.OnPointerExit(null);
        Assert.That(tooltip.Hovered, Is.Null);
    }

    [Test]
    public void TooltipBoxIsBesideThePanelAndNeverARaycastTarget()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        CADMenuTooltip tooltip = menu.Panel.Tooltip;
        var trigger = ContextButton("Focus").GetComponent<CADMenuTooltipTrigger>();
        tooltip.Enter(trigger, 0f);
        tooltip.Tick(1f);

        Image box = tooltip.GetComponentsInChildren<Image>(true).First(i => i.name == "Tooltip");
        Assert.That(box.raycastTarget, Is.False);
        Assert.That(box.GetComponentInChildren<Text>().raycastTarget, Is.False);
        var canvas = (RectTransform)box.transform.parent;
        Vector3 local = canvas.InverseTransformPoint(((RectTransform)box.transform).TransformPoint(Vector3.zero));
        Assert.That(local.x, Is.GreaterThan(canvas.rect.xMax), "outside the panel (and its ray surface), not over the label");

        Call(menu, "Hide", "test");
        Assert.That(tooltip.IsShowing, Is.False, "hidden with the panel");
    }

    // ================= Model replacement =================

    // 41, 42, 43, 44, 45
    [Test]
    public void ModelReplacementClearsFocusMenusAndSessions()
    {
        svc.EnterScope("A");
        svc.Focus("P1");
        OpenObjectMenu("P1");
        menu.Panel.Tooltip.Enter(ContextButton("Clear Focus").GetComponent<CADMenuTooltipTrigger>(), 0f);
        menu.Panel.Tooltip.Tick(1f);
        StartDrag(rightController, "P2");
        int focusEvents = 0;
        svc.FocusChanged += () => focusEvents++;

        (Transform newRoot, Dictionary<string, Transform> nodes) = BuildModel("2");
        Object.DestroyImmediate(root.gameObject);
        svc.ReplaceImportedModel(newRoot, nodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));
        Frame();
        Tick();

        Assert.That(svc.IsFocusActive, Is.False, "41: focus cleared");
        Assert.That(focusEvents, Is.EqualTo(1));
        Assert.That(display.GhostedRendererCount, Is.EqualTo(0));
        Assert.That(menu.IsOpen, Is.False, "43: menu closed");
        Assert.That(menu.Panel.Tooltip.IsShowing, Is.False, "42: no stale tooltip");
        Release(rightController);
        Assert.That(pointer.IsManipulating, Is.False, "44: session ended");

        Tap(leftController, "N_A", nodes["A"].position);
        Assert.That(Selected(), Is.EqualTo(new[] { "N_A" }), "45: the new model is usable");
    }

    // ================= Helpers =================

    private void AssertAbove(Bounds bounds, string what)
    {
        Transform panel = PanelOf(menu);
        float bottom = PanelBottom(menu.Panel);
        Assert.That(bottom, Is.GreaterThanOrEqualTo(bounds.max.y - 1e-3f), $"{what}: panel bottom above the target top");
        Vector2 flat = new Vector2(panel.position.x - bounds.center.x, panel.position.z - bounds.center.z);
        Assert.That(flat.magnitude, Is.LessThan(bounds.extents.magnitude + 0.2f), $"{what}: over the target, not elsewhere");
        Assert.That(Vector3.Dot(panel.forward, (panel.position - head.position).normalized), Is.GreaterThan(0.99f),
            $"{what}: faces the user");
    }

    private static float PanelBottom(CADMenuPanel panel) =>
        panel.Root.transform.position.y - panel.Height * CADMenuPanel.CanvasScale * panel.Root.transform.lossyScale.y * 0.5f;

    private static Transform PanelOf(CADContextMenu contextMenu) => ((GameObject)Get(contextMenu, "panelRoot")).transform;

    private void OpenObjectMenu(string id)
    {
        svc.Select(id);
        typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { new CADContextMenuRequest(id, t.ContainsKey(id) ? t[id].position : Vector3.zero, true) });
        Assert.That(menu.IsOpen, Is.True, $"menu open for {id}");
    }

    private void Tick() => Call(menu, "LateUpdate");

    private string[] ContextLabels() =>
        PanelOf(menu).GetComponentsInChildren<Button>(true)
            .Where(b => b.gameObject.activeSelf)
            .OrderByDescending(b => ((RectTransform)b.transform).anchoredPosition.y)
            .Select(CADMenuPanel.GetLabel).ToArray();

    private Button ContextButton(string label) =>
        PanelOf(menu).GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => b.gameObject.activeSelf && CADMenuPanel.GetLabel(b) == label)
        ?? throw new AssertionException($"No context button '{label}'.");

    private Button MainButton(string label, bool includeInactive = false) =>
        mainMenu.PanelTransform.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => (includeInactive || b.gameObject.activeSelf) && CADMenuPanel.GetLabel(b) == label)
        ?? throw new AssertionException($"No main menu button '{label}'.");

    private Material[] Materials(string id) => t[id].GetComponent<MeshRenderer>().sharedMaterials;

    private GameObject Overlay(string id) => t[id].Find(CADDisplayModeController.OverlayName)?.gameObject;

    private string[] Selected() => svc.GetSelectedIds().ToArray();

    private void Frame(float dt = 0.02f)
    {
        now += dt;
        Call(pointer, "ProcessSources", now);
    }

    private void Aim(FakeSource source, string id, Vector3? hit = null)
    {
        if (id == null)
        {
            source.Target = CADPointerTargetKind.None;
            source.CadId = null;
            source.Hit = null;
            return;
        }
        source.Target = CADPointerTargetKind.Cad;
        source.CadId = id;
        source.Hit = hit ?? t[id].position;
    }

    private void Press(FakeSource source, Vector3 position)
    {
        source.Pose = new Pose(position, Quaternion.identity);
        source.IsSelecting = true;
        Frame();
    }

    private void PressUi(FakeSource source, Vector3 hit)
    {
        source.Target = CADPointerTargetKind.Ui;
        source.CadId = null;
        source.Hit = hit;
        source.IsSelecting = true;
        Frame();
    }

    private void MoveTo(FakeSource source, Vector3 position, float dt)
    {
        source.Pose = new Pose(position, source.Pose.rotation);
        Frame(dt);
    }

    private void Release(FakeSource source)
    {
        source.IsSelecting = false;
        Frame();
    }

    private void Tap(FakeSource source, string id, Vector3? hit = null)
    {
        Aim(source, id, hit);
        source.IsSelecting = true;
        Frame();
        source.IsSelecting = false;
        Frame();
    }

    private void StartDrag(FakeSource source, string id)
    {
        Aim(source, id);
        Press(source, Vector3.zero);
        MoveTo(source, new Vector3(0.05f, 0f, 0f), 0.3f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Dragging), "drag started");
    }

    private (Transform root, Dictionary<string, Transform> t) BuildModel(string suffix)
    {
        var modelRoot = Track(new GameObject("CADVisionModelRoot" + suffix)).transform;
        modelRoot.position = new Vector3(0f, 1.2f, 1f);
        var nodes = new Dictionary<string, Transform>();
        nodes["A"] = Node("A" + suffix, modelRoot, Vector3.zero, false);
        nodes["P1"] = Node("P1" + suffix, nodes["A"], new Vector3(-0.15f, 0f, 0f), true);
        nodes["P2"] = Node("P2" + suffix, nodes["A"], new Vector3(0f, 0f, 0f), true);
        nodes["P3"] = Node("P3" + suffix, nodes["A"], new Vector3(0.15f, 0f, 0f), true);
        nodes["S"] = Node("S" + suffix, nodes["A"], new Vector3(0f, 0.15f, 0f), false);
        nodes["S1"] = Node("S1" + suffix, nodes["S"], Vector3.zero, true);

        // CAD origin 5 m away from its geometry (common in SolidWorks exports).
        nodes["P4"] = Node("P4" + suffix, nodes["A"], new Vector3(5f, 0f, 0f), false);
        Transform mesh = Node("P4 mesh" + suffix, nodes["P4"], new Vector3(-5f, -0.15f, 0f), true);
        mesh.localScale = Vector3.one * 0.08f;
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
            go.transform.localScale = Vector3.one * 0.1f;
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
