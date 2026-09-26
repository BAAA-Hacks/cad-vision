# CADEN Gemini Tool Declarations — Implemented Non-Unity V1.1

This document freezes the **current Gemini-facing implementation**, not a proposed second
API. Revision 1.1 adds two diagnostic reads and keyed cross-object memory lookup. All 20 semantic tools and the Gemini function-call loop are implemented. Runtime/Unity
tools remain excluded. A tool is advertised only when its backing capability is usable.

The exact declarations below were exported offline from `SemanticQueryTools.Create` with
default `ToolLimits` and all backing capabilities initialized. The companion
[JSON declaration array](CADEN_Gemini_Tool_Declarations_V1.json) is the same array, ordered
for documentation. Its parameter schemas and description strings are copied from the
runtime registry rather than rewritten. Production sessions must still use the dynamic
registry: **do not send this full catalogue unconditionally**.

Each declaration is executable by CADEN's current dispatcher, subject to its capability,
identity and semantic validation. The parameter schema alone does not express every
cross-field rule; those additional rules are part of this contract and documented below.
Defaults described in prose are applied by handlers, not necessarily emitted as JSON
`default` keywords. The emitted schemas use the current `nullable` convention and do not
claim to be standalone, exhaustive JSON Schema validation documents.

## Implemented surface

| Group | Tools |
| --- | --- |
| Model / metadata | get_model_summary, get_object_details, find_objects, query_hierarchy |
| Mechanical | get_mates, get_mechanical_neighborhood, find_mechanical_path |
| Issues | get_issue, list_issues, get_issues_for_object, get_issues_for_mate, get_issue_summary, get_issue_evidence, revalidate_issue, revalidate_object_issues, set_issue_disposition |
| Memory | get_project_memory, write_project_memory |
| Diagnostics | get_diagnostic_summary, get_diagnostics |

No aliases are implied. `searchText`, `fromObjectId`, `toObjectId`, `presentedOnly`,
disposition `state`, memory `scope`, and `includeRequirements` are not current parameters.
There is no generic `get_issues` tool.

## Current ownership and validation

All tools except `get_model_summary` require explicit **projectId and snapshotId in Gemini's
arguments**, including get_issue_summary. Obtain them from host startup context or get_model_summary and copy
exactly. CADEN validates them against the host association and loaded snapshot. Object,
mate and issue IDs must come from the corresponding snapshot, never guessed or remapped.

All four actions (revalidate_issue, revalidate_object_issues, set_issue_disposition,
write_project_memory) require **operationId and expectedRevision** in Gemini's arguments.
Issue actions use the `issues` revision returned by issue reads; memory writes use the
`memory` revision returned by get_project_memory. These revisions are independent.
set_issue_disposition also requires the expectedEvidenceHash from the reviewed finding.

The host already owns the durable project association, provenance authority, correlation
IDs, cancellation, storage, capability selection and cursor validation. Gemini cannot
assign UserEstablished provenance or override protected memory records.

Unknown argument fields are rejected at every declared object level, even though the
emitted schemas do not include `additionalProperties: false`. Null is rejected unless the
particular schema explicitly permits it. Identifiers and ordinary non-enum strings are
bounded to 1–512 characters; domain operations may additionally reject blank strings.
Numeric arguments must be finite. Do not send numbers as strings.

## Default bounds and pagination

This freeze records default limits, not every possible host override. The runtime-emitted
declaration is authoritative if a host deliberately configures other supported limits.

| Bound | Default |
| --- | --- |
| Object ID request arrays | 32 |
| find_objects.scopeObjectIds | 256 |
| Requested detail fields / membership operands | 32 |
| Memory object IDs, keys and types | 32 (fixed by memory declarations) |
| Paged result limit | 1–50; default 20 |
| Hierarchy depth / explicit mechanical maxHops | 0–32 |
| expectedRevision | Integer 0–9,007,199,254,740,991 |
| Serialized argument length | At most 64,000 characters |
| Recursive schema validation depth | At most 12 |
| Serialized response length | Default 64,000 characters |
| Session cursor capacity | 4,096 |

Paged tools are find_objects, query_hierarchy, get_mates, list_issues,
get_issues_for_object, get_issues_for_mate, get_diagnostics and get_project_memory. They return top-level
pagination with limit, total and nextCursor. Resume using the original query arguments
and `cursor=nextCursor`; do not pass an offset or a field named nextCursor. Null nextCursor
means the last page; omit cursor rather than sending null. Filtering and paging do not
improve underlying evaluation coverage.

