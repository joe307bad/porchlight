using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Avatars
{
    /// <summary>
    /// Walks the player's character around a town: arrows or the left stick move relative to the
    /// camera, Shift or a stick click runs, Space or A jumps. Collisions come from the shared
    /// <see cref="TownWalking"/> rules, so there are no colliders or physics to set up.
    /// </summary>
    [RequireComponent(typeof(CharacterRig))]
    public sealed class PlayerController : MonoBehaviour
    {
        public float WalkSpeed = 3.5f;
        public float RunSpeed = 7f;
        public float JumpSpeed = 5f;
        public float Gravity = 16f;
        public float TurnSpeed = 720f;

        private CharacterRig _rig;
        private ThirdPersonCamera _camera;
        private TownDto _town;
        private float _heading;
        private float _verticalSpeed;
        private bool _grounded = true;

        public void Begin(TownDto town, ThirdPersonCamera camera, Vec2 position, float heading)
        {
            _rig = GetComponent<CharacterRig>();
            _town = town;
            _camera = camera;
            _heading = heading;
            _verticalSpeed = 0f;
            _grounded = true;
            transform.SetPositionAndRotation(
                new Vector3(position.X, TownWalking.GroundHeight(town, position), position.Z),
                Quaternion.Euler(0f, heading, 0f));
            gameObject.SetActive(true);
            enabled = true;
        }

        private void Update()
        {
            if (_town == null) return;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Stick and arrows are relative to where the camera looks.
            var input = PInput.Move();
            var forward = Quaternion.Euler(0f, _camera.Yaw, 0f);
            var direction = forward * new Vector3(input.x, 0f, input.y);
            var speed = PInput.Fast() ? RunSpeed : WalkSpeed;

            var position = transform.position;
            var from = new Vec2(position.x, position.z);
            var to = TownWalking.Move(_town, from, new Vec2(direction.x, direction.z) * (speed * dt));
            var groundSpeed = Vec2.Distance(from, to) / dt;

            if (direction.sqrMagnitude > 0.01f)
            {
                _heading = Mathf.MoveTowardsAngle(_heading, Vec2.HeadingOf(new Vec2(direction.x, direction.z)), TurnSpeed * dt);
            }

            var ground = TownWalking.GroundHeight(_town, to);
            if (_grounded && PInput.Jump())
            {
                _verticalSpeed = JumpSpeed;
                _grounded = false;
            }

            // Downhill, stay on the ground; off the edge of a park, drop.
            var y = position.y;
            if (_grounded && y > ground + 0.05f + TownTerrain.MaxSlope * Vec2.Distance(from, to)) _grounded = false;
            else if (_grounded) y = Mathf.Min(y, ground);
            if (!_grounded)
            {
                _verticalSpeed -= Gravity * dt;
                y += _verticalSpeed * dt;
            }

            if (y <= ground)
            {
                y = ground;
                _verticalSpeed = 0f;
                _grounded = true;
            }

            transform.SetPositionAndRotation(new Vector3(to.X, y, to.Z), Quaternion.Euler(0f, _heading, 0f));
            _rig.Tick(groundSpeed, _grounded, dt);
        }

        public void End()
        {
            _town = null;
            enabled = false;
            gameObject.SetActive(false);
        }
    }
}
