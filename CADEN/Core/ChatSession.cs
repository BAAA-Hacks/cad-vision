using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Core
{
    public sealed class ChatMessage
    {
        public string Role { get; }
        public string Text { get; }
        internal JObject? WireContent { get; }
        public ChatMessage(string role, string text) { Role = role; Text = text; }
        internal ChatMessage(JObject content, string text = "")
        {
            Role = (string)content["role"]!; Text = text; WireContent = (JObject)content.DeepClone();
        }
    }

    public sealed class ChatReply
    {
        public string Text { get; }
        internal IReadOnlyList<ChatMessage> Continuation { get; }
        public ChatReply(string text) : this(text, new[] { new ChatMessage("model", text) }) { }
        internal ChatReply(string text, IReadOnlyList<ChatMessage> continuation) { Text = text; Continuation = continuation; }
    }

    public interface IChatClient
    {
        Task<ChatReply> ReplyAsync(IReadOnlyList<ChatMessage> history, string prompt, CancellationToken cancellation);
    }

    public sealed class ChatException : Exception
    {
        public ChatException(string message) : base(message) { }
    }

    // No UI, filesystem, environment, or Unity dependencies. Failed turns never enter history.
    public sealed class ChatSession
    {
        private readonly IChatClient client;
        private readonly List<ChatMessage> messages = new List<ChatMessage>();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public IReadOnlyList<ChatMessage> Messages => messages.AsReadOnly();

        public ChatSession(IChatClient client) { this.client = client; }

        public async Task<string> SendAsync(string prompt, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a message.");
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                ChatReply answer = await client.ReplyAsync(messages.ToArray(), prompt.Trim(), cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(answer.Text)) throw new ChatException("Gemini returned no text.");
                messages.Add(new ChatMessage("user", prompt.Trim()));
                messages.AddRange(answer.Continuation);
                return answer.Text;
            }
            finally { gate.Release(); }
        }

        public void Clear()
        {
            if (!gate.Wait(0)) throw new InvalidOperationException("Cancel or finish the current request before resetting.");
            try { messages.Clear(); }
            finally { gate.Release(); }
        }
    }
}
