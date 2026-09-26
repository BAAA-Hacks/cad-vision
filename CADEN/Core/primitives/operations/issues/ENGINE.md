# Subject-based issue engine

The engine discovers candidates from a canonical snapshot, records coverage for each
checker/subject pair, and reconciles candidates atomically. Presentation is a separate
view over those candidates. It does not change user dispositions.

## Usage

```csharp
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.Operations.Issues;
using Core.Primitives.Operations.Issues.Checkers;

var store = new IssueStore(snapshot);
var engine = InitialIssueCheckers.CreateEngine();
var scan = await engine.ScanAsync(store, cancellationToken: cancellationToken);
if (!scan.Success)
{
    // Handle scan.ErrorCode / scan.Message. No batch changes were committed.
    return;
}
foreach (var evaluation in scan.Value!.Evaluations)
{
    // Inspect CheckerId, Subject, Status and Reason, even when there are no findings.
}
var candidates = store.GetPresentation(objectId);
var presented = candidates.Where(candidate => candidate.IsPresented);
// Always expose uncertainty when candidate.IsVerified is false.
// Disposition filtering is a separate product decision.

// Reevaluate one checker and all its precedence peers on this same subject:
var recheck = await engine.RevalidateAsync(store, issueKey, cancellationToken);

// Evaluate selected subjects, including ones that have never had a finding:
var targeted = await engine.ScanAsync(store,
    new[] { new IssueSubject(IssueSubjectKind.Object, objectId) },
    new[] { "material.missing" }, cancellationToken);
```

No desktop launch hook, chat tool or Gemini call is added by this milestone. The host
must load a canonical snapshot and call the engine. Detection reads canonical property
values; it does not bypass missing/invalid states using raw export fields.

## Coverage and reconciliation

`ISubjectIssueChecker.EvaluateAsync` evaluates one subject, rather than requiring a prior
finding. The engine's scan enumerates known object occurrences and mates when no explicit
subjects are supplied, dispatching each registered checker to its supported subject kind.
An explicit empty subject/checker list runs nothing. Duplicate targets are evaluated once.

`SubjectEvaluation` records project ID, snapshot ID, checker ID/version, canonical subject,
status, reason and evaluation time. Timestamp/freshness are outside the evidence hash.
`GetEvaluation(checkerId, subject)` returns the last committed outcome even when no
candidate exists. Different snapshots require different stores.

| Subject outcome | Reconciliation |
| --- | --- |
| Complete with findings | Replace candidates for exactly this checker and subject; compare evidence to preserve/reopen dispositions |
| Complete without findings | Remove candidates for exactly this checker and subject |
| UnableToEvaluate | Retain candidates and dispositions; mark retained candidates unverified |
| Failed | Retain candidates and dispositions; mark retained candidates unverified |
| Subject not requested | No changes to candidates or evaluation status |

The report summary is Complete, Partial, UnableToEvaluate, Failed, or NotEvaluated.
It summarizes the listed evaluations only, never the entire assembly's health or export
coverage. An empty evaluation list is NotEvaluated. Correctness always comes from the
subject records, not from an empty finding list or the summary alone. A successful
`IssueResult` means the batch was reconciled, not that every check could evaluate.

The engine catches a checker exception for that subject and records Failed with its
exception type; other subjects can still succeed. Missing checkers/capabilities record
UnableToEvaluate. Checkers may supply detailed reasons for expected missing inputs.
Unknown target IDs, duplicate finding keys, wrong subject/checker/version, invalid
references or snapshot mismatches reject the batch without any candidate, evaluation,
index or disposition changes. Canceled scans do not commit. Any concurrent store mutation
causes STORE_CHANGED, including disposition edits. Retry against current state if needed.
Execution is sequential and cancellation is cooperative; checkers must honor the token.

## Candidate presentation and freshness

Existing `GetFinding` and `By*` APIs query **all candidates**, including those hidden by
precedence. `GetPresentation` returns a consistent read-only view with:

- Finding and its separate Disposition;
- LatestEvaluation and IsVerified;
- SuppressedBy keys and IsPresented.

Only verified candidates suppress another candidate on the same canonical subject.
The initial rules are `mate.dangling/dangling` over `mate.error/error`, and
`constraint.unsolvable/unsolvable` over `constraint.over_defined/over_defined`.
The mate rule is ready for future checkers; mate detection is not registered yet.

Every scan request expands these precedence groups for its requested subjects, including
an unavailable peer so its previously retained candidates lose suppression authority.
The same rules drive presentation. Generic candidates are never deleted or marked Ignored
by precedence. If the specific candidate disappears or becomes unverified, the generic
candidate becomes presented again. A retained unverified candidate remains accessible
with its last evaluation reason. User dispositions do not influence precedence.

Freshness here means confirmed by a committed evaluation against this immutable snapshot;
there is no wall-clock expiration. Direct Add/Replace operations cannot establish verified
status. Direct replacement invalidates verification even if an older subject evaluation
says Complete; consumers must use IsVerified, not infer it from LatestEvaluation alone.
New snapshots and disposition carry-over never inherit verification from another store.

The legacy `RevalidateIssue`/`IIssueChecker` API remains for unmanaged findings. It rejects
scan-managed subjects and precedence-group members with USE_ISSUE_ENGINE. New detection
code should use ISubjectIssueChecker and IssueEngine.

## Initial checkers

All use version `1`, separate from their stable checker ID.

| Checker ID / issue type | Confirmed trigger | Severity |
| --- | --- | --- |
| material.missing / missing | Part with available material and assigned=false | Warning |
| constraint.unsolvable / unsolvable | definitionStatus is no_solution or invalid_solution | Error |
| constraint.over_defined / over_defined | definitionStatus is over_defined | Warning |
| constraint.under_defined / under_defined | definitionStatus is under_defined | Question, requires design intent |

All ordinary checkers skip explicitly suppressed occurrences with Complete/inapplicable.
Unknown suppression produces UnableToEvaluate. Material assignment is applicable only to
parts; constraint status applies to components, with Assembly scope for assembly objects.
Available native status is required; unknown/unrecognized values cannot prove absence.
No native API aliases are guessed beyond the canonical loader's supported status contract.
Fixture engineering placeholders remain unavailable and cannot produce design findings.

Evidence includes canonical field states/values, suppression, source document and
configuration. Constraint findings also retain available fixed/DOF context. Active-mate
information is explicitly unavailable until scoped coverage and active-set contracts
exist; no empty incident-mate list is asserted. Missing optional context does not block
a finding supported by the required native status. Project/snapshot identity is on the
finding; source fields/provenance remain accessible through that canonical snapshot.

## Scope boundaries

Evaluation addresses use object-occurrence or mate subjects. Mechanical checker registrations
bind an explicit assembly/configuration scope using a canonical structured-key hash. Pass
the loaded snapshot to InitialIssueCheckers.CreateEngine(snapshot) to register these checks.
Island findings are keyed to one deterministic representative, carry all affected IDs and
evaluate the whole scope; singleton islands are supported. See the
[mechanical contract](../../../tools/MECHANICAL_CONTRACT.md) for eligibility and coverage.

Mate/dangling/suppression detection, unavailable-material extraction checks,
physical-value classification, density/unit comparisons, repeated-part checks, persistence,
and automatic host/chat integration remain deferred. The initial precedence policy contains
only the two supported groups; add further groups together with their checker contracts.

`IssueEngineChecks` uses synthetic exports and fake checkers to verify discovery, per-subject
coverage, suppressed/unknown/fixture safeguards, precedence freshness, targeted groups,
atomic invalid-output rejection, graceful capability degradation, concurrency and cancellation.
