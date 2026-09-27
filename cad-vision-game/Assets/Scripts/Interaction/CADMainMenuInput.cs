using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using IsdkController = Oculus.Interaction.Input.Controller;
using IsdkHandedness = Oculus.Interaction.Input.Handedness;

/// <summary>
/// Left controller Menu (≡) button → CADMainMenu.ToggleMainMenu(). Input detection only; the
/// menu owns everything else.
///
/// Button sources (either counts; the first to report a press is logged):
/// - OVRInput RawButton.Start on LTouch. With the Input System package this is Meta's
///   MetaQuestActionMap action "QuestTouch_ButtonStart", bound to
///   &lt;OculusTouchController&gt;{LeftHand}/menu (and the Touch Plus / Pro equivalents).
/// - Unity XR: the left-hand XR device's CommonUsages.menuButton (the same OpenXR
///   /user/hand/left/input/menu/click, independent of Meta's action map).
///
/// Controller vs hand: with hand tracking the runtime reports the left hand's system menu
/// gesture (and, on Quest, ordinary left pinches) through the same menu input. A press counts
/// as the controller button when the Interaction SDK's left Controller is connected — not
/// "is the hand tracked", which can be true while holding controllers (multimodal). Hand
/// presses are ignored unless leftHandMenuGesture is on.
///
/// Toggles only on the press edge; holding the button never re-toggles.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADMainMenu))]
public class CADMainMenuInput : MonoBehaviour
{
    [Tooltip("Also toggle on the left-hand menu gesture. Off: Quest reports ordinary left pinches as the same input.")]
    [SerializeField] private bool leftHandMenuGesture;
    [Tooltip("TEMP diagnostics: log initialization, button presses and toggles (never per frame).")]
    [SerializeField] private bool verboseLogging = true;

    private CADMainMenu mainMenu;
    private IsdkController leftController;
    private bool wasPressed;
    private bool loggedMissingController;

    /// <summary>Test hook: replaces the hardware read (true while the menu button is held).</summary>
    public Func<bool> ButtonProvider;
    /// <summary>Test hook: replaces "is the left controller (not the hand) in use".</summary>
    public Func<bool> ControllerInUseProvider;

    private void Awake()
    {
        mainMenu = GetComponent<CADMainMenu>();
    }

    private void Start()
    {
        leftController = FindLeftController();
        if (!verboseLogging)
            return;

        InputDevice device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        Debug.Log("[CADMainMenuInput] Initialized. Sources: OVRInput RawButton.Start on LTouch " +
            "(MetaQuestActionMap QuestTouch_ButtonStart → <OculusTouchController>{LeftHand}/menu), " +
            $"Unity XR CommonUsages.menuButton on left device '{(device.isValid ? device.name : "<none yet>")}'. " +
            $"Controller-in-use check: {(leftController != null ? $"ISDK Controller '{leftController.name}'" : "ISDK left Controller not found, using OVRInput tracking")}. " +
            $"Hand gesture {(leftHandMenuGesture ? "on" : "off")}.");
    }

    private void Update()
    {
        bool pressed = ReadButton(out string source);
        Process(pressed, source);
    }

    /// <summary>One frame of button state. Toggles on the press edge only.</summary>
    public void Process(bool pressed, string source = "test")
    {
        bool down = pressed && !wasPressed;
        wasPressed = pressed;
        if (!down)
            return;

        bool controller = ControllerInUse();
        if (verboseLogging)
            Debug.Log($"[CADMainMenuInput] Menu button down via {source}; left controller in use: {controller}.");

        if (!controller && !leftHandMenuGesture)
        {
            if (verboseLogging)
                Debug.Log("[CADMainMenuInput] Ignored: hand menu gesture / pinch (leftHandMenuGesture is off).");
            return;
        }

        if (verboseLogging)
            Debug.Log($"[CADMainMenuInput] ToggleMainMenu() (was {(mainMenu.IsOpen ? "open" : "hidden")}).");
        mainMenu.ToggleMainMenu();
    }

    // ---------------- Hardware ----------------

    private bool ReadButton(out string source)
    {
        if (ButtonProvider != null)
        {
            source = "provider";
            return ButtonProvider();
        }

        if (OVRInput.Get(OVRInput.RawButton.Start, OVRInput.Controller.LTouch))
        {
            source = "OVRInput RawButton.Start (LTouch)";
            return true;
        }

        InputDevice device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (device.isValid && device.TryGetFeatureValue(CommonUsages.menuButton, out bool menu) && menu)
        {
            source = $"XR CommonUsages.menuButton ('{device.name}')";
            return true;
        }

        source = null;
        return false;
    }

    private bool ControllerInUse()
    {
        if (ControllerInUseProvider != null)
            return ControllerInUseProvider();

        if (leftController == null)
            leftController = FindLeftController();
        if (leftController != null)
        {
            try
            {
                return leftController.IsConnected;
            }
            catch (Exception e)
            {
                if (verboseLogging)
                    Debug.Log($"[CADMainMenuInput] ISDK Controller unreadable ({e.GetType().Name}); using OVRInput tracking.");
                leftController = null;
            }
        }

        if (!loggedMissingController && verboseLogging)
        {
            loggedMissingController = true;
            Debug.Log("[CADMainMenuInput] No ISDK left Controller; using OVRInput LTouch tracking instead.");
        }
        return OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch);
    }

    private static IsdkController FindLeftController()
    {
        IEnumerable<IsdkController> controllers =
            FindObjectsByType<IsdkController>(FindObjectsInactive.Exclude);
        return controllers.FirstOrDefault(c => c.isActiveAndEnabled && IsLeft(c));
    }

    // Handedness reads the controller's data source, which may not be initialized yet.
    private static bool IsLeft(IsdkController controller)
    {
        try
        {
            return controller.Handedness == IsdkHandedness.Left;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
