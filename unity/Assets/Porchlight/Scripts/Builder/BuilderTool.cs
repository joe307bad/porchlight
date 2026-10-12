using System;
using Porchlight.App;
using Porchlight.Input;
using Porchlight.Net;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Metrics;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Builder
{
    /// <summary>
    /// The town builder: place buildings, lay roads, remove things, and save. Every rule check and
    /// mutation goes through the shared TownRules and TownEditor, so the server agrees with what the
    /// player sees. Only builds with editing enabled turn this on (not the Switch viewer).
    /// </summary>
    public sealed class BuilderTool : MonoBehaviour
    {
        private enum Tool
        {
            Build,
            Road,
            Remove,
        }

        private const float RotationStep = 15f;
        private static readonly string[] ToolNames = { "Build (1)", "Road (2)", "Remove (3)" };
        private static readonly Color ValidTint = new Color(0.55f, 1f, 0.55f, 1f);
        private static readonly Color InvalidTint = new Color(1f, 0.35f, 0.35f, 1f);

        private TownDto _town;
        private TownView _view;
        private ITownSource _source;
        private Camera _camera;
        private Action _onExit;

        private Tool _tool;
        private string _buildingTypeId = Catalog.Buildings[0].Id;
        private string _roadTypeId = Catalog.Roads[0].Id;
        private float _rotation;
        private bool _alignToRoads = true;
        private bool _snapToGrid;
        private Vec2? _roadStart;
        private string _hint;

        private TownMetrics _metrics;
        private int _editCount;
        private int _savedEditCount;
        private bool _saving;
        private string _saveStatus;
        private bool _confirmLeave;
        private bool _exitRequested;
        private Vector2 _scroll;

        private Material _validMaterial;
        private Material _invalidMaterial;
        private MeshFilter _ghost;
        private MeshFilter _roadGhost;

        private bool IsDirty => _editCount != _savedEditCount;
        private static Rect PanelRect => new Rect(10f, 10f, 270f, Ui.Height - 20f);

        public void Begin(TownDto town, TownView view, ITownSource source, Camera camera, Material baseMaterial, Action onExit)
        {
            _town = town;
            _view = view;
            _source = source;
            _camera = camera;
            _onExit = onExit;
            _metrics = TownMetrics.Compute(town);
            _editCount = _savedEditCount = 0;
            _saveStatus = null;
            _confirmLeave = _exitRequested = false;
            _roadStart = null;

            _validMaterial = new Material(baseMaterial) { name = "Ghost (valid)" };
            _validMaterial.SetColor("_Tint", ValidTint);
            _invalidMaterial = new Material(baseMaterial) { name = "Ghost (invalid)" };
            _invalidMaterial.SetColor("_Tint", InvalidTint);
            _ghost = CreateGhost("Ghost");
            _roadGhost = CreateGhost("Road ghost");
            _roadGhost.sharedMesh = new Mesh { name = "road ghost" };

            enabled = true;
        }

        public void End()
        {
            if (_ghost != null) Destroy(_ghost.gameObject);
            if (_roadGhost != null)
            {
                Destroy(_roadGhost.sharedMesh);
                Destroy(_roadGhost.gameObject);
            }

            if (_validMaterial != null) Destroy(_validMaterial);
            if (_invalidMaterial != null) Destroy(_invalidMaterial);
            _town = null;
            enabled = false;
        }

        private void Update()
        {
            if (_town == null) return;

            if (_exitRequested)
            {
                _exitRequested = false;
                _onExit();
                return;
            }

            HandleHotkeys();
            _hint = null;

            if (Ui.PointerIn(PanelRect) || !TryGroundPoint(out var cursor))
            {
                HideGhosts();
                return;
            }

            switch (_tool)
            {
                case Tool.Build: UpdateBuild(cursor); break;
                case Tool.Road: UpdateRoad(cursor); break;
                case Tool.Remove: UpdateRemove(cursor); break;
            }
        }

        private void HandleHotkeys()
        {
            if (PInput.KeyDown(PKey.Alpha1)) SetTool(Tool.Build);
            if (PInput.KeyDown(PKey.Alpha2)) SetTool(Tool.Road);
            if (PInput.KeyDown(PKey.Alpha3)) SetTool(Tool.Remove);
            if (PInput.KeyDown(PKey.Q)) _rotation -= RotationStep;
            if (PInput.KeyDown(PKey.E)) _rotation += RotationStep;
            if (PInput.KeyDown(PKey.Escape)) _roadStart = null;
        }

        private void SetTool(Tool tool)
        {
            _tool = tool;
            _roadStart = null;
            HideGhosts();
        }

        private void UpdateBuild(Vec2 cursor)
        {
            if (!Catalog.TryGetBuilding(_buildingTypeId, out var type)) return;

            var position = _snapToGrid ? new Vec2(Mathf.Round(cursor.X), Mathf.Round(cursor.Z)) : cursor;
            var rotation = _rotation;
            if (_alignToRoads && TownQueries.TrySnapToRoad(_town, type, cursor, out var snapped, out var heading))
            {
                // Beside a road, Q/E rotate relative to "facing the road".
                position = snapped;
                rotation = heading + _rotation;
            }

            position = TownMath.Quantize(position);
            rotation = TownMath.NormalizeAngle(rotation);

            var check = TownRules.CanPlaceBuilding(_town, type.Id, position.X, position.Z, rotation);
            ShowGhost(MeshCache.Building(type.Id), position, rotation, 1f, check.Ok);
            _hint = check.Ok ? null : check.Error;

            if (check.Ok && PInput.PrimaryPressedThisFrame())
            {
                Apply(new PlaceBuildingCommand
                {
                    BuildingId = Ids.New(),
                    TypeId = type.Id,
                    X = position.X,
                    Z = position.Z,
                    RotationDeg = rotation,
                });
            }
        }

        private void UpdateRoad(Vec2 cursor)
        {
            if (!Catalog.TryGetRoad(_roadTypeId, out var type)) return;

            var end = TownRules.SnapToNode(_town, TownMath.Quantize(cursor));
            if (_roadStart == null)
            {
                ShowRoadGhost(end.Position, end.Position, type.Width, true);
                _hint = "Click to start a road";
                if (PInput.PrimaryPressedThisFrame()) _roadStart = end.Position;
                return;
            }

            var start = _roadStart.Value;
            var check = TownRules.CanAddRoad(_town, type.Id, start, end.Position);
            ShowRoadGhost(start, end.Position, type.Width, check.Ok);
            _hint = check.Ok ? "Click to lay this segment. Esc to finish." : check.Error;

            if (check.Ok && PInput.PrimaryPressedThisFrame())
            {
                var placed = Apply(new AddRoadCommand
                {
                    SegmentId = Ids.New(),
                    TypeId = type.Id,
                    StartX = start.X,
                    StartZ = start.Z,
                    EndX = end.Position.X,
                    EndZ = end.Position.Z,
                });

                // Keep drawing from where this segment ended.
                if (placed) _roadStart = end.Position;
            }
        }

        private void UpdateRemove(Vec2 cursor)
        {
            var building = TownQueries.FindBuildingAt(_town, cursor);
            if (building != null)
            {
                ShowGhost(MeshCache.Building(building.TypeId), new Vec2(building.X, building.Z), building.RotationDeg, 1.04f, false);
                _hint = "Click to remove this building";
                if (PInput.PrimaryPressedThisFrame()) Apply(new RemoveBuildingCommand { BuildingId = building.Id });
                return;
            }

            var road = TownQueries.FindRoadAt(_town, cursor);
            if (road.HasValue)
            {
                ShowRoadGhost(road.Value.A, road.Value.B, road.Value.Type.Width + 0.4f, false);
                _hint = "Click to remove this road";
                if (PInput.PrimaryPressedThisFrame()) Apply(new RemoveRoadCommand { SegmentId = road.Value.Segment.Id });
                return;
            }

            HideGhosts();
        }

        private bool Apply(TownCommand command)
        {
            var result = TownEditor.Apply(_town, command);
            if (!result.Ok)
            {
                _hint = result.Error;
                return false;
            }

            _view.Sync(_town);
            _metrics = TownMetrics.Compute(_town);
            _editCount++;
            _saveStatus = null;
            _confirmLeave = false;
            return true;
        }

        private async void Save()
        {
            if (_saving) return;
            _saving = true;
            _saveStatus = "Saving...";
            var editsAtSave = _editCount;
            var town = _town;
            try
            {
                var saved = await _source.SaveTownAsync(town);
                if (this == null || _town != town) return;
                _town.Version = saved.Version;
                _savedEditCount = editsAtSave;
                _saveStatus = IsDirty ? "Saved (newer edits still unsaved)" : "Saved";
            }
            catch (TownApiException e)
            {
                if (this == null) return;
                _saveStatus = e.IsConflict
                    ? "Someone else saved this town. Go back and reopen it to get their changes."
                    : "Save failed: " + e.Message;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this != null) _saveStatus = "Save failed: " + e.Message;
            }
            finally
            {
                _saving = false;
            }
        }

        private bool TryGroundPoint(out Vec2 point)
        {
            var ray = _camera.ScreenPointToRay(PInput.PointerPosition());
            if (ray.direction.y > -1e-4f)
            {
                point = default;
                return false;
            }

            var hit = ray.origin + ray.direction * (-ray.origin.y / ray.direction.y);
            point = new Vec2(hit.x, hit.z);
            return true;
        }

        private MeshFilter CreateGhost(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go.AddComponent<MeshFilter>();
        }

        private void ShowGhost(Mesh mesh, Vec2 position, float rotationDeg, float scale, bool valid)
        {
            _roadGhost.gameObject.SetActive(false);
            _ghost.sharedMesh = mesh;
            _ghost.GetComponent<MeshRenderer>().sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            _ghost.transform.SetPositionAndRotation(new Vector3(position.X, 0.02f, position.Z), Quaternion.Euler(0f, rotationDeg, 0f));
            _ghost.transform.localScale = Vector3.one * scale;
            _ghost.gameObject.SetActive(true);
        }

        private void ShowRoadGhost(Vec2 a, Vec2 b, float width, bool valid)
        {
            _ghost.gameObject.SetActive(false);
            var mb = new MeshBuilder();
            var color = Color.white;
            var pa = new Vector3(a.X, 0.08f, a.Z);
            var pb = new Vector3(b.X, 0.08f, b.Z);
            LowPolyMeshes.Segment(mb, pa, pb, width, color, 0f);
            LowPolyMeshes.Disc(mb, pa, width * 0.5f, color);
            LowPolyMeshes.Disc(mb, pb, width * 0.5f, color);
            mb.ToMesh(_roadGhost.sharedMesh);
            _roadGhost.GetComponent<MeshRenderer>().sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            _roadGhost.gameObject.SetActive(true);
        }

        private void HideGhosts()
        {
            if (_ghost != null) _ghost.gameObject.SetActive(false);
            if (_roadGhost != null) _roadGhost.gameObject.SetActive(false);
        }

        private void OnGUI()
        {
            if (_town == null) return;
            Ui.Begin();

            GUILayout.BeginArea(PanelRect, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label(_town.Name, Ui.Title);
            GUILayout.Label($"Population {_metrics.Population}    Jobs {_metrics.Jobs}");
            GUILayout.Label($"{_metrics.BuildingCount} buildings ({_metrics.ConnectedBuildingCount} on a road)");
            GUILayout.Label($"{_metrics.RoadLengthMeters:0} m of road");
            GUILayout.Space(8f);

            var tool = (Tool)GUILayout.Toolbar((int)_tool, ToolNames);
            if (tool != _tool) SetTool(tool);
            GUILayout.Space(6f);

            switch (_tool)
            {
                case Tool.Build:
                    foreach (var type in Catalog.Buildings)
                    {
                        if (GUILayout.Toggle(_buildingTypeId == type.Id, $"{type.DisplayName}  ({Describe(type)})", GUI.skin.button))
                        {
                            _buildingTypeId = type.Id;
                        }
                    }

                    GUILayout.Label($"Rotation {_rotation:0}°  (Q / E)");
                    _alignToRoads = GUILayout.Toggle(_alignToRoads, " Snap beside roads");
                    _snapToGrid = GUILayout.Toggle(_snapToGrid, " Snap to 1 m grid");
                    break;

                case Tool.Road:
                    foreach (var type in Catalog.Roads)
                    {
                        if (GUILayout.Toggle(_roadTypeId == type.Id, $"{type.DisplayName}  ({type.Width:0} m)", GUI.skin.button))
                        {
                            _roadTypeId = type.Id;
                        }
                    }

                    GUILayout.Label("Click to start, click again for each segment. Ends snap to nearby junctions.", Ui.Wrap);
                    break;

                case Tool.Remove:
                    GUILayout.Label("Click a building or road to remove it.", Ui.Wrap);
                    break;
            }

            if (!string.IsNullOrEmpty(_hint)) GUILayout.Label(_hint, Ui.Hint);
            GUILayout.EndScrollView();

            GUI.enabled = !_saving;
            if (GUILayout.Button(IsDirty ? "Save *" : "Save", GUILayout.Height(32f))) Save();
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_saveStatus)) GUILayout.Label(_saveStatus, Ui.Wrap);

            if (GUILayout.Button(_confirmLeave ? "Discard changes and leave" : "Back to towns"))
            {
                if (IsDirty && !_confirmLeave) _confirmLeave = true;
                else _exitRequested = true;
            }

            if (_confirmLeave) GUILayout.Label("You have unsaved changes.", Ui.Hint);

            GUILayout.Label("Move: arrows · Look: WASD or hold right mouse · Up/down: Space / C · Fast: Shift", Ui.Small);
            GUILayout.EndArea();
        }

        private static string Describe(BuildingType type)
        {
            if (type.Population > 0) return $"{type.Population} residents";
            if (type.Jobs > 0) return $"{type.Jobs} jobs";
            return "decor";
        }
    }
}
