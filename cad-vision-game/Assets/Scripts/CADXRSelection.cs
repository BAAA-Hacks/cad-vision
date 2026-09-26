using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

[DisallowMultipleComponent]
public class CADXRSelection : MonoBehaviour
{
    private CADVisionManipulationService manipulationService;
    private CADObject cadObject;
    private readonly HashSet<RayInteractable> rayTargets = new();
    private bool subscribed;

    public void Initialize(CADVisionManipulationService service, CADObject owner,
        IEnumerable<RayInteractable> targets)
    {
        Unsubscribe();
        manipulationService = service;
        cadObject = owner;
        rayTargets.Clear();

        foreach (RayInteractable target in targets)
        {
            if (target != null)
                rayTargets.Add(target);
        }

        if (isActiveAndEnabled)
            Subscribe();
    }

    private void OnEnable() => Subscribe();
    private void OnDisable() => Unsubscribe();
    private void OnDestroy() => Unsubscribe();

    private void Subscribe()
    {
        // OnEnable can run before runtime initialization.
        if (subscribed || cadObject == null || manipulationService == null)
            return;

        foreach (RayInteractable target in rayTargets)
        {
            if (target != null)
                target.WhenPointerEventRaised += HandlePointerEvent;
        }
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        foreach (RayInteractable target in rayTargets)
        {
            if (target != null)
                target.WhenPointerEventRaised -= HandlePointerEvent;
        }
        subscribed = false;
    }

    private void HandlePointerEvent(PointerEvent evt)
    {
        // CADPointerInteraction owns click/drag semantics when present; this is the legacy path.
        if (CADPointerInteraction.Active)
            return;

        if (evt.Type == PointerEventType.Select && isActiveAndEnabled &&
            cadObject != null && manipulationService != null)
        {
            manipulationService.SelectFromHit(cadObject.id, evt.Pose.position);
        }
    }
}
