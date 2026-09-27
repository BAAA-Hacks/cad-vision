using System;
using System.Threading.Tasks;
using Core;
using CADEN.Unity;
using UnityEngine;

/// <summary>Panel adapter for the shared Quest CADEN host; no second client, registry or credential store.</summary>
public sealed class CadenSessionHost : MonoBehaviour
{
    private CadenUnityHost host;
    private ChatSession observedSession;
    private int observedMessages = -1;
    public ChatSession Session => host == null ? null : host.Session;
    public string Status => host == null ? "Waiting for CADEN host" : host.Status;
    public event Action Changed;
    public event Action<string> Feedback;
    public CadenVoiceInput Voice => FindAnyObjectByType<CadenVoiceInput>();
    public bool IsBusy => (host != null && host.IsBusy) || (Voice != null && Voice.Busy);
    public bool IsSpeaking => host != null && host.IsSpeaking;
    public float SpeechLevel => host == null ? 0 : host.SpeechLevel();
    public string AssemblyName
    {
        get
        {
            var metadata = host == null ? null : host.ModelRuntime?.Metadata;
            return metadata == null ? "No assembly loaded" : (string)metadata.GetObject(metadata.RootId)["name"] ?? "Current assembly";
        }
    }
    private void Update()
    {
        var found = FindAnyObjectByType<CadenUnityHost>();
        if (found != host)
        {
            if (host != null) host.ErrorReceived.RemoveListener(OnError);
            host = found;
            if (host != null) host.ErrorReceived.AddListener(OnError);
        }
        var session = Session;
        int count = session?.Messages.Count ?? 0;
        if (session != observedSession || count != observedMessages)
        {
            observedSession = session; observedMessages = count;
            Changed?.Invoke();
        }
    }
    private void OnError(string message) => Feedback?.Invoke(message);
    public async void NewChat()
    {
        Cancel();
        try
        {
            if (host == null) host = FindAnyObjectByType<CadenUnityHost>();
            if (host == null) throw new InvalidOperationException("CADEN host not found in this scene.");
            await host.ReloadAsync();
        }
        catch (Exception ex) { Feedback?.Invoke(Core.Diagnostics.DiagnosticLog.Redact(ex.Message)); }
    }
    public void Cancel() { Voice?.Cancel(); if (host != null) host.CancelTurn(); }
    public void ToggleVoice()
    {
        var voice = Voice;
        if (voice == null) { Feedback?.Invoke("Voice input has not initialized."); return; }
        if (voice.Busy && !voice.Recording) voice.Cancel(); else voice.Toggle(host);
    }
    public Task<string> SendAsync(string prompt)
    {
        if (host == null || !host.Ready) throw new InvalidOperationException(Status);
        if (IsBusy) throw new InvalidOperationException("A voice or chat request is already in progress.");
        return host.SendAsync(prompt);
    }
    private void OnDestroy() { if (host != null) host.ErrorReceived.RemoveListener(OnError); }
}