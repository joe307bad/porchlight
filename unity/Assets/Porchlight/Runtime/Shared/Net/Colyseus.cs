// The parts of @colyseus/sdk (0.17) this game uses: matchmake over HTTP, join
// a room over a WebSocket, decode its schema state, send and receive messages,
// leave, and reconnect after a drop (the SDK's backoff). The platform supplies
// sockets, HTTP and a clock; everything here runs on the game's thread (the
// platform queues socket events and Timers.Tick fires timeouts).

using System;
using System.Collections.Generic;
using System.Text;
using Json = Porchlight.Core.Json;

namespace Porchlight.Net
{
    public interface IWebSocket
    {
        void Connect(string url);
        void Send(byte[] data);
        void Close(int code);
        Action OnOpen { get; set; }
        Action<byte[]> OnMessage { get; set; }
        Action<int, string> OnClose { get; set; }
    }

    public interface INetPlatform
    {
        IWebSocket NewSocket();
        /// <summary>An HTTP request with a JSON body (or none); done(status, body), status 0 on a network failure.</summary>
        void Request(string method, string url, string json, Action<int, string> done);
        /// <summary>Milliseconds on a steady clock (performance.now()).</summary>
        double Now();
    }

    /// <summary>setTimeout / setInterval, fired from the game loop.</summary>
    public sealed class Timers
    {
        sealed class Timer
        {
            public double at, every;
            public Action fn;
            public bool dead;
        }

        readonly List<Timer> list = new List<Timer>();
        readonly Func<double> now;

        public Timers(Func<double> now) => this.now = now;

        public object After(double ms, Action fn)
        {
            var t = new Timer { at = now() + ms, fn = fn };
            list.Add(t);
            return t;
        }

        public object Every(double ms, Action fn)
        {
            var t = new Timer { at = now() + ms, every = ms, fn = fn };
            list.Add(t);
            return t;
        }

        public void Cancel(object handle)
        {
            if (handle is Timer t)
            {
                t.dead = true;
                list.Remove(t);
            }
        }

        public void Tick()
        {
            double n = now();
            var due = new List<Timer>();
            foreach (var t in list) if (!t.dead && t.at <= n) due.Add(t);
            foreach (var t in due)
            {
                if (t.dead) continue;
                if (t.every > 0) t.at = Math.Max(t.at + t.every, n);
                else
                {
                    t.dead = true;
                    list.Remove(t);
                }
                t.fn();
            }
        }
    }

    public static class CloseCode
    {
        public const int NORMAL_CLOSURE = 1000;
        public const int GOING_AWAY = 1001;
        public const int NO_STATUS_RECEIVED = 1005;
        public const int ABNORMAL_CLOSURE = 1006;
        public const int CONSENTED = 4000;
        public const int FAILED_TO_RECONNECT = 4003;
        public const int MAY_TRY_RECONNECT = 4010;
    }

    static class Wire
    {
        public const byte JOIN_ROOM = 10;
        public const byte ERROR = 11;
        public const byte LEAVE_ROOM = 12;
        public const byte ROOM_DATA = 13;
        public const byte ROOM_STATE = 14;
        public const byte ROOM_STATE_PATCH = 15;
        public const byte ROOM_DATA_BYTES = 17;
        public const byte PING = 18;
    }

    public sealed class ColyseusClient
    {
        readonly string http;
        readonly string ws;
        readonly INetPlatform platform;
        public readonly Timers timers;

        /// <summary>The server's base URL, e.g. https://host/mp.</summary>
        public ColyseusClient(string endpoint, INetPlatform platform, Timers timers)
        {
            http = endpoint.TrimEnd('/');
            ws = http.StartsWith("https:") ? "wss:" + http.Substring(6) : http.StartsWith("http:") ? "ws:" + http.Substring(5) : http;
            this.platform = platform;
            this.timers = timers;
        }

