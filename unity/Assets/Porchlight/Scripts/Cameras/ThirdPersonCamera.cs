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
    /// zooms. The camera pulls in rather than ending up inside a building or a hill.
    /// Inside a cottage (<see cref="OpenBuildingId"/>) it comes in close over the right shoulder, at
    /// eye level, and never through a wall: it stops short of them, and backed into a corner it looks
    /// from the eyes until there's room (<see cref="CloseUp"/>). V (or <see cref="FirstPerson"/>) looks
    /// out of the character's eyes instead, anywhere.
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

        /// <summary>Indoors: how far behind the head, how far off to the right, and the tilt it settles to on coming in.</summary>
        public float ShoulderDistance = 1.8f;
        public float ShoulderOffset = 0.45f;
        public float ShoulderPitch = 12f;

        private float _eyeHeight = 1.4f;
        private bool _wasInside;
        private bool _settlePitch;

        /// <summary>Indoors, closer than this to the head and the camera looks from the eyes instead.</summary>
        public float CloseUpDistance = 0.55f;

        /// <summary>Indoors, how far above the floor the camera may go, keeping it under the ceiling.</summary>
        public float CeilingClearance = 2.2f;

        /// <summary>Indoors with no room behind (backed into a corner), so looking from the eyes for now.</summary>
        public bool CloseUp { get; private set; }

        /// <summary>Looking out of the character's eyes rather than from behind them.</summary>
        public bool FirstPerson { get; set; }

        /// <summary>Which way the camera faces, in degrees. The player moves relative to this.</summary>
        public float Yaw => _yaw;

        /// <summary>The cottage the player is inside, cut away so the camera can see in, or null.</summary>
        public string OpenBuildingId { get; set; }

        public void Follow(Transform target, float targetHeight, TownDto town, float yaw)
        {
            _target = target;
            _town = town;
            _lookHeight = targetHeight * 0.85f;
            _eyeHeight = targetHeight * 0.92f;
            _wasInside = false;
            _yaw = yaw;
            _pitch = 18f;
            _distance = Distance;
            _focus = Focus();
            enabled = true;
            Place();
        }

        /// <summary>Follows on over a new version of the same town (its baked ground, say).</summary>
        public void SetTown(TownDto town) => _town = town;

        private Vector3 Focus() => _target.position + Vector3.up * _lookHeight;

        private void LateUpdate()
        {
            if (_target == null) return;
            var dt = Time.unscaledDeltaTime;
            if (PInput.KeyDown(PKey.V)) FirstPerson = !FirstPerson;

            var pointerLook = PInput.MouseLookDelta() * MouseSensitivity + PInput.StickLook() * (StickLookSpeed * dt);
            if (FlyCamera.InvertY) pointerLook.y = -pointerLook.y;
            var keys = PInput.KeyLook();
            _yaw += pointerLook.x + keys.x * KeyTurnSpeed * dt;
            var looked = pointerLook.sqrMagnitude > 0f || keys.sqrMagnitude > 0f;
            _pitch = Mathf.Clamp(_pitch - pointerLook.y - keys.y * KeyTiltSpeed * dt, FirstPerson ? -70f : -10f, FirstPerson ? 70f : 75f);

            // Coming indoors, settle to a level look over the shoulder, unless the player looks around first.
            var inside = OpenBuildingId != null;
            if (inside && !_wasInside) _settlePitch = true;
            _wasInside = inside;
            if (looked || !inside || FirstPerson) _settlePitch = false;
            if (_settlePitch)
            {
                _pitch = Mathf.Lerp(_pitch, ShoulderPitch, 1f - Mathf.Exp(-6f * dt));
                if (Mathf.Abs(_pitch - ShoulderPitch) < 0.5f) _settlePitch = false;
            }

            var scroll = PInput.Scroll();
            if (!inside && !FirstPerson)
            {
                if (scroll > 0f) Distance *= 0.9f;
                if (scroll < 0f) Distance *= 1.1f;
                Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
            }

            _focus = Vector3.Lerp(_focus, Focus(), 1f - Mathf.Exp(-14f * dt));
            Place(dt);
        }

        private void Place(float dt = 0f)
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var eye = _target.position + Vector3.up * _eyeHeight;
            if (FirstPerson)
            {
                CloseUp = false;
                transform.SetPositionAndRotation(eye, rotation);
                return;
            }

            var back = rotation * Vector3.back;
            if (OpenBuildingId != null)
            {
                PlaceInside(rotation, eye, dt);
                return;
            }

            CloseUp = false;

            // Walk out from the character and stop short of the first wall or roof.
            var clear = Distance;
            for (var d = 0.4f; d <= Distance; d += 0.25f)
            {
                var p = _focus + back * d;
                if (p.y < TownTerrain.HeightAt(_town, new Vec2(p.x, p.z)) + 0.2f || TownWalking.IsInsideBuilding(_town, new Vec2(p.x, p.z), p.y, null))
                {
                    clear = Mathf.Max(MinDistance * 0.5f, d - 0.4f);
                    break;
                }
            }

            // Snap in quickly so walls never block the view; ease back out.
            _distance = dt <= 0f || clear < _distance ? clear : Mathf.Lerp(_distance, clear, 1f - Mathf.Exp(-3f * dt));
            var position = _focus + back * _distance;
            position.y = Mathf.Max(position.y, TownTerrain.HeightAt(_town, new Vec2(position.x, position.z)) + 0.2f);
            transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>
        /// Indoors: over the right shoulder, but never through a wall. The way from the head out to the
        /// shoulder and back to the camera is checked against the cottage's walls (the ones you bump into
        /// walking), and the camera stops short of the first. With no room at all (backed into a corner),
        /// it looks from the eyes until there is.
        /// </summary>
        private void PlaceInside(Quaternion rotation, Vector3 eye, float dt)
        {
            var feet = _target.position.y;
            var head = new Vec2(eye.x, eye.z);
            var right = rotation * Vector3.right;
            var back = rotation * Vector3.back;

            // The shoulder, if there's room beside the head; then back from it.
            var shoulderOffset = Clear(head, new Vec2(right.x, right.z), ShoulderOffset, feet);
            var shoulder = eye + right * shoulderOffset;
            var flatBack = new Vec2(back.x, back.z);
            var flatLength = flatBack.Length;
            var clear = flatLength < 1e-3f ? ShoulderDistance : Clear(new Vec2(shoulder.x, shoulder.z), flatBack * (1f / flatLength), ShoulderDistance * flatLength, feet) / flatLength;

            _distance = dt <= 0f || clear < _distance ? clear : Mathf.Lerp(_distance, clear, 1f - Mathf.Exp(-4f * dt));
            CloseUp = _distance < CloseUpDistance;
            if (CloseUp)
            {
                transform.SetPositionAndRotation(eye, rotation);
                return;
            }

            // Kept under the ceiling.
            var position = shoulder + back * _distance;
            position.y = Mathf.Min(position.y, feet + CeilingClearance);
            transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>How far, up to <paramref name="want"/>, a thin probe can go from a point along a direction before a wall.</summary>
        private float Clear(Vec2 from, Vec2 direction, float want, float feet)
        {
            const float step = 0.15f, probe = 0.15f, margin = 0.25f;
            for (var d = step; d <= want + 1e-3f; d += step)
            {
                var target = from + direction * d;
                var reached = TownWalking.MoveAt(_town, from, direction * d, feet, probe);
                if (Vec2.Distance(reached, target) > 0.03f) return Mathf.Max(0f, d - step - margin);
            }

            return want;
        }    }
}
