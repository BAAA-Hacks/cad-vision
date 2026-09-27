using System.Collections.Generic;
using CADVision;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Adopts the Task 2 runtime model into Task 3: on every CADVisionRuntime.ModelChanged
/// (publish, replace, clear) the imported registry is handed to the manipulation service,
/// then XR ray selection is provisioned for it. Add alongside the service.
///
/// Placement: each adopted model is first placed in front of the user (visible bounds centered
/// on the head's horizontal forward, its near side placementGap ahead, slightly below eye
/// level; rotation and scale untouched). Task 2 places it at import time, which at app start
/// is often before head tracking is live (rig origin, arbitrary facing), so adoption waits for
/// a tracked headset (at most headTrackingTimeout seconds). Placement happens before the
/// service registers the model, so its reset pose is this placement.
///
/// Recenter: when the user recenters the view (hold the Meta button), the model follows the
/// new view: the model root and its home (the pose Reset everything returns to) move by the
/// change of the head's horizontal frame since the model was placed (or last recentered), so a
/// model at home ends up in front of the user again and a moved model keeps its place
/// relative to the user. Detected through OVRDisplay.RecenteredPose and the XR input
/// subsystem's trackingOriginUpdated, handled once, a few frames later (fresh head pose).
/// </summary>
[DefaultExecutionOrder(110)] // After the service (Start registers scene objects) and CADXRRaySetup.
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADRuntimeBridge : MonoBehaviour
{
    [Tooltip("Task 2 runtime. Defaults to the one in the scene (the auto-loader creates it after scene load).")]
    [SerializeField] private CADVisionRuntime runtime;

    [Header("Placement")]
    [Tooltip("Place each new model in front of the user when it is adopted.")]
    [SerializeField] private bool placeInFrontOfUser = true;
    [Tooltip("Gap between the eyes and the model's near side (m).")]
    [SerializeField, Min(0.2f)] private float placementGap = 0.8f;
    // Renamed from placementDrop so the scene's old value (0.2 m, too low) doesn't override the
    // new default.
    [Tooltip("Model center below eye level (m).")]
    [SerializeField] private float modelBelowEye = 0.08f;
    [Tooltip("Longest wait for head tracking before placing anyway (s).")]
    [SerializeField, Min(0f)] private float headTrackingTimeout = 5f;
    [Tooltip("When the user recenters the view, bring the model (and its reset pose) along to the new view.")]
    [SerializeField] private bool followRecenter = true;

    // Frames to wait after a recenter event before reading the head (poses update a frame late).
    private const int RecenterSettleFrames = 2;

    private const float RuntimeSearchInterval = 0.5f;

    private CADVisionManipulationService manipulationService;
    private CADXRRaySetup raySetup;
    private CADVisionRuntime subscribedRuntime;
    private bool started;
    private bool hasImport;
    private float nextRuntimeSearch;
    private bool adoptPending;       // A model is waiting for head tracking before placement.
    private float adoptPendingSince;

    // Head's horizontal frame (position + yaw) when the model's home was last set.
    private bool hasHomeFrame;
    private Vector3 homeHeadPosition;
    private float homeHeadYaw;
    private int recenterAtFrame = -1;
    private OVRDisplay subscribedDisplay;
    private readonly List<XRInputSubsystem> inputSubsystems = new();
    private readonly List<XRInputSubsystem> subscribedSubsystems = new();

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        raySetup = GetComponent<CADXRRaySetup>();
    }

    private void Start()
    {
        started = true;
        TryConnect();
    }

    private void OnEnable()
    {
        // Re-enable after Start: reconnect and catch up on changes missed while disabled.
        if (started)
            TryConnect();
    }

    private void OnDisable()
    {
        Unsubscribe();
        UnsubscribeRecenter();
    }

    private void Update()
    {
        if (followRecenter)
        {
            SubscribeRecenter();
            if (recenterAtFrame >= 0 && Time.frameCount >= recenterAtFrame)
            {
                recenterAtFrame = -1;
                FollowRecenter();
            }
        }

        if (adoptPending)
            Adopt();

        if (subscribedRuntime != null)
            return;

        // The subscribed runtime was destroyed (its OnDestroy clears and notifies, but guard
        // anyway): drop the stale model and wait for a new runtime.
        if (hasImport)
            Adopt();

        if (Time.unscaledTime >= nextRuntimeSearch)
        {
            nextRuntimeSearch = Time.unscaledTime + RuntimeSearchInterval;
            TryConnect();
        }
    }

    private void TryConnect()
    {
        if (runtime == null)
            runtime = FindAnyObjectByType<CADVisionRuntime>();

        if (runtime == null)
            return;

        if (subscribedRuntime != runtime)
        {
            Unsubscribe();
            runtime.ModelChanged += Adopt;
            subscribedRuntime = runtime;
            Debug.Log($"[CADRuntimeBridge] Connected to {runtime.name}.");
        }

        // Handles a model that was already loaded before the bridge connected.
        Adopt();
    }

    private void Unsubscribe()
    {
        if (subscribedRuntime != null)
            subscribedRuntime.ModelChanged -= Adopt;

        subscribedRuntime = null;
    }

    private void Adopt()
    {
        GameObject root = subscribedRuntime != null ? subscribedRuntime.GetRoot() : null;

        if (root == null)
        {
            adoptPending = false;
            manipulationService.ReplaceImportedModel(null, null);
            hasImport = false;
        }
        else
        {
            if (placeInFrontOfUser)
            {
                if (!adoptPending)
                {
                    adoptPending = true;
                    adoptPendingSince = Time.unscaledTime;
                }

                bool timedOut = Time.unscaledTime - adoptPendingSince >= headTrackingTimeout;
                if (!TryGetTrackedHead(out Transform head) && !timedOut)
                    return; // Retried every frame from Update.

                if (head != null && PlaceInFront(root.transform, head, placementGap, modelBelowEye))
                    Debug.Log($"[CADRuntimeBridge] Placed model in front of the user" +
                        $"{(timedOut ? " (head tracking not confirmed)" : "")}.");
            }

            adoptPending = false;
            manipulationService.ReplaceImportedModel(root.transform, subscribedRuntime.GetAllObjects());
            hasImport = true;
            RecordHomeFrame();
        }

        // Provision ray selection for the newly registered objects (idempotent).
        if (raySetup != null)
            raySetup.ConfigureRegisteredObjects();

        Debug.Log($"[CADRuntimeBridge] Adopted runtime model " +
            $"(revision {(subscribedRuntime != null ? subscribedRuntime.Revision : -1)}, " +
            $"root {(root != null ? root.name : "<none>")}).");
    }

    // ---------------- Recenter ----------------

    private void RecordHomeFrame()
    {
        Transform head = Camera.main != null ? Camera.main.transform : null;
        hasHomeFrame = head != null;
        if (hasHomeFrame)
        {
            homeHeadPosition = head.position;
            homeHeadYaw = Yaw(head);
        }
    }

    private void SubscribeRecenter()
    {
        if (subscribedDisplay == null && OVRManager.display != null)
        {
            subscribedDisplay = OVRManager.display;
            subscribedDisplay.RecenteredPose += OnRecentered;
        }

        SubsystemManager.GetSubsystems(inputSubsystems);
        foreach (XRInputSubsystem subsystem in inputSubsystems)
        {
            if (!subscribedSubsystems.Contains(subsystem))
            {
                subsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
                subscribedSubsystems.Add(subsystem);
            }
        }
    }

    private void UnsubscribeRecenter()
    {
        if (subscribedDisplay != null)
            subscribedDisplay.RecenteredPose -= OnRecentered;
        subscribedDisplay = null;

        foreach (XRInputSubsystem subsystem in subscribedSubsystems)
        {
            if (subsystem != null)
                subsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        }
        subscribedSubsystems.Clear();
    }

    private void OnTrackingOriginUpdated(XRInputSubsystem _) => OnRecentered();

    // Both sources may report the same recenter: handled once, after the poses settle.
    private void OnRecentered()
    {
        if (recenterAtFrame < 0)
            recenterAtFrame = Time.frameCount + RecenterSettleFrames;
    }

    /// <summary>
    /// Brings the model along to the recentered view: the root and its home move by the change
    /// of the head's horizontal frame since the home was set. Without a recorded frame (the
    /// scene's own model) the model is placed in front of the user and that becomes its home.
    /// </summary>
    public void FollowRecenter()
    {
        Transform root = manipulationService.ModelRoot;
        Transform head = Camera.main != null ? Camera.main.transform : null;
        if (root == null || head == null)
            return;

        if (!hasHomeFrame)
        {
            PlaceInFront(root, head, placementGap, modelBelowEye);
            manipulationService.SetModelHomePose(root.position, root.rotation);
        }
        else
        {
            Vector3 newHead = head.position;
            float newYaw = Yaw(head);
            MoveWithHeadFrame(root.position, root.rotation, homeHeadPosition, homeHeadYaw, newHead, newYaw,
                out Vector3 position, out Quaternion rotation);
            manipulationService.SetModelWorldPose(position, rotation);

            if (manipulationService.TryGetModelHomePose(out Vector3 homePosition, out Quaternion homeRotation))
            {
                MoveWithHeadFrame(homePosition, homeRotation, homeHeadPosition, homeHeadYaw, newHead, newYaw,
                    out homePosition, out homeRotation);
                manipulationService.SetModelHomePose(homePosition, homeRotation);
            }
        }

        RecordHomeFrame();
        Debug.Log("[CADRuntimeBridge] View recentered: model moved to the new view.");
    }

    /// <summary>
    /// A pose held fixed relative to the head's horizontal frame (position + yaw) while that
    /// frame moves from (oldHead, oldYaw) to (newHead, newYaw).
    /// </summary>
    public static void MoveWithHeadFrame(Vector3 position, Quaternion rotation, Vector3 oldHead, float oldYaw,
        Vector3 newHead, float newYaw, out Vector3 movedPosition, out Quaternion movedRotation)
    {
        Quaternion turn = Quaternion.AngleAxis(Mathf.DeltaAngle(oldYaw, newYaw), Vector3.up);
        movedPosition = newHead + turn * (position - oldHead);
        movedRotation = turn * rotation;
    }

    // Heading of the head's horizontal forward (degrees about +Y).
    private static float Yaw(Transform head)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        return forward.sqrMagnitude < 1e-4f ? 0f : Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    // The main camera (center eye), once the headset reports it tracked. Without an active XR
    // device (desktop, Editor without a headset) the camera is used as is.
    private static bool TryGetTrackedHead(out Transform head)
    {
        head = Camera.main != null ? Camera.main.transform : null;
        if (head == null)
            return false;
        if (!XRSettings.isDeviceActive)
            return true;

        InputDevice eye = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
        return eye.isValid && eye.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
    }

    /// <summary>
    /// Moves root so its visible geometry sits in front of head: bounds center on the head's
    /// horizontal forward, near side gap metres ahead, center drop metres below eye level.
    /// Only the position changes. Returns false if there is no visible geometry.
    /// </summary>
    public static bool PlaceInFront(Transform root, Transform head, float gap, float drop)
    {
        bool hasBounds = false;
        Bounds bounds = default;
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled || renderer.TryGetComponent(out CADVisualOverlay _))
                continue;
            if (hasBounds) bounds.Encapsulate(renderer.bounds);
            else { bounds = renderer.bounds; hasBounds = true; }
        }
        if (!hasBounds)
            return false;

        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // Looking straight up/down.
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.forward;
        forward.Normalize();

        // Half-depth of the bounds along the viewing direction: the near side sits at gap.
        Vector3 e = bounds.extents;
        float halfDepth = Mathf.Abs(forward.x) * e.x + Mathf.Abs(forward.z) * e.z;
        Vector3 target = head.position + forward * (gap + halfDepth) + Vector3.down * drop;
        root.position += target - bounds.center;
        return true;
    }
}
