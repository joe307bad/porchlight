// Playing the town with other people (the server's CityRoom, through
// Colyseus). Everyone exploring the same town (seed and params) shares a
// room. This client plays one character: it tells the room where that
// character is about 30 times a second while it moves (and a little after it
// stops, and once a second at rest), each position stamped with when it was
// taken so others can play it back evenly; it hands the room's patches about
// everyone nearby to the view, and does what the room says (start where you
// left off, snap back after a bad move).
//
// The town is shared as well: the room names one client its host, which runs
// the traffic and townsfolk and sends them about ten times a second for the
// others to follow; night or day, the town's cars someone has taken and each
// player's parked car come with the room's state.
//
// Movement is predicted: your character moves the moment you press a key, and
// the server only ever corrects it. If the server can't be reached the town
// still works, on your own, and the client keeps trying to rejoin.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Porchlight.Core;
using Porchlight.Net;
using Porchlight.Rendering;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>What the game view does for the network (ExploreView).</summary>
    public interface INetHost
    {
        LocalState? GetLocalState();
        /// <summary>Joined: start where they left off in this town (or at its spawn point; at = {x, z, yaw} or null), looking like this.</summary>
        void ApplySpawn(double[] at, string look);
        /// <summary>
        /// The server says they're here: a minimap jump it accepted (`teleported`:
        /// on foot, there), or a correction (put back, in whatever car they're in).
        /// </summary>
        void PlacePlayer(double x, double z, bool teleported);
        void SyncRemotes(List<RemoteState> players, string me);
        void ClearRemotes();
        /// <summary>Everyone else in the room (names and rough positions), for the minimap and the player list.</summary>
        void SetOthers(List<OtherPlayer> others);
        /// <summary>A line about the connection (null clears it).</summary>
        void Notify(string text);
        /// <summary>Run the town's traffic and townsfolk here (alone, or the room's host), or follow the host's.</summary>
        void SetHost(bool host);
        /// <summary>The traffic and townsfolk to send, while running them.</summary>
        WorldState? World();
        void ApplyWorld(byte[] cars, byte[] people, List<string> looks);
        /// <summary>The room's list of the town's cars someone has got into ("p3,t12").</summary>
        void SetTaken(string list);
        void SetTownNight(bool on);
        void SyncParked(List<RemoteParked> cars, string me);
        /// <summary>Your own car, if it's parked.</summary>
        ParkedState OwnCar();
    }

    public struct WorldState
    {
        public byte[] cars, people;
        public List<string> looks;
    }

    /// <summary>A parked car of your own: model + 1 (0: none, or you're driving it), where and which way (map heading).</summary>
    public struct ParkedState
    {
        public int model;
        public double x, z, yaw;
    }

    /// <summary>Someone else's parked car: model index, map position and heading.</summary>
    public struct RemoteParked
    {
        public string key;
        public int model;
        public double x, y, heading;
    }

    /// <summary>How this tab is doing online, for the sidebar.</summary>
    public struct NetStatus
    {
        public string state; // "joining" | "online" | "offline"
        public int here;
        public string why;

        public static NetStatus Joining => new NetStatus { state = "joining" };
        public static NetStatus Online(int here) => new NetStatus { state = "online", here = here };
        public static NetStatus Offline(string why) => new NetStatus { state = "offline", why = why };
    }

    public sealed class TownKey
    {
        /// <summary>Identifies the town: its seed and params.</summary>
        public string key;
        public double width, height;
    }

    public sealed class Multiplayer
    {
        /// <summary>After the last change, keep sending this long (ms), so others see exactly where they stopped.</summary>
        const double SETTLE_MS = 300;
        /// <summary>At rest, send this often anyway (ms).</summary>
        const double HEARTBEAT_MS = 1000;
        /// <summary>A correction this soon after asking to jump somewhere is the jump (ms).</summary>
        const double TELEPORT_ANSWER_MS = 5000;
        /// <summary>Rejoin delays after losing the room (ms), longest last and then repeated.</summary>
        static readonly double[] RETRY_MS = { 2000, 5000, 10000, 30000 };

        readonly ColyseusClient client;
        readonly INetPlatform platform;
        readonly Timers timers;
        Room room;
        TownKey town;
        int seq;
        double epoch;
        bool spawned;
        /// <summary>The town this tab has placed its character in (rejoining it, they carry on from where they are).</summary>
        string placedIn;
        /// <summary>When we last asked to jump somewhere (ms), so its answer isn't taken for a correction.</summary>
        double teleportAt = double.NegativeInfinity;
        string lastSent = "";
        double changedAt, sentAt;
        object timer, worldTimer, retry;
        string lastCar = "";
        int retries;
        bool stopped;
        int generation;
        readonly INetHost host;
        public readonly Character character;
        public Action<NetStatus> onStatus;
        NetStatus status = NetStatus.Joining;

        public Multiplayer(INetHost host, Character character, string origin, INetPlatform platform, Timers timers)
        {
            this.host = host;
            this.character = character;
            this.platform = platform;
            this.timers = timers;
            client = new ColyseusClient(origin + "/mp", platform, timers);
        }

        /// <summary>The tab came back into view: say where we are now.</summary>
        public void Visible() => Send(true);

        /// <summary>Play this town (leaving any other).</summary>
        public void Enter(TownKey t)
        {
            if (town != null && town.key == t.key && (room != null || retry != null)) return;
            town = t;
            if (stopped) return;
            retries = 0;
            Join();
        }

        /// <summary>Leave the room for good (switching character, or closing).</summary>
        public void Close()
        {
            town = null;
            stopped = true;
            Disconnect();
        }

        public string Debug()
        {
            var players = new List<object>();
            if (room?.State?.Get("players") is SchemaMap map)
                foreach (var k in map.keys)
                    if (map.items[k] is SchemaObj p)
                        players.Add(new Dictionary<string, object> { ["key"] = k, ["me"] = k == room.sessionId, ["name"] = p.Str("name"), ["x"] = Math.Round(p.Num("x"), 1), ["z"] = Math.Round(p.Num("z"), 1), ["anim"] = p.Num("anim") });
            return Json.Serialize(new Dictionary<string, object>
            {
                ["character"] = character.name, ["town"] = town?.key, ["room"] = room?.roomId, ["session"] = room?.sessionId,
                ["spawned"] = spawned, ["stopped"] = stopped, ["status"] = status.state, ["sentTo"] = players,
            });
        }

        void SetStatus(NetStatus s)
        {
            status = s;
            onStatus?.Invoke(s);
        }

        /// <summary>A minimap jump: ask the server, which answers with where to go. False when offline (just go).</summary>
        public bool Teleport(double x, double z)
        {
            if (room == null || !spawned) return false;
            room.Send(Protocol.MSG_TELEPORT, new Dictionary<string, object> { ["x"] = x, ["z"] = z });
            teleportAt = platform.Now();
            return true;
        }

        /// <summary>A new look for the character (it's saved with them).</summary>
        public void LookChanged(string model) => room?.Send(Protocol.MSG_LOOK, new Dictionary<string, object> { ["model"] = model });

        void Disconnect()
        {
            generation++;
            if (retry != null) timers.Cancel(retry);
            retry = null;
            StopTimers();
            var r = room;
            room = null;
            spawned = false;
            Offline();
            r?.Leave();
        }

        void StopTimers()
        {
            if (timer != null) timers.Cancel(timer);
            timer = null;
            if (worldTimer != null) timers.Cancel(worldTimer);
            worldTimer = null;
        }

        /// <summary>Out of the room: nobody else, and the town runs here again.</summary>
        void Offline()
        {
            host.ClearRemotes();
            host.SetOthers(new List<OtherPlayer>());
            host.SetHost(true);
        }

        /// <summary>Night or day, for everyone in the town.</summary>
        public void SetNight(bool on) => room?.Send(Protocol.MSG_NIGHT, new Dictionary<string, object> { ["on"] = on });

        /// <summary>One of the town's cars is yours now ("p3", "t12"): off the street for everyone.</summary>
        public void Take(string car) => room?.Send(Protocol.MSG_TAKE, new Dictionary<string, object> { ["car"] = car });

        public bool Online => room != null;

        void Join()
        {
            Disconnect();
            var t = town;
            if (t == null) return;
            int gen = generation;
            var options = new Dictionary<string, object> { ["town"] = t.key, ["width"] = t.width, ["height"] = t.height, ["character"] = character.id };
            SetStatus(NetStatus.Joining);
            // The room can fill up between matchmaking and joining: try once more.
            client.JoinOrCreate(Protocol.CITY_ROOM, options, (r, err) =>
            {
                if (r == null) client.JoinOrCreate(Protocol.CITY_ROOM, options, (r2, err2) => Joined(gen, r2, err2));
                else Joined(gen, r, null);
            });
        }

        void Joined(int gen, Room r, string err)
        {
            if (r == null)
            {
                if (gen != generation) return;
                UnityEngine.Debug.LogWarning("multiplayer: could not join: " + err);
                string why = string.IsNullOrEmpty(err) ? "the game server isn't answering" : err;
                host.Notify($"Playing alone: {why}. Trying again…");
                SetStatus(NetStatus.Offline(why));
                ScheduleRejoin();
                return;
            }
            if (gen != generation)
            {
                r.Leave();
                return;
            }
            room = r;
            seq = 0;
            epoch = 0;
            lastSent = "";
            lastCar = "";
            // Following the room's host until the room says otherwise.
            host.SetHost(false);
            retries = 0;
            host.Notify(null);
            SetStatus(NetStatus.Online(1));

            r.OnMessage(Protocol.MSG_SPAWN, m =>
            {
                double[] at = null;
                string look = null;
                if (m is Dictionary<string, object> d)
                {
                    if (d.TryGetValue("at", out var a) && a is Dictionary<string, object> ad)
                        at = new[] { MsgPack.Num(ad["x"]), MsgPack.Num(ad["z"]), MsgPack.Num(ad["yaw"]) };
                    if (d.TryGetValue("look", out var l)) look = l as string;
                }
                // Back in a town we were already playing (after a dropped connection, or
                // the server restarting): stay where we are, car and all, rather than
                // going back to where the server last saved us. The first move says where.
                if (placedIn != town?.key) host.ApplySpawn(at, look);
                placedIn = town?.key;
                spawned = true;
                Send(true);
            });
            r.OnMessage(Protocol.MSG_CORRECT, m =>
            {
                if (!(m is Dictionary<string, object> c)) return;
                epoch = MsgPack.Num(c["epoch"]);
                bool teleported = platform.Now() - teleportAt < TELEPORT_ANSWER_MS;
                if (teleported) teleportAt = double.NegativeInfinity;
                host.PlacePlayer(MsgPack.Num(c["x"]), MsgPack.Num(c["z"]), teleported);
                Send(true);
            });
            r.OnMessage(Protocol.MSG_DOTS, m =>
            {
                if (!(m is List<object> dots)) return;
                int here = Math.Max(1, dots.Count);
                if (status.state != "online" || status.here != here) SetStatus(NetStatus.Online(here));
                var list = new List<OtherPlayer>();
                foreach (var e in dots)
                    if (e is List<object> dot && dot.Count >= 3 && dot[0] is string key && key != r.sessionId)
                        list.Add(new OtherPlayer { key = key, name = dot.Count > 3 ? dot[3] as string ?? "" : "", x = MsgPack.Num(dot[1]), y = MsgPack.Num(dot[2]) });
                host.SetOthers(list);
            });
            r.OnMessage(Protocol.MSG_WORLD, m =>
            {
                if (!(m is Dictionary<string, object> w)) return;
                var looks = new List<string>();
                if (w.TryGetValue("looks", out var l) && l is List<object> ll) foreach (var x in ll) if (x is string look) looks.Add(look);
                host.ApplyWorld(w.TryGetValue("cars", out var c) ? c as byte[] : null, w.TryGetValue("people", out var p) ? p as byte[] : null, looks);
            });
            r.onStateChange = state =>
            {
                var players = new List<RemoteState>();
                if (state.Get("players") is SchemaMap map)
                    foreach (var key in map.keys)
                        if (key != r.sessionId && map.items[key] is SchemaObj p) players.Add(Pick(p, key));
                host.SyncRemotes(players, r.sessionId);
                host.SetHost(state.Str("host") == r.sessionId);
                host.SetTownNight(state.Bool("night"));
                host.SetTaken(state.Str("taken") ?? "");
                var parked = new List<RemoteParked>();
                if (state.Get("cars") is SchemaMap cars)
                    foreach (var key in cars.keys)
                        if (cars.items[key] is SchemaObj c && c.Num("model") > 0)
                            parked.Add(new RemoteParked { key = key, model = (int)c.Num("model") - 1, x = c.Num("x"), y = c.Num("z"), heading = Protocol.YawFromWire(c.Num("yaw")) });
                host.SyncParked(parked, r.sessionId);
            };
            r.onDrop = () => host.Notify("Connection lost. Reconnecting…");
            r.onReconnect = () =>
            {
                host.Notify(null);
                Send(true);
            };
            r.onLeave = code =>
            {
                if (room != r) return; // we left it ourselves
                room = null;
                spawned = false;
                StopTimers();
                Offline();
                if (code == Protocol.CLOSE_SIGNED_IN_ELSEWHERE)
                {
                    stopped = true;
                    SetStatus(NetStatus.Offline("playing somewhere else"));
                    host.Notify($"{character.name} is being played somewhere else now. Playing alone here.");
                }
                else if (code == Protocol.CLOSE_KICKED)
                {
                    stopped = true;
                    SetStatus(NetStatus.Offline("sent out by the server"));
                    host.Notify("The server sent you out of the town. Playing alone here.");
                }
                else
                {
                    host.Notify("Lost the game server. Playing alone, trying to rejoin…");
                    SetStatus(NetStatus.Offline("lost the server"));
                    ScheduleRejoin();
                }
            };
            timer = timers.Every(Protocol.SEND_MS, () => Send(false));
            worldTimer = timers.Every(Protocol.WORLD_MS, SendWorld);
        }

        /// <summary>The host's traffic and townsfolk, for everyone else.</summary>
        void SendWorld()
        {
            if (room == null || !spawned) return;
            var w = host.World();
            if (w == null) return;
            room.Send(Protocol.MSG_WORLD, new Dictionary<string, object>
            {
                ["cars"] = w.Value.cars, ["people"] = w.Value.people, ["looks"] = w.Value.looks.Cast<object>().ToList(),
            });
        }

        void ScheduleRejoin()
        {
            if (stopped || town == null || retry != null) return;
            double delay = RETRY_MS[Math.Min(retries++, RETRY_MS.Length - 1)];
            retry = timers.After(delay, () =>
            {
                retry = null;
                Join();
            });
        }

        static string F(double v, int places) => v.ToString("F" + places, CultureInfo.InvariantCulture);

        /// <summary>Walk / run / jump, for the anim byte (others' clients mostly work it out from the motion).</summary>
        static int AnimOf(LocalState s)
        {
            if (!string.IsNullOrEmpty(s.ride)) return Protocol.ANIM_RIDE;
            if (s.car != 0) return Protocol.ANIM_DRIVE;
            if (s.y > 0) return Protocol.ANIM_JUMP;
            if (s.speed > 2.5) return Protocol.ANIM_RUN;
            return s.speed > 0.05 ? Protocol.ANIM_WALK : Protocol.ANIM_IDLE;
        }

        /// <summary>
        /// Send where the character is: while it's changing, for a moment after it
        /// stops (so others see it come to rest exactly), once a second at rest, or
        /// when forced. Each is stamped with this clock's time.
        /// </summary>
        void Send(bool force)
        {
            var r = room;
            if (r == null || !spawned) return;
            var own = host.OwnCar();
            string car = own.model == 0 ? "" : $"{own.model},{F(own.x, 2)},{F(own.z, 2)},{F(own.yaw, 2)}";
            if (car != lastCar)
            {
                lastCar = car;
                r.Send(Protocol.MSG_CAR, new Dictionary<string, object> { ["model"] = own.model, ["x"] = own.x, ["z"] = own.z, ["yaw"] = own.yaw });
            }
            var ls = host.GetLocalState();
            if (ls == null) return;
            var s = ls.Value;
            double now = platform.Now();
            int anim = AnimOf(s);
            string key = $"{F(s.x, 3)},{F(s.y, 3)},{F(s.z, 3)},{F(s.yaw, 2)},{anim},{s.car},{s.ride}";
            if (key != lastSent)
            {
                lastSent = key;
                changedAt = now;
            }
            else if (!force && now - changedAt > SETTLE_MS && now - sentAt < HEARTBEAT_MS) return;
            sentAt = now;
            seq = (seq + 1) & 0xffff;
            // Stamped with when the position was worked out (the last frame), not
            // when this timer fired, up to a frame later. A stale frame (a hidden
            // tab, where nothing moves) is stamped now.
            double t = (double)(uint)(long)Numbers.Round(now - s.at < 100 ? s.at : now);
            r.Send(Protocol.MSG_MOVE, new Dictionary<string, object>
            {
                ["seq"] = seq, ["t"] = t, ["epoch"] = epoch, ["x"] = s.x, ["y"] = s.y, ["z"] = s.z,
                ["yaw"] = s.yaw, ["anim"] = anim, ["car"] = s.car, ["ride"] = s.ride ?? "",
            });
        }

        /// <summary>The synced fields, copied off the live schema object.</summary>
        static RemoteState Pick(SchemaObj p, string key) => new RemoteState
        {
            key = key,
            name = p.Str("name") ?? "",
            x = p.Num("x"),
            y = p.Num("y"),
            z = p.Num("z"),
            rotY = p.Num("rotY"),
            anim = (int)p.Num("anim"),
            car = (int)p.Num("car"),
            ride = p.Str("ride") ?? "",
            appearance = p.Str("appearance") ?? "",
            away = p.Bool("away"),
            t = p.Num("t"),
        };
    }
}
