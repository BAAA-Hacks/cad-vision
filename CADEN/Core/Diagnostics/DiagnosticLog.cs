using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Core.Diagnostics
{
    public sealed class DiagnosticEntry
    {
        public string CorrelationId { get; }
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
        public string Operation { get; }
        public string? ProjectId { get; }
        public string? SnapshotId { get; }
        public string? SubjectId { get; }
        public string ExceptionType { get; }
        public string Message { get; }
        public string ExceptionDetails { get; }
        internal DiagnosticEntry(Exception exception, string operation, string? project, string? snapshot, string? subject)
        {
            CorrelationId = Guid.NewGuid().ToString("N"); Operation = DiagnosticLog.Redact(operation);
            ProjectId = project == null ? null : DiagnosticLog.Redact(project); SnapshotId = snapshot == null ? null : DiagnosticLog.Redact(snapshot);
            SubjectId = subject == null ? null : DiagnosticLog.Redact(subject);
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            Message = DiagnosticLog.Redact(exception.Message); ExceptionDetails = DiagnosticLog.Redact(exception.ToString());
        }
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);
    }

    public sealed class DiagnosticReceipt
    {
        public DiagnosticEntry Entry { get; }
        public string Location { get; }
        public bool LogWriteFailed { get; }
        internal DiagnosticReceipt(DiagnosticEntry entry, string location, bool failed) { Entry = entry; Location = location; LogWriteFailed = failed; }
        public string UserMessage => Entry.Operation + " failed: " + Entry.ExceptionType + ": " + Entry.Message
            + "\nDiagnostic ID: " + Entry.CorrelationId + "\nDiagnostics: " + Location
            + (LogWriteFailed ? "\nWARNING: Writing the diagnostic log failed; details were retained in memory and sent to stderr.\n" + Entry.ExceptionDetails : "");
    }

    // Host-configurable, with no filesystem or UI dependency. Unexpected failures remain observable without a host sink.
    public static class DiagnosticLog
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<DiagnosticEntry> Recent = new Queue<DiagnosticEntry>();
        private static Action<DiagnosticEntry>? sink;
        private static string location = "stderr and in-memory diagnostics";
        public static event Action<DiagnosticReceipt>? Reported;
        public static void Configure(Action<DiagnosticEntry>? writer, string description)
        { lock (Gate) { sink = writer; location = description; } }
        public static void RegisterSecret(string value)
        { if (!string.IsNullOrEmpty(value)) lock (Gate) { Secrets.Add(value); Secrets.Add(Uri.EscapeDataString(value)); } }
        public static IReadOnlyList<DiagnosticEntry> GetRecent()
        { lock (Gate) return Recent.ToList().AsReadOnly(); }
        public static string Redact(string value)
        {
            string[] secrets; lock (Gate) secrets = Secrets.OrderByDescending(s => s.Length).ToArray();
            foreach (var secret in secrets) value = value.Replace(secret, "[REDACTED]");
            value = Regex.Replace(value, @"AIza[\w-]+", "[REDACTED]");
            value = Regex.Replace(value, @"(?i)(bearer\s+)[^\s,;]+", "$1[REDACTED]");
            return Regex.Replace(value, @"(?i)([""']?(?:api[_-]?key|x-goog-api-key|access_token|password|secret)[""']?\s*[=:]\s*[""']?)[^\s,;""'}]+", "$1[REDACTED]");
        }
        public static DiagnosticReceipt Report(Exception exception, string operation, string? projectId = null, string? snapshotId = null, string? subjectId = null)
        {
            var entry = new DiagnosticEntry(exception, operation, projectId, snapshotId, subjectId);
            Action<DiagnosticEntry>? writer; string destination;
            lock (Gate)
            {
                Recent.Enqueue(entry); while (Recent.Count > 100) Recent.Dequeue();
                writer = sink; destination = location;
            }
            bool failed = false;
            try { if (writer == null) Console.Error.WriteLine(entry.ToJson()); else writer(entry); }
            catch (Exception loggingError)
            {
                failed = true;
                try { Console.Error.WriteLine("DIAGNOSTIC_LOG_WRITE_FAILED: " + Redact(loggingError.ToString())); Console.Error.WriteLine(entry.ToJson()); } catch { /* In-memory record and receipt still expose the failure. */ }
            }
            var receipt = new DiagnosticReceipt(entry, destination, failed);
            foreach (Action<DiagnosticReceipt> observer in Reported?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { observer(receipt); } catch (Exception observerError) { try { Console.Error.WriteLine("DIAGNOSTIC_OBSERVER_FAILED: " + Redact(observerError.ToString())); } catch { } }
            return receipt;
        }
    }
}
