// The missions on offer: the game server's /mp/missions, which compiles the ink
// in server/missions afresh whenever a file changes, so a new or edited mission
// is here at the next Refresh with no new build of the game.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Net;

namespace Porchlight.App
{
    public sealed class MissionInfo
    {
        public string id;
        public string title;
        public string summary;
        /// <summary>Why it can't be played (its ink has errors), or null.</summary>
        public string error;
    }

    public static class MissionCatalog
    {
        static string Error(string body, int status)
        {
            try
            {
                if (Json.Parse(body) is Dictionary<string, object> d && Json.Str(d, "error") is string e) return e;
            }
            catch (Exception) { }
            return status == 0 ? "The game server isn't answering, so there are no missions." : $"The game server isn't answering ({status}).";
        }

        /// <summary>done(missions, error): every mission (broken ones too, with their errors).</summary>
        public static void List(INetPlatform net, string origin, Action<List<MissionInfo>, string> done) =>
            net.Request("GET", origin + "/mp/missions", null, (status, text) =>
            {
                object json = null;
                try { json = Json.Parse(text); }
                catch (Exception) { }
                if (status < 200 || status >= 300 || json == null)
                {
                    done(null, Error(text, status));
                    return;
                }
                var list = new List<MissionInfo>();
                foreach (var m in Json.Arr(json, "missions") ?? new List<object>())
                    list.Add(new MissionInfo { id = Json.Str(m, "id"), title = Json.Str(m, "title"), summary = Json.Str(m, "summary"), error = Json.Str(m, "error") });
                done(list, null);
            });

        /// <summary>done(info, story, error): one mission, with its compiled story (ink's JSON).</summary>
        public static void Get(INetPlatform net, string origin, string id, Action<MissionInfo, string, string> done) =>
            net.Request("GET", $"{origin}/mp/missions/{Uri.EscapeDataString(id)}", null, (status, text) =>
            {
                object json = null;
                try { json = Json.Parse(text); }
                catch (Exception) { }
                string story = Json.Str(json, "story");
                if (status < 200 || status >= 300 || story == null)
                {
                    done(null, null, Error(text, status));
                    return;
                }
                done(new MissionInfo { id = Json.Str(json, "id"), title = Json.Str(json, "title"), summary = Json.Str(json, "summary") }, story, null);
            });
    }
}
