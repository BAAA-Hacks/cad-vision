using System;
using System.IO;
using CADVision;
using NUnit.Framework;

public class CadFilesTests
{
    private string folder;
    [SetUp] public void SetUp() => Directory.CreateDirectory(folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
    [TearDown] public void TearDown() => Directory.Delete(folder, true);

    [Test] public void FindsDifferentNamesAndIgnoresUnityMetaFiles()
    {
        File.WriteAllText(Path.Combine(folder, "assembly.GLB"), "");
        File.WriteAllText(Path.Combine(folder, "export-data.JSON"), "");
        File.WriteAllText(Path.Combine(folder, "assembly.GLB.meta"), "");
        var pair = CadFilesPackage.FindPair(folder);
        Assert.That(Path.GetFileName(pair.glb), Is.EqualTo("assembly.GLB"));
        Assert.That(Path.GetFileName(pair.json), Is.EqualTo("export-data.JSON"));
    }

    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(0, 1)]
    [TestCase(2, 1)]
    [TestCase(1, 2)]
    public void RejectsMissingOrAmbiguousPairs(int glbs, int jsons)
    {
        for (int i = 0; i < glbs; i++) File.WriteAllText(Path.Combine(folder, i + ".glb"), "");
        for (int i = 0; i < jsons; i++) File.WriteAllText(Path.Combine(folder, i + ".json"), "");
        Assert.Throws<InvalidDataException>(() => CadFilesPackage.FindPair(folder));
    }
}
