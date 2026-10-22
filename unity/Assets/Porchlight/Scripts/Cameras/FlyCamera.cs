using Porchlight.App;
using Porchlight.Input;
using Porchlight.Rendering;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Cameras
{
    /// <summary>
    /// Free-flying camera. Arrow keys move, WASD looks (A/D turn, W/S tilt), Space/C change height,
    /// holding the right mouse button also looks around, Shift goes faster. On a gamepad: left stick
    /// moves, right stick looks, triggers change height. Moves faster the higher it is, so a big town doesn't feel slow.
    /// With the mouse, dragging with the left or middle button pans (the ground under the pointer
    /// stays under it), the scroll wheel zooms towards whatever is under the pointer, and holding
    /// Ctrl while dragging orbits around the middle of the view to change the viewing angle.
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

        /// <summary>How much closer one notch of the scroll wheel brings you.</summary>
        public float ZoomStep = 0.07f;

        /// <summary>Degrees of orbit per pixel of Ctrl-drag.</summary>
        public float OrbitSensitivity = 0.25f;

        /// <summary>
        /// While the terrain builder is holding a shape, the arrow keys and the scroll wheel move the
        /// shape instead of the camera. WASD still looks around and dragging the empty ground still pans.
        /// </summary>
        public static bool KeysCaptured { get; set; }

        /// <summary>While something in the town is being dragged (a resize handle), dragging doesn't pan or orbit.</summary>
        public static bool PointerCaptured { get; set; }

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
        private Camera _camera;
        private bool _panning;
        private Vector3 _panAnchor;
        private bool _orbiting;
        private Vector3 _orbitPivot;

        private TownDto _town;

        /// <summary>Puts the camera south of the town, looking north and down at it.</summary>
        public void Frame(TownDto town)
        {
            _town = town;
            var townSize = town.Size;
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
            if (!PointerCaptured && Orbit()) return;

            var pointerLook = PInput.MouseLookDelta() * MouseSensitivity + PInput.StickLook() * (StickLookSpeed * dt);
            if (InvertY) pointerLook.y = -pointerLook.y;

            var keys = PInput.KeyLook();
            var look = pointerLook + new Vector2(keys.x * KeyTurnSpeed, keys.y * KeyTiltSpeed) * dt;
            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, -20f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            var position = transform.position;
            if (PointerCaptured) _panning = false;
            else Pan(ref position);
            if (!KeysCaptured) Zoom(ref position);
            var heightBoost = Mathf.Lerp(0.6f, 3f, Mathf.InverseLerp(MinHeight, 200f, position.y));
            var speed = Speed * heightBoost * (PInput.Fast() ? FastMultiplier : 1f);

            // Move on the ground plane so looking down doesn't send you into the ground.
            var yaw = Quaternion.Euler(0f, _yaw, 0f);
            var move = KeysCaptured ? Vector2.zero : PInput.Move();
            position += yaw * new Vector3(move.x, 0f, move.y) * (speed * dt);
            position.y += PInput.Vertical() * VerticalSpeed * heightBoost * dt;

            position.x = Mathf.Clamp(position.x, -_bounds, _bounds);
            position.z = Mathf.Clamp(position.z, -_bounds, _bounds);
            position.y = Mathf.Clamp(position.y, Floor(position), MaxHeight);
            transform.position = position;
        }

        /// <summary>Swings the camera round to look at <paramref name="target"/> from <paramref name="distance"/> away, keeping its heading.</summary>
        public void FocusOn(Vector3 target, float distance)
        {
            _pitch = Mathf.Clamp(_pitch, 20f, 70f);
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var position = target - rotation * Vector3.forward * distance;
            position.y = Mathf.Clamp(position.y, Floor(position), MaxHeight);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
            var euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = Mathf.Clamp(euler.x > 180f ? euler.x - 360f : euler.x, -20f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        /// <summary>The lowest the camera may go here: <see cref="MinHeight"/> above the ground.</summary>
        private float Floor(Vector3 position) =>
            MinHeight + (_town == null ? 0f : Mathf.Max(0f, TownTerrain.HeightAt(_town, new Vec2(position.x, position.z))));

        /// <summary>
        /// Ctrl-drag (with either button, since a Mac browser turns Ctrl+click into a right click)
        /// circles the camera around the ground in the middle of the view, keeping it in the middle.
        /// Returns true while orbiting, when the other controls are skipped.
        /// </summary>
        private bool Orbit()
        {
            var held = PInput.Control() && (PointerGesture.Dragging || PInput.SecondaryHeld());
            if (!held)
            {
                _orbiting = false;
                return false;
            }

            if (!_orbiting)
            {
                if (_camera == null) _camera = GetComponent<Camera>();
                if (!GroundUnder(_camera.pixelRect.center, out _orbitPivot)) _orbitPivot = transform.position + transform.forward * 50f;
                _orbiting = true;
            }

            var delta = PInput.PointerDelta() * OrbitSensitivity;
            if (InvertY) delta.y = -delta.y;
            _yaw += delta.x;
            _pitch = Mathf.Clamp(_pitch - delta.y, 5f, 89f);

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var distance = Vector3.Distance(transform.position, _orbitPivot);
            var position = _orbitPivot - rotation * Vector3.forward * distance;
            position.y = Mathf.Clamp(position.y, Floor(position), MaxHeight);
            transform.SetPositionAndRotation(position, rotation);
            return true;
        }

        /// <summary>Grab-and-drag: keeps the ground point that was under the pointer at the start under it.</summary>
        private void Pan(ref Vector3 position)
        {
            if (PointerGesture.DragStarted || (PInput.MiddlePressedThisFrame() && !HudBar.PointerInBar()))
            {
                // Anchor where the press began, so the first few pixels of the drag aren't lost.
                var start = PInput.MiddlePressedThisFrame() ? PInput.PointerPosition() : PointerGesture.PressPosition;
                _panning = GroundUnder(start, out _panAnchor);
            }

            if (!PointerGesture.Dragging && !PInput.MiddleHeld())
            {
                _panning = false;
                return;
            }

            if (_panning && GroundUnder(PInput.PointerPosition(), out var under))
            {
                position.x += _panAnchor.x - under.x;
                position.z += _panAnchor.z - under.z;
                transform.position = position;
            }
        }

        /// <summary>Moves along the line to the ground under the pointer, so that spot stays put.</summary>
        private void Zoom(ref Vector3 position)
        {
            var notches = Mathf.Clamp(PInput.Scroll(), -3f, 3f);
            if (Mathf.Abs(notches) < 0.01f || HudBar.PointerInBar() || !GroundUnder(PInput.PointerPosition(), out var focus)) return;

            var offset = position - focus;
            var scale = Mathf.Pow(1f - ZoomStep, notches);

            // Stop at the height limits rather than sliding along them.
            if (offset.y > 0.01f) scale = Mathf.Clamp(scale, (MinHeight - focus.y) / offset.y, (MaxHeight - focus.y) / offset.y);
            position = focus + offset * scale;
        }

        private bool GroundUnder(Vector2 screenPoint, out Vector3 point)
        {
            point = default;
            if (_camera == null) _camera = GetComponent<Camera>();
            var ray = _camera.ScreenPointToRay(screenPoint);
            if (ray.direction.y > -0.02f) return false;
            return TerrainMeshes.Raycast(_town, ray, out point);
        }
    }
}
