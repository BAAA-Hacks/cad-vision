# Memory tools

The host opens MemoryAccess with its explicit project association, immutable snapshot and
durable storage adapter, then supplies it to SemanticQueryTools.Create. Desktop uses
data/memory.caden.json, ignored by Git. A corrupt/wrong-project sidecar disables memory
tools and reports a diagnostic; it is never replaced with empty data.

Both tools require projectId and snapshotId under response contract 3.0.

get_project_memory accepts objectIds, keys, types, kind (memory/requirement),
includeProjectScope (default true), includeStale/includeRetired (default false), limit and
cursor. Omitted objectIds means project scope only; IDs are exact, never descendant-expanded.
For a known exact key with unknown attachment, allObjectScopes=true searches stored object
references without discovering every CAD object. It requires nonempty keys and cannot be
combined with objectIds. Lifecycle/stale filters and bounded pagination still apply.
Unknown IDs can be used to inspect historical references with includeStale. Requirements
match any requested object and retain their full reference scope. Their key is their ID.
Coverage counts stored records in the selected scope before filters; it is not proof of
complete design knowledge. Cursors bind exact query, project, snapshot and memory revision.

write_project_memory requires targetObjectIds, kind, type, key, operationId, expectedRevision
and exactly one of value (text) or valueJson (encoded structured JSON). Optional context
stores the value as {content, context}. Empty targetObjectIds explicitly means project scope.
For kind=memory, one record is upserted in each (object, type, key) slot, atomically. For
kind=requirement, key is its stable requirement ID and all targets form one requirement.
Lifecycle defaults to Active; Retired preserves history. Null is never deletion.

The trusted adapter fixes Gemini provenance to AssistantInferred and actor to gemini;
arguments cannot elevate authority or override UserEstablished entries. A future explicit
host authorization workflow is needed to assign user-established provenance. Source metadata
and issue disposition are never changed by these tools.

MemoryAccess stages primitive mutations, then commits their combined document plus one
public receipt in a single durable write before publishing state. Validation, cancellation
before commit, or persistence failure publishes none of the batch. After commit, cancellation
cannot hide the receipt. Exact retries recover the original receipt after restart;
different arguments with the same operationId fail. The public memory revision advances once
per batch; internal primitive revisions may advance for each record.

The tool sidecar wraps the existing primitive document and receipts; it is not a bare
ProjectMemoryStore sidecar and does not silently migrate one. Limits remain 10 MB, 10,000
tool operations, 32 target IDs, bounded JSON values and response pages. Historical content
and receipts are retained, not returned wholesale to Gemini. Cross-snapshot references use
the primitive's stable-identity guarantees; filenames never establish project identity.

MemoryToolChecks covers multi-object atomicity, rollback, durable replay, cancellation,
requirements, retirement, stale references and mate lookup. Tests require no live Gemini.
