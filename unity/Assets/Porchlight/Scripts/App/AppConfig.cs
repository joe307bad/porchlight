using System;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// Settings that differ between builds. The browser (WebGL) build is the town builder; the Switch
    /// build is a viewer for now. Both come from the same code; only <see cref="CanEdit"/> changes.
    /// </summary>
    public static class AppConfig
    {
        public const string ViewerModePref = "Porchlight.ViewerMode";
        public const string ProductionServerPref = "Porchlight.UseProductionServer";

        /// <summary>The Fly.io deployment (infra/prod). Builds with PORCHLIGHT_PROD default to it.</summary>
        public const string ProductionApi = "https://porchlight-server.fly.dev";
        public const string LocalApi = "http://localhost:5080";

#if UNITY_SWITCH || PORCHLIGHT_PROD
        private const string DefaultApi = ProductionApi;
#else
        private const string DefaultApi = LocalApi;
#endif

        public static bool CanEdit
        {
            get
            {
#if UNITY_SWITCH || PORCHLIGHT_VIEWER_ONLY
                return false;
#else
#if UNITY_EDITOR
                // Porchlight > Viewer Mode in the editor previews the Switch flow.
                if (UnityEditor.EditorPrefs.GetBool(ViewerModePref, false)) return false;
#endif
                return !HasArg("-viewer");
#endif
            }
        }

        /// <summary>
        /// The WebGL build talks to the server that served it. Everything else uses -api &lt;url&gt;,
        /// then the PORCHLIGHT_API environment variable, then the build's default: production for
        /// Switch and PORCHLIGHT_PROD builds, localhost otherwise. In the editor,
        /// Porchlight > Use Production Server switches to production.
        /// </summary>
        public static string ApiBaseUrl
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var page))
                {
                    return page.GetLeftPart(UriPartial.Authority);
                }
#endif
                var fromArgs = ArgValue("-api");
                if (!string.IsNullOrEmpty(fromArgs)) return fromArgs.TrimEnd('/');

                var fromEnv = Environment.GetEnvironmentVariable("PORCHLIGHT_API");
                if (!string.IsNullOrEmpty(fromEnv)) return fromEnv.TrimEnd('/');

#if UNITY_EDITOR
                if (UnityEditor.EditorPrefs.GetBool(ProductionServerPref, false)) return ProductionApi;
#endif
                return DefaultApi;
            }
        }

        private static bool HasArg(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
