using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace CADVision
{
    [RequireComponent(typeof(CADVisionRuntime))]
    public sealed class CadModelLoader : MonoBehaviour
    {
        public Transform ReviewOrigin;
        [Min(0.1f)] public float ReviewDistance = 1.2f;
        [Tooltip("Optional presentation scaling. Leave disabled for physical size: 1 Unity unit = 1 metre.")]
        public bool FitForReview = false;
        [Min(0.01f)] public float ReviewSize = 1f;
        public bool CreateSelectionColliders = true;
        public bool IsLoading { get; private set; }
        public string LastError { get; private set; }
        private CancellationTokenSource pending;

        /// <summary>Local desktop path or files already received in persistentDataPath.</summary>
        public async Task LoadFilesAsync(string glbPath, string metadataPath)
        {
            if (!File.Exists(glbPath) || !File.Exists(metadataPath))
                throw new FileNotFoundException("A complete model.glb and metadata.json pair is required.");
            // Reading off-thread keeps disk I/O out of the Unity frame loop.
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(glbPath));
            string json = await Task.Run(() => File.ReadAllText(metadataPath));
            await LoadPackageAsync(bytes, json);
        }

        /// <summary>Transport-independent entry point; invoke on Unity's main thread with a complete pair.</summary>
        public async Task LoadPackageAsync(byte[] glb, string json, CancellationToken cancellationToken = default)
        {
            if (IsLoading) throw new InvalidOperationException("A CAD import is already running.");
            if (this == null) throw new OperationCanceledException();
            IsLoading = true;
            LastError = null;
            pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = pending.Token;
            var runtime = GetComponent<CADVisionRuntime>();
            int revision = runtime.Revision;
            GameObject root = null;
            GltfImport importer = null;
            try
            {
                var metadata = new CadMetadata(json);
                CadGlbPackage.Validate(glb, metadata);
                token.ThrowIfCancellationRequested();
                root = new GameObject("CADVisionModelRoot");
                root.SetActive(false);
                // Edit Mode has no frame loop for glTFast's persistent frame-budget agent.
                importer = new GltfImport(deferAgent: Application.isPlaying ? null : new UninterruptedDeferAgent());
                if (!await importer.LoadGltfBinary(glb, importSettings: new ImportSettings
                    { NodeNameMethod = NameImportMethod.Original }, cancellationToken: token))
                    throw new InvalidDataException("glTFast could not load the GLB. See the Unity Console for details.");
                var instantiator = new IndexedInstantiator(importer, root.transform);
                if (!await importer.InstantiateMainSceneAsync(instantiator, token))
                    throw new InvalidDataException("glTFast could not instantiate the GLB scene.");
                token.ThrowIfCancellationRequested();
                if (runtime == null || runtime.Revision != revision)
                    throw new OperationCanceledException("Runtime changed while this package was importing.");
                if (CreateSelectionColliders)
                    foreach (var mesh in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mesh.sharedMesh == null) continue;
                        // Broad-phase selection boxes; Task 3 can replace these with precise colliders.
                        var collider = mesh.gameObject.AddComponent<BoxCollider>();
                        collider.center = mesh.sharedMesh.bounds.center;
                        collider.size = mesh.sharedMesh.bounds.size;
                    }
                Place(root);
                root.SetActive(true);
                runtime.PublishImportedModel(metadata, root, instantiator.Nodes, importer);
                CADPackageIdentity.SetLoaded(glb, json);
                root = null;
                importer = null; // Runtime owns meshes/materials until replacement or clear.
            }
            catch (Exception e)
            {
                LastError = e.Message;
                throw;
            }
            finally
            {
                if (root != null)
                {
                    if (Application.isPlaying) Destroy(root);
                    else DestroyImmediate(root);
                }
                importer?.Dispose();
                pending.Dispose();
                pending = null;
                IsLoading = false;
            }
        }

        public void Cancel() => pending?.Cancel();
        private void OnDestroy() => Cancel();

        private void Place(GameObject root)
        {
            // glTF is meters/Y-up; glTFast already converts handedness. Metadata units describe
            // engineering values and must NOT be applied again to glTF geometry.
            var meshes = root.GetComponentsInChildren<MeshFilter>(true);
            Bounds bounds = default;
            bool hasBounds = false;
            foreach (var filter in meshes)
            {
                if (filter.sharedMesh == null) continue;
                var local = filter.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = filter.transform.TransformPoint(local.center + Vector3.Scale(local.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                    if (!hasBounds) { bounds = new Bounds(point, Vector3.zero); hasBounds = true; }
                    else bounds.Encapsulate(point);
                }
            }
            if (!hasBounds) throw new InvalidDataException("GLB contains no reviewable mesh geometry.");
            float largest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (float.IsNaN(largest) || float.IsInfinity(largest)) throw new InvalidDataException("GLB bounds are invalid.");
            float scale = FitForReview && largest > 0 ? Mathf.Max(0.01f, ReviewSize) / largest : 1;
            root.transform.localScale = Vector3.one * scale;
            var origin = ReviewOrigin != null ? ReviewOrigin : Camera.main != null ? Camera.main.transform : null;
            Vector3 forward = origin != null ? Vector3.ProjectOnPlane(origin.forward, Vector3.up).normalized : Vector3.forward;
            if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
            Vector3 position = origin != null ? origin.position : new Vector3(0, 1.5f, 0);
            root.transform.position = position + forward * (Mathf.Max(0.1f, ReviewDistance) + bounds.extents.magnitude * scale)
                - Vector3.up * 0.3f - bounds.center * scale;
        }

        private sealed class IndexedInstantiator : GameObjectInstantiator
        {
            public IndexedInstantiator(IGltfReadable gltf, Transform root) : base(gltf, root,
                settings: new InstantiationSettings { Mask = ComponentType.Mesh }) { }

            public IReadOnlyDictionary<int, GameObject> Nodes
            {
                get
                {
                    var result = new Dictionary<int, GameObject>();
                    foreach (var node in m_Nodes) result.Add(checked((int)node.Key), node.Value);
                    return result;
                }
            }
        }
    }
}
