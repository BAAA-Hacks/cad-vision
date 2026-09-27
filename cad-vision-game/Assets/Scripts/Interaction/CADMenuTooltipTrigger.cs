using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Tooltip text for one menu button; forwards ray hover (uGUI enter/exit) to the panel's CADMenuTooltip.</summary>
[DisallowMultipleComponent]
public sealed class CADMenuTooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string Text;
    public CADMenuTooltip Tooltip;

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (Tooltip != null)
            Tooltip.Enter(this, Time.unscaledTime);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (Tooltip != null)
            Tooltip.Exit(this);
    }

    private void OnDisable()
    {
        if (Tooltip != null)
            Tooltip.Exit(this);
    }
}
