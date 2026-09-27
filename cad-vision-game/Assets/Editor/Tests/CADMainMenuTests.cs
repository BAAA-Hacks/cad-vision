using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// The single global main menu (CADMainMenu + CADUISettings), built on CADMenuPanel like the
/// context menu: shared construction, lifecycle and placement, scope,
/// CADEN placeholder, display mode, outline, reset dropdown, UI scale, title-bar drag, model
/// replacement, controller/hand UI presses, the context menu's "Main menu" entry, and the
/// removal of the old wrist quick menu / Settings window.
/// Model (registered like a Task 2 import): Root / A / { P1, P2, S / { S1 } }.
/// </summary>
public class CADMainMenuTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    private const string ScenePath = "Assets/Scenes/ManipulationTest.unity";
    private const string MainMenuGuid = "3bbd8245793bd774cab8f051fd3b888d";
    private const string UISettingsGuid = "d571129fcaf5f8349847cf6885ccf24d";
    private const string MainMenuInputGuid = "0fcd3a6fbf334273acb6649854f5f896";
    private const string OldQuickMenuGuid = "8c7552d6752426c429848b822e4250f3";
    private const string OldSettingsMenuGuid = "069b25aa5daf2874b80a2be69c1c4c0e";

    private sealed class FakeSource : ICADPointerSource
    {
        public string SourceId { get; set; } = "Right Hand (fake)";
        public CADPointerSourceKind Kind { get; set; } = CADPointerSourceKind.Hand;
        public CADPointerHandedness Handedness => CADPointerHandedness.Right;
        public bool IsAvailable { get; set; } = true;
        public Pose Pose { get; set; } = Pose.identity;
        public bool IsSelecting { get; set; }
        public CADPointerTargetKind Target = CADPointerTargetKind.None;
        public Vector3? Hit;

        public CADPointerTargetKind Classify(out string cadId, out Vector3? hitPoint)
        {
            cadId = null;
            hitPoint = Hit;
            return Target;
        }
    }

    private readonly List<Object> created = new();
    private CADVisionManipulationService svc;
    private CADPointerInteraction pointer;
    private CADSelectionOutline outline;
    private CADUISettings settings;
    private CADMainMenu menu;
    private CADContextMenu context;
    private Transform head;
    private Transform root;
    private Dictionary<string, Transform> t;
    private FakeSource hand, controller;
    private float now;
    private readonly List<GameObject> untaggedCameras = new();

    [SetUp]
    public void SetUp()
    {
        // The menu places itself in front of Camera.main: make the test's eye the only one
        // (a loaded scene may have its own MainCamera); restored in TearDown.
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
        outline = host.AddComponent<CADSelectionOutline>();
        settings = host.AddComponent<CADUISettings>();
        menu = host.AddComponent<CADMainMenu>();
        context = host.AddComponent<CADContextMenu>();
        Call(pointer, "Awake");
        Call(outline, "Awake");
        Call(outline, "OnEnable");
        Call(menu, "Awake");
        Call(menu, "OnEnable");
        Call(context, "Awake");

        (root, t) = BuildModel("");
        svc.ReplaceImportedModel(root, t.ToDictionary(kv => kv.Key, kv => kv.Value.gameObject));

        hand = new FakeSource();
        controller = new FakeSource { SourceId = "Right Controller (fake)", Kind = CADPointerSourceKind.Controller };
        pointer.RegisterSource(hand);
        pointer.RegisterSource(controller);
        now = 0f;
        Frame();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (object owner in new object[] { menu, context })
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

    // ---------------- Lifecycle and placement ----------------

    // 1, 2, 3
    [Test]
    public void StartsHiddenAndToggles()
    {
        Assert.That(menu.IsOpen, Is.False, "1: hidden by default");
        menu.ToggleMainMenu();
        Assert.That(menu.IsOpen, Is.True, "2");
        menu.ToggleMainMenu();
        Assert.That(menu.IsOpen, Is.False, "3");

        menu.ShowMainMenu();
        Button("Close").onClick.Invoke();
        Assert.That(menu.IsOpen, Is.False, "Close hides it");
    }

    [Test]
    public void HiddenMenuCannotBeHitByTheRay()
    {
        var interactable = menu.Panel.Interactable;
        Assert.That(interactable.enabled, Is.False);
        Assert.That(menu.PanelTransform.gameObject.activeSelf, Is.False, "hidden: nothing renders, collider inactive");
        menu.ShowMainMenu();
        Assert.That(interactable.enabled, Is.True);
        menu.HideMainMenu();
        Assert.That(interactable.enabled, Is.False);
        Assert.That(menu.PanelTransform.GetComponentInChildren<BoxCollider>(true).gameObject.activeInHierarchy, Is.False,
            "no active ray surface");
        menu.ShowMainMenu();
        Assert.That(menu.PanelTransform.GetComponent<CADUIPointerTarget>(), Is.Not.Null, "presses never deselect");
    }

    // 4
    [Test]
    public void OpensInFrontOfTheHeadAndRespawnsThereOnReopen()
    {
        head.rotation = Quaternion.Euler(0f, 90f, 0f); // Looking along +X.
        menu.ShowMainMenu();
        AssertInFrontOfHead();

        menu.PanelTransform.position += new Vector3(3f, 1f, -2f); // Moved away (e.g. dragged).
        menu.HideMainMenu();
        head.SetPositionAndRotation(new Vector3(1f, 1.5f, 2f), Quaternion.Euler(0f, -45f, 0f));
        menu.ShowMainMenu();
        AssertInFrontOfHead();

        // Showing while already open also brings it back ("bring the menu to me").
        menu.PanelTransform.position += Vector3.up * 2f;
        menu.ShowMainMenu();
        AssertInFrontOfHead();
    }

    // 5
    [Test]
    public void OnlyOneMenuInstanceExists()
    {
        menu.ShowMainMenu();
        menu.HideMainMenu();
        menu.ShowMainMenu();
        menu.ToggleMainMenu();
        menu.ToggleMainMenu();
        int panels = Object.FindObjectsByType<CADUIPointerTarget>(FindObjectsInactive.Include)
            .Count(target => target.name == "CAD Main Menu");
        Assert.That(panels, Is.EqualTo(1));
    }

    // ---------------- Scope ----------------

    // 6, 7, 8, 9, 10
    [Test]
    public void ScopeTextAndContextAwareEnterExit()
    {
        menu.ShowMainMenu();
        Assert.That(ScopeText(), Is.EqualTo("Scope: Full model"), "6");
        Assert.That(Button("Enter assembly").interactable, Is.False, "9: nothing selected");

        svc.Select("P1");
        menu.Refresh();
        Assert.That(Button("Enter assembly").interactable, Is.False, "9: a part isn't enterable");

        svc.Select("A");
        menu.Refresh();
        Button("Enter assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"));
        Assert.That(ScopeText(), Is.EqualTo("Scope: A"), "7");
        Assert.That(Button("Exit assembly").interactable, Is.True, "10: Enter became Exit");
        Assert.That(Labels(), Has.No.Member("Enter assembly"), "one context-aware button");

        svc.Select("S");
        svc.EnterScope("S");
        menu.Refresh();
        Button("Exit assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.EqualTo("A"), "one logical level up");
        Button("Exit assembly").onClick.Invoke();
        Assert.That(svc.CurrentScopeId, Is.Null);
        Assert.That(ScopeText(), Is.EqualTo("Scope: Full model"), "8");
        Assert.That(menu.IsOpen, Is.True, "scope changes keep the menu open");
    }

    // ---------------- CADEN, display, outline ----------------

    // 11
    [Test]
    public void CadenToggleOnlyChangesPlaceholderState()
    {
        menu.ShowMainMenu();
        svc.Select("P1");
        var events = new List<bool>();
        settings.CadenEnabledChanged += events.Add;

        Assert.That(settings.CadenEnabled, Is.True, "CADEN is on by default");
        Assert.That(menu.Panel.IsSelected(Button("CADEN: On")), Is.True);
        Button("CADEN: On").onClick.Invoke();
        Assert.That(settings.CadenEnabled, Is.False);
        Assert.That(menu.Panel.IsSelected(Button("CADEN: Off")), Is.False);
        Button("CADEN: Off").onClick.Invoke();
        Assert.That(settings.CadenEnabled, Is.True);
        Assert.That(events, Is.EqualTo(new[] { false, true }));
        Assert.That(svc.IsSelected("P1"), Is.True, "selection untouched");
        Assert.That(svc.CurrentScopeId, Is.Null, "scope untouched");
    }

    // 12
    [Test]
    public void DisplayModeDefaultsToEdgesAndFiresItsEvent()
    {
        menu.ShowMainMenu();
        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Edges));
        Assert.That(menu.Panel.IsSelected(Button("Edges")), Is.True, "Edges highlighted initially");

        var events = new List<CADDisplayMode>();
        settings.DisplayModeChanged += events.Add;
        Button("Wireframe").onClick.Invoke();
        Button("Shaded").onClick.Invoke();
        Button("Shaded").onClick.Invoke(); // No change, no event.

        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Shaded));
        Assert.That(events, Is.EqualTo(new[] { CADDisplayMode.Wireframe, CADDisplayMode.Shaded }));
        Assert.That(menu.Panel.IsSelected(Button("Shaded")), Is.True);
        Assert.That(menu.Panel.IsSelected(Button("Edges")), Is.False, "segmented: one on");
    }

    // 13
    [Test]
    public void OutlineTogglePreservesSelectionWithoutTint()
    {
        menu.ShowMainMenu();
        svc.Select("P1");
        Call(outline, "LateUpdate");
        Assert.That(ShellCount("P1"), Is.GreaterThan(0));

        Button("On").onClick.Invoke();
        Call(outline, "LateUpdate");
        Assert.That(settings.OutlineEnabled, Is.False);
        Assert.That(outline.ShowOutlines, Is.False);
        Assert.That(ShellCount("P1"), Is.EqualTo(0), "outlines hidden");
        Assert.That(svc.IsSelected("P1"), Is.True, "selection kept");
        Assert.That(svc.UseSelectionTint, Is.False, "no tint fallback");

        Button("Off").onClick.Invoke();
        Call(outline, "LateUpdate");
        Assert.That(ShellCount("P1"), Is.GreaterThan(0), "outline restored for the current selection");
    }

    // ---------------- Reset ----------------

    // 14
    [Test]
    public void ResetDropdownRoutesToTheServiceResets()
    {
        menu.ShowMainMenu();
        Assert.That(Labels(), Has.No.Member("Reset selected"), "collapsed by default");
        Assert.That(Button("Reset selected", includeInactive: true).gameObject.activeSelf, Is.False);

        Button("Reset ▼").onClick.Invoke();
        Assert.That(menu.IsResetExpanded, Is.True);
        Assert.That(Button("Reset selected").interactable, Is.False, "nothing selected");
        Assert.That(Button("Reset assembly").interactable, Is.False, "no selection, root scope");
        Assert.That(Button("Reset everything").interactable, Is.True);

        // Reset object: the one selected object.
        Vector3 p1 = t["P1"].position;
        svc.Select("P1");
        svc.SetObjectWorldPose("P1", p1 + Vector3.up, t["P1"].rotation);
        menu.Refresh();
        Button("Reset selected").onClick.Invoke();
        Assert.That(Vector3.Distance(t["P1"].position, p1), Is.LessThan(1e-4f));
        Assert.That(menu.IsResetExpanded, Is.False, "an option collapses the dropdown");

        // Reset assembly: the assembly the user is in resets with its parts.
        Vector3 p2 = t["P2"].position;
        svc.Select("P2");
        menu.Refresh();
        Button("Reset ▼").onClick.Invoke();
        Assert.That(Button("Reset assembly").interactable, Is.False, "model scope: no assembly to reset");
        Button("Reset ▲").onClick.Invoke();
        svc.EnterScope("A");
        svc.Select("P2");
        svc.SetObjectWorldPose("P2", p2 + Vector3.right, t["P2"].rotation);
        Button("Reset ▼").onClick.Invoke();
        Assert.That(Button("Reset assembly").interactable, Is.True);
        Button("Reset assembly").onClick.Invoke();
        Assert.That(Vector3.Distance(t["P2"].position, p2), Is.LessThan(1e-4f));

        // Reset model: everything, including detached parts.
        svc.Detach("P1");
        svc.Select("P1");
        svc.SetObjectWorldPose("P1", p1 + Vector3.forward, t["P1"].rotation);
        Button("Reset ▼").onClick.Invoke();
        Button("Reset everything").onClick.Invoke();
        Assert.That(Vector3.Distance(t["P1"].position, p1), Is.LessThan(1e-4f));
        Assert.That(svc.IsDetached("P1"), Is.False);
    }

    // ---------------- UI scale ----------------

    // 15
    [Test]
    public void UiScaleClampsAndScalesOnlyTheMenu()
    {
        menu.ShowMainMenu();
        Assert.That(ScaleText(), Is.EqualTo("100%"));

        for (int i = 0; i < 20; i++)
            Button("+").onClick.Invoke();
        Assert.That(settings.UiScale, Is.EqualTo(CADUISettings.MaxUiScale).Within(1e-4f));
        Assert.That(ScaleText(), Is.EqualTo("150%"));
        Assert.That(Button("+").interactable, Is.False);
        Assert.That(menu.PanelTransform.localScale.x, Is.EqualTo(1.5f).Within(1e-4f));

        for (int i = 0; i < 20; i++)
            Button("-").onClick.Invoke();
        Assert.That(settings.UiScale, Is.EqualTo(CADUISettings.MinUiScale).Within(1e-4f));
        Assert.That(ScaleText(), Is.EqualTo("70%"));
        Assert.That(Button("-").interactable, Is.False);

        settings.SetUiScale(1.04f);
        Assert.That(settings.UiScale, Is.EqualTo(1f).Within(1e-4f), "snaps to 10% steps");

        var contextRoot = (GameObject)Get(context, "panelRoot");
        Assert.That(contextRoot.transform.localScale, Is.EqualTo(Vector3.one), "context menus unaffected");
        Assert.That(root.localScale, Is.EqualTo(Vector3.one * 10f), "CAD unaffected");
    }

    // ---------------- Title-bar drag ----------------

    // 16
    [Test]
    public void TitleBarDragFollowsThePointerOnlyFromTheTitleBar()
    {
        menu.ShowMainMenu();
        Transform panel = menu.PanelTransform;
        var titleBar = ((Text)Get(menu, "title")).rectTransform; // Plain header text, a grab region.

        // A press on a button does not drag.
        Press(hand, Button("Close").transform.position + Vector3.zero, hold: true);
        Assert.That(menu.IsDragging, Is.False, "only the title bar grabs");
        Release(hand);

        Vector3 start = panel.position;
        hand.Pose = new Pose(new Vector3(0f, 1.4f, 0.2f), Quaternion.identity);
        Press(hand, titleBar.TransformPoint(titleBar.rect.center), hold: true);
        Assert.That(menu.IsDragging, Is.True);

        hand.Pose = new Pose(hand.Pose.position + new Vector3(0.3f, 0.1f, 0f), Quaternion.Euler(0f, 20f, 0f));
        Call(menu, "LateUpdate");
        Assert.That(Vector3.Distance(panel.position, start), Is.GreaterThan(0.1f), "follows the pointer");

        Release(hand);
        Assert.That(menu.IsDragging, Is.False);
        Vector3 dropped = panel.position;
        hand.Pose = new Pose(Vector3.zero, Quaternion.identity);
        Call(menu, "LateUpdate");
        Assert.That(panel.position, Is.EqualTo(dropped), "stays where dropped");
    }

    // ---------------- Model replacement ----------------

    // 17
    [Test]
    public void ModelReplacementKeepsTheMenuOpenAndCurrent()
    {
        settings.SetDisplayMode(CADDisplayMode.Wireframe);
        settings.SetUiScale(1.2f);
        svc.Select("A");
        svc.EnterScope("A");
        menu.ShowMainMenu();
        Button("Reset ▼").onClick.Invoke();
        Assert.That(ScopeText(), Is.EqualTo("Scope: A"));

        (Transform newRoot, Dictionary<string, Transform> newNodes) = BuildModel("2");
        svc.ReplaceImportedModel(newRoot, newNodes.ToDictionary(kv => "N_" + kv.Key, kv => kv.Value.gameObject));
        Object.DestroyImmediate(root.gameObject); // The old model is gone.
        Call(menu, "LateUpdate");

        Assert.That(menu.IsOpen, Is.True, "not closed by the model change");
        Assert.That(ScopeText(), Is.EqualTo("Scope: Full model"));
        Assert.That(Button("Enter assembly").interactable, Is.False);
        Assert.That(Button("Reset selected").interactable, Is.False);
        Assert.That(Button("Reset assembly").interactable, Is.False);
        Assert.That(Button("Reset everything").interactable, Is.True);
        Assert.That(settings.DisplayMode, Is.EqualTo(CADDisplayMode.Wireframe), "preferences kept");
        Assert.That(settings.UiScale, Is.EqualTo(1.2f).Within(1e-4f));

        svc.Select("N_A");
        Call(menu, "LateUpdate");
        Button("Enter assembly").onClick.Invoke();
        Assert.That(ScopeText(), Is.EqualTo("Scope: A2"), "new model's names");
    }

    // ---------------- Pointer presses ----------------

    // 18
    [Test]
    public void ControllerAndHandUiPressesPreserveSelection()
    {
        menu.ShowMainMenu();
        svc.Select("P1");
        Vector3 onPanel = Button("Close").transform.position;

        foreach (FakeSource source in new[] { controller, hand })
        {
            Press(source, onPanel, hold: false);
            Assert.That(svc.IsSelected("P1"), Is.True, $"{source.Kind}: UI press keeps selection");
            Assert.That(svc.CurrentScopeId, Is.Null);
        }
    }

    [Test]
    public void ContextMenuMainMenuEntryShowsIt()
    {
        svc.Select("P1");
        typeof(CADContextMenu).GetMethod("Open", Any).Invoke(context,
            new object[] { new CADContextMenuRequest("P1", Vector3.forward, true) });
        var contextRoot = (GameObject)Get(context, "panelRoot");
        contextRoot.GetComponentsInChildren<Button>(true)
            .First(b => b.gameObject.activeSelf && CADMenuPanel.GetLabel(b) == "Main menu")
            .onClick.Invoke();

        Assert.That(menu.IsOpen, Is.True);
        Assert.That(context.IsOpen, Is.False);
        Assert.That(svc.IsSelected("P1"), Is.True, "selection kept");
    }

    // ---------------- Quick menu removal ----------------

    // 19
    [Test]
    public void SceneHasTheMainMenuAndNoOldMenus()
    {
        string scene = File.ReadAllText(ScenePath);
        Assert.That(scene, Does.Contain(MainMenuGuid), "CADMainMenu on ManipulationManager");
        Assert.That(scene, Does.Contain(UISettingsGuid), "CADUISettings on ManipulationManager");
        Assert.That(scene, Does.Contain(MainMenuInputGuid), "CADMainMenuInput on ManipulationManager");
        Assert.That(scene, Does.Not.Contain(OldQuickMenuGuid), "no CADQuickMenu component");
        Assert.That(scene, Does.Not.Contain(OldSettingsMenuGuid), "no CADSettingsMenu component");
    }

    // ---------------- Shared construction ----------------

    [Test]
    public void MainMenuAndContextMenuUseTheSameSharedPanel()
    {
        var contextPanel = (CADMenuPanel)Get(context, "panel");
        Assert.That(menu.Panel, Is.TypeOf<CADMenuPanel>());
        Assert.That(contextPanel, Is.TypeOf<CADMenuPanel>());
        Assert.That(contextPanel, Is.Not.SameAs(menu.Panel), "two instances of the one implementation");
        Assert.That(menu.PanelTransform.gameObject, Is.SameAs(menu.Panel.Root));
        Assert.That(Get(context, "panelRoot"), Is.SameAs(contextPanel.Root));

        foreach (GameObject root in new[] { menu.Panel.Root, contextPanel.Root })
            AssertQuestMenuStructure(root);
    }

    [Test]
    public void EveryMainMenuControlIsASharedPanelButton()
    {
        menu.ShowMainMenu();
        Button("Reset ▼").onClick.Invoke(); // Reset options are rows of the same panel.
        var contextRoot = (GameObject)Get(context, "panelRoot");
        ColorBlock reference = contextRoot.GetComponentsInChildren<Button>(true).First().colors;

        Button[] buttons = menu.PanelTransform.GetComponentsInChildren<Button>(true);
        Assert.That(buttons.Select(CADMenuPanel.GetLabel),
            Is.SupersetOf(new[] { "Reset selected", "Reset assembly", "Reset everything", "Shaded", "Edges", "Wireframe", "+", "-" }));
        foreach (Button button in buttons)
        {
            Assert.That(button.colors, Is.EqualTo(reference), $"'{CADMenuPanel.GetLabel(button)}' uses the context-menu button");
            Assert.That(button.targetGraphic, Is.TypeOf<Image>());
            Assert.That(((Image)button.targetGraphic).sprite, Is.Null.Or.SameAs(CADMenuPanel.RoundedSprite),
                "plain Image with the one shared rounded sprite");
            Assert.That(button.GetComponent<CADMenuButtonState>(), Is.Not.Null, "shared style-guide button states");
            Assert.That(button.GetComponentInChildren<Text>(true).font.name, Is.EqualTo("LegacyRuntime"));
        }
    }

    // Same structure and settings as the Quest-proven context menu (checked on both).
    private static void AssertQuestMenuStructure(GameObject root)
    {
        Assert.That(root.transform.parent, Is.Null, "separate root");
        Assert.That(root.GetComponent<CADUIPointerTarget>(), Is.Not.Null);

        Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
        Assert.That(canvases.Length, Is.EqualTo(1), "one canvas, no nested canvases");
        Canvas canvas = canvases[0];
        Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
        Assert.That(canvas.transform.localScale, Is.EqualTo(Vector3.one * CADMenuPanel.CanvasScale));
        Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
        Assert.That(canvas.GetComponent<Oculus.Interaction.PointableCanvas>(), Is.Not.Null);
        Assert.That(canvas.GetComponent<CanvasScaler>(), Is.Null);
        Assert.That(root.GetComponentsInChildren<Mask>(true), Is.Empty);
        Assert.That(root.GetComponentsInChildren<RectMask2D>(true), Is.Empty);
        Assert.That(root.GetComponentsInChildren<Graphic>(true).All(g => g.material == g.defaultMaterial),
            Is.True, "built-in UI material only");

        var interactable = root.GetComponentInChildren<Oculus.Interaction.RayInteractable>(true);
        Assert.That(interactable, Is.Not.Null);
        Assert.That(interactable.gameObject.layer, Is.EqualTo(2), "Ignore Raycast");
        Assert.That(interactable.GetComponent<BoxCollider>(), Is.Not.Null);
        Assert.That(interactable.GetComponent<Oculus.Interaction.Surfaces.ColliderSurface>(), Is.Not.Null);
        Assert.That(root.GetComponentsInChildren<Oculus.Interaction.PokeInteractable>(true), Is.Empty, "ray only");
    }

    // 20
    [Test]
    public void OldMenuTypesAreGone()
    {
        Assembly game = typeof(CADMainMenu).Assembly;
        Assert.That(game.GetType("CADQuickMenu"), Is.Null);
        Assert.That(game.GetType("CADSettingsMenu"), Is.Null);
        Assert.That(game.GetType("CADWorldPanel"), Is.Null);
    }

    // ---------------- Helpers ----------------

    private void AssertInFrontOfHead()
    {
        Assert.That(Camera.main.transform, Is.SameAs(head), "the test eye is Camera.main");
        Vector3 offset = menu.PanelTransform.position - head.position;
        Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        Assert.That(Vector3.Dot(Vector3.ProjectOnPlane(offset, Vector3.up), flatForward), Is.InRange(0.75f, 0.95f),
            "0.75–0.95 m ahead");
        Assert.That(offset.y, Is.InRange(-0.2f, -0.01f), "slightly below eye level");
        Assert.That(Vector3.Dot(menu.PanelTransform.forward, offset.normalized), Is.GreaterThan(0.99f), "faces the user");
    }

    private void Frame()
    {
        now += 0.02f;
        Call(pointer, "ProcessSources", now);
    }

    private void Press(FakeSource source, Vector3 point, bool hold)
    {
        source.Target = CADPointerTargetKind.Ui;
        source.Hit = point;
        source.IsSelecting = true;
        Frame();
        Call(menu, "LateUpdate");
        if (!hold)
            Release(source);
    }

    private void Release(FakeSource source)
    {
        source.IsSelecting = false;
        Frame();
        Call(menu, "LateUpdate");
    }

    private string ScopeText() => ((Text)Get(menu, "scopeText")).text;

    private string ScaleText() => ((Text)Get(menu, "scaleValue")).text;

    private Button Button(string label, bool includeInactive = false) =>
        menu.PanelTransform.GetComponentsInChildren<Button>(true)
            .FirstOrDefault(b => (includeInactive || b.gameObject.activeSelf) && CADMenuPanel.GetLabel(b) == label)
        ?? throw new AssertionException($"No button '{label}'.");

    private string[] Labels() =>
        menu.PanelTransform.GetComponentsInChildren<Button>(true)
            .Where(b => b.gameObject.activeSelf)
            .Select(CADMenuPanel.GetLabel).ToArray();

    private int ShellCount(string id) => t[id].GetComponentsInChildren<CADVisualOverlay>(true).Length;

    private (Transform root, Dictionary<string, Transform> t) BuildModel(string suffix)
    {
        var modelRoot = Track(new GameObject("CADVisionModelRoot" + suffix)).transform;
        modelRoot.localScale = Vector3.one * 10f;
        modelRoot.position = new Vector3(0f, 0f, 2f);
        var nodes = new Dictionary<string, Transform>();
        nodes["A"] = Node("A" + suffix, modelRoot, new Vector3(0f, 0.15f, 0f), false);
        nodes["P1"] = Node("P1" + suffix, nodes["A"], new Vector3(0.02f, 0f, 0f), true);
        nodes["P2"] = Node("P2" + suffix, nodes["A"], new Vector3(0.04f, 0f, 0f), true);
        nodes["S"] = Node("S" + suffix, nodes["A"], new Vector3(-0.02f, 0.01f, 0f), false);
        nodes["S1"] = Node("S1" + suffix, nodes["S"], new Vector3(0f, 0.02f, 0f), true);
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
