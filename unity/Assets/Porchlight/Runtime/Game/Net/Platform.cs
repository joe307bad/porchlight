// What the game needs from wherever it runs: sockets, HTTP, a clock, and in a
// browser the page itself (its URL, storage, downloads). The WebGL build goes
// through Plugins/WebGL/Porchlight.jslib; the editor, desktop and consoles use
// .NET's ClientWebSocket and Unity's own storage. Socket events are queued and
// handed over in Pump(), on the game's thread.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Porchlight.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace Porchlight.App
{
    public sealed class Platform : INetPlatform
    {
        public static readonly Platform Instance = new Platform();

        readonly List<IPumped> sockets = new List<IPumped>();

        interface IPumped
        {
            /// <summary>Deliver what arrived; false once the socket is done.</summary>
            bool Pump();
        }

        public double Now() => Time.realtimeSinceStartupAsDouble * 1000;

        public IWebSocket NewSocket()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var s = new BrowserSocket();
#else
            var s = new DotNetSocket();
#endif
            sockets.Add(s);
            return s;
        }

        /// <summary>Call once a frame.</summary>
        public void Pump()
        {
            for (int i = sockets.Count - 1; i >= 0; i--)
                if (i < sockets.Count && !sockets[i].Pump()) sockets.RemoveAt(i);
        }

        public void Request(string method, string url, string json, Action<int, string> done)
        {
            var req = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer() };
            if (json != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.SetRequestHeader("Accept", "application/json");
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                int status = req.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)req.responseCode;
                string body = req.downloadHandler?.text ?? "";
                req.Dispose();
                done(status, body);
            };
        }

        /// <summary>The page's address, or a configured one off the web (PORCHLIGHT_ORIGIN, else localhost:3017).</summary>
        public string Href
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return PL_Location();
#else
                return Environment.GetEnvironmentVariable("PORCHLIGHT_ORIGIN") ?? "http://localhost:3017/";
#endif
            }
        }

        public string Origin
        {
            get
            {
                var u = new Uri(Href);
                return u.GetLeftPart(UriPartial.Authority);
            }
        }

        public string Hash
        {
            get
            {
                string h = Href;
                int i = h.IndexOf('#');
                return i < 0 ? "" : h.Substring(i);
            }
        }

        public string Search
        {
            get
            {
                string h = Href;
                int hash = h.IndexOf('#');
                if (hash >= 0) h = h.Substring(0, hash);
                int q = h.IndexOf('?');
                return q < 0 ? "" : h.Substring(q);
            }
        }

        public string PathName => new Uri(Href).AbsolutePath;

        public void SetHash(string hash)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PL_SetHash(hash);
#else
            localHash = hash;
#endif
        }

#pragma warning disable CS0414
        string localHash = "";
#pragma warning restore CS0414

        public void ReplaceUrl(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PL_ReplaceUrl(url);
#endif
        }

        public void Navigate(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PL_Navigate(url);
#else
            Application.OpenURL(url.StartsWith("/") ? Origin + url : url);
#endif
        }

        public void PostForm(string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PL_PostForm(url);
#else
            Request("POST", Origin + url, null, (_, __) => { });
#endif
        }

        public string Load(string key, bool session = false)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return PL_StorageGet(session ? 1 : 0, key);
#else
            if (session) return sessionStore.TryGetValue(key, out var v) ? v : null;
            return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
#endif
        }

        public void Save(string key, string value, bool session = false)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PL_StorageSet(session ? 1 : 0, key, value);
#else
            if (session)
            {
                if (value == null) sessionStore.Remove(key);
                else sessionStore[key] = value;
                return;
            }
            if (value == null) PlayerPrefs.DeleteKey(key);
            else PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
#endif
        }

#pragma warning disable CS0414
        readonly Dictionary<string, string> sessionStore = new Dictionary<string, string>();
#pragma warning restore CS0414

        public void Download(byte[] data, string name, string type)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { PL_Download(h.AddrOfPinnedObject(), data.Length, name, type); }
            finally { h.Free(); }
#else
            string path = System.IO.Path.Combine(Application.persistentDataPath, name);
            System.IO.File.WriteAllBytes(path, data);
            Debug.Log("Saved " + path);
#endif
        }

        public bool Hidden
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return PL_Hidden() != 0;
#else
                return false;
