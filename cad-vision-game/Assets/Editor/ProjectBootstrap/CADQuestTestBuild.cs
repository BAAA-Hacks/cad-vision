using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Builds a local test APK without changing package identity or signing settings.</summary>
public static class CADQuestTestBuild
{
    [MenuItem("CAD Vision/Build Quest Test APK")]
    public static void Build()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Debug.LogError("[CAD room] Switch the active build target to Android first.");
            return;
        }

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
        {
            Debug.LogError("[CAD room] No enabled scene is available to build.");
            return;
        }

        string outputDirectory = Path.GetFullPath("Builds");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "CADVision-Multiplayer-Test.apk");
        EditorUserBuildSettings.buildAppBundle = false;
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        if (report.summary.result == BuildResult.Succeeded)
            Debug.Log("[CAD room] Test APK ready: " + output);
        else
            Debug.LogError("[CAD room] Test APK failed: " + report.summary.result +
                " (" + report.summary.totalErrors + " errors)");
    }
}
