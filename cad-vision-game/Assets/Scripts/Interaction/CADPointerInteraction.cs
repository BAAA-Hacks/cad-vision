using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Semantic pointer interaction: a ray + one "select" signal drives click-to-select,
/// click-selected-again (context menu request), press-and-drag manipulation, and empty-click
/// deselect. Old button paths stay as debug fallbacks.
///
/// Input comes from pointer sources (ICADPointerSource), not buttons: controller rays
/// (trigger) and hand rays (index pinch) are discovered from the Interaction SDK rig; tests or
/// a desktop mouse can register their own. Every source feeds the same state machine, grab
/// session, menus and service calls.
///
/// Arbitration: the first source to press owns the interaction until it releases or loses
/// tracking; presses from other sources meanwhile never click, select or steal the drag. The
/// one exception is scaling: a source of the other hand pressing on the same logical target
/// (the held object, any member of the held group, any geometry of the held assembly, any
/// model geometry in model mode) joins as the scale partner (the shared CADScaleGesture), both
/// while the owner drags and while its press is still pending (Pressed, not yet a drag): then
/// the pending press is promoted to a drag at the current pose, so both hands can grab at about
/// the same time with no wait and no click. If the owner releases first, the partner is
/// promoted to owner and keeps dragging. A source that appears (e.g. hands replacing
/// controllers) with select already held is ignored until it releases, so switching never
/// clicks.
///
/// While the service's whole-model manipulation mode is active, a drag on any CAD geometry
/// holds the model root (at the pressed point) instead of an object; clicks leave selection
/// alone.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADPointerInteraction : MonoBehaviour
{
    /// <summary>True while an enabled instance owns ray selection (legacy adapters defer).</summary>
    public static bool Active { get; private set; }

    // Left and right are symmetric: every source feeds the same state machine, session, scaling
    // and UI path, and whichever presses first owns the interaction.
    [Header("Pointer Sources")]
    [Tooltip("Controller rays: trigger = select.")]
    [SerializeField] private bool useRightController = true;
    [SerializeField] private bool useLeftController = true;
    [Tooltip("Hand rays: index pinch = select.")]
    [SerializeField] private bool useRightHand = true;
    [SerializeField] private bool useLeftHand = true;

    [Header("Ray Visuals")]
    [Tooltip("Show the hand ray whenever the hand is pointing, not only over something interactive (easier aiming).")]
    [SerializeField] private bool alwaysShowHandRay = true;
    [Tooltip("Show the controller ray all the time, not only over something interactive.")]
    [SerializeField] private bool alwaysShowControllerRay = true;
    [Tooltip("Drawn length of the always-on rays (m). The rig default is a 0.25 m stub.")]
    [SerializeField, Min(0.1f)] private float handRayVisualLength = 1.5f;

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

    [Header("Two-Pointer Scaling (same limits as CADXRGrab)")]
    [SerializeField, Min(0.01f)] private float minimumScaleSeparation = 0.08f;
    [SerializeField, Range(0.01f, 1f)] private float minimumScaleRatio = 0.1f;
    [SerializeField, Min(1f)] private float maximumScaleRatio = 10f;

    /// <summary>The selected CAD object was activated again (menu hook; no visual yet).</summary>
    public event Action<CADContextMenuRequest> ContextMenuRequested;

    /// <summary>
    /// A source pressed on UI at a world point (e.g. a panel's title bar). Listeners may follow
    /// that source's pose while it stays selected; the press is otherwise a normal UI press.
    /// </summary>
    public event Action<ICADPointerSource, Vector3> UiPressed;

    public bool IsManipulating => session.IsActive;
    public bool IsManipulatingModel => session.IsModel;
    public bool IsScaling => pointerScale.IsActive;
    public CADPointerStateMachine.State State => machine.Current;
    /// <summary>The source that owns the current press/drag, if any.</summary>
    public string OwnerSourceId => owner?.SourceId;

    private const float SourceSearchInterval = 1f;

    private sealed class SourceState
    {
        public bool WasAvailable;
        public bool WasSelecting;
    }

    private CADVisionManipulationService manipulationService;
    private CADXRGrab gripFallback;
    private readonly CADPointerStateMachine machine = new CADPointerStateMachine();
    private readonly CADGrabSession session = new CADGrabSession();
    private readonly CADScaleGesture pointerScale = new CADScaleGesture();
    private readonly List<ICADPointerSource> sources = new();
    private readonly Dictionary<ICADPointerSource, SourceState> sourceStates = new();
    private float nextSourceSearch;
    private ICADPointerSource owner;        // Source whose press the state machine tracks.
    private ICADPointerSource scalePartner; // Second source during two-pointer scaling.
    private bool pressedSelectedTarget; // The press started on an already-selected object.
    private string pressedResolvedId;   // The selectable object the press resolved to.
    private bool scaledLastFrame;       // Something scaled the held target last frame (rebase once more).

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
        gripFallback = GetComponent<CADXRGrab>();
    }

    private void OnEnable() => Active = true;

    private void OnDisable()
    {
        Active = false;
        EndPointerScale("component disabled");
        EndManipulation("component disabled");
        machine.Cancel();
        owner = null;
        // Selects still held when re-enabled are ignored until released.
        foreach (SourceState state in sourceStates.Values)
            state.WasAvailable = false;
    }

    // LateUpdate: ISDK interactors resolve state in Update, and the rig has moved by now.
    private void LateUpdate()
    {
        ApplySettings();
        DiscoverRaySources();
        ProcessSources(Time.unscaledTime);
    }

    // ---------------- Sources ----------------

    /// <summary>Adds a pointer source (the SDK's controller/hand rays are found automatically).</summary>
    public void RegisterSource(ICADPointerSource source)
    {
        if (source == null || sources.Contains(source))
            return;

        sources.Add(source);
        sourceStates[source] = new SourceState();
        Debug.Log($"[CADPointer] Pointer source added: {source.SourceId}.");
    }

    public void UnregisterSource(ICADPointerSource source)
    {
        if (source == null || !sources.Remove(source))
            return;

        SourceLost(source);
        sourceStates.Remove(source);
        Debug.Log($"[CADPointer] Pointer source removed: {source.SourceId}.");
    }

    // Controller and hand RayInteractors from the rig (inactive ones too: hand interactors are
    // switched on and off with tracking). Rechecked periodically for late-spawned rigs.
    private void DiscoverRaySources()
    {
        if (Time.unscaledTime < nextSourceSearch)
            return;
        nextSourceSearch = Time.unscaledTime + SourceSearchInterval;

        foreach (ICADPointerSource stale in sources
                     .Where(src => src is CADRayPointerSource raySource && raySource.Ray == null).ToList())
            UnregisterSource(stale);

        foreach (RayInteractor ray in FindObjectsByType<RayInteractor>(FindObjectsInactive.Include))
        {
            if (sources.Any(src => src is CADRayPointerSource known && known.Ray == ray))
                continue;
            if (CADRayPointerSource.TryCreate(ray, out CADRayPointerSource source) && IsWanted(source))
            {
                RegisterSource(source);
                bool alwaysOn = source.Kind == CADPointerSourceKind.Hand ? alwaysShowHandRay
                    : source.Kind == CADPointerSourceKind.Controller && alwaysShowControllerRay;
                if (alwaysOn)
                    ConfigureRayVisual(ray);
            }
        }
    }

    // The rig's hand and controller ray visuals hide unless they're over an interactable and
    // are only 0.25 m long. RayInteractorRayVisual exposes no setters for those, so its two
    // serialized fields are set directly (the SDK still hides a hand ray whenever the hand
    // isn't in a pointing pose, and any ray whose hand/controller isn't tracked).
    private void ConfigureRayVisual(RayInteractor ray)
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo interactorField = typeof(RayInteractorRayVisual).GetField("_rayInteractor", Fields);
        FieldInfo hideField = typeof(RayInteractorRayVisual).GetField("_hideWhenNoInteractable", Fields);
        FieldInfo lengthField = typeof(RayInteractorRayVisual).GetField("_maxRayVisualLength", Fields);
        if (interactorField == null || hideField == null || lengthField == null)
        {
            Debug.LogWarning("[CADPointer] RayInteractorRayVisual fields changed in this SDK version; ray visual left as is.");
            return;
        }

        foreach (RayInteractorRayVisual visual in FindObjectsByType<RayInteractorRayVisual>(FindObjectsInactive.Include))
        {
            if (!ReferenceEquals(interactorField.GetValue(visual), ray))
                continue;
            hideField.SetValue(visual, false);
            lengthField.SetValue(visual, handRayVisualLength);
            Debug.Log($"[CADPointer] Ray visual on '{ray.name}': always shown, {handRayVisualLength:F1} m.");
        }
    }

    private bool IsWanted(ICADPointerSource source) => source.Kind switch
    {
        CADPointerSourceKind.Controller => source.Handedness == CADPointerHandedness.Right ? useRightController : useLeftController,
        CADPointerSourceKind.Hand => source.Handedness == CADPointerHandedness.Right ? useRightHand : useLeftHand,
        _ => true,
    };

    // One frame of semantic input from every source.
    private void ProcessSources(float time)
    {
        foreach (ICADPointerSource source in sources.ToList())
        {
            SourceState state = sourceStates[source];
            if (!source.IsAvailable)
            {
                if (state.WasAvailable)
                    SourceLost(source);
                state.WasAvailable = false;
                state.WasSelecting = false;
                continue;
            }

            bool selecting = source.IsSelecting;
            if (!state.WasAvailable)
            {
                // Newly tracked (or re-enabled) with select already held: not a press.
                state.WasAvailable = true;
                state.WasSelecting = selecting;
                if (selecting)
                    Debug.Log($"[CADPointer] {source.SourceId} appeared with select held; ignored until released.");
                continue;
            }

            Pose pose = source.Pose;
            if (selecting && !state.WasSelecting)
                SourcePressed(source, pose, time);
            else if (!selecting && state.WasSelecting)
                SourceReleased(source, pose, time);
            else if (selecting && source == owner)
                Handle(machine.Move(pose, time), null, pose);
            state.WasSelecting = selecting;
        }

        if (owner != null && owner.IsAvailable)
        {
            UpdatePointerScale();
            UpdateSession(owner.Pose);
        }
    }

    private void SourcePressed(ICADPointerSource source, Pose pose, float time)
    {
        if (owner == null)
        {
            owner = source;
            Press(source, pose, time);
            return;
        }

        if (TryBeginPointerScale(source) || TryJoinPendingPress(source))
            return;

        Debug.Log($"[CADPointer] {source.SourceId} press ignored: {owner.SourceId} owns the interaction.");
    }

    private void SourceReleased(ICADPointerSource source, Pose pose, float time)
    {
        if (source == scalePartner)
        {
            EndPointerScale($"{source.SourceId} released");
            return;
        }

        if (source != owner)
            return; // An ignored press ends: nothing to do (never a click).

        // Two-pointer gesture and the owner lets go first: the partner keeps the drag.
        if (scalePartner != null && session.IsActive && scalePartner.IsAvailable && scalePartner.IsSelecting)
        {
            ICADPointerSource partner = scalePartner;
            EndPointerScale($"{source.SourceId} released; {partner.SourceId} continues");
            owner = partner;
            session.Rebase(partner.Pose);
            scaledLastFrame = false;
            return;
        }

        EndPointerScale($"{source.SourceId} released");
        owner = null;
        Handle(machine.Up(time), null, pose);
    }

    // Tracking lost / interactor disabled / source removed: cancel, never click.
    private void SourceLost(ICADPointerSource source)
    {
        if (source == scalePartner)
            EndPointerScale($"{source.SourceId} lost");

        if (source != owner)
            return;

        EndPointerScale($"{source.SourceId} lost");
        owner = null;
        Handle(machine.Cancel(), $"{source.SourceId} lost");
        EndManipulation($"{source.SourceId} lost");
    }

    // ---------------- Two-pointer scaling ----------------

    // While the owner drags, the other hand pressing on the held object (or anywhere on the
    // model in model mode) scales it around the owner's held point.
    private bool TryBeginPointerScale(ICADPointerSource source)
    {
        if (!session.IsActive || pointerScale.IsActive || source.Handedness == owner.Handedness)
            return false;

        if (source.Classify(out string cadId, out _) != CADPointerTargetKind.Cad || !IsOnHeldTarget(cadId))
            return false;

        float distance = Vector3.Distance(owner.Pose.position, source.Pose.position);
        Vector3 pivot = session.GrabPointWorld;
        bool started = session.IsModel
            ? pointerScale.TryBeginModel(manipulationService, distance, pivot)
            : pointerScale.TryBeginObjects(manipulationService, session.HeldObjects.ToList(), distance, pivot);
        if (!started)
            return false;

        scalePartner = source;
        Debug.Log($"[CADPointer] Two-pointer scaling started ({owner.SourceId} + {source.SourceId}, " +
            $"{(session.IsModel ? "model" : session.Description)}).");
        return true;
    }

    // The owner's press is still pending (not yet a drag) and the other hand presses the same
    // logical target far enough away: start the owner's drag now, at its current pose, and join
    // as the scale partner. Checked before anything changes, so an invalid second press leaves
    // the pending press (and its click) exactly as it was.
    private bool TryJoinPendingPress(ICADPointerSource source)
    {
        if (machine.Current != CADPointerStateMachine.State.Pressed || machine.PressKind != CADPointerTargetKind.Cad ||
            source.Handedness == owner.Handedness || scalePartner != null)
        {
            return false;
        }

        if (source.Classify(out string cadId, out _) != CADPointerTargetKind.Cad)
            return false;

        float distance = Vector3.Distance(owner.Pose.position, source.Pose.position);
        if (!(distance >= Mathf.Max(0.01f, minimumScaleSeparation)) || !WouldHoldTarget(cadId))
            return false;

        Handle(machine.BeginDragNow(), null, owner.Pose);
        if (!session.IsActive)
            return false; // The drag was refused (e.g. grip fallback holding); nothing to scale.

        Debug.Log($"[CADPointer] {source.SourceId} joined {owner.SourceId}'s press: drag started for two-pointer scaling.");
        return TryBeginPointerScale(source);
    }

    // What BeginManipulation would hold for the owner's pending press contains cadId: any CAD in
    // model mode; the selection's transform roots for a group drag; else the pressed object.
    private bool WouldHoldTarget(string cadId)
    {
        if (manipulationService.IsModelManipulationActive)
            return manipulationService.ModelRoot != null;

        bool picking = manipulationService.IsMultiSelectActive;
        bool pressedSelected = pressedResolvedId != null && manipulationService.IsSelected(pressedResolvedId);
        HashSet<string> held;
        if (picking || (pressedSelected && manipulationService.GetSelectedIds().Count > 1))
        {
            held = new HashSet<string>(manipulationService.GetSelectedTransformRoots());
            if (picking && !pressedSelected && pressedResolvedId != null)
                held.Add(pressedResolvedId);
        }
        else
        {
            if (pressedResolvedId == null)
                return false;
            held = new HashSet<string> { pressedResolvedId };
        }

        return IsUnder(cadId, held);
    }

    // The hit belongs to what the session holds: any CAD in model mode; otherwise the hit or
    // one of its logical ancestors (up to the first detached unit) is a held object.
    private bool IsOnHeldTarget(string cadId) =>
        session.IsModel || IsUnder(cadId, new HashSet<string>(session.GrabbedIds));

    private bool IsUnder(string cadId, HashSet<string> held)
    {
        for (string id = cadId; id != null; id = manipulationService.GetLogicalParentId(id))
        {
            if (held.Contains(id))
                return true;
            if (manipulationService.IsDetached(id))
                break;
        }

        return false;
    }

    private void UpdatePointerScale()
    {
        if (!pointerScale.IsActive)
            return;

        if (scalePartner == null || !scalePartner.IsAvailable)
        {
            EndPointerScale("second pointer lost");
            return;
        }

        float distance = Vector3.Distance(owner.Pose.position, scalePartner.Pose.position);
        if (!pointerScale.Update(distance))
            EndPointerScale("scaled target gone");
    }

    private void EndPointerScale(string reason)
    {
        if (!pointerScale.IsActive && scalePartner == null)
            return;

        pointerScale.End();
        scalePartner = null;
        Debug.Log($"[CADPointer] Two-pointer scaling ended ({reason}).");
    }

    // ---------------- Interaction ----------------

    private void UpdateSession(Pose pose)
    {
        // While two-pointer or grip model scaling runs it owns the held transform: follow its
        // result instead of applying the held pose, and once more after it ends, so neither
        // hand snaps it back.
        bool scaling = pointerScale.IsActive ||
            (session.IsModel && gripFallback != null && gripFallback.IsScalingModel);
        if (session.IsActive && (scaling || scaledLastFrame))
            session.Rebase(pose);
        else if (session.IsActive && !session.Update(pose))
            EndManipulation("object no longer selected or active");
        scaledLastFrame = scaling;
    }

    /// <summary>The held point of an active whole-model drag (two-hand scaling pivots on it).</summary>
    public bool TryGetModelGrabPoint(out Vector3 point)
    {
        point = session.IsModel ? session.GrabPointWorld : default;
        return session.IsModel;
    }

    private void Press(ICADPointerSource source, Pose pose, float time)
    {
        CADPointerTargetKind kind = source.Classify(out string cadId, out Vector3? hitPoint);

        // Decided before anything changes: was this press on something already selected?
        // (Multi-select toggles instead of opening a menu, so it never needs this.)
        pressedSelectedTarget = false;
        pressedResolvedId = kind == CADPointerTargetKind.Cad ? manipulationService.ResolveHitTarget(cadId) : null;
        if (pressedResolvedId != null && !manipulationService.IsMultiSelectActive)
            pressedSelectedTarget = manipulationService.IsSelected(pressedResolvedId);

        machine.Down(kind, cadId, hitPoint, pose, time);
        if (kind == CADPointerTargetKind.Ui && hitPoint.HasValue)
            UiPressed?.Invoke(source, hitPoint.Value);
        Debug.Log($"[CADPointer] {source.SourceId} down on {kind}{(cadId != null ? $" '{cadId}'" : "")}" +
            $"{(pressedSelectedTarget ? " (already selected)" : "")}.");
    }

    private void Handle(CADPointerStateMachine.Intent intent, string reason = null, Pose pose = default)
    {
        if (manipulationService.IsModelManipulationActive &&
            (intent == CADPointerStateMachine.Intent.ClickCad || intent == CADPointerStateMachine.Intent.ClickEmpty))
        {
            // Model mode: only drags act; the selection and the model menu are kept.
            Debug.Log($"[CADPointer] {intent} ignored during model manipulation.");
            return;
        }

        switch (intent)
        {
            case CADPointerStateMachine.Intent.ClickCad:
                if (manipulationService.IsMultiSelectActive)
                {
                    string toggled = manipulationService.ToggleFromHit(machine.PressCadId,
                        machine.PressHasHitPoint ? machine.PressHitPoint : (Vector3?)null);
                    Debug.Log($"[CADPointer] Multi-select toggle '{toggled}'.");
                }
                else if (pressedSelectedTarget)
                    RequestContextMenu();
                else
                    manipulationService.SelectFromHit(machine.PressCadId,
                        machine.PressHasHitPoint ? machine.PressHitPoint : (Vector3?)null);
                break;

            case CADPointerStateMachine.Intent.ClickEmpty:
                if (manipulationService.IsMultiSelectActive)
                {
                    // Keep the set; the selection menu's Clear / Done end multi-select.
                    Debug.Log("[CADPointer] Empty click ignored during multi-select.");
                    break;
                }

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

    // The menu decides object menu vs selection menu from the selection size.
    private void RequestContextMenu()
    {
        CADObject target = manipulationService.GetSelectedObjects()
            .FirstOrDefault(o => o.id == pressedResolvedId);
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

        if (manipulationService.IsModelManipulationActive)
        {
            BeginModelDrag(pose);
            return;
        }

        // Grabbing the selection moves the selection: any selected object when several are
        // selected, and anything while picking (an unselected object is added first).
        bool picking = manipulationService.IsMultiSelectActive;
        bool pressedSelected = pressedResolvedId != null && manipulationService.IsSelected(pressedResolvedId);
        if (picking || (pressedSelected && manipulationService.GetSelectedIds().Count > 1))
        {
            BeginGroupDrag(pose, addPressedFirst: picking && !pressedSelected);
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

    // Moves every selected transform root as one rigid group, held at the pressed point. The
    // selection is never collapsed. Roots avoid moving a selected child twice with its parent.
    private void BeginGroupDrag(Pose pose, bool addPressedFirst)
    {
        if (pressedResolvedId == null)
        {
            Debug.Log("[CADPointer] Group drag ignored: nothing selectable at the press point.");
            return;
        }

        if (addPressedFirst)
            manipulationService.AddToSelection(pressedResolvedId);

        List<CADObject> roots = manipulationService.GetSelectedTransformRoots()
            .Select(id => manipulationService.GetSelectedObjects().FirstOrDefault(o => o.id == id))
            .Where(o => o != null && o.gameObject.activeInHierarchy)
            .ToList();
        if (roots.Count == 0)
            return;

        // Anchor the grab point on the root that carries the pressed object.
        CADObject pressed = manipulationService.GetSelectedObjects().FirstOrDefault(o => o.id == pressedResolvedId);
        int anchor = pressed == null ? -1 : roots.FindIndex(r => pressed.transform.IsChildOf(r.transform));
        if (anchor > 0)
            (roots[0], roots[anchor]) = (roots[anchor], roots[0]);

        string info = session.Begin(manipulationService, roots, pose,
            machine.PressHasHitPoint ? machine.PressHitPoint : (Vector3?)null);
        Debug.Log($"[CADPointer] Group drag started: {string.Join(", ", roots.Select(r => r.name))}; {info}.");
    }

    // Holds the whole model root at the pressed point on the geometry (else the visible bounds
    // center): the root moves and rotates rigidly; no CAD object is moved individually.
    private void BeginModelDrag(Pose pose)
    {
        Transform root = manipulationService.ModelRoot;
        if (root == null)
        {
            Debug.Log("[CADPointer] Model drag ignored: no model root.");
            return;
        }

        Vector3 grabPoint = machine.PressHasHitPoint ? machine.PressHitPoint
            : manipulationService.TryGetModelBounds(out Bounds bounds) ? bounds.center
            : root.position;
        string info = session.BeginModel(manipulationService, pose, grabPoint);
        scaledLastFrame = false;
        Debug.Log($"[CADPointer] Model drag started ({(machine.PressHasHitPoint ? "hit point" : "bounds center")}); {info}.");
    }

    private void EndManipulation(string reason)
    {
        EndPointerScale(reason); // Nothing left to scale.
        if (!session.IsActive)
            return;

        Debug.Log($"[CADPointer] Drag ended: '{session.Description}' ({reason}).");
        session.End();
    }

    private void ApplySettings()
        => ConfigureMovement(machine, session);

    /// <summary>Shares the scene's tuned thresholds and reach assistance with movable UI.</summary>
    public void ConfigureMovement(CADPointerStateMachine machine, CADGrabSession session)
    {
        machine.DragStartDistance = dragDistance;
        machine.DragStartAngle = dragAngle;
        machine.DragStartDelay = dragDelay;
        machine.FastDragFactor = fastDragFactor;
        machine.ClickMaxDuration = clickMaxDuration;
        session.ReachDistance = reachDistance;
        session.MaxExtraGain = maxExtraGain;
        session.DecayRate = decayRate;
        pointerScale.MinimumSeparation = minimumScaleSeparation;
        pointerScale.MinimumRatio = minimumScaleRatio;
        pointerScale.MaximumRatio = maximumScaleRatio;
    }
}
