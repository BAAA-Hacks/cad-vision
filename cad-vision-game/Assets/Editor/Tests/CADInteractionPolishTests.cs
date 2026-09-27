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
/// focus / ghost, context-aware menus, beside-bounds menu placement, hover tooltips and model
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
        var title = ((Text)Get(mainMenu, "title")).rectTransform;

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
        MainButton("Reset all sizes").onClick.Invoke();
        Assert.That(Vector3.Distance(root.localScale, reviewScale), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(root.rotation, rotation), Is.LessThan(1e-3f));
        svc.TryGetModelBounds(out Bounds after);
        Assert.That(Vector3.Distance(after.center, before.center), Is.LessThan(1e-3f));
    }

    // 17
    [Test]
    public void ResetAllSizesUndoesEveryResizeWithoutMovingAnything()
    {
        // What a user typically does: scale the top-level assembly at model scope (an object
        // scale, not the model root), a part inside it, and the model in model mode.
        Vector3 a = t["A"].localScale, s1 = t["P1"].localScale, s2 = t["P2"].localScale;
        Vector3 reviewScale = root.localScale;
        svc.SetObjectScaleAroundPoint("A", a * 2f, Vector3.zero, t["A"].position);
        svc.SetObjectScaleAroundPoint("P1", s1 * 3f, Vector3.zero, t["P1"].position);
        svc.SetModelScaleAroundPoint(1.5f, Vector3.zero, root.position);
        svc.SetObjectWorldPose("P2", t["P2"].position + Vector3.up * 0.1f, t["P2"].rotation);
        svc.Select("P1");

        mainMenu.ShowMainMenu();
        MainButton("Reset ▼").onClick.Invoke();
        Assert.That(MainButton("Reset all sizes").interactable, Is.True);
        MainButton("Reset all sizes").onClick.Invoke();

        Assert.That(Vector3.Distance(t["A"].localScale, a), Is.LessThan(Eps));
        Assert.That(Vector3.Distance(t["P1"].localScale, s1), Is.LessThan(Eps));
        Assert.That(Vector3.Distance(t["P2"].localScale, s2), Is.LessThan(Eps), "never resized: untouched");
        Assert.That(Vector3.Distance(root.localScale, reviewScale), Is.LessThan(Eps));
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "selection kept");

        mainMenu.Refresh();
        Assert.That(MainButton("Reset all sizes", includeInactive: true).interactable, Is.True,
            "always available: it doesn't depend on the selection");
    }

    [Test]
    public void ModelMenuResetAllSizesAlsoUndoesAnAssemblyResize()
    {
        Vector3 a = t["A"].localScale;
        svc.SetObjectScaleAroundPoint("A", a * 2f, Vector3.zero, t["A"].position);
        svc.BeginModelManipulation();
        Tick();
        ContextButton("Reset all sizes").onClick.Invoke();
        Assert.That(Vector3.Distance(t["A"].localScale, a), Is.LessThan(Eps),
            "an assembly scaled at model scope is reset too, not just the model root");
    }

    [Test]
    public void ResetEverythingReturnsToAMovedHome()
    {
        Vector3 home = root.position + new Vector3(1f, 0f, 2f);
        Quaternion homeRotation = Quaternion.Euler(0f, 90f, 0f);
        Assert.That(svc.SetModelHomePose(home, homeRotation), Is.True);
        Assert.That(root.position, Is.Not.EqualTo(home), "setting the home moves nothing");

        svc.ResetModel();
        Assert.That(Vector3.Distance(root.position, home), Is.LessThan(Eps));
        Assert.That(Quaternion.Angle(root.rotation, homeRotation), Is.LessThan(1e-3f));
        Assert.That(svc.TryGetModelHomePose(out Vector3 got, out _), Is.True);
        Assert.That(Vector3.Distance(got, home), Is.LessThan(Eps));
    }

    [Test]
    public void RecenterKeepsThePoseRelativeToTheHead()
    {
        // Head at the origin facing +Z, model 1 m ahead; after recentering the head faces +X.
        CADRuntimeBridge.MoveWithHeadFrame(new Vector3(0f, 1.4f, 1f), Quaternion.identity,
            new Vector3(0f, 1.6f, 0f), 0f, new Vector3(2f, 1.6f, 0f), 90f,
            out Vector3 position, out Quaternion rotation);
        Assert.That(Vector3.Distance(position, new Vector3(3f, 1.4f, 0f)), Is.LessThan(Eps), "still 1 m ahead");
        Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(1e-3f), "turned with the view");
    }

    [Test]
    public void FocusIgnoresHiddenGeometry()
    {
        svc.EnterScope("A");
        string[] allButP4 = { "P1", "P2", "P3", "S" };
        Assert.That(svc.WouldFocusGhostAnything(allButP4), Is.True, "P4 would be ghosted");
        svc.Hide("P4");
        Assert.That(svc.WouldFocusGhostAnything(allButP4), Is.False, "hidden P4 wouldn't visibly change");
    }

    // ================= Focus =================

    // 18, 19, 20, 21, 22
    [Test]
    public void FocusGhostsEverythingElseAndClearRestores()
    {
        settings.SetDisplayMode(CADDisplayMode.Shaded); // Clear Focus below checks "no edges".
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
        Assert.That(ContextLabels(), Does.Contain("Clear focus").And.Not.Contain("Focus"), "one context-aware action");
        ContextButton("Clear focus").onClick.Invoke();
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
        ContextButton("Focus selection").onClick.Invoke();

        Assert.That(svc.IsInFocus("P1") && svc.IsInFocus("P2"), Is.True);
        Assert.That(Materials("P3")[0].name, Is.EqualTo("CAD Ghost Surface"));
        Assert.That(Materials("P1")[0].name, Is.Not.EqualTo("CAD Ghost Surface"));
        Tick();
        Assert.That(ContextLabels(), Does.Contain("Clear focus"));
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
        Assert.That(outline.RenderQueue, Is.InRange(2981, 2999), "highlight after the see-through ghosts, before UI");
        svc.ClearFocus();
        Assert.That(outline.RenderQueue, Is.InRange(2981, 2999), "same order without focus");
    }

    // ================= Context-aware menus =================

    // 25, 26
    [Test]
    public void PartMenuShowsDetachOrReattachNeverBoth()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Exit assembly", "Focus", "Detach", "Reset part", "Reset assembly", "Multi-select", "Main menu", "Close" }));

        svc.Detach("P1");
        OpenObjectMenu("P1");
        Assert.That(ContextLabels(), Does.Contain("Reattach").And.Not.Contain("Detach"));
    }

    // 27
    [Test]
    public void AssemblyMenuShowsAssemblyActions()
    {
        OpenObjectMenu("A");
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Enter assembly", "Reset part", "Multi-select", "Main menu", "Close" }),
            "model scope, the only top-level assembly: no Focus (nothing to ghost), no Reset assembly (not inside one), " +
            "no Detach (no parent)");

        svc.EnterScope("A");
        OpenObjectMenu("S");
        Assert.That(ContextLabels(), Does.Contain("Enter assembly").And.Contain("Detach"));

        svc.EnterScope("S");
        svc.Select("S"); // The current scope itself: nothing to enter.
        OpenObjectMenu("S");
        Assert.That(ContextLabels(), Does.Not.Contain("Enter assembly"));
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
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Exit assembly", "Done", "Focus selection", "Reset selected", "Reset assembly", "Clear selection" }));

        ContextButton("Done").onClick.Invoke();
        Tick();
        typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { new CADContextMenuRequest("P1", t["P1"].position, true) });
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Exit assembly", "Focus selection", "Reset selected", "Reset assembly", "Edit selection", "Clear selection", "Close" }));
    }

    // 29
    [Test]
    public void GlobalActionsAreNotInObjectMenus()
    {
        string[] global = { "Reset everything", "Manipulate model", "Show all", "Isolate", "Isolate selected" };
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
            AssertBeside(bounds, id);
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
        AssertBeside(bounds, "selection");
    }

    // 33
    [Test]
    public void ModelMenuUsesModelBounds()
    {
        svc.BeginModelManipulation();
        Tick();
        svc.TryGetModelBounds(out Bounds bounds);
        AssertBeside(bounds, "model");
    }

    // 35
    [Test]
    public void TinyHugeAndCloseTargetsPlaceComfortably()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1"); // Any open menu: the panel's size is what matters.
        var panel = menu.Panel;

        var tiny = new Bounds(new Vector3(0.2f, 1.5f, 0.9f), Vector3.one * 0.002f);
        panel.PlaceBesideBounds(tiny, head);
        AssertBesidePanel(panel, tiny, "tiny");

        // A 4 m assembly: beside it would be far out of view, so the menu stays within 35°.
        var huge = new Bounds(new Vector3(0f, 2f, 3f), new Vector3(4f, 4f, 4f));
        panel.PlaceBesideBounds(huge, head);
        Vector3 position = panel.Root.transform.position;
        Vector3 flat = Vector3.ProjectOnPlane(position - head.position, Vector3.up);
        Assert.That(Vector3.Angle(flat, Vector3.forward), Is.LessThanOrEqualTo(35f + 0.1f), "huge: stays in view");
        Assert.That(position.y, Is.LessThanOrEqualTo(head.position.y + 0.1f + 1e-3f), "not absurdly high");
        Assert.That(Vector3.Distance(position, head.position), Is.InRange(CADMenuPanel.MinMenuDistance - 1e-3f, 1.6f));

        // A part held right in front of the face: the menu still keeps its distance.
        var close = new Bounds(new Vector3(0f, 1.6f, 0.25f), Vector3.one * 0.1f);
        panel.PlaceBesideBounds(close, head);
        Assert.That(Vector3.Distance(panel.Root.transform.position, head.position),
            Is.GreaterThanOrEqualTo(CADMenuPanel.MinMenuDistance - 1e-3f), "close: minimum distance");
    }

    [Test]
    public void MenuOpensOnTheSideTowardTheMiddleOfTheView()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P3"); // Right of the gaze (x = +0.15): the menu goes on its left.
        svc.TryGetObjectBounds("P3", out Bounds right);
        Assert.That(PanelOf(menu).position.x, Is.LessThan(right.min.x), "left of a part on the right");

        Call(menu, "Hide", "test");
        OpenObjectMenu("P1"); // Left of the gaze: the menu goes on its right.
        svc.TryGetObjectBounds("P1", out Bounds left);
        Assert.That(PanelOf(menu).position.x, Is.GreaterThan(left.max.x), "right of a part on the left");
    }

    [Test]
    public void MainMenuRespectsTheMinimumDistance()
    {
        typeof(CADMainMenu).GetField("spawnDistance", Any).SetValue(mainMenu, 0.4f);
        mainMenu.ShowMainMenu();
        Vector3 flat = Vector3.ProjectOnPlane(mainMenu.PanelTransform.position - head.position, Vector3.up);
        Assert.That(flat.magnitude, Is.GreaterThanOrEqualTo(CADMenuPanel.MinMenuDistance - 1e-3f));
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
        var reset = ContextButton("Reset part").GetComponent<CADMenuTooltipTrigger>();

        tooltip.Enter(focus, 10f);
        tooltip.Tick(10.2f);
        Assert.That(tooltip.IsShowing, Is.False, "36: not on a transient crossing");
        tooltip.Tick(10.6f);
        Assert.That(tooltip.ShownText, Is.EqualTo("Emphasize this selection and ghost the rest of the model."));

        tooltip.Enter(reset, 11f);
        Assert.That(tooltip.IsShowing, Is.False, "38: new target restarts the delay");
        tooltip.Tick(11.6f);
        Assert.That(tooltip.ShownText, Is.EqualTo("Put this back where it was: original position, rotation and size."));

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
        menu.Panel.Tooltip.Enter(ContextButton("Clear focus").GetComponent<CADMenuTooltipTrigger>(), 0f);
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

    // ================= Scope in context menus =================

    // 1, 3
    [Test]
    public void RootMenuShowsFullModelScopeAndNoExit()
    {
        OpenObjectMenu("A");
        Assert.That(ScopeLine(), Is.EqualTo("Scope: Full model"));
        Assert.That(ContextLabels(), Does.Not.Contain("Exit assembly"), "hidden at the root, not disabled");
        Assert.That(((Text)Get(menu, "scopeText")).raycastTarget, Is.False, "not clickable");
    }

    // 2, 4, 5
    [Test]
    public void ScopedMenuShowsTheAssemblyAndExitsOneLevel()
    {
        svc.EnterScope("A");
        svc.EnterScope("S");
        OpenObjectMenu("S1");
        Assert.That(ScopeLine(), Is.EqualTo("Scope: S"));
        Assert.That(ContextLabels(), Does.Contain("Exit assembly"));

        ContextButton("Exit assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "one logical level, not the root");

        OpenObjectMenu("P1");
        Assert.That(ScopeLine(), Is.EqualTo("Scope: A"));
        ContextButton("Exit assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.Null);
    }

    // 6
    [Test]
    public void NestedAssemblyMenuOffersBothDirections()
    {
        svc.EnterScope("A");
        OpenObjectMenu("S");
        string[] labels = ContextLabels();
        Assert.That(labels.Take(3), Is.EqualTo(new[] { "Exit assembly", "Enter assembly", "Focus" }),
            "navigation at the top: Exit, then Enter, then the actions");
        float exitY = ((RectTransform)ContextButton("Exit assembly").transform).anchoredPosition.y;
        float enterY = ((RectTransform)ContextButton("Enter assembly").transform).anchoredPosition.y;
        float focusY = ((RectTransform)ContextButton("Focus").transform).anchoredPosition.y;
        Assert.That(enterY - focusY, Is.GreaterThan(exitY - enterY), "a gap separates navigation from the actions");

        ContextButton("Enter assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.EqualTo("S"));
    }

    // 7, 8
    [Test]
    public void SelectionMenusShowScopeAndExit()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        Tick();
        Assert.That(ScopeLine(), Is.EqualTo("Scope: A"));
        ContextButton("Exit assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.Null, "no need to pick a single object to go back up");
    }

    [Test]
    public void ModelMenuHasNoScopeLineOrExit()
    {
        svc.EnterScope("A");
        svc.BeginModelManipulation();
        Tick();
        Assert.That(((Text)Get(menu, "scopeText")).gameObject.activeSelf, Is.False);
        Assert.That(ContextLabels(), Does.Not.Contain("Exit assembly"));
    }

    // ================= Movable menus =================

    // 9, 10, 11, 12
    [Test]
    public void EveryMenuHasABorderGrabBandButButtonsAreNotGrabbable()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");

        OpenObjectMenu("P1");
        AssertGrabBand(menu.Panel, ContextButton("Focus"), "object menu");
        OpenObjectMenu("S");
        AssertGrabBand(menu.Panel, ContextButton("Enter assembly"), "assembly menu");

        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        Tick();
        AssertGrabBand(menu.Panel, ContextButton("Done"), "picking menu");

        mainMenu.ShowMainMenu();
        AssertGrabBand(mainMenu.Panel, MainButton("Close"), "main menu");
        var title = ((Text)Get(mainMenu, "title")).rectTransform;
        Assert.That(mainMenu.Panel.IsGrabPoint(title.TransformPoint(title.rect.center)), Is.True, "the header also grabs");
        Assert.That(title.GetComponent<Button>(), Is.Null, "the header is text, not a button");
        Assert.That(mainMenu.PanelTransform.GetComponentsInChildren<Button>(true)
            .Select(CADMenuPanel.GetLabel).Any(label => label.ToLowerInvariant().Contains("move")), Is.False,
            "no move-window button");
    }

    // 13, 14, 15, 16, 17
    [Test]
    public void BorderDragMovesTheMenuAndButtonsNeverDo()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Transform panel = PanelOf(menu);
        Vector3 placed = panel.position;

        PressUi(rightController, ContextButton("Focus").transform.position);
        Tick();
        Assert.That(menu.Panel.IsDragging, Is.False, "13: a button press never moves the menu");
        Release(rightController);

        rightController.Pose = new Pose(new Vector3(0.1f, 1.3f, 0.3f), Quaternion.identity);
        PressUi(rightController, BorderPoint(menu.Panel, left: true));
        Tick();
        Assert.That(menu.Panel.IsDragging, Is.True, "14: border press starts moving");
        Assert.That(Vector3.Distance(panel.position, placed), Is.LessThan(Eps), "no jump at grab start");

        rightController.Pose = new Pose(new Vector3(0.4f, 1.5f, 0.3f), Quaternion.Euler(0f, 20f, 0f));
        Tick();
        Assert.That(Vector3.Distance(panel.position, placed), Is.GreaterThan(0.2f), "15: follows the pointer");

        Release(rightController);
        Tick();
        Assert.That(menu.Panel.IsDragging, Is.False, "16: release ends the drag");
        Vector3 dropped = panel.position;
        Quaternion droppedRotation = panel.rotation;
        head.position += new Vector3(0.6f, 0f, 0f); // Head moves: no re-facing of a placed menu.
        svc.Detach("P1"); // Relayout (Detach → Reattach label) must not re-place it either.
        Tick();
        Tick();
        Assert.That(menu.IsOpen, Is.True);
        Assert.That(Vector3.Distance(panel.position, dropped), Is.LessThan(Eps), "17: stays where dropped");
        Assert.That(Quaternion.Angle(panel.rotation, droppedRotation), Is.LessThan(1e-3f));
    }

    // 18
    [Test]
    public void ReopeningAContextMenuPlacesItAutomaticallyAgain()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        DragMenuBorder(menu.Panel, new Vector3(0.5f, 0.4f, 0f));
        Tick();
        Assert.That(menu.Panel.WasMoved, Is.True);

        Call(menu, "Hide", "test");
        OpenObjectMenu("P3");
        svc.TryGetObjectBounds("P3", out Bounds bounds);
        AssertBeside(bounds, "reopened for another target");

        DragMenuBorder(menu.Panel, new Vector3(0.5f, 0.4f, 0f));
        OpenObjectMenu("P1"); // Replacing an open, moved menu also re-places it.
        svc.TryGetObjectBounds("P1", out bounds);
        AssertBeside(bounds, "replaced while open");
    }

    // 19
    [Test]
    public void ReopeningTheMainMenuBringsItBackInFrontOfTheUser()
    {
        mainMenu.ShowMainMenu();
        Vector3 inFront = mainMenu.PanelTransform.position;
        DragMenuBorder(mainMenu.Panel, new Vector3(-0.6f, 0.3f, 0.2f), main: true);
        Assert.That(Vector3.Distance(mainMenu.PanelTransform.position, inFront), Is.GreaterThan(0.2f));

        mainMenu.HideMainMenu();
        mainMenu.ShowMainMenu();
        Assert.That(Vector3.Distance(mainMenu.PanelTransform.position, inFront), Is.LessThan(Eps));
    }

    // 20
    [Test]
    public void HiddenMenuCannotBeGrabbedAndEndsItsDrag()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Vector3 border = BorderPoint(menu.Panel, left: true);
        PressUi(rightController, border);
        Assert.That(menu.Panel.IsDragging, Is.True);

        Call(menu, "Hide", "test");
        Assert.That(menu.Panel.IsDragging, Is.False, "hiding cancels the drag");
        Assert.That(menu.Panel.Interactable.enabled, Is.False, "no ray surface while hidden");
        Release(rightController);

        PressUi(rightController, border);
        Assert.That(menu.Panel.IsDragging, Is.False, "a hidden menu is never grabbed");
        Release(rightController);
    }

    // 21
    [Test]
    public void ModelReplacementDuringAMenuDragEndsItSafely()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        PressUi(rightController, BorderPoint(menu.Panel, left: false));
        Assert.That(menu.Panel.IsDragging, Is.True);

        (Transform newRoot, Dictionary<string, Transform> nodes) = BuildModel("2");
        Object.DestroyImmediate(root.gameObject);
        svc.ReplaceImportedModel(newRoot, nodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));
        Tick();
        Assert.That(menu.IsOpen, Is.False);
        Assert.That(menu.Panel.IsDragging, Is.False);
        Assert.That(Get(menu, "target"), Is.Null, "no stale CAD reference");
        Release(rightController);

        svc.Select("N_A");
        typeof(CADContextMenu).GetMethod("Open", Any).Invoke(menu, new object[] { new CADContextMenuRequest("N_A", nodes["A"].position, true) });
        svc.TryGetObjectBounds("N_A", out Bounds bounds);
        AssertBeside(bounds, "new model");
    }

    // ================= Tooltip delay =================

    // 22, 23, 24
    [Test]
    public void TooltipDelayIsPointThreeSeconds()
    {
        Assert.That(CADMenuTooltip.DefaultHoverDelay, Is.EqualTo(0.3f).Within(1e-4f));
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        CADMenuTooltip tooltip = menu.Panel.Tooltip;
        Assert.That(tooltip.HoverDelay, Is.EqualTo(CADMenuTooltip.DefaultHoverDelay), "one shared default");
        var focus = ContextButton("Focus").GetComponent<CADMenuTooltipTrigger>();

        tooltip.Enter(focus, 5f);
        tooltip.Tick(5.25f);
        Assert.That(tooltip.IsShowing, Is.False, "23: not before 0.3 s");
        tooltip.Tick(5.31f);
        Assert.That(tooltip.IsShowing, Is.True, "24: after 0.3 s");
        Assert.That(mainMenu.Panel.Tooltip.HoverDelay, Is.EqualTo(tooltip.HoverDelay), "same in the Main Menu");
    }

    // ---- menu helpers ----

    private string ScopeLine() => ((Text)Get(menu, "scopeText")).text;

    private void AssertGrabBand(CADMenuPanel panel, Button button, string what)
    {
        foreach (bool left in new[] { true, false })
            Assert.That(panel.IsGrabPoint(BorderPoint(panel, left)), Is.True, $"{what}: side border grabs");
        Assert.That(panel.IsGrabPoint(EdgePoint(panel, top: true)), Is.True, $"{what}: top border grabs");
        Assert.That(panel.IsGrabPoint(EdgePoint(panel, top: false)), Is.True, $"{what}: bottom border grabs");
        Assert.That(panel.IsGrabPoint(button.transform.position), Is.False, $"{what}: buttons win");
        var box = panel.Root.GetComponentInChildren<BoxCollider>();
        Assert.That(box.size.x, Is.EqualTo((panel.Width + 2 * CADMenuPanel.BorderWidth) * CADMenuPanel.CanvasScale).Within(1e-5f),
            $"{what}: the ray surface covers the border");
    }

    private static RectTransform CanvasOf(CADMenuPanel panel) => (RectTransform)panel.Root.transform.Find("Canvas");

    // Middle of the left/right grab band, outside the content.
    private static Vector3 BorderPoint(CADMenuPanel panel, bool left)
    {
        RectTransform canvas = CanvasOf(panel);
        Rect r = canvas.rect;
        float x = left ? r.xMin - CADMenuPanel.BorderWidth * 0.5f : r.xMax + CADMenuPanel.BorderWidth * 0.5f;
        return canvas.TransformPoint(new Vector3(x, r.center.y, 0f));
    }

    private static Vector3 EdgePoint(CADMenuPanel panel, bool top)
    {
        RectTransform canvas = CanvasOf(panel);
        Rect r = canvas.rect;
        float y = top ? r.yMax + CADMenuPanel.BorderWidth * 0.5f : r.yMin - CADMenuPanel.BorderWidth * 0.5f;
        return canvas.TransformPoint(new Vector3(r.center.x, y, 0f));
    }

    private void DragMenuBorder(CADMenuPanel panel, Vector3 move, bool main = false)
    {
        rightController.Pose = new Pose(new Vector3(0f, 1.3f, 0.3f), Quaternion.identity);
        PressUi(rightController, BorderPoint(panel, left: true));
        Assert.That(panel.IsDragging, Is.True);
        rightController.Pose = new Pose(rightController.Pose.position + move, Quaternion.identity);
        if (main) Call(mainMenu, "LateUpdate"); else Tick();
        Release(rightController);
        if (main) Call(mainMenu, "LateUpdate"); else Tick();
    }

    // ================= Near-simultaneous two-pointer scaling =================

    // 2, 10, 13, 14
    [Test]
    public void SecondPointerJoinsWhileTheFirstIsOnlyPressed()
    {
        svc.EnterScope("A");
        svc.Select("P1");
        Vector3 scale0 = t["P1"].localScale, position0 = t["P1"].position;
        Quaternion rotation0 = t["P1"].rotation;

        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed), "no drag yet");

        Aim(leftController, "P1");
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True, "joined without waiting for a drag");
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Dragging));
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(rightController.SourceId), "one primary pointer");
        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.Count, Is.EqualTo(1), "13: one grab session holding P1");
        Assert.That(session.GrabbedId, Is.EqualTo("P1"));
        Assert.That(t["P1"].localScale, Is.EqualTo(scale0), "10: baseline 1.0, no scale jump");
        Assert.That(Vector3.Distance(t["P1"].position, position0), Is.LessThan(Eps), "no position jump");
        Assert.That(Quaternion.Angle(t["P1"].rotation, rotation0), Is.LessThan(1e-3f), "no rotation jump");

        MoveTo(leftController, new Vector3(0.6f, 0f, 0f), 0.02f); // 0.3 → 0.6 m.
        Assert.That(t["P1"].localScale.x, Is.EqualTo(scale0.x * 2f).Within(1e-4f), "separation at join is the baseline");
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "14: selection unchanged");
    }

    // 3, 4
    [TestCase(true)]
    [TestCase(false)]
    public void AdjacentFramePressesInEitherOrderStartScaling(bool leftFirst)
    {
        svc.EnterScope("A");
        FakeSource first = leftFirst ? leftController : rightController;
        FakeSource second = leftFirst ? rightController : leftController;
        Aim(first, "P2");
        Aim(second, "P2");
        Press(first, new Vector3(leftFirst ? -0.15f : 0.15f, 0f, 0f));
        Press(second, new Vector3(leftFirst ? 0.15f : -0.15f, 0f, 0f)); // Next frame.

        Assert.That(pointer.IsScaling, Is.True);
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(first.SourceId), "whichever pressed first is primary");
    }

    [Test]
    public void SameFramePressesStartScaling()
    {
        svc.EnterScope("A");
        Aim(leftController, "P2");
        Aim(rightController, "P2");
        leftController.Pose = new Pose(new Vector3(-0.15f, 0f, 0f), Quaternion.identity);
        rightController.Pose = new Pose(new Vector3(0.15f, 0f, 0f), Quaternion.identity);
        leftController.IsSelecting = true;
        rightController.IsSelecting = true;
        Frame();
        Assert.That(pointer.IsScaling, Is.True);
    }

    // 5
    [Test]
    public void ControllerAndHandCanGrabTogether()
    {
        svc.EnterScope("A");
        Aim(rightController, "P3");
        Press(rightController, Vector3.zero);
        Aim(leftHand, "P3");
        Press(leftHand, new Vector3(0.25f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
    }

    // 6
    [Test]
    public void DifferentTargetDoesNotJoinAndThePendingClickSurvives()
    {
        svc.EnterScope("A");
        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Aim(leftController, "P3");
        Press(leftController, new Vector3(0.3f, 0f, 0f));

        Assert.That(pointer.IsScaling, Is.False);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed), "the first press is untouched");
        Release(leftController);
        Release(rightController);
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "the first press is still a normal click");
    }

    // 7
    [Test]
    public void GroupMembersJoinWhileTheFirstIsPressed()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        svc.AddToSelection("P2");
        svc.EndMultiSelect();
        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Aim(leftController, "P2");
        Press(leftController, new Vector3(0.3f, 0f, 0f));

        Assert.That(pointer.IsScaling, Is.True);
        Assert.That(((CADGrabSession)Get(pointer, "session")).Count, Is.EqualTo(2), "the group is held");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
    }

    [Test]
    public void AssemblyGeometryJoinsWhileTheFirstIsPressed()
    {
        Aim(rightController, "P1"); // Model scope: resolves to assembly A.
        Press(rightController, Vector3.zero);
        Aim(leftController, "S1");
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
        Assert.That(Selected(), Is.EqualTo(new[] { "A" }));
    }

    // 8
    [Test]
    public void ModelModeJoinsOnAnyModelGeometry()
    {
        svc.BeginModelManipulation();
        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Aim(leftController, "P4");
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Assert.That(pointer.IsScaling, Is.True);
        Assert.That(pointer.IsManipulatingModel, Is.True);
    }

    // 9, 14
    [Test]
    public void JoiningSuppressesClicksAndMenus()
    {
        svc.EnterScope("A");
        svc.Select("P1"); // A click on it would open the context menu.
        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Aim(leftController, "P1");
        Press(leftController, new Vector3(0.3f, 0f, 0f));
        Release(leftController);
        Release(rightController);
        Tick();

        Assert.That(menu.IsOpen, Is.False, "no context menu");
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "no selection change");
        Assert.That(pointer.IsManipulating, Is.False);
    }

    // 11
    [Test]
    public void SecondPointerReleaseContinuesOneHandedWithoutJump()
    {
        svc.EnterScope("A");
        Vector3 near = t["P1"].position + new Vector3(0f, 0f, -0.2f); // Within reach: 1:1.
        Aim(rightController, "P1");
        Press(rightController, near);
        Aim(leftController, "P1");
        Press(leftController, near + new Vector3(0.3f, 0f, 0f));
        MoveTo(leftController, near + new Vector3(0.45f, 0f, 0f), 0.02f);

        Vector3 scaled = t["P1"].position;
        Release(leftController);
        Assert.That(pointer.IsManipulating, Is.True);
        Assert.That(Vector3.Distance(t["P1"].position, scaled), Is.LessThan(Eps));
        MoveTo(rightController, near + new Vector3(0f, 0.1f, 0f), 0.02f);
        Assert.That(Vector3.Distance(t["P1"].position, scaled + new Vector3(0f, 0.1f, 0f)), Is.LessThan(1e-3f),
            "the primary keeps moving it 1:1");
    }

    [Test]
    public void PrimaryReleaseHandsTheDragToTheOtherPointer()
    {
        svc.EnterScope("A");
        Vector3 near = t["P1"].position + new Vector3(-0.15f, 0f, -0.2f); // Within reach: 1:1.
        Aim(rightController, "P1");
        Press(rightController, near);
        Aim(leftController, "P1");
        Press(leftController, near + new Vector3(0.3f, 0f, 0f));

        Vector3 held = t["P1"].position;
        Release(rightController);
        Assert.That(pointer.IsScaling, Is.False);
        Assert.That(pointer.IsManipulating, Is.True, "the remaining pointer keeps the drag");
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(leftController.SourceId));
        Assert.That(Vector3.Distance(t["P1"].position, held), Is.LessThan(Eps), "no snap");

        MoveTo(leftController, near + new Vector3(0.3f, 0.1f, 0f), 0.02f);
        Assert.That(Vector3.Distance(t["P1"].position, held + new Vector3(0f, 0.1f, 0f)), Is.LessThan(1e-3f));
        Release(leftController);
        Assert.That(pointer.IsManipulating, Is.False);
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }));
    }

    // 12
    [Test]
    public void TooCloseSecondPointerDoesNotForceADrag()
    {
        svc.EnterScope("A");
        Aim(rightController, "P1");
        Press(rightController, Vector3.zero);
        Aim(leftController, "P1");
        Press(leftController, new Vector3(0.03f, 0f, 0f)); // Below the 8 cm minimum.

        Assert.That(pointer.IsScaling, Is.False);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed), "no forced drag");
        Release(leftController);
        Release(rightController);
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "still a normal click");
    }

    // ================= Single-menu rule =================

    // 1, 2, 3, 6, 9, 11
    [Test]
    public void ObjectMenuAndMainMenuReplaceEachOther()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Assert.That(CADMenuPanel.ActivePanel, Is.SameAs(menu.Panel), "1: object menu is the active menu");

        mainMenu.ShowMainMenu();
        Assert.That(mainMenu.IsOpen, Is.True);
        Assert.That(menu.IsOpen, Is.False, "2: the object menu closed");
        Assert.That(menu.Panel.Interactable.enabled, Is.False, "6: and stopped taking rays");
        Assert.That(CADMenuPanel.ActivePanel, Is.SameAs(mainMenu.Panel));
        Assert.That(Selected(), Is.EqualTo(new[] { "P1" }), "9: selection kept");

        OpenObjectMenu("P1");
        Assert.That(mainMenu.IsOpen, Is.False, "3: the Main Menu closed");
        Assert.That(mainMenu.Panel.Interactable.enabled, Is.False);
        Assert.That(OpenMenuCount(), Is.EqualTo(1), "11: one menu visible");
        Tick();
        Call(mainMenu, "LateUpdate");
        Assert.That(OpenMenuCount(), Is.EqualTo(1));
    }

    // 4
    [Test]
    public void MultiSelectMenuReplacesTheObjectMenu()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        ContextButton("Multi-select").onClick.Invoke();
        Tick();
        Assert.That(menu.IsOpen, Is.True);
        Assert.That(ContextLabels(), Does.Contain("Done"), "the picking menu");
        Assert.That(OpenMenuCount(), Is.EqualTo(1));
    }

    // 5
    [Test]
    public void EnteringModelModeReplacesAnyOpenMenu()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        svc.AddToSelection("P1");
        Tick();
        Assert.That(ContextLabels(), Does.Contain("Done"));

        mainMenu.ShowMainMenu(); // Main Menu takes over while picking...
        Tick();
        Assert.That(mainMenu.IsOpen, Is.True, "...and the picking menu doesn't fight back");
        Assert.That(menu.IsOpen, Is.False);

        MainButton("Manipulate model").onClick.Invoke(); // Model mode starts: its menu opens.
        Tick();
        Call(mainMenu, "LateUpdate");
        Assert.That(menu.IsOpen, Is.True);
        Assert.That(ContextLabels(), Is.EqualTo(new[] { "Done", "Reset all sizes", "Reset everything" }));
        Assert.That(mainMenu.IsOpen, Is.False);
        Assert.That(OpenMenuCount(), Is.EqualTo(1));

        mainMenu.ShowMainMenu();
        Tick();
        Assert.That(menu.IsOpen, Is.False, "model menu waits while the Main Menu is open");
        mainMenu.HideMainMenu();
        Tick();
        Assert.That(menu.IsOpen, Is.True, "and comes back once nothing else is open");
    }

    // 7, 8
    [Test]
    public void SwitchingMenusClosesTooltipsAndCancelsDrags()
    {
        menu.Panel.EnableBorderDrag(pointer);
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        menu.Panel.Tooltip.Enter(ContextButton("Focus").GetComponent<CADMenuTooltipTrigger>(), 0f);
        menu.Panel.Tooltip.Tick(1f);
        Assert.That(menu.Panel.Tooltip.IsShowing, Is.True);
        PressUi(rightController, BorderPoint(menu.Panel, left: true));
        Assert.That(menu.Panel.IsDragging, Is.True);

        mainMenu.ShowMainMenu();
        Assert.That(menu.Panel.Tooltip.IsShowing, Is.False, "7: tooltip closed");
        Assert.That(menu.Panel.IsDragging, Is.False, "8: drag cancelled");
        Release(rightController);
    }

    // 10
    [Test]
    public void ModelReplacementLeavesNoStaleActiveMenu()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        (Transform newRoot, Dictionary<string, Transform> nodes) = BuildModel("2");
        Object.DestroyImmediate(root.gameObject);
        svc.ReplaceImportedModel(newRoot, nodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));
        Tick();

        Assert.That(menu.IsOpen, Is.False);
        Assert.That(CADMenuPanel.ActivePanel, Is.Null, "no stale active menu");
        mainMenu.ShowMainMenu();
        Assert.That(mainMenu.IsOpen, Is.True, "the Main Menu still opens");
        Assert.That(CADMenuPanel.ActivePanel, Is.SameAs(mainMenu.Panel));
    }

    private int OpenMenuCount() => (menu.IsOpen ? 1 : 0) + (mainMenu.IsOpen ? 1 : 0);

    // ================= Style guide =================

    [Test]
    public void MenusUseTheStyleGuidePalette()
    {
        CADMenuStyle style = CADMenuStyle.Default;
        Assert.That((Color32)style.PanelColor, Is.EqualTo(new Color32(0x0C, 0x15, 0x24, 0xFF)), "Background");
        Assert.That((Color32)style.ButtonColor, Is.EqualTo(new Color32(0x17, 0x26, 0x39, 0xFF)), "Surface");
        Assert.That((Color32)style.SelectedButtonColor, Is.EqualTo(new Color32(0x2A, 0xDC, 0xDB, 0xFF)), "Accent");
        Assert.That((Color32)style.BorderColor, Is.EqualTo(new Color32(0x28, 0x68, 0x81, 0xFF)), "Border");
        Assert.That((Color32)style.SecondaryTextColor, Is.EqualTo(new Color32(0x9F, 0xB6, 0xCD, 0xFF)), "Secondary text");

        svc.EnterScope("A");
        OpenObjectMenu("P1");
        var background = PanelOf(menu).GetComponentsInChildren<Image>(true).First(i => i.name == "Background");
        Assert.That((Color32)background.color, Is.EqualTo((Color32)style.PanelColor));
        Assert.That(((Text)Get(menu, "scopeText")).color, Is.EqualTo(style.SecondaryTextColor), "scope line is quiet");
        Assert.That(((Text)Get(menu, "titleText")).fontSize, Is.EqualTo(CADMenuPanel.TitleSize));
        Button focus = ContextButton("Focus");
        Assert.That(focus.GetComponentInChildren<Text>().color, Is.EqualTo(Color.white), "secondary: white label");
        Assert.That(((RectTransform)focus.transform).rect.height, Is.EqualTo(CADMenuPanel.ControlHeight), "52-unit target");
    }

    [Test]
    public void PrimarySelectedAndDisabledButtonsLookDistinct()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Tick();
        Button done = ContextButton("Done");
        Text label = done.GetComponentInChildren<Text>();
        Assert.That(done.GetComponent<CADMenuButtonState>().Primary, Is.True, "Done is the primary action");
        Assert.That(label.color, Is.EqualTo(CADMenuStyle.Default.PanelColor), "navy label on cyan");
        Assert.That(label.fontStyle, Is.EqualTo(FontStyle.Bold));

        Button reset = ContextButton("Reset selected"); // Nothing selected yet: disabled.
        Assert.That(reset.interactable, Is.False);
        Assert.That(reset.GetComponentInChildren<Text>().color.a, Is.LessThan(1f), "disabled label dimmed but legible");

        mainMenu.ShowMainMenu();
        Button edges = MainButton("Edges");
        Assert.That(mainMenu.Panel.IsSelected(edges), Is.True);
        Assert.That(edges.GetComponentInChildren<Text>().color, Is.EqualTo(CADMenuStyle.Default.PanelColor),
            "selected segment: navy on cyan");
        Assert.That(MainButton("Shaded").GetComponentInChildren<Text>().color, Is.EqualTo(Color.white));
    }

    [Test]
    public void MainMenuHeaderShowsTheLogoWordmarkAndSubtitle()
    {
        mainMenu.ShowMainMenu();
        var header = (RectTransform)Get(mainMenu, "header");
        RawImage logo = header.GetComponentInChildren<RawImage>();
        Assert.That(logo, Is.Not.Null, "original CADVision logo");
        Assert.That(logo.texture.name, Is.EqualTo("CADVisionLogo"));
        Rect r = logo.rectTransform.rect;
        Assert.That(r.width / r.height, Is.EqualTo((float)logo.texture.width / logo.texture.height).Within(1e-3f),
            "proportions preserved");
        Assert.That(logo.raycastTarget, Is.False, "decoration never takes input");
        string[] texts = header.GetComponentsInChildren<Text>().Select(x => x.text).ToArray();
        Assert.That(texts, Is.EquivalentTo(new[] { "CADVision", "Main menu" }));
        Assert.That(mainMenu.Panel.IsGrabPoint(logo.rectTransform.TransformPoint(r.center)), Is.True, "header moves the menu");
    }

    [Test]
    public void RoundedCornersCanBeSwitchedOff()
    {
        Assert.That(CADMenuPanel.RoundedSprite, Is.Not.Null);
        CADMenuPanel.RoundedCorners = false;
        try
        {
            Assert.That(CADMenuPanel.RoundedSprite, Is.Null, "square fallback");
            var panel = new CADMenuPanel("Square Panel", 200f, CADMenuStyle.Default);
            Button button = panel.CreateButton("Test", null);
            Assert.That(((Image)button.targetGraphic).sprite, Is.Null);
            panel.Destroy();
        }
        finally
        {
            CADMenuPanel.RoundedCorners = true;
        }
    }

    // ================= Menu edits: detach exits, no no-op buttons =================

    [Test]
    public void DetachLeavesTheAssemblyAndKeepsThePartSelected()
    {
        svc.EnterScope("A");
        svc.EnterScope("S");
        OpenObjectMenu("S1");
        ContextButton("Detach").onClick.Invoke();

        Assert.That(svc.IsDetached("S1"), Is.True);
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "one level up, out of the assembly it left");
        Assert.That(Selected(), Is.EqualTo(new[] { "S1" }), "the detached part stays selected");

        OpenObjectMenu("S1");
        ContextButton("Reattach").onClick.Invoke();
        Assert.That(svc.IsDetached("S1"), Is.False);
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "reattaching doesn't change scope");
    }

    [Test]
    public void DetachAtModelScopeKeepsTheScope()
    {
        svc.EnterScope("A");
        OpenObjectMenu("S");
        ContextButton("Detach").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.Null, "A → model scope");
        Assert.That(Selected(), Is.EqualTo(new[] { "S" }));
    }

    [Test]
    public void ResetAssemblyIsOnlyOfferedInsideAnAssembly()
    {
        OpenObjectMenu("A"); // Model scope: the top-level assembly.
        Assert.That(ContextLabels(), Does.Not.Contain("Reset assembly"));
        Assert.That(ContextLabels(), Does.Contain("Reset part"), "a selected assembly has Reset part");

        svc.EnterScope("A");
        OpenObjectMenu("S"); // A subassembly inside A.
        Assert.That(ContextLabels(), Does.Contain("Reset part"));
        Assert.That(ContextLabels(), Does.Contain("Reset assembly"));
    }

    [Test]
    public void ResetPartResetsTheSelectionAndResetAssemblyTheScope()
    {
        svc.EnterScope("A");
        Vector3 s = t["S"].localPosition, s1 = t["S1"].localPosition, p1 = t["P1"].localPosition;
        svc.SetObjectWorldPose("S1", t["S1"].position + Vector3.up * 0.1f, t["S1"].rotation);
        svc.SetObjectWorldPose("S", t["S"].position + Vector3.right * 0.1f, t["S"].rotation);
        svc.SetObjectWorldPose("P1", t["P1"].position + Vector3.forward * 0.1f, t["P1"].rotation);

        OpenObjectMenu("S");
        ContextButton("Reset part").onClick.Invoke(); // The selected assembly, with what's in it.
        Assert.That(Vector3.Distance(t["S"].localPosition, s), Is.LessThan(Eps));
        Assert.That(Vector3.Distance(t["S1"].localPosition, s1), Is.LessThan(Eps));
        Assert.That(Vector3.Distance(t["P1"].localPosition, p1), Is.GreaterThan(0.05f), "only the selected assembly");

        OpenObjectMenu("P2");
        ContextButton("Reset assembly").onClick.Invoke(); // A, the scope, not P2.
        Assert.That(Vector3.Distance(t["P1"].localPosition, p1), Is.LessThan(Eps));
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"));
    }

    [Test]
    public void FocusIsHiddenWhenItWouldChangeNothing()
    {
        Assert.That(svc.WouldFocusGhostAnything(new[] { "A" }), Is.False, "A holds all the geometry");
        Assert.That(svc.WouldFocusGhostAnything(new[] { "P1" }), Is.True);
        Assert.That(svc.WouldFocusGhostAnything(new[] { "S" }), Is.True);
        Assert.That(svc.WouldFocusGhostAnything(new[] { "P1", "P2", "P3", "S", "P4" }), Is.False,
            "every part of A together: nothing left to ghost");

        OpenObjectMenu("A");
        Assert.That(ContextLabels(), Does.Not.Contain("Focus"));

        svc.EnterScope("A");
        svc.BeginMultiSelect();
        foreach (string id in new[] { "P1", "P2", "P3", "S", "P4" })
            svc.AddToSelection(id);
        Tick();
        Assert.That(ContextLabels(), Does.Not.Contain("Focus selection"), "the whole assembly selected");
        svc.RemoveFromSelection("P4");
        Tick();
        Assert.That(ContextLabels(), Does.Contain("Focus selection"));
    }

    [Test]
    public void ClearFocusStaysAvailableWhileFocused()
    {
        svc.Focus("A"); // Focused elsewhere (e.g. via CADEN): nothing ghosted, but it is active.
        OpenObjectMenu("A");
        Assert.That(ContextLabels(), Does.Contain("Clear focus"));
        ContextButton("Clear focus").onClick.Invoke();
        Assert.That(svc.IsFocusActive, Is.False);
    }

    // ================= Grab affordance (edge glow) =================

    [Test]
    public void EdgeGlowLightsWhileARayIsOnTheGrabBand()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        CADMenuPanel panel = menu.Panel;
        Assert.That(panel.GrabGlow, Is.EqualTo(0f));

        RaisePointer(panel, 7, Oculus.Interaction.PointerEventType.Hover, ContextButton("Focus").transform.position);
        panel.UpdateGrabGlow(1f);
        Assert.That(panel.GrabGlow, Is.EqualTo(0f), "over a button: no glow");

        RaisePointer(panel, 7, Oculus.Interaction.PointerEventType.Move, BorderPoint(panel, left: true));
        panel.UpdateGrabGlow(0.05f);
        Assert.That(panel.GrabGlow, Is.InRange(0.01f, 0.99f), "fades in");
        panel.UpdateGrabGlow(1f);
        Assert.That(panel.GrabGlow, Is.EqualTo(1f), "on the grab band: full glow");

        RaisePointer(panel, 7, Oculus.Interaction.PointerEventType.Unhover, BorderPoint(panel, left: true));
        panel.UpdateGrabGlow(1f);
        Assert.That(panel.GrabGlow, Is.EqualTo(0f), "ray left: off");

        RaisePointer(panel, 7, Oculus.Interaction.PointerEventType.Hover, BorderPoint(panel, left: false));
        panel.UpdateGrabGlow(1f);
        Hide(menu);
        Assert.That(panel.GrabGlow, Is.EqualTo(0f), "hiding clears it");
    }

    [Test]
    public void MenusAreThreeQuartersOfTheStyleGuideSize()
    {
        Assert.That(CADMenuPanel.CanvasScale, Is.EqualTo(0.00075f).Within(1e-7f));
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        Assert.That(CanvasOf(menu.Panel).lossyScale.x, Is.EqualTo(0.00075f).Within(1e-7f));
    }

    [Test]
    public void EdgeGlowIsOnlyNearTheCursorAndOnlyOutsideTheWindow()
    {
        svc.EnterScope("A");
        OpenObjectMenu("P1");
        CADMenuPanel panel = menu.Panel;
        RaisePointer(panel, 3, Oculus.Interaction.PointerEventType.Hover, BorderPoint(panel, left: true));
        panel.UpdateGrabGlow(1f);

        Rect r = CanvasOf(panel).rect;
        float edge = CADMenuPanel.BorderWidth;
        Assert.That(panel.EdgeGlow.AlphaAt(new Vector2(r.xMin - edge, r.center.y)), Is.GreaterThan(0.9f), "lit at the cursor");
        Assert.That(panel.EdgeGlow.AlphaAt(new Vector2(r.xMax + edge, r.center.y)), Is.LessThan(0.01f), "far edge stays dark");
        Assert.That(panel.EdgeGlow.AlphaAt(new Vector2(r.center.x, r.yMax + edge)), Is.LessThan(0.2f), "fades along the edge");

        // Every glow vertex is on or outside the window's rounded edge (the panel background).
        List<Vector3> vertices = panel.EdgeGlow.GetVertexPositions();
        Assert.That(vertices, Is.Not.Empty);
        var window = new Rect(r.xMin - edge, r.yMin - edge, r.width + 2 * edge, r.height + 2 * edge);
        float radius = panel.EdgeGlow.Radius;
        foreach (Vector3 v in vertices)
        {
            Vector2 q = new Vector2(Mathf.Abs(v.x - window.center.x), Mathf.Abs(v.y - window.center.y))
                - (window.size * 0.5f - Vector2.one * radius);
            float outside = Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
            Assert.That(outside, Is.GreaterThanOrEqualTo(-1e-3f), $"glow vertex {v} is over the window");
        }
    }

    [Test]
    public void MenusOpenAtLeastPointEightMetresAway()
    {
        Assert.That(CADMenuPanel.MinMenuDistance, Is.EqualTo(0.8f).Within(1e-4f));
    }

    private static void RaisePointer(CADMenuPanel panel, int id, Oculus.Interaction.PointerEventType type, Vector3 point) =>
        typeof(CADMenuPanel).GetMethod("TrackHover", Any).Invoke(panel,
            new object[] { new Oculus.Interaction.PointerEvent(id, type, new Pose(point, Quaternion.identity)) });

    private static void Hide(CADContextMenu contextMenu) =>
        typeof(CADContextMenu).GetMethod("Hide", Any).Invoke(contextMenu, new object[] { "test" });

    // ================= Helpers =================

    private void AssertBeside(Bounds bounds, string what) => AssertBesidePanel(menu.Panel, bounds, what);

    // Beside the target (clear of it sideways, or at the 35° view limit for huge ones), not
    // behind its near face, at least the minimum distance away, facing the user.
    private void AssertBesidePanel(CADMenuPanel menuPanel, Bounds bounds, string what)
    {
        Transform panel = menuPanel.Root.transform;
        Vector3 forward = Vector3.ProjectOnPlane(bounds.center - head.position, Vector3.up).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 e = bounds.extents;
        float lateralExtent = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.z) * e.z;
        float depthExtent = Mathf.Abs(forward.x) * e.x + Mathf.Abs(forward.z) * e.z;
        float halfWidth = menuPanel.OuterWorldWidth * 0.5f;

        float lateral = Mathf.Abs(Vector3.Dot(panel.position - bounds.center, right));
        float depth = Vector3.Dot(panel.position - head.position, forward);
        bool clear = lateral >= lateralExtent + halfWidth - 1e-3f;
        bool atViewLimit = Mathf.Abs(Mathf.Atan2(lateral, depth) * Mathf.Rad2Deg - 35f) < 0.5f;
        Assert.That(clear || atViewLimit, Is.True, $"{what}: beside the target, not in front of it");
        Assert.That(lateral, Is.LessThan(lateralExtent + halfWidth + 0.15f), $"{what}: next to the target, not elsewhere");

        float nearFace = Vector3.Dot(bounds.center - head.position, forward) - depthExtent;
        Assert.That(depth, Is.LessThanOrEqualTo(Mathf.Max(nearFace, CADMenuPanel.MinMenuDistance) + 1e-3f),
            $"{what}: not behind the part's near face");
        Assert.That(Vector3.Distance(panel.position, head.position), Is.GreaterThanOrEqualTo(CADMenuPanel.MinMenuDistance - 1e-3f),
            $"{what}: minimum distance");
        Assert.That(Vector3.Dot(panel.forward, (panel.position - head.position).normalized), Is.GreaterThan(0.99f),
            $"{what}: faces the user");
    }


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
