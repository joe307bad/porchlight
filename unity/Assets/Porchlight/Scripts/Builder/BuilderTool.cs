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
    /// The town builder: place buildings, paint the ground's tiles (streets, paths, plazas, gardens),
    /// raise and lower them, and remove things. Edits save
    /// themselves a moment after you stop (<see cref="AutoSaveDelay"/>). Every rule check and
    /// mutation goes through the shared TownRules and TownEditor, so the server agrees with what the
    /// player sees. Only builds with editing enabled turn this on (not the Switch viewer). The
    /// controls live in the bottom bar (<see cref="App.BuilderHud"/>), which reads and changes the state here.
    /// The terrain tool's Builder mode (shapes and the overlay) is in BuilderTool.Shapes.cs.
    /// </summary>
    public sealed partial class BuilderTool : MonoBehaviour
    {
        public enum Tool
        {
            Build,
            Paint,
            Remove,
            Terrain,
            Nature,
        }

        public const float RotationStep = 15f;

        /// <summary>Seconds after the last edit before the town saves itself, so a burst of clicks is one save.</summary>
        public const float AutoSaveDelay = 1f;

        /// <summary>Seconds to wait before trying again after a save fails.</summary>
        private const float RetryDelay = 5f;

        /// <summary>
        /// The squares the terrain tool raises or lowers, by half their side length in metres: one tile,
        /// centred on it (<see cref="BlockPad"/>), then 2, 4 and 8 tiles a side round a grid point.
        /// </summary>
        public static readonly (string Name, float HalfSize)[] PadSizes = { ("Block", TownGrid.Spacing * 0.5f), ("Small", 4f), ("Medium", 8f), ("Large", 16f) };

        /// <summary>The index of the one-square pad in <see cref="PadSizes"/>.</summary>
        public const int BlockPad = 0;

        private static readonly Color ValidTint = new Color(0.55f, 1f, 0.55f, 1f);
        private static readonly Color InvalidTint = new Color(1f, 0.35f, 0.35f, 1f);

        private TownDto _town;
        private TownView _view;
        private GridOverlay _grid;
        private ITownSource _source;
        private Camera _camera;
        private Action _onExit;

        private Tool _tool;
        private string _buildingTypeId = Catalog.Buildable[0].Id;
        private string _surfaceId = Catalog.Surfaces[0].Id;
        private float _rotation;
        private bool _snapToGrid;
        private bool _snapToRoads = true;
        private bool _raiseTerrain = true;
        private int _padSize = 2;

        // The Paint tool: a line of street or path, or a rectangle of anything else, dragged out tile by tile.
        private Vec2? _paintStart;
        private (int From, int To, string Surface, int Edits) _paintKey = (-1, -1, null, -1);
        private System.Collections.Generic.List<int> _paintCells = new System.Collections.Generic.List<int>();
        private EditResult _paintCheck;

        // The Nature tool: painting or erasing where plants grow, by dragging out a rectangle.
        private bool _natureErase;
        private Vec2? _areaStart;
        private Vec2 _areaEnd;
        private MeshFilter _areasView;
        private Material _areasMaterial;
        private int _areasDrawnFor = -1;
        private string _hint;

        // The last terrain check, which is too slow to redo every frame while the pointer sits still.
        private (Vec2 Center, float HalfSize, float Delta, int Edits) _terrainCheckKey;
        private EditResult _terrainCheck;
        private System.Collections.Generic.List<TileChange> _terrainChanges;

        // Likewise the last placement check, since on a slope it works out how to level the ground.
        private (string Type, Vec2 Position, float Rotation, int Edits) _placeCheckKey;
        private EditResult _placeCheck;
        private float _placeGround;

        private TownMetrics _metrics;
        private int _editCount;
        private int _savedEditCount;
        private bool _saving;
        private string _saveStatus;
        private bool _confirmLeave;
        private bool _exitRequested;
        private bool _leaveAfterSave;

        /// <summary>When to save next, in unscaled time. Infinity while there's nothing to save.</summary>
        private float _saveAt = float.PositiveInfinity;

        /// <summary>Someone else saved this town; saving again would only conflict, so stop until it's reopened.</summary>
        private bool _conflicted;

        private Material _validMaterial;
        private Material _invalidMaterial;
        private MeshFilter _ghost;
        private MeshFilter _flatGhost;

        /// <summary>While the player walks around the town, the builder hides its panel and ignores the mouse.</summary>
        public bool Paused { get; set; }

        /// <summary>A cottage was clicked with the Build tool: edit its inside.</summary>
        public event Action<BuildingDto> InteriorRequested;

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

        /// <summary>What the Paint tool lays: a <see cref="SurfaceType"/> id from <see cref="Catalog.Surfaces"/>.</summary>
        public string SurfaceId
        {
            get => _surfaceId;
            set
            {
                if (value == _surfaceId) return;
                _surfaceId = value;
                CancelPaintDrag();
            }
        }

        public bool SnapToGrid
        {
            get => _snapToGrid;
            set => _snapToGrid = value;
        }

        /// <summary>Beside a street or path, put buildings at its edge facing it. With this and grid snapping off, placement is free form.</summary>
        public bool SnapToRoads
        {
            get => _snapToRoads;
            set => _snapToRoads = value;
        }

        /// <summary>Whether the terrain tool raises the ground (or lowers it).</summary>
        public bool RaiseTerrain
        {
            get => _raiseTerrain;
            set => _raiseTerrain = value;
        }

        /// <summary>How heavy the town's grass, flowers and trees are. See <see cref="TownDto.Foliage"/>.</summary>
        public FoliageDto Foliage => _town?.Foliage ?? new FoliageDto();

        public void SetFoliage(FoliageDto foliage) => ApplyEdit(new SetFoliageCommand { Foliage = foliage });

        /// <summary>The Nature tool erases plants from the areas dragged out, rather than painting them in.</summary>
        public bool NatureErase
        {
            get => _natureErase;
            set => _natureErase = value;
        }

        public int FoliageAreaCount => _town?.FoliageAreas?.Count ?? 0;

        public void ClearFoliageAreas() => ApplyEdit(new ClearFoliageAreasCommand());

        /// <summary>Index into <see cref="PadSizes"/>.</summary>
        public int PadSize
        {
            get => _padSize;
            set => _padSize = Mathf.Clamp(value, 0, PadSizes.Length - 1);
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
            _confirmLeave = _exitRequested = _leaveAfterSave = _conflicted = false;
            _saveAt = float.PositiveInfinity;
            _paintStart = null;
            _paintKey = (-1, -1, null, -1);
            _terrainCheckKey = default;
            _placeCheckKey = default;

            _validMaterial = new Material(baseMaterial) { name = "Ghost (valid)" };
            _validMaterial.SetColor("_Tint", ValidTint);
            _invalidMaterial = new Material(baseMaterial) { name = "Ghost (invalid)" };
            _invalidMaterial.SetColor("_Tint", InvalidTint);
            _ghost = CreateGhost("Ghost");
            _flatGhost = CreateGhost("Flat ghost");
            _flatGhost.sharedMesh = new Mesh { name = "flat ghost" };
            BeginShapes(baseMaterial);
            _areasMaterial = new Material(baseMaterial) { name = "Foliage areas" };
            _areasView = CreateGhost("Foliage areas");
            _areasView.sharedMesh = new Mesh { name = "foliage areas" };
            _areasView.GetComponent<MeshRenderer>().sharedMaterial = _areasMaterial;
            _areasDrawnFor = -1;
            _areaStart = null;

            enabled = true;
        }

        public void End()
        {
            EndShapes();
            if (_areasView != null)
            {
                Destroy(_areasView.sharedMesh);
                Destroy(_areasView.gameObject);
            }

            if (_areasMaterial != null) Destroy(_areasMaterial);
            if (_ghost != null) Destroy(_ghost.gameObject);
            if (_flatGhost != null)
            {
                Destroy(_flatGhost.sharedMesh);
                Destroy(_flatGhost.gameObject);
            }

            if (_validMaterial != null) Destroy(_validMaterial);
            if (_invalidMaterial != null) Destroy(_invalidMaterial);
            if (_grid != null) _grid.HideCross();
            CancelPaintDrag();
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

            // Saving carries on while walking around, too.
            if (IsDirty && !_saving && Time.unscaledTime >= _saveAt) Save();

            if (Paused)
            {
                CancelAreaDrag();
                CancelPaintDrag();
                ShowAreas(false);
                LeaveShapeBuilder();
                HideGhosts();
                _grid.HideCross();
                return;
            }

            HandleHotkeys();
            _hint = null;
            FinishAreaDrag();
            FinishPaintDrag();
            ShowAreas(_tool == Tool.Nature);

            // The terrain builder works in the air, not on the ground under the pointer.
            if (InShapeBuilder)
            {
                HideGhosts();
                _grid.HideCross();
                UpdateShapeBuilder();
                return;
            }

            LeaveShapeBuilder();

            if (HudBar.PointerInBar() || !TryGroundPoint(out var cursor) || !InTown(cursor))
            {
                HideGhosts();
                _grid.HideCross();
                return;
            }

            // The red cross marks the grid point under the pointer, but the Paint tool works in whole tiles.
            if (_tool == Tool.Paint) _grid.HideCross();
            else _grid.ShowCross(ShownTown, cursor);

            switch (_tool)
            {
                case Tool.Build: UpdateBuild(cursor); break;
                case Tool.Paint: UpdatePaint(cursor); break;
                case Tool.Remove: UpdateRemove(cursor); break;
                case Tool.Terrain: UpdateTerrain(cursor); break;
                case Tool.Nature: UpdateNature(cursor); break;
            }
        }

        private void HandleHotkeys()
        {
            if (PInput.KeyDown(PKey.Alpha1)) SetTool(Tool.Build);
            if (PInput.KeyDown(PKey.Alpha2)) SetTool(Tool.Paint);
            if (PInput.KeyDown(PKey.Alpha3)) SetTool(Tool.Remove);
            if (PInput.KeyDown(PKey.Alpha4)) SetTool(Tool.Terrain);
            if (PInput.KeyDown(PKey.Alpha5)) SetTool(Tool.Nature);
            if (InShapeBuilder) return;
            if (PInput.KeyDown(PKey.Q)) Rotate(-RotationStep);
            if (PInput.KeyDown(PKey.E)) Rotate(RotationStep);
            if (PInput.KeyDown(PKey.Escape)) CancelPaintDrag();
        }

        public void Rotate(float degrees) => _rotation = TownMath.NormalizeAngle(_rotation + degrees);

        /// <summary>
        /// Back to the town list, saving first if there's anything unsaved. If that save fails, it asks,
        /// and calling again leaves anyway.
        /// </summary>
        public void Leave()
        {
            // An overlay that's showing becomes the ground on the way out, like leaving the builder any other way.
            LeaveShapeBuilder();
            if (!IsDirty || _confirmLeave)
            {
                _exitRequested = true;
                return;
            }

            _leaveAfterSave = true;
            if (!_saving) Save();
        }

        public void SetTool(Tool tool)
        {
            if (tool != _tool)
            {
                CancelAreaDrag();
                CancelPaintDrag();
            }

            _tool = tool;
            HideGhosts();
        }

        private void UpdateBuild(Vec2 cursor)
        {
            // Over a cottage, a click goes inside it instead.
            var existing = TownQueries.FindBuildingAt(_town, cursor);
            if (existing != null && CottageInterior.HasInterior(existing))
            {
                ShowGhost(MeshCache.Building(existing.TypeId), new Vec2(existing.X, existing.Z), TownTerrain.BaseHeight(_town, existing), existing.RotationDeg, 1.03f, true);
                _hint = "Click to edit the inside of this cottage";
                if (PointerGesture.Clicked) InteriorRequested?.Invoke(existing);
                return;
            }

            if (!Catalog.TryGetBuilding(_buildingTypeId, out var type)) return;

            var position = _snapToGrid ? TownGrid.Snap(cursor) : cursor;
            var rotation = _rotation;
            if (_snapToRoads && TownQueries.TrySnapToRoad(_town, type, cursor, out var snapped, out var heading))
            {
                // Beside a street or path, Q/E rotate relative to "facing it".
                position = snapped;
                rotation = heading + _rotation;
            }

            position = TownMath.Quantize(position);
            rotation = TownMath.NormalizeAngle(rotation);

            // Across tiles at different levels, they're levelled to the one under its middle, so show
            // the building at the height it will end up at.
            var key = (type.Id, position, rotation, _editCount);
            if (!key.Equals(_placeCheckKey))
            {
                _placeCheckKey = key;
                _placeCheck = TownRules.CanPlaceBuilding(_town, type.Id, position.X, position.Z, rotation, out var levelling);
                _placeGround = levelling != null && levelling.Count > 0 ? levelling[0].Level : TownTerrain.BaseHeight(_town, TownRules.Footprint(type, position.X, position.Z, rotation));
            }

            var check = _placeCheck;
            ShowGhost(MeshCache.Building(type.Id), position, _placeGround, rotation, 1f, check.Ok);
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

        /// <summary>
        /// Paints tiles: press on a tile and drag. Streets and paths run in a line from the first tile to
        /// the one under the pointer, a tile at a time, turning one corner (and ramping or stepping
        /// where the ground changes level); plazas, gardens and grass fill the rectangle. Letting go lays it.
        /// </summary>
        private void UpdatePaint(Vec2 cursor)
        {
            if (!Catalog.TryGetSurface(_surfaceId, out var surface)) return;
            var line = surface.Surface == TileSurface.Street || surface.Surface == TileSurface.Path;
            if (PInput.PrimaryPressedThisFrame() && _paintStart == null)
            {
                _paintStart = cursor;
                FlyCamera.PointerCaptured = true;
            }

            var start = _paintStart ?? cursor;
            if (!TownTiles.TryCell(_town, start, out var fi, out var fj) || !TownTiles.TryCell(_town, cursor, out var ti, out var tj))
            {
                HideGhosts();
                return;
            }

            var key = (TownTiles.Index(_town, fi, fj), TownTiles.Index(_town, ti, tj), surface.Id, _editCount);
            if (!key.Equals(_paintKey))
            {
                _paintKey = key;
                _paintCells = line ? TownTiles.Line(_town, start, cursor) : TownTiles.Area(_town, start, cursor);
                _paintCheck = TownTiles.CanPaint(_town, _paintCells, surface.Surface, line, out _);
            }

            var mb = new MeshBuilder();
            foreach (var k in _paintCells) TileOutline(mb, k, LowPolyMeshes.Hex(surface.Color));
            ShowFlatGhost(mb, _paintCheck.Ok);

            var dragging = _paintStart != null;
            if (!_paintCheck.Ok) _hint = _paintCheck.Error;
            else if (!dragging) _hint = line ? $"Drag to lay {surface.DisplayName.ToLowerInvariant()}, tile by tile" : $"Drag out an area of {surface.DisplayName.ToLowerInvariant()}";
            else _hint = line ? $"{_paintCells.Count} tile{(_paintCells.Count == 1 ? "" : "s")} of {surface.DisplayName.ToLowerInvariant()}. Let go to lay it, Esc to stop."
                : $"{_paintCells.Count} tile{(_paintCells.Count == 1 ? "" : "s")}. Let go to lay it, Esc to stop.";
        }

        /// <summary>Letting go of a paint drag lays it, if it can be laid.</summary>
        private void FinishPaintDrag()
        {
            if (_paintStart == null || PInput.PrimaryHeld()) return;
            CancelPaintDrag();
            if (!_paintCheck.Ok || _paintCells.Count == 0 || !Catalog.TryGetSurface(_surfaceId, out var surface)) return;
            var line = surface.Surface == TileSurface.Street || surface.Surface == TileSurface.Path;
            Apply(new PaintTilesCommand { Cells = new System.Collections.Generic.List<int>(_paintCells), SurfaceId = surface.Id, Line = line });
        }

        private void CancelPaintDrag()
        {
            if (_paintStart == null) return;
            _paintStart = null;
            _paintKey = (-1, -1, null, -1);
            FlyCamera.PointerCaptured = false;
        }

        /// <summary>A tile's outline, lying on it, with a faint fill in <paramref name="fill"/>.</summary>
        private void TileOutline(MeshBuilder mb, int k, Color fill)
        {
            var n = TownTiles.Cells(_town);
            var c = TownTiles.Center(_town, k % n, k / n);
            var half = TownTiles.Spacing(_town) * 0.5f;
            TerrainMeshes.SquareOutline(mb, ShownTown, c, half - 0.15f, 0.3f, Color.white, 0.14f);
            var inner = half - 0.5f;
            TerrainMeshes.Drape(mb, ShownTown, new System.Collections.Generic.List<Vec2>
            {
                c + new Vec2(-inner, -inner), c + new Vec2(inner, -inner), c + new Vec2(inner, inner), c + new Vec2(-inner, inner),
            }, fill, 0.12f);
        }

        /// <summary>The middle of the grid square a point is in.</summary>
        private static Vec2 CellCenter(Vec2 point)
        {
            var step = TownGrid.Spacing;
            return new Vec2((MathF.Floor(point.X / step) + 0.5f) * step, (MathF.Floor(point.Z / step) + 0.5f) * step);
        }

        private void UpdateRemove(Vec2 cursor)
        {
            var building = TownQueries.FindBuildingAt(_town, cursor);
            if (building != null)
            {
                ShowGhost(MeshCache.Building(building.TypeId), new Vec2(building.X, building.Z), TownTerrain.BaseHeight(_town, building), building.RotationDeg, 1.04f, false);
                _hint = "Click to remove this building";
                if (PointerGesture.Clicked) Apply(new RemoveBuildingCommand { BuildingId = building.Id });
                return;
            }

            // Paving and gardens go back to grass, a tile at a time.
            if (TownTiles.TryCell(_town, cursor, out var i, out var j) && TownTiles.Surface(_town, i, j) != TileSurface.Grass)
            {
                var k = TownTiles.Index(_town, i, j);
                var surface = Catalog.Surface(TownTiles.Surface(_town, i, j));
                var mb = new MeshBuilder();
                TileOutline(mb, k, Color.white);
                ShowFlatGhost(mb, false);
                _hint = $"Click to turn this {surface.DisplayName.ToLowerInvariant()} back to grass";
                if (PointerGesture.Clicked) Apply(new PaintTilesCommand { Cells = new System.Collections.Generic.List<int> { k }, SurfaceId = Catalog.Surface(TileSurface.Grass).Id });
                return;
            }

            HideGhosts();
        }


        /// <summary>
        /// Raises or lowers the tiles in a square (one tile, or round the grid point under the pointer) a
        /// level, a metre. Where they meet lower tiles, stone walls hold them up; streets ramp and paths
        /// step up to tiles a level higher. Buildings wholly in the square ride along.
        /// </summary>
        private void UpdateTerrain(Vec2 cursor)
        {
            // A block is a grid square; bigger pads centre on a grid point.
            var center = _padSize == BlockPad ? CellCenter(cursor) : TownGrid.Snap(cursor);
            var halfSize = PadSizes[_padSize].HalfSize;
            var delta = _raiseTerrain ? 1f : -1f;
            var key = (center, halfSize, delta, _editCount);
            if (!key.Equals(_terrainCheckKey))
            {
                _terrainCheckKey = key;
                _terrainCheck = TownRules.CanShapeTerrain(_town, center, halfSize, delta, out _terrainChanges);
            }

            // The square, and an arrow-ish mark on each tile that will move.
            var mb = new MeshBuilder();
            TerrainMeshes.SquareOutline(mb, ShownTown, center, halfSize, 0.5f, Color.white, 0.14f);
            if (_terrainCheck.Ok && _terrainChanges != null)
            {
                var n = TownTiles.Cells(_town);
                foreach (var change in _terrainChanges)
                {
                    var c = TownTiles.Center(_town, change.Index % n, change.Index / n);
                    TerrainMeshes.Disc(mb, ShownTown, c, 0.45f, Color.white, 0.14f);
                }
            }

            ShowFlatGhost(mb, _terrainCheck.Ok);
            _hint = _terrainCheck.Ok ? null : _terrainCheck.Error;

            if (_terrainCheck.Ok && PointerGesture.Clicked)
            {
                Apply(new ShapeTerrainCommand { X = center.X, Z = center.Z, PadHalfSize = halfSize, Delta = delta });
            }
        }

        /// <summary>
        /// Drag out a rectangle on the ground (snapped to the foliage spacing) to paint where plants grow,
        /// or erase them. The drag holds the camera still; middle-drag or the arrow keys still move it.
        /// </summary>
        private void UpdateNature(Vec2 cursor)
        {
            var step = Porchlight.Shared.Rules.Foliage.Spacing;
            var p = new Vec2(MathF.Round(cursor.X / step) * step, MathF.Round(cursor.Z / step) * step);
            var mb = new MeshBuilder();
            if (_areaStart == null)
            {
                TerrainMeshes.SquareOutline(mb, ShownTown, p, step * 0.5f, 0.25f, Color.white, 0.12f);
                ShowFlatGhost(mb, !_natureErase);
                _hint = _natureErase ? "Drag to clear plants from an area" : "Drag to mark an area where plants grow";
                if (PInput.PrimaryPressedThisFrame())
                {
                    _areaStart = p;
                    _areaEnd = p;
                    FlyCamera.PointerCaptured = true;
                }

                return;
            }

            _areaEnd = p;
            RectOutline(mb, ShownTown, _areaStart.Value, p, 0.35f, Color.white);
            ShowFlatGhost(mb, !_natureErase);
            _hint = $"{MathF.Abs(p.X - _areaStart.Value.X):0} × {MathF.Abs(p.Z - _areaStart.Value.Z):0} m";
        }

        /// <summary>Letting go of a drag paints (or erases) the area, wherever the pointer ended up.</summary>
        private void FinishAreaDrag()
        {
            if (_areaStart == null || PInput.PrimaryHeld()) return;
            var start = _areaStart.Value;
            CancelAreaDrag();
            if (MathF.Abs(_areaEnd.X - start.X) < 1f || MathF.Abs(_areaEnd.Z - start.Z) < 1f) return;
            Apply(new AddFoliageAreaCommand { Area = new FoliageAreaDto { X0 = start.X, Z0 = start.Z, X1 = _areaEnd.X, Z1 = _areaEnd.Z, Erase = _natureErase } });
        }

        private void CancelAreaDrag()
        {
            if (_areaStart == null) return;
            _areaStart = null;
            FlyCamera.PointerCaptured = false;
        }

        /// <summary>The painted areas' outlines in the Nature tool: green where plants grow, red where they were erased.</summary>
        private void ShowAreas(bool show)
        {
            if (_areasView == null) return;
            _areasView.gameObject.SetActive(show);
            if (!show || _areasDrawnFor == _editCount) return;
            _areasDrawnFor = _editCount;

            var mb = new MeshBuilder();
            var paint = LowPolyMeshes.Hex("#9BE07A");
            var erase = LowPolyMeshes.Hex("#E86A5A");
            foreach (var area in _town.FoliageAreas ?? new System.Collections.Generic.List<FoliageAreaDto>())
            {
                RectOutline(mb, ShownTown, new Vec2(area.X0, area.Z0), new Vec2(area.X1, area.Z1), 0.3f, area.Erase ? erase : paint);
            }

            mb.ToMesh(_areasView.sharedMesh);
        }

        private static void RectOutline(MeshBuilder mb, TownDto town, Vec2 a, Vec2 b, float width, Color color)
        {
            var c1 = new Vec2(b.X, a.Z);
            var c3 = new Vec2(a.X, b.Z);
            TerrainMeshes.Strip(mb, town, a, c1, width, color, 0.1f);
            TerrainMeshes.Strip(mb, town, c1, b, width, color, 0.1f);
            TerrainMeshes.Strip(mb, town, b, c3, width, color, 0.1f);
            TerrainMeshes.Strip(mb, town, c3, a, width, color, 0.1f);
        }

        private bool Apply(TownCommand command) => ApplyEdit(command).Ok;

        /// <summary>Makes a change to the town, redraws it, and saves it shortly. The interior editor's edits come through here too.</summary>
        public EditResult ApplyEdit(TownCommand command)
        {
            var result = TownEditor.Apply(_town, command);
            if (!result.Ok)
            {
                _hint = result.Error;
                return result;
            }

            // Painting, raising and lowering, levelling for a building and the overlay change the tiles:
            // any overlay preview is out of date, and the grid lies on the tiles.
            var tiles = command is PaintTilesCommand || command is ShapeTerrainCommand || command is PlaceBuildingCommand || command is ApplyTerrainOverlayCommand;
            if (tiles)
            {
                DropOverlayPreview();
                ShowGround();
            }
            else
            {
                // While the overlay is previewed, the buildings stand on it.
                _view.Sync(ShownTown);
            }

            _metrics = TownMetrics.Compute(_town);
            _editCount++;
            _saveStatus = null;
            _confirmLeave = false;
            if (!_conflicted) _saveAt = Time.unscaledTime + AutoSaveDelay;
            return result;
        }

        /// <summary>Saves now. Normally this happens by itself shortly after each edit.</summary>
        public async void Save()
        {
            if (_saving || !IsDirty) return;
            _saving = true;
            _saveAt = float.PositiveInfinity;
            _saveStatus = "Saving…";
            var editsAtSave = _editCount;
            var town = _town;
            try
            {
                var saved = await _source.SaveTownAsync(town);
                if (this == null || _town != town) return;
                _town.Version = saved.Version;
                _savedEditCount = editsAtSave;
                _saveStatus = "All changes saved";

                // Edits made while this save was on its way get their own save.
                if (IsDirty) _saveAt = Time.unscaledTime + AutoSaveDelay;
                else if (_leaveAfterSave) _exitRequested = true;
            }
            catch (TownApiException e)
            {
                if (this == null || _town != town) return;
                if (e.IsConflict)
                {
                    _conflicted = true;
                    _saveStatus = "Someone else saved this town. Go back and reopen it to get their changes.";
                }
                else
                {
                    _saveStatus = "Couldn't save: " + e.Message;
                    _saveAt = Time.unscaledTime + RetryDelay;
                }

                SaveFailed();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (this == null || _town != town) return;
                _saveStatus = "Couldn't save: " + e.Message;
                _saveAt = Time.unscaledTime + RetryDelay;
                SaveFailed();
            }
            finally
            {
                _saving = false;
            }
        }

        /// <summary>If we were saving on the way out, ask before leaving without it.</summary>
        private void SaveFailed()
        {
            if (!_leaveAfterSave) return;
            _leaveAfterSave = false;
            _confirmLeave = true;
        }

        private bool InTown(Vec2 point)
        {
            var half = _town.Size * 0.5f;
            return Mathf.Abs(point.X) <= half && Mathf.Abs(point.Z) <= half;
        }

        private bool TryGroundPoint(out Vec2 point)
        {
            point = default;
            if (!TerrainMeshes.Raycast(ShownTown, _camera.ScreenPointToRay(PInput.PointerPosition()), out var hit)) return false;
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

        private void ShowGhost(Mesh mesh, Vec2 position, float ground, float rotationDeg, float scale, bool valid)
        {
            _flatGhost.gameObject.SetActive(false);
            _ghost.sharedMesh = mesh;
            _ghost.GetComponent<MeshRenderer>().sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            _ghost.transform.SetPositionAndRotation(new Vector3(position.X, ground + 0.02f, position.Z), Quaternion.Euler(0f, rotationDeg, 0f));
            _ghost.transform.localScale = Vector3.one * scale;
            _ghost.gameObject.SetActive(true);
        }

        /// <summary>Shows something lying on the ground (tiles to paint, a pad's outline) in the ghost colours.</summary>
        private void ShowFlatGhost(MeshBuilder mb, bool valid)
        {
            _ghost.gameObject.SetActive(false);
            mb.ToMesh(_flatGhost.sharedMesh);
            _flatGhost.GetComponent<MeshRenderer>().sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            _flatGhost.gameObject.SetActive(true);
        }

        private void HideGhosts()
        {
            if (_ghost != null) _ghost.gameObject.SetActive(false);
            if (_flatGhost != null) _flatGhost.gameObject.SetActive(false);
        }
    }
}
