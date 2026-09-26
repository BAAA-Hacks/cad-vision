using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Trigger on empty space clears the CAD selection. Trigger over a CAD object is left to the
/// normal ray selection (RayInteractable → CADXRSelection → SelectFromHit). Thin adapter:
/// it only reads the controller ray's hover state and calls the service.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADXRDeselect : MonoBehaviour
{
    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch;

    private CADVisionManipulationService manipulationService;
    private bool warnedNoRay;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
    }

    // TEMP diagnostics: which rays exist at startup and which one matches this controller.
    private void Start()
    {
        RayInteractor[] rays = FindObjectsByType<RayInteractor>(FindObjectsInactive.Include);
        string matches = "";
        foreach (RayInteractor ray in rays)
        {
            if (ray.TryGetComponent(out ControllerRef controllerRef) &&
                controllerRef.Handedness == TargetHandedness)
            {
                matches += $" '{ray.gameObject.name}'(active={ray.isActiveAndEnabled})";
            }
        }

        Debug.Log($"[CADXRDeselect] Started ({controller}); {rays.Length} RayInteractor(s) in scene; " +
            $"{TargetHandedness} controller ray: {(matches.Length > 0 ? matches.Trim() : "NOT FOUND")}.");
    }

    private Handedness TargetHandedness => controller == OVRInput.Controller.LTouch
        ? Handedness.Left : Handedness.Right;

    private void Update()
    {
        // CADPointerInteraction handles empty clicks (and keeps selection on UI) when present.
        if (CADPointerInteraction.Active)
            return;

        if (!OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, controller))
            return;

        Debug.Log($"[CADXRDeselect] Trigger pressed ({controller}).");

        bool? overCad = IsRayOverCadObject();
        if (overCad == null)
        {
            // Without the controller's ray we can't tell; keep the selection.
            if (!warnedNoRay)
                Debug.LogWarning($"[CADXRDeselect] No active {controller} controller ray found; trigger deselect disabled.");
            warnedNoRay = true;
            return;
        }

        if (overCad == false)
        {
            Debug.Log("[CADXRDeselect] Ray not over a CAD object; calling ClearSelection().");
            manipulationService.ClearSelection();
        }
        else
        {
            Debug.Log("[CADXRDeselect] Ray over a CAD object; ClearSelection() not called.");
        }
    }

    // True/false for the matching controller's ray; null when no such ray is active.
    // Runs only on trigger press, so the lookup cost is negligible.
    private bool? IsRayOverCadObject()
    {
        Handedness handedness = TargetHandedness;
        bool foundRay = false;

        foreach (RayInteractor ray in FindObjectsByType<RayInteractor>(FindObjectsInactive.Exclude))
        {
            // Controller ray prefabs carry their ControllerRef on the interactor's GameObject.
            if (!ray.isActiveAndEnabled ||
                !ray.TryGetComponent(out ControllerRef controllerRef) ||
                controllerRef.Handedness != handedness)
            {
                continue;
            }

            foundRay = true;
            RayInteractable target = ray.HasInteractable ? ray.Interactable : ray.Candidate;
            // Ray targets are provisioned only on registered CAD colliders (CADXRRaySetup).
            CADObject cadObject = target != null ? target.GetComponentInParent<CADObject>() : null;
            Debug.Log($"[CADXRDeselect] Ray '{ray.gameObject.name}' (state {ray.State}): " +
                $"interactable={(ray.HasInteractable ? ray.Interactable.name : "none")}, " +
                $"candidate={(ray.HasCandidate ? ray.Candidate.name : "none")}, " +
                $"CADObject={(cadObject != null ? cadObject.id : "none")}.");
            if (cadObject != null)
                return true;
        }

        return foundRay ? false : (bool?)null;
    }
}
