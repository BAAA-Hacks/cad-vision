using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Pointer source over an Interaction SDK RayInteractor. The same adapter serves the
/// controller ray (ControllerRef, trigger selector) and the hand ray (HandRef, index-pinch
/// selector, HandPointerPose): the SDK already turns either into a ray pose and a Select
/// state, and draws the ray, cursor and pinch feedback.
/// </summary>
public sealed class CADRayPointerSource : ICADPointerSource
{
    private readonly IActiveState tracking; // ControllerRef / HandRef: connected and tracked.

    public RayInteractor Ray { get; }
    public CADPointerSourceKind Kind { get; }
    public CADPointerHandedness Handedness { get; }
    public string SourceId { get; }

    private CADRayPointerSource(RayInteractor ray, CADPointerSourceKind kind, CADPointerHandedness handedness,
        IActiveState tracking)
    {
        Ray = ray;
        Kind = kind;
        Handedness = handedness;
        this.tracking = tracking;
        SourceId = $"{handedness} {kind}";
    }

    /// <summary>
    /// Wraps a controller or hand ray. Rig prefabs put the ControllerRef / HandRef on the
    /// interactor's own GameObject; other rays are not CAD pointers.
    /// </summary>
    public static bool TryCreate(RayInteractor ray, out CADRayPointerSource source)
    {
        source = null;
        if (ray == null)
            return false;

        try
        {
            if (ray.TryGetComponent(out ControllerRef controller))
                source = new CADRayPointerSource(ray, CADPointerSourceKind.Controller, Map(controller.Handedness), controller);
            else if (ray.TryGetComponent(out HandRef hand))
                source = new CADRayPointerSource(ray, CADPointerSourceKind.Hand, Map(hand.Handedness), hand);
        }
        catch (System.NullReferenceException)
        {
            return false; // Ref not initialized yet; discovery retries.
        }

        return source != null;
    }

    // The SDK marks the interactor Disabled when its hand/controller isn't tracked (its
    // ActiveState), so hands and controllers switch without any button.
    public bool IsAvailable =>
        Ray != null && Ray.isActiveAndEnabled && Ray.State != InteractorState.Disabled &&
        (tracking == null || tracking.Active);

    public Pose Pose => new Pose(Ray.Origin, Ray.Rotation);

    public bool IsSelecting => Ray.State == InteractorState.Select;

    // CAD: the interactable belongs to a CADObject (CADXRRaySetup only provisions CAD colliders).
    // UI: any other interactable. None: the ray is over nothing interactive.
    public CADPointerTargetKind Classify(out string cadId, out Vector3? hitPoint)
    {
        cadId = null;
        hitPoint = null;

        RayInteractable target = Ray.HasSelectedInteractable ? Ray.SelectedInteractable
            : Ray.HasInteractable ? Ray.Interactable
            : Ray.Candidate;
        if (target == null)
            return CADPointerTargetKind.None;

        hitPoint = Ray.CollisionInfo.HasValue ? Ray.CollisionInfo.Value.Point : Ray.End;

        if (target.GetComponentInParent<CADUIPointerTarget>() != null)
            return CADPointerTargetKind.Ui;

        CADObject owner = target.GetComponentInParent<CADObject>();
        if (owner == null || string.IsNullOrEmpty(owner.id))
            return CADPointerTargetKind.Ui;

        cadId = owner.id;
        return CADPointerTargetKind.Cad;
    }

    private static CADPointerHandedness Map(Oculus.Interaction.Input.Handedness handedness) =>
        handedness == Oculus.Interaction.Input.Handedness.Left ? CADPointerHandedness.Left : CADPointerHandedness.Right;
}
