using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The application's single global menu: Scope (current scope + one context-aware
/// Enter/Exit Assembly button), CADEN (show/hide the assistant), View (display mode buttons,
/// outline toggle), Model (Manipulate model / Stop manipulating, and the Reset options shown
/// under "Reset ▼": selected, assembly, all sizes, everything), Close. Its corners resize it
/// (the UI scale, CADUISettings.UiScale). Buttons carry
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
/// HideMainMenu are the entry points. Every show respawns it ~0.85 m (at least
/// CADMenuPanel.MinMenuDistance) in front of the head, a little below eye level, facing the user (never head-locked). While open it stays put,
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
    [SerializeField, Range(0.4f, 1.2f)] private float spawnDistance = 0.85f;
    [Tooltip("How far below eye level it appears (m).")]
    [SerializeField] private float spawnDrop = 0.12f;

    [Header("Activation")]
    [Tooltip("Development safety net: show the menu at startup.")]
    [SerializeField] private bool startVisible;

    // Layout in canvas units (1 unit = 1 mm at 100% UI scale); style guide sizes (CADMenuPanel).
    private const float PanelWidth = 440f;
    private const float HeaderHeight = 64f;
    private const float LogoSize = 56f;
    private const float SectionHeight = 28f;
    private const float TextHeight = 30f;
    private const float ButtonHeight = CADMenuPanel.ControlHeight;
    private const float SectionSpacing = CADMenuPanel.SectionSpacing;
    private const string LogoResource = "CADVision/CADVisionLogo";

    private CADVisionManipulationService manipulationService;
    private CADUISettings settings;
    private CADPointerInteraction pointerInteraction;

    private CADMenuPanel panel;
    private CADMenuPanel codePanel;
    private GameObject panelRoot; // panel.Root.
    private RectTransform header; // Logo + wordmark + subtitle; also a grab region of the border drag.
    private Text title;           // "CADVision" wordmark.

    private Text scopeSection, roomSection, roomStatus, cadenSection, viewSection, modelSection, interfaceSection;
    private Text scopeText, displayLabel, outlineLabel, scaleLabel, scaleValue;
    private readonly Dictionary<CADDisplayMode, Button> displayButtons = new();
    private Button scopeButton, hostRoomButton, hostVirtualRoomButton, joinRoomButton, leaveRoomButton;
    private Button cadenButton, outlineButton;
    private Button manipulateButton, resetButton, resetObjectButton, resetScaleButton, resetAssemblyButton, resetModelButton;
    private Button closeButton;
    private bool resetExpanded;
    private CADMultiplayerCoordinator multiplayer;
    private Text enteredRoomCode;
    private Button confirmRoomCode;
    private string roomCode = string.Empty;


    public bool IsOpen => panel != null && panel.IsOpen || codePanel != null && codePanel.IsOpen;
    public bool IsDragging => panel != null && panel.IsDragging || codePanel != null && codePanel.IsDragging;
    public bool IsResetExpanded => resetExpanded;
    public Transform PanelTransform => codePanel != null && codePanel.IsOpen
        ? codePanel.Root.transform : panelRoot != null ? panelRoot.transform : null;
    /// <summary>The shared panel this menu is built from (same class as the context menu's).</summary>
    public CADMenuPanel Panel => panel;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        settings = GetComponent<CADUISettings>();
        multiplayer = GetComponent<CADMultiplayerCoordinator>();
        if (multiplayer == null) multiplayer = gameObject.AddComponent<CADMultiplayerCoordinator>();
        pointerInteraction = GetComponent<CADPointerInteraction>();
        BuildPanel();
        BuildCodePanel();
        panel.Hide();
        codePanel.Hide();
        if (startVisible)
            ShowMainMenu();
    }

    private void OnEnable()
    {
        panel?.EnableBorderDrag(pointerInteraction);
        codePanel?.EnableBorderDrag(pointerInteraction);
    }

    private void OnDisable()
    {
        panel?.DisableBorderDrag();
        codePanel?.DisableBorderDrag();
    }

    private void OnDestroy()
    {
        panel?.Destroy();
        codePanel?.Destroy();
    }

    // ---------------- Show / hide ----------------

    public void ToggleMainMenu()
    {
        if (IsOpen) HideMainMenu();
        else ShowMainMenu();
    }

    /// <summary>Shows the menu in front of the user; if already open, brings it back in front.</summary>
    public void ShowMainMenu()
    {
        codePanel?.Hide();
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
        codePanel?.Hide();
    }

    private void LateUpdate()
    {
        if (codePanel != null && codePanel.IsOpen)
        {
            codePanel.UpdateDrag();
            return;
        }
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

        // Never closer than the shared minimum menu distance.
        float distance = Mathf.Max(spawnDistance, CADMenuPanel.MinMenuDistance);
        Vector3 position = head.position + forward * distance + Vector3.down * spawnDrop;
        panelRoot.transform.SetPositionAndRotation(position,
            Quaternion.LookRotation(position - head.position, Vector3.up));
    }

    // ---------------- State → UI ----------------

    public void Refresh()
    {
        SetText(scopeText, $"Scope: {manipulationService.CurrentScopeDisplayName}");
        SetText(roomStatus, multiplayer.IsInRoom
            ? $"Room: {multiplayer.RoomCode} ({multiplayer.ParticipantCount}/2)\n{multiplayer.Status}"
            : $"Room: {multiplayer.Status}");
        CADMenuPanel.SetInteractable(hostRoomButton, !multiplayer.IsInRoom &&
            multiplayer.State != CADMultiplayerCoordinator.RoomState.Connecting);
        CADMenuPanel.SetInteractable(hostVirtualRoomButton, !multiplayer.IsInRoom &&
            multiplayer.State != CADMultiplayerCoordinator.RoomState.Connecting);
        CADMenuPanel.SetInteractable(joinRoomButton, !multiplayer.IsInRoom &&
            multiplayer.State != CADMultiplayerCoordinator.RoomState.Connecting);
        CADMenuPanel.SetInteractable(leaveRoomButton, multiplayer.IsInRoom);

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
        CADMenuPanel.SetInteractable(resetObjectButton, !multiplayer.IsInRoom && manipulationService.GetSelectedIds().Count > 0);
        CADMenuPanel.SetInteractable(resetScaleButton, !multiplayer.IsInRoom && (modelMode
            ? manipulationService.ModelRoot != null
            : manipulationService.GetSelectedIds().Count > 0));
        CADMenuPanel.SetInteractable(resetAssemblyButton, !multiplayer.IsInRoom && ResetAssemblyTarget() != null);
        CADMenuPanel.SetInteractable(resetModelButton, !multiplayer.IsInRoom && manipulationService.ModelRoot != null);

        Vector3 scale = Vector3.one * settings.UiScale;
        if (panelRoot.transform.localScale != scale)
            panelRoot.transform.localScale = scale;
    }

    private static void SetText(Text text, string value)
    {
        if (text.text != value)
            text.text = value;
    }

    // The assembly the user is in (current scope); none at model scope. Same rule as the
    // context menus.
    private string ResetAssemblyTarget() => manipulationService.CurrentScopeId;

    // ---------------- Actions ----------------

    private void OnScopeButton()
    {
        if (!manipulationService.IsAtRootScope)
            manipulationService.ExitScope();
        else if (manipulationService.TryGetEnterableSelection(out string assemblyId))
            manipulationService.EnterScope(assemblyId);
    }

    private void PromptJoinRoom()
    {
        roomCode = string.Empty;
        RefreshCodePanel();
        codePanel.Root.transform.SetPositionAndRotation(
            panelRoot.transform.position, panelRoot.transform.rotation);
        codePanel.Root.transform.localScale = Vector3.one * settings.UiScale;
        panel.Hide();
        codePanel.Show();
    }

    private void AppendRoomCode(char value)
    {
        if (roomCode.Length >= 12) return;
        roomCode += value;
        RefreshCodePanel();
    }

    private void BackspaceRoomCode()
    {
        if (roomCode.Length == 0) return;
        roomCode = roomCode.Substring(0, roomCode.Length - 1);
        RefreshCodePanel();
    }

    private void RefreshCodePanel()
    {
        SetText(enteredRoomCode, "Code: " + (roomCode.Length == 0 ? "------" : roomCode));
        CADMenuPanel.SetInteractable(confirmRoomCode, roomCode.Length >= 4);
    }

    private void SubmitRoomCode()
    {
        if (roomCode.Length < 4) return;
        string code = roomCode;
        ShowMainMenu();
        multiplayer.JoinRoom(code);
    }

    private void BuildCodePanel()
    {
        codePanel = new CADMenuPanel("CAD Room Code", PanelWidth, CADMenuStyle.Default);
        codePanel.CloseRequested = HideMainMenu;
        Text heading = codePanel.CreateText("Title", "Join shared room",
            CADMenuPanel.FontSize + 2, FontStyle.Bold);
        codePanel.AddGrabRegion(heading.rectTransform);
        enteredRoomCode = codePanel.CreateText("Entered code", "Code: ------",
            CADMenuPanel.FontSize + 4, FontStyle.Bold);
        Text hint = codePanel.CreateText("Hint", "Tap the code shown on the host headset");
        var rows = new List<CADMenuPanel.Row>
        {
            new(TitleHeight, heading),
            new(ButtonHeight, enteredRoomCode),
            new(TextHeight, SectionSpacing, hint)
        };
        const string keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        for (int row = 0; row < 6; row++)
        {
            var buttons = new Component[6];
            for (int column = 0; column < 6; column++)
            {
                char key = keys[row * 6 + column];
                buttons[column] = codePanel.CreateButton(key.ToString(),
                    () => AppendRoomCode(key));
            }
            rows.Add(new CADMenuPanel.Row(ButtonHeight, buttons));
        }
        Button backspace = codePanel.CreateButton("Delete", BackspaceRoomCode);
        Button clear = codePanel.CreateButton("Clear", () =>
        {
            roomCode = string.Empty;
            RefreshCodePanel();
        });
        confirmRoomCode = codePanel.CreateButton("Join", SubmitRoomCode);
        Button cancel = codePanel.CreateButton("Cancel", ShowMainMenu);
        rows.Add(new CADMenuPanel.Row(ButtonHeight, backspace, clear, confirmRoomCode, cancel));
        codePanel.Stack(rows);
        RefreshCodePanel();
    }

    private void ToggleReset()
    {
        resetExpanded = !resetExpanded;
        ApplyLayout();
    }

    // The selection: parts reset, assemblies reset with everything in them.
    private void ResetObjectAction()
    {
        manipulationService.ResetSelected();
        CollapseReset();
    }

    // Every resized object and the model root back to their original size; nothing moves.
    private void ResetScaleAction()
    {
        manipulationService.ResetAllScales();
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
        // Corner resize sets the UI scale (applied at once so the opposite corner stays put).
        panel.EnableResize(() => settings.UiScale, size =>
        {
            settings.SetUiScale(size);
            panelRoot.transform.localScale = Vector3.one * settings.UiScale;
        });

        BuildHeader();

        scopeSection = Section("Scope");
        scopeText = panel.CreateText("Scope Value", "Scope: Full model", CADMenuPanel.FontSize, FontStyle.Normal,
            TextAnchor.MiddleLeft);
        scopeButton = AddButton("Enter assembly", OnScopeButton,
            "Enter the selected assembly, or go back up one level.");

        roomSection = Section("Shared room");
        roomStatus = panel.CreateText("Room status", "Room: Offline");
        hostRoomButton = AddButton("Host passthrough", multiplayer.HostRoom,
            "Create a shared room for two headsets in the same physical space.");
        hostVirtualRoomButton = AddButton("Host virtual", multiplayer.HostVirtualRoom,
            "Create a virtual room for headsets in different locations.");
        joinRoomButton = AddButton("Join", PromptJoinRoom,
            "Tap the code shown on the host headset using the VR keypad.");
        leaveRoomButton = AddButton("Leave", multiplayer.LeaveRoom,
            "Disconnect from the shared room.");

        cadenSection = Section("CADEN");
        cadenButton = AddButton("CADEN: Off", settings.ToggleCaden, "Show or hide the CADEN design assistant.");

        viewSection = Section("View");
        displayLabel = Label("Display");
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
        outlineLabel = Label("Outline");
        outlineButton = AddButton("On", () => settings.SetOutlineEnabled(!settings.OutlineEnabled),
            "Show or hide the selection outline.");

        modelSection = Section("Model");
        manipulateButton = AddButton("Manipulate model", ToggleModelManipulation,
            "Move, rotate, or scale the entire CAD model.");
        resetButton = AddButton("Reset ▼", ToggleReset, "Show the reset options.");
        resetObjectButton = AddButton("Reset selected", ResetObjectAction,
            "Put the selected parts back: original positions, rotations and sizes.");
        resetScaleButton = AddButton("Reset all sizes", ResetScaleAction,
            "Undo all resizing of the model and its parts. Nothing moves.");
        resetAssemblyButton = AddButton("Reset assembly", ResetAssemblyAction,
            "Put the assembly you're in and all its parts back: original positions, rotations and sizes.");
        resetModelButton = AddButton("Reset everything", ResetModelAction,
            "Undo every change: all parts back in place, the model back in front of you at its original size.");

        closeButton = AddButton("Close", HideMainMenu);

        ApplyLayout();
    }

    // Section heading: bold white (style guide 20–22).
    private Text Section(string heading) =>
        panel.CreateText(heading, heading, CADMenuPanel.SectionSize, FontStyle.Bold, TextAnchor.LowerLeft);

    // Control label: muted secondary text, left-aligned beside or above its control.
    private Text Label(string value) =>
        panel.CreateText(value + " Label", value, CADMenuPanel.SecondarySize, FontStyle.Normal, TextAnchor.MiddleLeft,
            panel.SecondaryTextColor);

    // Branding for the app's main menu (style guide): the original logo (proportions kept, clear
    // space around it), the CADVision wordmark and a quiet feature subtitle.
    private void BuildHeader()
    {
        header = panel.CreateGroup("Header");
        panel.AddGrabRegion(header);

        float textLeft = 0f;
        var logo = Resources.Load<Texture2D>(LogoResource);
        if (logo != null)
        {
            float aspect = logo.height > 0 ? (float)logo.width / logo.height : 1f;
            RawImage image = panel.CreateRawImage(header, "Logo", logo);
            PlaceInHeader(image.rectTransform, 0f, 0f, LogoSize * aspect, LogoSize, verticalCenter: true);
            textLeft = LogoSize * aspect + LogoSize * 0.25f; // Clear space ≈ a quarter of its height.
        }
        else
        {
            Debug.LogWarning($"[CADMainMenu] Logo '{LogoResource}' not found; header shows the wordmark only.");
        }

        title = panel.CreateText(header, "Title", "CADVision", CADMenuPanel.TitleSize + 2, FontStyle.Bold,
            TextAnchor.LowerLeft);
        PlaceInHeader(title.rectTransform, textLeft, 0f, 300f, 36f, verticalCenter: false);
        Text subtitle = panel.CreateText(header, "Subtitle", "Main menu", CADMenuPanel.SecondarySize, FontStyle.Normal,
            TextAnchor.UpperLeft, panel.SecondaryTextColor);
        PlaceInHeader(subtitle.rectTransform, textLeft, 38f, 300f, 24f, verticalCenter: false);
    }

    // Top-left based placement inside the header group (canvas units).
    private static void PlaceInHeader(RectTransform rect, float x, float top, float width, float height, bool verticalCenter)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, verticalCenter ? 0.5f : 1f);
        rect.pivot = new Vector2(0f, verticalCenter ? 0.5f : 1f);
        rect.anchoredPosition = new Vector2(x, verticalCenter ? 0f : -top);
        rect.sizeDelta = new Vector2(width, height);
    }

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
            new(HeaderHeight, SectionSpacing, header),

            new(SectionHeight, scopeSection),
            new(TextHeight, scopeText),
            new(ButtonHeight, SectionSpacing, scopeButton),

            new(SectionHeight, roomSection),
            new(TextHeight * 2f, roomStatus),
            new(ButtonHeight, SectionSpacing, hostRoomButton, hostVirtualRoomButton),
            new(ButtonHeight, joinRoomButton, leaveRoomButton),

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
            rows.Add(new(ButtonHeight, resetAssemblyButton));
            rows.Add(new(ButtonHeight, resetScaleButton));
            rows.Add(new(ButtonHeight, SectionSpacing, resetModelButton));
        }
        rows.Add(new(ButtonHeight, closeButton));

        panel.Stack(rows);
    }
}
