using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CADVision
{
    [RequireComponent(typeof(CadModelLoader))]
    public sealed class CadFilesAutoLoader : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            var loader = FindFirstObjectByType<CadModelLoader>();
            if (loader == null) loader = new GameObject("CADVisionRuntime").AddComponent<CadModelLoader>();
            if (loader.GetComponent<CadFilesAutoLoader>() == null)
                loader.gameObject.AddComponent<CadFilesAutoLoader>();
        }

        private IEnumerator Start()
        {
            // Allow headset tracking to update before placing the model in front of the viewer.
            yield return null;
            string glbUrl;
            string jsonUrl;
            try
            {
#if UNITY_EDITOR
                if (!CadFilesPackage.TryFindPair(CadFilesPackage.SourceFolder(Application.dataPath), out var pair))
                {
                    Debug.Log("CAD auto-load: no bundled design. Ready to load a design during this session.");
                    yield break;
                }
                glbUrl = new Uri(pair.glb).AbsoluteUri;
                jsonUrl = new Uri(pair.json).AbsoluteUri;
#else
                var basePath = Application.streamingAssetsPath + "/CadFiles/";
                // Android StreamingAssets live inside the APK and must be read with UnityWebRequest.
                if (!basePath.Contains("://") && !Directory.Exists(basePath))
                {
                    Debug.Log("CAD auto-load: no bundled design. Ready to load a design during this session.");
                    yield break;
                }
                if (!basePath.Contains("://")) basePath = new Uri(basePath).AbsoluteUri;
                glbUrl = basePath + "model.glb";
                jsonUrl = basePath + "metadata.json";
#endif
            }
            catch (Exception e) { Debug.LogError("CAD auto-load: " + e.Message); yield break; }

            using var model = UnityWebRequest.Get(glbUrl);
            yield return model.SendWebRequest();
            if (model.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("CAD auto-load: optional bundled GLB unavailable (" + model.error +
                    "). Ready to load a design during this session.");
                yield break;
            }
            using var metadata = UnityWebRequest.Get(jsonUrl);
            yield return metadata.SendWebRequest();
            if (metadata.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("CAD auto-load: optional bundled metadata unavailable (" + metadata.error +
                    "). Ready to load a design during this session.");
                yield break;
            }
            var loader = GetComponent<CadModelLoader>();
            while (loader.IsLoading) yield return null;
            var load = loader.LoadPackageAsync(model.downloadHandler.data,
                Encoding.UTF8.GetString(metadata.downloadHandler.data).TrimStart('\uFEFF'));
            while (!load.IsCompleted) yield return null;
            if (load.IsFaulted) Debug.LogException(load.Exception.GetBaseException());
            else if (!load.IsCanceled)
            {
                var metadataState = GetComponent<CADVisionRuntime>().Metadata;
                Debug.Log(metadataState.HasVerifiedNodeMapping
                    ? "CAD auto-load: loaded Assets/CadFiles pair."
                    : "CAD auto-load: loaded geometry and metadata; per-part CAD mapping is unavailable.");
            }
        }
    }
}
