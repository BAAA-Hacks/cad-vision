# CADEN semantic tool contract 3.0

The desktop uses `SemanticQueryTools.Create(ProjectSnapshot, ProjectAssociation, ToolLimits?)` with the canonical loader.
The earlier `QueryTools`/`MetadataStore` API remains for compatibility checks; its four
legacy declarations are not exposed by the desktop. Do not mix the two registries in a chat.
Replacing metadata creates a new registry and conversation only after loading succeeds.
Invalid graph/hierarchy capability does not discard usable property metadata.

## Shared envelope and execution

```json
{
  "contractVersion": "3.0",
  "success": true,
  "projectId": "caden-project-id",
  "snapshotId": "sha256:...",
  "provenance": {
    "identityScope": "SnapshotOnly",
    "fixture": false,
    "sourceProjectId": "exporter-project-id",
    "sourceIdentityTrusted": false
  },
  "data": {},
  "coverage": {
    "status": "complete",
    "countUnit": "objects",
    "requestedCount": 10,
    "evaluatedCount": 10,
    "excludedUnknownCount": 0,
    "excludedSuppressedCount": 0,
    "reasonCodes": []
  },
  "pagination": null,
  "errors": []
}
```

Failures set success=false and data/coverage/pagination=null, with structured errors
containing code, message and details. Unexpected errors also carry correlationId linked
to the host's redacted stack-trace diagnostics. Success means execution succeeded, not
that engineering checks passed. Revalidation may successfully commit an UnableToEvaluate
result. Coverage describes evaluated subjects, independently of matching rows and pages.

Every query except get_model_summary requires its top-level projectId and snapshotId.
The host must supply an explicit ProjectAssociation; exporter identity stays provenance.
Wrong project and stale snapshot are rejected before retrieval. IDs are case-sensitive;
matching strings across exports do not establish persistent identity. Replacing metadata
resets chat history and session cursors. Missing model context returns CAPABILITY_UNAVAILABLE.

The registry validates declared shapes, enums, finite numbers, bounds and required fields;
unexpected fields are rejected. Handlers enforce cross-field rules. No scalar coercion.
Defaults: 64,000 input/response JSON characters, 1-32 detail IDs, 1-32 fields, 0-256 scope
IDs, page limit 1-50 (default 20), hierarchy depth 0-32. Host ToolLimits configures detail
IDs, scope IDs, page size, depth and response size. Oversized reads fail QUERY_TOO_LARGE.

Pagination is top-level {limit,total,nextCursor}. Total counts matching rows within the
requested scope/depth. Send nextCursor as cursor with the same query arguments, including
limit. Tokens bind the tool, normalized query, project, snapshot and relevant subsystem
revision. They are opaque, session-local and invalidated on registry replacement. Invalid
or mismatched tokens return INVALID_CURSOR; start a fresh query. There is no public offset.

Use ExecuteAsync with cancellation. Read cancellation returns CANCELLED and the host omits
the cancelled chat turn. IActionCadenTool defines future mutating handlers: they require
operationId, durable commit before success, and RecoverCommittedAsync validating the same
operation ID and request content without executing uncommitted work. A committed action
returns/replays its receipt despite cancellation or a recoverable post-commit exception:

    receipt: {operationId, subsystem: "memory" | "issues" | "view",
              revision, applied: true, replayed: false}

Replays preserve the original receipt and set replayed=true. Oversized optional action
results may be omitted while preserving the receipt. Revision/idempotency enforcement and
durable receipt storage belong to the action subsystem. No action is exposed by the current
query registry; these execution guarantees are checked using offline action doubles.

Public codes include INVALID_ARGUMENT, UNKNOWN_OBJECT_ID, STALE_SNAPSHOT_REFERENCE,
WRONG_PROJECT, CAPABILITY_UNAVAILABLE, UNIT_MISMATCH, QUERY_TOO_LARGE, MAX_DEPTH_EXCEEDED,
PERSISTENCE_FAILED, REVISION_CONFLICT, IDEMPOTENCY_CONFLICT, UNSUPPORTED_SCHEMA_VERSION,
CORRUPT_STORAGE, CANCELLED and INTERNAL_ERROR. UNKNOWN_TOOL and INVALID_CURSOR are extensions.
Unknown IDs reject a requested batch atomically.

## Tools

`get_model_summary {}` returns name, validated root ID when hierarchy is available,
occurrence counts, property/hierarchy/graph capability states and bounded diagnostic codes.
Capability states are Available/Unavailable/Invalid; they do not certify analysis coverage.
No issue/memory/runtime action tools are registered. Two [mechanical queries](MECHANICAL_CONTRACT.md)
are declared when explicit scoped graph data is available; the summary lists their scopes.

