using System;
using CADVision;
using UnityEditor;
using UnityEngine;

public static class CadVisionLocalDesign
{
    [MenuItem("CAD Vision/Start receiver (Play Mode)")]
    public static void StartReceiver()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog("CAD Vision", "Enter Play Mode first.", "OK");
            return;
        }
        var loader = UnityEngine.Object.FindFirstObjectByType<CadModelLoader>();
        if (loader == null) loader = new GameObject("CADVisionRuntime").AddComponent<CadModelLoader>();
        var receiver = loader.GetComponent<CadDesignReceiver>() ?? loader.gameObject.AddComponent<CadDesignReceiver>();
        receiver.StartReceiver();
    }

    [MenuItem("CAD Vision/Load local design (Play Mode)")]
    public static async void Load()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog("CAD Vision", "Enter Play Mode, then load a GLB and its matching metadata JSON.", "OK");
            return;
        }
        string glb = EditorUtility.OpenFilePanel("Select model.glb", "", "glb");
        if (string.IsNullOrEmpty(glb)) return;
        string json = EditorUtility.OpenFilePanel("Select matching metadata.json", System.IO.Path.GetDirectoryName(glb), "json");
        if (string.IsNullOrEmpty(json)) return;
        var loader = UnityEngine.Object.FindFirstObjectByType<CadModelLoader>();
        if (loader == null) loader = new GameObject("CADVisionRuntime").AddComponent<CadModelLoader>();
        try
        {
            await loader.LoadFilesAsync(glb, json);
            Debug.Log($"CAD Vision ready: {loader.GetComponent<CADVisionRuntime>().GetAllObjects().Count} CAD objects.");
        }
        catch (Exception e) { Debug.LogError("CAD Vision import failed: " + e.Message); }
    }
}
