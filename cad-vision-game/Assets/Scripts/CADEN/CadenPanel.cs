using System;
using System.Collections;
using System.Linq;
using CADVision;
using CADEN.Core;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>World-space CADEN conversation surface, using the same Meta ray bridge as CADContextMenu.</summary>
[RequireComponent(typeof(CadenSessionHost))]
public sealed class CadenPanel : MonoBehaviour
{
    private const float W = 720, H = 820, Scale = 0.001f;
    private static readonly Color Navy = new Color32(12, 21, 36, 255);
    private static readonly Color Card = new Color32(23, 38, 57, 255);
    private static readonly Color Muted = new Color32(159, 182, 205, 255);
    private static readonly Color Cyan = new Color32(42, 220, 219, 255);
    private CadenSessionHost host;
    private RectTransform canvasRect, messages;
    private GameObject expanded;
    private Image microphoneFace;
    private GameObject microphoneSlash;
    private bool microphonePreview;
    private float microphonePreviewEndsAt;
    private Sprite circle;
    private Texture2D circleTexture;
    private Text status, context, feedback;
    private InputField input;
    private Button send, cancel;
    private ScrollRect scroll;
    private BoxCollider surfaceBox;
    private Sprite rounded;
    private Texture2D roundedTexture;
    private bool busy, positioned, trackingReady;
    private int generation;
    private float nextContextUpdate;
    private Transform viewer;

    private bool draggingPanel;
    private int dragPointer;

