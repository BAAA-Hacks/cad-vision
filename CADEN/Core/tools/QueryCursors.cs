using System;
using System.Collections.Generic;
using Core.Primitives.DataStructures.Issues;
using Newtonsoft.Json.Linq;

namespace Core.Tools
{
    // Session-local opaque tokens. Binding includes all normalized query arguments and immutable context.
    public sealed class QueryCursors
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, (string Binding, int Offset)> cursors = new Dictionary<string, (string, int)>();
        private static string Binding(string tool, string project, string snapshot, JObject args, string? subsystem, long? revision)
        {
            var query = (JObject)args.DeepClone(); query.Remove("cursor"); query.Remove("offset");
            if (query["limit"] == null) query["limit"] = 20;
            return IssueValidation.Canonical(new JObject { ["tool"] = tool, ["project"] = project, ["snapshot"] = snapshot, ["query"] = query, ["subsystem"] = subsystem, ["revision"] = revision });
        }
        public int Resolve(string tool, string project, string snapshot, JObject args, string? subsystem = null, long? revision = null)
        {
            if (args["cursor"] == null) return 0;
            string binding = Binding(tool, project, snapshot, args, subsystem, revision);
            lock (gate)
            {
                if (!cursors.TryGetValue((string)args["cursor"]!, out var stored) || stored.Binding != binding)
                    throw new ToolInputException("INVALID_CURSOR", "Cursor expired or does not match this query, project, snapshot or revision.");
                return stored.Offset;
            }
        }
        public string Issue(string tool, string project, string snapshot, JObject args, int offset, string? subsystem = null, long? revision = null)
        {
            lock (gate)
            {
                string binding = Binding(tool, project, snapshot, args, subsystem, revision);
                foreach (var existing in cursors) if (existing.Value.Binding == binding && existing.Value.Offset == offset) return existing.Key;
                if (cursors.Count >= 4096) throw new ToolInputException("QUERY_TOO_LARGE", "Session cursor capacity reached; start a new chat.");
                string token = Guid.NewGuid().ToString("N"); cursors.Add(token, (binding, offset)); return token;
            }
        }
    }
}
