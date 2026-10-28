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

        public CarView(ModelLibrary.Model model, Transform parent)
        {
            var kind = Catalog.KindOf(model.name);
            scale = ScaleOf(model);
            lift = LiftOf(kind);
            root = Props.Place(model, parent, 0, 0, 0, scale, scale, scale, lift);
            if (kind == VehicleKind.Boat) return;
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

        public void Update(double dusk)
        {
            for (int i = 0; i < views.Count; i++)
            {
                var c = traffic.cars[i];
                var v = views[i];
                v.Visible = !c.taken;
                if (c.taken) continue;
                v.Place(c.x, c.y, CarView.RotOf(c.heading));
                v.Lights(dusk);
            }
        }

        public void Dispose() => UnityEngine.Object.Destroy(root);
    }
}
