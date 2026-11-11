// The whole app. Signed in: choose who's playing and where they live (FrontDoor),
// and walk their town in 3D with everyone else in it, playing missions (ink
// stories the server hands out) and keeping what they earn with the account.
// With no sign-in service, dev mode, starting blank each time: grow a town from
// the seed in the URL, show its blueprint with the sidebar, and on Explore walk it alone.
// One of these sits in the only scene (Boot.unity).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Porchlight.Core;
using Porchlight.County;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;
using Porchlight.Net;
using Porchlight.Rendering;
using Porchlight.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Rect = UnityEngine.Rect;
using Space = Porchlight.County.Space;

namespace Porchlight.App
{
    public sealed class PorchlightApp : MonoBehaviour
    {
        const string DEBUG_KEY = "porchlight.debugPanel";

        readonly Platform platform = Platform.Instance;
        Timers timers;
        GameInput input;
        string origin;

        // UI
        PanelSettings panelSettings;
        UIDocument doc;
        VisualElement root;
        BlueprintView map;
        VisualElement isoHost;
        Sidebar sidebar;
        DebugPanel panel;
        Label hoverLabel, statusLabel;
        VisualElement footer;
        float dpr = 1;

        // The town
        string seed;
        GenParams @params;
        TownModel model;
        /// <summary>Until the user pans or zooms, keep the whole map fitted to the window.</summary>
        bool autoFit = true;
        string writtenHash;
        double hashCheckAt;

        // Explore mode
        Camera camera3d;
        Camera clearCamera;
        ExploreView iso;
        ExploreOverlay overlay;
        bool exploring;
        TownModel isoTown;
        /// <summary>Explore mode is shared with everyone in the same town when signed in.</summary>
        bool online;
        User user;
        Multiplayer net;
        /// <summary>Signed in, the county round the town on screen: its highways out to the next towns (CountyDrive).</summary>
        CountyDrive county;
        /// <summary>What the room sees of the game, in its own terms (the county's spaces each have their own).</summary>
        NetFrame netFrame;
        /// <summary>Who's playing (signed in): chosen at the front door.</summary>
        Character playing;
        FrontDoor door;
        HousePicker housePicker;
        Journal journal;
        /// <summary>Come in at your own front door next time the server says you've joined, wherever you were last.</summary>
        bool preferHome;
        double lastFollow;
        /// <summary>The map keeps your character in view until you pan or zoom it yourself.</summary>
        bool followOnMap = true;
        bool started;
        /// <summary>The town behind the sign-in card: a random one each time the page loads, by day or night.</summary>
        ExploreView attract;
        /// <summary>The pictures in the boxes by the minimap, and the car model to bring round.</summary>
        Portraits portraits;
        int carModel;
        double playersAt;
        /// <summary>Who you've joined (by the button under your name), until they're found.</summary>
        string joining, joiningSession;
        double joiningUntil;
        /// <summary>The "Join" buttons showing, and when to look again.</summary>
        string joinsShown = "";
        double joinsAt;
        const double JOINS_MS = 5000;
        const double JOIN_FIND_MS = 30000;

        // Missions
        MissionPlay mission;
        MissionPanel missionPanel;
        List<MissionInfo> missions;
        /// <summary>Loading, or why there aren't any (null: the list is shown).</summary>
        string missionsStatus;
        bool missionsAsked;
        /// <summary>The mission being fetched to start (only the latest Start counts).</summary>
        string missionStarting;
        /// <summary>How often the account has finished each mission.</summary>
        Dictionary<string, int> finished = new Dictionary<string, int>();

        void Awake()
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 1;
            timers = new Timers(platform.Now);
            origin = platform.Origin;
            input = new GameInput();
            input.Typing = Typing;
            input.KeyDown += OnKeyDown;
#if UNITY_WEBGL && !UNITY_EDITOR
            dpr = Mathf.Max(1, PlatformDpr());
#elif !UNITY_EDITOR
            dpr = DesktopDpr();
            Debug.Log($"porchlight: UI scale {dpr} (screen {Screen.dpi:0} dpi)");
#endif
            BuildCameras();
            BuildUi();
            BeginSignIn();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern float PL_Dpr();
        static float PlatformDpr() => PL_Dpr();
#endif

