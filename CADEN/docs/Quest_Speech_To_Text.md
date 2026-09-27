# Quest speech to text

Build and install the updated Android app, send the CAD export, and wait for CADEN ready. If necessary, use Reload on the runtime panel.

- Press **left Y** (or **Record**) to start recording. On first use, grant microphone permission, then press again.
- Speak a command, then press **Y** again (or **Stop and send**). Capture stops automatically at 30 seconds.
- The panel displays the transcript and CADEN's response. Existing ElevenLabs TTS plays the answer if configured.
- **Cancel voice** stops capture, transcription, or the voice-initiated CADEN turn. Pausing/leaving the app or replacing the model also cancels it.
- Click the **left thumbstick** to hide/show and reposition the panel. X retains the existing passthrough control.

Configuration comes from this device's CADEN/.env. On Quest that is `/sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN/.env`, not the PC copy.

```
ELEVENLABS_API_KEY=your_existing_key
# Optional; default:
ELEVENLABS_STT_MODEL=scribe_v2
```

STT is independent of ELEVENLABS_ENABLED (that flag controls spoken output). The key must have speech-to-text access. Gemini configuration is also needed for the subsequent CADEN answer. Reloading configuration makes no paid calls; recording starts only on user input. Stopping a recording uploads it to ElevenLabs, and a nonempty transcript is submitted through the existing Gemini/tool pipeline.

This first version uses recorded WAV uploads, not realtime streaming or a wake word. Audio stays in memory and is not saved to disk. Actual capture sample rate/channel count is preserved. No automatic retry duplicates a paid request. Errors include STT-specific codes and a diagnostic correlation ID; provider error bodies and credentials are not displayed.

Source: https://elevenlabs.io/docs/api-reference/speech-to-text/convert

Offline verification: `dotnet run --project CADEN/Checks/Checks.csproj -- --transcription`. This uses fake HTTP responses and needs no credentials. Device microphone, controller input, networking, and live transcription still require testing on Quest.
