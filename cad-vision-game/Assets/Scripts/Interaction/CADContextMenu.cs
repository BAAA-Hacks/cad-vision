using System;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Floating context menu for the selected CAD object, opened by
/// CADPointerInteraction.ContextMenuRequested. Presentation + wiring only: every button calls
/// a public CADVisionManipulationService method; nothing here edits CAD transforms, parents,
/// visibility or selection directly.
///
/// The panel is a world-space uGUI canvas driven through the Meta Interaction SDK
/// (RayInteractable → PointableCanvas → PointableCanvasModule), so any ray + select source
/// works: controller trigger now, hand pinch later. It is built in code (no per-object setup)
/// and is never a CAD object; CADPointerInteraction classifies it as UI, so clicking it never
/// deselects.
///
/// While the service's multi-select mode is active the same panel is re-laid-out as a
/// selection menu (Done / Reset Selected / Isolate Selected / Show All / Clear Selection),
/// anchored at the selected objects' combined visible bounds.
///
/// While whole-model manipulation mode is active it shows a minimal model menu (Done / Reset
/// Model) at the model's visible bounds; it hides while the model is being moved or scaled.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADContextMenu : MonoBehaviour
{
    private enum MenuAction
    {
        EnterAssembly, ExitAssembly, Isolate, ShowAll, DetachOrReattach, ResetObject, ResetAssembly,
        ResetModel, MultiSelect, Close,
        Done, ResetSelected, IsolateSelected, ClearSelection, EditSelection,
        ManipulateModel, ModelDone,
    }

    [Header("Placement")]
    [Tooltip("Pull toward the headset from the anchor so the panel isn't inside the geometry (m).")]
    [SerializeField, Min(0f)] private float offsetTowardHead = 0.15f;
    [SerializeField] private float verticalOffset = 0.05f;
    [SerializeField, Min(0.1f)] private float minHeadDistance = 0.45f;
    [SerializeField, Min(0.2f)] private float maxHeadDistance = 1.2f;
    [Tooltip("Re-face the headset only after it moves this far while the menu is open (m).")]
    [SerializeField, Min(0.05f)] private float refaceHeadMovement = 0.4f;

    [Header("Style")]
    [SerializeField] private Color panelColor = new Color(0.10f, 0.11f, 0.14f, 0.96f);
    [SerializeField] private Color borderColor = new Color(0.55f, 0.85f, 1f, 1f);
    [SerializeField] private Color buttonColor = new Color(0.30f, 0.33f, 0.40f, 1f);
    [SerializeField] private Color textColor = Color.white;

    // Layout in canvas units (1 unit = 1 mm at the canvas scale below).
    private const float CanvasScale = 0.001f;
    private const float PanelWidth = 260f;
    private const float Padding = 12f;
    private const float TitleHeight = 40f;
    private const float ButtonHeight = 44f;
    private const float ButtonSpacing = 8f;
    private const int FontSize = 20;

    // Object menu (one selected object).
    private static readonly MenuAction[] SingleLayout =
    {
        MenuAction.EnterAssembly, MenuAction.ExitAssembly, MenuAction.Isolate, MenuAction.ShowAll,
        MenuAction.DetachOrReattach, MenuAction.ResetObject, MenuAction.ResetAssembly,
        MenuAction.ResetModel, MenuAction.ManipulateModel, MenuAction.MultiSelect, MenuAction.Close,
    };

    // Selection menu (multi-select mode).
    private static readonly MenuAction[] MultiLayout =
    {
        MenuAction.Done, MenuAction.ResetSelected, MenuAction.IsolateSelected, MenuAction.ShowAll,
        MenuAction.ManipulateModel, MenuAction.ClearSelection,
    };

    // Selection menu outside multi-select (clicked an object that is part of a multi-selection).
    private static readonly MenuAction[] GroupLayout =
    {
        MenuAction.EditSelection, MenuAction.ResetSelected, MenuAction.IsolateSelected,
        MenuAction.ShowAll, MenuAction.ManipulateModel, MenuAction.ClearSelection, MenuAction.Close,
    };

    // Model menu (whole-model manipulation mode).
    private static readonly MenuAction[] ModelLayout =
    {
        MenuAction.ModelDone, MenuAction.ResetModel,
    };

    private static readonly Dictionary<MenuAction, string> Labels = new()
    {
        { MenuAction.EnterAssembly, "Enter Assembly" },
        { MenuAction.ExitAssembly, "Exit Assembly" },
        { MenuAction.Isolate, "Isolate" },
        { MenuAction.ShowAll, "Show All" },
        { MenuAction.DetachOrReattach, "Detach" },
        { MenuAction.ResetObject, "Reset Object" },
        { MenuAction.ResetAssembly, "Reset Assembly" },
        { MenuAction.ResetModel, "Reset Model" },
        { MenuAction.MultiSelect, "Multi-Select" },
        { MenuAction.Close, "Close" },
        { MenuAction.Done, "Done" },
        { MenuAction.ResetSelected, "Reset Selected" },
        { MenuAction.IsolateSelected, "Isolate Selected" },
        { MenuAction.ClearSelection, "Clear Selection" },
        { MenuAction.EditSelection, "Edit Selection" },
        { MenuAction.ManipulateModel, "Manipulate Model" },
        { MenuAction.ModelDone, "Done" },
    };

    private CADVisionManipulationService manipulationService;
    private CADPointerInteraction pointerInteraction;
    private CADXRGrab gripFallback;

    private GameObject panelRoot;
    private RectTransform canvasRect;
    private BoxCollider surfaceBox;
    private Text titleText;
    private bool multiMode;          // Panel shows the multi-select (picking) menu.
    private bool groupMode;          // Panel shows the selection menu for an existing multi-selection.
    private bool modelMode;          // Panel shows the model menu (whole-model manipulation).
    private string multiSignature;   // Selected IDs the selection menu was last placed for.
    private RayInteractable panelInteractable;
    private readonly Dictionary<MenuAction, Button> buttons = new();

    private string targetId;
    private CADObject target;
    private Vector3 headPositionAtFacing;

    public bool IsOpen => panelRoot != null && panelRoot.activeSelf;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        pointerInteraction = GetComponent<CADPointerInteraction>();
        gripFallback = GetComponent<CADXRGrab>();

        EnsureCanvasEventSystem();
        BuildPanel();
        Hide("initial");
    }

    private void OnEnable()
    {
        if (pointerInteraction != null)
            pointerInteraction.ContextMenuRequested += Open;
        else
            Debug.LogWarning("[CADContextMenu] No CADPointerInteraction on this object; menu will never open.");
    }

    private void OnDisable()
    {
        if (pointerInteraction != null)
            pointerInteraction.ContextMenuRequested -= Open;
        Hide("component disabled");
    }

    private void OnDestroy()
    {
        if (panelRoot != null)
            Destroy(panelRoot);
    }

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

        // A newer request replaces the current menu.
        multiMode = false;
        groupMode = false;
        modelMode = false;
        ApplyLayout(SingleLayout);
        targetId = request.TargetId;
        target = selected;
        titleText.text = selected.name;

        Place(request);
        RefreshButtons();
        panelRoot.SetActive(true);
        panelInteractable.enabled = true;
        Debug.Log($"[CADContextMenu] Opened for '{targetId}'.");
    }

    private void Hide(string reason)
    {
        bool wasOpen = IsOpen;
        if (panelRoot != null)
        {
            // Disable the interactable first so a hidden panel can never swallow ray clicks.
            if (panelInteractable != null)
                panelInteractable.enabled = false;
            panelRoot.SetActive(false);
        }

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
        multiMode = false;
        groupMode = true;
        modelMode = false;
        targetId = request.TargetId;
        target = null;
        ApplyLayout(GroupLayout);
        multiSignature = SelectionSignature(out int count);
        titleText.text = $"{count} selected";
        if (request.AnchorIsHitPoint)
            PlaceAt(request.AnchorPoint);
        else
            PlaceMulti(true);
        RefreshButtons();
        panelRoot.SetActive(true);
        panelInteractable.enabled = true;
        Debug.Log($"[CADContextMenu] Opened selection menu ({count} selected).");
    }

    private string SelectionSignature(out int count)
    {
        List<string> ids = manipulationService.GetSelectedIds();
        ids.Sort(StringComparer.Ordinal);
        count = ids.Count;
        return string.Join("|", ids);
    }

    private bool IsMoving() =>
        (pointerInteraction != null && pointerInteraction.IsManipulating) ||
        (gripFallback != null && (gripFallback.IsGrabbing || gripFallback.IsScalingModel));

    private void OpenModel()
    {
        multiMode = false;
        groupMode = false;
        modelMode = true;
        multiSignature = null;
        targetId = null;
        target = null;
        ApplyLayout(ModelLayout);
        titleText.text = "Manipulate Model";
        PlaceModel();
        RefreshButtons();
        panelRoot.SetActive(true);
        panelInteractable.enabled = true;
        Debug.Log("[CADContextMenu] Opened model menu.");
    }

    private void OpenMulti()
    {
        multiMode = true;
        groupMode = false;
        modelMode = false;
        multiSignature = null;
        targetId = null;
        target = null;
        ApplyLayout(MultiLayout);
        UpdateMulti(forcePlace: true);
        panelRoot.SetActive(true);
        panelInteractable.enabled = true;
        Debug.Log("[CADContextMenu] Opened selection menu (multi-select).");
    }

    // Keeps the selection menu's title/buttons current; re-places it when the set changes.
    private void UpdateMulti(bool forcePlace = false)
    {
        List<string> ids = manipulationService.GetSelectedIds();
        ids.Sort(StringComparer.Ordinal);
        string signature = string.Join("|", ids);
        titleText.text = $"Multi-Select: {ids.Count} selected";
        if (forcePlace || signature != multiSignature)
        {
            PlaceMulti(!IsOpen || forcePlace);
            multiSignature = signature;
        }

        RefreshButtons();
    }

    private void LateUpdate()
    {
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
                OpenModel();
            else
                RefaceIfHeadMoved();
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
                OpenMulti();
            else
            {
                UpdateMulti();
                RefaceIfHeadMoved();
            }
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
            else
            {
                RefaceIfHeadMoved();
                RefreshButtons();
            }
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
        if ((pointerInteraction != null && pointerInteraction.IsManipulating) ||
            (gripFallback != null && gripFallback.IsGrabbing))
        {
            Hide("object is being moved");
            return;
        }

        RefaceIfHeadMoved();
        RefreshButtons();
    }

    // Minimal facing: only after the head has moved noticeably, never every frame.
    private void RefaceIfHeadMoved()
    {
        Transform head = Head();
        if (head != null && Vector3.Distance(head.position, headPositionAtFacing) > refaceHeadMovement)
            Face(head);
    }

    // ---------------- Placement ----------------

    private void Place(CADContextMenuRequest request)
    {
        // Anchor: click hit point, else visible geometry, else (last resort) the origin.
        Vector3 anchor;
        if (request.AnchorIsHitPoint)
        {
            anchor = request.AnchorPoint;
        }
        else if (manipulationService.TryGetSelectionPoint(out Vector3 selectionPoint))
        {
            anchor = selectionPoint;
        }
        else
        {
            // VisualCenter falls back to the Transform origin only when there are no renderers.
            anchor = CADGrabSession.VisualCenter(target);
        }

        PlaceAt(anchor);
    }

    // Selection menu: the point of the selected objects' combined visible bounds nearest the
    // headset (never a CAD Transform origin). With nothing selected it stays where it is, or
    // opens in front of the headset.
    private void PlaceMulti(bool placeEvenIfEmpty)
    {
        bool hasBounds = false;
        Bounds bounds = default;
        foreach (CADObject selected in manipulationService.GetSelectedObjects())
        {
            foreach (Renderer renderer in selected.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer.TryGetComponent(out CADVisualOverlay _))
                    continue;
                if (hasBounds) bounds.Encapsulate(renderer.bounds);
                else { bounds = renderer.bounds; hasBounds = true; }
            }
        }

        Transform head = Head();
        if (hasBounds)
            PlaceAt(head != null ? bounds.ClosestPoint(head.position) : bounds.center);
        else if (placeEvenIfEmpty && head != null)
            PlaceAt(head.position + Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.8f);
    }

    // Model menu: the point of the model's visible bounds nearest the headset (never the
    // root's CAD origin); in front of the headset if there is no geometry or it surrounds it.
    private void PlaceModel()
    {
        Transform head = Head();
        bool hasBounds = manipulationService.TryGetModelBounds(out Bounds bounds);
        if (hasBounds && (head == null || !bounds.Contains(head.position)))
            PlaceAt(head != null ? bounds.ClosestPoint(head.position) : bounds.center);
        else if (head != null)
            PlaceAt(head.position + Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.8f);
    }

    private void PlaceAt(Vector3 anchor)
    {
        Transform head = Head();
        if (head == null)
        {
            panelRoot.transform.position = anchor;
            return;
        }

        Vector3 toHead = head.position - anchor;
        Vector3 position = anchor + (toHead.sqrMagnitude > 1e-6f ? toHead.normalized : Vector3.back) * offsetTowardHead
            + Vector3.up * verticalOffset;

        // Keep it readable: not too close to the face, not far away with a reach-assisted object.
        Vector3 fromHead = position - head.position;
        float distance = fromHead.magnitude;
        if (distance > 1e-4f)
            position = head.position + fromHead / distance * Mathf.Clamp(distance, minHeadDistance, maxHeadDistance);

        panelRoot.transform.position = position;
        Face(head);
    }

    private void Face(Transform head)
    {
        Vector3 away = panelRoot.transform.position - head.position;
        if (away.sqrMagnitude > 1e-6f)
            panelRoot.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        headPositionAtFacing = head.position;
    }

    private static Transform Head() => Camera.main != null ? Camera.main.transform : null;

    // ---------------- Actions ----------------

    private void RefreshButtons()
    {
        SetInteractable(MenuAction.ManipulateModel, manipulationService.ModelRoot != null);
        if (modelMode)
        {
            SetInteractable(MenuAction.ModelDone, true);
            SetInteractable(MenuAction.ResetModel, true);
            return;
        }

        if (multiMode || groupMode)
        {
            bool any = manipulationService.GetSelectedObjects().Any();
            SetInteractable(MenuAction.EditSelection, true);
            SetInteractable(MenuAction.Close, true);
            SetInteractable(MenuAction.Done, true);
            SetInteractable(MenuAction.ResetSelected, any);
            SetInteractable(MenuAction.IsolateSelected, any);
            SetInteractable(MenuAction.ShowAll, true);
            SetInteractable(MenuAction.ClearSelection, any);
            return;
        }

        bool hasChildren = manipulationService.HasCadChildren(targetId);
        SetInteractable(MenuAction.EnterAssembly, hasChildren);
        SetInteractable(MenuAction.ExitAssembly, manipulationService.CurrentScopeId != null);
        SetInteractable(MenuAction.Isolate, true);
        SetInteractable(MenuAction.ShowAll, true);
        // One button: "Reattach" while detached, else "Detach" (needs a logical parent assembly).
        bool detached = manipulationService.IsDetached(targetId);
        SetLabel(MenuAction.DetachOrReattach, detached ? "Reattach" : "Detach");
        SetInteractable(MenuAction.DetachOrReattach,
            detached || manipulationService.GetLogicalParentId(targetId) != null);
        SetInteractable(MenuAction.ResetObject, true);
        SetInteractable(MenuAction.ResetAssembly, ResetAssemblyTarget() != null);
        SetInteractable(MenuAction.ResetModel, true);
        SetInteractable(MenuAction.MultiSelect, true);
        SetInteractable(MenuAction.Close, true);
    }

    // The target itself if it is an assembly, else the assembly that logically contains it.
    private string ResetAssemblyTarget() =>
        manipulationService.HasCadChildren(targetId) ? targetId
            : manipulationService.GetLogicalParentId(targetId);

    private void SetLabel(MenuAction action, string label)
    {
        if (buttons.TryGetValue(action, out Button button))
        {
            Text text = button.GetComponentInChildren<Text>();
            if (text != null && text.text != label)
                text.text = label;
        }
    }

    private void SetInteractable(MenuAction action, bool value)
    {
        if (buttons.TryGetValue(action, out Button button) && button.interactable != value)
            button.interactable = value;
    }

    private void OnAction(MenuAction action)
    {
        string id = targetId;
        string assemblyId = ResetAssemblyTarget();
        bool detached = manipulationService.IsDetached(id);
        Debug.Log($"[CADContextMenu] {action} on '{id}'.");

        // Object menu: every action closes it (most change selection, scope or pose anyway).
        // Selection menu: stays open for Reset/Isolate/Show All; Done and Clear end the mode.
        bool keepOpen = (multiMode && (action == MenuAction.ResetSelected ||
            action == MenuAction.IsolateSelected || action == MenuAction.ShowAll)) ||
            (modelMode && action == MenuAction.ResetModel);
        if (!keepOpen)
            Hide($"action {action}");

        switch (action)
        {
            case MenuAction.EnterAssembly: manipulationService.EnterScope(id); break;
            case MenuAction.ExitAssembly: manipulationService.ExitScope(); break;
            case MenuAction.Isolate: manipulationService.Isolate(id); break;
            case MenuAction.ShowAll: manipulationService.ShowAll(); break;
            case MenuAction.DetachOrReattach:
                if (detached) manipulationService.Reattach(id);
                else manipulationService.Detach(id);
                break;
            case MenuAction.ResetObject: manipulationService.ResetObject(id); break;
            case MenuAction.ResetAssembly:
                if (assemblyId != null) manipulationService.ResetAssembly(assemblyId);
                break;
            case MenuAction.ResetModel: manipulationService.ResetModel(); break;
            case MenuAction.MultiSelect: manipulationService.BeginMultiSelect(); break; // Selection menu opens next frame.
            case MenuAction.Close: break;
            case MenuAction.Done: manipulationService.EndMultiSelect(); break;
            case MenuAction.ResetSelected: manipulationService.ResetSelected(); break;
            case MenuAction.IsolateSelected: manipulationService.IsolateSelected(); break;
            case MenuAction.ClearSelection: manipulationService.EndMultiSelect(clearSelection: true); break;
            case MenuAction.EditSelection: manipulationService.BeginMultiSelect(); break; // Picking menu opens next frame.
            case MenuAction.ManipulateModel: manipulationService.BeginModelManipulation(); break; // Model menu opens next frame.
            case MenuAction.ModelDone: manipulationService.EndModelManipulation(); break;
        }

        // The model is back at its review pose: follow it.
        if (keepOpen && modelMode && action == MenuAction.ResetModel)
            PlaceModel();
    }

    // ---------------- Construction ----------------

    // The ISDK canvas bridge needs exactly one EventSystem with a PointableCanvasModule.
    private static void EnsureCanvasEventSystem()
    {
        EventSystem eventSystem = FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
        if (eventSystem == null)
        {
            var go = new GameObject("CAD Vision EventSystem");
            eventSystem = go.AddComponent<EventSystem>();
        }

        if (eventSystem.GetComponent<PointableCanvasModule>() == null &&
            FindAnyObjectByType<PointableCanvasModule>(FindObjectsInactive.Include) == null)
        {
            eventSystem.gameObject.AddComponent<PointableCanvasModule>();
        }
    }

    private void BuildPanel()
    {
        float height = PanelHeight(SingleLayout.Length);

        // Separate root (never under a CAD object or the model root). Marked as UI.
        panelRoot = new GameObject("CAD Context Menu");
        panelRoot.AddComponent<CADUIPointerTarget>();

        var canvasObject = new GameObject("Canvas", typeof(RectTransform));
        canvasObject.transform.SetParent(panelRoot.transform, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasObject.AddComponent<GraphicRaycaster>(); // Required by PointableCanvas.
        canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(PanelWidth, height);
        canvasRect.localScale = Vector3.one * CanvasScale;

        // Thin border behind the panel for contrast against passthrough and dark scenes.
        Image border = CreateImage("Border", canvasRect, borderColor);
        Stretch((RectTransform)border.transform, -3f);
        Image background = CreateImage("Background", canvasRect, panelColor);
        Stretch((RectTransform)background.transform, 0f);

        titleText = CreateText("Title", canvasRect, "", FontSize, FontStyle.Bold);
        PlaceTopDown((RectTransform)titleText.transform, Padding, TitleHeight, height);

        foreach (KeyValuePair<MenuAction, string> entry in Labels)
            buttons[entry.Key] = CreateButton(entry.Key, entry.Value, canvasRect);

        // Ray surface: a thin collider covering the panel, forwarding pointer events to the
        // canvas. Same SDK path as CAD parts, but not under a CADObject, so it counts as UI.
        // Ignore Raycast layer keeps it out of desktop Physics.Raycast (CADSelection).
        var surfaceObject = new GameObject("Ray Surface");
        surfaceObject.layer = 2; // Ignore Raycast
        surfaceObject.transform.SetParent(panelRoot.transform, false);
        surfaceBox = surfaceObject.AddComponent<BoxCollider>();

        var pointableCanvas = canvasObject.AddComponent<PointableCanvas>();
        pointableCanvas.InjectAllPointableCanvas(canvas);

        var surface = surfaceObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(surfaceBox);
        panelInteractable = surfaceObject.AddComponent<RayInteractable>();
        panelInteractable.InjectAllRayInteractable(surface);
        panelInteractable.InjectOptionalSelectSurface(surface);
        panelInteractable.InjectOptionalPointableElement(pointableCanvas);
    }

    private static float PanelHeight(int buttonCount) =>
        Padding * 2 + TitleHeight + buttonCount * ButtonHeight + Mathf.Max(0, buttonCount - 1) * ButtonSpacing;

    // Shows exactly the layout's buttons, stacks them top-down and resizes panel + ray surface.
    private void ApplyLayout(MenuAction[] layout)
    {
        float height = PanelHeight(layout.Length);
        canvasRect.sizeDelta = new Vector2(PanelWidth, height);
        surfaceBox.size = new Vector3(PanelWidth * CanvasScale, height * CanvasScale, 0.004f);
        PlaceTopDown((RectTransform)titleText.transform, Padding, TitleHeight, height);

        foreach (KeyValuePair<MenuAction, Button> entry in buttons)
            entry.Value.gameObject.SetActive(Array.IndexOf(layout, entry.Key) >= 0);

        float y = Padding + TitleHeight;
        foreach (MenuAction action in layout)
        {
            PlaceTopDown((RectTransform)buttons[action].transform, y, ButtonHeight, height);
            y += ButtonHeight + ButtonSpacing;
        }
    }

    private Button CreateButton(MenuAction action, string label, RectTransform parent)
    {
        Image image = CreateImage(label, parent, buttonColor);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        // Tints multiply buttonColor: normal slightly dimmed so hover (full) reads as brighter.
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(0.75f, 0.75f, 0.75f, 1f);
        colors.highlightedColor = Color.white;
        colors.pressedColor = new Color(0.55f, 0.85f, 1f, 1f);
        colors.selectedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
        colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.5f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => OnAction(action));

        Text text = CreateText("Label", (RectTransform)image.transform, label, FontSize, FontStyle.Normal);
        Stretch((RectTransform)text.transform, 0f);
        return button;
    }

    private Image CreateImage(string name, RectTransform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private Text CreateText(string name, RectTransform parent, string value, int size, FontStyle style)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = textColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    // Positions a full-width row `top` units from the panel's top edge.
    private static void PlaceTopDown(RectTransform rect, float top, float rowHeight, float panelHeight)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(Padding, 0f);
        rect.offsetMax = new Vector2(-Padding, 0f);
        rect.anchoredPosition = new Vector2(0f, -top);
        rect.sizeDelta = new Vector2(-2 * Padding, rowHeight);
    }
}
