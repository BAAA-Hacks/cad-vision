using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CADVision.Editor
{
    public static class CADMultiplayerPackageInstaller
    {
        private static AddAndRemoveRequest request;
        private static double deadline;
        private static bool exitWhenDone;

        [InitializeOnLoadMethod]
        private static void InstallIfRequested()
        {
            string marker = Path.Combine(Directory.GetCurrentDirectory(),
                "ProjectSettings/CADMultiplayerPackages.pending");
            if (!File.Exists(marker)) return;
            File.Delete(marker);
            EditorApplication.delayCall += () => Begin(false);
        }

        public static void Install()
        {
            Begin(true);
        }

        private static void Begin(bool shouldExit)
        {
            if (request != null) return;
            exitWhenDone = shouldExit;
            request = Client.AddAndRemove(new[]
            {
                "com.unity.services.multiplayer@2.3.3",
                "com.unity.netcode.gameobjects"
            }, new string[0]);
            deadline = EditorApplication.timeSinceStartup + 600;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
            EditorApplication.update -= Poll;
            if (!request.IsCompleted)
            {
                Debug.LogError("[CAD multiplayer] Package installation timed out.");
                if (exitWhenDone) EditorApplication.Exit(2);
            }
            else if (request.Status != StatusCode.Success)
            {
                Debug.LogError("[CAD multiplayer] Package installation failed: " + request.Error?.message);
                if (exitWhenDone) EditorApplication.Exit(1);
            }
            else
            {
                Debug.Log("[CAD multiplayer] Installed: " +
                    string.Join(", ", request.Result.Select(p => p.name + "@" + p.version)));
                if (exitWhenDone) EditorApplication.Exit(0);
            }
            request = null;
        }
    }
}
