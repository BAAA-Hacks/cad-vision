using System;
using System.IO;
using System.Linq;

namespace CADVision
{
    public static class CadFilesPackage
    {
        public static (string glb, string json) FindPair(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Create Assets/CadFiles and add one GLB and one metadata JSON.");
            var files = Directory.GetFiles(folder);
            var glbs = files.Where(p => string.Equals(Path.GetExtension(p), ".glb", StringComparison.OrdinalIgnoreCase)).ToArray();
            var jsons = files.Where(p => string.Equals(Path.GetExtension(p), ".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (glbs.Length != 1 || jsons.Length != 1)
                throw new InvalidDataException($"CadFiles requires exactly one GLB and one JSON; found {glbs.Length} GLB and {jsons.Length} JSON files.");
            return (glbs[0], jsons[0]);
        }

        public static string SourceFolder(string assetsPath)
        {
            return Directory.GetDirectories(assetsPath).FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), "CadFiles", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(assetsPath, "CadFiles");
        }
    }
}
