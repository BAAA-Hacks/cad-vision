# Project memory primitive

`ProjectMemoryStore` implements the seven V1 operations: GetProjectMemory,
GetObjectMemory, GetRequirements, GetIssueDisposition, UpsertMemory, UpsertRequirement,
and SetIssueDisposition. It owns CADEN knowledge only; it never edits imported metadata
or persists generated issue findings. The [memory tool adapter](../../../tools/MEMORY_CONTRACT.md)
now connects reads/writes to Gemini and desktop storage using the host's explicit association.

## Host binding and storage

The host supplies a durable CADEN `ProjectAssociation`, a canonical snapshot, and an
`IMemoryPersistence` adapter. A CADEN project ID should be generated once and retained by
the host's project catalog/workspace. A filename is a storage location, not project identity.
Selecting the association is an explicit host responsibility; this primitive does not
automatically discover which CADEN project an export belongs to.

An optional trusted external identity consists of SourceSystem plus
TrustedSourceProjectId. If supplied, the source ID must match the export's ProjectId and
the stored association must match exactly on reopen. Without that guarantee, exporter
project IDs are not used as durable CADEN identity. Changing/removing an existing trusted
external binding requires a future explicit association-migration operation.

```csharp
// The host owns both this durable ID and the path. Never derive the association from a filename.
var association = new ProjectAssociation(cadenProjectId);
// Desktop.Persistence.ProjectMemoryFile is a host adapter, not part of Core.
var storage = new ProjectMemoryFile(sidecarPath); // must end in .caden.json

// Regenerate findings before binding if dispositions should be restored now.
var issues = new IssueStore(snapshot);
await InitialIssueCheckers.CreateEngine().ScanAsync(issues);
var opened = ProjectMemoryStore.Open(storage, association, snapshot, issues);
if (!opened.Success) { /* Show ErrorCode/Message; never substitute an empty store. */ return; }
var memory = opened.Value!;

// Context is created by trusted host code, not deserialized from Gemini's arguments.
var context = new MemoryWriteContext(operationId, memory.Revision,
    MemoryProvenance.UserEstablished, actor: userId);
var result = memory.UpsertMemory(new MemoryMutation("intended_dof", "rotation",
    new JValue("This shaft is intended to rotate."), objectId), context);
if (!result.Success) { /* Report the failure; do not claim it was remembered. */ }
```

Opening a confirmed missing sidecar initializes an empty runtime store. It is written on
the first successful mutation. A malformed, unreadable, duplicate-key, unsupported-schema,
or wrong-project sidecar never becomes an empty store and is never overwritten by Open.
Reads and Open do not rewrite sidecar contents. `lastKnownSnapshotId` records the snapshot
of the last successfully persisted mutation/reconciliation; it is not proof that IDs are
stable and does not advance merely because another snapshot was opened.

The desktop adapter uses a same-directory temporary file, write-through/Flush(true), and
atomic replacement (or first-file move). A lock file serializes cooperating processes;
exact-content comparison rejects external changes with STORAGE_CONFLICT. The `.lock` file
may remain empty on disk; the open exclusive handle is the actual lock. An adapter must
throw before commit on failure and must not throw a false failure after a successful commit.
There is no non-atomic delete-and-rewrite fallback.

Core has no path/filesystem/Unity dependency. Storage operations are synchronous under
store locks; the host should run them off its UI thread. There is no cancellable interval
between durable commit and runtime publication. The Windows adapter is tested locally;
Quest filesystem/replacement/power-loss durability behavior still needs device validation.
Unity must supply an appropriate writable host path and tested adapter. No CAD source
files are used as sidecars.

## Reads and record structure

Records include stable record ID, type/value, lifecycle, provenance, timestamps, revision,
originSnapshotId and references. Memory also has key and nullable objectId. Requirements
use their caller-supplied stable requirementId as `id`; empty references mean project scope.
Values preserve structured JSON and units as supplied. The primitive validates finite JSON,
not the engineering meaning of a requirement or unit conversion; semantic tools/checkers
must enforce their own requirement schemas.

GetProjectMemory returns only project memory. GetObjectMemory uses exactly the requested
object IDs, without subtree expansion. Filter omission means unrestricted; an empty type,
key or object-ID set matches nothing. Types and keys are separate: use
`types=["intended_dof"], keys=["rotation"]` for the example above. All identifiers and filters
are ordinal case-sensitive.

GetRequirements with no object scope returns only project requirements when
includeProjectScope=true. With an object scope, it returns requirements referencing **any**
requested object, preserving the full requirement scope. Project requirements are included
independently according to that flag. A multi-object requirement with any stale/untrusted
reference is excluded by default, even when the queried object itself is still valid.
Use includeStale to inspect it explicitly.

