// Who's signed in, which characters they play and where those live, and what
// the account has earned: the auth service's /auth/me (server/: the session is
// an HttpOnly cookie, never seen here) and the game server's /mp routes.
// One account plays as any of its characters, and more than one at once.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Net;

namespace Porchlight.App
{
    /// <summary>Where a character lives: their town, and (once they've moved in) a tile of their house.</summary>
    public sealed class Home
    {
        public string townId, townName, townKey;
        public bool hasHouse;
        public int x, y;
    }

    /// <summary>Where someone is playing right now (Account.Where).</summary>
    public struct PlayerWhere
    {
        /// <summary>Their room: a town's key, or the north room's.</summary>
        public string room;
        public string session;
        public string name;
        /// <summary>Where in the room's terms (tiles).</summary>
        public double x, z;
    }

    public sealed class Character
    {
        public string id;
        public string name;
        /// <summary>Character model (a CHARACTERS entry), if they've been given one.</summary>
        public string look;
        /// <summary>The town (its key) they're playing in right now, if they are.</summary>
        public string town;
        /// <summary>Their session there: how to pick them out in the town.</summary>
        public string session;
        /// <summary>Someone on another tab or device is playing them, so they can't be chosen.</summary>
        public bool inUse;
        /// <summary>Where they live; null until they've chosen a town.</summary>
        public Home home;

        public static Character From(object o)
        {
            if (!(o is Dictionary<string, object> d)) return null;
            var c = new Character { id = Json.Str(d, "id"), name = Json.Str(d, "name"), look = Json.Str(d, "look"), town = Json.Str(d, "town"), session = Json.Str(d, "session"), inUse = d.TryGetValue("inUse", out var used) && used is bool busy && busy };
            var home = Json.Obj(d, "home");
            if (home != null)
            {
                c.home = new Home { townId = Json.Str(home, "townId"), townName = Json.Str(home, "townName"), townKey = Json.Str(home, "townKey") };
                var house = Json.Obj(home, "house");
                if (house != null)
                {
                    c.home.hasHouse = true;
                    c.home.x = (int)Json.Num(house, "x");
                    c.home.y = (int)Json.Num(house, "y");
                }
            }
            return c;
        }
    }

    public sealed class Town
    {
        public string id, name, key;
        /// <summary>This account made it (and can delete it).</summary>
        public bool mine;
        public int residents, playing;
        /// <summary>Where it is in the county: a town that comes with the game's place ("porchlight", "east"...), or null.</summary>
        public string place;
        /// <summary>One of this account's: which of its three places north of Porchlight it's in (0-2), or -1.</summary>
        public int slot = -1;
    }

    /// <summary>Every town there is, and how many of them this account has made of how many it can.</summary>
    public sealed class TownList
    {
        public List<Town> towns = new List<Town>();
        public int made, max;
        /// <summary>The account's id: its part of the county's room is named for it.</summary>
        public string account;
    }

    /// <summary>A house someone lives in.</summary>
    /// <summary>A mission's best times in one town: fastest first, one per account.</summary>
    public sealed class Board
    {
        public readonly List<BoardRow> best = new List<BoardRow>();
        /// <summary>This account's best there (0: none).</summary>
        public double mine;
    }

    public sealed class BoardRow
    {
        public string name;
        public double seconds;
        public bool mine;
    }

    public sealed class HomeRow
    {
        public int x, y;
        public bool mine;
        public List<string> residents = new List<string>();
    }

    public sealed class MissionProgress
    {
        public string id, title, summary, unlocks, firstAt, firstCharacter;
        public int times;
    }

    public sealed class UnlockProgress
    {
        public string id, name, at, mission;
    }

