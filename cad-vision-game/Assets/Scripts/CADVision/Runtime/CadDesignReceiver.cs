using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CADVision
{
    [RequireComponent(typeof(CadModelLoader))]
    public sealed class CadDesignReceiver : MonoBehaviour
    {
        [Range(1024, 65535)] public int Port = 8085;
        public bool ListenOnEnable = true;
        public bool AllowLan = true;
        public bool IsListening => server != null;
        public string LastStatus { get; private set; }
        private CadDesignServer server;
        private SynchronizationContext unityContext;

        private void OnEnable()
        {
            if (Application.isPlaying && ListenOnEnable) StartReceiver();
        }

        public void StartReceiver()
        {
            if (server != null) return;
            unityContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Start receiver on Unity's main thread.");
            try
            {
                server = new CadDesignServer(AllowLan ? IPAddress.Any : IPAddress.Loopback, Port, ImportOnMainThread);
                LastStatus = $"Listening on port {server.Port}.";
                Debug.Log("CAD Vision receiver: " + LastStatus);
            }
            catch (Exception e) { LastStatus = e.Message; Debug.LogError("CAD Vision receiver: " + e.Message); }
        }

        private async Task<CadReceiveResult> ImportOnMainThread(byte[] glb, string json, CancellationToken token)
        {
            var result = new TaskCompletionSource<CadReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => result.TrySetCanceled());
            unityContext.Post(async _ =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (this == null || !isActiveAndEnabled) throw new OperationCanceledException();
                    var loader = GetComponent<CadModelLoader>();
                    if (loader.IsLoading) throw new CadDesignServer.HttpFailure(409, "A local design import is already running.");
                    LastStatus = "Importing received design...";
                    await loader.LoadPackageAsync(glb, json, token);
                    var runtime = GetComponent<CADVisionRuntime>();
                    LastStatus = runtime.Metadata.HasVerifiedNodeMapping
                        ? $"Loaded {runtime.GetAllObjects().Count} CAD objects."
                        : "Loaded geometry and metadata; CAD-to-GLB mapping is unavailable.";
                    result.TrySetResult(new CadReceiveResult { success = true, message = LastStatus,
                        objectCount = runtime.GetAllObjects().Count, revision = runtime.Revision });
                }
                catch (Exception e)
                {
                    if (this != null) LastStatus = e.Message;
                    if (!(e is OperationCanceledException)) Debug.LogException(e);
                    result.TrySetException(e);
                }
            }, null);
            return await result.Task.ConfigureAwait(false);
        }

        public void StopReceiver()
        {
            server?.Dispose();
            server = null;
            LastStatus = "Stopped.";
        }

        private void OnDisable() => StopReceiver();
        private void OnDestroy() => StopReceiver();
    }
}