#endif
            }
        }

        public bool Confirm(string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return PL_Confirm(text) != 0;
#else
            return true;
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int PL_WsOpen(string url);
        [DllImport("__Internal")] static extern int PL_WsState(int id);
        [DllImport("__Internal")] static extern int PL_WsCloseCode(int id);
        [DllImport("__Internal")] static extern int PL_WsPeek(int id);
        [DllImport("__Internal")] static extern int PL_WsRead(int id, IntPtr ptr);
        [DllImport("__Internal")] static extern void PL_WsSend(int id, IntPtr ptr, int length);
        [DllImport("__Internal")] static extern void PL_WsClose(int id, int code);
        [DllImport("__Internal")] static extern void PL_WsFree(int id);
        [DllImport("__Internal")] static extern string PL_Location();
        [DllImport("__Internal")] static extern void PL_SetHash(string hash);
        [DllImport("__Internal")] static extern void PL_ReplaceUrl(string url);
        [DllImport("__Internal")] static extern void PL_Navigate(string url);
        [DllImport("__Internal")] static extern void PL_PostForm(string url);
        [DllImport("__Internal")] static extern string PL_StorageGet(int session, string key);
        [DllImport("__Internal")] static extern void PL_StorageSet(int session, string key, string value);
        [DllImport("__Internal")] static extern void PL_Download(IntPtr ptr, int length, string name, string type);
        [DllImport("__Internal")] static extern int PL_Hidden();
        [DllImport("__Internal")] static extern int PL_Confirm(string text);

        sealed class BrowserSocket : IWebSocket, IPumped
        {
            int id;
            bool opened, closed;
            public Action OnOpen { get; set; }
            public Action<byte[]> OnMessage { get; set; }
            public Action<int, string> OnClose { get; set; }

            public void Connect(string url) => id = PL_WsOpen(url);

            public void Send(byte[] data)
            {
                var h = GCHandle.Alloc(data, GCHandleType.Pinned);
                try { PL_WsSend(id, h.AddrOfPinnedObject(), data.Length); }
                finally { h.Free(); }
            }

            public void Close(int code) => PL_WsClose(id, code);

            public bool Pump()
            {
                if (closed || id == 0) return !closed;
                int state = PL_WsState(id);
                if (state >= 1 && !opened)
                {
                    opened = true;
                    if (state == 1) OnOpen?.Invoke();
                }
                int n;
                while ((n = PL_WsPeek(id)) >= 0)
                {
                    var buf = new byte[n];
                    var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    try { PL_WsRead(id, h.AddrOfPinnedObject()); }
                    finally { h.Free(); }
                    OnMessage?.Invoke(buf);
                    if (closed) return false;
                }
                if (PL_WsState(id) == 3)
                {
                    closed = true;
                    int code = PL_WsCloseCode(id);
                    PL_WsFree(id);
                    OnClose?.Invoke(code, "");
                    return false;
                }
                return true;
            }
        }
#else
        sealed class DotNetSocket : IWebSocket, IPumped
        {
            readonly System.Net.WebSockets.ClientWebSocket ws = new System.Net.WebSockets.ClientWebSocket();
            readonly ConcurrentQueue<byte[]> inbox = new ConcurrentQueue<byte[]>();
            readonly System.Threading.SemaphoreSlim sending = new System.Threading.SemaphoreSlim(1, 1);
            volatile bool opened, ended;
            volatile int code = 1006;
            bool openFired, closeFired;
            public Action OnOpen { get; set; }
            public Action<byte[]> OnMessage { get; set; }
            public Action<int, string> OnClose { get; set; }

            public async void Connect(string url)
            {
                try
                {
                    await ws.ConnectAsync(new Uri(url), System.Threading.CancellationToken.None);
                    opened = true;
                    var buf = new byte[1 << 16];
                    var msg = new System.IO.MemoryStream();
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), System.Threading.CancellationToken.None);
                        if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            code = (int?)r.CloseStatus ?? 1005;
                            break;
                        }
                        msg.Write(buf, 0, r.Count);
                        if (r.EndOfMessage)
                        {
                            inbox.Enqueue(msg.ToArray());
                            msg.SetLength(0);
                        }
                    }
                    if (ws.CloseStatus != null) code = (int)ws.CloseStatus.Value;
                }
                catch (Exception)
                {
                    if (ws.CloseStatus != null) code = (int)ws.CloseStatus.Value;
                }
                ended = true;
            }

            public async void Send(byte[] data)
            {
                if (!opened || ended) return;
                await sending.WaitAsync();
                try { await ws.SendAsync(new ArraySegment<byte>(data), System.Net.WebSockets.WebSocketMessageType.Binary, true, System.Threading.CancellationToken.None); }
                catch (Exception) { }
                finally { sending.Release(); }
            }

            public async void Close(int c)
            {
                try { await ws.CloseOutputAsync((System.Net.WebSockets.WebSocketCloseStatus)c, "", System.Threading.CancellationToken.None); }
                catch (Exception) { }
            }

            public bool Pump()
            {
                if (closeFired) return false;
                if (opened && !openFired)
                {
                    openFired = true;
                    OnOpen?.Invoke();
                }
                while (inbox.TryDequeue(out var m)) OnMessage?.Invoke(m);
                if (ended && inbox.IsEmpty)
                {
                    closeFired = true;
                    OnClose?.Invoke(code, "");
                    ws.Dispose();
                    return false;
                }
                return true;
            }
        }
#endif
    }
}
