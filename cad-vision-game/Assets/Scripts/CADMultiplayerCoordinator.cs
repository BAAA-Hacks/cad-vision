using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CADVision;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// A two-person, host-authoritative CAD room. NGO carries ID-addressed commands;
/// Meta's shared anchor supplies the physical coordinate frame on each headset.
/// </summary>
[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
[RequireComponent(typeof(CADVisionManipulationService))]
public sealed class CADMultiplayerCoordinator : MonoBehaviour
{
    private const string MessageName = "cadvision.room.v1";
    private const string SessionType = "cadvision.same-room.v1";
    private const int MaxPacketBytes = 2 * 1024 * 1024;
    private const float PoseInterval = 0.05f;
    private const float HeartbeatInterval = 1f;

    public enum RoomState { Offline, Connecting, Localizing, Syncing, Ready, Error }
    public RoomState State { get; private set; } = RoomState.Offline;
    public string Status { get; private set; } = "Offline";
    public string RoomCode => session?.Code ?? string.Empty;
    public bool IsInRoom => roomActive;
    public bool IsHost => session != null && session.IsHost;
    public int ParticipantCount => session?.Players?.Count ?? 0;

    private CADVisionManipulationService service;
    private CADXRPassthroughToggle environment;
    private CADSharedAnchor alignment;
    private NetworkManager network;
    private ISession session;
    private CADMultiplayerLeaseTable leases;
    private readonly Dictionary<string, CADObject> byId = new();
    private readonly Dictionary<string, CADRoomPart> accepted = new();
    private readonly Dictionary<string, CADRoomPart> lastSent = new();
    private CADRoomPart acceptedModel;
    private readonly HashSet<ulong> waitingForAnchor = new();
    private readonly HashSet<ulong> readyClients = new();
    private readonly List<CADRoomWire> bufferedUpdates = new();
    private string[] localTargets;
    private ulong localToken;
    private int localLeaseReferences;
    private bool leasePending;
    private bool roomActive;
    private bool everReady;
    private bool applyingNetworkState;
    private long revision;
    private long receivedRevision;
    private float nextPoseTime;
    private float nextHeartbeatTime;
    private int operationGeneration;

    private void Awake()
    {
        service = GetComponent<CADVisionManipulationService>();
        environment = GetComponent<CADXRPassthroughToggle>();
        alignment = GetComponent<CADSharedAnchor>();
        if (alignment == null) alignment = gameObject.AddComponent<CADSharedAnchor>();
        service.ModelReplaced += OnModelReplaced;
        OnModelReplaced();
    }

    private void OnModelReplaced()
    {
        byId.Clear();
        foreach (CADObject obj in service.GetRegisteredObjects())
            if (!string.IsNullOrEmpty(obj.id)) byId[obj.id] = obj;
        leases = new CADMultiplayerLeaseTable(id => byId.ContainsKey(id), service.GetLogicalParentId);
        if (roomActive && State == RoomState.Ready)
            SetError("The CAD model changed. Leave and create a new room.");
    }

    private void EnsureNetworkManager()
    {
        network = NetworkManager.Singleton;
        if (network == null)
        {
            var go = new GameObject("CAD Room Network");
            go.SetActive(false);
            UnityTransport initialTransport = go.AddComponent<UnityTransport>();
            network = go.AddComponent<NetworkManager>();
            network.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = initialTransport,
                EnableSceneManagement = false,
                PlayerPrefab = null
            };
            go.SetActive(true);
            DontDestroyOnLoad(go);
        }
        UnityTransport transport = network.GetComponent<UnityTransport>();
        if (transport == null) transport = network.gameObject.AddComponent<UnityTransport>();
        // MPS reads the transport from NetworkConfig, not just the component.
        if (network.NetworkConfig == null) network.NetworkConfig = new NetworkConfig();
        network.NetworkConfig.NetworkTransport = transport;
        network.NetworkConfig.EnableSceneManagement = false;
        network.NetworkConfig.PlayerPrefab = null;
        Debug.Log("[CAD room] Using network transport " + transport.GetType().Name);
    }

    private async Task<bool> PrepareAsync()
    {
        State = RoomState.Connecting;
        Status = "Preparing CAD room";
        for (int i = 0; i < 100 &&
            (service.ModelRoot == null || string.IsNullOrEmpty(CADPackageIdentity.Current)); i++)
            await Task.Delay(100);
        if (service.ModelRoot == null || string.IsNullOrEmpty(CADPackageIdentity.Current))
        {
            SetError("The bundled CAD model has not loaded.");
            return false;
        }
        EnsureNetworkManager();
        await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        EnsureNetworkManager();
        return true;
    }

    public async void HostRoom() => await HostRoomAsync();

    public async Task HostRoomAsync()
    {
        if (roomActive) return;
        int current = ++operationGeneration;
        try
        {
            if (!await PrepareAsync() || current != operationGeneration) return;
            Status = "Creating room";
            var options = new SessionOptions { Type = SessionType, MaxPlayers = 2,
                Name = "CAD Vision room", IsPrivate = true }.WithRelayNetwork();
            session = await MultiplayerService.Instance.CreateSessionAsync(options);
            if (current != operationGeneration) return;
            BeginConnectedRoom();
            State = RoomState.Localizing;
            Status = "Creating shared location";
            Vector3 center = service.TryGetModelBounds(out Bounds bounds)
                ? bounds.center : service.ModelRoot.position;
            if (!await alignment.CreateAndShareAsync(new Pose(center, Quaternion.identity)))
            { SetError(alignment.Status); return; }
            if (current != operationGeneration) return;
            CaptureAcceptedState();
            everReady = true;
            State = RoomState.Ready;
            Status = "Room ready — give the code to the other headset";
            foreach (ulong peer in waitingForAnchor.ToArray()) SendAnchor(peer);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            SetError("Could not host room: " + e.Message);
        }
    }

    public async void JoinRoom(string code) => await JoinRoomAsync(code);

    public async Task JoinRoomAsync(string code)
    {
        if (roomActive) return;
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length < 4 || code.Length > 12)
        { SetError("Enter the room code shown on the host headset."); return; }
        int current = ++operationGeneration;
        try
        {
            if (!await PrepareAsync() || current != operationGeneration) return;
            Status = "Joining room";
            session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code,
                new JoinSessionOptions { Type = SessionType });
            if (current != operationGeneration) return;
            BeginConnectedRoom();
            State = RoomState.Localizing;
            Status = "Waiting for shared location";
            Send(new CADRoomWire { kind = "hello" }, NetworkManager.ServerClientId);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            SetError("Could not join room: " + e.Message);
        }
    }

    private void BeginConnectedRoom()
    {
        roomActive = true;
        if (environment != null) environment.SetPassthroughEnabled(true);
        GetComponent<CADVirtualLocomotion>()?.SetRoomActive(true);
        network.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnMessage);
        network.OnClientDisconnectCallback += OnClientDisconnected;
        network.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        // The joining headset sends hello after it has registered its message handler.
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (IsHost)
        {
            leases.ReleaseOwner(clientId);
            readyClients.Remove(clientId);
            waitingForAnchor.Remove(clientId);
            Status = "Other headset disconnected";
        }
        else if (roomActive && clientId == NetworkManager.ServerClientId)
            SetError("Host left the room");
    }

    private void SendAnchor(ulong clientId)
    {
        if (!alignment.IsReady) return;
        Send(new CADRoomWire { kind = "anchor", groupId = alignment.GroupId.ToString(),
            anchorId = alignment.AnchorId.ToString() }, clientId);
    }

    private async void LocalizeGuestAsync(CADRoomWire message)
    {
        if (message.protocol != CADPackageIdentity.Protocol ||
            message.packageId != CADPackageIdentity.Current)
        { SetError("The headsets have different CAD files or app versions."); return; }
        if (!Guid.TryParse(message.groupId, out Guid group) ||
            !Guid.TryParse(message.anchorId, out Guid anchorId))
        { SetError("Invalid shared location from host"); return; }
        try
        {
            if (!await alignment.LoadSharedAsync(group, anchorId))
            { SetError(alignment.Status); return; }
            State = RoomState.Syncing;
            Status = "Loading shared CAD state";
            Send(new CADRoomWire { kind = "ready" }, NetworkManager.ServerClientId);
        }
        catch (Exception e) { SetError("Could not align the room: " + e.Message); }
    }

    private void OnMessage(ulong sender, FastBufferReader reader)
    {
        CADRoomWire message;
        try
        {
            reader.ReadValueSafe(out int size);
            if (size <= 0 || size > MaxPacketBytes || size > reader.Length - reader.Position)
                return;
            byte[] bytes = new byte[size];
            reader.ReadBytesSafe(ref bytes, size);
            message = JsonUtility.FromJson<CADRoomWire>(Encoding.UTF8.GetString(bytes));
            if (message == null || message.protocol != CADPackageIdentity.Protocol) return;
        }
        catch (Exception e) { Debug.LogWarning("[CAD room] Invalid message: " + e.Message); return; }

        if (IsHost) HandleHostMessage(sender, message);
        else if (sender == NetworkManager.ServerClientId) HandleGuestMessage(message);
    }

    private void HandleHostMessage(ulong sender, CADRoomWire message)
    {
        if (sender == network.LocalClientId) return;
        switch (message.kind)
        {
            case "hello":
                if (message.packageId != CADPackageIdentity.Current)
                { SendError(sender, "The headsets have different CAD files or app versions."); return; }
                waitingForAnchor.Add(sender);
                SendAnchor(sender);
                break;
            case "ready":
                if (!waitingForAnchor.Contains(sender) || !alignment.IsReady) return;
                readyClients.Add(sender);
                SendSnapshot(sender);
                break;
            case "lease-request":
                if (!readyClients.Contains(sender) || message.ids == null) return;
                if (leases.TryAcquire(sender, message.ids, Time.unscaledTimeAsDouble,
                    out ulong granted, out string denial))
                    Send(new CADRoomWire { kind = "lease-grant", ids = message.ids,
                        token = granted.ToString() }, sender);
                else
                    Send(new CADRoomWire { kind = "lease-deny", ids = message.ids,
                        reason = denial }, sender);
                break;
            case "pose":
                if (!readyClients.Contains(sender) ||
                    !ulong.TryParse(message.token, out ulong token) ||
                    !leases.Renew(sender, token, message.id, Time.unscaledTimeAsDouble) ||
                    !ValidPose(message)) return;
                AcceptPose(message);
                break;
            case "release":
                if (ulong.TryParse(message.token, out ulong released)) leases.Release(sender, released);
                break;
        }
    }

    private void HandleGuestMessage(CADRoomWire message)
    {
        switch (message.kind)
        {
            case "anchor": LocalizeGuestAsync(message); break;
            case "snapshot": ApplySnapshot(message); break;
            case "state":
                if (State != RoomState.Ready) bufferedUpdates.Add(message);
                else ApplyState(message);
                break;
            case "lease-grant":
                if (leasePending && SameTargets(localTargets, message.ids) &&
                    ulong.TryParse(message.token, out ulong granted))
                { localToken = granted; leasePending = false; Status = "You can move the selection"; }
                break;
            case "lease-deny":
                if (SameTargets(localTargets, message.ids))
                { leasePending = false; Status = message.reason ?? "Selection is busy"; }
                break;
            case "error": SetError(message.reason ?? "Room error"); break;
        }
    }

    private void SendError(ulong peer, string reason) =>
        Send(new CADRoomWire { kind = "error", reason = reason }, peer);

    private void Send(CADRoomWire message, ulong peer)
    {
        if (network == null || !network.IsListening || network.CustomMessagingManager == null) return;
        message.protocol = CADPackageIdentity.Protocol;
        message.packageId = CADPackageIdentity.Current;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
        if (bytes.Length > MaxPacketBytes) { SetError("CAD room message is too large"); return; }
        using var writer = new FastBufferWriter(bytes.Length + 8, Allocator.Temp);
        writer.WriteValueSafe(bytes.Length);
        writer.WriteBytesSafe(bytes);
        network.CustomMessagingManager.SendNamedMessage(MessageName, peer, writer,
            NetworkDelivery.ReliableFragmentedSequenced);
    }

    private void Broadcast(CADRoomWire message)
    {
        foreach (ulong peer in readyClients)
            Send(message, peer);
    }

    private void SendSnapshot(ulong peer)
    {
        if (!alignment.IsReady) return;
        var model = CaptureModel();
        Send(new CADRoomWire { kind = "snapshot", revision = revision,
            position = model.position, rotation = model.rotation, scale = model.scale,
            parts = accepted.Values.OrderBy(p => Depth(p.id)).ToArray() }, peer);
    }

    private void ApplySnapshot(CADRoomWire message)
    {
        if (State != RoomState.Syncing || !alignment.IsReady ||
            message.packageId != CADPackageIdentity.Current ||
            !ValidPose(message) || message.parts == null)
        { SetError("Shared CAD snapshot is invalid"); return; }
        applyingNetworkState = true;
        accepted.Clear();
        ApplyModel(message);
        foreach (CADRoomPart part in message.parts.OrderBy(p => Depth(p.id)))
        {
            if (part == null || !byId.ContainsKey(part.id) || !ValidPart(part))
            { applyingNetworkState = false; SetError("Shared CAD hierarchy differs"); return; }
            service.ApplyAcceptedPartState(part.id, part.detached,
                part.position, part.rotation, part.scale);
            accepted[part.id] = Copy(part);
        }
        acceptedModel = CaptureModel();
        receivedRevision = message.revision;
        applyingNetworkState = false;
        everReady = true;
        State = RoomState.Ready;
        Status = "Room ready";
        foreach (CADRoomWire update in bufferedUpdates.OrderBy(m => m.revision))
            ApplyState(update);
        bufferedUpdates.Clear();
    }

    private void ApplyState(CADRoomWire message)
    {
        if (message.revision <= receivedRevision || !ValidPose(message)) return;
        if (message.id != CADMultiplayerLeaseTable.ModelId && !byId.ContainsKey(message.id)) return;
        applyingNetworkState = true;
        if (message.id == CADMultiplayerLeaseTable.ModelId)
        {
            ApplyModel(message);
            acceptedModel = CaptureModel();
        }
        else
        {
            bool detached = accepted.TryGetValue(message.id, out CADRoomPart old) && old.detached;
            service.ApplyAcceptedPartState(message.id, detached,
                message.position, message.rotation, message.scale);
            accepted[message.id] = CapturePart(message.id);
        }
        receivedRevision = message.revision;
        applyingNetworkState = false;
    }

    private void AcceptPose(CADRoomWire message)
    {
        applyingNetworkState = true;
        if (message.id == CADMultiplayerLeaseTable.ModelId)
        {
            ApplyModel(message);
            acceptedModel = CaptureModel();
            message.position = acceptedModel.position;
            message.rotation = acceptedModel.rotation;
            message.scale = acceptedModel.scale;
        }
        else if (byId.ContainsKey(message.id))
        {
            bool detached = accepted.TryGetValue(message.id, out CADRoomPart old) && old.detached;
            service.ApplyAcceptedPartState(message.id, detached,
                message.position, message.rotation, message.scale);
            accepted[message.id] = CapturePart(message.id);
            message.position = accepted[message.id].position;
            message.rotation = accepted[message.id].rotation;
            message.scale = accepted[message.id].scale;
        }
        applyingNetworkState = false;
        message.kind = "state";
        message.revision = ++revision;
        Broadcast(message);
    }

    private void ApplyModel(CADRoomWire message)
    {
        Pose frame = alignment.WorldPose;
        service.ApplyAcceptedModelState(frame.position + frame.rotation * message.position,
            frame.rotation * message.rotation, message.scale);
    }

    private void CaptureAcceptedState()
    {
        accepted.Clear();
        acceptedModel = CaptureModel();
        foreach (string id in byId.Keys) accepted[id] = CapturePart(id);
    }

    private CADRoomPart CaptureModel()
    {
        Transform root = service.ModelRoot;
        Pose frame = alignment.WorldPose;
        return new CADRoomPart { id = CADMultiplayerLeaseTable.ModelId,
            position = Quaternion.Inverse(frame.rotation) * (root.position - frame.position),
            rotation = Quaternion.Inverse(frame.rotation) * root.rotation,
            scale = root.localScale };
    }

    private CADRoomPart CapturePart(string id)
    {
        Transform t = byId[id].transform;
        return new CADRoomPart { id = id, detached = service.IsDetached(id),
            position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
    }

    private static CADRoomPart Copy(CADRoomPart p) => new CADRoomPart { id = p.id,
        detached = p.detached, position = p.position, rotation = p.rotation, scale = p.scale };

    private int Depth(string id)
    {
        int depth = 0;
        var seen = new HashSet<string>();
        for (string parent = service.GetLogicalParentId(id);
            parent != null && seen.Add(parent) && depth < 100;
            parent = service.GetLogicalParentId(parent)) depth++;
        return depth;
    }

    private static bool Different(CADRoomPart a, CADRoomPart b) => a == null || b == null ||
        a.detached != b.detached || a.position != b.position ||
        a.rotation != b.rotation || a.scale != b.scale;

    private static bool ValidPart(CADRoomPart p) => p != null &&
        Finite(p.position) && Finite(p.scale) && Finite(p.rotation) &&
        p.scale.x > 0.0001f && p.scale.y > 0.0001f && p.scale.z > 0.0001f &&
        p.scale.x < 1000f && p.scale.y < 1000f && p.scale.z < 1000f;

    private static bool ValidPose(CADRoomWire p) => p != null &&
        Finite(p.position) && Finite(p.rotation) && Finite(p.scale) &&
        p.scale.x > 0.0001f && p.scale.y > 0.0001f && p.scale.z > 0.0001f &&
        p.scale.x < 1000f && p.scale.y < 1000f && p.scale.z < 1000f &&
        p.position.sqrMagnitude < 1000000f;

    private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) &&
        float.IsFinite(q.z) && float.IsFinite(q.w) &&
        Mathf.Abs(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w - 1f) < 0.02f;

    private void LateUpdate()
    {
        if (!roomActive || State != RoomState.Ready || applyingNetworkState || !alignment.IsReady)
        {
            if (roomActive && State == RoomState.Ready && !alignment.IsReady)
            {
                ReleaseAllLocalLeases();
                State = RoomState.Localizing;
                Status = "Shared location lost — hold still to relocalize";
            }
            else if (roomActive && everReady && State == RoomState.Localizing && alignment.IsReady)
            {
                State = IsHost ? RoomState.Ready : RoomState.Syncing;
                Status = IsHost ? "Room ready" : "Refreshing shared CAD state";
                if (!IsHost) Send(new CADRoomWire { kind = "ready" }, NetworkManager.ServerClientId);
            }
            return;
        }
        leases?.Expire(Time.unscaledTimeAsDouble);
        bool sendNow = Time.unscaledTime >= nextPoseTime;
        bool heartbeat = Time.unscaledTime >= nextHeartbeatTime;
        if (!sendNow && !heartbeat) return;
        nextPoseTime = Time.unscaledTime + PoseInterval;
        if (heartbeat) nextHeartbeatTime = Time.unscaledTime + HeartbeatInterval;

        CADRoomPart model = CaptureModel();
        if (Different(model, acceptedModel)) ProcessLocalPose(model, heartbeat);
        else if (heartbeat && OwnsTarget(CADMultiplayerLeaseTable.ModelId)) ProcessLocalPose(model, true);
        foreach (string id in byId.Keys.ToArray())
        {
            if (!byId.TryGetValue(id, out CADObject obj) || obj == null) continue;
            CADRoomPart current = CapturePart(id);
            accepted.TryGetValue(id, out CADRoomPart previous);
            if (Different(current, previous)) ProcessLocalPose(current, heartbeat);
            else if (heartbeat && OwnsTarget(id)) ProcessLocalPose(current, true);
        }
    }

    private void ProcessLocalPose(CADRoomPart part, bool heartbeat)
    {
        if (!OwnsTarget(part.id))
        {
            if (part.id == CADMultiplayerLeaseTable.ModelId && acceptedModel != null)
                ApplyAcceptedModel(acceptedModel);
            else if (accepted.TryGetValue(part.id, out CADRoomPart old))
                service.ApplyAcceptedPartState(part.id, old.detached,
                    old.position, old.rotation, old.scale);
            Status = "Grab an unlocked part to edit it in the room";
            return;
        }
        if (part.id != CADMultiplayerLeaseTable.ModelId &&
            accepted.TryGetValue(part.id, out CADRoomPart canonical) &&
            part.detached != canonical.detached)
        {
            service.ApplyAcceptedPartState(part.id, canonical.detached,
                canonical.position, canonical.rotation, canonical.scale);
            Status = "Detach and reset are unavailable in a shared room";
            return;
        }
        if (IsHost)
        {
            leases.Renew(network.LocalClientId, localToken, part.id, Time.unscaledTimeAsDouble);
            if (!heartbeat && !Different(part, part.id == CADMultiplayerLeaseTable.ModelId
                ? acceptedModel : accepted.GetValueOrDefault(part.id))) return;
            var state = ToPoseMessage(part);
            state.kind = "state";
            state.revision = ++revision;
            if (part.id == CADMultiplayerLeaseTable.ModelId) acceptedModel = Copy(part);
            else accepted[part.id] = Copy(part);
            Broadcast(state);
        }
        else if (heartbeat || !lastSent.TryGetValue(part.id, out CADRoomPart sent) || Different(part, sent))
        {
            var pose = ToPoseMessage(part);
            pose.kind = "pose";
            pose.token = localToken.ToString();
            Send(pose, NetworkManager.ServerClientId);
            lastSent[part.id] = Copy(part);
        }
    }

    private void ApplyAcceptedModel(CADRoomPart model)
    {
        Pose frame = alignment.WorldPose;
        service.ApplyAcceptedModelState(frame.position + frame.rotation * model.position,
            frame.rotation * model.rotation, model.scale);
    }

    private static CADRoomWire ToPoseMessage(CADRoomPart p) => new CADRoomWire
    { id = p.id, position = p.position, rotation = p.rotation, scale = p.scale };

    private bool OwnsTarget(string id) => localToken != 0 && localTargets != null &&
        localTargets.Contains(id);

    private static string[] Normalize(IEnumerable<string> ids) => ids?.Where(id => !string.IsNullOrEmpty(id))
        .Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
    private static bool SameTargets(string[] a, IEnumerable<string> b) =>
        a != null && a.SequenceEqual(Normalize(b));

    public bool RequestLease(IEnumerable<string> ids)
    {
        if (!roomActive) return true;
        if (State != RoomState.Ready || !alignment.IsReady) return false;
        string[] requested = Normalize(ids);
        if (requested.Length == 0) return false;
        if (SameTargets(localTargets, requested))
        { localLeaseReferences++; return localToken != 0; }
        if (localLeaseReferences > 0) return false;
        localTargets = requested;
        localLeaseReferences = 1;
        localToken = 0;
        if (IsHost)
        {
            if (leases.TryAcquire(network.LocalClientId, requested, Time.unscaledTimeAsDouble,
                out localToken, out string reason)) return true;
            Status = reason;
            return false;
        }
        leasePending = true;
        Status = "Waiting for edit access";
        Send(new CADRoomWire { kind = "lease-request", ids = requested }, NetworkManager.ServerClientId);
        return false;
    }

    public bool HasLease(IEnumerable<string> ids) => !roomActive ||
        State == RoomState.Ready && localToken != 0 && SameTargets(localTargets, ids);

    public void ReleaseLease(IEnumerable<string> ids)
    {
        if (!roomActive || !SameTargets(localTargets, ids)) return;
        if (--localLeaseReferences > 0) return;
        if (localToken != 0)
        {
            foreach (string id in alignment.IsReady ? localTargets : Array.Empty<string>())
            {
                CADRoomPart p = id == CADMultiplayerLeaseTable.ModelId ? CaptureModel()
                    : byId.ContainsKey(id) ? CapturePart(id) : null;
                if (p != null) ProcessLocalPose(p, false);
            }
            if (IsHost) leases.Release(network.LocalClientId, localToken);
            else Send(new CADRoomWire { kind = "release", token = localToken.ToString() },
                NetworkManager.ServerClientId);
        }
        localTargets = null;
        localToken = 0;
        localLeaseReferences = 0;
        leasePending = false;
        lastSent.Clear();
    }

    private void ReleaseAllLocalLeases()
    {
        if (localTargets != null) { localLeaseReferences = 1; ReleaseLease(localTargets); }
    }

    public async void LeaveRoom() => await LeaveRoomAsync();

    public async Task LeaveRoomAsync()
    {
        ++operationGeneration;
        ReleaseAllLocalLeases();
        roomActive = false;
        everReady = false;
        if (network != null)
        {
            network.OnClientDisconnectCallback -= OnClientDisconnected;
            network.OnClientConnectedCallback -= OnClientConnected;
            network.CustomMessagingManager?.UnregisterNamedMessageHandler(MessageName);
        }
        ISession old = session;
        session = null;
        if (old != null)
        {
            try { await old.LeaveAsync(); }
            catch (Exception e) { Debug.LogWarning("[CAD room] Leave: " + e.Message); }
        }
        if (network != null && network.IsListening) network.Shutdown();
        leases?.Clear();
        readyClients.Clear();
        waitingForAnchor.Clear();
        bufferedUpdates.Clear();
        alignment.ResetAnchor();
        GetComponent<CADVirtualLocomotion>()?.SetRoomActive(false);
        State = RoomState.Offline;
        Status = "Offline";
    }

    private void SetError(string reason)
    {
        State = RoomState.Error;
        Status = reason;
        Debug.LogWarning("[CAD room] " + reason);
    }

    private async void OnDestroy()
    {
        if (service != null) service.ModelReplaced -= OnModelReplaced;
        if (roomActive) await LeaveRoomAsync();
    }
}
