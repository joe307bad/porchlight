using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Porchlight.EditorTools
{
    /// <summary>
    /// Player builds, from the menu or the command line:
    ///   Unity -batchmode -quit -projectPath unity -executeMethod Porchlight.EditorTools.BuildScripts.BuildWebGL -buildOutput ../browser/dist
    /// BuildWebGL is the release build that CI deploys. BuildWebGLDev is the quick one for trying
    /// changes locally (./browser-dev.sh --build).
    /// </summary>
    public static class BuildScripts
    {
        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

        [MenuItem("Porchlight/Build/WebGL (browser builder)")]
        public static void BuildWebGL()
        {
            Build(BuildTarget.WebGL, OutputArg() ?? Path.Combine(RepoRoot, "browser/dist"), clean: true);
        }

        /// <summary>
        /// A quicker WebGL build for checking changes locally. It trades runtime speed and download
        /// size for build time: the final wasm link skips its heavy optimisation, IL2CPP's C++ is
        /// compiled unoptimised and with less generated code, and files aren't compressed (the dev
        /// server serves them as they are). The release settings are put back afterwards, so
        /// BuildWebGL and CI are unaffected.
        /// </summary>
        [MenuItem("Porchlight/Build/WebGL (quick dev build)")]
        public static void BuildWebGLDev()
        {
            var target = NamedBuildTarget.WebGL;
            var optimization = UnityEditor.WebGL.UserBuildSettings.codeOptimization;
            var compiler = PlayerSettings.GetIl2CppCompilerConfiguration(target);
            var codeGeneration = PlayerSettings.GetIl2CppCodeGeneration(target);
            var compression = PlayerSettings.WebGL.compressionFormat;

            // Unity recommends Development builds for the fastest WebGL build times.
            Build(BuildTarget.WebGL, OutputArg() ?? Path.Combine(RepoRoot, "browser/dist"), clean: true, BuildOptions.Development, configure: () =>
            {
                UnityEditor.WebGL.UserBuildSettings.codeOptimization = UnityEditor.WebGL.WasmCodeOptimization.BuildTimes;
                PlayerSettings.SetIl2CppCompilerConfiguration(target, Il2CppCompilerConfiguration.Debug);
                PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSize);
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            }, restore: () =>
            {
                UnityEditor.WebGL.UserBuildSettings.codeOptimization = optimization;
                PlayerSettings.SetIl2CppCompilerConfiguration(target, compiler);
                PlayerSettings.SetIl2CppCodeGeneration(target, codeGeneration);
                PlayerSettings.WebGL.compressionFormat = compression;
                AssetDatabase.SaveAssets();
            });
        }

        /// <summary>A desktop build of the Switch viewer, pointed at production, for checking it without a devkit.</summary>
        [MenuItem("Porchlight/Build/macOS viewer (Switch preview)")]
        public static void BuildMacViewer()
        {
            Build(BuildTarget.StandaloneOSX, OutputArg() ?? Path.Combine(RepoRoot, "builds/mac-viewer/Porchlight.app"), clean: false, "PORCHLIGHT_VIEWER_ONLY", "PORCHLIGHT_PROD");
        }

        /// <summary>Needs the Nintendo Switch build module, which you get through the Nintendo Developer Portal.</summary>
        [MenuItem("Porchlight/Build/Nintendo Switch")]
        public static void BuildSwitch()
        {
            Build(BuildTarget.Switch, OutputArg() ?? Path.Combine(RepoRoot, "builds/switch/Porchlight.nsp"), clean: false, "PORCHLIGHT_PROD");
        }

        private static void Build(BuildTarget target, string output, bool clean, params string[] defines) =>
            Build(target, output, clean, BuildOptions.None, null, null, defines);

        /// <param name="configure">Changes settings for this build only, after project setup has run.</param>
        /// <param name="restore">Puts them back, whether or not the build worked.</param>
        private static void Build(BuildTarget target, string output, bool clean, BuildOptions options, Action configure, Action restore, params string[] defines)
        {
            PorchlightProjectSetup.Run();

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new InvalidOperationException("No scenes in Build Settings. Run Porchlight > Run Project Setup.");

            if (clean && Directory.Exists(output))
            {
                // Clear old hashed build files, but keep the .gitkeep that holds the folder in git.
                foreach (var entry in Directory.GetFileSystemEntries(output))
                {
                    if (Path.GetFileName(entry) == ".gitkeep") continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
            }

            BuildReport report;
            try
            {
                configure?.Invoke();
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = target,
                    extraScriptingDefines = defines,
                    options = options,
                });
            }
            finally
            {
                restore?.Invoke();
            }

            var summary = report.summary;
            Debug.Log($"[Porchlight] {target} build {summary.result}: {output} ({summary.totalSize / (1024f * 1024f):0.0} MB, {summary.totalTime.TotalSeconds:0}s)");
            if (Application.isBatchMode && summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
        }

        private static string OutputArg()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-buildOutput");
            return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        }
    }
}
