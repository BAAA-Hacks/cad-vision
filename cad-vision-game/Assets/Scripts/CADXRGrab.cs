using System.Linq;
using UnityEngine;

/// <summary>
/// TEMP debug fallback: right-controller grip grabs the currently selected CAD object;
/// releasing grip drops it. Normal use is trigger press-and-drag via CADPointerInteraction.
/// Uses the same CADGrabSession math (rigid pickup, depth-only reach assist), driven by the
/// right controller anchor. All pose changes go through the service.
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
    private CADPointerInteraction pointerInteraction;
    private readonly CADGrabSession session = new CADGrabSession();

    public bool IsGrabbing => session.IsActive;

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

    private void OnDisable() => Release("component disabled");

    // LateUpdate so the rig has applied this frame's controller pose.
    private void LateUpdate()
    {
        LogGripTransitions();

        if (controllerAnchor == null)
            return;

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

        if (scaling && (!bothHeld || !tracked || !session.IsStillGrabbable()))
        {
            bool resumeGrab = rightHeld && tracked && session.IsStillGrabbable();
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
            if (session.GrabbedId == null || !session.IsStillGrabbable())
            {
                Release("starting two-hand scaling");
                TryGrab();
            }
            if (session.GrabbedId == null) return true;

            initialHandDistance = distance;
            initialScale = session.GrabbedTransform.localScale;
            scalePivotWorld = CADGrabSession.VisualCenter(session.GrabbedTransform.GetComponent<CADObject>());
            scalePivotLocal = session.GrabbedTransform.InverseTransformPoint(scalePivotWorld);
            scaling = true;
        }

        float ratio = Mathf.Clamp(distance / initialHandDistance,
            Mathf.Clamp(minimumScaleRatio, 0.01f, 1f), Mathf.Max(1f, maximumScaleRatio));
        manipulationService.SetObjectScaleAroundPoint(
            session.GrabbedId, initialScale * ratio, scalePivotLocal, scalePivotWorld);
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
        string info = session.Begin(manipulationService, selected,
            new Pose(controllerAnchor.position, controllerAnchor.rotation));
        Debug.Log($"[CADXRGrab] Grab started: '{selected.id}' ({selected.name}); {info}.");
    }

    private void Release(string reason)
    {
        if (session.IsActive)
            Debug.Log($"[CADXRGrab] Grab ended: '{session.GrabbedId}' ({reason}).");

        session.End();
        scaling = false;
    }
}
