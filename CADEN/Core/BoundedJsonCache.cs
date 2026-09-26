using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core
{
    // Character/entry bounded retained DTOs; callers always receive independent copies.
    internal sealed class BoundedJsonCache
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, (JObject Value, int Size)> entries = new Dictionary<string, (JObject, int)>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();
        private readonly int budget;
        private int size;
        internal BoundedJsonCache(int budget) { this.budget = budget; }
        internal JObject Get(string key, Func<JObject> build)
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out var cached)) { Diagnostics.ToolPerformanceLog.Cache(true); return (JObject)cached.Value.DeepClone(); }
                Diagnostics.ToolPerformanceLog.Cache(false);
                var result = build();
                int count = key.Length + result.ToString(Formatting.None).Length;
                if (count <= budget)
                {
                    while (entries.Count >= 128 || size + count > budget) { string first = order.Dequeue(); size -= entries[first].Size; entries.Remove(first); }
                    entries.Add(key, ((JObject)result.DeepClone(), count)); order.Enqueue(key); size += count;
                }
                return result;
            }
        }
        internal void Clear() { lock (gate) { entries.Clear(); order.Clear(); size = 0; } }
    }
}
