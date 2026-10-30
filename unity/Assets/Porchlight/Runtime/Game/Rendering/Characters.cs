// Townsfolk, other players and you, as RG Poly's rigged Cartoon City people.
// Each Avatar is an instance of a person prefab (ModelLibrary) whose Animator
// plays the shared controller: idle, walk and run blended by pace, played
// faster as they hurry, and jump, fall and land in the air. Around that, the
// body leans into speeding up and into turns, and squashes and stretches on
// take-off and landing. You're drawn on the Player layer, which the renderer
// shows through anything in front of you (the x-ray outline). The kit has no
// wave, so a townsperson waves by having their right arm raised over the
// Animator's pose (ArmWave, after the Animator has run).

using System;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    public sealed class Avatar
    {
        public const double PERSON_HEIGHT = 0.36;
        /// <summary>The layer the renderer draws through walls (Editor/Setup.cs adds it).</summary>
        public const string PLAYER_LAYER = "Player";
        const double NATURAL_WALK = 0.53;
        const double NATURAL_RUN = 1.1;
        const double WALK_TOP = 1.7;
        const double RUN_FROM = 2.5;
        const double CADENCE = 0.55;
        const double RUN_LEAN = 0.12;
        const double ACCEL_LEAN = 0.012;
        const double BANK = 0.05;
        const double MAX_TILT = 0.3;
        const double FEET = 0.035;
        /// <summary>Where the head starts, as a share of a person's height.</summary>
        const double HEAD_SHARE = 0.71;

        static readonly int Speed = Animator.StringToHash("Speed");
        static readonly int Cadence = Animator.StringToHash("Cadence");
        static readonly int Air = Animator.StringToHash("Air");
        static readonly int Falling = Animator.StringToHash("Falling");

        public readonly GameObject root;
        readonly Transform body;
        readonly Animator animator;

        double rootYaw;
        double bodyX, bodyZ;
        bool wasAir;
        double lastRise;
        double sinceLanding = double.PositiveInfinity;
        double impact;
        double sinceTakeoff = double.PositiveInfinity;
        double lastSpeed;
        double? lastHeading;
        double accel, turn, moveWeight, airWeight;
        bool visible = true;

        public Avatar(ModelLibrary.Person person, double height, double phase, Transform parent)
        {
            root = new GameObject("person " + person.name);
            root.transform.SetParent(parent, false);
            body = new GameObject("body").transform;
            body.SetParent(root.transform, false);
            var model = UnityEngine.Object.Instantiate(person.prefab, body, false).transform;
            // Sized by where the head starts, so a tall hat or a quiff doesn't make anyone smaller;
            // turned round, as the people face backwards in their files.
            double h = !float.IsNaN(person.headY) ? person.headY / HEAD_SHARE : Math.Max(1e-3, person.maxY - person.minY);
            float k = (float)(height / h);
            model.localScale = Vector3.one * k;
            model.localPosition = new Vector3(0, -person.minY * k, 0);
            model.localRotation = Quaternion.Euler(0, 180, 0);
            animator = model.GetComponent<Animator>();
            animator.Update((float)(phase * 2));
        }

        /// <summary>Draw this one through walls (you).</summary>
        public void SeeThrough()
        {
            int layer = LayerMask.NameToLayer(PLAYER_LAYER);
            if (layer < 0) return;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        public bool Visible
        {
            get => visible;
            set
            {
                if (visible == value) return;
                visible = value;
                root.SetActive(value);
            }
        }

        static double Smoothstep(double a, double b, double x)
        {
            double t = Math.Min(1, Math.Max(0, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        static double Ease(double from, double to, double rate, double dt) => from + (to - from) * (1 - Math.Exp(-rate * dt));

        /// <summary>Pose the model where the walker is.</summary>
        public void Update(Walker w, double dt)
        {
            double k = Math.Max(1e-3, dt);
            root.transform.localPosition = Coords.Map(w.x, w.y, FEET + w.lift);
            root.transform.localScale = Vector3.one * (float)w.scale;
            rootYaw += Orbit.ShortestTurn(rootYaw, w.heading) * Math.Min(1, dt * (12 + w.speed * 2));
            root.transform.localRotation = Coords.Yaw(rootYaw);

            double pace = w.speed / w.scale;
            accel = Ease(accel, (pace - lastSpeed) / k, 8, dt);
            lastSpeed = pace;
            double turned = lastHeading == null ? 0 : Orbit.ShortestTurn(lastHeading.Value, w.heading);
            lastHeading = w.heading;
            turn = Ease(turn, turned / k, 10, dt);

            bool air = w.lift > 0;
            if (air && !wasAir) sinceTakeoff = 0;
            if (!air && wasAir)
            {
                sinceLanding = 0;
                impact = Math.Min(1, -lastRise / 2.2);
            }
            wasAir = air;
            lastRise = w.rise;
            sinceLanding += dt;
            sinceTakeoff += dt;
            airWeight = Ease(airWeight, air ? 1 : 0, air ? 20 : 12, dt);

            bool moving = pace > 0.05;
            moveWeight = Ease(moveWeight, moving ? 1 : 0, moving ? 10 : 7, dt);
            double run = Smoothstep(WALK_TOP, RUN_FROM, pace);
            double walkRate = Math.Pow(Math.Max(0.05, pace) / NATURAL_WALK, CADENCE);
            double runRate = Math.Pow(Math.Max(0.05, pace) / NATURAL_RUN, CADENCE);
            animator.SetFloat(Speed, (float)(moving ? Math.Max(0.4, pace) : pace * moveWeight));
            animator.SetFloat(Cadence, moving ? (float)Math.Min(3, walkRate + (runRate - walkRate) * run) : 1);
            animator.SetBool(Air, air);
            animator.SetBool(Falling, air && w.rise < 0.2);

            double runLean = moveWeight * Smoothstep(0.3, RUN_FROM, pace);
            double lean = Math.Max(-MAX_TILT, Math.Min(MAX_TILT, runLean * RUN_LEAN + accel * ACCEL_LEAN * (1 - airWeight)));
            double bank = Math.Max(-MAX_TILT, Math.Min(MAX_TILT, -turn * BANK * runLean * (1 - airWeight)));
            bodyX = Ease(bodyX, lean, 10, dt);
            bodyZ = Ease(bodyZ, bank, 10, dt);
            body.localRotation = Quaternion.Euler((float)(bodyX * Mathf.Rad2Deg), 0, (float)(-bodyZ * Mathf.Rad2Deg));
            double stretch = 0.12 * Math.Max(0, 1 - sinceTakeoff / 0.18);
            double squash = 0.2 * impact * Math.Sin(Math.PI * Math.Min(1, sinceLanding / 0.2));
            double sy = 1 + stretch - squash;
            float side = (float)(1 / Math.Sqrt(sy));
            body.localScale = new Vector3(side, (float)sy, side);
        }

        ArmWave wave;

        /// <summary>Hold the pose where it is (a town left standing while the next fades in).</summary>
        public void Freeze() => animator.speed = 0;

        /// <summary>Wave (a raised right hand, side to side) while `on`; the arm eases up and back down.</summary>
        public void SetWaving(bool on)
        {
            if (!on && wave == null) return;
            if (wave == null)
            {
                wave = root.AddComponent<ArmWave>();
                if (!wave.Bind(root.transform)) return;
            }
            wave.on = on;
        }

        public void SnapYaw(double yaw)
        {
            rootYaw = yaw;
            root.transform.localRotation = Coords.Yaw(yaw);
        }

        public void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }
}
