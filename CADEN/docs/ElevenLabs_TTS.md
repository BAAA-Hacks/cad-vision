# ElevenLabs speech

Add these values to CADEN/.env (or environment variables), keeping your real key local:

```dotenv
ELEVENLABS_ENABLED=true
ELEVENLABS_API_KEY=your_key
ELEVENLABS_VOICE_ID=your_voice_id
ELEVENLABS_MODEL=eleven_flash_v2_5
ELEVENLABS_TIMEOUT_SECONDS=30
```

Copy a voice ID from your ElevenLabs voice library and use an API key with Text to Speech access. Restart Desktop after installing this change. New chat reloads configuration afterward. Missing or invalid speech configuration is reported through diagnostics without preventing chat.

Speak controls automatic reading of completed CADEN replies. Stop voice cancels generation/playback. Speak again synthesizes the last answer again (another potentially billable ElevenLabs request), without invoking Gemini or tools. Sending a new message, loading metadata, starting a new chat, or closing the window stops previous speech. Cancellation cannot undo provider usage already incurred. Speech errors retain the successful chat answer and do not enable Gemini Retry.

Only final reply text is sent to ElevenLabs. Tool results, metadata files, prompts, and chat history are not attached. Provider error bodies are excluded from diagnostics; HTTP status and actionable guidance are logged with the normal correlation ID. Speech is disabled by default and no automatic paid retries occur. Existing token logs measure Gemini usage, not ElevenLabs billing.

The shared Core/Speech under cad-vision-game/Assets/Scripts/CADEN contains the Unity-independent HTTP client, settings and sentence queue, returning mono signed 16-bit little-endian PCM at 24 kHz. Desktop wraps this in WAV for Windows playback; CadenUnityHost now uses AudioClip/AudioSource with the same configuration settings. No Python or ElevenLabs agent is involved.

With Speak enabled, Desktop uses Gemini's streamGenerateContent SSE endpoint. Complete sentences are queued for ElevenLabs as answer text arrives, so the first sentence can be synthesized and played while Gemini produces the remainder. Sentences shorter than 31 characters are combined; decimal values split across network chunks remain intact. The trailing sentence is flushed when the answer commits. A short single-sentence answer may therefore see little improvement. Each sentence is a separate TTS generation; this is sentence streaming, not ElevenLabs WebSocket input/audio streaming, and there may be a pause between sentences. Gemini request count is unchanged, and cumulative usage metadata is counted once per request, not once per chunk.

Thought text and function-call payloads never enter speech. The streaming instruction prohibits tool preambles. A detected function-call round cancels its queued/in-flight speech; subsequent answer rounds start a fresh queue. Streamed speech is provisional: prose emitted before a later function call, safety ending, error or cancellation may already have been heard and cannot be retracted. Failed or interrupted Gemini streams do not commit partial chat history; their speech is cancelled. Stopping voice alone leaves Gemini running. Speech failures are reported independently and do not rerun Gemini or trigger automatic paid retries. Turning Speak off uses the existing non-streaming Gemini path on the next send. Speak again uses a single full-answer TTS request.

Text is capped at 5,000 characters per response round and audio at five minutes per generation; exceeding limits reports an error rather than silently truncating the answer. Offline checks cover early delivery, exact text, token accounting, thought/tool isolation, signatures, stream interruption, cancellation, and speech failure isolation. Real latency, voice availability and audible playback require a user-run test. Run offline streaming checks with `dotnet run --project Checks/Checks.csproj -- --streaming`.

API reference: https://elevenlabs.io/docs/api-reference/text-to-speech/convert

Gemini streaming reference: https://ai.google.dev/api/generate-content#method:-models.streamgeneratecontent

Unity/Quest now uses the `/stream` PCM endpoint through `IStreamingSpeechClient`, with a 200 ms startup buffer, bounded PCM FIFO and DSP-tail draining. It starts playback during the download. Desktop retains full-buffer playback. See Quest_Speech_To_Text.md for the realtime input/output path.