        /// <summary>Joins (or creates) a room; done(room, null) or done(null, error message).</summary>
        public void JoinOrCreate(string roomName, Dictionary<string, object> options, Action<Room, string> done)
        {
            platform.Request("POST", $"{http}/matchmake/joinOrCreate/{roomName}", Json.Serialize(options), (status, body) =>
            {
                Dictionary<string, object> res = null;
                try { res = Json.Parse(body) as Dictionary<string, object>; }
                catch (Exception) { }
                if (status < 200 || status >= 300 || res == null || !res.ContainsKey("roomId"))
                {
                    string why = res != null && res.TryGetValue("error", out var e) && e is string es ? es
                        : status == 0 ? "Failed to fetch" : $"HTTP {status}";
                    done(null, why);
                    return;
                }
                var room = new Room(res["name"] as string ?? roomName, platform, timers)
                {
                    roomId = res["roomId"] as string,
                    sessionId = res["sessionId"] as string,
                };
                string address = res.TryGetValue("publicAddress", out var pa) && pa is string ps && ps.Length > 0
                    ? (ws.StartsWith("wss:") ? "wss://" : "ws://") + ps
                    : ws;
                string url = $"{address}/{res["processId"]}/{room.roomId}?sessionId={room.sessionId}";
                bool settled = false;
                room.onError = (code, message) =>
                {
                    if (settled) return;
                    settled = true;
                    done(null, message ?? $"closed ({code})");
                };
                room.onJoin = () =>
                {
                    if (settled) return;
                    settled = true;
                    room.onError = null;
                    done(room, null);
                };
                room.Connect(url);
            });
        }
    }

    public sealed class Room
    {
        public readonly string name;
        public string roomId;
        public string sessionId;
        public string reconnectionToken;
        public SchemaDecoder decoder;
        public SchemaObj State => decoder?.state;

        public Action onJoin;
        public Action<int, string> onError;
        public Action<int> onLeave;
        public Action onDrop;
        public Action onReconnect;
        public Action<SchemaObj> onStateChange;
        readonly Dictionary<string, Action<object>> handlers = new Dictionary<string, Action<object>>();

        readonly INetPlatform platform;
        readonly Timers timers;
        IWebSocket socket;
        string url;
        double joinedAt;
        bool open;
        bool left;
        bool reconnecting;
        int retryCount;
        readonly List<byte[]> enqueued = new List<byte[]>();
        Action<int> leaveWaiter;

        const int MAX_RETRIES = 15;
        const double MIN_UPTIME = 5000;

        public Room(string name, INetPlatform platform, Timers timers)
        {
            this.name = name;
            this.platform = platform;
            this.timers = timers;
        }

        public void OnMessage(string type, Action<object> fn) => handlers[type] = fn;

        public void Connect(string endpoint, bool skipHandshake = false)
        {
            url = endpoint;
            Open(skipHandshake ? endpoint + "&skipHandshake=1" : endpoint);
        }

        void Open(string to)
        {
            var s = platform.NewSocket();
            socket = s;
            s.OnOpen = () => { if (socket == s) open = true; };
            s.OnMessage = data => { if (socket == s) Receive(data); };
            s.OnClose = (code, reason) => { if (socket == s) Closed(code, reason); };
            s.Connect(to);
        }

        void Closed(int code, string reason)
        {
            open = false;
            if (left) return;
            if (joinedAt == 0)
            {
                onError?.Invoke(code, reason);
                return;
            }
            if (code == CloseCode.NO_STATUS_RECEIVED || code == CloseCode.ABNORMAL_CLOSURE || code == CloseCode.GOING_AWAY || code == CloseCode.MAY_TRY_RECONNECT)
            {
                onDrop?.Invoke();
                HandleReconnection(code);
            }
            else Finish(code);
        }

