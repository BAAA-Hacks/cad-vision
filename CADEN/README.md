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
- GEMINI_MAX_TOOL_ROUNDS (default 12; range 1–128)
- GEMINI_MAX_TOOL_CALLS (default 48; range 1–1024)

Tool budgets are ceilings, not targets. A turn can use up to the configured number of
tool rounds followed by a final answer request. The existing whole-turn timeout still
applies independently; increase GEMINI_TIMEOUT_SECONDS if longer investigations time out.

Process environment variables override their corresponding .env values.
Click **New chat** after editing configuration to reload it and clear history.
The parser supports NAME=value, optional export, quoted single-line values, and comments.
Variable expansion and multiline values are not supported.

For a separate configuration directory:
`run-caden.cmd --config-dir "C:\path\to\CADEN"`.

The desktop creates `caden-project.json` in that directory with a durable CADEN project ID.
Keep it when re-exporting the same project; use a separate configuration directory for a
different project. All metadata selected within one workspace shares that association.
Exporter IDs remain source provenance. A corrupt association file fails visibly and is
never silently replaced. Unity will supply its own explicit `ProjectAssociation`.

## Behavior

- Send with the button or Ctrl+Enter; Enter inserts a newline.
- History stays in memory and is sent with follow-up questions.
- System instructions remain hidden from the transcript.
- New chat clears history and reloads configuration.
- Cancel stops waiting and leaves that turn out of history. Google may already have processed it.
- Failed turns remain outside history and can be retried manually.
- Errors include status, Google's explanation, and guidance, with credentials redacted.
- The transcript displays plain text, including any Markdown syntax returned.
- Four general queries are connected; two mechanical queries appear when scoped export data is usable. Automatic retry and visualization remain future work.

## Query metadata

The desktop loads `data/metadata.json` if present. This machine has a local copy of the
supplied FRED synthetic metadata there; the file is ignored by Git. Use **Load metadata**
to choose another JSON. Successful replacement starts a new chat. A rejected file leaves
the previous design/conversation intact. New chat reloads the currently selected file.
No GLB upload or Unity connection is needed for these metadata queries.
Metadata schemas 1.0 and 2.1 are supported. See the [2.1 importer mapping](Core/primitives/operations/project/SCHEMA_2_1.md)
for extraction status, units and unknown/not-applicable semantics.

Try “What is loaded?”, “Find objects named R_0805”, “Show the root's direct children”,
and “What is the root assembly's mass?” The fixture has no engineering mass/material
evidence, so CADEN should explain that it is unavailable. The status line reports the
number of queries executed for the last successful turn.

Tools live in `Core/tools`, with query code in `Core/tools/query`.
See [the shared semantic tool contract](Core/tools/SEMANTIC_CONTRACT.md) for formats, bounds and errors.
See [capability discovery and recovery](Core/tools/CAPABILITIES.md) for unavailable tools,
specific failure reasons and supported alternatives. The model summary works before loading metadata.
The hidden system prompt defines CADEN's role and the same evidence rules.
Responses use contract 3.0: `success`, explicit project/snapshot identity, top-level
coverage and pagination, structured errors, and opaque cursors bound to the query.
The host runs an issue scan on startup and every successful metadata reload, including New chat.
Six issue reads, two revalidation actions and set_issue_disposition are connected to Gemini.
Disposition supports Open, Resolved and Ignored with durable storage and evidence/revision
guards. Direct issue creation, deletion and evidence edits are not exposed.
See [issue access](Core/tools/ISSUE_CONTRACT.md) for coverage, revision and durable receipt rules.
See [mechanical queries and exporter fields](Core/tools/MECHANICAL_CONTRACT.md)
for the required `mechanicalScopes` extension. The FRED fixture does not provide usable
mate evidence, so it retains the four general queries, nine issue tools and two memory tools with explicit
evaluation limitations. Missing mate analysis does not mean the assembly is defect-free.

## Structure and Unity migration

- **Core:** .NET Standard 2.1; chat history, Gemini REST client, settings, and IChatClient.
- **Core/tools:** registry, shared JSON contract, general queries, scoped mechanical queries and issue access.
- **Core/primitives:** internal data structures and operations, including the [mechanical multigraph](Core/primitives/README.md). It is not exposed to Gemini.
- **Canonical loader:** [ProjectSnapshot and scoped load diagnostics](Core/primitives/operations/project/README.md). Used by the desktop semantic queries; unusable graph/hierarchy data degrades independently from properties.
- **Issue store:** [Immutable findings, indexed queries, dispositions and targeted revalidation](Core/primitives/operations/issues/README.md).
- **Project memory:** [Durable sidecar primitives](Core/primitives/operations/memory/README.md) and [chat read/write tools](Core/tools/MEMORY_CONTRACT.md). Desktop opens data/memory.caden.json using the host project association. Atomic multi-object writes, requirements, retirement, provenance and stale references are supported. Gemini writes are AssistantInferred; project selection UI and user-authority promotion remain deferred.
- **Issue engine:** [Subject-level scans, candidate presentation and initial checkers](Core/primitives/operations/issues/ENGINE.md). Startup scans and chat access are connected. Includes missing material, native constraint-state checks and scoped connectivity checks where supported by export evidence; dedicated mate diagnostics remain deferred.
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

Unexpected failures now show a diagnostic ID and log location. Full redacted exception
details are written to `%LOCALAPPDATA%\CADEN\logs\caden-YYYY-MM-DD.jsonl`.
See [failure diagnostics](Core/Diagnostics/README.md) for coverage and host integration.

Desktop also records [per-turn token usage](Core/Diagnostics/TOKEN_USAGE.md) in
`%LOCALAPPDATA%\CADEN\logs\usage-YYYY-MM-DD.jsonl`: input, output and cached input,
summed over every API request in the turn. Partial/missing usage is explicitly marked.

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

The 24-tool orchestration surface includes hidden local startup context, keyed memory
recall and export/load diagnostic reads. See [orchestration testing](docs/CADEN_Orchestration_Testing.md)
for the isolated 25-prompt and adversarial suites, and the
[reevaluation report](docs/CADEN_Orchestration_Reevaluation.md) for results and remaining limits.

The chat host additionally provides session-local `recall_result` and
[bounded outgoing context](docs/CADEN_Session_Context.md). Old raw tool responses stay
local; subsequent requests send recent conversation and a small result directory.

[Active scope](docs/CADEN_Active_Scope.md) adds set_scope/clear_scope/get_scope, scoped
object/issue queries and boundary-aware mates. Its mixed assembly/subassembly/part suite
uses a labelled synthetic fixture; the original real two-part baseline is preserved.

To verify a live metadata-to-tool-to-answer turn using `data/metadata.json`:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --live-query
```
