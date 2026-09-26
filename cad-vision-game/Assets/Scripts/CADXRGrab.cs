using System.Linq;
using UnityEngine;

/// <summary>
/// Right grip moves the selected CAD object using CADGrabSession.
/// Both grips scale it around its visible center; releasing the left grip resumes movement.
/// Trigger press-and-drag remains owned by CADPointerInteraction and takes priority.
/// All transform changes go through the manipulation service.
/// TEMP debug fallback: right-controller grip grabs the currently selected CAD object;
/// releasing grip drops it. Normal use is trigger press-and-drag via CADPointerInteraction.
/// Uses the same CADGrabSession math (rigid pickup, depth-only reach assist), driven by the
/// right controller anchor. All pose changes go through the service.
/// Holding both controller grips scales around the selected object's visible center, or, in
/// whole-model manipulation mode, scales the model root around the held/visible point (single
/// grip does nothing in that mode: model manipulation wins).
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

    private CADObject scaleTarget;
    private bool scaling;
    private float initialHandDistance;
    private Vector3 initialScale;
    private Vector3 scalePivotLocal;
    private Vector3 scalePivotWorld;
    // Shared two-hand scale math (also used by two-pointer pinch scaling).
    private readonly CADScaleGesture partScale = new CADScaleGesture();
    private readonly CADScaleGesture modelScale = new CADScaleGesture(); // Model manipulation mode.
    private bool scaleCancelledUntilRelease;

    private CADVisionManipulationService manipulationService;
    private CADPointerInteraction pointerInteraction;
    private readonly CADGrabSession session = new CADGrabSession();
    // The object held by the grip (session anchor); also the two-hand scaling target.
    private CADObject grabbedObject;

    public bool IsGrabbing => session.IsActive;
    public bool IsScalingModel => modelScale.IsActive;

    // TEMP grab diagnostics: last logged grip state, so only transitions are logged.
    private bool loggedGripHeld;
    private bool loggedGripAxisHeld;

    // Start, not Awake: OVRCameraRig assigns its anchors in its own Awake.
    private void Start()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        pointerInteraction = GetComponent<CADPointerInteraction>();

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

    private void OnDisable()
    {
        Release("component disabled");
        EndModelScale("component disabled");
    }

    // LateUpdate so the rig has applied this frame's controller pose.
    private void LateUpdate()
    {
        LogGripTransitions();

        if (controllerAnchor == null)
            return;

        ApplyScaleSettings(partScale);
        ApplyScaleSettings(modelScale);

        // Model mode: the grips only scale the whole model; part grabs are released.
        if (manipulationService.IsModelManipulationActive || modelScale.IsActive)
        {
            if (session.IsActive)
                Release("model manipulation active");
            UpdateModelTwoHandScale();
            return;
        }

        if (UpdateTwoHandScale())
            return;

        if (session.IsActive)
        {
            session.ReachDistance = reachDistance;
            session.MaxExtraGain = maxExtraGain;
            session.DecayRate = decayRate;

            if (!OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller))
            {
                Release("grip released");
                return;
            }

            if (!session.Update(new Pose(controllerAnchor.position, controllerAnchor.rotation)))
                Release("object no longer selected or active");
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

        if (partScale.IsActive && (!bothHeld || !tracked || !IsStillGrabbable()))
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
        if (!partScale.IsActive)
        {
            if (distance < Mathf.Max(0.01f, minimumScaleSeparation)) return true;
            if (!IsStillGrabbable())
            {
                Release("starting two-hand scaling");
                TryGrab();
            }
            if (session.GrabbedId == null) return true;

            initialHandDistance = distance;
            initialScale = scaleTarget.transform.localScale;
            scalePivotWorld = CADGrabSession.VisualCenter(scaleTarget);
            scalePivotLocal = scaleTarget.transform.InverseTransformPoint(scalePivotWorld);
            scaling = true;
        }

        float ratio = Mathf.Clamp(distance / initialHandDistance,
            Mathf.Clamp(minimumScaleRatio, 0.01f, 1f), Mathf.Max(1f, maximumScaleRatio));
        manipulationService.SetObjectScaleAroundPoint(
            session.GrabbedId, initialScale * ratio, scalePivotLocal, scalePivotWorld);
            if (!IsStillGrabbable()) return true;

            partScale.TryBeginObjects(manipulationService, new[] { grabbedObject }, distance,
                CADGrabSession.VisualCenter(grabbedObject));
            if (!partScale.IsActive) return true;
        }

        if (!partScale.Update(distance))
            Release("scaled object gone");
        return true;
    }

    private void ApplyScaleSettings(CADScaleGesture gesture)
    {
        gesture.MinimumSeparation = minimumScaleSeparation;
        gesture.MinimumRatio = minimumScaleRatio;
        gesture.MaximumRatio = maximumScaleRatio;
    }

    // Both grips scale the model root uniformly around a fixed pivot on the visible model:
    // the point held by an active one-hand model drag, else the visible bounds center (never
    // the root's CAD origin). The pointer's model drag rebases while this runs, so moving
    // between one and two hands never snaps.
    private void UpdateModelTwoHandScale()
    {
        bool rightHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller);
        bool leftHeld = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        bool tracked = leftControllerAnchor != null &&
            OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch) &&
            OVRInput.GetControllerPositionTracked(controller);
        Transform root = manipulationService.ModelRoot;
        bool canScale = rightHeld && leftHeld && tracked && root != null &&
            manipulationService.IsModelManipulationActive;

        if (modelScale.IsActive && !canScale)
        {
            EndModelScale("grips released or model mode ended");
            return;
        }

        if (!canScale)
            return;

        float distance = Vector3.Distance(leftControllerAnchor.position, controllerAnchor.position);
        if (!modelScale.IsActive)
        {
            Vector3 pivot = default;
            bool fromGrab = pointerInteraction != null && pointerInteraction.TryGetModelGrabPoint(out pivot);
            if (!fromGrab)
                pivot = manipulationService.TryGetModelBounds(out Bounds bounds) ? bounds.center : root.position;

            if (!modelScale.TryBeginModel(manipulationService, distance, pivot))
                return; // Hands too close to start.
            Debug.Log($"[CADXRGrab] Model scaling started around {(fromGrab ? "held point" : "visible center")} " +
                $"(scale ×{manipulationService.ModelScaleRatio:F2}).");
        }

        if (!modelScale.Update(distance))
            EndModelScale("model replaced");
    }

    private void EndModelScale(string reason)
    {
        if (!modelScale.IsActive)
            return;

        modelScale.End();
        Debug.Log($"[CADXRGrab] Model scaling ended ({reason}; scale ×{manipulationService.ModelScaleRatio:F2}).");
    }

    // The grip-held object still exists, is visible and is selected.
    private bool IsStillGrabbable() =>
        session.IsActive && grabbedObject != null &&
        grabbedObject.gameObject.activeInHierarchy &&
        manipulationService.IsSelected(grabbedObject.id);

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
        if (pointerInteraction != null && pointerInteraction.IsManipulating)
        {
            Debug.Log("[CADXRGrab] Grab aborted: trigger-drag manipulation is active.");
            return;
        }

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

        session.ReachDistance = reachDistance;
        session.MaxExtraGain = maxExtraGain;
        session.DecayRate = decayRate;
        scaleTarget = selected;
        string info = session.Begin(manipulationService, selected,
            new Pose(controllerAnchor.position, controllerAnchor.rotation));
        grabbedObject = selected;
        Debug.Log($"[CADXRGrab] Grab started: '{selected.id}' ({selected.name}); {info}.");
    }

    private bool IsStillGrabbable()
    {
        return session.IsActive && scaleTarget != null &&
            scaleTarget.gameObject.activeInHierarchy &&
            manipulationService.GetSelectedObjects().Any(o => o == scaleTarget);
    }

    private void Release(string reason)
    {
        if (session.IsActive)
            Debug.Log($"[CADXRGrab] Grab ended: '{session.GrabbedId}' ({reason}).");

        session.End();
        scaling = false;
        scaleTarget = null;
        partScale.End();
        grabbedObject = null;
    }
}
