using System;
using System.Collections.Generic;
using Porchlight.Avatars;
using Porchlight.Builder;
using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Net;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The whole app. Play: pick a character, pick a town, then walk around it in third person.
    /// Build: pick or create a town and edit it; Tab drops your character in to walk around.
    /// Builds with <see cref="AppConfig.CanEdit"/> (the browser) open on a Play / Build title screen;
    /// the Switch has no builder, so it goes straight to Play.
    /// Everything is created in code, so any scene works. See <see cref="PorchlightBootstrap"/>.
    /// </summary>
    public sealed class PorchlightApp : MonoBehaviour
    {
        private enum Mode
        {
            Title,
            CharacterSelect,
            Menu,
            Loading,
            Town,
        }

        private const string CharacterPref = "Porchlight.Character";
        private static readonly Color Sky = new Color(0.64f, 0.79f, 0.93f);

        private bool _canEdit;

        /// <summary>True after choosing Build on the title screen, false after Play.</summary>
        private bool _building;

        private TownDto _stockTown;
        private bool _showingStock;
        private ITownSource _source;
        private Camera _camera;
        private FlyCamera _fly;
        private ThirdPersonCamera _thirdPerson;
        private TitleScreen _title;
        private CharacterSelectScreen _characters;
        private TownListScreen _townList;
        private HudBar _hud;

        /// <summary>The terrain builder's layers, down the right-hand side. Builder only.</summary>
        private HudBar _layers;
        private bool _layersShown;
        private GridOverlay _grid;
        private PlayerController _player;
        private TownView _view;
        private FoliageView _foliage;
        private DebugOverlay _debug;
        private BuilderTool _builder;
        private InteriorEditor _interior;
        private WalkDrop _drop;
        private OrbitCamera _orbit;
        private Material _material;

        private Mode _mode;
        private int _choice;
        private CharacterType _character;
        private bool _walking;
        private bool _editingInside;
        private bool _dropping;
        private Vector3 _flyPosition;
        private Pose _menuCamera;
        private bool _menuCameraMoving;
        private bool _inMenus;
        private Quaternion _flyRotation;
        private List<TownSummaryDto> _towns = new List<TownSummaryDto>();
        private string _status;
        private bool _busy;

        private TownDto _town;

        /// <summary>
        /// While walking: the town as it was when walking started (nothing changes while walking), readied
        /// for play (<see cref="TownLandscape.ForPlay"/>: terraces softened, steps and ladders wherever a
        /// wall would cut somewhere off). In the builder it starts from what the builder shows, which may
        /// be a preview of the terrain overlay.
        /// </summary>
        private TownDto _walkTown;

        /// <summary>The ground to walk on: <see cref="_walkTown"/> while walking, otherwise the builder's view of the town (it changes) or the town.</summary>
        private TownDto WalkTown => _walking && _walkTown != null ? _walkTown
            : _building && _builder != null && _builder.Town != null ? _builder.ShownTown : _town;
        private TownMetrics _metrics;

        private void Awake()
        {
            _canEdit = AppConfig.CanEdit;
            _source = new HttpTownSource(AppConfig.ApiBaseUrl);

            var saved = PlayerPrefs.GetString(CharacterPref, "");
            for (var i = 0; i < Characters.All.Count; i++)
            {
                if (Characters.All[i].Id == saved) _choice = i;
            }

            // Building skips character select, so walking around while building uses the last pick.
            _character = Characters.All[_choice];

            SetUpScene();
            if (_canEdit) ShowTitle();
            else ShowCharacterSelect();
        }

        private void SetUpScene()
        {
#if !UNITY_WEBGL
            // WebGL should follow the browser's frame timing; elsewhere aim for a steady 60.
            Application.targetFrameRate = 60;
#endif
            _camera = Camera.main;
            if (_camera == null)
            {
                var cameraObject = new GameObject("Camera") { tag = "MainCamera" };
                _camera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Sky;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 1500f;
            _fly = _camera.gameObject.AddComponent<FlyCamera>();
            _fly.enabled = false;
            _thirdPerson = _camera.gameObject.AddComponent<ThirdPersonCamera>();
            _thirdPerson.enabled = false;
            _orbit = _camera.gameObject.AddComponent<OrbitCamera>();
            _orbit.enabled = false;
            _camera.cullingMask &= ~(1 << HudBar.Layer);

            if (FindAnyObjectByType<Light>() == null)
            {
                var sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1f, 0.96f, 0.88f);
                sun.intensity = 1.1f;
                sun.shadows = LightShadows.None;
                sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 250f;
            RenderSettings.fogEndDistance = 900f;

            _material = TownView.CreateMaterial();
            _view = new GameObject("Town").AddComponent<TownView>();
            _view.transform.SetParent(transform, false);
            _view.Init(_material);
            _view.gameObject.SetActive(false);

            _foliage = new GameObject("Foliage").AddComponent<FoliageView>();
            _foliage.transform.SetParent(transform, false);
            _foliage.Init(_material);

            _debug = gameObject.AddComponent<DebugOverlay>();
            _debug.Describe = DescribeForDebug;

            _grid = new GameObject("Grid").AddComponent<GridOverlay>();
            _grid.transform.SetParent(transform, false);
            _grid.Init(_material);

            // The menus all lie on the stock town's plaza.
            if (_canEdit)
            {
                _builder = gameObject.AddComponent<BuilderTool>();
                _builder.enabled = false;
                _builder.InteriorRequested += EditInside;
                _interior = gameObject.AddComponent<InteriorEditor>();
                _interior.enabled = false;
                _drop = new GameObject("Drop").AddComponent<WalkDrop>();
                _drop.transform.SetParent(transform, false);
                _drop.enabled = false;

                _title = CreateScreen<TitleScreen>("Title");
                _title.Init(_material);
                _title.Chosen += item =>
                {
                    if (TitleScreen.Items[item] == "Build") StartBuild();
                    else StartPlay();
                };
            }

            _characters = CreateScreen<CharacterSelectScreen>("Character select");
            _characters.Init(_material, canGoBack: _canEdit);
            _characters.Chosen += ChooseCharacter;
            _characters.Back += ShowTitle;

            _townList = CreateScreen<TownListScreen>("Town list");
            _townList.Init(_material);
            _townList.Open += OpenTown;
            _townList.Create += CreateTown;
            _townList.Refresh += RefreshTowns;
            _townList.Back += () =>
            {
                if (_building) ShowTitle();
                else ShowCharacterSelect();
            };

            _hud = CreateScreen<HudBar>("Bottom bar");
            _hud.Init(_material, (int)_camera.depth);

            if (_canEdit)
            {
                _layers = CreateScreen<HudBar>("Layers panel");
                _layers.Init(_material, (int)_camera.depth, side: true);
            }
        }

        private T CreateScreen<T>(string name) where T : Component
        {
            var screen = new GameObject(name).AddComponent<T>();
            screen.transform.SetParent(transform, false);
            return screen;
        }

        private void ShowTitle()
        {
            EnterMenu(Mode.Title);
            _title.Show(Mathf.Max(0, _title.Selected));
        }

        private void StartPlay()
        {
            _building = false;
            ShowCharacterSelect();
        }

        private void StartBuild()
        {
            _building = true;
            ShowMenu();
        }

        private void ShowCharacterSelect()
        {
            EnterMenu(Mode.CharacterSelect);
            _characters.Show(_choice);
        }

        private void ChooseCharacter(int index)
        {
            _choice = index;
            _character = Characters.All[index];
            PlayerPrefs.SetString(CharacterPref, _character.Id);
            PlayerPrefs.Save();
            ShowMenu();
        }

        private void ShowMenu()
        {
            EnterMenu(Mode.Menu);
            var subtitle = _building ? "Pick a town to build, or start a new one." : $"Playing as {_character.DisplayName}";
            _townList.Show(_building, subtitle, "Server  " + _source.Description);
            PushTowns();
            RefreshTowns();
        }

        /// <summary>Switches to a menu: the stock town and grid behind, seen at an angle, and no other menu showing.</summary>
        private void EnterMenu(Mode mode)
        {
            _mode = mode;
            _fly.enabled = false;
            _thirdPerson.enabled = false;
            _orbit.enabled = false;
            _camera.rect = new Rect(0f, 0f, 1f, 1f);
            _hud.Hide();
            if (_title != null) _title.Hide();
            _characters.Hide();
            _townList.Hide();

            if (!_showingStock)
            {
                if (_stockTown == null) _stockTown = StockTown.Create();
                _view.Show(_stockTown);
                _showingStock = true;
            }

            _grid.Show(_stockTown);

            // Glide between menus (angled title to the flat menus and back); jump there from anywhere else.
            _menuCamera = MenuStyle.CameraPose(_camera, angled: mode == Mode.Title);
            _menuCameraMoving = _inMenus;
            if (!_inMenus) _camera.transform.SetPositionAndRotation(_menuCamera.position, _menuCamera.rotation);
            _inMenus = true;
        }

        private void PushTowns() => _townList.SetTowns(_towns, _status, _busy);

        private async void RefreshTowns()
        {
            if (_busy) return;
            _busy = true;
            _status = "Loading towns…";
            PushTowns();
            try
            {
                var towns = await _source.ListTownsAsync();
                if (this == null) return;
                _towns = towns ?? new List<TownSummaryDto>();
                _status = _towns.Count > 0 ? null
                    : _building ? "No towns yet. Start one with New town."
                    : _canEdit ? "No towns yet. Choose Build on the title screen to make one."
                    : "No towns yet. Build one in the browser.";
            }
            catch (Exception e)
            {
                if (this == null) return;
                _towns = new List<TownSummaryDto>();
                _status = $"Can't reach the server at {_source.Description}. Is ./browser-dev.sh running? ({e.Message})";
            }
            finally
            {
                if (this != null)
                {
                    _busy = false;
                    PushTowns();
                }
            }
        }

        private async void OpenTown(string id)
        {
            if (_busy) return;
            _busy = true;
            _mode = Mode.Loading;
            _status = "Opening town…";
            PushTowns();
            try
            {
                var town = await _source.GetTownAsync(id);
                if (this == null) return;
                _busy = false;
                EnterTown(town);
            }
            catch (Exception e)
            {
                if (this == null) return;
                _busy = false;
                _mode = Mode.Menu;
                _status = "Couldn't load that town: " + e.Message;
                PushTowns();
            }
        }

        private async void CreateTown(string name)
        {
            if (_busy || string.IsNullOrWhiteSpace(name)) return;
            _busy = true;
            _status = "Creating town…";
            PushTowns();
            try
            {
                var town = await _source.CreateTownAsync(name.Trim());
                if (this == null) return;
                _busy = false;
                EnterTown(town);
            }
            catch (Exception e)
            {
                if (this == null) return;
                _busy = false;
                _status = "Couldn't create the town: " + e.Message;
                PushTowns();
            }
        }

        private void EnterTown(TownDto town)
        {
            // Towns saved before the ground was tiles are converted to them.
            TownTiles.Upgrade(town);
            _town = town;
            _metrics = TownMetrics.Compute(town);
            _townList.Hide();
            _inMenus = false;
            _menuCameraMoving = false;
            _showingStock = false;

            // Visiting, the town is only ever walked, so it's shown readied for play from the start.
            var play = _building ? null : TownLandscape.ForPlay(town);
            _view.Show(play ?? town);
            if (_building) _grid.Show(town);
            else _grid.Hide();
            _status = null;
            _mode = Mode.Town;

            // The town gets the top of the screen; the bar below it isn't drawn over the town.
            _camera.rect = new Rect(0f, HudBar.Fraction, 1f, 1f - HudBar.Fraction);

            if (_player == null || _player.GetComponent<CharacterRig>().Type != _character)
            {
                if (_player != null) Destroy(_player.gameObject);
                _player = CharacterRig.Create(_character, _material, transform).gameObject.AddComponent<PlayerController>();
                _player.End();
            }

            if (_building)
            {
                // The builder starts from above; Tab drops the character in to look around.
                _fly.Frame(town);
                _fly.enabled = true;
                _builder.Begin(town, _view, _grid, _source, _camera, _material, ExitTown);
                _hud.Show(new BuilderHud(_builder, _material, ToggleWalking));
            }
            else
            {
                _view.BakeStatic();
                StartWalking(TownWalking.FindSpawn(play, Vec2.Zero), 0f, play: play);
            }
        }

        /// <param name="play">The town already readied for play, if it has been.</param>
        private void StartWalking(Vec2 spot, float heading, float floorY = float.NaN, TownDto play = null)
        {
            _walkTown = play ?? TownLandscape.ForPlay(_building && _builder != null && _builder.Town != null ? _builder.ShownTown : _town);
            _view.Sync(_walkTown);
            _walking = true;
            _fly.enabled = false;
            _grid.Hide();
            if (_builder != null) _builder.Paused = true;
            _player.Begin(WalkTown, _thirdPerson, spot, heading, floorY);
            _thirdPerson.Follow(_player.transform, _character.Height, WalkTown, heading);
            _view.Walker = _player.transform;
            _hud.Show(new WalkHud(_town, _character, _material, _building, _thirdPerson, _building ? ToggleWalking : ExitTown));
        }

        private void StopWalking()
        {
            _walking = false;
            _walkTown = null;

            // Back in the builder, the ground is shown as built again.
            if (_building && _builder != null && _builder.Town != null) _view.Sync(_builder.ShownTown);
            _player.SetHidden(false);
            _player.End();
            _thirdPerson.enabled = false;
            _thirdPerson.OpenBuildingId = null;
            _view.Walker = null;
            _view.SetCutaway(null, 0, 0);
            if (_builder != null) _builder.Paused = false;
        }

        /// <summary>Builder only: between flying around to build and walking around as the character.</summary>
        private void ToggleWalking()
        {
            if (_walking)
            {
                StopWalking();
                _grid.Show(WalkTown);
                _hud.Show(new BuilderHud(_builder, _material, ToggleWalking));
                _camera.transform.SetPositionAndRotation(_flyPosition, _flyRotation);
                _fly.enabled = true;
                return;
            }

            // First choose where to drop the character in.
            BeginDrop(null, 0);
        }

        /// <summary>
        /// Builder only: a ghost of the character follows the pointer until a click drops them in. With a
        /// building, they go on that floor of the cottage being edited.
        /// </summary>
        private void BeginDrop(BuildingDto building, int floor)
        {
            if (_dropping || _walking) return;
            _dropping = true;
            if (building == null) _builder.Paused = true;
            else _interior.Paused = true;
            _drop.Begin(WalkTown, _camera, _character, _material, building, floor, Drop, CancelDrop);
            _hud.Show(new DropHud(_drop, CancelDrop));
        }

        private void CancelDrop()
        {
            if (!_dropping) return;
            _dropping = false;
            _drop.End();
            if (_editingInside)
            {
                _interior.Paused = false;
                _hud.Show(new InteriorHud(_interior, _builder, _material, WalkInside));
                return;
            }

            _builder.Paused = false;
            _hud.Show(new BuilderHud(_builder, _material, ToggleWalking));
        }

        private void Drop(Vec2 spot, float heading, float floorY)
        {
            _dropping = false;
            _drop.End();

            // Tab or the Walk button brings the camera back to where it was.
            if (_editingInside) LeaveInside();
            else
            {
                _flyPosition = _camera.transform.position;
                _flyRotation = _camera.transform.rotation;
            }

            StartWalking(spot, heading, floorY);
        }

        /// <summary>Builder only: edit the inside of a cottage, with the camera locked on it.</summary>
        private void EditInside(BuildingDto building)
        {
            if (_editingInside || _walking) return;
            _editingInside = true;
            _flyPosition = _camera.transform.position;
            _flyRotation = _camera.transform.rotation;
            _fly.enabled = false;
            _builder.Paused = true;
            _grid.Hide();
            _interior.Begin(_town, building, _view, _camera, _orbit, _material, _builder.ApplyEdit, LeaveInside);
            _hud.Show(new InteriorHud(_interior, _builder, _material, WalkInside));
        }

        /// <summary>Back to building the town, where the camera was before.</summary>
        private void LeaveInside()
        {
            if (!_editingInside) return;
            _editingInside = false;
            _interior.End();
            _camera.transform.SetPositionAndRotation(_flyPosition, _flyRotation);
            _fly.enabled = true;
            _builder.Paused = false;
            _grid.Show(WalkTown);
            _hud.Show(new BuilderHud(_builder, _material, ToggleWalking));
        }

        /// <summary>From editing a cottage, walk around, dropped in on the floor being edited.</summary>
        private void WalkInside()
        {
            if (_interior.Building != null) BeginDrop(_interior.Building, _interior.Floor);
        }

        private void ExitTown()
        {
            if (_dropping)
            {
                _dropping = false;
                _drop.End();
            }

            if (_editingInside) LeaveInside();
            if (_walking) StopWalking();
            if (_builder != null) _builder.End();
            _foliage.Clear();
            _hud.Hide();
            _view.Clear();
            _grid.Hide();
            _town = null;
            ShowMenu();
        }

        private void Update()
        {
            if (_menuCameraMoving && _mode != Mode.Town) MoveMenuCamera();
            UpdateLayersPanel();
            UpdateFoliage();

            // The menus handle their own input; this is only for inside a town.
            if (_mode != Mode.Town) return;
            if (_dropping)
            {
                if (PInput.KeyDown(PKey.Tab)) CancelDrop();
            }
            else if (_editingInside)
            {
                if (PInput.KeyDown(PKey.Tab)) WalkInside();
            }
            else if (_building && (PInput.KeyDown(PKey.Tab) || (_walking && PInput.Pause()))) ToggleWalking();
            else if (!_building && PInput.Pause()) ExitTown();
        }

        /// <summary>Foliage grows while walking around, and in the builder's Nature tool so its sliders show their effect.</summary>
        private void UpdateFoliage()
        {
            var town = _mode == Mode.Town ? WalkTown : null;
            var nature = _building && _builder != null && _builder.enabled && !_builder.Paused && _builder.CurrentTool == BuilderTool.Tool.Nature;
            if (town != null && (_walking || nature)) _foliage.Show(town);
            else _foliage.Hide();
        }

        /// <summary>What the debug overlay says about the app, beyond frame timings.</summary>
        private IEnumerable<string> DescribeForDebug()
        {
            yield return $"Mode: {_mode}{(_building ? " (build)" : "")}{(_walking ? ", walking" : "")}{(_editingInside ? ", inside a cottage" : "")}";
            if (_town == null) yield break;
            yield return $"Town: {_town.Name}  ·  {_town.Size:0} m  ·  {_town.Buildings.Count} buildings";
            if (TownTiles.Has(_town))
            {
                var ground = WalkTown ?? _town;
                var counts = new int[5];
                foreach (var surface in ground.Tiles.Surfaces) counts[Mathf.Clamp(surface, 0, 4)]++;
                var hills = 0;
                for (var k = 0; k < ground.Tiles.Levels.Count; k++) hills += TownTiles.HillMark(ground, k) ? 1 : 0;
                yield return $"Tiles: {TownTiles.Cells(ground)}² of {TownTiles.Spacing(ground):0} m  ·  street {counts[1]}  ·  path {counts[2]}  ·  plaza {counts[3]}  ·  garden {counts[4]}  ·  hillside {hills}  ·  shapes {_town.TerrainShapes?.Count ?? 0}";
                if (_walking && _player != null)
                {
                    var at = new Vec2(_player.transform.position.x, _player.transform.position.z);
                    if (TownTiles.TryCell(ground, at, out var ti, out var tj))
                        yield return $"Standing on tile ({ti}, {tj}): {TownTiles.Surface(ground, ti, tj)}, level {TownTiles.Level(ground, ti, tj)}{(TownTiles.TryClimb(ground, ti, tj, out var side) ? $", climbing {side}" : "")}{(TownTiles.IsHill(ground, ti, tj) ? $", hillside at {TownTiles.HeightAt(ground, at):0.00} m" : "")}";
                }
            }

            if (_walking && _walkTown?.Tiles != null)
            {
                var flights = _walkTown.Tiles.Flights?.FindAll(f => f != 0).Count ?? 0;
                var ladders = _walkTown.Tiles.Ladders?.FindAll(l => l != 0).Count ?? 0;
                yield return $"Readied for play: {flights} flights of steps  ·  {ladders} ladders";
            }

            yield return $"Foliage: {(_foliage.Showing ? _foliage.PlantCount.ToString() + " plants" : "hidden")}";
            if (_builder != null && _builder.enabled) yield return $"Builder: {_builder.CurrentTool}  ·  unsaved {_builder.IsDirty}  ·  {_builder.SaveStatus}";
            var p = _camera.transform.position;
            yield return $"Camera: ({p.x:0}, {p.y:0}, {p.z:0})";
        }

        /// <summary>The layers panel is up while the builder's terrain tool is in Builder mode, and nothing else is going on.</summary>
        private void UpdateLayersPanel()
        {
            if (_layers == null) return;
            var wanted = _mode == Mode.Town && _builder.ShowingLayers && !_walking && !_dropping && !_editingInside;
            if (wanted == _layersShown) return;
            _layersShown = wanted;
            if (wanted) _layers.Show(new TerrainLayersHud(_builder));
            else _layers.Hide();
        }

        private void LateUpdate()
        {
            if (!_walking || _player == null || _town == null) return;
            _player.SetHidden(_thirdPerson.FirstPerson || _thirdPerson.CloseUp);

            // Inside a cottage, cut it away to see in: the floors above, the roof, and the walls on the camera's side.
            if (_player.TryGetInterior(out var buildingId, out var floor))
            {
                // Inside, the cottage is whole, walls and ceiling and all: the camera stays within the
                // walls rather than looking through them (see ThirdPersonCamera).
                _view.SetCutaway(null, 0, 0);
                _thirdPerson.OpenBuildingId = buildingId;
            }
            else
            {
                _view.SetCutaway(null, 0, 0);
                _thirdPerson.OpenBuildingId = null;
            }
        }

        private void MoveMenuCamera()
        {
            var t = 1f - Mathf.Exp(-7f * Time.unscaledDeltaTime);
            var camera = _camera.transform;
            camera.SetPositionAndRotation(Vector3.Lerp(camera.position, _menuCamera.position, t), Quaternion.Slerp(camera.rotation, _menuCamera.rotation, t));
            if ((camera.position - _menuCamera.position).sqrMagnitude < 0.0004f && Quaternion.Angle(camera.rotation, _menuCamera.rotation) < 0.05f)
            {
                camera.SetPositionAndRotation(_menuCamera.position, _menuCamera.rotation);
                _menuCameraMoving = false;
            }
        }
    }

    public static class PorchlightBootstrap
    {
        /// <summary>
        /// Starts the app after any scene loads, so the project needs no hand-authored scene objects.
        /// Add a PorchlightApp to a scene yourself if you'd rather control it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (UnityEngine.Object.FindAnyObjectByType<PorchlightApp>() != null) return;
            var app = new GameObject("Porchlight");
            UnityEngine.Object.DontDestroyOnLoad(app);
            app.AddComponent<PorchlightApp>();
        }
    }
}
