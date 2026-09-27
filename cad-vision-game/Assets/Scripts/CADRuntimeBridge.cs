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
    [Tooltip("Model center below eye level (m).")]
    [SerializeField] private float placementDrop = 0.2f;
    [Tooltip("Longest wait for head tracking before placing anyway (s).")]
    [SerializeField, Min(0f)] private float headTrackingTimeout = 5f;

    private const float RuntimeSearchInterval = 0.5f;

    private CADVisionManipulationService manipulationService;
    private CADXRRaySetup raySetup;
    private CADVisionRuntime subscribedRuntime;
    private bool started;
    private bool hasImport;
    private float nextRuntimeSearch;
    private bool adoptPending;       // A model is waiting for head tracking before placement.
    private float adoptPendingSince;

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

    private void OnDisable() => Unsubscribe();

    private void Update()
    {
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

                if (head != null && PlaceInFront(root.transform, head, placementGap, placementDrop))
                    Debug.Log($"[CADRuntimeBridge] Placed model in front of the user" +
                        $"{(timedOut ? " (head tracking not confirmed)" : "")}.");
            }

            adoptPending = false;
            manipulationService.ReplaceImportedModel(root.transform, subscribedRuntime.GetAllObjects());
            hasImport = true;
        }

        // Provision ray selection for the newly registered objects (idempotent).
        if (raySetup != null)
            raySetup.ConfigureRegisteredObjects();

        Debug.Log($"[CADRuntimeBridge] Adopted runtime model " +
            $"(revision {(subscribedRuntime != null ? subscribedRuntime.Revision : -1)}, " +
            $"root {(root != null ? root.name : "<none>")}).");
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
