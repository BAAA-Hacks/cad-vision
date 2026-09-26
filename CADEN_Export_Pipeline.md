# CADEN Export Pipeline

The laptop-side CADEN pipeline is split into two independent scopes:

1. **SolidWorks Plugin**
   - Extract the current SolidWorks assembly.
   - Export the assembly as a `.glb`.
   - Extract engineering/design metadata into a JSON file.
   - Hand both files to the CADEN Shipper.

2. **CADEN Shipper `.exe`**
   - Receive the `.glb` and metadata JSON from the plugin.
   - Perform any CADEN-side preprocessing/validation.
   - Transfer the resulting files to the Meta Quest.
   - The shipper should not depend directly on SolidWorks APIs.

The intended end-user workflow is:

```text
SolidWorks
    ↓
[Launch in CADEN]
    ↓
SolidWorks Plugin
    ├── model.glb
    └── metadata.json
            ↓
        CADEN Shipper
            ↓
     preprocess / validate
            ↓
          Quest
```

---

# Scope 1 — SolidWorks Plugin

## Technology

Implement as a **C# SolidWorks add-in/plugin** using the regular SolidWorks API.

The plugin is responsible only for converting the currently active SolidWorks project into CADEN's interchange format.

It should not contain Quest-specific networking logic.

---

## Plugin Inputs

Primary input:

```text
Currently active SolidWorks assembly
```

The plugin should determine the active assembly and its referenced parts/subassemblies automatically.

The user should not need to manually select the CAD folder.

---

## Plugin Outputs

The plugin must produce exactly two primary artifacts:

```text
model.glb
metadata.json
```

These are passed to the CADEN Shipper.

The GLB should represent the same active assembly represented by `metadata.json`.

---

## GLB Requirements

Export the currently active assembly to:

```text
model.glb
```

Requirements:

- Preserve the assembly hierarchy as much as the SolidWorks GLB exporter allows.
- Preserve component/node names.
- Preserve per-component transforms.
- Preserve geometry and visual appearance where practical.
- Preserve subassembly nesting where possible.
- Ensure GLB node/component naming can be correlated with objects in `metadata.json`.

The plugin should generate the GLB automatically when the user selects:

```text
Launch in CADEN
```

The user should not have to export the GLB manually.

---

# Metadata JSON

Target structure:

```json
{
  "schemaVersion": "1.0",

  "project": {
    "id": "PROJECT_001",
    "name": "Robot Assembly",

    "units": {
      "length": "mm",
      "mass": "kg",
      "angle": "deg"
    },

    "rootObjectId": "ASSY_001"
  },

  "objects": [],

  "mates": [],

  "interferences": []
}
```

---

# Project Metadata

Required:

```json
{
  "id": "PROJECT_001",
  "name": "Robot Assembly",

  "units": {
    "length": "mm",
    "mass": "kg",
    "angle": "deg"
  },

  "rootObjectId": "ASSY_001"
}
```

Fields:

- Stable project ID
- Project name
- Length unit
- Mass unit
- Angle unit
- Root assembly object ID

All exported numeric values should be normalized to the units declared here.

Do not rely on SolidWorks UI display units internally without explicitly converting them.

---

# Objects

Every unique assembly occurrence and part occurrence in the active assembly should generate an entry in:

```json
"objects": []
```

Example:

```json
{
  "id": "COMP_001",

  "name": "Drive Shaft",
  "type": "part",

  "parentId": "ASSY_002",
  "childIds": [],

  "sourceDocument": "DriveShaft.SLDPRT",
  "configuration": "Default",

  "partNumber": "SHAFT-001",
  "description": "Main drivetrain shaft",

  "suppressed": false,
  "fixed": false,

  "material": {
    "assigned": true,
    "name": "AISI 1020 Steel",
    "density": 7870
  },

  "mass": 0.84,
  "volume": 106700,

  "centerOfMass": [
    0.0,
    0.0,
    42.3
  ],

  "inertia": {
    "ixx": 0.0,
    "iyy": 0.0,
    "izz": 0.0,
    "ixy": 0.0,
    "ixz": 0.0,
    "iyz": 0.0
  },

  "definitionStatus": "under_defined",

  "remainingDOF": [],

  "referenceGeometry": {
    "axes": [],
    "planes": [],
    "points": []
  },

  "dimensions": [],

  "customProperties": {}
}
```

