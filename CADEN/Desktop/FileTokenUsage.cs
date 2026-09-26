using Core.Diagnostics;
using System.Text;

namespace Desktop;

public sealed class FileTokenUsage
{
    private readonly object gate = new();
    public string DirectoryPath { get; }
    public FileTokenUsage(string directory) { DirectoryPath = Path.GetFullPath(directory); }
    public void Write(TurnTokenUsage entry)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "usage-" + entry.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");
            File.AppendAllText(path, entry.ToJson() + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
