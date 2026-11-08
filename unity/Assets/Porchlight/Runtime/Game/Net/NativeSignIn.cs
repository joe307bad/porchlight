// Signing in from the desktop app, which has no browser cookies of its own: the
// system browser does the Google sign-in, as native apps do (RFC 8252), and
// comes back to a one-off address on this machine (127.0.0.1:<port>) with a
// one-time code. The code, with a verifier only this app ever had, buys the
// session from the auth service (server/src/app.ts, POST /auth/native), and
// the game sends that as its cookie from then on. It's kept in a file in the
// player's own data folder, readable by them alone, as the browser keeps its
// cookie in the profile.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Porchlight.App
{
    public static class NativeSignIn
    {
        /// <summary>How long to wait for the browser to come back.</summary>
        const int WAIT_MS = 10 * 60 * 1000;

        /// <summary>How the browser is sent (the smoke run fetches the address instead).</summary>
        public static Action<string> Open = Application.OpenURL;

        static string session;
        static bool loaded;
        static TcpListener listening;

        static string FilePath => Path.Combine(Application.persistentDataPath, "session");

        /// <summary>The session kept from last time, or null.</summary>
        public static string Session
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    try
                    {
                        if (File.Exists(FilePath)) session = File.ReadAllText(FilePath).Trim();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("porchlight: couldn't read the session: " + e.Message);
                    }
                }
                return string.IsNullOrEmpty(session) ? null : session;
            }
        }

        public static void Forget()
        {
            session = null;
            loaded = true;
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("porchlight: couldn't forget the session: " + e.Message);
            }
        }

        static void Keep(string value)
        {
            session = value;
            loaded = true;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, value);
            // Only the player may read it (macOS); Windows keeps a user's AppData theirs already.
            if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
            {
                try { System.Diagnostics.Process.Start("/bin/chmod", "600 \"" + FilePath + "\"")?.WaitForExit(2000); }
                catch (Exception) { }
            }
        }

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        [Serializable]
        class Traded
        {
            public string session;
            public string error;
        }

        /// <summary>Off to the browser to sign in; `done` gets null once there's a session, or what went wrong.</summary>
        public static async void Begin(string origin, Action<string> done)
        {
            // A second press: the first one's wait is over.
            try { listening?.Stop(); } catch (Exception) { }
            var listener = listening = new TcpListener(IPAddress.Loopback, 0);
            string verifier, challenge;
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[32];
                rng.GetBytes(bytes);
                verifier = Base64Url(bytes);
            }
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            string query;
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Open($"{origin}/auth/login?native={port}&challenge={challenge}");
                query = await Answer(listener);
            }
            catch (Exception e)
            {
                if (listening != listener) return;
                Debug.LogWarning("porchlight: sign-in: " + e);
                done("Sign-in didn’t finish. Please try again.");
                return;
            }
            finally
            {
                try { listener.Stop(); } catch (Exception) { }
            }
            if (listening != listener) return;
            listening = null;
            if (query == null)
            {
                done("Sign-in took too long. Please try again.");
                return;
            }
            var got = Parse(query);
            if (got.error != null)
            {
                done(got.error);
                return;
            }
            string json = "{\"code\":\"" + got.code + "\",\"verifier\":\"" + verifier + "\"}";
            Platform.Instance.Request("POST", origin + "/auth/native", json, (status, text) =>
            {
                Traded t = null;
                try { t = JsonUtility.FromJson<Traded>(text); } catch (Exception) { }
                if (status == 200 && !string.IsNullOrEmpty(t?.session))
                {
                    try { Keep(t.session); }
                    catch (Exception e)
                    {
                        done("Couldn’t save the sign-in: " + e.Message);
                        return;
                    }
                    done(null);
                }
                else done(t?.error ?? (status == 0 ? "The game server isn’t answering." : "Sign-in failed. Please try again."));
            });
        }

        static (string code, string error) Parse(string query)
        {
            string code = null, error = null;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part.Substring(0, eq), v = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                if (k == "code") code = v;
                else if (k == "error") error = v;
            }
            if (error == null && string.IsNullOrEmpty(code)) error = "Sign-in was interrupted. Please try again.";
            // Only ever the characters the server makes codes from, as it goes into JSON.
            if (code != null && !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z0-9_-]+$")) error = "Sign-in was interrupted. Please try again.";
            return (code, error);
        }

        /// <summary>The query the browser comes back with (answering it with a page saying so), or null if it never does.</summary>
        static async Task<string> Answer(TcpListener listener)
        {
            var found = new TaskCompletionSource<string>();
            var timeout = Task.Delay(WAIT_MS);
            _ = Task.Run(async () =>
            {
                // Browsers may open a connection or two ahead and send nothing on them: each is read on its own.
                while (!found.Task.IsCompleted)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { found.TrySetResult(null); return; }
                    _ = Task.Run(() => Serve(client, found));
                }
            });
            var first = await Task.WhenAny(found.Task, timeout);
            return first == found.Task ? found.Task.Result : null;
        }

        static async Task Serve(TcpClient client, TaskCompletionSource<string> found)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var line = await reader.ReadLineAsync();
                    if (line == null) return;
                    // "GET /?code=… HTTP/1.1"
                    var parts = line.Split(' ');
                    string target = parts.Length > 1 ? parts[1] : "/";
                    int q = target.IndexOf('?');
                    bool ours = parts[0] == "GET" && q >= 0 && (target.Contains("code=") || target.Contains("error="));
                    string said = target.Contains("error=") ? "Sign-in didn’t finish. Porchlight will say why" : "You’re signed in";
                    string body = ours
                        ? "<!doctype html><meta charset=utf-8><title>Porchlight</title><body style=\"font:16px system-ui;background:#1f1e1b;color:#fff;display:grid;place-items:center;height:90vh\"><p>" + said + ". You can close this tab.</p>"
                        : "";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                    await stream.WriteAsync(head, 0, head.Length);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                    if (ours) found.TrySetResult(target.Substring(q));
                }
                catch (Exception)
                {
                    // A connection that went nowhere: there may be another.
                }
            }
        }
    }
}
