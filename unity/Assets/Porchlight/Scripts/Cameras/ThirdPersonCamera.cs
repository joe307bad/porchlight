using Porchlight.Input;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Cameras
{
    /// <summary>
    /// Follows the player's character from behind and orbits around them. Looking uses the same
    /// inputs as <see cref="FlyCamera"/> (WASD, right mouse, right stick, Invert Y); the scroll wheel
    /// zooms. The camera pulls in rather than ending up inside a building.
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        public float Distance = 4f;
        public float MinDistance = 1.5f;
        public float MaxDistance = 14f;
        public float MouseSensitivity = 0.15f;
        public float StickLookSpeed = 140f;
        public float KeyTurnSpeed = 110f;
        public float KeyTiltSpeed = 60f;

        private Transform _target;
        private TownDto _town;
        private float _lookHeight = 1f;
        private float _yaw;
        private float _pitch = 18f;
        private float _distance;
        private Vector3 _focus;

        /// <summary>Which way the camera faces, in degrees. The player moves relative to this.</summary>
        public float Yaw => _yaw;

        public void Follow(Transform target, float targetHeight, TownDto town, float yaw)
        {
            _target = target;
            _town = town;
            _lookHeight = targetHeight * 0.85f;
            _yaw = yaw;
            _pitch = 18f;
            _distance = Distance;
            _focus = Focus();
            enabled = true;
            Place();
        }

        private Vector3 Focus() => _target.position + Vector3.up * _lookHeight;

        private void LateUpdate()
        {
            if (_target == null) return;
            var dt = Time.unscaledDeltaTime;

            var pointerLook = PInput.MouseLookDelta() * MouseSensitivity + PInput.StickLook() * (StickLookSpeed * dt);
            if (FlyCamera.InvertY) pointerLook.y = -pointerLook.y;
            var keys = PInput.KeyLook();
            _yaw += pointerLook.x + keys.x * KeyTurnSpeed * dt;
            _pitch = Mathf.Clamp(_pitch - pointerLook.y - keys.y * KeyTiltSpeed * dt, -10f, 75f);

            var scroll = PInput.Scroll();
            if (scroll > 0f) Distance *= 0.9f;
            if (scroll < 0f) Distance *= 1.1f;
            Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);

            _focus = Vector3.Lerp(_focus, Focus(), 1f - Mathf.Exp(-14f * dt));
            Place(dt);
        }

        private void Place(float dt = 0f)
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var back = rotation * Vector3.back;

            // Walk out from the character and stop short of the first wall or roof.
            var clear = Distance;
            for (var d = 0.4f; d <= Distance; d += 0.25f)
            {
                var p = _focus + back * d;
                if (p.y < 0.2f || TownWalking.IsInsideBuilding(_town, new Vec2(p.x, p.z), p.y))
                {
                    clear = Mathf.Max(MinDistance * 0.5f, d - 0.4f);
                    break;
                }
            }

            // Snap in quickly so walls never block the view; ease back out.
            _distance = dt <= 0f || clear < _distance ? clear : Mathf.Lerp(_distance, clear, 1f - Mathf.Exp(-3f * dt));
            var position = _focus + back * _distance;
            position.y = Mathf.Max(position.y, 0.2f);
            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
