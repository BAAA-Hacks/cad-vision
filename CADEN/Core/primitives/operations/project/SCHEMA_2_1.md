# Schema 2.1 importer mapping

The canonical loader accepts exactly 1.0 and 2.1. The original document and schema label
are retained; snapshot identity hashes the original input. Legacy 1.0 behavior remains.
Unsupported versions still fail explicitly. No SolidWorks API calls or geometry inference
are part of this importer.

For 2.1:

- Hierarchy, IDs, native definitionStatus, scalar properties and dimensions use the existing
  typed validation. An exporter identity label does not automatically grant durable identity.
- Material assigned=null/absent is Missing when its other known fields are well formed.
  It is not assigned=false. An explicit false still represents unassigned material.
- fixedState=not_applicable_root_document maps to NotApplicable only on the identified root
  assembly with null parent and absent/null fixed. Contradictory data is Invalid.
- project.units is the authoritative numeric unit declaration. Volume uses its explicit
  volume unit, not an inferred cube of the length unit. Graph aggregates use the same
  validated availability and convert mass/volume to kg/m^3 once. documentUnits and toSI
  remain exporter declarations for inspection, not additional conversions.
- Reported material density can remain available with a supported explicit density unit.
  effectiveDensity never substitutes for material density or assigned material.
- Relevant non-complete extractionStatus blocks use of a reported field conservatively.
  Malformed status is Invalid. Partial data is retained raw but not represented as complete.
- A well-formed empty collection with explicit complete extraction is Available and empty.
  An empty collection without that evidence remains Missing. Complete means exporter-reported
  completeness, not independent verification or a successful engineering check.
- Center of mass and inertia are available with complete mass-property extraction, supported
  units and the exact schema 2.1 coordinateSystem declaration used by AssemTest2:
  `SolidWorks root document axes; positions/transform translations in project.units.length; inertia about center of mass; directions and rotations dimensionless`.
  This versioned mapping identifies root-document axes, root-origin XYZ coordinates for
  center of mass, and object-center-of-mass reference for inertia. Values and cross terms
  remain exactly as exported. SpatialReference records the mapping and exporter-declared
  verification level. It does not establish principal moments, cross-term sign conversion,
  or Unity coordinate transforms. Unrecognized declarations retain SPATIAL_REFERENCE_UNMAPPED.
  Reference geometry and DOFs still require their own mappings.

The public inertia property displays SolidWorks' Lxx through Lzz labels (the center-of-mass,
output-axis section), including symmetric counterparts. componentSourceFields maps each
label to the unchanged canonical/exported ixx through iyz keys. No Y/Z swap, sign change or
rounding is performed. Principal P values/axes and output-origin I values are not computed;
the response distinguishes these and notes the undeclared cross-term sign convention.

MetadataValue.SourceEvidence retains defensive copies of relevant extraction/native/source
annotations. get_object_details exposes it as sourceEvidence. get_model_summary exposes
exportContext with schema, declared units/coordinate system, extraction status, mapping status
and warnings. These source annotations are not validated engineering conclusions. Extra
nested dimension annotations, including tolerance metadata, remain source data; the current
dimension validator establishes the dimension's basic shape/value/unit, not tolerance analysis.

Graph topology accepts 2.1 and scoped projections retain the same schema. No scope membership,
mate suppression, mate satisfaction or complete extraction is inferred. The AssemTest2 export
therefore loads for general queries while its mechanical capability stays Unavailable.

Offline audit from the CADEN directory:

```powershell
.\.tools\dotnet\dotnet.exe run --project Checks/Checks.csproj -- --metadata "C:\path\metadata.json"
```

This loads the supplied file, prints diagnostics, retrieves the first 32 objects' basic
properties, and explicitly scans issues in memory. It never calls Gemini, saves memory,
replaces desktop metadata, or modifies the input file. Failures return a nonzero exit code.

Schema21Checks covers supported/unsupported evidence, unknown/inapplicable states, explicit
volume/density units, defensive provenance copies, graph projection and shared tool responses.