        /// <summary>
        /// The desktop's pixels to one of the UI's, as a browser's devicePixelRatio: a Mac's
        /// Retina screen is 2 (it draws at its full resolution), Windows its scale setting
        /// (96 dpi is 100%). PORCHLIGHT_UI_SCALE says otherwise.
        /// </summary>
        static float DesktopDpr()
        {
            if (float.TryParse(Environment.GetEnvironmentVariable("PORCHLIGHT_UI_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var given) && given > 0) return given;
#if !UNITY_WEBGL && !UNITY_EDITOR
            // A Mac says exactly, for the screen the window's on now (it changes as the window moves).
            if (Application.platform == RuntimePlatform.OSXPlayer)
            {
                float backing = MacBackingScale();
                if (backing > 0) return backing;
            }
#endif
            float dpi = Screen.dpi;
            if (dpi <= 0) return 1;
            if (Application.platform == RuntimePlatform.OSXPlayer) return dpi > 150 ? 2 : 1;
            return Mathf.Max(1, Mathf.Round(dpi / 96f * 4) / 4);
        }

#if !UNITY_WEBGL && !UNITY_EDITOR
        const string OBJC = "/usr/lib/libobjc.A.dylib";
        [System.Runtime.InteropServices.DllImport(OBJC)] static extern IntPtr objc_getClass(string name);
        [System.Runtime.InteropServices.DllImport(OBJC)] static extern IntPtr sel_registerName(string name);
        [System.Runtime.InteropServices.DllImport(OBJC, EntryPoint = "objc_msgSend")] static extern IntPtr ObjcPointer(IntPtr self, IntPtr selector);
        [System.Runtime.InteropServices.DllImport(OBJC, EntryPoint = "objc_msgSend")] static extern double ObjcDouble(IntPtr self, IntPtr selector);

        /// <summary>The game window's pixels to a point (2 on a Retina screen, 1 on most others); 0 if there's no window to ask.</summary>
        static float MacBackingScale()
        {
            try
            {
                var app = ObjcPointer(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return 0;
                var window = ObjcPointer(app, sel_registerName("mainWindow"));
                if (window == IntPtr.Zero) window = ObjcPointer(app, sel_registerName("keyWindow"));
                var on = window != IntPtr.Zero ? window : ObjcPointer(objc_getClass("NSScreen"), sel_registerName("mainScreen"));
                return on == IntPtr.Zero ? 0 : (float)ObjcDouble(on, sel_registerName("backingScaleFactor"));
            }
            catch (Exception)
            {
                return 0;
            }
        }
#endif

        float dprCheckedAt;

        /// <summary>Moved to another screen (a Retina laptop's to a plain monitor, say): everything at that screen's scale.</summary>
        void FollowScreenScale()
        {
#if !UNITY_WEBGL && !UNITY_EDITOR
            if (Time.unscaledTime < dprCheckedAt + 0.5f) return;
            dprCheckedAt = Time.unscaledTime;
            float now = DesktopDpr();
            if (Mathf.Abs(now - dpr) < 0.01f) return;
            Debug.Log($"porchlight: UI scale {dpr} → {now}");
            dpr = now;
            if (panelSettings != null) panelSettings.scale = dpr;
            if (overlay != null) overlay.Dpr = dpr;
#endif
        }

        void BuildCameras()
        {
            // Clears the whole screen each frame (the map and sidebar are UI drawn over it).
            var c = new GameObject("Clear").AddComponent<Camera>();
            c.cullingMask = 0;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Coords.Hex(0x2b2a27);
            c.depth = -100;
            c.orthographic = true;
            clearCamera = c;
            camera3d = new GameObject("Explore Camera").AddComponent<Camera>();
            camera3d.clearFlags = CameraClearFlags.SolidColor;
            camera3d.tag = "MainCamera";
            camera3d.depth = 0;
            camera3d.allowMSAA = true;
            camera3d.enabled = false;
            camera3d.cullingMask = ~(1 << Portraits.Layer);
        }

        void BuildUi()
        {
            var template = Resources.Load<PanelSettings>("Porchlight/UI/Panel");
            panelSettings = Instantiate(template);
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            panelSettings.scale = dpr;
            doc = new GameObject("UI").AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;
            root = doc.rootVisualElement;
            root.styleSheets.Add(Resources.Load<StyleSheet>("Porchlight/UI/Porchlight"));
            root.AddToClassList("root");
            // Inter throughout (the stylesheet says so too; this holds wherever it isn't reached).
            root.style.unityFontDefinition = FontDefinition.FromFont(Resources.Load<Font>("Porchlight/Fonts/Inter-Regular"));
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;
            loader = new LoadBar(platform.Now, key => platform.Load(key), (key, value) => platform.Save(key, value));
            root.Add(loader.root);
            loader.Begin(LoadBar.FIRST, loader.PageShare());
        }

        // The loader: carries on from the page's, over everything, until there's a real screen
        // to show. Building a town stops everything for a moment, so it has to be drawn first.

        LoadBar loader;
        Action afterPaint;
        /// <summary>A town being built a part at a time: it isn't to be played (or updated) till it's done.</summary>
        bool buildingTown;
        readonly List<TaskCompletionSource<bool>> frameWaiters = new List<TaskCompletionSource<bool>>();
        /// <summary>How long a frame may spend building before the loader gets drawn again.</summary>
        const double FRAME_BUDGET_MS = 50;
        int paintFrames;
        /// <summary>Going into a town: the loader stays till they're in it and in their place.</summary>
        bool enteringTown;
        double enteringUntil;
        const double ENTERING_MS = 5000;

        bool Loading => loader.Visible;

        /// <summary>The loader up for a load (`flow` names it, for its timings), or carrying on with the one showing.</summary>
        void ShowLoader(string flow, string what = null) => loader.Begin(flow, 0, what);

        /// <summary>`keep`: it got where it was going, so its timings are worth remembering.</summary>
        void HideLoader(bool keep = true)
        {
            loader.End(keep);
            enteringTown = false;
            enteringUntil = 0;
        }

        Task NextFrame()
        {
            var next = new TaskCompletionSource<bool>();
            frameWaiters.Add(next);
            return next.Task;
        }

        /// <summary>Do the steps; while the loader's up, drawing it between them whenever a frame's worth is gone.</summary>
        async Task Run(IEnumerable<string> steps, string prefix)
        {
            double until = platform.Now() + FRAME_BUDGET_MS;
            foreach (var step in steps)
            {
                loader.Step(prefix + step);
                if (!Loading || platform.Now() < until) continue;
                await NextFrame();
                until = platform.Now() + FRAME_BUDGET_MS;
            }
        }

        /// <summary>Do the steps now, all of them (timed, if the loader's up).</summary>
        void Drain(IEnumerable<string> steps, string prefix)
        {
            foreach (var step in steps) loader.Step(prefix + step);
        }

        /// <summary>Do something heavy once what's on screen now has been drawn.</summary>
        void AfterPaint(Action then)
        {
            afterPaint = then;
            paintFrames = 2;
        }

        void UpdateLoader()
        {
            if (frameWaiters.Count > 0)
            {
                var waiting = frameWaiters.ToArray();
                frameWaiters.Clear();
                foreach (var w in waiting) w.SetResult(true);
            }
            if (afterPaint != null && --paintFrames <= 0)
            {
                var then = afterPaint;
                afterPaint = null;
                then();
            }
            if (!Loading) return;
            loader.Update();
            if (root[root.childCount - 1] != loader.root) loader.root.BringToFront();
            if (!enteringTown || !exploring || buildingTown) return;
            double now = platform.Now();
            if (enteringUntil == 0) enteringUntil = now + ENTERING_MS;
            if ((net == null || net.Spawned) && joining == null || now > enteringUntil)
            {
                loader.Step("join");
                HideLoader();
            }
        }

        // Sign-in: nothing starts until someone is signed in, when the
        // page is served by the auth service.

        void BeginSignIn()
        {
            // Smoke run (PORCHLIGHT_SMOKE_SIGNIN=1, against a stand-in auth service): the desktop
            // sign-in with a fetch for a browser, then who that made us, then forgotten again.
            if (Platform.Desktop && Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_SIGNIN") == "1")
            {
                NativeSignIn.Open = url => platform.Request("GET", url, null, (st, _) => Debug.Log($"smoke: signin: the browser came back with {st}"));
                NativeSignIn.Begin(origin, problem =>
                {
                    Debug.Log($"smoke: signin: {problem ?? "signed in"}, cookie {(Platform.Cookie != null ? "kept" : "none")}");
                    Account.FetchMe(platform, origin, (u, _, err, status) =>
                    {
                        Debug.Log($"smoke: signin: /auth/me {status} as {u?.name ?? err}");
                        NativeSignIn.Forget();
                        Debug.Log($"smoke: signin: forgotten, cookie {(Platform.Cookie != null ? "kept" : "none")}");
                        Application.Quit(0);
                    });
                });
                return;
            }
            if (!Platform.CanSignIn)
            {
                // Off the web with no session handed over: play alone.
                Start2();
                return;
            }
            string error = null;
            var query = ParseQuery(platform.Search.TrimStart('?'));
            if (query.TryGetValue("auth_error", out var e))
            {
                error = e;
                query.Remove("auth_error");
                string rest = EncodeQuery(query);
                platform.ReplaceUrl(platform.PathName + (rest.Length > 0 ? "?" + rest : "") + platform.Hash);
            }
            AskWho(error, platform.Now() + SERVER_WAIT_MS);
        }

        const double SERVER_WAIT_MS = 15000, SERVER_RETRY_MS = 1000;

        void AskWho(string error, double giveUpAt)
        {
            Account.FetchMe(platform, origin, (u, provider, err, status) =>
            {
                loader.Step("signin");
                if (u != null)
                {
                    user = u;
                    Start2();
                    return;
                }
                // No sign-in service here at all (a static host, or dev.sh without Docker): play alone.
                if (status == 404 && error == null)
                {
                    Debug.Log("porchlight: no sign-in service (" + err + "); playing offline");
                    Start2();
                    return;
                }
                // There should be one but it isn't answering, which says nothing about who you
                // are: wait through a restart, then say so. Never carry on as if signed out.
                if ((status == 0 || status == 502 || status == 503 || status == 504) && error == null)
                {
                    if (platform.Now() < giveUpAt)
                    {
                        timers.After(SERVER_RETRY_MS, () => AskWho(null, giveUpAt));
                        return;
                    }
                    VisualElement down = null;
                    down = SignInGate.Show(root, null, "The game server isn’t answering.", () =>
                    {
                        down.RemoveFromHierarchy();
                        root.RemoveFromClassList("signed-out");
                        ShowLoader("retry");
                        AskWho(null, platform.Now() + SERVER_WAIT_MS);
                    }, "Try again");
                    HideLoader(false);
                    return;
                }
                // The card straight away; the scenery behind it can follow.
                var gate = SignInGate.Show(root, provider, error ?? err, () => SignIn(provider));
                // Not a first load to time: the next one will most likely be signed in.
                HideLoader(false);
                AfterPaint(StartAttract);
            });
        }

        /// <summary>Off to sign in: the page goes to the auth service; the desktop app sends the system browser, and starts again signed in.</summary>
        void SignIn(string provider)
        {
            if (!Platform.Desktop)
            {
                string returnTo = platform.PathName + platform.Search + platform.Hash;
                platform.Navigate("/auth/login?returnTo=" + Uri.EscapeDataString(returnTo));
                return;
            }
            NativeSignIn.Begin(origin, problem =>
            {
                if (problem == null && platform.Relaunch()) return;
                root.Q("signin")?.RemoveFromHierarchy();
                // Signed in, somewhere it can't start again (the editor): ask who, as a reload would.
                if (problem == null) AskWho(null, platform.Now() + SERVER_WAIT_MS);
                else SignInGate.Show(root, provider, problem, () => SignIn(provider));
            });
        }

        /// <summary>Signed out: the page goes via the auth service; the desktop app forgets its session and starts again.</summary>
        void SignOut()
        {
            if (!Platform.Desktop)
            {
                platform.PostForm("/auth/logout");
                return;
            }
            // Ends the sign-in at Keycloak too, then forgets it here, whatever the answer.
            platform.Request("POST", origin + "/auth/logout", null, (_, __) =>
            {
                NativeSignIn.Forget();
                if (!platform.Relaunch()) Application.Quit();
            });
        }

        /// <summary>Behind the sign-in card: a fresh little town, by day or night.</summary>
        void StartAttract()
        {
            try
            {
                var host = new VisualElement();
                host.style.display = DisplayStyle.None;
                root.Add(host);
                attract = new ExploreView(camera3d, ModelLibrary.Instance, new ExploreOverlay(host) { Dpr = dpr }, input) { Attract = true };
                // A little town, seen close: light enough for phones and weak machines, its
                // edge about 35 tiles out from the middle, past the ~24 the view's corners reach as it turns.
                var p = GenParams.Defaults;
                p.width = p.height = 96;
                attract.SetTown(Generator.Generate(RandomSeed(), GenParams.Normalize(p)));
                attract.SetNightNow(new Rng(RandomSeed()).Chance(0.5));
            }
            catch (Exception err)
            {
                // Only scenery: the card works without it.
                Debug.LogException(err);
                attract = null;
            }
        }

        void Start2()
        {
            online = user != null;
            BuildApp();
            started = true;
            if (Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE") == "1") _ = Smoke();
            var shot = Environment.GetEnvironmentVariable("PORCHLIGHT_SHOT");
            if (!string.IsNullOrEmpty(shot)) _ = Shot(shot);
        }

        /// <summary>
        /// A picture of a town for sharing (PORCHLIGHT_SHOT=file.png, PORCHLIGHT_SHOT_SEED
        /// optional): explore it at night, zoomed all the way out, settle, save 2400×1260, quit.
        /// </summary>
        async Task Shot(string path)
        {
            var s = Environment.GetEnvironmentVariable("PORCHLIGHT_SHOT_SEED");
            if (!string.IsNullOrEmpty(s)) Regenerate(s, @params);
            await Explore();
            iso.SetNightNow(true);
            iso.SetZoom(0);
            double until = platform.Now() + 6000;
            while (platform.Now() < until) await Task.Yield();
            var rt = new RenderTexture(2400, 1260, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            iso.Capture(rt);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = was;
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"shot: town {seed} saved to {path}");
            Application.Quit(0);
        }

        /// <summary>
        /// How long the county's towns take to build, a part at a time as CountyDrive builds them on the
        /// way (PORCHLIGHT_SMOKE_BUILD=1): each part's milliseconds (a long one is a dropped frame), and
        /// the frame rate as the town on screen is walked, dry and in the rain.
        /// </summary>
        async Task SmokeBuild()
        {
            var keys = new List<string> { TownKey };
            keys.AddRange(Porchlight.County.CountyMap.Cities.Select(c => c.key));
            foreach (var key in keys)
            {
                var st = ParseUrlState(key);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var town = Porchlight.Gen.Generator.Generate(st.seed, st.@params);
                var prepared = Porchlight.Rendering.TownWorld.Prepared.Of(town);
                double grow = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var world = new Porchlight.Rendering.TownWorld(town, iso.Library, stepwise: true, inCounty: true, prepared: prepared);
                var parts = new List<string> { $"begin {clock.Elapsed.TotalMilliseconds:0}" };
                double total = clock.Elapsed.TotalMilliseconds, worst = total;
                foreach (var part in world.Build())
                {
                    double ms = clock.Elapsed.TotalMilliseconds - total;
                    total += ms;
                    worst = Math.Max(worst, ms);
                    parts.Add($"{part} {ms:0}");
                    await Task.Yield();
                    clock.Restart();
                    total = 0;
                }
                Debug.Log($"smoke build: {st.seed} {town.width}x{town.height}: grown and prepared in {grow:0} ms (worker thread); built in {parts.Count} parts, the longest {worst:0} ms: {string.Join(", ", parts)}");
                world.Dispose();
            }
            iso?.SetAuto(true);
            foreach (var rain in new[] { false, true })
            {
                iso?.SetWeather(rain, false);
                for (int i = 0; i < 60; i++) await Task.Yield();
                double start = platform.Now(), longest = 0, last = start;
                int frames = 0;
                while (platform.Now() < start + 5000)
                {
                    await Task.Yield();
                    double now = platform.Now();
                    longest = Math.Max(longest, now - last);
                    last = now;
                    frames++;
                }
                Debug.Log($"smoke build: walking {(rain ? "in the rain" : "dry")}: {frames / ((platform.Now() - start) / 1000):0} fps, the longest frame {longest:0} ms");
            }
            iso?.SetWeather(false, false);
            iso?.SetAuto(false);
        }

        /// <summary>A headless run through the app (PORCHLIGHT_SMOKE=1): explore, walk a while, back to the map, quit.</summary>
        async Task Smoke()
        {
            Debug.Log($"smoke: town {seed}: {statusLabel.text}");
            await Explore();
            // Trash day on its own (PORCHLIGHT_SMOKE_TRASH: its story as the server compiles it): quits 0 if it passed.
            var trash = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_TRASH");
            if (!string.IsNullOrEmpty(trash))
            {
                bool passed = await SmokeTrash(System.IO.File.ReadAllText(trash));
                Debug.Log("smoke: done");
                Application.Quit(passed ? 0 : 1);
                return;
            }
            if (Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_BUILD") == "1")
            {
                await SmokeBuild();
                Application.Quit(0);
                return;
            }
            Debug.Log($"smoke: exploring={exploring} player={iso?.PlayerPosition}");
            iso?.SetAuto(true);
            double start = platform.Now(), until = start + 8000;
            int frames = 0;
            while (platform.Now() < until)
            {
                await Task.Yield();
                frames++;
            }
            var renderers = FindObjectsByType<Renderer>();
            Debug.Log($"smoke: {frames / ((platform.Now() - start) / 1000):0} fps, {renderers.Length} renderers ({renderers.Count(r => r.isPartOfStaticBatch)} static-batched), {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576} MB allocated, {UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576} MB reserved, {GC.GetTotalMemory(false) / 1048576} MB managed");
            Debug.Log($"smoke: after auto walk player={iso?.PlayerPosition} auto={iso?.IsAuto}");
            // Rain and snow together, for a few seconds: what falls, lands, settles and is tracked.
            iso?.SetWeather(true, true);
            double weatherUntil = platform.Now() + 4000;
            while (platform.Now() < weatherUntil) await Task.Yield();
            Debug.Log($"smoke: weather: {iso?.WeatherReport()}");
            iso?.SetWeather(false, false);
            iso?.SetAuto(false);
            var story = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_MISSION");
            if (!string.IsNullOrEmpty(story)) await SmokeMission(System.IO.File.ReadAllText(story));
            var fire = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_FIRE");
            if (!string.IsNullOrEmpty(fire)) await SmokeFire(System.IO.File.ReadAllText(fire));
            var marathon = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_MARATHON");
            if (!string.IsNullOrEmpty(marathon)) await SmokeMarathon(System.IO.File.ReadAllText(marathon));
            SmokeZoo();
            // The animals unlocked: offered in the character box, and one to be.
            Unlock("animals");
            ChooseLook();
            for (int i = 0; i < 30; i++) await Task.Yield();
            overlay.CloseChooser();
            foreach (var animal in new[] { "animals/animal-penguin", "animals/animal-giraffe" })
            {
                iso.ChangeLook(animal);
                for (int i = 0; i < 10; i++) await Task.Yield();
                Debug.Log($"smoke: character box with {Catalog.CHARACTERS.Length + (Unlocks.Contains("animals") ? Catalog.ANIMALS.Length : 0)} to choose from; now {iso.LookModel}");
            }
            Regenerate(RandomSeed(), @params);
            Debug.Log($"smoke: regenerated {seed}: {statusLabel.text}");
            Debug.Log("smoke: done");
            Application.Quit(0);
        }

        /// <summary>
        /// The smoke run plays a mission too (PORCHLIGHT_SMOKE_MISSION: a story as the server
        /// compiles it, the chase): out of the zoo, running about till the police catch you and
        /// put you back, out again, and out in a car; with how smoothly it ran meanwhile.
        /// </summary>
        async Task SmokeMission(string story)
        {
            // The longest frame, and how many were long (a hitch you'd see), while it plays.
            float worst = 0;
            int long_ = 0, frames = 0;
            // And how far you went meanwhile.
            double moved = 0;
            Vec2? was = null;
            string Frames()
            {
                string said = $"frames: {frames}, the longest {worst * 1000:0} ms, {long_} over 50 ms; you went {moved:0} tiles";
                worst = 0;
                long_ = frames = 0;
                moved = 0;
                was = null;
                return said;
            }
            async Task<bool> Until(Func<bool> met, double seconds)
            {
                double end = platform.Now() + seconds * 1000;
                while (platform.Now() < end && mission != null)
                {
                    frames++;
                    worst = Math.Max(worst, Time.unscaledDeltaTime);
                    if (Time.unscaledDeltaTime > 0.05f) long_++;
                    if (iso.PlayerPosition is Vec2 now)
                    {
                        // (Put back in the zoo isn't going anywhere.)
                        if (was is Vec2 before && Geo.Dist(before, now) < 1) moved += Geo.Dist(before, now);
                        was = now;
                    }
                    // Reading on at once, as a player pressing Enter would.
                    if (mission.script.Line != null) mission.script.Advance();
                    if (met()) return true;
                    await Task.Yield();
                }
                return false;
            }
            string Objective() => mission?.CurrentObjective ?? "(no mission)";
            bool InZoo() => iso.PlayerPosition is Vec2 p && iso.world.model.zoo is Zoo z && Menagerie.Inside(z, p);
            var zoo = iso.world.model.zoo;
            if (zoo == null)
            {
                Debug.Log("smoke: mission: no zoo in this town");
                return;
            }
            double began = platform.Now();
            mission = new MissionPlay("smoke", "Smoke", story, iso, ModelLibrary.Instance, input) { OnUnlock = what => Debug.Log($"smoke: mission: unlocked {what}") };
            bool ok = await Until(() => Objective().StartsWith("Get out"), 5);
            Debug.Log($"smoke: mission: '{Objective()}' ok={ok} in zoo={InZoo()} look={iso.LookOverride}, started in {platform.Now() - began:0} ms");
            iso.PutPlayer(Menagerie.RoadEnd(zoo), 0);
            ok = await Until(() => Objective().StartsWith("Keep away"), 5);
            Debug.Log($"smoke: mission: out of the zoo: '{Objective()}' ok={ok}, {mission?.Chasing} cars after you");
            double start = platform.Now();
            // On the move all the while, along the sidewalks, as a player running about would be.
            iso.SetAuto(true);
            Frames();
            string officers = "";
            ok = await Until(() =>
            {
                if (mission?.Chasing > 0) officers = mission.Officers();
                return Objective().StartsWith("Get out");
            }, 45);
            Debug.Log($"smoke: mission: chase {Frames()}");
            Debug.Log($"smoke: mission: when caught: {officers}");
            Debug.Log($"smoke: mission: caught={ok} after {(platform.Now() - start) / 1000:0.0} s, in zoo={InZoo()}, {mission?.Chasing} cars after you");
            iso.PutPlayer(Menagerie.RoadEnd(zoo), 0);
            iso.SetAuto(true);
            await Until(() => false, 8);
            iso.SetAuto(false);
            Debug.Log($"smoke: mission: out again: '{Objective()}', {mission?.Chasing} cars after you, error={mission?.script.Error}, {Frames()}");
            // And out again at the wheel, driving off with them after you.
            iso.PutPlayer(Menagerie.RoadEnd(zoo), 0);
            await Until(() => Objective().StartsWith("Keep away"), 5);
            bool driving = iso.SpawnCar(Array.IndexOf(Catalog.CARS, "cars/sedan")) && iso.ToggleCar();
            iso.SetAuto(true);
            Frames();
            await Until(() => false, 12);
            Debug.Log($"smoke: mission: driving={driving && iso.IsDriving}, '{Objective()}', {mission?.Chasing} cars after you, {Frames()}");
            iso.SetAuto(false);
            if (iso.IsDriving) iso.ToggleCar();
            EndMission(null);
            Debug.Log($"smoke: mission: ended, look={iso.LookOverride ?? "(yourself)"}");
        }

        /// <summary>
        /// The smoke run runs the marathon too (PORCHLIGHT_SMOKE_MARATHON: its story as the server
        /// compiles it): the course built, ribbon and crowd and all, then you along it from the start
        /// line to the finish a hop at a time, the stopwatch going, the time kept and the sprint earned.
        /// </summary>
        async Task SmokeMarathon(string story)
        {
            double began = platform.Now();
            double kept = 0;
            string earned = null;
            mission = new MissionPlay("marathon", "Marathon", story, iso, ModelLibrary.Instance, input, new MissionPlay.Times()) { OnUnlock = what => earned = what, OnRecord = s => kept = s };
            var course = mission.CourseOf("race");
            var renderers = FindObjectsByType<Renderer>();
            Debug.Log($"smoke: marathon: course {course?.Length * Directions.METRES_PER_TILE:0} m over {course?.roads.Count} streets, built in {platform.Now() - began:0} ms, {renderers.Count(r => r.name.StartsWith("race"))} race meshes ({renderers.Where(r => r.name.StartsWith("race crowd")).Sum(r => r.GetComponent<MeshFilter>().sharedMesh.vertexCount)} crowd vertices), error={mission.script.Error}");
            if (course == null) return;
            foreach (var line in mission.CrowdOf("race") ?? new List<string>()) Debug.Log($"smoke: marathon: crowd {line}");
            int held = 0, tried = 0;
            for (double s = 1; s < course.Length - 1; s += course.Length / 40)
            {
                var (p, along, width) = course.At(s);
                foreach (int side in new[] { 1, -1 })
                {
                    tried++;
                    // Walking straight out sideways, a little at a time, as feet do: held if still on the course
                    // (at a corner, sideways is down the next stretch, which is fine).
                    var at = p;
                    for (int k = 0; k < 120 && iso.Fences != null; k++)
                    {
                        var next = new Vec2(at.x + along.y * side * 0.05, at.y - along.x * side * 0.05);
                        if (iso.Fences.Blocks(at.x, at.y, next.x, next.y, Player.PLAYER_RADIUS)) break;
                        at = next;
                    }
                    if (iso.Fences != null && course.Nearest(at, 0, course.Length).d <= width + 0.3) held++;
                }
            }
            Debug.Log($"smoke: marathon: ribbon of {iso.Fences?.Count} pieces held {held} of {tried} steps off the course");
            float worst = 0;
            int frames = 0;
            async Task Frame()
            {
                if (mission?.script.Line != null) mission.script.Advance();
                frames++;
                worst = Math.Max(worst, Time.unscaledDeltaTime);
                await Task.Yield();
            }
            for (int i = 0; i < 20 && mission?.script.Line != null; i++) await Frame();
            Debug.Log($"smoke: marathon: at the start: '{mission?.CurrentObjective}', player={iso.PlayerPosition}");
            // A short cut first: straight to halfway gains nothing.
            var (half, _, _) = course.At(course.Length / 2);
            iso.PutPlayer(half, 0);
            for (int i = 0; i < 5; i++) await Frame();
            Debug.Log($"smoke: marathon: after a short cut: '{mission?.CurrentObjective}'");
            for (double s = 0.15; s <= course.Length + 0.5 && mission != null && !mission.script.Done; s += 0.5)
            {
                var (p, along, _) = course.At(s);
                iso.PutPlayer(p, DetMath.Atan2(along.x, along.y));
                await Frame();
                if (s < 3 || frames % 400 == 0) Debug.Log($"smoke: marathon: {s:0} tiles along: '{mission?.CurrentObjective}'");
            }
            for (int i = 0; i < 60 && mission != null && !mission.script.Done; i++) await Frame();
            Debug.Log($"smoke: marathon: done={mission?.script.Done} error={mission?.script.Error} kept={kept:0.00}s earned={earned} frames={frames} longest={worst * 1000:0} ms");
            Unlock("sprint");
            Debug.Log($"smoke: marathon: sprint unlocked={Unlocks.Contains("sprint")}");
            EndMission(null);
        }

        /// <summary>
        /// The smoke run fights fires too (PORCHLIGHT_SMOKE_FIRE: the firefighter story as the
        /// server compiles it): the fire engine put straight into each burning house's spot, up
        /// to the turret, and its water on each burning window in turn till all three are saved.
        /// </summary>
        /// <summary>Each pen's gate: shut to anyone else, open to its own animal coming up from the path, and open to get out from inside.</summary>
        void SmokeZoo()
        {
            var world = iso.world;
            var z = world.model.zoo;
            if (z == null) return;
            foreach (var g in world.penGates)
            {
                var o = Menagerie.AlongV(z);
                int side = Menagerie.PenGateOut(g.pen);
                var path = new Vec2(g.at.x + o.x * side * 0.2, g.at.y + o.y * side * 0.2);
                var pen = new Vec2(g.at.x - o.x * side * 0.2, g.at.y - o.y * side * 0.2);
                string own = g.pen.animals[0].animal;
                bool Clear() => !world.solids.Hits(g.at.x, g.at.y, 0.1);
                world.UpdateZoo(0, Catalog.FIREFIGHTER, path);
                bool shut = !Clear();
                world.UpdateZoo(0, own, path);
                bool opens = Clear();
                bool stuck = false;
                for (double k = 0; k <= 1; k += 0.1)
                    stuck |= world.solids.Hits(path.x + (pen.x - path.x) * k, path.y + (pen.y - path.y) * k, 0.1);
                world.UpdateZoo(0, Catalog.FIREFIGHTER, pen);
                bool outward = Clear();
                world.UpdateZoo(0, null, null);
                Debug.Log($"smoke: zoo: {own} gate shut to others={shut}, opens for its own={opens}, walked through={!stuck}, lets you out={outward}, shut again={!Clear()}");
            }
            Debug.Log($"smoke: zoo: {world.animals.Count} animals in {world.penGates.Count} pens");
        }

        async Task SmokeFire(string story)
        {
            int frames = 0, long_ = 0;
            float worst = 0;
            async Task<bool> Until(Func<bool> met, double seconds)
            {
                double end = platform.Now() + seconds * 1000;
                while (platform.Now() < end && mission != null)
                {
                    frames++;
                    worst = Math.Max(worst, Time.unscaledDeltaTime);
                    if (Time.unscaledDeltaTime > 0.05f) long_++;
                    if (mission.script.Line != null) mission.script.Advance();
                    if (met()) return true;
                    await Task.Yield();
                }
                return mission == null && met();
            }
            string Objective() => mission?.CurrentObjective ?? "(no mission)";
            int had = iso.CarModel;
            mission = new MissionPlay("smoke-fire", "Smoke fire", story, iso, ModelLibrary.Instance, input) { OnUnlock = Unlock };
            for (int house = 1; house <= 3 && mission != null; house++)
            {
                bool ok = await Until(() => Objective().StartsWith("Drive to"), 5);
                Debug.Log($"smoke: fire: house {house}: '{Objective()}' ok={ok} look={iso.LookOverride} car={(iso.CarModel >= 0 ? Catalog.CARS[iso.CarModel] : "none")} at the wheel={iso.DrivingMyCar} error={mission?.script.Error}");
                var spot = mission?.SpotOf("fire");
                var car = iso.OwnVehicle;
                if (spot == null || car == null) break;
                car.x = spot.Value.x;
                car.y = spot.Value.y;
                car.heading = spot.Value.heading;
                car.speed = 0;
                ok = await Until(() => Objective().StartsWith("Put out"), 5);
                bool up = mission != null && mission.TryTalk();
                Debug.Log($"smoke: fire: parked={ok}, '{Objective()}', up at the turret={up && mission.Fight.Manned}; walls {mission?.Fight.Walls()}");
                double start = platform.Now();
                string grips = "";
                frames = long_ = 0;
                worst = 0;
                ok = await Until(() =>
                {
                    var f = mission?.Fight;
                    if (f != null && f.Manned)
                    {
                        f.holdSpray = f.AimAtBurning();
                        grips = f.Grips();
                    }
                    return mission == null || !Objective().StartsWith("Put out");
                }, 40);
                if (!ok) Debug.Log($"smoke: fire: still burning: {mission?.Fight.Burning()}");
                Debug.Log($"smoke: fire: hands short of the branch by {grips}");
                Debug.Log($"smoke: fire: house {house} out={ok} in {(platform.Now() - start) / 1000:0.0} s, now '{Objective()}', error={mission?.script.Error}; frames {frames}, the longest {worst * 1000:0} ms, {long_} over 50 ms");
                if (!ok) break;
            }
            await Until(() => false, 3);
            Debug.Log($"smoke: fire: mission over={mission == null}, look={iso.LookOverride ?? "(yourself)"}, car {(iso.CarModel >= 0 ? Catalog.CARS[iso.CarModel] : "none")} (had {(had >= 0 ? Catalog.CARS[had] : "none")}), unlocked firefighter={Unlocks.Contains("firefighter")}, fire engine in the car box={Catalog.GarageCars(Unlocks).Contains(Array.IndexOf(Catalog.CARS, Catalog.FIRETRUCK))}");
            EndMission(null);
            if (iso.IsDriving) iso.ToggleCar();
            iso.ChangeLook(Catalog.FIREFIGHTER);
            for (int i = 0; i < 10; i++) await Task.Yield();
            Debug.Log($"smoke: fire: now {iso.LookModel}");
        }

        /// <summary>
        /// Trash day, played through (PORCHLIGHT_SMOKE_TRASH: the story as the server compiles it): the garbage truck
        /// put straight into each bin's spot, as a player pulling up would leave it, and the crew followed from
        /// hopping out to hopping back in: to the bin, wheeling it round the back, tipping it in, wheeling it back
        /// and in again, five times, till the mission's complete. True if it all happened, in that order.
        /// </summary>
        async Task<bool> SmokeTrash(string story)
        {
            const int STOPS = 5;
            // What the crew should do at every stop, in order.
            string[] round = { "hopout", "walk bin", "takebin", "walk back", "dump", "walk bin", "returnbin", "hopin" };
            float worst = 0;
            int frames = 0, long_ = 0;
            string doing = null;
            var seen = new List<string>();
            // How far the hands fell short of the bin in each thing the crew does (worst over the round).
            var miss = new Dictionary<string, float>();
            double phaseStart = 0;
            var m = mission = new MissionPlay("trash-day", "Trash day", story, iso, ModelLibrary.Instance, input);
            string Objective() => mission?.CurrentObjective ?? "(no mission)";
            // Reading on at once, as a player pressing Enter would; and the crew watched, a line each time they start something new.
            async Task<bool> Until(Func<bool> met, double seconds)
            {
                double end = platform.Now() + seconds * 1000;
                while (platform.Now() < end && mission != null)
                {
                    frames++;
                    worst = Math.Max(worst, Time.unscaledDeltaTime);
                    if (Time.unscaledDeltaTime > 0.05f) long_++;
                    if (mission.script.Line != null) mission.script.Advance();
                    string now = m.Doing("stop");
                    if (doing != null) miss[doing] = Math.Max(miss.TryGetValue(doing, out float was) ? was : 0, m.GripMiss("stop"));
                    if (now != doing)
                    {
                        double t = platform.Now();
                        if (doing != null && doing != "none") Debug.Log($"smoke: trash:   {doing} took {(t - phaseStart) / 1000:0.0} s; bin: {m.BinReport("stop")}");
                        doing = now;
                        phaseStart = t;
                        if (now != "none" && now != "idle" && now != "aboard") seen.Add(now);
                    }
                    if (met()) return true;
                    await Task.Yield();
                }
                return met();
            }
            bool truck = iso.CarModel >= 0 && Catalog.CARS[iso.CarModel] == "cars/garbage-truck";
            Debug.Log($"smoke: trash: the garbage truck={truck}, at the wheel={iso.DrivingMyCar}, error={m.script.Error}");
            int stops = 0, onRoad = 0;
            bool inOrder = true;
            for (int stop = 1; stop <= STOPS; stop++)
            {
                bool ok = await Until(() => Objective().StartsWith("Pull up"), 8);
                var spot = m.SpotOf("stop");
                var car = iso.OwnVehicle;
                bool binOnRoad = m.BinOnRoad("stop");
                if (binOnRoad) onRoad++;
                Debug.Log($"smoke: trash: stop {stop}: '{Objective()}' ok={ok}, bin: {m.BinReport("stop")}, {(binOnRoad ? "IN THE STREET" : "off the street")}, error={m.script.Error}");
                if (!ok || spot == null || car == null) break;
                car.x = spot.Value.x;
                car.y = spot.Value.y;
                car.heading = spot.Value.heading;
                car.speed = 0;
                seen.Clear();
                ok = await Until(() => Objective().StartsWith("Wait while"), 5);
                Debug.Log($"smoke: trash: stop {stop}: parked={ok}, '{Objective()}'");
                if (!ok) break;
                double start = platform.Now();
                frames = long_ = 0;
                worst = 0;
                ok = await Until(() => m.script.Done || (Objective().StartsWith("Pull up") && m.Doing("stop") == "none"), 60);
                bool order = string.Join(",", seen) == string.Join(",", round);
                inOrder &= order;
                Debug.Log($"smoke: trash: stop {stop}: crew done={ok} in {(platform.Now() - start) / 1000:0.0} s, did {string.Join(" > ", seen)} (in order={order}); out {m.HoppedOut}, emptied {m.BinsEmptied}, put back {m.BinsReturned}, back in {m.HoppedIn}; frames {frames}, the longest {worst * 1000:0} ms, {long_} over 50 ms");
                if (!ok) break;
                stops++;
            }
            await Until(() => mission == null, 3);
            finished.TryGetValue("trash-day", out int completed);
            Debug.Log($"smoke: trash: hands short of the bin by up to: {string.Join(", ", miss.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value:0.000}"))} tiles");
            // Lifted to the tailgate and over (its mouth above the truck's back wall), lid flapping open.
            var truckModel = ModelLibrary.Instance.Get("cars/garbage-truck");
            double tailgate = truckModel.size.y * CarView.ScaleOf(truckModel) * 0.5;
            bool tipped = m.PeakMouth > tailgate && m.PeakLid > 60;
            Debug.Log($"smoke: trash: bins tipped: mouth up to {m.PeakMouth:0.00} tiles (tailgate about {tailgate:0.00}), lid open up to {m.PeakLid:0}°: {(tipped ? "over the tailgate" : "NOT over the tailgate")}");
            Debug.Log($"smoke: trash: bins in the street: {onRoad} of {stops}");
            bool passed = stops == STOPS && inOrder && tipped && onRoad == 0 && truck && m.script.Done && m.script.Error == null && completed == 1
                && m.HoppedOut == STOPS && m.BinsEmptied == STOPS && m.BinsReturned == STOPS && m.HoppedIn == STOPS;
            Debug.Log($"smoke: trash: {(passed ? "PASS" : "FAIL")}: {stops} of {STOPS} stops, crew in order={inOrder}, mission complete={completed == 1} (error={m.script.Error ?? "none"}), out {m.HoppedOut}, emptied {m.BinsEmptied}, put back {m.BinsReturned}, back in {m.HoppedIn}, hands short of a grip by up to {m.WorstGripMiss:0.000} tiles, your car now {(iso.CarModel >= 0 ? Catalog.CARS[iso.CarModel] : "none")}");
            if (mission != null) EndMission(null);
            return passed;
        }

        void BuildApp()
        {
            // Signed in, the character says which town; until then, any behind the front door.
            var initial = online ? new UrlState { @params = GenParams.Defaults } : ReadUrlState();
            seed = initial.seed ?? RandomSeed();
            @params = initial.@params;

            map = new BlueprintView { name = "map" };
            map.WheelDelta = () => input.WheelDeltaY;
            map.OnViewChanged = () =>
            {
                autoFit = false;
                followOnMap = false;
            };
            map.OnHover = world =>
            {
                hoverLabel.text = DescribeTile(model, world);
                housePicker?.Hover(world);
            };
            map.OnClick = world => housePicker?.Click(world);
            map.OnDoubleClick = Fit;
            map.RegisterCallback<PointerDownEvent>(_ => Unfocus(), TrickleDown.TrickleDown);
            map.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (autoFit && model != null) map.Fit();
                map.Redraw();
            });
            root.Add(map);

            isoHost = new VisualElement { name = "iso" };
            isoHost.pickingMode = PickingMode.Ignore;
            isoHost.style.display = DisplayStyle.None;
            root.Add(isoHost);

            footer = new VisualElement { name = "footer" };
            footer.pickingMode = PickingMode.Ignore;
            hoverLabel = El.Text("", "footer-line", "mono");
            statusLabel = El.Text("", "footer-line", "footer-status", "mono");
            footer.Add(hoverLabel);
            footer.Add(statusLabel);
            root.Add(footer);

            panel = new DebugPanel(map.visibleLayers, ToggleLayer);
            panel.SetOpen(false);
            root.Add(panel.root);

            var host = new VisualElement();
            root.Add(host);
            sidebar = new Sidebar(host, @params, new SidebarHandlers
            {
                SignedIn = online,
                OnParamsChange = ChangeParams,
                OnRegenerate = () => Regenerate(RandomSeed(), @params),
                OnSeed = value => Regenerate(value, @params),
                OnExplore = () => _ = Explore(),
                OnBack = () => LeaveExplore(),
                OnResetParams = ResetParams,
                OnWeather = SetWeather,
                OnMissionsRefresh = RefreshMissions,
            }, SetSidebarOpen);
            if (user != null)
            {
                sidebar.SetAccount(user.name, user.email, SignOut);
                footer.style.display = DisplayStyle.None;
                panel.root.style.visibility = Visibility.Hidden;
            }

            // Small screens start with the map, not the controls.
            if (Screen.width / dpr <= 720) SetSidebarOpen(false);
            if (!online && platform.Load(DEBUG_KEY) == "1") SetDebugOpen(true);
            var weather = (platform.Load(WEATHER_KEY) ?? "").Split(',');
            raining = Array.IndexOf(weather, "rain") >= 0;
            snowing = Array.IndexOf(weather, "snow") >= 0;
            sidebar.SetWeather(raining, snowing);

            Regenerate(seed, @params);
            loader.Step("app");
            if (online) OpenFrontDoor();
            else HideLoader();
        }

        // The front door: who's playing, and where they live, then into their town.

        void OpenFrontDoor(string message = null)
        {
            root.AddToClassList("choosing");
            door ??= new FrontDoor(root, platform, origin, () => portraits ??= new Portraits(ModelLibrary.Instance), () => RandomSeed(), SignOut);
            door.Listed = () =>
            {
                loader.Step("characters");
                HideLoader();
            };
            // A reload in this tab carries straight on, where they were.
            FrontDoor.Remembered(platform, origin, c =>
            {
                loader.Step("who");
                if (c?.home != null && c.home.hasHouse && !c.inUse) PlayAs(c, false);
                else ShowCharacters(message ?? (c?.inUse == true ? InUse(c) : null));
            });
        }

        void ShowCharacters(string message = null) => door.Characters(Settle, c => ChooseTown(c), message);

        static string InUse(Character c) => $"{c.name} is being played on another device. Choose someone else.";

        bool settling;

        /// <summary>This character, please (if they're still free): somewhere to go, if others on the account are playing.</summary>
        void Settle(Character chosen)
        {
            if (settling) return;
            settling = true;
            Account.ListCharacters(platform, origin, (list, _) =>
            {
                settling = false;
                var c = list?.Find(o => o.id == chosen.id) ?? chosen;
                if (c.inUse)
                {
                    ShowCharacters(InUse(c));
                    return;
                }
                var others = list?.Where(o => o.id != c.id && o.inUse && !string.IsNullOrEmpty(o.town)).ToList();
                if (others != null && others.Count > 0) door.WhereTo(c, others, them => Visit(c, them), () => CarryOn(c), () => ShowCharacters());
                else CarryOn(c);
            });
        }

        /// <summary>On to whatever they haven't chosen yet, or into their town.</summary>
        void CarryOn(Character c)
        {
            if (c.home == null) ChooseTown(c);
            else if (!c.home.hasHouse) ChooseHouse(c);
            else PlayAs(c, true);
        }

        void ChooseTown(Character c, string message = null) => door.Towns(c, moved => ChooseHouse(moved), () => ShowCharacters(), () => OpenEditor(c), message);

        // The town editor: the blueprint and every setting, for a town to save by name.

        /// <summary>Who the town's being made for (null: not in the editor).</summary>
        Character editingFor;

        void OpenEditor(Character c)
        {
            door.Close();
            editingFor = c;
            root.RemoveFromClassList("choosing");
            sidebar.SetEditor(true, SaveTown, LeaveEditor);
            SetSidebarOpen(true);
            footer.style.display = DisplayStyle.Flex;
            var fresh = GenParams.Defaults;
            sidebar.SetParams(fresh);
            autoFit = true;
            Regenerate(RandomSeed(), fresh);
        }

        void CloseEditor()
        {
            editingFor = null;
            sidebar.SetEditor(false, null, null);
            footer.style.display = DisplayStyle.None;
            root.AddToClassList("choosing");
            if (Screen.width / dpr <= 720) SetSidebarOpen(false);
        }

        void LeaveEditor()
        {
            var c = editingFor;
            CloseEditor();
            ChooseTown(c);
        }

        /// <summary>Save the town on the map by this name, and move them there to choose a house.</summary>
        void SaveTown(string name)
        {
            var c = editingFor;
            if (c == null) return;
            if (Homes.All(model).Count == 0)
            {
                sidebar.EditorMessage("This town has no houses to live in. Try more buildings, or another seed.");
                return;
            }
            Account.CreateTown(platform, origin, name, TownKey, (town, err) =>
            {
                if (town == null)
                {
                    sidebar.EditorMessage(err);
                    return;
                }
                Account.MoveToTown(platform, origin, c.id, town.id, (moved, refused) =>
                {
                    CloseEditor();
                    if (moved != null) ChooseHouse(moved);
                    else ChooseTown(c, refused);
                });
            });
        }

        /// <summary>Their town's map, to choose a house on.</summary>
        void ChooseHouse(Character c, string note = null)
        {
            ShowLoader("house", c.home?.townName);
            AfterPaint(() => OpenHousePicker(c, note));
        }

        async void OpenHousePicker(Character c, string note)
        {
            door.Close();
            root.AddToClassList("choosing");
            await Run(ShowTownSteps(c.home.townKey), "town/");
            autoFit = true;
            map.Fit();
            map.Redraw();
            Account.ListHomes(platform, origin, c.home.townId, (homes, err) =>
            {
                if (homes == null)
                {
                    ShowCharacters(err);
                    return;
                }
                loader.Step("homes");
                HideLoader();
                var rng = new Rng(RandomSeed());
                housePicker = new HousePicker(root, map, model, c.name, c.home.townName, homes, note, () => rng.Next(), (x, y) =>
                    Account.ClaimHome(platform, origin, c.id, x, y, (moved, refused) =>
                    {
                        if (moved == null)
                        {
                            housePicker?.Refused(refused);
                            return;
                        }
                        CloseHousePicker();
                        PlayAs(moved, true);
                    }), () =>
                    {
                        CloseHousePicker();
                        ChooseTown(c);
                    });
            });
        }

        void CloseHousePicker()
        {
            housePicker?.Close();
            housePicker = null;
        }

        /// <summary>This town on the map, if it isn't already.</summary>
        void ShowTown(string key) => Drain(ShowTownSteps(key), "town/");

        IEnumerable<string> ShowTownSteps(string key)
        {
            if (key == TownKey) yield break;
            var state = ParseUrlState(key);
            sidebar.SetParams(state.@params);
            foreach (var step in RegenerateSteps(state.seed ?? RandomSeed(), state.@params)) yield return step;
        }

        string TownKey => EncodeUrlState(seed, @params);

        /// <summary>Into this character's town, as them: at their front door, or (a reload) where they were.</summary>
        void PlayAs(Character c, bool atDoor)
        {
            ShowLoader("town", c.home?.townName);
            AfterPaint(() => EnterAs(c, atDoor));
        }

        async void EnterAs(Character c, bool atDoor)
        {
            door.Close();
            net?.Close();
            net = null;
            if (county != null) county.Net = null;
            if (iso != null) iso.TeleportHook = null;
            await Run(ShowTownSteps(c.home.townKey), "town/");
            // The county's space on screen isn't their town (left out on a highway): their town afresh, as going home does.
            if (county != null && county.Active?.townKey != c.home.townKey) isoTown = null;
            // Generated afresh, a town can have changed since they moved in.
            if (Homes.At(Homes.All(model), c.home.x, c.home.y) == null)
            {
                ChooseHouse(c, "The town has changed, and your house isn’t there any more. Choose another.");
                return;
            }
            playing = c;
            preferHome = atDoor;
            FrontDoor.Remember(platform, c.id);
            root.RemoveFromClassList("choosing");
            sidebar.SetCharacter(c.name, () => SwitchCharacter(), c.home.townName, GoHome, ToggleJournal);
            enteringTown = true;
            await Explore();
        }

        /// <summary>Into someone else's town as this character, who needn't live anywhere yet.</summary>
        void Visit(Character c, Character them)
        {
            ShowLoader("town", TownSpace(them.town)?.name);
            AfterPaint(() => VisitNow(c, them));
        }

        void VisitNow(Character c, Character them)
        {
            door.Close();
            net?.Close();
            net = null;
            if (county != null) county.Net = null;
            if (iso != null) iso.TeleportHook = null;
            playing = c;
            preferHome = false;
            FrontDoor.Remember(platform, c.id);
            root.RemoveFromClassList("choosing");
            sidebar.SetCharacter(c.name, () => SwitchCharacter(), c.home?.townName, c.home?.hasHouse == true ? GoHome : (Action)null, ToggleJournal);
            GoTo(them);
        }

        /// <summary>The town on screen's id on the server (null: not known yet).</summary>
        string townIdOnScreen;
        string townIdFor;

        /// <summary>
        /// Who lives where in the town on screen, asked of the server afresh (on entering the town,
        /// and each time the big map comes up): every lived-in house but your own goes on the maps.
        /// </summary>
        void RefreshNeighbours()
        {
            if (!online || overlay == null || iso?.world == null) return;
            string key = TownKey;
            void Ask(string townId)
            {
                Account.ListHomes(platform, origin, townId, (homes, _) =>
                {
                    if (homes == null || key != TownKey || iso?.world == null) return;
                    var own = HouseOfHome();
                    var houses = new List<IList<Vec2>>();
                    foreach (var h in homes)
                    {
                        var b = Homes.At(iso.world.model.buildings, h.x, h.y);
                        if (b != null && b != own && b.footprint != null) houses.Add(b.footprint);
                    }
                    overlay.SetNeighbours(houses);
                });
            }
            var home = playing?.home;
            if (home != null && home.townKey == key && !string.IsNullOrEmpty(home.townId))
            {
                Ask(home.townId);
                return;
            }
            if (townIdFor == key && townIdOnScreen != null)
            {
                Ask(townIdOnScreen);
                return;
            }
            // Visiting: which town this is, by its key.
            Account.ListTowns(platform, origin, (list, _) =>
            {
                var town = list?.towns.Find(t => t.key == key);
                if (town == null || key != TownKey) return;
                townIdFor = key;
                townIdOnScreen = town.id;
                Ask(town.id);
            });
        }

        /// <summary>Your house, if you're in your own town and it's there.</summary>
        Building HouseOfHome()
        {
            var home = playing?.home;
            if (home == null || !home.hasHouse || iso?.world == null || home.townKey != TownKey) return null;
            return Homes.At(iso.world.model.buildings, home.x, home.y);
        }

        /// <summary>Your front door, if you're in your own town and it's there.</summary>
        (Vec2 at, double heading)? DoorOfHome()
        {
            var home = playing?.home;
            if (home == null || !home.hasHouse || iso?.world == null || home.townKey != TownKey) return null;
            var house = Homes.At(iso.world.model.buildings, home.x, home.y);
            return house != null ? Homes.Door(iso.world.rooms, house.id) : null;
        }

        /// <summary>Back to your own front door, from wherever you are (another town too).</summary>
        void GoHome()
        {
            var home = playing?.home;
            if (home == null || !home.hasHouse || iso == null || buildingTown || Loading) return;
            // In your own town (the space you're in, not just the last town you drove through): straight to the door.
            if (InHomeTown(home) && DoorOfHome() is (Vec2 at, double heading))
            {
                iso.PutPlayer(at, heading);
                return;
            }
            // Anywhere else (another town, out on the highways, the junction): your town afresh, at your door.
            ShowLoader("town", home.townName);
            AfterPaint(async () =>
            {
                preferHome = true;
                await Run(ShowTownSteps(home.townKey), "town/");
                // Rebuilt even when it's still the town on file (driven out of it onto the road): the county starts from it again.
                isoTown = null;
                enteringTown = true;
                await Explore();
                if (DoorOfHome() is (Vec2 door, double facing)) iso?.PutPlayer(door, facing);
                // Still joining the town's room, its spawn puts you at the door too; already in it, nothing will.
                if (net == null || net.Spawned) preferHome = false;
            });
        }

        /// <summary>Are you in your home's town right now: on the county, the space you're in; alone, the town on screen.</summary>
        bool InHomeTown(Home home)
        {
            if (county != null)
            {
                var here = county.Active;
                return here != null && here.kind == SpaceKind.Town && here.townKey == home.townKey && TownKey == home.townKey;
            }
            return home.townKey == TownKey;
        }

        void ToggleJournal()
        {
            if (playing == null) return;
            if (journal != null)
            {
                journal.Close();
                return;
            }
            journal = new Journal(root, platform, origin, () =>
            {
                journal = null;
                if (iso != null) iso.PadHeld = false;
            });
            if (iso != null) iso.PadHeld = true;
        }

        bool Typing()
        {
            // A screen over the town has the keys, as a text field would.
            if (Loading || journal != null || housePicker != null || door?.Open == true) return true;
            var focused = root?.panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null);
        }

        /// <summary>Out of any text field, so the keys drive the game again.</summary>
        void Unfocus() => (root?.panel?.focusController?.focusedElement as VisualElement)?.Blur();

        void SetSidebarOpen(bool open) => root.EnableInClassList("sidebar-closed", !open);

        void Update()
        {
            FollowScreenScale();
            platform.Pump();
            timers.Tick();
            input.Update();
            UpdateLoader();
            if (attract != null)
            {
                attract.viewport = new Rect(0, 0, Screen.width, Screen.height);
                attract.Update();
            }
            if (!started) return;
            root.EnableInClassList("narrow", Screen.width / dpr <= 720);
            // Before there's a town to walk, the front door's pictures still need filming.
            if (iso == null) portraits?.Update(Time.deltaTime, false);
            if (iso != null && !buildingTown)
            {
                iso.viewport = ScreenRect(isoHost);
                if (exploring) ZoomWithWheel();
                iso.FastSprint = Unlocks.Contains("sprint");
                iso.Update();
                if (county != null)
                {
                    county.Paused = mission != null || missionStarting != null;
                    county.Update();
                    overlay?.SetLoading(county.Waiting);
                }
                UpdateMission();
                UpdateGarage();
                UpdatePlayers();
            }
            Follow();
            CheckHash();
            // After the town's had its turn, so a press that closes one of these isn't read by the town too.
            door?.Update(input.Pad, input.Connected);
            housePicker?.Update(input.Pad, input.Connected);
            journal?.Update(input.Pad);
            if (editingFor != null && input.Pad.Pressed(Button.B)) LeaveEditor();
        }

        /// <summary>
        /// The mouse wheel zooms the 3D view, unless the pointer is over the UI (the
        /// sidebar, the minimap). The view's own element lets the pointer through to the
        /// town, so the wheel is read here rather than as a UI event.
        /// </summary>
        void ZoomWithWheel()
        {
            double delta = input.WheelDeltaY;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (delta == 0 || mouse == null || root.panel == null) return;
            var screen = mouse.position.ReadValue();
            var at = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var over = root.panel.Pick(at);
            if (over != null && over != root && over != isoHost) return;
            iso.Wheel(delta, input.CtrlHeld);
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus) net?.Visible();
        }

        void OnDestroy() => net?.Close();

        /// <summary>An element's box in screen pixels (origin bottom left), for the 3D camera.</summary>
        Rect ScreenRect(VisualElement e)
        {
            var b = e.worldBound;
            if (float.IsNaN(b.width) || b.width <= 0) return new Rect(0, 0, Screen.width, Screen.height);
            float x = b.x * dpr, y = b.y * dpr, w = b.width * dpr, h = b.height * dpr;
            return new Rect(x, Screen.height - (y + h), w, h);
        }

        // The town.

        void Regenerate(string nextSeed, GenParams nextParams) => Drain(RegenerateSteps(nextSeed, nextParams), "");

        /// <summary>Regenerate a generator stage at a time (the town's only taken on at the end).</summary>
        IEnumerable<string> RegenerateSteps(string nextSeed, GenParams nextParams)
        {
            bool sizeChanged = model == null || model.width != nextParams.width || model.height != nextParams.height;
            var sw = Stopwatch.StartNew();
            TownModel next = null;
            Exception failed = null;
            using (var stages = Generator.GenerateSteps(nextSeed, nextParams, m => next = m, check: Application.isEditor).GetEnumerator())
            {
                while (true)
                {
                    try
                    {
                        if (!stages.MoveNext()) break;
                    }
                    catch (Exception err)
                    {
                        failed = err;
                        break;
                    }
                    yield return "gen/" + stages.Current;
                }
            }
            seed = nextSeed;
            @params = nextParams;
            try
            {
                if (failed != null) throw failed;
                model = next;
                string counts = $"{model.buildings.Count} buildings · {model.roads.edges.Count} roads · {model.lots.Count} lots";
                statusLabel.text = $"{counts} · generated in {sw.Elapsed.TotalMilliseconds:0} ms";
                statusLabel.RemoveFromClassList("error");
            }
            catch (Exception err)
            {
                Debug.LogException(err);
                statusLabel.text = $"invariant failed: {err.Message}";
                statusLabel.AddToClassList("error");
                model = Generator.Generate(seed, @params); // still show what we got
            }
            sidebar.SetSeed(seed);
            WriteUrlState();
            map.marker = null; // a new town; the character's spot no longer applies
            map.route = new List<Vec2>();
            map.goal = null;
            map.SetModel(model);
            if (autoFit || sizeChanged) map.Fit();
            map.Redraw();
            if (exploring && iso != null)
            {
                // A new town: drop the old walk and start afresh at the new town's spawn point.
                // (All at once: the town is in play, with others' moves arriving, so it can't be half built.)
                EndMission(null);
                StopCounty();
                iso.InCounty = online;
                iso.SetTown(model);
                isoTown = model;
                SpawnCharacter();
                EnterTown();
                StartCounty();
                yield return "world";
            }
            else if (iso != null && iso.IsAuto)
            {
                // Regenerated from the map: stop the old town's auto-walk running behind it.
                iso.SetAuto(false);
            }
        }

        void Fit()
        {
            autoFit = true;
            map.Fit();
            map.Redraw();
        }

        /// <summary>Params changed: show them and rebuild.</summary>
        void ChangeParams(GenParams next)
        {
            sidebar.SetParams(next);
            Regenerate(seed, next);
        }

        void ResetParams() => ChangeParams(GenParams.Defaults);

        void ToggleLayer(string id, bool on)
        {
            if (on) map.visibleLayers.Add(id);
            else map.visibleLayers.Remove(id);
            map.Redraw();
        }

        void SetDebugOpen(bool open)
        {
            panel.SetOpen(open);
            platform.Save(DEBUG_KEY, open ? "1" : "0");
        }

        // Explore mode: the 3D town, loaded on first use.

        void LoadIso()
        {
            overlay = new ExploreOverlay(isoHost) { Dpr = dpr };
            iso = new ExploreView(camera3d, ModelLibrary.Instance, overlay, input);
            iso.SetWeather(raining, snowing);
            iso.OnTake = car => net?.Take(car);
            iso.OnTripEnd = () =>
            {
                tripTo = null;
                OfferDirections();
            };
            portraits ??= new Portraits(ModelLibrary.Instance);
            overlay.SignedIn = online;
            iso.HomeDoor = saved =>
            {
                if (saved && !preferHome) return null;
                preferHome = false;
                return DoorOfHome();
            };
            missionPanel = new MissionPanel(isoHost);
            overlay.OnPersonBox = ChooseLook;
            overlay.OnCarBox = ChooseCar;
            overlay.OnMapOpened = RefreshNeighbours;
            iso.OnPadAction = action =>
            {
                if (action == PadAction.Menu)
                {
                    if (exploring) LeaveExplore();
                    else if (!online) _ = Explore();
                    return;
                }
                if (!exploring) return;
                if (action == PadAction.BackToMap)
                {
                    if (online) ToggleJournal();
                    else LeaveExplore();
                }
                if (action == PadAction.Car) ToggleCar();
                if (action == PadAction.Night) ToggleNight();
                if (action == PadAction.NewCharacter) NewLook();
                if (action == PadAction.Center) iso.CenterBehind();
                // LB: the characters' grid, then on through them; RB (with a grid open): the cars', then on through those.
                if (action == PadAction.Characters) StepChooser("look", ChooseLook);
                if (action == PadAction.Cars) StepChooser("car", ChooseCar);
                if (action == PadAction.ChooserPick) overlay.PickHighlighted();
                if (action == PadAction.ChooserClose) overlay.CloseChooser();
            };
        }

        void SpawnCharacter()
        {
            iso?.Spawn(Catalog.RandomLook(new Rng(RandomSeed())));
        }

        /// <summary>A new look for your character (C): online, it's saved with them.</summary>
        void NewLook()
        {
            SpawnCharacter();
            var look = iso?.LookModel;
            if (look != null) net?.LookChanged(look);
        }

        async Task Explore()
        {
            sidebar.SetLoading(true);
            buildingTown = true;
            try
            {
                // Let "Loading…" show before the town is built (a frame's work).
                await NextFrame();
                if (iso == null)
                {
                    isoHost.style.display = DisplayStyle.Flex;
                    LoadIso();
                    loader.Step("explore");
                }
                exploring = true;
                Unfocus();
                root.AddToClassList("exploring");
                sidebar.SetExploring(true);
                isoHost.style.display = DisplayStyle.Flex;
                map.style.display = DisplayStyle.None;
                footer.style.display = DisplayStyle.None;
                panel.root.style.visibility = Visibility.Hidden;
                iso.SetBackground(false);
                if (isoTown != model)
                {
                    EndMission(null);
                    isoTown = null;
                    StopCounty();
                    iso.InCounty = online;
                    await Run(iso.SetTownSteps(model), "world/");
                    isoTown = model;
                    SpawnCharacter();
                }
                else if (iso.PlayerPosition == null) SpawnCharacter();
                // Same town as last time: carry on as the same character, where you left off.
                // Online: into this town's room, as whoever's playing.
                Connect();
                EnterTown();
                StartCounty();
                if (!missionsAsked) RefreshMissions();
            }
            catch (Exception err)
            {
                Debug.LogException(err);
                statusLabel.text = $"couldn't start explore mode: {err.Message}";
                statusLabel.AddToClassList("error");
                LeaveExplore(true);
                HideLoader(false);
            }
            finally
            {
                buildingTown = false;
                sidebar.SetLoading(false);
            }
        }

        /// <summary>Back to the blueprint: dev mode's. Signed in, you stay in the town (`force`: until you're choosing again).</summary>
        void LeaveExplore(bool force = false)
        {
            if (online && !force) return;
            iso?.CloseMap();
            exploring = false;
            root.RemoveFromClassList("exploring");
            sidebar.SetExploring(false);
            isoHost.style.display = DisplayStyle.None;
            map.style.display = DisplayStyle.Flex;
            footer.style.display = online ? DisplayStyle.None : DisplayStyle.Flex;
            panel.root.style.visibility = online ? Visibility.Hidden : Visibility.Visible;
            iso?.SetBackground(true); // keep the town (and an auto-walking character) going behind the map
            // Mark where the character is on the blueprint and bring it into view.
            var at = iso?.PlayerPosition;
            map.marker = at;
            followOnMap = true;
            if (at != null)
            {
                autoFit = false;
                // The map was hidden, so it has no size until the next layout: centre it then.
                map.CenterOnceLaidOut(at.Value);
            }
            map.Redraw();
        }

        /// <summary>Signed in: whoever's playing goes online (if they aren't yet).</summary>
        const string WEATHER_KEY = "porchlight.weather";
        bool raining, snowing;

        /// <summary>The Biomes list: this browser's weather, from town to town (not shared with others).</summary>
        void SetWeather(bool rain, bool snow)
        {
            raining = rain;
            snowing = snow;
            platform.Save(WEATHER_KEY, (rain ? "rain," : "") + (snow ? "snow" : ""));
            iso?.SetWeather(rain, snow);
        }

        /// <summary>Out of the county (a town loaded afresh, or signed out): the spaces built round the town go.</summary>
        void StopCounty()
        {
            county?.Dispose();
            county = null;
            if (netFrame != null) netFrame.county = null;
            overlay?.SetCounty(null, null, null, null, null);
            overlay?.SetLoading(null);
        }

        /// <summary>Where you are in the county (world tiles), for its map.</summary>
        Vec2? WhereInCounty()
        {
            var at = iso?.PlayerPosition;
            var here = county?.Active;
            return at != null && here != null ? here.ToWorld(at.Value) : (Vec2?)null;
        }

        /// <summary>On a trip to another town, the next town on the way there (built before any other); null: none.</summary>
        Space TripNextTown()
        {
            if (tripTo == null || iso == null || !iso.OnTrip || county == null) return null;
            var dest = tripTown != null ? TownSpace(tripTown) : null;
            var from = WhereInCounty();
            return dest != null && from != null ? CountyRoute.NextTown(county.map, county.Active, from.Value, dest) : null;
        }

        /// <summary>On a trip to another town, the way there from where you are (world tiles), for the county's map; null: none.</summary>
        List<Vec2> TripWay()
        {
            var from = WhereInCounty();
            var dest = tripTown != null ? TownSpace(tripTown) : null;
            var here = county?.Active;
            if (tripTo == null || from == null || dest == null || here == null || here.id == dest.id) return null;
            var stop = CountyRoute.Next(county.map, here, from.Value, dest);
            if (stop == null) return null;
            var way = new List<Vec2> { from.Value, stop.Value.at };
            way.AddRange(CountyRoute.Ahead(county.map, here, from.Value, dest, double.PositiveInfinity));
            return way;
        }

        /// <summary>
        /// Signed in: the county round the town on screen (Porchlight, the cities, and the account's own
        /// towns in their places), so you can drive and fly on out of it to the others.
        /// </summary>
        void StartCounty()
        {
            if (!online || iso?.world == null || county != null) return;
            string key = TownKey;
            var on = model;
            Account.ListTowns(platform, origin, (list, err) =>
            {
                if (list == null || county != null || !exploring || iso?.world == null || iso.world.model != on || key != TownKey) return;
                var placed = new List<PlacedTown>();
                foreach (var t in list.towns)
                {
                    Place? where = null;
                    if (!t.mine && t.place != null && Enum.TryParse(t.place, true, out Place p)) where = p;
                    else if (t.mine && t.slot >= 0 && t.slot < 3) where = CountyMap.MineSlot(t.slot);
                    if (where == null) continue;
                    var state = ParseUrlState(t.key);
                    placed.Add(new PlacedTown { place = where.Value, name = t.name, key = t.key, width = (int)state.@params.width, height = (int)state.@params.height });
                }
                CountyMap plan;
                try
                {
                    plan = CountyMap.Build(placed, list.account);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("county: " + e.Message);
                    return;
                }
                var here = plan.spaces.Find(sp => sp.kind == SpaceKind.Town && sp.townKey == key);
                if (here == null) return; // not a town of the county (one someone else made)
                county = new CountyDrive(iso, plan, here, k =>
                {
                    var st = ParseUrlState(k);
                    return (st.seed, st.@params);
                })
                { Net = net, Frame = netFrame, OnSpace = DroveInto, Heading = TripNextTown };
                county.EnterRoom();
                // Now the houses in the county's other towns have roads to them.
                OfferDirections();
                // And the big map has a County tab.
                overlay?.SetCounty(plan, county.Terrain, WhereInCounty, TripWay, playing?.home?.townKey);
            });
        }

        /// <summary>Driven (or flown) into another space of the county: into a town, it's the town on screen now.</summary>
        void DroveInto(Space space, TownModel town)
        {
            if (space.kind != SpaceKind.Town || town == null)
            {
                // On the road: no house of yours or neighbours' here.
                overlay?.SetHome(null, null);
                overlay?.SetNeighbours(null);
                if (leftTownKey != TownKey) leftTownKey = TownKey;
                leftTownAt = platform.Now();
                return;
            }
            bool back = space.townKey == leftTownKey && platform.Now() - leftTownAt < WELCOME_AGAIN_MS;
            var state = ParseUrlState(space.townKey);
            seed = state.seed;
            @params = state.@params;
            model = town;
            isoTown = town;
            sidebar.SetSeed(seed);
            sidebar.SetParams(@params);
            map.marker = null;
            map.SetModel(model);
            map.Redraw();
            overlay?.SetHome(DoorOfHome()?.at, HouseOfHome()?.footprint);
            overlay?.SetNeighbours(null);
            RefreshNeighbours();
            if (!back) overlay?.Flash($"Welcome to {space.name}");
            if (playing != null) OfferJoins();
        }

        /// <summary>The town you last went out of onto the road, and when (ms): back in it soon after, no welcome.</summary>
        string leftTownKey;
        double leftTownAt;
        const double WELCOME_AGAIN_MS = 20000;

        void Connect()
        {
            if (playing == null || iso == null || net != null) return;
            netFrame = new NetFrame(iso);
            net = new Multiplayer(netFrame, playing, origin, platform, timers);
            netFrame.Room = () => net?.RoomKey;
            net.onStatus = s => sidebar.SetOnline(s.state, s.here, s.why);
            net.onUnlockedElsewhere = SayUnlocked;
            net.onInUse = () => SwitchCharacter(InUse(playing));
            iso.TeleportHook = (x, z) =>
            {
                if (net == null) return false;
                var (rx, rz) = netFrame.ToRoom(x, z);
                return net.Teleport(rx, rz);
            };
            if (county != null)
            {
                county.Net = net;
                county.Frame = netFrame;
            }
        }

        /// <summary>Play the town on screen with everyone else in it (no-op when it's already the one).</summary>
        void EnterTown()
        {
            if (isoTown != model) return;
            if (county != null) county.EnterRoom();
            else net?.Enter(new TownKey { key = EncodeUrlState(seed, @params), width = model.width, height = model.height });
            overlay?.SetHome(DoorOfHome()?.at, HouseOfHome()?.footprint);
            overlay?.SetNeighbours(null);
            RefreshNeighbours();
            // Joining is for someone in another town, and which that is has just changed.
            if (playing != null) OfferJoins();
        }

        /// <summary>
        /// Signed in while one of the account's characters is already playing (another
        /// tab, another device, someone else on this sign-in) in another town: a button
        /// under the name to go straight to them.
        /// </summary>
        void OfferJoins()
        {
            string mine = playing?.id;
            Account.ListCharacters(platform, origin, (list, err) =>
            {
                if (list == null || playing?.id != mine) return;
                accountCharacters = list;
                OfferDirections();
                var others = list.Where(c => c.inUse && !string.IsNullOrEmpty(c.town) && c.id != mine && c.town != TownKey).ToList();
                string shown = string.Join(",", others.Select(c => c.id));
                if (shown == joinsShown) return;
                joinsShown = shown;
                sidebar.SetJoins(others.Select(c => (c.name, (Action)(() => JoinWhereverNow(c.id)))).ToList());
            });
        }

        /// <summary>The account's characters, as last asked (OfferJoins): whose houses there are directions to.</summary>
        List<Character> accountCharacters;
        /// <summary>Whose house (a character's id) you're being shown the way to, across the county; null: nobody's.</summary>
        string tripTo;
        /// <summary>The town (its key) that house is in.</summary>
        string tripTown;
        /// <summary>The way shown runs on past the space you're in all the way to the town's gate (so its end, and every dot laid back from it, stays put).</summary>
        const double TRIP_AHEAD = double.PositiveInfinity;

        /// <summary>
        /// The sidebar's Directions: the account's houses, in this town or in any town of the county
        /// (your own towns too, out along the highways); the one on the way there ticked.
        /// </summary>
        void OfferDirections()
        {
            // A town loaded afresh ends any trip without a word.
            if (iso == null || !iso.OnTrip) tripTo = null;
            if (playing == null || iso == null || accountCharacters == null)
            {
                sidebar.SetDirections(null);
                return;
            }
            var rows = new List<(string name, string where, bool on, Action go)>();
            foreach (var c in accountCharacters)
            {
                var h = c.home;
                if (h == null || !h.hasHouse || string.IsNullOrEmpty(h.townKey) || TownSpace(h.townKey) == null && h.townKey != TownKey) continue;
                string label = c.id == playing.id ? "your house" : $"{c.name}'s house";
                string name = c.id == playing.id ? "Your house" : $"{c.name}'s house";
                string id = c.id, townKey = h.townKey, townName = h.townName;
                int x = h.x, y = h.y;
                rows.Add((name, h.townKey == TownKey ? null : h.townName, tripTo == c.id, () => DirectionsTo(id, townKey, townName, x, y, label)));
            }
            sidebar.SetDirections(rows);
        }

        /// <summary>The county's space for this town (by its key), if it's one of the county's.</summary>
        Space TownSpace(string townKey) => county?.map.spaces.Find(s => s.kind == SpaceKind.Town && s.townKey == townKey);

        /// <summary>
        /// The way to a house, shown as you go: in another town of the county, out by the right gate and
        /// along the highways (CountyRoute), then along its streets to the door. Already on the way there: stop.
        /// </summary>
        void DirectionsTo(string id, string townKey, string townName, int x, int y, string label)
        {
            if (iso == null) return;
            if (tripTo == id)
            {
                iso.Trip(null);
                return;
            }
            if (TownSpace(townKey) == null && townKey != TownKey)
            {
                overlay?.Flash($"There's no road to {townName} from here");
                return;
            }
            tripTo = id;
            tripTown = townKey;
            iso.Trip(from =>
            {
                if (iso?.world == null) return null;
                var here = county?.Active;
                bool there = here != null ? here.kind == SpaceKind.Town && here.townKey == townKey : townKey == TownKey;
                if (there)
                {
                    var house = Homes.At(iso.world.model.buildings, x, y);
                    var door = house != null ? Homes.Door(iso.world.rooms, house.id) : null;
                    if (door == null) return null;
                    // A door by the edge of town: the way ends a little way in from it (where the town still has you),
                    // never out over the edge, where the road would take you back out of town and in again.
                    var m = iso.world.model;
                    const double IN = 2;
                    var at = new Vec2(Math.Max(IN, Math.Min(m.width - IN, door.Value.at.x)), Math.Max(IN, Math.Min(m.height - IN, door.Value.at.y)));
                    return (at, label, true, (List<Vec2>)null);
                }
                var dest = TownSpace(townKey);
                if (here == null || dest == null) return null;
                var w = here.ToWorld(from);
                var stop = CountyRoute.Next(county.map, here, w, dest);
                if (stop == null) return null;
                // And on along the highways, in this space's terms, for the dots to run on over.
                var ahead = CountyRoute.Ahead(county.map, here, w, dest, TRIP_AHEAD).Select(here.ToLocal).ToList();
                return (here.ToLocal(stop.Value.at), stop.Value.name, false, ahead);
            });
            overlay?.Flash(townKey == TownKey ? $"Directions to {label}" : $"Directions to {label} in {townName}: {Directions.FormatDistance(TripLength(townKey))} by road");
            OfferDirections();
        }

        /// <summary>From where you are to the town, along the highways (tiles, roughly).</summary>
        double TripLength(string townKey)
        {
            var here = county?.Active;
            var dest = TownSpace(townKey);
            var at = iso?.PlayerPosition;
            if (here == null || dest == null || at == null) return 0;
            return CountyRoute.Next(county.map, here, here.ToWorld(at.Value), dest)?.remaining ?? 0;
        }

        /// <summary>A Join button: they may have moved on since it appeared, so ask where they are now.</summary>
        void JoinWhereverNow(string id)
        {
            string mine = playing?.id;
            Account.ListCharacters(platform, origin, (list, _) =>
            {
                if (list == null || playing?.id != mine) return;
                var them = list.Find(c => c.id == id);
                if (them != null && them.inUse && !string.IsNullOrEmpty(them.town) && them.town != TownKey) Join(them);
                else OfferJoins();
            });
        }

        /// <summary>Into their town (if we're not there), arriving beside them once they're seen.</summary>
        void Join(Character them)
        {
            ShowLoader("town", TownSpace(them.town)?.name);
            AfterPaint(() => GoTo(them));
        }

        async void GoTo(Character them)
        {
            await Run(ShowTownSteps(them.town), "town/");
            enteringTown = true;
            joining = them.name;
            joiningSession = them.session;
            joiningUntil = platform.Now() + JOIN_FIND_MS;
            _ = Explore();
        }

        /// <summary>Someone else is playing: back to the front door (saying why, if there's a reason to).</summary>
        void SwitchCharacter(string message = null)
        {
            EndMission(null);
            journal?.Close();
            net?.Close();
            net = null;
            if (county != null) county.Net = null;
            if (iso != null) iso.TeleportHook = null;
            FrontDoor.Forget(platform);
            playing = null;
            joining = null;
            joinsShown = "";
            sidebar.SetJoins(null);
            accountCharacters = null;
            tripTo = null;
            iso?.Trip(null);
            sidebar.SetDirections(null);
            LeaveExplore(true);
            OpenFrontDoor(message);
        }

        /// <summary>Night or day: online, for everyone in the town (the room says when it's done).</summary>
        void ToggleNight()
        {
            if (iso == null) return;
            if (net != null && net.Online) net.SetNight(!iso.IsNight);
            else iso.SetTownNight(!iso.IsNight);
        }

        // The boxes by the minimap: your character and your car.

        /// <summary>From the controller: open this grid (the cursor on the current one), or with it open, move the cursor on one.</summary>
        void StepChooser(string kind, Action open)
        {
            if (overlay.ChooserKind == kind)
            {
                overlay.Highlight(1);
                return;
            }
            open();
            overlay.Highlight(0);
        }

        /// <summary>The character box: a grid of everyone to be (and the firefighter and every animal, once they're unlocked), and you become the one picked.</summary>
        void ChooseLook()
        {
            if (iso == null) return;
            var looks = Catalog.CHARACTERS
                .Concat(Unlocks.Contains("firefighter") ? new[] { Catalog.FIREFIGHTER } : new string[0])
                .Concat(Unlocks.Contains("animals") ? Catalog.ANIMALS : new string[0]).ToList();
            var names = looks.Select((l, i) => Catalog.IsAnimal(l) ? Catalog.AnimalName(l) : l == Catalog.FIREFIGHTER ? "Firefighter" : $"Person {i + 1}").ToList();
            overlay.Choose("Choose your character", portraits.LookStills(looks), names,
                looks.IndexOf(iso.LookModel), i =>
                {
                    var look = iso.ChangeLook(looks[i]);
                    if (look != null) net?.LookChanged(look);
                }, "look");
        }

        /// <summary>Dev mode's unlocks: blank each time, and gone with the page.</summary>
        readonly HashSet<string> devUnlocks = new HashSet<string>();

        /// <summary>What's unlocked (by finishing missions): the account's, for every character in every town, which the server keeps.</summary>
        HashSet<string> Unlocks => net != null ? net.unlocks : devUnlocks;

        /// <summary>A mission's unlock: once, so playing the mission again earns nothing new; kept, and said.</summary>
        void Unlock(string what)
        {
            if (Unlocks.Contains(what)) return;
            if (net != null) net.Unlock(what);
            else devUnlocks.Add(what);
            SayUnlocked(what);
        }

        void SayUnlocked(string what)
        {
            if (what == "animals") overlay?.Notify("Unlocked for all your characters: the zoo's animals. Choose one from the character box.");
            if (what == "firefighter") overlay?.Notify("Unlocked for all your characters: the firefighter and the fire engine. Find them in the character and car boxes.");
            if (what == "sprint") overlay?.Notify("Unlocked for all your characters: a faster sprint. Hold Shift (or RT) to try it.");
        }

        /// <summary>The car box: a grid of every car (the fire engine once it's unlocked), and the one picked is yours, brought right beside you (in place of any other).</summary>
        void ChooseCar()
        {
            if (iso == null) return;
            var models = Catalog.GarageCars(Unlocks);
            var names = models.Select(m => CarName(Catalog.CARS[m])).ToList();
            var stills = portraits.CarStills();
            overlay.Choose("Choose your car", models.Select(m => stills[m]).ToArray(), names, Array.IndexOf(models, carModel), i =>
            {
                carModel = models[i];
                // At the wheel it's swapped under you; otherwise it's brought right beside you.
                if (iso.IsDriving) iso.ChangeCar(carModel);
                else if (!iso.SpawnCar(carModel)) overlay.Flash("No room for a car near here. Try by a street.");
                else overlay.Notify(null);
            }, "car");
        }

        /// <summary>"cars/suv-luxury" as "Suv luxury".</summary>
        static string CarName(string model)
        {
            string n = model.Substring(model.LastIndexOf('/') + 1).Replace('-', ' ');
            return char.ToUpper(n[0]) + n.Substring(1);
        }

        void UpdateGarage()
        {
            if (portraits == null) return;
            // The box shows the car you have (one you got into, too: not a boat or a plane, which stay where they are).
            if (iso.CarModel >= 0 && Catalog.IsCar(iso.CarModel)) carModel = iso.CarModel;
            portraits.SetLook(iso.LookModel);
            portraits.SetCar(carModel);
            portraits.Update(Time.deltaTime, exploring);
            if (!garageShown)
            {
                garageShown = true;
                overlay.SetGarage(portraits.person, portraits.car);
            }
        }

        bool garageShown;

        /// <summary>Who else is here, in the sidebar: tick one for the way to them.</summary>
        void UpdatePlayers()
        {
            double now = platform.Now();
            if (now < playersAt) return;
            playersAt = now + 500;
            var list = iso.Others.Select(o => (o.key, o.name)).OrderBy(o => o.name, StringComparer.OrdinalIgnoreCase).ToList();
            if (joining != null)
            {
                // Joined someone: beside them as soon as they're on the map (or not, after a while).
                var them = iso.Others.FirstOrDefault(o => o.key == joiningSession);
                if (them.key == null) them = iso.Others.FirstOrDefault(o => o.name == joining);
                if (them.key != null && net?.Spawned == true)
                {
                    iso.JumpBeside(new Vec2(them.x, them.y));
                    joining = null;
                }
                else if (now > joiningUntil) joining = null;
            }
            // Who else on the account has started or stopped playing.
            if (playing != null && now >= joinsAt)
            {
                joinsAt = now + JOINS_MS;
                OfferJoins();
            }
            sidebar.SetPlayers(list, iso.Finding, key => iso.Find(key));
        }

        void ToggleAutoWalk()
        {
            if (iso == null) return;
            iso.SetAuto(!iso.IsAuto);
        }

        /// <summary>Into the nearest car, or out of this one.</summary>
        void ToggleCar() => iso?.ToggleCar();

        void ToggleCloseUp()
        {
            if (iso == null) return;
            iso.SetCameraMode(iso.Mode == CameraMode.Close ? CameraMode.Iso : CameraMode.Close);
        }

        void ToggleWalkMode()
        {
            if (iso == null) return;
            iso.SetStreetsOnly(!iso.IsStreetsOnly);
        }

        void OnKeyDown(Key code, bool repeat)
        {
            if (!started) return;
            if (exploring)
            {
                if (journal != null)
                {
                    if (code == Key.Escape) journal.Close();
                    return;
                }
                if (Typing()) return;
                if (code == Key.J && !repeat)
                {
                    ToggleJournal();
                    return;
                }
                // A conversation takes the keys (Enter also starts one, beside someone waiting to talk).
                if (code != Key.Escape && mission != null && mission.OnKey(code)) return;
                if (code == Key.N) ToggleNight();
                if (code == Key.R) ToggleWalkMode();
                if (code == Key.T) ToggleAutoWalk();
                if (code == Key.V && !repeat) ToggleCloseUp();
                if (code == Key.C) NewLook();
                if (code == Key.M && !repeat) iso.ToggleMap();
                // On the big map, Tab: the town or the county round it.
                if (code == Key.Tab && !repeat && iso.MapOpen) overlay.SwitchMapTab(1);
                if (code == Key.Escape)
                {
                    if (iso.MapOpen) iso.CloseMap();
                    else if (overlay.Choosing) overlay.CloseChooser();
                    else LeaveExplore();
                }
                return;
            }
            if (Typing() || online) return;
            if (code == Key.Backquote) SetDebugOpen(!panel.IsOpen);
        }

        // Missions: the list from the server, and the one being played.

        /// <summary>Fetch the missions (again): a new or edited one on the server shows up with no new build of the game.</summary>
        void RefreshMissions()
        {
            missionsAsked = true;
            missionsStatus = "Loading…";
            ShowMissions();
            MissionCatalog.List(platform, origin, (list, err) =>
            {
                missions = list;
                missionsStatus = err;
                ShowMissions();
            });
            RefreshProgress();
        }

        /// <summary>How often the account has finished each mission, for the sidebar's list.</summary>
        void RefreshProgress()
        {
            if (!online) return;
            Account.FetchProgress(platform, origin, (p, _) =>
            {
                if (p == null) return;
                finished = p.missions.ToDictionary(m => m.id, m => m.times);
                ShowMissions();
            });
        }

        void ShowMissions() => sidebar.SetMissions(missions, missionsStatus, mission?.id, StartMission, () => EndMission("Mission abandoned"), finished);

        /// <summary>Fetch the mission's story as the server has it now, and start it (in place of any other).</summary>
        void StartMission(string id)
        {
            if (iso == null || !exploring) return;
            if (iso.world?.model.IsRoad == true)
            {
                overlay.Flash("Missions start in town.");
                return;
            }
            missionStarting = id;
            overlay.Flash("Starting the mission…");
            MissionCatalog.Get(platform, origin, id, (info, story, err) =>
            {
                if (missionStarting != id) return;
                missionStarting = null;
                if (!exploring || iso?.world == null) return;
                if (story == null)
                {
                    overlay.Notify($"Couldn't start the mission: {err}");
                    return;
                }
                // This town's best times first: the story may say the record from its first line.
                FetchTimes(id, times =>
                {
                    if (missionStarting != null || !exploring || iso?.world == null) return;
                    EndMission(null);
                    string town = TownKey;
                    try
                    {
                        mission = new MissionPlay(id, info.title, story, iso, ModelLibrary.Instance, input, times) { OnUnlock = Unlock, OnRecord = seconds => KeepTime(town, id, seconds) };
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        overlay.Notify($"Couldn't start the mission: {e.Message}");
                        return;
                    }
                    overlay.Flash($"Mission: {info.title}");
                    ShowMissions();
                });
            });
        }

        /// <summary>Dev mode's best times (town key and mission → seconds): gone with the page.</summary>
        readonly Dictionary<string, double> devTimes = new Dictionary<string, double>();

        /// <summary>This mission's best times in the town on screen: the server's (signed in), else dev mode's own; none if the server doesn't answer.</summary>
        void FetchTimes(string id, Action<MissionPlay.Times> done)
        {
            string key = $"{TownKey}\n{id}";
            if (!online || playing == null)
            {
                devTimes.TryGetValue(key, out double mine);
                done(new MissionPlay.Times { mine = mine, best = mine, holder = mine > 0 ? "you" : "" });
                return;
            }
            Account.FetchRecords(platform, origin, TownKey, id, (board, _) =>
            {
                var times = new MissionPlay.Times();
                if (board != null)
                {
                    times.mine = board.mine;
                    if (board.best.Count > 0)
                    {
                        times.best = board.best[0].seconds;
                        times.holder = board.best[0].name ?? "";
                    }
                }
                done(times);
            });
        }

        /// <summary>A time the mission keeps: for this town, with the account (signed in) or for the session (dev mode).</summary>
        void KeepTime(string town, string id, double seconds)
        {
            if (!online || playing == null)
            {
                string key = $"{town}\n{id}";
                if (!devTimes.TryGetValue(key, out double had) || seconds < had) devTimes[key] = seconds;
                return;
            }
            Account.KeepRecord(platform, origin, town, id, playing.id, seconds, (_, err) =>
            {
                if (err != null) overlay?.Notify($"Couldn't keep your time: {err}");
            });
        }

        /// <summary>Stop the mission being played (and say so, unless `notice` is null).</summary>
        void EndMission(string notice)
        {
            if (mission == null) return;
            mission.Dispose();
            mission = null;
            overlay?.SetObjective(null, null);
            missionPanel?.Hide();
            if (notice != null) overlay?.Flash(notice);
            ShowMissions();
        }

        void UpdateMission()
        {
            if (mission == null || !exploring) return;
            mission.Update(Time.deltaTime);
            var script = mission.script;
            if (script.Done)
            {
                if (script.Error != null)
                {
                    Debug.LogWarning($"porchlight: mission {mission.id} stopped: {script.Error}");
                    string title = mission.title;
                    EndMission(null);
                    overlay.Notify($"{title} stopped: {script.Error}");
                }
                else
                {
                    // Played to its end: the account's for good (abandoning or an error doesn't count).
                    net?.MissionDone(mission.id);
                    finished.TryGetValue(mission.id, out int times);
                    finished[mission.id] = times + 1;
                    EndMission($"Mission complete: {mission.title}");
                }
                return;
            }
            overlay.SetObjective(mission.title, mission.CurrentObjective);
            missionPanel.Show(script.Line, script.Choices, mission.Highlighted, input.Connected, mission.Advance, mission.Choose);
        }

        /// <summary>
        /// While the map is showing, follow the explore-mode character: move the pin
        /// (and redraw auto-walk's route) as they walk, and keep them in view.
        /// </summary>
        void Follow()
        {
            double now = platform.Now();
            if (exploring || iso == null || isoTown != model || now - lastFollow < 66) return;
            var at = iso.PlayerPosition;
            if (at == null) return;
            var was = map.marker;
            if (was != null && Math.Abs(was.Value.x - at.Value.x) < 1e-4 && Math.Abs(was.Value.y - at.Value.y) < 1e-4) return;
            lastFollow = now;
            map.marker = at;
            map.route = iso.Route;
            map.goal = iso.Goal;
            if (followOnMap && map.ViewW > 0 && !map.IsInView(at.Value, 0.12))
            {
                autoFit = false;
                map.CenterOn(at.Value);
            }
            map.Redraw();
        }

        // The URL hash holds the seed and the params that differ from their defaults.

        struct UrlState
        {
            public string seed;
            public GenParams @params;
        }

        UrlState ReadUrlState() => ParseUrlState(platform.Hash.TrimStart('#'));

        /// <summary>A town's key (the hash, without its #: also what its room is called).</summary>
        static UrlState ParseUrlState(string query)
        {
            var hash = ParseQuery(query ?? "");
            var partial = new Dictionary<string, double>();
            foreach (var spec in ParamSpec.All)
                if (hash.TryGetValue(spec.key, out var raw)) partial[spec.key] = Numbers.ParseNumber(raw);
            hash.TryGetValue("seed", out var s);
            s = s?.Trim();
            return new UrlState { seed = string.IsNullOrEmpty(s) ? null : s, @params = GenParams.Normalize(partial) };
        }

        static string EncodeUrlState(string seed, GenParams p)
        {
            var d = GenParams.Defaults;
            var pairs = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("seed", seed) };
            foreach (var spec in ParamSpec.All)
                if (p[spec.key] != d[spec.key]) pairs.Add(new KeyValuePair<string, string>(spec.key, Numbers.NumberToString(p[spec.key])));
            return EncodeQuery(pairs);
        }

