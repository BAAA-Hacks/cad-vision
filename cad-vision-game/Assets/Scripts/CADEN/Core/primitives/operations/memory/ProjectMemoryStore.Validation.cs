#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Core.Primitives.DataStructures.Issues;
using Core.Primitives.DataStructures.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.Operations.Memory
{
    public sealed partial class ProjectMemoryStore
    {
        private static JObject Parse(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > 10000000) throw new InvalidDataException("Sidecar exceeds 10 MB.");
            using var source = new StringReader(text);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Trailing sidecar content."); return result;
        }
        private static JObject Shape(JToken? token, params string[] fields)
        {
            if (!(token is JObject obj) || obj.Count != fields.Length || fields.Any(f => obj.Property(f) == null)) throw new InvalidDataException("Unexpected or missing sidecar fields: " + string.Join(",", fields));
            return obj;
        }
        private static string Text(JToken? token, int max = 512)
        { if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token) || ((string)token!).Length > max) throw new InvalidDataException("Invalid sidecar text."); return (string)token!; }
        private static void NullableText(JToken? token) { if (token == null) throw new InvalidDataException("Missing nullable field."); if (token.Type != JTokenType.Null) Text(token); }
        private static JArray List(JToken? token) => token is JArray a && a.Count <= 10000 ? a : throw new InvalidDataException("Expected bounded sidecar array.");
        private static long RevisionValue(JToken? token)
        { if (token?.Type != JTokenType.Integer) throw new InvalidDataException("Invalid revision."); long n = (long)token; if (n < 0 || n > 10000) throw new InvalidDataException("Revision outside V1 capacity."); return n; }
        private static void Timestamp(JToken? token)
        { if (!DateTimeOffset.TryParseExact(Text(token), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new InvalidDataException("Expected round-trip timestamp."); }
        private static void EnumValue<T>(JToken? token) where T : struct
        { string value = Text(token); if (!Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(typeof(T), parsed) || parsed.ToString() != value) throw new InvalidDataException("Unknown enum value."); }
        private static void Provenance(JToken? token)
        { var p = Shape(token, "type", "actor"); EnumValue<MemoryProvenance>(p["type"]); Text(p["actor"]); }
        private static void References(JToken? token, bool allowMates)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in List(token))
            {
                Shape(r, "id", "kind", "originSnapshotId", "stable"); string kind = Text(r["kind"]);
                if (kind != "object" && !(allowMates && kind == "mate") || r["stable"]?.Type != JTokenType.Boolean) throw new InvalidDataException("Invalid reference kind/identity guarantee.");
                Text(r["originSnapshotId"]); if (!keys.Add(new JArray(kind, Text(r["id"])).ToString(Formatting.None))) throw new InvalidDataException("Duplicate reference.");
            }
        }
        private static IssueKey ReadKey(JObject token)
        {
            Shape(token, "checkerId", "issueType", "subjectKey"); string checker = Text(token["checkerId"]), type = Text(token["issueType"]), subjectText = Text(token["subjectKey"], 64000);
            var subject = Parse(subjectText); IssueKey key;
            IEnumerable<string> Ids(JToken? ids) => List(ids).Select(id => Text(id));
            Dictionary<string, string> Roles(JToken? roles) => roles is JObject o ? o.Properties().ToDictionary(p => p.Name, p => Text(p.Value), StringComparer.Ordinal) : throw new InvalidDataException("Invalid issue roles.");
            if (subject["project"]?.Type == JTokenType.Boolean && (bool)subject["project"]!) key = IssueKey.ForProject(checker, type);
            else if (subject["objects"] != null) key = IssueKey.ForEntities(checker, type, Ids(subject["objects"]), Ids(subject["mates"]));
            else key = IssueKey.ForRoles(checker, type, Roles(subject["objectRoles"]), Roles(subject["mateRoles"]));
            if (key.SubjectKey != subjectText) throw new InvalidDataException("Noncanonical issue subject."); return key;
        }
        private static void ValidateRecord(JObject r, string bucket, long revision)
        {
            if (bucket == "dispositions")
            {
                Shape(r, "id", "issueKey", "references", "state", "reason", "expectedEvidenceHash", "acceptedEvidenceHash", "originSnapshotId", "updatedAt", "revision", "provenance");
                if (!(r["issueKey"] is JObject identity)) throw new InvalidDataException("Invalid issue identity.");
                var key = ReadKey(identity); if (Text(r["id"]) != Hash(KeyText(key))) throw new InvalidDataException("Issue identity/hash mismatch.");
                EnumValue<IssueDispositionState>(r["state"]); NullableText(r["reason"]); Text(r["expectedEvidenceHash"]); NullableText(r["acceptedEvidenceHash"]);
                bool open = (string?)r["state"] == "Open";
                if (open ? r["acceptedEvidenceHash"]!.Type != JTokenType.Null : !JToken.DeepEquals(r["expectedEvidenceHash"], r["acceptedEvidenceHash"])) throw new InvalidDataException("Inconsistent acceptance hash.");
                References(r["references"], true);
                var refs = (JArray)r["references"]!;
                if (key.Subjects(false).Any(id => !refs.Any(v => (string?)v["id"] == id && (string?)v["kind"] == "object")) || key.Subjects(true).Any(id => !refs.Any(v => (string?)v["id"] == id && (string?)v["kind"] == "mate"))) throw new InvalidDataException("Issue subjects are missing references.");
            }
            else
            {
                var fields = new[] { "id", "type", "value", "references", "lifecycle", "provenance", "originSnapshotId", "createdAt", "updatedAt", "revision" };
                Shape(r, bucket == "requirements" ? fields : fields.Concat(new[] { "key", "objectId" }).ToArray());
                Text(r["id"]); Text(r["type"]); ValidateValue(r["value"]!); EnumValue<MemoryLifecycle>(r["lifecycle"]); Timestamp(r["createdAt"]); References(r["references"], false);
                var refs = (JArray)r["references"]!;
                if (bucket != "requirements")
                {
                    Text(r["key"]); NullableText(r["objectId"]);
                    if (bucket == "project" ? r["objectId"]!.Type != JTokenType.Null || refs.Count != 0 : r["objectId"]!.Type != JTokenType.String || refs.Count != 1 || !JToken.DeepEquals(r["objectId"], refs[0]!["id"])) throw new InvalidDataException("Memory scope/reference mismatch.");
                }
                else if (!refs.Select(v => (string)v["id"]!).SequenceEqual(refs.Select(v => (string)v["id"]!).OrderBy(id => id, StringComparer.Ordinal))) throw new InvalidDataException("Requirement scope is not canonical.");
            }
            Text(r["originSnapshotId"]); Timestamp(r["updatedAt"]); Provenance(r["provenance"]);
            long recordRevision = RevisionValue(r["revision"]); if (recordRevision < 1 || recordRevision > revision) throw new InvalidDataException("Record revision outside committed state.");
        }
        private static void ValidateDocument(JObject doc)
        {
            Shape(doc, "schemaVersion", "revision", "project", "memory", "issueState", "history", "receipts");
            if ((string?)doc["schemaVersion"] != "1") throw new InvalidDataException("Unsupported sidecar schema version.");
            long revision = RevisionValue(doc["revision"]);
            var project = Shape(doc["project"], "projectId", "projectName", "sourceSystem", "trustedSourceProjectId", "lastKnownSnapshotId", "createdAt", "updatedAt");
            Text(project["projectId"]); Text(project["projectName"]); Text(project["lastKnownSnapshotId"]); Timestamp(project["createdAt"]); Timestamp(project["updatedAt"]);
            NullableText(project["sourceSystem"]); NullableText(project["trustedSourceProjectId"]);
            if ((project["sourceSystem"]!.Type == JTokenType.Null) != (project["trustedSourceProjectId"]!.Type == JTokenType.Null)) throw new InvalidDataException("Incomplete source project binding.");
            Shape(doc["memory"], "project", "objects", "requirements"); Shape(doc["issueState"], "dispositions"); Shape(doc["history"], "memoryChanges", "requirementChanges", "issueDispositionChanges");
            var memoryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string bucket in new[] { "project", "objects", "requirements", "dispositions" })
            {
                foreach (var record in List(bucket == "dispositions" ? doc["issueState"]![bucket] : doc["memory"]![bucket]))
                {
                    if (!(record is JObject r)) throw new InvalidDataException("Invalid record."); ValidateRecord(r, bucket, revision);
                    if ((bucket == "project" || bucket == "objects") && !memoryIds.Add((string)r["id"]!)) throw new InvalidDataException("Duplicate memory ID.");
                }
            }
            var operations = new HashSet<string>(StringComparer.Ordinal); var revisions = new HashSet<long>();
            var receiptByOperation = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var receipt in List(doc["receipts"]))
            {
                Shape(receipt, "operationId", "recordId", "revision", "fingerprint"); Text(receipt["recordId"]);
                string fingerprint = Text(receipt["fingerprint"]); if (fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid request fingerprint.");
                long r = RevisionValue(receipt["revision"]);
                if (r == 0 || r > revision || !revisions.Add(r) || !operations.Add(Text(receipt["operationId"]))) throw new InvalidDataException("Duplicate/invalid receipt identity.");
                receiptByOperation.Add((string)receipt["operationId"]!, receipt);
            }
            if (revisions.Count != revision) throw new InvalidDataException("Missing committed operation receipts.");
            var historyOps = new HashSet<string>(StringComparer.Ordinal);
            foreach (string history in new[] { "memoryChanges", "requirementChanges", "issueDispositionChanges" })
                foreach (var entry in List(doc["history"]![history]))
                {
                    Shape(entry, "operationId", "revision", "before", "after"); string op = Text(entry["operationId"]);
                    if (!operations.Contains(op) || !historyOps.Add(op)) throw new InvalidDataException("History lacks a unique receipt.");
                    var receipt = receiptByOperation[op];
                    if (!JToken.DeepEquals(receipt["revision"], entry["revision"])) throw new InvalidDataException("History revision mismatch.");
                    foreach (string field in new[] { "before", "after" })
                    {
                        if (field == "before" && entry[field]!.Type == JTokenType.Null) continue;
                        if (!(entry[field] is JObject record)) throw new InvalidDataException("Invalid history record.");
                        string bucket = history == "issueDispositionChanges" ? "dispositions" : history == "requirementChanges" ? "requirements" : record["objectId"]?.Type == JTokenType.Null ? "project" : "objects";
                        ValidateRecord(record, bucket, revision);
                        if (!JToken.DeepEquals(record["id"], receipt["recordId"])) throw new InvalidDataException("History record identity mismatch.");
                    }
                }
            if (historyOps.Count != operations.Count) throw new InvalidDataException("Missing mutation history.");
            // Validates finite JSON values throughout, including historical data.
            Canon(doc);
        }
    }
}
