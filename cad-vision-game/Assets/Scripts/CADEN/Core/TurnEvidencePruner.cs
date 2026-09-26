#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Core
{
    // Wire-only projections. The cache and local transcript retain original evidence.
    internal sealed class TurnEvidencePruner
    {
        private sealed class Entry
        {
            internal JObject Wire = null!, Original = null!;
            internal readonly HashSet<string> Followed = new HashSet<string>(StringComparer.Ordinal);
        }
        private readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<string> Discovery = new HashSet<string>(StringComparer.Ordinal)
        { "find_objects", "query_hierarchy", "find_connections", "get_mates", "list_issues", "get_issues_for_object", "get_issues_for_mate" };
        private static readonly HashSet<string> ArgumentIds = new HashSet<string>(StringComparer.Ordinal)
        { "objectId", "objectIds", "startObjectId", "endObjectId", "startObjectIds", "targetObjectIds", "mateId", "mateIds", "issueId", "issueIds" };
        private static readonly HashSet<string> RowIds = new HashSet<string>(StringComparer.Ordinal)
        { "id", "objectId", "objectIds", "endpointObjectIds", "mateId", "issueId", "candidateId" };

        private static IEnumerable<JValue> Values(JToken token) =>
            token is JContainer container ? container.Descendants().OfType<JValue>() : new[] { token }.OfType<JValue>();

        internal void ObserveCalls(IEnumerable<JObject> calls)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
                if (call["functionCall"]?["args"] is JObject args)
                    foreach (var p in args.Descendants().OfType<JProperty>().Where(p => ArgumentIds.Contains(p.Name)))
                        foreach (var value in Values(p.Value).Where(v => v.Type == JTokenType.String))
                            selected.Add((string)value!);
            foreach (var entry in entries)
            {
                entry.Followed.UnionWith(selected);
                var projection = (JObject)entry.Original.DeepClone();
                var references = new JArray();
                if (projection["data"] is JObject data)
                    foreach (string key in new[] { "items", "candidates" })
                        if (data[key] is JArray rows)
                        {
                            bool Relevant(JToken row) => row is JObject obj && obj.Descendants().OfType<JProperty>()
                                .Where(p => RowIds.Contains(p.Name)).Any(p => Values(p.Value)
                                    .Any(v => v.Type == JTokenType.String && entry.Followed.Contains((string)v!)));
                            var kept = rows.Where(Relevant).ToArray();
                            if (kept.Length == rows.Count) continue;
                            references.Add(new JObject { ["resultId"] = projection["sessionResultId"], ["path"] = "/data/" + key,
                                ["originalPageCount"] = rows.Count, ["retainedCount"] = kept.Length, ["omittedCount"] = rows.Count - kept.Length });
                            data[key] = new JArray(kept.Select(r => r.DeepClone()));
                        }
                if (references.Count > 0)
                    projection["contextPruning"] = new JObject { ["references"] = references,
                        ["meaning"] = "Unfollowed rows omitted from context, not ruled out. Coverage/pagination describe the original result, not retained rows. Recall by resultId/path for original alternatives; do not infer absence or resolved ambiguity." };
                entry.Wire.RemoveAll();
                foreach (var property in projection.Properties()) entry.Wire[property.Name] = property.Value.DeepClone();
            }
        }

        internal void Track(string name, JObject response)
        {
            if (!Discovery.Contains(name) || (bool?)response["success"] != true || response["sessionResultId"] == null
                || response["receipt"] != null && response["receipt"]!.Type != JTokenType.Null) return;
            entries.Add(new Entry { Wire = response, Original = (JObject)response.DeepClone() });
        }
    }
}
