using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Core.Primitives.DataStructures.Project;

namespace Core.Primitives.DataStructures.Issues
{
    public enum IssueSeverity { Info, Question, Warning, Error }
    public enum IssueScope { Object, Mate, MultiObject, Assembly, Project }
    public enum IssueDispositionState { Open, Resolved, Ignored }
    public enum RevalidationOutcome { Present, Absent, UnableToEvaluate }
    [Flags] public enum IssueCapabilities { None = 0, Properties = 1, Hierarchy = 2, MechanicalGraph = 4 }

    public readonly struct IssueKey : IEquatable<IssueKey>
    {
        public string CheckerId { get; }
        public string IssueType { get; }
        public string SubjectKey { get; }
        private IssueKey(string checker, string type, string subject)
        { CheckerId = IssueValidation.Text(checker, nameof(checker)); IssueType = IssueValidation.Text(type, nameof(type)); SubjectKey = subject; }
        public static IssueKey ForEntities(string checker, string type, IEnumerable<string> objectIds, IEnumerable<string>? mateIds = null)
        {
            var objects = IssueValidation.Ids(objectIds); var mates = IssueValidation.Ids(mateIds ?? Array.Empty<string>());
            if (objects.Count + mates.Count == 0) throw new ArgumentException("Use ForProject for a project-wide subject.");
            return new IssueKey(checker, type, new JObject { ["objects"] = new JArray(objects), ["mates"] = new JArray(mates) }.ToString(Formatting.None));
        }
        public static IssueKey ForProject(string checker, string type) => new IssueKey(checker, type, "{\"project\":true}");
        public static IssueKey ForRoles(string checker, string type, IReadOnlyDictionary<string, string> objectRoles, IReadOnlyDictionary<string, string>? mateRoles = null)
        {
            JObject Roles(IReadOnlyDictionary<string, string> roles)
            {
                var result = new JObject(); foreach (var pair in roles.OrderBy(p => p.Key, StringComparer.Ordinal))
                    result[IssueValidation.Text(pair.Key, "role")] = IssueValidation.Text(pair.Value, "entity ID");
                return result;
            }
            if (objectRoles == null || objectRoles.Count + (mateRoles?.Count ?? 0) == 0) throw new ArgumentException("At least one subject role is required.");
            return new IssueKey(checker, type, new JObject { ["objectRoles"] = Roles(objectRoles), ["mateRoles"] = Roles(mateRoles ?? new Dictionary<string, string>()) }.ToString(Formatting.None));
        }
        internal IEnumerable<string> Subjects(bool mates)
        {
            var subject = JObject.Parse(SubjectKey);
            return subject[mates ? "mates" : "objects"] is JArray ids ? ids.Select(v => (string)v!).ToArray()
                : subject[mates ? "mateRoles" : "objectRoles"] is JObject roles ? roles.Properties().Select(p => (string)p.Value!).ToArray() : Array.Empty<string>();
        }
        internal bool Valid => !string.IsNullOrWhiteSpace(CheckerId) && !string.IsNullOrWhiteSpace(IssueType) && !string.IsNullOrWhiteSpace(SubjectKey);
        internal IssueKey EvaluationKey => new IssueKey(CheckerId, "$evaluation", SubjectKey);
        public bool Equals(IssueKey other) => CheckerId == other.CheckerId && IssueType == other.IssueType && SubjectKey == other.SubjectKey;
        public override bool Equals(object? obj) => obj is IssueKey other && Equals(other);
        public override int GetHashCode() { unchecked { return ((CheckerId?.GetHashCode() ?? 0) * 397 ^ (IssueType?.GetHashCode() ?? 0)) * 397 ^ (SubjectKey?.GetHashCode() ?? 0); } }
        public static bool operator ==(IssueKey a, IssueKey b) => a.Equals(b);
        public static bool operator !=(IssueKey a, IssueKey b) => !a.Equals(b);
    }

    internal static class IssueValidation
    {
        internal static string Text(string value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Non-empty " + name + " is required.");
        internal static IReadOnlyList<string> Ids(IEnumerable<string> ids) => (ids ?? throw new ArgumentNullException(nameof(ids)))
            .Select(v => Text(v, "ID")).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList().AsReadOnly();
        internal static string Canonical(JToken token)
        {
            JToken Normalize(JToken t)
            {
                if (t is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Normalize(p.Value))));
                if (t is JArray array) return new JArray(array.Select(Normalize));
                if (!(t.Type == JTokenType.Null || t.Type == JTokenType.String || t.Type == JTokenType.Boolean || t.Type == JTokenType.Integer || t.Type == JTokenType.Float))
                    throw new ArgumentException("Evidence must contain JSON values only; encode timestamps explicitly as strings outside material evidence.");
                if ((t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && (double.IsInfinity((double)t) || double.IsNaN((double)t)))
                    throw new ArgumentException("Evidence numbers must be finite.");
                return t.DeepClone();
            }
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            using var json = new JsonTextWriter(writer) { Formatting = Formatting.None, Culture = CultureInfo.InvariantCulture };
            Normalize(token).WriteTo(json); json.Flush(); return writer.ToString();
        }
    }

