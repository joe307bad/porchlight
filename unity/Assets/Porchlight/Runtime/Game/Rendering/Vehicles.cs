// Cars in the scene: each an instance of its Kenney model, with two
// headlights and two tail lights that glow at night (and bloom, through the
// camera's post-processing). Boats and planes are drawn the same way: a boat
// sits down in the water, a plane has red and green lights on its wingtips and
// banks and pitches as it flies. The Fleet keeps the traffic's cars (and the
// boats sailing the lakes) where the simulation says.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>One drawn car (or boat, or plane): a model with its lamps.</summary>
    public sealed class CarView
    {
        public const double LIFT = 0.035;
        static readonly Color Head = Coords.Hex(0xfff4dc);
        static readonly Color Tail = Coords.Hex(0xff2a1a);
        static readonly Color Port = Coords.Hex(0xff3020);
        static readonly Color Starboard = Coords.Hex(0x40ff70);
        /// <summary>How far down into the water a boat's hull sits.</summary>
        const double DRAUGHT = 0.07;
        static Material headMaterial, tailMaterial, portMaterial, starboardMaterial;

        public readonly GameObject root;
        /// <summary>Which car model (Catalog.CARS) it is, where the owner keeps track.</summary>
        public int model;
        readonly double scale;
        readonly double lift;
        readonly List<Renderer> heads = new List<Renderer>();
        readonly List<Renderer> tails = new List<Renderer>();
        readonly List<Renderer> ports = new List<Renderer>();
        readonly List<Renderer> starboards = new List<Renderer>();
        /// <summary>Santa's sleigh: its reindeer, Santa and the sack, bobbing as it flies.</summary>
        readonly SleighRig sleigh;
        /// <summary>Santa's sleigh's reindeer, Santa and the sack, if it's the sleigh (null for anything else).</summary>
        public SleighRig Sleigh => sleigh;

        public CarView(ModelLibrary.Model model, Transform parent)
        {
            var kind = Catalog.KindOf(model.name);
            scale = ScaleOf(model);
            lift = LiftOf(kind);
            root = Props.Place(model, parent, 0, 0, 0, scale, scale, scale, lift);
            if (kind == VehicleKind.Boat) return;
            if (kind == VehicleKind.Plane && model.name == Catalog.SLEIGH)
            {
                // Drawn ahead of where it is: that's Santa's seat (Catalog.SLEIGH_AHEAD), what it turns and banks about.
                var drawn = root.transform.GetChild(0);
                drawn.localPosition += new Vector3(0, 0, (float)(Catalog.SLEIGH_AHEAD / scale));
                sleigh = new SleighRig(root) { Space = parent };
                return;
            }
            if (kind == VehicleKind.Plane)
            {
                portMaterial ??= Materials.Glowing(Port);
                starboardMaterial ??= Materials.Glowing(Starboard);
                var light = Vector3.one * (float)(0.07 / scale);
                float tip = model.size.x / 2 - 0.03f / (float)scale, wing = model.size.y * 0.4f;
                ports.Add(Lamp(new Vector3(-tip, wing, 0), light, portMaterial));
                starboards.Add(Lamp(new Vector3(tip, wing, 0), light, starboardMaterial));
                return;
            }
            headMaterial ??= Materials.Glowing(Head);
            tailMaterial ??= Materials.Glowing(Tail);
            float halfLength = model.size.z / 2, halfWidth = model.size.x / 2, height = model.size.y * 0.36f;
            float side = Math.Max(0.05f / (float)scale, halfWidth - 0.084f / (float)scale);
            var size = Vector3.one * (float)(0.056 / scale);
            foreach (float s in new[] { side, -side })
            {
                heads.Add(Lamp(new Vector3(s, height, halfLength - 0.012f / (float)scale), size, headMaterial));
                tails.Add(Lamp(new Vector3(s, height, -halfLength + 0.012f / (float)scale), size, tailMaterial));
            }
        }

        Renderer Lamp(Vector3 at, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.name = "lamp";
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        /// <summary>How big a vehicle model is drawn: cars to one length, boats at one scale, each plane its own length.</summary>
        public static double ScaleOf(ModelLibrary.Model model) => Catalog.KindOf(model.name) switch
        {
            VehicleKind.Boat => Catalog.BOAT_SCALE,
            VehicleKind.Plane => Catalog.PlaneLength(model.name) / model.size.z,
            _ => Props.CarScale(model),
        };

        /// <summary>How high it stands: a car on the road, a boat down in the water.</summary>
        public static double LiftOf(VehicleKind kind) => kind == VehicleKind.Boat ? TownWorld.WATER_Y - DRAUGHT : LIFT;

        /// <summary>Put the car at map (x, y) facing heading `rot` (Coords.Yaw); a plane `altitude` up, nose `pitch` up and banked `bank` (radians, + to the right).</summary>
        public void Place(double x, double y, double rot, double altitude = 0, double pitch = 0, double bank = 0)
        {
            root.transform.localPosition = Coords.Map(x, y, lift + altitude);
            // The sleigh turns and climbs about Santa with its team out ahead: it tips and leans only a little, or the
            // reindeer would swing up and down and out to the side of him.
            if (sleigh != null)
            {
                pitch *= SleighRig.PITCH_SHARE;
                bank *= SleighRig.BANK_SHARE;
            }
            var turn = Coords.Yaw(rot);
            if (pitch != 0 || bank != 0) turn *= Quaternion.Euler((float)(-pitch * 180 / Math.PI), 0, (float)(-bank * 180 / Math.PI));
            root.transform.localRotation = turn;
            sleigh?.Animate(x, y, altitude);
        }

        public bool Visible
        {
            get => root.activeSelf;
            set => root.SetActive(value);
        }

        /// <summary>How brightly its lamps shine (0 by day, 1 at night).</summary>
        public void Lights(double dusk)
        {
            float d = (float)dusk;
            foreach (var r in heads) Materials.SetGlow(r, Head, d * 2.5f);
            foreach (var r in tails) Materials.SetGlow(r, Tail, d * 1.8f);
            // Navigation lights are on day and night.
            foreach (var r in ports) Materials.SetGlow(r, Port, 0.8f + d * 1.6f);
            foreach (var r in starboards) Materials.SetGlow(r, Starboard, 0.8f + d * 1.6f);
        }

        public void SetLayer(int layer)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);

        /// <summary>A traffic car's heading (map angle, facing (cos h, sin h)) as Coords.Yaw's.</summary>
        public static double RotOf(double heading) => DetMath.Atan2(Math.Cos(heading), Math.Sin(heading));
    }

    /// <summary>
    /// Santa's sleigh as it goes (Tools~/christmas made it with these as nodes of their own): each of the nine
    /// reindeer galloping, a rotary gallop (the hind legs a beat apart, then the front ones half a stride on),
    /// each leg folding at the knee (in front) or the hock (behind) as it swings forward, the body rocking and
    /// rising through each stride, every reindeer a little out of step with the rest; down on a roof, those with
    /// roof under them stand on it and only breathe, the rest paddle the air; Santa bobbing gently in his seat
    /// (and gone from it while you're out of it); the sack rocking behind him; Rudolph's nose aglow.
    /// </summary>
    public sealed class SleighRig
    {
        /// <summary>How much of a plane's nose-up and lean into a turn the sleigh shows.</summary>
        public const double PITCH_SHARE = 0.3, BANK_SHARE = 0.55;
        /// <summary>How far a reindeer rises through a stride and rocks (model units, degrees), and Santa and the sack bob.</summary>
        const float DEER_RISE = 0.06f, DEER_ROCK = 6, SANTA_BOB = 0.025f, SACK_BOB = 0.02f;
        /// <summary>Strides a second, galloping flat out and paddling the air standing still.</summary>
        const float STRIDE = 1.9f, PADDLE = 0.9f;
        /// <summary>How far each leg swings either way from the hip (degrees), and folds at the knee or hock as it comes forward.</summary>
        const float SWING = 30, KNEE_FOLD = 70, HOCK_FOLD = 50;
        static readonly Color NoseRed = Coords.Hex(0xff2010);
        static Material noseMaterial;

        sealed class Leg
        {
            public Transform hip, knee;
            public Quaternion hipRest, kneeRest;
            /// <summary>Turned about these (their own), a positive angle swings the hoof forward.</summary>
            public Vector3 hipAxis, kneeAxis;
            public bool front;
            /// <summary>Where in the stride it's furthest forward (a share of it).</summary>
            public float phase;
        }

        sealed class Deer
        {
            public Transform t;
            public Vector3 rest;
            public Quaternion turn;
            public Vector3 rockAxis;
            /// <summary>How far out of step with the team it is (a share of a stride).</summary>
            public float offset;
            /// <summary>How far it's raised (or lowered) to stand on the roof under it (model units), eased; how much it's standing (0 galloping, 1 stood).</summary>
            public float lift, stood;
            public readonly List<Leg> legs = new List<Leg>();
        }

        readonly GameObject root;
        readonly List<Deer> deer = new List<Deer>();
        readonly Transform santa, sack;
        readonly Vector3 santaRest, sackRest;
        readonly Quaternion santaTurn, sackTurn;
        readonly Renderer nose;
        double lastX, lastY, lastAltitude, lastTime = -1;
        /// <summary>How much it's on the move (0 standing, 1 flying), eased; how far through a stride the team is.</summary>
        float going, stride;

        /// <summary>
        /// Down somewhere, how high the ground (or roof) is at a map point (NaN: none there, the air): the reindeer over
        /// some stand on it. Null: in the air, they gallop. `Space` is the frame map points are in (Coords.Map's).
        /// </summary>
        public Func<double, double, double> Ground;
        public Transform Space;

        /// <summary>Santa in his seat (you, at the reins); not while you're out of it.</summary>
        public bool SantaAboard
        {
            set
            {
                if (santa != null && santa.gameObject.activeSelf != value) santa.gameObject.SetActive(value);
            }
        }

        /// <summary>Where Santa is drawn, and the middle of everything drawn (the sleigh and its team: world space).</summary>
        public Vector3 SantaAt => santa != null ? santa.position : root.transform.position;
        public Vector3 Middle
        {
            get
            {
                Bounds? all = null;
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (all == null) all = r.bounds;
                    else
                    {
                        var b = all.Value;
                        b.Encapsulate(r.bounds);
                        all = b;
                    }
                }
                return all?.center ?? root.transform.position;
            }
        }
        readonly Renderer[] renderers;

        public SleighRig(GameObject root)
        {
            renderers = root.GetComponentsInChildren<Renderer>(true);
            this.root = root;
            var all = root.GetComponentsInChildren<Transform>(true);
            // The model's own up and forward (its nose is down the holder's +z), in each part's own terms below.
            Vector3 up = root.transform.up, forward = root.transform.forward;
            foreach (var t in all)
            {
                string name = t.name;
                // Each reindeer is "reindeer-1" .. "reindeer-9" (its body mesh and nose under it, named on from that).
                if (name.StartsWith("reindeer-") && int.TryParse(name.Substring("reindeer-".Length), out int k))
                {
                    var d = new Deer { t = t, rest = t.localPosition, turn = t.localRotation, offset = ((k * 37) % 9) / 9f * 0.18f };
                    // Nose up: turning forward towards up.
                    d.rockAxis = Vector3.Cross(t.InverseTransformDirection(forward), t.InverseTransformDirection(up));
                    foreach (Transform hip in t)
                    {
                        if (!hip.name.StartsWith("leg-")) continue;
                        bool front = hip.name.StartsWith("leg-f");
                        bool left = hip.name.Length > 5 && hip.name[5] == 'l';
                        Transform knee = null;
                        foreach (Transform c in hip) if (c.name.StartsWith("knee-")) knee = c;
                        var leg = new Leg
                        {
                            hip = hip,
                            hipRest = hip.localRotation,
                            knee = knee,
                            kneeRest = knee != null ? knee.localRotation : Quaternion.identity,
                            front = front,
                            // The rotary gallop: left hind, right hind a beat on, then left fore and right fore half a stride after.
                            phase = front ? (left ? 0.5f : 0.6f) : (left ? 0f : 0.1f),
                        };
                        // Down crossed with forward: about it, a positive turn swings what hangs down forward.
                        leg.hipAxis = Vector3.Cross(hip.InverseTransformDirection(-up), hip.InverseTransformDirection(forward)).normalized;
                        if (knee != null) leg.kneeAxis = Vector3.Cross(knee.InverseTransformDirection(-up), knee.InverseTransformDirection(forward)).normalized;
                        d.legs.Add(leg);
                    }
                    d.rockAxis.Normalize();
                    deer.Add(d);
                }
                else if (name == "santa")
                {
                    santa = t;
                    santaRest = t.localPosition;
                    santaTurn = t.localRotation;
                }
                else if (name == "sack")
                {
                    sack = t;
                    sackRest = t.localPosition;
                    sackTurn = t.localRotation;
                }
                else if (name == "rudolph-nose")
                {
                    nose = t.GetComponentInChildren<Renderer>();
                    if (nose != null)
                    {
                        noseMaterial ??= Materials.Glowing(NoseRed);
                        nose.sharedMaterial = noseMaterial;
                        nose.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                }
            }
        }

        /// <summary>Pose it for now, where it is (map x, y and height): on the move, they gallop; down, they stand (or paddle the air).</summary>
        public void Animate(double x, double y, double altitude)
        {
            double now = Time.timeAsDouble;
            float dt = 0;
            if (lastTime >= 0)
            {
                dt = (float)Math.Min(0.1, Math.Max(0, now - lastTime));
                double moved = Math.Sqrt((x - lastX) * (x - lastX) + (y - lastY) * (y - lastY) + (altitude - lastAltitude) * (altitude - lastAltitude));
                float target = dt > 0 && moved / dt > 0.3 ? 1 : 0;
                going += (target - going) * Math.Min(1, dt * 2.5f);
            }
            lastX = x;
            lastY = y;
            lastAltitude = altitude;
            lastTime = now;
            stride = (stride + dt * Mathf.Lerp(PADDLE, STRIDE, going)) % 1;
            float t = (float)now;
            bool down = Ground != null && Space != null && going < 0.5f;
            foreach (var d in deer)
            {
                // Down, with roof under it: stand on it. Otherwise up in the air, galloping (or paddling, standing still).
                float wantLift = 0, wantStood = 0;
                if (down)
                {
                    var at = Space.InverseTransformPoint(d.t.parent.TransformPoint(d.rest));
                    double g = Ground(at.x, -at.z);
                    float scale = Math.Max(1e-4f, d.t.parent.lossyScale.y / Math.Max(1e-4f, Space.lossyScale.y));
                    if (!double.IsNaN(g) && Math.Abs(g - at.y) < 0.6)
                    {
                        wantLift = (float)((g - at.y) / scale);
                        wantStood = 1;
                    }
                }
                float ease = Math.Min(1, dt * 4);
                d.lift += (wantLift - d.lift) * ease;
                d.stood += (wantStood - d.stood) * ease;
                float gait = (1 - d.stood) * Mathf.Lerp(0.55f, 1, going);
                float cycle = (stride + d.offset) * Mathf.PI * 2;
                // Up through each stride (highest as the forelegs leave the ground), and rocking nose up and down with it.
                float rise = DEER_RISE * gait * 0.5f * (1 - Mathf.Cos(cycle - 0.8f * Mathf.PI * 2));
                float rock = DEER_ROCK * gait * Mathf.Sin(cycle - 0.3f * Mathf.PI * 2);
                // Stood, it breathes.
                float breathe = 0.006f * d.stood * Mathf.Sin(t * 1.6f + d.offset * 20);
                d.t.localPosition = d.rest + Vector3.up * (d.lift + rise + breathe);
                d.t.localRotation = d.turn * Quaternion.AngleAxis(rock, d.rockAxis);
                foreach (var leg in d.legs)
                {
                    float psi = cycle - leg.phase * Mathf.PI * 2;
                    // Furthest forward at its phase; folding at the joint as it swings forward, straight as it pushes back.
                    float swing = SWING * gait * Mathf.Cos(psi) + (leg.front ? 4 : -4) * gait;
                    float fold = gait * Mathf.Max(0, -Mathf.Sin(psi));
                    leg.hip.localRotation = leg.hipRest * Quaternion.AngleAxis(swing, leg.hipAxis);
                    if (leg.knee != null)
                        leg.knee.localRotation = leg.kneeRest * Quaternion.AngleAxis(leg.front ? -KNEE_FOLD * fold : HOCK_FOLD * fold, leg.kneeAxis);
                }
            }
            if (santa != null)
            {
                santa.localPosition = santaRest + Vector3.up * (SANTA_BOB * (0.5f + 0.5f * going) * Mathf.Sin(t * 2.1f));
                santa.localRotation = santaTurn * Quaternion.Euler(0, 0, 3 * Mathf.Sin(t * 1.3f));
            }
            if (sack != null)
            {
                sack.localPosition = sackRest + Vector3.up * (SACK_BOB * going * Mathf.Abs(Mathf.Sin(t * 2.1f + 0.9f)));
                sack.localRotation = sackTurn * Quaternion.Euler(4 * going * Mathf.Sin(t * 1.7f), 0, 0);
            }
            if (nose != null) Materials.SetGlow(nose, NoseRed, 2.2f + 0.8f * Mathf.Sin(t * 3));
        }
    }

    public sealed class Fleet
    {
        readonly Traffic traffic;
        readonly List<CarView> views = new List<CarView>();
        readonly GameObject root;

        public Fleet(Traffic traffic, ModelLibrary library, Transform parent)
        {
            this.traffic = traffic;
            root = new GameObject("traffic");
            root.transform.SetParent(parent, false);
            foreach (var c in traffic.cars) views.Add(new CarView(library.Get(Catalog.CARS[c.model]), root.transform));
            Update(0);
        }

        /// <summary>Put the cars where the traffic has them: all but those taken, and those `hidden` says not to draw here.</summary>
        public void Update(double dusk, Func<double, double, bool> hidden = null)
        {
            for (int i = 0; i < views.Count; i++)
            {
                var c = traffic.cars[i];
                var v = views[i];
                v.Visible = !c.taken && (hidden == null || !hidden(c.x, c.y));
                if (!v.Visible) continue;
                v.Place(c.x, c.y, CarView.RotOf(c.heading));
                v.Lights(dusk);
            }
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
    }
}
