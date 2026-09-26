using System;
using Newtonsoft.Json.Linq;

namespace Core.Diagnostics
{
    public static class ToolPerformanceLog
    {
        private static readonly object gate = new object();
        private static Action<JObject>? sink;
        internal sealed class Probe { internal int Hits, Misses; }
        internal static readonly System.Threading.AsyncLocal<Probe?> Current = new System.Threading.AsyncLocal<Probe?>();
        internal static void Cache(bool hit) { var probe = Current.Value; if (probe != null) { if (hit) probe.Hits++; else probe.Misses++; } }
        public static void Configure(Action<JObject>? writer) { lock (gate) sink = writer; }
        internal static bool Enabled { get { lock (gate) return sink != null; } }
        internal static void Write(JObject entry)
        {
            Action<JObject>? writer; lock (gate) writer = sink;
            try { writer?.Invoke(entry); }
            catch (Exception ex) { DiagnosticLog.Report(ex, "performance.log_write"); }
        }
    }
}
