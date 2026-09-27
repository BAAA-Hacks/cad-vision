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
///   pinch) pressing a grab point moves the window's root rigidly with that pointer until it
///   releases or loses tracking;
/// - glow: while a ray hovers a grab point (or drags), the part of the border nearest the ray
///   lights up.
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
    private Vector3 dragPositionOffset;
    private Quaternion dragRotationOffset;
    private Vector3 spotPoint;

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
        if (canInteract() && dragSource == null && IsGrabPoint(hitPoint))
            BeginDrag(source);
    }

    private void BeginDrag(ICADPointerSource source)
    {
        Pose pose = source.Pose;
        Quaternion inverse = Quaternion.Inverse(pose.rotation);
        dragPositionOffset = inverse * (root.position - pose.position);
        dragRotationOffset = inverse * root.rotation;
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
        UpdateGrabGlow(Time.unscaledDeltaTime);
        if (dragSource == null)
            return;

        if (!canInteract() || !dragSource.IsAvailable || !dragSource.IsSelecting)
        {
            EndDrag();
            return;
        }

        Pose pose = dragSource.Pose;
        root.SetPositionAndRotation(pose.position + pose.rotation * dragPositionOffset,
            pose.rotation * dragRotationOffset);
        WasMoved = true;
    }

    public void EndDrag() => dragSource = null;

    /// <summary>A new placement: forget the hand placement so the owner places the window again.</summary>
    public void ForgetMove() => WasMoved = false;

    /// <summary>Hidden: no drag, no hover, no glow, next show placed automatically.</summary>
    public void Reset()
    {
        EndDrag();
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

    /// <summary>Fades the glow toward on (a ray on a grab point, or dragging) or off.</summary>
    public void UpdateGrabGlow(float deltaTime)
    {
        bool hot = IsDragging;
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
