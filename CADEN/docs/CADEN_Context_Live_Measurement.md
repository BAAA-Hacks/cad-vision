# Live context-compaction measurement — 2026-09-26

One authorized run of the standard 25-question suite, using the same metadata and
configured `gemini-flash-latest` alias as the preceding baseline. No automatic retries,
additional adversarial run, tuning or application-code changes were made during measurement.
Project identity, issue files and memory files were isolated under the new run directory.

The metadata SHA-256 and all 25 user prompts match run03. The system prompt includes the
new recall instructions and the host now bounds history; this is a whole-change comparison,
not an isolated tokenizer benchmark. The alias is not a pinned model version, and model
generation/cache behavior can vary between runs.

## Usage reported by Gemini

| Metric | Before: run03 | Compacted: run05 | Change |
| --- | ---: | ---: | ---: |
| Questions completed | 25 | 25 | unchanged |
| API requests | 48 | 53 | +10.4% |
| Tool calls | 24 | 30 | +25% |
| Session recall calls | 0 | 2 | one successful; one path error |
| Total input tokens | 1,247,946 | 694,417 | **-44.4%** |
| Cached input tokens, included above | 911,290 | 101,426 | -88.9% |
| Uncached input tokens | 336,656 | 592,991 | **+76.1%** |
| Output tokens | 7,506 | 8,844 | +17.8% |
| Reported thought tokens | 10,423 | 10,640 | +2.1% |
| Total reported tokens | 1,265,875 | 713,901 | -43.6% |
| Summed turn duration | 110.65 s | 114.84 s | +3.8% |

Average input per request fell from about 26,000 to 13,102 tokens. Average uncached input
per question rose from 13,466 to 23,720 tokens. Cached input share fell from 73.0% to 14.6%.
The first 16 turns of the new run reported no cached input; later turns did receive hits.

This is a context-size improvement, **not a demonstrated billing improvement**. More
uncached input and output were consumed. Exact dollars were not calculated and no price
assumptions were made. The provider does not explain its cache decisions in these captures.
Changing directory/history prefixes is a possible contributor, not an established cause.

The bounded-history benefit is visible on late simple turns: question 20 used 11,618 input
tokens versus 53,236 before. Large current-turn tool responses and additional follow-up
requests can still be expensive: question 18 consumed 93,061 across five requests.

## Functional observations

- All 25 questions returned answers; no round-limit or transport failures.
- Structural assertions passed 24/25. Question 18 attempted `/items` on a cached response
  whose records were under `/data/items`, producing `RESULT_PATH_NOT_FOUND`. It then tried
  an incorrect issue-type filter (empty result), corrected the filter and fetched recorded
  issue evidence. The eventual answer addressed the question, but the recovery cost extra calls.
- Question 4 successfully used `recall_result` for the previously fetched material evidence.
  Recall made no new underlying CAD query in that step.
- Question 22 reopened the chat/stores and correctly read `stress.rotation` from durable
  project memory, not the cleared temporary result cache. Intent was not claimed as physical proof.
- All five memory/disposition tasks completed correctly. Independent disk reopen confirmed
  all three findings Open and `stress.rotation` Retired with AssistantInferred provenance.
- Reviewed numeric answers retain correct mass, COM, units/frame and Lxx/Lyy/Lzz values.
  Partial path coverage and unavailable DOFs/interference were retained.
- Presentation is still verbose. Question 11 correctly declines ungrounded L-to-P relabeling,
  but overstates the general mathematical restriction when discussing zero off-diagonal
  terms. This run should not be described as universally flawless semantic behavior.

## Recommended follow-up (not implemented in this measurement)

1. Inspect request-prefix stability and improve provider cache reuse while keeping context bounded.
2. Supply the cache response structure or a default data path so Gemini does not guess `/items`.
3. Measure another explicitly authorized run after any fix, separating cached and uncached
   input rather than judging improvement only from total tokens.

## Evidence

- [Raw run05 results, calls, usage and durable state](../.tools/live-stress/runs/run05/results.json)
- [Numeric comparison](../.tools/live-stress/runs/run05/comparison.json)
- [Structural assessment](../.tools/live-stress/runs/run05/structural-assessment.json)
- [Exact test cases](../.tools/live-stress/runs/run05/cases.json)
- [Pre-compaction baseline](../.tools/live-stress/runs/run03/results.json)

Raw test artifacts remain local and ignored by Git; this report records the comparison.
