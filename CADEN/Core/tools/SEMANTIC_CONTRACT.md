# CADEN semantic tool contract 2.0

The desktop uses `SemanticQueryTools.Create(ProjectSnapshot)` with the canonical loader.
The earlier `QueryTools`/`MetadataStore` API remains for compatibility checks; its four
legacy declarations are not exposed by the desktop. Do not mix the two registries in a chat.
Replacing metadata creates a new registry and conversation only after loading succeeds.
Invalid graph/hierarchy capability does not discard usable property metadata.

## Shared envelope and execution

```json
{
  "contractVersion": "2.0",
  "ok": true,
  "context": {
    "projectId": "P",
    "snapshotId": "sha256:...",
    "identityScope": "SnapshotOnly",
    "fixture": false
  },
  "data": {}
}
```

Failures replace `data` with `error: {code,message}` and retain context. With no model,
context fields are null. `ok=true` means a query executed, not an engineering pass.
All queries except `get_model_summary` require `snapshotId` from the summary's context.
A mismatch returns STALE_SNAPSHOT before retrieving objects. IDs are case-sensitive;
matching strings across exports do not establish persistent identity. Snapshot replacement
also resets chat history. Project identity is bound by the snapshot and registry.

The registry recursively validates declared object/array/scalar shapes, enums, numeric
bounds, finite numbers and required fields; unexpected fields are rejected. Handlers
enforce cross-field rules. No strings are coerced into numbers or booleans.
Inputs and responses are capped at 64,000 JSON characters; oversized results return
RESULT_TOO_LARGE rather than silently dropping data. Details accept 1-32 object IDs,
1-32 explicit fields; search scopes accept 0-256 IDs. Pagination uses limit 1-50 (default
20), offset 0-10000 (default 0), total, truncated, nextOffset. Total counts matching rows
within the requested scope/depth, not the whole assembly regardless of limits.

Execution uses `ExecuteAsync` and a cancellation token through the Gemini client.
Cancellation propagates without committing a chat turn. The synchronous Execute adapter
is retained for legacy callers; hosts should use async execution. Tools in this milestone
are read-only. Action idempotency/receipts will be implemented before any action is exposed.

Errors include UNKNOWN_TOOL, INVALID_ARGUMENTS, MODEL_NOT_LOADED, STALE_SNAPSHOT,
OBJECT_NOT_FOUND, RESULT_TOO_LARGE and TOOL_EXECUTION_FAILED. Unexpected exceptions do
not expose raw details. Unknown IDs in a requested batch reject the entire request.

## Tools

`get_model_summary {}` returns name, validated root ID when hierarchy is available,
occurrence counts, property/hierarchy/graph capability states and bounded diagnostic codes.
Capability states are Available/Unavailable/Invalid; they do not certify analysis coverage.
No issue/mechanical/memory/runtime tools are registered by this milestone.

`get_object_details {snapshotId, objectIds, fields?}` returns contextual identity plus
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
{"snapshotId":"...","query":"R_0805","limit":20}
```

```json
{"snapshotId":"...","property":"mass","operator":"greater_than",
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
are deduplicated exact ID sets, not subtrees. Search reports coverage.status Complete/Partial,
scopeCount, evaluatedCount, unknownCount and a bounded unknownObjectIds list with its own
truncation flag. UnknownCount includes values lacking a comparable scalar. An empty match
page with Partial coverage is not proof that no matches exist among unknown subjects.
Pagination does not change query evaluation coverage.

`query_hierarchy {snapshotId, objectId, direction, maxDepth?, limit?, offset?}` supports
parent/children (depth exactly 1) and ancestors/descendants (depth 0-32, default 1). Start
object is excluded. Ancestors are nearest-first; descendants preserve exported child order
within each depth. Results report relative depth, maxDepth and depthLimited independently
of pagination. Depth zero returns no relatives and reports whether deeper relatives exist.
Complete coverage applies to the bounded requested traversal, not every deeper descendant.
Invalid/unavailable hierarchy returns ok=true, items=null and a coverage reason/state,
never a confirmed empty tree. Metadata hierarchy remains authoritative.

## Verification and next milestones

`SemanticQueryChecks` verifies snapshot guards, canonical property mapping, unit-aware
filters, unknown booleans/not_equals, exact scope, nested validation, hierarchy degradation,
pagination, cancellation and fake-HTTP Gemini dispatch. No live API call is required.
Legacy query checks remain to catch compatibility regressions.

Next: host-owned initial issue scans and issue read/revalidation/disposition tools, followed
by mechanical tools when coverage is trustworthy, then persistent memory and Unity actions.
Issue presentation/freshness and action idempotency contracts remain as agreed; they are
not advertised as working tools before their implementation.
