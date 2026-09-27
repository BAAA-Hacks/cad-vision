> Current UI: the large runtime/status/test panel has been removed. Only the chat-log viewer remains, hidden initially. Click the left thumbstick to view logs. Hold Y to talk and release to send, or tap Y to start and tap again to send; Y cancels while processing. Older UI instructions below describe the previous diagnostic panel.

# Quest runtime status

The CADEN status panel is created automatically in the running game, including Android builds. It shows platform/build identity, CADEN readiness, the imported metadata's root name/ID and revision, mapped object count, receiver status, and configuration location. It does not display credentials. Status/reload make no paid API calls. The **Test Gemini: model name + identity** button sends exactly "What is the name of this CAD model, and who are you?" through the normal chat pipeline and displays the response or error. It requires CADEN ready and is disabled during a request; configured ElevenLabs speech may also play.

Use the controller ray and trigger to click **Reload CADEN / settings**. Click the **left thumbstick** to hide/show the panel and place it in front of the current view when reopened. **Left Y** now records/sends speech; see [Quest speech to text](Quest_Speech_To_Text.md).

The Editor and installed Quest APK are separate instances. The Editor loads `CADEN/.env` from the repository. Quest loads `.env` from `Application.persistentDataPath/CADEN`, currently:

```
/sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN/.env
```

For this development headset, provision the configuration from the repository root:

```powershell
adb shell mkdir -p /sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN
adb push CADEN/.env /sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN/.env
```

Keep credentials out of Assets and source control. The system prompt is packaged separately by CadenPromptSync. Changing the PC `.env` does not update the headset copy. App uninstall/clear-data may remove it.

Build and install the Android app to get new panel code. Click Reload after changing configuration while a model is loaded. If the app was restarted and has no model, send the export again. Received model geometry/metadata is held in the runtime; the receiver does not persist the uploaded pair to disk. Project association, issue/memory data, and diagnostic logs are separate persistent files.

On 2026-09-26 ADB confirmed a native Quest process and an installed APK. Its diagnostic log showed `unity.reload` failed on missing `GEMINI_API_KEY` after model import. The PC configuration was then copied to the headset. Readiness must still be verified by reload/reimport; no live Gemini request was made.

## Chat debugging

Use **Chat logs / status** at the top of the Quest panel. It shows timestamped input sent to Gemini, Gemini replies, voice stages, and errors. Drag the transcript or use Older/Newer; Clear log clears only the visible diagnostic history, not the Gemini chat session. History is in memory, capped at 100 entries / 12,000 characters, and does not include raw tool payloads or HTTP bodies. Entries over 8,000 characters are truncated. An animated processing indicator remains visible during transcription, chat, and panel-triggered reload. Rebuild/install the Android app to receive this UI update.