Cursors bind the query and project/snapshot; issue and memory cursors additionally bind
their subsystem revision. Cursors are session-local. Reload or revision changes can require
restarting a query. Details and graph traversal tools are not paginated: request a smaller
scope/bound if the response exceeds the configured limit. Oversized action responses retain
their committed receipt while optional details may be omitted.

## Response envelope and action guarantees

All semantic responses use contractVersion="3.0". They contain success, projectId,
snapshotId, provenance, data, coverage, pagination and errors. Unused coverage/pagination
may be null. Errors contain code, message and details; unexpected errors include a
correlationId. Identity provenance distinguishes the source export ID from the host's
CADEN project association.

Successful actions include a top-level receipt containing exactly operationId, subsystem,
revision, applied=true and replayed. A replay returns the original receipt/revision with
replayed=true, not an assertion about the latest state. Exact retries retain the original
arguments, operationId and expectedRevision. Different content under the same operationId
is rejected. A new intentional action requires a new operationId.

Persistence must commit before success/runtime publication. Persistence failure leaves
previously committed state intact. Cancellation after commit cannot turn a committed
action into an uncommitted one: the tool layer returns or recovers its receipt. The chat
loop may stop before sending that receipt back to Gemini after cancellation, so durable
receipt recovery remains distinct from visible conversation history.

Important codes include CAPABILITY_UNAVAILABLE, INVALID_ARGUMENT, UNKNOWN_TOOL,
UNKNOWN_OBJECT_ID, UNKNOWN_MATE_ID, UNKNOWN_ISSUE_ID, WRONG_PROJECT,
STALE_SNAPSHOT_REFERENCE, INVALID_CURSOR, UNIT_MISMATCH, QUERY_TOO_LARGE,
MAX_DEPTH_EXCEEDED, REVISION_CONFLICT, IDEMPOTENCY_CONFLICT, EVIDENCE_CHANGED,
AUTHORITY_CONFLICT, STALE_REFERENCE, PERSISTENCE_FAILED, CANCELLED and INTERNAL_ERROR.
Subsystem validation can return additional specific codes; this list is not exhaustive.

## Capability discovery and recovery

get_model_summary remains callable with no loaded metadata. It reports modelLoaded and
toolCapabilities for all 20 tools, including tools absent from the current declarations.
Entries contain tool, state, usable, reasonCode, retryable, recovery and alternativeTools;
host initialization failures may include a correlationId. Do not infer complete evidence
from usable=true.

- Object queries require a loaded snapshot; hierarchy additionally needs usable metadata hierarchy.
- get_mates requires usable exported mate records or supported explicit zero-mate scope evidence.
  It does **not** require usable traversal. Synthetic fixtures are not mate evidence.
- Neighborhood/path require an available scoped graph. A specific request also needs a
  usable scopeAssemblyId/configuration pair; CADEN does not guess between configurations.
- Issue tools require successful host issue initialization; reads can still report partial
  checker coverage. Memory tools require successful host memory initialization.

Known unavailable calls return success=false with CAPABILITY_UNAVAILABLE and detailed
reason/recovery fields in errors[].details. Truly unknown names return UNKNOWN_TOOL.
Examples include MODEL_NOT_LOADED, MECHANICAL_SCOPE_MISSING, MECHANICAL_SCOPE_INVALID,
MATE_COVERAGE_MISSING, OCCURRENCE_MEMBERSHIP_MISSING, HIERARCHY_INVALID,
MEMORY_STORAGE_INVALID and ISSUES_NOT_INITIALIZED. retryable=false means the requirement
must change before retrying; do not loop or automatically reset storage.

Available alternatives answer narrower questions: get_mates does not establish a path,
rigidity or a functioning constraint. Preserve successful partial evidence with its
limitations. An unavailable analysis is never an empty list proving no issues.
See [capability details](../Core/tools/CAPABILITIES.md).

## Existing function-call loop

The current client uses Gemini REST through HttpClient and the existing registry; an SDK
migration is not part of this freeze. It sends currently available functionDeclarations,
dispatches returned calls locally, returns contract-3.0 function responses, preserves call
IDs/model continuation parts, and continues until an answer. The current per-turn bounds
are six tool rounds and sixteen tool calls; calls within a model response execute in order.
No automatic live API retry was added by this document.

## Deferred host-orchestration migration

