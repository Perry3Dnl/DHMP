using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;

namespace DHMP.Unity.Editor
{
    public static class DhmpDemoBuilder
    {
        [MenuItem("DHMP/Open imported Demo Arena")]
        public static void OpenArena()
        {
            string scene = FindScene();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(scene);
        }
        [MenuItem("DHMP/Build Demo/Windows Client (Development)")]
        public static void BuildWindowsDevelopment() => Build(false, true);
        [MenuItem("DHMP/Build Demo/Linux Server (Development)")]
        public static void BuildServerDevelopment() => Build(true, true);
        [MenuItem("DHMP/Build Demo/Windows Client (Licensed Release)")]
        public static void BuildWindowsRelease() => Build(false, false);
        [MenuItem("DHMP/Build Demo/Linux Server (Licensed Release)")]
        public static void BuildServerRelease() => Build(true, false);

        // Also callable with Unity -batchmode -executeMethod DHMP.Unity.Editor.DhmpDemoBuilder.BuildServerDevelopment.
        private static void Build(bool server, bool development)
        {
            string scene = FindScene();
            string settingsPath = Path.GetDirectoryName(scene).Replace('\\', '/') + "/DhmpDemoSettings.asset";
            DhmpUnitySettings settings = AssetDatabase.LoadAssetAtPath<DhmpUnitySettings>(settingsPath);
            if (settings == null) throw new InvalidOperationException("The imported sample's settings asset is missing.");
            settings.ValidateStartup(server, development);
            // Do not silently mutate the developer's active input setting or other project settings.
#if !ENABLE_INPUT_SYSTEM
            if (!server)
                throw new InvalidOperationException("Enable Input System Package (New) or Both under Player > Active Input Handling, then restart Unity.");
#endif
            string output = server ? "Builds/DHMP-Server/DHMP-Arena.x86_64" : "Builds/DHMP-Client/DHMP-Arena.exe";
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene }, locationPathName = output,
                target = server ? BuildTarget.StandaloneLinux64 : BuildTarget.StandaloneWindows64,
                subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                options = development ? BuildOptions.Development : BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("DHMP demo build failed: " + report.summary.result);
        }
        private static string FindScene()
        {
            string[] scenes = AssetDatabase.FindAssets("DemoArena t:Scene").Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith("/DemoArena.unity", StringComparison.Ordinal)).ToArray();
            if (scenes.Length != 1) throw new InvalidOperationException("Import exactly one Demo Arena sample from Window > Package Manager > DHMP Networking 1 > Samples.");
            return scenes[0];
        }
    }
}
