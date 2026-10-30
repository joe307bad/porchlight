// The whole app: sign in (when the page is
// served by the auth service), grow a town from the seed in the URL, show its
// blueprint with the sidebar, and on Explore walk it in 3D with everyone else
// in the same town, and play missions (ink stories the server hands out).
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
        /// <summary>Asked who's playing (choosing to play alone counts).</summary>
        bool asked;
        double lastFollow;
        /// <summary>The map keeps your character in view until you pan or zoom it yourself.</summary>
        bool followOnMap = true;
        bool started;
        /// <summary>The town behind the sign-in card: someone wandering its streets.</summary>
        ExploreView attract;
        /// <summary>Behind the sign-in card, a new town every ATTRACT_EVERY seconds, faded in over the last (a capture of it), day and night in turn.</summary>
        const float ATTRACT_EVERY = 15, ATTRACT_FADE = 3;
        bool attractNight, attractSwapping;
        float attractNext;
        VisualElement attractFade;
        RenderTexture attractShot;
        /// <summary>The card's title: the button's blue by day, white by night, easing over as the towns change.</summary>
        /// <summary>The pictures in the boxes by the minimap, and the car model to bring round.</summary>
        Portraits portraits;
        int carModel;
        double playersAt;
        /// <summary>Who you've joined (by the button under your name), until they're found.</summary>
        string joining;
        double joiningUntil;
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
            // A town fading out on the sign-in screen is drawn on its own, not with the new one.
            int fading = LayerMask.NameToLayer(ExploreView.FADING_LAYER);
            if (fading >= 0) camera3d.cullingMask &= ~(1 << fading);
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
                StartAttract();
                var gate = SignInGate.Show(root, provider, error ?? err, () =>
                {
                    string returnTo = platform.PathName + platform.Search + platform.Hash;
                    platform.Navigate("/auth/login?returnTo=" + Uri.EscapeDataString(returnTo));
                });
            });
        }

        /// <summary>Behind the sign-in card: a fresh little town, by day or night; then another, and another.</summary>
        void StartAttract()
        {
            try
            {
                var host = new VisualElement();
                host.style.display = DisplayStyle.None;
                root.Add(host);
                // The last town, held over the next as it fades away (under the card, which comes after).
                attractFade = new VisualElement { pickingMode = PickingMode.Ignore };
                attractFade.style.position = Position.Absolute;
                attractFade.style.left = attractFade.style.top = attractFade.style.right = attractFade.style.bottom = 0;
                attractFade.style.display = DisplayStyle.None;
                root.Add(attractFade);
                attract = new ExploreView(camera3d, ModelLibrary.Instance, new ExploreOverlay(host) { Dpr = dpr }, input) { Attract = true };
                // Day or night to start with, then each in turn.
                attractNight = new Rng(RandomSeed()).Chance(0.5);
                NextAttractTown();
            }
            catch (Exception err)
            {
                // Only scenery: the card works without it.
                Debug.LogException(err);
                attract = null;
            }
        }

        /// <summary>The next town (no one of yours in it: just the town going about its day). `fade`: keep the last one standing to fade out.</summary>
        void NextAttractTown(bool fade = false)
        {
            var p = GenParams.Defaults;
            // A little town, seen close: light enough for phones and weak machines, its
            // edge about 35 tiles out from the middle, past the ~24 the view's corners reach as it turns.
            p.width = p.height = 96;
            var town = Generator.Generate(RandomSeed(), GenParams.Normalize(p));
            if (fade) attract.SetTownFading(town);
            else attract.SetTown(town);
            attract.SetNightNow(attractNight);
            attractNight = !attractNight;
            attractNext = Time.realtimeSinceStartup + ATTRACT_EVERY;
        }

        /// <summary>
        /// Build the next town and fade it in over the last, which is still drawn live
        /// (into a picture under the card, from the camera as it keeps turning) until
        /// it's gone. Without the Fading layer, the towns just change.
        /// </summary>
        System.Collections.IEnumerator SwapAttractTown()
        {
            attractSwapping = true;
            bool live = ExploreView.CanFade;
            if (live && (attractShot == null || attractShot.width != Screen.width || attractShot.height != Screen.height))
            {
                if (attractShot != null) attractShot.Release();
                attractShot = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32) { name = "last town" };
            }
            try
            {
                NextAttractTown(live);
            }
            catch (Exception err)
            {
                Debug.LogException(err);
                attractNext = Time.realtimeSinceStartup + ATTRACT_EVERY;
                live = false;
            }
            if (!live)
            {
                attractSwapping = false;
                yield break;
            }
            attract.RenderFading(attractShot);
            attractFade.style.backgroundImage = Background.FromRenderTexture(attractShot);
            attractFade.style.opacity = 1;
            attractFade.style.display = DisplayStyle.Flex;
            for (float t = 0; t < ATTRACT_FADE; t += Time.unscaledDeltaTime)
            {
                yield return null;
                if (attract == null) yield break;
                attract.RenderFading(attractShot);
                attractFade.style.opacity = 1 - Mathf.SmoothStep(0, 1, t / ATTRACT_FADE);
            }
            attractFade.style.display = DisplayStyle.None;
            attractFade.style.backgroundImage = StyleKeyword.None;
            attract.EndFade();
            attractSwapping = false;
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
            map.OnViewChanged = () =>
            {
                autoFit = false;
                followOnMap = false;
            };
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

            panel = new DebugPanel(map.visibleLayers, ToggleLayer);
            panel.SetOpen(false);
            root.Add(panel.root);

            var host = new VisualElement();
            root.Add(host);
            sidebar = new Sidebar(host, @params, new SidebarHandlers
            {
                OnParamsChange = ChangeParams,
                OnRegenerate = () => Regenerate(RandomSeed(), @params),
                OnSeed = value => Regenerate(value, @params),
                OnExplore = () => _ = Explore(),
                OnBack = LeaveExplore,
                OnResetParams = ResetParams,
                OnMissionsRefresh = RefreshMissions,
            }, SetSidebarOpen);
            if (user != null)
            {
                sidebar.SetAccount(user.name, user.email, () => platform.PostForm("/auth/logout"));
                OfferJoins();
            }

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
            if (attract != null)
            {
                attract.viewport = new Rect(0, 0, Screen.width, Screen.height);
                attract.Update();
                if (!attractSwapping && Time.realtimeSinceStartup >= attractNext) StartCoroutine(SwapAttractTown());
            }
            if (!started) return;
            root.EnableInClassList("narrow", Screen.width / dpr <= 720);
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
            portraits = new Portraits(ModelLibrary.Instance);
            missionPanel = new MissionPanel(isoHost);
            overlay.OnPersonBox = ChooseLook;
            overlay.OnCarBox = ChooseCar;
            iso.OnPadAction = action =>
            {
                if (action == PadAction.Menu)
                {
                    if (exploring) LeaveExplore();
                    else _ = Explore();
                    return;
                }
                if (!exploring) return;
                if (action == PadAction.BackToMap) LeaveExplore();
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
                // Online: who's playing (asked once), then into this town's room.
                Connect(EnterTown);
                if (!missionsAsked) RefreshMissions();
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
            iso?.CloseMap();
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
            followOnMap = true;
            if (at != null)
            {
                autoFit = false;
                // The map was hidden, so it has no size until the next layout: centre it then.
                map.CenterOnceLaidOut(at.Value);
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

        /// <summary>
        /// Signed in while one of the account's characters is already playing (another
        /// tab, another device, someone else on this sign-in): a button under the name
        /// to go to their town and be shown the way to them.
        /// </summary>
        void OfferJoins()
        {
            string mine = CharacterPicker.RememberedId(platform);
            Account.ListCharacters(platform, origin, (list, err) =>
            {
                if (list == null) return;
                var playing = list.Where(c => !string.IsNullOrEmpty(c.town) && c.id != mine).ToList();
                sidebar.SetJoins(playing.Select(c => (c.name, (Action)(() => Join(c)))).ToList());
            });
        }

        /// <summary>Into their town (playing as whoever's picked next), finding the way to them once they're seen.</summary>
        void Join(Character them)
        {
            sidebar.SetJoins(null);
            var state = ParseUrlState(them.town);
            sidebar.SetParams(state.@params);
            Regenerate(state.seed ?? RandomSeed(), state.@params);
            joining = them.name;
            joiningUntil = platform.Now() + JOIN_FIND_MS;
            _ = Explore();
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

        /// <summary>The character box: a grid of everyone to be, and you become the one picked.</summary>
        void ChooseLook()
        {
            if (iso == null) return;
            var looks = Catalog.CHARACTERS;
            overlay.Choose("Choose your character", portraits.LookStills(), looks.Select((_, i) => $"Person {i + 1}").ToList(),
                Array.IndexOf(looks, iso.LookModel), i =>
                {
                    var look = iso.ChangeLook(looks[i]);
                    if (look != null) net?.LookChanged(look);
                }, "look");
        }

        /// <summary>The car box: a grid of every car, and the one picked is yours, brought right beside you (in place of any other).</summary>
        void ChooseCar()
        {
            if (iso == null) return;
            var names = Catalog.CARS.Take(Catalog.CarCount).Select(CarName).ToList();
            overlay.Choose("Choose your car", portraits.CarStills(), names, carModel, i =>
            {
                carModel = i;
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
                // Joined someone: the way to them as soon as they're on the map (or not, after a while).
                var them = iso.Others.FirstOrDefault(o => o.name == joining);
                if (them.key != null) iso.Find(them.key);
                if (them.key != null || now > joiningUntil) joining = null;
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
                if (Typing()) return;
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
            if (Typing()) return;
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
        }

        void ShowMissions() => sidebar.SetMissions(missions, missionsStatus, mission?.id, StartMission, () => EndMission("Mission abandoned"));

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
                    mission = new MissionPlay(id, info.title, story, iso, ModelLibrary.Instance, input);
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
                else EndMission($"Mission complete: {mission.title}");
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
