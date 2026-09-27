using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating context menu for the selected CAD object, opened by
/// CADPointerInteraction.ContextMenuRequested. Presentation + wiring only: every button calls
/// a public CADVisionManipulationService method; nothing here edits CAD transforms, parents,
/// visibility or selection directly.
///
/// The panel is a CADMenuPanel (the shared menu construction, also used by CADMainMenu): a
/// world-space uGUI canvas driven through the Meta Interaction SDK (RayInteractable →
/// PointableCanvas → PointableCanvasModule), so any ray + select source works (either
/// controller's trigger, either hand's pinch). It is never a CAD object; CADPointerInteraction
/// classifies it as UI, so clicking it never deselects.
///
/// Context-aware: the menu only shows what applies to the target right now. Under the title,
/// a non-interactive line shows the current interaction scope ("Scope: Full model" /
/// "Scope: <assembly>", from the service), except in the model menu. The scope line and the
/// navigation buttons directly below it form the hierarchy section, always at the top and
/// followed by a gap: Exit Assembly (one logical level up, ExitScope; only when the scope is
/// not the model root), then Enter Assembly (only for an enterable assembly). Both can show
/// for a nested assembly: they go in opposite directions.
/// - Part: [Exit Assembly] | Focus / Clear Focus, Detach or Reattach (only when possible),
///   Reset Object, Multi-Select, Main Menu, Close.
/// - Assembly: [Exit Assembly] [Enter Assembly] | Focus / Clear Focus, Detach or Reattach
///   (subassemblies), Reset Assembly, Multi-Select, Main Menu, Close.
/// - Selection (clicked a member of a multi-selection): [Exit Assembly] | Focus Selection /
///   Clear Focus, Reset Selected, Edit Selection, Clear Selection, Close.
/// - Picking (multi-select mode): [Exit Assembly] | Done, Focus Selection / Clear Focus, Reset
///   Selected, Clear Selection.
/// - Model (whole-model manipulation): Done, Reset Scale, Reset Model.
/// App-wide actions (display, outline, CADEN, UI scale, Manipulate model, model reset outside
/// model mode) live in the Main Menu. Buttons carry hover tooltips.
///
/// Every variant can be moved by its border (CADMenuPanel's shared border drag); once moved it
/// stays where it was dropped until it closes, and the next open is placed automatically.
///
/// Single-menu rule (CADMenuPanel): opening this closes any other open menu and vice versa.
/// The picking and model menus open when their mode starts; if another menu (e.g. the Main
/// Menu) takes over while the mode lasts, they reopen only once no other menu is open.
///
/// Placement: every variant opens beside its target's visible bounds (object, selection or
/// model; never a Transform origin) so the part never blocks it, at least
/// CADMenuPanel.MinMenuDistance from the user, via CADMenuPanel.PlaceBesideBounds.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADContextMenu : MonoBehaviour
{
    private enum MenuAction
    {
        EnterAssembly, Focus, DetachOrReattach, ResetObject, ResetAssembly, MultiSelect, MainMenu, Close,
        Done, FocusSelection, ResetSelected, EditSelection, ClearSelection,
        ModelDone, ResetScale, ResetModel, ExitAssembly, ResetSize,
    }


    // Layout in canvas units (1 unit = 1 mm); style guide sizes (CADMenuPanel).
    private const float PanelWidth = 320f;
    private const float TitleHeight = 36f;
    private const float ScopeHeight = 26f;
    private const float SectionGap = CADMenuPanel.SectionSpacing; // Separates the hierarchy section from the actions.
    private const float ButtonHeight = CADMenuPanel.ControlHeight;

    private static readonly Dictionary<MenuAction, string> Labels = new()
    {
        { MenuAction.EnterAssembly, "Enter assembly" },
        { MenuAction.Focus, "Focus" },
        { MenuAction.DetachOrReattach, "Detach" },
        { MenuAction.ResetObject, "Reset part" },
        { MenuAction.ResetSize, "Reset size" },
        { MenuAction.ResetAssembly, "Reset assembly" },
        { MenuAction.MultiSelect, "Multi-select" },
        { MenuAction.MainMenu, "Main menu" },
        { MenuAction.Close, "Close" },
        { MenuAction.Done, "Done" },
        { MenuAction.FocusSelection, "Focus selection" },
        { MenuAction.ResetSelected, "Reset selected" },
        { MenuAction.EditSelection, "Edit selection" },
        { MenuAction.ClearSelection, "Clear selection" },
        { MenuAction.ModelDone, "Done" },
        { MenuAction.ResetScale, "Reset all sizes" },
        { MenuAction.ResetModel, "Reset everything" },
        { MenuAction.ExitAssembly, "Exit assembly" },
    };

    private static readonly Dictionary<MenuAction, string> Tooltips = new()
    {
        { MenuAction.EnterAssembly, "Inspect and interact with this assembly's children." },
        { MenuAction.Focus, "Emphasize this selection and ghost the rest of the model." },
        { MenuAction.DetachOrReattach, "Move this part independently from its assembly." },
        { MenuAction.ResetObject, "Put this back where it was: original position, rotation and size." },
        { MenuAction.ResetSize, "Back to the original size. It stays where it is." },
        { MenuAction.ResetAssembly, "Put the assembly you're in and all its parts back: original positions, rotations and sizes." },
        { MenuAction.MultiSelect, "Select multiple parts and move them together." },
        { MenuAction.MainMenu, "Open the main menu." },
        { MenuAction.FocusSelection, "Emphasize the selection and ghost the rest of the model." },
        { MenuAction.ResetSelected, "Put the selected parts back: original positions, rotations and sizes." },
        { MenuAction.EditSelection, "Add or remove parts from this selection." },
        { MenuAction.ClearSelection, "Deselect everything." },
        { MenuAction.Done, "Finish picking; the selection stays." },
        { MenuAction.ModelDone, "Stop manipulating the whole model." },
        { MenuAction.ResetScale, "Undo all resizing of the model and its parts. Nothing moves." },
        { MenuAction.ResetModel, "Undo every change: all parts back in place, the model back in front of you at its original size." },
        { MenuAction.ExitAssembly, "Go up one assembly level." },
    };

    private const string ClearFocusTooltip = "Show the whole model normally again.";
    private const string ReattachTooltip = "Return this part to its assembly.";

    private CADVisionManipulationService manipulationService;
    private CADPointerInteraction pointerInteraction;
    private CADMainMenu mainMenu;

    private CADMenuPanel panel;
    private GameObject panelRoot; // panel.Root.
    private Text titleText;
    private Text scopeText;          // "Scope: ..." under the title (not interactive).
    private bool multiMode;          // Panel shows the multi-select (picking) menu.
    private bool groupMode;          // Panel shows the selection menu for an existing multi-selection.
    private bool modelMode;          // Panel shows the model menu (whole-model manipulation).
    private string multiSignature;   // Selected IDs the selection menu was last placed for.
    private readonly Dictionary<MenuAction, Button> buttons = new();
    private readonly List<MenuAction> shown = new();

    private string targetId;
    private CADObject target;
    private bool wasModelMode;       // Mode edges: a mode that just started always shows its menu.
    private bool wasMultiMode;

    public bool IsOpen => panelRoot != null && panelRoot.activeSelf;
    /// <summary>The shared panel (placement and tooltips are CADMenuPanel's).</summary>
    public CADMenuPanel Panel => panel;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        pointerInteraction = GetComponent<CADPointerInteraction>();
        mainMenu = GetComponent<CADMainMenu>();

        BuildPanel();
        Hide("initial");
    }

    private void OnEnable()
    {
        if (pointerInteraction != null)
            pointerInteraction.ContextMenuRequested += Open;
        else
            Debug.LogWarning("[CADContextMenu] No CADPointerInteraction on this object; menu will never open.");
        panel?.EnableBorderDrag(pointerInteraction);
    }

    private void OnDisable()
    {
        if (pointerInteraction != null)
            pointerInteraction.ContextMenuRequested -= Open;
        panel?.DisableBorderDrag();
        Hide("component disabled");
    }

    private void OnDestroy() => panel?.Destroy();

    // ---------------- Open / close ----------------

    private void Open(CADContextMenuRequest request)
    {
        if (manipulationService.IsMultiSelectActive || manipulationService.IsModelManipulationActive)
            return; // The selection / model menu is shown instead.

        CADObject selected = manipulationService.GetSelectedObjects()
            .FirstOrDefault(o => o.id == request.TargetId);
        if (selected == null)
            return;

        // Clicked one object of a multi-selection: menu for the whole selection.
        if (manipulationService.GetSelectedIds().Count > 1)
        {
            OpenGroup(request);
            return;
        }

        // A newer request replaces the current menu (placed automatically again).
        panel.ForgetMove();
        multiMode = false;
        groupMode = false;
        modelMode = false;
        targetId = request.TargetId;
        target = selected;
        titleText.text = selected.name;

        Refresh(forceLayout: true);
        PlaceObject(request);
        panel.Show();
        Debug.Log($"[CADContextMenu] Opened for '{targetId}' ({string.Join(", ", shown)}).");
    }

    private void Hide(string reason)
    {
        bool wasOpen = IsOpen;
        panel?.Hide(); // Interactable off first: a hidden panel never swallows ray clicks.

        if (wasOpen)
            Debug.Log($"[CADContextMenu] Closed ({reason}).");

        targetId = null;
        target = null;
        multiMode = false;
        groupMode = false;
        modelMode = false;
        multiSignature = null;
    }

    private void OpenGroup(CADContextMenuRequest request)
    {
        panel.ForgetMove();
        multiMode = false;
        groupMode = true;
        modelMode = false;
        targetId = request.TargetId;
        target = null;
        multiSignature = SelectionSignature(out int count);
        titleText.text = $"{count} selected";
        Refresh(forceLayout: true);
        PlaceSelection();
        panel.Show();
        Debug.Log($"[CADContextMenu] Opened selection menu ({count} selected).");
    }

    private string SelectionSignature(out int count)
    {
        List<string> ids = manipulationService.GetSelectedIds();
        ids.Sort(StringComparer.Ordinal);
        count = ids.Count;
        return string.Join("|", ids);
    }

    private bool IsMoving() => pointerInteraction != null && pointerInteraction.IsManipulating;

    private void OpenModel()
    {
        panel.ForgetMove();
        multiMode = false;
        groupMode = false;
        modelMode = true;
        multiSignature = null;
        targetId = null;
        target = null;
        titleText.text = "Manipulate model";
        Refresh(forceLayout: true);
        PlaceModel();
        panel.Show();
        Debug.Log("[CADContextMenu] Opened model menu.");
    }

    private void OpenMulti()
    {
        panel.ForgetMove();
        multiMode = true;
        groupMode = false;
        modelMode = false;
        multiSignature = null;
        targetId = null;
        target = null;
        UpdateMulti(forcePlace: true);
        panel.Show();
        Debug.Log("[CADContextMenu] Opened selection menu (multi-select).");
    }

    // Keeps the selection menu's title/buttons current; re-places it when the set changes.
    private void UpdateMulti(bool forcePlace = false)
    {
        List<string> ids = manipulationService.GetSelectedIds();
        ids.Sort(StringComparer.Ordinal);
        string signature = string.Join("|", ids);
        titleText.text = $"Multi-select: {ids.Count} selected";
        bool relaid = Refresh(forceLayout: forcePlace);
        if (forcePlace || relaid || signature != multiSignature)
        {
            PlaceSelection();
            multiSignature = signature;
        }
    }

    private void LateUpdate()
    {
        if (IsOpen)
            panel.UpdateDrag();

        // A mode that just started shows its menu (closing any other); later, while the mode
        // lasts, its menu comes back only when no other menu is open (single-menu rule).
        bool modelStarted = manipulationService.IsModelManipulationActive && !wasModelMode;
        bool multiStarted = manipulationService.IsMultiSelectActive && !wasMultiMode;
        wasModelMode = manipulationService.IsModelManipulationActive;
        wasMultiMode = manipulationService.IsMultiSelectActive;
        bool otherMenuOpen = CADMenuPanel.ActivePanel != null && CADMenuPanel.ActivePanel != panel;

        // Model mode drives the model menu for as long as it lasts, hidden while the model is
        // moved or scaled (it reappears at the model's new position).
        if (manipulationService.IsModelManipulationActive)
        {
            if (IsMoving())
            {
                if (IsOpen)
                    Hide("model is being moved");
            }
            else if (!IsOpen || !modelMode)
            {
                if (modelStarted || !otherMenuOpen)
                    OpenModel();
            }
            return;
        }

        if (IsOpen && modelMode)
        {
            Hide("model manipulation ended");
            return;
        }

        // Multi-select mode drives the picking menu: shown for as long as the mode lasts, except
        // while the group is being dragged (it reappears at the group's new position).
        if (manipulationService.IsMultiSelectActive)
        {
            if (IsMoving())
            {
                if (IsOpen)
                    Hide("group is being moved");
            }
            else if (!IsOpen || !multiMode)
            {
                if (multiStarted || !otherMenuOpen)
                    OpenMulti();
            }
            else
                UpdateMulti();
            return;
        }

        if (IsOpen && multiMode)
        {
            Hide("multi-select ended");
            return;
        }

        if (!IsOpen)
            return;

        if (groupMode)
        {
            // The selection menu describes one exact set; any change, or a drag, closes it.
            if (SelectionSignature(out int count) != multiSignature || count < 2)
                Hide("selection changed");
            else if (IsMoving())
                Hide("selection is being moved");
            else if (Refresh())
                PlaceSelection();
            return;
        }

        // Lifecycle: close when the menu no longer describes the current selection.
        if (target == null)
        {
            Hide("target destroyed or model replaced");
            return;
        }

        if (!manipulationService.GetSelectedObjects().Any(o => o.id == targetId))
        {
            Hide("selection changed or cleared");
            return;
        }

        // Moving the object leaves the panel floating at the old spot and its buttons in the
        // way of the drag, so any manipulation closes it; re-open with another click.
        if (pointerInteraction != null && pointerInteraction.IsManipulating)
        {
            Hide("object is being moved");
            return;
        }

        if (Refresh())
            PlaceObject(null);
        // No re-facing: an open menu stays exactly where it is until the user moves it.
    }

    // ---------------- Context-aware contents ----------------

    // The actions that apply to the current target right now, in display order.
    private List<MenuAction> CurrentActions()
    {
        var actions = new List<MenuAction>();
        if (modelMode)
        {
            actions.Add(MenuAction.ModelDone);
            actions.Add(MenuAction.ResetScale);
            actions.Add(MenuAction.ResetModel);
            return actions;
        }

        // Hierarchy navigation first, in every non-model variant.
        AddExitAssembly(actions);

        if (multiMode)
        {
            actions.Add(MenuAction.Done);
            if (FocusApplies())
                actions.Add(MenuAction.FocusSelection);
            actions.Add(MenuAction.ResetSelected);
            AddResetAssembly(actions);
            actions.Add(MenuAction.ClearSelection);
            return actions;
        }

        if (groupMode)
        {
            if (FocusApplies())
                actions.Add(MenuAction.FocusSelection);
            actions.Add(MenuAction.ResetSelected);
            AddResetAssembly(actions);
            actions.Add(MenuAction.EditSelection);
            actions.Add(MenuAction.ClearSelection);
            actions.Add(MenuAction.Close);
            return actions;
        }

        if (targetId == null)
            return actions;

        bool assembly = manipulationService.HasCadChildren(targetId);
        if (assembly && targetId != manipulationService.CurrentScopeId)
            actions.Add(MenuAction.EnterAssembly);
        if (FocusApplies())
            actions.Add(MenuAction.Focus);
        if (manipulationService.IsDetached(targetId) || manipulationService.GetLogicalParentId(targetId) != null)
            actions.Add(MenuAction.DetachOrReattach);
        actions.Add(MenuAction.ResetObject); // The selected part or assembly.
        if (manipulationService.IsResized(targetId))
            actions.Add(MenuAction.ResetSize);
        AddResetAssembly(actions);
        actions.Add(MenuAction.MultiSelect);
        if (mainMenu != null)
            actions.Add(MenuAction.MainMenu);
        actions.Add(MenuAction.Close);
        return actions;
    }

    // Only inside an assembly: resets the assembly the user is in (the current scope).
    private void AddResetAssembly(List<MenuAction> actions)
    {
        if (!manipulationService.IsAtRootScope)
            actions.Add(MenuAction.ResetAssembly);
    }

    private static bool IsNavigation(MenuAction action) =>
        action == MenuAction.ExitAssembly || action == MenuAction.EnterAssembly;

    // Only inside an assembly: exits exactly one logical level.
    private void AddExitAssembly(List<MenuAction> actions)
    {
        if (!manipulationService.IsAtRootScope)
            actions.Add(MenuAction.ExitAssembly);
    }

    // Labels / tooltips / enabled states for the current state; re-stacks the panel when the set
    // of actions changed. Returns true if it re-stacked (the caller re-places the panel).
    private bool Refresh(bool forceLayout = false)
    {
        List<MenuAction> actions = CurrentActions();
        bool relayout = forceLayout || !actions.SequenceEqual(shown);
        if (relayout)
        {
            shown.Clear();
            shown.AddRange(actions);
            var rows = new List<CADMenuPanel.Row> { new CADMenuPanel.Row(TitleHeight, 0f, titleText) };
            if (!modelMode)
                rows.Add(new CADMenuPanel.Row(ScopeHeight, CADMenuPanel.RowSpacing, scopeText));
            for (int i = 0; i < shown.Count; i++)
            {
                // A gap after the last navigation button separates hierarchy from actions.
                bool endOfNavigation = IsNavigation(shown[i]) && i + 1 < shown.Count && !IsNavigation(shown[i + 1]);
                rows.Add(new CADMenuPanel.Row(ButtonHeight, endOfNavigation ? SectionGap : CADMenuPanel.RowSpacing,
                    buttons[shown[i]]));
            }
            panel.Stack(rows);
        }

        string scope = $"Scope: {manipulationService.CurrentScopeDisplayName}";
        if (scopeText.text != scope)
            scopeText.text = scope;

        bool focusHere = IsFocusOnTarget();
        SetLabel(MenuAction.Focus, focusHere ? "Clear focus" : Labels[MenuAction.Focus]);
        SetTooltip(MenuAction.Focus, focusHere ? ClearFocusTooltip : Tooltips[MenuAction.Focus]);
        SetLabel(MenuAction.FocusSelection, focusHere ? "Clear focus" : Labels[MenuAction.FocusSelection]);
        SetTooltip(MenuAction.FocusSelection, focusHere ? ClearFocusTooltip : Tooltips[MenuAction.FocusSelection]);

        if (targetId != null && !groupMode)
        {
            bool detached = manipulationService.IsDetached(targetId);
            SetLabel(MenuAction.DetachOrReattach, detached ? "Reattach" : "Detach");
            SetTooltip(MenuAction.DetachOrReattach, detached ? ReattachTooltip : Tooltips[MenuAction.DetachOrReattach]);
        }

        bool any = manipulationService.GetSelectedIds().Count > 0;
        SetInteractable(MenuAction.FocusSelection, any || focusHere);
        SetInteractable(MenuAction.ResetSelected, any);
        SetInteractable(MenuAction.ClearSelection, any);
        return relayout;
    }

    // Focus is offered only when it changes something: Clear focus while focused on the target,
    // otherwise Focus only if it would ghost some other geometry.
    private bool FocusApplies()
    {
        if (IsFocusOnTarget())
            return true;
        IEnumerable<string> targets = multiMode || groupMode
            ? manipulationService.GetSelectedLogicalRoots()
            : targetId != null ? new[] { targetId } : Array.Empty<string>();
        return manipulationService.WouldFocusGhostAnything(targets);
    }

    // Focus is "on" the target when every target object is inside the current focus.
    private bool IsFocusOnTarget()
    {
        if (!manipulationService.IsFocusActive)
            return false;
        if (multiMode || groupMode)
        {
            List<string> ids = manipulationService.GetSelectedIds();
            return ids.Count > 0 && ids.All(manipulationService.IsInFocus);
        }
        return targetId != null && manipulationService.IsInFocus(targetId);
    }

    private void SetLabel(MenuAction action, string label)
    {
        if (buttons.TryGetValue(action, out Button button))
            CADMenuPanel.SetLabel(button, label);
    }

    private void SetTooltip(MenuAction action, string text)
    {
        if (buttons.TryGetValue(action, out Button button))
            panel.SetTooltip(button, text);
    }

    private void SetInteractable(MenuAction action, bool value)
    {
        if (buttons.TryGetValue(action, out Button button))
            CADMenuPanel.SetInteractable(button, value);
    }

    // ---------------- Placement (always beside the target's visible bounds) ----------------

    private void PlaceObject(CADContextMenuRequest? request)
    {
        if (panel.WasMoved)
            return;
        if (manipulationService.TryGetObjectBounds(targetId, out Bounds bounds))
            PlaceBeside(bounds);
        else if (request.HasValue)
            PlaceBeside(new Bounds(request.Value.AnchorPoint, Vector3.zero)); // No visible geometry.
        else if (target != null)
            PlaceBeside(new Bounds(target.transform.position, Vector3.zero));
    }

    // The selected objects' combined visible bounds; with nothing visible, in front of the head.
    private void PlaceSelection()
    {
        if (panel.WasMoved)
            return;
        if (manipulationService.TryGetSelectionBounds(out Bounds bounds))
            PlaceBeside(bounds);
        else
            PlaceInFrontOfHead();
    }

    // The model's visible bounds; in front of the head if there is none or it surrounds the head.
    private void PlaceModel()
    {
        if (panel.WasMoved)
            return;
        Transform head = Head();
        if (manipulationService.TryGetModelBounds(out Bounds bounds) && (head == null || !bounds.Contains(head.position)))
            PlaceBeside(bounds);
        else
            PlaceInFrontOfHead();
    }

    private void PlaceBeside(Bounds bounds)
    {
        panel.PlaceBesideBounds(bounds, Head());
    }

    private void PlaceInFrontOfHead()
    {
        Transform head = Head();
        if (head == null)
            return;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.forward;
        panelRoot.transform.position = head.position + forward.normalized * Mathf.Max(0.8f, CADMenuPanel.MinMenuDistance);
        panel.FaceHead(head);
    }

    private static Transform Head() => Camera.main != null ? Camera.main.transform : null;

    // ---------------- Actions ----------------

    private void OnAction(MenuAction action)
    {
        string id = targetId;
        bool detached = manipulationService.IsDetached(id);
        bool focusHere = IsFocusOnTarget();
        Debug.Log($"[CADContextMenu] {action} on '{id}'.");

        // Object menu: every action closes it (most change selection, scope or pose anyway).
        // Picking menu: stays open for Focus / Reset; model menu for its resets.
        bool keepOpen = (multiMode && (action == MenuAction.ResetSelected || action == MenuAction.ResetAssembly ||
                action == MenuAction.FocusSelection)) ||
            (modelMode && (action == MenuAction.ResetModel || action == MenuAction.ResetScale));
        if (!keepOpen)
            Hide($"action {action}");

        switch (action)
        {
            case MenuAction.EnterAssembly: manipulationService.EnterScope(id); break;
            case MenuAction.Focus:
                if (focusHere) manipulationService.ClearFocus();
                else manipulationService.Focus(id);
                break;
            case MenuAction.DetachOrReattach:
                if (detached)
                    manipulationService.Reattach(id);
                else if (manipulationService.Detach(id) && !manipulationService.IsAtRootScope)
                {
                    // The part no longer belongs to the assembly being worked in: step out one
                    // level (which clears the selection) and keep the detached part selected.
                    manipulationService.ExitScope();
                    manipulationService.Select(id);
                }
                break;
            case MenuAction.ResetSize: manipulationService.ResetObjectScale(id); break;
            case MenuAction.ResetObject:
                // An assembly resets with everything in it.
                if (manipulationService.HasCadChildren(id)) manipulationService.ResetAssembly(id);
                else manipulationService.ResetObject(id);
                break;
            case MenuAction.ResetAssembly:
                if (!manipulationService.IsAtRootScope)
                    manipulationService.ResetAssembly(manipulationService.CurrentScopeId);
                break;
            case MenuAction.MultiSelect: manipulationService.BeginMultiSelect(); break; // Selection menu opens next frame.
            case MenuAction.MainMenu:
                if (mainMenu != null) mainMenu.ShowMainMenu();
                break;
            case MenuAction.Close: break;
            case MenuAction.Done: manipulationService.EndMultiSelect(); break;
            case MenuAction.FocusSelection:
                if (focusHere) manipulationService.ClearFocus();
                else manipulationService.FocusSelected();
                break;
            case MenuAction.ResetSelected: manipulationService.ResetSelected(); break;
            case MenuAction.EditSelection: manipulationService.BeginMultiSelect(); break; // Picking menu opens next frame.
            case MenuAction.ClearSelection: manipulationService.EndMultiSelect(clearSelection: true); break;
            case MenuAction.ModelDone: manipulationService.EndModelManipulation(); break;
            case MenuAction.ResetScale: manipulationService.ResetAllScales(); break;
            case MenuAction.ResetModel: manipulationService.ResetModel(); break;
            case MenuAction.ExitAssembly: manipulationService.ExitScope(); break;
        }

        if (!keepOpen)
            return;

        // Still open: labels may have changed, and a reset model moved; follow it.
        Refresh();
        if (modelMode)
            PlaceModel();
        else if (multiMode)
            PlaceSelection();
    }

    // ---------------- Construction ----------------

    private void BuildPanel()
    {
        panel = new CADMenuPanel("CAD Context Menu", PanelWidth, CADMenuStyle.Default);
        panelRoot = panel.Root;
        // Corner resize: one size for every context menu (the panel is reused), kept for the session.
        panel.EnableResize(() => panelRoot.transform.localScale.x,
            size => panelRoot.transform.localScale = Vector3.one * size);
        panel.CloseRequested = () => Hide("another menu opened");

        // Header: title, then the quiet scope line (the hierarchy section starts here).
        titleText = panel.CreateText("Title", "", CADMenuPanel.TitleSize, FontStyle.Bold, TextAnchor.MiddleLeft);
        scopeText = panel.CreateText("Scope", "Scope: Full model", CADMenuPanel.SecondarySize, FontStyle.Normal,
            TextAnchor.MiddleLeft, panel.SecondaryTextColor);
        foreach (KeyValuePair<MenuAction, string> entry in Labels)
        {
            MenuAction action = entry.Key;
            Tooltips.TryGetValue(action, out string tooltip);
            // "Done" is the one dominant action of the picking and model menus.
            bool primary = action == MenuAction.Done || action == MenuAction.ModelDone;
            buttons[action] = panel.CreateButton(entry.Value, () => OnAction(action), tooltip, primary);
        }

        Refresh(forceLayout: true);
    }
}
