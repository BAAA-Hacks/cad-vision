using System;
using Core.Primitives.DataStructures.Memory;
using Core.Primitives.DataStructures.Project;
using Newtonsoft.Json.Linq;

namespace Core.Tools
{
    public static class ToolResponseContract
    {
        public const string Version = "3.0";
        public static string PublicCode(string code) => code switch
        {
            "INVALID_ARGUMENTS" => "INVALID_ARGUMENT", "OBJECT_NOT_FOUND" => "UNKNOWN_OBJECT_ID",
            "STALE_SNAPSHOT" => "STALE_SNAPSHOT_REFERENCE", "PROJECT_MISMATCH" => "WRONG_PROJECT",
            "MODEL_NOT_LOADED" => "CAPABILITY_UNAVAILABLE", "RESULT_TOO_LARGE" => "QUERY_TOO_LARGE",
            "TOOL_EXECUTION_FAILED" => "INTERNAL_ERROR", "STORE_CHANGED" or "STORAGE_CONFLICT" => "REVISION_CONFLICT",
            "UNSUPPORTED_SCHEMA" => "UNSUPPORTED_SCHEMA_VERSION", "INVALID_METADATA" => "CORRUPT_STORAGE", _ => code
        };
        public static JObject Wrap(JObject legacy, ProjectAssociation? association, ProjectSnapshot? snapshot)
        {
            bool success = (bool?)legacy["ok"] == true;
            var data = legacy["data"] is JObject body ? (JObject)body.DeepClone() : null;
            var coverage = data?["coverage"]?.DeepClone(); data?.Remove("coverage");
            var pagination = data?["pagination"]?.DeepClone(); data?.Remove("pagination");
            var receipt = data?["receipt"]?.DeepClone(); data?.Remove("receipt");
            var errors = new JArray();
            if (!success && legacy["error"] is JObject error)
            {
                var e = new JObject { ["code"] = PublicCode((string?)error["code"] ?? "INTERNAL_ERROR"), ["message"] = error["message"]?.DeepClone(), ["details"] = error["details"]?.DeepClone() ?? new JObject() };
                if (error["correlationId"] != null) e["correlationId"] = error["correlationId"]!.DeepClone(); errors.Add(e);
            }
            var result = new JObject { ["contractVersion"] = Version, ["success"] = success,
                ["projectId"] = association == null ? JValue.CreateNull() : new JValue(association.ProjectId),
                ["snapshotId"] = snapshot == null ? JValue.CreateNull() : new JValue(snapshot.SnapshotId),
                ["provenance"] = new JObject { ["identityScope"] = snapshot?.IdentityScope.ToString(), ["fixture"] = snapshot == null ? JValue.CreateNull() : new JValue(snapshot.IsFixture),
                    ["sourceProjectId"] = snapshot?.ProjectId, ["sourceIdentityTrusted"] = association?.TrustedSourceProjectId != null },
                ["data"] = success ? data : null, ["coverage"] = success ? coverage : null,
                ["pagination"] = success ? pagination : null, ["errors"] = errors };
            if (receipt != null) result["receipt"] = receipt;
            return result;
        }
    }
}
