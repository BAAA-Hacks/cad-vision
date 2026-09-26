using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CADEN.Unity.Editor
{
    public sealed class CadenPromptSync : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        [InitializeOnLoadMethod]
        private static void OnLoad() => EditorApplication.delayCall += Sync;
        public void OnPreprocessBuild(BuildReport report) => Sync();
        private static void Sync()
        {
            string source = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "CADEN", "prompts", "system.md"));
            if (!File.Exists(source)) return;
            const string asset = "Assets/Scripts/CADEN/Resources/CadenSystemPrompt.txt";
            string destination = Path.Combine(Application.dataPath, "Scripts", "CADEN", "Resources", "CadenSystemPrompt.txt");
            string text = File.ReadAllText(source);
            if (File.Exists(destination) && File.ReadAllText(destination) == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllText(destination, text);
            AssetDatabase.ImportAsset(asset);
        }
    }
}
