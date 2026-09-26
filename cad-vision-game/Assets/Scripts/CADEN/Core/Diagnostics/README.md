# Failure diagnostics

Unexpected tool/checker failures, Gemini reply failures, desktop load/send/startup failures,
and unhandled desktop failures now emit a diagnostic entry with:

- a unique correlation ID and UTC timestamp;
- operation name and project/snapshot/subject identity where available;
- exception type/message and full exception text, including inner exceptions and stacks.

`DiagnosticLog.Report` returns a receipt and notifies host observers. The core has no
filesystem or Unity dependency. Without a configured sink, entries go to stderr and a
bounded in-memory queue (last 100 entries, accessible through `GetRecent`). Unity can
configure its own sink/observer. Checker failures still preserve findings and report the
diagnostic ID in the subject evaluation reason. Tool failures return `error.correlationId`
to Gemini; stacks and local log paths are not included in tool responses.

The desktop configures a daily JSONL file sink at:

```text
%LOCALAPPDATA%\CADEN\logs\caden-YYYY-MM-DD.jsonl
```

The error panel shows the operation, exception message, diagnostic ID and log folder.
Tool failures are announced independently of the chat result, so a later successful model
answer cannot hide the failure. Startup/unhandled UI failures use a message box. Nonfatal
metadata load errors are logged with diagnostic codes and source paths while usable
capabilities remain loaded. Reload failure preserves the previous chat.

Search the JSONL file for the diagnostic ID to inspect the stack. Logs are appended and
are not automatically deleted. Disk writes are synchronized within the desktop sink.
If writing fails, the receipt explicitly reports it, includes redacted exception details
for the UI, and falls back to stderr plus the in-memory queue. Diagnostic observers cannot
replace the original failure if they themselves throw; their failures go to stderr.

GeminiSettings registers its API key with the redactor. Registered secrets and their
URI-escaped forms, Google-key patterns, Bearer credentials, and common credential assignments
are redacted before any record reaches a sink or observer. Hosts must register additional
secrets through `RegisterSecret`. Request headers, prompts, tool argument payloads, .env
contents and HTTP response bodies are not deliberately logged. Exception messages can
contain application data, so these are local developer diagnostics, not telemetry.

Expected tool argument errors retain their specific model-visible error codes instead of
producing crash records. Expected missing data remains explicit coverage/availability.
User cancellation is not a crash. An unexpected cancellation from a tool when the caller's
token is not canceled is diagnosed as a tool failure. Existing graph/load validation APIs
continue returning structured diagnostics; they are not all converted into exceptions.

Offline `DiagnosticChecks` verifies file persistence, redaction, inner exceptions/stacks,
correlation to notifications and tool errors, parser exception preservation, scope fields,
log-write failure fallback and cancellation. No Gemini requests are sent.
