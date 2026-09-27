using System;
using System.Collections;
using System.Linq;
using CADEN.Unity;
using CADVision;
using Core;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// World-space CADEN conversation surface: one of the CADVision windows, with CADEN's purpose.
/// It shares their frame and movement (CADWindowFrame: grab band, edge glow, border drag with
/// hands or controllers), scale (CADMenuPanel.CanvasScale) and minimum distance, but it is
/// exempt from the one-menu rule: it stays open next to the other menus. Collapsed, it is the
/// head-locked CADVision logo. The Main Menu's CADEN toggle (CADUISettings.CadenEnabled) shows or
/// hides every part of it.
/// </summary>
[RequireComponent(typeof(CadenSessionHost))]
public sealed class CadenPanel : MonoBehaviour
{
    private const float W = 720, H = 820, Scale = CADMenuPanel.CanvasScale;
    // Minimized tile framing the logo in the panel's top-left corner.
    private const float TileX = 34, TileY = 12, TileSize = 82, MinimizeSeconds = 0.28f;
    private static readonly Vector2 LogoCenter = new Vector2(TileX + TileSize / 2, TileY + TileSize / 2);
    private static readonly Vector3 LogoLocal = new Vector3((LogoCenter.x - W / 2) * Scale, (H / 2 - LogoCenter.y) * Scale, 0);
    // Head-space position of the minimized logo; it stays pinned to the top-left of view.
    [SerializeField] private Vector3 minimizedLogoOffset = new Vector3(-0.34f, 0.15f, 0.9f);
    // The opened panel: centered in front of the user, facing them, this far away (clamped to
    // 0.8–1.5 m and never closer than CADMenuPanel.MinMenuDistance), a little below eye level.
    // It grows out of the logo.
    [SerializeField, Range(0.8f, 1.5f)] private float expandedDistance = 1.1f;
    [SerializeField] private float expandedDrop = 0.08f;
    private Vector3 expandedPosition;
    private Quaternion expandedRotation = Quaternion.identity;
    // Logo feedback: glow while speaking, spin-and-settle cycles while thinking.
    private const float SpinSeconds = 0.75f, SpinPauseSeconds = 0.25f, SpeechGapSeconds = 0.6f;
    private RectTransform logoRect;
    private Image logoGlow;
    private Sprite glowSprite;
    private Texture2D glowTexture;
    private float glow, spinTime = -1, lastSpokeAt = float.NegativeInfinity;
    private static readonly Color Navy = new Color32(12, 21, 36, 255);
    private static readonly Color Card = new Color32(23, 38, 57, 255);
    private static readonly Color Muted = new Color32(159, 182, 205, 255);
    private static readonly Color Cyan = new Color32(42, 220, 219, 255);
    private CadenSessionHost host;
    private RectTransform canvasRect, messages;
    private RectTransform loadingSpinner;
    private GameObject expanded;
    private CanvasGroup expandedGroup, minimizedTile;
    private bool minimized;
    private float openness = 1;
    private Image microphoneFace;
    // Voice feedback that works without looking at the panel (hands have no button to feel):
    // a listening pulse on the logo, a hint beside the collapsed logo, and a click on start/stop.
    private CanvasGroup voiceHint;
    private Text voiceHintText;
    private AudioSource clickSource;
    private AudioClip startClick, stopClick;
    private bool wasRecording;
    private GameObject microphoneSlash;
    private bool microphonePreview;

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
    private string displayedVoiceError;
    private bool showingVoiceDraft;
    private string savedTypedDraft;
    private Transform viewer;

    // Shared window frame and movement; CADEN on/off from the Main Menu.
    private CADWindowFrame frame;
    // Window size from corner resizing (1 = normal), kept for the session. The head-locked logo
    // tile keeps its own size.
    private float size = 1f;
    private CADPointerInteraction pointer;
    private CADUISettings settings;

