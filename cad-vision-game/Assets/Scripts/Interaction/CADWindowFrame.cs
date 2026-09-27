using System;
using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The window chrome and movement every CADVision window shares (CADMenuPanel menus and the
/// CADEN panel), so all of them look and move the same:
///
/// - look: an opaque navy background reaching BorderWidth past the window rect (the grab
///   band), a thin border line, a grab-handle pill in the bottom band, and the edge glow
///   (CADMenuEdgeGlow) drawn only outside the window;
/// - grab points: the band, an invisible GrabMargin beyond the visible edge, the padding strip
///   just inside the window, and extra grab regions (e.g. a title bar); never a button or other
///   control;
/// - drag: a semantic pointer (CADPointerInteraction.UiPressed: controller trigger or hand
///   pinch) pressing a grab point moves the window's root with that pointer until it releases
///   or loses tracking, the same way CAD objects move (CADGrabSession: rigid pickup, and
///   beyond ReachDistance pushing/pulling along the ray is amplified, so a small flick sends
///   the window away or reels it in; sideways and up/down stay 1:1). Gentler than objects
///   (windows sit about 1 m away, so the boost is always on), and the window stays between
///   MinDistance and MaxDistance from the head;
/// - glow: while a ray hovers a grab point (or drags), the part of the border nearest the ray
///   lights up;
/// - resize (Quest style, once the owner calls EnableResize): a corner handle outside each of
///   the four corners fades in as a ray comes near and turns cyan on it. Pressing a corner and
///   dragging zooms the whole window (content included) between MinScale and MaxScale, the
///   opposite corner staying put: the pressed point follows the ray on the window's plane.
///   Corners win over moving.
///
/// The owner builds the canvas, window rect and ray surface; it forwards the surface's pointer
/// events to TrackHover, calls UpdateDrag each frame while the window shows, and sizes the
/// surface with SurfaceSize. Windows are built in canvas units (CADMenuPanel.CanvasScale).
/// </summary>
public sealed class CADWindowFrame
{
    public const float BorderWidth = CADMenuPanel.BorderWidth;
    public const float GrabMargin = CADMenuPanel.GrabMargin;
    public const float PanelRadius = 20f;
    private const float GlowSize = 16f;       // Soft glow outside the border (canvas units).
    private const float GlowFadeTime = 0.12f; // Seconds to fade fully in or out.

    private readonly Transform root;
    private readonly RectTransform window;
    private readonly Func<bool> canInteract;
    private readonly List<RectTransform> grabRegions = new();
    private readonly Dictionary<int, Vector3> hoverPoints = new();
    private readonly CADMenuEdgeGlow edgeGlow;

    private CADPointerInteraction dragPointer;
    private ICADPointerSource dragSource;
    private readonly CADGrabSession dragSession = new CADGrabSession(); // Same math as CAD objects.
    private Vector3 spotPoint;

    /// <summary>Hold distance (m, pointer to grabbed point) beyond which push/pull is amplified.</summary>
    public float ReachDistance = 0.7f;
    /// <summary>Largest extra push/pull gain (1 = at most 2× the hand's depth movement).</summary>
    public float MaxExtraGain = 1f;
    /// <summary>How quickly the gain rises beyond reach (per metre).</summary>
    public float DecayRate = 1.5f;
    /// <summary>Closest a dragged window comes to the head (m).</summary>
    public float MinDistance = 0.4f;
    /// <summary>Farthest a dragged window goes from the head (m).</summary>
    public float MaxDistance = 3f;

    // Resize: corner zones (canvas units: from the window corner outward to the end of the grab
    // margin, and CornerInside inward), handle look, and the drag state.
    private const float CornerInside = 18f;
    private const float HandleLength = 30f, HandleThickness = 6f, HandleGap = 7f;
    private const float HandleShowDistance = 40f, HandleFadeDistance = 60f;
    private readonly RectTransform[] handles = new RectTransform[4];
    private readonly Image[][] handleBars = new Image[4][];
    private readonly Color handleRest, handleHot;
    private Func<float> scaleGetter;
    private Action<float> scaleSetter;
    private ICADPointerSource resizeSource;
    private int resizeCorner = -1;
    private Vector3 resizeAnchorLocal, resizeAnchorWorld, resizeNormal, resizeDiagonal;
    private float resizePressProjection, resizeStartScale;

    /// <summary>Smallest and largest window size (× its normal size).</summary>
    public float MinScale = 0.6f;
    public float MaxScale = 1.6f;
    /// <summary>A resize ended at this size (owners remember it).</summary>
    public event Action<float> Resized;
    public bool CanResize => scaleGetter != null && scaleSetter != null;
    public bool IsResizing => resizeSource != null;

    /// <summary>Padding strip just inside the window that also grabs (canvas units).</summary>
    public float InnerGrabInset = CADMenuPanel.Padding - 2f;
    /// <summary>A drag started (owners hide tooltips and the like).</summary>
    public event Action DragStarted;

    public CADMenuEdgeGlow EdgeGlow => edgeGlow;
    /// <summary>Grab-band highlight, 0 (off) to 1 (a ray on a grab point, or dragging).</summary>
    public float GrabGlow { get; private set; }
    public bool IsDragging => dragSource != null;
    /// <summary>The user moved the window since the last Reset (owners then stop auto-placing it).</summary>
    public bool WasMoved { get; private set; }

    /// <summary>
    /// Adds the frame's background, border, handle and glow as the window's first children
    /// (behind its content). canInteract says whether the window currently shows and accepts
    /// grabs.
    /// </summary>
    public CADWindowFrame(Transform root, RectTransform window, CADMenuStyle style, Func<bool> canInteract)
    {
        this.root = root;
        this.window = window;
        this.canInteract = canInteract;

        // Opaque navy panel reaching over the grab band, with a thin border outline: readable
        // over passthrough and models. The band is ordinary panel background (the drag handle).
        Image border = CreateImage("Border", style.BorderColor, PanelRadius + 2f);
        Stretch((RectTransform)border.transform, -BorderWidth - 2f);
        Image background = CreateImage("Background", style.PanelColor, PanelRadius);
        Stretch((RectTransform)background.transform, -BorderWidth);

        // A short pill in the bottom band, like a system window's grab bar.
        Color handleColor = style.SecondaryTextColor;
        handleColor.a = 0.45f;
        Image handle = CreateImage("Grab Handle", handleColor, 3f);
        handle.raycastTarget = false;
        var handleRect = (RectTransform)handle.transform;
        handleRect.anchorMin = handleRect.anchorMax = new Vector2(0.5f, 0f);
        handleRect.pivot = new Vector2(0.5f, 0.5f);
        handleRect.sizeDelta = new Vector2(64f, 6f);
        handleRect.anchoredPosition = new Vector2(0f, -BorderWidth * 0.5f);

        // Edge glow: only outside the background, only near the pointer.
        var glowObject = new GameObject("Grab Glow", typeof(RectTransform));
        glowObject.transform.SetParent(window, false);
        edgeGlow = glowObject.AddComponent<CADMenuEdgeGlow>();
        edgeGlow.raycastTarget = false;
        edgeGlow.color = style.SelectedButtonColor;
        edgeGlow.Inset = BorderWidth;
        edgeGlow.Radius = CADMenuPanel.RoundedCorners ? PanelRadius : 0f;
        edgeGlow.GlowSize = GlowSize;
        Stretch((RectTransform)glowObject.transform, 0f);

        // Corner resize handles, hidden until EnableResize and a ray comes near.
        handleRest = style.SecondaryTextColor;
        handleHot = style.SelectedButtonColor;
        for (int corner = 0; corner < 4; corner++)
            BuildHandle(corner);

        // Behind everything the owner adds (or already added).
        glowObject.transform.SetAsFirstSibling();
        handle.transform.SetAsFirstSibling();
        background.transform.SetAsFirstSibling();
        border.transform.SetAsFirstSibling();
    }

    /// <summary>
    /// Ray surface size for a window of width × height canvas units: the window, its grab band
    /// and the grab margin outside the visible edge, so presses there reach the window.
    /// </summary>
    public static Vector3 SurfaceSize(float width, float height)
    {
        float grab = 2 * (BorderWidth + 2f + GrabMargin);
        return new Vector3((width + grab) * CADMenuPanel.CanvasScale, (height + grab) * CADMenuPanel.CanvasScale, 0.004f);
    }

    // ---------------- Grab points ----------------

    /// <summary>An extra grab area inside the window (e.g. a title bar); controls on it still win.</summary>
    public void AddGrabRegion(RectTransform region)
    {
        if (region != null && !grabRegions.Contains(region))
            grabRegions.Add(region);
    }

    /// <summary>
    /// True if a world point (a ray hit) is a grab point: on the band or the margin outside the
    /// window, on the padding strip just inside it, or on a grab region; never on a control.
    /// </summary>
    public bool IsGrabPoint(Vector3 worldPoint)
    {
        Vector3 local = window.InverseTransformPoint(worldPoint);
        if (Mathf.Abs(local.z) > 30f)
            return false; // Not on this window's plane.

        Rect content = window.rect;
        float reach = BorderWidth + 2f + GrabMargin; // Band, border line and the margin outside.
        var outer = Rect.MinMaxRect(content.xMin - reach, content.yMin - reach, content.xMax + reach, content.yMax + reach);
        if (!outer.Contains(local))
            return false;

        // Controls sit inside the padding, so the band and the strip need no control check.
        float inset = InnerGrabInset;
        var inner = Rect.MinMaxRect(content.xMin + inset, content.yMin + inset, content.xMax - inset, content.yMax - inset);
        if (!inner.Contains(local))
            return true;

        foreach (RectTransform region in grabRegions)
        {
            if (region != null && region.gameObject.activeInHierarchy && CADMenuPanel.Contains(region, worldPoint, 0f))
                return !IsOnControl(worldPoint);
        }
        return false;
    }

    // Buttons, input fields and other controls are never grab points.
    private bool IsOnControl(Vector3 worldPoint)
    {
        foreach (Selectable control in root.GetComponentsInChildren<Selectable>())
        {
            if (control.IsInteractable() && CADMenuPanel.Contains((RectTransform)control.transform, worldPoint, 0f))
                return true;
        }
        return false;
    }

    // ---------------- Drag ----------------

    /// <summary>Lets pointer presses on grab points move the window.</summary>
    public void EnableBorderDrag(CADPointerInteraction pointer)
    {
        if (pointer == dragPointer)
            return;
        DisableBorderDrag();
        dragPointer = pointer;
        if (dragPointer != null)
            dragPointer.UiPressed += OnUiPressed;
    }

    public void DisableBorderDrag()
    {
        if (dragPointer != null)
            dragPointer.UiPressed -= OnUiPressed;
        dragPointer = null;
        EndDrag();
    }

    private void OnUiPressed(ICADPointerSource source, Vector3 hitPoint)
    {
        if (!canInteract() || dragSource != null || resizeSource != null)
            return;
        if (CanResize && IsResizePoint(hitPoint, out int corner))
            BeginResize(source, hitPoint, corner);
        else if (IsGrabPoint(hitPoint))
            BeginDrag(source, hitPoint);
    }

    private void BeginDrag(ICADPointerSource source, Vector3 hitPoint)
    {
        dragSession.ReachDistance = ReachDistance;
        dragSession.MaxExtraGain = MaxExtraGain;
        dragSession.DecayRate = DecayRate;
        dragSession.BeginStandalone(root, source.Pose, hitPoint); // Held at the pressed point.
        dragSource = source;
        DragStarted?.Invoke();
        Debug.Log($"[CADWindowFrame] '{root.name}' moved with {source.SourceId}.");
    }

    /// <summary>
    /// Follows the dragging pointer rigidly (call each frame while the window shows); ends on
    /// release or tracking loss. Also animates the glow.
    /// </summary>
    public void UpdateDrag()
    {
        UpdateResize();
        UpdateGrabGlow(Time.unscaledDeltaTime);
        if (dragSource == null)
            return;

        if (!canInteract() || !dragSource.IsAvailable || !dragSource.IsSelecting)
        {
            EndDrag();
            return;
        }

        Pose pose = dragSource.Pose;
        if (!dragSession.Update(pose))
        {
            EndDrag();
            return;
        }
        KeepWithinReach(pose);
        WasMoved = true;
    }

    // Never closer than MinDistance nor farther than MaxDistance from the head (along the line
    // from the head); the pickup then continues from the clamped pose.
    private void KeepWithinReach(Pose pointer)
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        Vector3 head = camera.transform.position;
        Vector3 fromHead = root.position - head;
        float distance = fromHead.magnitude;
        float clamped = Mathf.Clamp(distance, MinDistance, Mathf.Max(MinDistance, MaxDistance));
        if (distance < 1e-4f || Mathf.Approximately(distance, clamped))
            return;

        root.position = head + fromHead / distance * clamped;
        dragSession.Rebase(pointer);
    }

    public void EndDrag()
    {
        dragSource = null;
        dragSession.End();
    }

    /// <summary>A new placement: forget the hand placement so the owner places the window again.</summary>
    public void ForgetMove() => WasMoved = false;

    /// <summary>Hidden: no drag, no hover, no glow, next show placed automatically.</summary>
    public void Reset()
    {
        EndDrag();
        EndResize();
        WasMoved = false;
        hoverPoints.Clear();
        SetGrabGlow(0f);
    }

    // ---------------- Glow ----------------

    /// <summary>The window's ray surface events: where each hovering pointer's ray hits.</summary>
    public void TrackHover(PointerEvent evt)
    {
        switch (evt.Type)
        {
            case PointerEventType.Hover:
            case PointerEventType.Move:
            case PointerEventType.Select:
            case PointerEventType.Unselect:
                hoverPoints[evt.Identifier] = evt.Pose.position;
                break;
            case PointerEventType.Unhover:
            case PointerEventType.Cancel:
                hoverPoints.Remove(evt.Identifier);
                break;
        }
    }

    /// <summary>Fades the glow toward on (a ray on a grab point, dragging or resizing) or off.</summary>
    public void UpdateGrabGlow(float deltaTime)
    {
        UpdateHandles();
        bool hot = IsDragging || IsResizing;
        foreach (Vector3 point in hoverPoints.Values)
        {
            if (IsGrabPoint(point))
            {
                hot = true;
                spotPoint = point;
                break;
            }
        }

        float target = hot && canInteract() ? 1f : 0f;
        SetGrabGlow(Mathf.MoveTowards(GrabGlow, target, deltaTime / GlowFadeTime));
    }

    private void SetGrabGlow(float glow)
    {
        GrabGlow = glow;
        // The glow's own space: its origin (rect center) differs from the window's pivot when the
        // window isn't pivoted at its center (e.g. the CADEN panel, pivoted at its logo).
        Vector3 local = edgeGlow.rectTransform.InverseTransformPoint(spotPoint);
        edgeGlow.SetState(glow, new Vector2(local.x, local.y));
    }

    // ---------------- Resize ----------------

    /// <summary>
    /// Lets the corners resize the window: get/set its size factor (1 = normal). The owner
    /// applies the factor (scaling the window about any point); the frame keeps the opposite
    /// corner in place by moving the root.
    /// </summary>
    public void EnableResize(Func<float> getScale, Action<float> setScale)
    {
        scaleGetter = getScale;
        scaleSetter = setScale;
    }

    // Corners 0..3: top-left, top-right, bottom-right, bottom-left.
    private static Vector2 CornerSign(int corner) => corner switch
    {
        0 => new Vector2(-1f, 1f),
        1 => new Vector2(1f, 1f),
        2 => new Vector2(1f, -1f),
        _ => new Vector2(-1f, -1f),
    };

    private Vector3 CornerLocal(int corner)
    {
        Rect r = window.rect;
        Vector2 sign = CornerSign(corner);
        return new Vector3(sign.x < 0 ? r.xMin : r.xMax, sign.y < 0 ? r.yMin : r.yMax, 0f);
    }

    /// <summary>True if a world point (a ray hit) is on a corner resize handle (see the class summary).</summary>
    public bool IsResizePoint(Vector3 worldPoint, out int corner)
    {
        corner = -1;
        Vector3 local = window.InverseTransformPoint(worldPoint);
        if (Mathf.Abs(local.z) > 30f)
            return false;

        float reach = BorderWidth + 2f + GrabMargin;
        for (int c = 0; c < 4; c++)
        {
            Vector3 cornerLocal = CornerLocal(c);
            Vector2 sign = CornerSign(c);
            // Outward along each axis up to the end of the grab margin, inward CornerInside.
            float dx = (local.x - cornerLocal.x) * sign.x;
            float dy = (local.y - cornerLocal.y) * sign.y;
            if (dx >= -CornerInside && dx <= reach && dy >= -CornerInside && dy <= reach &&
                (dx >= 0f || dy >= 0f || !IsOnControl(worldPoint)))
            {
                corner = c;
                return true;
            }
        }
        return false;
    }

    private void BeginResize(ICADPointerSource source, Vector3 hitPoint, int corner)
    {
        resizeCorner = corner;
        resizeAnchorLocal = CornerLocal((corner + 2) % 4); // The opposite corner stays put.
        resizeAnchorWorld = window.TransformPoint(resizeAnchorLocal);
        resizeNormal = window.forward;
        resizeDiagonal = window.TransformPoint(CornerLocal(corner)) - resizeAnchorWorld;
        resizePressProjection = Vector3.Dot(hitPoint - resizeAnchorWorld, resizeDiagonal);
        resizeStartScale = scaleGetter();
        if (resizePressProjection <= 1e-8f)
            return;
        resizeSource = source;
        DragStarted?.Invoke();
        Debug.Log($"[CADWindowFrame] '{root.name}' resized with {source.SourceId}.");
    }

    // The pressed point follows the ray on the window's plane (fixed at the press), measured
    // along the diagonal from the anchor corner: its distance ratio is the size ratio.
    private void UpdateResize()
    {
        if (resizeSource == null)
            return;

        if (!canInteract() || !CanResize || !resizeSource.IsAvailable || !resizeSource.IsSelecting)
        {
            EndResize();
            return;
        }

        Pose pose = resizeSource.Pose;
        Vector3 direction = pose.rotation * Vector3.forward;
        float denominator = Vector3.Dot(direction, resizeNormal);
        if (Mathf.Abs(denominator) < 1e-4f)
            return; // Ray parallel to the window: keep the size.
        float distance = Vector3.Dot(resizeAnchorWorld - pose.position, resizeNormal) / denominator;
        if (distance <= 0f)
            return;

        Vector3 onPlane = pose.position + direction * distance;
        float ratio = Vector3.Dot(onPlane - resizeAnchorWorld, resizeDiagonal) / resizePressProjection;
        float size = Mathf.Clamp(resizeStartScale * ratio, MinScale, Mathf.Max(MinScale, MaxScale));
        scaleSetter(size);
        root.position += resizeAnchorWorld - window.TransformPoint(resizeAnchorLocal);
        WasMoved = true;
    }

    public void EndResize()
    {
        if (resizeSource == null)
            return;
        resizeSource = null;
        resizeCorner = -1;
        if (CanResize)
            Resized?.Invoke(scaleGetter());
    }

    // An L around each corner, in the grab margin just outside the border.
    private void BuildHandle(int corner)
    {
        Vector2 sign = CornerSign(corner);
        var go = new GameObject($"Resize Handle {corner}", typeof(RectTransform));
        go.transform.SetParent(window, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(sign.x < 0 ? 0f : 1f, sign.y < 0 ? 0f : 1f);
        rect.sizeDelta = Vector2.zero;
        float offset = BorderWidth + 2f + HandleGap;
        rect.anchoredPosition = sign * offset;

        float along = HandleLength * 0.5f - HandleThickness * 0.5f;
        Image horizontal = HandleBar(rect, new Vector2(HandleLength, HandleThickness), new Vector2(-sign.x * along, 0f));
        Image vertical = HandleBar(rect, new Vector2(HandleThickness, HandleLength), new Vector2(0f, -sign.y * along));
        handles[corner] = rect;
        handleBars[corner] = new[] { horizontal, vertical };
        go.SetActive(false);
    }

    private Image HandleBar(RectTransform parent, Vector2 size, Vector2 center)
    {
        var go = new GameObject("Bar", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        CADMenuPanel.MakeRounded(image, HandleThickness * 0.5f);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = center;
        return image;
    }

    // Each handle fades in as a ray comes near its corner, turns cyan on it or while resizing
    // from it.
    private void UpdateHandles()
    {
        for (int corner = 0; corner < 4; corner++)
        {
            float visibility = 0f;
            bool hot = corner == resizeCorner;
            if (CanResize && canInteract())
            {
                Vector3 cornerLocal = CornerLocal(corner);
                foreach (Vector3 point in hoverPoints.Values)
                {
                    Vector3 local = window.InverseTransformPoint(point);
                    float near = Vector2.Distance(new Vector2(local.x, local.y), new Vector2(cornerLocal.x, cornerLocal.y));
                    visibility = Mathf.Max(visibility, 1f - Mathf.Clamp01((near - HandleShowDistance) / HandleFadeDistance));
                    if (IsResizePoint(point, out int onCorner) && onCorner == corner)
                        hot = true;
                }
                if (hot)
                    visibility = 1f;
            }

            bool show = visibility > 0f;
            if (handles[corner].gameObject.activeSelf != show)
                handles[corner].gameObject.SetActive(show);
            if (!show)
                continue;
            Color color = hot ? handleHot : handleRest;
            color.a *= visibility;
            foreach (Image bar in handleBars[corner])
                bar.color = color;
        }
    }

    /// <summary>Tests: how visible a corner's handle is (0 hidden .. 1) and whether it is lit.</summary>
    public float HandleVisibility(int corner, out bool lit)
    {
        Image bar = handleBars[corner][0];
        lit = handles[corner].gameObject.activeSelf && bar.color.r == handleHot.r && bar.color.g == handleHot.g;
        return handles[corner].gameObject.activeSelf ? bar.color.a / Mathf.Max(handleRest.a, 1e-4f) : 0f;
    }

    // ---------------- Helpers ----------------

    private Image CreateImage(string name, Color color, float radius)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(window, false);
        var image = go.AddComponent<Image>();
        image.color = color;
        CADMenuPanel.MakeRounded(image, radius);
        return image;
    }

    private static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }
}
