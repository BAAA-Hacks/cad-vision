using System;

using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CADEN.Unity
{
    // Optional chat-log viewer; voice input remains active while the viewer is hidden.
    public sealed class CadenStatusPanel : MonoBehaviour
    {
        private CadenUnityHost host;
        private Canvas canvas;

        private CadenVoiceInput voice;
        private CadenChatDebugView debugView;
        private Text progress;

        private Font font;

        private bool positioned;
        private float nextRefresh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CadenStatusPanel>() == null)
                new GameObject("CADEN Chat Logs").AddComponent<CadenStatusPanel>();
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
            rect.sizeDelta = new Vector2(900, 700);
            rect.localScale = Vector3.one * 0.001f;
            var background = go.AddComponent<Image>();
            background.color = new Color(0.025f, 0.04f, 0.065f, 0.97f);
            progress = Label("Progress", "Chat logs — left stick click to hide/show. Y: record / send / cancel.", 20, 24, 80);
            debugView = new GameObject("Chat debug log").AddComponent<CadenChatDebugView>();
            debugView.Build(canvas.transform, font);
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
            box.size = new Vector3(0.9f, 0.7f, 0.004f);
            var surface = surfaceObject.AddComponent<ColliderSurface>();
            surface.InjectAllColliderSurface(box);
            var interactable = surfaceObject.AddComponent<RayInteractable>();
            interactable.InjectAllRayInteractable(surface);
            interactable.InjectOptionalSelectSurface(surface);
            interactable.InjectOptionalPointableElement(pointable);
            // Hide the ray surface together with the canvas.
            surfaceObject.transform.SetParent(go.transform, true);
            canvas.gameObject.SetActive(false);
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

        private void Update()
        {
            if (canvas == null) return;
            bool processing = (host != null && host.IsBusy) || (voice.Busy && !voice.Recording);
            string stage = voice.Busy || !string.IsNullOrEmpty(voice.LastError) ? voice.Status : host == null ? "Waiting for CADEN host" : host.Status;
            progress.text = "CHAT LOGS — left stick click to close\n" + (processing ? "[" + "|/-\\"[(int)(Time.unscaledTime * 8) % 4] + "] " : "") + stage + "\nHold Y: talk / Tap Y: record, send, cancel";
            if (!positioned) Position();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) voice.Press(host);
            if (OVRInput.GetUp(OVRInput.Button.Two, OVRInput.Controller.LTouch)) voice.Release();
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

        private void OnInput(string message) { debugView.Append("INPUT TO GEMINI", message); }
        private void OnAnswer(string message) { debugView.Append("GEMINI", message); }
        private void OnError(string message) { if (debugView != null) debugView.Append("ERROR", message); }
        private void OnVoiceMessage(string message)
        {
            if (!message.StartsWith("YOU: ", StringComparison.Ordinal)) debugView.Append("VOICE", message);
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
