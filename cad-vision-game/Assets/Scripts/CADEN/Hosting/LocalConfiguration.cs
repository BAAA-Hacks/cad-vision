#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Core;

namespace Desktop.Configuration
{
    // Local host adapter only. Unity will supply settings and prompt text through its own host.
    public static class LocalConfiguration
    {
        public static string FindDirectory(string? explicitDirectory = null)
        {
            if (explicitDirectory != null) return Path.GetFullPath(explicitDirectory);
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "prompts", "system.md"))) return dir.FullName;
                    string child = Path.Combine(dir.FullName, "CADEN");
                    if (File.Exists(Path.Combine(child, "prompts", "system.md"))) return child;
                }
            }
            throw new ArgumentException("Could not locate CADEN/prompts/system.md. Launch from the CADEN folder or pass --config-dir <folder>.");
        }

        public static GeminiSettings Load(string directory, string? promptOverride = null)
        {
            var values = ReadValues(directory);
            string Get(string name, string fallback = "") => Environment.GetEnvironmentVariable(name)
                ?? (values.TryGetValue(name, out string? value) ? value : fallback);
            string key = Get("GEMINI_API_KEY").Trim();
            if (key.Length == 0) key = Get("GOOGLE_API_KEY").Trim();
            int Number(string name, int fallback)
            {
                if (!int.TryParse(Get(name, fallback.ToString()), out int result) || result <= 0)
                    throw new ArgumentException(name + " must be a positive integer.");
                return result;
            }
            string promptPath = Path.Combine(directory, "prompts", "system.md");
            if (promptOverride == null && !File.Exists(promptPath)) throw new ArgumentException("Missing prompts/system.md in the CADEN configuration directory.");
            return new GeminiSettings(key, Get("GEMINI_MODEL", "gemini-flash-latest").Trim(), promptOverride ?? File.ReadAllText(promptPath),
                Number("GEMINI_TIMEOUT_SECONDS", 60), Number("GEMINI_MAX_OUTPUT_TOKENS", 4096),
                Number("GEMINI_MAX_TOOL_ROUNDS", 12), Number("GEMINI_MAX_TOOL_CALLS", 48));
        }

        public static Core.Speech.ElevenLabsSettings? LoadSpeech(string directory)
        {
            var values = ReadValues(directory);
            string Get(string name, string fallback = "") => Environment.GetEnvironmentVariable(name)
                ?? (values.TryGetValue(name, out string? value) ? value : fallback);
            if (!bool.TryParse(Get("ELEVENLABS_ENABLED", "false"), out bool enabled))
                throw new ArgumentException("ELEVENLABS_ENABLED must be true or false.");
            if (!enabled) return null;
            if (!int.TryParse(Get("ELEVENLABS_TIMEOUT_SECONDS", "30"), out int timeout))
                throw new ArgumentException("ELEVENLABS_TIMEOUT_SECONDS must be an integer.");
            return new Core.Speech.ElevenLabsSettings(Get("ELEVENLABS_API_KEY"), Get("ELEVENLABS_VOICE_ID"), Get("ELEVENLABS_MODEL", "eleven_flash_v2_5"), timeout);
        }

        private static Dictionary<string, string> ReadValues(string directory)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string envPath = Path.Combine(directory, ".env");
            if (File.Exists(envPath))
            {
                foreach (string raw in File.ReadAllLines(envPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("export ")) line = line.Substring(7).Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0) throw new ArgumentException("Invalid .env format. Use one NAME=value assignment per line.");
                    string name = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    if (value.StartsWith("\"") || value.StartsWith("'"))
                    {
                        char quote = value[0];
                        int end = value.IndexOf(quote, 1);
                        if (end < 0) throw new ArgumentException("Unclosed quote in .env. Only single-line values are supported.");
                        string tail = value.Substring(end + 1).Trim();
                        if (tail.Length > 0 && !tail.StartsWith("#")) throw new ArgumentException("Unexpected text after a quoted .env value.");
                        value = value.Substring(1, end - 1);
                    }
                    else
                    {
                        int comment = value.IndexOf(" #", StringComparison.Ordinal);
                        if (comment >= 0) value = value.Substring(0, comment).TrimEnd();
                    }
                    values[name] = value;
                }
            }
            return values;
        }
    }
}
