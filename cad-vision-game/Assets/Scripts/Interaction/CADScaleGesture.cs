using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The one two-hand transform algorithm, shared by the grip fallback (CADXRGrab) and
/// two-pointer pinch/trigger gestures (CADPointerInteraction). Scale follows the separation of
/// the two hands relative to the separation when the gesture began, clamped per gesture,
/// around a fixed world pivot on visible geometry. Targets are the model root (the service
/// clamps its absolute scale) or one or more CAD objects (each keeps the pivot fixed in its
/// own space, so a group scales about one common point). All writes go through the service.
///
/// Given both hand positions (Update(Vector3, Vector3)), the gesture also rotates: the turn
/// of the line between the hands since the start (like a steering wheel) turns the targets
/// around the pivot. Scale and rotation run at the same time from the same two points, each
/// behind its own dead zone so a pure turn doesn't resize and a pure stretch doesn't turn
/// (below ScaleDeadZone / RotationDeadZone a channel stays exactly still; past twice the dead
/// zone it follows exactly, measured from the gesture start; in between it eases in, so it
/// never jumps). Two points can't express a roll around the line between the hands.
/// Update(float) is the scale-only path (grip fallback): no dead zone, no rotation.
/// </summary>
public sealed class CADScaleGesture
{
    public float MinimumSeparation = 0.08f; // Hands closer than this can't start a gesture.
    public float MinimumRatio = 0.1f;       // Per gesture, relative to the start size.
    public float MaximumRatio = 10f;
    public float ScaleDeadZone = 0.05f;     // Fraction of the start separation.
    public float RotationDeadZone = 6f;     // Degrees.
    public bool Rotate = true;

    private struct ObjectTarget
    {
        public string Id;
        public Transform Transform;
        public Vector3 InitialScale;
        public Quaternion InitialRotation;
        public Vector3 PivotLocal;
    }

    private CADVisionManipulationService service;
    private CADMultiplayerCoordinator multiplayer;
    private readonly List<ObjectTarget> objects = new();
    private Transform modelRoot;
    private Vector3 modelPivotLocal;
    private Quaternion initialModelRotation;
    private float initialModelRatio;
    private float initialDistance;
    private Vector3 initialAxis; // Second hand minus first, at the start.

    public bool IsActive { get; private set; }
    public bool IsModel => IsActive && modelRoot != null;
    public Vector3 PivotWorld { get; private set; }
    /// <summary>Scale applied now, relative to the start (after the dead zone).</summary>
    public float AppliedRatio { get; private set; } = 1f;
    /// <summary>Rotation applied now, in degrees (after the dead zone).</summary>
    public float AppliedAngle { get; private set; }

    /// <summary>Scale the whole model root around pivotWorld.</summary>
    public bool TryBeginModel(CADVisionManipulationService manipulationService, float handDistance, Vector3 pivotWorld)
    {
        End();
        Transform root = manipulationService.ModelRoot;
        if (root == null || !CanStart(handDistance))
            return false;

        service = manipulationService;
        multiplayer = service.GetComponent<CADMultiplayerCoordinator>();
        multiplayer?.RequestLease(new[] { CADMultiplayerLeaseTable.ModelId });
        modelRoot = root;
        initialModelRatio = manipulationService.ModelScaleRatio;
        initialModelRotation = root.rotation;
        modelPivotLocal = root.InverseTransformPoint(pivotWorld);
        return Start(handDistance, pivotWorld);
    }

    /// <summary>Scale and rotate the whole model root with the hands at first and second.</summary>
    public bool TryBeginModel(CADVisionManipulationService manipulationService, Vector3 first, Vector3 second,
        Vector3 pivotWorld)
    {
        if (!TryBeginModel(manipulationService, Vector3.Distance(first, second), pivotWorld))
            return false;
        initialAxis = second - first;
        return true;
    }

    /// <summary>Scale CAD objects (pass transform roots, so nothing scales twice) around pivotWorld.</summary>
    public bool TryBeginObjects(CADVisionManipulationService manipulationService, IEnumerable<CADObject> targets,
        float handDistance, Vector3 pivotWorld)
    {
        End();
        if (!CanStart(handDistance))
            return false;

        foreach (CADObject target in targets)
        {
            if (target == null)
                continue;
            objects.Add(new ObjectTarget
            {
                Id = target.id,
                Transform = target.transform,
                InitialScale = target.transform.localScale,
                InitialRotation = target.transform.rotation,
                PivotLocal = target.transform.InverseTransformPoint(pivotWorld),
            });
        }

        if (objects.Count == 0)
            return false;

        service = manipulationService;
        multiplayer = service.GetComponent<CADMultiplayerCoordinator>();
        multiplayer?.RequestLease(objects.ConvertAll(target => target.Id));
        return Start(handDistance, pivotWorld);
    }

    /// <summary>Scale and rotate CAD objects with the hands at first and second.</summary>
    public bool TryBeginObjects(CADVisionManipulationService manipulationService, IEnumerable<CADObject> targets,
        Vector3 first, Vector3 second, Vector3 pivotWorld)
    {
        if (!TryBeginObjects(manipulationService, targets, Vector3.Distance(first, second), pivotWorld))
            return false;
        initialAxis = second - first;
        return true;
    }

