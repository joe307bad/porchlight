// The sidebar's Manage link: managing Porchlight (manage/, a React app built
// into StreamingAssets/manage, so every build of the game carries it), for
// now its traffic view: every car, townsperson and player in the county, live.
//
// On the web it's the site's own page, /manage/, opened in a new tab: Caddy
// serves it from the game's build, and it signs in with the same cookie.
//
// The desktop app has no browser cookies to share, so it serves the page
// itself (ManageHost): on a one-off port on 127.0.0.1, the files from its
// StreamingAssets, and the three things the page asks the server for (who's
// signed in, the county map and the live feed) passed on to the server with
// the game's own session, read-only. The system browser opens it there.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Porchlight.App
{
    public static class ManageLink
    {
        public const string PATH = "/manage/";

        /// <summary>Open Manage Porchlight.</summary>
        public static void Open()
        {
            var p = Platform.Instance;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!p.OpenTab(PATH)) Debug.LogWarning("porchlight: the browser didn't open Manage (a pop-up blocker?)");
#else
            string cookie = Platform.Cookie;
            if (cookie == null)
            {
                // Not signed in here: the web version's, which signs in itself.
                p.OpenTab(PATH);
                return;
            }
            try
            {
                Application.OpenURL(ManageHost.Start(p.Origin, cookie, Path.Combine(Application.streamingAssetsPath, "manage")));
            }
            catch (Exception e)
            {
                Debug.LogWarning("porchlight: couldn't serve Manage here, so it's the web's: " + e.Message);
                p.OpenTab(PATH);
            }
#endif
        }
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    /// <summary>
    /// The desktop app's Manage page, at http://127.0.0.1:port/manage/. Only GETs, only of the page's
    /// own files and the three server routes it uses, and only asked for by that address (a page
    /// elsewhere rebinding its own name to 127.0.0.1 is turned away by the Host it sends). One request a
    /// connection, each on the thread pool, so the feed's stream never waits on the game's frames.
    /// </summary>
    sealed class ManageHost
    {
        static ManageHost running;
        static readonly HttpClient http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>What the page may ask the server for, passed on as it is.</summary>
        static readonly HashSet<string> PASSED = new HashSet<string> { "/auth/me", "/mp/traffic/county", "/mp/traffic/feed" };

        static readonly Dictionary<string, string> TYPES = new Dictionary<string, string>
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".ico"] = "image/x-icon",
            [".woff2"] = "font/woff2",
        };

        readonly TcpListener listener;
        readonly int port;
        readonly string root;
        readonly Uri origin;
        /// <summary>The game's session, sent on to the server (it can change: signing in again).</summary>
        volatile string cookie;

        ManageHost(string origin, string root)
        {
            this.origin = new Uri(origin);
            this.root = Path.GetFullPath(root);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        /// <summary>Serve Manage (once; later calls bring its session up to date). Its address.</summary>
        public static string Start(string origin, string cookie, string root)
        {
            if (running == null || running.origin != new Uri(origin)) running = new ManageHost(origin, root);
            running.cookie = cookie;
            return $"http://127.0.0.1:{running.port}{ManageLink.PATH}";
        }

        async Task Accept()
        {
            for (;;)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var (method, target, headers) = await ReadRequest(stream).ConfigureAwait(false);
                    if (method == null) return;
                    if (!headers.TryGetValue("host", out var host) || host != $"127.0.0.1:{port}")
                    {
                        await Reply(stream, 403, "text/plain", "Not here.").ConfigureAwait(false);
                        return;
                    }
                    if (method != "GET" && method != "HEAD")
                    {
                        await Reply(stream, 405, "text/plain", "Only GET.").ConfigureAwait(false);
                        return;
                    }
                    int q = target.IndexOf('?');
                    string path = q < 0 ? target : target.Substring(0, q);
                    if (path == "/" || path == "/manage") await Redirect(stream, ManageLink.PATH).ConfigureAwait(false);
                    else if (path.StartsWith(ManageLink.PATH, StringComparison.Ordinal)) await ServeFile(stream, path.Substring(ManageLink.PATH.Length), method == "HEAD").ConfigureAwait(false);
                    else if (PASSED.Contains(path)) await Pass(stream, target, headers).ConfigureAwait(false);
                    else await Reply(stream, 404, "text/plain", "Not found.").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The browser went (closed the tab, or the feed with it): nothing to tell anyone.
                }
            }
        }

        /// <summary>The request line and headers (lower-cased names); a null method if it isn't one.</summary>
        static async Task<(string method, string target, Dictionary<string, string> headers)> ReadRequest(NetworkStream stream)
        {
            var buf = new byte[16 * 1024];
            int have = 0;
            using (var timeout = new CancellationTokenSource(10_000))
            {
                for (;;)
                {
                    if (have == buf.Length) return (null, null, null);
                    int n = await stream.ReadAsync(buf, have, buf.Length - have, timeout.Token).ConfigureAwait(false);
                    if (n <= 0) return (null, null, null);
                    have += n;
                    string text = Encoding.ASCII.GetString(buf, 0, have);
                    int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    var lines = text.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    if (first.Length != 3 || !first[1].StartsWith("/", StringComparison.Ordinal)) return (null, null, null);
                    var headers = new Dictionary<string, string>();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
                    }
                    return (first[0], first[1], headers);
                }
            }
        }

        static Task Head(NetworkStream stream, int status, string reason, string type, long length, string extra = "")
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            if (type != null) head.Append("Content-Type: ").Append(type).Append("\r\n");
            if (length >= 0) head.Append("Content-Length: ").Append(length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
            head.Append(extra).Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        static async Task Reply(NetworkStream stream, int status, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await Head(stream, status, status == 200 ? "OK" : "Error", type + "; charset=utf-8", bytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        static Task Redirect(NetworkStream stream, string to) => Head(stream, 302, "Found", null, 0, $"Location: {to}\r\n");

        /// <summary>One of the page's files; anything that isn't one is the page itself (it has one route).</summary>
        async Task ServeFile(NetworkStream stream, string rel, bool headOnly)
        {
            rel = Uri.UnescapeDataString(rel).Replace('\\', '/');
            string full = Path.GetFullPath(Path.Combine(root, rel.Length == 0 ? "index.html" : rel));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !System.IO.File.Exists(full))
            {
                full = Path.Combine(root, "index.html");
                if (!System.IO.File.Exists(full))
                {
                    await Reply(stream, 404, "text/plain", "Manage isn't in this build (npm run build in manage/ puts it in StreamingAssets).").ConfigureAwait(false);
                    return;
                }
            }
            var bytes = System.IO.File.ReadAllBytes(full);
            TYPES.TryGetValue(Path.GetExtension(full).ToLowerInvariant(), out var type);
            await Head(stream, 200, "OK", type ?? "application/octet-stream", bytes.Length).ConfigureAwait(false);
            if (!headOnly) await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        /// <summary>Passed on to the server with the game's session, and its answer streamed back as it comes (the feed never ends).</summary>
        async Task Pass(NetworkStream stream, string target, Dictionary<string, string> headers)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, target)))
            {
                string c = cookie;
                if (!string.IsNullOrEmpty(c)) req.Headers.TryAddWithoutValidation("Cookie", c);
                req.Headers.TryAddWithoutValidation("Accept", headers.TryGetValue("accept", out var accept) ? accept : "*/*");
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    string type = res.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                    long length = res.Content.Headers.ContentLength ?? -1;
                    await Head(stream, (int)res.StatusCode, res.ReasonPhrase ?? "", type, length).ConfigureAwait(false);
                    using (var body = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var buf = new byte[32 * 1024];
                        int n;
                        while ((n = await body.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await stream.WriteAsync(buf, 0, n).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }
#endif
}