        void WriteUrlState()
        {
            if (online) return; // the character says which town, not the URL
            writtenHash = "#" + EncodeUrlState(seed, @params);
            platform.SetHash(writtenHash.Substring(1));
        }

        /// <summary>The hash edited by hand (hashchange): load what it says.</summary>
        void CheckHash()
        {
            if (online) return;
            double now = platform.Now();
            if (now < hashCheckAt) return;
            hashCheckAt = now + 250;
#if UNITY_WEBGL && !UNITY_EDITOR
            string h = platform.Hash;
            if (writtenHash == null || h == writtenHash) return;
            writtenHash = h;
            var state = ReadUrlState();
            sidebar.SetParams(state.@params);
            Regenerate(state.seed ?? RandomSeed(), state.@params);
#endif
        }

        /// <summary>URLSearchParams' parse (application/x-www-form-urlencoded).</summary>
        static Dictionary<string, string> ParseQuery(string s)
        {
            var o = new Dictionary<string, string>();
            foreach (var part in s.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                k = Uri.UnescapeDataString(k.Replace('+', ' '));
                v = Uri.UnescapeDataString(v.Replace('+', ' '));
                if (!o.ContainsKey(k)) o[k] = v;
            }
            return o;
        }

        static string EncodeQuery(IEnumerable<KeyValuePair<string, string>> pairs) =>
            string.Join("&", pairs.Select(kv => FormEncode(kv.Key) + "=" + FormEncode(kv.Value)));

