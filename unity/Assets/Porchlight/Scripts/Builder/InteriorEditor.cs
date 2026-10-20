using System;
using System.Collections.Generic;
using Porchlight.App;
using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Builder
{
    /// <summary>
    /// Edits the inside of one cottage, one floor at a time. The camera circles the cottage (see
    /// <see cref="OrbitCamera"/>), which is cut away to show the floor being edited. Draw walls, put
    /// doors and windows in them, place furniture and move the stairs; everything snaps (see
    /// <see cref="InteriorSnap"/>) and is checked by the shared <see cref="InteriorRules"/>. Edits go
    /// through the builder, so they save themselves like any other. The controls are in the bottom
    /// bar (<see cref="App.InteriorHud"/>); keys: 1-4 tools, F floor, Q/E turn, Esc stop or leave.
    /// </summary>
    public sealed class InteriorEditor : MonoBehaviour
    {
        public enum Tool
        {
            Walls,
            Place,
            Stairs,
            Remove,
        }

        public const float RotationStep = 90f;

        /// <summary>How tall ghosts of walls are drawn.</summary>
        private const float GhostWallHeight = 2.4f;

        private static readonly Color ValidTint = new Color(0.55f, 1f, 0.55f, 1f);
        private static readonly Color InvalidTint = new Color(1f, 0.35f, 0.35f, 1f);

        private TownDto _town;
        private BuildingDto _building;
        private BuildingType _type;
        private TownView _view;
        private Camera _camera;
        private OrbitCamera _orbit;
        private Func<TownCommand, EditResult> _apply;
        private Action _onDone;

        private Tool _tool = Tool.Walls;
        private int _floor;
        private string _itemTypeId = Furnishings.Door;
        private float _rotation;
        private Vec2? _wallStart;
        private string _hint;
        private bool _exitRequested;

        private Material _validMaterial;
        private Material _invalidMaterial;
        private MeshFilter _ghost;
        private MeshFilter _grid;
        private string _gridKey;

        /// <summary>Keeps the camera and the cut-away view, but leaves the pointer and keys alone (while dropping the character in).</summary>
        public bool Paused { get; set; }

        public BuildingDto Building => _building;
        public Tool CurrentTool => _tool;
        public int Floor => _floor;
        public string ItemTypeId => _itemTypeId;
        public float Rotation => _rotation;
        public bool DrawingWall => _wallStart.HasValue;

        /// <summary>What the pointer is over, or why it can't go there.</summary>
        public string Hint => _hint;

        public void Begin(TownDto town, BuildingDto building, TownView view, Camera camera, OrbitCamera orbit, Material baseMaterial,
            Func<TownCommand, EditResult> apply, Action onDone)
        {
            _town = town;
            _building = building;
            Catalog.TryGetBuilding(building.TypeId, out _type);
            _view = view;
            _camera = camera;
            _orbit = orbit;
            _apply = apply;
            _onDone = onDone;
            _floor = 0;
            _wallStart = null;
            _exitRequested = false;
            _gridKey = null;
            Paused = false;

            _validMaterial = new Material(baseMaterial) { name = "Interior ghost (valid)" };
            _validMaterial.SetColor("_Tint", ValidTint);
            _invalidMaterial = new Material(baseMaterial) { name = "Interior ghost (invalid)" };
            _invalidMaterial.SetColor("_Tint", InvalidTint);
            _ghost = CreateChild("Interior ghost", null);
            _grid = CreateChild("Interior grid", baseMaterial);

            // Start looking at the front of the house from above, three-quarters on.
            _orbit.Begin(FloorCenter(), building.RotationDeg + 155f, 58f, 15f);
            enabled = true;
        }

        public void End()
        {
            if (_ghost != null)
            {
                Destroy(_ghost.sharedMesh);
                Destroy(_ghost.gameObject);
            }

            if (_grid != null)
            {
                Destroy(_grid.sharedMesh);
                Destroy(_grid.gameObject);
            }

            if (_validMaterial != null) Destroy(_validMaterial);
            if (_invalidMaterial != null) Destroy(_invalidMaterial);
            if (_view != null) _view.SetCutaway(null, 0, 0);
            if (_orbit != null) _orbit.enabled = false;
            _building = null;
            _town = null;
            enabled = false;
        }

        /// <summary>Back to the builder, on the next frame.</summary>
        public void Done() => _exitRequested = true;

        public void SetTool(Tool tool)
        {
            _tool = tool;
            _wallStart = null;
        }

        public void SelectItem(string typeId)
        {
            _itemTypeId = typeId;
            SetTool(Tool.Place);
        }

        public void SetFloor(int floor)
        {
            _floor = Mathf.Clamp(floor, 0, CottageInterior.FloorCount - 1);
            _wallStart = null;
        }

        public void Rotate(float degrees) => _rotation = TownMath.NormalizeAngle(_rotation + degrees);

        private void Update()
        {
            if (_building == null) return;
            if (_exitRequested)
            {
                _exitRequested = false;
                _onDone();
                return;
            }

            if (!Paused) HandleKeys();
            if (_building == null) return;

            _orbit.Center = FloorCenter();
            _view.SetCutaway(_building.Id, _floor, SidesFacing(_camera.transform.forward, _building.RotationDeg));
            UpdateGrid();

            _hint = null;
            if (Paused)
            {
                _ghost.gameObject.SetActive(false);
                return;
            }

            if (HudBar.PointerInBar() || !TryPointer(out var cursor))
            {
                _ghost.gameObject.SetActive(false);
                return;
            }

            switch (_tool)
            {
                case Tool.Walls: UpdateWalls(cursor); break;
                case Tool.Place: UpdatePlace(cursor); break;
                case Tool.Stairs: UpdateStairs(cursor); break;
                case Tool.Remove: UpdateRemove(cursor); break;
            }
        }

        private void HandleKeys()
        {
            if (PInput.KeyDown(PKey.Alpha1)) SetTool(Tool.Walls);
            if (PInput.KeyDown(PKey.Alpha2)) SetTool(Tool.Place);
            if (PInput.KeyDown(PKey.Alpha3)) SetTool(Tool.Stairs);
            if (PInput.KeyDown(PKey.Alpha4)) SetTool(Tool.Remove);
            if (PInput.KeyDown(PKey.F)) SetFloor(1 - _floor);
            if (PInput.KeyDown(PKey.Q)) Rotate(-RotationStep);
            if (PInput.KeyDown(PKey.E)) Rotate(RotationStep);
            if (PInput.KeyDown(PKey.Escape))
            {
                if (_wallStart.HasValue) _wallStart = null;
                else Done();
            }
        }

        private InteriorDto Interior => CottageInterior.Of(_building);
        private float FloorY => CottageInterior.FloorHeight(_floor);

        // ----- Tools -----

        /// <summary>Click to start a wall and click to end it; it carries on from there until Esc or a click on its own end.</summary>
        private void UpdateWalls(Vec2 cursor)
        {
            var interior = Interior;
            var end = InteriorSnap.WallPoint(_type, interior, _floor, cursor, _wallStart);
            var mb = new MeshBuilder();
            if (_wallStart == null)
            {
                Post(mb, end);
                ShowGhost(mb, true);
                _hint = "Click to start a wall";
                if (PointerGesture.Clicked) _wallStart = end;
                return;
            }

            var start = _wallStart.Value;
            var check = InteriorRules.CanAddWall(_type, interior, _floor, start, end);
            var line = new InteriorWallDto { X0 = start.X, Z0 = start.Z, X1 = end.X, Z1 = end.Z };
            if (CottageInterior.TryWallLine(line, out var wall) && wall.Length > 0.01f)
            {
                var box = wall.Box;
                mb.Box(new Vector3(box.Center.X, FloorY, box.Center.Z), box.HalfX * 2f, GhostWallHeight, box.HalfZ * 2f, Color.white, Color.white);
            }

            Post(mb, start);
            Post(mb, end);
            ShowGhost(mb, check.Ok);
            _hint = check.Ok ? $"{Vec2.Distance(start, end):0.00} m  ·  Click to build it. Esc to stop." : check.Error;
            if (!PointerGesture.Clicked) return;

            if (end.Equals(start))
            {
                _wallStart = null;
                return;
            }

            if (check.Ok && Apply(new AddInteriorWallCommand { WallId = Ids.New(), X0 = start.X, Z0 = start.Z, X1 = end.X, Z1 = end.Z })) _wallStart = end;
        }

        private void UpdatePlace(Vec2 cursor)
        {
            if (Furnishings.TryGetOpening(_itemTypeId, out var opening))
            {
                PlaceOpening(opening, cursor);
                return;
            }

            if (!Furnishings.TryGetFurniture(_itemTypeId, out var kind)) return;

            var interior = Interior;
            var (position, rotation, onId) = InteriorSnap.Item(_type, interior, _floor, kind, cursor, _rotation);
            var check = InteriorRules.CanPlaceItem(_type, interior, _floor, kind.Id, position.X, position.Z, rotation, onId);

            var mb = new MeshBuilder();
            var onWall = onId == null && kind.Placement == FurniturePlacement.DresserOrWall;
            InteriorMeshes.Furniture(mb, kind, position.X, position.Z, rotation, FloorY + 0.01f, onId != null, onWall);
            ShowGhost(mb, check.Ok);
            _hint = check.Ok ? $"Click to place the {kind.DisplayName.ToLowerInvariant()}" : check.Error;

            if (check.Ok && PointerGesture.Clicked)
            {
                Apply(new PlaceFurnitureCommand { ItemId = Ids.New(), TypeId = kind.Id, X = position.X, Z = position.Z, RotationDeg = rotation, OnId = onId });
            }
        }

        private void PlaceOpening(OpeningType kind, Vec2 cursor)
        {
            var interior = Interior;
            if (!InteriorSnap.Opening(_type, interior, _floor, kind.Id, cursor, out var wallId, out var at)
                || !CottageInterior.TryFindWall(_type, interior.Floors[_floor], wallId, out var wall))
            {
                _ghost.gameObject.SetActive(false);
                _hint = kind.IsDoor ? "Point at a wall to put a door in it" : "Point at a wall to put a window in it";
                return;
            }

            var check = InteriorRules.CanAddOpening(_type, interior, _floor, kind.Id, wallId, at);
            var mb = new MeshBuilder();
            var box = wall.Span(at - kind.Width * 0.5f, at + kind.Width * 0.5f, 0.04f);
            mb.Box(new Vector3(box.Center.X, FloorY + kind.Bottom, box.Center.Z), box.HalfX * 2f, kind.Top - kind.Bottom, box.HalfZ * 2f, Color.white, Color.white);
            ShowGhost(mb, check.Ok);
            _hint = check.Ok ? $"Click to put a {kind.DisplayName.ToLowerInvariant()} here" : check.Error;

            if (check.Ok && PointerGesture.Clicked)
            {
                Apply(new AddOpeningCommand { OpeningId = Ids.New(), TypeId = kind.Id, WallId = wallId, At = at });
            }
        }

        private void UpdateStairs(Vec2 cursor)
        {
            var interior = Interior;
            var position = InteriorSnap.Stairs(_type, interior, cursor, _rotation);
            var stairs = new StairsDto { X = position.X, Z = position.Z, RotationDeg = _rotation };
            var check = InteriorRules.CanMoveStairs(_type, interior, position.X, position.Z, _rotation);

            var mb = new MeshBuilder();
            InteriorMeshes.Stairs(mb, stairs, false);

            // The landings at the foot and the head, which have to stay clear.
            for (var floor = 0; floor < CottageInterior.FloorCount; floor++)
            {
                var landing = CottageInterior.StairsLanding(stairs, floor);
                mb.Box(new Vector3(landing.Center.X, CottageInterior.FloorHeight(floor) + 0.01f, landing.Center.Z), landing.HalfX * 2f, 0.03f, landing.HalfZ * 2f, Color.white, Color.white);
            }

            ShowGhost(mb, check.Ok);
            _hint = check.Ok ? "Click to move the stairs here  ·  Q / E turns them" : check.Error;

            if (check.Ok && PointerGesture.Clicked)
            {
                Apply(new MoveStairsCommand { X = position.X, Z = position.Z, RotationDeg = _rotation });
            }
        }

        private void UpdateRemove(Vec2 cursor)
        {
            var interior = Interior;
            var floor = interior.Floors[_floor];
            var mb = new MeshBuilder();
            string partId = null;
            string what = null;

            // TVs sit on top of dressers, so look for them first; then other furniture, doors and windows, and walls.
            foreach (var pass in new[] { true, false })
            {
                for (var i = floor.Items.Count - 1; i >= 0 && partId == null; i--)
                {
                    var item = floor.Items[i];
                    if ((item.TypeId == Furnishings.Tv) != pass || !CottageInterior.TryItemBox(item, out var kind, out var box)) continue;
                    var onWall = item.OnId == null && kind.Placement == FurniturePlacement.DresserOrWall;

                    // TVs are up on a wall or a dresser, so look where the pointer is at their height.
                    var tv = item.TypeId == Furnishings.Tv;
                    var lift = onWall ? CottageInterior.WallTvBottom : item.OnId != null ? 0.96f : 0f;
                    var under = cursor;
                    if (tv && !TryPointer(lift + kind.Height * 0.5f, out under)) continue;
                    if (!box.Expanded(tv ? 0.15f : 0f).Contains(under)) continue;
                    InteriorMeshes.Furniture(mb, kind, item.X, item.Z, item.RotationDeg, FloorY + 0.01f, item.OnId != null, onWall);
                    partId = item.Id;
                    what = kind.DisplayName.ToLowerInvariant();
                }
            }

            if (partId == null)
            {
                foreach (var opening in CottageInterior.Openings(interior, _floor))
                {
                    if (!Furnishings.TryGetOpening(opening.TypeId, out var kind) || !CottageInterior.TryFindWall(_type, floor, opening.WallId, out var wall)) continue;
                    var width = CottageInterior.OpeningWidth(opening);
                    if (!wall.Span(opening.At - width * 0.5f, opening.At + width * 0.5f, 0.3f).Contains(cursor)) continue;
                    var box = wall.Span(opening.At - width * 0.5f, opening.At + width * 0.5f, 0.04f);
                    mb.Box(new Vector3(box.Center.X, FloorY + kind.Bottom, box.Center.Z), box.HalfX * 2f, kind.Top - kind.Bottom, box.HalfZ * 2f, Color.white, Color.white);
                    partId = opening.Id;
                    what = kind.DisplayName.ToLowerInvariant();
                    break;
                }
            }

            if (partId == null)
            {
                foreach (var dto in floor.Walls)
                {
                    if (!CottageInterior.TryWallLine(dto, out var wall) || !wall.Box.Expanded(0.2f).Contains(cursor)) continue;
                    var box = wall.Box.Expanded(0.03f);
                    mb.Box(new Vector3(box.Center.X, FloorY, box.Center.Z), box.HalfX * 2f, GhostWallHeight, box.HalfZ * 2f, Color.white, Color.white);
                    partId = dto.Id;
                    what = "wall";
                    break;
                }
            }

            if (partId == null)
            {
                _ghost.gameObject.SetActive(false);
                _hint = "Point at a wall, door, window or piece of furniture";
                return;
            }

            ShowGhost(mb, false);
            if (partId == CottageInterior.FrontDoorId)
            {
                _hint = "The front door stays";
                return;
            }

            _hint = what == "wall" ? "Click to remove this wall and its doors and windows" : $"Click to remove this {what}";
            if (PointerGesture.Clicked) Apply(new RemoveInteriorPartCommand { PartId = partId });
        }

        private bool Apply(InteriorCommand command)
        {
            command.BuildingId = _building.Id;
            command.Floor = _floor;
            var result = _apply(command);
            if (!result.Ok) _hint = result.Error;
            return result.Ok;
        }

        // ----- Helpers -----

        /// <summary>The middle of the floor being edited, in the town. The camera stays centred on it.</summary>
        private Vector3 FloorCenter() =>
            new Vector3(_building.X, TownTerrain.BaseHeight(_town, _building) + FloorY + 0.8f, _building.Z);

        /// <summary>The point on the floor being edited under the pointer, in the cottage's frame.</summary>
        private bool TryPointer(out Vec2 local) => TryPointer(0f, out local);

        /// <summary>Where the pointer is at <paramref name="height"/> above the floor being edited, in the cottage's frame.</summary>
        private bool TryPointer(float height, out Vec2 local)
        {
            local = default;
            var ray = _camera.ScreenPointToRay(PInput.PointerPosition());
            var plane = new Plane(Vector3.up, Vector3.up * (TownTerrain.BaseHeight(_town, _building) + FloorY + height));
            if (!plane.Raycast(ray, out var distance)) return false;
            var hit = ray.GetPoint(distance);
            local = Frames.ToLocal(new Vec2(_building.X, _building.Z), _building.RotationDeg, new Vec2(hit.x, hit.z));

            // Only inside the house, give or take a little for reaching the walls.
            return CottageInterior.Inside(_type).Expanded(0.6f).Contains(local);
        }

        /// <summary>The outside walls facing the camera, which get lowered so you can see in.</summary>
        public static int SidesFacing(Vector3 cameraForward, float buildingRotationDeg)
        {
            var local = Quaternion.Inverse(Quaternion.Euler(0f, buildingRotationDeg, 0f)) * -cameraForward;
            var flat = new Vector2(local.x, local.z);
            if (flat.sqrMagnitude < 1e-4f) return 0;
            flat.Normalize();
            var sides = 0;
            if (flat.y > 0.3f) sides |= InteriorMeshes.Front;
            if (flat.y < -0.3f) sides |= InteriorMeshes.Back;
            if (flat.x > 0.3f) sides |= InteriorMeshes.Right;
            if (flat.x < -0.3f) sides |= InteriorMeshes.Left;
            return sides;
        }

        private void Post(MeshBuilder mb, Vec2 at) => mb.Box(new Vector3(at.X, FloorY, at.Z), 0.16f, GhostWallHeight + 0.1f, 0.16f, Color.white, Color.white);

        private void ShowGhost(MeshBuilder mb, bool valid)
        {
            mb.ToMesh(_ghost.sharedMesh);
            _ghost.GetComponent<MeshRenderer>().sharedMaterial = valid ? _validMaterial : _invalidMaterial;
            PlaceOnBuilding(_ghost.transform);
            _ghost.gameObject.SetActive(true);
        }

        private void PlaceOnBuilding(Transform t) => t.SetPositionAndRotation(
            new Vector3(_building.X, TownTerrain.BaseHeight(_town, _building), _building.Z), Quaternion.Euler(0f, _building.RotationDeg, 0f));

        /// <summary>Faint lines every <see cref="CottageInterior.Grid"/> on the floor being edited, darker each metre.</summary>
        private void UpdateGrid()
        {
            var interior = Interior;
            var key = $"{_floor}|{interior.Stairs?.X}|{interior.Stairs?.Z}|{interior.Stairs?.RotationDeg}";
            if (key != _gridKey)
            {
                _gridKey = key;
                var inside = CottageInterior.Inside(_type);
                var pieces = new List<Aabb> { inside };
                if (_floor > 0 && interior.Stairs != null) pieces = Around(inside, CottageInterior.StairsBox(interior.Stairs));

                var mb = new MeshBuilder();
                var y = FloorY + 0.012f;
                var fine = LowPolyMeshes.Hex("#B48D67");
                var bold = LowPolyMeshes.Hex("#9E7853");
                foreach (var piece in pieces)
                {
                    for (var x = Mathf.Ceil(piece.MinX / CottageInterior.Grid) * CottageInterior.Grid; x <= piece.MaxX + 0.001f; x += CottageInterior.Grid)
                    {
                        var metre = Mathf.Abs(x - Mathf.Round(x)) < 0.01f;
                        Line(mb, new Vector3(x, y, piece.MinZ), new Vector3(x, y, piece.MaxZ), metre ? 0.025f : 0.012f, metre ? bold : fine);
                    }

                    for (var z = Mathf.Ceil(piece.MinZ / CottageInterior.Grid) * CottageInterior.Grid; z <= piece.MaxZ + 0.001f; z += CottageInterior.Grid)
                    {
                        var metre = Mathf.Abs(z - Mathf.Round(z)) < 0.01f;
                        Line(mb, new Vector3(piece.MinX, y, z), new Vector3(piece.MaxX, y, z), metre ? 0.025f : 0.012f, metre ? bold : fine);
                    }
                }

                mb.ToMesh(_grid.sharedMesh);
            }

            PlaceOnBuilding(_grid.transform);
            _grid.gameObject.SetActive(true);
        }

        private static void Line(MeshBuilder mb, Vector3 a, Vector3 b, float width, Color color)
        {
            var side = Vector3.Cross(Vector3.up, (b - a).normalized) * (width * 0.5f);
            mb.Quad(a - side, a + side, b + side, b - side, color, Vector3.up);
        }

        /// <summary>A rectangle less a hole in it, as up to four rectangles.</summary>
        private static List<Aabb> Around(in Aabb outer, in Aabb hole)
        {
            var pieces = new List<Aabb>();
            if (hole.MinX > outer.MinX) pieces.Add(new Aabb(outer.MinX, outer.MinZ, hole.MinX, outer.MaxZ));
            if (hole.MaxX < outer.MaxX) pieces.Add(new Aabb(hole.MaxX, outer.MinZ, outer.MaxX, outer.MaxZ));
            if (hole.MinZ > outer.MinZ) pieces.Add(new Aabb(hole.MinX, outer.MinZ, hole.MaxX, hole.MinZ));
            if (hole.MaxZ < outer.MaxZ) pieces.Add(new Aabb(hole.MinX, hole.MaxZ, hole.MaxX, outer.MaxZ));
            return pieces;
        }

        private MeshFilter CreateChild(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = new Mesh { name = name };
            go.SetActive(false);
            return filter;
        }
    }
}
