using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Colors of a CADMenuPanel: the CADVision UI style guide palette.</summary>
[Serializable]
public struct CADMenuStyle
{
    public Color PanelColor;          // Background #0C1524: panel surfaces; label on cyan.
    public Color BorderColor;         // Border #286881: thin outline.
    public Color ButtonColor;         // Surface #172639: secondary buttons, cards.
    public Color SelectedButtonColor; // Accent #2ADCDB: primary action, active state.
    public Color TextColor;           // Primary text #FFFFFF.
    public Color SecondaryTextColor;  // Secondary text #9FB6CD: subtitles, labels, status.

    public static CADMenuStyle Default => new CADMenuStyle
    {
        PanelColor = new Color32(0x0C, 0x15, 0x24, 0xFF),
        BorderColor = new Color32(0x28, 0x68, 0x81, 0xFF),
        ButtonColor = new Color32(0x17, 0x26, 0x39, 0xFF),
        SelectedButtonColor = new Color32(0x2A, 0xDC, 0xDB, 0xFF),
        TextColor = Color.white,
        SecondaryTextColor = new Color32(0x9F, 0xB6, 0xCD, 0xFF),
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
/// - a world-space uGUI Canvas (+ GraphicRaycaster) at CanvasScale (1 canvas unit = 0.75 mm),
///   no CanvasScaler, no nested canvases, masks, custom materials or shaders (built-in UI
///   Images and legacy Text with LegacyRuntime.ttf);
/// - the CADVision UI style guide: navy panel with a thin border, navy-surface secondary
///   buttons, cyan primary / selected buttons with navy labels, white and muted text,
///   LegacyRuntime.ttf at the guide's sizes, 52-unit controls, rounded corners (one
///   generated 9-slice sprite; RoundedCorners = false falls back to square Images);
/// - a "Ray Surface" child on the Ignore Raycast layer: BoxCollider → ColliderSurface →
///   RayInteractable (select surface + PointableCanvas as pointable element), so any ray +
///   select source (controller trigger, hand pinch) drives the canvas through the scene's
///   EventSystem + PointableCanvasModule (created if missing);
/// - buttons (Image + Button + Text label, same tints), texts, top-down row stacking that
///   shows exactly the stacked elements and resizes panel + ray surface around them;
/// - Show/Hide: the ray interactable is disabled before the root is deactivated, so a hidden
///   panel never renders, never catches rays and leaves no active collider behind;
/// - hover tooltips (CADMenuTooltip) for buttons created with tooltip text;
/// - border drag: a grab band around the content (BorderWidth outside the canvas rect, an invisible GrabMargin beyond the visible edge, plus the
///   empty padding inside it; owners may add regions such as a title bar). A semantic pointer
///   pressing there (CADPointerInteraction.UiPressed) moves the panel rigidly with the pointer
///   until release; presses on buttons never start it. Owners call EnableBorderDrag once and
///   UpdateDrag each frame while open; Hide ends any drag;
/// - PlaceBesideBounds: the one placement rule for target-relative menus (beside the target's
///   visible bounds so the part never blocks them, at least MinMenuDistance away, facing the
///   user);
/// - LogSelectEvents: TEMP diagnostics, logs which interactor (left/right hand/controller
///   ray) selects or unselects the panel;
/// - the single-menu rule: at most one panel is open app-wide (ActivePanel). Showing a panel
///   first closes the open one through its owner's CloseRequested (so the owner's own state
///   stays consistent) and Hide clears the slot, so no menu needs to know any other menu.
/// </summary>
public sealed class CADMenuPanel
{
    // World metres per canvas unit: every menu at 75% of the style guide's 1 unit = 1 mm.
    public const float CanvasScale = 0.00075f;
    // Style guide spacing: outer padding ≈ 28 (Padding + the grab band, which is panel
    // background), 12 between related elements, 24 between sections.
    public const float Padding = 16f;
    public const float RowSpacing = 12f;
    public const float SectionSpacing = 24f;
    public const float ControlHeight = 52f;
    // Style guide type sizes (canvas units).
    public const int TitleSize = 26;
    public const int SectionSize = 20;
    public const int FontSize = 22;       // Body, buttons.
    public const int SecondarySize = 17;  // Subtitles, labels, status.
    public const int SmallSize = 15;
    /// <summary>Width of the grab band around the content (canvas units); drawn as panel background.</summary>
    public const float BorderWidth = 16f; // 12 mm at the 75% canvas scale: still easy to hit.
    /// <summary>
    /// Invisible extra grab area beyond the window's visible edge (canvas units), so a ray that
    /// just misses the edge still grabs; the edge glow shows it has been found.
    /// </summary>
    public const float GrabMargin = 20f; // 15 mm.

    // Grab affordance (Meta-style edge glow) and border drag live in CADWindowFrame, shared
    // with every CADVision window.

    /// <summary>
    /// Rounded panels and buttons (a generated 9-slice sprite). Set false before menus are built
    /// to fall back to square Images if a device shows rendering problems.
    /// </summary>
    public static bool RoundedCorners = true;

    /// <summary>Closest any automatically placed menu comes to the user's head (m).</summary>
    public static float MinMenuDistance = 0.8f;
    private const float ControlRadius = 12f;
    private const float SpriteRadius = 24f; // Pixels in the generated sprite (also its 9-slice border).
    private static Sprite roundedSprite;

    // Button fills sit under a 0.85 tint at rest so hover (full) reads brighter.
    private const float RestTint = 0.85f;

    /// <summary>TEMP diagnostics: log select / unselect / cancel on every panel with the interactor's name.</summary>
    public static bool LogSelectEvents = true;

    // The one open floating menu (single-menu rule). May refer to a destroyed or hidden panel;
    // ActivePanel filters those out, so the slot can never go stale.
    private static CADMenuPanel activePanel;

    /// <summary>The currently open floating menu panel, if any.</summary>
    public static CADMenuPanel ActivePanel => activePanel != null && activePanel.IsOpen ? activePanel : null;

    /// <summary>
    /// The owner's close (e.g. CADContextMenu.Hide, CADMainMenu.HideMainMenu), called when another
    /// panel opens. Without one the panel just hides.
    /// </summary>
    public Action CloseRequested;

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

    // Background, border, grab band, edge glow and border drag (shared with every window).
    private readonly CADWindowFrame frame;

    /// <summary>The edge glow graphic (lit around the pointer, outside the window).</summary>
    public CADMenuEdgeGlow EdgeGlow => frame.EdgeGlow;
    /// <summary>Grab-band highlight, 0 (off) to 1 (a ray on the grab band, or dragging).</summary>
    public float GrabGlow => frame.GrabGlow;

    public bool IsDragging => frame.IsDragging;
    /// <summary>The user moved the panel since it was last shown (owners then stop auto-placing it).</summary>
    public bool WasMoved => frame.WasMoved;
    /// <summary>World height of the whole panel including the grab band.</summary>
    public float OuterWorldHeight => (Height + 2 * BorderWidth) * CanvasScale * Root.transform.lossyScale.y;
    /// <summary>World width of the whole panel including the grab band.</summary>
    public float OuterWorldWidth => (Width + 2 * BorderWidth) * CanvasScale * Root.transform.lossyScale.x;
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

        // Background, border, grab handle and edge glow (CADWindowFrame): the look and the
        // border drag every CADVision window shares.
        frame = new CADWindowFrame(Root.transform, canvasRect, style, () => IsOpen);
        frame.DragStarted += () => Tooltip.Hide();

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
        interactable.WhenPointerEventRaised += TrackHover;

        Tooltip = Root.AddComponent<CADMenuTooltip>();
        Tooltip.Initialize(canvasRect, LegacyFont);

        Resize(width, 2 * Padding);
    }

    // ---------------- Show / hide ----------------

    public void Show()
    {
        // Single-menu rule: the open menu closes first (its rays, tooltip and drag go with it).
        CADMenuPanel previous = ActivePanel;
        if (previous != null && previous != this)
            previous.RequestClose();

        Root.SetActive(true);
        interactable.enabled = true;
        activePanel = this;
    }

    /// <summary>Closes the panel through its owner (keeps the owner's state consistent).</summary>
    public void RequestClose()
    {
        if (!IsOpen)
            return;
        CloseRequested?.Invoke();
        if (IsOpen)
            Hide(); // Owner didn't hide it (or has none).
    }

    public void Hide()
    {
        // Disable the interactable first so a hidden panel can never swallow ray clicks.
        interactable.enabled = false;
        frame.Reset(); // No drag or glow; the next show is placed automatically again.
        Tooltip.Hide();
        Root.SetActive(false);
        if (activePanel == this)
            activePanel = null;
    }

    // ---------------- Border drag (CADWindowFrame) ----------------

    /// <summary>Lets pointer presses on the grab band move this panel.</summary>
    public void EnableBorderDrag(CADPointerInteraction pointer) => frame.EnableBorderDrag(pointer);

    public void DisableBorderDrag() => frame.DisableBorderDrag();

    /// <summary>An extra grab area inside the content (e.g. a title bar).</summary>
    public void AddGrabRegion(RectTransform region) => frame.AddGrabRegion(region);

    /// <summary>
    /// True if a world point (a ray hit) is on this panel's grab band: outside the content's
    /// inner area (padding strip) but within the border (plus the margin outside it), or on an
    /// extra grab region. Button areas are never grab points.
    /// </summary>
    public bool IsGrabPoint(Vector3 worldPoint) => frame.IsGrabPoint(worldPoint);

    /// <summary>
    /// Follows the dragging pointer rigidly (call each frame while open); ends on release or
    /// tracking loss. Also animates the grab-band glow.
    /// </summary>
    public void UpdateDrag() => frame.UpdateDrag();

    public void EndDrag() => frame.EndDrag();

    /// <summary>Fades the glow toward on (a ray on the grab band, or dragging) or off.</summary>
    public void UpdateGrabGlow(float deltaTime) => frame.UpdateGrabGlow(deltaTime);

    /// <summary>A new target: forget the hand placement so the owner places the panel automatically.</summary>
    public void ForgetMove() => frame.ForgetMove();

    /// <summary>
    /// Corner resize (Quest style): get/set the panel's size factor; the owner applies it
    /// (usually Root's scale) and the frame keeps the opposite corner in place.
    /// </summary>
    public void EnableResize(Func<float> getScale, Action<float> setScale) => frame.EnableResize(getScale, setScale);

    public bool IsResizing => frame.IsResizing;

    /// <summary>The shared window frame (resize handles, glow, drag), for tests and owners.</summary>
    public CADWindowFrame Frame => frame;

    private void TrackHover(PointerEvent evt) => frame.TrackHover(evt);

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
        if (activePanel == this)
            activePanel = null;
        if (Root == null)
            return;
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(Root);
        else
            UnityEngine.Object.DestroyImmediate(Root);
    }