Moving project/snapshot/operation/revision fields out of Gemini-visible schemas is a
**separate future change**. Until an adapter exists, the schemas below retain those fields.
That adapter must preserve the internal validation contract: bind calls to the originating
snapshot, retain operation identity and expected revision across retries, and never
silently substitute the latest revision to bypass conflicts. The host does not treat a
new retry as a new user action or rebind stale IDs to another export. Evidence hashes remain
tied to the evidence actually reviewed. Scope can only be injected when unambiguous.

The future migration must update adapter, declarations, prompt, checks and documentation
together. Keep memory writes AssistantInferred unless the host explicitly authorizes
user-established provenance. User authorization for disposition changes is currently a
prompt policy; this document does not claim a separate host confirmation mechanism exists.

## Startup and excluded surface

The host initializes issue/memory services and supplies a locally generated model summary
as hidden startup context before the first chat request. This makes no Gemini request and
adds no visible chat message. New chat/reload refreshes the context. Live issue and memory
state still require reads; initial context is not a current subsystem revision receipt.

No get_view/get_highlighted or view actions are implemented or declared. Diagnostics expose
loaded export/load records separately from CAD design issues; they do not expose host logs.
Gemini cannot create/delete/replace findings or edit their evidence. Recorded issue evidence
can support deterministic or heuristic findings; inspect the finding's flags.

## Exact declarations and handler rules


## get_model_summary

Status: implemented; always declared. No arguments, including no identity arguments. With a model, returns compact counts, capabilities, scoped mechanical entries, bounded load-diagnostic codes and toolCapabilities. With no model, returns modelLoaded=false and capability discovery. Does not dump the export. All other calls use its projectId/snapshotId.

Exact emitted declaration:

```json
{
  "name": "get_model_summary",
  "description": "Discover the loaded CADEN project ID, snapshot ID, identity scope, counts and capability states. Call first; use its top-level projectId and snapshotId in all other queries. Counts describe exported occurrences, not verified BOM quantities.",
  "parameters": {
    "type": "object",
    "properties": {},
    "required": []
  }
}
```

## get_object_details

Status: implemented when metadata is loaded. objectIds has 1â€“32 entries; IDs are deduplicated and an unknown ID rejects the entire batch. fields, when supplied, has 1â€“32 entries from the exact enum below. Defaults: suppressed, fixed, material, mass, constraintStatus. Returns identity context plus requested availability-wrapped fields; missing values are not zero/false. Retrieval coverage does not certify engineering-property completeness. No pagination. Inertia output uses documented SolidWorks L-components about COM in the output coordinate frame; it does not compute principal moments or output-origin inertia.

Exact emitted declaration:

```json
{
  "name": "get_object_details",
  "description": "Read known object IDs with compact identity and requested availability-wrapped properties. Unknown fields are errors; missing values are not false or zero. Default fields: suppressed, fixed, material, mass, constraintStatus.",
  "parameters": {
    "type": "object",
    "properties": {
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 32,
        "minItems": 1
      },
      "fields": {
        "type": "array",
        "items": {
          "type": "string",
          "enum": [
            "name",
            "type",
            "parentId",
            "sourceDocument",
            "configuration",
            "partNumber",
            "description",
            "suppressed",
            "fixed",
            "material",
            "mass",
            "volume",
            "centerOfMass",
            "inertia",
            "constraintStatus",
            "remainingDOF",
            "dimensions",
            "referenceGeometry",
            "customProperties"
          ]
        },
        "maxItems": 32,
        "minItems": 1
      }
    },
    "required": [
      "snapshotId",
      "projectId",
      "objectIds"
    ]
  }
}
```

## find_objects

Status: implemented when metadata is loaded. Choose exactly one mode: nonblank query, OR property plus operator. Query cannot be combined with property/operator/value/values. Query performs ordinal case-insensitive ID/name matching, requiring every whitespace-separated word to match; exact ID matches sort first, then name and ID. Property filtering uses ordinal case-sensitive string equality and deterministic name/ID ordering. Non-in operators require value and reject values; in requires values (1â€“32 operands) and rejects value. Each operand contains exactly one of text, number or boolean matching the property's type; only number can include unit. mass/volume require units. Mass: kg, g, lb. Volume: m^3, cm^3, mm^3, in^3, ft^3. Numeric comparisons normalize units; equality has no hidden tolerance. Ordered comparisons require a numeric property. Unknown values satisfy neither ordinary equality nor not_equals. Omitted or null scopeObjectIds means all objects; [] means none. Scope is exact IDs, not descendants. Pagination defaults to 20. No availability-state search operator is exposed in V1.

