using Porchlight.Rendering;
using Porchlight.Shared.Content;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Avatars
{
    /// <summary>
    /// A character's model plus a tiny procedural animator: idle breathing, a walk cycle that speeds
    /// up with the character, arms out in the air, a wave and a hop. No animation assets needed.
    /// Whoever moves the character calls <see cref="Tick"/> each frame.
    /// </summary>
    public sealed class CharacterRig : MonoBehaviour
    {
        private const float HopDuration = 0.4f;

        private Transform _visual;
        private Transform _leftArm;
        private Transform _rightArm;
        private Transform _leftLeg;
        private Transform _rightLeg;
        private float _scale = 1f;
        private float _phase;
        private float _stride;
        private float _hopTime = HopDuration;
        private float _air;

        public CharacterType Type { get; private set; }

        /// <summary>Raises the right arm and waves. The character select screen uses it for the chosen one.</summary>
        public bool Waving { get; set; }

        public static CharacterRig Create(CharacterType type, Material material, Transform parent = null)
        {
            var root = new GameObject(type.DisplayName);
            if (parent != null) root.transform.SetParent(parent, false);
            var rig = root.AddComponent<CharacterRig>();
            rig.Build(type, material);
            return rig;
        }

        private void Build(CharacterType type, Material material)
        {
            Type = type;
            _scale = type.Height / CharacterMeshes.ModelHeight;
            var meshes = CharacterMeshes.Get(type);

            _visual = new GameObject("Visual").transform;
            _visual.SetParent(transform, false);
            _visual.localScale = Vector3.one * _scale;

            Part("Body", meshes.Body, Vector3.zero, material);
            _leftArm = Part("Left arm", meshes.Arm, new Vector3(-CharacterMeshes.ShoulderOffset, CharacterMeshes.ShoulderHeight, 0f), material);
            _rightArm = Part("Right arm", meshes.Arm, new Vector3(CharacterMeshes.ShoulderOffset, CharacterMeshes.ShoulderHeight, 0f), material);
            _leftLeg = Part("Left leg", meshes.Leg, new Vector3(-CharacterMeshes.HipOffset, CharacterMeshes.HipHeight, 0f), material);
            _rightLeg = Part("Right leg", meshes.Leg, new Vector3(CharacterMeshes.HipOffset, CharacterMeshes.HipHeight, 0f), material);
        }

        private Transform Part(string name, Mesh mesh, Vector3 pivot, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_visual, false);
            go.transform.localPosition = pivot;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        public void Hop() => _hopTime = 0f;

        /// <param name="groundSpeed">How fast the character is moving over the ground, in m/s.</param>
        /// <param name="grounded">False while jumping or falling.</param>
        public void Tick(float groundSpeed, bool grounded, float dt)
        {
            // Shorter legs take quicker steps.
            var strideTarget = grounded ? Mathf.Clamp01(groundSpeed / 3f) : 0f;
            _stride = Mathf.MoveTowards(_stride, strideTarget, dt * 6f);
            _phase += dt * (groundSpeed / _scale) * 2.6f;
            _air = Mathf.MoveTowards(_air, grounded ? 0f : 1f, dt * 8f);

            var swing = Mathf.Sin(_phase) * 38f * _stride;
            var legTuck = 25f * _air;
            _leftLeg.localRotation = Quaternion.Euler(swing - legTuck, 0f, 0f);
            _rightLeg.localRotation = Quaternion.Euler(-swing + legTuck * 0.4f, 0f, 0f);

            var armsOut = 8f + 55f * _air;
            _leftArm.localRotation = Quaternion.Euler(-swing * 0.8f, 0f, -armsOut);
            _rightArm.localRotation = Waving
                ? Quaternion.Euler(0f, 0f, 150f + Mathf.Sin(Time.time * 9f) * 18f)
                : Quaternion.Euler(swing * 0.8f, 0f, armsOut);

            // Bob with each step, breathe while standing, and hop when asked.
            _hopTime += dt;
            var hop = _hopTime < HopDuration ? Mathf.Sin(Mathf.PI * _hopTime / HopDuration) * 0.18f : 0f;
            var bob = Mathf.Abs(Mathf.Sin(_phase)) * 0.035f * _stride + Mathf.Sin(Time.time * 2.2f) * 0.004f * (1f - _stride);
            _visual.localPosition = Vector3.up * ((bob + hop) * _scale);
        }
    }
}
