# Active query scope — first implementation

The semantic registry now has 24 tools (including set_scope, clear_scope, get_scope and find_connections).
The Gemini host adds recall_result, giving 25 functions when all capabilities are available.
Scope controls require the existing projectId/snapshotId identity contract. set_scope also
requires an exact objectId; use discovery to resolve names without guessing.

## Scope contract

- Immutable membership: selected object plus all authoritative descendants, resolved once.
  Structurally suppressed occurrences remain members. Invalid/unavailable hierarchy fails
  resolution; failure/cancellation retains the previously selected scope.
- Session-local scope ID, monotonic revision and project/snapshot identity. No durable
  write or operationId is needed for navigation. New chat/reload resets scope.
- No active scope preserves existing query defaults. Assembly scope does not replace the
  source snapshot, coordinate frame, mechanical configuration or export coverage.
- Scoped collection filtering happens before pagination/aggregation. Explicit object targets
  outside the active scope fail OUT_OF_SCOPE; explicit external mates do likewise.
- Responses carry current scope metadata, including failures. get_model_summary is labelled
  global_discovery. Replayed durable receipts are explicitly historical, not reexecuted
  under the current scope. No receipt replay implies that the current scope was its original scope.
  New issue commits also persist scopeAtCommit; replay retains it. Older receipts may lack it.
- Capability discovery includes activeScopeSupport. Newly added, unmigrated tools fail closed
  under active scope until they explicitly adopt the policy; they cannot silently answer globally.
- Query cursors bind scope revision as well as their existing arguments/identity/revisions.
  Changing, clearing or resetting scope invalidates affected cursors, even if membership
  later happens to be identical.
- A registry serializes scope transitions and tool execution so a scope change cannot race
  with a query and mislabel its response. Session recall preserves original scope metadata.

## Tool behavior

| Tools | Active-scope behavior |
| --- | --- |
| find_objects | Search/filter only members; coverage over scope before property filtering |
| get_object_details | Explicit IDs must be members; original property values/frames retained |
| query_hierarchy | Navigate within membership; parent/ancestor boundary explicitly labelled |
| get_mates | objectIds may be omitted to use active membership; internal + boundary mates, labelled; external mates excluded |
| get_mechanical_neighborhood / find_mechanical_path | Intersect traversal with active membership; still require exported mechanical scope/configuration; no leaving/re-entering via outside objects |
| Six issue reads | Findings touching scoped objects or incident mates; full evidence retained, outside object participants labelled; scoped evaluation coverage before presentation filters |
| Issue actions | Explicit scoped target guards; established checker/precedence-group semantics remain unchanged, including related finding subjects |
| Memory tools / diagnostic tools | SCOPE_NOT_SUPPORTED while active; explicit clear_scope restores original behavior |
| get_model_summary | Explicitly global model/capability discovery, alongside current scope label |
| recall_result | Historical cached evidence with original scope; not a fresh scoped query |

get_mates still requires objectIds when no scope is active. Mate enumeration does not prove
export completeness. Graph queries retain original source coverage; restricted searches
cannot claim global disconnection or global shortest-path completeness. External traversal
is deferred: clear/change scope explicitly to request the wider model.

Scope does not add an engineering aggregator. Assembly-reported mass, leaf-mass aggregation,
and combined COM/inertia are distinct operations. New aggregate tools remain separate work,
requiring suppression/completeness rules and compatible transforms/frames. Likewise no
interference calculation, selection-set scope, or duplicate assembly-specific API was added.

## Tests and mixed question suite

ActiveScopeChecks covers nested and single-part scope, pre-pagination filtering, preserved
issue coverage, boundary mates, a path that would have to leave/re-enter scope, exact-target
and mutation guards, unsupported tools, cursor invalidation and reset behavior. Existing
unscoped tests continue to run. All these checks are offline.

The real two-part export used previously has no subassemblies. Its original 25 prompts are
preserved in `Checks/OrchestrationCases.PartsBaseline.json`. Historical captures must be
assessed with their matching cases, not the revised suite.

The revised standard `Checks/OrchestrationCases.json` contains 25 mixed questions, paired
with `Checks/Fixtures/scope_assembly_metadata.json`. This is explicitly SYNTHETIC metadata,
not new SolidWorks evidence. It has 11 objects: root, four nested/peer assemblies, six part
occurrences, repeated Bolt names, a suppressed branch, and internal/boundary mate edges.

The questions cover assembly/subassembly membership, leaf-versus-assembly mass accounting,
part properties, scope changes, boundary connectivity, intentional OUT_OF_SCOPE failure,
suppression, unavailable analysis and persistent memory/disposition workflows. Case 22
starts a new chat. Expected error codes are asserted separately from unexpected failures.

An explicitly authorized live run from CADEN can use:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --live --orchestration .tools/live-stress/runs/new-scope-run Checks/Fixtures/scope_assembly_metadata.json . Checks/OrchestrationCases.json
```

The runner now also records mean, median and slowest end-to-end turn times, including all
Gemini requests/tool rounds. No live Gemini run was performed for this implementation.
