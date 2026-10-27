// Who's signed in and which characters they play: the auth service's /auth/me
// (server/: the session is an HttpOnly cookie, never seen here) and the game
// server's /mp/characters. Ports of auth/session.ts and net/characters.ts.
// One account plays as any of its characters, and more than one at once.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Net;

namespace Porchlight.App
{
    public sealed class Character
    {
        public string id;
        public string name;
        /// <summary>Character model (a CHARACTERS entry), if they've been given one.</summary>
        public string look;

        public static Character From(object o) => o is Dictionary<string, object> d
            ? new Character { id = Json.Str(d, "id"), name = Json.Str(d, "name"), look = Json.Str(d, "look") }
            : null;
    }

    public sealed class User
    {
        public string name;
        public string email;
    }

    public static class Account
    {
        static string Error(string body, int status, string fallback)
        {
            try
            {
                if (Json.Parse(body) is Dictionary<string, object> d && Json.Str(d, "error") is string e) return e;
            }
            catch (Exception) { }
            return fallback;
        }

        static void Call(INetPlatform net, string origin, string method, string path, object body, Action<object, string> done)
        {
            net.Request(method, $"{origin}/mp/characters{path}", body != null ? Json.Serialize(body) : null, (status, text) =>
            {
                if (status < 200 || status >= 300)
                {
                    done(null, Error(text, status, $"The game server isn't answering ({status})."));
                    return;
                }
                object json = null;
                try { json = Json.Parse(text); }
                catch (Exception) { }
                done(json ?? new Dictionary<string, object>(), null);
            });
        }

        public static void ListCharacters(INetPlatform net, string origin, Action<List<Character>, string> done) =>
            Call(net, origin, "GET", "", null, (json, err) =>
            {
                if (err != null)
                {
                    done(null, err);
                    return;
                }
                var list = new List<Character>();
                var arr = Json.Arr(json, "characters");
                if (arr != null)
                    foreach (var c in arr)
                        if (Character.From(c) is Character ch) list.Add(ch);
                done(list, null);
            });

        public static void CreateCharacter(INetPlatform net, string origin, string name, string look, Action<Character, string> done) =>
            Call(net, origin, "POST", "", new Dictionary<string, object> { ["name"] = name, ["look"] = look },
                (json, err) => done(err != null ? null : Character.From(json), err));

        public static void DeleteCharacter(INetPlatform net, string origin, string id, Action<string> done) =>
            Call(net, origin, "DELETE", "/" + Uri.EscapeDataString(id), null, (_, err) => done(err));

        /// <summary>done(user, provider, error): the user when signed in; otherwise the sign-in provider ("google") or an error.</summary>
        public static void FetchMe(INetPlatform net, string origin, Action<User, string, string> done) =>
            net.Request("GET", origin + "/auth/me", null, (status, text) =>
            {
                object json = null;
                try { json = Json.Parse(text); }
                catch (Exception) { }
                if (status == 401)
                {
                    done(null, Json.Str(json, "signIn"), null);
                    return;
                }
                if (status < 200 || status >= 300 || json == null)
                {
                    done(null, null, $"The sign-in service isn't answering ({status}).");
                    return;
                }
                done(new User { name = Json.Str(json, "name"), email = Json.Str(json, "email") }, null, null);
            });
    }
}
