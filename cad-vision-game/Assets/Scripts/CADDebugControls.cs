using UnityEngine;
using UnityEngine.InputSystem;

public class CADDebugControls : MonoBehaviour
{
    [SerializeField]
    private CADVisionManipulationService manipulationService;

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        if (Keyboard.current.digit1Key.wasPressedThisFrame)
            manipulationService.Isolate("SUBASSEMBLY_001");

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
            manipulationService.ShowAll();

        if (Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            manipulationService.MoveObject(
                "SUBASSEMBLY_001",
                new Vector3(0, 1, 0)
            );
        }

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
            manipulationService.ResetObject("SUBASSEMBLY_001");

        if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            manipulationService.RotateModel(
                new Vector3(0, 45, 0)
            );
        }

        if (Keyboard.current.digit6Key.wasPressedThisFrame)
            manipulationService.ScaleModel(1.25f);

        if (Keyboard.current.digit7Key.wasPressedThisFrame)
            manipulationService.ResetModelTransform();

        if (Keyboard.current.digit8Key.wasPressedThisFrame)
        {
            manipulationService.Highlight(
                new[] { "COMP_001", "COMP_003" }
            );
        }

        if (Keyboard.current.digit9Key.wasPressedThisFrame)
            manipulationService.ClearHighlights();
    }
}