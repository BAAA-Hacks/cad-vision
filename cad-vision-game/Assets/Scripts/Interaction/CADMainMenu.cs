using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The application's single global menu: Scope (current scope + one context-aware
/// Enter/Exit Assembly button), CADEN (placeholder toggle), View (display mode buttons,
/// outline toggle), Reset (options shown under "Reset ▼"), Interface (UI scale), Close.
///
/// Rendering and input are CADMenuPanel, the same construction the object/assembly context
/// menus use (same canvas, images, texts, buttons, ray surface, show/hide, row stacking); this
/// class only decides contents, labels, callbacks, placement and lifecycle. Every control is a
/// plain CADMenuPanel button or text; "on"/selected states use the panel's selected button
/// color. The Reset options are extra rows that are stacked only while expanded, the same way
/// the context menu switches its button sets.
///
/// Lifecycle: starts hidden (Inspector: startVisible). ToggleMainMenu / ShowMainMenu /
/// HideMainMenu are the entry points. Every show respawns it ~0.7 m in front of the head,
/// a little below eye level, facing the user (never head-locked). While open it stays put,
/// except that pressing and holding the title bar (a normal panel button) with trigger or
/// pinch drags it rigidly with the pointer. It stays open across model replacement; all state
/// is re-read from the service and CADUISettings every frame.
///
/// Activation input lives in CADMainMenuInput (left controller Menu button → ToggleMainMenu);
/// with hands, the context menu's "Main Menu" entry calls ShowMainMenu.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
[RequireComponent(typeof(CADUISettings))]
public class CADMainMenu : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("Distance in front of the headset when shown (m).")]
    [SerializeField, Range(0.4f, 1.2f)] private float spawnDistance = 0.7f;
    [Tooltip("How far below eye level it appears (m).")]
    [SerializeField] private float spawnDrop = 0.12f;

    [Header("Activation")]
    [Tooltip("Development safety net: show the menu at startup.")]
    [SerializeField] private bool startVisible;

    // Layout in canvas units (1 unit = 1 mm at 100% UI scale).
    private const float PanelWidth = 360f;
    private const float TitleBarHeight = 44f;
    private const float SectionHeight = 26f;
    private const float TextHeight = 32f;
    private const float ButtonHeight = 44f;
    private const float SectionSpacing = 16f;

    private CADVisionManipulationService manipulationService;
    private CADUISettings settings;
    private CADPointerInteraction pointerInteraction;

    private CADMenuPanel panel;
    private GameObject panelRoot; // panel.Root.
    private Button titleBar;
    private Text scopeSection, cadenSection, viewSection, resetSection, interfaceSection;
    private Text scopeText, displayLabel, outlineLabel, scaleLabel, scaleValue;
    private readonly Dictionary<CADDisplayMode, Button> displayButtons = new();
    private Button scopeButton, cadenButton, outlineButton;
    private Button resetButton, resetObjectButton, resetAssemblyButton, resetModelButton;
    private Button scaleDownButton, scaleUpButton, closeButton;
    private bool resetExpanded;

    // Title-bar drag: the pressing pointer and the panel's pose relative to it.
    private ICADPointerSource dragSource;
    private Vector3 dragPositionOffset;
    private Quaternion dragRotationOffset;

    public bool IsOpen => panel != null && panel.IsOpen;
    public bool IsDragging => dragSource != null;
    public bool IsResetExpanded => resetExpanded;
    public Transform PanelTransform => panelRoot != null ? panelRoot.transform : null;
    /// <summary>The shared panel this menu is built from (same class as the context menu's).</summary>
    public CADMenuPanel Panel => panel;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        settings = GetComponent<CADUISettings>();
        pointerInteraction = GetComponent<CADPointerInteraction>();
        BuildPanel();
        panel.Hide();
        if (startVisible)
            ShowMainMenu();
    }

    private void OnEnable()
    {
        if (pointerInteraction != null)
            pointerInteraction.UiPressed += OnUiPressed;
    }

    private void OnDisable()
    {
        if (pointerInteraction != null)
            pointerInteraction.UiPressed -= OnUiPressed;
        EndDrag();
    }

    private void OnDestroy() => panel?.Destroy();

    // ---------------- Show / hide ----------------

    public void ToggleMainMenu()
    {
        if (IsOpen) HideMainMenu();
        else ShowMainMenu();
    }

    /// <summary>Shows the menu in front of the user; if already open, brings it back in front.</summary>
    public void ShowMainMenu()
    {
        EndDrag();
        resetExpanded = false;
        ApplyLayout();
        Refresh();
        PlaceInFrontOfHead();
        panel.Show();
        Debug.Log("[CADMainMenu] Shown.");
    }

    public void HideMainMenu()
    {
        EndDrag();
        resetExpanded = false;
        if (IsOpen)
            Debug.Log("[CADMainMenu] Hidden.");
        panel.Hide();
    }

    private void LateUpdate()
    {
        if (!IsOpen)
            return;

        UpdateDrag();
        Refresh();
    }

    private void PlaceInFrontOfHead()
    {
        Transform head = Camera.main != null ? Camera.main.transform : null;
        if (head == null)
            return;

        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // Looking straight up/down.
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 position = head.position + forward * spawnDistance + Vector3.down * spawnDrop;
        panelRoot.transform.SetPositionAndRotation(position,
            Quaternion.LookRotation(position - head.position, Vector3.up));
    }

    // ---------------- Title-bar drag ----------------

    private void OnUiPressed(ICADPointerSource source, Vector3 hitPoint)
    {
        if (IsOpen && dragSource == null &&
            CADMenuPanel.Contains((RectTransform)titleBar.transform, hitPoint))
        {
            BeginDrag(source);
        }
    }

    private void BeginDrag(ICADPointerSource source)
    {
        Pose pose = source.Pose;
        Quaternion inverse = Quaternion.Inverse(pose.rotation);
        Transform root = panelRoot.transform;
        dragPositionOffset = inverse * (root.position - pose.position);
        dragRotationOffset = inverse * root.rotation;
        dragSource = source;
        Debug.Log($"[CADMainMenu] Moving with {source.SourceId}.");
    }

    private void UpdateDrag()
    {
        if (dragSource == null)
            return;

        if (!dragSource.IsAvailable || !dragSource.IsSelecting)
        {
            EndDrag();
            return;
        }

        Pose pose = dragSource.Pose;
        panelRoot.transform.SetPositionAndRotation(pose.position + pose.rotation * dragPositionOffset,
            pose.rotation * dragRotationOffset);
    }

    private void EndDrag() => dragSource = null;

    // ---------------- State → UI ----------------

    public void Refresh()
    {
        SetText(scopeText, $"Scope: {manipulationService.CurrentScopeDisplayName}");

        // Exit inside an assembly; else Enter (enabled only for one selected assembly).
        bool atRoot = manipulationService.IsAtRootScope;
        CADMenuPanel.SetLabel(scopeButton, atRoot ? "Enter assembly" : "Exit assembly");
        CADMenuPanel.SetInteractable(scopeButton, !atRoot || manipulationService.TryGetEnterableSelection(out _));

        CADMenuPanel.SetLabel(cadenButton, settings.CadenEnabled ? "CADEN: On" : "CADEN: Off");
        panel.SetSelected(cadenButton, settings.CadenEnabled);

        foreach (KeyValuePair<CADDisplayMode, Button> entry in displayButtons)
            panel.SetSelected(entry.Value, entry.Key == settings.DisplayMode);

        CADMenuPanel.SetLabel(outlineButton, settings.OutlineEnabled ? "On" : "Off");
        panel.SetSelected(outlineButton, settings.OutlineEnabled);

        CADMenuPanel.SetLabel(resetButton, resetExpanded ? "Reset ▲" : "Reset ▼");
        CADMenuPanel.SetInteractable(resetObjectButton, manipulationService.GetSelectedIds().Count > 0);
        CADMenuPanel.SetInteractable(resetAssemblyButton, ResetAssemblyTarget() != null);
        CADMenuPanel.SetInteractable(resetModelButton, manipulationService.ModelRoot != null);

        SetText(scaleValue, $"{Mathf.RoundToInt(settings.UiScale * 100f)}%");
        CADMenuPanel.SetInteractable(scaleDownButton, settings.UiScale > CADUISettings.MinUiScale + 1e-4f);
        CADMenuPanel.SetInteractable(scaleUpButton, settings.UiScale < CADUISettings.MaxUiScale - 1e-4f);

        Vector3 scale = Vector3.one * settings.UiScale;
        if (panelRoot.transform.localScale != scale)
            panelRoot.transform.localScale = scale;
    }

    private static void SetText(Text text, string value)
    {
        if (text.text != value)
            text.text = value;
    }

    // The selected assembly, else the assembly containing the one selected object, else the
    // current scope (entered assembly). Same rule as the object context menu.
    private string ResetAssemblyTarget()
    {
        List<string> selected = manipulationService.GetSelectedIds();
        if (selected.Count == 1)
        {
            string id = selected[0];
            return manipulationService.HasCadChildren(id) ? id : manipulationService.GetLogicalParentId(id);
        }

        return manipulationService.CurrentScopeId;
    }

    // ---------------- Actions ----------------

    private void OnScopeButton()
    {
        if (!manipulationService.IsAtRootScope)
            manipulationService.ExitScope();
        else if (manipulationService.TryGetEnterableSelection(out string assemblyId))
            manipulationService.EnterScope(assemblyId);
    }

    private void ToggleReset()
    {
        resetExpanded = !resetExpanded;
        ApplyLayout();
    }

    // One selected object: ResetObject; several: ResetSelected (same per-root rules).
    private void ResetObjectAction()
    {
        List<string> selected = manipulationService.GetSelectedIds();
        if (selected.Count == 1)
            manipulationService.ResetObject(selected[0]);
        else if (selected.Count > 1)
            manipulationService.ResetSelected();
        CollapseReset();
    }

    private void ResetAssemblyAction()
    {
        string assemblyId = ResetAssemblyTarget();
        if (assemblyId != null)
            manipulationService.ResetAssembly(assemblyId);
        CollapseReset();
    }

    private void ResetModelAction()
    {
        manipulationService.ResetModel();
        CollapseReset();
    }

    private void CollapseReset()
    {
        resetExpanded = false;
        ApplyLayout();
    }

    // ---------------- Construction ----------------

    private void BuildPanel()
    {
        panel = new CADMenuPanel("CAD Main Menu", PanelWidth, CADMenuStyle.Default);
        panelRoot = panel.Root;

        // Title bar: a normal panel button; pressing and holding it drags the menu (UiPressed).
        titleBar = AddButton("Main menu  (hold to move)", null);
        titleBar.GetComponentInChildren<Text>(true).fontStyle = FontStyle.Bold;

        scopeSection = Section("Scope");
        scopeText = panel.CreateText("Scope Value", "Scope: Full model");
        scopeButton = AddButton("Enter assembly", OnScopeButton);

        cadenSection = Section("CADEN");
        cadenButton = AddButton("CADEN: Off", settings.ToggleCaden);

        viewSection = Section("View");
        displayLabel = panel.CreateText("Display Label", "Display");
        foreach (CADDisplayMode mode in new[] { CADDisplayMode.Shaded, CADDisplayMode.Edges, CADDisplayMode.Wireframe })
        {
            CADDisplayMode captured = mode;
            displayButtons[mode] = AddButton(mode.ToString(), () => settings.SetDisplayMode(captured));
        }
        outlineLabel = panel.CreateText("Outline Label", "Outline");
        outlineButton = AddButton("On", () => settings.SetOutlineEnabled(!settings.OutlineEnabled));

        resetSection = Section("Reset");
        resetButton = AddButton("Reset ▼", ToggleReset);
        resetObjectButton = AddButton("Reset object", ResetObjectAction);
        resetAssemblyButton = AddButton("Reset assembly", ResetAssemblyAction);
        resetModelButton = AddButton("Reset model", ResetModelAction);

        interfaceSection = Section("Interface");
        scaleLabel = panel.CreateText("Scale Label", "UI scale");
        scaleDownButton = AddButton("-", () => settings.StepUiScale(-1));
        scaleValue = panel.CreateText("Scale Value", "100%", CADMenuPanel.FontSize, FontStyle.Bold);
        scaleUpButton = AddButton("+", () => settings.StepUiScale(1));

        closeButton = AddButton("Close", HideMainMenu);

        ApplyLayout();
    }

    private Text Section(string title) => panel.CreateText(title, title, CADMenuPanel.FontSize - 2, FontStyle.Bold);

    // Every button refreshes the menu after its action (labels, selected and enabled states).
    private Button AddButton(string label, UnityEngine.Events.UnityAction onClick)
    {
        Button button = panel.CreateButton(label, onClick);
        button.onClick.AddListener(Refresh);
        return button;
    }

    // Rows top-down, stacked by the shared panel; the reset options are stacked only while
    // expanded (like the context menu's layout switching).
    private void ApplyLayout()
    {
        var rows = new List<CADMenuPanel.Row>
        {
            new(TitleBarHeight, SectionSpacing, titleBar),

            new(SectionHeight, scopeSection),
            new(TextHeight, scopeText),
            new(ButtonHeight, SectionSpacing, scopeButton),

            new(SectionHeight, cadenSection),
            new(ButtonHeight, SectionSpacing, cadenButton),

            new(SectionHeight, viewSection),
            new(TextHeight, displayLabel),
            new(ButtonHeight, displayButtons[CADDisplayMode.Shaded], displayButtons[CADDisplayMode.Edges],
                displayButtons[CADDisplayMode.Wireframe]),
            new(ButtonHeight, SectionSpacing, outlineLabel, outlineButton),

            new(SectionHeight, resetSection),
            new(ButtonHeight, resetExpanded ? CADMenuPanel.RowSpacing : SectionSpacing, resetButton),
        };
        if (resetExpanded)
        {
            rows.Add(new(ButtonHeight, resetObjectButton));
            rows.Add(new(ButtonHeight, resetAssemblyButton));
            rows.Add(new(ButtonHeight, SectionSpacing, resetModelButton));
        }
        rows.Add(new(SectionHeight, interfaceSection));
        rows.Add(new(ButtonHeight, SectionSpacing, scaleLabel, scaleDownButton, scaleValue, scaleUpButton));
        rows.Add(new(ButtonHeight, closeButton));

        panel.Stack(rows);
    }
}
