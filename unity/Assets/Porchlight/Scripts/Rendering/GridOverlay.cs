using Porchlight.Shared.Geometry;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The <see cref="TownGrid"/> drawn across the whole town floor as one transparent quad, plus the
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

        public void Show(float townSize)
        {
            var half = townSize * 0.5f;
            var mb = new MeshBuilder();
            mb.Quad(new Vector3(-half, Lift, -half), new Vector3(-half, Lift, half), new Vector3(half, Lift, half), new Vector3(half, Lift, -half), Color.white, Vector3.up);
            if (_floor.sharedMesh != null) Destroy(_floor.sharedMesh);
            _floor.sharedMesh = mb.ToMesh(name: "grid");
            HideCross();
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>Puts the red cross on the grid intersection nearest to <paramref name="point"/>.</summary>
        public void ShowCross(Vec2 point)
        {
            var snapped = TownGrid.Snap(point);
            _cross.position = new Vector3(snapped.X, 0f, snapped.Z);
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
