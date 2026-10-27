// One-time project setup and the builds, from the Porchlight menu or batch
// mode (-executeMethod Porchlight.EditorTools.Setup.Run / BuildWebGL):
// URP with 4x MSAA and no post-processing (the web game draws with
// three.js's own shading, which the shaders reproduce), linear colour, the
// Input System, the UI panel and its theme, the boot scene, and the WebGL
// player (a full-window canvas at the screen's pixel ratio).

using System;
using System.IO;
using System.Linq;
using Porchlight.App;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Porchlight.EditorTools
{
    public static class Setup
    {
        const string Root = "Assets/Porchlight";
        const string SettingsDir = Root + "/Settings";
        const string UiDir = Root + "/Resources/Porchlight/UI";
        const string ScenePath = Root + "/Scenes/Boot.unity";

        [MenuItem("Porchlight/Set Up Project")]
        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(Root + "/Scenes");
            Pipeline();
            Player();
            Panel();
            Scene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Porchlight: project set up");
        }

        static void Pipeline()
        {
            string rendererPath = SettingsDir + "/Renderer.asset";
            string pipelinePath = SettingsDir + "/Pipeline.asset";
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            renderer.postProcessData = null;
            EditorUtility.SetDirty(renderer);

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            pipeline.msaaSampleCount = 4;
            pipeline.supportsHDR = false;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            pipeline.shadowDistance = 0;
            pipeline.renderScale = 1;
            EditorUtility.SetDirty(pipeline);

            GraphicsSettings.defaultRenderPipeline = pipeline;
            int levels = QualitySettings.names.Length;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levels; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
                QualitySettings.antiAliasing = 4;
                QualitySettings.vSyncCount = 1;
            }
            QualitySettings.SetQualityLevel(current, false);
        }

        static void Player()
        {
            PlayerSettings.companyName = "Porchlight";
            PlayerSettings.productName = "Porchlight";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.runInBackground = true;
            // Both input backends: the game reads the Input System; UI Toolkit works with either.
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings != null)
            {
                var so = new SerializedObject(settings);
                var handler = so.FindProperty("activeInputHandler");
                if (handler != null) handler.intValue = 2;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);
            PlayerSettings.WebGL.template = "PROJECT:Porchlight";
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.WebGL.initialMemorySize = 256;
            PlayerSettings.WebGL.maximumMemorySize = 2048;
            PlayerSettings.WebGL.powerPreference = WebGLPowerPreference.HighPerformance;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 });
        }

        static void Panel()
        {
            string themePath = UiDir + "/Theme.tss";
            if (!File.Exists(themePath))
            {
                File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(themePath);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
            string panelPath = UiDir + "/Panel.asset";
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, panelPath);
            }
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            panel.scale = 1;
            panel.clearColor = false;
            EditorUtility.SetDirty(panel);
        }

        static void Scene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Porchlight");
            go.AddComponent<PorchlightApp>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        /// <summary>The models the game loads, from the web app (app/public/models) into StreamingAssets.</summary>
        static void CopyModels()
        {
            string from = Path.GetFullPath(Path.Combine(Application.dataPath, "../../app/public/models"));
            string to = Path.Combine(Application.dataPath, "StreamingAssets/models");
            if (Directory.Exists(to)) Directory.Delete(to, true);
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, file);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith("."))) continue;
                string dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest);
            }
            AssetDatabase.Refresh();
        }

        [MenuItem("Porchlight/Build WebGL")]
        public static void BuildWebGL() => Build(BuildTarget.WebGL, "Builds/WebGL");

        [MenuItem("Porchlight/Build macOS")]
        public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "Builds/macOS/Porchlight.app");

        static void Build(BuildTarget target, string path)
        {
            Run();
            CopyModels();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = target,
                options = BuildOptions.None,
            });
            var s = report.summary;
            Debug.Log($"Porchlight: build {s.result} in {s.totalTime} ({s.totalErrors} errors, {s.totalWarnings} warnings) → {path}");
            if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
