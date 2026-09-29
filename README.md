# CADVision + CADEN AI

Bring your SolidWorks designs into mixed reality on Meta Quest. Take them apart with your hands, ask an AI assistant about them, and review them together with a teammate.

The application connects a runtime-loaded **GLB** with **engineering metadata JSON**. Geometry supplies the visual model; metadata supplies component identity, hierarchy, properties, and exported mechanical relationships.

**Inspection transforms do not rewrite the source SolidWorks design or its mates.**

## Table of contents

| Section | What you'll find |
| --- | --- |
| [Install the app](#install-the-app) | [Meta release channel (Recommended)](#meta-release-channel-recommended) or [APK sideloading](#alternative-sideload-the-apk). |
| [Send a design](#wifi-transfer-recommended) | [WiFi transfer (Recommended)](#wifi-transfer-recommended) or [optional USB](#optional-usb-transfer). |
| [Review features](#review-features) | [Inspection tools](#review-features), [controls](#controls), and [two-person multiplayer](#two-person-multiplayer). |
| [CADEN AI](#caden-ai) | [Capabilities](#caden-ai) and [API keys / headset setup](#api-keys-and-headset-setup). |
| [Repository layout](#repository-layout) | Source directories and their roles |
| [Run the Unity project](#run-the-unity-project) | [Developer setup and Quest builds](#run-the-unity-project), and [model transfer](#send-a-model-to-a-running-receiver). |
| [Developer configuration](#developer-configuration) | [Configuration paths and persistence](#developer-configuration), and the [desktop harness](#desktop-harness). |
| [Validation and limitations](#validation-and-limitations) | [Offline checks, device testing, and engineering boundaries](#validation-and-limitations). |
| [License](#license) | MIT License. |

## Install the app

### Meta release channel (Recommended)

1. **Access the Meta Quest app:** Open the [CADVision release-channel link](https://www.oculus.com/experiences/1239050502635661/release-channels/969512065511035/) and sign in with the Meta account used on your headset.
2. **Install CAD Vision:** Follow Meta's prompts to join the channel and install the app on your headset. Access depends on the channel's enrollment settings.
3. **Launch CAD Vision:** Open the app from your headset's app library.
4. **Download and install the SolidWorks plugin:** On your PC, download `CADVision-SolidWorks-Setup.exe` from the [latest GitHub release](https://github.com/BAAA-Hacks/cad-vision/releases/latest) and run it. In SolidWorks, enable **CAD Vision** through **Tools > Add-ins** if necessary.

This route does not require downloading the APK manually, a USB cable, or Developer Mode. Continue with [WiFi transfer (Recommended)](#wifi-transfer-recommended).

### Alternative: sideload the APK

Download `CADVision.apk` from the [latest GitHub release](https://github.com/BAAA-Hacks/cad-vision/releases/latest) for a manual installation. This alternative requires **Developer Mode** and USB debugging.

1. **Enable Developer Mode:** Enable it for your headset through the Meta Horizon mobile app.
2. **Connect your headset:** Connect it to your PC over USB and allow USB debugging in the headset.
3. **Install the APK:** Install `CADVision.apk` using Meta Quest Developer Hub, or run:

   ```powershell
   adb install -r CADVision.apk
   ```

4. **Launch CAD Vision:** Open **CAD Vision** from **Library > Unknown Sources** on the headset.
5. **Install the SolidWorks plugin:** Download, install, and enable the add-in as described above.

## WiFi transfer (Recommended)

For normal use, put the PC and headset on the **same Wi-Fi network**, open CAD Vision on the headset, and send your part or assembly from the SolidWorks add-in. No USB cable or Developer Mode is required for the Wi-Fi transfer itself once the app is installed. The network must allow the PC to reach the headset.

### Optional USB transfer

For large models or slow Wi-Fi, USB provides a wired alternative that may transfer faster, depending on the cable and connection. This route uses **ADB**, so Developer Mode and USB debugging are required.

Connect the headset, approve USB debugging, keep CAD Vision open, and forward a local PC port to the headset receiver:

```powershell
adb forward tcp:18085 tcp:8085
```

Send the exported GLB/metadata pair through that forwarded port using the development sender (requires Python):

```powershell
python tools/send_design.py --glb "path/to/model.glb" --metadata "path/to/metadata.json" --quest 127.0.0.1 --port 18085
```

If using an exporter with a configurable receiver URL, use `http://127.0.0.1:18085/design`. This tunnels the same model-transfer protocol over USB; it does not replace internet access for CADEN API calls or multiplayer services. Remove the forwarding when finished with `adb forward --remove tcp:18085`.

CADEN is optional; model inspection does not require AI keys. See [API keys and headset setup](#api-keys-and-headset-setup) to get voice and chat running.

## Review features

- **Passthrough and VR:** place the model in your physical workspace or use a virtual environment with walking, snap turning, and teleportation.
- **Controllers and tracked hands:** ray-select objects and use gestures to move, rotate, and scale parts, selections, or the complete model.
- **Assembly navigation:** enter nested subassemblies, inspect their children, and move back up the logical hierarchy.
- **Contextual menus and multi-selection:** inspect individual occurrences or manipulate groups together.
- **Focus:** emphasize a part, assembly, or selection while ghosting surrounding geometry. Focus preserves context rather than hiding everything else.
- **Detach and reattach:** move a component independently for inspection, then return it to its assembly.
- **Display modes:** shaded surfaces, shaded surfaces with feature edges, wireframe, and optional selection outlines.
- **Scoped resets:** restore selected objects, the current assembly, object sizes, or the whole model.
- **Floating windows:** move menus by their edges and resize them from their corners using controllers or hands.
- **Voice or text assistance:** ask CADEN about engineering data or request scene actions, including focusing and exploding components for inspection.

### Controls

| Action | Controllers | Tracked hands |
| --- | --- | --- |
| Select / move | Trigger | Index pinch |
| Scale and rotate | Both triggers | Pinch with both hands |
| Context menu | Trigger an already-selected part | Pinch an already-selected part |
| Main menu | Left menu button | Main menu from a context menu |
| Talk to CADEN | Hold or tap **Y** | Hold or tap middle-finger pinch |
| Move / resize a window | Trigger on its edge / corner | Pinch on its edge / corner |

### Two-person multiplayer

The main menu provides **Host passthrough**, **Host virtual**, **Join**, and **Leave**. A host loads a model and shares a room code; the joining headset receives the model package automatically.

Rooms use Unity Multiplayer Services, Relay, and Netcode for GameObjects. The host coordinates component ownership and synchronized transforms so two users do not manipulate overlapping targets simultaneously. Passthrough rooms use Meta shared spatial anchors for a common physical reference frame; virtual rooms use a shared virtual frame and display remote head presence.

Both headsets need internet access. Passthrough sharing also needs successful anchor creation and localization. Rooms currently support **two participants**. Some operations, including reset controls and CADEN scene mutations, are restricted while in a shared room; the UI reflects these restrictions. Multiplayer does not imply shared CADEN conversation or project memory.

To start a shared review:

1. Run the **same app version** on both headsets and connect both to the internet.
2. For same-room passthrough, enable **Enhanced Spatial Services** in headset privacy settings on both devices and use the same scanned physical space. Virtual rooms support reviewing from different places.
3. On the headset with the model, open **Main Menu > Shared room** and choose **Host passthrough** or **Host virtual**.
4. Choose **Join** on the other headset and enter the host's room code.

Selection, visibility, and display mode remain local to each headset; shared manipulation does not synchronize every UI setting. CADEN can answer questions in a room but cannot move parts. If joining fails with a DNS/network error, check headset connectivity and try a regular Wi-Fi network instead of a laptop hotspot.

## CADEN AI

CADEN is a C# assistant integrated into the Unity runtime, with a separate Windows desktop harness for development. Gemini chooses structured tools; deterministic code retrieves engineering evidence and performs authorized application actions.

Available capabilities include:

- **Model discovery and properties:** search names, IDs, and properties; inspect materials, mass, volume, center of mass, inertia, custom properties, and native state when exported.
- **Hierarchy and query scope:** navigate assemblies and constrain searches to a selected subtree through explicit scope tools.
- **Mechanical relationships:** inspect mates, find connections across assembly boundaries, and query neighborhoods or paths through a component/mate multigraph.
- **Issue review:** read findings and recorded evidence, revalidate subjects, and set dispositions to Open, Resolved, or Ignored. Startup/load scans include missing material and native constraint-state checks; connectivity checks require sufficient scoped export coverage.
- **Project memory:** persist design intent, requirements, and notes separately from immutable CAD facts, with provenance and stale-reference handling.
- **Scene actions:** select, focus, hide/show, detach groups, explode assemblies or selections for inspection, reattach, and reset objects through Unity's manipulation service, subject to runtime restrictions.
- **Export diagnostics:** explain unavailable capabilities and incomplete data rather than treating missing evidence as a successful check.

ElevenLabs supplies realtime speech recognition and streamed speech output. The in-world panel shows conversation history, live transcription, processing feedback, and errors. Controller Y and tracked-hand middle-finger pinch gestures provide voice input. CADEN aims for concise spoken answers.

The core uses bounded results, metadata indexes, cached result references, and context pruning to avoid resending unnecessary tool history. Structured responses preserve project/snapshot identity, availability, coverage, and errors. Memory and issue mutations use revision/idempotency protections and durable storage. Missing mass properties do not establish a mating problem, and under-defined motion is not automatically a defect.

### API keys and headset setup

CADEN needs your own Gemini API key for chat. For voice input and spoken replies, also supply an ElevenLabs API key and voice ID. Keep the headset connected to the internet and allow microphone access when using voice.

For the released Quest app, create a local `.env` file with your own credentials:

```dotenv
GEMINI_API_KEY=your-key
GEMINI_MODEL=gemini-flash-latest
ELEVENLABS_ENABLED=true
ELEVENLABS_API_KEY=your-key
ELEVENLABS_VOICE_ID=your-voice-id
```

After opening the app once, enable Developer Mode, connect over USB, and approve USB debugging for this ADB configuration step. Copy the file to the headset:

```powershell
adb shell mkdir -p /sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN
adb push .env /sdcard/Android/data/com.DefaultCompany.cadvisiongame/files/CADEN/.env
```

Press **New chat** in the CADEN panel to reload configuration. Set `ELEVENLABS_ENABLED=false` for text-only replies; this disables speech output, not the separate voice-input capability. Typed chat needs only Gemini configuration. If building with a different Android application ID, replace `com.DefaultCompany.cadvisiongame` in the device path.

## Repository layout

| Path | Purpose |
| --- | --- |
| `solidworks-plugin/` | SolidWorks integration source and packaged installer. |
| `cad-vision-game/` | Main Unity/Quest project; open this directory in Unity Hub. |
| `cad-vision-game/Assets/Scripts/Interaction/` | Menus, selection, gestures, and presentation. |
| `cad-vision-game/Assets/Scripts/CADVision/Runtime/` | Runtime GLB/metadata ingestion and network receiver. |
| `cad-vision-game/Assets/Scripts/CADEN/` | Unity assistant, voice, configuration host, and shared Core sources. |
| `CADEN/` | Desktop harness, offline checks, prompt source and configuration example. |
| `tools/` | Development utilities, including model transfer and synthetic fixture generation. |

The shared Core targets .NET Standard 2.1. Desktop and Checks reference the sources used by Unity; do not copy the desktop runtime or generate a duplicate Core DLL into Unity. There is no Python backend for CADEN; Python is used by optional development utilities.

## Run the Unity project

1. Install **Unity 6000.6.0f1**, matching `cad-vision-game/ProjectSettings/ProjectVersion.txt`. For Quest builds, include Android Build Support, SDK/NDK, and OpenJDK.
2. Open **`cad-vision-game/`** in Unity Hub and allow package restoration. Dependencies include Meta XR, OpenXR, glTFast, Newtonsoft.Json, and Unity multiplayer packages.
3. Open `Assets/Scenes/ManipulationTest.unity`, the currently enabled build scene.
4. In Play Mode, choose **CAD Vision > Load local design (Play Mode)** and select a matching GLB and metadata JSON. Use **CAD Vision > CADEN > Open panel (Play Mode)** to open the assistant panel.
5. For Quest, switch the build target to Android and use **Build And Run**, or **CAD Vision > Build Quest Test APK** to produce `Builds/CADVision-Multiplayer-Test.apk`.

Multiplayer requires a Unity project configured for Authentication and Multiplayer/Relay services. Passthrough rooms additionally depend on the Meta shared-anchor configuration and device permissions. Importing source or compiling an APK alone does not configure those services.

### Send a model to a running receiver

The runtime receiver accepts a matched GLB/JSON pair on port **8085**. In the Editor, start it through **CAD Vision > Start receiver (Play Mode)**. With Python installed, run from the repository root:

```powershell
python tools/send_design.py --glb "path/to/model.glb" --metadata "path/to/metadata.json" --quest <headset-ip>
```

Omit `--quest` for the local Editor receiver. The headset and sender must be reachable over the network. This development receiver uses unauthenticated HTTP; use it on a trusted network.

Use metadata produced for the exact GLB, including its component-to-node mapping. Names alone are not reliable identifiers for repeated parts. Synthetic mapping fixtures are useful for import tests but are not engineering evidence.

## Developer configuration

Copy `CADEN/.env.example` to `CADEN/.env` and set `GEMINI_API_KEY` (or `GOOGLE_API_KEY`). Set `GEMINI_MODEL` to the model available to your account. For spoken responses, also set `ELEVENLABS_ENABLED=true`, `ELEVENLABS_API_KEY`, and `ELEVENLABS_VOICE_ID`. Voice input requires an ElevenLabs key and microphone permission.

- **Editor/desktop:** use the repository's `CADEN` configuration directory by default.
- **Quest/player:** use `Application.persistentDataPath/CADEN/.env`. The headset has its own configuration and storage; changing the PC file does not update it.
- **Credentials:** keep `.env` files out of Git and Unity Assets. API calls require network access and use provider quota/billing.

Reload CADEN or start a new chat after configuration changes. Model loading and issue scanning do not themselves require a Gemini request. Project memory and issue sidecars persist separately from session conversation context; preserve the corresponding project association when continuing the same design.

### Desktop harness

On Windows with the .NET 10 SDK, run from the repository root:

```powershell
.\CADEN\run-caden.cmd
```

The launcher uses a locally installed SDK under `CADEN/.tools/dotnet` when present, otherwise `dotnet` on PATH. This harness supports metadata/chat development without running Unity; Quest scene actions require the Unity host.

## Validation and limitations

Run the offline Core checks from the repository root:

```powershell
dotnet run --project CADEN/Checks/Checks.csproj
```

Unity also includes interaction and multiplayer tests under `Assets/Editor/Tests`, plus **CADEN > Run offline integration checks**. Offline checks use fake API transports; live API tests are separate and consume quota. Hardware behavior, shared-anchor localization, and two-headset synchronization still require device testing.

Engineering capabilities depend on export content and coverage. CADEN is not a replacement CAD solver, and the runtime does not establish interference or remaining degrees of freedom from visualization geometry. Visual scaling and detaching do not change engineering properties. Unavailable mate data can disable mechanical analysis while property and hierarchy queries remain usable.

Further implementation notes:

- [Mechanical query contract](cad-vision-game/Assets/Scripts/CADEN/Core/tools/MECHANICAL_CONTRACT.md)
- [Issue engine](cad-vision-game/Assets/Scripts/CADEN/Core/primitives/operations/issues/ENGINE.md)
- [Memory contract](cad-vision-game/Assets/Scripts/CADEN/Core/tools/MEMORY_CONTRACT.md)

## License

[MIT](LICENSE).
