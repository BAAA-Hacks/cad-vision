using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Colors of a CADMenuPanel (the context menu's Inspector style; defaults are its defaults).</summary>
[Serializable]
public struct CADMenuStyle
{
    public Color PanelColor;
    public Color BorderColor;
    public Color ButtonColor;
    public Color SelectedButtonColor;
    public Color TextColor;

    public static CADMenuStyle Default => new CADMenuStyle
    {
        PanelColor = new Color(0.10f, 0.11f, 0.14f, 0.96f),
        BorderColor = new Color(0.55f, 0.85f, 1f, 1f),
        ButtonColor = new Color(0.30f, 0.33f, 0.40f, 1f),
        SelectedButtonColor = new Color(0.20f, 0.55f, 0.70f, 1f),
        TextColor = Color.white,
    };
}

/// <summary>
/// The one menu panel implementation, shared by CADContextMenu and CADMainMenu: the
/// construction that has been proven to render and take input on Quest, extracted unchanged
/// from CADContextMenu. Owners decide contents, labels, callbacks, placement and lifecycle;
/// this class owns everything that displays the panel and routes pointer input to it:
///
/// - a separate root GameObject (never under a CAD object or the model root), marked
///   CADUIPointerTarget so pressing it never deselects CAD;
/// - a world-space uGUI Canvas (+ GraphicRaycaster) at 0.001 scale (1 canvas unit = 1 mm),
///   no CanvasScaler, no nested canvases, masks, custom materials or shaders (built-in UI
///   Images and legacy Text with LegacyRuntime.ttf);
/// - a border Image behind a background Image;
/// - a "Ray Surface" child on the Ignore Raycast layer: BoxCollider → ColliderSurface →
///   RayInteractable (select surface + PointableCanvas as pointable element), so any ray +
///   select source (controller trigger, hand pinch) drives the canvas through the scene's
///   EventSystem + PointableCanvasModule (created if missing);
/// - buttons (Image + Button + Text label, same tints), texts, top-down row stacking that
///   shows exactly the stacked elements and resizes panel + ray surface around them;
/// - Show/Hide: the ray interactable is disabled before the root is deactivated, so a hidden
///   panel never renders, never catches rays and leaves no active collider behind;
/// - hover tooltips (CADMenuTooltip) for buttons created with tooltip text;
/// - PlaceAboveBounds: the one placement rule for target-relative menus (above the target's
///   visible bounds, facing the user);
/// - LogSelectEvents: TEMP diagnostics, logs which interactor (left/right hand/controller
///   ray) selects or unselects the panel.
/// </summary>
public sealed class CADMenuPanel
{
    public const float CanvasScale = 0.001f;
    public const float Padding = 12f;
    public const float RowSpacing = 8f;
    public const int FontSize = 20;

    /// <summary>TEMP diagnostics: log select / unselect / cancel on every panel with the interactor's name.</summary>
    public static bool LogSelectEvents = true;

    /// <summary>One stacked row: its elements share the width equally, left to right.</summary>
    public readonly struct Row
    {
        public readonly Component[] Items;
        public readonly float Height;
        public readonly float SpacingAfter;

        public Row(float height, params Component[] items) : this(height, RowSpacing, items) { }

        public Row(float height, float spacingAfter, params Component[] items)
        {
            Height = height;
            SpacingAfter = spacingAfter;
            Items = items;
        }
    }

    private readonly CADMenuStyle style;
    private readonly RectTransform canvasRect;
    private readonly BoxCollider surfaceBox;
    private readonly RayInteractable interactable;
    private readonly List<GameObject> elements = new(); // Everything Stack() manages.

    public GameObject Root { get; }
    public RayInteractable Interactable => interactable;
    public CADMenuTooltip Tooltip { get; }
    public float Width { get; private set; }
    public float Height { get; private set; }
    public bool IsOpen => Root != null && Root.activeSelf;

    public CADMenuPanel(string name, float width, CADMenuStyle style)
    {
        this.style = style;
        Width = width;
        EnsureCanvasEventSystem();

        // Separate root (never under a CAD object or the model root). Marked as UI.
        Root = new GameObject(name);
        Root.AddComponent<CADUIPointerTarget>();

        var canvasObject = new GameObject("Canvas", typeof(RectTransform));
        canvasObject.transform.SetParent(Root.transform, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasObject.AddComponent<GraphicRaycaster>(); // Required by PointableCanvas.
        canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.localScale = Vector3.one * CanvasScale;

        // Thin border behind the panel for contrast against passthrough and dark scenes.
        Image border = CreateImage("Border", style.BorderColor);
        Stretch((RectTransform)border.transform, -3f);
        Image background = CreateImage("Background", style.PanelColor);
        Stretch((RectTransform)background.transform, 0f);

        // Ray surface: a thin collider covering the panel, forwarding pointer events to the
        // canvas. Same SDK path as CAD parts, but not under a CADObject, so it counts as UI.
        // Ignore Raycast layer keeps it out of desktop Physics.Raycast (CADSelection).
        var surfaceObject = new GameObject("Ray Surface");
        surfaceObject.layer = 2; // Ignore Raycast
        surfaceObject.transform.SetParent(Root.transform, false);
        surfaceBox = surfaceObject.AddComponent<BoxCollider>();

        var pointableCanvas = canvasObject.AddComponent<PointableCanvas>();
        pointableCanvas.InjectAllPointableCanvas(canvas);

        var surface = surfaceObject.AddComponent<ColliderSurface>();
        surface.InjectAllColliderSurface(surfaceBox);
        interactable = surfaceObject.AddComponent<RayInteractable>();
        interactable.InjectAllRayInteractable(surface);
        interactable.InjectOptionalSelectSurface(surface);
        interactable.InjectOptionalPointableElement(pointableCanvas);
        interactable.WhenPointerEventRaised += LogPointerEvent;

        Tooltip = Root.AddComponent<CADMenuTooltip>();
        Tooltip.Initialize(canvasRect, LegacyFont);

        Resize(width, 2 * Padding);
    }

    // ---------------- Show / hide ----------------

    public void Show()
    {
        Root.SetActive(true);
        interactable.enabled = true;
    }

    public void Hide()
    {
        // Disable the interactable first so a hidden panel can never swallow ray clicks.
        interactable.enabled = false;
        Tooltip.Hide();
        Root.SetActive(false);
    }

    private void LogPointerEvent(PointerEvent evt)
    {
        if (!LogSelectEvents || evt.Type == PointerEventType.Hover || evt.Type == PointerEventType.Move ||
            evt.Type == PointerEventType.Unhover)
        {
            return;
        }

        string source = evt.Data is Component component
            ? $"{(component.transform.parent != null ? component.transform.parent.name + "/" : "")}{component.name}"
            : evt.Data?.ToString() ?? $"pointer {evt.Identifier}";
        Debug.Log($"[CADMenuPanel] '{Root.name}' {evt.Type} by {source}.");
    }

    public void Destroy()
    {
        if (Root == null)
            return;
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(Root);
        else
            UnityEngine.Object.DestroyImmediate(Root);
    }

    // ---------------- Elements ----------------

    public Text CreateText(string name, string value, int size = FontSize, FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        Text text = CreateText(name, canvasRect, value, size, fontStyle, alignment);
        elements.Add(text.gameObject);
        return text;
    }

    /// <summary>A panel button; with tooltip text it shows that text after a short ray hover.</summary>
    public Button CreateButton(string label, UnityAction onClick, string tooltip = null)
    {
        Image image = CreateImage(label, style.ButtonColor);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        // Tints multiply the button color: normal slightly dimmed so hover (full) reads as brighter.
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
        if (onClick != null)
            button.onClick.AddListener(onClick);

        Text text = CreateText("Label", (RectTransform)image.transform, label, FontSize, FontStyle.Normal,
            TextAnchor.MiddleCenter);
        Stretch((RectTransform)text.transform, 0f);
        if (!string.IsNullOrEmpty(tooltip))
            SetTooltip(button, tooltip);
        elements.Add(button.gameObject);
        return button;
    }

    /// <summary>Sets or changes a button's tooltip text.</summary>
    public void SetTooltip(Button button, string tooltip)
    {
        if (!button.TryGetComponent(out CADMenuTooltipTrigger trigger))
            trigger = button.gameObject.AddComponent<CADMenuTooltipTrigger>();
        trigger.Text = tooltip;
        trigger.Tooltip = Tooltip;
    }

    // ---------------- Placement ----------------

    /// <summary>
    /// Target-relative placement shared by every contextual menu: the panel's bottom edge sits
    /// clearance above the top of the target's visible world bounds (never a Transform origin),
    /// nudged headBias toward the user, facing the user. Tiny targets still get minRise above
    /// their center; if the top is more than maxAboveEye above eye level (huge assemblies), the
    /// panel comes down to that height at the side of the bounds nearest the user instead.
    /// Finally clamped to minDistance..maxDistance from the head.
    /// </summary>
    public void PlaceAboveBounds(Bounds bounds, Transform head, float clearance = 0.05f, float minRise = 0.1f,
        float maxAboveEye = 0.25f, float headBias = 0.08f, float minDistance = 0.45f, float maxDistance = 1.4f)
    {
        float halfHeight = Height * CanvasScale * Root.transform.lossyScale.y * 0.5f;
        Vector3 anchor = bounds.center;
        anchor.y = Mathf.Max(bounds.max.y + clearance, bounds.center.y + minRise);

        if (head == null)
        {
            Root.transform.position = anchor + Vector3.up * halfHeight;
            return;
        }

        if (anchor.y > head.position.y + maxAboveEye)
        {
            Vector3 near = bounds.ClosestPoint(head.position);
            anchor = new Vector3(near.x, head.position.y + maxAboveEye, near.z);
        }

        Vector3 toHead = Vector3.ProjectOnPlane(head.position - anchor, Vector3.up);
        if (toHead.sqrMagnitude > 1e-6f)
            anchor += toHead.normalized * headBias;

        Vector3 position = anchor + Vector3.up * halfHeight;
        Vector3 fromHead = position - head.position;
        float distance = fromHead.magnitude;
        if (distance > 1e-4f)
            position = head.position + fromHead / distance * Mathf.Clamp(distance, minDistance, maxDistance);

        Root.transform.position = position;
        FaceHead(head);
    }

    /// <summary>Turns the panel to face the head (upright).</summary>
    public void FaceHead(Transform head)
    {
        Vector3 away = Root.transform.position - head.position;
        if (away.sqrMagnitude > 1e-6f)
            Root.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
    }

    // ---------------- Button state ----------------

    public static string GetLabel(Button button) => button.GetComponentInChildren<Text>(true).text;

    public static void SetLabel(Button button, string label)
    {
        Text text = button.GetComponentInChildren<Text>(true);
        if (text != null && text.text != label)
            text.text = label;
    }

    public static void SetInteractable(Button button, bool value)
    {
        if (button.interactable != value)
            button.interactable = value;
    }

    /// <summary>Selected / "on" state: the button's base color (tints still apply on top).</summary>
    public void SetSelected(Button button, bool selected)
    {
        Color color = selected ? style.SelectedButtonColor : style.ButtonColor;
        if (button.targetGraphic.color != color)
            button.targetGraphic.color = color;
    }

    public bool IsSelected(Button button) => button.targetGraphic.color == style.SelectedButtonColor;

    // ---------------- Layout ----------------

    /// <summary>
    /// Stacks rows top-down from the top padding; each row's items share the width equally.
    /// Shows exactly the stacked elements (every other element made by this panel is hidden)
    /// and resizes the panel and its ray surface around them.
    /// </summary>
    public void Stack(IEnumerable<Row> rows)
    {
        var shown = new HashSet<GameObject>();
        float inner = Width - 2 * Padding;
        float y = Padding;
        float lastSpacing = 0f;
        foreach (Row row in rows)
        {
            int count = row.Items.Length;
            float itemWidth = (inner - (count - 1) * RowSpacing) / count;
            for (int i = 0; i < count; i++)
            {
                Place((RectTransform)row.Items[i].transform, Padding + i * (itemWidth + RowSpacing), y, itemWidth,
                    row.Height);
                shown.Add(row.Items[i].gameObject);
            }
            y += row.Height + row.SpacingAfter;
            lastSpacing = row.SpacingAfter;
        }

        foreach (GameObject element in elements)
            element.SetActive(shown.Contains(element));

        Resize(Width, y - lastSpacing + Padding);
    }

    public void Resize(float width, float height)
    {
        Width = width;
        Height = height;
        canvasRect.sizeDelta = new Vector2(width, height);
        surfaceBox.size = new Vector3(width * CanvasScale, height * CanvasScale, 0.004f);
    }

    /// <summary>True if a world point (a ray hit) lies on rect, within slack canvas units.</summary>
    public static bool Contains(RectTransform rect, Vector3 worldPoint, float slack = 4f)
    {
        Vector3 local = rect.InverseTransformPoint(worldPoint);
        Rect area = rect.rect;
        return Mathf.Abs(local.z) <= 30f &&
            local.x >= area.xMin - slack && local.x <= area.xMax + slack &&
            local.y >= area.yMin - slack && local.y <= area.yMax + slack;
    }

    // ---------------- Construction helpers ----------------

    // The ISDK canvas bridge needs exactly one EventSystem with a PointableCanvasModule.
    public static void EnsureCanvasEventSystem()
    {
        EventSystem eventSystem = UnityEngine.Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
        if (eventSystem == null)
        {
            var go = new GameObject("CAD Vision EventSystem");
            eventSystem = go.AddComponent<EventSystem>();
        }

        if (eventSystem.GetComponent<PointableCanvasModule>() == null &&
            UnityEngine.Object.FindAnyObjectByType<PointableCanvasModule>(FindObjectsInactive.Include) == null)
        {
            eventSystem.gameObject.AddComponent<PointableCanvasModule>();
        }
    }

    private static Font LegacyFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

    private Image CreateImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(canvasRect, false);
        var image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private Text CreateText(string name, RectTransform parent, string value, int size, FontStyle fontStyle,
        TextAnchor alignment)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = LegacyFont;
        text.text = value;
        text.fontSize = size;
        text.fontStyle = fontStyle;
        text.color = style.TextColor;
        text.alignment = alignment;
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

    // Places rect at (x, top) from the panel's top-left, size width × height (canvas units).
    private static void Place(RectTransform rect, float x, float top, float width, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -top);
        rect.sizeDelta = new Vector2(width, height);
    }
}
