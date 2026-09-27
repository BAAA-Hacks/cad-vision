using System;
using UnityEngine;

public enum CADDisplayMode { Shaded, Edges, Wireframe }

/// <summary>
/// Global UI/display settings, set from the main menu (CADMainMenu). State and hooks only:
/// the menu calls these setters; whatever implements a feature listens. No menu placement or
/// visibility state lives here.
///
/// - DisplayMode / DisplayModeChanged: Shaded (surfaces only), Edges (shaded + CAD edge
///   overlay; the default) or Wireframe, rendered by CADDisplayModeController.
/// - OutlineEnabled: drives CADSelectionOutline.ShowOutlines (selection is never touched).
/// - UiScale / UiScaleChanged: main menu scale (never the CAD model, the rig or context menus).
/// - CadenEnabled / CadenEnabledChanged: PLACEHOLDER. CADEN integration attaches here: its
///   Unity host subscribes to CadenEnabledChanged on the ManipulationManager's CADUISettings
///   (or reads CadenEnabled) to start/stop listening. Nothing here references CADEN, and the
///   toggle works with no listener.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public class CADUISettings : MonoBehaviour
{
    public const float MinUiScale = 0.7f;
    public const float MaxUiScale = 1.5f;
    public const float UiScaleStep = 0.1f;

    // Default: shaded surfaces with the CAD edge overlay. A session starts here; the user's
    // choice then persists across model replacement.
    public CADDisplayMode DisplayMode { get; private set; } = CADDisplayMode.Edges;
    public event Action<CADDisplayMode> DisplayModeChanged;

    public bool OutlineEnabled { get; private set; } = true;
    public event Action<bool> OutlineEnabledChanged;

    public float UiScale { get; private set; } = 1f;
    public event Action<float> UiScaleChanged;

    public bool CadenEnabled { get; private set; }
    public event Action<bool> CadenEnabledChanged;

    public void SetDisplayMode(CADDisplayMode mode)
    {
        if (DisplayMode == mode)
            return;

        DisplayMode = mode;
        Debug.Log($"[CADUISettings] Display mode: {mode}.");
        DisplayModeChanged?.Invoke(mode);
    }

    public void SetOutlineEnabled(bool enabled)
    {
        OutlineEnabled = enabled;
        if (TryGetComponent(out CADSelectionOutline outline))
            outline.ShowOutlines = enabled;
        Debug.Log($"[CADUISettings] Selection outline {(enabled ? "on" : "off")}.");
        OutlineEnabledChanged?.Invoke(enabled);
    }

    public void SetUiScale(float scale)
    {
        if (!float.IsFinite(scale))
            return;

        // Snap to the step so repeated +/- never drifts (0.7, 0.8 ... 1.5).
        float snapped = Mathf.Clamp(Mathf.Round(scale / UiScaleStep) * UiScaleStep, MinUiScale, MaxUiScale);
        if (Mathf.Approximately(snapped, UiScale))
            return;

        UiScale = snapped;
        UiScaleChanged?.Invoke(snapped);
    }

    public void StepUiScale(int steps) => SetUiScale(UiScale + steps * UiScaleStep);

    public void SetCadenEnabled(bool enabled)
    {
        if (CadenEnabled == enabled)
            return;

        CadenEnabled = enabled;
        Debug.Log($"[CADUISettings] CADEN toggle {(enabled ? "on" : "off")} (placeholder; " +
            $"{(CadenEnabledChanged == null ? "no listener" : "notifying listeners")}).");
        CadenEnabledChanged?.Invoke(enabled);
    }

    public void ToggleCaden() => SetCadenEnabled(!CadenEnabled);
}
