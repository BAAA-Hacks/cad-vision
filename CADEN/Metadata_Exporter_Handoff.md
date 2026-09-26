# Metadata integration handoff: AssemTest2 export

Reviewed input: `metadata.json`, schema `2.1`, project `AssemTest2.SLDASM`.
This is a proposed contract delta, not a corrected export or a claim that missing
SolidWorks evidence has been verified. No exporter source code was supplied.

## Ownership and minimum path to a test

| Owner | Change | Needed for |
| --- | --- | --- |
| CADEN | Support schema 2.1 through a validated importer mapping; retain 1.0 compatibility | Loading this file at all |
| Exporter | Add actual nullable mate suppression state | Confirmed-active mechanical queries |
| Exporter | Add explicit occurrence membership, mate selection and coverage per assembly/configuration | Scoped mechanical queries and trustworthy isolation checks |
| CADEN | Map root material unknown/not-applicable correctly | Avoiding an invalid-property classification for an honest unknown |
| Joint, later | Structured spatial frame/unit and extraction-status mapping | Spatial properties and complete-empty collection semantics |

The shortest route is parallel work on the first three rows. General object/property/
hierarchy testing can start once CADEN supports 2.1. Do not downgrade the export's schema
label just to pass the current loader. CADEN must not invent suppression or coverage.

## Exporter change 1: suppression on every mate

Both Coincident1 and Concentric1 currently omit `suppressed`. Add it independently
of `status`, `satisfaction`, native errors and lockRotation:

```diff
 {
   "name": "Coincident1",
   "status": "unknown",
+  "suppressed": null,
   "satisfaction": "unknown"
 }
```

The null above illustrates the field shape, not the desired value for this assembly:

- `false`: the exporter verified the mate is unsuppressed in the scoped configuration.
- `true`: the exporter verified it is suppressed.
- `null`: the read was unavailable/failed; preserve the uncertainty.

CADEN V1 traverses only explicitly unsuppressed mates between explicitly unsuppressed
occurrences. Null is valid uncertainty but excludes that relationship from traversal.
Do not infer false from nativeErrorCode=0, an existing endpoint, status=unknown or
lockRotation=false. Preserve the native evidence and unknown satisfaction already exported.

## Exporter change 2: add top-level mechanicalScopes

The following illustrates a conservative declaration for the supplied records. Partial
means these are known records, with exhaustiveness not yet established. The exporter must
confirm the configuration/ownership and set the real source/reason before emitting it.

```json
{
  "mechanicalScopes": [
    {
      "scopeAssemblyId": "ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44",
      "configuration": "Default",
      "source": "SolidWorks metadata exporter",
      "reason": "Supplied occurrences and mates are listed; exhaustive scoped extraction has not been verified.",
      "membershipCoverage": "Partial",
      "mateCoverage": "Partial",
      "occurrenceIds": [
        "COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716",
        "COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82"
      ],
      "mateIds": [
        "MATE_ea16ddbe008f2c3b2d36266ee06f97286603376752b965165959a13e769f3154",
        "MATE_059f6caae397a7f95a3e70d25310285242af61635cbea11b72d59d64f53816f8"
      ]
    }
  ]
}
```

Coverage strings are case-sensitive: `Complete`, `Partial`, `Unavailable`, `Invalid`.

- Membership includes mechanical occurrences with zero mates. Do not derive it solely
  from mate endpoints. Do not automatically insert the root assembly as a vertex.
- Each scoped mate ID refers to an existing global mate record with exactly two distinct
  endpoints, both listed in occurrenceIds. Preserve both parallel mates in this export.
- Complete membership requires exhaustive occurrence enumeration for that scope.
- Complete mate coverage requires exhaustive mate extraction for that scope. Receiving
  two mates, or reporting `attempted_requires_live_verification`, does not prove it.
- If even scoped membership/relationships cannot be established, use Unavailable rather
  than treating a guessed selection as Partial. Invalid denotes supplied invalid data.
- Scope records must agree with extractionStatus; never attach a blanket Complete claim
  to an unverified extraction. Different configurations require explicit separate scopes.
- Membership completeness and mate completeness are independent. Neither proves every
  suppression state is known. Unknown suppression still limits connectivity conclusions.

With verified suppression=false and Partial scoped coverage, CADEN can return a confirmed
path through the known mates. It cannot prove isolation/disconnection or claim globally
shortest paths. Complete coverage is not required merely to test positive connectivity.

## CADEN changes, not exporter workarounds

The current loader rejects 2.1 before evaluating fields. A version-only in-memory audit
(not a supported migration) showed valid hierarchy, mass, volume and part dimensions.
CADEN should add deliberate schema support and preserve the richer export provenance.

Root material.assigned=null currently becomes Invalid because the old material shape
requires a boolean. Preserve unknown/not-applicable meaning during import; do not have
the exporter replace null with false. Root fixed=null with fixedState=
not_applicable_root_document likewise should not become a floating component.

Both parts explicitly report material.assigned=false and body material assigned=false.
Their reported mass/effectiveDensity is not evidence of assigned material. Keep those
concepts separate. Unknown mate satisfaction must remain unknown despite native code 0.

## Spatial follow-up (not a blocker for basic mechanical queries)

The export provides project units, conversion factors, a coordinateSystem description,
center of mass, inertia and reference geometry. CADEN's older rules currently mark those
spatial properties unavailable. Agree a machine-readable mapping for frame identity,
tensor axes/reference point, units and quantity provenance rather than parsing free text.
CADEN must also interpret per-field extractionStatus before treating empty arrays as
confirmed empty. No immediate exporter field names are prescribed for this follow-up.

Do not infer precise remaining DOFs from under_defined or lockRotation=false. GLB mapping
is explicitly pending and is not needed for these metadata-only tests. Identity strings
and persistent-reference labels alone do not establish a cross-re-export stability guarantee.

## Acceptance checks for the next export/import pair

1. Original 2.1 JSON loads without manually relabeling schemaVersion.
2. Three objects retain a valid hierarchy; both parts retain their eight total dimensions.
3. Root mass 228.89242435869366 g equals the sum of the two leaf masses within rounding;
   root volume likewise matches the leaf total. This checks internal consistency only.
4. A scoped graph has the two listed parts and both distinct mates, without an artificial
   isolated root vertex. Actual suppression values are present on both mate records.
5. If both mates and parts are confirmed active, a one-hop path preserves mate status and
   reports the parallel relationship. It makes no rigidity/valid-constraint claim.
6. Partial coverage/unknown suppression never produces ConfirmedDisconnected or a definitive
   unmated/island finding. Missing scope data degrades mechanical tools, not property loading.

Implementation contract: [MECHANICAL_CONTRACT.md](Core/tools/MECHANICAL_CONTRACT.md).