Exact emitted declaration:

```json
{
  "name": "find_objects",
  "description": "Find objects by query (case-insensitive ID/name substrings; every word matches), OR one property/operator filter. Use value={text:...}, {boolean:...}, or {number:...,unit:...}; in uses values=[...]. Numeric mass/volume operands require units. Text equality is ordinal case-sensitive. Unknown values never match, including not_equals. scopeObjectIds limits to exact IDs; omitted/null means all; [] means none. Inspect coverage and pagination.",
  "parameters": {
    "type": "object",
    "properties": {
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "query": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "property": {
        "type": "string",
        "enum": [
          "name",
          "type",
          "parentId",
          "sourceDocument",
          "configuration",
          "partNumber",
          "description",
          "suppressed",
          "fixed",
          "mass",
          "volume",
          "constraintStatus",
          "material.name",
          "material.assigned"
        ]
      },
      "operator": {
        "type": "string",
        "enum": [
          "equals",
          "not_equals",
          "greater_than",
          "greater_than_or_equal",
          "less_than",
          "less_than_or_equal",
          "in"
        ]
      },
      "value": {
        "type": "object",
        "properties": {
          "text": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "number": {
            "type": "number"
          },
          "boolean": {
            "type": "boolean"
          },
          "unit": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          }
        }
      },
      "values": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "minLength": 1,
              "maxLength": 512
            },
            "number": {
              "type": "number"
            },
            "boolean": {
              "type": "boolean"
            },
            "unit": {
              "type": "string",
              "minLength": 1,
              "maxLength": 512
            }
          }
        },
        "maxItems": 32,
        "minItems": 1
      },
      "scopeObjectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 256,
        "minItems": 0,
        "nullable": true
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "snapshotId",
      "projectId"
    ]
  }
}
```

## query_hierarchy

Status: implemented only with usable metadata hierarchy. direction is required; maxDepth defaults to 1. parent/children require depth exactly 1; ancestors/descendants accept 0â€“32. Depth zero returns no relatives, excludes the start object and reports the bounded query's coverage. Ancestors order nearest-first; descendants preserve exported child order within each depth. Pagination defaults to 20. Metadata hierarchy is authoritative and distinct from mate connectivity. Invalid/unavailable hierarchy is gated and fails with CAPABILITY_UNAVAILABLE; see the emitted-description caveat above.

Exact emitted declaration:

```json
{
  "name": "query_hierarchy",
  "description": "Read authoritative metadata containment, never mechanical connectivity. parent/children return depth 1; ancestors/descendants allow maxDepth 0-32 (default 1). Start object excluded. Depth 0 returns no relatives. Inspect coverage.depthLimited separately from pagination; invalid/unavailable hierarchy fails with CAPABILITY_UNAVAILABLE.",
  "parameters": {
    "type": "object",
    "properties": {
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "direction": {
        "type": "string",
        "enum": [
          "parent",
          "children",
          "ancestors",
          "descendants"
        ]
      },
      "maxDepth": {
        "type": "integer",
        "minimum": 0,
        "maximum": 32
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "snapshotId",
      "projectId",
      "objectId",
      "direction"
    ]
  }
}
```

## get_mates

Status: implemented, independently gated from scoped traversal. objectIds has 1â€“32 entries. Returns mates incident to ANY requested object, deduplicated and ordered by mate ID. Unknown object IDs reject the call. includeSuppressed defaults false and excludes explicitly suppressed mates only; unknown suppression remains visible as Unknown, never Active. Error/dangling relationships can be returned without implying functioning constraints. Returns contextual endpoints, availability-wrapped mate properties and provenance. Unestablished spatial frame/units are reported with limitations; raw spatial values are not usable geometry. The current unscoped lookup conservatively reports partial coverage with UNSCOPED_MATE_COVERAGE_NOT_ESTABLISHED. limit defaults to 20; filtering never establishes complete export coverage.

Exact emitted declaration:

```json
{
  "name": "get_mates",
  "description": "Read exported mates incident to ANY requested object, deduplicated by mate ID. Does not traverse or prove valid constraints. Explicitly suppressed mates excluded by default; unknown suppression retained and labeled unknown, never assumed active. Includes availability/provenance for optional fields. Missing export coverage is partial even when the list is empty. Raw spatial fields are not usable geometry without explicit frame/units.",
  "parameters": {
    "type": "object",
    "required": [
      "projectId",
      "snapshotId",
      "objectIds"
    ],
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "minItems": 1,
        "maxItems": 32
      },
      "includeSuppressed": {
        "type": "boolean"
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    }
  }
}
```

