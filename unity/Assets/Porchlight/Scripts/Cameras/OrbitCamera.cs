using Porchlight.App;
using Porchlight.Input;
using UnityEngine;

namespace Porchlight.Cameras
{
    /// <summary>
    /// Circles a fixed point, which stays in the middle of the screen: the interior editor locks it on
    /// the cottage being edited. Dragging with any mouse button turns and tilts, as do WASD, the arrows
    /// and the right stick; the scroll wheel zooms in and out. When the point moves (to another floor)
    /// the camera glides after it.
    /// </summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public float DragSensitivity = 0.3f;
        public float StickSpeed = 120f;
        public float KeyTurnSpeed = 90f;
        public float KeyTiltSpeed = 60f;
        public float ZoomStep = 0.12f;
        public float MinDistance = 5f;
        public float MaxDistance = 40f;
        public float MinPitch = 12f;
        public float MaxPitch = 88f;

        private Vector3 _target;
        private Vector3 _center;
        private float _yaw;
        private float _pitch;
        private float _distance;

        /// <summary>The point kept in the middle of the screen.</summary>
        public Vector3 Center
        {
            get => _target;
            set => _target = value;
        }

        public void Begin(Vector3 center, float yaw, float pitch, float distance)
        {
            _target = _center = center;
            _yaw = yaw;
            _pitch = Mathf.Clamp(pitch, MinPitch, MaxPitch);
            _distance = Mathf.Clamp(distance, MinDistance, MaxDistance);
            enabled = true;
            Place();
        }

        private void LateUpdate()
        {
            var dt = Time.unscaledDeltaTime;

            // Any drag turns: the left button (a click places things instead), the right, or the middle.
            var look = Vector2.zero;
            if (PointerGesture.Dragging || PInput.MiddleHeld()) look += PInput.PointerDelta() * DragSensitivity;
            else look += PInput.MouseLookDelta() * DragSensitivity;
            look += PInput.StickLook() * (StickSpeed * dt);
            if (FlyCamera.InvertY) look.y = -look.y;

            var keys = PInput.KeyLook() + PInput.Move();
            look += new Vector2(keys.x * KeyTurnSpeed, keys.y * KeyTiltSpeed) * dt;
            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, MinPitch, MaxPitch);

            var scroll = Mathf.Clamp(PInput.Scroll(), -3f, 3f);
            if (Mathf.Abs(scroll) > 0.01f && !HudBar.PointerInBar()) _distance = Mathf.Clamp(_distance * Mathf.Pow(1f - ZoomStep, scroll), MinDistance, MaxDistance);

            _center = Vector3.Lerp(_center, _target, 1f - Mathf.Exp(-8f * dt));
            Place();
        }

        private void Place()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_center - rotation * Vector3.forward * _distance, rotation);
        }
    }
}
