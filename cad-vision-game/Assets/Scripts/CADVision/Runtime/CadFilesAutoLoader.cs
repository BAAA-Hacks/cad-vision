using System;
using System.Collections;
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
                var pair = CadFilesPackage.FindPair(CadFilesPackage.SourceFolder(Application.dataPath));
                glbUrl = new Uri(pair.glb).AbsoluteUri;
                jsonUrl = new Uri(pair.json).AbsoluteUri;
#else
                var basePath = Application.streamingAssetsPath + "/CadFiles/";
                if (!basePath.Contains("://")) basePath = new Uri(basePath).AbsoluteUri;
                glbUrl = basePath + "model.glb";
                jsonUrl = basePath + "metadata.json";
#endif
            }
            catch (Exception e) { Debug.LogError("CAD auto-load: " + e.Message); yield break; }

            using var model = UnityWebRequest.Get(glbUrl);
            yield return model.SendWebRequest();
            if (model.result != UnityWebRequest.Result.Success)
            { Debug.LogError("CAD auto-load GLB: " + model.error); yield break; }
            using var metadata = UnityWebRequest.Get(jsonUrl);
            yield return metadata.SendWebRequest();
            if (metadata.result != UnityWebRequest.Result.Success)
            { Debug.LogError("CAD auto-load JSON: " + metadata.error); yield break; }
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
