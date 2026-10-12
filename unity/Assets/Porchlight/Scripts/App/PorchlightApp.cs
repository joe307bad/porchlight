using System;
using System.Collections.Generic;
using Porchlight.Builder;
using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Net;
using Porchlight.Rendering;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// The whole app: town list, then a town. The browser and Switch builds run the same flow. The only
    /// difference is <see cref="AppConfig.CanEdit"/>, which turns on the builder or the read-only HUD.
    /// Everything is created in code, so any scene works. See <see cref="PorchlightBootstrap"/>.
    /// </summary>
    public sealed class PorchlightApp : MonoBehaviour
    {
        private enum Mode
        {
            Menu,
            Loading,
            Town,
        }

        private static readonly Color Sky = new Color(0.64f, 0.79f, 0.93f);

        private bool _canEdit;
        private ITownSource _source;
        private Camera _camera;
        private FlyCamera _fly;
        private TownView _view;
        private BuilderTool _builder;
        private Material _material;

        private Mode _mode;
        private List<TownSummaryDto> _towns = new List<TownSummaryDto>();
        private string _status;
        private bool _busy;
        private string _newTownName = "New Town";
        private int _selected;
        private Vector2 _scroll;

        private TownDto _town;
        private TownMetrics _metrics;

        private void Awake()
        {
            _canEdit = AppConfig.CanEdit;
            _source = new HttpTownSource(AppConfig.ApiBaseUrl);
            SetUpScene();
            ShowMenu();
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

            if (_canEdit)
            {
                _builder = gameObject.AddComponent<BuilderTool>();
                _builder.enabled = false;
            }
        }

        private void ShowMenu()
        {
            _mode = Mode.Menu;
            _fly.enabled = false;
            RefreshTowns();
        }

        private async void RefreshTowns()
        {
            if (_busy) return;
            _busy = true;
            _status = "Loading towns...";
            try
            {
                var towns = await _source.ListTownsAsync();
                if (this == null) return;
                _towns = towns ?? new List<TownSummaryDto>();
                _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _towns.Count - 1));
                _status = _towns.Count == 0 ? (_canEdit ? "No towns yet. Create one below." : "No towns yet. Build one in the browser.") : null;
            }
            catch (Exception e)
            {
                if (this == null) return;
                _towns.Clear();
                _status = $"Can't reach the server at {_source.Description}. Is ./browser-dev.sh running?\n{e.Message}";
            }
            finally
            {
                _busy = false;
            }
        }

        private async void OpenTown(string id)
        {
            if (_busy) return;
            _busy = true;
            _mode = Mode.Loading;
            _status = "Loading town...";
            try
            {
                var town = await _source.GetTownAsync(id);
                if (this == null) return;
                EnterTown(town);
            }
            catch (Exception e)
            {
                if (this == null) return;
                _mode = Mode.Menu;
                _status = "Couldn't load that town: " + e.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async void CreateTown()
        {
            var name = _newTownName?.Trim();
            if (_busy || string.IsNullOrEmpty(name)) return;
            _busy = true;
            _status = "Creating town...";
            try
            {
                var town = await _source.CreateTownAsync(name);
                if (this == null) return;
                EnterTown(town);
            }
            catch (Exception e)
            {
                if (this == null) return;
                _status = "Couldn't create the town: " + e.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void EnterTown(TownDto town)
        {
            _town = town;
            _metrics = TownMetrics.Compute(town);
            _view.Show(town);
            _fly.Frame(town.Size);
            _fly.enabled = true;

            if (_canEdit)
            {
                _builder.Begin(town, _view, _source, _camera, _material, ExitTown);
            }
            else
            {
                _view.BakeStatic();
            }

            _status = null;
            _mode = Mode.Town;
        }

        private void ExitTown()
        {
            if (_builder != null) _builder.End();
            _view.Clear();
            _town = null;
            ShowMenu();
        }

        private void Update()
        {
            switch (_mode)
            {
                case Mode.Menu:
                    if (_towns.Count == 0) break;
                    if (PInput.NavUp()) _selected = (_selected + _towns.Count - 1) % _towns.Count;
                    if (PInput.NavDown()) _selected = (_selected + 1) % _towns.Count;
                    // Don't steal Enter while the new-town name field has focus.
                    if (PInput.Submit() && GUIUtility.keyboardControl == 0) OpenTown(_towns[_selected].Id);
                    break;

                case Mode.Town:
                    if (!_canEdit && PInput.Back()) ExitTown();
                    break;
            }
        }

        private void OnGUI()
        {
            Ui.Begin();
            switch (_mode)
            {
                case Mode.Menu:
                    DrawMenu();
                    break;
                case Mode.Loading:
                    GUI.Label(new Rect(0f, Ui.Height * 0.5f - 20f, Ui.Width, 40f), _status, new GUIStyle(Ui.Title) { alignment = TextAnchor.MiddleCenter });
                    break;
                case Mode.Town:
                    if (!_canEdit) DrawViewerHud();
                    break;
            }
        }

        private void DrawMenu()
        {
            const float width = 460f;
            GUILayout.BeginArea(new Rect((Ui.Width - width) * 0.5f, 40f, width, Ui.Height - 80f), GUI.skin.box);

            GUILayout.Label("Porchlight", Ui.Title);
            GUILayout.Label(_canEdit ? "Pick a town to build, or start a new one." : "Pick a town to visit.", Ui.Wrap);
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, Ui.Hint);

            _scroll = GUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < _towns.Count; i++)
            {
                var town = _towns[i];
                var previous = GUI.backgroundColor;
                if (i == _selected) GUI.backgroundColor = new Color(0.6f, 0.85f, 1f);
                var label = $"<b>{town.Name}</b>\n<size=11>Pop {town.Population} · {town.Jobs} jobs · {town.BuildingCount} buildings</size>";
                if (GUILayout.Button(label, Ui.ListButton, GUILayout.Height(48f)))
                {
                    _selected = i;
                    OpenTown(town.Id);
                }

                GUI.backgroundColor = previous;
            }

            GUILayout.EndScrollView();

            GUI.enabled = !_busy;
            if (GUILayout.Button("Refresh")) RefreshTowns();

            if (_canEdit)
            {
                GUILayout.Space(10f);
                GUILayout.Label("New town");
                GUILayout.BeginHorizontal();
                _newTownName = GUILayout.TextField(_newTownName, TownLimits.MaxNameLength);
                if (GUILayout.Button("Create", GUILayout.Width(90f))) CreateTown();
                GUILayout.EndHorizontal();
            }

            GUI.enabled = true;
            GUILayout.Label($"Server: {_source.Description}" + (_canEdit ? "" : "    ·    Up/Down + Enter to choose"), Ui.Small);
            GUILayout.EndArea();
        }

        private void DrawViewerHud()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 320f, 170f), GUI.skin.box);
            GUILayout.Label(_town.Name, Ui.Title);
            GUILayout.Label($"Population {_metrics.Population}    Jobs {_metrics.Jobs}    Buildings {_metrics.BuildingCount}");
            GUILayout.Label("Move: arrows / left stick · Look: WASD, right mouse / right stick · Up/down: Space, C / triggers", Ui.Small);
            if (GUILayout.Button("Back to towns (Esc)")) ExitTown();
            GUILayout.EndArea();
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
