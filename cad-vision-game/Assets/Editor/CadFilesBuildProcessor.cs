using System.IO;
using CADVision;
using UnityEditor.Build;
using UnityEngine;

public sealed class CadFilesBuildProcessor : BuildPlayerProcessor
{
    public override void PrepareForBuild(BuildPlayerContext context)
    {
        if (!CadFilesPackage.TryFindPair(CadFilesPackage.SourceFolder(Application.dataPath), out var pair))
            return;
        CadGlbPackage.Validate(File.ReadAllBytes(pair.glb), new CadMetadata(File.ReadAllText(pair.json)));
        // Package raw files, independent of Unity's GLB asset importer and source names.
        context.AddAdditionalPathToStreamingAssets(pair.glb, "CadFiles/model.glb");
        context.AddAdditionalPathToStreamingAssets(pair.json, "CadFiles/metadata.json");
    }
}
