// The whole app: sign in (when the page is
// served by the auth service), grow a town from the seed in the URL, show its
// blueprint with the sidebar, and on Explore walk it in 3D with everyone else
// in the same town. One of these sits in the only scene (Boot.unity).

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
        ExploreControls controls;
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
        /// <summary>Asked who's playing (choosing to play alone counts).</summary>
        bool asked;
        double lastFollow;
        bool started;

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
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;
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
            Account.FetchMe(platform, origin, (u, provider, err, status) =>
            {
                if (u != null)
                {
                    user = u;
                    Start2();
                    return;
                }
                // No sign-in service here (a static host, or a dev server without
                // Keycloak or the game server behind it): play alone.
                bool absent = status == 0 || status == 404 || status == 502 || status == 503 || status == 504;
                if (absent && error == null)
                {
                    Debug.Log("porchlight: no sign-in service (" + err + "); playing offline");
                    Start2();
                    return;
                }
                SignInGate.Show(root, provider, error ?? err, () =>
                {
                    string returnTo = platform.PathName + platform.Search + platform.Hash;
                    platform.Navigate("/auth/login?returnTo=" + Uri.EscapeDataString(returnTo));
                });
            });
        }

        void Start2()
        {
            online = user != null;
            BuildApp();
            started = true;
            if (Environment.GetEnvironmentVariable("PORCHLIGHT_SMOKE") == "1") _ = Smoke();
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
            LeaveExplore();
            Regenerate(RandomSeed(), @params);
            Debug.Log($"smoke: regenerated {seed}: {statusLabel.text}");
            Debug.Log("smoke: done");
            Application.Quit(0);
        }

        void BuildApp()
        {
            var initial = ReadUrlState();
            seed = initial.seed ?? RandomSeed();
            @params = initial.@params;

            map = new BlueprintView { name = "map" };
            map.WheelDelta = () => input.WheelDeltaY;
            map.OnViewChanged = () => autoFit = false;
            map.OnHover = world => hoverLabel.text = DescribeTile(model, world);
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

            panel = new DebugPanel(@params, map.visibleLayers, ChangeParams, ResetParams, ToggleLayer);
            panel.SetOpen(false);
            root.Add(panel.root);

            controls = Sidebar.BuildExploreControls();
            var host = new VisualElement();
            root.Add(host);
            sidebar = new Sidebar(host, @params, map.visibleLayers, new SidebarHandlers
            {
                OnParamsChange = ChangeParams,
                OnRegenerate = () => Regenerate(RandomSeed(), @params),
                OnSeed = value => Regenerate(value, @params),
                OnExplore = () => _ = Explore(),
                OnBack = LeaveExplore,
                OnResetParams = ResetParams,
                OnLayerToggle = ToggleLayer,
                OnFitView = Fit,
                OnExportJson = () => Export.Json(platform, model),
                OnExportPng = () => StartCoroutine(Export.Png(platform, model, map.visibleLayers, panelSettings)),
                OnToggleDebug = () => SetDebugOpen(!panel.IsOpen),
            }, controls, SetSidebarOpen);
            if (user != null) sidebar.SetAccount(user.name, user.email, () => platform.PostForm("/auth/logout"));

            controls.walkMode.clicked += ToggleWalkMode;
            controls.autoWalk.clicked += ToggleAutoWalk;
            controls.car.clicked += ToggleCar;
            controls.dayNight.clicked += ToggleNight;
            controls.newChar.clicked += NewLook;
            controls.turnLeft.clicked += () => iso?.Rotate(-1);
            controls.turnRight.clicked += () => iso?.Rotate(1);
            controls.lookAhead.clicked += () => iso?.LookAlongWalk();
            controls.closeUp.clicked += ToggleCloseUp;
            controls.tiltDown.clicked += () => iso?.Tilt(-1);
            controls.tiltUp.clicked += () => iso?.Tilt(1);

            // Small screens start with the map, not the controls.
            if (Screen.width / dpr <= 720) SetSidebarOpen(false);
            if (platform.Load(DEBUG_KEY) == "1") SetDebugOpen(true);

            Regenerate(seed, @params);
        }

        bool Typing()
        {
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
            if (!started) return;
            root.EnableInClassList("narrow", Screen.width / dpr <= 720);
            if (iso != null)
            {
                iso.viewport = ScreenRect(isoHost);
                if (exploring) ZoomWithWheel();
                iso.Update();
            }
            Follow();
            CheckHash();
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
                iso.SetTown(model);
                isoTown = model;
                SpawnCharacter();
                EnterTown();
            }
            else if (iso != null && iso.IsAuto)
            {
                // Regenerated from the map: stop the old town's auto-walk running behind it.
                iso.SetAuto(false);
                SyncAutoWalkButton();
            }
        }

        void Fit()
        {
            autoFit = true;
            map.Fit();
            map.Redraw();
        }

        /// <summary>Params changed in one control surface: mirror them in the other and rebuild.</summary>
        void ChangeParams(GenParams next)
        {
            panel.SetParams(next);
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
            sidebar.SetDebugOpen(open);
            platform.Save(DEBUG_KEY, open ? "1" : "0");
        }

        // Explore mode: the 3D town, loaded on first use.

        void LoadIso()
        {
            overlay = new ExploreOverlay(isoHost) { Dpr = dpr };
            iso = new ExploreView(camera3d, ModelLibrary.Instance, overlay, input);
            iso.OnCarChange = SyncCarButton;
            iso.OnAutoChange = SyncAutoWalkButton;
            iso.OnPadAction = action =>
            {
                if (action == PadAction.Menu)
                {
                    if (exploring) LeaveExplore();
                    else _ = Explore();
                    return;
                }
                if (!exploring) return;
                if (action == PadAction.Car) ToggleCar();
                if (action == PadAction.Auto) ToggleAutoWalk();
                if (action == PadAction.CloseUp) ToggleCloseUp();
                if (action == PadAction.Night) ToggleNight();
                if (action == PadAction.NewCharacter) NewLook();
            };
        }

        void SpawnCharacter()
        {
            iso?.Spawn(Catalog.RandomLook(new Rng(RandomSeed())));
            SyncAutoWalkButton();
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
                    iso.SetTown(model);
                    isoTown = model;
                    SpawnCharacter();
                }
                else if (iso.PlayerPosition == null) SpawnCharacter();
                // Same town as last time: carry on as the same character, where you left off.
                // Online: who's playing (asked once), then into this town's room.
                Connect(EnterTown);
            }
            catch (Exception err)
            {
                Debug.LogException(err);
                statusLabel.text = $"couldn't start explore mode: {err.Message}";
                statusLabel.AddToClassList("error");
                LeaveExplore();
            }
            finally
            {
                sidebar.SetLoading(false);
            }
        }

        void LeaveExplore()
        {
            exploring = false;
            root.RemoveFromClassList("exploring");
            sidebar.SetExploring(false);
            isoHost.style.display = DisplayStyle.None;
            map.style.display = DisplayStyle.Flex;
            footer.style.display = DisplayStyle.Flex;
            panel.root.style.visibility = Visibility.Visible;
            iso?.SetBackground(true); // keep the town (and an auto-walking character) going behind the map
            // Mark where the character is on the blueprint and bring it into view.
            var at = iso?.PlayerPosition;
            map.marker = at;
            if (at != null)
            {
                autoFit = false;
                map.CenterOn(at.Value);
            }
            map.Redraw();
        }

        /// <summary>Online: ask (once) who's playing, then join their town's room.</summary>
        void Connect(Action then)
        {
            if (!online || asked || iso == null)
            {
                then();
                return;
            }
            asked = true;
            CharacterPicker.Remembered(platform, origin, remembered =>
            {
                void Use(Character character)
                {
                    sidebar.SetCharacter(character?.name, SwitchCharacter);
                    if (character == null || iso == null)
                    {
                        then();
                        return;
                    }
                    net = new Multiplayer(iso, character, origin, platform, timers);
                    net.onStatus = s => sidebar.SetOnline(s.state, s.here, s.why);
                    iso.TeleportHook = (x, z) => net?.Teleport(x, z) ?? false;
                    then();
                }
                if (remembered != null) Use(remembered);
                else CharacterPicker.Pick(root, platform, origin, () => Catalog.RandomLook(new Rng(RandomSeed())), Use);
            });
        }

        /// <summary>Play the town on screen with everyone else in it (no-op when it's already the one).</summary>
        void EnterTown()
        {
            if (isoTown == model) net?.Enter(new TownKey { key = EncodeUrlState(seed, @params), width = model.width, height = model.height });
        }

        void SwitchCharacter()
        {
            net?.Close();
            net = null;
            if (iso != null) iso.TeleportHook = null;
            CharacterPicker.Forget(platform);
            asked = false;
            Connect(EnterTown);
        }

        void SyncAutoWalkButton()
        {
            bool on = iso?.IsAuto ?? false;
            string what = iso != null && iso.IsDriving ? "auto drive" : "auto walk";
            controls.autoWalk.text = on ? $"Stop {what}" : char.ToUpper(what[0]) + what.Substring(1);
            controls.autoWalk.EnableInClassList("active", on);
        }

        void ToggleNight()
        {
            if (iso == null) return;
            iso.SetNight(!iso.IsNight);
            controls.dayNight.text = iso.IsNight ? "Day" : "Night";
        }

        void ToggleAutoWalk()
        {
            if (iso == null) return;
            iso.SetAuto(!iso.IsAuto);
            SyncAutoWalkButton();
        }

        /// <summary>Into the nearest car, or out of this one.</summary>
        void ToggleCar()
        {
            if (iso == null) return;
            if (!iso.ToggleCar())
            {
                // No car near enough to get into, or nowhere to get out.
                controls.car.AddToClassList("nudge");
                timers.After(400, () => controls.car.RemoveFromClassList("nudge"));
            }
        }

        /// <summary>In or out of a car: the car button follows, and auto walk becomes auto drive (or back).</summary>
        void SyncCarButton(bool driving)
        {
            controls.car.text = driving ? "Get out" : "Get in car";
            controls.car.EnableInClassList("active", driving);
            SyncAutoWalkButton();
        }

        void ToggleCloseUp()
        {
            if (iso == null) return;
            iso.SetCameraMode(iso.Mode == CameraMode.Close ? CameraMode.Iso : CameraMode.Close);
            bool close = iso.Mode == CameraMode.Close;
            controls.closeUp.text = close ? "Overview" : "Close up";
            controls.closeUp.EnableInClassList("active", close);
        }

        void ToggleWalkMode()
        {
            if (iso == null) return;
            iso.SetStreetsOnly(!iso.IsStreetsOnly);
            controls.walkMode.text = iso.IsStreetsOnly ? "Walk: streets only" : "Walk: anywhere";
        }

        void OnKeyDown(Key code, bool repeat)
        {
            if (!started) return;
            if (exploring)
            {
                if (Typing()) return;
                if (code == Key.N) ToggleNight();
                if (code == Key.R) ToggleWalkMode();
                if (code == Key.T) ToggleAutoWalk();
                if (code == Key.V && !repeat) ToggleCloseUp();
                if (code == Key.C) NewLook();
                if (code == Key.Escape) LeaveExplore();
                return;
            }
            if (Typing()) return;
            if (code == Key.Backquote) SetDebugOpen(!panel.IsOpen);
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
            if (!map.IsInView(at.Value, 0.12))
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

        UrlState ReadUrlState()
        {
            var hash = ParseQuery(platform.Hash.TrimStart('#'));
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
            writtenHash = "#" + EncodeUrlState(seed, @params);
            platform.SetHash(writtenHash.Substring(1));
        }

        /// <summary>The hash edited by hand (hashchange): load what it says.</summary>
        void CheckHash()
        {
            double now = platform.Now();
            if (now < hashCheckAt) return;
            hashCheckAt = now + 250;
#if UNITY_WEBGL && !UNITY_EDITOR
            string h = platform.Hash;
            if (writtenHash == null || h == writtenHash) return;
            writtenHash = h;
            var state = ReadUrlState();
            panel.SetParams(state.@params);
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
