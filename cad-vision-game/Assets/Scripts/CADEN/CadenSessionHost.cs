using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CADVision;
using CADEN.Core;
using CADEN.Core.Tools.Query;
using CADEN.Core.Primitives.DataStructures.Memory;
using CADEN.Core.Primitives.Operations.Project;
using UnityEngine;

/// <summary>Unity host for the existing CADEN core. Credentials are supplied in memory, never serialized.</summary>
public sealed class CadenSessionHost : MonoBehaviour
{
    public ChatSession Session { get; private set; }
    public string Status { get; private set; } = "Connect CADEN to begin";
    public event Action Changed;
    public string AssemblyName { get; private set; } = "No assembly loaded";
    private readonly Dictionary<string, ChatSession> chats = new Dictionary<string, ChatSession>();
    private readonly LinkedList<string> recent = new LinkedList<string>();
    private string chatKey;
    private HttpClient http;
    private GeminiSettings settings;
    private CADVisionRuntime runtime;
    private int revision = -1;
    private CancellationTokenSource pending;

    public void Configure(GeminiSettings configuration)
    {
        Cancel();
        chats.Clear(); recent.Clear();
        settings = configuration ?? throw new ArgumentNullException(nameof(configuration));
        http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Rebuild();
    }

    private void Update()
    {
        var current = FindAnyObjectByType<CADVisionRuntime>();
        if (current != runtime || (current != null && current.Revision != revision))
        {
            runtime = current;
            Rebuild();
        }
    }

    public void NewChat()
    {
        if (chatKey != null) { chats.Remove(chatKey); recent.Remove(chatKey); }
        Rebuild();
    }
    public void Cancel() => pending?.Cancel();

    private void Rebuild()
    {
        Cancel();
        Session = null;
        revision = runtime != null ? runtime.Revision : -1;
        AssemblyName = runtime?.Metadata == null ? "No assembly loaded"
            : (string)runtime.Metadata.GetObject(runtime.Metadata.RootId)["name"] ?? "Current assembly";
        chatKey = null;
        try
        {
            if (settings == null) { Status = "Connect CADEN to begin"; return; }
            var snapshot = runtime?.Metadata == null ? null : LoadProject.Load(runtime.Metadata.RawJson).Snapshot;
            if (runtime?.Metadata != null && snapshot == null)
                throw new InvalidOperationException("The loaded model metadata is not supported by CADEN.");
            // Snapshot identity prevents old engineering answers/tools leaking into a changed export.
            chatKey = snapshot == null ? "no-model" : snapshot.ProjectId + "/" + snapshot.SnapshotId;
            if (snapshot != null) AssemblyName = snapshot.Name;
            if (chats.TryGetValue(chatKey, out var saved))
            {
                Session = saved;
                recent.Remove(chatKey); recent.AddLast(chatKey);
                Status = "Ready · conversation restored";
                return;
            }
            var association = snapshot == null ? null : new ProjectAssociation(snapshot.ProjectId, "Unity");
            var client = new GeminiClient(http, settings, SemanticQueryTools.Create(snapshot, association));
            Session = new ChatSession(client);
            chats.Add(chatKey, Session); recent.AddLast(chatKey);
            while (recent.Count > 12) { chats.Remove(recent.First.Value); recent.RemoveFirst(); }
            Status = snapshot == null ? "Ready · no model loaded" : "Ready · model queries available";
        }
        catch (Exception e) { Status = e is ChatException ? e.Message : "CADEN setup failed. Check configuration and model metadata."; }
        finally { Changed?.Invoke(); }
    }

    public async Task<string> SendAsync(string prompt)
    {
        if (Session == null) throw new InvalidOperationException("Connect CADEN first.");
        if (pending != null) throw new InvalidOperationException("A reply is already in progress.");
        var request = new CancellationTokenSource();
        pending = request;
        var session = Session;
        try
        {
            var answer = await session.SendAsync(prompt, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (session != Session) throw new OperationCanceledException();
            return answer;
        }
        finally
        {
            if (pending == request) pending = null;
            request.Dispose();
        }
    }

    private void OnDestroy() { Cancel(); http?.Dispose(); }
}
