using System.Linq;
using UnityEngine;

/// <summary>
/// Test input for interaction scope: A enters the selected assembly, B exits one level.
/// Thin adapter; the service validates and owns all scope/selection state.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADXRScopeControls : MonoBehaviour
{
    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch;

    private CADVisionManipulationService manipulationService;

    private void Awake()
    {
        manipulationService = GetComponent<CADVisionManipulationService>();
    }

    // TEMP diagnostics.
    private void Start()
    {
        Debug.Log($"[CADXRScopeControls] Started (controller {controller}, " +
            $"scope {manipulationService.CurrentScopeId ?? "<model>"}).");
    }

    private void Update()
    {
        // Button.One/Two on RTouch are A/B.
        if (OVRInput.GetDown(OVRInput.Button.One, controller))
        {
            CADObject selected = manipulationService.GetSelectedObjects().FirstOrDefault();
            Debug.Log($"[CADXRScopeControls] A pressed; selected: {selected?.id ?? "<none>"}, " +
                $"scope: {manipulationService.CurrentScopeId ?? "<model>"}.");

            if (selected != null)
            {
                Debug.Log($"[CADXRScopeControls] Calling EnterScope('{selected.id}').");
                manipulationService.EnterScope(selected.id);
            }
            else
            {
                Debug.Log("[CADXRScopeControls] EnterScope not called: nothing selected.");
            }
        }

        if (OVRInput.GetDown(OVRInput.Button.Two, controller))
        {
            Debug.Log($"[CADXRScopeControls] B pressed; calling ExitScope() from scope " +
                $"{manipulationService.CurrentScopeId ?? "<model>"}.");
            manipulationService.ExitScope();
        }
    }
}