---

# Object Identity

Every part and assembly occurrence needs:

- Stable unique ID
- Name
- Type

Valid types:

```text
part
assembly
```

IDs should represent **component instances**, not merely source files.

Example:

```text
LeftWheel-1
LeftWheel-2
```

may both use:

```text
LeftWheel.SLDPRT
```

but must receive different CADEN object IDs.

A stable deterministic ID system is preferred.

If useful, retain SolidWorks persistent references internally, but expose CADEN-owned IDs in the exported schema.

---

# Hierarchy

For every object:

```json
"parentId": "ASSY_002",
"childIds": [
  "COMP_003",
  "COMP_004"
]
```

Requirements:

- Every non-root object has a parent.
- Assemblies list their direct children.
- Parts normally have no children.
- Preserve nested subassembly structure.

The hierarchy should correspond as closely as possible to the hierarchy exported into the GLB.

---

# Source Information

For every object:

- Source SolidWorks document filename/path
- Referenced configuration
- Part number
- Description

Example:

```json
"sourceDocument": "DriveShaft.SLDPRT",
"configuration": "Default",
"partNumber": "SHAFT-001",
"description": "Main drivetrain shaft"
```

---

# Assembly State

For every object:

```json
"suppressed": false,
"fixed": false
```

Capture:

- Suppressed state
- Fixed/grounded state

---

# Material

Target:

```json
"material": {
  "assigned": true,
  "name": "AISI 1020 Steel",
  "density": 7870
}
```

Capture:

- Whether a material is assigned
- Material name
- Density

If no material is assigned:

```json
"material": {
  "assigned": false,
  "name": null,
  "density": null
}
```

---

# Physical Properties

Capture when available:

- Mass
- Volume
- Center of mass
- Moments/products of inertia

Target:

```json
"mass": 0.84,
"volume": 106700,

"centerOfMass": [
  0.0,
  0.0,
  42.3
],

"inertia": {
  "ixx": 0.0,
  "iyy": 0.0,
  "izz": 0.0,
  "ixy": 0.0,
  "ixz": 0.0,
  "iyz": 0.0
}
```

Physical properties are considered high priority.

If a property cannot be calculated, use `null` rather than inventing a value.

---

# Definition / Constraint State

For every component:

```json
"definitionStatus": "under_defined"
```

Normalize SolidWorks state into one of:

```text
fully_defined
under_defined
over_defined
no_solution
invalid_solution
unknown
```

This is a high-priority field.

---

# Remaining Degrees of Freedom

Desired structure:

```json
"remainingDOF": [
  {
    "type": "rotation",
    "axis": [
      0.0,
      0.0,
      1.0
    ]
  }
]
```

Each DOF should contain:

```text
type:
    translation
    rotation

axis:
    normalized direction vector
```

However, precise remaining DOF extraction may require additional computation and should be treated as a stretch capability.

If exact DOFs cannot be determined reliably:

```json
"remainingDOF": null
```

Do not guess DOFs.

The definition status should still be exported.

---

# Mates

Every SolidWorks mate should be represented in:

```json
"mates": []
```

Example:

```json
{
  "id": "MATE_001",
  "name": "Shaft Concentric",

  "type": "concentric",

  "componentIds": [
    "COMP_001",
    "COMP_002"
  ],

  "references": [
    {
      "componentId": "COMP_001",
      "entityType": "cylindrical_face",
      "entityId": "FACE_001"
    },
    {
      "componentId": "COMP_002",
      "entityType": "cylindrical_face",
      "entityId": "FACE_004"
    }
  ],

  "status": "solved",

  "alignment": "aligned",

  "axis": [
    0.0,
    0.0,
    1.0
  ],

  "limits": {
    "enabled": false,
    "minimum": null,
    "maximum": null
  }
}
```

---

# Mate Requirements

Capture:

- Stable mate ID
- Mate name
- Mate type
- IDs of every involved component
- Referenced CAD entities
- Entity types
- Mate status
- Alignment/orientation
- Axis or direction where applicable
- Minimum/maximum limits where applicable

Normalize mate status into:

```text
solved
suppressed
dangling
over_defined
conflicting
error
unknown
```

---

# Mate References

Desired form:

