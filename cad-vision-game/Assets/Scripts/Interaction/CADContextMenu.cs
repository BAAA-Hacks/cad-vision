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
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADContextMenu : MonoBehaviour
{
    private enum MenuAction { EnterAssembly, ExitAssembly, Isolate, ShowAll, DetachOrReattach, ResetObject, ResetAssembly, ResetModel, Close }

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

    private static readonly (MenuAction action, string label)[] Layout =
    {
        (MenuAction.EnterAssembly, "Enter Assembly"),
        (MenuAction.ExitAssembly, "Exit Assembly"),
        (MenuAction.Isolate, "Isolate"),
        (MenuAction.ShowAll, "Show All"),
        (MenuAction.DetachOrReattach, "Detach"),
        (MenuAction.ResetObject, "Reset Object"),
        (MenuAction.ResetAssembly, "Reset Assembly"),
        (MenuAction.ResetModel, "Reset Model"),
        (MenuAction.Close, "Close"),
    };

    private CADVisionManipulationService manipulationService;
    private CADPointerInteraction pointerInteraction;
    private CADXRGrab gripFallback;

    private GameObject panelRoot;
    private Text titleText;
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
        CADObject selected = manipulationService.GetSelectedObjects()
            .FirstOrDefault(o => o.id == request.TargetId);
        if (selected == null)
            return;

        // A newer request replaces the current menu.
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
    }

    private void LateUpdate()
    {
        if (!IsOpen)
            return;

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

        // Minimal facing: only after the head has moved noticeably, never every frame.
        Transform head = Head();
        if (head != null && Vector3.Distance(head.position, headPositionAtFacing) > refaceHeadMovement)
            Face(head);

        RefreshButtons();
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

        // Every action closes the menu: most change the selection, scope or the object's pose
        // anyway, and a predictable "one action per menu" is easier with a ray.
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
            case MenuAction.Close: break;
        }
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
        int count = Layout.Length;
        float height = Padding * 2 + TitleHeight + count * ButtonHeight + (count - 1) * ButtonSpacing;

        // Separate root (never under a CAD object or the model root). Marked as UI.
        panelRoot = new GameObject("CAD Context Menu");
        panelRoot.AddComponent<CADUIPointerTarget>();

        var canvasObject = new GameObject("Canvas", typeof(RectTransform));
        canvasObject.transform.SetParent(panelRoot.transform, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasObject.AddComponent<GraphicRaycaster>(); // Required by PointableCanvas.
        var canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(PanelWidth, height);
        canvasRect.localScale = Vector3.one * CanvasScale;

        // Thin border behind the panel for contrast against passthrough and dark scenes.
        Image border = CreateImage("Border", canvasRect, borderColor);
        Stretch((RectTransform)border.transform, -3f);
        Image background = CreateImage("Background", canvasRect, panelColor);
        Stretch((RectTransform)background.transform, 0f);

        titleText = CreateText("Title", canvasRect, "", FontSize, FontStyle.Bold);
        PlaceTopDown((RectTransform)titleText.transform, Padding, TitleHeight, height);

        float y = Padding + TitleHeight;
        foreach ((MenuAction action, string label) in Layout)
        {
            Button button = CreateButton(action, label, canvasRect);
            PlaceTopDown((RectTransform)button.transform, y, ButtonHeight, height);
            buttons[action] = button;
            y += ButtonHeight + ButtonSpacing;
        }

        // Ray surface: a thin collider covering the panel, forwarding pointer events to the
        // canvas. Same SDK path as CAD parts, but not under a CADObject, so it counts as UI.
        // Ignore Raycast layer keeps it out of desktop Physics.Raycast (CADSelection).
        var surfaceObject = new GameObject("Ray Surface");
        surfaceObject.layer = 2; // Ignore Raycast
        surfaceObject.transform.SetParent(panelRoot.transform, false);
        var box = surfaceObject.AddComponent<BoxCollider>();
        box.size = new Vector3(PanelWidth * CanvasScale, height * CanvasScale, 0.004f);

        var pointableCanvas = canvasObject.AddComponent<PointableCanvas>();
        pointableCanvas.InjectAllPointableCanvas(canvas);

        var surface = surfaceObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(box);
        panelInteractable = surfaceObject.AddComponent<RayInteractable>();
        panelInteractable.InjectAllRayInteractable(surface);
        panelInteractable.InjectOptionalSelectSurface(surface);
        panelInteractable.InjectOptionalPointableElement(pointableCanvas);
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
