using System.Linq;
using UnityEngine;

/// <summary>
/// One rigid "remote pickup" of a CAD object, driven by any pointer pose (controller anchor,
/// controller ray, later a hand ray). Math moved unchanged from CADXRGrab:
/// the object keeps its grab-start position and rotation relative to the pointer, so it moves
/// and rotates rigidly; beyond reachDistance only pointer movement toward/away from the held
/// point is amplified. All poses go through CADVisionManipulationService.SetObjectWorldPose.
/// </summary>
public sealed class CADGrabSession
{
    public float ReachDistance = 0.5f;
    public float MaxExtraGain = 4f;
    public float DecayRate = 1.5f;

    private CADVisionManipulationService manipulationService;
    private Transform grabbedTransform;
    private Vector3 lastPointerPosition;
    private Quaternion lastPointerRotation;
    // The point being held (selection hit point, or visual center fallback), in the grabbed
    // object's local space. Reach assist measures to this, never to the Transform origin.
    private Vector3 grabPointLocal;
    // Object pose relative to the pointer (the virtual pickup transform).
    private Vector3 heldPositionOffset;    // In pointer space.
    private Quaternion heldRotationOffset; // In pointer space.

    public string GrabbedId { get; private set; }
    public bool IsActive => GrabbedId != null;

    /// <summary>Starts holding the object. Returns a short description for logging.</summary>
    public string Begin(CADVisionManipulationService service, CADObject target, Pose pointer)
    {
        manipulationService = service;

        // Only relative motion from here on, so the object does not snap.
        Transform targetTransform = target.transform;
        Quaternion inversePointer = Quaternion.Inverse(pointer.rotation);
        heldPositionOffset = inversePointer * (targetTransform.position - pointer.position);
        heldRotationOffset = inversePointer * targetTransform.rotation;
        lastPointerPosition = pointer.position;
        lastPointerRotation = pointer.rotation;

        // Hold the point the user picked; direct selections (CADEN, debug) have none.
        bool fromHit = service.TryGetSelectionPoint(out Vector3 grabPoint);
        if (!fromHit)
            grabPoint = VisualCenter(target);
        grabPointLocal = targetTransform.InverseTransformPoint(grabPoint);

        GrabbedId = target.id;
        grabbedTransform = targetTransform;
        return $"holding {(fromHit ? "selection hit point" : "visual center")} at " +
            $"{Vector3.Distance(pointer.position, grabPoint):F2} m";
    }

    /// <summary>
    /// Applies this frame's pointer pose. Returns false (without moving anything) if the object
    /// can no longer be held: destroyed, hidden, or no longer selected.
    /// </summary>
    public bool Update(Pose pointer)
    {
        if (!IsActive || !IsStillGrabbable())
            return false;

        Vector3 pointerPosition = pointer.position;
        Quaternion pointerRotation = pointer.rotation;

        // Depth assist: the rigid pickup already moves the object 1:1 with the pointer
        // (lateral, vertical and depth) and swings it at constant distance on rotation.
        // Beyond reach, only the depth component of the pointer's movement (along the
        // pointer → grab point axis) additionally lengthens or shortens the hold distance.
        // Measured to the held point, not the Transform origin: imported CAD origins can be
        // far from the visible geometry.
        Vector3 grabPoint = grabbedTransform.TransformPoint(grabPointLocal);
        // Pointer space as of the pose the object was placed with (last frame), i.e. the
        // grab point's rigid offset.
        Vector3 grabOffset = Quaternion.Inverse(lastPointerRotation) *
            (grabPoint - lastPointerPosition);
        float holdDistance = grabOffset.magnitude;
        float gain = TranslationGain(holdDistance);
        if (gain > 1f && holdDistance > 0f)
        {
            Vector3 holdAxis = grabOffset / holdDistance; // Pointer space.
            Vector3 localDelta = Quaternion.Inverse(pointerRotation) *
                (pointerPosition - lastPointerPosition);
            float depthDelta = Vector3.Dot(localDelta, holdAxis);

            // Assist never pulls the grab point inside reach; within reach it is rigid 1:1.
            float assistedDistance = Mathf.Max(
                ReachDistance, holdDistance + (gain - 1f) * depthDelta);

            // Translate the whole held pose along the axis; origin and geometry move together.
            heldPositionOffset += holdAxis * (assistedDistance - holdDistance);
        }

        Vector3 targetPosition = pointerPosition + pointerRotation * heldPositionOffset;
        Quaternion targetRotation = pointerRotation * heldRotationOffset;

        manipulationService.SetObjectWorldPose(GrabbedId, targetPosition, targetRotation);

        lastPointerPosition = pointerPosition;
        lastPointerRotation = pointerRotation;
        return true;
    }

    public void End()
    {
        GrabbedId = null;
        grabbedTransform = null;
    }

    // World center of the object's active, enabled renderers; its origin if it has none.
    public static Vector3 VisualCenter(CADObject cadObject)
    {
        bool hasBounds = false;
        Bounds bounds = default;

        foreach (Renderer renderer in cadObject.GetComponentsInChildren<Renderer>())
        {
            // Skip disabled renderers and visual overlays (outline shells, edge lines).
            if (!renderer.enabled || renderer.TryGetComponent(out CADVisualOverlay _))
                continue;

            if (hasBounds)
            {
                bounds.Encapsulate(renderer.bounds);
            }
            else
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
        }

        return hasBounds ? bounds.center : cadObject.transform.position;
    }

    // Exactly 1 within reach; beyond it, a bounded exponential rise toward 1 + MaxExtraGain.
    // Recomputed every frame, so bringing the object back within reach restores exact 1:1.
    private float TranslationGain(float distance)
    {
        if (distance <= ReachDistance)
            return 1f;

        float excessDistance = distance - ReachDistance;
        return 1f + MaxExtraGain * (1f - Mathf.Exp(-DecayRate * excessDistance));
    }

    // Selecting something else (or hiding/destroying the object) mid-grab ends the grab.
    private bool IsStillGrabbable()
    {
        return grabbedTransform != null &&
            grabbedTransform.gameObject.activeInHierarchy &&
            manipulationService.GetSelectedObjects().Any(o => o.id == GrabbedId);
    }
}
