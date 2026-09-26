using CADVision;
using UnityEngine;

/// <summary>
/// Adopts the Task 2 runtime model into Task 3: on every CADVisionRuntime.ModelChanged
/// (publish, replace, clear) the imported registry is handed to the manipulation service,
/// then XR ray selection is provisioned for it. Add alongside the service.
/// </summary>
[DefaultExecutionOrder(110)] // After the service (Start registers scene objects) and CADXRRaySetup.
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADRuntimeBridge : MonoBehaviour
{
    [Tooltip("Task 2 runtime. Defaults to the one in the scene (the auto-loader creates it after scene load).")]
    [SerializeField] private CADVisionRuntime runtime;

    private const float RuntimeSearchInterval = 0.5f;

    private CADVisionManipulationService manipulationService;
    private CADXRRaySetup raySetup;
    private CADVisionRuntime subscribedRuntime;
    private bool started;
    private bool hasImport;
    private float nextRuntimeSearch;

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
            manipulationService.ReplaceImportedModel(null, null);
            hasImport = false;
        }
        else
        {
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
}
