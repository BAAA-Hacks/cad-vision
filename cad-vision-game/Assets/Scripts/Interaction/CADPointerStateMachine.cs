using UnityEngine;

/// <summary>
/// Input-agnostic press/click/drag interpretation for one pointer. Fed "down / move / up /
/// cancel" with the pointer pose; returns what happened. Knows nothing about controllers,
/// hands, Meta SDK or CAD logic (plain C#, so it can be unit-tested).
/// </summary>
public sealed class CADPointerStateMachine
{
    public enum State { Idle, Pressed, Dragging }

    public enum Intent
    {
        None,
        BeginDrag,  // Pressed on CAD, moved past the threshold.
        EndDrag,    // Released (or cancelled) while dragging.
        ClickCad,   // Short press + release on CAD without dragging.
        ClickEmpty, // Short press + release on empty world without sweeping.
        ClickUi,    // Press + release on UI (informational; never affects CAD selection).
    }

    // XR-friendly defaults. Squeezing a trigger (or pinching) jerks the pointer by a few cm /
    // degrees in the first ~100 ms, so a drag also needs a minimum hold time.
    public float DragStartDistance = 0.03f; // Pointer origin travel (m).
    public float DragStartAngle = 6f;       // Pointer direction change (degrees).
    public float DragStartDelay = 0.15f;    // Minimum hold (s) before movement can start a drag...
    public float FastDragFactor = 3f;       // ...unless it moves this many times the threshold.
    public float ClickMaxDuration = 1.0f;   // Longer presses without drag do nothing.

    public State Current { get; private set; } = State.Idle;
    public CADPointerTargetKind PressKind { get; private set; }
    public string PressCadId { get; private set; }
    public Vector3 PressHitPoint { get; private set; }
    public bool PressHasHitPoint { get; private set; }

    private Pose pressPose;
    private float pressTime;
    private bool movedPastThreshold;

    public void Down(CADPointerTargetKind kind, string cadId, Vector3? hitPoint, Pose pose, float time)
    {
        Current = State.Pressed;
        PressKind = kind;
        PressCadId = kind == CADPointerTargetKind.Cad ? cadId : null;
        PressHasHitPoint = hitPoint.HasValue;
        PressHitPoint = hitPoint ?? pose.position;
        pressPose = pose;
        pressTime = time;
        movedPastThreshold = false;
    }

    public Intent Move(Pose pose, float time)
    {
        if (Current != State.Pressed)
            return Intent.None;

        float distance = Vector3.Distance(pose.position, pressPose.position);
        float angle = Vector3.Angle(pose.rotation * Vector3.forward, pressPose.rotation * Vector3.forward);

        bool pastThreshold = distance >= DragStartDistance || angle >= DragStartAngle;
        // Latched only to reject empty-space sweeps as clicks.
        if (pastThreshold)
            movedPastThreshold = true;

        // Uses the current displacement, so a squeeze jolt that settles back stays a click.
        // A deliberate yank past the (larger) fast threshold starts immediately.
        bool heldLongEnough = time - pressTime >= DragStartDelay;
        bool fastDrag = distance >= DragStartDistance * FastDragFactor ||
            angle >= DragStartAngle * FastDragFactor;

        if (pastThreshold && PressKind == CADPointerTargetKind.Cad && (heldLongEnough || fastDrag))
        {
            Current = State.Dragging;
            return Intent.BeginDrag;
        }

        return Intent.None;
    }

    /// <summary>
    /// Promotes a pending press on CAD to a drag without waiting for movement (a second pointer
    /// joined to scale). No click can follow: the release is an EndDrag.
    /// </summary>
    public Intent BeginDragNow()
    {
        if (Current != State.Pressed || PressKind != CADPointerTargetKind.Cad)
            return Intent.None;

        Current = State.Dragging;
        return Intent.BeginDrag;
    }

    public Intent Up(float time)
    {
        State previous = Current;
        Current = State.Idle;

        if (previous == State.Dragging)
            return Intent.EndDrag;

        if (previous != State.Pressed)
            return Intent.None;

        switch (PressKind)
        {
            case CADPointerTargetKind.Ui:
                return Intent.ClickUi;
            case CADPointerTargetKind.Cad:
                return time - pressTime <= ClickMaxDuration ? Intent.ClickCad : Intent.None;
            default:
                // A sweeping press across empty space is not a deliberate click.
                return !movedPastThreshold && time - pressTime <= ClickMaxDuration
                    ? Intent.ClickEmpty : Intent.None;
        }
    }

    /// <summary>Pointer lost mid-press (tracking lost, interactor disabled): no click.</summary>
    public Intent Cancel()
    {
        State previous = Current;
        Current = State.Idle;
        return previous == State.Dragging ? Intent.EndDrag : Intent.None;
    }
}