    public sealed class IssueFinding
    {
        private readonly JObject evidence;
        public IssueKey Key { get; }
        public string Type => Key.IssueType;
        public string CheckerId => Key.CheckerId;
        public string CheckerVersion { get; }
        public string ProjectId { get; }
        public string SnapshotId { get; }
        public IssueSeverity Severity { get; }
        public IssueScope Scope { get; }
        public bool IsHeuristic { get; }
        public bool RequiresDesignIntent { get; }
        public IReadOnlyList<string> AffectedObjectIds { get; }
        public IReadOnlyList<string> RelatedMateIds { get; }
        public string EvidenceHash { get; }
        public JObject Evidence => (JObject)evidence.DeepClone();
        public IssueFinding(IssueKey key, string checkerVersion, string projectId, string snapshotId, IssueSeverity severity,
            IssueScope scope, IEnumerable<string> affectedObjectIds, IEnumerable<string> relatedMateIds, JObject materialEvidence,
            bool requiresDesignIntent = false, bool isHeuristic = false)
        {
            if (!key.Valid) throw new ArgumentException("A canonical issue key is required.");
            if (!Enum.IsDefined(typeof(IssueSeverity), severity) || !Enum.IsDefined(typeof(IssueScope), scope)) throw new ArgumentException("Unknown severity or scope.");
            if (isHeuristic && severity == IssueSeverity.Error) throw new ArgumentException("Heuristic findings cannot have Error severity.");
            Key = key; CheckerVersion = IssueValidation.Text(checkerVersion, "checker version"); ProjectId = IssueValidation.Text(projectId, "project ID"); SnapshotId = IssueValidation.Text(snapshotId, "snapshot ID");
            Severity = severity; Scope = scope; IsHeuristic = isHeuristic; RequiresDesignIntent = requiresDesignIntent;
            AffectedObjectIds = IssueValidation.Ids(affectedObjectIds); RelatedMateIds = IssueValidation.Ids(relatedMateIds);
            if (key.Subjects(false).Any(id => !AffectedObjectIds.Contains(id)) || key.Subjects(true).Any(id => !RelatedMateIds.Contains(id)))
                throw new ArgumentException("Subject entities must be included in the finding's affected/related IDs.");
            if (scope == IssueScope.Object && AffectedObjectIds.Count != 1 || scope == IssueScope.Assembly && AffectedObjectIds.Count != 1 ||
                scope == IssueScope.MultiObject && AffectedObjectIds.Count < 2 || scope == IssueScope.Mate && RelatedMateIds.Count == 0)
                throw new ArgumentException("Affected entities do not match the issue scope.");
            evidence = (JObject)(materialEvidence ?? throw new ArgumentNullException(nameof(materialEvidence))).DeepClone();
            string canonical = IssueValidation.Canonical(new JObject
            {
                ["hashFormat"] = 1, ["checkerVersion"] = CheckerVersion, ["evidence"] = evidence.DeepClone(),
                ["severity"] = severity.ToString(), ["scope"] = scope.ToString(), ["requiresIntent"] = requiresDesignIntent,
                ["heuristic"] = isHeuristic, ["objects"] = new JArray(AffectedObjectIds), ["mates"] = new JArray(RelatedMateIds)
            });
            using var sha = SHA256.Create(); EvidenceHash = "sha256:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }
    }

    public sealed class IssueDisposition
    {
        public IssueKey Key { get; }
        public IssueDispositionState State { get; }
        public string? Reason { get; }
        public string? AcceptedEvidenceHash { get; }
        public string? AcceptedSnapshotId { get; }
        public DateTimeOffset UpdatedAt { get; }
        internal IssueDisposition(IssueKey key, IssueDispositionState state, string? reason, string? evidence, string? snapshot, DateTimeOffset updatedAt)
        { Key = key; State = state; Reason = reason; AcceptedEvidenceHash = evidence; AcceptedSnapshotId = snapshot; UpdatedAt = updatedAt; }
    }

    public sealed class IssueResult<T> where T : class
    {
        public bool Success => ErrorCode == null;
        public T? Value { get; }
        public string? ErrorCode { get; }
        public string? Message { get; }
        private IssueResult(T? value, string? code, string? message) { Value = value; ErrorCode = code; Message = message; }
        public static IssueResult<T> Ok(T value) => new IssueResult<T>(value, null, null);
        public static IssueResult<T> Fail(string code, string message) => new IssueResult<T>(null, code, message);
    }

    public sealed class IssueCheckResult
    {
        public RevalidationOutcome Outcome { get; }
        public IReadOnlyList<IssueFinding> Findings { get; }
        public string? Reason { get; }
        public IssueCheckResult(RevalidationOutcome outcome, IEnumerable<IssueFinding>? findings = null, string? reason = null)
        {
            if (!Enum.IsDefined(typeof(RevalidationOutcome), outcome)) throw new ArgumentException("Unknown revalidation outcome.");
            Outcome = outcome; Findings = (findings ?? Array.Empty<IssueFinding>()).ToList().AsReadOnly(); Reason = reason;
            if (Findings.Any(f => f == null) || outcome == RevalidationOutcome.UnableToEvaluate && Findings.Count != 0) throw new ArgumentException("Invalid checker result findings.");
        }
    }
}
