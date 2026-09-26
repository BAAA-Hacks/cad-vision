using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;

/// <summary>
/// Provisions ray selection for the manipulation service's registered CAD objects.
/// Add alongside the service. Does not create colliders or own CAD selection state.
/// </summary>
[DefaultExecutionOrder(100)] // Service.Start registers objects before this Start.
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADXRRaySetup : MonoBehaviour
{
    private CADVisionManipulationService manipulationService;

    private void Start()
    {
        ConfigureRegisteredObjects();
    }

    /// <summary>
    /// Safe to repeat after startup, e.g. after adding colliders to registered objects.
    /// Object registration remains the responsibility of the manipulation service.
    /// </summary>
    public void ConfigureRegisteredObjects()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();

        foreach (CADObject owner in manipulationService.RegisteredObjects)
        {
            if (owner == null)
                continue;

            var targets = new List<RayInteractable>();
            var visitedObjects = new HashSet<GameObject>();

            foreach (Collider collider in owner.GetComponentsInChildren<Collider>(true))
            {
                // Include inactive ancestors and stop at the nearest CAD identity.
                if (FindOwner(collider.transform) != owner ||
                    !visitedObjects.Add(collider.gameObject))
                {
                    continue;
                }

                ConfigureColliderObject(collider.gameObject, targets);
            }

            CADXRSelection selection = owner.GetComponent<CADXRSelection>();
            if (selection == null)
                selection = owner.gameObject.AddComponent<CADXRSelection>();

            selection.Initialize(manipulationService, owner, targets);
        }
    }

    private static CADObject FindOwner(Transform current)
    {
        while (current != null)
        {
            if (current.TryGetComponent(out CADObject owner))
                return owner;
            current = current.parent;
        }
        return null;
    }

    private static void ConfigureColliderObject(GameObject target,
        List<RayInteractable> targets)
    {
        Collider[] colliders = target.GetComponents<Collider>();
        ColliderSurface[] surfaces = target.GetComponents<ColliderSurface>();
        RayInteractable[] rays = target.GetComponents<RayInteractable>();

        // Use component slots, not GetComponent: a GameObject may have several
        // colliders. Repeating setup reuses the same slots without adding pairs.
        for (int i = 0; i < colliders.Length; i++)
        {
            ColliderSurface surface = i < surfaces.Length
                ? surfaces[i] : target.AddComponent<ColliderSurface>();
            surface.InjectAllColliderSurface(colliders[i]);

            RayInteractable ray = i < rays.Length
                ? rays[i] : target.AddComponent<RayInteractable>();
            ray.InjectAllRayInteractable(surface);
            // Start caches the fallback select surface. Set it explicitly too,
            // so repairing a previously started/manual target works correctly.
            ray.InjectOptionalSelectSurface(surface);
            targets.Add(ray);
        }
    }
}
