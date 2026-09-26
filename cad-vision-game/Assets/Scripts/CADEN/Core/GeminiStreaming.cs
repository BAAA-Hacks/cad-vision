#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Core
{
    public sealed partial class GeminiClient
    {
        private static async Task<JObject> ReadStreamAsync(HttpResponseMessage response,
            Action<ChatStreamUpdate> observer, UsageAccumulator usage, CancellationToken cancellation)
        {
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new ChatException("GEMINI_STREAM_FORMAT: expected a server-sent event stream.");
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            // StreamReader.ReadLineAsync has no CancellationToken on netstandard2.1.
            using var registration = cancellation.Register(stream.Dispose);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parts = new JArray();
            var content = new JObject { ["role"] = "model", ["parts"] = parts };
            var candidate = new JObject { ["content"] = content };
            var result = new JObject { ["candidates"] = new JArray(candidate) };
            var data = new StringBuilder();
            long length = 0;
            bool toolRound = false, finished = false;
            void ProcessEvent()
            {
                if (data.Length == 0) return;
                string value = data.ToString(); data.Clear();
                if (value.Trim() == "[DONE]") return;
                var chunk = JObject.Parse(value);
                if (chunk["usageMetadata"] is JObject report) result["usageMetadata"] = report.DeepClone();
                if (chunk["promptFeedback"] != null) result["promptFeedback"] = chunk["promptFeedback"]!.DeepClone();
                if (chunk["error"] != null) throw new ChatException("GEMINI_STREAM_ERROR: provider reported an error during generation.");
                var candidates = chunk["candidates"] as JArray;
                if (candidates == null || candidates.Count == 0) return;
                if (candidates.Count != 1 || ((int?)candidates[0]?["index"] ?? 0) != 0)
                    throw new ChatException("GEMINI_STREAM_CANDIDATE: expected one answer candidate.");
                var next = candidates[0]!;
                var nextParts = next["content"]?["parts"] as JArray;
                if (nextParts != null)
                {
                    if (finished && nextParts.Count > 0) throw new ChatException("GEMINI_STREAM_ORDER: content arrived after completion.");
                    if (nextParts.OfType<JObject>().Any(p => p["functionCall"] != null))
                    {
                        toolRound = true;
                        observer(new ChatStreamUpdate(ChatStreamEvent.DiscardRound));
                    }
                    foreach (var part in nextParts.OfType<JObject>())
                    {
                        parts.Add(part.DeepClone()); // Retain call IDs and thought signatures for continuation.
                        if (!toolRound && (bool?)part["thought"] != true && part["text"]?.Type == JTokenType.String)
                            observer(new ChatStreamUpdate(ChatStreamEvent.Text, (string)part["text"]!));
                    }
                }
                if (next["finishReason"] != null)
                {
                    candidate["finishReason"] = next["finishReason"]!.DeepClone(); finished = true;
                    string finish = (string)next["finishReason"]!;
                    if (finish != "STOP" && finish != "MAX_TOKENS")
                        throw new ChatException("GEMINI_STREAM_INCOMPLETE: generation ended with " + finish + ".");
                }
            }
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    length += line.Length;
                    if (length > 8000000) throw new ChatException("GEMINI_STREAM_TOO_LARGE: response exceeds 8 million characters.");
                    if (line.Length == 0) ProcessEvent();
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart(' '));
                    }
                }
                ProcessEvent(); cancellation.ThrowIfCancellationRequested();
                if (!finished) throw new ChatException("GEMINI_STREAM_INTERRUPTED: connection ended before completion. Partial speech may already have played; this turn was not saved.");
                return result;
            }
            catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
            finally
            {
                // Streaming usage is cumulative, not a separate billable request per chunk.
                usage.Observe(result);
            }
        }
    }
}
