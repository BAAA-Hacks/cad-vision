using System;
using System.Collections.Generic;
using Core.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace CADEN.Unity
{
    // Session-only, bounded transcript. Never includes keys or raw provider HTTP bodies.
    public sealed class CadenChatDebugView : MonoBehaviour
    {
        private readonly Queue<string> entries = new Queue<string>();
        private int characters;
        private Text text;
        private ScrollRect scroll;
        private RectTransform content;

        public void Build(Transform canvas, Font font)
        {
            transform.SetParent(canvas, false);
            var root = gameObject.AddComponent<RectTransform>();
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 1);
            root.pivot = new Vector2(0.5f, 1); root.anchoredPosition = new Vector2(0, -112);
            root.sizeDelta = new Vector2(850, 550);
            scroll = gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            viewportObject.transform.SetParent(transform, false);
            var viewport = (RectTransform)viewportObject.transform;
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(0, 60); viewport.offsetMax = Vector2.zero;
            viewportObject.GetComponent<Image>().color = new Color(0.015f, 0.02f, 0.035f, 1);
            scroll.viewport = viewport;
            var textObject = new GameObject("Transcript", typeof(RectTransform));
            textObject.transform.SetParent(viewport, false);
            content = (RectTransform)textObject.transform;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1); content.sizeDelta = new Vector2(-20, 0);
            text = textObject.AddComponent<Text>(); text.font = font; text.fontSize = 21;
            text.color = Color.white; text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
            scroll.content = content;
            Button("Older", -280, font, () => Move(1));
            Button("Newer", 0, font, () => Move(-1));
            Button("Clear log", 280, font, () => { entries.Clear(); characters = 0; Refresh(); });
            Append("INFO", "Received prompts, Gemini replies, voice stages and errors appear here. Session-only; oldest entries are removed at 100 entries / 12,000 characters.");
        }

        private void Button(string caption, float x, Font font, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(UnityEngine.UI.Button));
            go.transform.SetParent(transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0);
            rect.anchoredPosition = new Vector2(x, 26); rect.sizeDelta = new Vector2(260, 48);
            var image = go.GetComponent<Image>(); image.color = new Color(0.12f, 0.3f, 0.48f);
            var button = go.GetComponent<UnityEngine.UI.Button>(); button.targetGraphic = image; button.onClick.AddListener(action);
            var label = new GameObject("Label", typeof(RectTransform), typeof(Text)); label.transform.SetParent(go.transform, false);
            var lr = (RectTransform)label.transform; lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            var t = label.GetComponent<Text>(); t.font = font; t.fontSize = 20; t.text = caption;
            t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
        }
        private void Move(int direction)
        {
            Canvas.ForceUpdateCanvases();
            float travel = content.rect.height - scroll.viewport.rect.height;
            if (travel > 0) scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + direction * scroll.viewport.rect.height * 0.8f / travel);
        }
        public void Append(string role, string message)
        {
            message = DiagnosticLog.Redact(message ?? "");
            if (message.Length > 8000) message = message.Substring(0, 8000) + "\n[entry truncated]";
            string entry = DateTime.Now.ToString("HH:mm:ss") + "  " + role + "\n" + message + "\n\n";
            entries.Enqueue(entry); characters += entry.Length;
            while (entries.Count > 100 || characters > 12000) characters -= entries.Dequeue().Length;
            Refresh();
        }
        private void Refresh()
        {
            text.text = string.Concat(entries);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(490, text.preferredHeight + 16));
            Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 0;
        }
        private void OnEnable() { if (text != null) Refresh(); }
    }
}
