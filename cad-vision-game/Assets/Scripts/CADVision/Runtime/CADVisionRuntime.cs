using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CADVision
{
    /// <summary>Authoritative ID registry. Call on Unity's main thread after GLB instantiation.</summary>
    [DisallowMultipleComponent]
    public sealed class CADVisionRuntime : MonoBehaviour
    {
        public CadMetadata Metadata { get; private set; }
        public GameObject RootGameObject { get; private set; }
        public int Revision { get; private set; }
        public event Action ModelChanged;
        private Dictionary<string, GameObject> registry = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private IDisposable resources;

        public GameObject GetRoot() => RootGameObject;
        public GameObject GetObject(string id) => id != null && registry.TryGetValue(id, out var obj)
            ? obj : throw new KeyNotFoundException($"Unknown CAD ID '{id}'.");
        public IReadOnlyDictionary<string, GameObject> GetAllObjects() =>
            new ReadOnlyDictionary<string, GameObject>(registry);
        public JObject GetMetadata(string id) => Metadata?.GetObject(id)
            ?? throw new InvalidOperationException("No CAD model is loaded.");
        public JObject GetProjectMetadata() => Metadata?.Project
            ?? throw new InvalidOperationException("No CAD model is loaded.");

        /// <summary>
        /// Takes ownership only after all mappings validate. On failure the caller owns cleanup.
        /// Pass the dedicated model root and glTFast's node-index mapping, never names.
        /// importerResources must keep imported meshes/materials alive until replacement or Clear.
        /// </summary>
        public void PublishImportedModel(CadMetadata metadata, GameObject root,
            IReadOnlyDictionary<int, GameObject> nodes, IDisposable importerResources = null)
        {
            if (metadata == null || root == null || nodes == null) throw new ArgumentNullException();
            if (root == gameObject || transform.IsChildOf(root.transform))
                throw new InvalidDataException("Model root cannot contain the runtime service.");
            if (RootGameObject != null && (root == RootGameObject || root.transform.IsChildOf(RootGameObject.transform)
                || RootGameObject.transform.IsChildOf(root.transform)))
                throw new InvalidDataException("Replacement must have an independent model root.");
            var candidate = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var usedObjects = new HashSet<GameObject>();
            foreach (var mapping in metadata.NodeIndices)
            {
                if (!nodes.TryGetValue(mapping.Value, out var obj) || obj == null)
                    throw new InvalidDataException($"CAD ID '{mapping.Key}' has no instantiated GLB node {mapping.Value}.");
                if (obj != root && !obj.transform.IsChildOf(root.transform))
                    throw new InvalidDataException($"'{mapping.Key}' is outside the imported root.");
                if (!usedObjects.Add(obj)) throw new InvalidDataException("Multiple CAD IDs resolve to one GameObject.");
                var component = obj.GetComponent<CadVisionObject>();
                if (component != null && component.Id != null && component.Id != mapping.Key)
                    throw new InvalidDataException($"'{mapping.Key}' already has a different CAD ID.");
                candidate.Add(mapping.Key, obj);
            }
            // Compare the nearest mapped ancestor, allowing unannotated GLB helper nodes.
            foreach (var pair in candidate)
            {
                Transform ancestor = pair.Value.transform.parent;
                while (ancestor != null && !usedObjects.Contains(ancestor.gameObject)) ancestor = ancestor.parent;
                string parentId = metadata.GetParentId(pair.Key);
                if ((parentId == null && ancestor != null) ||
                    (parentId != null && (ancestor == null || ancestor.gameObject != candidate[parentId])))
                    throw new InvalidDataException($"GLB hierarchy disagrees with metadata for '{pair.Key}'.");
            }
            foreach (var pair in candidate)
                (pair.Value.GetComponent<CadVisionObject>() ?? pair.Value.AddComponent<CadVisionObject>()).Initialize(pair.Key);

            var oldRoot = RootGameObject;
            var oldResources = resources;
            // Clear held dictionary views as well; old IDs must not remain usable after reload.
            registry.Clear();
            registry = candidate;
            Metadata = metadata;
            RootGameObject = root;
            resources = importerResources;
            Revision++;
            Release(oldRoot, oldResources);
            NotifyChanged();
        }

        public void Clear()
        {
            var oldRoot = RootGameObject;
            var oldResources = resources;
            RootGameObject = null;
            Metadata = null;
            resources = null;
            registry.Clear();
            Revision++;
            Release(oldRoot, oldResources);
            NotifyChanged();
        }

        private static void Release(GameObject root, IDisposable disposable)
        {
            if (root != null)
            {
                root.SetActive(false);
                if (Application.isPlaying) Destroy(root);
                else DestroyImmediate(root);
            }
            try { disposable?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void NotifyChanged()
        {
            if (ModelChanged == null) return;
            foreach (Action listener in ModelChanged.GetInvocationList())
                try { listener(); }
                catch (Exception e) { Debug.LogException(e); }
        }

        private void OnDestroy() => Clear();
    }
}
