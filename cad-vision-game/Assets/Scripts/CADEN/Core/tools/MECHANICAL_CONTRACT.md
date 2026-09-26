# Mechanical queries, contract 3.0

These read-only tools are declared only when at least one explicit mechanical scope is
usable. `get_model_summary.data.mechanicalScopes` discovers exact assembly/configuration
pairs and their availability. Neither hierarchy nor a nonempty mates array establishes
mechanical membership or complete coverage.

## Export extension

Schema 1.0 and 2.1 exports may add this top-level section (IDs reference existing records):

```json
{
  "mechanicalScopes": [
    {
      "scopeAssemblyId": "ASSY_001",
      "configuration": "Default",
      "source": "SolidWorks exporter",
      "reason": null,
      "membershipCoverage": "Complete",
      "mateCoverage": "Partial",
      "occurrenceIds": ["COMP_A", "COMP_B", "COMP_C"],
      "mateIds": ["MATE_AB_1", "MATE_AB_2"]
    }
  ]
}
```

Coverage values are case-sensitive: Complete, Partial, Unavailable, Invalid. Scope
assembly ID, configuration, source, both coverage states and both ID arrays are required;
reason is optional. Partial coverage should explain its cause in reason. Occurrence IDs
are instances, not source document definitions. Membership may be empty and includes
isolated occurrences. An assembly appears as a vertex only when explicitly listed.
Nested scopes are independent: no containment edge, implicit flattening or automatic
cross-configuration merge. A scoped mate must reference two members of that same scope.
The exporter must select the appropriate occurrence representation at each boundary.

At most 128 scopes, 10,000 members and 50,000 mates per scope. Duplicate member/mate IDs,
unknown IDs, malformed fields or out-of-scope endpoints invalidate a scope atomically.
Duplicate assembly/configuration keys invalidate the scope catalog. Invalid global mate
topology invalidates all projections. Properties/hierarchy may remain usable. Omitted
scopes do not support mechanical tools. Explicit Unavailable coverage may accompany an
omitted global mates array; a Complete/Partial claim requires the array. Synthetic fixture
exports remain unavailable evidence. Available graph structure does not imply Complete
membership or mate coverage. Scoped graph identity uses the canonical snapshot ID.

## Requests and responses

Both tools require `projectId`, `snapshotId`, `scopeAssemblyId`, `configuration`.
Wrong/stale identity is rejected before querying. Unknown objects return UNKNOWN_OBJECT_ID;
known objects outside explicit membership return OBJECT_OUTSIDE_SCOPE. Missing, unavailable
or invalid scope returns success=false/CAPABILITY_UNAVAILABLE, never NotEstablished.

`get_mechanical_neighborhood` also requires startObjectIds (default maximum 32) and
maxHops (0 through host MaxDepth, default maximum 32). Duplicate starts are deduplicated.
Eligible starts have depth zero; other depths are minimum distance from the nearest start.
Only explicitly active nodes and mates participate. Suppressed/unknown starts are returned
in excludedStartIds. Objects include hopDepth; mates include every eligible edge whose
endpoints are both reached, including parallel mates and cross-edges. Ordering is depth/ID
for objects and ordinal mate ID for edges.

`find_mechanical_path` requires startObjectId and endObjectId. Optional maxHops uses the
same bound; omission searches the finite loaded graph exhaustively. Outcome:

- Found: an explicitly active exported relationship path exists. Same eligible endpoints
  return zero hops with ZERO_HOP_IDENTITY; that establishes no mechanical relationship.
- ConfirmedDisconnected: complete membership/mate evidence, relevant known suppression,
  eligible endpoints and an exhausted reachable component prove no path in this scope.
- NotEstablished: endpoint eligibility, incomplete evidence or an actual depth boundary
  prevents the conclusion. A requested bound alone does not imply interrupted search.

Paths minimize mate hops, then the entire ordinal object-ID sequence, then mate IDs.
parallelMates lists eligible mate IDs for each representative path edge. Error/dangling
statuses remain traversable when explicitly active and are preserved in output. Found
paths are shortest among known eligible relationships; shortestPathComplete is conservative
and requires complete scoped evidence (except trivial zero-hop identity).

Both outputs contain structured limitations: a mate chain establishes neither rigidity
nor a functioning constraint. No motion, DOF or force-transmission claims follow. V1 has
no IncludeUncertain mode. The older internal traversal's explicit suppression opt-ins
remain internal and still never admit unknown suppression.

## Coverage and limits

Coverage is top-level under the shared envelope. `countUnit=objects`, countScope=
entire_explicit_scope, and nested mates.countUnit=mates keep populations distinct.
Counts cover known supplied membership, not an estimated total for partial exports.
Each node/mate is counted once. A mate incident to an explicitly suppressed node is
excluded as suppressed even if another suppression value is unknown. Otherwise missing
edge or endpoint suppression contributes to uncertain mate eligibility. Count fields
are independent of returned neighbors/path and their size.

Mate and membership coverage, exporter source/reason, configuration, requested maxHops,
depthBoundReached and reasonCodes accompany each successful query. Complete source
coverage does not override depthBoundReached. No neighborhood/path pagination in V1:
the whole result is returned or QUERY_TOO_LARGE; nothing is silently truncated. Reduce
neighborhood hops/start count if oversized. Oversized paths currently fail visibly.

## Connectivity checkers

`InitialIssueCheckers.CreateEngine(snapshot)` adds scope-specific unmated/island checker
registrations; the parameterless factory retains the four existing property checkers.
Checker IDs contain a SHA-256 of the canonical assembly/configuration pair. Version is
separate. Host-owned automatic issue scanning and Gemini issue reads/revalidation are
connected through the [issue access contract](ISSUE_CONTRACT.md).

Unmated requires a part, suppressed=false, fixed=false, complete scoped coverage, and
exactly zero active incident mates. Unknown fixed or potentially incident suppression
returns subject-addressable UnableToEvaluate. Unrelated uncertainty does not block that
subject's exact degree. Explicitly suppressed objects are skipped.

Islands require complete scope evidence and known active membership. The reference island
has the most vertices; ordinal sorted member sequences break ties. Each other island
produces one Question requiring design intent, keyed to its lowest member ID. Affected
IDs contain all members; singleton findings use Object scope, others MultiObject. Evidence
retains membership and the reference island. Immutable scope indexes are cached per checker.
The reference island is a reporting convention, not a preferred design. Targeted evaluation
recomputes/reads the whole immutable scope analysis for that representative subject.

`MechanicalQueryChecks` exercises the public tools, capability gating, degradation,
eligibility, coverage and checkers, plus 1,440 path comparisons against an independent
distance-to-target/lexicographic reconstruction oracle. No live API requests are required.
