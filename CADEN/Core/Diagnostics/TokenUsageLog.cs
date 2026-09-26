using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Core.Diagnostics
{
    public sealed class TurnTokenUsage
    {
        public string TurnId { get; }
        public string SessionId { get; }
        public string Model { get; }
        public DateTimeOffset StartedUtc { get; }
        public DateTimeOffset FinishedUtc { get; }
        public string Outcome { get; }
        public int ApiRequests { get; }
        // Input includes cached input. Partial totals count only provider-reported values.
        public long? InputTokens { get; }
        public long? OutputTokens { get; }
        public long? CachedInputTokens { get; }
        public bool UsageComplete { get; }
        public int RequestsWithInputUsage { get; }
        public int RequestsWithOutputUsage { get; }
        public int RequestsWithCacheUsage { get; }
        internal TurnTokenUsage(UsageAccumulator a, string session, string model, string outcome)
        {
            TurnId = a.Id; SessionId = session; Model = model; StartedUtc = a.Started; FinishedUtc = DateTimeOffset.UtcNow;
            Outcome = outcome; ApiRequests = a.Requests;
            InputTokens = a.InputCount > 0 ? a.Input : (long?)null;
            OutputTokens = a.OutputCount > 0 ? a.Output : (long?)null;
            CachedInputTokens = a.CacheCount > 0 ? a.Cached : (long?)null;
            RequestsWithInputUsage = a.InputCount; RequestsWithOutputUsage = a.OutputCount; RequestsWithCacheUsage = a.CacheCount;
            UsageComplete = a.Requests > 0 && a.InputCount == a.Requests && a.OutputCount == a.Requests && a.CacheCount == a.Requests;
        }
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);
    }

    internal sealed class UsageAccumulator
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        internal int Requests, InputCount, OutputCount, CacheCount;
        internal long Input, Output, Cached;
        private static bool Add(JToken? value, ref long total)
        {
            if (value?.Type != JTokenType.Integer || !long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number < 0 || total > long.MaxValue - number) return false;
            total += number; return true;
        }
        internal void Observe(JObject? json)
        {
            if (!(json?["usageMetadata"] is JObject usage)) return;
            if (Add(usage["promptTokenCount"], ref Input)) InputCount++;
            if (Add(usage["candidatesTokenCount"], ref Output)) OutputCount++;
            // Gemini omits cachedContentTokenCount when no cache tokens were reported.
            if (Add(usage["cachedContentTokenCount"] ?? new JValue(0), ref Cached)) CacheCount++;
        }
    }

    public static class TokenUsageLog
    {
        private static readonly object Gate = new object();
        private static Action<TurnTokenUsage>? sink;
        public static void Configure(Action<TurnTokenUsage>? writer) { lock (Gate) sink = writer; }
        internal static void Write(TurnTokenUsage entry)
        {
            Action<TurnTokenUsage>? writer; lock (Gate) writer = sink;
            if (writer == null) return;
            try { writer(entry); }
            catch (Exception ex) { DiagnosticLog.Report(ex, "usage.log_write", subjectId: entry.TurnId); }
        }
    }
}
