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
#endif
            BuildCameras();
            BuildUi();
            BeginSignIn();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern float PL_Dpr();
        static float PlatformDpr() => PL_Dpr();
#endif

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
            loader = El.Box("loader");
            loader.Add(El.Text("Loading Porchlight…", "loader-text"));
            loader.Add(El.Box("loader-bar"));
            root.Add(loader);
        }

        // The loader: carries on from the page's, over everything, until there's a real screen
        // to show. Building a town stops everything for a moment, so it has to be drawn first.

        VisualElement loader;
        Action afterPaint;
        int paintFrames;
        /// <summary>Going into a town: the loader stays till they're in it and in their place.</summary>
        bool enteringTown;
        double enteringUntil;
        const double ENTERING_MS = 5000;

        bool Loading => loader.style.display != DisplayStyle.None;

        void ShowLoader() => loader.style.display = DisplayStyle.Flex;

        void HideLoader()
        {
            loader.style.display = DisplayStyle.None;
            enteringTown = false;
            enteringUntil = 0;
        }

        /// <summary>Do something heavy once what's on screen now has been drawn.</summary>
        void AfterPaint(Action then)
        {
            afterPaint = then;
            paintFrames = 2;
        }

        void UpdateLoader()
        {
            if (afterPaint != null && --paintFrames <= 0)
            {
                var then = afterPaint;
                afterPaint = null;
                then();
            }
            if (!Loading) return;
            if (root[root.childCount - 1] != loader) loader.BringToFront();
            if (!enteringTown || !exploring) return;
            double now = platform.Now();
            if (enteringUntil == 0) enteringUntil = now + ENTERING_MS;
            if ((net == null || net.Spawned) && joining == null || now > enteringUntil) HideLoader();
        }

        // Sign-in: nothing starts until someone is signed in, when the
        // page is served by the auth service.

        void BeginSignIn()
        {
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
                        ShowLoader();
                        AskWho(null, platform.Now() + SERVER_WAIT_MS);
                    }, "Try again");
                    HideLoader();
                    return;
                }
                // The card straight away; the scenery behind it can follow.
                var gate = SignInGate.Show(root, provider, error ?? err, () =>
                {
                    string returnTo = platform.PathName + platform.Search + platform.Hash;
                    platform.Navigate("/auth/login?returnTo=" + Uri.EscapeDataString(returnTo));
                });
                HideLoader();
                AfterPaint(StartAttract);
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

        /// <summary>A headless run through the app (PORCHLIGHT_SMOKE=1): explore, walk a while, back to the map, quit.</summary>
        async Task Smoke()
        {
            Debug.Log($"smoke: town {seed}: {statusLabel.text}");
            await Explore();
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
            iso?.SetAuto(false);
            var story = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_MISSION");
            if (!string.IsNullOrEmpty(story)) await SmokeMission(System.IO.File.ReadAllText(story));
            var fire = Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE_FIRE");
            if (!string.IsNullOrEmpty(fire)) await SmokeFire(System.IO.File.ReadAllText(fire));
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
                OnMissionsRefresh = RefreshMissions,
            }, SetSidebarOpen);
            if (user != null)
            {
                sidebar.SetAccount(user.name, user.email, () => platform.PostForm("/auth/logout"));
                footer.style.display = DisplayStyle.None;
                panel.root.style.visibility = Visibility.Hidden;
            }

            // Small screens start with the map, not the controls.
            if (Screen.width / dpr <= 720) SetSidebarOpen(false);
            if (!online && platform.Load(DEBUG_KEY) == "1") SetDebugOpen(true);

            Regenerate(seed, @params);
            if (online) OpenFrontDoor();
            else HideLoader();
        }

        // The front door: who's playing, and where they live, then into their town.

        void OpenFrontDoor(string message = null)
        {
            root.AddToClassList("choosing");
            door ??= new FrontDoor(root, platform, origin, () => portraits ??= new Portraits(ModelLibrary.Instance), () => RandomSeed(), () => platform.PostForm("/auth/logout"));
            door.Listed = HideLoader;
            // A reload in this tab carries straight on, where they were.
            FrontDoor.Remembered(platform, origin, c =>
            {
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
            ShowLoader();
            AfterPaint(() => OpenHousePicker(c, note));
        }

        void OpenHousePicker(Character c, string note)
        {
            door.Close();
            root.AddToClassList("choosing");
            ShowTown(c.home.townKey);
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
        void ShowTown(string key)
        {
            if (key == TownKey) return;
            var state = ParseUrlState(key);
            sidebar.SetParams(state.@params);
            Regenerate(state.seed ?? RandomSeed(), state.@params);
        }

        string TownKey => EncodeUrlState(seed, @params);

        /// <summary>Into this character's town, as them: at their front door, or (a reload) where they were.</summary>
        void PlayAs(Character c, bool atDoor)
        {
            ShowLoader();
            AfterPaint(() => EnterAs(c, atDoor));
        }

        void EnterAs(Character c, bool atDoor)
        {
            door.Close();
            net?.Close();
            net = null;
            if (iso != null) iso.TeleportHook = null;
            ShowTown(c.home.townKey);
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
            _ = Explore();
        }

        /// <summary>Into someone else's town as this character, who needn't live anywhere yet.</summary>
        void Visit(Character c, Character them)
        {
            ShowLoader();
            AfterPaint(() => VisitNow(c, them));
        }

        void VisitNow(Character c, Character them)
        {
            door.Close();
            net?.Close();
            net = null;
            if (iso != null) iso.TeleportHook = null;
            playing = c;
            preferHome = false;
            FrontDoor.Remember(platform, c.id);
            root.RemoveFromClassList("choosing");
            sidebar.SetCharacter(c.name, () => SwitchCharacter(), c.home?.townName, c.home?.hasHouse == true ? GoHome : (Action)null, ToggleJournal);
            GoTo(them);
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
            if (home == null || iso == null) return;
            if (home.townKey != TownKey)
            {
                ShowLoader();
                AfterPaint(() =>
                {
                    preferHome = true;
                    ShowTown(home.townKey);
                    enteringTown = true;
                });
                return;
            }
            if (DoorOfHome() is (Vec2 at, double heading)) iso.PutPlayer(at, heading);
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
            if (iso != null)
            {
                iso.viewport = ScreenRect(isoHost);
                if (exploring) ZoomWithWheel();
                iso.Update();
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

        void Regenerate(string nextSeed, GenParams nextParams)
        {
            bool sizeChanged = model == null || model.width != nextParams.width || model.height != nextParams.height;
            seed = nextSeed;
            @params = nextParams;
            var sw = Stopwatch.StartNew();
            try
            {
                model = Generator.Generate(seed, @params, check: Application.isEditor);
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
                EndMission(null);
                iso.SetTown(model);
                isoTown = model;
                SpawnCharacter();
                EnterTown();
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
            iso.OnTake = car => net?.Take(car);
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
            try
            {
                // Let "Loading…" show before the town is built (a frame's work).
                await Task.Yield();
                if (iso == null)
                {
                    isoHost.style.display = DisplayStyle.Flex;
                    LoadIso();
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
                    iso.SetTown(model);
                    isoTown = model;
                    SpawnCharacter();
                }
                else if (iso.PlayerPosition == null) SpawnCharacter();
                // Same town as last time: carry on as the same character, where you left off.
                // Online: into this town's room, as whoever's playing.
                Connect();
                EnterTown();
                if (!missionsAsked) RefreshMissions();
            }
            catch (Exception err)
            {
                Debug.LogException(err);
                statusLabel.text = $"couldn't start explore mode: {err.Message}";
                statusLabel.AddToClassList("error");
                LeaveExplore(true);
                HideLoader();
            }
            finally
            {
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
        void Connect()
        {
            if (playing == null || iso == null || net != null) return;
            net = new Multiplayer(iso, playing, origin, platform, timers);
            net.onStatus = s => sidebar.SetOnline(s.state, s.here, s.why);
            net.onUnlockedElsewhere = SayUnlocked;
            net.onInUse = () => SwitchCharacter(InUse(playing));
            iso.TeleportHook = (x, z) => net?.Teleport(x, z) ?? false;
        }

        /// <summary>Play the town on screen with everyone else in it (no-op when it's already the one).</summary>
        void EnterTown()
        {
            if (isoTown != model) return;
            net?.Enter(new TownKey { key = EncodeUrlState(seed, @params), width = model.width, height = model.height });
            overlay?.SetHome(DoorOfHome()?.at, HouseOfHome()?.footprint);
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
                var others = list.Where(c => c.inUse && !string.IsNullOrEmpty(c.town) && c.id != mine && c.town != TownKey).ToList();
                string shown = string.Join(",", others.Select(c => c.id));
                if (shown == joinsShown) return;
                joinsShown = shown;
                sidebar.SetJoins(others.Select(c => (c.name, (Action)(() => JoinWhereverNow(c.id)))).ToList());
            });
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
            ShowLoader();
            AfterPaint(() => GoTo(them));
        }

        void GoTo(Character them)
        {
            enteringTown = true;
            ShowTown(them.town);
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
            if (iso != null) iso.TeleportHook = null;
            FrontDoor.Forget(platform);
            playing = null;
            joining = null;
            joinsShown = "";
            sidebar.SetJoins(null);
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
                EndMission(null);
                try
                {
                    mission = new MissionPlay(id, info.title, story, iso, ModelLibrary.Instance, input) { OnUnlock = Unlock };
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
