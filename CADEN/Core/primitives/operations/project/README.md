# Canonical project foundation — milestone 1

`LoadProject.Load(json, options)` returns an immutable ProjectSnapshot and scoped
LoadDiagnostics. No files, Gemini calls, or Unity objects are used. The parser reads
one JSON object once and preserves date-like strings as strings. Unsupported fields
remain in defensive raw-export copies; they are not silently treated as validated facts.

```csharp
var result = LoadProject.Load(json, new ProjectLoadOptions
{
    MateExportState = CapabilityState.Available, // only after exporter confirms coverage
    ComponentIdsStableAcrossSnapshots = true    // only with an exporter identity guarantee
});
if (!result.Success) { /* inspect fatal diagnostics */ return; }
var snapshot = result.Snapshot!;
var mass = snapshot.ComponentsById["COMP_001"].Properties["mass"];
// Check mass.State before using mass.Value. Inspect RawValue only for diagnosis.
```

## Degradation boundaries

- Project-fatal: malformed/unsupported JSON or schema, invalid required project identity,
  malformed required component id/name/type, duplicate component IDs, invalid fixture provenance.
- Properties: optional invalid values have Invalid status and a diagnostic. Valid fields
  remain usable. Properties=Available describes access, not complete engineering coverage.
- Hierarchy: absent/null child lists or missing hierarchy fields give Unavailable;
  supplied invalid references, contradictions, cycles, or disconnected trees give Invalid.
  These do not prevent component lookup or a healthy mechanical graph capability.
- MechanicalGraph: supplied malformed mates give Invalid; missing coverage gives
  Unavailable; explicitly confirmed empty mate arrays are Available. Invalid mate data
  publishes no partial mate ID index. Its raw records remain preserved for diagnosis.
- MateAnalysisInput mirrors graph readiness; it is not an implemented issue engine.

No result with a project-fatal diagnostic contains a snapshot. Successful snapshots may
contain nonfatal Error diagnostics for disabled capabilities. Consumers must inspect
capability states rather than treating Success as "all data valid".

## Values and identity

MetadataValue contains Available/Missing/Invalid/NotApplicable status, value, raw value,
presence, reason, expected format, unit/frame, and provenance. NotApplicable remains
reserved; no inapplicability is guessed. Absence and explicit JSON null remain distinct
through WasPresent/RawValue. Zero and false remain available when valid. Raw JSON tokens
and nested records are returned as deep copies; dictionaries are read-only.

Schema 1.0 spatial frames and several compound units remain unspecified. The frame is
null and spatial interpretation is unavailable; raw vectors and unknown extension
fields are preserved. No extra exporter frame format is invented in this milestone.
MateMetadata currently normalizes identity/endpoints and retains the complete raw record;
its detailed field projection belongs to the graph migration milestone.

SnapshotId is SHA-256 of the exact UTF-8 input text (including formatting), not a random
load ID. RevisionId is an optional project.revisionId label, never a replacement for
the content identity. IDs default to SnapshotOnly. ProjectStable requires an explicit
host guarantee and is never allowed for fixture data. A matching fixture ID in changed
content is not evidence of a matching physical component.

Limits: 10 MB UTF-8, 10,000 components, 128 JSON/hierarchy levels, bounded names/IDs.
Graph validation retains its 50,000-mate cap. Parser-range failures produce fatal JSON
diagnostics; representable large integers follow ordinary field availability rules.

## Deliberate migration boundary

This is a tested foundation, not yet the desktop's active loader. The existing desktop
query path still uses MetadataStore and its stricter loading behavior. Query primitives,
property indexes, and graph projection migration are the next milestone.

To avoid divergent property rules, both canonical loading and the existing query adapter
use MetadataPropertyRules here. Mechanical topology validation reuses the graph builder
on the already-parsed document; the temporary graph projection is not published on the
snapshot. The later migration should consume canonical fields directly and remove that
transitional projection. This milestone does not add issue generation or memory storage.

ProjectLoaderChecks runs with the existing offline checks. One independent review of
this foundation found a large-integer conversion bug in reused graph validation; that
bug is fixed and covered by regression tests. No live Gemini tests are required.
