# Runtime issue store

`IssueStore` is an in-memory analysis layer over one immutable `ProjectSnapshot`.
Models live in `data_structures/issues`; controlled mutations and targeted revalidation
live here. The store has no Gemini, Unity or file I/O dependencies. The
[subject-based issue engine](ENGINE.md) now provides discovery, coverage, presentation
normalization and the first material/native-constraint checkers.

## Identity and evidence

`IssueKey.ForEntities` sorts and deduplicates object and mate IDs independently.
JSON encoding prevents delimiter collisions. `ForRoles` preserves directional roles;
`ForProject` identifies a project-wide subject. IDs and checker/type names are case-sensitive.
Only entities defining the logical issue belong in its key; other involved entities
can be included in `AffectedObjectIds` and `RelatedMateIds`.

`IssueFinding` is immutable. Evidence is a copied JSON object; getters return copies.
The SHA-256 material-evidence hash includes checker version, severity, scope, intent/
heuristic flags, affected/related IDs and evidence. Object property order is normalized;
array order is preserved. Checkers must sort arrays that semantically represent sets.
Checkers must include availability, units, coordinate frames and other material context
where relevant. Do not put incidental timestamps, run IDs or snapshot IDs in evidence.
Changing a checker version conservatively invalidates previous acceptance. JSON numeric
representations are preserved (for example, integer 1 and floating-point 1.0 can hash
differently); checkers should use consistent numeric types. Nonfinite numbers are rejected.

Heuristic findings cannot be `Error`. Deterministic findings still require a semantic
severity decision. Finding constructors reject malformed model arguments; store operations
return `IssueResult<T>` errors for stale state, invalid references and rejected mutations.

## Store operations and queries

```csharp
var store = new IssueStore(snapshot);
var key = IssueKey.ForEntities("constraint-status", "under_defined", new[] { objectId });
var finding = new IssueFinding(key, "1", snapshot.ProjectId, snapshot.SnapshotId,
    IssueSeverity.Question, IssueScope.Object, new[] { objectId }, Array.Empty<string>(),
    new JObject { ["constraintStatus"] = "under_defined" }, requiresDesignIntent: true);
var added = store.AddFinding(finding);
if (added.Success)
    store.SetDisposition(key, IssueDispositionState.Ignored,
        "This shaft is intended to rotate.", finding.EvidenceHash);
```

This example constructs a finding from assumed evidence; it is not a detection rule.

- `GetFinding` retrieves by exact key; `ByObject`, `ByMate`, `ByType`, `BySeverity`,
  and `ByDisposition` return read-only copies of indexed key sets in deterministic order.
  Compose result sets externally. Each query is synchronized; multiple separate queries
  do not form a shared transaction when other threads mutate the store.
- `AddFinding` is idempotent for identical key/hash, rejects conflicting evidence, and
  indexes a new finding as `Open`. Every referenced object/mate must exist in the snapshot.
- `ReplaceFindingEvidence` and `RemoveFinding` require the expected evidence hash.
  Replacement takes a new immutable finding with the same key and updates all indexes.
- `SetDisposition` requires the expected evidence hash. `Resolved` and `Ignored` record
  the accepted hash, originating snapshot, reason and timestamp. Neither removes a finding.
- Changed evidence reopens accepted findings. Matching evidence preserves acceptance,
  including removal followed by reappearance in the same store.
- Removal clears every active index, including disposition, but retains disposition and
  history. `GetDisposition` can therefore return a historical record for an absent finding;
  use `GetFinding` to determine current presence. Absence does not automatically mean
  the user resolved an issue.

All mutation/index maintenance occurs under one lock. Indexes contain only keys; current
findings are stored once. No public reindex operation is needed because external index
mutation is prohibited. History is retained in memory for the store's lifetime.

## Legacy targeted revalidation

New checkers should use [IssueEngine](ENGINE.md). The API below remains for unmanaged
findings and rejects scan-managed subjects or precedence groups with USE_ISSUE_ENGINE.

Implement `IIssueChecker` with stable ID/version/capability properties and register it
in `IssueCheckerRegistry`. A checker receives the prior finding (including target IDs)
and the same immutable snapshot. It must evaluate only the relevant scope. Use a new
store for a new export; never replace the snapshot underneath a store.

`await RevalidateIssue.RunAsync(store, key, registry, cancellationToken)` calls only the
registered checker for the finding. It evaluates outside the store lock, then validates
the entire output before committing:

| Outcome | Required output | Store behavior |
| --- | --- | --- |
| Present | Original key plus optional related findings | Replace/add; preserve acceptance only for matching evidence |
| Absent | No original key; optional related findings | Remove original finding and add/update related findings |
| UnableToEvaluate | No findings | Preserve existing state |

Duplicate keys, wrong checker/version, invalid references, wrong snapshot, or unrelated
outputs reject the entire batch. A related finding must share an object or mate with the
original scope; project-wide checks may return findings anywhere in the project.
Only the original key can be removed. Other findings omitted from a targeted result remain.

Unknown checkers, missing/invalid required capabilities and checker exceptions produce
`UnableToEvaluate`, never successful absence. Exception results report the exception type;
checkers can return a structured inability outcome with a useful `Reason`. Cancellation
throws without committing. Any store mutation during evaluation returns `STORE_CHANGED`;
the caller may retry against current state. This intentionally conservative guard also
covers concurrent disposition changes and changes to unrelated findings.

Inspect both `IssueResult.Success` and `Value.Outcome`: successful handling of
`UnableToEvaluate` does not establish that a finding is present or absent.

## Re-export and boundaries

`CarryDispositionFrom` explicitly transfers a prior accepted disposition only when:

- both stores contain the same key in the same project, with identical evidence hashes;
- different snapshots both guarantee `ProjectStable` component identity;
- findings referencing mates additionally receive the host's explicit
  `mateIdsStableAcrossSnapshots: true` guarantee (the loader currently guarantees only
  component identity);
- the target finding has its initial `Open` disposition and no intervening decision.

Originating acceptance provenance is preserved. Snapshot-only/fixture identities cannot
be silently remapped across exports. Missing IDs return `NOT_FOUND`; a future persistent
memory layer must retain such references as stale. This store does not guess replacements
or implement disk persistence, stale-memory reconciliation, Gemini tool declarations,
or visualization. Detection and scanning are provided separately by IssueEngine.
Import failures remain `LoadDiagnostics`.
Valid property findings/checkers remain usable when mechanical-graph capability is invalid.

`Checks/IssueStoreChecks.cs` verifies identity, immutable evidence, indexes, disposition
history, revalidation transactions, graceful degradation, concurrency, cancellation and
explicit cross-snapshot carry-over without live API calls.
