# CADEN development workspace

The shipped assistant lives in [`cad-vision-game/Assets/Scripts/CADEN`](../cad-vision-game/Assets/Scripts/CADEN). This directory contains its active prompt, .NET build/test harnesses, optional desktop UI, and developer configuration. It is not a second implementation or a Python backend.

For installation, Quest configuration, and current product features, start with the [project README](../README.md).

## What is still used?

| Path | Status and purpose |
| --- | --- |
| `prompts/system.md` | Canonical system prompt. Unity's `CadenPromptSync` copies it to the Resources asset on Editor load and before builds. Desktop reads it directly. |
| `Core/Core.csproj` | .NET Standard 2.1 build adapter for the sources in Unity's `Assets/Scripts/CADEN/Core`. Contains no duplicate Core implementation. Required by Checks and Desktop. |
| `Checks/` | Offline regression checks, synthetic fixtures, benchmarks, and explicitly opt-in live evaluation tools. |
| `Desktop/` | Optional Windows Forms development client. Its file diagnostics/token logging classes are also compiled into Checks. Not shipped in Quest. |
| `run-caden.cmd` | Desktop launcher; uses `.tools/dotnet` when available, otherwise `dotnet` on PATH. |
| `.env.example` | Configuration template. Copy to `.env` for local development; never commit credentials. |
| `Metadata_Exporter_*.md` | Historical exporter handoffs, not current runtime schemas. |

Local ignored content is not obsolete merely because it is untracked:

- `.env` contains developer credentials/configuration and is used by the Editor and desktop host.
- `caden-project.json` records durable project association.
- `data/` may contain metadata and durable issue/memory sidecars. Preserve these when continuing the same project.
- `.tools/` contains the optional local SDK, validation harnesses, and build outputs.
- `bin/` and `obj/` are generated build output and can be regenerated after closing processes using them.

Quest uses its own `Application.persistentDataPath/CADEN` configuration and storage. It does not read this PC directory.

## Run and validate

From the repository root, with the .NET 10 SDK installed:

```powershell
# Optional desktop development window
.\CADEN\run-caden.cmd

# Offline checks: fake API transports, no paid requests
dotnet run --project CADEN/Checks/Checks.csproj

# Focused realtime speech transport checks, also offline
dotnet run --project CADEN/Checks/Checks.csproj -- --realtime-voice
```

If using the local SDK, replace `dotnet` with `.\CADEN\.tools\dotnet\dotnet.exe`.

The desktop loads `data/metadata.json` when present, or a file selected through Load metadata. New chat reloads configuration. Gemini requires a key and a model available to your account; optional ElevenLabs settings are in `.env.example`. Live checks require explicit live flags and consume API quota. Do not run them as part of routine cleanup.

Unity compiles Core and Hosting directly through their assembly definitions. Do not generate a Core DLL into `Assets/Plugins` or import the .NET 10 desktop runtime into Unity. Use Unity's Test Runner and **CADEN > Run offline integration checks** for host-level validation; standalone Core checks do not verify headset behavior.

## Sources of truth

- Behavior: [`prompts/system.md`](prompts/system.md).
- Tool declarations and capability gating: [Core/tools](../cad-vision-game/Assets/Scripts/CADEN/Core/tools).
- Data structures, queries, and checkers: [Core/primitives](../cad-vision-game/Assets/Scripts/CADEN/Core/primitives).
- Unity actions: [UnityViewHost.cs](../cad-vision-game/Assets/Scripts/CADEN/UnityViewHost.cs).

Tool availability depends on the current metadata and runtime. Static tool counts and old benchmark results are not a promise of the tools or performance available in a particular session.