    private bool IsShowing => canvasRect != null && canvasRect.gameObject.activeSelf;
    private bool CadenOn
    {
        get
        {
            if (settings == null) settings = FindAnyObjectByType<CADUISettings>();
            return settings == null || settings.CadenEnabled;
        }
    }

    private void LateUpdate()
    {
        if (!positioned) return;
        if (IsShowing) frame.UpdateDrag();
        microphonePreview = host.Voice != null && host.Voice.Recording;
        var voice = host.Voice;
        if (voice != null && voice.Busy)
        {
            if (!showingVoiceDraft) { savedTypedDraft = input.text; showingVoiceDraft = true; }
            input.SetTextWithoutNotify(voice.Transcript);
        }
        else if (showingVoiceDraft)
        {
            input.SetTextWithoutNotify(savedTypedDraft ?? "");
            showingVoiceDraft = false;
        }
        bool voiceFailed = voice != null && !string.IsNullOrEmpty(voice.LastError);
        status.text = voice != null && (voice.Busy || voiceFailed) ? voice.Status : host.Status;
        status.color = voiceFailed ? new Color32(255, 105, 105, 255) : Muted;
        if (voiceFailed && displayedVoiceError != voice.LastError)
        {
            displayedVoiceError = voice.LastError;
            AddMessage("VOICE ERROR", voice.LastError, false);
            feedback.text = "Voice failed. See the error above; tap the mic, Y or pinch to retry.";
        }
        if (!voiceFailed) displayedVoiceError = null;
        bool processing = (host.IsBusy && !microphonePreview) || host.Status.StartsWith("Loading", StringComparison.Ordinal);
        loadingSpinner.gameObject.SetActive(processing);
        if (processing) loadingSpinner.Rotate(0, 0, -240f * Time.unscaledDeltaTime);
        if (microphoneFace != null)
            microphoneFace.color = microphonePreview
                ? Color.Lerp(Card, Cyan, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f)) : Card;
        if (microphoneSlash != null) microphoneSlash.SetActive(!microphonePreview);
        AnimateLogo(host.IsBusy && !microphonePreview);
        UpdateVoiceFeedback(voice);
    }

    // Hint beside the collapsed logo while CADEN listens or thinks; a click when listening
    // starts and stops.
    private void UpdateVoiceFeedback(CadenVoiceInput voice)
    {
        bool recording = voice != null && voice.Recording;
        if (recording != wasRecording && IsShowing && clickSource != null)
            clickSource.PlayOneShot(recording ? startClick : stopClick);
        wasRecording = recording;

        string hint = recording ? "Listening… release, or tap again, to send"
            : voice != null && voice.Busy ? "Transcribing…"
            : host.IsBusy ? "CADEN is thinking…"
            : null;
        bool show = hint != null && openness < 0.5f;
        if (show && voiceHintText.text != hint) voiceHintText.text = hint;
        voiceHint.alpha = Mathf.MoveTowards(voiceHint.alpha, show ? 1 : 0, Time.unscaledDeltaTime / 0.15f);
    }

    private static AudioClip Click(string name, float frequency)
    {
        const int rate = 24000;
        int samples = rate * 45 / 1000;
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / rate;
            data[i] = 0.25f * Mathf.Sin(2 * Mathf.PI * frequency * t) * Mathf.Exp(-t * 90f);
        }
        var clip = AudioClip.Create(name, samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private void AnimateLogo(bool working)
    {
        bool speaking = host.IsSpeaking;
        if (speaking) lastSpokeAt = Time.unscaledTime;
        // Glow follows speech loudness; it eases out between sentences instead of flickering.
        // Listening: a slow pulse, so it's clear CADEN hears you (hands have no button to feel).
        float target = speaking ? 0.55f + 0.45f * Mathf.Clamp01(host.SpeechLevel * 6f)
            : microphonePreview ? 0.45f + 0.25f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f))
            : 0;
        glow = Mathf.Lerp(glow, target, 1 - Mathf.Exp(-12f * Time.unscaledDeltaTime));
        logoGlow.color = new Color(Cyan.r, Cyan.g, Cyan.b, glow * 0.8f);
        logoGlow.rectTransform.localScale = Vector3.one * (0.9f + 0.2f * glow);

        // Gaps between spoken sentences are still "talking", not thinking.
        bool thinking = working && Time.unscaledTime - lastSpokeAt > SpeechGapSeconds;
        if (spinTime < 0 && !thinking) return;
        if (spinTime < 0) spinTime = 0;
        spinTime += Time.unscaledDeltaTime;
        // A cycle always finishes, so the logo never stops at an odd angle.
        if (spinTime >= SpinSeconds + SpinPauseSeconds) spinTime = thinking ? 0 : -1;
        float angle = spinTime < 0 ? 0 : 360f * EaseOutBack(Mathf.Clamp01(spinTime / SpinSeconds));
        logoRect.localRotation = Quaternion.Euler(0, 0, -angle);
    }

    // Starts fast, overshoots slightly, then settles back onto the target.
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.2f, c3 = c1 + 1;
        float u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
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
        host.Feedback += ShowFeedback;
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
        bool visible = positioned && CadenOn;
        if (IsShowing != visible) SetVisible(visible);
        if (pointer == null) pointer = FindAnyObjectByType<CADPointerInteraction>();
        frame.EnableBorderDrag(pointer);
        AnimateMinimize();
        if (Time.unscaledTime < nextContextUpdate) return;
        nextContextUpdate = Time.unscaledTime + 0.5f;
        context.text = "Assembly  /  " + host.AssemblyName;
        SetBusy(host.IsBusy);
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
        frame.EndDrag();

        PlaceAtLogo();
        ApplyPose(Mathf.SmoothStep(0, 1, openness));
        canvasRect.GetComponent<Canvas>().worldCamera = head.GetComponent<Camera>() ?? Camera.main;
        positioned = true;
        SetVisible(CadenOn);
    }

    // Every CADEN element (panel, logo, ray surface) shows only while CADEN is on.
    private void SetVisible(bool visible)
    {
        canvasRect.gameObject.SetActive(visible);
        surfaceBox.gameObject.SetActive(visible);
        if (!visible) frame.Reset();
    }

    private void Build()
    {
        MakeRoundedSprite();
        CADMenuPanel.EnsureCanvasEventSystem();
        canvasRect = Rect("Canvas", transform, 0, 0, W, H);
        canvasRect.anchorMin = canvasRect.anchorMax = canvasRect.pivot = new Vector2(0.5f, 0.5f);
        canvasRect.anchoredPosition = Vector2.zero;
        canvasRect.localScale = Vector3.one * Scale;
        var canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvasRect.gameObject.AddComponent<GraphicRaycaster>();
        expanded = Rect("Expanded", canvasRect, 0, 0, W, H).gameObject;
        expandedGroup = expanded.AddComponent<CanvasGroup>();
        var p = (RectTransform)expanded.transform;
        // Scale about the logo so the panel collapses into it and grows back out of it.
        p.pivot = new Vector2(LogoCenter.x / W, 1 - LogoCenter.y / H);
        p.anchoredPosition = new Vector2(LogoCenter.x, -LogoCenter.y);
        // The CADVision window frame: background, border, grab band, edge glow and drag, exactly
        // as the other menus. Grabs only while the panel is open (not collapsed to the logo).
        frame = new CADWindowFrame(transform, p, CADMenuStyle.Default,
            () => IsShowing && !minimized && openness >= 1f);
        frame.AddGrabRegion(Rect("Header grab region", p, 0, 0, W, 104)); // Like the Main Menu's header.
        frame.EnableResize(() => size, SetSize);
        Image("Brand accent", p, 28, 22, 5, 58, Cyan);
        Label("Brand", p, 120, 25, 270, 30, "CADVision", 27, Color.white, true);
        Label("Subtitle", p, 120, 58, 300, 24, "CADEN · Assembly chat", 18, Muted);
        ActionButton("New chat", p, 568, 29, 124, 46, () => host.NewChat());

        Image("Context card", p, 28, 116, 664, 46, Card);
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
        // Pointer hits on empty conversation space still reach the ScrollRect.
        var empty = Image("Empty conversation space", viewport, 0, 0, 664, 420, new Color(0, 0, 0, 0.001f));
        empty.transform.SetAsFirstSibling();
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
        feedback = Label("Feedback", p, 30, 762, 455, 37, "Hold the edge to move the panel.", 14, Muted);
        send = ActionButton("Send  >", p, 530, 732, 162, 58, Send, 22, true);
        cancel = ActionButton("Stop", p, 530, 732, 162, 58, () => host.Cancel(), 22);
        cancel.gameObject.SetActive(false);
        BuildMicrophone(p);
        loadingSpinner = Rect("Loading spinner", p, 488, 748, 32, 32);
        loadingSpinner.pivot = new Vector2(0.5f, 0.5f);
        for (int i = 0; i < 12; i++)
        {
            float angle = i * Mathf.PI * 2 / 12;
            var dot = Image("Spinner dot", loadingSpinner, 0, 0, 5, 5,
                new Color(Cyan.r, Cyan.g, Cyan.b, 0.15f + 0.85f * i / 11));
            dot.sprite = circle; dot.type = UnityEngine.UI.Image.Type.Simple; dot.raycastTarget = false;
            dot.rectTransform.anchorMin = dot.rectTransform.anchorMax = dot.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            dot.rectTransform.anchoredPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 13;
        }
        loadingSpinner.gameObject.SetActive(false);
        BuildLogoToggle();

        var surfaceObject = new GameObject("CADEN ray surface"); surfaceObject.layer = 2;
        surfaceObject.transform.SetParent(transform, false);
        surfaceBox = surfaceObject.AddComponent<BoxCollider>();
        surfaceBox.size = ExpandedSurfaceSize(); // Includes the grab band and margin.
        var pointable = canvasRect.gameObject.AddComponent<PointableCanvas>(); pointable.InjectAllPointableCanvas(canvas);
        var surface = surfaceObject.AddComponent<ColliderSurface>(); surface.InjectAllColliderSurface(surfaceBox);
        var ray = surfaceObject.AddComponent<RayInteractable>(); ray.InjectAllRayInteractable(surface);
        ray.InjectOptionalSelectSurface(surface); ray.InjectOptionalPointableElement(pointable);
        ray.WhenPointerEventRaised += frame.TrackHover; // Edge glow.
    }

    public void ShowPanel() { SetMinimized(false); Recenter(); }

    // The logo lives outside Expanded so it stays visible and clickable when the panel collapses.
    private void BuildLogoToggle()
    {
        var tile = Rect("Minimized tile", canvasRect, TileX, TileY, TileSize, TileSize);
        minimizedTile = tile.gameObject.AddComponent<CanvasGroup>();
        minimizedTile.alpha = 0; minimizedTile.blocksRaycasts = false;
        Image("Outline", tile, -2, -2, TileSize + 4, TileSize + 4, new Color32(40, 104, 129, 255)).raycastTarget = false;
        Image("Surface", tile, 0, 0, TileSize, TileSize, Navy).raycastTarget = false;
        BuildGlow();
        var logo = Rect("CADVision logo", canvasRect, 46, 24, 58, 58).gameObject.AddComponent<RawImage>();
        logo.texture = Resources.Load<Texture2D>("CADEN/CADVisionLogo");
        // Centered pivot so the thinking spin turns the logo in place.
        logoRect = logo.rectTransform;
        logoRect.pivot = new Vector2(0.5f, 0.5f);
        logoRect.anchoredPosition = new Vector2(LogoCenter.x, -LogoCenter.y);
        var button = logo.gameObject.AddComponent<Button>(); button.targetGraphic = logo;
        var colors = button.colors; colors.highlightedColor = new Color(0.8f, 0.97f, 1); colors.pressedColor = new Color(0.6f, 0.85f, 0.9f);
        button.colors = colors; button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => SetMinimized(!minimized));

        // Voice hint beside the collapsed logo (head-locked with it); not a ray target.
        var hintCard = Image("Voice hint", canvasRect, TileX + TileSize + 10, TileY + 22, 300, 38, Navy);
        hintCard.raycastTarget = false;
        voiceHint = hintCard.gameObject.AddComponent<CanvasGroup>();
        voiceHint.alpha = 0; voiceHint.blocksRaycasts = false; voiceHint.interactable = false;
        voiceHintText = Label("Hint", hintCard.rectTransform, 14, 0, 280, 38, "", 17, Cyan);

        clickSource = gameObject.AddComponent<AudioSource>();
        clickSource.playOnAwake = false; clickSource.spatialBlend = 0; clickSource.volume = 0.6f;
        startClick = Click("CADEN listen start", 1320f);
        stopClick = Click("CADEN listen stop", 880f);
    }

    private void BuildGlow()
    {
        const int size = 64;
        glowTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float d = Mathf.Clamp01(Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f)) / 32f);
            pixels[y * size + x] = new Color(1, 1, 1, (1 - d) * (1 - d));
        }
        glowTexture.SetPixels(pixels); glowTexture.Apply();
        glowSprite = Sprite.Create(glowTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        logoGlow = Image("Speaking glow", canvasRect, 0, 0, 124, 124, Color.clear);
        logoGlow.sprite = glowSprite; logoGlow.type = UnityEngine.UI.Image.Type.Simple; logoGlow.raycastTarget = false;
        logoGlow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        logoGlow.rectTransform.anchoredPosition = new Vector2(LogoCenter.x, -LogoCenter.y);
    }

    public void SetMinimized(bool value)
    {
        if (minimized == value) return;
        // Collapse from wherever the panel was dragged; expand like other menus, in front of the user.
        if (value && openness == 1) { expandedPosition = transform.position; expandedRotation = transform.rotation; }
        if (value) frame.EndDrag();
        if (!value && viewer != null) PlaceAtLogo();
        minimized = value;
        if (!value) expanded.SetActive(true);
        expandedGroup.interactable = expandedGroup.blocksRaycasts = !value;
        // Shrink the ray collider to the logo tile so the collapsed panel doesn't block the scene.
        surfaceBox.size = value ? new Vector3(TileSize * Scale, TileSize * Scale, 0.004f) : ExpandedSurfaceSize();
        surfaceBox.center = value
            ? new Vector3((TileX + TileSize / 2 - W / 2) * Scale, (H / 2 - TileY - TileSize / 2) * Scale, 0)
            : surfaceBox.center; // Set with the size (ExpandedSurfaceSize).
    }

    private void AnimateMinimize()
    {
        float target = minimized ? 0 : 1;
        if (openness == target) return;
        openness = Mathf.MoveTowards(openness, target, Time.unscaledDeltaTime / MinimizeSeconds);
        float e = Mathf.SmoothStep(0, 1, openness);
        expanded.transform.localScale = Vector3.one * Mathf.Lerp(TileSize / W, size, e);
        // Content fades out early on collapse; the logo tile fades in as the panel reaches it.
        expandedGroup.alpha = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.2f, 1, openness));
        minimizedTile.alpha = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.4f, 0, openness));
        ApplyPose(e);
        if (openness == 0) expanded.SetActive(false);
    }

    // Corner resize: the open panel zooms (it is pivoted at the logo; the frame keeps the
    // opposite corner in place). The collapsed tile is unaffected.
    private void SetSize(float value)
    {
        size = value;
        if (!minimized) expanded.transform.localScale = Vector3.one * size;
        if (!minimized) surfaceBox.size = ExpandedSurfaceSize();
    }

    // The ray surface of the open panel, centered on it at any size: the panel is pivoted at the
    // logo, so a resized panel's center moves away from the root.
    private Vector3 ExpandedSurfaceSize()
    {
        Vector3 full = CADWindowFrame.SurfaceSize(W, H);
        surfaceBox.center = new Vector3((W / 2 - LogoCenter.x) * Scale * (size - 1f), -(H / 2 - LogoCenter.y) * Scale * (size - 1f), 0f);
        return new Vector3(full.x * size, full.y * size, full.z);
    }

    private void PlaceAtLogo()
    {
        var forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        // Like the Main Menu: centered in front of the user, a little below eye level, facing them.
        float distance = Mathf.Max(Mathf.Clamp(expandedDistance, 0.8f, 1.5f), CADMenuPanel.MinMenuDistance);
        expandedPosition = viewer.position + forward * distance + Vector3.down * expandedDrop;
        expandedRotation = Quaternion.LookRotation(expandedPosition - viewer.position, Vector3.up);
    }

    // Blends between the panel's world pose and the head-pinned pose that puts the logo top-left.
    private void ApplyPose(float e)
    {
        if (viewer == null) return;
        var minimizedRotation = viewer.rotation * Quaternion.LookRotation(minimizedLogoOffset, Vector3.up);
        var minimizedPosition = viewer.position + viewer.rotation * minimizedLogoOffset - minimizedRotation * LogoLocal;
        transform.SetPositionAndRotation(Vector3.Lerp(minimizedPosition, expandedPosition, e),
            Quaternion.Slerp(minimizedRotation, expandedRotation, e));
    }

    // Runs after the rig's late head update so the pinned logo doesn't lag behind the view.
    private void FollowViewer() { if (positioned && openness < 1) ApplyPose(Mathf.SmoothStep(0, 1, openness)); }
    private void OnEnable() => Application.onBeforeRender += FollowViewer;
    private void OnDisable() => Application.onBeforeRender -= FollowViewer;

    private void ResetConversation()
    {
        displayedVoiceError = null;
        generation++;
        foreach (Transform child in messages) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
        AddMessage("CADEN", "Hey, I'm CADEN.\nWhat would you like to know about this assembly?", false);
        var history = host.Session?.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        if (history != null)
            foreach (var message in history.Skip(Math.Max(0, history.Length - 38)))
                AddMessage(message.Role == "user" ? "YOU" : "CADEN", message.Text, message.Role == "user");
        microphonePreview = false;
        status.text = host.Status; feedback.text = "Hold the edge to move the panel.";
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
            input.text = ""; feedback.text = "Hold the edge to move the panel.";
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
    private void BuildMicrophone(Transform parent)
    {
        const int size = 64;
        circleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(31.5f - Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f))));
        circleTexture.SetPixels(pixels); circleTexture.Apply();
        circle = Sprite.Create(circleTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        microphoneFace = Image("Microphone (tap, left Y, or middle-finger pinch)", parent, 28, 634, 64, 64, Card);
        microphoneFace.sprite = circle; microphoneFace.type = UnityEngine.UI.Image.Type.Simple;
        // Tap: start listening, tap again to send, tap while working to cancel (like tapping Y).
        var micButton = microphoneFace.gameObject.AddComponent<Button>();
        micButton.targetGraphic = microphoneFace;
        micButton.navigation = new Navigation { mode = Navigation.Mode.None };
        micButton.onClick.AddListener(() => host.ToggleVoice());
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
    private void ShowFeedback(string message) { feedback.text = Core.Diagnostics.DiagnosticLog.Redact(message); }
    private void OnDestroy()
    {
        generation++; if (host != null) { host.Changed -= ResetConversation; host.Feedback -= ShowFeedback; }
        frame?.DisableBorderDrag();
        if (rounded != null) Destroy(rounded); if (roundedTexture != null) Destroy(roundedTexture);
        if (circle != null) Destroy(circle); if (circleTexture != null) Destroy(circleTexture);
        if (glowSprite != null) Destroy(glowSprite); if (glowTexture != null) Destroy(glowTexture);
        if (startClick != null) Destroy(startClick); if (stopClick != null) Destroy(stopClick);
    }
}
