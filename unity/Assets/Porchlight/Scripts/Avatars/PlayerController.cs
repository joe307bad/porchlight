using Porchlight.Cameras;
using Porchlight.Input;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Avatars
{
    /// <summary>
    /// Walks the player's character around a town: arrows or the left stick move relative to the
    /// camera, Shift or the right trigger runs, Space or A jumps. Collisions come from the shared
    /// <see cref="TownWalking"/> rules, so there are no colliders or physics to set up.
    ///
    /// There are no buttons for doing things: walking into a couch, chair or toilet sits down on it,
    /// and walking into a bed lies down on it. Any move after that gets up again, off the side you
    /// pushed towards (or the nearest side with room). (Doors open by themselves; see
    /// <see cref="Rendering.TownView"/>.) Indoors, everyone walks a little slower.
    /// </summary>
    [RequireComponent(typeof(CharacterRig))]
    public sealed class PlayerController : MonoBehaviour
    {
        public float WalkSpeed = 3.5f;
        public float RunSpeed = 7f;
        public float JumpSpeed = 5f;
        public float Gravity = 16f;
        public float TurnSpeed = 720f;

        /// <summary>
        /// The steepest rise that can be walked up, over <see cref="ClimbProbe"/> metres ahead: banks and
        /// stairs go, terrace walls don't (jump down them, or take the steps).
        /// </summary>
        public float MaxClimb = 0.45f;
        public float ClimbProbe = 0.35f;

        /// <summary>Walking and running speed inside a cottage, as a share of outside.</summary>
        public float IndoorSpeedScale = 0.55f;

        /// <summary>Seconds after getting up before walking into something sits down again.</summary>
        public float SitCooldown = 0.6f;

        /// <summary>Seconds to settle onto a seat or bed.</summary>
        public float SettleTime = 0.3f;

        /// <summary>The shortest a sit or lie lasts before moving gets up again.</summary>
        public float MinRestTime = 0.5f;

        private CharacterRig _rig;
        private ThirdPersonCamera _camera;
        private TownDto _town;
        private float _heading;
        private float _verticalSpeed;
        private bool _grounded = true;
        private FurnitureSpot? _resting;
        private float _cooldown;

        // Settling in: from where they were, over SettleTime. Getting up waits until the move that sat
        // them down has been let go.
        private float _restTime;
        private bool _awaitRelease;
        private Vector3 _settleFrom;
        private Quaternion _settleFromRotation;

        /// <summary>Sitting or lying on something.</summary>
        public bool Resting => _resting.HasValue;

        /// <param name="floorY">Roughly where their feet are, to pick a cottage floor; NaN for ground level.</param>
        public void Begin(TownDto town, ThirdPersonCamera camera, Vec2 position, float heading, float floorY = float.NaN)
        {
            _rig = GetComponent<CharacterRig>();
            _town = town;
            _camera = camera;
            _heading = heading;
            _verticalSpeed = 0f;
            _grounded = true;
            _resting = null;
            _rig.CurrentPose = CharacterRig.Pose.Standing;
            transform.SetPositionAndRotation(
                new Vector3(position.X, TownWalking.GroundHeightAt(town, position, floorY), position.Z),
                Quaternion.Euler(0f, heading, 0f));
            gameObject.SetActive(true);
            enabled = true;
        }

        /// <summary>Carries on walking on a new version of the same town (its baked ground, say), standing on it.</summary>
        public void SetTown(TownDto town)
        {
            if (_town == null || town == null) return;
            _town = town;
            var p = transform.position;
            transform.position = new Vector3(p.x, TownWalking.GroundHeightAt(town, new Vec2(p.x, p.z), p.y), p.z);
        }

        /// <summary>The cottage the character is inside, and which floor, if any.</summary>
        public bool TryGetInterior(out string buildingId, out int floor)
        {
            buildingId = null;
            floor = 0;
            if (_town == null) return false;
            var p = transform.position;
            if (!TownWalking.TryFindInterior(_town, new Vec2(p.x, p.z), p.y, out var building, out floor)) return false;
            buildingId = building.Id;
            return true;
        }

        private void Update()
        {
            if (_town == null) return;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            _cooldown -= dt;

            // Stick and arrows are relative to where the camera looks.
            var input = PInput.Move();
            var forward = Quaternion.Euler(0f, _camera.Yaw, 0f);
            var direction = forward * new Vector3(input.x, 0f, input.y);
            if (_resting.HasValue)
            {
                _restTime += dt;
                var moving = input.sqrMagnitude > 0.04f;
                if (!moving) _awaitRelease = false;
                if (!_awaitRelease && _restTime >= MinRestTime && (moving || PInput.Jump())) GetUp(moving ? new Vec2(direction.x, direction.z) : Vec2.Zero);
                else
                {
                    Settle();
                    _rig.Tick(0f, true, dt);
                    return;
                }
            }
            var speed = PInput.Run() ? RunSpeed : WalkSpeed;
            if (TryGetInterior(out _, out _)) speed *= IndoorSpeedScale;

            var position = transform.position;
            var from = new Vec2(position.x, position.z);
            var step = new Vec2(direction.x, direction.z) * (speed * dt);

            // Walking into something to sit or lie on does that instead.
            if (_grounded && _cooldown <= 0f && direction.sqrMagnitude > 0.01f && TownWalking.TryFindFurnitureSpot(_town, from, step, position.y, out var spot))
            {
                Rest(spot);
                return;
            }

            var to = TownWalking.MoveAt(_town, from, step, position.y);

            // A wall too high to step up stops you; slide along it if you're going partly sideways.
            if (_grounded && TooSteep(from, to - from, position.y))
            {
                var alongX = TownWalking.MoveAt(_town, from, new Vec2(step.X, 0f), position.y);
                var alongZ = TownWalking.MoveAt(_town, from, new Vec2(0f, step.Z), position.y);
                to = !TooSteep(from, alongX - from, position.y) ? alongX : !TooSteep(from, alongZ - from, position.y) ? alongZ : from;
            }
            var groundSpeed = Vec2.Distance(from, to) / dt;

            if (direction.sqrMagnitude > 0.01f)
            {
                _heading = Mathf.MoveTowardsAngle(_heading, Vec2.HeadingOf(new Vec2(direction.x, direction.z)), TurnSpeed * dt);
            }

            var ground = TownWalking.GroundHeightAt(_town, to, position.y);
            if (_grounded && PInput.Jump())
            {
                _verticalSpeed = JumpSpeed;
                _grounded = false;
            }

            // Downhill (or downstairs), stay on the ground; off the edge of a park, drop.
            var y = position.y;
            var fall = Mathf.Max(TownTerrain.MaxSlope, 1f) * Vec2.Distance(from, to);
            if (_grounded && y > ground + 0.05f + fall) _grounded = false;
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

        private bool TooSteep(Vec2 from, Vec2 move, float y)
        {
            var length = move.Length;
            if (length < 1e-5f) return false;
            var ahead = from + move * (ClimbProbe / length);
            return TownWalking.GroundHeightAt(_town, ahead, y) - y > MaxClimb;
        }

        private void Rest(FurnitureSpot spot)
        {
            _resting = spot;
            _heading = spot.Heading;
            _verticalSpeed = 0f;
            _restTime = 0f;
            _awaitRelease = true;
            _settleFrom = transform.position;
            _settleFromRotation = transform.rotation;
            _rig.CurrentPose = spot.Action == FurnitureAction.LieDown ? CharacterRig.Pose.Lying : CharacterRig.Pose.Sitting;
            Settle();
            _rig.Tick(0f, true, 0f);
        }

        /// <summary>Glides from where they were onto the seat or bed, turning to face the right way, with a little hop.</summary>
        private void Settle()
        {
            var spot = _resting.Value;
            var target = new Vector3(spot.Position.X, spot.Height, spot.Position.Z);
            var t = SettleTime <= 0f ? 1f : Mathf.Clamp01(_restTime / SettleTime);
            var eased = t * t * (3f - 2f * t);
            var position = Vector3.Lerp(_settleFrom, target, eased) + Vector3.up * (Mathf.Sin(Mathf.PI * t) * 0.15f);
            transform.SetPositionAndRotation(position, Quaternion.Slerp(_settleFromRotation, Quaternion.Euler(0f, spot.Heading, 0f), eased));
        }

        /// <summary>
        /// Back on their feet beside whatever they were on, ready to walk: off the side towards
        /// <paramref name="toward"/> (in the town), facing that way. Zero means the usual side.
        /// </summary>
        private void GetUp(Vec2 toward)
        {
            var spot = _resting.Value;
            _resting = null;
            _cooldown = SitCooldown;
            _rig.CurrentPose = CharacterRig.Pose.Standing;
            if (toward.LengthSquared > 1e-4f) _heading = Vec2.HeadingOf(toward);

            // Stand at the exit, nudged clear of anything there.
            var floorY = spot.Height - 0.3f;
            var exit = TownWalking.MoveAt(_town, TownWalking.ExitToward(_town, spot, toward), Vec2.Zero, floorY);
            var ground = TownWalking.GroundHeightAt(_town, exit, floorY);
            _grounded = true;
            transform.SetPositionAndRotation(new Vector3(exit.X, ground, exit.Z), Quaternion.Euler(0f, _heading, 0f));
        }

        /// <summary>Hides the character (for first-person view) or shows them again.</summary>
        public void SetHidden(bool hidden)
        {
            if (hidden == _hidden) return;
            _hidden = hidden;
            foreach (var renderer in GetComponentsInChildren<Renderer>(true)) renderer.enabled = !hidden;
        }

        private bool _hidden;

        public void End()
        {
            _town = null;
            _resting = null;
            enabled = false;
            gameObject.SetActive(false);
        }
    }
}