        void Finish(int code)
        {
            if (left) return;
            left = true;
            socket = null;
            onLeave?.Invoke(code);
            leaveWaiter?.Invoke(code);
            handlers.Clear();
            onLeave = null;
            onDrop = null;
            onReconnect = null;
            onStateChange = null;
        }

        void HandleReconnection(int code)
        {
            if (platform.Now() - joinedAt < MIN_UPTIME)
            {
                Finish(CloseCode.ABNORMAL_CLOSURE);
                return;
            }
            if (!reconnecting)
            {
                retryCount = 0;
                reconnecting = true;
            }
            RetryReconnection();
        }

        void RetryReconnection()
        {
            if (retryCount >= MAX_RETRIES)
            {
                reconnecting = false;
                Finish(CloseCode.FAILED_TO_RECONNECT);
                return;
            }
            retryCount++;
            double delay = Math.Min(5000, Math.Max(100, Math.Floor(Math.Pow(2, retryCount) * 100)));
            timers.After(delay, () =>
            {
                if (left) return;
                string token = reconnectionToken?.Split(':')[1] ?? "";
                Open($"{url}&reconnectionToken={token}&skipHandshake=1");
            });
        }

        void Receive(byte[] b)
        {
            if (b.Length == 0) return;
            int i = 1;
            byte code = b[0];
            switch (code)
            {
                case Wire.JOIN_ROOM:
                {
                    int n = b[i++];
                    string token = Encoding.UTF8.GetString(b, i, n);
                    i += n;
                    n = b[i++];
                    i += n; // serializer id: always "schema" here
                    if (b.Length > i && decoder == null) decoder = Reflection.Decode(b, i);
                    if (joinedAt == 0)
                    {
                        joinedAt = platform.Now();
                        onJoin?.Invoke();
                    }
                    else
                    {
                        reconnecting = false;
                        onReconnect?.Invoke();
                    }
                    reconnectionToken = $"{roomId}:{token}";
                    socket?.Send(new[] { Wire.JOIN_ROOM });
                    foreach (var m in enqueued) socket?.Send(m);
                    enqueued.Clear();
                    break;
                }
                case Wire.ERROR:
                {
                    int ec = (int)SchemaWire.Number(b, ref i);
                    string message = SchemaWire.String(b, ref i);
                    onError?.Invoke(ec, message);
                    break;
                }
                case Wire.LEAVE_ROOM:
                    Leave();
                    break;
                case Wire.ROOM_STATE:
                case Wire.ROOM_STATE_PATCH:
                    if (decoder == null) break;
                    decoder.Decode(b, i);
                    onStateChange?.Invoke(decoder.state);
                    break;
                case Wire.ROOM_DATA:
                {
                    string type = SchemaWire.StringCheck(b, i) ? SchemaWire.String(b, ref i) : "i" + SchemaWire.Number(b, ref i);
                    object message = b.Length > i ? MsgPack.Read(b, ref i) : null;
                    if (handlers.TryGetValue(type, out var fn)) fn(message);
                    break;
                }
            }
        }

        public void Send(string type, object message)
        {
            var o = new List<byte> { Wire.ROOM_DATA };
            SchemaWire.WriteString(o, type);
            if (message != null) MsgPack.Write(o, message);
            var data = o.ToArray();
            if (open && !reconnecting) socket.Send(data);
            else if (reconnecting)
            {
                enqueued.Add(data);
                if (enqueued.Count > 10) enqueued.RemoveAt(0);
            }
        }

        /// <summary>Leave the room (consented); done(code) when it's closed.</summary>
        public void Leave(Action<int> done = null)
        {
            if (left)
            {
                done?.Invoke(CloseCode.CONSENTED);
                return;
            }
            leaveWaiter = done;
            if (socket != null && open) socket.Send(new[] { Wire.LEAVE_ROOM });
            else
            {
                socket?.Close(CloseCode.NORMAL_CLOSURE);
                Finish(CloseCode.CONSENTED);
            }
        }
    }
}
