using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Hover tooltip for one CADMenuPanel (on the panel root). Buttons made with a tooltip carry a
/// CADMenuTooltipTrigger; uGUI pointer enter/exit (sent by PointableCanvasModule for any ray:
/// controller or hand) report hover here. After hoverDelay the text is shown in a small
/// dark box beside the panel, level with the hovered button, never over its label. Built from
/// the panel's own Image/Text primitives, not raycast targets, and outside the panel's ray
/// surface, so it can never steal the hover. Hides when hover ends and when the panel hides.
/// </summary>
[DisallowMultipleComponent]
public sealed class CADMenuTooltip : MonoBehaviour
{
    public const float HoverDelay = 0.5f;
    private const float Width = 250f;
    private const float Gap = 10f;
    private const float Padding = 8f;
    private const int FontSize = 16;

    private RectTransform canvas;
    private Image box;
    private Text text;
    private CADMenuTooltipTrigger hovered;
    private float hoverStart;

    public bool IsShowing => box != null && box.gameObject.activeSelf;
    public string ShownText => IsShowing ? text.text : null;
    public CADMenuTooltipTrigger Hovered => hovered;

    public void Initialize(RectTransform canvasRect, Font font)
    {
        canvas = canvasRect;

        var boxObject = new GameObject("Tooltip", typeof(RectTransform));
        boxObject.transform.SetParent(canvas, false);
        box = boxObject.AddComponent<Image>();
        box.color = new Color(0.03f, 0.04f, 0.06f, 0.95f);
        box.raycastTarget = false;

        var textObject = new GameObject("Tooltip Text", typeof(RectTransform));
        textObject.transform.SetParent(boxObject.transform, false);
        text = textObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = FontSize;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(Padding, Padding);
        textRect.offsetMax = new Vector2(-Padding, -Padding);

        boxObject.SetActive(false);
    }

    public void Enter(CADMenuTooltipTrigger trigger, float now)
    {
        if (trigger == hovered)
            return;
        hovered = trigger;
        hoverStart = now;
        HideBox(); // A new target restarts the delay.
    }

    public void Exit(CADMenuTooltipTrigger trigger)
    {
        if (trigger != hovered)
            return; // Another button already took over.
        hovered = null;
        HideBox();
    }

    /// <summary>Shows the hovered button's tooltip once it has been hovered for HoverDelay.</summary>
    public void Tick(float now)
    {
        if (hovered == null || box == null)
            return;
        if (!hovered.isActiveAndEnabled)
        {
            hovered = null;
            HideBox();
            return;
        }
        if (!IsShowing && now - hoverStart >= HoverDelay)
            Show(hovered);
    }

    public void Hide()
    {
        hovered = null;
        HideBox();
    }

    private void Update() => Tick(Time.unscaledTime);

    private void OnDisable() => Hide();

    private void HideBox()
    {
        if (box != null && box.gameObject.activeSelf)
            box.gameObject.SetActive(false);
    }

    // Right of the panel, top-aligned with the button (canvas units, top-left based).
    private void Show(CADMenuTooltipTrigger trigger)
    {
        text.text = trigger.Text;
        float height = Mathf.Max(28f, text.preferredHeight + 2 * Padding);
        if (text.preferredHeight <= 0f)
            height = 2 * Padding + FontSize * 1.3f * Mathf.Ceil(trigger.Text.Length / 28f);

        var button = (RectTransform)trigger.transform;
        Vector3 buttonTopLeft = canvas.InverseTransformPoint(button.TransformPoint(new Vector3(button.rect.xMin, button.rect.yMax, 0f)));
        Rect area = canvas.rect;

        var rect = (RectTransform)box.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f); // Canvas-local coordinates.
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(area.xMax + Gap, buttonTopLeft.y);
        rect.sizeDelta = new Vector2(Width, height);
        rect.SetAsLastSibling(); // Drawn over the panel.
        box.gameObject.SetActive(true);
    }
}
