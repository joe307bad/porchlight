using System.Collections.Generic;
using Porchlight.App;
using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Editing;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Builder
{
    /// <summary>
    /// The terrain tool's Builder mode. Pick a triangle, sphere or cube and place it like a building,
    /// except it can go anywhere in the air: the pointer puts it over a spot, the scroll wheel lifts
    /// it, the arrow keys slide it, Q/E turn it and R/F tilt it. A placed shape can be clicked (or
    /// picked in the layers panel) to select it, dragged to move it across at its height, moved with
    /// the same keys, or resized by dragging the corners of the box around it, evenly or (with
    /// free-form on) one way at a time.
    ///
    /// Each shape has its own overlay switch, which shapes the ground to take that shape's form
    /// (<see cref="TerrainShapes.Overlay"/>) and shows the result. Leaving Builder mode makes the
    /// overlaid ground the real ground, ready to build on.
    /// </summary>
    public sealed partial class BuilderTool
    {
        public enum TerrainMode
        {
            Simple,
            Builder,
        }

        /// <summary>How far one press of Q/E or R/F turns a shape, in degrees.</summary>
        public const float ShapeTurnStep = 15f;

        /// <summary>Metres a second the arrow keys slide a shape (three times that with Shift).</summary>
        private const float ShapeMoveSpeed = 12f;

        /// <summary>How close, in pixels, the pointer has to be to a corner to grab it.</summary>
        private const float HandleReach = 16f;

        /// <summary>Seconds after a shape stops moving before the move is saved as an edit.</summary>
        private const float CommitDelay = 0.35f;

        /// <summary>Shortest time between redrawing the overlay while shapes move.</summary>
        private const float OverlayRefresh = 0.15f;

        private static readonly Color GhostValid = new Color(0.55f, 1f, 0.55f, 0.45f);
        private static readonly Color GhostInvalid = new Color(1f, 0.35f, 0.35f, 0.45f);

        private TerrainMode _terrainMode;
        private bool _shapesActive;

        private string _placingKind;
        private TerrainShapeDto _ghostShape;
        private float _ghostLift;
        private bool _ghostOverGround;

        // The selected shape. Moves change this copy, which becomes an edit once it stops moving.
        private string _selectedId;
        private TerrainShapeDto _working;
        private bool _workingDirty;
        private float _workingChangedAt;
        private int _workingVersion;

        private bool _freeResize;
        private int _hoverCorner = -1;
        private int _dragCorner = -1;
        private int _dragEndedFrame = -1;
        private Plane _dragPlane;
        private Vector3 _dragStartLocal;
        private Vector3 _dragStartScale;

        // Dragging the selected shape across at its height, holding it where it was grabbed.
        private bool _moving;
        private Plane _movePlane;
        private Vector3 _moveOffset;

        /// <summary>The town with the overlay's tiles, while it's previewed.</summary>
        private TownDto _overlayTown;
        private (int Edits, int Moves) _overlayKey = (-1, -1);
        private float _overlayAt;

        private Material _glassMaterial;
        private Material _boxMaterial;
        private MaterialPropertyBlock _tint;
        private Transform _shapesRoot;
        private readonly Dictionary<string, MeshFilter> _shapeObjects = new Dictionary<string, MeshFilter>();
        private readonly HashSet<string> _seenShapes = new HashSet<string>();
        private MeshFilter _shapeGhost;
        private MeshFilter _selectionBox;
        private FlyCamera _fly;

        public TerrainMode CurrentTerrainMode => _terrainMode;

        /// <summary>The terrain tool in Builder mode, with nothing else (walking, a cottage) in the way.</summary>
        public bool InShapeBuilder => _town != null && !Paused && _tool == Tool.Terrain && _terrainMode == TerrainMode.Builder;

        /// <summary>Whether the layers panel should be up.</summary>
        public bool ShowingLayers => enabled && InShapeBuilder;

        /// <summary>The kind of shape following the pointer to be placed, or null.</summary>
        public string PlacingKind => _placingKind;

        public string SelectedShapeId => _selectedId;

        /// <summary>The selected shape as it is right now, moves and all, or null.</summary>
        public TerrainShapeDto SelectedShape => _working;

        public IReadOnlyList<TerrainShapeDto> Shapes => _town?.TerrainShapes ?? (IReadOnlyList<TerrainShapeDto>)System.Array.Empty<TerrainShapeDto>();

        /// <summary>Whether the selected shape's overlay is on. Leaving Builder mode makes overlaid ground the real ground.</summary>
        public bool OverlayOn => _working != null && _working.Overlay;

        /// <summary>Corner handles stretch the shape each way separately, rather than scaling it evenly.</summary>
        public bool FreeResize
        {
            get => _freeResize;
            set => _freeResize = value;
        }

        /// <summary>The town as drawn and walked: with the overlay's tiles while that's showing.</summary>
        public TownDto ShownTown => _overlayTown ?? _town;

        public void SetTerrainMode(TerrainMode mode)
        {
            _terrainMode = mode;
            SetTool(Tool.Terrain);
        }

        /// <summary>Starts placing a shape, or stops if that kind was already being placed.</summary>
        public void StartPlacing(string kind)
        {
            SetTerrainMode(TerrainMode.Builder);
            Deselect();
            if (_placingKind == kind)
            {
                _placingKind = null;
                return;
            }

            var scale = TerrainShapes.DefaultScale(kind);
            _placingKind = kind;
            _ghostShape = new TerrainShapeDto { Kind = kind, ScaleX = scale.X, ScaleY = scale.Y, ScaleZ = scale.Z };
            _ghostLift = scale.Y * 0.5f;
            _ghostOverGround = false;
        }

        /// <summary>Turns the selected shape's overlay on or off.</summary>
        public void SetOverlay(bool on)
        {
            if (_working == null || _working.Overlay == on) return;
            _working.Overlay = on;
            TouchWorking();
            FlushWorking();
            _overlayAt = 0f;
        }

        /// <summary>Selects a shape; with <paramref name="focus"/>, the camera turns to it too.</summary>
        public void SelectShape(string id, bool focus)
        {
            FlushWorking();
            var shape = _town?.TerrainShapes.Find(s => s.Id == id);
            if (shape == null) return;

            SetTerrainMode(TerrainMode.Builder);
            _placingKind = null;
            _selectedId = id;
            _working = shape.Clone();
            _workingDirty = false;
            _hoverCorner = _dragCorner = -1;
            _moving = false;
            if (focus && _fly != null) _fly.FocusOn(ShapeCenter(shape), Mathf.Clamp(TerrainShapes.BoundingRadius(shape) * 3.5f, 25f, 300f));
        }

        public void DeleteShape(string id)
        {
            if (id == _selectedId)
            {
                _working = null;
                _workingDirty = false;
                _selectedId = null;
                _hoverCorner = _dragCorner = -1;
            }

            ApplyEdit(new RemoveTerrainShapeCommand { ShapeId = id });
        }

        /// <summary>A copy beside the original, which is then selected.</summary>
        public void DuplicateShape(string id)
        {
            FlushWorking();
            var source = _town?.TerrainShapes.Find(s => s.Id == id);
            if (source == null) return;

            var copy = source.Clone();
            copy.Id = Ids.New();
            copy.Name = TerrainShapes.NextName(_town, source.Kind);
            var reach = _town.Size * 0.5f;
            var step = source.ScaleX * 0.6f + 2f;
            copy.X = source.X + step <= reach ? source.X + step : source.X - step;
            if (ApplyEdit(new AddTerrainShapeCommand { Shape = copy }).Ok) SelectShape(copy.Id, false);
        }

        private void BeginShapes(Material baseMaterial)
        {
            if (_town.TerrainShapes == null) _town.TerrainShapes = new List<TerrainShapeDto>();
            _glassMaterial = ShapeMeshes.GlassMaterial();
            _tint = new MaterialPropertyBlock();
            _boxMaterial = new Material(baseMaterial) { name = "Terrain shape box" };

            _shapesRoot = new GameObject("Terrain shapes").transform;
            _shapesRoot.SetParent(transform, false);
            _shapesRoot.gameObject.SetActive(false);
            _shapeGhost = CreateGhost("Shape ghost");
            _shapeGhost.GetComponent<MeshRenderer>().sharedMaterial = _glassMaterial;
            _selectionBox = CreateGhost("Shape box");
            _selectionBox.sharedMesh = new Mesh { name = "shape box" };
            _selectionBox.GetComponent<MeshRenderer>().sharedMaterial = _boxMaterial;
            _fly = _camera.GetComponent<FlyCamera>();

            _terrainMode = TerrainMode.Simple;
            _shapesActive = false;
            _placingKind = null;
            _selectedId = null;
            _working = null;
            _workingDirty = false;
            _overlayTown = null;
            _hoverCorner = _dragCorner = -1;
            _moving = false;
        }

        private void EndShapes()
        {
            ReleaseInput();
            _overlayTown = null;
            _placingKind = null;
            _selectedId = null;
            _working = null;
            _shapeObjects.Clear();
            if (_shapesRoot != null) Destroy(_shapesRoot.gameObject);
            if (_shapeGhost != null) Destroy(_shapeGhost.gameObject);
            if (_selectionBox != null)
            {
                Destroy(_selectionBox.sharedMesh);
                Destroy(_selectionBox.gameObject);
            }

            if (_glassMaterial != null) Destroy(_glassMaterial);
            if (_boxMaterial != null) Destroy(_boxMaterial);
        }

        /// <summary>
        /// Called whenever the builder isn't in Builder mode. The first time after it was, it saves the
        /// shape being moved, makes a showing overlay the ground, and puts the shapes away.
        /// </summary>
        private void LeaveShapeBuilder()
        {
            if (!_shapesActive) return;
            _shapesActive = false;

            FlushWorking();
            _placingKind = null;
            _selectedId = null;
            _working = null;
            _hoverCorner = _dragCorner = -1;
            _moving = false;
            ReleaseInput();

            // Overlaid ground becomes the ground. If there's none (or it can't be done), back to the ground as it was.
            var showing = _overlayTown != null;
            DropOverlayPreview();
            var applied = _town.TerrainShapes.Exists(s => s.Overlay) && ApplyEdit(new ApplyTerrainOverlayCommand()).Ok;
            if (showing && !applied) ShowGround();

            _shapesRoot.gameObject.SetActive(false);
            _shapeGhost.gameObject.SetActive(false);
            _selectionBox.gameObject.SetActive(false);
        }

        /// <summary>Redraws the ground (and everything on it) and the grid as the town is shown.</summary>
        private void ShowGround()
        {
            _view.Sync(ShownTown);
            _grid.Show(ShownTown);
        }

        private void DropOverlayPreview()
        {
            _overlayTown = null;
            _overlayKey = (-1, -1);
        }

        private static void ReleaseInput()
        {
            FlyCamera.KeysCaptured = false;
            FlyCamera.PointerCaptured = false;
        }

        private void UpdateShapeBuilder()
        {
            if (!_shapesActive)
            {
                _shapesActive = true;
                _shapesRoot.gameObject.SetActive(true);
            }

            var pointerFree = !HudBar.PointerInBar();
            if (PInput.KeyDown(PKey.Escape))
            {
                if (_placingKind != null) _placingKind = null;
                else Deselect();
            }

            if (_placingKind != null) UpdatePlacingShape(pointerFree);
            else UpdateSelectedShape(pointerFree);

            if (_workingDirty && _dragCorner < 0 && !_moving && Time.unscaledTime - _workingChangedAt > CommitDelay) FlushWorking();

            RefreshOverlay();
            DrawShapes();
            DrawSelectionBox();
            FlyCamera.KeysCaptured = _placingKind != null || _working != null;
            FlyCamera.PointerCaptured = _dragCorner >= 0 || _moving;
        }

        /// <summary>The ghost follows the pointer over the ground at its lift, or wherever the keys take it. A click places it.</summary>
        private void UpdatePlacingShape(bool pointerFree)
        {
            var ghost = _ghostShape;
            if (pointerFree && (!_ghostOverGround || PInput.PointerDelta().sqrMagnitude > 0.25f) && TryGroundPoint(out var cursor))
            {
                ghost.X = cursor.X;
                ghost.Z = cursor.Z;
                _ghostOverGround = true;
            }

            SteerWithKeys(ghost);
            _ghostLift += ScrollSteps(pointerFree);
            ghost.Y = TownTerrain.HeightAt(ShownTown, new Vec2(ghost.X, ghost.Z)) + _ghostLift;

            var check = TerrainShapes.Check(_town, TerrainShapes.Normalize(WithId(ghost)));
            _hint = !_ghostOverGround ? "Point at the ground to start" : check.Ok ? null : check.Error;
            if (!_ghostOverGround || !check.Ok || !pointerFree || !PointerGesture.Clicked) return;

            var shape = ghost.Clone();
            shape.Id = Ids.New();
            shape.Name = TerrainShapes.NextName(_town, shape.Kind);
            if (!ApplyEdit(new AddTerrainShapeCommand { Shape = shape }).Ok) return;
            SelectShape(shape.Id, false);
        }

        private void UpdateSelectedShape(bool pointerFree)
        {
            var ray = _camera.ScreenPointToRay(PInput.PointerPosition());
            if (_moving)
            {
                if (_working != null && PInput.PrimaryHeld())
                {
                    if (_movePlane.Raycast(ray, out var along))
                    {
                        var spot = ray.GetPoint(along) + _moveOffset;
                        _working.X = spot.x;
                        _working.Z = spot.z;
                        TouchWorking();
                    }

                    _hint = "Drag to move it  ·  scroll afterwards to lift it";
                    return;
                }

                _moving = false;
                _dragEndedFrame = Time.frameCount;
            }

            if (_working != null)
            {
                if (PInput.KeyDown(PKey.Delete) || PInput.KeyDown(PKey.Backspace))
                {
                    DeleteShape(_selectedId);
                    return;
                }

                var corners = Corners(_working);
                if (_dragCorner >= 0)
                {
                    if (PInput.PrimaryHeld()) DragCorner(ray);
                    else
                    {
                        _dragCorner = -1;
                        _dragEndedFrame = Time.frameCount;
                        TouchWorking();
                    }
                }
                else
                {
                    _hoverCorner = pointerFree ? CornerUnderPointer(corners) : -1;
                    if (_hoverCorner >= 0 && PInput.PrimaryPressedThisFrame()) StartDrag(corners);
                }

                if (_dragCorner < 0)
                {
                    var moved = SteerWithKeys(_working);
                    var lift = ScrollSteps(pointerFree);
                    if (lift != 0f)
                    {
                        _working.Y = Mathf.Clamp(_working.Y + lift, TerrainShapes.MinY, TerrainShapes.MaxY);
                        moved = true;
                    }

                    if (moved) TouchWorking();
                }

                _hint = _dragCorner >= 0 ? (_freeResize ? "Drag to stretch it any way" : "Drag to resize it evenly")
                    : _hoverCorner >= 0 ? "Drag this corner to resize" : null;
            }

            // Pressing on a shape selects it and starts moving it, across at the height where it was grabbed.
            if (pointerFree && _dragCorner < 0 && _hoverCorner < 0 && PInput.PrimaryPressedThisFrame())
            {
                var grabbed = PickShape(ray, out var distance);
                if (grabbed != null)
                {
                    if (grabbed != _selectedId) SelectShape(grabbed, false);
                    var hit = ray.GetPoint(distance);
                    _movePlane = new Plane(Vector3.up, hit);
                    _moveOffset = ShapeCenter(_working) - hit;
                    _moveOffset.y = 0f;
                    _moving = true;
                    FlyCamera.PointerCaptured = true;
                    return;
                }
            }

            // Click a shape to select it, or anywhere else to let go.
            if (!pointerFree || !PointerGesture.Clicked || _dragCorner >= 0 || _hoverCorner >= 0 || _dragEndedFrame == Time.frameCount) return;
            var picked = PickShape(ray, out _);
            if (picked != null) SelectShape(picked, false);
            else Deselect();
        }

        private void Deselect()
        {
            FlushWorking();
            _selectedId = null;
            _working = null;
            _hoverCorner = _dragCorner = -1;
        }

        /// <summary>Arrow keys slide a shape across the view, Q/E turn it and R/F tilt it. Returns whether it moved.</summary>
        private bool SteerWithKeys(TerrainShapeDto shape)
        {
            var changed = false;
            var move = PInput.Move();
            if (move.sqrMagnitude > 0.0001f)
            {
                var forward = _camera.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) forward = _camera.transform.up;
                forward.y = 0f;
                forward.Normalize();
                var right = new Vector3(forward.z, 0f, -forward.x);
                var step = (right * move.x + forward * move.y) * (ShapeMoveSpeed * (PInput.Fast() ? 3f : 1f) * Time.unscaledDeltaTime);
                shape.X += step.x;
                shape.Z += step.z;
                changed = true;
            }

            if (PInput.KeyDown(PKey.Q)) changed |= Turn(shape, 0f, -ShapeTurnStep);
            if (PInput.KeyDown(PKey.E)) changed |= Turn(shape, 0f, ShapeTurnStep);
            if (PInput.KeyDown(PKey.R)) changed |= Turn(shape, ShapeTurnStep, 0f);
            if (PInput.KeyDown(PKey.F)) changed |= Turn(shape, -ShapeTurnStep, 0f);
            return changed;
        }

        private static bool Turn(TerrainShapeDto shape, float tilt, float turn)
        {
            shape.RotationX = TownMath.NormalizeAngle(shape.RotationX + tilt);
            shape.RotationY = TownMath.NormalizeAngle(shape.RotationY + turn);
            return true;
        }

        /// <summary>Metres the scroll wheel lifts (or lowers) a shape this frame: one a notch, four with Shift.</summary>
        private static float ScrollSteps(bool pointerFree)
        {
            if (!pointerFree) return 0f;
            var notches = Mathf.Clamp(PInput.Scroll(), -3f, 3f);
            return Mathf.Abs(notches) < 0.01f ? 0f : notches * (PInput.Fast() ? 4f : 1f);
        }

        private void TouchWorking()
        {
            _workingDirty = true;
            _workingChangedAt = Time.unscaledTime;
            _workingVersion++;
        }

        /// <summary>Saves the selected shape's moves as an edit, if it has any.</summary>
        private void FlushWorking()
        {
            if (_working == null || !_workingDirty) return;
            _workingDirty = false;
            ApplyEdit(new UpdateTerrainShapeCommand { Shape = _working.Clone() });

            // Back to what was stored: rounded if it worked, as it was if it didn't.
            var stored = _town.TerrainShapes.Find(s => s.Id == _working.Id);
            _working = stored?.Clone();
            if (stored == null) _selectedId = null;
        }

        private int CornerUnderPointer(Vector3[] corners)
        {
            var pointer = PInput.PointerPosition();
            var best = -1;
            var bestDistance = HandleReach * HandleReach;
            for (var i = 0; i < corners.Length; i++)
            {
                var screen = _camera.WorldToScreenPoint(corners[i]);
                if (screen.z <= 0f) continue;
                var distance = (new Vector2(screen.x, screen.y) - pointer).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = i;
            }

            return best;
        }

        /// <summary>The grabbed corner moves on a plane facing the camera, through where it was.</summary>
        private void StartDrag(Vector3[] corners)
        {
            _dragCorner = _hoverCorner;
            var corner = corners[_dragCorner];
            _dragPlane = new Plane(-_camera.transform.forward, corner);
            _dragStartLocal = Quaternion.Inverse(ShapeRotation(_working)) * (corner - ShapeCenter(_working));
            _dragStartScale = ShapeScale(_working);
            FlyCamera.PointerCaptured = true;
        }

        /// <summary>
        /// The middle stays put. Evenly, the shape scales by how far the corner has come along the line
        /// from the middle; free-form, each side becomes twice the corner's distance from the middle that way.
        /// </summary>
        private void DragCorner(Ray ray)
        {
            if (!_dragPlane.Raycast(ray, out var distance)) return;
            var local = Quaternion.Inverse(ShapeRotation(_working)) * (ray.GetPoint(distance) - ShapeCenter(_working));

            Vector3 scale;
            if (_freeResize)
            {
                scale = new Vector3(Fit(2f * Mathf.Abs(local.x)), Fit(2f * Mathf.Abs(local.y)), Fit(2f * Mathf.Abs(local.z)));
            }
            else
            {
                var factor = Vector3.Dot(local, _dragStartLocal) / Mathf.Max(1e-4f, _dragStartLocal.sqrMagnitude);
                var smallest = Mathf.Min(_dragStartScale.x, Mathf.Min(_dragStartScale.y, _dragStartScale.z));
                var largest = Mathf.Max(_dragStartScale.x, Mathf.Max(_dragStartScale.y, _dragStartScale.z));
                factor = Mathf.Clamp(factor, TerrainShapes.MinScale / smallest, TerrainShapes.MaxScale / largest);
                scale = _dragStartScale * factor;
            }

            _working.ScaleX = scale.x;
            _working.ScaleY = scale.y;
            _working.ScaleZ = scale.z;
            TouchWorking();
        }

        private static float Fit(float size) => Mathf.Clamp(size, TerrainShapes.MinScale, TerrainShapes.MaxScale);

        /// <summary>The nearest shape under the pointer, buried or not.</summary>
        private string PickShape(Ray ray, out float bestDistance)
        {
            var origin = new Vec3(ray.origin.x, ray.origin.y, ray.origin.z);
            var direction = new Vec3(ray.direction.x, ray.direction.y, ray.direction.z);
            string best = null;
            bestDistance = float.MaxValue;
            foreach (var shape in VisibleShapes())
            {
                if (!TerrainShapes.Raycast(shape, origin, direction, out var distance) || distance >= bestDistance) continue;
                bestDistance = distance;
                best = shape.Id;
            }

            return best;
        }

        /// <summary>The town's shapes, with the selected one as it is mid-move.</summary>
        private List<TerrainShapeDto> VisibleShapes()
        {
            var shapes = new List<TerrainShapeDto>(_town.TerrainShapes.Count);
            foreach (var shape in _town.TerrainShapes) shapes.Add(_working != null && shape.Id == _working.Id ? _working : shape);
            return shapes;
        }

        /// <summary>Recomputes the overlay when the shapes have changed, at most every <see cref="OverlayRefresh"/> seconds.</summary>
        private void RefreshOverlay()
        {
            if (!VisibleShapes().Exists(s => s.Overlay))
            {
                if (_overlayTown == null) return;
                DropOverlayPreview();
                ShowGround();
                return;
            }

            var key = (_editCount, _workingVersion);
            if (key == _overlayKey || Time.unscaledTime < _overlayAt) return;
            _overlayKey = key;
            _overlayAt = Time.unscaledTime + OverlayRefresh;

            var draft = _town.WithTiles(_town.Tiles);
            draft.TerrainShapes = VisibleShapes();
            var preview = _town.WithTiles(_town.Tiles.Clone());
            TownTiles.Apply(preview, TerrainShapes.Overlay(draft));
            _overlayTown = preview;
            ShowGround();
        }

        private void DrawShapes()
        {
            _seenShapes.Clear();
            foreach (var shape in VisibleShapes())
            {
                _seenShapes.Add(shape.Id);
                if (!_shapeObjects.TryGetValue(shape.Id, out var filter) || filter == null)
                {
                    var go = new GameObject("Shape " + shape.Id);
                    go.transform.SetParent(_shapesRoot, false);
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.sharedMaterial = _glassMaterial;
                    filter = go.AddComponent<MeshFilter>();
                    _shapeObjects[shape.Id] = filter;
                }

                // See-through in its own colour; the selected one a little more solid.
                filter.sharedMesh = ShapeMeshes.Unit(shape.Kind);
                var selected = shape.Id == _selectedId;
                ShapeMeshes.Tint(filter.GetComponent<MeshRenderer>(), _tint, ShapeMeshes.ColorFor(shape.Id, selected ? ShapeMeshes.SelectedAlpha : ShapeMeshes.Alpha));
                PoseObject(filter.transform, shape);
            }

            var gone = new List<string>();
            foreach (var pair in _shapeObjects)
            {
                if (!_seenShapes.Contains(pair.Key)) gone.Add(pair.Key);
            }

            foreach (var id in gone)
            {
                if (_shapeObjects[id] != null) Destroy(_shapeObjects[id].gameObject);
                _shapeObjects.Remove(id);
            }

            var showGhost = _placingKind != null && _ghostOverGround;
            _shapeGhost.gameObject.SetActive(showGhost);
            if (!showGhost) return;
            _shapeGhost.sharedMesh = ShapeMeshes.Unit(_ghostShape.Kind);
            ShapeMeshes.Tint(_shapeGhost.GetComponent<MeshRenderer>(), _tint, _hint == null ? GhostValid : GhostInvalid);
            PoseObject(_shapeGhost.transform, _ghostShape);
        }

        private void DrawSelectionBox()
        {
            if (_working == null)
            {
                _selectionBox.gameObject.SetActive(false);
                return;
            }

            // Handles stay about the same size on screen however far away the shape is.
            var center = ShapeCenter(_working);
            var distance = Vector3.Distance(_camera.transform.position, center);
            var mb = new MeshBuilder();
            ShapeMeshes.SelectionBox(mb, Corners(_working), distance * 0.003f, distance * 0.018f, _dragCorner >= 0 ? _dragCorner : _hoverCorner);
            mb.ToMesh(_selectionBox.sharedMesh);
            _selectionBox.gameObject.SetActive(true);
        }

        private static void PoseObject(Transform target, TerrainShapeDto shape)
        {
            target.SetPositionAndRotation(ShapeCenter(shape), ShapeRotation(shape));
            target.localScale = ShapeScale(shape);
        }

        private static Vector3[] Corners(TerrainShapeDto shape)
        {
            var center = ShapeCenter(shape);
            var rotation = ShapeRotation(shape);
            var scale = ShapeScale(shape);
            var corners = new Vector3[ShapeMeshes.BoxCorners.Length];
            for (var i = 0; i < corners.Length; i++) corners[i] = center + rotation * Vector3.Scale(ShapeMeshes.BoxCorners[i], scale);
            return corners;
        }

        private static TerrainShapeDto WithId(TerrainShapeDto shape)
        {
            var copy = shape.Clone();
            if (string.IsNullOrEmpty(copy.Id)) copy.Id = "new";
            return copy;
        }

        private static Vector3 ShapeCenter(TerrainShapeDto shape) => new Vector3(shape.X, shape.Y, shape.Z);
        private static Quaternion ShapeRotation(TerrainShapeDto shape) => Quaternion.Euler(shape.RotationX, shape.RotationY, shape.RotationZ);
        private static Vector3 ShapeScale(TerrainShapeDto shape) => new Vector3(shape.ScaleX, shape.ScaleY, shape.ScaleZ);
    }
}