```json
{
  "componentId": "COMP_001",
  "entityType": "cylindrical_face",
  "entityId": "FACE_001"
}
```

Possible entity types may include:

```text
face
cylindrical_face
planar_face
edge
axis
plane
point
vertex
unknown
```

Entity IDs should be stable when practical.

SolidWorks persistent reference IDs may be used internally to support this.

---

# Reference Geometry

For each object:

```json
"referenceGeometry": {
  "axes": [],
  "planes": [],
  "points": []
}
```

## Axis

```json
{
  "id": "AXIS_001",
  "name": "Shaft Axis",

  "origin": [
    0.0,
    0.0,
    0.0
  ],

  "direction": [
    0.0,
    0.0,
    1.0
  ]
}
```

Capture:

- Stable ID
- Name
- Origin
- Direction

---

## Plane

```json
{
  "id": "PLANE_001",
  "name": "Front Plane",

  "origin": [
    0.0,
    0.0,
    0.0
  ],

  "normal": [
    0.0,
    0.0,
    1.0
  ]
}
```

Capture:

- Stable ID
- Name
- Origin
- Normal

---

## Reference Point

```json
{
  "id": "POINT_001",
  "name": "Pivot",

  "position": [
    20.0,
    10.0,
    0.0
  ]
}
```

Capture:

- Stable ID
- Name
- Position

---

# Dimensions

Each exported object may contain:

```json
"dimensions": [
  {
    "id": "DIM_001",
    "name": "Shaft Diameter",

    "type": "diameter",

    "value": 12.7,
    "unit": "mm",

    "references": []
  }
]
```

Capture:

- Dimension ID
- Name
- Type
- Value
- Unit
- Referenced CAD entities if available

Possible normalized dimension types may include:

```text
linear
diameter
radius
angle
distance
unknown
```

---

# Custom Properties

Export **all available SolidWorks custom properties**.

Target:

```json
"customProperties": {
  "Manufacturer": "REV",
  "Vendor": "REV Robotics",
  "Purpose": "Drivetrain"
}
```

Do not hardcode expected property names.

Any custom property discovered should be copied into this dictionary.

---

# Native Interference Results

If practical, run SolidWorks native interference detection and export results.

Target:

```json
{
  "id": "INTERFERENCE_001",

  "componentIds": [
    "COMP_005",
    "COMP_012"
  ],

  "volume": 250.0
}
```

Capture:

- Stable interference ID
- Involved component IDs
- Interference volume

Interference checking may be made optional if performance becomes a concern.

---

# Plugin Priority Order

## Priority 1

Must work first:

```text
GLB export
stable IDs
hierarchy
source document/configuration
part/assembly types
suppression/fixed state

mates
mate references
mate status

definition status

material
mass
volume
center of mass
inertia

custom properties
```

## Priority 2

Add afterward:

```text
reference axes
reference planes
reference points
dimensions
mate alignment
mate limits
interference detection
```

## Priority 3 / Stretch

```text
precise remaining translational/rotational DOFs
```

Do not delay the MVP for DOF-vector extraction.

---

# Plugin → Shipper Interface

Once export completes, the plugin should hand the two generated files to the shipper.

Recommended interface:

```text
CadenShipper.exe
    --glb "C:\...\model.glb"
    --metadata "C:\...\metadata.json"
```

Equivalent IPC is acceptable later, but command-line arguments are sufficient for MVP.

The plugin should then launch the shipper.

The plugin is finished once the files have been generated and handed off successfully.

---

# Scope 2 — CADEN Shipper `.exe`

The shipper is a **standalone Windows executable**.

It should have no direct dependency on the SolidWorks API.

Inputs:

```text
model.glb
metadata.json
```

Outputs:

```text
Files transferred to the Quest
```

---

# Primary Responsibilities

The shipper owns:

```text
input validation
metadata validation
preprocessing
packaging if necessary
Quest discovery/configuration
network connection
file transmission
progress reporting
error reporting
```

The SolidWorks plugin should not contain any of this logic.

---

# Basic Invocation

Initial MVP interface:

```text
CadenShipper.exe
    --glb model.glb
    --metadata metadata.json
```

The shipper should:

```text
1. Validate both files exist.
2. Parse metadata.json.
3. Verify the schema version.
4. Perform preprocessing.
5. Connect to the Quest.
6. Send both files.
7. Wait for acknowledgement.
8. Report success/failure.
```

