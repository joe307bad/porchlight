// Project setup and the builds, from the Porchlight menu or batch mode
// (-executeMethod Porchlight.EditorTools.Setup.Run / BuildWebGL): URP (HDR,
// 4x MSAA, soft shadows, per-pixel lamp lights, bloom and tonemapping), the
// renderer's two extra passes (you, drawn through walls; name tags, over
// everything) on their layers, linear colour, the Input System, TextMeshPro,
// the UI panel and its theme, the model library, the boot scene, and the
// WebGL player (a full-window canvas at the screen's pixel ratio).

using System;
using System.IO;
using System.Linq;
using Porchlight.App;
using Porchlight.Rendering;
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
            TextMeshPro();
            Layers();
            Pipeline();
            Player();
            Panel();
            Library.Build();
            Scene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Porchlight: project set up");
        }

        /// <summary>TextMeshPro's shaders and settings (its "essential resources", kept in Assets/TextMesh Pro).</summary>
        static void TextMeshPro()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
                throw new Exception("Assets/TextMesh Pro is missing: Window ▸ TextMeshPro ▸ Import TMP Essential Resources");
        }

        static int Layer(SerializedProperty layers, string name, int preferred)
        {
            for (int i = 8; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return i;
            for (int i = preferred; i < layers.arraySize; i++)
            {
                var l = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(l.stringValue))
                {
                    l.stringValue = name;
                    return i;
                }
            }
            throw new Exception("no free layer for " + name);
        }

        static int playerLayer, labelsLayer;

        /// <summary>You (drawn through walls) and the name tags (drawn over everything) each have a layer.</summary>
        static void Layers()
        {
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tags.FindProperty("layers");
            playerLayer = Layer(layers, Porchlight.Rendering.Avatar.PLAYER_LAYER, 8);
            labelsLayer = Layer(layers, NameTag.LABELS_LAYER, 9);
            tags.ApplyModifiedPropertiesWithoutUndo();
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
            renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            int labels = 1 << labelsLayer;
            renderer.opaqueLayerMask = ~labels;
            renderer.transparentLayerMask = ~labels;
            Features(renderer);
            EditorUtility.SetDirty(renderer);

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            var so = new SerializedObject(pipeline);
            void Set(string name, Action<SerializedProperty> set)
            {
                var prop = so.FindProperty(name);
                if (prop == null) Debug.LogWarning("Porchlight: no pipeline setting " + name);
                else set(prop);
            }
            Set("m_SupportsHDR", p => p.boolValue = true);
            Set("m_MSAA", p => p.intValue = 4);
            Set("m_RenderScale", p => p.floatValue = 1);
            Set("m_MainLightShadowsSupported", p => p.boolValue = true);
            Set("m_MainLightShadowmapResolution", p => p.intValue = 2048);
            Set("m_SoftShadowsSupported", p => p.boolValue = true);
            Set("m_ShadowDistance", p => p.floatValue = 110);
            Set("m_ShadowCascadeCount", p => p.intValue = 2);
            Set("m_AdditionalLightsRenderingMode", p => p.intValue = (int)LightRenderingMode.PerPixel);
            Set("m_AdditionalLightsPerObjectLimit", p => p.intValue = 8);
            Set("m_RequireDepthTexture", p => p.boolValue = false);
            Set("m_RequireOpaqueTexture", p => p.boolValue = false);
            so.ApplyModifiedPropertiesWithoutUndo();
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

            // The close-up camera's linear fog is turned on at runtime: keep its shader variants in builds.
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            graphics.FindProperty("m_FogStripping").intValue = 1;
            graphics.FindProperty("m_FogKeepLinear").boolValue = true;
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// The renderer's extra passes: you (and the car you drive) drawn in flat orange
        /// wherever something hides you; then the name tags, their plates and text, over
        /// everything (their layer is left out of the ordinary passes).
        /// </summary>
        static void Features(UniversalRendererData renderer)
        {
            var so = new SerializedObject(renderer);
            var list = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            foreach (var old in renderer.rendererFeatures.ToList()) if (old != null) AssetDatabase.RemoveObjectFromAsset(old);
            list.ClearArray();
            map.ClearArray();
            so.ApplyModifiedPropertiesWithoutUndo();

            string xrayPath = Root + "/Generated/Materials/X-ray.mat";
            Directory.CreateDirectory(Root + "/Generated/Materials");
            var xray = AssetDatabase.LoadAssetAtPath<Material>(xrayPath);
            if (xray == null)
            {
                xray = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(xray, xrayPath);
            }
            xray.SetColor("_BaseColor", new Color(1f, 0.416f, 0f));

            RenderObjects Feature(string name, RenderPassEvent at, RenderQueueType queue, int layers, Material material, CompareFunction depth)
            {
                var f = ScriptableObject.CreateInstance<RenderObjects>();
                f.name = name;
                f.settings.Event = at;
                f.settings.filterSettings.RenderQueueType = queue;
                f.settings.filterSettings.LayerMask = layers;
                f.settings.overrideMaterial = material;
                f.settings.overrideDepthState = true;
                f.settings.depthCompareFunction = depth;
                f.settings.enableWrite = false;
                f.Create();
                AssetDatabase.AddObjectToAsset(f, renderer);
                return f;
            }
            var features = new[]
            {
                Feature("Player x-ray", RenderPassEvent.AfterRenderingOpaques, RenderQueueType.Opaque, 1 << playerLayer, xray, CompareFunction.Greater),
                Feature("Name tag plates", RenderPassEvent.AfterRenderingTransparents, RenderQueueType.Opaque, 1 << labelsLayer, null, CompareFunction.Always),
                Feature("Name tag text", RenderPassEvent.AfterRenderingTransparents, RenderQueueType.Transparent, 1 << labelsLayer, null, CompareFunction.Always),
            };
            so.Update();
            foreach (var f in features)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(f, out string _, out long id);
                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = f;
                map.InsertArrayElementAtIndex(map.arraySize);
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = id;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
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

        /// <summary>A fresh checkout has no model library (it's generated): make it when the editor opens.</summary>
        [InitializeOnLoadMethod]
        static void EnsureLibrary()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                // Only the library: the rest of the setup is committed, and Run() would
                // replace the open scene.
                if (AssetDatabase.LoadAssetAtPath<ModelLibrary>(Library.LibraryPath) == null) Library.Build();
            };
        }

        [MenuItem("Porchlight/Build WebGL")]
        public static void BuildWebGL() => Build(BuildTarget.WebGL, "Builds/WebGL");

        [MenuItem("Porchlight/Build macOS")]
        public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "Builds/macOS/Porchlight.app");

        static void Build(BuildTarget target, string path)
        {
            Run();
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
