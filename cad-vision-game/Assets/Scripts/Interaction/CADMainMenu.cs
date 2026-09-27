using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The application's single global menu: Scope (current scope + one context-aware
/// Enter/Exit Assembly button), CADEN (placeholder toggle), View (display mode buttons,
/// outline toggle), Model (Manipulate model / Stop manipulating, and the Reset options shown
/// under "Reset ▼": object, scale, assembly, model), Interface (UI scale), Close. Buttons carry
/// hover tooltips (CADMenuPanel).
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
/// except that pressing and holding its border or its header with trigger or pinch drags it
/// rigidly with the pointer (CADMenuPanel's shared border drag; the plain-text header is an
/// extra grab region). It stays open across model replacement; all state
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
    private const float TitleHeight = 32f;
    private const float SectionHeight = 26f;
    private const float TextHeight = 32f;
    private const float ButtonHeight = 44f;
    private const float SectionSpacing = 16f;

    private CADVisionManipulationService manipulationService;
    private CADUISettings settings;
    private CADPointerInteraction pointerInteraction;

    private CADMenuPanel panel;
    private GameObject panelRoot; // panel.Root.
    private Text title; // Plain header text; also a grab region of the border drag.
    private Text scopeSection, cadenSection, viewSection, modelSection, interfaceSection;
    private Text scopeText, displayLabel, outlineLabel, scaleLabel, scaleValue;
    private readonly Dictionary<CADDisplayMode, Button> displayButtons = new();
    private Button scopeButton, cadenButton, outlineButton;
    private Button manipulateButton, resetButton, resetObjectButton, resetScaleButton, resetAssemblyButton, resetModelButton;
    private Button scaleDownButton, scaleUpButton, closeButton;
    private bool resetExpanded;


    public bool IsOpen => panel != null && panel.IsOpen;
    public bool IsDragging => panel != null && panel.IsDragging;
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

    private void OnEnable() => panel?.EnableBorderDrag(pointerInteraction);

    private void OnDisable() => panel?.DisableBorderDrag();

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
        panel.EndDrag();
        resetExpanded = false;
        ApplyLayout();
        Refresh();
        PlaceInFrontOfHead();
        panel.Show();
        Debug.Log("[CADMainMenu] Shown.");
    }

    public void HideMainMenu()
    {
        resetExpanded = false;
        if (IsOpen)
            Debug.Log("[CADMainMenu] Hidden.");
        panel.Hide();
    }

    private void LateUpdate()
    {
        if (!IsOpen)
            return;

        panel.UpdateDrag();
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

        bool modelMode = manipulationService.IsModelManipulationActive;
        CADMenuPanel.SetLabel(manipulateButton, modelMode ? "Stop manipulating" : "Manipulate model");
        CADMenuPanel.SetInteractable(manipulateButton, modelMode || manipulationService.ModelRoot != null);
        panel.SetSelected(manipulateButton, modelMode);

        CADMenuPanel.SetLabel(resetButton, resetExpanded ? "Reset ▲" : "Reset ▼");
        CADMenuPanel.SetInteractable(resetObjectButton, manipulationService.GetSelectedIds().Count > 0);
        CADMenuPanel.SetInteractable(resetScaleButton, modelMode
            ? manipulationService.ModelRoot != null
            : manipulationService.GetSelectedIds().Count > 0);
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

    // Model mode: the model root's review scale; else the selection's (one object or the
    // selected transform roots). Position and rotation stay.
    private void ResetScaleAction()
    {
        if (manipulationService.IsModelManipulationActive)
            manipulationService.ResetModelScale();
        else
        {
            List<string> selected = manipulationService.GetSelectedIds();
            if (selected.Count == 1)
                manipulationService.ResetObjectScale(selected[0]);
            else if (selected.Count > 1)
                manipulationService.ResetSelectedScale();
        }
        CollapseReset();
    }

    private void ToggleModelManipulation()
    {
        if (manipulationService.IsModelManipulationActive)
            manipulationService.EndModelManipulation();
        else
            manipulationService.BeginModelManipulation();
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
        panel.CloseRequested = HideMainMenu; // Another menu opened (single-menu rule).

        // Title bar: a normal panel button that is also a grab region of the shared border drag.
        // Header: plain text (not a button) that, like the border, can be grabbed to move the menu.
        title = panel.CreateText("Title", "Main menu", CADMenuPanel.FontSize + 2, FontStyle.Bold);
        panel.AddGrabRegion(title.rectTransform);

        scopeSection = Section("Scope");
        scopeText = panel.CreateText("Scope Value", "Scope: Full model");
        scopeButton = AddButton("Enter assembly", OnScopeButton,
            "Enter the selected assembly, or go back up one level.");

        cadenSection = Section("CADEN");
        cadenButton = AddButton("CADEN: Off", settings.ToggleCaden, "CADEN design assistant (placeholder toggle).");

        viewSection = Section("View");
        displayLabel = panel.CreateText("Display Label", "Display");
        foreach (CADDisplayMode mode in new[] { CADDisplayMode.Shaded, CADDisplayMode.Edges, CADDisplayMode.Wireframe })
        {
            CADDisplayMode captured = mode;
            displayButtons[mode] = AddButton(mode.ToString(), () => settings.SetDisplayMode(captured), mode switch
            {
                CADDisplayMode.Edges => "Shaded surfaces with dark feature edges.",
                CADDisplayMode.Wireframe => "See-through faces with visible edges.",
                _ => "Normal shaded surfaces.",
            });
        }
        outlineLabel = panel.CreateText("Outline Label", "Outline");
        outlineButton = AddButton("On", () => settings.SetOutlineEnabled(!settings.OutlineEnabled),
            "Show or hide the selection outline.");

        modelSection = Section("Model");
        manipulateButton = AddButton("Manipulate model", ToggleModelManipulation,
            "Move, rotate, or scale the entire CAD model.");
        resetButton = AddButton("Reset ▼", ToggleReset, "Show the reset options.");
        resetObjectButton = AddButton("Reset object", ResetObjectAction,
            "Restore the selected object to its original assembly transform.");
        resetScaleButton = AddButton("Reset scale", ResetScaleAction,
            "Restore the original scale without changing position or rotation.");
        resetAssemblyButton = AddButton("Reset assembly", ResetAssemblyAction,
            "Restore the assembly and its parts to their original transforms.");
        resetModelButton = AddButton("Reset model", ResetModelAction,
            "Restore the whole model and every part to the review pose.");

        interfaceSection = Section("Interface");
        scaleLabel = panel.CreateText("Scale Label", "UI scale");
        scaleDownButton = AddButton("-", () => settings.StepUiScale(-1), "Make the main menu smaller.");
        scaleValue = panel.CreateText("Scale Value", "100%", CADMenuPanel.FontSize, FontStyle.Bold);
        scaleUpButton = AddButton("+", () => settings.StepUiScale(1), "Make the main menu larger.");

        closeButton = AddButton("Close", HideMainMenu);

        ApplyLayout();
    }

    private Text Section(string title) => panel.CreateText(title, title, CADMenuPanel.FontSize - 2, FontStyle.Bold);

    // Every button refreshes the menu after its action (labels, selected and enabled states).
    private Button AddButton(string label, UnityEngine.Events.UnityAction onClick, string tooltip = null)
    {
        Button button = panel.CreateButton(label, onClick, tooltip);
        button.onClick.AddListener(Refresh);
        return button;
    }

    // Rows top-down, stacked by the shared panel; the reset options are stacked only while
    // expanded (like the context menu's layout switching).
    private void ApplyLayout()
    {
        var rows = new List<CADMenuPanel.Row>
        {
            new(TitleHeight, CADMenuPanel.RowSpacing, title),

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

            new(SectionHeight, modelSection),
            new(ButtonHeight, manipulateButton),
            new(ButtonHeight, resetExpanded ? CADMenuPanel.RowSpacing : SectionSpacing, resetButton),
        };
        if (resetExpanded)
        {
            rows.Add(new(ButtonHeight, resetObjectButton));
            rows.Add(new(ButtonHeight, resetScaleButton));
            rows.Add(new(ButtonHeight, resetAssemblyButton));
            rows.Add(new(ButtonHeight, SectionSpacing, resetModelButton));
        }
        rows.Add(new(SectionHeight, interfaceSection));
        rows.Add(new(ButtonHeight, SectionSpacing, scaleLabel, scaleDownButton, scaleValue, scaleUpButton));
        rows.Add(new(ButtonHeight, closeButton));

        panel.Stack(rows);
    }
}
