using System.Collections.Generic;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Turns a <see cref="TownDto"/> into GameObjects. Shared by the builder and the viewer. The builder
    /// calls <see cref="Sync"/> after every edit, and only buildings that changed are touched.
    /// </summary>
    public sealed class TownView : MonoBehaviour
    {
        private readonly Dictionary<string, GameObject> _buildings = new Dictionary<string, GameObject>();
        private Material _material;
        private Transform _buildingsRoot;
        private MeshFilter _ground;
        private MeshFilter _roads;

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
            _buildingsRoot = new GameObject("Buildings").transform;
            _buildingsRoot.SetParent(transform, false);
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
            var seen = new HashSet<string>();
            foreach (var building in town.Buildings)
            {
                seen.Add(building.Id);
                if (!_buildings.TryGetValue(building.Id, out var go))
                {
                    go = CreateBuilding(building.TypeId);
                    _buildings.Add(building.Id, go);
                }

                go.transform.SetPositionAndRotation(
                    new Vector3(building.X, TownTerrain.BaseHeight(town, building), building.Z),
                    Quaternion.Euler(0f, building.RotationDeg, 0f));
            }

            var removed = new List<string>();
            foreach (var pair in _buildings)
            {
                if (!seen.Contains(pair.Key)) removed.Add(pair.Key);
            }

            foreach (var id in removed)
            {
                Destroy(_buildings[id]);
                _buildings.Remove(id);
            }

            // Rebuilding every road is cheap at this scale. Chunk it when towns get big.
            LowPolyMeshes.Roads(town, _roads.sharedMesh);
        }

        /// <summary>
        /// Viewer only: merges the static buildings into large batches to cut draw calls. Once this
        /// runs, buildings can't be moved, so don't call it in the builder.
        /// </summary>
        public void BakeStatic()
        {
            StaticBatchingUtility.Combine(_buildingsRoot.gameObject);
        }

        public void Clear()
        {
            foreach (var go in _buildings.Values) Destroy(go);
            _buildings.Clear();

            // Static batching can't be undone, so start the next town with a fresh root.
            if (_buildingsRoot != null) Destroy(_buildingsRoot.gameObject);
            _buildingsRoot = new GameObject("Buildings").transform;
            _buildingsRoot.SetParent(transform, false);

            gameObject.SetActive(false);
        }

        private GameObject CreateBuilding(string typeId)
        {
            var go = new GameObject(typeId);
            go.transform.SetParent(_buildingsRoot, false);
            go.AddComponent<MeshFilter>().sharedMesh = MeshCache.Building(typeId);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
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
