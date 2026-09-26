# find_connections

Read-only semantic query combining name discovery, authoritative hierarchy expansion,
and direct incident mate lookup. Exposed through the existing Gemini registry when
hierarchy and mate-record capabilities are available; a traversable graph is unnecessary.

Supply exactly one of `query` or `objectId`, plus the normal project/snapshot identity.
`query` matches all whitespace-separated words as case-insensitive substrings of ID/name.
Exact IDs, then exact names, take precedence. Otherwise matching descendants under a
matching assembly are folded into that assembly candidate. Repeated occurrences remain
separate. This is lexical candidate selection, not semantic identity resolution.

Each candidate expands to itself and all authoritative descendants. `relation` defaults
to `boundary` (one endpoint inside); `internal` requires both endpoints inside; `all`
includes both. A mate is returned once with all applicable candidate relations.
`otherQuery` ranks matching counterpart names/descendants first; it never removes
nonmatches or establishes an informal alias. No active-scope mutation occurs.

Active scope restricts primary candidates. Boundary endpoint context remains visible.
Explicit targets outside scope fail. Suppressed mates or endpoint ancestors are excluded
unless `includeSuppressed=true`; unknown suppression remains visible and labelled.
Native constraint/fixed states and optional mate fields retain availability/provenance.
Malformed optional values cannot become available evidence. Exported relationships do
not establish solver validity, mounting-hole identity, or remaining degrees of freedom.

`candidateLimit` defaults to 8, range 1–16; truncation is explicit and requires narrowing
the query for omitted candidates. Mate `limit` defaults to 10, bounded by host MaxResults.
Connections are paged deterministically (hint match first, then mate ID), with cursors
bound to query/project/snapshot/scope revision. Response-size limits may shorten a page.
Coverage remains partial: this operation does not certify scoped export completeness.
Underlying incident-record counts precede filtering; suppression counters explicitly
refer to the relation-filtered records. Empty results do not establish no connection.

Offline verification with `metadata (2).json`: `query="redstart", otherQuery="drone base"`
returns two assembly candidates and two mates in one call. Both mates connect descendants
of `redstart (1).step-1` to `FRED_Prototype_1-1`: Coincident6 and Tangent2. The other
redstart assembly has no matching exported mates. Both assemblies report under_defined;
the base part reports fixed/fully_defined. No exact name match establishes “drone base”.
One cold local measurement was approximately 140 ms and 10,865 JSON characters, excluding
Gemini latency/tokens. This is not a model-response benchmark.

Offline regression tests live in ConnectionQueryChecks.cs. Live Gemini tool selection
still requires a separately authorized test.

## Answer coverage and stopping

answerCoverage separates supplied candidate/connection evidence, available/unestablished
mate types and native definition states, actionable retrieval gaps, and evidence limits.
Counts describe selected candidates and returned endpoints/mates, not the entire model.
Candidate truncation and additional pages can justify further retrieval. Unconfirmed
informal identity, named mounting-hole identity and solver/DOF inference remain explicit
limits; partial export coverage alone does not justify rereading the same snapshot.
The system prompt requires a specific requested fact and a tool capable of adding it
before another call. This is a behavioral rule, not a hard one-call runtime limit.

Checks/OrchestrationCases.RedstartConnections.json targets metadata (2).json and requires
exactly find_connections followed by an answer. The assessor checks the actual tool
sequence, including duplicate calls; factual/qualified-answer correctness still needs
semantic review. Synthetic passing traces test the assessor, not Gemini compliance.
