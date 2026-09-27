# Quest realtime speech

Build/install the updated Android app, send the CAD export, and wait for CADEN ready.

- Press **left Y** to record. On first use, grant microphone permission and press again.
- Audio streams to ElevenLabs while you speak; partial transcription appears in the optional chat-log viewer's status line.
- Press **Y again** to stop and commit. Only the final transcript is sent to CADEN. Recording stops at 30 seconds.
- Press **Y while processing** to cancel. Pausing the app or changing the model/session cancels the operation too.
- Click the **left thumbstick** to view/hide chat logs. X retains passthrough control. No large status/test panel is shown.

The headset uses its own `/sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN/.env`. The existing `ELEVENLABS_API_KEY` is used for realtime STT. Its key permissions must allow speech-to-text. The realtime endpoint uses `scribe_v2_realtime`; the old `ELEVENLABS_STT_MODEL` setting remains applicable only to the retained batch client, not this realtime path. No new credentials or uploads into Assets are needed.

STT is independent of `ELEVENLABS_ENABLED`, which controls spoken output. A realtime connection opens only on Y input and closes after commit/cancellation. Unlike the former batch flow, audio is uploaded while recording, so cancelling may still incur STT usage. No automatic paid retries or batch fallback occur. Audio is not saved locally.

Microphone capture starts before connection setup to retain first words. Chunks are at most 100 ms, downmixed to mono PCM16 at the actual capture sample rate. The socket uses manual commit, with a short silence tail. Gemini receives only the committed transcript. Empty/near-silent input does not invoke Gemini. Connection, send and final-transcript waits are bounded and cancellation-aware. Unsupported rates fail explicitly.

When TTS is enabled, Unity now requests streamed PCM audio, buffers 500 ms and starts playback during the HTTP download. A bounded FIFO handles split samples and backpressure. The audio callback fills temporary underflow with silence without dropping samples. Completion waits for the FIFO and DSP output queue to drain; cancellation interrupts immediately. Desktop retains its existing buffered audio adapter.

Sources:
- https://elevenlabs.io/docs/api-reference/speech-to-text/v-1-speech-to-text-realtime
- https://elevenlabs.io/docs/api-reference/text-to-speech/stream

Offline checks: `dotnet run --project CADEN/Checks/Checks.csproj -- --realtime-voice`, `--speech`, and `--streaming`. These use fake transports and no credentials. Headset/WebSocket compatibility, audio quality and real latency still require a user-run Quest test.