using System.Collections.Generic;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Draws the town's foliage (<see cref="Foliage"/>): every plant stamped from its look in
    /// <see cref="FoliageMeshes"/>, merged into one mesh per <see cref="ChunkSize"/> square so thousands of
    /// plants cost a few dozen draw calls. Rebuilt only when the town's ground, buildings, roads or
    /// foliage amounts change.
    /// </summary>
    public sealed class FoliageView : MonoBehaviour
    {
        /// <summary>Metres a side of each merged mesh.</summary>
        public const float ChunkSize = 64f;

        /// <summary>How far plants sit into the ground, so they don't float on slopes.</summary>
        private const float Sink = 0.06f;

        private Material _material;
        private readonly List<GameObject> _chunks = new List<GameObject>();
        private string _key;

        /// <summary>How many plants are showing (for the debug overlay).</summary>
        public int PlantCount { get; private set; }

        public bool Showing => gameObject.activeSelf;

        public void Init(Material material)
        {
            _material = material;
            gameObject.SetActive(false);
        }

        /// <summary>Shows the town's foliage, rebuilding it first if the town changed since last time.</summary>
        public void Show(TownDto town)
        {
            var key = Key(town);
            if (key != _key) Rebuild(town, key);
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>Forgets the town, so the next one is built fresh.</summary>
        public void Clear()
        {
            foreach (var chunk in _chunks)
            {
                Destroy(chunk.GetComponent<MeshFilter>().sharedMesh);
                Destroy(chunk);
            }

            _chunks.Clear();
            _key = null;
            PlantCount = 0;
            Hide();
        }

        private void Rebuild(TownDto town, string key)
        {
            Clear();
            _key = key;

            var plants = Foliage.Plan(town);
            PlantCount = plants.Count;
            var half = town.Size * 0.5f;
            var across = Mathf.Max(1, Mathf.CeilToInt(town.Size / ChunkSize));
            var groups = new Dictionary<int, List<CombineInstance>>();
            foreach (var plant in plants)
            {
                var cx = Mathf.Clamp((int)((plant.Position.X + half) / ChunkSize), 0, across - 1);
                var cz = Mathf.Clamp((int)((plant.Position.Z + half) / ChunkSize), 0, across - 1);
                var chunk = cz * across + cx;
                if (!groups.TryGetValue(chunk, out var list)) groups[chunk] = list = new List<CombineInstance>();

                var ground = TownTerrain.HeightAt(town, plant.Position) - Sink;
                list.Add(new CombineInstance
                {
                    mesh = FoliageMeshes.Get(plant.Kind, plant.Variant),
                    transform = Matrix4x4.TRS(new Vector3(plant.Position.X, ground, plant.Position.Z), Quaternion.Euler(0f, plant.RotationDeg, 0f), Vector3.one * plant.Scale),
                });
            }

            foreach (var pair in groups)
            {
                var mesh = new Mesh { name = "foliage " + pair.Key, indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(pair.Value.ToArray(), true, true);
                mesh.RecalculateBounds();

                var go = new GameObject("Foliage " + pair.Key);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _chunks.Add(go);
            }
        }

        /// <summary>Changes whenever something that decides the plants changes.</summary>
        private static string Key(TownDto town)
        {
            var layout = 0f;
            foreach (var building in town.Buildings) layout += building.X * 3f + building.Z * 7f + building.RotationDeg;

            // The tiles decide a lot (gardens, paving, walls), and they change in place.
            var tiles = 17;
            if (town.Tiles != null)
            {
                for (var k = 0; k < town.Tiles.Levels.Count; k++) tiles = unchecked(tiles * 31 + town.Tiles.Levels[k] * 7 + town.Tiles.Surfaces[k] + (TownTiles.HillMark(town, k) ? 5 : 0));
            }

            var areas = town.FoliageAreas?.Count ?? 0;
            foreach (var area in town.FoliageAreas ?? new List<FoliageAreaDto>()) layout += area.X0 * 13f + area.Z0 * 17f + area.X1 * 19f + area.Z1 * 23f + (area.Erase ? 1f : 0f);
            var f = town.Foliage ?? new FoliageDto();
            return $"{town.Id}|{tiles}|{town.Buildings.Count}|{areas}|{layout:0.00}|{f.Overall}|{f.Trees}|{f.Flowers}|{f.Grass}";
        }
    }
}
