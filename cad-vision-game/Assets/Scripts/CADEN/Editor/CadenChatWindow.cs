using System;
using CADEN.Unity;
using UnityEditor;
using UnityEngine;

public sealed class CadenChatWindow : EditorWindow
{
    private string input = "";
    private string history = "";
    private bool sending;
    private Vector2 scroll;
    [MenuItem("CADEN/Chat")]
    public static void Open() => GetWindow<CadenChatWindow>("CADEN");
    private void OnInspectorUpdate() => Repaint();
    private void OnGUI()
    {
        var host = UnityEngine.Object.FindAnyObjectByType<CadenUnityHost>();
        EditorGUILayout.LabelField(host == null ? "Enter Play mode in the CAD Vision scene." : host.Status);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.SelectableLabel(history, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(200), GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
        input = EditorGUILayout.TextArea(input, GUILayout.Height(60));
        using (new EditorGUI.DisabledScope(!Application.isPlaying || host == null))
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(sending || host == null || !host.Ready || string.IsNullOrWhiteSpace(input)))
                if (GUILayout.Button("Send")) Send(host);
            if (GUILayout.Button("Stop voice")) host.StopSpeech();
            if (GUILayout.Button("Cancel")) host.CancelTurn();
            if (GUILayout.Button("New chat / reload settings")) Reload(host);
            EditorGUILayout.EndHorizontal();
        }
    }
    private async void Send(CadenUnityHost host)
    {
        string prompt = input; input = ""; sending = true; history += "YOU: " + prompt + "\n";
        try { history += "CADEN: " + await host.SendAsync(prompt) + "\n\n"; }
        catch (OperationCanceledException) { history += "Cancelled.\n\n"; }
        catch (Exception ex) { history += "ERROR: " + Core.Diagnostics.DiagnosticLog.Redact(ex.Message) + "\n\n"; }
        finally { sending = false; if (this != null) Repaint(); }
    }
    private async void Reload(CadenUnityHost host) { history = ""; await host.ReloadAsync(); if (this != null) Repaint(); }
}