Reads return MemoryPage with copied JSON records, Total, Offset, NextOffset. Defaults are
50 records; limits are 1-100. Object/requirement scope indexes are built once per committed
state; reads never reparse the sidecar. Type/key filters operate over those scoped records.
Returned JSON is detached from internal state. Stale and retired records require their
separate includeStale/includeRetired flags. Confirmed empty results apply to those filters,
not to hidden stale/retired records. Unknown requested IDs are not remapped.

GetIssueDisposition returns a copied persisted judgment or null for confirmed absence in
a successfully opened store. It adds applicability: Applicable, EvidenceChanged,
Historical (finding absent), StaleReference, or NotEvaluated (no bound IssueStore).
Historical acceptance is not current acceptance. References expose their detailed validity.

## Identity, lifecycle and provenance

Each reference stores object/mate ID, origin snapshot and the host's identity guarantee at
creation. Its current computed referenceState is separate from record lifecycle:

- Active: ID exists and either snapshot is unchanged or both origin/current identities
  are trustworthy across snapshots.
- StaleReference: referenced entity is missing from the current snapshot.
- UntrustedIdentity: the ID string exists but cross-snapshot identity is not guaranteed.

Components require ProjectStable on both sides. Fixtures remain snapshot-only. Mate
references additionally require the explicit `mateIdsStableAcrossSnapshots` host guarantee
on both sides. No filename, name, part number, hierarchy path or similarity remapping occurs.
Project-scoped memory follows the explicit CADEN association without requiring object IDs.

UpsertMemory's logical slot is the canonical tuple (objectId-or-project, type, key).
Updating it keeps its record ID and creation provenance in history. UpsertRequirement
uses its stable ID; an explicitly supplied new scope is not inferred from old scope.
Both support Active/Retired, retain previous versions in history, and reject null as a
deletion/value. Retiring a stale record is allowed; reactivating or updating stale references
as Active is rejected with STALE_REFERENCE. Explicit reference reconciliation is deferred.

Provenance is UserEstablished, AssistantInferred, Imported or System, plus a host actor.
Non-user writes cannot overwrite user-established records without a host-issued override.
This also protects requirement retirement and persisted disposition edits. The model must
never supply its own trusted write context/override. Memory writes never change issue
disposition. User intent remains distinct from measured source CAD facts.

## Durable mutations and disposition coordination

Mutations validate expected store revision, prepare the new document/indexes/history and
receipt, commit storage, then publish runtime state. Failure leaves the previous committed
runtime state intact. All seven operations are synchronized within a store. Faults return
traceable diagnostics through the existing DiagnosticLog; expected conflicts return codes.

Every write requires an operation ID and expected store revision. Receipts are persisted.
An exact retry (same content, actor/provenance/override, expected revision and snapshot)
returns the original record ID/revision with Replayed=true, without another save/history
entry. Reusing an operation ID for different content/context returns IDEMPOTENCY_CONFLICT.
A replay acknowledges the original commit, not that the record has remained unchanged
since then. Current-state queries remain necessary before making a present-tense claim.

SetIssueDisposition requires a bound IssueStore, existing finding and matching evidence
hash. It holds the issue-store lock across storage commit, then updates runtime disposition
and indexes. A failed save changes neither memory nor runtime acceptance. Use this operation
instead of the volatile IssueStore setter for persistent user decisions. Open clears the
accepted hash; the evaluated hash is retained separately to trace what was reviewed.

On Open, eligible matching dispositions can restore into freshly generated findings only
when identity and evidence match and no runtime user decision has intervened. Changed
evidence remains Open under normal issue-store rules; absent findings keep historical
records. Runtime revalidation never erases persisted historical judgments. A persisted
Open decision has no accepted evidence. Origin acceptance timestamps/snapshot remain intact.

Sidecar additions beyond the suggested shape: root revision, mutation receipts, and
requirementChanges history. History records before/after state; no event-sourced runtime
is required. V1 bounds are 10 MB sidecar, 32 KB per value, 10,000 committed mutations and
bounded nesting. Reaching capacity rejects writes; it never silently evicts history or
idempotency protection. Archival/migration is future work.

## Verification and remaining integration

MemoryChecks verifies disk and in-memory failure rollback, revision/idempotency conflicts,
authority protection, retirement/reactivation, exact selective scopes, multi-reference
validity, fixture safeguards, disposition restoration/evidence changes, corruption,
actual file replacement and competing-writer rejection. Tests use temporary sidecars and
synthetic snapshots, without Gemini.

Desktop memory wiring and tool exposure live in the separate MemoryAccess adapter. Issue
disposition actions use IssueAccess and its own journal. Project selection/association UI
remains deferred. Never expose the full sidecar or its mutation history to Gemini.