## get_mechanical_neighborhood

Status: implemented with an available scoped graph. scopeAssemblyId, configuration, startObjectIds and maxHops are required. Simultaneous starts are bounded by minimum mate-hop distance, using confirmed-active nodes and mates only. Unknown suppression is excluded and reflected in coverage; explicit active error/dangling mates remain exported relationships. Depth zero includes eligible start vertices and eligible edges between them. Results include all eligible mates with both endpoints reached, including parallel and cross edges. No pagination. Coverage separates evidence completeness from the requested search boundary; a relationship is not proof of rigidity or remaining DOF.

Exact emitted declaration:

```json
{
  "name": "get_mechanical_neighborhood",
  "description": "Return confirmed-active exported mate neighbors with minimum hops from simultaneous starts and every eligible mate between reached objects. Explicit scope/configuration from summary required. maxHops is required. Coverage and depth boundaries limit claims. No rigidity or motion inference.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "scopeAssemblyId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "configuration": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "maxHops": {
        "type": "integer",
        "minimum": 0,
        "maximum": 32
      },
      "startObjectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "minItems": 1,
        "maxItems": 32
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "scopeAssemblyId",
      "configuration",
      "startObjectIds",
      "maxHops"
    ]
  }
}
```

## find_mechanical_path

Status: implemented with an available scoped graph. scopeAssemblyId, configuration, startObjectId and endObjectId are required. maxHops is optional: omission requests an unbounded search over the eligible scoped graph; explicitly supplied values are 0â€“32. Outcomes: Found, ConfirmedDisconnected or NotEstablished. Uses confirmed-active exported relationships; unavailable/invalid graph is an execution failure, not NotEstablished. A found path is minimum-hop among known eligible relationships; complete object-ID sequence then mate IDs break ties. Parallel mate alternatives and edge status are preserved. Same eligible start/end returns Found with zero hops/no mates. ConfirmedDisconnected needs exhaustive search and complete evidence. No pagination; path certainty and shortestPathComplete retain their distinct meanings.

Exact emitted declaration:

```json
{
  "name": "find_mechanical_path",
  "description": "Return one deterministic minimum-hop exported mate path, or ConfirmedDisconnected only with complete evidence and exhaustive search; otherwise NotEstablished. Optional maxHops bounds the search. Error/dangling active mates remain relationships. Shortest path is only among known eligible relationships unless shortestPathComplete=true. Same eligible endpoints give zero-hop identity, not a mechanical relationship.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "scopeAssemblyId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "configuration": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "maxHops": {
        "type": "integer",
        "minimum": 0,
        "maximum": 32
      },
      "startObjectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "endObjectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "scopeAssemblyId",
      "configuration",
      "startObjectId",
      "endObjectId"
    ]
  }
}
```

## get_issue

Status: implemented after successful host issue initialization. Requires an opaque snapshot-scoped issueId. includeSuppressedCandidates defaults false; precedence-hidden findings require true even for direct lookup. Returns finding, affected entities, disposition, presentation, heuristic/design-intent flags, evidence hash, verification and freshness, but not full evidence. Does not run checkers. Coverage describes registered evaluations, not every possible engineering defect.

Exact emitted declaration:

```json
{
  "name": "get_issue",
  "description": "Read one current snapshot-scoped finding by issueId, including verification and disposition; does not run checks.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "issueId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "issueId"
    ]
  }
}
```

## list_issues

Status: implemented after successful host issue initialization. filters is an optional object with the exact keys below; supplied conditions combine with AND and exact matching. includeSuppressedCandidates defaults false (presented findings). This is independent of the disposition filter and CAD suppression. Pagination defaults to 20; sort is severity descending then issueId. Cursors bind the issues revision. Returns current findings plus revision and evaluation coverage. Filtering/presentation/paging do not alter underlying coverage; an empty filtered page does not establish a defect-free design.

Exact emitted declaration:

```json
{
  "name": "list_issues",
  "description": "List current findings with bounded pagination and optional exact-match AND filters. Presented findings by default; includeSuppressedCandidates includes precedence-suppressed candidates, not suppressed CAD components. Coverage remains independent of filtering. Repeat identical arguments with nextCursor.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "filters": {
        "type": "object",
        "properties": {
          "severity": {
            "type": "string",
            "enum": [
              "Error",
              "Warning",
              "Question",
              "Info"
            ]
          },
          "disposition": {
            "type": "string",
            "enum": [
              "Open",
              "Resolved",
              "Ignored"
            ]
          },
          "checkerId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "issueType": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "objectId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "mateId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          }
        }
      }
    },
    "required": [
      "projectId",
      "snapshotId"
    ]
  }
}
```

