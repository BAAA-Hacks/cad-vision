using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The one two-hand uniform-scale algorithm, shared by the grip fallback (CADXRGrab) and
/// two-pointer pinch/trigger scaling (CADPointerInteraction). Scale follows the separation of
/// the two hands relative to the separation when the gesture began, clamped per gesture,
/// around a fixed world pivot on visible geometry. Targets are the model root (the service
/// clamps its absolute scale) or one or more CAD objects (each keeps the pivot fixed in its
/// own space, so a group scales about one common point). All writes go through the service.
/// </summary>
public sealed class CADScaleGesture
{
    public float MinimumSeparation = 0.08f; // Hands closer than this can't start a gesture.
    public float MinimumRatio = 0.1f;       // Per gesture, relative to the start size.
    public float MaximumRatio = 10f;

    private struct ObjectTarget
    {
        public string Id;
        public Transform Transform;
        public Vector3 InitialScale;
        public Vector3 PivotLocal;
    }

    private CADVisionManipulationService service;
    private readonly List<ObjectTarget> objects = new();
    private Transform modelRoot;
    private Vector3 modelPivotLocal;
    private float initialModelRatio;
    private float initialDistance;

    public bool IsActive { get; private set; }
    public bool IsModel => IsActive && modelRoot != null;
    public Vector3 PivotWorld { get; private set; }

    /// <summary>Scale the whole model root around pivotWorld.</summary>
    public bool TryBeginModel(CADVisionManipulationService manipulationService, float handDistance, Vector3 pivotWorld)
    {
        End();
        Transform root = manipulationService.ModelRoot;
        if (root == null || !CanStart(handDistance))
            return false;

        service = manipulationService;
        modelRoot = root;
        initialModelRatio = manipulationService.ModelScaleRatio;
        modelPivotLocal = root.InverseTransformPoint(pivotWorld);
        return Start(handDistance, pivotWorld);
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
                PivotLocal = target.transform.InverseTransformPoint(pivotWorld),
            });
        }

        if (objects.Count == 0)
            return false;

        service = manipulationService;
        return Start(handDistance, pivotWorld);
    }

    /// <summary>Gesture ratio for the current hand separation (1 at the start: no jump).</summary>
    public float Ratio(float handDistance) =>
        Mathf.Clamp(handDistance / Mathf.Max(initialDistance, 0.01f),
            Mathf.Clamp(MinimumRatio, 0.01f, 1f), Mathf.Max(1f, MaximumRatio));

    /// <summary>
    /// Applies the scale for this separation. Returns false (and applies nothing) if a target
    /// is gone or the model root was replaced; the caller then ends the gesture.
    /// </summary>
    public bool Update(float handDistance)
    {
        if (!IsActive || !float.IsFinite(handDistance))
            return false;

        float ratio = Ratio(handDistance);
        if (modelRoot != null)
        {
            if (service.ModelRoot != modelRoot)
                return false;
            service.SetModelScaleAroundPoint(initialModelRatio * ratio, modelPivotLocal, PivotWorld);
            return true;
        }

        foreach (ObjectTarget target in objects)
        {
            if (target.Transform == null)
                return false;
        }

        foreach (ObjectTarget target in objects)
            service.SetObjectScaleAroundPoint(target.Id, target.InitialScale * ratio, target.PivotLocal, PivotWorld);
        return true;
    }

    public void End()
    {
        IsActive = false;
        objects.Clear();
        modelRoot = null;
        service = null;
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
