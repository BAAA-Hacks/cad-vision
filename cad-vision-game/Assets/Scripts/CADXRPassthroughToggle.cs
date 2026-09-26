using UnityEngine;

/// <summary>
/// TEMP: toggles the scene's existing Meta passthrough layer at runtime.
/// Left controller X is test input only; UI/menus should call TogglePassthrough or
/// SetPassthroughEnabled. Only the layer's hidden flag changes, so the passthrough
/// system stays initialized and its rendering settings are untouched.
/// </summary>
[DisallowMultipleComponent]
public class CADXRPassthroughToggle : MonoBehaviour
{
    [Tooltip("Existing passthrough layer. Defaults to the one in the scene.")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;

    [Tooltip("Listen for the left controller X button. Disable when driven only by UI.")]
    [SerializeField] private bool useControllerInput = true;

    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.LTouch;

    public bool IsPassthroughEnabled { get; private set; }

    private void Start()
    {
        if (passthroughLayer == null)
            passthroughLayer = FindAnyObjectByType<OVRPassthroughLayer>();

        if (passthroughLayer == null)
        {
            Debug.LogWarning("[CADXRPassthroughToggle] No OVRPassthroughLayer in the scene; toggle disabled.");
            return;
        }

        // Read the real state instead of assuming it.
        IsPassthroughEnabled = passthroughLayer.isActiveAndEnabled && !passthroughLayer.hidden;
        Debug.Log($"[CADXRPassthroughToggle] Started; passthrough {(IsPassthroughEnabled ? "on" : "off")} " +
            $"(layer '{passthroughLayer.name}').");
    }

    private void Update()
    {
        // With an LTouch-only mask, OVRInput maps X to Button.One (Button.Three is X only
        // for the combined Touch mask).
        if (useControllerInput && OVRInput.GetDown(OVRInput.Button.One, controller))
            TogglePassthrough();
    }

    public void TogglePassthrough() => SetPassthroughEnabled(!IsPassthroughEnabled);

    public void SetPassthroughEnabled(bool enabled)
    {
        if (passthroughLayer == null)
        {
            Debug.LogWarning("[CADXRPassthroughToggle] No passthrough layer to toggle.");
            return;
        }

        // Also enable the component in case it started disabled; hidden is the actual switch.
        if (enabled && !passthroughLayer.enabled)
            passthroughLayer.enabled = true;

        passthroughLayer.hidden = !enabled;
        IsPassthroughEnabled = enabled;
        Debug.Log($"[CADXRPassthroughToggle] Passthrough {(enabled ? "on" : "off")}.");
    }
}
