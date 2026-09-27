using System;
using System.IO;
using CADVision;
using Core.Diagnostics;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CADEN.Unity
{
    // Runtime UI: exists in the APK as well as the Editor. Test and voice controls send explicit requests.
    public sealed class CadenStatusPanel : MonoBehaviour
    {
        private CadenUnityHost host;
        private Canvas canvas;
        private Text status;
        private Text feedback;
        private Button reload;
        private Button testChat;
        private Button record;
        private CadenVoiceInput voice;
        private CadenChatDebugView debugView;
        private Text progress;
        private bool debugOpen;
        private bool sending;
        private const string TestPrompt = "What is the name of this CAD model, and who are you?";
        private Font font;
        private bool reloading;
        private bool positioned;
        private float nextRefresh;
        private string lastError = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CadenStatusPanel>() == null)
                new GameObject("CADEN Status Panel").AddComponent<CadenStatusPanel>();
        }

        private void Start()
        {
            voice = gameObject.AddComponent<CadenVoiceInput>();
            voice.Message += OnVoiceMessage;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            gameObject.AddComponent<CADUIPointerTarget>();
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<GraphicRaycaster>();
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(900, 960);
            rect.localScale = Vector3.one * 0.001f;
            var background = go.AddComponent<Image>();
            background.color = new Color(0.025f, 0.04f, 0.065f, 0.97f);
            var title = Label("Title", "CADEN", 28, 24, 45); title.fontStyle = FontStyle.Bold;
            title.rectTransform.sizeDelta = new Vector2(400, 45); title.rectTransform.anchoredPosition = new Vector2(-210, -24);
            status = Label("Status", "Starting…", 21, 112, 308);
            progress = Label("Progress", "Idle", 20, 72, 32);
            debugView = new GameObject("Chat debug log").AddComponent<CadenChatDebugView>();
            debugView.Build(canvas.transform, font);
            debugView.gameObject.SetActive(false);
            var debugButton = MakeButton("Chat logs / status", 230, ToggleDebug);
            ((RectTransform)debugButton.transform).anchoredPosition = new Vector2(230, 433);
            ((RectTransform)debugButton.transform).sizeDelta = new Vector2(360, 44);
            feedback = Label("Feedback", "Y: record / send. Left stick click: show/hide panel. Voice uses ElevenLabs then Gemini.", 20, 430, 240);
            record = MakeButton("Record / send (Y)", -210, () => voice.Toggle(host));
            ((RectTransform)record.transform).anchoredPosition = new Vector2(-210, -320);
            var cancel = MakeButton("Cancel voice", 210, () => voice.Cancel());
            ((RectTransform)cancel.transform).anchoredPosition = new Vector2(210, -320);
            testChat = MakeButton("Test Gemini: model name + identity", 0, SendTest);
            var testRect = (RectTransform)testChat.transform;
            testRect.anchoredPosition = new Vector2(0, -240);
            testRect.sizeDelta = new Vector2(815, 64);
            testChat.interactable = false;
            reload = MakeButton("Reload CADEN / settings", -210, Reload);
            MakeButton("Hide (left stick to reopen)", 210, () => canvas.gameObject.SetActive(false));

            var eventSystem = EventSystem.current;
            if (eventSystem == null) eventSystem = new GameObject("CADEN EventSystem").AddComponent<EventSystem>();
            if (FindAnyObjectByType<PointableCanvasModule>() == null)
                eventSystem.gameObject.AddComponent<PointableCanvasModule>();
            var pointable = go.AddComponent<PointableCanvas>();
            pointable.InjectAllPointableCanvas(canvas);
            var surfaceObject = new GameObject("Ray Surface");
            surfaceObject.layer = 2;
            surfaceObject.transform.SetParent(transform, false);
            var box = surfaceObject.AddComponent<BoxCollider>();
            box.size = new Vector3(0.9f, 0.96f, 0.004f);
            var surface = surfaceObject.AddComponent<ColliderSurface>();
            surface.InjectAllColliderSurface(box);
            var interactable = surfaceObject.AddComponent<RayInteractable>();
            interactable.InjectAllRayInteractable(surface);
            interactable.InjectOptionalSelectSurface(surface);
            interactable.InjectOptionalPointableElement(pointable);
            // Hide the ray surface together with the canvas.
            surfaceObject.transform.SetParent(go.transform, true);
        }

        private Text Label(string name, string value, int size, float top, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -top);
            rect.sizeDelta = new Vector2(850, height);
            var text = go.AddComponent<Text>();
            text.font = font; text.fontSize = size; text.color = Color.white;
            text.supportRichText = false; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate; text.text = value;
            return text;
        }

        private Button MakeButton(string title, float x, UnityEngine.Events.UnityAction click)
        {
            var go = new GameObject(title, typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = new Vector2(x, -400); rect.sizeDelta = new Vector2(395, 64);
            var image = go.AddComponent<Image>(); image.color = new Color(0.12f, 0.3f, 0.48f);
            var button = go.AddComponent<Button>(); button.targetGraphic = image; button.onClick.AddListener(click);
            var label = new GameObject("Label", typeof(RectTransform)); label.transform.SetParent(go.transform, false);
            var labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var text = label.AddComponent<Text>(); text.font = font; text.fontSize = 22;
            text.alignment = TextAnchor.MiddleCenter; text.text = title; text.raycastTarget = false;
            return button;
        }

        private void Update()
        {
            if (canvas == null) return;
            bool processing = sending || reloading || (host != null && host.IsBusy) || (voice.Busy && !voice.Recording);
            string stage = voice.Recording ? "Recording" : voice.Busy ? voice.Status : reloading ? "Loading CADEN" : processing ? "Gemini processing" : "Idle";
            progress.text = processing ? "[" + "|/-\\"[(int)(Time.unscaledTime * 8) % 4] + "] " + stage : stage;
            if (!positioned) Position();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
            {
                if (!reloading && !sending) voice.Toggle(host);
            }
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch))
            {
                bool show = !canvas.gameObject.activeSelf;
                canvas.gameObject.SetActive(show);
                if (show) Position();
            }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.5f;
            var found = FindAnyObjectByType<CadenUnityHost>();
            if (host != found)
            {
                UnsubscribeHost();
                host = found;
                if (host != null)
                {
                    host.ErrorReceived.AddListener(OnError);
                    host.InputReceived.AddListener(OnInput);
                    host.AnswerReceived.AddListener(OnAnswer);
                }
            }
            var runtime = host != null ? host.ModelRuntime : FindAnyObjectByType<CADVisionRuntime>();
            var receiver = runtime == null ? null : runtime.GetComponent<CadDesignReceiver>();
            string config = host != null ? host.ConfigurationPath : Path.Combine(Application.persistentDataPath, "CADEN");
            string model = "No imported metadata";
            if (runtime != null && runtime.Metadata != null)
            {
                var metadata = runtime.Metadata;
                model = Convert.ToString(metadata.GetObject(metadata.RootId)["name"]) +
                    "\nRoot ID: " + metadata.RootId + "\nRevision: " + runtime.Revision +
                    " | mapped objects: " + runtime.GetAllObjects().Count;
            }
            status.text = "Platform: " + Application.platform + (Application.isEditor ? " (Editor)" : " (installed player)") +
                "\nBuild: " + Application.version + " / " + Application.buildGUID +
                "\nCADEN: " + (host == null ? "Host missing — scene needs CADVisionManipulationService" : host.Status) +
                "\nModel: " + model + "\nReceiver: " + (receiver == null ? "not found" : receiver.LastStatus) +
                "\nConfig: " + config + "\n.env: " + (File.Exists(Path.Combine(config, ".env")) ? "present (contents hidden)" : "MISSING");
            reload.interactable = host != null && !host.IsBusy && !reloading && !sending && !voice.Busy;
            testChat.interactable = host != null && host.Ready && !host.IsBusy && !reloading && !sending && !voice.Busy;
            record.interactable = voice.Recording || (host != null && host.Ready && !host.IsBusy && !reloading && !sending && !voice.Busy);
            record.GetComponentInChildren<Text>().text = voice.Recording ? "Stop and send (Y)" : "Record (Y)";
            if (lastError.Length > 0) feedback.text = lastError;
        }

        private void Position()
        {
            var camera = Camera.main;
            if (camera == null) return;
            canvas.worldCamera = camera;
            transform.position = camera.transform.position + camera.transform.forward * 1.25f;
            transform.rotation = camera.transform.rotation;
            positioned = true;
        }

        private void ToggleDebug()
        {
            debugOpen = !debugOpen; debugView.gameObject.SetActive(debugOpen);
            status.gameObject.SetActive(!debugOpen); feedback.gameObject.SetActive(!debugOpen);
        }
        private void OnInput(string message) { debugView.Append("INPUT TO GEMINI", message); }
        private void OnAnswer(string message) { debugView.Append("GEMINI", message); }
        private void OnError(string message)
        { lastError = DiagnosticLog.Redact(message); if (debugView != null) debugView.Append("ERROR", lastError); }
        private void OnVoiceMessage(string message)
        {
            lastError = ""; feedback.text = message;
            // Full prompt/answer are logged once from the host; voice messages contain those too.
            if (!message.StartsWith("YOU: ", StringComparison.Ordinal)) debugView.Append("VOICE", message);
            if (!canvas.gameObject.activeSelf) { canvas.gameObject.SetActive(true); Position(); }
        }
        private async void SendTest()
        {
            if (host == null || !host.Ready || host.IsBusy || reloading || sending || voice.Busy) return;
            sending = true; lastError = "";
            testChat.interactable = false; reload.interactable = false;
            feedback.text = "YOU: " + TestPrompt + "\n\nWaiting for CADEN...";
            try
            {
                string answer = await host.SendAsync(TestPrompt);
                if (this != null && lastError.Length == 0) feedback.text = "CADEN: " + answer;
            }
            catch (OperationCanceledException) { if (this != null) { feedback.text = "Chat cancelled (the model or session may have changed)."; debugView.Append("CANCELLED", feedback.text); } }
            catch (Exception ex) { if (this != null) OnError(ex.Message); }
            finally { sending = false; }
        }
        private async void Reload()
        {
            if (host == null || reloading || host.IsBusy || sending || voice.Busy) return;
            reloading = true; lastError = ""; feedback.text = "Reloading metadata and configuration…";
            try { await host.ReloadAsync(); if (this != null && lastError.Length == 0) feedback.text = host.Status; }
            catch (Exception ex) { if (this != null) OnError(ex.Message); }
            finally { reloading = false; }
        }
        private void OnDestroy()
        {
            UnsubscribeHost();
            if (voice != null) voice.Message -= OnVoiceMessage;
        }
        private void UnsubscribeHost()
        {
            if (host == null) return;
            host.ErrorReceived.RemoveListener(OnError);
            host.InputReceived.RemoveListener(OnInput);
            host.AnswerReceived.RemoveListener(OnAnswer);
        }
    }
}
