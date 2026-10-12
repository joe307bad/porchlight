using Porchlight.Input;
using UnityEngine;

namespace Porchlight.Cameras
{
    /// <summary>
    /// Free-flying camera. Arrow keys move, WASD looks (A/D turn, W/S tilt), Space/C change height,
    /// holding the right mouse button also looks around, Shift goes faster. On a gamepad: left stick
    /// moves, right stick looks, triggers change height. Moves faster the higher it is, so a big town doesn't feel slow.
    /// </summary>
    public sealed class FlyCamera : MonoBehaviour
    {
        public float Speed = 25f;
        public float FastMultiplier = 3f;
        public float VerticalSpeed = 20f;
        public float MouseSensitivity = 0.15f;
        public float StickLookSpeed = 120f;
        public float KeyTurnSpeed = 90f;
        public float KeyTiltSpeed = 60f;
        public float MinHeight = 2f;
        public float MaxHeight = 400f;

        private const string InvertYPref = "Porchlight.InvertY";
        private static int _invertY = -1;

        /// <summary>
        /// Flips up/down for the mouse and right stick (W/S always look up/down). Saved in PlayerPrefs,
        /// which also works on the Switch.
        /// </summary>
        public static bool InvertY
        {
            get
            {
                if (_invertY < 0) _invertY = PlayerPrefs.GetInt(InvertYPref, 0);
                return _invertY == 1;
            }
            set
            {
                _invertY = value ? 1 : 0;
                PlayerPrefs.SetInt(InvertYPref, _invertY);
                PlayerPrefs.Save();
            }
        }

        private float _yaw;
        private float _pitch;
        private float _bounds = 500f;

        /// <summary>Puts the camera south of the town, looking north and down at it.</summary>
        public void Frame(float townSize)
        {
            _bounds = townSize * 0.75f;
            _yaw = 0f;
            _pitch = 35f;
            transform.SetPositionAndRotation(new Vector3(0f, townSize * 0.3f, -townSize * 0.55f), Quaternion.Euler(_pitch, _yaw, 0f));
        }

        private void OnEnable()
        {
            var euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;

            var pointerLook = PInput.MouseLookDelta() * MouseSensitivity + PInput.StickLook() * (StickLookSpeed * dt);
            if (InvertY) pointerLook.y = -pointerLook.y;

            var keys = PInput.KeyLook();
            var look = pointerLook + new Vector2(keys.x * KeyTurnSpeed, keys.y * KeyTiltSpeed) * dt;
            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, -20f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            var position = transform.position;
            var heightBoost = Mathf.Lerp(0.6f, 3f, Mathf.InverseLerp(MinHeight, 200f, position.y));
            var speed = Speed * heightBoost * (PInput.Fast() ? FastMultiplier : 1f);

            // Move on the ground plane so looking down doesn't send you into the ground.
            var yaw = Quaternion.Euler(0f, _yaw, 0f);
            var move = PInput.Move();
            position += yaw * new Vector3(move.x, 0f, move.y) * (speed * dt);
            position.y += PInput.Vertical() * VerticalSpeed * heightBoost * dt;

            position.x = Mathf.Clamp(position.x, -_bounds, _bounds);
            position.z = Mathf.Clamp(position.z, -_bounds, _bounds);
            position.y = Mathf.Clamp(position.y, MinHeight, MaxHeight);
            transform.position = position;
        }
    }
}
