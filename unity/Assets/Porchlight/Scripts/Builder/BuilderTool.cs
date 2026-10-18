using System;
using Porchlight.App;
using Porchlight.Cameras;
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
    /// player sees. Only builds with editing enabled turn this on (not the Switch viewer). The
    /// controls live in the bottom bar (<see cref="App.BuilderHud"/>), which reads and changes the state here.
    /// </summary>
    public sealed class BuilderTool : MonoBehaviour
    {
        public enum Tool
        {
            Build,
            Road,
            Remove,
        }

        public const float RotationStep = 15f;
        private static readonly Color ValidTint = new Color(0.55f, 1f, 0.55f, 1f);
        private static readonly Color InvalidTint = new Color(1f, 0.35f, 0.35f, 1f);

        private TownDto _town;
        private TownView _view;
        private GridOverlay _grid;
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

        private Material _validMaterial;
        private Material _invalidMaterial;
        private MeshFilter _ghost;
        private MeshFilter _roadGhost;

        /// <summary>While the player walks around the town, the builder hides its panel and ignores the mouse.</summary>
        public bool Paused { get; set; }

        public TownDto Town => _town;
        public TownMetrics Metrics => _metrics;
        public Tool CurrentTool => _tool;
        public float Rotation => _rotation;
        public bool IsDirty => _editCount != _savedEditCount;
        public bool Saving => _saving;
        public string SaveStatus => _saveStatus;

        /// <summary>True after Leave was asked for with unsaved changes; asking again leaves anyway.</summary>
        public bool ConfirmingLeave => _confirmLeave;

        /// <summary>Why the thing under the pointer can't be placed or removed, if it can't.</summary>
        public string Hint => _hint;

        public string BuildingTypeId
        {
            get => _buildingTypeId;
            set => _buildingTypeId = value;
        }

        public string RoadTypeId
        {
            get => _roadTypeId;
            set => _roadTypeId = value;
        }

        public bool AlignToRoads
        {
            get => _alignToRoads;
            set => _alignToRoads = value;
        }

        public bool SnapToGrid
        {
            get => _snapToGrid;
            set => _snapToGrid = value;
        }

        public void Begin(TownDto town, TownView view, GridOverlay grid, ITownSource source, Camera camera, Material baseMaterial, Action onExit)
        {
            _town = town;
            _view = view;
            _grid = grid;
            _source = source;
            _camera = camera;
            _onExit = onExit;
            Paused = false;
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
            if (_grid != null) _grid.HideCross();
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

            if (Paused)
            {
                HideGhosts();
                _grid.HideCross();
                return;
            }

            HandleHotkeys();
            _hint = null;

            if (HudBar.PointerInBar() || !TryGroundPoint(out var cursor) || !InTown(cursor))
            {
                HideGhosts();
                _grid.HideCross();
                return;
            }

            // The red cross marks the grid intersection under the pointer, whatever the tool.
            _grid.ShowCross(cursor);

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
            if (PInput.KeyDown(PKey.Q)) Rotate(-RotationStep);
            if (PInput.KeyDown(PKey.E)) Rotate(RotationStep);
            if (PInput.KeyDown(PKey.Escape)) _roadStart = null;
        }

        public void Rotate(float degrees) => _rotation = TownMath.NormalizeAngle(_rotation + degrees);

        /// <summary>Back to the town list. With unsaved changes, the first call asks and the second leaves.</summary>
        public void Leave()
        {
            if (IsDirty && !_confirmLeave) _confirmLeave = true;
            else _exitRequested = true;
        }

        public void SetTool(Tool tool)
        {
            _tool = tool;
            _roadStart = null;
            HideGhosts();
        }

        private void UpdateBuild(Vec2 cursor)
        {
            if (!Catalog.TryGetBuilding(_buildingTypeId, out var type)) return;

            var position = _snapToGrid ? TownGrid.Snap(cursor) : cursor;
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

            if (check.Ok && PointerGesture.Clicked)
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
                if (PointerGesture.Clicked) _roadStart = end.Position;
                return;
            }

            var start = _roadStart.Value;
            var check = TownRules.CanAddRoad(_town, type.Id, start, end.Position);
            ShowRoadGhost(start, end.Position, type.Width, check.Ok);
            _hint = check.Ok ? "Click to lay this segment. Esc to finish." : check.Error;

            if (check.Ok && PointerGesture.Clicked)
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
                if (PointerGesture.Clicked) Apply(new RemoveBuildingCommand { BuildingId = building.Id });
                return;
            }

            var road = TownQueries.FindRoadAt(_town, cursor);
            if (road.HasValue)
            {
                ShowRoadGhost(road.Value.A, road.Value.B, road.Value.Type.Width + 0.4f, false);
                _hint = "Click to remove this road";
                if (PointerGesture.Clicked) Apply(new RemoveRoadCommand { SegmentId = road.Value.Segment.Id });
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

        public async void Save()
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

        private bool InTown(Vec2 point)
        {
            var half = _town.Size * 0.5f;
            return Mathf.Abs(point.X) <= half && Mathf.Abs(point.Z) <= half;
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
    }
}
