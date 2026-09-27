using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Pointer sources (controller ray / hand ray / anything implementing ICADPointerSource)
/// driving the one semantic interaction layer: clicks, menus, drags, model mode, arbitration,
/// tracking loss, source switching, model replacement and two-pointer scaling. Fake sources
/// stand in for the SDK rays, so this runs without a device.
/// Model (registered like a Task 2 import): Root / A / { P1, P2, P3, S / { S1 } }.
/// </summary>
public class CADPointerSourceTests
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
    private CADVisionManipulationService svc;
    private CADPointerInteraction pointer;
    private CADContextMenu menu;
    private Transform root;
    private Dictionary<string, Transform> t;
    private FakeSource rightHand, leftHand, rightController;
    private float now;
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

        rightHand = new FakeSource(CADPointerSourceKind.Hand, CADPointerHandedness.Right);
        leftHand = new FakeSource(CADPointerSourceKind.Hand, CADPointerHandedness.Left);
        rightController = new FakeSource(CADPointerSourceKind.Controller, CADPointerHandedness.Right);
        pointer.RegisterSource(rightController);
        pointer.RegisterSource(rightHand);
        pointer.RegisterSource(leftHand);
        now = 0f;
        Frame(); // Sources seen available and idle.
        menuRequests = 0;
    }

    [TearDown]
    public void TearDown()
    {
        if (pointer != null)
            Call(pointer, "OnDestroy"); // The gesture readout.
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
    public void SourceDrivesSelectDownAndUp()
    {
        Aim(rightHand, "P1");
        rightHand.IsSelecting = true;
        Frame();
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed));
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(rightHand.SourceId));

        rightHand.IsSelecting = false;
        Frame();
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Idle));
        Assert.That(pointer.OwnerSourceId, Is.Null);
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }), "model scope: P1 resolves to A");
    }

    // 2
    [Test]
    public void HandClickBehavesExactlyLikeControllerClick()
    {
        svc.EnterScope("A");
        Tap(rightController, "P1");
        string[] byController = Selected();
        Tap(rightController, "P1");
        bool controllerMenu = menu.IsOpen;

        svc.ClearSelection();
        Tick();
        Assert.That(menu.IsOpen, Is.False);

        Tap(rightHand, "P1");
        Assert.That(Selected(), Is.EquivalentTo(byController));
        Tap(rightHand, "P1");
        Assert.That(menu.IsOpen, Is.EqualTo(controllerMenu).And.True, "pinching the selected part opens the same menu");
        Assert.That(menuRequests, Is.EqualTo(2));
    }

    // 3
    [Test]
    public void HandDragUsesTheSameThresholds()
    {
        Aim(rightHand, "P1");
        Press(rightHand, Vector3.zero);

        MoveTo(rightHand, new Vector3(0.02f, 0f, 0f), 0.3f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed), "below 3 cm: still a click candidate");

        rightHand.IsSelecting = false;
        Frame();
        Aim(rightHand, "P1");
        Press(rightHand, Vector3.zero);
        MoveTo(rightHand, new Vector3(0.05f, 0f, 0f), 0.05f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Pressed), "pinch jolt before 0.15 s is not a drag");
        MoveTo(rightHand, new Vector3(0.05f, 0f, 0f), 0.2f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Dragging));

        rightHand.IsSelecting = false;
        Frame();
        Aim(rightHand, "P1");
        Press(rightHand, Vector3.zero);
        MoveTo(rightHand, new Vector3(0.1f, 0f, 0f), 0.02f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Dragging), "fast yank starts at once");
    }

    // 4
    [Test]
    public void HandDragUsesTheSharedGrabSession()
    {
        Vector3 a = t["A"].position;
        Vector3 p1Local = t["P1"].localPosition;

        StartDrag(rightHand, "P1");
        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.IsActive && session.GrabbedId == "A", Is.True, "same session, scope-resolved target");
        MoveTo(rightHand, new Vector3(0.2f, 0.1f, 0f), 0.1f);
        Release(rightHand);

        Assert.That(Vector3.Distance(t["A"].position, a), Is.GreaterThan(1e-3f), "A moved");
        Assert.That(t["P1"].localPosition, Is.EqualTo(p1Local), "P1 moved with A, not on its own");
        Assert.That(session.IsActive, Is.False);
    }

    // 5
    [Test]
    public void UiPinchPreservesCadSelection()
    {
        Tap(rightHand, "P1");
        rightHand.Target = CADPointerTargetKind.Ui;
        rightHand.Hit = Vector3.one;

        rightHand.IsSelecting = true;
        Frame();
        rightHand.IsSelecting = false;
        Frame();

        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }));
    }

    // 6
    [Test]
    public void EmptySpacePinchClearsSelection()
    {
        Tap(rightHand, "P1");
        AimEmpty(rightHand);

        rightHand.IsSelecting = true;
        Frame();
        rightHand.IsSelecting = false;
        Frame();

        Assert.That(Selected(), Is.Empty);
    }

    // 7
    [Test]
    public void ModelModeRoutesHandDragToTheModelRoot()
    {
        Vector3 rootStart = root.position;
        var locals = t.ToDictionary(kv => kv.Key, kv => kv.Value.localPosition);
        svc.BeginModelManipulation();

        StartDrag(rightHand, "P2");
        Assert.That(pointer.IsManipulatingModel, Is.True);
        MoveTo(rightHand, new Vector3(0.1f, 0.2f, 0f), 0.1f);
        Release(rightHand);

        Assert.That(Vector3.Distance(root.position, rootStart), Is.GreaterThan(1e-3f));
        foreach (var entry in locals)
            Assert.That(Vector3.Distance(t[entry.Key].localPosition, entry.Value), Is.LessThan(1e-6f), entry.Key);
    }

    // 8
    [Test]
    public void MultiSelectionSurvivesAHandGroupDrag()
    {
        svc.EnterScope("A");
        svc.BeginMultiSelect();
        Tap(rightHand, "P1");
        Tap(rightHand, "P2");
        svc.EndMultiSelect();
        Vector3 p1 = t["P1"].position, p2 = t["P2"].position;

        StartDrag(rightHand, "P1");
        MoveTo(rightHand, new Vector3(0.15f, 0f, 0.1f), 0.1f);
        Release(rightHand);

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1", "P2" }));
        Assert.That(Vector3.Distance(t["P1"].position - p1, t["P2"].position - p2), Is.LessThan(Eps), "moved as one group");
        Assert.That(Vector3.Distance(t["P1"].position, p1), Is.GreaterThan(1e-3f));
    }

    // 9
    [Test]
    public void DetachedPartMovesIndependentlyUnderHandDrag()
    {
        svc.Detach("P3");
        Vector3 a = t["A"].position, p3 = t["P3"].position;

        StartDrag(rightHand, "P3");
        MoveTo(rightHand, new Vector3(-0.1f, 0.1f, 0f), 0.1f);
        Release(rightHand);

        Assert.That(Selected(), Is.EquivalentTo(new[] { "P3" }), "the detached part is its own unit");
        Assert.That(Vector3.Distance(t["P3"].position, p3), Is.GreaterThan(1e-3f));
        Assert.That(t["A"].position, Is.EqualTo(a), "its assembly stayed put");
        Assert.That(svc.IsDetached("P3"), Is.True);
    }

    // 10
    [Test]
    public void TrackingLossMidDragEndsItSafely()
    {
        StartDrag(rightHand, "P1");
        Vector3 a = t["A"].position;

        rightHand.IsAvailable = false;
        Frame();

        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.IsActive, Is.False, "drag ended");
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Idle));
        Assert.That(pointer.OwnerSourceId, Is.Null);
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }), "selection intact");

        // Tracking returns with the pinch still held, then releases: no click, no drag.
        rightHand.IsAvailable = true;
        MoveTo(rightHand, new Vector3(0.3f, 0f, 0f), 0.1f);
        Assert.That(t["A"].position, Is.EqualTo(a), "nothing moves after tracking loss");
        rightHand.IsSelecting = false;
        Frame();
        Assert.That(menu.IsOpen, Is.False);
        Assert.That(menuRequests, Is.Zero, "release after re-tracking is not a click");
    }

    // 11
    [Test]
    public void RuntimeReplacementClearsHandInteractionSafely()
    {
        StartDrag(rightHand, "P1");
        var (root2, t2) = BuildModel("_B");
        svc.ReplaceImportedModel(root2, t2.ToDictionary(kv => kv.Key + "_B", kv => kv.Value.gameObject));
        Object.DestroyImmediate(root.gameObject);

        MoveTo(rightHand, new Vector3(0.1f, 0f, 0f), 0.1f);
        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(session.IsActive, Is.False, "stale drag ended");
        Release(rightHand);
        Assert.That(Selected(), Is.Empty);

        t = t2.ToDictionary(kv => kv.Key + "_B", kv => kv.Value);
        Tap(rightHand, "P1_B");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A_B" }), "hand works on the new model");
    }

    // 12
    [Test]
    public void SwitchingSourcesNeverDuplicatesAClick()
    {
        // Controller pressed on P1, then put down (controller lost) before releasing.
        Aim(rightController, "P1");
        rightController.IsSelecting = true;
        Frame();
        rightController.IsAvailable = false;
        Frame();
        Assert.That(Selected(), Is.Empty, "a lost press is cancelled, never clicked");

        // The hand appears already pinching: ignored until released.
        rightHand.IsAvailable = false;
        Frame();
        Aim(rightHand, "P1");
        rightHand.IsSelecting = true;
        rightHand.IsAvailable = true;
        Frame();
        rightHand.IsSelecting = false;
        Frame();
        Assert.That(Selected(), Is.Empty);

        // A fresh pinch selects once; the menu needs a second, separate pinch.
        Tap(rightHand, "P1");
        Assert.That(Selected(), Is.EquivalentTo(new[] { "A" }));
        Assert.That(menuRequests, Is.Zero);
    }

    // 13
    [Test]
    public void AnotherSourceCannotStealAnActiveDrag()
    {
        svc.EnterScope("A");
        StartDrag(rightHand, "P1");

        // Same hand's controller, and the other hand on a different (unheld) part.
        Aim(rightController, "P2");
        rightController.IsSelecting = true;
        Aim(leftHand, "P2");
        leftHand.IsSelecting = true;
        Frame();

        var session = (CADGrabSession)Get(pointer, "session");
        Assert.That(pointer.OwnerSourceId, Is.EqualTo(rightHand.SourceId));
        Assert.That(session.GrabbedId, Is.EqualTo("P1"));
        Assert.That(pointer.IsScaling, Is.False, "pressing elsewhere doesn't scale");

        rightController.IsSelecting = false;
        leftHand.IsSelecting = false;
        Frame();
        Assert.That(Selected(), Is.EquivalentTo(new[] { "P1" }), "ignored presses never click");
        Assert.That(session.IsActive, Is.True, "the right hand still holds P1");
    }

    // 14: two pointers scale with the shared gesture, no jump on add/remove.
    [Test]
    public void TwoHandPinchScalesTheHeldPartWithoutJumps()
    {
        svc.EnterScope("A");
        StartDrag(rightHand, "P1");
        var session = (CADGrabSession)Get(pointer, "session");
        Vector3 scale0 = t["P1"].localScale;
        Vector3 rightPos = rightHand.Pose.position;

        // Second hand pinches on the held part 20 cm away: no change yet.
        leftHand.Pose = new Pose(rightPos + new Vector3(-0.2f, 0f, 0f), Quaternion.identity);
        Aim(leftHand, "P1");
        leftHand.IsSelecting = true;
        Frame();
        Assert.That(pointer.IsScaling, Is.True);
        Assert.That(t["P1"].localScale, Is.EqualTo(scale0), "adding the second hand doesn't jump");
        Vector3 pivot = session.GrabPointWorld;

        // Hands twice as far apart: ×2 around the held point, via the shared gesture math.
        leftHand.Pose = new Pose(rightPos + new Vector3(-0.4f, 0f, 0f), Quaternion.identity);
        Frame();
        var reference = new CADScaleGesture();
        reference.TryBeginModel(svc, 0.2f, pivot);
        float expected = reference.Ratio(0.4f);
        reference.End();
        Assert.That(t["P1"].localScale.x, Is.EqualTo(scale0.x * expected).Within(1e-5f));
        Assert.That(Vector3.Distance(session.GrabPointWorld, pivot), Is.LessThan(Eps), "held point is the pivot");

        // Releasing the second hand: the one-hand drag resumes from here (no snap back).
        leftHand.IsSelecting = false;
        Frame();
        Vector3 afterScale = t["P1"].position;
        Frame();
        Assert.That(pointer.IsScaling, Is.False);
        Assert.That(Vector3.Distance(t["P1"].position, afterScale), Is.LessThan(Eps), "no snap");
        Assert.That(t["P1"].localScale.x, Is.EqualTo(scale0.x * expected).Within(1e-5f), "scale kept");
        Assert.That(session.IsActive, Is.True);
    }

    [Test]
    public void TwoHandPinchScalesTheModelInModelModeAndStopsCleanlyOnTrackingLoss()
    {
        svc.BeginModelManipulation();
        StartDrag(rightHand, "P1");
        float ratio0 = svc.ModelScaleRatio;
        Vector3 rightPos = rightHand.Pose.position;

        leftHand.Pose = new Pose(rightPos + new Vector3(-0.2f, 0f, 0f), Quaternion.identity);
        Aim(leftHand, "S1"); // Any CAD geometry holds the model.
        leftHand.IsSelecting = true;
        Frame();
        leftHand.Pose = new Pose(rightPos + new Vector3(-0.3f, 0f, 0f), Quaternion.identity);
        Frame();
        Assert.That(svc.ModelScaleRatio, Is.EqualTo(ratio0 * 1.5f).Within(1e-4f));

        leftHand.IsAvailable = false;
        Frame();
        Vector3 rootAfter = root.position;
        Frame();
        Assert.That(pointer.IsScaling, Is.False, "scale ended when the second hand was lost");
        Assert.That(Vector3.Distance(root.position, rootAfter), Is.LessThan(Eps), "no snap");
        Assert.That(svc.ModelScaleRatio, Is.EqualTo(ratio0 * 1.5f).Within(1e-4f));
        Assert.That(pointer.IsManipulatingModel, Is.True, "the one-hand model drag continues");
    }

    [Test]
    public void ScaleGestureRejectsHandsTooCloseAndClampsPerGesture()
    {
        var gesture = new CADScaleGesture { MinimumSeparation = 0.08f, MinimumRatio = 0.1f, MaximumRatio = 10f };
        Assert.That(gesture.TryBeginModel(svc, 0.01f, Vector3.zero), Is.False, "too close to start");
        Assert.That(gesture.TryBeginModel(svc, float.NaN, Vector3.zero), Is.False);
        Assert.That(gesture.TryBeginModel(svc, 0.2f, t["P1"].position), Is.True);
        Assert.That(gesture.Ratio(0.2f), Is.EqualTo(1f));
        Assert.That(gesture.Ratio(0f), Is.EqualTo(0.1f), "hands together can't collapse the scale");
        Assert.That(gesture.Ratio(1000f), Is.EqualTo(10f));
        gesture.End();
    }

    // ---- helpers ----

    private void Frame(float dt = 0.02f)
    {
        now += dt;
        Call(pointer, "ProcessSources", now);
    }

    private void Tick() => Call(menu, "LateUpdate");

    private void Aim(FakeSource source, string id)
    {
        source.Target = CADPointerTargetKind.Cad;
        source.CadId = id;
        source.Hit = t[id].position;
    }

    private static void AimEmpty(FakeSource source)
    {
        source.Target = CADPointerTargetKind.None;
        source.CadId = null;
        source.Hit = null;
    }

    private void Press(FakeSource source, Vector3 position)
    {
        source.Pose = new Pose(position, Quaternion.identity);
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

    // Short press + release on the part.
    private void Tap(FakeSource source, string id)
    {
        Aim(source, id);
        source.IsSelecting = true;
        Frame();
        source.IsSelecting = false;
        Frame();
    }

    // Press on the part and move past the drag threshold after the hold delay.
    private void StartDrag(FakeSource source, string id)
    {
        Aim(source, id);
        Press(source, Vector3.zero);
        MoveTo(source, new Vector3(0.05f, 0f, 0f), 0.3f);
        Assert.That(pointer.State, Is.EqualTo(CADPointerStateMachine.State.Dragging), "drag started");
    }

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
