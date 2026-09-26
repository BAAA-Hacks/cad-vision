using System;
using System.IO;
using System.Text;
using Core.Primitives.Operations.Memory;
using Core.Diagnostics;

namespace Desktop.Persistence
{
    // Host-owned path. The core never derives a filename from an object/project name.
    public sealed class ProjectMemoryFile : IMemoryPersistence
    {
        public string FilePath { get; }
        public ProjectMemoryFile(string path)
        {
            FilePath = Path.GetFullPath(path);
            if (!FilePath.EndsWith(".caden.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Memory storage requires a dedicated .caden.json sidecar path.");
        }
        private FileStream Lock()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        private string? ReadLocked()
        {
            try
            {
                using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 10000000) throw new InvalidDataException("Memory sidecar exceeds 10 MB.");
                using var reader = new StreamReader(file, new UTF8Encoding(false, true), true); return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
        }
        public string? Read() { using var fileLock = Lock(); return ReadLocked(); }
        public void Commit(string? expectedContent, string newContent)
        {
            using var fileLock = Lock();
            if (!string.Equals(ReadLocked(), expectedContent, StringComparison.Ordinal)) throw new MemoryPersistenceConflictException("Sidecar changed outside this store. Reopen before editing.");
            if (Encoding.UTF8.GetByteCount(newContent) > 10000000) throw new InvalidDataException("Memory sidecar exceeds 10 MB.");
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(newContent);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (expectedContent == null) File.Move(temporary, FilePath);
                else File.Replace(temporary, FilePath, null);
            }
            finally
            {
                // Never turn a committed replacement into a reported failed mutation because cleanup failed.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) { DiagnosticLog.Report(ex, "memory.temp_cleanup"); }
            }
        }
    }
}
