using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Core.Primitives.DataStructures.Project
{
    public sealed class PrecomputeOptions
    {
        // Bounds optional duplicated memberships, not the canonical snapshot's total RAM.
        public int MaxScopeMemberships { get; set; } = 100000;
        public int OptionalStartupMilliseconds { get; set; } = 100;
        public int MaxDtoCacheCharacters { get; set; } = 250000;
    }

    // Owned by one immutable snapshot. No mutable issue/memory data is stored here.
    public sealed class ProjectIndexes
    {
        private readonly Dictionary<string, string?> parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> children = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> incident = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> scopes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> search = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly bool validHierarchy;
        public int CachedScopeCount => scopes.Count;
        public int CachedMembershipCount { get; private set; }
        public double BuildMilliseconds { get; }
        public int DtoCacheCharacterBudget { get; }
        internal ProjectIndexes(ProjectSnapshot snapshot, PrecomputeOptions options)
        {
            if (options.MaxScopeMemberships < 0 || options.OptionalStartupMilliseconds < 0 || options.MaxDtoCacheCharacters < 0) throw new ArgumentOutOfRangeException(nameof(options));
            DtoCacheCharacterBudget = options.MaxDtoCacheCharacters;
            var timer = Stopwatch.StartNew();
            validHierarchy = snapshot.Capabilities.Hierarchy == CapabilityState.Available;
            foreach (var c in snapshot.ComponentsById.Values)
            {
                var raw = c.CopyRawRecord();
                parents[c.Id] = raw["parentId"]?.Type == JTokenType.String ? (string?)raw["parentId"] : null;
                children[c.Id] = raw["childIds"] is JArray array ? array.Where(v => v.Type == JTokenType.String).Select(v => (string)v!).ToArray() : Array.Empty<string>();
                search[c.Id] = c.Id + " " + c.Name;
            }
            var lists = snapshot.ComponentsById.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var m in snapshot.MatesById.Values) { lists[m.ObjectAId].Add(m.Id); lists[m.ObjectBId].Add(m.Id); }
            foreach (var p in lists) incident[p.Key] = p.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            // Optional closure caching stops at either budget; uncached scopes use the same traversal.
            if (validHierarchy && options.OptionalStartupMilliseconds > 0)
                foreach (var c in snapshot.ComponentsById.Values.Where(c => c.Type == "assembly").OrderBy(c => c.Id, StringComparer.Ordinal))
                {
                    if (timer.ElapsedMilliseconds >= options.OptionalStartupMilliseconds || CachedMembershipCount >= options.MaxScopeMemberships) break;
                    var values = new List<string>(); var queue = new Queue<string>(); queue.Enqueue(c.Id);
                    bool fits = true;
                    while (queue.Count > 0)
                    {
                        if (values.Count >= options.MaxScopeMemberships - CachedMembershipCount || timer.ElapsedMilliseconds >= options.OptionalStartupMilliseconds) { fits = false; break; }
                        string id = queue.Dequeue(); values.Add(id); foreach (var child in children[id]) queue.Enqueue(child);
                    }
                    if (!fits) continue;
                    scopes[c.Id] = values.ToArray(); CachedMembershipCount += values.Count;
                }
            BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
        internal string? Parent(string id) => parents[id];
        internal IEnumerable<string> Children(string id) => children[id];
        internal IEnumerable<string> Ancestors(string id)
        {
            if (!validHierarchy) throw new InvalidOperationException("Hierarchy is unavailable or invalid.");
            for (var parent = parents[id]; parent != null; parent = parents[parent]) yield return parent;
        }
        internal IEnumerable<string> Scope(string id, CancellationToken token)
        {
            if (!validHierarchy) throw new InvalidOperationException("Hierarchy is unavailable or invalid.");
            if (scopes.TryGetValue(id, out var cached))
            { Core.Diagnostics.ToolPerformanceLog.Cache(true); foreach (var member in cached) { token.ThrowIfCancellationRequested(); yield return member; } yield break; }
            Core.Diagnostics.ToolPerformanceLog.Cache(false);
            var queue = new Queue<string>(); queue.Enqueue(id);
            while (queue.Count > 0) { token.ThrowIfCancellationRequested(); var member = queue.Dequeue(); yield return member; foreach (var child in children[member]) queue.Enqueue(child); }
        }
        internal IEnumerable<string> Incident(IEnumerable<string> ids) => ids.SelectMany(id => incident[id]).Distinct(StringComparer.Ordinal);
        internal bool Matches(string id, IEnumerable<string> words) => words.All(word => search[id].IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
