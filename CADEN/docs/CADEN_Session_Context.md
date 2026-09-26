# Bounded session context and result recall

The Gemini host advertises the existing capability-gated semantic tools plus one
session-only `recall_result` tool. With all capabilities available, that is 20 semantic
tools plus recall. The semantic registry and its exported declaration catalogue remain
20 tools; recall belongs to the chat transport, not project memory or CAD capabilities.

Completed turns retain their full local trace in ChatSession. Subsequent requests send
only the last six complete visible user/answer pairs, bounded to 16,000 text characters.
Older raw function calls, function results and thought signatures are omitted. The active
turn still preserves its full tool-call sequence, call IDs and continuation signatures.
No model-generated summarization or background Gemini requests are used.

The normal system prompt, startup summary and available tool definitions remain in each
request. The new bounds are deterministic character bounds, not exact token budgets.
Current user input and the active tool sequence are separate from the history bound.
If even the newest completed turn exceeds the history budget, it is omitted as a whole;
the model must recall evidence or clarify rather than guess an absent referent.

## Local result cache

Every ordinary dispatched tool response receives a transport-added `sessionResultId`.
The unmodified response is copied into a session-local dictionary. IDs include a random
session prefix and are never reused after reset. Cache retention is FIFO, bounded to
128 results and 8 Mi characters of serialized result data (not a total RAM cap).
The local chat trace remains separate; cache eviction is not transcript deletion.

The prompt directory contains the newest 12 result references, source tool names, bounded
request-subject descriptions, success state and whether a committed receipt was present.
It does not summarize or invent engineering conclusions. Directory descriptors may be
truncated; full returned evidence is never silently truncated by recall.

| recall_result argument | Meaning |
| --- | --- |
| resultId | Exact cache key; omit to list directory entries |
| path | Optional JSON Pointer into the original response, e.g. `/data/items` |
| offset | Directory/selected-array offset, default 0 |
| limit | Directory/selected-array page size, default 10, maximum 20 |

Directory entries are newest-first; a fresh source-tool call can change directory offsets.
Use an exact result ID once discovered. Array offsets within a cached result are immutable.
Recall never re-executes a source tool and is not itself cached, preventing recursive copies.
It consumes the same existing per-turn tool-call budget as other tools.

Recall returns a contract-3.0 envelope with historical provenance, source response context
(including coverage, pagination, errors and receipts) and an unchanged selected value.
Large recalled values return RESULT_TOO_LARGE with child-key guidance; narrow the pointer
or page size. Unknown/evicted/reset keys return RESULT_NOT_CACHED. Bad paths and arguments
return explicit errors. Recall failure is not missing CAD data or evidence of no finding.

Historical issue/memory revisions must not be used as fresh state. A stored receipt proves
an earlier commit, not current disposition or a new action. Successful tool results are
cached even if the following model request fails; an already committed action remains
inspectable. Reset/reload clears the cache; durable action receipt recovery remains the
responsibility of the existing issue/memory stores.

## Verification

ContextCacheChecks exercises fake HTTP end to end: unchanged active continuation,
compacted follow-ups, exact recall, array pages, JSON Pointer escaping, unavailable
property status/units, strict argument errors, FIFO eviction, old-directory access and
reset isolation. Existing loop, cancellation, persistence and failure tests still run.

The synthetic large-result check reduced a follow-up payload from roughly 45,700 to
2,000 JSON characters. That is a payload-size check, not a measured live token reduction.
Live model recall behavior and billing changes still require a separately authorized run.