`get_object_details {projectId, snapshotId, objectIds, fields?}` returns contextual identity plus
availability-wrapped properties. Default fields: suppressed, fixed, material, mass,
constraintStatus. Identity includes ID/name/type, wrapped parentId, ancestor path and
childCount. Path/count are null when hierarchy is unusable; names are never merged.

Public detail fields: name, type, parentId, sourceDocument, configuration, partNumber,
description, suppressed, fixed, material, mass, volume, centerOfMass, inertia,
constraintStatus, remainingDOF, dimensions, referenceGeometry, customProperties.
`constraintStatus` maps internally to `definitionStatus`. Raw/export aliases are rejected.

Properties preserve status, value, reason, sourceField, units and coordinate frame where
applicable. Structural fields carry status/value/reason/sourceField/unit. Snapshot/project
provenance is shared in the envelope; sourceField identifies the original JSON field.
Available means usable under the loader contract, not independently verified. Missing or
invalid fields have null values. A validated root's available parentId=null means no parent.
Fixture engineering placeholders remain missing. Numeric zero and boolean false stay valid.
Details coverage describes retrieval of the requested objects, not engineering completeness;
inspect property status individually. Nested material density retains its separate status.

`find_objects` accepts exactly one of two modes:

```json
{"projectId":"...","snapshotId":"...","query":"R_0805","limit":20}
```

```json
{"projectId":"...","snapshotId":"...","property":"mass","operator":"greater_than",
 "value":{"number":2,"unit":"kg"},"scopeObjectIds":["A","B"]}
```

Query is an ordinal case-insensitive ID/name substring search: every whitespace-separated
word must match. Exact ID matches sort first, then case-insensitive name, then ordinal ID.
Property results use the same name/ID order. Duplicate occurrences remain distinct.

Searchable properties: name, type, parentId, sourceDocument, configuration, partNumber,
description, suppressed, fixed, mass, volume, constraintStatus, material.name,
material.assigned. Strings use ordinal case-sensitive equality. A confirmed null parent
has no comparable scalar value and is reported among unevaluated subjects; use hierarchy
to determine root containment. No null-equality or availability operator is exposed yet.

Operators: equals, not_equals, greater_than, greater_than_or_equal, less_than,
less_than_or_equal, in. Ordered comparisons require numeric fields. An operand contains
exactly one of text/number/boolean matching the property type. Only numbers accept unit,
and mass/volume require it. `in` uses `values` (1-32 typed operands) instead of `value`.

Mass units: kg, g, lb. Volume units: m^3, cm^3, mm^3, in^3, ft^3. Both exported values and
query operands are normalized before comparison. Equality compares normalized doubles
exactly; there is no hidden engineering tolerance. Nonfinite/overflowing operands are
errors; overflowing source normalization makes that subject unevaluable. Numeric strings
and unsupported units are rejected. Unknown/missing/invalid values never satisfy ordinary
comparisons, including not_equals.

Omitted or null scopeObjectIds means all occurrences; [] means no objects. Explicit scopes
are deduplicated exact ID sets, not subtrees. Search reports coverage.status complete/partial,
scopeCount, evaluatedCount, excludedUnknownCount and a bounded unknownObjectIds list with its own
truncation flag. excludedUnknownCount includes values lacking a comparable scalar. An empty match
page with partial coverage is not proof that no matches exist among unknown subjects.
Pagination does not change query evaluation coverage.

`query_hierarchy {projectId, snapshotId, objectId, direction, maxDepth?, limit?, cursor?}` supports
parent/children (depth exactly 1) and ancestors/descendants (depth 0-32, default 1). Start
object is excluded. Ancestors are nearest-first; descendants preserve exported child order
within each depth. Results report relative depth, with coverage.requestedMaxDepth and coverage.depthLimited independently
of pagination. Depth zero returns no relatives and reports whether deeper relatives exist.
Complete coverage applies to the bounded requested traversal, not every deeper descendant.
Invalid/unavailable hierarchy returns success=true, items=null and a coverage reason/state,
never a confirmed empty tree. Metadata hierarchy remains authoritative.

## Verification and next milestones

`ToolContractChecks` verifies envelope, host identity, cursor bindings and action recovery.
`SemanticQueryChecks` verifies snapshot guards, canonical property mapping, unit-aware
filters, unknown booleans/not_equals, exact scope, nested validation, hierarchy degradation,
pagination, cancellation and fake-HTTP Gemini dispatch. No live API call is required.
Legacy query checks remain to catch compatibility regressions.

Next: host-owned initial issue scans and issue read/revalidation/disposition tools,
then memory tool exposure and Unity actions. Scoped mechanical query tools are implemented.
Issue presentation/freshness and durable memory primitives exist; these capabilities are
not advertised as working tools before their implementation.
