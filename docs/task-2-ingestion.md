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

## Network receiver and sender

Attach `CadDesignReceiver` to the runtime host in the build scene. It starts on
enable in Play Mode and listens on TCP port 8085. In the Editor, use **CAD Vision >
Start receiver (Play Mode)**. `AllowLan=false` restricts it to loopback; the default
allows the laptop to connect on the same local network. This MVP uses plain HTTP
without authentication and is intended for a trusted development network.

From the repository root:

```powershell
python tools/send_design.py --glb TestASM.glb --metadata TestASM_metadata_sample.json
# Same command for a Quest on Wi-Fi:
python tools/send_design.py --quest <quest-ip> --glb TestASM.glb --metadata TestASM_metadata_sample.json
```

Wire contract for the standalone shipper: `POST /design`, `Content-Type:
application/zip`, and a positive `Content-Length`. The ZIP contains exactly
`model.glb` and `metadata.json` at the root. No multipart, chunked encoding,
`Expect: 100-continue`, additional files, or nested paths. Files are read into
bounded memory; no archive paths are extracted to disk. Limits: 100 MiB GLB,
8 MiB metadata, and 120 seconds for receipt plus import. Uploads are serialized;
overlapping requests get HTTP 409. Incomplete uploads never reach the importer.

HTTP 200 is returned only after the Unity model and CAD registry are published:

```json
{"success":true,"message":"Loaded 3 CAD objects.","objectCount":3,"revision":1}
```

Failures return a non-200 status and `success:false`; the sender exits nonzero.
A timeout/disconnect can leave the sender without an acknowledgement. There is
no automatic retry or idempotency key yet. A complete upload already importing
may finish even if the sender disconnects; receiver shutdown/timeout cancels
pending import through the loader's cancellation token.

The Python sender is a test client, not the Task 1 standalone shipper executable.
Its ZIP names and acknowledgement handling define the current integration contract.

## Quest smoke build

`CadReceiverSmokeBuild.Prepare()` creates `Assets/ReceiverSmoke/ReceiverSmoke.unity`
and material assets derived from TestASM to retain its runtime shader variants.
It preserves the existing workspace scene and does not modify the application's
build-scene list. If the workspace scene is untitled, it saves it to a new
`WorkspaceSnapshot.unity` file first.

With Android active, `CadReceiverSmokeBuild.QueueBuild()` produces
`.utmp/CADVisionReceiverTest.apk` using ARM64/IL2CPP and the separate application
ID `com.cadvision.receivertest`. The test build uses Input System Package (New),
since Unity rejects Both input handling on Android. Temporary product name,
application ID, scripting backend, CPU architecture, and input-handling settings
are restored after the build. Status is
written to `.utmp/receiver-build-status.txt`.

## Automatic startup loading

Put exactly one `.glb` and its matching `.json` directly in `Assets/CadFiles`
(the existing `Assets/cadFiles` spelling also works). Names can differ and may
change; selection uses case-insensitive extensions and ignores `.meta` files.
Enter Play Mode to load the pair automatically. No receiver upload or scene
component setup is required. JSON must still follow the CAD metadata contract.

Every player build validates the pair and bundles the raw files in StreamingAssets.
The Quest reads these bundled files automatically at startup. Changing the files
requires rebuilding/reinstalling the APK; this is not a live desktop-folder sync.
Missing or duplicate files fail validation rather than selecting an arbitrary pair.

## Verification and remaining integration

The receiver smoke camera now uses Input System `TrackedPoseDriver` bindings for
`<XRHMD>/centerEyePosition`, `centerEyeRotation`, and `trackingState`. Imported CAD
roots remain independent world objects, so the tracked viewer can move around
them during the session. This does not persist room placement across restarts.
The updated APK was installed and TestASM imported successfully; physical
head-turn and walking verification is pending user confirmation.

Android builds require the `GLTFAST_BUILTIN_RP` scripting define with the current
Built-in rendering configuration. glTFast 6.20.0 detects the installed URP package
and otherwise excludes its Built-in material generator from players (but not the
Editor). The Quest diagnostic build confirmed this caused an
`InvalidOperationException` during `GltfImport` construction. Keep the define
while this project uses Built-in rendering alongside the installed URP package.
Unexpected receiver exceptions are now logged on-device for diagnosis.

Run `CadIngestionTests` in Unity Test Runner (Edit Mode). These cover malformed
metadata, identity/hierarchy failures, metadata retention, malformed GLBs,
registry replacement, and real FRED import/reload/failure cleanup. The FRED test
is skipped when the local GLB or generated JSON is missing.

Verified in Unity 6000.6.0f1: all 17 Edit Mode tests passed, including two real
FRED imports (261 mapped nodes each) and retaining the loaded model after a
rejected package. This is not yet a Quest or Play Mode validation result.

All seven `CadReceiverTests` passed in Edit Mode, including a real TestASM HTTP
upload into the runtime, incomplete and oversized requests, concurrent-request
rejection, and shutdown cancellation. Run these tests with Play Mode stopped.

Still to implement/verify:

- Quest 3S ARM64/IL2CPP receiver verified over Wi-Fi on 2026-09-26: TestASM
  returned success with three mapped CAD objects, then replacement returned
  revision 2 with three objects. No Unity error entries appeared for that app
  process after both imports. The user confirmed the model is visible with
  normal materials in the headset.
- Final shipper mapping agreement and real SolidWorks metadata pair validation.
- Play Mode/XR interaction integration, broader material shader inclusion,
  physical scale/orientation check, and headset performance.
- Progress reporting, optimized/precise colliders, and very large assembly handling.

Dependencies installed via Unity Package Manager: glTFast 6.20.0 and Newtonsoft
JSON 3.2.2. Pipeline is an Editor automation dependency. The dependency installer
menu is retained for explicit repair/setup; it does not run automatically.
