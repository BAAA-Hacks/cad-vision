using UnityEngine;
using UnityEngine.InputSystem;

public class CADSelection : MonoBehaviour
{
    [SerializeField]
    private CADVisionManipulationService manipulationService;

    private void Update()
    {
        if (Mouse.current == null ||
            !Mouse.current.leftButton.wasPressedThisFrame)
        {
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(
            Mouse.current.position.ReadValue()
        );

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            CADObject cadObject =
                hit.collider.GetComponentInParent<CADObject>();

            if (cadObject != null)
            {
                manipulationService.SelectFromHit(cadObject.id, hit.point);
                return;
            }
        }

        manipulationService.ClearSelection();
    }
}