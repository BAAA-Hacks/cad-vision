using System.Linq;
using UnityEngine;

/// <summary>
/// TEMP debug fallback: right-controller grip grabs the currently selected CAD object;
/// releasing grip drops it. Normal use is trigger press-and-drag via CADPointerInteraction.
/// Uses the same CADGrabSession math (rigid pickup, depth-only reach assist), driven by the
/// right controller anchor. All pose changes go through the service.
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

        if (controllerAnchor == null)
        {
            OVRCameraRig rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null)
                controllerAnchor = rig.rightControllerAnchor;
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
    }
}
