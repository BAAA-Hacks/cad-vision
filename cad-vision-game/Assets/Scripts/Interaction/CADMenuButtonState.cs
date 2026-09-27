using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visual state of one CADMenuPanel button (style guide): secondary buttons are a navy surface
/// with a white label; primary and selected ("on") buttons are cyan with a bold navy label;
/// disabled buttons keep their shape but dim their label. Hover, pressed and disabled fills come
/// from the button's shared ColorBlock tint over the base color set here.
/// </summary>
[DisallowMultipleComponent]
public sealed class CADMenuButtonState : MonoBehaviour
{
    public bool Primary;
    public bool Selected;
    public Color SurfaceBase;     // Base fill for secondary buttons (before the ColorBlock tint).
    public Color AccentBase;      // Base fill for primary / selected buttons.
    public Color LabelColor;      // White.
    public Color AccentLabelColor; // Navy, on cyan.
    public Color DisabledLabelColor;

    public bool IsAccent => Primary || Selected;

    /// <summary>Applies fill, label color and weight for the current state.</summary>
    public void Apply(Button button)
    {
        Color fill = IsAccent ? AccentBase : SurfaceBase;
        if (button.targetGraphic.color != fill)
            button.targetGraphic.color = fill;

        Text label = button.GetComponentInChildren<Text>(true);
        if (label == null)
            return;
        Color color = !button.interactable ? DisabledLabelColor : IsAccent ? AccentLabelColor : LabelColor;
        if (label.color != color)
            label.color = color;
        FontStyle weight = IsAccent ? FontStyle.Bold : FontStyle.Normal;
        if (label.fontStyle != weight)
            label.fontStyle = weight;
    }
}
