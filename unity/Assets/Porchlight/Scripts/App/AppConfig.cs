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
        private const string DefaultApi = "http://localhost:5080";

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
        /// then the PORCHLIGHT_API environment variable, then localhost.
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
                return string.IsNullOrEmpty(fromEnv) ? DefaultApi : fromEnv.TrimEnd('/');
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
