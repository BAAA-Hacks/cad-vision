using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// One rigid "remote pickup" of a CAD object, driven by any pointer pose (controller anchor,
/// controller ray, later a hand ray). Math moved unchanged from CADXRGrab:
/// the object keeps its grab-start position and rotation relative to the pointer, so it moves
/// and rotates rigidly; beyond reachDistance only pointer movement toward/away from the held
/// point is amplified. All poses go through CADVisionManipulationService.SetObjectWorldPose.
///
/// A session can hold several objects (a multi-selection) as one rigid group: each member
/// keeps its own pose relative to the pointer, and the depth-assist shift computed at the
/// held point (on the first member) is applied to every member.
/// </summary>
public sealed class CADGrabSession
{
    public float ReachDistance = 0.5f;
    public float MaxExtraGain = 4f;
    public float DecayRate = 1.5f;

    private sealed class Member
    {
        public string Id;
        public Transform Transform;
        // Object pose relative to the pointer (the virtual pickup transform).
        public Vector3 HeldPositionOffset;    // In pointer space.
        public Quaternion HeldRotationOffset; // In pointer space.
    }

    private CADVisionManipulationService manipulationService;
    private bool standalone;
    private readonly List<Member> members = new();
    private Vector3 lastPointerPosition;
    private Quaternion lastPointerRotation;
    // The point being held (selection hit point, or visual center fallback), in the first
    // member's local space. Reach assist measures to this, never to the Transform origin.
    private Vector3 grabPointLocal;

    // First (anchor) member; all members for groups.
    public Transform GrabbedTransform => members.Count > 0 ? members[0].Transform : null;
    public string GrabbedId => members.Count > 0 ? members[0].Id : null;
    public IEnumerable<string> GrabbedIds => members.Select(m => m.Id);
    public int Count => members.Count;
    public bool IsActive => members.Count > 0;

    /// <summary>
    /// Starts holding the object. grabPointWorld overrides the held point (default: the
    /// service's selection hit point, else the visual center). Returns a description for logs.
    /// </summary>
    public string Begin(CADVisionManipulationService service, CADObject target, Pose pointer,
        Vector3? grabPointWorld = null) =>
        Begin(service, new[] { target }, pointer, grabPointWorld);

    /// <summary>
    /// Starts holding a rigid group. targets[0] anchors the grab point; pass transform roots
    /// (CADVisionManipulationService.GetSelectedTransformRoots) so nothing moves twice.
    /// </summary>
    public string Begin(CADVisionManipulationService service, IReadOnlyList<CADObject> targets, Pose pointer,
        Vector3? grabPointWorld = null)
    {
        standalone = false;
        manipulationService = service;
        members.Clear();

        // Only relative motion from here on, so nothing snaps.
        Quaternion inversePointer = Quaternion.Inverse(pointer.rotation);
        foreach (CADObject cadObject in targets)
        {
            members.Add(new Member
            {
                Id = cadObject.id,
                Transform = cadObject.transform,
                HeldPositionOffset = inversePointer * (cadObject.transform.position - pointer.position),
                HeldRotationOffset = inversePointer * cadObject.transform.rotation,
            });
        }
        lastPointerPosition = pointer.position;
        lastPointerRotation = pointer.rotation;

        CADObject target = targets[0];
        Transform targetTransform = target.transform;

        // Hold the point the user picked; direct selections (CADEN, debug) have none.
        Vector3 grabPoint;
        bool fromHit;
        if (grabPointWorld.HasValue)
        {
            grabPoint = grabPointWorld.Value;
            fromHit = true;
        }
        else
        {
            fromHit = service.TryGetSelectionPoint(out grabPoint);
            if (!fromHit)
                grabPoint = VisualCenter(target);
        }
        grabPointLocal = targetTransform.InverseTransformPoint(grabPoint);

        return $"{(members.Count > 1 ? $"group of {members.Count}; " : "")}holding {(fromHit ? "selection hit point" : "visual center")} at " +
            $"{Vector3.Distance(pointer.position, grabPoint):F2} m";
    }

    /// <summary>UI uses the identical pickup/reach math without entering the CAD ID registry.</summary>
    public void BeginStandalone(Transform target, Pose pointer, Vector3 hitPoint)
    {
        End();
        if (target == null) return;
        standalone = true;
        manipulationService = null;
        Quaternion inverse = Quaternion.Inverse(pointer.rotation);
        members.Add(new Member
        {
            Transform = target,
            HeldPositionOffset = inverse * (target.position - pointer.position),
            HeldRotationOffset = inverse * target.rotation
        });
        grabPointLocal = target.InverseTransformPoint(hitPoint);
        lastPointerPosition = pointer.position;
        lastPointerRotation = pointer.rotation;
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
        Vector3 grabPoint = members[0].Transform.TransformPoint(grabPointLocal);
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

            // Translate every held pose along the axis; origins, geometry and the rest of the
            // group move together.
            Vector3 shift = holdAxis * (assistedDistance - holdDistance);
            foreach (Member member in members)
                member.HeldPositionOffset += shift;
        }

        foreach (Member member in members)
        {
            Vector3 position = pointerPosition + pointerRotation * member.HeldPositionOffset;
            Quaternion rotation = pointerRotation * member.HeldRotationOffset;
            if (standalone) member.Transform.SetPositionAndRotation(position, rotation);
            else manipulationService.SetObjectWorldPose(member.Id, position, rotation);
        }

        lastPointerPosition = pointerPosition;
        lastPointerRotation = pointerRotation;
        return true;
    }

    public void End() { members.Clear(); standalone = false; }

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

    // Every member must still exist, be visible and be selected; otherwise the grab ends.
    public bool IsStillGrabbable()
    {
        if (!IsActive) return false;
        foreach (Member member in members)
        {
            if (member.Transform == null ||
                !member.Transform.gameObject.activeInHierarchy ||
                (!standalone && !manipulationService.IsSelected(member.Id)))
            {
                return false;
            }
        }

        return true;
    }
}