    /// <summary>What the account has earned, for all its characters, in every town.</summary>
    public sealed class Progress
    {
        public List<MissionProgress> missions = new List<MissionProgress>();
        public List<UnlockProgress> unlocks = new List<UnlockProgress>();
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
            net.Request(method, $"{origin}/mp{path}", body != null ? Json.Serialize(body) : null, (status, text) =>
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

        static string Id(string id) => Uri.EscapeDataString(id);

        /// <summary>Which tab or device this is: the server won't give a character being played by another.</summary>
        public static string Tab = "";

        public static void ListCharacters(INetPlatform net, string origin, Action<List<Character>, string> done) =>
            Call(net, origin, "GET", "/characters?tab=" + Id(Tab), null, (json, err) =>
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

        /// <summary>
        /// Where a character is playing right now (anyone's, by the id their room shows): their room's key, their
        /// session there, and their spot in its terms; null (and an error) if they aren't playing.
        /// </summary>
        public static void Where(INetPlatform net, string origin, string character, Action<PlayerWhere?, string> done) =>
            Call(net, origin, "GET", "/players/" + Id(character), null, (json, err) =>
            {
                if (err != null || !(json is Dictionary<string, object> d))
                {
                    done(null, err ?? "no answer");
                    return;
                }
                done(new PlayerWhere { room = Json.Str(d, "town"), session = Json.Str(d, "session"), name = Json.Str(d, "name"), x = Json.Num(d, "x", 0), z = Json.Num(d, "z", 0) }, null);
            });

        public static void CreateCharacter(INetPlatform net, string origin, string name, string look, Action<Character, string> done) =>
            Call(net, origin, "POST", "/characters", new Dictionary<string, object> { ["name"] = name, ["look"] = look },
                (json, err) => done(err != null ? null : Character.From(json), err));

        public static void DeleteCharacter(INetPlatform net, string origin, string id, Action<string> done) =>
            Call(net, origin, "DELETE", "/characters/" + Id(id), null, (_, err) => done(err));

        /// <summary>Move a character to a town (out of any house they had in another).</summary>
        public static void MoveToTown(INetPlatform net, string origin, string character, string town, Action<Character, string> done) =>
            Call(net, origin, "PUT", $"/characters/{Id(character)}/town", new Dictionary<string, object> { ["town"] = town },
                (json, err) => done(err != null ? null : Character.From(json), err));

        /// <summary>Move a character into the house at this tile of their town.</summary>
        public static void ClaimHome(INetPlatform net, string origin, string character, int x, int y, Action<Character, string> done) =>
            Call(net, origin, "PUT", $"/characters/{Id(character)}/home", new Dictionary<string, object> { ["x"] = x, ["y"] = y },
                (json, err) => done(err != null ? null : Character.From(json), err));

        static Town TownFrom(object t) => new Town
        {
            id = Json.Str(t, "id"),
            name = Json.Str(t, "name"),
            key = Json.Str(t, "key"),
            mine = t is Dictionary<string, object> d && d.TryGetValue("mine", out var m) && m is bool b && b,
            residents = (int)Json.Num(t, "residents", 0),
            playing = (int)Json.Num(t, "playing", 0),
            place = Json.Str(t, "place"),
            slot = t is Dictionary<string, object> ds && ds.TryGetValue("slot", out var sl) && sl != null ? (int)Json.Num(t, "slot", -1) : -1,
        };

        public static void ListTowns(INetPlatform net, string origin, Action<TownList, string> done) =>
            Call(net, origin, "GET", "/towns", null, (json, err) =>
            {
                if (err != null)
                {
                    done(null, err);
                    return;
                }
                var list = new TownList { made = (int)Json.Num(json, "made", 0), max = (int)Json.Num(json, "max", 0), account = Json.Str(json, "account") };
                foreach (var t in Json.Arr(json, "towns") ?? new List<object>()) list.towns.Add(TownFrom(t));
                done(list, null);
            });

        /// <summary>A new town: from the town editor's key, or (null) a fresh seed.</summary>
        public static void CreateTown(INetPlatform net, string origin, string name, string key, Action<Town, string> done)
        {
            var body = new Dictionary<string, object> { ["name"] = name };
            if (key != null) body["key"] = key;
            Call(net, origin, "POST", "/towns", body, (json, err) => done(err != null ? null : TownFrom(json), err));
        }

        public static void DeleteTown(INetPlatform net, string origin, string id, Action<string> done) =>
            Call(net, origin, "DELETE", "/towns/" + Id(id), null, (_, err) => done(err));

        public static void ListHomes(INetPlatform net, string origin, string town, Action<List<HomeRow>, string> done) =>
            Call(net, origin, "GET", $"/towns/{Id(town)}/homes", null, (json, err) =>
            {
                if (err != null)
                {
                    done(null, err);
                    return;
                }
                var list = new List<HomeRow>();
                foreach (var h in Json.Arr(json, "homes") ?? new List<object>())
                {
                    var row = new HomeRow { x = (int)Json.Num(h, "x", 0), y = (int)Json.Num(h, "y", 0), mine = h is Dictionary<string, object> d && d.TryGetValue("mine", out var m) && m is bool b && b };
                    foreach (var r in Json.Arr(h, "residents") ?? new List<object>()) if (r is string name) row.residents.Add(name);
                    list.Add(row);
                }
                done(list, null);
            });

        public static void FetchProgress(INetPlatform net, string origin, Action<Progress, string> done) =>
            Call(net, origin, "GET", "/progress", null, (json, err) =>
            {
                if (err != null)
                {
                    done(null, err);
                    return;
                }
                var p = new Progress();
                foreach (var m in Json.Arr(json, "missions") ?? new List<object>())
                    p.missions.Add(new MissionProgress { id = Json.Str(m, "id"), title = Json.Str(m, "title"), summary = Json.Str(m, "summary"), unlocks = Json.Str(m, "unlocks"), firstAt = Json.Str(m, "firstAt"), firstCharacter = Json.Str(m, "firstCharacter"), times = (int)Json.Num(m, "times", 0) });
                foreach (var u in Json.Arr(json, "unlocks") ?? new List<object>())
                    p.unlocks.Add(new UnlockProgress { id = Json.Str(u, "id"), name = Json.Str(u, "name"), at = Json.Str(u, "at"), mission = Json.Str(u, "mission") });
                done(p, null);
            });

        /// <summary>done(board, error): a mission's best times in a town (its key), fastest first, and this account's.</summary>
        public static void FetchRecords(INetPlatform net, string origin, string town, string mission, Action<Board, string> done) =>
            Call(net, origin, "GET", $"/records?town={Uri.EscapeDataString(town)}&mission={Uri.EscapeDataString(mission)}", null, (json, err) => done(err != null ? null : BoardFrom(json), err));

        /// <summary>Keep a time a character ran (if it's the account's best in the town): done(board after, error).</summary>
        public static void KeepRecord(INetPlatform net, string origin, string town, string mission, string character, double seconds, Action<Board, string> done) =>
            Call(net, origin, "POST", "/records", new Dictionary<string, object> { ["town"] = town, ["mission"] = mission, ["character"] = character, ["seconds"] = seconds }, (json, err) => done(err != null ? null : BoardFrom(json), err));

        static Board BoardFrom(object json)
        {
            var b = new Board { mine = Json.Num(json, "mine", 0) };
            foreach (var r in Json.Arr(json, "best") ?? new List<object>())
                b.best.Add(new BoardRow { name = Json.Str(r, "name"), seconds = Json.Num(r, "seconds", 0), mine = r is Dictionary<string, object> d && d.TryGetValue("mine", out var m) && m is bool yes && yes });
            return b;
        }

        /// <summary>
        /// done(user, provider, error, status): the user when signed in; otherwise the
        /// sign-in provider ("google") or an error. Status 0 is no answer at all.
        /// </summary>
        public static void FetchMe(INetPlatform net, string origin, Action<User, string, string, int> done) =>
            net.Request("GET", origin + "/auth/me", null, (status, text) =>
            {
                object json = null;
                try { json = Json.Parse(text); }
                catch (Exception) { }
                if (status == 401)
                {
                    done(null, Json.Str(json, "signIn"), null, status);
                    return;
                }
                if (status < 200 || status >= 300 || json == null)
                {
                    done(null, null, $"The sign-in service isn't answering ({status}).", status);
                    return;
                }
                done(new User { name = Json.Str(json, "name"), email = Json.Str(json, "email") }, null, null, status);
            });
    }
}
