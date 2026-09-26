# Token usage logs

Desktop chat appends one JSON record per attempted turn to:

```text
%LOCALAPPDATA%\CADEN\logs\usage-YYYY-MM-DD.jsonl
```

The filename date is the turn's UTC start date. Restart the desktop application after
building this change to enable logging. Existing requests cannot be reconstructed.

Each record contains TurnId, SessionId, Model, StartedUtc, FinishedUtc, Outcome,
ApiRequests, InputTokens, OutputTokens, CachedInputTokens and usage-completeness fields.
Counts sum all Gemini requests in that turn, including tool-call continuations.

- InputTokens is Gemini promptTokenCount, **including cached input**.
- CachedInputTokens is cachedContentTokenCount, a subset of input, not an additional charge/count.
- OutputTokens is candidatesTokenCount. Separately reported thought tokens are not included
  in this output field; these three counts are not a full billing-total calculation.
- UsageComplete is false if any attempted request lacks usable counts. Nullable totals
  mean no usable values were reported; partial totals retain the values received before
  a failure/cancellation. The RequestsWith*Usage fields show how many requests contributed.
- If usageMetadata exists but cachedContentTokenCount is omitted, cached input is recorded
  as zero for that response. Missing input/output counts remain unknown, not inferred zero.
- Outcome is completed, failed or cancelled. Retries are separate turns with separate IDs.
  New chat starts a new session ID. No additional Gemini requests are made for logging.

No prompt, answer, CAD payload, API key or HTTP header is logged. Timestamp/session/turn
identifiers allow a future log viewer to group records. This change adds no UI viewer.

Example read from PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\CADEN\logs\usage-2026-09-26.jsonl" |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Select-Object StartedUtc, Outcome, InputTokens, OutputTokens, CachedInputTokens, UsageComplete
```

The Core collector has no filesystem dependency. Desktop supplies FileTokenUsage through
TokenUsageLog.Configure; a Unity host can supply its own sink. A write failure is reported
through existing diagnostics and does not discard a successfully generated chat answer.

Offline TokenUsageChecks verify multi-request sums, failure/cancellation preservation,
missing/invalid counts, session reset, append-only JSONL and sink-failure diagnostics.
