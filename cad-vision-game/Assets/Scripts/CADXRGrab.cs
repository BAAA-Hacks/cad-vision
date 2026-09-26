using System.Linq;
using UnityEngine;

/// <summary>
/// Right-controller grip grabs the currently selected CAD object; releasing grip drops it.
/// Holding both controller grips scales around the selected object's visible center.
/// Selection stays with the trigger/ray flow. All pose changes go through the service.
/// Behaves like a conventional VR pickup: the object keeps its grab-start position and
/// rotation relative to the controller, so it moves and rotates rigidly with the hand.
/// Beyond reachDistance, only controller movement toward/away from the held point is amplified
/// (it lengthens or shortens the hold distance); lateral movement and rotation stay 1:1.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADXRGrab : MonoBehaviour
{
    [Tooltip("Right controller tracking transform. Defaults to OVRCameraRig.rightControllerAnchor.")]
    [SerializeField] private Transform controllerAnchor;

    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch;

    [Header("Translation Gain")]
    [Tooltip("Controller-to-grab-point distance (m) within which the pickup is exactly 1:1.")]
    [SerializeField, Min(0f)] private float reachDistance = 0.5f;

    [Tooltip("Gain approaches 1 + this value as the object moves far beyond reach.")]
    [SerializeField, Min(0f)] private float maxExtraGain = 4f;

    [Tooltip("How quickly (per metre beyond reach) gain approaches its maximum.")]
    [SerializeField, Min(0f)] private float decayRate = 1.5f;

    [Header("Two-Hand Scaling")]
    [SerializeField] private Transform leftControllerAnchor;
    [Tooltip("Minimum hand separation needed to start scaling, in metres.")]
    [SerializeField, Min(0.01f)] private float minimumScaleSeparation = 0.08f;
    [Tooltip("Smallest size relative to the size when both grips were pressed.")]
    [SerializeField, Range(0.01f, 1f)] private float minimumScaleRatio = 0.1f;
    [Tooltip("Largest size relative to the size when both grips were pressed.")]
    [SerializeField, Min(1f)] private float maximumScaleRatio = 10f;

    private bool scaling;
    private float initialHandDistance;
    private Vector3 initialScale;
    private Vector3 scalePivotLocal;
    private Vector3 scalePivotWorld;
    private bool scaleCancelledUntilRelease;

    private CADVisionManipulationService manipulationService;

    private string grabbedId;
    private Transform grabbedTransform;
    private Vector3 lastControllerPosition;
    private Quaternion lastControllerRotation;
    // The point being held (selection hit point, or visual center fallback), in the grabbed
    // object's local space. Reach assist measures to this, never to the Transform origin.
    private Vector3 grabPointLocal;
    // Object pose relative to the controller (the virtual pickup transform).
    private Vector3 heldPositionOffset;    // In controller space.
    private Quaternion heldRotationOffset; // In controller space.

    // TEMP grab diagnostics: last logged grip state, so only transitions are logged.
    private bool loggedGripHeld;
    private bool loggedGripAxisHeld;

    // Start, not Awake: OVRCameraRig assigns its anchors in its own Awake.
    private void Start()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();

        if (controllerAnchor == null || leftControllerAnchor == null)
        {
            OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                if (controllerAnchor == null) controllerAnchor = rig.rightControllerAnchor;
                if (leftControllerAnchor == null) leftControllerAnchor = rig.leftControllerAnchor;
            }
        }

        if (controllerAnchor == null)
            Debug.LogWarning("[CADXRGrab] No controller anchor resolved (OVRCameraRig not found or anchor null); grabbing disabled.");
        else
            Debug.Log($"[CADXRGrab] Controller anchor resolved: {controllerAnchor.name} " +
                $"(controller mask {controller}).");
    }

    private void OnDisable() => Release("component disabled");

    // LateUpdate so the rig has applied this frame's controller pose.
    private void LateUpdate()
    {
        LogGripTransitions();

        if (controllerAnchor == null)
            return;

        if (UpdateTwoHandScale())
            return;

        if (grabbedId != null)
        {
            if (!OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller) ||
                !IsStillGrabbable())
            {
                Release(OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller)
                    ? "object no longer selected or active"
                    : "grip released");
                return;
            }

            Vector3 controllerPosition = controllerAnchor.position;
            Quaternion controllerRotation = controllerAnchor.rotation;

            // Depth assist: the rigid pickup already moves the object 1:1 with the controller
            // (lateral, vertical and depth) and swings it at constant distance on rotation.
            // Beyond reach, only the depth component of the controller's movement (along the
            // controller → grab point axis) additionally lengthens or shortens the hold distance.
            // Measured to the held point, not the Transform origin: imported CAD origins can be
            // far from the visible geometry.
            Vector3 grabPoint = grabbedTransform.TransformPoint(grabPointLocal);
            // Controller space as of the pose the object was placed with (last frame), i.e. the
            // grab point's rigid offset.
            Vector3 grabOffset = Quaternion.Inverse(lastControllerRotation) *
                (grabPoint - lastControllerPosition);
            float holdDistance = grabOffset.magnitude;
            float gain = TranslationGain(holdDistance);
            if (gain > 1f && holdDistance > 0f)
            {
                Vector3 holdAxis = grabOffset / holdDistance; // Controller space.
                Vector3 localDelta = Quaternion.Inverse(controllerRotation) *
                    (controllerPosition - lastControllerPosition);
                float depthDelta = Vector3.Dot(localDelta, holdAxis);

                // Assist never pulls the grab point inside reach; within reach it is rigid 1:1.
                float assistedDistance = Mathf.Max(
                    reachDistance, holdDistance + (gain - 1f) * depthDelta);

                // Translate the whole held pose along the axis; origin and geometry move together.
                heldPositionOffset += holdAxis * (assistedDistance - holdDistance);
            }

            Vector3 targetPosition = controllerPosition + controllerRotation * heldPositionOffset;
            Quaternion targetRotation = controllerRotation * heldRotationOffset;

            manipulationService.SetObjectWorldPose(grabbedId, targetPosition, targetRotation);

            lastControllerPosition = controllerPosition;
            lastControllerRotation = controllerRotation;
        }
        else if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, controller))
        {
            Debug.Log("[CADXRGrab] Grip GetDown detected; attempting grab.");
            TryGrab();
        }
    }

    // Returns true while two-hand input owns this frame, preventing grab pose updates.
    private bool UpdateTwoHandScale()
    {
        bool rightHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller);
        bool leftHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        bool bothHeld = rightHeld && leftHeld;
        bool tracked = leftControllerAnchor != null &&
            OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch) &&
            OVRInput.GetControllerPositionTracked(controller);

        if (!bothHeld) scaleCancelledUntilRelease = false;

        if (scaling && (!bothHeld || !tracked || !IsStillGrabbable()))
        {
            bool resumeGrab = rightHeld && tracked && IsStillGrabbable();
            Release("two-hand scaling ended");
            scaleCancelledUntilRelease = bothHeld;
            // Recapture the current pose after scaling so the old grab offset cannot snap back.
            if (resumeGrab && !bothHeld) TryGrab();
            return true;
        }

        if (!bothHeld) return false;
        if (scaleCancelledUntilRelease || !tracked) return true;

        float distance = Vector3.Distance(leftControllerAnchor.position, controllerAnchor.position);
        if (!scaling)
        {
            if (distance < Mathf.Max(0.01f, minimumScaleSeparation)) return true;
            if (grabbedId == null || !IsStillGrabbable())
            {
                Release("starting two-hand scaling");
                TryGrab();
            }
            if (grabbedId == null) return true;

            initialHandDistance = distance;
            initialScale = grabbedTransform.localScale;
            scalePivotWorld = VisualCenter(grabbedTransform.GetComponent<CADObject>());
            scalePivotLocal = grabbedTransform.InverseTransformPoint(scalePivotWorld);
            scaling = true;
        }

        float ratio = Mathf.Clamp(distance / initialHandDistance,
            Mathf.Clamp(minimumScaleRatio, 0.01f, 1f), Mathf.Max(1f, maximumScaleRatio));
        manipulationService.SetObjectScaleAroundPoint(
            grabbedId, initialScale * ratio, scalePivotLocal, scalePivotWorld);
        return true;
    }

    // TEMP: logs grip button/axis changes independently of the anchor and grab state.
    private void LogGripTransitions()
    {
        bool held = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller);
        float axis = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller);
        bool axisHeld = axis > 0.5f;

        if (held == loggedGripHeld && axisHeld == loggedGripAxisHeld)
            return;

        loggedGripHeld = held;
        loggedGripAxisHeld = axisHeld;
        Debug.Log($"[CADXRGrab] Grip changed: button={held}, axis={axis:F2}, " +
            $"active={OVRInput.GetActiveController()}, " +
            $"connected={OVRInput.GetConnectedControllers()}, " +
            $"anchor={(controllerAnchor != null ? controllerAnchor.name : "null")}.");
    }

    private void TryGrab()
    {
        CADObject selected = manipulationService.GetSelectedObjects().FirstOrDefault();
        if (selected == null)
        {
            Debug.Log("[CADXRGrab] Grab aborted: nothing selected.");
            return;
        }

        if (!selected.gameObject.activeInHierarchy)
        {
            Debug.Log($"[CADXRGrab] Grab aborted: selected '{selected.id}' is inactive.");
            return;
        }

        // Only relative motion from here on, so the object does not snap.
        Transform target = selected.transform;
        Quaternion inverseController = Quaternion.Inverse(controllerAnchor.rotation);
        heldPositionOffset = inverseController * (target.position - controllerAnchor.position);
        heldRotationOffset = inverseController * target.rotation;
        lastControllerPosition = controllerAnchor.position;
        lastControllerRotation = controllerAnchor.rotation;

        // Hold the point the user picked; direct selections (CADEN, debug) have none.
        bool fromHit = manipulationService.TryGetSelectionPoint(out Vector3 grabPoint);
        if (!fromHit)
            grabPoint = VisualCenter(selected);
        grabPointLocal = target.InverseTransformPoint(grabPoint);

        grabbedId = selected.id;
        grabbedTransform = selected.transform;
        Debug.Log($"[CADXRGrab] Grab started: '{grabbedId}' ({selected.name}); holding " +
            $"{(fromHit ? "selection hit point" : "visual center")} at " +
            $"{Vector3.Distance(controllerAnchor.position, grabPoint):F2} m.");
    }

    // World center of the object's active, enabled renderers; its origin if it has none.
    private static Vector3 VisualCenter(CADObject cadObject)
    {
        bool hasBounds = false;
        Bounds bounds = default;

        foreach (Renderer renderer in cadObject.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled)
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

    // Exactly 1 within reach; beyond it, a bounded exponential rise toward 1 + maxExtraGain.
    // Recomputed every frame, so bringing the object back within reach restores exact 1:1.
    private float TranslationGain(float distance)
    {
        if (distance <= reachDistance)
            return 1f;

        float excessDistance = distance - reachDistance;
        return 1f + maxExtraGain * (1f - Mathf.Exp(-decayRate * excessDistance));
    }

    // Selecting something else (or hiding the object) mid-grab ends the grab.
    private bool IsStillGrabbable()
    {
        return grabbedTransform != null &&
            grabbedTransform.gameObject.activeInHierarchy &&
            manipulationService.GetSelectedObjects().Any(o => o.id == grabbedId);
    }

    private void Release(string reason)
    {
        if (grabbedId != null)
            Debug.Log($"[CADXRGrab] Grab ended: '{grabbedId}' ({reason}).");

        scaling = false;
        grabbedId = null;
        grabbedTransform = null;
    }
}
