using System;
using System.IO;
using System.Linq;
using CADVision;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

/// <summary>Reproducible, separate receiver test APK. Does not replace the application's build scenes.</summary>
public static class CadReceiverSmokeBuild
{
    private const string Folder = "Assets/ReceiverSmoke";
    private const string ScenePath = Folder + "/ReceiverSmoke.unity";
    private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
    private static string Status => Path.Combine(Repo, ".utmp/receiver-build-status.txt");

    public static async void Prepare()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Status));
        File.WriteAllText(Status, "Preparing smoke scene");
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play Mode before preparing the test build.");
        var previous = SceneManager.GetActiveScene();
        Scene scene = default;
        GameObject sample = null;
        try
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "ReceiverSmoke");
            if (!AssetDatabase.IsValidFolder(Folder + "/Resources")) AssetDatabase.CreateFolder(Folder, "Resources");
            // Unity refuses additive scene creation while any existing scene is untitled.
            // Preserve it in a new file; never overwrite or discard the user's scene.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(open.path))
                    if (!EditorSceneManager.SaveScene(open, AssetDatabase.GenerateUniqueAssetPath(Folder + "/WorkspaceSnapshot.unity")))
                        throw new IOException("Could not preserve the untitled workspace scene.");
            }
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var camera = new GameObject("Review Camera", typeof(Camera), typeof(AudioListener));
            SceneManager.MoveGameObjectToScene(camera, scene);
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 1.5f, 0);
            // Track the viewer, while imported models remain independent world objects.
            var tracking = camera.AddComponent<TrackedPoseDriver>();
            tracking.positionInput = new InputActionProperty(new InputAction("Head Position",
                InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            tracking.rotationInput = new InputActionProperty(new InputAction("Head Rotation",
                InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
            tracking.trackingStateInput = new InputActionProperty(new InputAction("Head Tracking State",
                InputActionType.Value, "<XRHMD>/trackingState", expectedControlType: "Integer"));
            camera.GetComponent<Camera>().backgroundColor = new Color(0.07f, 0.09f, 0.12f);
            camera.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            var light = new GameObject("Review Light", typeof(Light));
            SceneManager.MoveGameObjectToScene(light, scene);
            light.GetComponent<Light>().type = LightType.Directional;
            light.GetComponent<Light>().intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(35, -30, 0);
            var host = new GameObject("CADVisionRuntime", typeof(CadDesignReceiver));
            SceneManager.MoveGameObjectToScene(host, scene);
            host.GetComponent<CadModelLoader>().ReviewOrigin = camera.transform;
            // Save actual TestASM material references so runtime-only glTF shader variants
            // survive player build stripping. These are test assets, not imported CAD meshes.
            sample = new GameObject("Temporary shader collector", typeof(CadModelLoader));
            SceneManager.MoveGameObjectToScene(sample, scene);
            await sample.GetComponent<CadModelLoader>().LoadFilesAsync(
                Path.Combine(Repo, "TestASM.glb"), Path.Combine(Repo, "TestASM_metadata_sample.json"));
            var root = sample.GetComponent<CADVisionRuntime>().RootGameObject;
            var materials = root.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToArray();
            for (int i = 0; i < materials.Length; i++)
            {
                string path = Folder + $"/Resources/TestMaterial{i}.mat";
                var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (existing != null) EditorUtility.CopySerialized(materials[i], existing);
                else AssetDatabase.CreateAsset(new Material(materials[i]), path);
            }
            sample.GetComponent<CADVisionRuntime>().Clear();
            UnityEngine.Object.DestroyImmediate(sample);
            sample = null;
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            File.WriteAllText(Status, "Prepared: " + ScenePath);
        }
        catch (Exception e) { File.WriteAllText(Status, "Preparation failed: " + e); Debug.LogException(e); }
        finally
        {
            if (sample != null)
            {
                sample.GetComponent<CADVisionRuntime>().Clear();
                UnityEngine.Object.DestroyImmediate(sample);
            }
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
        }
    }

    public static void QueueBuild()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            throw new InvalidOperationException("Switch the active platform to Android before building.");
        Directory.CreateDirectory(Path.GetDirectoryName(Status));
        File.WriteAllText(Status, "Build queued");
        EditorApplication.delayCall += Build;
    }

    private static void Build()
    {
        var target = NamedBuildTarget.Android;
        string identifier = PlayerSettings.GetApplicationIdentifier(target);
        string product = PlayerSettings.productName;
        var backend = PlayerSettings.GetScriptingBackend(target);
        var architectures = PlayerSettings.Android.targetArchitectures;
        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        int inputHandling = settings.FindProperty("activeInputHandler").intValue;
        try
        {
            PlayerSettings.SetApplicationIdentifier(target, "com.cadvision.receivertest");
            PlayerSettings.productName = "CAD Vision Receiver Test";
            PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            settings.Update();
            settings.FindProperty("activeInputHandler").intValue = 1;
            settings.ApplyModifiedPropertiesWithoutUndo();
            File.WriteAllText(Status, "Building Android ARM64 IL2CPP APK");
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, target = BuildTarget.Android,
                locationPathName = Path.Combine(Repo, ".utmp/CADVisionReceiverTest.apk"),
                options = BuildOptions.Development
            });
            File.WriteAllText(Status, $"{result.summary.result}: {result.summary.totalErrors} errors; {result.summary.totalWarnings} warnings; {result.summary.totalTime}");
            if (result.summary.result != BuildResult.Succeeded) Debug.LogError("Receiver smoke build failed.");
        }
        catch (Exception e) { File.WriteAllText(Status, "Build failed: " + e); Debug.LogException(e); }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(target, identifier);
            PlayerSettings.productName = product;
            PlayerSettings.SetScriptingBackend(target, backend);
            PlayerSettings.Android.targetArchitectures = architectures;
            settings.Update();
            settings.FindProperty("activeInputHandler").intValue = inputHandling;
            settings.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
