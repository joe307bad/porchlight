// Cars in the scene: each an instance of its Kenney model, with two
// headlights and two tail lights that glow at night (and bloom, through the
// camera's post-processing). The Fleet keeps the traffic's cars where the
// simulation says.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>One drawn car: a model with its lamps.</summary>
    public sealed class CarView
    {
        public const double LIFT = 0.035;
        static readonly Color Head = Coords.Hex(0xfff4dc);
        static readonly Color Tail = Coords.Hex(0xff2a1a);
        static Material headMaterial, tailMaterial;

        public readonly GameObject root;
        readonly double scale;
        readonly List<Renderer> heads = new List<Renderer>();
        readonly List<Renderer> tails = new List<Renderer>();

        public CarView(ModelLibrary.Model model, Transform parent)
        {
            scale = Props.CarScale(model);
            root = Props.Place(model, parent, 0, 0, 0, scale, scale, scale, LIFT);
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

        /// <summary>Put the car at map (x, y) facing heading `rot` (Coords.Yaw).</summary>
        public void Place(double x, double y, double rot)
        {
            root.transform.localPosition = Coords.Map(x, y, LIFT);
            root.transform.localRotation = Coords.Yaw(rot);
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
