#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core.Primitives.DataStructures.Project;
using Core.Primitives.DataStructures.MechanicalGraph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools
{
    public sealed class HostCapabilityFailure
    {
        public string ReasonCode { get; }
        public string Recovery { get; }
        public string? CorrelationId { get; }
        public HostCapabilityFailure(string reasonCode, string recovery, string? correlationId = null)
        { ReasonCode = reasonCode; Recovery = recovery; CorrelationId = correlationId; }
        public static HostCapabilityFailure FromException(string subsystem, Exception error, string correlationId)
        {
            string prefix = subsystem == "memory" ? "MEMORY" : "ISSUES";
            bool invalid = error is InvalidDataException || error is JsonException || error is ToolInputException t && new[] { "MEMORY_LOAD_FAILED", "WRONG_PROJECT", "PROJECT_MISMATCH", "CORRUPT_STORAGE" }.Contains(t.Code);
            return new HostCapabilityFailure(prefix + (invalid ? "_STORAGE_INVALID" : "_INITIALIZATION_FAILED"),
                invalid ? "Have the host inspect the storage diagnostic and repair or select the correct sidecar, then reload. Do not overwrite or reset it automatically."
                    : "Have the host inspect the initialization diagnostic and correct the storage or service problem, then reload.", correlationId);
        }
    }
    // The same catalogue drives discovery, declarations and execution rejection.
    public sealed class ToolCapabilities
    {
        private readonly Dictionary<string, JObject> entries = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private readonly ProjectSnapshot? snapshot;
        private static readonly string[] IssueNames = { "get_issue", "list_issues", "get_issues_for_object", "get_issues_for_mate", "get_issue_summary", "get_issue_evidence", "revalidate_issue", "revalidate_object_issues", "set_issue_disposition" };
        internal ToolCapabilities(ProjectSnapshot? snapshot, IEnumerable<ICadenTool> handlers, IReadOnlyDictionary<string, HostCapabilityFailure>? failures)
        {
            this.snapshot = snapshot; var tools = handlers.ToDictionary(t => t.Name, StringComparer.Ordinal);
            var names = new[] { "get_model_summary", "get_object_details", "find_objects", "query_hierarchy", "get_mates", "find_connections", "get_mechanical_neighborhood", "find_mechanical_path" }
                .Concat(IssueNames).Concat(new[] { "get_project_memory", "write_project_memory", "get_diagnostic_summary", "get_diagnostics", "set_scope", "clear_scope", "get_scope" });
            foreach (var name in names)
            {
                JObject entry = Entry(name);
                if (name == "get_model_summary") { entries.Add(name, entry); continue; }
                if (snapshot == null) Block(entry, "MODEL_NOT_LOADED", "Load valid metadata, then call get_model_summary.");
                else if ((name == "query_hierarchy" || name == "find_connections") && snapshot.Capabilities.Hierarchy != CapabilityState.Available)
                    Block(entry, "HIERARCHY_" + snapshot.Capabilities.Hierarchy.ToString().ToUpperInvariant(), "Correct or provide metadata hierarchy and reload. Object properties can still be queried.", "get_object_details");
                else if ((name == "get_mates" || name == "find_connections") && (!tools.TryGetValue(name, out var mateTool) || mateTool is ICapabilityCadenTool c && !c.Available))
                    Block(entry, snapshot.IsFixture ? "FIXTURE_MATE_EVIDENCE_UNAVAILABLE" : snapshot.Capabilities.MechanicalGraph == CapabilityState.Invalid ? "MATE_DATA_INVALID" : "MATE_DATA_UNAVAILABLE", "Provide a valid export containing usable mate records or explicit zero-mate coverage, then reload.");
                else if (name == "get_mechanical_neighborhood" || name == "find_mechanical_path")
                {
                    if (!snapshot.MechanicalScopes.Any(s => s.State == GraphDataState.Available)) Block(entry, ScopeReason(null), "Provide explicit assembly/configuration membership and usable mate coverage, correct invalid records, then reload.", "get_mates");
                    entry["scopeRequired"] = true;
                }
                else if (!tools.ContainsKey(name))
                {
                    string subsystem = IssueNames.Contains(name) ? "issues" : "memory";
                    if (failures != null && failures.TryGetValue(subsystem, out var failure))
                    { Block(entry, failure.ReasonCode, failure.Recovery); entry["correlationId"] = failure.CorrelationId; }
                    else Block(entry, subsystem.ToUpperInvariant() + "_NOT_INITIALIZED", "The host must initialize " + subsystem + " successfully and reload the tool session.");
                }
                entries.Add(name, entry);
            }
            foreach (var tool in tools.Values.Where(t => !entries.ContainsKey(t.Name)))
            {
                var entry = Entry(tool.Name);
                if (tool is ICapabilityCadenTool host && !host.Available)
                    Block(entry, "VIEW_MAPPING_UNAVAILABLE", "Load a verified CAD-to-geometry mapping and initialize the manipulation service. Metadata queries remain available.");
                entry["activeScopeSupport"] = "explicit_targets_independent_of_query_scope";
                entries.Add(tool.Name, entry);
            }
            // Alternatives are suggestions only when actually usable in this session.
            foreach (var entry in entries.Values) entry["alternativeTools"] = new JArray(((JArray)entry["alternativeTools"]!).Where(n => IsAvailable((string)n!)));
        }
        private static JObject Entry(string name) => new JObject { ["tool"] = name, ["state"] = "Available", ["usable"] = true,
            ["reasonCode"] = null, ["retryable"] = false, ["recovery"] = null, ["alternativeTools"] = new JArray(), ["activeScopeSupport"] = ScopeManager.Behavior(name) };
        private static void Block(JObject entry, string reason, string recovery, params string[] alternatives)
        { entry["state"] = reason.EndsWith("INVALID", StringComparison.Ordinal) ? "Invalid" : "Unavailable"; entry["usable"] = false; entry["reasonCode"] = reason; entry["recovery"] = recovery; entry["alternativeTools"] = new JArray(alternatives); }
        public bool IsAvailable(string name) => !entries.TryGetValue(name, out var entry) || (bool)entry["usable"]!;
        public JArray Describe() => new JArray(entries.Values.Select(e => e.DeepClone()));
        private string ScopeReason(MechanicalScope? scope)
        {
            if (snapshot!.IsFixture) return "FIXTURE_MATE_EVIDENCE_UNAVAILABLE";
            if (scope == null && snapshot.MechanicalScopes.Count == 0) return "MECHANICAL_SCOPE_MISSING";
            if (scope?.State == GraphDataState.Invalid || scope == null && snapshot.MechanicalScopes.Any(s => s.State == GraphDataState.Invalid)) return "MECHANICAL_SCOPE_INVALID";
            if (scope?.MembershipCoverage == MechanicalCoverage.Unavailable) return "OCCURRENCE_MEMBERSHIP_MISSING";
            return "MATE_COVERAGE_MISSING";
        }
        public JObject? Failure(string name, JObject? args = null)
        {
            if (!entries.TryGetValue(name, out var entry)) return null;
            var blocked = (JObject)entry.DeepClone();
            if ((bool)blocked["usable"]!)
            {
                if (args == null || (name != "get_mechanical_neighborhood" && name != "find_mechanical_path")) return null;
                var scope = snapshot!.MechanicalScopes.FirstOrDefault(s => s.ScopeAssemblyId == (string?)args["scopeAssemblyId"] && s.Configuration == (string?)args["configuration"]);
                if (scope?.State == GraphDataState.Available) return null;
                Block(blocked, scope == null ? "MECHANICAL_SCOPE_MISSING" : ScopeReason(scope), "Select an available assembly/configuration from get_model_summary, or correct its export and reload.", IsAvailable("get_mates") ? new[] { "get_mates" } : Array.Empty<string>());
            }
            var error = ToolRegistry.Error("CAPABILITY_UNAVAILABLE", "Tool requirements are not met: " + (string?)blocked["reasonCode"] + ". Follow recovery guidance before calling again.");
            error["error"]!["details"] = blocked;
            if (blocked["correlationId"]?.Type == JTokenType.String) error["error"]!["correlationId"] = blocked["correlationId"]!.DeepClone();
            return error;
        }
    }
}
