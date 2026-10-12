using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Porchlight.EditorTools
{
    /// <summary>
    /// Player builds, from the menu or the command line:
    ///   Unity -batchmode -quit -projectPath unity -executeMethod Porchlight.EditorTools.BuildScripts.BuildWebGL -buildOutput ../browser/dist
    /// </summary>
    public static class BuildScripts
    {
        private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

        [MenuItem("Porchlight/Build/WebGL (browser builder)")]
        public static void BuildWebGL()
        {
            Build(BuildTarget.WebGL, OutputArg() ?? Path.Combine(RepoRoot, "browser/dist"), clean: true);
        }

        /// <summary>A desktop build running the Switch viewer flow, for checking it without a devkit.</summary>
        [MenuItem("Porchlight/Build/macOS viewer (Switch preview)")]
        public static void BuildMacViewer()
        {
            Build(BuildTarget.StandaloneOSX, OutputArg() ?? Path.Combine(RepoRoot, "builds/mac-viewer/Porchlight.app"), clean: false, "PORCHLIGHT_VIEWER_ONLY");
        }

        /// <summary>Needs the Nintendo Switch build module, which you get through the Nintendo Developer Portal.</summary>
        [MenuItem("Porchlight/Build/Nintendo Switch")]
        public static void BuildSwitch()
        {
            Build(BuildTarget.Switch, OutputArg() ?? Path.Combine(RepoRoot, "builds/switch/Porchlight.nsp"), clean: false);
        }

        private static void Build(BuildTarget target, string output, bool clean, params string[] defines)
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

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = target,
                extraScriptingDefines = defines,
                options = BuildOptions.None,
            });

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
