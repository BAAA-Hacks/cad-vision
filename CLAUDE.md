# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

- `cad-vision-game/` — the Unity project (Unity **6000.6.0f1**, URP, Input System, Meta XR SDK v207 + OpenXR, targeting Meta Quest/Android). All real code lives in `cad-vision-game/Assets/Scripts/`.
- `solidworks-plugin/main.cs` — placeholder for a SolidWorks add-in (currently empty).

There are no `.asmdef` files, so all scripts compile into the default `Assembly-CSharp`. The only scene in build settings is `Assets/Scenes/ManipulationTest.unity`.

## Building and testing

There is no CLI build script; work is done in the Unity Editor. For headless verification, Unity can be run in batch mode (editor path depends on the Hub install, typically `C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe`):

```
Unity.exe -batchmode -quit -projectPath cad-vision-game -logFile -          # compile check
Unity.exe -batchmode -projectPath cad-vision-game -runTests -testPlatform EditMode -testResults results.xml
```

`com.unity.test-framework` is installed but no tests exist yet; batch mode fails if the project is already open in the Editor.

When adding/moving assets or scripts, keep the accompanying `.meta` files in sync — scene references are by GUID.

## Architecture

`CADVisionManipulationService` is the single owner of CAD model state; every input path is a thin adapter that translates input into calls on it by **string ID**.

- **`CADObject`** — identity component (`id`) on each part/subassembly GameObject. Captures its original local transform in `Awake` for reset. Subassemblies are CAD objects that parent other CAD objects.
- **`CADVisionManipulationService`** — in `Start`, registers every `CADObject` in the scene (including inactive ones) into an ID dictionary, skipping empty and duplicate IDs. Exposes the whole command surface: select, highlight (via `MaterialPropertyBlock` on `_BaseColor`/`_Color`, never by mutating materials), hide/show/isolate (isolate keeps descendants of the isolated object visible), per-object move/rotate/reset, and whole-model transform on `modelRoot`. Unknown IDs log a warning rather than throwing. Keep new operations ID-based so every adapter can share them.
- **Input adapters** (none own selection state):
  - `CADSelection` — desktop mouse raycast → `Select`/`ClearSelection`.
  - `CADDebugControls` — number-key shortcuts for exercising the service with hardcoded test IDs (`SUBASSEMBLY_001`, `COMP_001`, `COMP_003`).
  - `CADXRRaySetup` + `CADXRSelection` — Quest ray selection. `CADXRRaySetup` sits on the same GameObject as the service and runs with `[DefaultExecutionOrder(100)]` so the service has already registered objects. For each registered `CADObject` it attaches a `ColliderSurface` + `RayInteractable` per existing collider (it does not create colliders), assigning colliders to their *nearest* `CADObject` ancestor, then adds/initializes a `CADXRSelection` that forwards `PointerEventType.Select` to `Select(id)`. `ConfigureRegisteredObjects()` is idempotent — it reuses existing component slots — so it can be re-run after colliders change.
  - `CADXRGrab` — right-controller grip (`OVRInput`) grabs the *currently selected* object and drives it via `SetObjectWorldPose`. It is a conventional rigid VR pickup: the grab-start pose relative to the controller is held, so rotation follows the controller 1:1. Beyond `reachDistance`, a depth-only assist adds `(gain - 1)` × the controller's per-frame movement *along the controller→object axis* to the hold distance (gain rises exponentially toward `1 + maxExtraGain`); lateral movement and rotation stay rigid 1:1, and within reach the pickup is exactly rigid. It uses the existing `OVRCameraRig.rightControllerAnchor` and does not use Meta's Grabbable/Transformer stack.

## Current project boundaries

- Task 2 owns runtime CAD import, GLB loading, CAD ID ↔ GameObject resolution, and model-root creation.
- Task 3 owns all visual interaction state: selection, highlighting, visibility, object movement, model movement, and XR input.
- CADEN/AI code must call Task 3 APIs by CAD ID and must never directly mutate CAD renderers or transforms.
- Avoid adding duplicate object registries once Task 2's registry becomes available; adapt Task 3 to consume it instead.
- Do not use bare GameObject names as unique CAD identifiers.

## XR interaction rules

- Meta XR interaction code should remain in adapter/setup classes, not in CADObject.
- `CADObject` should stay SDK-agnostic.
- Existing Quest ray-selection flow is proven working:
  controller ray → RayInteractable → CADXRSelection → CADVisionManipulationService.Select(id)
- Runtime XR setup should remain idempotent.
- Do not manually wire per-object UnityEvents for imported CAD objects.
- Preserve desktop mouse selection while adding XR controls.

## Hackathon priorities

Prefer the shortest path to a working demo over generalized infrastructure.
Do not:
- change render pipelines
- restructure the entire Unity project
- introduce new packages unless necessary
- rewrite working systems without a concrete benefit

Current priority order:
1. XR grabbing/manipulation
2. whole-model move/rotate/scale
3. passthrough
4. hand tracking
5. Task 2 real CAD integration
6. CADEN tool integration

## Change discipline

Before making broad changes, inspect the existing implementation and preserve working behavior.
For Meta XR SDK questions, prefer inspecting the installed package source over relying on outdated examples.
Do not modify scene YAML unless necessary; if a change can be made safely through scripts or prefabs, prefer that.
Always summarize files changed and any required Unity Inspector actions.