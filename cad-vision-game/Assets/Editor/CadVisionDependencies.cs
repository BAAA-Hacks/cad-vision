using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class CadVisionDependencies
{
    private static AddAndRemoveRequest request;
    [MenuItem("CAD Vision/Install ingestion dependencies")]
    public static void Install()
    {
        if (request != null) return;
        request = Client.AddAndRemove(new[] { "com.unity.cloud.gltfast", "com.unity.nuget.newtonsoft-json" });
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (!request.IsCompleted) return;
        EditorApplication.update -= Poll;
        if (request.Status == StatusCode.Success) Debug.Log("CAD Vision ingestion dependencies installed.");
        else Debug.LogError("CAD Vision dependency installation failed: " + request.Error.message);
        request = null;
    }
}
