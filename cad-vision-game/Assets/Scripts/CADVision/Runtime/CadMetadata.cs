using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADVision
{
    /// <summary>Export schemas 1.0 and 2.1; retains unknown engineering fields.</summary>
    public sealed class CadMetadata
    {
        private readonly JObject document;
        private readonly Dictionary<string, JObject> objects;
        private readonly Dictionary<string, int> nodeIndices;
        private readonly Dictionary<string, string> parents;

        public string RawJson { get; }
        public bool HasVerifiedNodeMapping { get; }
        public string RootId { get; }
        public IReadOnlyDictionary<string, int> NodeIndices { get; }
        // Return copies so consumers cannot invalidate the validated registry contract.
        public JObject Document => (JObject)document.DeepClone();
        public JObject Project => (JObject)document["project"].DeepClone();
        public JObject GetObject(string id) => objects.TryGetValue(id, out var value)
            ? (JObject)value.DeepClone() : throw new KeyNotFoundException($"Unknown CAD ID '{id}'.");
        public string GetParentId(string id) => parents[id];

        public CadMetadata(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Metadata is empty.");
            RawJson = json;
            try
            {
                document = JObject.Parse(json, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (JsonException e) { throw new InvalidDataException("Metadata is not a valid JSON object.", e); }

            var version = document["schemaVersion"];
            if (version == null || (version.Type != JTokenType.String && version.Type != JTokenType.Integer)
                || (version.ToString() != "1" && version.ToString() != "1.0" && version.ToString() != "2.1"))
                throw new InvalidDataException("Unsupported schemaVersion; expected 1, 1.0 or 2.1.");
            bool uncorrelated = version.ToString() == "2.1" &&
                (string)document["mappingStatus"] == "not_correlated_to_glb";
            if (uncorrelated && document["glbMapping"] != null && document["glbMapping"].Type != JTokenType.Null)
                throw new InvalidDataException("Uncorrelated metadata must not claim a GLB mapping.");
            HasVerifiedNodeMapping = !uncorrelated;
            if (!(document["project"] is JObject)) throw new InvalidDataException("Missing project object.");
            RootId = RequiredString((JObject)document["project"], "rootObjectId");
            if (!(document["objects"] is JArray entries) || entries.Count == 0)
                throw new InvalidDataException("objects must be a nonempty array.");

            objects = new Dictionary<string, JObject>(StringComparer.Ordinal);
            nodeIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            parents = new Dictionary<string, string>(StringComparer.Ordinal);
            var usedNodes = new HashSet<int>();
            foreach (var entry in entries)
            {
                if (!(entry is JObject obj)) throw new InvalidDataException("Every object entry must be an object.");
                string id = RequiredString(obj, "id");
                if (objects.ContainsKey(id)) throw new InvalidDataException($"Duplicate CAD ID '{id}'.");
                var node = obj["glbNodeIndex"];
                if (uncorrelated)
                {
                    if (node != null && node.Type != JTokenType.Null)
                        throw new InvalidDataException("Uncorrelated metadata must not claim GLB node indices.");
                }
                else
                {
                    if (node?.Type != JTokenType.Integer || !int.TryParse(node.ToString(), out int index) || index < 0)
                        throw new InvalidDataException($"'{id}' requires a nonnegative glbNodeIndex.");
                    if (!usedNodes.Add(index)) throw new InvalidDataException($"GLB node {index} is mapped more than once.");
                    nodeIndices.Add(id, index);
                }
                var parent = obj["parentId"];
                if (parent != null && parent.Type != JTokenType.Null &&
                    (parent.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)parent)))
                    throw new InvalidDataException($"'{id}' has an invalid parentId.");
                objects.Add(id, obj);
                parents.Add(id, (string)parent);
            }
            if (!objects.ContainsKey(RootId)) throw new InvalidDataException("rootObjectId does not exist in objects.");
            if (parents[RootId] != null) throw new InvalidDataException("The root object must not have a parent.");
            foreach (string id in objects.Keys)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string cursor = id;
                while (cursor != RootId)
                {
                    if (!visited.Add(cursor)) throw new InvalidDataException($"Hierarchy cycle at '{cursor}'.");
                    if (!parents.TryGetValue(cursor, out string parent) || parent == null || !objects.ContainsKey(parent))
                        throw new InvalidDataException($"'{cursor}' does not connect to root '{RootId}'.");
                    cursor = parent;
                }
                if (objects[id]["childIds"] is JToken childToken)
                {
                    if (!(childToken is JArray children)) throw new InvalidDataException($"'{id}' has invalid childIds.");
                    var listed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var child in children)
                    {
                        if (child.Type != JTokenType.String || !listed.Add((string)child) ||
                            !parents.TryGetValue((string)child, out var owner) || owner != id)
                            throw new InvalidDataException($"'{id}' has inconsistent childIds.");
                    }
                    foreach (var parent in parents)
                        if (parent.Value == id && !listed.Contains(parent.Key))
                            throw new InvalidDataException($"'{id}' is missing child '{parent.Key}'.");
                }
            }
            NodeIndices = new ReadOnlyDictionary<string, int>(nodeIndices);
        }

        private static string RequiredString(JObject obj, string key)
        {
            if (obj[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)obj[key]))
                throw new InvalidDataException($"Missing or invalid '{key}'.");
            return (string)obj[key];
        }
    }
}
