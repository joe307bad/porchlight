using System.IO;
using Porchlight.App;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Porchlight.EditorTools
{
    /// <summary>
    /// Sets up the project files that are hard to write by hand: a URP asset tuned for low-end GPUs,
    /// player settings for WebGL, and a scene in Build Settings. It runs once automatically when the
    /// project opens; every step checks first, so running it again is safe.
    /// Re-run from Porchlight > Run Project Setup.
    /// </summary>
    [InitializeOnLoad]
    public static class PorchlightProjectSetup
    {
        private const string SettingsFolder = "Assets/Porchlight/Settings";
        private const string ScenePath = "Assets/Porchlight/Scenes/Main.unity";

        static PorchlightProjectSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode) Run();
            };
        }

        [MenuItem("Porchlight/Run Project Setup")]
        public static void Run()
        {
            EnsureRenderPipeline();
            EnsurePlayerSettings();
            EnsureScene();
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Porchlight/Viewer Mode (Switch preview)")]
        private static void ToggleViewerMode()
        {
            EditorPrefs.SetBool(AppConfig.ViewerModePref, !EditorPrefs.GetBool(AppConfig.ViewerModePref, false));
        }

        [MenuItem("Porchlight/Viewer Mode (Switch preview)", true)]
        private static bool ToggleViewerModeValidate()
        {
            Menu.SetChecked("Porchlight/Viewer Mode (Switch preview)", EditorPrefs.GetBool(AppConfig.ViewerModePref, false));
            return true;
        }

        private static void EnsureRenderPipeline()
        {
            if (GraphicsSettings.defaultRenderPipeline != null) return;

            EnsureFolder(SettingsFolder);
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, SettingsFolder + "/PorchlightRenderer.asset");

            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            // Flat-coloured, unshadowed, low poly: spend nothing on HDR or shadows, and use cheap MSAA for clean edges.
            pipeline.supportsHDR = false;
            pipeline.msaaSampleCount = 4;
            pipeline.shadowDistance = 0f;
            AssetDatabase.CreateAsset(pipeline, SettingsFolder + "/PorchlightURP.asset");

            // Quality levels without their own pipeline asset use this default.
            GraphicsSettings.defaultRenderPipeline = pipeline;

            Debug.Log("[Porchlight] Created and assigned a URP asset at " + SettingsFolder);
        }

        private static void EnsurePlayerSettings()
        {
            if (PlayerSettings.companyName != "Porchlight") PlayerSettings.companyName = "Porchlight";
            if (PlayerSettings.productName != "Porchlight") PlayerSettings.productName = "Porchlight";
            if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;

            // The dev server is plain http on localhost. Tighten this before release.
            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            const string template = "PROJECT:Porchlight";
            if (PlayerSettings.WebGL.template != template) PlayerSettings.WebGL.template = template;

            // Gzip plus Content-Encoding from our server works over plain http; Brotli needs https.
            if (PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Gzip)
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            if (PlayerSettings.WebGL.decompressionFallback) PlayerSettings.WebGL.decompressionFallback = false;
        }

        private static void EnsureScene()
        {
            if (EditorBuildSettings.scenes.Length > 0) return;

            if (!File.Exists(ScenePath))
            {
                var active = EditorSceneManager.GetActiveScene();
                if (active.isDirty || !string.IsNullOrEmpty(active.path))
                {
                    Debug.LogWarning("[Porchlight] Add a scene to Build Settings (any empty scene works) or run Porchlight > Run Project Setup from an empty, unsaved scene.");
                    return;
                }

                EnsureFolder(Path.GetDirectoryName(ScenePath).Replace('\\', '/'));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[Porchlight] Added " + ScenePath + " to Build Settings. The app boots itself in any scene.");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