    // Prevent two background hit areas/controllers from grabbing the panel simultaneously.
    public bool BeginPanelDrag(int pointer)
    {
        if (draggingPanel) return false;
        draggingPanel = true; dragPointer = pointer;

        return true;
    }
    public void EndPanelDrag(int pointer)
    {
        if (draggingPanel && pointer == dragPointer) draggingPanel = false;
    }
    private void LateUpdate()
    {
        if (!positioned) return;
        if (microphonePreview && Time.unscaledTime >= microphonePreviewEndsAt)
        {
            microphonePreview = false;
            if (feedback.text == "Microphone preview · no audio recorded")
                feedback.text = "Hold empty space to move the panel.";
        }
        if (microphoneFace != null)
            microphoneFace.color = microphonePreview
                ? Color.Lerp(Card, Cyan, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f)) : Card;
        if (microphoneSlash != null) microphoneSlash.SetActive(!microphonePreview);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsurePanel();
    }
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsurePanel();
    public static CadenPanel EnsurePanel()
    {
        var existing = FindAnyObjectByType<CadenPanel>(FindObjectsInactive.Include);
        if (existing != null) return existing;
        // Create independently of model loading: bootstrap callback order is not guaranteed.
        return new GameObject("CADEN · Design assistant").AddComponent<CadenPanel>();
    }

    private void Awake()
    {
        host = GetComponent<CadenSessionHost>();
        gameObject.AddComponent<CADUIPointerTarget>();
        Build();
        host.Changed += ResetConversation;
        ResetConversation();
        canvasRect.gameObject.SetActive(false);
        surfaceBox.gameObject.SetActive(false);
    }
    private IEnumerator Start()
    {
        // Let the camera rig initialize before using its eye pose.
        yield return null;
        yield return null;
        trackingReady = true;
    }

    private void Update()
    {
        if (!positioned && trackingReady) Recenter();
        if (Time.unscaledTime < nextContextUpdate) return;
        nextContextUpdate = Time.unscaledTime + 0.5f;
        context.text = "Assembly  /  " + host.AssemblyName;
        send.interactable = !busy && host.Session != null && !string.IsNullOrWhiteSpace(input.text);
    }

    public void Recenter()
    {
        if (!trackingReady) return;
        if (UnityEngine.XR.XRSettings.isDeviceActive)
        {
            var eye = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye);
            if (!eye.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) || !tracked) return;
        }
        var rig = FindAnyObjectByType<OVRCameraRig>();
        var head = rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor
            : Camera.main != null ? Camera.main.transform : null;
        if (head == null) return;
        viewer = head;
        draggingPanel = false;

        var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        transform.position = head.position + forward * 0.95f - Vector3.Cross(Vector3.up, forward) * 0.85f;
        transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
        canvasRect.GetComponent<Canvas>().worldCamera = head.GetComponent<Camera>() ?? Camera.main;
        canvasRect.gameObject.SetActive(true);
        surfaceBox.gameObject.SetActive(true);
        positioned = true;
    }

    private void Build()
    {
        MakeRoundedSprite();
        var events = FindAnyObjectByType<EventSystem>();
        if (events == null) events = new GameObject("CAD Vision EventSystem").AddComponent<EventSystem>();
        if (FindAnyObjectByType<PointableCanvasModule>() == null) events.gameObject.AddComponent<PointableCanvasModule>();
        canvasRect = Rect("Canvas", transform, 0, 0, W, H);
        canvasRect.anchorMin = canvasRect.anchorMax = canvasRect.pivot = new Vector2(0.5f, 0.5f);
        canvasRect.anchoredPosition = Vector2.zero;
        canvasRect.localScale = Vector3.one * Scale;
        var canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvasRect.gameObject.AddComponent<GraphicRaycaster>();
        expanded = Rect("Expanded", canvasRect, 0, 0, W, H).gameObject;
        var p = (RectTransform)expanded.transform;
        Draggable(Image("Outline", p, -2, -2, W + 4, H + 4, new Color32(40, 104, 129, 255)));
        Draggable(Image("Surface", p, 0, 0, W, H, Navy));
        Draggable(Image("Brand accent", p, 28, 22, 5, 58, Cyan));
        var logo = Rect("CADVision logo", p, 46, 24, 58, 58).gameObject.AddComponent<RawImage>();
        logo.texture = Resources.Load<Texture2D>("CADEN/CADVisionLogo");
        logo.raycastTarget = false;
        Label("Brand", p, 120, 25, 270, 30, "CADVision", 27, Color.white, true);
        Label("Subtitle", p, 120, 58, 300, 24, "CADEN · Assembly chat", 18, Muted);
        ActionButton("New chat", p, 568, 29, 124, 46, () => host.NewChat());

        Draggable(Image("Context card", p, 28, 116, 664, 46, Card));
        context = Label("Context", p, 44, 125, 632, 28, "Assembly  /  No assembly loaded", 18, Cyan);
        var viewport = Rect("Conversation", p, 28, 182, 664, 420);
        viewport.gameObject.AddComponent<RectMask2D>();
        var hit = viewport.gameObject.AddComponent<Image>(); hit.color = new Color(0, 0, 0, 0.001f);
        scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = viewport; scroll.scrollSensitivity = 35;
        messages = Rect("Messages", viewport, 0, 0, 650, 0);
        var layout = messages.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 14; layout.padding = new RectOffset(0, 0, 4, 10);
        layout.childControlHeight = true; layout.childControlWidth = true;
        layout.childForceExpandHeight = false; layout.childForceExpandWidth = true;
        var fitter = messages.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = messages;
        // Empty viewport area moves the panel; message cards bubble drag events to ScrollRect.
        var empty = Image("Empty conversation space", viewport, 0, 0, 664, 420, new Color(0, 0, 0, 0.001f));
        empty.transform.SetAsFirstSibling();
        Draggable(empty);
        // The editable composer itself is the speech bubble, not a separate suggested prompt.
        var tail = Image("Composer bubble tail", p, 126, 666, 22, 22, Card);
        tail.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        tail.rectTransform.anchoredPosition = new Vector2(126, -666);
        tail.sprite = null; tail.type = UnityEngine.UI.Image.Type.Simple;
        tail.rectTransform.localRotation = Quaternion.Euler(0, 0, 45); tail.raycastTarget = false;
        var field = Image("Speech bubble composer", p, 126, 621, 566, 90, Card);
        field.pixelsPerUnitMultiplier = 0.5f;
        input = field.gameObject.AddComponent<InputField>();
        input.targetGraphic = field; input.lineType = InputField.LineType.MultiLineNewline; input.characterLimit = 4000;
        var value = Label("Input", field.rectTransform, 22, 12, 520, 65, "", 22, Color.white);
        value.supportRichText = false;
        var placeholder = Label("Placeholder", field.rectTransform, 22, 12, 520, 65, "Ask about your design…", 22, Muted);
        input.textComponent = value; input.placeholder = placeholder;
        input.onValueChanged.AddListener(_ => send.interactable = !busy && host.Session != null && !string.IsNullOrWhiteSpace(input.text));
        status = Label("Status", p, 30, 730, 435, 25, "Connect CADEN to begin", 17, Muted);
        feedback = Label("Feedback", p, 30, 762, 455, 37, "Hold empty space to move the panel.", 14, Muted);
        send = ActionButton("Send  >", p, 530, 732, 162, 58, Send, 22, true);
        cancel = ActionButton("Stop", p, 530, 732, 162, 58, () => host.Cancel(), 22);
        cancel.gameObject.SetActive(false);
        BuildMicrophone(p);

        var surfaceObject = new GameObject("CADEN ray surface"); surfaceObject.layer = 2;
        surfaceObject.transform.SetParent(transform, false);
        surfaceBox = surfaceObject.AddComponent<BoxCollider>();
        surfaceBox.size = new Vector3(W * Scale, H * Scale, 0.004f);
        var pointable = canvasRect.gameObject.AddComponent<PointableCanvas>(); pointable.InjectAllPointableCanvas(canvas);
        var surface = surfaceObject.AddComponent<ColliderSurface>(); surface.InjectAllColliderSurface(surfaceBox);
        var ray = surfaceObject.AddComponent<RayInteractable>(); ray.InjectAllRayInteractable(surface);
        ray.InjectOptionalSelectSurface(surface); ray.InjectOptionalPointableElement(pointable);
    }

    public void ShowPanel() => Recenter();

    private void ResetConversation()
    {
        generation++;
        foreach (Transform child in messages) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        AddMessage("CADEN", "Hey, I'm CADEN.\nWhat would you like to know about this assembly?", false);
        var history = host.Session?.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        if (history != null)
            foreach (var message in history.Skip(Math.Max(0, history.Length - 38)))
                AddMessage(message.Role == "user" ? "YOU" : "CADEN", message.Text, message.Role == "user");
        microphonePreview = false;
        status.text = host.Status; feedback.text = "Hold empty space to move the panel.";
        input.text = ""; SetBusy(false);
    }
    private void SetBusy(bool value)
    {
        busy = value; input.interactable = !value;
        send.gameObject.SetActive(!value); cancel.gameObject.SetActive(value);
        send.interactable = !value && host.Session != null && !string.IsNullOrWhiteSpace(input.text);
    }
    private async void Send()
    {
        if (busy || host.Session == null || string.IsNullOrWhiteSpace(input.text)) return;
        var prompt = input.text.Trim(); int turn = generation;
        SetBusy(true); status.text = "CADEN is thinking…"; feedback.text = "Reviewing your question and available evidence.";
        try
        {
            var answer = await host.SendAsync(prompt);
            if (this == null || turn != generation) return;
            AddMessage("YOU", prompt, true); AddMessage("CADEN", answer, false);
            input.text = ""; feedback.text = "Hold empty space to move the panel.";
        }
        catch (OperationCanceledException) { if (this != null && turn == generation) feedback.text = "Stopped. Your draft is ready to retry."; }
        catch (Exception e)
        {
            if (this != null && turn == generation) feedback.text = e is ChatException ? e.Message : "Could not get a reply. Check the connection and retry.";
        }
        finally { if (this != null && turn == generation) { status.text = host.Status; SetBusy(false); } }
    }
    private void AddMessage(string speaker, string text, bool user)
    {
        var card = Image(speaker, messages, 0, 0, 650, 100, user ? new Color32(22, 55, 70, 255) : Card);
        var group = card.gameObject.AddComponent<VerticalLayoutGroup>();
        group.padding = new RectOffset(20, 20, 16, 18); group.spacing = 8;
        group.childControlHeight = group.childControlWidth = true; group.childForceExpandHeight = false;
        var title = Label("Speaker", card.rectTransform, 0, 0, 590, 23, speaker, 15, Cyan, true);
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 23;
        var body = Label("Body", card.rectTransform, 0, 0, 590, 60, text, 22, Color.white);
        body.supportRichText = false; body.verticalOverflow = VerticalWrapMode.Overflow;
        StartCoroutine(ScrollToBottom());
        int visible = 0;
        foreach (Transform child in messages) if (child.gameObject.activeSelf) visible++;
        foreach (Transform child in messages)
        {
            if (visible <= 40) break;
            if (!child.gameObject.activeSelf) continue;
            child.gameObject.SetActive(false); Destroy(child.gameObject); visible--;
        }
    }
    private IEnumerator ScrollToBottom() { yield return null; Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 0; }

    private RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height); return rect;
    }
    private Image Image(string name, Transform parent, float x, float y, float width, float height, Color color)
    {
        var image = Rect(name, parent, x, y, width, height).gameObject.AddComponent<Image>();
        image.color = color; image.sprite = rounded; image.type = UnityEngine.UI.Image.Type.Sliced; return image;
    }
    private Text Label(string name, Transform parent, float x, float y, float width, float height, string value, int size, Color color, bool bold = false)
    {
        var text = Rect(name, parent, x, y, width, height).gameObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text = value; text.fontSize = size;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal; text.color = color; text.supportRichText = false;
        text.alignment = TextAnchor.MiddleLeft; text.raycastTarget = false; return text;
    }
    private Button ActionButton(string title, Transform parent, float x, float y, float width, float height, UnityEngine.Events.UnityAction action, int fontSize = 17, bool primary = false)
    {
        var image = Image(title, parent, x, y, width, height, primary ? Cyan : Card);
        var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var colors = button.colors; colors.highlightedColor = new Color(0.7f, 0.95f, 1); colors.pressedColor = new Color(0.4f, 0.75f, 0.85f);
        colors.disabledColor = new Color(0.4f, 0.4f, 0.4f); button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None }; button.onClick.AddListener(action);
        var label = Label("Label", image.transform, 12, 0, width - 24, height, title, fontSize, primary ? Navy : Color.white, primary);
        label.alignment = TextAnchor.MiddleCenter; return button;
    }
    private void Draggable(Image background)
    {
        background.gameObject.AddComponent<CadenPanelDrag>().Panel = transform;
    }

    private void BuildMicrophone(Transform parent)
    {
        const int size = 64;
        circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(31.5f - Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f))));
        circleTexture.SetPixels(pixels); circleTexture.Apply();
        circle = Sprite.Create(circleTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        microphoneFace = Image("Microphone preview (visual only)", parent, 28, 634, 64, 64, Card);
        microphoneFace.sprite = circle; microphoneFace.type = UnityEngine.UI.Image.Type.Simple;
        var button = microphoneFace.gameObject.AddComponent<Button>();
        button.targetGraphic = microphoneFace;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() =>
        {
            microphonePreview = !microphonePreview;
            if (microphonePreview) microphonePreviewEndsAt = Time.unscaledTime + 5f;
            feedback.text = microphonePreview ? "Microphone preview · no audio recorded" : "Hold empty space to move the panel.";
        });
        // Vector-like UI shapes avoid platform-dependent emoji glyphs. No audio API is used.
        Image("Mic capsule", microphoneFace.transform, 25, 13, 14, 25, Color.white).raycastTarget = false;
        Image("Mic left", microphoneFace.transform, 19, 27, 3, 14, Color.white).raycastTarget = false;
        Image("Mic right", microphoneFace.transform, 42, 27, 3, 14, Color.white).raycastTarget = false;
        Image("Mic cradle", microphoneFace.transform, 20, 39, 24, 4, Color.white).raycastTarget = false;
        Image("Mic stem", microphoneFace.transform, 30, 42, 4, 8, Color.white).raycastTarget = false;
        Image("Mic foot", microphoneFace.transform, 24, 49, 16, 3, Color.white).raycastTarget = false;
        var slash = Image("Microphone off slash", microphoneFace.transform, 32, 32, 43, 4, Cyan);
        slash.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        slash.rectTransform.anchoredPosition = new Vector2(32, -32);
        slash.rectTransform.localRotation = Quaternion.Euler(0, 0, -45);
        slash.raycastTarget = false;
        microphoneSlash = slash.gameObject;
    }

    private void MakeRoundedSprite()
    {
        const int side = 32; const float radius = 10;
        roundedTexture = new Texture2D(side, side, TextureFormat.RGBA32, false);
        var pixels = new Color[side * side];
        for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
        {
            float dx = Mathf.Max(Mathf.Abs(x - 15.5f) - (16 - radius), 0);
            float dy = Mathf.Max(Mathf.Abs(y - 15.5f) - (16 - radius), 0);
            pixels[y * side + x] = new Color(1, 1, 1, Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy)));
        }
        roundedTexture.SetPixels(pixels); roundedTexture.Apply();
        rounded = Sprite.Create(roundedTexture, new Rect(0, 0, side, side), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(12, 12, 12, 12));
    }
    private void OnDestroy()
    {
        generation++; if (host != null) { host.Changed -= ResetConversation; host.Cancel(); }
        if (rounded != null) Destroy(rounded); if (roundedTexture != null) Destroy(roundedTexture);
        if (circle != null) Destroy(circle); if (circleTexture != null) Destroy(circleTexture);
    }
}
