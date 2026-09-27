using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A small floating label next to a two-hand gesture's pivot showing what it is doing right
/// now ("×1.25", "35°", or both), so it's clear whether a gesture scales, rotates or both.
/// Text only: no raycaster, not a raycast target, on the Ignore Raycast layer, so it never
/// blocks pointing. Built lazily, in the menus' style.
/// </summary>
public sealed class CADGestureReadout
{
    private const float CanvasScale = CADMenuPanel.CanvasScale;
    private const float TowardHead = 0.08f; // In front of the pivot (m).
    private const float Above = 0.07f;      // Above the pivot (m).

    private GameObject root;
    private Text text;

    public bool IsShowing => root != null && root.activeSelf;
    public string Text => text != null ? text.text : null;

    /// <summary>Shows the gesture's current ratio and angle near pivot; hides when neither applies.</summary>
    public void Show(Vector3 pivot, float ratio, float angle)
    {
        string label = Format(ratio, angle);
        if (label == null)
        {
            Hide();
            return;
        }

        Build();
        if (text.text != label)
            text.text = label;

        Transform head = Camera.main != null ? Camera.main.transform : null;
        Vector3 toHead = head != null ? (head.position - pivot).normalized : Vector3.back;
        root.transform.position = pivot + toHead * TowardHead + Vector3.up * Above;
        if (head != null)
            root.transform.rotation = Quaternion.LookRotation(root.transform.position - head.position, Vector3.up);
        root.SetActive(true);
    }

    /// <summary>"×1.25", "35°", "×1.25   35°", or null when neither is active.</summary>
    public static string Format(float ratio, float angle)
    {
        bool scaling = Mathf.Abs(ratio - 1f) > 0.005f;
        bool rotating = angle >= 0.5f;
        if (!scaling && !rotating)
            return null;
        string scale = scaling ? $"×{ratio:0.00}" : null;
        string turn = rotating ? $"{Mathf.RoundToInt(angle)}°" : null;
        return scaling && rotating ? $"{scale}   {turn}" : scale ?? turn;
    }

    public void Hide()
    {
        if (root != null)
            root.SetActive(false);
    }

    public void Destroy()
    {
        if (root == null)
            return;
        if (Application.isPlaying)
            Object.Destroy(root);
        else
            Object.DestroyImmediate(root);
        root = null;
    }

    private void Build()
    {
        if (root != null)
            return;

        CADMenuStyle style = CADMenuStyle.Default;
        root = new GameObject("CAD Gesture Readout") { layer = 2 }; // Ignore Raycast.
        root.AddComponent<CADUIPointerTarget>();

        var canvasObject = new GameObject("Canvas", typeof(RectTransform)) { layer = 2 };
        canvasObject.transform.SetParent(root.transform, false);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.localScale = Vector3.one * CanvasScale;
        canvasRect.sizeDelta = new Vector2(200f, 44f);

        var background = new GameObject("Background", typeof(RectTransform)) { layer = 2 };
        background.transform.SetParent(canvasRect, false);
        var image = background.AddComponent<Image>();
        image.color = style.PanelColor;
        image.raycastTarget = false;
        CADMenuPanel.MakeRounded(image, 12f);
        var backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = backgroundRect.offsetMax = Vector2.zero;

        var label = new GameObject("Label", typeof(RectTransform)) { layer = 2 };
        label.transform.SetParent(canvasRect, false);
        text = label.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = CADMenuPanel.FontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = style.SelectedButtonColor;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        var labelRect = (RectTransform)label.transform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
    }
}
