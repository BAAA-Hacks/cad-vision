using Core.Diagnostics;
using System.Text;

namespace Desktop;

public sealed class FileDiagnostics
{
    private readonly object gate = new();
    public string DirectoryPath { get; }
    public FileDiagnostics(string directory) { DirectoryPath = Path.GetFullPath(directory); }
    public void Write(DiagnosticEntry entry)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "caden-" + entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl");
            File.AppendAllText(path, entry.ToJson() + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
