#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Speech
{
    // Complete sentences avoid speaking half a number or sending every token as a paid request.
    public sealed class SpeechTextBuffer
    {
        private readonly StringBuilder pending = new StringBuilder();
        private int length;
        public IReadOnlyList<string> Append(string text, bool complete = false)
        {
            length += text.Length;
            if (length > 5000) throw new ArgumentException("Speech exceeds the 5,000-character limit; the full answer remains in chat.");
            pending.Append(text);
            var results = new List<string>();
            for (int i = 0; i + 1 < pending.Length; i++)
            {
                char c = pending[i];
                if ((c == '.' || c == '!' || c == '?') && char.IsWhiteSpace(pending[i + 1]) && i >= 30)
                {
                    results.Add(pending.ToString(0, i + 1).Trim());
                    pending.Remove(0, i + 1); i = -1;
                }
            }
            if (complete && pending.Length > 0)
            {
                string remainder = pending.ToString().Trim(); pending.Clear();
                if (remainder.Length > 0) results.Add(remainder);
            }
            return results;
        }
    }
}