## get_issues_for_object

Status: implemented after successful host issue initialization. Requires an existing objectId and uses exact affected-object membership. Supports the same nested filters and pagination as list_issues; includeSuppressedCandidates defaults false. Coverage includes the object and relevant existing finding subjects (for example an island representative). Does not run checkers or imply subtree expansion.

Exact emitted declaration:

```json
{
  "name": "get_issues_for_object",
  "description": "List current findings with bounded pagination and optional exact-match AND filters. Presented findings by default; includeSuppressedCandidates includes precedence-suppressed candidates, not suppressed CAD components. Coverage remains independent of filtering. Repeat identical arguments with nextCursor.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "filters": {
        "type": "object",
        "properties": {
          "severity": {
            "type": "string",
            "enum": [
              "Error",
              "Warning",
              "Question",
              "Info"
            ]
          },
          "disposition": {
            "type": "string",
            "enum": [
              "Open",
              "Resolved",
              "Ignored"
            ]
          },
          "checkerId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "issueType": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "objectId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "mateId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          }
        }
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "objectId"
    ]
  }
}
```

## get_issues_for_mate

Status: implemented after successful host issue initialization. Requires a known mateId and selects findings related to that mate. Supports nested filters and pagination; includeSuppressedCandidates defaults false. No dedicated mate checker is registered in the current initial checker set: an empty result may have unavailable evaluation coverage. The existence of this read tool is not proof of implemented mate diagnostics.

Exact emitted declaration:

```json
{
  "name": "get_issues_for_mate",
  "description": "List current findings with bounded pagination and optional exact-match AND filters. Presented findings by default; includeSuppressedCandidates includes precedence-suppressed candidates, not suppressed CAD components. Coverage remains independent of filtering. Repeat identical arguments with nextCursor.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "mateId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "filters": {
        "type": "object",
        "properties": {
          "severity": {
            "type": "string",
            "enum": [
              "Error",
              "Warning",
              "Question",
              "Info"
            ]
          },
          "disposition": {
            "type": "string",
            "enum": [
              "Open",
              "Resolved",
              "Ignored"
            ]
          },
          "checkerId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "issueType": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "objectId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          },
          "mateId": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
          }
        }
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "mateId"
    ]
  }
}
```

## get_issue_summary

Status: implemented after successful host issue initialization. Unlike get_model_summary, requires projectId and snapshotId. includeSuppressedCandidates defaults false. Returns count, candidate/presented/precedence-suppressed counts, severity/disposition counts, registered checker IDs, issues revision and evaluation coverage; it does not attach full evidence. Reading does not rescan. The host scans on startup and metadata reload (including New chat).

Exact emitted declaration:

```json
{
  "name": "get_issue_summary",
  "description": "Read presented issue counts, registered checker IDs, current issues revision, and evaluation coverage. Zero findings is not proof of a defect-free design.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      }
    },
    "required": [
      "projectId",
      "snapshotId"
    ]
  }
}
```

## get_issue_evidence

Status: implemented after successful host issue initialization. Requires issueId; includeSuppressedCandidates defaults false. Returns the finding with its recorded structured evidence. Evidence remains read-only and may support a deterministic or heuristic finding; it is not universally proof of a deterministic defect. Use this to explain a finding without inventing supporting facts.

Exact emitted declaration:

```json
{
  "name": "get_issue_evidence",
  "description": "Read a finding and its structured evidence; evidence is read-only.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "issueId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "includeSuppressedCandidates": {
        "type": "boolean"
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "issueId"
    ]
  }
}
```

## revalidate_issue

Status: implemented analysis action. Requires projectId, snapshotId, issueId, operationId and expectedRevision from issue reads. Reruns the finding's checker precedence group for its subject against the loaded immutable snapshot, not live SolidWorks. Committed success may report UnableToEvaluate. Outcome can be Present, Absent or UnableToEvaluate. Updates evaluation/freshness/presentation; it does not explicitly mark Resolved/Ignored. Exact retries reuse every original argument and recover the durable issues receipt.

Exact emitted declaration:

