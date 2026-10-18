using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The <see cref="TownGrid"/> drawn across the whole town floor as one transparent layer over the ground, plus the
    /// red cross the builder puts on the intersection under the pointer.
    /// </summary>
    public sealed class GridOverlay : MonoBehaviour
    {
        private const float Lift = 0.06f;

        private MeshFilter _floor;
        private Transform _cross;

        public void Init(Material flatMaterial)
        {
            var shader = Resources.Load<Shader>("PorchlightGrid");
            if (shader == null) Debug.LogError("PorchlightGrid shader missing from Resources.");
            var material = new Material(shader) { name = "Porchlight Grid" };
            material.SetFloat("_Spacing", TownGrid.Spacing);
            material.SetFloat("_MajorEvery", TownGrid.MajorEvery);

            _floor = Child("Grid", material);
            _cross = Child("Grid cross", flatMaterial).transform;
            _cross.GetComponent<MeshFilter>().sharedMesh = CrossMesh();
            _cross.gameObject.SetActive(false);
            gameObject.SetActive(false);
        }

        /// <summary>Lays the grid over the town's ground. Call again after the terrain changes.</summary>
        public void Show(TownDto town)
        {
            var half = town.Size * 0.5f;
            var mb = new MeshBuilder();
            if (TownTerrain.IsShaped(town)) TerrainMeshes.Surface(mb, town, Lift, (x, z) => Color.white);
            else mb.Quad(new Vector3(-half, Lift, -half), new Vector3(-half, Lift, half), new Vector3(half, Lift, half), new Vector3(half, Lift, -half), Color.white, Vector3.up);
            if (_floor.sharedMesh != null) Destroy(_floor.sharedMesh);
            _floor.sharedMesh = mb.ToMesh(name: "grid");
            HideCross();
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>Puts the red cross on the grid intersection nearest to <paramref name="point"/>, lying flat on the ground there.</summary>
        public void ShowCross(TownDto town, Vec2 point)
        {
            var snapped = TownGrid.Snap(point);
            _cross.SetPositionAndRotation(TerrainMeshes.OnGround(town, snapped), Quaternion.FromToRotation(Vector3.up, TerrainMeshes.Normal(town, snapped)));
            _cross.gameObject.SetActive(true);
        }

        public void HideCross()
        {
            if (_cross != null) _cross.gameObject.SetActive(false);
        }

        private static Mesh CrossMesh()
        {
            // Two flat bars lying on the floor, raised just above the grid lines and roads.
            var mb = new MeshBuilder();
            var red = LowPolyMeshes.Hex("#E2332B");
            const float arm = 1.8f, half = 0.22f, y = 0.1f;
            mb.Box(new Vector3(0f, y, 0f), arm * 2f, 0.04f, half * 2f, red, red);
            mb.Box(new Vector3(0f, y, 0f), half * 2f, 0.04f, arm * 2f, red, red);
            return mb.ToMesh(name: "grid cross");
        }

        private MeshFilter Child(string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.AddComponent<MeshFilter>();
        }
    }
}
