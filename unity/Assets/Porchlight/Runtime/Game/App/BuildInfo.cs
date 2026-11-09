// Which Porchlight this is: the release and commit the build was stamped with
// (Editor/Setup.cs writes Resources/Porchlight/BuildInfo.txt from what CI passes:
// v0.0.N for the desktop app, web.N for the site). Unstamped (the editor, a local
// build) it's "dev".

using UnityEngine;

namespace Porchlight.App
{
    public static class BuildInfo
    {
        /// <summary>Where Setup.cs writes the stamp, under Assets/Porchlight/Resources.</summary>
        public const string RESOURCE = "Porchlight/BuildInfo";

        static bool read;
        static string release, commit;

        /// <summary>"v0.0.42", "web.87", or "dev".</summary>
        public static string Release
        {
            get
            {
                Read();
                return release;
            }
        }

        /// <summary>The commit it was built from, short; empty if not known.</summary>
        public static string Commit
        {
            get
            {
                Read();
                return commit;
            }
        }

        /// <summary>For showing: "Porchlight v0.0.42 (a1b2c3d)".</summary>
        public static string Label => string.IsNullOrEmpty(Commit) ? $"Porchlight {Release}" : $"Porchlight {Release} ({Commit})";

        static void Read()
        {
            if (read) return;
            read = true;
            release = "dev";
            commit = "";
            var stamp = Resources.Load<TextAsset>(RESOURCE);
            if (stamp == null) return;
            var lines = stamp.text.Split('\n');
            if (lines.Length > 0 && lines[0].Trim().Length > 0) release = lines[0].Trim();
            if (lines.Length > 1) commit = lines[1].Trim();
        }
    }
}