```json
{
  "name": "revalidate_issue",
  "description": "Rerun the finding's checker precedence group against the loaded immutable snapshot. Requires operationId and expectedRevision from issue reads. Does not resolve/ignore findings or query live CAD. Successful commit may still yield UnableToEvaluate.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "issueId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "operationId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "expectedRevision": {
        "type": "integer",
        "minimum": 0,
        "maximum": 9007199254740991
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "issueId",
      "operationId",
      "expectedRevision"
    ]
  }
}
```

## revalidate_object_issues

Status: implemented analysis action. Requires objectId plus identity/operation/revision fields. Runs registered checks for the object and existing related finding subjects, including island representatives, against the immutable snapshot. Success means the evaluation operation committed; coverage may still be partial or UnableToEvaluate. No CAD edit or automatic resolved/ignored assignment is implied. Uses the issues revision/receipt subsystem.

Exact emitted declaration:

```json
{
  "name": "revalidate_object_issues",
  "description": "Rerun registered checks for an object and existing related finding subjects, including island representatives. Requires operationId and expectedRevision. Does not mutate CAD/evidence directly or mark findings resolved or ignored.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "operationId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "expectedRevision": {
        "type": "integer",
        "minimum": 0,
        "maximum": 9007199254740991
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "objectId",
      "operationId",
      "expectedRevision"
    ]
  }
}
```

## set_issue_disposition

Status: implemented action. Requires issueId, disposition (Open/Resolved/Ignored), expectedEvidenceHash, operationId and expectedRevision, plus project/snapshot identity. reason is optional, 1â€“512 characters when supplied. Changes explicit user disposition independently of finding evidence. Rejects stale evidence and revision conflicts. Resolved records an assessment, not proof of a physical fix; Ignored accepts a condition; Open returns it to attention. The prompt requires user instruction for the change. No finding creation/deletion/evidence-edit API is exposed. Committed dispositions restore for matching snapshot and evidence; memory writes do not change them.

Exact emitted declaration:

```json
{
  "name": "set_issue_disposition",
  "description": "Set Open, Resolved or Ignored only when requested by the user. Requires issueId, expectedEvidenceHash from an issue read, expectedRevision and operationId; optional reason. Saves disposition durably without changing finding evidence or proving a CAD fix. Exact retries reuse all original arguments.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "issueId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "operationId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "expectedRevision": {
        "type": "integer",
        "minimum": 0,
        "maximum": 9007199254740991
      },
      "disposition": {
        "type": "string",
        "enum": [
          "Open",
          "Resolved",
          "Ignored"
        ]
      },
      "expectedEvidenceHash": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "reason": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "issueId",
      "operationId",
      "expectedRevision",
      "disposition",
      "expectedEvidenceHash"
    ]
  }
}
```

## get_project_memory

Status: implemented after successful host memory initialization. kind omitted reads both memory and requirements; kind=memory or requirement selects one. includeProjectScope defaults true; includeStale/includeRetired default false. Omitted objectIds means project scope only; supplied IDs are exact, without descendants. objectIds=[] selects no object attachments; project inclusion is independent. types/keys omission is unrestricted; [] matches nothing. Filters use ordinal exact matching. Requirement key means its stable ID and any matching requested object selects the whole multi-object requirement. Unknown IDs can inspect historical references with includeStale; they are not remapped. Returns records with separate lifecycle/reference validity and provenance, memory revision, bounded pagination (default 20), and coverage of stored records before filters. Does not expose the full sidecar/history or certify complete design knowledge. Use allObjectScopes=true with nonempty exact keys when attachment is unknown. It cannot be combined with objectIds. Lifecycle/stale filters and bounded pagination still apply.

Exact emitted declaration:

```json
{
  "name": "get_project_memory",
  "description": "Read stored intent/requirements; this does not establish CAD facts. For a known key with unknown attachment use keys plus allObjectScopes=true directly; no object discovery is needed. Otherwise omitted objectIds means project only; explicit IDs are exact scopes. Project scope included by default. Requirement key is its ID. Stale/retired records excluded unless requested. Inspect provenance/reference validity. Pagination binds query and memory revision.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 32
      },
      "keys": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 32
      },
      "types": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 32
      },
      "kind": {
        "type": "string",
        "enum": [
          "memory",
          "requirement"
        ]
      },
      "allObjectScopes": {
        "type": "boolean",
        "description": "Search exact keys across all object attachments. Requires nonempty keys; cannot combine with objectIds. Use when the memory key is known but the attached object is not."
      },
      "includeProjectScope": {
        "type": "boolean"
      },
      "includeStale": {
        "type": "boolean"
      },
      "includeRetired": {
        "type": "boolean"
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "projectId",
      "snapshotId"
    ]
  }
}
```