    // ---------------- Elements ----------------

    /// <summary>Stacked text (white by default; pass SecondaryTextColor for muted labels).</summary>
    public Text CreateText(string name, string value, int size = FontSize, FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter, Color? color = null)
    {
        Text text = CreateText(name, canvasRect, value, size, fontStyle, alignment);
        if (color.HasValue)
            text.color = color.Value;
        elements.Add(text.gameObject);
        return text;
    }

    /// <summary>Text inside a group (not stacked on its own; the group is).</summary>
    public Text CreateText(RectTransform parent, string name, string value, int size, FontStyle fontStyle,
        TextAnchor alignment, Color? color = null)
    {
        Text text = CreateText(name, parent, value, size, fontStyle, alignment);
        if (color.HasValue)
            text.color = color.Value;
        return text;
    }

    /// <summary>An empty stackable container; lay its children out inside it (e.g. a header).</summary>
    public RectTransform CreateGroup(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(canvasRect, false);
        elements.Add(go);
        return (RectTransform)go.transform;
    }

    /// <summary>A non-interactive image (e.g. the logo) inside a group; never a raycast target.</summary>
    public RawImage CreateRawImage(RectTransform parent, string name, Texture texture)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;
        return image;
    }

    public Color SecondaryTextColor => style.SecondaryTextColor;

    /// <summary>
    /// A panel button: navy surface with a white label (secondary), or cyan with a bold navy
    /// label (primary: the one dominant action). With tooltip text it shows that text after a
    /// short ray hover.
    /// </summary>
    public Button CreateButton(string label, UnityAction onClick, string tooltip = null, bool primary = false)
    {
        Image image = CreateImage(label, style.ButtonColor, ControlRadius);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        // Tints multiply the base fill: rest slightly dimmed (the base is the palette color
        // brightened by 1/RestTint, so rest shows the palette color), hover full, pressed darker.
        ColorBlock colors = button.colors;
        colors.normalColor = new Color(RestTint, RestTint, RestTint, 1f);
        colors.highlightedColor = Color.white;
        colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
        colors.selectedColor = new Color(RestTint, RestTint, RestTint, 1f);
        colors.disabledColor = new Color(RestTint, RestTint, RestTint, 0.45f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        if (onClick != null)
            button.onClick.AddListener(onClick);

        Text text = CreateText("Label", (RectTransform)image.transform, label, FontSize, FontStyle.Normal,
            TextAnchor.MiddleCenter);
        Stretch((RectTransform)text.transform, 8f);

        var state = button.gameObject.AddComponent<CADMenuButtonState>();
        state.Primary = primary;
        state.SurfaceBase = AtRest(style.ButtonColor);
        state.AccentBase = AtRest(style.SelectedButtonColor);
        state.LabelColor = style.TextColor;
        state.AccentLabelColor = style.PanelColor;
        Color dim = style.SecondaryTextColor;
        state.DisabledLabelColor = new Color(dim.r, dim.g, dim.b, 0.7f);
        state.Apply(button);

        if (!string.IsNullOrEmpty(tooltip))
            SetTooltip(button, tooltip);
        elements.Add(button.gameObject);
        return button;
    }

    // The base fill that shows `color` under the rest tint.
    private static Color AtRest(Color color) =>
        new Color(Mathf.Min(1f, color.r / RestTint), Mathf.Min(1f, color.g / RestTint), Mathf.Min(1f, color.b / RestTint), color.a);

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
    /// Target-relative placement shared by every contextual menu: beside the target's visible
    /// world bounds (never a Transform origin), so the part never blocks the menu.
    /// - Side: the one toward the middle of the user's view (a part on the right gets its menu
    ///   on its left), clear of the bounds' horizontal extent by sideGap.
    /// - Depth: level with the bounds' face nearest the user (in front of the part, not inside
    ///   or behind it), never closer than MinMenuDistance nor farther than maxDistance.
    /// - Height: the part's center height, kept between maxBelowEye below and maxAboveEye above
    ///   eye level.
    /// - Huge targets: the menu stays within maxViewAngle of the direction to the target.
    /// Faces the user.
    /// </summary>
    public void PlaceBesideBounds(Bounds bounds, Transform head, float sideGap = 0.06f, float maxViewAngle = 35f,
        float maxAboveEye = 0.1f, float maxBelowEye = 0.4f, float maxDistance = 1.4f)
    {
        float halfWidth = OuterWorldWidth * 0.5f;
        if (head == null)
        {
            Root.transform.position = bounds.center + Vector3.right * (bounds.extents.x + sideGap + halfWidth);
            return;
        }

        // Horizontal viewing direction to the target, and the side toward the middle of the view.
        Vector3 toTarget = Vector3.ProjectOnPlane(bounds.center - head.position, Vector3.up);
        if (toTarget.sqrMagnitude < 1e-6f)
            toTarget = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (toTarget.sqrMagnitude < 1e-6f)
            toTarget = Vector3.forward;
        Vector3 forward = toTarget.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 gazeRight = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(head.forward, Vector3.up));
        float side = Vector3.Dot(toTarget, gazeRight) > 0.02f ? -1f : 1f;

        Vector3 e = bounds.extents;
        float lateralExtent = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.z) * e.z;
        float depthExtent = Mathf.Abs(forward.x) * e.x + Mathf.Abs(forward.z) * e.z;

        float minDistance = MinMenuDistance;
        float depth = Mathf.Clamp(Vector3.Dot(bounds.center - head.position, forward) - depthExtent, minDistance, maxDistance);
        float lateral = Mathf.Min(lateralExtent + sideGap + halfWidth, depth * Mathf.Tan(maxViewAngle * Mathf.Deg2Rad));
        float height = Mathf.Clamp(bounds.center.y, head.position.y - maxBelowEye, head.position.y + maxAboveEye);

        Vector3 position = head.position + forward * depth + right * (side * lateral);
        position.y = height;

        // Never closer than the minimum (e.g. a part held right in front of the face).
        Vector3 fromHead = position - head.position;
        float distance = fromHead.magnitude;
        if (distance > 1e-4f && distance < minDistance)
            position = head.position + fromHead / distance * minDistance;

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

    /// <summary>Enabled / disabled; disabled buttons keep their shape with a dimmed label.</summary>
    public static void SetInteractable(Button button, bool value)
    {
        if (button.interactable != value)
            button.interactable = value;
        if (button.TryGetComponent(out CADMenuButtonState state))
            state.Apply(button);
    }

    /// <summary>Selected / "on" state (toggles, segments): cyan fill with a bold navy label.</summary>
    public void SetSelected(Button button, bool selected)
    {
        if (!button.TryGetComponent(out CADMenuButtonState state))
            return;
        state.Selected = selected;
        state.Apply(button);
    }

    public bool IsSelected(Button button) => button.TryGetComponent(out CADMenuButtonState state) && state.Selected;

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
        // Covers the grab band and the margin outside the window too, so presses there reach
        // the panel (and the drag).
        surfaceBox.size = CADWindowFrame.SurfaceSize(width, height);
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

    /// <summary>Rounds an Image's corners (radius in canvas units) with the shared sprite; no-op when RoundedCorners is off.</summary>
    public static void MakeRounded(Image image, float radius)
    {
        Sprite sprite = RoundedSprite;
        if (sprite == null || radius <= 0f)
            return;
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = SpriteRadius / radius;
    }

    /// <summary>The shared rounded-rect sprite (null when RoundedCorners is off).</summary>
    public static Sprite RoundedSprite
    {
        get
        {
            if (!RoundedCorners)
                return null;
            if (roundedSprite != null)
                return roundedSprite;

            // One anti-aliased white rounded rect, 9-sliced; tinted by each Image's color.
            const int side = 64;
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false)
            {
                name = "CAD Menu Rounded Rect",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color32[side * side];
            float half = side / 2f;
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - (half - SpriteRadius), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - (half - SpriteRadius), 0f);
                    float alpha = Mathf.Clamp01(SpriteRadius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    pixels[y * side + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            roundedSprite = Sprite.Create(texture, new Rect(0, 0, side, side), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(SpriteRadius, SpriteRadius, SpriteRadius, SpriteRadius));
            roundedSprite.name = "CAD Menu Rounded Rect";
            roundedSprite.hideFlags = HideFlags.DontSave;
            return roundedSprite;
        }
    }

    private Image CreateImage(string name, Color color, float radius = 0f)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(canvasRect, false);
        var image = go.AddComponent<Image>();
        image.color = color;
        if (radius > 0f)
            MakeRounded(image, radius);
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
