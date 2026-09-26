using System;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Semantic pointer interaction: one ray + one "select" signal (controller trigger today,
/// hand pinch later) drives click-to-select, click-selected-again (context menu request),
/// press-and-drag manipulation, and empty-click deselect. Replaces the trigger-select /
/// grip-grab split; old button paths stay as debug fallbacks.
///
/// Input comes from an ISDK RayInteractor's Select state, not from a specific button, so
/// any interactor whose selector is a pinch works the same way.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADPointerInteraction : MonoBehaviour
{
    /// <summary>True while an enabled instance owns ray selection (legacy adapters defer).</summary>
    public static bool Active { get; private set; }

    [Tooltip("Which controller's ray drives interaction.")]
    [SerializeField] private Handedness handedness = Handedness.Right;

    // Renamed from dragStartDistance/dragStartAngle so existing scene values (1.5 cm / 2.5°,
    // too sensitive to the trigger-squeeze jolt) don't override the new defaults.
    [Header("Click / Drag")]
    [Tooltip("Pointer travel (m) before a press on CAD can become a drag.")]
    [SerializeField, Min(0f)] private float dragDistance = 0.03f;
    [Tooltip("Pointer rotation (degrees) before a press on CAD can become a drag.")]
    [SerializeField, Min(0f)] private float dragAngle = 6f;
    [Tooltip("Minimum hold (s) before movement starts a drag, so the trigger-squeeze jolt stays a click.")]
    [SerializeField, Min(0f)] private float dragDelay = 0.15f;
    [Tooltip("Moving this many times the distance/angle threshold starts a drag immediately.")]
    [SerializeField, Min(1f)] private float fastDragFactor = 3f;
    [SerializeField, Min(0.05f)] private float clickMaxDuration = 1.0f;

    [Header("Translation Gain (same as CADXRGrab)")]
    [SerializeField, Min(0f)] private float reachDistance = 0.5f;
    [SerializeField, Min(0f)] private float maxExtraGain = 4f;
    [SerializeField, Min(0f)] private float decayRate = 1.5f;

    /// <summary>The selected CAD object was activated again (menu hook; no visual yet).</summary>
    public event Action<CADContextMenuRequest> ContextMenuRequested;

    public bool IsManipulating => session.IsActive;
    public CADPointerStateMachine.State State => machine.Current;

    private const float RaySearchInterval = 1f;

    private CADVisionManipulationService manipulationService;
    private CADXRGrab gripFallback;
    private readonly CADPointerStateMachine machine = new CADPointerStateMachine();
    private readonly CADGrabSession session = new CADGrabSession();
    private RayInteractor ray;
    private float nextRaySearch;
    private bool wasSelecting;
    private bool pressedSelectedTarget; // The press started on the already-selected object.

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        gripFallback = GetComponent<CADXRGrab>();
    }

    private void OnEnable() => Active = true;

    private void OnDisable()
    {
        Active = false;
        EndManipulation("component disabled");
        machine.Cancel();
        wasSelecting = false;
    }

    // LateUpdate: ISDK interactors resolve state in Update, and the rig has moved by now.
    private void LateUpdate()
    {
        ApplySettings();

        if (!ResolveRay())
        {
            if (wasSelecting)
                Handle(machine.Cancel(), "pointer lost");
            wasSelecting = false;
            return;
        }

        bool selecting = ray.State == InteractorState.Select;
        Pose pose = new Pose(ray.Origin, ray.Rotation);

        if (selecting && !wasSelecting)
            Press(pose);
        else if (selecting)
            Handle(machine.Move(pose, Time.unscaledTime), null, pose);
        else if (wasSelecting)
            Handle(machine.Up(Time.unscaledTime), null, pose);

        if (session.IsActive && !session.Update(pose))
            EndManipulation("object no longer selected or active");

        wasSelecting = selecting;
    }

    private void Press(Pose pose)
    {
        CADPointerTargetKind kind = Classify(out string cadId, out Vector3? hitPoint);

        // Decided before anything changes: was this press on the one selected object?
        pressedSelectedTarget = false;
        if (kind == CADPointerTargetKind.Cad)
        {
            string resolved = manipulationService.ResolveSelectable(cadId);
            var selected = manipulationService.GetSelectedObjects().ToList();
            pressedSelectedTarget = resolved != null && selected.Count == 1 && selected[0].id == resolved;
        }

        machine.Down(kind, cadId, hitPoint, pose, Time.unscaledTime);
        Debug.Log($"[CADPointer] Down on {kind}{(cadId != null ? $" '{cadId}'" : "")}" +
            $"{(pressedSelectedTarget ? " (already selected)" : "")}.");
    }

    private void Handle(CADPointerStateMachine.Intent intent, string reason = null, Pose pose = default)
    {
        switch (intent)
        {
            case CADPointerStateMachine.Intent.ClickCad:
                if (pressedSelectedTarget)
                    RequestContextMenu();
                else
                    manipulationService.SelectFromHit(machine.PressCadId,
                        machine.PressHasHitPoint ? machine.PressHitPoint : (Vector3?)null);
                break;

            case CADPointerStateMachine.Intent.ClickEmpty:
                Debug.Log("[CADPointer] Empty click; clearing selection.");
                manipulationService.ClearSelection();
                break;

            case CADPointerStateMachine.Intent.ClickUi:
                // UI handles its own click; CAD selection is deliberately untouched.
                Debug.Log("[CADPointer] UI click; selection kept.");
                break;

            case CADPointerStateMachine.Intent.BeginDrag:
                BeginManipulation(pose);
                break;

            case CADPointerStateMachine.Intent.EndDrag:
                EndManipulation(reason ?? "released");
                break;
        }
    }

    private void RequestContextMenu()
    {
        CADObject target = manipulationService.GetSelectedObjects().FirstOrDefault();
        if (target == null)
            return;

        bool fromHit = machine.PressHasHitPoint;
        Vector3 anchor = fromHit ? machine.PressHitPoint
            : manipulationService.TryGetSelectionPoint(out Vector3 point) ? point
            : CADGrabSession.VisualCenter(target);
        var request = new CADContextMenuRequest(target.id, anchor, fromHit);

        Debug.Log($"[CADPointer] ContextMenuRequested '{target.id}' at {anchor} " +
            $"({(fromHit ? "hit point" : "fallback")}).");
        ContextMenuRequested?.Invoke(request);
    }

    private void BeginManipulation(Pose pose)
    {
        if (gripFallback != null && gripFallback.IsGrabbing)
        {
            Debug.Log("[CADPointer] Drag ignored: grip fallback is holding an object.");
            return;
        }

        // Select at drag start (scope-aware, stores the pressed point as the grab point).
        manipulationService.SelectFromHit(machine.PressCadId,
            machine.PressHasHitPoint ? machine.PressHitPoint : (Vector3?)null);
        CADObject target = manipulationService.GetSelectedObjects().FirstOrDefault();
        if (target == null || !target.gameObject.activeInHierarchy)
        {
            Debug.Log("[CADPointer] Drag ignored: nothing selectable at the press point.");
            return;
        }

        string info = session.Begin(manipulationService, target, pose);
        Debug.Log($"[CADPointer] Drag started: '{target.id}' ({target.name}); {info}.");
    }

    private void EndManipulation(string reason)
    {
        if (!session.IsActive)
            return;

        Debug.Log($"[CADPointer] Drag ended: '{session.GrabbedId}' ({reason}).");
        session.End();
    }

    // CAD: the interactable belongs to a CADObject (CADXRRaySetup only provisions CAD colliders).
    // UI: any other interactable. None: the ray is over nothing interactive.
    private CADPointerTargetKind Classify(out string cadId, out Vector3? hitPoint)
    {
        cadId = null;
        hitPoint = null;

        RayInteractable target = ray.HasSelectedInteractable ? ray.SelectedInteractable
            : ray.HasInteractable ? ray.Interactable
            : ray.Candidate;
        if (target == null)
            return CADPointerTargetKind.None;

        hitPoint = ray.CollisionInfo.HasValue ? ray.CollisionInfo.Value.Point : ray.End;

        if (target.GetComponentInParent<CADUIPointerTarget>() != null)
            return CADPointerTargetKind.Ui;

        CADObject owner = target.GetComponentInParent<CADObject>();
        if (owner == null || string.IsNullOrEmpty(owner.id))
            return CADPointerTargetKind.Ui;

        cadId = owner.id;
        return CADPointerTargetKind.Cad;
    }

    private bool ResolveRay()
    {
        if (ray != null && ray.isActiveAndEnabled)
            return true;

        if (Time.unscaledTime < nextRaySearch)
            return false;
        nextRaySearch = Time.unscaledTime + RaySearchInterval;

        ray = null;
        foreach (RayInteractor candidate in FindObjectsByType<RayInteractor>(FindObjectsInactive.Exclude))
        {
            // Controller ray prefabs carry their ControllerRef on the interactor's GameObject.
            if (candidate.isActiveAndEnabled &&
                candidate.TryGetComponent(out ControllerRef controllerRef) &&
                controllerRef.Handedness == handedness)
            {
                ray = candidate;
                Debug.Log($"[CADPointer] Using {handedness} ray '{ray.gameObject.name}'.");
                break;
            }
        }

        return ray != null;
    }

    private void ApplySettings()
    {
        machine.DragStartDistance = dragDistance;
        machine.DragStartAngle = dragAngle;
        machine.DragStartDelay = dragDelay;
        machine.FastDragFactor = fastDragFactor;
        machine.ClickMaxDuration = clickMaxDuration;
        session.ReachDistance = reachDistance;
        session.MaxExtraGain = maxExtraGain;
        session.DecayRate = decayRate;
    }
}
