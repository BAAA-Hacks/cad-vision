using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Tools.Query
{
    // Immutable parsed snapshot. The host supplies JSON; Core never opens a path.
    public sealed class MetadataStore
    {
        private readonly JObject document;
        private readonly Dictionary<string, JObject> objects = new Dictionary<string, JObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> sourceIndices = new Dictionary<string, int>();
        public string RootId { get; }
        public string Name => (string)document["project"]!["name"]!;
        public string SnapshotId { get; } = Guid.NewGuid().ToString("N");
        public bool IsFixture => document["sampleInfo"]?["fixture"]?.Type == JTokenType.Boolean && (bool)document["sampleInfo"]!["fixture"]!;
        public int Count => objects.Count;
        internal IEnumerable<JObject> Objects => objects.Values;
        internal JObject Document => document;

        public MetadataStore(string json)
        {
            try
            {
                if (json.Length > 10000000) throw Invalid("Metadata exceeds the 10 MB prototype limit.");
                document = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if ((string?)document["schemaVersion"] != "1.0") throw Invalid("Expected schemaVersion 1.0.");
                if (!(document["project"] is JObject project) || !Text(project["id"]) || !Text(project["name"]) || !Text(project["rootObjectId"]))
                    throw Invalid("Project requires string id, name, and rootObjectId.");
                RootId = (string)project["rootObjectId"]!;
                if (!(document["objects"] is JArray entries) || entries.Count == 0 || entries.Count > 10000)
                    throw Invalid("Expected 1 to 10,000 objects.");
                if (document["sampleInfo"] != null && !(document["sampleInfo"] is JObject)) throw Invalid("sampleInfo must be an object.");
                if (document["sampleInfo"]?["fixture"] != null && document["sampleInfo"]!["fixture"]!.Type != JTokenType.Boolean)
                    throw Invalid("sampleInfo.fixture must be a boolean.");
                var mapped = new HashSet<int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(entries[i] is JObject o) || !Text(o["id"], 128) || !Text(o["name"]) ||
                        (o["type"]?.Type != JTokenType.String || !new[] { "part", "assembly" }.Contains((string)o["type"]!)) ||
                        !(o["childIds"] is JArray children) || children.Any(c => !Text(c, 128)) ||
                        o["parentId"] == null || !(o["parentId"]!.Type == JTokenType.Null || Text(o["parentId"], 128)))
                        throw Invalid("Each object needs id, name, type, parentId, and a string-array childIds.");
                    string id = (string)o["id"]!;
                    if (objects.ContainsKey(id)) throw Invalid("Duplicate object ID: " + id);
                    if (children.Select(c => (string)c!).Distinct().Count() != children.Count) throw Invalid("Duplicate child ID on " + id);
                    if ((string)o["type"]! == "part" && children.Count != 0) throw Invalid("Parts cannot have children: " + id);
                    var index = o["glbNodeIndex"];
                    if (o["glbNodePath"] != null && o["glbNodePath"]!.Type != JTokenType.Null && o["glbNodePath"]!.Type != JTokenType.String)
                        throw Invalid("glbNodePath must be a string when present.");
                    if (index != null && index.Type != JTokenType.Null)
                    {
                        if (index.Type != JTokenType.Integer || index.Value<double>() < 0 || index.Value<double>() > int.MaxValue || !mapped.Add((int)index))
                            throw Invalid("glbNodeIndex must be unique non-negative integers.");
                    }
                    objects.Add(id, o); sourceIndices.Add(id, i);
                }
                if (!objects.ContainsKey(RootId) || objects[RootId]["parentId"]!.Type != JTokenType.Null || (string?)objects[RootId]["type"] != "assembly")
                    throw Invalid("Root must be an assembly with a null parentId.");
                foreach (var pair in objects)
                {
                    var o = pair.Value;
                    if (pair.Key != RootId)
                    {
                        string? parent = (string?)o["parentId"];
                        if (parent == null || !objects.ContainsKey(parent) || !((JArray)objects[parent]["childIds"]!).Any(c => (string)c! == pair.Key))
                            throw Invalid("Missing or inconsistent parent for " + pair.Key);
                    }
                    foreach (string child in ((JArray)o["childIds"]!).Select(v => (string)v!))
                        if (!objects.ContainsKey(child) || (string?)objects[child]["parentId"] != pair.Key)
                            throw Invalid("Missing or inconsistent child for " + pair.Key);
                }
                var visited = new HashSet<string>(); var pending = new Stack<(string id, int depth)>(); pending.Push((RootId, 0));
                while (pending.Count > 0)
                {
                    var item = pending.Pop();
                    if (!visited.Add(item.id) || item.depth > 128) throw Invalid("Hierarchy contains a cycle or exceeds 128 levels.");
                    foreach (string child in ((JArray)objects[item.id]["childIds"]!).Select(v => (string)v!)) pending.Push((child, item.depth + 1));
                }
                if (visited.Count != Count) throw Invalid("All objects must be reachable from the root; disconnected objects/cycles are invalid.");
            }
            catch (JsonException) { throw Invalid("Metadata must be valid JSON without duplicate keys."); }
            catch (InvalidCastException) { throw Invalid("Unexpected metadata field type."); }
        }
        private static bool Text(JToken? token, int max = 512) => token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)token) && ((string)token!).Length <= max;
        private static ToolInputException Invalid(string reason) => new ToolInputException("INVALID_METADATA", reason);
        internal JObject Get(string id) => objects.TryGetValue(id, out var o) ? o : throw new ToolInputException("OBJECT_NOT_FOUND", "No object has ID " + id + ". Use search_objects to find valid IDs.");
        internal string Source(string id, string field) => "/objects/" + sourceIndices[id] + "/" + field.Replace("~", "~0").Replace("/", "~1");
        internal JObject Context(string id)
        {
            var o = Get(id); var path = new List<JObject>(); JObject? current = o;
            while (current != null)
            {
                path.Insert(0, new JObject { ["id"] = current["id"]!.DeepClone(), ["name"] = current["name"]!.DeepClone() });
                current = current["parentId"]!.Type == JTokenType.Null ? null : Get((string)current["parentId"]!);
            }
            return new JObject { ["id"] = id, ["name"] = o["name"]!.DeepClone(), ["type"] = o["type"]!.DeepClone(),
                ["parentId"] = o["parentId"]!.DeepClone(), ["path"] = new JArray(path), ["childCount"] = ((JArray)o["childIds"]!).Count };
        }
        internal JObject Provenance => new JObject { ["snapshotId"] = SnapshotId, ["fixture"] = IsFixture,
            ["identityBasis"] = IsFixture ? "GLB-derived fixture structure; type labels may include wrapper assemblies. IDs apply to the matching export only." : "Exported metadata; GLB correspondence not verified by these queries.",
            ["engineeringEvidence"] = IsFixture ? "unavailable" : "exported fields only; no independent verification" };
    }
}
