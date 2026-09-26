# Bounded local precompute and performance

This pass preserves existing tool declarations, JSON evidence and numerical semantics.
No Gemini calls or model-based summaries are used during precompute.

Implemented:

- Snapshot-owned parent/child tables, reusable ID/name search strings, and incident-mate indexes.
- Optional assembly scope closures under startup-time and retained-membership budgets.
  Missing cache entries use the same indexed hierarchy traversal, with cancellation.
- Connection queries reuse those indexes instead of cloning every object's raw JSON and
  scanning all mates. Mate reads use incident IDs, preserving deduplication and ordering.
- Per-graph confirmed-active sorted adjacency, connected components and island membership.
  Unbounded unscoped disconnected queries can short-circuit, but still require complete
  membership/mate/suppression evidence before reporting ConfirmedDisconnected. Scoped and
  depth-limited paths retain BFS and existing uncertainty semantics.
- Cached declaration/schema construction, with availability-set invalidation and defensive
  copies. Cached immutable model-summary body and bounded property DTOs.
- Issue-summary DTOs keyed by request/scope and committed issue revision. A failed durable
  mutation cannot publish or invalidate the committed summary. Existing issue and memory
  indexes remain the sources of truth.
- Per-tool local performance logging and an offline benchmark mode.

## Budgets and fallback

The host supplies ProjectLoadOptions.Precompute (Core contains no environment access):

| Option | Default | Meaning |
|---|---:|---|
| MaxScopeMemberships | 100000 | Maximum duplicated member references in cached assembly closures |
| OptionalStartupMilliseconds | 100 | Stop optional closure construction after this elapsed index-build budget |
| MaxDtoCacheCharacters | 250000 | Retained serialized-character budget per property/issue-summary cache |

DTO caches also have a 128-entry FIFO limit. Setting the respective budget to zero disables
that optional cache. These are retained-data bounds, not total process RAM limits; canonical
metadata, essential linear indexes, JSON object overhead and transient serialization use
additional memory. The startup budget does not cancel essential validation/index construction.
Options are copied into snapshot state where needed; changing the caller's options later
does not reconfigure a loaded snapshot.

No all-source routing tables were added: measured path dispatch is already below 1 ms for
these fixtures. No new material/physical aggregation API, derived COM/inertia, concurrent
scope execution or arbitrary-query memoization was introduced. Existing calculations and
read/mutation coordination remain authoritative. Material/aggregate and additional inverted
indexes can be added when an actual measured query needs them, rather than allocating unused
caches. The fixed Gemini startup payload has not changed.

## Logs

Desktop appends performance-YYYY-MM-DD.jsonl beside usage logs under
%LOCALAPPDATA%/CADEN/logs. Records contain tool name, total/queue/execution duration, measured
serialization duration, response UTF-8 bytes, argument-field count, returned item count,
active-scope object count and instrumented DTO/scope cache hits/misses. No argument values,
property values, prompts or credentials are logged. Sink failures go through DiagnosticLog.

Execution duration includes validation, handler work, envelope construction and its size
check. measurementSerializationDurationMs is an additional serialization used to measure
response size, not total serialization throughout the request. primitiveDurationMs remains
null because it is not separately measured. cacheHit is null when no instrumented cache was
consulted; it otherwise means at least one cache hit. Logging overhead is excluded from
totalDurationMs. A cold/full first-token AI timeline requires separate transport work;
the current transport is non-streaming.

## Verification and benchmark

PrecomputeChecks verifies budget fallback equivalence, scope boundaries, caller-mutation
isolation, issue-summary commit/rollback freshness and telemetry failure isolation.
Existing graph, cancellation, scope, property and Gemini fake-HTTP checks also pass.

From the repository root:

    CADEN/.tools/dotnet/dotnet.exe CADEN/Checks/bin/Debug/net10.0/Checks.dll --benchmark <metadata.json> <output.json>

This command is offline. It loads isolated in-memory issue/memory stores, performs 10 warmup
calls and 100 measured calls per operation, and includes response serialization. Reported
allocations are process allocation estimates per call. Builds are Debug and measurements
are Windows-host results, not Quest guarantees or end-to-end Gemini speedups.

Baseline: commit ee5cfb2, built in an isolated archive without changing the working tree.
Final sequential real-export comparison (261 occurrences):

| Tool | Before mean ms | After mean ms | After p95 ms |
|---|---:|---:|---:|
| find_connections | 58.10 | 1.71 | 3.36 |
| set_scope | 10.86 | 0.082 | 0.099 |
| get_issue_summary | 16.62 | 0.83 | 1.79 |
| find_objects | 0.69 | 0.52 | 0.70 |
| get_object_details | 0.28 | 0.16 | 0.25 |
| get_model_summary | 2.01 | 1.58 | 3.85 |
| find_mechanical_path | 0.26 | 0.19 | 0.21 |
| get_mates | 0.27 | 0.58 | 0.80 |

The small two-mate read did not improve in this run; both versions remain below 1 ms.
Connection-call allocations dropped from approximately 12.55 MB to 0.785 MB. Response
byte lengths stayed identical for all measured queries. One cold-load sample increased
from 0.76 s to 1.08 s; cold/JIT timing is noisy and this is not a statistically established
startup regression measurement. Warm results likewise are indicative local measurements.
Raw artifacts: .tools/precompute-benchmark/before-real-final.json and after-real-final.json.