        /// <summary>URLSearchParams' serializer: alphanumerics and *-._ as they are, space as +, the rest %XX of UTF-8.</summary>
        static string FormEncode(string s)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                char c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '*' || c == '-' || c == '.' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('+');
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        const string ALPHABET = "abcdefghjkmnpqrstuvwxyz23456789";
        static readonly System.Random entropy = new System.Random();

        /// <summary>New seeds for the Regenerate button (UI-side entropy, not generator randomness).</summary>
        static string RandomSeed(int length = 6)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++) sb.Append(ALPHABET[entropy.Next(256) % ALPHABET.Length]);
            return sb.ToString();
        }

        /// <summary>Readout for the tile under the cursor.</summary>
        static string DescribeTile(TownModel model, Vec2? world)
        {
            if (model == null || world == null) return "";
            int x = (int)Math.Floor(world.Value.x), y = (int)Math.Floor(world.Value.y);
            if (x < 0 || y < 0 || x >= model.width || y >= model.height) return "";
            int i = y * model.width + x;
            var t = model.tiles;
            int id = t.buildingId[i];
            string building = id == -1 ? "—" : $"{id} ({(id < model.buildings.Count ? model.buildings[id].kind.ToString().ToLowerInvariant() : "?")})";
            byte terrain = t.terrain[i], over = t.overlay[i];
            return string.Join("  ·  ", new[]
            {
                $"tile {x}, {y}",
                $"terrain {(terrain < Terrain.Names.Length ? Terrain.Names[terrain] : terrain.ToString())}",
                $"overlay {(over < Overlay.Names.Length ? Overlay.Names[over] : over.ToString())}",
                $"building {building}",
                $"walkable {(t.walkable[i] != 0 ? "yes" : "no")}",
            });
        }
    }
}
