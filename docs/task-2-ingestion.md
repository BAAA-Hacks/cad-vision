# Task 2: runtime ingestion

## Implemented desktop path

`CadModelLoader.LoadFilesAsync(glbPath, metadataPath)` reads a complete local pair.
`LoadPackageAsync(byte[], string)` is the receiver integration point and must be
called on Unity's main thread. It validates the package, imports with glTFast,
maps exact node indices, prepares box colliders, positions the model, and publishes
it through `CADVisionRuntime`. There is no Editor pre-import of the GLB.

In Unity, enter Play Mode and choose **CAD Vision > Load local design (Play Mode)**.
Select a GLB and its matching metadata JSON. This creates a runtime host if needed.
The scene should have a camera tagged MainCamera (or assign `ReviewOrigin` on the
loader). No scene or XR configuration is changed by installing this code.

The runtime provides `GetRoot`, `GetObject`, `GetMetadata`, `GetAllObjects`,
`GetProjectMetadata`, and full metadata through `Metadata.Document`/`RawJson`.
Metadata getters return copies. `GetAllObjects` is a read-only registry view;
old views are emptied on replacement. Subscribe to `ModelChanged` and track
`Revision` to discard stale selections, findings, and asynchronous tool calls.
Unknown IDs throw; matching is case-sensitive. Use `GetComponentInParent<CadVisionObject>()`
on a selected collider to recover its CAD ID, including mesh helper children.

## Metadata and mapping contract

The parser follows `CADEN_Export_Pipeline.md`: schema version `1.0`,
`project.rootObjectId`, and an `objects` array with `id` and `parentId`.
It checks `childIds` consistency when provided. All original engineering fields,
custom properties, and future fields are retained, including nulls.

For this initial importer, **each object must have an integer `glbNodeIndex`**.
This explicit mapping extends the export contract's optional mapping section.
The shipper/exporter must supply it for the exact GLB being sent. Bare names are
not a fallback: the supplied FRED GLB contains repeated names. Node indices are
not stable between exports. The registry exposes stable CAD IDs, not node indices.
The imported hierarchy is compared using the closest mapped ancestor, allowing
unmapped helper nodes. Missing nodes and duplicate node/object mappings fail.
This validates correspondence, not cryptographic proof that two exports match;
a production package fingerprint is still an integration follow-up.

GLBs must be self-contained glTF 2 binaries. External buffer/image URIs are
rejected. A valid GLB envelope and JSON chunk are checked before import.
glTFast performs the detailed geometry decoding and material import.

## FRED test fixture

The original FRED GLB remains outside `Assets`. Its scene contains a camera at
node 0 and the assembly root at node 1. To generate local, synthetic mapping data:

```powershell
python tools/create_glb_fixture.py FRED_P1_Assembly.glb FRED_P1_Assembly_metadata_sample.json --root-node 1
```

This produces 261 structural node entries. Types are inferred from hierarchy
only; masses/material assignments remain null and definition status is unknown.
`provenance.synthetic` is true. The file is **not SolidWorks engineering evidence**.
It records the source GLB SHA-256 for traceability. Do not overwrite real exporter
metadata with this generator.

## Coordinates, selection, and lifecycle

glTF geometry uses meters and Y-up. glTFast converts handedness; the loader does
not convert it again or multiply by `project.units.length` (which describes
engineering metadata values). Original child transforms and names are preserved.
No inferred SolidWorks-specific rotation is applied.

`FitForReview` defaults to true and scales only `CADVisionModelRoot` to a
`ReviewSize` of 1 meter. Disable it for physical 1:1 geometry. The bounds center
is placed in front of the review origin, with space for its bounding sphere.
Visual review scaling does not change metadata values. Upright orientation and
physical scale still need confirmation against a known real CAD dimension.

Box colliders use imported mesh bounds and support broad selection; they are
approximations, not interference geometry. The loader does not implement
selection state, highlighting, grabbing, or other Task 3 behavior.
Imported cameras, lights, and animations are excluded from instantiation.

Replacement is transactional: the old model remains available until the new
model's full mapping validates. Failure cleans up the candidate. Successful
replacement clears the old registry, disables/destroys the old root, and disposes
its glTFast resources. `Clear()` drops all active metadata and IDs. Concurrent
imports are rejected; `Cancel()` stops the pending loader operation.

## Verification and remaining integration

Run `CadIngestionTests` in Unity Test Runner (Edit Mode). These cover malformed
metadata, identity/hierarchy failures, metadata retention, malformed GLBs,
registry replacement, and real FRED import/reload/failure cleanup. The FRED test
is skipped when the local GLB or generated JSON is missing.

Verified in Unity 6000.6.0f1: all 17 Edit Mode tests passed, including two real
FRED imports (261 mapped nodes each) and retaining the loaded model after a
rejected package. This is not yet a Quest or Play Mode validation result.

Still to implement/verify:

- Quest HTTP receiver, transfer limits, atomic package completion, and shipper acknowledgement.
- Final shipper mapping agreement and real SolidWorks metadata pair validation.
- Play Mode/XR interaction integration, Quest/IL2CPP build, material shader inclusion,
  physical scale/orientation check, and headset performance.
- Progress reporting, optimized/precise colliders, and very large assembly handling.

Dependencies installed via Unity Package Manager: glTFast 6.20.0 and Newtonsoft
JSON 3.2.2. Pipeline is an Editor automation dependency. The dependency installer
menu is retained for explicit repair/setup; it does not run automatically.
