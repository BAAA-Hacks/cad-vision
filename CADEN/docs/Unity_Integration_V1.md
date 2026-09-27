# CADEN / CAD Vision integration V1

Core sources now live in `cad-vision-game/Assets/Scripts/CADEN/Core`. `CADEN/Core/Core.csproj` remains a .NET build wrapper pointing at those sources, so Desktop and Checks use the same implementation. No bin/obj output belongs in Unity Assets. Hosting contains the shared local configuration and persistence adapters; their existing namespaces are retained for compatibility.

## Start and test

Open the CAD Vision scene and enter Play mode. CADEN automatically attaches to the existing manipulation service, connects to the runtime registry, loads the canonical metadata, opens the issue/memory stores, performs the startup issue scan and creates a new chat session. No model-provider calls occur until a user sends a message.

Use **CADEN > Chat** in the Unity Editor to test. In the Editor, the default configuration directory is the existing repository `CADEN` folder, including `.env`, `caden-project.json`, `data`, and `prompts/system.md`. Avoid simultaneously editing the same project sidecars from Desktop and Unity: stale writers fail with a storage conflict.

For the headset or a standalone player, bind your text/STT UI to `CadenUnityHost.SendMessageToCaden(string)` (UnityEvent friendly), or await `SendAsync(string)` on the main thread. Subscribe to AnswerReceived, ErrorReceived and StatusChanged. There is no automatic microphone/STT capture in this change. The included chat window is Editor-only.

Player configuration defaults to `Application.persistentDataPath/CADEN`. Supply `.env` there, set ConfigurationDirectory explicitly, or pass `--caden-config-dir <directory>` on supported desktop launchers. Keys are never serialized into Unity assets. The prompt is bundled as a TextAsset, synchronized from CADEN/prompts/system.md before Editor builds. ElevenLabs uses the existing enabled/key/voice/model settings; Unity AudioSource playback shares the portable sentence streaming queue with Desktop.

Launching the Unity application now initializes CADEN in-process. An installer/shipper does not need to launch Desktop separately. A newly received model automatically revokes the old tools, cancels Gemini/speech, reloads metadata/stores and rebuilds the session. The installer executable itself was not changed.

## Public view tools

All tools retain CADEN 3.0 envelopes. Reads require projectId/snapshotId. Actions additionally require operationId, viewSessionId and expectedRevision, with the latter two available in captured turn context or get_view_state. Infrastructure arguments currently follow the existing CADEN tool pattern; a general Gemini-hidden orchestration migration remains separate.

| Tool | Purpose |
| --- | --- |
| get_view_state | Selection, interaction scope, multi-pick state, detached IDs, model/view identity and revision. Visibility readback remains explicitly unavailable. |
| select_objects / clear_selection | Exact IDs; replace/add/remove. Replacing selection is blocked during multi-pick. |
| focus_objects / clear_focus | Use the menu Focus effect on logical subtrees, including detached members; ghost other parts. Clearing restores normal appearance and preserves explicit hides. |
| hide_objects / show_objects | Hide/unhide logical subtrees. Unhide restores required physical ancestors but not unrelated hidden branches. |
| detach_for_inspection | Detach `objectIds`. `mode: group` (default) moves them together toward the headset's right, clear of their assemblies, keeping their relative layout. `mode: explode` moves each away from the targets' mean geometry center by `spread` (0.25–3, default 1 doubles the distance); one assembly ID explodes its direct children. Already detached objects are not moved again. |
| reattach_objects | Original parent and imported local pose; attached objects are no-ops. |
| reset_objects | Restore object or logical subtree poses/parents; preserve visibility and selection. |
| reset_view | Explicit global reset of imported poses, model placement, visibility, selection and interaction scope. |

No explicit enter/exit interaction-scope, camera/focus, transparency, or freeform movement tools are exposed. The geometric inspection offset is at least 0.1 m with 0.1 m bounding-box clearance and is rejected above 2 world metres. It preserves orientation, is presentation-only, and does not establish physical clearance or change exported CAD constraints. A headset/view camera and usable bounds are required.

## Identity, context and transactions

Selection is captured before each Gemini turn. 'This/these/selected' resolves to those IDs; singular references with several selected objects require clarification. Selection never implicitly invokes set_scope. Explicit view targets are independent of CADEN's query scope, while explicit query scope continues to govern query tools. Runtime hierarchy/mapping must match the immutable metadata; unavailable geometry mapping disables actions while metadata reads continue.

View context lists are capped at 64 IDs with total counts and completeness flags. A larger selection requires clarification rather than silently acting on a subset. The host uses `await ExecuteAsync`; synchronous view dispatch is rejected to prevent main-thread deadlocks.

Dispatch validates model identity on Unity's main thread. A command validates every requested ID before changing anything, checks the view revision (including human selection/pose/visibility changes), and snapshots the current inspection state. Ordinary failures restore that state. A model replacement invalidates the command instead of restoring old references onto the new model. Unexpected errors use correlated diagnostics.

**View receipts are deliberately session-scoped, not disk-durable.** They are retained for the loaded view session, bounded to 2,048 operations, with no silent eviction. Exact replay returns the original receipt and result with replayed=true; different arguments with the same operation ID fail. View state resets on model/session replacement, so old viewSessionIds are rejected. This is the V1 exception to the durable memory/issues action policy. A committed view action returns its receipt even when cancellation arrives after application; cancellation before application does nothing. Speech/chat cancellation cannot undo an already committed view change.

## Verification

Desktop and Checks still build through their original projects. `CADEN > Run offline integration checks` exercises native Unity selection, invalid batches, independent query scope, hide/unhide ancestors, detach offsets, replay/conflicts, detached assembly isolation/reset, human interaction conflicts, cancellation and stale queued commands using disposable objects. It does not call Gemini or ElevenLabs. Run this on an empty test scene for predictable camera selection.

Unity source compilation and native Editor checks do not substitute for an Android/Quest IL2CPP build and headset test. Core/Hosting linker preservation is included for their reflection-serialized contracts.

Implementation validation: Desktop/Core/Checks builds and the full offline Checks suite passed. Native Unity 6000.6.0f1 checks passed in an isolated batch-mode project, including rollback after an injected post-mutation failure and receipt recovery after cancellation during commit. The complete Unity adapter/Editor sources also compile against the installed Unity/project assemblies. No paid API calls were used.
