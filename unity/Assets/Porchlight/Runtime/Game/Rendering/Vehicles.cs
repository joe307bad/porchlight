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

        public CarView(ModelLibrary.Model model, Transform parent)
        {
            var kind = Catalog.KindOf(model.name);
            scale = ScaleOf(model);
            lift = LiftOf(kind);
            root = Props.Place(model, parent, 0, 0, 0, scale, scale, scale, lift);
            if (kind == VehicleKind.Boat) return;
            if (kind == VehicleKind.Plane && model.name == Catalog.SLEIGH)
            {
                sleigh = new SleighRig(root);
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
    /// reindeer bobbing in its own time, legs galloping while it's on the move; Santa bobbing gently in his
    /// seat, swaying a little; the sack of toys rocking behind him; Rudolph's nose aglow.
    /// </summary>
    public sealed class SleighRig
    {
        /// <summary>How far a reindeer bobs, Santa and the sack (model units, which the sleigh's drawn at about two thirds of a tile).</summary>
        const float DEER_BOB = 0.07f, SANTA_BOB = 0.03f, SACK_BOB = 0.02f;
        /// <summary>A gallop's stride: how often (per second) and how far each leg swings (degrees).</summary>
        const float GALLOP = 2.2f, SWING = 28;
        static readonly Color NoseRed = Coords.Hex(0xff2010);
        static Material noseMaterial;

        readonly List<(Transform t, Vector3 rest, float phase)> deer = new List<(Transform, Vector3, float)>();
        readonly List<(Transform t, Quaternion rest, float phase)> legs = new List<(Transform, Quaternion, float)>();
        readonly Transform santa, sack;
        readonly Vector3 santaRest, sackRest;
        readonly Quaternion santaTurn, sackTurn;
        readonly Renderer nose;
        double lastX, lastY, lastAltitude, lastTime = -1;
        /// <summary>How much it's on the move (0 standing, 1 flying), eased.</summary>
        float going;

        public SleighRig(GameObject root)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string name = t.name;
                // Each reindeer is "reindeer-1" .. "reindeer-9" (its body mesh and nose are under it, named on from that).
                if (name.StartsWith("reindeer-") && int.TryParse(name.Substring("reindeer-".Length), out int k))
                {
                    deer.Add((t, t.localPosition, k * 0.83f));
                    foreach (Transform leg in t)
                    {
                        if (!leg.name.StartsWith("leg-")) continue;
                        // Front legs together, back legs half a stride on; each reindeer a little after the one ahead.
                        bool front = leg.name.StartsWith("leg-f");
                        bool left = leg.name.Length > 5 && leg.name[5] == 'l';
                        legs.Add((leg, leg.localRotation, k * 0.83f + (front ? 0 : Mathf.PI) + (left ? 0.35f : 0)));
                    }
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

        /// <summary>Pose it for now, where it is (map x, y and height): moving, they gallop; standing, they only breathe.</summary>
        public void Animate(double x, double y, double altitude)
        {
            double now = Time.timeAsDouble;
            if (lastTime >= 0)
            {
                double dt = Math.Max(1e-3, now - lastTime);
                double moved = Math.Sqrt((x - lastX) * (x - lastX) + (y - lastY) * (y - lastY) + (altitude - lastAltitude) * (altitude - lastAltitude));
                float target = moved / dt > 0.4 || altitude > 0.3 ? 1 : 0;
                going += (target - going) * (float)Math.Min(1, dt * 2.5);
            }
            lastX = x;
            lastY = y;
            lastAltitude = altitude;
            lastTime = now;
            float t = (float)now;
            float bob = 0.35f + 0.65f * going;
            foreach (var (tr, rest, phase) in deer)
                tr.localPosition = rest + Vector3.up * (DEER_BOB * bob * Mathf.Abs(Mathf.Sin(t * GALLOP * Mathf.PI + phase)));
            foreach (var (tr, rest, phase) in legs)
                tr.localRotation = rest * Quaternion.AngleAxis(SWING * going * Mathf.Sin(t * GALLOP * 2 * Mathf.PI * 0.5f + phase), Vector3.forward);
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
