// Plays the real game server with the C# Colyseus client (Shared/Net): two
// accounts each make a character, join the same town, move about, and must
// see each other in the room's state, get a spawn and dots, and leave
// cleanly. Start the server first (../../net/server.ts) and pass its JSON line.
//
//   node unity/Tools~/net/server.ts                # prints {"port":…,"cookie":…,"a":…,"b":…}
//   dotnet run --project "unity/Tools~/parity/GenCheck" -- net '<that line>'

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Porchlight.Net;
using Json = Porchlight.Core.Json;

static class NetCheck
{
    /// <summary>A desktop platform for one account: its cookie on every request and socket.</summary>
    sealed class TestPlatform : INetPlatform
    {
        readonly string cookie;
        readonly HttpClient http = new HttpClient();
        readonly Stopwatch clock = Stopwatch.StartNew();
        public readonly ConcurrentQueue<System.Action> queue = new ConcurrentQueue<System.Action>();

        public TestPlatform(string cookie) => this.cookie = cookie;

        public double Now() => clock.Elapsed.TotalMilliseconds;

        public void Request(string method, string url, string json, System.Action<int, string> done)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            req.Headers.Add("Cookie", cookie);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            Task.Run(async () =>
            {
                int status;
                string body;
                try
                {
                    var res = await http.SendAsync(req);
                    status = (int)res.StatusCode;
                    body = await res.Content.ReadAsStringAsync();
                }
                catch (Exception e)
                {
                    status = 0;
                    body = e.Message;
                }
                queue.Enqueue(() => done(status, body));
            });
        }

        public IWebSocket NewSocket() => new Socket(this);

        public void Pump()
        {
            while (queue.TryDequeue(out var a)) a();
        }

        sealed class Socket : IWebSocket
        {
            readonly TestPlatform p;
            readonly ClientWebSocket ws = new ClientWebSocket();
            readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
            public System.Action OnOpen { get; set; }
            public System.Action<byte[]> OnMessage { get; set; }
            public System.Action<int, string> OnClose { get; set; }

            public Socket(TestPlatform p)
            {
                this.p = p;
                ws.Options.SetRequestHeader("Cookie", p.cookie);
            }

            public void Connect(string url) => Task.Run(async () =>
            {
                int code = 1006;
                try
                {
                    await ws.ConnectAsync(new Uri(url), CancellationToken.None);
                    p.queue.Enqueue(() => OnOpen?.Invoke());
                    var buf = new byte[1 << 16];
                    var msg = new System.IO.MemoryStream();
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        msg.Write(buf, 0, r.Count);
                        if (r.EndOfMessage)
                        {
                            var data = msg.ToArray();
                            msg.SetLength(0);
                            p.queue.Enqueue(() => OnMessage?.Invoke(data));
                        }
                    }
                }
                catch (Exception) { }
                if (ws.CloseStatus != null) code = (int)ws.CloseStatus.Value;
                p.queue.Enqueue(() => OnClose?.Invoke(code, ws.CloseStatusDescription ?? ""));
            });

