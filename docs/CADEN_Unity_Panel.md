# CADEN panel integration

The caden-panel UI is the main world-space CADEN interface: logo, draggable background, scrollable conversation, typed composer, Send/Stop, New chat, and microphone control.

It now uses the existing CadenUnityHost through a thin CadenSessionHost adapter. Typed messages and voice share one chat session, metadata snapshot, issue/memory stores, view tools, token logging, realtime transcription and streaming speech playback. The imported standalone CADEN.Core DLL and duplicate configuration reader are removed. Core compiles directly from Assets/Scripts/CADEN/Core.

Build/install Android to update Quest. Credentials stay in the headset's existing persistent CADEN/.env. The Editor uses the repository CADEN/.env. The panel loads automatically and waits for the model; New chat reloads configuration and creates a fresh session without a paid API call. Model replacement invalidates the old session. This adapter does not retain separate per-model conversation caches.

The microphone icon and left Y both record / commit / cancel using the same voice component. The icon pulses while recording. Typed Send is disabled while voice/chat is busy. Answers from either input appear in the same conversation. Left thumbstick still opens the optional debug log viewer.

Editor menu: CAD Vision > CADEN > Open panel (Play Mode). Connect local CADEN now reloads the shared host rather than creating a second client. The old build-caden-core.ps1 is a compatibility notice; do not regenerate a DLL in Assets.

Preserved branch UI changes include the CADVision logo, background dragging, and pointer/grab UI exclusion. Quest layout, keyboard input, drag feel and microphone interaction require device testing. No live API calls were made while merging.