    /// <summary>Gesture ratio for the current hand separation (1 at the start: no jump).</summary>
    public float Ratio(float handDistance) =>
        Mathf.Clamp(handDistance / Mathf.Max(initialDistance, 0.01f),
            Mathf.Clamp(MinimumRatio, 0.01f, 1f), Mathf.Max(1f, MaximumRatio));

    /// <summary>
    /// Scale only, for this separation, with no dead zone. Returns false (and applies nothing)
    /// if a target is gone or the model root was replaced; the caller then ends the gesture.
    /// </summary>
    public bool Update(float handDistance)
    {
        if (!IsActive || !float.IsFinite(handDistance))
            return false;
        return Apply(Ratio(handDistance), null);
    }

    /// <summary>Scale and rotate for the hands at first and second (dead zones applied).</summary>
    public bool Update(Vector3 first, Vector3 second)
    {
        if (!IsActive)
            return false;

        float distance = Vector3.Distance(first, second);
        if (!float.IsFinite(distance))
            return false;

        // Shared room without the lease yet: nothing moves; the gesture keeps re-baselining so
        // it starts from here (no jump) once the lease arrives.
        if (multiplayer != null && multiplayer.IsInRoom &&
            !multiplayer.HasLease(modelRoot != null
                ? new[] { CADMultiplayerLeaseTable.ModelId }
                : objects.ConvertAll(target => target.Id)))
        {
            initialDistance = distance;
            initialAxis = second - first;
            if (modelRoot != null)
            {
                initialModelRatio = service.ModelScaleRatio;
                initialModelRotation = modelRoot.rotation;
            }
            else
                for (int i = 0; i < objects.Count; i++)
                {
                    ObjectTarget target = objects[i];
                    if (target.Transform == null) return false;
                    target.InitialScale = target.Transform.localScale;
                    target.InitialRotation = target.Transform.rotation;
                    objects[i] = target;
                }
            return true;
        }

        // Scale in log space, so growing and shrinking have the same dead zone.
        float log = Mathf.Log(Ratio(distance));
        float ratio = Mathf.Exp(Mathf.Sign(log) * SoftDeadZone(Mathf.Abs(log), Mathf.Log(1f + Mathf.Max(0f, ScaleDeadZone))));

        Quaternion? turn = null;
        Vector3 axis = second - first;
        if (Rotate && axis.sqrMagnitude > 1e-6f && initialAxis.sqrMagnitude > 1e-6f)
        {
            Quaternion.FromToRotation(initialAxis, axis).ToAngleAxis(out float angle, out Vector3 turnAxis);
            if (angle > 180f)
                angle -= 360f;
            float applied = Mathf.Sign(angle) * SoftDeadZone(Mathf.Abs(angle), Mathf.Max(0f, RotationDeadZone));
            turn = float.IsFinite(turnAxis.x) && turnAxis.sqrMagnitude > 1e-6f
                ? Quaternion.AngleAxis(applied, turnAxis)
                : Quaternion.identity;
            AppliedAngle = Mathf.Abs(applied);
        }

        return Apply(ratio, turn);
    }

    /// <summary>
    /// 0 up to deadZone, value itself from twice the dead zone on, and a linear ease between
    /// (continuous: no jump where it starts following).
    /// </summary>
    public static float SoftDeadZone(float value, float deadZone)
    {
        if (value <= deadZone)
            return 0f;
        return value >= 2f * deadZone ? value : 2f * (value - deadZone);
    }

    // Sets rotation (if any) first, then the scale around the pivot, which also puts the pivot
    // back in place: together a turn and a resize around the pivot.
    private bool Apply(float ratio, Quaternion? turn)
    {
        if (modelRoot != null)
        {
            if (service.ModelRoot != modelRoot)
                return false;
            if (turn.HasValue)
                service.SetModelWorldPose(modelRoot.position, turn.Value * initialModelRotation);
            service.SetModelScaleAroundPoint(initialModelRatio * ratio, modelPivotLocal, PivotWorld);
            AppliedRatio = ratio;
            return true;
        }

        foreach (ObjectTarget target in objects)
        {
            if (target.Transform == null)
                return false;
        }

        foreach (ObjectTarget target in objects)
        {
            if (turn.HasValue)
                service.SetObjectWorldPose(target.Id, target.Transform.position, turn.Value * target.InitialRotation);
            service.SetObjectScaleAroundPoint(target.Id, target.InitialScale * ratio, target.PivotLocal, PivotWorld);
        }
        AppliedRatio = ratio;
        return true;
    }

    public void End()
    {
        if (modelRoot != null)
            multiplayer?.ReleaseLease(new[] { CADMultiplayerLeaseTable.ModelId });
        else if (objects.Count > 0)
            multiplayer?.ReleaseLease(objects.ConvertAll(target => target.Id));
        IsActive = false;
        objects.Clear();
        modelRoot = null;
        service = null;
        initialAxis = Vector3.zero;
        AppliedRatio = 1f;
        AppliedAngle = 0f;
    }

    private bool CanStart(float handDistance) =>
        float.IsFinite(handDistance) && handDistance >= Mathf.Max(0.01f, MinimumSeparation);

    private bool Start(float handDistance, Vector3 pivotWorld)
    {
        initialDistance = handDistance;
        PivotWorld = pivotWorld;
        IsActive = true;
        return true;
    }
}
