using System;
using UnityEditor;
using UnityEngine;

public static class CadenPanelEditor
{
    [MenuItem("CAD Vision/CADEN/Open panel (Play Mode)")]
    public static void Open()
    {
        if (!Application.isPlaying) { EditorUtility.DisplayDialog("CADEN", "Enter Play Mode first, then open the panel.", "OK"); return; }
        var panel = CadenPanel.EnsurePanel();
        if (panel == null) panel = new GameObject("CADEN · Design assistant").AddComponent<CadenPanel>();
        panel.ShowPanel();
        Selection.activeGameObject = panel.gameObject;
    }

    [MenuItem("CAD Vision/CADEN/Connect local CADEN (Editor only)")]
    public static void Connect()
    {
        if (!Application.isPlaying) { EditorUtility.DisplayDialog("CADEN", "Enter Play Mode first. Connection uses your local CADEN configuration; sending a message uses its Gemini account.", "OK"); return; }
        try
        {
            Open();

            FindHost().NewChat();
        }
        catch (Exception)
        {
            EditorUtility.DisplayDialog("CADEN configuration", "Could not connect local CADEN. Check CADEN/.env (GEMINI_API_KEY) and CADEN/prompts/system.md. No credentials are copied into the Unity project.", "OK");
        }
    }

    private static CadenSessionHost FindHost() => UnityEngine.Object.FindAnyObjectByType<CadenSessionHost>();
}
