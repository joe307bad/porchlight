// Which server a run off the web plays on, decided from what it's given: whether
// a server can be chosen at all (a dev build of the desktop app can; the
// released one can't), `-porchlightServer` at launch, the server kept from last
// time, and PORCHLIGHT_ORIGIN. App/Platform.cs reads those and does what this
// says; Checks' `servers` holds it to the rules. Pure.
//
// The rule that matters: the released desktop app plays on the live server
// whatever it finds. A dev build's server file (from before dev builds had
// their own folder), a launch argument or an environment variable can't send it
// elsewhere, so a player never ends up signed in to a dev server.

using System;

namespace Porchlight.Net
{
    public static class ServerChoice
    {
        /// <summary>The live server: where the released desktop app plays.</summary>
        public const string LIVE = "https://porchlight.joebad.com/";

        /// <summary>What to do with the kept server's file.</summary>
        public enum Keep
        {
            /// <summary>Leave it as it is.</summary>
            AsIs,
            /// <summary>Write the chosen server to it (none: delete it).</summary>
            Save,
            /// <summary>Delete it: it's not this build's to follow.</summary>
            Forget,
        }

        /// <summary>
        /// The server chosen ("http://jbhs:5173/"), or null for the live one (or the build's own home).
        /// `args` are the command line; `saved` the kept file's text (null: none).
        /// </summary>
        public static string Chosen(bool choosable, string[] args, string saved, out Keep keep)
        {
            if (!choosable)
            {
                keep = saved != null ? Keep.Forget : Keep.AsIs;
                return null;
            }
            int at = args == null ? -1 : Array.IndexOf(args, "-porchlightServer");
            if (at >= 0 && at + 1 < args.Length)
            {
                keep = Keep.Save;
                return args[at + 1] == "default" || !TryServer(args[at + 1], out var given) || SameServer(given, LIVE) ? null : given;
            }
            keep = Keep.AsIs;
            return saved != null && TryServer(saved, out var kept) && !SameServer(kept, LIVE) ? kept : null;
        }

        /// <summary>
        /// Where a run plays: PORCHLIGHT_ORIGIN (`env`) if the server can be chosen, else the chosen one,
        /// else `home` (the live server for the desktop app, localhost:3017 for a plain player).
        /// </summary>
        public static string Origin(bool choosable, string env, string chosen, string home)
        {
            if (!choosable) return home;
            if (!string.IsNullOrEmpty(env)) return env;
            return chosen ?? home;
        }

        /// <summary>
        /// The session's file for a server: "session" for the live one, its own for any other
        /// ("session-jbhs-5173"), so signing in on one never signs the app out of another.
        /// </summary>
        public static string SessionFile(string origin)
        {
            if (SameServer(origin, LIVE)) return "session";
            var u = new Uri(origin);
            return "session-" + u.Host.Replace(':', '_') + "-" + u.Port;
        }

        /// <summary>
        /// A server's address as typed ("jbhs:5173", "http://jbhs:5173/anything") as an origin with a
        /// slash ("http://jbhs:5173/"); false if it isn't one. No scheme is http.
        /// </summary>
        public static bool TryServer(string text, out string origin)
        {
            origin = null;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            if (!text.Contains("://")) text = "http://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || string.IsNullOrEmpty(u.Host)) return false;
            origin = u.GetLeftPart(UriPartial.Authority) + "/";
            return true;
        }

        public static bool SameServer(string a, string b) =>
            string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
