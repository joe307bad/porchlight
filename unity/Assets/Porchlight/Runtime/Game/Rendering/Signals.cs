// The town's traffic signals: at each signalled junction, for every road in,
// a pole at the kerb by the stop line with a three-lamp head facing the
// oncoming cars, and a white stop bar across their lane. Each frame the lamps
// show what the junction's plan has for that road (Junction.HeadLight) at
// the traffic's clock; at night, where the town flashes its signals, yellow
// on the main roads and red on the side ones, blinking.

using System.Collections.Generic;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Signals
    {
        const float POLE = 0.55f, HEAD = 0.07f, BULB = 0.045f, GLOW = 3f;
        static readonly Color RED = new Color(1f, 0.12f, 0.08f), YELLOW = new Color(1f, 0.72f, 0.1f), GREEN = new Color(0.2f, 1f, 0.45f);

        struct Head
        {
            public Junction junction;
            public int leg;
            public Renderer red, yellow, green;
            public Lamp shown;
            public bool lit;
        }

        readonly Traffic traffic;
        readonly List<Head> heads = new List<Head>();
        readonly GameObject root;
        readonly List<Material> made = new List<Material>();

        public Signals(Traffic traffic, Transform parent)
        {
            this.traffic = traffic;
            root = new GameObject("signals");
            root.transform.SetParent(parent, false);
            var pole = Make(Materials.Lit(0x2b2f33));
            var bar = Make(Materials.Lit(0xe9e6dc));
            var red = Make(Materials.Glowing(new Color(0.25f, 0.05f, 0.04f)));
            var yellow = Make(Materials.Glowing(new Color(0.25f, 0.18f, 0.03f)));
            var green = Make(Materials.Glowing(new Color(0.04f, 0.22f, 0.1f)));
            foreach (var j in traffic.junctions)
            {
                if (j == null || !j.IsSignal) continue;
                for (int leg = 0; leg < j.legs.Count; leg++)
                {
                    var (at, along, width) = traffic.StopLine(j.legs[leg]);
                    if (along.x == 0 && along.y == 0) continue;
                    // The lane's kerb side: the lane sits to (-along.y, along.x) of the road's middle.
                    var kerb = new Porchlight.Core.Vec2(at.x - along.y * (width / 2 + 0.2), at.y + along.x * (width / 2 + 0.2));
                    var post = new GameObject("signal").transform;
                    post.SetParent(root.transform, false);
                    post.localPosition = Coords.Map(kerb.x, kerb.y);
                    Shape(PrimitiveType.Cylinder, post, new Vector3(0, POLE / 2, 0), new Vector3(0.03f, POLE / 2, 0.03f), pole);
                    Shape(PrimitiveType.Cube, post, new Vector3(0, POLE + HEAD * 1.6f, 0), new Vector3(HEAD, HEAD * 3.4f, HEAD), pole);
                    // Its lamps on the side facing the cars coming.
                    var face = new Vector3((float)-along.x, 0, (float)along.y) * (HEAD / 2);
                    var h = new Head { junction = j, leg = leg, shown = Lamp.Off };
                    h.red = Shape(PrimitiveType.Sphere, post, face + new Vector3(0, POLE + HEAD * 2.7f, 0), Vector3.one * BULB, red, false).GetComponent<Renderer>();
                    h.yellow = Shape(PrimitiveType.Sphere, post, face + new Vector3(0, POLE + HEAD * 1.6f, 0), Vector3.one * BULB, yellow, false).GetComponent<Renderer>();
                    h.green = Shape(PrimitiveType.Sphere, post, face + new Vector3(0, POLE + HEAD * 0.5f, 0), Vector3.one * BULB, green, false).GetComponent<Renderer>();
                    heads.Add(h);
                    // The stop bar, across the lane.
                    var stop = Shape(PrimitiveType.Cube, root.transform, Coords.Map(at.x, at.y, 0.012), new Vector3((float)width * 0.95f, 0.004f, 0.07f), bar, false);
                    stop.transform.localRotation = Quaternion.LookRotation(new Vector3((float)along.x, 0, (float)-along.y));
                }
            }
            Update();
        }

        Material Make(Material m)
        {
            made.Add(m);
            return m;
        }

        static GameObject Shape(PrimitiveType type, Transform parent, Vector3 at, Vector3 size, Material material, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Light each head as its junction's plan has it now.</summary>
        public void Update()
        {
            long now = traffic.Now;
            bool flash = Junction.FlashOn(now);
            for (int i = 0; i < heads.Count; i++)
            {
                var h = heads[i];
                var lamp = h.junction.HeadLight(h.leg, now, traffic.night);
                bool lit = lamp != Lamp.FlashRed && lamp != Lamp.FlashYellow || flash;
                if (lamp == h.shown && lit == h.lit) continue;
                h.shown = lamp;
                h.lit = lit;
                bool r = lamp == Lamp.Red || (lamp == Lamp.FlashRed && lit);
                bool y = lamp == Lamp.Yellow || (lamp == Lamp.FlashYellow && lit);
                bool g = lamp == Lamp.Green || lamp == Lamp.Permitted;
                Materials.SetGlow(h.red, RED, r ? GLOW : 0);
                Materials.SetGlow(h.yellow, YELLOW, y ? GLOW : 0);
                Materials.SetGlow(h.green, GREEN, g ? GLOW : 0);
                heads[i] = h;
            }
        }

        public void Dispose()
        {
            Object.Destroy(root);
            foreach (var m in made) Materials.Free(m);
        }
    }
}