            public void Send(byte[] data) => Task.Run(async () =>
            {
                await sending.WaitAsync();
                try { await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None); }
                catch (Exception) { }
                finally { sending.Release(); }
            });

            public void Close(int code) => Task.Run(async () =>
            {
                try { await ws.CloseOutputAsync((WebSocketCloseStatus)code, "", CancellationToken.None); }
                catch (Exception) { }
            });
        }
    }

    sealed class Player
    {
        public string name;
        public TestPlatform platform;
        public Timers timers;
        public Room room;
        public string error;
        public bool spawned;
        public List<object> dots;
        public int patches;
        public int left = -1;
    }

    static void Pump(Player[] ps, int ms, Func<bool> until = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var p in ps)
            {
                p.platform.Pump();
                p.timers.Tick();
            }
            if (until != null && until()) return;
            Thread.Sleep(5);
        }
    }

    public static int Run(string config)
    {
        var c = JsonNode.Parse(config);
        string origin = $"http://localhost:{(int)c["port"]}";
        string cookieName = (string)c["cookie"];
        var ps = new[] { new Player { name = "a" }, new Player { name = "b" } };
        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok) fails++;
        }

        foreach (var p in ps)
        {
            p.platform = new TestPlatform($"{cookieName}={(string)c[p.name]}");
            p.timers = new Timers(p.platform.Now);
        }

        // Characters over HTTP, as the picker does.
        var ids = new string[2];
        for (int k = 0; k < 2; k++)
        {
            var p = ps[k];
            string got = null, err = null;
            p.platform.Request("POST", origin + "/mp/characters", Json.Serialize(new Dictionary<string, object> { ["name"] = "Unity " + p.name, ["look"] = "characters/character-female-b" }), (s, body) =>
            {
                if (s == 201 || s == 200) got = Json.Str(Json.Parse(body), "id");
                else err = $"{s} {body}";
            });
            Pump(ps, 3000, () => got != null || err != null);
            Check(got != null, $"create character {p.name}: {got ?? err}");
            ids[k] = got;
        }

        // Join the same town.
        for (int k = 0; k < 2; k++)
        {
            var p = ps[k];
            var client = new ColyseusClient(origin + "/mp", p.platform, p.timers);
            bool done = false;
            client.JoinOrCreate("city", new Dictionary<string, object> { ["town"] = "seed=unity-net", ["width"] = 128.0, ["height"] = 128.0, ["character"] = ids[k] }, (room, err) =>
            {
                p.room = room;
                p.error = err;
                done = true;
            });
            Pump(ps, 5000, () => done);
            Check(p.room != null, $"join {p.name}: {p.room?.roomId ?? p.error}");
            if (p.room == null) return 1;
            var me = p;
            p.room.OnMessage("spawn", m => me.spawned = true);
            p.room.OnMessage("dots", m => me.dots = m as List<object>);
            p.room.OnMessage("correct", m => Console.WriteLine($"     {me.name} corrected: {Json.Serialize(m)}"));
            p.room.onStateChange = s => me.patches++;
            p.room.onLeave = code => me.left = code;
        }
        Check(ps[0].room.roomId == ps[1].room.roomId, "both in one room");

        Pump(ps, 1500, () => ps[0].spawned && ps[1].spawned);
        Check(ps[0].spawned && ps[1].spawned, "spawn messages");

        // Move: a walks east from where the server knows them; b stands.
        double x = 40, z = 40;
        var sw = Stopwatch.StartNew();
        int seq = 0;
        var tA = ps[0].timers.Every(33, () =>
        {
            x += 3.0 * 0.033;
            seq = (seq + 1) & 0xffff;
            ps[0].room.Send("move", new Dictionary<string, object> { ["seq"] = seq, ["t"] = Math.Round(ps[0].platform.Now()), ["epoch"] = 0, ["x"] = x, ["y"] = 0.0, ["z"] = z, ["yaw"] = Math.PI / 2, ["anim"] = 1, ["car"] = 0, ["ride"] = "" });
        });
        var tB = ps[1].timers.Every(1000, () =>
        {
            ps[1].room.Send("move", new Dictionary<string, object> { ["seq"] = 1, ["t"] = Math.Round(ps[1].platform.Now()), ["epoch"] = 0, ["x"] = 45.5, ["y"] = 0.0, ["z"] = 41.25, ["yaw"] = 0, ["anim"] = 0, ["car"] = 0, ["ride"] = "" });
        });
        Pump(ps, 2500);

        foreach (var p in ps)
        {
            var players = p.room.State?.Get("players") as SchemaMap;
            Check(players != null, $"{p.name}: state has a players map ({p.patches} patches)");
            if (players == null) continue;
            foreach (var key in players.keys)
            {
                var o = (SchemaObj)players.items[key];
                Console.WriteLine($"     {p.name} sees {key}{(key == p.room.sessionId ? " (me)" : "")}: name={o.Str("name")} x={o.Num("x"):0.00} y={o.Num("y"):0.00} z={o.Num("z"):0.00} rotY={o.Num("rotY")} anim={o.Num("anim")} car={o.Num("car")} ride='{o.Str("ride")}' look={o.Str("appearance")} away={o.Bool("away")} t={o.Num("t")}");
            }
            var other = p == ps[0] ? ps[1] : ps[0];
            Check(players.items.ContainsKey(other.room.sessionId), $"{p.name} sees {other.name}");
            Check(p.dots != null && p.dots.Count == 2, $"{p.name} dots: {(p.dots == null ? "none" : Json.Serialize(p.dots))}");
        }
        if (ps[1].room.State?.Get("players") is SchemaMap pm && pm.items.TryGetValue(ps[0].room.sessionId, out var ao))
        {
            double ax = ((SchemaObj)ao).Num("x");
            Check(Math.Abs(ax - x) < 1.0, $"b sees a walking (x {ax:0.00}, sent {x:0.00})");
            Check(((SchemaObj)ao).Str("name") == "Unity a", "a's name");
        }

        // Leave: b goes; a's map drops them.
        ps[0].timers.Cancel(tA);
        ps[1].timers.Cancel(tB);
        int code = -1;
        ps[1].room.Leave(k => code = k);
        Pump(ps, 3000, () => code != -1 && !(((SchemaMap)ps[0].room.State.Get("players")).items.ContainsKey(ps[1].room.sessionId)));
        Check(code == CloseCode.CONSENTED, $"b left (code {code})");
        Check(!((SchemaMap)ps[0].room.State.Get("players")).items.ContainsKey(ps[1].room.sessionId), "a no longer sees b");
        code = -1;
        ps[0].room.Leave(k => code = k);
        Pump(ps, 3000, () => code != -1);
        Check(code == CloseCode.CONSENTED, $"a left (code {code})");

        // Clean up the characters.
        foreach (var (p, id) in new[] { (ps[0], ids[0]), (ps[1], ids[1]) })
        {
            int st = -1;
            p.platform.Request("DELETE", $"{origin}/mp/characters/{id}", null, (s, _) => st = s);
            Pump(ps, 3000, () => st != -1);
        }

        Console.WriteLine(fails == 0 ? "net: all good" : $"net: {fails} failed");
        return fails == 0 ? 0 : 1;
    }
}
