using System;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Applies the Android launcher name and existing CAD Vision artwork.</summary>
public static class CADVisionAppBranding
{
    private const string IconPath = "Assets/Branding/CADVisionIcon.png";

    [MenuItem("CAD Vision/Apply Quest App Branding")]
    public static void Apply()
    {
        AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        if (icon == null) throw new InvalidOperationException("CAD Vision launcher artwork is missing.");
        PlayerSettings.productName = "CAD Vision";
        // Keep the package identifier: changing it would install a different app.
        // Adaptive icons require background and foreground textures.
        var kind = AndroidPlatformIconKind.Adaptive;
        var icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
        if (icons.Length == 0) throw new InvalidOperationException("Android icon slots unavailable. Install Android Build Support.");
        const string backgroundPath = "Assets/Branding/CADVisionIconBackground.png";
        var background = AssetDatabase.LoadAssetAtPath<Texture2D>(backgroundPath);
        if (background == null)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
            texture.Apply();
            System.IO.File.WriteAllBytes(backgroundPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(backgroundPath, ImportAssetOptions.ForceSynchronousImport);
            background = AssetDatabase.LoadAssetAtPath<Texture2D>(backgroundPath);
        }
        if (background == null) throw new InvalidOperationException("Adaptive icon background is missing.");
        foreach (var slot in icons) slot.SetTextures(new[] { background, icon });
        PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
        AssetDatabase.SaveAssets();
        var saved = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
        foreach (var slot in saved)
            if (slot.GetTexture(0) != background || slot.GetTexture(1) != icon) throw new InvalidOperationException("Launcher icon assignment did not persist.");
        Debug.Log("CADVISION_BRANDING_OK: CAD Vision; Android launcher icon assigned to " + saved.Length + " slots.");
    }
}

public sealed class CADVisionAndroidBrandingBuildCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;
    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.Android) CADVisionAppBranding.Apply();
    }
}
