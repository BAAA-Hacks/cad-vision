# Shared tool capabilities

SemanticQueryTools constructs one ToolCapabilities catalogue for all 24 implemented entry
points. It drives model-summary discovery, declaration gating and execution failures.
get_model_summary remains declared and callable with no metadata; modelLoaded=false and
the other entries explain MODEL_NOT_LOADED. Loaded snapshots retain their existing summary.

Each toolCapabilities entry contains tool, state (Available/Unavailable/Invalid), usable,
reasonCode, retryable, recovery and alternativeTools. Host initialization failures may also
carry a correlationId. Unusable tools are not declared to Gemini. Usable is not a guarantee
of complete properties or checker coverage; ordinary responses retain their evidence rules.

Direct calls to known unavailable tools return contract 3.0 success=false, data=null,
errors[].code=CAPABILITY_UNAVAILABLE and the catalogue entry in errors[].details. Truly
unknown names remain UNKNOWN_TOOL. Existing request validation and identity errors remain
distinct. Capability failures have retryable=false: correcting data, selecting an available
scope, initializing host services or repairing storage must precede a retry. They are not
transient Gemini API failures and do not trigger automatic retries or storage resets.

Reasons include MODEL_NOT_LOADED, HIERARCHY_INVALID, HIERARCHY_UNAVAILABLE,
MATE_DATA_INVALID, MATE_DATA_UNAVAILABLE, FIXTURE_MATE_EVIDENCE_UNAVAILABLE,
MECHANICAL_SCOPE_MISSING, MECHANICAL_SCOPE_INVALID, OCCURRENCE_MEMBERSHIP_MISSING,
MATE_COVERAGE_MISSING, ISSUES_NOT_INITIALIZED and MEMORY_NOT_INITIALIZED. Desktop passes
sanitized host failure reasons such as MEMORY_STORAGE_INVALID or ISSUES_INITIALIZATION_FAILED
plus diagnostic IDs; raw exception details remain in host logs.

Mechanical availability is checked both globally and against the requested assembly and
configuration. Exported mate lookup is offered as an alternative only when available, and
cannot establish traversal or constraint validity. Hierarchy failure can suggest usable
object-property queries. These are narrower alternatives, never automatic substitutes.

Missing individual values, incomplete scans, uncertain suppression and partial graph
coverage retain successful partial-result semantics where trustworthy evidence exists.
The system does not disable issue reads merely because some checkers cannot evaluate.
Failed host initialization disables only its own subsystem. Reload constructs a fresh
catalogue; it does not silently repair or discard corrupt data.

CapabilityChecks covers discovery without a model, all 24 entries, declaration agreement,
safe host diagnostics, capability-vs-unknown errors and partial fallback evidence.
MechanicalQueryChecks additionally covers per-request scope rejection. No live API needed.
