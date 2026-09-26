# Orchestration checks and live evaluation

The active behavior contract is `prompts/system.md`; `CADEN_Orchestration_Behavior_V1.md`
mirrors it. Startup initialization obtains a local summary and adds it to hidden model
context without an API call or visible message. Issue scanning/storage initialization remain
host responsibilities. New chat/metadata reload constructs a fresh client and context.

The current additive V1.1 surface has 20 tools: the previous 18 plus
`get_diagnostic_summary` and `get_diagnostics`. The existing V1-named declaration files
contain the regenerated V1.1 runtime schemas. Infrastructure arguments remain explicit;
the proposed orchestration adapter migration is still separate.

Diagnostics expose immutable loader records and reported root `extractionStatus` entries,
with exact filters, bounded pages and coverage over the original records. They do not
expose host logs or imply all exporter diagnostics are represented. Missing diagnostic
records never prove a successful export or clean design.

## Offline checks

From CADEN:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj
```

These use fake HTTP, verify hidden startup context with zero startup API calls, exact-key
cross-object memory retrieval after restart, diagnostic coverage invariance, duplicate
names/IDs, malformed numeric values, unknown suppression and invalid graph degradation.
Structural evaluator guards also reject unauthorized mutations and missing required calls.
The synthetic fixture is `Checks/Fixtures/adversarial_metadata.json`.

## Live checks — require explicit authorization

Live runs incur Gemini usage. Use a new output directory every time; existing results
cannot be overwritten. The runner isolates project identity, memory and issue journals
under that directory. It does not change the normal desktop sidecars or source metadata.

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --live --orchestration .tools/live-stress/runs/my-new-run "C:/path/to/metadata.json" . Checks/OrchestrationCases.json
```

The baseline manifest contains 25 sequential prompts; case 22 reopens both the chat and
durable stores. The five-case adversarial manifest is
`Checks/OrchestrationCases.Adversarial.json` and uses the synthetic fixture instead.
Its duplicate-name question follows an object-A discussion, deliberately retaining that
context; it is not an independent cold-start ambiguity test. Add a separate new-session
case when testing discovery without a prior referent.

Cases specify expected behavior, relevant `expectedTools`, forbidden behavior and explicit
allowed mutation tools. `expectedTools` are source-selection guidance, not mandatory calls:
sufficient current context may be reused. `requiredTools` and `forbiddenTools`, when set,
are executable assertions. Structural assessment checks completion, required/forbidden
calls, unauthorized actions and failed tool responses. It is not a semantic accuracy judge.
Manually inspect engineering claims, coverage limitations, ambiguity and durable readback.

The runner saves prompts, answers, function calls/results, completion state, usage and
metadata/prompt hashes. It never records credentials, HTTP headers or hidden thought text.
Keep artifacts local: tool results can contain proprietary CAD data. Transport failure
stops the run; there is no silent retry. Model settings remain those configured by the user.

Existing captures can be assessed without Gemini:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --assess-orchestration .tools/live-stress/runs/my-new-run/results.json Checks/OrchestrationCases.json .tools/live-stress/runs/my-new-run/assessment.json
```

This initial suite covers observed regressions and a small set of hostile/malformed inputs.
It does not establish general prompt-injection resistance, exhaustive exporter coverage,
large-assembly performance, Quest compatibility or deterministic model behavior.
