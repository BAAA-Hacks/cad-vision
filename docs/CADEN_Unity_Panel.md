# CADEN in CADVision

The world-space panel uses the original CADVision logo, a navy/cyan palette, 22 px conversation text, controller-sized actions, a scrollable conversation, a multiline composer, one suggested question, background dragging, cancellation and new chat. It stays anchored in the world rather than following every head movement. Its physical size is 0.72 × 0.82 m.

## Try it

1. Refresh Unity and enter Play Mode in a CADVision scene. A panel appears when the scene has a CADVisionRuntime. You can also choose **CAD Vision → CADEN → Open panel (Play Mode)**.
2. Choose **CAD Vision → CADEN → Connect local CADEN (Editor only)**. This uses the same `CADEN/.env` and `CADEN/prompts/system.md` as the desktop host. No request is sent until Send is clicked.
3. Load a design, choose the suggested question or enter your own, then Send. Use the existing Meta controller ray/trigger interaction. InputField uses platform keyboard behavior; Quest keyboard and scrolling require device validation.
4. Stop cancels a pending turn; failures keep the draft for retry. New chat clears history. Changing assemblies cancels any pending request. Conversations are keyed by CADEN project ID and exact metadata snapshot ID: returning to the same export restores its conversation, while changed metadata starts a fresh one. The 12 most recently used conversations stay in memory for the lifetime of this panel; they do not persist after closing the app. New chat clears only the current assembly conversation.

## Existing CADEN integration

`CadenSessionHost` directly uses `Core.ChatSession`, `GeminiClient`, `LoadProject` and `SemanticQueryTools`. The active Unity model's original metadata is supplied to the canonical CADEN loader. Query tools use the full loaded model; the panel does not yet synchronize the Unity selection with CADEN active scope. Durable project memory and issue-store startup are not wired in this host; CADEN reports those capabilities as unavailable.

`Assets/Plugins/CADEN/CADEN.Core.dll` is built from the existing `CADEN/Core` source. It uses Unity's installed Newtonsoft package, not a second JSON DLL. `link.xml` preserves CADEN for IL2CPP reflection. Rebuild after changing Core:

```powershell
.\tools\build-caden-core.ps1 -UnityEditorData 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Data'
```

On Quest, the panel renders but remains disconnected until an application host supplies `GeminiSettings` to `CadenSessionHost.Configure(...)` at runtime. The Editor configuration reader and developer key are not included in the APK. CADEN currently has no server endpoint; provisioning device credentials or a relay is separate from the UI. Do not serialize a developer API key in a scene, prefab, Resources asset, or source file.

## Validation

The CADEN core, panel and Editor adapter are compiled against the locally installed Unity 6000.6 and Meta SDK reference assemblies. No live Gemini requests are made during these checks. Visual layout, ray interaction, native keyboard, long-response scrolling, TLS and IL2CPP still require Unity/Quest testing. Before a demo, verify Send/Stop, model replacement, New chat and panel dragging on the headset.

The chat opens with a CADEN greeting and one assembly-overview suggestion. The assembly name appears above the conversation. Click and hold unused panel space with the controller ray, then move the ray to reposition the panel in its plane. Release to leave it there. Buttons, composer and message-card scrolling retain their own interactions. There is no Move here button. The existing logo, colors and initial placement to the left at eye level are preserved. Drag feel and long-message scrolling still need headset validation.
The panel stays open; the minimize button and compact dock have been removed. No hand-tracking rig, gesture handler, or hand input binding was added by this panel. It reuses the existing scene pointer bridge for controller rays.