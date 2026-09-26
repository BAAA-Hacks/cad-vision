# CADEN primitives

Primitives are internal building blocks. Gemini sees higher-level tools, not these APIs.

```text
primitives/
  data_structures/mechanical_graph/MechanicalGraph.cs
  operations/mechanical_graph/BuildMechanicalGraph.cs
  operations/mechanical_graph/TraverseMechanicalGraph.cs
```

The representation contains immutable component/mate records and read-only ID indexes.
Construction and traversal live under operations. There are no Gemini, Unity, filesystem,
or visualization dependencies in this primitive. Parsing uses the core's existing Newtonsoft.Json.

## Construction

```csharp
using Core.Primitives.DataStructures.MechanicalGraph;
using Core.Primitives.Operations.MechanicalGraph;

// Only pass Available after the exporting host confirms mate extraction completed.
GraphBuildResult build = BuildMechanicalGraph.Build(metadataJson, GraphDataState.Available);
if (build.State != GraphDataState.Available)
{
    // Inspect Errors and Warnings. build.Graph is null; no partial graph is published.
    return;
}
var graph = build.Graph!;
```

The explicit export-state argument defaults to Unavailable. It is a host contract, not
inferred from an array's length. No new mandatory exporter JSON property is imposed.
Fixture metadata always remains Unavailable even if the caller passes Available.
A missing/null mates field with Unavailable state is unavailable; an Available claim
without a mates array is invalid. An explicitly confirmed empty array is Available.
Invalid structure takes precedence over Unavailable and never produces a usable graph.

Schema 1.0 inputs are project, objects, and mates. Components use existing id/name/type,
parentId, mass/volume/material, definitionStatus, fixed/suppressed, and optional issueIds.
Every component occurrence is indexed, including isolated nodes and assembly nodes.
Hierarchy fields are descriptive; this graph does not traverse or validate the assembly tree.

Each mate requires a unique ID and exactly two distinct existing componentIds.
No self-mates, N-way mates, missing endpoints, or duplicate IDs are accepted.
Each mate is one distinct shared object referenced in both endpoint adjacency lists.
Multiple reference entities are supported, including several for the same endpoint.
A reference's componentId must be one of that mate's endpoints. Invalid reference
ownership is a construction error rather than silently discarding an entity.

Limits: 10 MB JSON, 10,000 component nodes, 50,000 mates, 128-character IDs,
512-character names. Duplicate JSON properties are rejected.

Required identity/topology errors are fatal and returned with Code, Path, Message.
Malformed optional engineering fields produce warnings and unknown/invalid values,
not invented defaults. Quantity values distinguish Available/Missing/Invalid.
Unknown fixed/suppressed/material/constraint properties stay nullable. A mate with
status=suppressed is suppressed; an explicit contradictory suppressed=false is invalid.
Other statuses do not establish suppression=false when that field is absent.

Mass is normalized to kg from kg/g/lb; volume to m^3 from the cube of the declared
project length unit m/cm/mm/in/ft. This follows the export's schema 1.0 normalization
rule. Missing units make numeric values unavailable; unsupported units are invalid.
No assembly mass is decomposed or distributed among parts.

Vectors use doubles and contain nullable CoordinateFrame/Unit, supplied by optional
mate axisFrame/axisUnit metadata. They preserve the source vector without normalization
or frame conversion. A malformed/zero axis becomes unavailable with a warning.
Limits preserve enabled/minimum/maximum and optional limits.unit; unspecified units
stay unknown. No relationship analysis should infer physical motion from these alone.

## Traversal

```csharp
var result = TraverseMechanicalGraph.Traverse(graph, new GraphQuery
{
    StartIds = new List<string> { "COMP_SHAFT" },
    MaxDepth = 2,
    IncludeSuppressedComponents = false,
    EdgeFilter = new GraphEdgeFilter
    {
        IncludeSuppressed = false,
        AllowedMateTypes = null,
        AllowedMateStatuses = null
    },
    Return = new GraphReturnSpec
    {
        IncludeNodes = true,
        IncludeEdges = true,
        IncludeDepths = true,
        CountComponents = true,
        CountMates = true,
        SumMass = true
    }
});
```

The initial traversal is BFS only. Depth is minimum eligible mate hops from the nearest
eligible start. All distinct starts are seeded at depth zero. MaxDepth=null is unlimited;
zero returns eligible starts plus eligible edges between them. Negative depths, unknown
starts, null return/filter specs, and malformed filters return structured query errors.
Cancellation throws OperationCanceledException without returning partial results.

Nodes and mates with suppression=true are excluded by default. Separate options include
suppressed components and suppressed mates. Suppressed start IDs are listed in
ExcludedStartIds; if all starts are excluded the query successfully returns an empty scope.
Unknown suppression states remain eligible. UnknownSuppressionNodeCount and
UnknownSuppressionMateCount describe the reached nodes and included edges, and
SuppressionInformationComplete reports whether those states are fully known.

Type/status allowlists are case-insensitive; IDs remain case-sensitive. All active
filters must pass. A null set is unrestricted, an empty set permits no edges, and an
unknown edge type/status cannot satisfy a specified allowlist. Caller-supplied query
collections must not be mutated concurrently with a call.

Results include every filter-passing mate with both endpoints in the reached set:
parallel edges, cycles, and cross-edges at the depth boundary are included once.
Edges leading beyond the allowed depth or through excluded components are omitted.
Traversal maintains separate visited-node and visited-mate indexes and accumulates
aggregates in the same BFS. It scans each reached adjacency list, not the whole graph.

Node/edge/depth/issue result collections remain null unless requested. Counts and
groupings remain null unless requested. Bookkeeping for traversal still requires visited
sets and a queue. Returned node objects are immutable graph records: their Edges property
is full graph adjacency; use result.Edges for the filtered induced edge set.
Ordering follows distinct start order, BFS discovery, and source mate order. Issue IDs
are deduplicated across reached nodes/included mates and returned in ordinal order.

## Aggregation semantics

Counts/groupings cover the reached nodes and included edges. Material/constraint
groups include assemblies as vertices; missing labels go in a separate UnknownCount
rather than a potentially colliding dictionary key. Mate types are grouped per distinct mate.

Mass and volume sum only nodes whose type is part, irrespective of whether an assembly
node has children. Assembly-level quantities are excluded and counted separately.
Each requested QuantityAggregate contains:

- TotalKnown, KnownCount, MissingCount, InvalidCount, Unit
- ExcludedAssemblyCount, Overflowed, Complete

Known zero values count as known. If eligible parts exist but none have known values,
TotalKnown is null. For an empty part scope the known sum is zero and value completeness
is vacuously true; inspect counts before describing it as a design property. Overflow
sets TotalKnown=null and Complete=false. Partial known sums retain missing/invalid counts.

Complete means value coverage for the included part nodes, NOT total assembly mass,
complete reachability beyond MaxDepth, or complete suppression information. Inspect the
separate suppression diagnostics and query scope before making higher-level claims.

## Verification and boundaries

MechanicalGraphChecks runs in the existing offline Checks executable. It includes
parallel/cross-edge cases, cycles, simultaneous starts, filters, suppression, missing
values, unit conversion, overflow, invalid builds, and 80 deterministic random graphs
compared with an independent BFS-then-edge-scan oracle.

No higher-level graph tool, Gemini declaration, desktop loading hook, DFS, shortest-path
API, boundary API, spatial search, issue generation, or property index is added here.
The host/higher-level tools will invoke construction and traversal when those are designed.