---

# Preprocessing Responsibilities

Any processing that is **CADEN-specific rather than SolidWorks-specific** belongs here.

Examples:

```text
Validate JSON schema

Normalize data

Generate derived lookup tables

Create GLB-node ↔ CADEN-object mappings

Clean or normalize names

Generate search indexes

Generate derived engineering properties

Generate deterministic IDs if needed

Compress/package files

Perform data consistency checks

Reject invalid exports
```

Do not put these operations inside the SolidWorks plugin unless SolidWorks itself is required to calculate them.

---

# GLB ↔ Metadata Mapping

The shipper should validate that the exported GLB can be correlated with the metadata.

Ideally:

```text
SolidWorks component
        ↓
CADEN object ID
        ↓
GLB node
```

A mapping may be generated during preprocessing.

Possible future representation:

```json
{
  "COMP_001": {
    "glbNode": "Drive Shaft-1"
  }
}
```

The mapping does not necessarily need to exist in schema version 1.0 if component/node names are sufficient initially.

---

# Packaging

The MVP may send:

```text
model.glb
metadata.json
```

individually.

Alternatively, the shipper may package them as:

```text
design.caden
```

Internally:

```text
design.caden
├── model.glb
├── metadata.json
└── manifest.json
```

Packaging is optional for MVP.

The Quest-side importer should ultimately treat these files as one CADEN design package regardless of transport implementation.

---

# Quest Transport

Preferred architecture:

```text
Laptop
   ↓
local Wi-Fi
   ↓
Quest
```

The shipper should send the design to a receiver running inside the CADEN Quest application.

Use a simple local protocol initially, such as HTTP.

Conceptual request:

```text
POST /design
```

containing:

```text
model.glb
metadata.json
```

The Quest should acknowledge successful receipt.

---

# Quest Configuration

For MVP, the shipper may accept the Quest IP/address through:

```text
configuration file
command-line argument
simple UI
```

Example:

```text
--quest 192.168.1.42
```

Future versions may implement automatic Quest discovery.

Automatic discovery is not required initially.

---

# Shipper UI

The shipper does not need a large application UI.

A minimal interface is sufficient:

```text
CADEN Shipper

Design:
Robot Assembly

Quest:
192.168.1.42

Processing...
✓ Metadata validated
✓ Model validated
✓ Quest connected
✓ Upload complete
```

Since the SolidWorks plugin launches it automatically, the shipper can primarily act as a progress/error window.

---

# Error Handling

At minimum detect:

```text
GLB missing
JSON missing
invalid JSON
unsupported schema version
invalid object hierarchy
missing root object
duplicate IDs
Quest unreachable
connection timeout
upload failure
Quest rejects package
```

Return a non-zero process exit code on failure so the SolidWorks plugin can detect unsuccessful launches.

---

# Separation of Responsibilities

Keep this boundary strict:

```text
SOLIDWORKS PLUGIN

Knows:
- SolidWorks
- assemblies
- mates
- geometry
- configurations
- mass properties
- CAD metadata
- GLB export

Does NOT know:
- Quest protocol
- networking implementation
- headset discovery
- transfer retries
```

```text
CADEN SHIPPER

Knows:
- CADEN schema
- GLB
- metadata preprocessing
- validation
- packaging
- Quest connection
- networking
- transfer

Does NOT know:
- SolidWorks API
- mates API
- component traversal
- CAD extraction
```

This allows either component to evolve independently.

---

# MVP End State

The complete MVP experience should ultimately be:

```text
Engineer opens Robot.SLDASM
        ↓
Clicks "Launch in CADEN"
        ↓
SolidWorks add-in:
    exports model.glb
    exports metadata.json
        ↓
starts CADEN Shipper
        ↓
Shipper:
    validates
    preprocesses
    sends package
        ↓
Quest:
    receives package
    runtime-imports GLB
    loads metadata
        ↓
CADEN design review begins
```

The user should not need to manually select the GLB, CAD folder, metadata file, or transfer files to the Quest.

---

# Core Implementation Boundary

Anything requiring SolidWorks knowledge belongs in the plugin.

Anything CADEN-specific that can operate on the exported files belongs in the shipper.
