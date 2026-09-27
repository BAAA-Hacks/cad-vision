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
    private const int PackageChunkBytes = 16 * 1024;
    private const int MaxModelBytes = 100 * 1024 * 1024;
    private const int MaxMetadataBytes = 10 * 1024 * 1024;
    // Join handshake: the guest repeats hello until the host answers (the first one can be lost
    // if it leaves before the connection is up), and gives up with a clear error.
    private const float HelloInterval = 1f;
    private const float HostReplyTimeout = 60f;
    private const float PackageStallTimeout = 20f;
    // Online calls (Unity services sign-in, relay session) fail with a clear error instead of hanging.
    private const float ServiceTimeout = 20f;
    private const float ModelLoadWait = 30f;

    public enum RoomState { Offline, Connecting, Localizing, Syncing, Ready, Error }
    public enum RoomMode { Passthrough, Virtual }
    public RoomState State { get; private set; } = RoomState.Offline;
    public string Status { get; private set; } = "Offline";
    public string RoomCode => session?.Code ?? string.Empty;
    public bool IsInRoom => roomActive;
    public bool IsHost => session != null && session.IsHost;
    public int ParticipantCount => session?.Players?.Count ?? 0;
    public RoomMode Mode { get; private set; } = RoomMode.Passthrough;
    /// <summary>A model loaded through the CAD loader can be shared (hosting needs one).</summary>
    public bool HasShareableModel => service != null && service.ModelRoot != null &&
        !string.IsNullOrEmpty(CADPackageIdentity.Current);

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
    private Pose virtualFrame;
    private bool virtualFrameReady;
    private byte[] incomingGlb;
    private byte[] incomingJson;
    private string incomingPackageId;
    private int incomingOffset;
    private bool importingPackage;
    private bool awaitingHost;        // Guest: hello repeats until the host answers.
    private float awaitingHostSince;
    private float nextHelloTime;
    private float lastChunkTime;
    private bool guestLocalizing;

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

    private async Task<bool> PrepareAsync(bool requireModel)
    {
        State = RoomState.Connecting;
        Status = "Preparing CAD room";
        if (requireModel && !HasShareableModel)
        {
            Status = "Waiting for the CAD model to load";
            for (int i = 0; i < ModelLoadWait * 10 && !HasShareableModel; i++)
                await Task.Delay(100);
            if (!HasShareableModel)
            {
                SetError("No CAD model is loaded yet. Load one (it must come through the CAD loader), then host.");
                return false;
            }
        }
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            SetError("This headset is offline. Connect it to Wi-Fi with internet access, then try again.");
            return false;
        }
        EnsureNetworkManager();
        Status = "Connecting to Unity services";
        await WithTimeout(UnityServices.InitializeAsync(), "Connecting to Unity services");
        if (!AuthenticationService.Instance.IsSignedIn)
            await WithTimeout(AuthenticationService.Instance.SignInAnonymouslyAsync(), "Signing in to Unity services");
        EnsureNetworkManager();
        return true;
    }

    // An online call that doesn't answer in ServiceTimeout seconds fails instead of hanging.
    private static async Task WithTimeout(Task task, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(ServiceTimeout))) != task)
            throw new TimeoutException(what + " timed out");
        await task;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string what)
    {
        await WithTimeout((Task)task, what);
        return await task;
    }

    /// <summary>
    /// A readable reason for a failed host/join: offline or DNS, Unity services not enabled for
    /// the project, a wrong code, a full room, or the raw message.
    /// </summary>
    public static string ExplainFailure(Exception e, bool joining)
    {
        string text = (e.GetType().Name + " " + e.Message + " " + e.InnerException?.Message).ToLowerInvariant();
        if (e is TimeoutException || text.Contains("timed out") || text.Contains("timeout"))
            return e.Message + ". Check the headset's internet connection and try again.";
        if (text.Contains("resolve") || text.Contains("dns") || text.Contains("name or service") ||
            text.Contains("no such host") || text.Contains("network is unreachable") ||
            text.Contains("unable to connect") || text.Contains("httprequestexception") || text.Contains("transport"))
            return "This headset can't reach the internet (network/DNS failure). Check its Wi-Fi; a laptop hotspot may need to be turned off and on.";
        if (text.Contains("forbidden") || text.Contains("403") || text.Contains("not enabled") ||
            text.Contains("unauthorized") || text.Contains("401"))
            return "Unity Relay/Sessions refused the request. Make sure Relay and Sessions (Lobby) are enabled for this app's Unity Cloud project.";
        if (joining && (text.Contains("not found") || text.Contains("404") || text.Contains("invalid join code") ||
            text.Contains("code")))
            return "No room with that code. Check the code shown on the host headset.";
        if (joining && (text.Contains("full") || text.Contains("max players")))
            return "That room is full (2 people max).";
        return (joining ? "Could not join room: " : "Could not host room: ") + e.Message;
    }

    public async void HostRoom() => await HostRoomAsync(RoomMode.Passthrough);
    public async void HostVirtualRoom() => await HostRoomAsync(RoomMode.Virtual);

    public Task HostRoomAsync() => HostRoomAsync(RoomMode.Passthrough);

    public async Task HostRoomAsync(RoomMode mode)
    {
        if (roomActive) return;
        int current = ++operationGeneration;
        try
        {
            if (!await PrepareAsync(true) || current != operationGeneration) return;
            Status = "Creating room";
            var options = new SessionOptions { Type = SessionType, MaxPlayers = 2,
                Name = "CAD Vision room", IsPrivate = true }.WithRelayNetwork();
            session = await WithTimeout(MultiplayerService.Instance.CreateSessionAsync(options), "Creating the room");
            if (current != operationGeneration) return;
            BeginConnectedRoom(mode);
            State = RoomState.Localizing;
            if (mode == RoomMode.Passthrough)
            {
                Status = "Creating shared location";
                Vector3 center = service.TryGetModelBounds(out Bounds bounds)
                    ? bounds.center : service.ModelRoot.position;
                if (!await alignment.CreateAndShareAsync(new Pose(center, Quaternion.identity)))
                { SetError(alignment.Status); return; }
            }
            if (current != operationGeneration) return;
            CaptureAcceptedState();
            everReady = true;
            State = RoomState.Ready;
            Status = "Room ready — give the code to the other headset";
            foreach (ulong peer in waitingForAnchor.ToArray()) SendFrame(peer);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            SetError(ExplainFailure(e, joining: false));
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
            if (!await PrepareAsync(false) || current != operationGeneration) return;
            Status = "Joining room";
            session = await WithTimeout(MultiplayerService.Instance.JoinSessionByCodeAsync(code,
                new JoinSessionOptions { Type = SessionType }), "Joining the room");
            if (current != operationGeneration) return;
            BeginConnectedRoom(RoomMode.Passthrough);
            State = RoomState.Localizing;
            Status = "Connecting to the host";
            StartAwaitingHost(); // Hello goes out once connected, and repeats until the host answers.
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            SetError(ExplainFailure(e, joining: true));
        }
    }

    private void StartAwaitingHost()
    {
        awaitingHost = true;
        awaitingHostSince = Time.unscaledTime;
        nextHelloTime = 0f;
    }

    // Guest: send hello once connected, again every HelloInterval until the host answers
    // (wait / package-offer / frame); give up after HostReplyTimeout. Also ends a model transfer
    // that stopped arriving.
    private void UpdateGuestHandshake()
    {
        if (!roomActive || IsHost || State == RoomState.Error)
            return;
        float now = Time.unscaledTime;

        if (incomingGlb != null && !importingPackage && now - lastChunkTime > PackageStallTimeout)
        {
            SetError("Stopped receiving the host's CAD model. Leave the room and join again.");
            return;
        }

        if (!awaitingHost || importingPackage)
            return;
        if (now - awaitingHostSince > HostReplyTimeout)
        {
            awaitingHost = false;
            SetError("The host didn't answer. Check the code, that both headsets are online, and that they run the same app version.");
            return;
        }
        if (network == null || !network.IsConnectedClient || now < nextHelloTime)
            return;
        nextHelloTime = now + HelloInterval;
        Send(new CADRoomWire { kind = "hello" }, NetworkManager.ServerClientId);
    }

    private void BeginConnectedRoom(RoomMode mode)
    {
        SetMode(mode);
        roomActive = true;
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

    private void SetMode(RoomMode mode)
    {
        Mode = mode;
        if (environment != null) environment.SetPassthroughEnabled(mode == RoomMode.Passthrough);
        GetComponent<CADVirtualLocomotion>()?.SetRoomActive(mode == RoomMode.Passthrough);
        virtualFrameReady = false;
        if (mode == RoomMode.Virtual)
        {
            Transform head = Camera.main != null ? Camera.main.transform : transform;
            float yaw = head.eulerAngles.y;
            float floorY = environment != null && environment.VirtualFloor != null
                ? environment.VirtualFloor.position.y : 0f;
            virtualFrame = new Pose(new Vector3(head.position.x, floorY, head.position.z),
                Quaternion.Euler(0f, yaw, 0f));
            virtualFrameReady = true;
        }
    }

    private bool FrameReady => Mode == RoomMode.Virtual ? virtualFrameReady : alignment.IsReady;
    private Pose FramePose => Mode == RoomMode.Virtual ? virtualFrame : alignment.WorldPose;

    private void SendFrame(ulong clientId)
    {
        if (!FrameReady) return;
        Send(new CADRoomWire { kind = "frame", mode = Mode == RoomMode.Virtual ? "virtual" : "passthrough",
            groupId = Mode == RoomMode.Passthrough ? alignment.GroupId.ToString() : null,
            anchorId = Mode == RoomMode.Passthrough ? alignment.AnchorId.ToString() : null }, clientId);
    }

    private async void LocalizeGuestAsync(CADRoomWire message)
    {
        // Repeated hellos can bring repeated frames: localize once.
        if (guestLocalizing || State != RoomState.Localizing) return;
        if (message.protocol != CADPackageIdentity.Protocol ||
            message.packageId != CADPackageIdentity.Current)
        { SetError("The headsets have different CAD files or app versions."); return; }
        if (message.mode == "virtual")
        {
            SetMode(RoomMode.Virtual);
            State = RoomState.Syncing;
            Status = "Loading shared CAD state";
            Send(new CADRoomWire { kind = "ready" }, NetworkManager.ServerClientId);
            return;
        }
        if (message.mode != "passthrough" ||
            !Guid.TryParse(message.groupId, out Guid group) ||
            !Guid.TryParse(message.anchorId, out Guid anchorId))
        { SetError("Invalid shared location from host"); return; }
        SetMode(RoomMode.Passthrough);
        guestLocalizing = true;
        try
        {
            Status = "Finding the host's shared location";
            if (!await alignment.LoadSharedAsync(group, anchorId))
            { SetError(alignment.Status); return; }
            State = RoomState.Syncing;
            Status = "Loading shared CAD state";
            Send(new CADRoomWire { kind = "ready" }, NetworkManager.ServerClientId);
        }
        catch (Exception e) { SetError("Could not align the room: " + e.Message); }
        finally { guestLocalizing = false; }
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
                { OfferHostPackage(sender); return; }
                waitingForAnchor.Add(sender);
                if (FrameReady) SendFrame(sender);
                else Send(new CADRoomWire { kind = "wait", reason = Status }, sender); // Still setting up.
                break;
            case "package-next":
                SendPackageChunk(sender, message);
                break;
            case "ready":
                if (!waitingForAnchor.Contains(sender) || !FrameReady ||
                    message.packageId != CADPackageIdentity.Current) return;
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
            case "wait":
                if (awaitingHost) { awaitingHost = false; Status = "Host is setting up the shared location"; }
                break;
            case "package-offer": awaitingHost = false; ReceivePackageOffer(message); break;
            case "package-chunk": ReceivePackageChunk(message); break;
            case "frame": awaitingHost = false; LocalizeGuestAsync(message); break;
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

    private void OfferHostPackage(ulong peer)
    {
        byte[] glb = CADPackageIdentity.ModelBytes;
        byte[] json = CADPackageIdentity.MetadataBytes;
        if (glb == null || json == null || glb.Length > MaxModelBytes ||
            json.Length > MaxMetadataBytes)
        { SendError(peer, "Host CAD is unavailable or too large to share."); return; }
        Send(new CADRoomWire { kind = "package-offer", glbLength = glb.Length,
            jsonLength = json.Length }, peer);
    }

    private void SendPackageChunk(ulong peer, CADRoomWire request)
    {
        if (readyClients.Contains(peer) || request.packageId != CADPackageIdentity.Current)
            return;
        byte[] source = request.file == "glb" ? CADPackageIdentity.ModelBytes :
            request.file == "json" ? CADPackageIdentity.MetadataBytes : null;
        if (source == null || request.offset < 0 || request.offset >= source.Length) return;
        int count = Math.Min(PackageChunkBytes, source.Length - request.offset);
        Send(new CADRoomWire { kind = "package-chunk", file = request.file,
            offset = request.offset, data = Convert.ToBase64String(source, request.offset, count) }, peer);
    }

    private void ReceivePackageOffer(CADRoomWire message)
    {
        if (incomingGlb != null && message.packageId == incomingPackageId) return; // Already receiving it.
        if (State != RoomState.Localizing || importingPackage ||
            message.glbLength <= 0 || message.glbLength > MaxModelBytes ||
            message.jsonLength <= 0 || message.jsonLength > MaxMetadataBytes ||
            string.IsNullOrEmpty(message.packageId))
        { SetError("Host CAD package is invalid or too large."); return; }
        incomingGlb = new byte[message.glbLength];
        incomingJson = new byte[message.jsonLength];
        incomingPackageId = message.packageId;
        incomingOffset = 0;
        lastChunkTime = Time.unscaledTime;
        Status = "Receiving host CAD";
        RequestPackageChunk("glb");
    }

    private void RequestPackageChunk(string file)
    {
        Send(new CADRoomWire { kind = "package-next", file = file,
            offset = incomingOffset, packageId = incomingPackageId }, NetworkManager.ServerClientId);
    }

    private void ReceivePackageChunk(CADRoomWire message)
    {
        if (incomingGlb == null || importingPackage ||
            message.packageId != incomingPackageId || message.offset != incomingOffset ||
            string.IsNullOrEmpty(message.data)) return;
        byte[] target = message.file == "glb" ? incomingGlb :
            message.file == "json" ? incomingJson : null;
        if (target == null) return;
        byte[] chunk;
        try { chunk = Convert.FromBase64String(message.data); }
        catch (FormatException) { SetError("Host CAD transfer was corrupted."); return; }
        if (chunk.Length == 0 || chunk.Length > PackageChunkBytes ||
            incomingOffset + chunk.Length > target.Length)
        { SetError("Host CAD transfer was corrupted."); return; }
        Buffer.BlockCopy(chunk, 0, target, incomingOffset, chunk.Length);
        incomingOffset += chunk.Length;
        lastChunkTime = Time.unscaledTime;
        int received = (message.file == "glb" ? 0 : incomingGlb.Length) + incomingOffset;
        Status = $"Receiving host CAD ({100 * received / Math.Max(1, incomingGlb.Length + incomingJson.Length)}%)";
        if (incomingOffset < target.Length)
        { RequestPackageChunk(message.file); return; }
        incomingOffset = 0;
        if (message.file == "glb") RequestPackageChunk("json");
        else ImportHostPackageAsync();
    }

    private async void ImportHostPackageAsync()
    {
        importingPackage = true;
        int generation = operationGeneration;
        try
        {
            if (CADPackageIdentity.Compute(incomingGlb, incomingJson) != incomingPackageId)
                throw new InvalidOperationException("Host CAD verification failed.");
            Status = "Loading host CAD";
            CadModelLoader loader = FindAnyObjectByType<CadModelLoader>();
            if (loader == null) throw new InvalidOperationException("CAD loader is unavailable.");
            await loader.LoadPackageAsync(incomingGlb, Encoding.UTF8.GetString(incomingJson).TrimStart('\uFEFF'));
            if (generation != operationGeneration) return;
            if (CADPackageIdentity.Current != incomingPackageId)
                throw new InvalidOperationException("Host CAD verification failed after loading.");
            incomingGlb = null;
            incomingJson = null;
            incomingPackageId = null;
            Status = "Waiting for shared location";
            StartAwaitingHost();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (generation == operationGeneration) SetError("Could not load host CAD: " + e.Message);
        }
        finally { importingPackage = false; }
    }

    private void Send(CADRoomWire message, ulong peer)
    {
        if (network == null || !network.IsListening || network.CustomMessagingManager == null) return;
        message.protocol = CADPackageIdentity.Protocol;
        if (message.kind != "package-next") message.packageId = CADPackageIdentity.Current;
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
        if (!FrameReady) return;
        var model = CaptureModel();
        Send(new CADRoomWire { kind = "snapshot", revision = revision,
            position = model.position, rotation = model.rotation, scale = model.scale,
            parts = accepted.Values.OrderBy(p => Depth(p.id)).ToArray() }, peer);
    }

    private void ApplySnapshot(CADRoomWire message)
    {
        if (State != RoomState.Syncing || !FrameReady ||
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
        Pose frame = FramePose;
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
        Pose frame = FramePose;
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
        UpdateGuestHandshake();
        if (!roomActive || State != RoomState.Ready || applyingNetworkState || !FrameReady)
        {
            if (roomActive && State == RoomState.Ready && !FrameReady)
            {
                ReleaseAllLocalLeases();
                State = RoomState.Localizing;
                Status = "Shared location lost — hold still to relocalize";
            }
            else if (roomActive && everReady && State == RoomState.Localizing && FrameReady)
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
        Pose frame = FramePose;
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
        if (State != RoomState.Ready || !FrameReady) return false;
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
            foreach (string id in FrameReady ? localTargets : Array.Empty<string>())
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
        incomingGlb = null;
        incomingJson = null;
        incomingPackageId = null;
        incomingOffset = 0;
        awaitingHost = false;
        guestLocalizing = false;
        virtualFrameReady = false;
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
        if (IsHost && network != null && network.IsListening)
            foreach (ulong peer in waitingForAnchor.Union(readyClients).ToArray())
                SendError(peer, "Host: " + reason);
    }

    private async void OnDestroy()
    {
        if (service != null) service.ModelReplaced -= OnModelReplaced;
        if (roomActive) await LeaveRoomAsync();
    }
}
