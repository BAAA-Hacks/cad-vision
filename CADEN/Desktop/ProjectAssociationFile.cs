using Core.Primitives.DataStructures.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Desktop.Configuration;

public static class ProjectAssociationFile
{
    // The configuration workspace is one explicitly selected CADEN project. Filenames and
    // exporter IDs are never used to infer cross-workspace project identity.
    public static ProjectAssociation LoadOrCreate(string directory)
    {
        string path = Path.Combine(directory, "caden-project.json");
        if (!File.Exists(path))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var json = new JObject { ["schemaVersion"] = "1", ["projectId"] = Guid.NewGuid().ToString("N") };
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(json.ToString(Formatting.Indented));
                    stream.Write(data); stream.Flush(true);
                }
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { /* Another host created the association; load that one. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Project association file exceeds its limit.");
        using var text = File.OpenText(path);
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
        var doc = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || doc.Count != 2 || (string?)doc["schemaVersion"] != "1" || doc["projectId"]?.Type != JTokenType.String)
            throw new InvalidDataException("Invalid CADEN project association. File was not replaced.");
        return new ProjectAssociation((string)doc["projectId"]!);
    }
}
