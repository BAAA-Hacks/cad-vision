# Issue access

The desktop initializes IssueAccess after metadata load, runs the registered issue checkers,
and then supplies it to SemanticQueryTools.Create. This happens on startup, Load metadata
and New chat. Reads report the resulting state without rerunning checks. Initialization
failure is logged and displayed; metadata queries remain usable with issue tools unavailable.

## Public tools

All nine tools require the host's projectId and the loaded snapshotId under contract 3.0.

| Tool | Additional required arguments |
| --- | --- |
| get_issue | issueId |
| list_issues | none |
| get_issues_for_object | objectId |
| get_issues_for_mate | mateId |
| get_issue_summary | none |
| get_issue_evidence | issueId |
| revalidate_issue | issueId, operationId, expectedRevision |
| revalidate_object_issues | objectId, operationId, expectedRevision |
| set_issue_disposition | issueId, disposition, expectedEvidenceHash, operationId, expectedRevision |

Issue IDs are opaque and project/snapshot-scoped. Obtain them from issue reads; stale snapshot
references are rejected. Reads default to presented findings. Set includeSuppressedCandidates
to true to inspect candidates hidden by precedence. This is unrelated to CAD suppression.
Evidence is read-only. No create/delete/overwrite or evidence mutation tool exists.
set_issue_disposition accepts exactly Open, Resolved or Ignored, with an optional reason
(1–512 characters). It changes user disposition only, without rerunning checks, erasing a
finding or proving a CAD fix. The prompt requires a user request for this action.

Lists accept limit, cursor and optional filters. Filters are exact-match AND conditions on
severity (Error/Warning/Question/Info), disposition (Open/Resolved/Ignored), checkerId,
issueType, objectId and mateId. Results sort by severity then issue ID. Cursors bind the exact
query, project, snapshot and issues revision. A new scan or revalidation invalidates old cursors.

## Coverage and verification

Responses include the issues revision and coverage with countUnit=checker_subjects.
Coverage describes registered checker evaluations, not every possible engineering defect.
Filtering and pagination never improve the underlying coverage. Subject evaluations and
reason counts are bounded to 32 entries with explicit truncation flags.

Findings expose presented state, disposition, verification, freshness, evidence hash and
latest evaluation. get_issue_evidence additionally returns structured evidence. Unknown
suppression and missing source evidence remain unknown; an empty list is not a clean bill
of health. No dedicated mate checker is registered yet: mate reads can be empty with
unavailable evaluation coverage even when mate records exist.

## Revalidation and persistence

Revalidation runs against the immutable loaded snapshot, not live SolidWorks. It can update
findings, verification and presentation but cannot establish a CAD edit that has not been
exported. Issue revalidation reruns the subject's precedence group. Object revalidation
also includes existing related finding subjects, including island representatives.

Use expectedRevision from the latest issue read and a fresh operationId. Exact retries reuse
the same operationId and request. Successful actions commit a receipt before publishing
runtime state; persistence failure leaves the previous committed state unchanged. Replays
return the original result and receipt with replayed=true. Cancellation after commit does
not erase a committed receipt. success=true can still report UnableToEvaluate.

The desktop stores the revision and action journal in data/issues.caden.json through the
durable file adapter. Corrupt journals fail closed and are not replaced with empty data.
Disposition actions and their receipts are also stored in this journal. On load the latest
choice restores only for the exact snapshot, issue ID and evidence hash; changed exports
do not silently inherit an old acceptance. Older journals remain readable. Revision and
evidence conflicts reject the write; persistence failure leaves runtime state unchanged.
Findings are regenerated on load; this journal is not conversation history or persistent
design memory. Startup scans advance its revision. Unity must provide its own host storage
adapter and initialization lifecycle; Core contains no desktop filesystem assumptions.

IssueAccessChecks verifies registration, coverage, evidence isolation, precedence, revision
conflicts, durable replay, failed persistence, post-commit cancellation and a simulated
Gemini tool exchange. No live API request is needed.
