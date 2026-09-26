# CADEN — standalone C# chat

The current app is a native Windows chat window backed by a reusable C# core.
It does not require Python, Streamlit, a browser, or a local web server.

## Run

From PowerShell:

```powershell
cd C:\Users\agnco\cad-vision\CADEN
.\run-caden.cmd
```

Close the window to exit. The launcher uses the local SDK in .tools/dotnet if present,
otherwise dotnet from PATH. On another machine, install the .NET 10 SDK first.
The first build needs internet access to restore Newtonsoft.Json.

With dotnet on PATH, the equivalent command is:

```powershell
dotnet run --project Desktop/Desktop.csproj
```

The old Streamlit server does not conflict with this app, which uses no listening port.

## Configuration

The desktop host reads the existing CADEN/.env and prompts/system.md. It never copies
the key to build output. See .env.example for settings:

- GEMINI_API_KEY (or GOOGLE_API_KEY)
- GEMINI_MODEL (default gemini-flash-latest)
- GEMINI_TIMEOUT_SECONDS (default 60)
- GEMINI_MAX_OUTPUT_TOKENS (default 4096)

Process environment variables override their corresponding .env values.
Click **New chat** after editing configuration to reload it and clear history.
The parser supports NAME=value, optional export, quoted single-line values, and comments.
Variable expansion and multiline values are not supported.

For a separate configuration directory:
`run-caden.cmd --config-dir "C:\path\to\CADEN"`.

## Behavior

- Send with the button or Ctrl+Enter; Enter inserts a newline.
- History stays in memory and is sent with follow-up questions.
- System instructions remain hidden from the transcript.
- New chat clears history and reloads configuration.
- Cancel stops waiting and leaves that turn out of history. Google may already have processed it.
- Failed turns remain outside history and can be retried manually.
- Errors include status, Google's explanation, and guidance, with credentials redacted.
- The transcript displays plain text, including any Markdown syntax returned.
- Four read-only query tools are connected; automatic retry and visualization remain future work.

## Query metadata

The desktop loads `data/metadata.json` if present. This machine has a local copy of the
supplied FRED synthetic metadata there; the file is ignored by Git. Use **Load metadata**
to choose another JSON. Successful replacement starts a new chat. A rejected file leaves
the previous design/conversation intact. New chat reloads the currently selected file.
No GLB upload or Unity connection is needed for these metadata queries.

Try “What is loaded?”, “Find objects named R_0805”, “Show the root's direct children”,
and “What is the root assembly's mass?” The fixture has no engineering mass/material
evidence, so CADEN should explain that it is unavailable. The status line reports the
number of queries executed for the last successful turn.

Tools live in `Core/tools`, with query code in `Core/tools/query`.
See [the shared semantic tool contract](Core/tools/SEMANTIC_CONTRACT.md) for formats, bounds and errors.
The hidden system prompt defines CADEN's role and the same evidence rules.

## Structure and Unity migration

- **Core:** .NET Standard 2.1; chat history, Gemini REST client, settings, and IChatClient.
- **Core/tools:** registry, JSON contract, and the four query handlers plus metadata parsing.
- **Core/primitives:** internal data structures and operations, including the [mechanical multigraph](Core/primitives/README.md). It is not exposed to Gemini.
- **Canonical loader:** [ProjectSnapshot and scoped load diagnostics](Core/primitives/operations/project/README.md). Used by the desktop semantic queries; unusable graph/hierarchy data degrades independently from properties.
- **Issue store:** [Immutable findings, indexed queries, dispositions and targeted revalidation](Core/primitives/operations/issues/README.md). Persistent memory remains future work.
- **Issue engine:** [Subject-level scans, candidate presentation and initial checkers](Core/primitives/operations/issues/ENGINE.md). Includes missing material and native constraint-state checks; host/chat integration and export-dependent checks remain deferred.
- **Desktop:** .NET 10 Windows Forms; temporary chat UI and local configuration loader.
- **Checks:** console-based offline checks and an optional live connectivity check.

The core has no desktop filesystem paths, environment-variable loading, WinForms, or Unity
dependencies. The host supplies settings, prompt text, and HttpClient. There is no Python backend.

Unity 6 supports the core's .NET Standard 2.1 target. Later, replace the desktop UI/config
loader and supply Newtonsoft.Json through Unity's supported package without duplicate DLLs.
Do not import the desktop project or .NET 10 runtime into Unity.

This target is not proof of Quest support. Unity/IL2CPP, JSON package compatibility and
stripping, Android network permissions, TLS, and device lifecycle still require testing.
IChatClient allows a Unity-specific transport if necessary. A distributed Quest app needs
a secure credential arrangement rather than an embedded developer key.

## Checks

From CADEN (substitute dotnet if using an installed SDK):

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj
```

Offline checks use fake HTTP responses and dummy credentials. To explicitly send two
small requests with your configured key and verify follow-up recall:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --live
```

Live requests use your Google project's quota/billing.

To verify a live metadata-to-tool-to-answer turn using `data/metadata.json`:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --live-query
```
