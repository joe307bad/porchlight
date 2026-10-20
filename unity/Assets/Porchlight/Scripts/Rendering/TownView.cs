using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Turns a <see cref="TownDto"/> into GameObjects. Shared by the builder and the viewer. The builder
    /// calls <see cref="Sync"/> after every edit, and only buildings that changed are touched.
    ///
    /// Cottages are drawn from their insides, with doors that swing open as the <see cref="Walker"/>
    /// comes up to them. One cottage at a time can be cut away (<see cref="SetCutaway"/>) to see into
    /// it: the floors above the one in view, the roof, and the outside walls facing the camera come off.
    /// </summary>
    public sealed class TownView : MonoBehaviour
    {
        private const float DoorReach = 1.3f;
        private const float DoorSpeed = 320f;

        private sealed class Door
        {
            public Transform Leaf;
            public int Floor;
            public bool Front;
            public bool AlongX;
            public Vector3 LocalCenter;
            public float ClosedYaw;
            public float Angle;
            public float Swing = 1f;
        }

        private sealed class Building
        {
            public GameObject Go;
            public MeshRenderer Renderer;
            public string Key;
            public Transform DoorRoot;
            public readonly List<Door> Doors = new List<Door>();
            public GameObject Plate;
            public int Number;
        }

        private readonly Dictionary<string, Building> _buildings = new Dictionary<string, Building>();
        private readonly Dictionary<string, Mesh> _cottageMeshes = new Dictionary<string, Mesh>();
        private readonly Dictionary<string, Mesh> _cutawayMeshes = new Dictionary<string, Mesh>();
        private readonly Dictionary<string, Mesh> _plateMeshes = new Dictionary<string, Mesh>();
        private Material _material;
        private Transform _buildingsRoot;
        private Transform _doorsRoot;
        private MeshFilter _ground;
        private MeshFilter _roads;
        private MeshFilter _cutaway;
        private Mesh _frontDoorLeaf;
        private Mesh _insideDoorLeaf;
        private TownDto _town;

        private string _cutawayId;
        private int _cutawayFloor;
        private int _cutawayLowered;
        private string _cutawayKey;

        /// <summary>Whoever is walking around, so doors open for them. Null while building.</summary>
        public Transform Walker { get; set; }

        public static Material CreateMaterial()
        {
            var shader = Resources.Load<Shader>("PorchlightFlat");
            if (shader == null)
            {
                Debug.LogError("PorchlightFlat shader missing from Resources; falling back to an unlit shader.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
            }

            return new Material(shader) { name = "Porchlight Town", enableInstancing = true };
        }

        public void Init(Material material)
        {
            _material = material;
            _ground = CreateChild("Ground");
            _roads = CreateChild("Roads");
            _roads.sharedMesh = new Mesh { name = "roads" };
            _cutaway = CreateChild("Cut away");
            _cutaway.gameObject.SetActive(false);
            _frontDoorLeaf = InteriorMeshes.DoorLeaf(CottageInterior.FrontDoorWidth, InteriorMeshes.FrontDoorColor);
            Furnishings.TryGetOpening(Furnishings.Door, out var door);
            _insideDoorLeaf = InteriorMeshes.DoorLeaf(door.Width, InteriorMeshes.InsideDoorColor);
            NewRoots();
        }

        public void Show(TownDto town)
        {
            Clear();
            gameObject.SetActive(true);
            SyncTerrain(town);
        }

        /// <summary>Rebuilds the ground after the terrain changes, and moves everything on it.</summary>
        public void SyncTerrain(TownDto town)
        {
            if (_ground.sharedMesh != null) Destroy(_ground.sharedMesh);
            _ground.sharedMesh = LowPolyMeshes.Ground(town);
            Sync(town);
        }

        /// <summary>Adds, moves and removes buildings to match the town, and redraws the roads. Buildings stand on the ground.</summary>
        public void Sync(TownDto town)
        {
            _town = town;
            var seen = new HashSet<string>();
            foreach (var dto in town.Buildings)
            {
                seen.Add(dto.Id);
                if (!_buildings.TryGetValue(dto.Id, out var building))
                {
                    building = CreateBuilding(dto.Id);
                    _buildings.Add(dto.Id, building);
                }

                // Cottages look like their insides, so a changed inside means a new mesh.
                var interior = CottageInterior.HasInterior(dto) ? CottageInterior.Of(dto) : null;
                var key = interior == null ? dto.TypeId : dto.TypeId + "#" + InteriorMeshes.Key(interior);
                if (key != building.Key)
                {
                    building.Key = key;
                    building.Go.GetComponent<MeshFilter>().sharedMesh = interior == null ? MeshCache.Building(dto.TypeId) : CottageMesh(dto, interior, key);
                    BuildDoors(building, dto, interior);
                    if (dto.Id == _cutawayId) _cutawayKey = null;
                }

                if (dto.HouseNumber != building.Number) BuildPlate(building, dto);

                var pose = (new Vector3(dto.X, TownTerrain.BaseHeight(town, dto), dto.Z), Quaternion.Euler(0f, dto.RotationDeg, 0f));
                building.Go.transform.SetPositionAndRotation(pose.Item1, pose.Item2);
                building.DoorRoot.SetPositionAndRotation(pose.Item1, pose.Item2);
            }

            var removed = new List<string>();
            foreach (var pair in _buildings)
            {
                if (!seen.Contains(pair.Key)) removed.Add(pair.Key);
            }

            foreach (var id in removed)
            {
                if (id == _cutawayId) SetCutaway(null, 0, 0);
                Destroy(_buildings[id].Go);
                Destroy(_buildings[id].DoorRoot.gameObject);
                _buildings.Remove(id);
            }

            // Rebuilding every road is cheap at this scale. Chunk it when towns get big.
            LowPolyMeshes.Roads(town, _roads.sharedMesh);
            RefreshCutaway();
        }

        /// <summary>
        /// Cuts one cottage away to see inside: floors above <paramref name="floor"/>, the roof, and the
        /// outside walls in <paramref name="lowered"/> (<see cref="InteriorMeshes.Front"/> etc.) come off.
        /// Null puts it back. Cheap to call every frame.
        /// </summary>
        public void SetCutaway(string buildingId, int floor, int lowered)
        {
            if (buildingId == _cutawayId && floor == _cutawayFloor && lowered == _cutawayLowered) return;

            if (buildingId != _cutawayId)
            {
                if (_cutawayId != null && _buildings.TryGetValue(_cutawayId, out var previous))
                {
                    previous.Renderer.enabled = true;
                    ShowDoors(previous, CottageInterior.FloorCount - 1, 0);
                }

                foreach (var mesh in _cutawayMeshes.Values) Destroy(mesh);
                _cutawayMeshes.Clear();
            }

            _cutawayId = buildingId;
            _cutawayFloor = floor;
            _cutawayLowered = lowered;
            _cutawayKey = null;
            RefreshCutaway();
        }

        private void RefreshCutaway()
        {
            if (_cutawayId == null || _town == null || !_buildings.TryGetValue(_cutawayId, out var building))
            {
                _cutaway.gameObject.SetActive(false);
                return;
            }

            var dto = _town.Buildings.Find(b => b.Id == _cutawayId);
            if (dto == null || !Catalog.TryGetBuilding(dto.TypeId, out var type)) return;

            var key = $"{building.Key}@{_cutawayFloor}/{_cutawayLowered}";
            if (key != _cutawayKey)
            {
                if (!_cutawayMeshes.TryGetValue(key, out var mesh))
                {
                    mesh = InteriorMeshes.Cottage(type, CottageInterior.Of(dto), _cutawayFloor, false, _cutawayLowered);
                    _cutawayMeshes[key] = mesh;
                }

                _cutaway.sharedMesh = mesh;
                _cutawayKey = key;
            }

            _cutaway.transform.SetPositionAndRotation(building.Go.transform.position, building.Go.transform.rotation);
            _cutaway.gameObject.SetActive(true);
            building.Renderer.enabled = false;
            ShowDoors(building, _cutawayFloor, _cutawayLowered);
        }

        private static void ShowDoors(Building building, int topFloor, int lowered)
        {
            var frontDown = topFloor == 0 && (lowered & InteriorMeshes.Front) != 0;
            foreach (var door in building.Doors)
            {
                var hidden = door.Floor > topFloor || (door.Front && frontDown);
                door.Leaf.gameObject.SetActive(!hidden);
            }

            // The house number goes with the front wall.
            if (building.Plate != null) building.Plate.SetActive(!frontDown);
        }

        private void Update()
        {
            if (_town == null) return;
            var dt = Time.deltaTime;
            foreach (var building in _buildings.Values)
            {
                if (building.Doors.Count == 0) continue;
                var root = building.DoorRoot;

                // Only doors near the walker need any thought.
                var near = Walker != null && (Walker.position - root.position).sqrMagnitude < 100f;
                var walker = near ? root.InverseTransformPoint(Walker.position) : Vector3.zero;
                foreach (var door in building.Doors)
                {
                    var open = near && Mathf.Abs(walker.y - door.LocalCenter.y) < 1.6f
                        && new Vector2(walker.x - door.LocalCenter.x, walker.z - door.LocalCenter.z).sqrMagnitude < DoorReach * DoorReach;

                    // Swing away from whoever's coming through.
                    if (open && door.Angle < 1f)
                    {
                        var side = door.AlongX ? walker.z - door.LocalCenter.z : walker.x - door.LocalCenter.x;
                        door.Swing = (side >= 0f ? 1f : -1f) * (door.AlongX ? 1f : -1f);
                    }

                    var target = open ? 90f : 0f;
                    if (Mathf.Approximately(door.Angle, target)) continue;
                    door.Angle = Mathf.MoveTowards(door.Angle, target, DoorSpeed * dt);
                    door.Leaf.localRotation = Quaternion.Euler(0f, door.ClosedYaw + door.Angle * door.Swing, 0f);
                }
            }
        }

        /// <summary>
        /// Viewer only: merges the static buildings into large batches to cut draw calls. Once this
        /// runs, buildings can't be moved, so don't call it in the builder. Doors and the cut-away
        /// cottage live elsewhere, so they still move.
        /// </summary>
        public void BakeStatic()
        {
            StaticBatchingUtility.Combine(_buildingsRoot.gameObject);
        }

        public void Clear()
        {
            SetCutaway(null, 0, 0);
            _buildings.Clear();
            foreach (var mesh in _cottageMeshes.Values) Destroy(mesh);
            _cottageMeshes.Clear();
            foreach (var mesh in _plateMeshes.Values) Destroy(mesh);
            _plateMeshes.Clear();
            _town = null;

            // Static batching can't be undone, so start the next town with fresh roots.
            NewRoots();
            gameObject.SetActive(false);
        }

        private void NewRoots()
        {
            if (_buildingsRoot != null) Destroy(_buildingsRoot.gameObject);
            if (_doorsRoot != null) Destroy(_doorsRoot.gameObject);
            _buildingsRoot = new GameObject("Buildings").transform;
            _buildingsRoot.SetParent(transform, false);
            _doorsRoot = new GameObject("Doors").transform;
            _doorsRoot.SetParent(transform, false);
        }

        /// <summary>Cottages with the same inside share a mesh.</summary>
        private Mesh CottageMesh(BuildingDto dto, InteriorDto interior, string key)
        {
            if (_cottageMeshes.TryGetValue(key, out var mesh) && mesh != null) return mesh;
            Catalog.TryGetBuilding(dto.TypeId, out var type);
            mesh = InteriorMeshes.Cottage(type, interior);
            _cottageMeshes[key] = mesh;
            return mesh;
        }

        /// <summary>A leaf for the front door and each inside door, hinged at one side of its doorway.</summary>
        private void BuildDoors(Building building, BuildingDto dto, InteriorDto interior)
        {
            foreach (var door in building.Doors) Destroy(door.Leaf.gameObject);
            building.Doors.Clear();
            if (interior == null || !Catalog.TryGetBuilding(dto.TypeId, out var type)) return;

            for (var floor = 0; floor < interior.Floors.Count; floor++)
            {
                foreach (var opening in CottageInterior.Openings(interior, floor))
                {
                    if (!Furnishings.TryGetOpening(opening.TypeId, out var kind) || !kind.IsDoor) continue;
                    if (!CottageInterior.TryFindWall(type, interior.Floors[floor], opening.WallId, out var wall)) continue;

                    var front = opening.Id == CottageInterior.FrontDoorId;
                    var width = CottageInterior.OpeningWidth(opening);
                    var y = CottageInterior.FloorHeight(floor);
                    var hinge = wall.Point(opening.At - width * 0.5f, wall.Fixed);
                    var center = wall.Point(opening.At, wall.Fixed);
                    var closed = wall.AlongX ? 0f : -90f;

                    var go = new GameObject(front ? "Front door" : "Door");
                    go.transform.SetParent(building.DoorRoot, false);
                    go.transform.localPosition = new Vector3(hinge.X, y, hinge.Z);
                    go.transform.localRotation = Quaternion.Euler(0f, closed, 0f);
                    go.AddComponent<MeshFilter>().sharedMesh = front ? _frontDoorLeaf : _insideDoorLeaf;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = _material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;

                    building.Doors.Add(new Door
                    {
                        Leaf = go.transform,
                        Floor = floor,
                        Front = front,
                        AlongX = wall.AlongX,
                        LocalCenter = new Vector3(center.X, y, center.Z),
                        ClosedYaw = closed,
                    });
                }
            }
        }

        /// <summary>The house number beside the front door. It rides with the doors, which aren't batched, so it can hide.</summary>
        private void BuildPlate(Building building, BuildingDto dto)
        {
            if (building.Plate != null) Destroy(building.Plate);
            building.Plate = null;
            building.Number = dto.HouseNumber;
            if (dto.HouseNumber <= 0 || !Catalog.TryGetBuilding(dto.TypeId, out var type)) return;

            var key = dto.TypeId + "#" + dto.HouseNumber;
            if (!_plateMeshes.TryGetValue(key, out var mesh) || mesh == null)
            {
                mesh = InteriorMeshes.NumberPlate(type, dto.HouseNumber);
                _plateMeshes[key] = mesh;
            }

            var go = new GameObject("House number");
            go.transform.SetParent(building.DoorRoot, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            building.Plate = go;
        }

        private Building CreateBuilding(string id)
        {
            var go = new GameObject(id);
            go.transform.SetParent(_buildingsRoot, false);
            go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var doors = new GameObject(id + " doors").transform;
            doors.SetParent(_doorsRoot, false);
            return new Building { Go = go, Renderer = renderer, DoorRoot = doors };
        }

        private MeshFilter CreateChild(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.AddComponent<MeshFilter>();
        }
    }
}
