# CADEN tool contract 1.0

This documents the legacy query API retained for compatibility checks. The desktop now
uses the [semantic tool contract 2.0](SEMANTIC_CONTRACT.md) and canonical ProjectSnapshot.

All tool infrastructure lives here. `query/` contains the immutable metadata snapshot,
property contract, and the four initial read-only query handlers. Later tool families
can register additional ICadenTool handlers without adding operations to the query layer.
The core receives JSON text from its host; it does not read files or Unity objects.

## Response envelopes

Success: `{ "contractVersion": "1.0", "ok": true, "data": { ... } }`.
Failure: `{ "contractVersion": "1.0", "ok": false, "error": { "code": "...", "message": "..." } }`.

Every successful query carries provenance with snapshotId, fixture, identityBasis,
and engineeringEvidence. A new store creates a new snapshot. The host must start a
new ChatSession when replacing metadata so prior IDs/evidence are not reused.

Errors: UNKNOWN_TOOL, INVALID_ARGUMENTS, MODEL_NOT_LOADED, OBJECT_NOT_FOUND,
RESULT_TOO_LARGE, TOOL_EXECUTION_FAILED. Loader failures use INVALID_METADATA.
Tool-input errors are returned to Gemini so it can correct a query. Unexpected
handler failures produce TOOL_EXECUTION_FAILED without raw exception details.

## Tools

| Name | Arguments | Result |
| --- | --- | --- |
| get_model_summary | none | Root/context, occurrence counts, provenance, property availability counts |
| search_objects | query; optional type, parent_id, limit, offset | Case-insensitive ID/name substring matching; every query word must match; distinct instances with paths |
| get_object | id; optional fields | Contextual identity and properties with availability wrappers |
| get_hierarchy | optional id, depth, limit, offset | Root context and a breadth-first flat descendant page with parent IDs and relative depth |

Search parent_id filters direct children, not a whole subtree. Search is lexical;
it cannot identify shape, engineering function, synonyms, or relationships not exported.
Search orders exact IDs first, then name and ID. Hierarchy preserves exported child order.
Context contains id, name, type, parentId, an ancestor path of {id,name} pairs including
the object itself, and childCount. Duplicate display names are never merged.

Page defaults: limit=20, offset=0. Limit range 1–50, offset 0–10000, depth 1–5.
Pages include total, truncated, nextOffset. Hierarchy separately reports
deeperLevelsOmitted; reaching a page's end does not mean all deeper descendants were visited.
No matches is a successful empty result, not OBJECT_NOT_FOUND.
Maximum response size: 64,000 JSON characters. Larger valid results return
RESULT_TOO_LARGE; narrow fields/page instead of silently cutting data.

## Property availability

Every known requested property has status, value, reason, sourceField, expectedFormat,
and unit. sourceField is a JSON pointer into the original snapshot (possibly pointing
to an absent field). Numeric strings are not coerced. Zero and false remain valid values.

| Status | Meaning |
| --- | --- |
| available | Present in the expected format, with sufficient semantic context; not independently verified |
| missing | Absent, null, unknown, no entries reported, fixture placeholder, or required semantic context missing |
| invalid | Present but wrong type/shape, nonfinite number, invalid range, unsupported unit, or inconsistent structure |
| not_applicable | Reserved for explicitly established inapplicability; current tools do not emit it |

Missing/invalid values are returned as null and must not support engineering conclusions.
The synthetic fixture flag makes all engineering properties missing, including its
material.assigned=false placeholders. Structural labels/counts remain available but
describe GLB occurrences and wrappers, not a verified physical BOM.

Supported property formats are implemented in the shared primitive MetadataPropertyRules.cs (via the PropertyContract adapter) and returned in
expectedFormat. Known fields: sourceDocument, configuration, partNumber, description,
suppressed, fixed, material, mass, volume, centerOfMass, inertia, definitionStatus,
remainingDOF, referenceGeometry, dimensions, customProperties. Unknown field requests
return INVALID_ARGUMENTS. Other JSON fields are retained internally, not interpreted.

Mass requires a declared project mass unit (kg/g/lb). Volume uses the cube of declared
project length units (mm/cm/m/in/ft); this follows schema 1.0's normalization rule.
Spatial properties are currently missing for reasoning if the coordinate frame is
unspecified; inertia also lacks a settled unit contract. No frame/unit is guessed.
Material assignment/name can be available while its nested density is null with
densityStatus/densityReason explaining missing units or missing data.
Custom properties currently require string values. Empty collections mean no entries
reported, with coverage unknown; no engineering pass/fail is inferred.

The schema 1.0 prototype validates unique IDs, root assembly, parent/child agreement,
reachability, cycles, types, and optional unique nonnegative glbNodeIndex mappings.
It does not verify those indices against a GLB. Size caps: 10 MB JSON, 10,000 objects,
128 hierarchy levels, 128-character IDs, 512-character names.

## Gemini integration

Explicit function declarations accompany requests. The registry independently validates
arguments rather than trusting model output. A turn permits up to 6 tool rounds and
16 calls under the configured turn timeout. There is no automatic network retry.
All model content parts, thought signatures, and function IDs are retained unchanged
for continuation and later turns. Tool responses are structured user-role content.
Only the final text is shown in the desktop transcript. Failed/cancelled/limited turns
are not committed to conversation history. Read-only query execution has no model side effects.

The shared model-facing rules live in `CADEN/prompts/system.md`. Update this contract,
the property validators/tool schemas, and the prompt together if semantics change.