## write_project_memory

Status: implemented action after successful host memory initialization. Requires kind, targetObjectIds, type, key, operationId, expectedRevision plus project/snapshot identity. Supply exactly one of value (nonempty text) or valueJson (encoded structured JSON), each at most 32,000 characters; parsed values are additionally bounded to 32,000 UTF-8 bytes in the primitive's canonical representation. valueJson rejects malformed/duplicate-key/trailing JSON, excessive nesting and null as a value. Optional context is 1â€“512 characters and stores the value as {content, context}. targetObjectIds has 0â€“32 entries; [] explicitly means project scope. kind is memory or requirement, separate from attachment scope. Memory atomically upserts the same type/key slot for every target. Requirement writes one multi-object record with key as the stable requirement ID. lifecycle defaults Active; Retired preserves history, not deletion. Unknown targets and stale Active references fail; eligible historical records can be retired. Failed batch validation or persistence publishes none of it. Receipt contains the memory subsystem revision and result recordIds; retries preserve the original result. Gemini provenance is host-fixed AssistantInferred with no user-authority override, so it cannot overwrite protected UserEstablished entries. Writes preserve source CAD metadata and issue disposition. Persistence survives restart; conversation history remains session-only. Tool journal limits are 10 MB and 10,000 tool operations, with primitive history/capacity bounds also enforced. Capacity failure never silently discards history or receipts.

Exact emitted declaration:

```json
{
  "name": "write_project_memory",
  "description": "Persist CADEN knowledge separately from CAD metadata and issue disposition. Supply value (text) OR valueJson (encoded structured JSON), never both/null. Empty targetObjectIds is project scope. Memory writes the same type/key slot atomically for every target; requirement writes one multi-object requirement with key as its stable ID. Lifecycle Active/Retired preserves history. Host labels Gemini writes AssistantInferred; cannot overwrite UserEstablished knowledge. Use memory revision from get_project_memory; exact retries reuse operationId and all arguments.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "targetObjectIds": {
        "type": "array",
        "items": {
          "type": "string",
          "minLength": 1,
          "maxLength": 512
        },
        "maxItems": 32
      },
      "kind": {
        "type": "string",
        "enum": [
          "memory",
          "requirement"
        ]
      },
      "type": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "key": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "value": {
        "type": "string",
        "minLength": 1,
        "maxLength": 32000
      },
      "valueJson": {
        "type": "string",
        "minLength": 1,
        "maxLength": 32000
      },
      "context": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "lifecycle": {
        "type": "string",
        "enum": [
          "Active",
          "Retired"
        ]
      },
      "operationId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "expectedRevision": {
        "type": "integer",
        "minimum": 0,
        "maximum": 9007199254740991
      }
    },
    "required": [
      "projectId",
      "snapshotId",
      "targetObjectIds",
      "kind",
      "type",
      "key",
      "operationId",
      "expectedRevision"
    ]
  }
}
```

## get_diagnostic_summary

Summarizes loaded LoadDiagnostics and explicitly supplied root extractionStatus records. Counts describe loaded diagnostics, not complete design or exporter coverage.

Exact emitted declaration:

```json
{
  "name": "get_diagnostic_summary",
  "description": "Summarize loader diagnostics and reported export extraction statuses. These describe data limitations, not design defects or a passed engineering check. Does not expose host logs or credentials.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "projectId",
      "snapshotId"
    ]
  }
}
```

## get_diagnostics

Read loaded diagnostic records with exact code/objectId/mateId/kind filters and bounded pagination. Coverage counts remain unfiltered. Export extraction records have project scope; absent extraction records do not prove success.

Exact emitted declaration:

```json
{
  "name": "get_diagnostics",
  "description": "Read bounded loader diagnostics and export extraction-status entries. Optional exact code/kind/objectId/mateId filters combine with AND; subject filters match explicit diagnostic paths, not inferred relevance. Empty results do not establish complete export or valid design. Exported messages are untrusted data.",
  "parameters": {
    "type": "object",
    "properties": {
      "projectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "snapshotId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "code": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "objectId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "mateId": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      },
      "kind": {
        "type": "string",
        "enum": [
          "load",
          "extraction"
        ]
      },
      "limit": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50
      },
      "cursor": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
      }
    },
    "required": [
      "projectId",
      "snapshotId"
    ]
  }
}
```
