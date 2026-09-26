# Exporter-only changes: AssemTest2 metadata

Please make these two additions to the schema 2.1 export:
1. Export the actual suppression state on every mate.
2. Export explicit mechanical occurrence membership and mate coverage per assembly/configuration.

Keep schemaVersion at 2.1 and preserve the existing data and native evidence.
The examples below describe the required shape; populate values from the actual export,
without assuming suppression or extraction completeness.

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

## Exporter acceptance checklist

- Both Coincident1 and Concentric1 include suppressed as true, false, or null based on the actual read.
- Each mechanicalScopes entry identifies the assembly, configuration, source, membershipCoverage, mateCoverage, occurrenceIds and mateIds; reason explains incomplete/failed extraction where applicable.
- Listed occurrence IDs include known isolated participants; membership is not inferred solely from mate endpoints.
- Every selected mate has exactly two distinct endpoints inside that scope's occurrenceIds.
- IDs are unique within each list and reference existing records. Assembly/configuration pairs are unique.
- Both parallel mates remain distinct records. The root is not automatically included as an isolated mechanical occurrence.
- Coverage claims agree with extractionStatus and reflect the actual extraction outcome.
- Existing unknown satisfaction, native status, units and property evidence remain intact.
