// The zoo, laid out in its own (u along its front, v out through the gate:
// Menagerie) frame: the fence round it (the yards' fence model) with a gap for
// the gate, posts either side under an arch with its name on, a path in and
// one across, the pens between them (each a habitat: WorldExhibits.cs), and
// lanterns along the paths that light up at dusk. The fences are solid to walk
// into, but for a pen's gate, which opens for its own animal. The animals amble about their pens (made
// and moved by Creature): drawn here, stepped each frame by the view.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    public sealed partial class TownWorld
    {
        static class Z
        {
            public const int path = 0xc9a46e, sand = 0xe3cf98, dirt = 0xa88a5c, snow = 0xeef3f6, pool = 0x5fa8d8, rim = 0xd9d6cc;
            public const int rail = 0x8a6440, post = 0x6b4b30, pillar = 0xc2b49a, arch = 0x2f5d3a, lamp = 0x2c3a31;
            public const int grass = 0x86a653, jungle = 0x5f8f47, dryGrass = 0xbcae62, mud = 0x6e5233, ice = 0xcfe8f5, iron = 0x34383d;
            public const int leaf = 0x6c9440, leafLight = 0x86ab4d, bamboo = 0x7fb04f, bambooDark = 0x568a3a, hay = 0xd8bf68, stone = 0xa9a79f, rope = 0xc9b48a, tyre = 0x2b2b2d, roof = 0xa4553a;
        }

        const double PEN_RAIL = 0.26;
        const double PILLAR_HEIGHT = 0.95;

        /// <summary>One of the zoo's animals, ambling about its pen.</summary>
        public sealed class ZooAnimal
        {
            public Walker walker;
            public Avatar avatar;
            /// <summary>Where it may go (map rectangle), and where it's going.</summary>
            public (double x0, double y0, double x1, double y1) pen;
            /// <summary>What's in its pen to keep off (rocks and trees).</summary>
            public List<(Vec2 at, double r)> keep;
            public Vec2? to;
            public double rest;
            public double pace;
        }

        public readonly List<ZooAnimal> animals = new List<ZooAnimal>();
        Rng zooRng;

        void AddZoo()
        {
            var z = model.zoo;
            if (z == null) return;
            zooRng = new Rng($"{model.seed}::zoo");
            var b = new MeshBuilder(palette);
            V3 At(double u, double v, double h)
            {
                var p = Menagerie.ToWorld(z, u, v);
                return v3(p.x, h, p.y);
            }
            void Flat(Airfield.Box x, double h, int color) => b.Quad(At(x.u0, x.v0, h), At(x.u1, x.v0, h), At(x.u1, x.v1, h), At(x.u0, x.v1, h), color);
            void Block(Airfield.Box x, double h0, double h1, int color)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, x);
                b.Box(x0, y0, x1, y1, h0, h1, color);
            }
            void Solid(Airfield.Box x)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, x);
                solids.Add(new Rect(x0, y0, x1, y1));
            }

            foreach (var p in Menagerie.Paths()) Flat(p, 0.006, Z.path);

            // The pens: each a habitat for who lives there, with its own kind of fence and a gate on the path.
            foreach (var pen in Menagerie.Pens())
            {
                var keep = BuildExhibit(z, b, pen);
                Stock(z, pen, keep);
            }
            PathSides(z, b);

            // The fence round it, a tile at a time, but for the gate.
            var fence = library.Get("suburban/fence");
            double fsx = 1 / fence.size.x;
            double inset = Menagerie.FENCE_IN;
            double mid = Menagerie.LENGTH / 2.0, depth = Menagerie.DEPTH;
            var du = Menagerie.AlongU(z);
            double turnU = DetMath.Atan2(du.x, du.y) - Math.PI / 2, turnV = turnU + Math.PI / 2;
            for (int k = 0; k < Menagerie.LENGTH; k++)
            {
                double u = k + 0.5;
                var back = Menagerie.ToWorld(z, u, inset);
                Props.Place(fence, props, back.x, back.y, turnU, fsx, 1.3, 1.3);
                if (Math.Abs(u - mid) < Menagerie.GATE_HALF) continue;
                var front = Menagerie.ToWorld(z, u, depth - inset);
                Props.Place(fence, props, front.x, front.y, turnU, fsx, 1.3, 1.3);
            }
            for (int k = 0; k < Menagerie.DEPTH; k++)
                foreach (double u in new[] { inset, Menagerie.LENGTH - inset })
                {
                    var at = Menagerie.ToWorld(z, u, k + 0.5);
                    Props.Place(fence, props, at.x, at.y, turnV, fsx, 1.3, 1.3);
                }
            const double ft = 0.04;
            Solid(new Airfield.Box(0, inset - ft, Menagerie.LENGTH, inset + ft));
            Solid(new Airfield.Box(inset - ft, 0, inset + ft, depth));
            Solid(new Airfield.Box(Menagerie.LENGTH - inset - ft, 0, Menagerie.LENGTH - inset + ft, depth));
            Solid(new Airfield.Box(0, depth - inset - ft, mid - Menagerie.GATE_HALF, depth - inset + ft));
            Solid(new Airfield.Box(mid + Menagerie.GATE_HALF, depth - inset - ft, Menagerie.LENGTH, depth - inset + ft));

            // The gate: a post either side and an arch over it with the zoo's name.
            double ph = 0.09;
            foreach (double u in new[] { mid - Menagerie.GATE_HALF - ph, mid + Menagerie.GATE_HALF + ph })
            {
                var post = new Airfield.Box(u - ph, depth - inset - ph, u + ph, depth - inset + ph);
                Block(post, 0, PILLAR_HEIGHT, Z.pillar);
                Block(new Airfield.Box(post.u0 - 0.02, post.v0 - 0.02, post.u1 + 0.02, post.v1 + 0.02), PILLAR_HEIGHT, PILLAR_HEIGHT + 0.04, Z.arch);
                Solid(post);
            }
            var beam = new Airfield.Box(mid - Menagerie.GATE_HALF - ph * 2, depth - inset - 0.04, mid + Menagerie.GATE_HALF + ph * 2, depth - inset + 0.04);
            Block(beam, PILLAR_HEIGHT - 0.28, PILLAR_HEIGHT - 0.02, Z.arch);
            b.Build("zoo", still);
            GateSign(z, beam, PILLAR_HEIGHT - 0.15);
            ZooLamps(z);
        }

        /// <summary>
        /// Lanterns along the paths, between them and the pens: a pair inside the gate, and
        /// more down the path in and along the one across, taking turns side to side. They
        /// light up at dusk with the street lamps (they're among them), and none is faulty.
        /// </summary>
        void ZooLamps(Porchlight.Model.Zoo z)
        {
            const double H = 0.62, OFF = 0.12;
            var paths = Menagerie.Paths();
            var inPath = paths[0];
            var across = paths[1];
            var spots = new[]
            {
                (inPath.u0 - OFF, Menagerie.DEPTH - 0.75), (inPath.u1 + OFF, Menagerie.DEPTH - 0.75),
                (inPath.u1 + OFF, 7.3), (inPath.u0 - OFF, 2.7),
                // Along the way across, clear of the pens' gates and signs and the benches.
                (1.0, across.v1 + OFF), (3.4, across.v0 - OFF), (7.2, across.v1 + OFF), (9.7, across.v0 - OFF),
            };
            var post = Materials.Lit(Z.lamp);
            var bulbMaterial = Materials.Glowing(Coords.Hex(0xfff3d6));
            var poolMaterial = Materials.LightPool();
            var glowRoot = new GameObject("zoo lamps").transform;
            glowRoot.SetParent(root.transform, false);
            foreach (var (u, v) in spots)
            {
                var at = Menagerie.ToWorld(z, u, v);
                var lamp = new GameObject("zoo lamp").transform;
                lamp.SetParent(props, false);
                lamp.localPosition = Coords.Map(at.x, at.y);
                // A foot, a slim post, and over the lantern a little roof with a finial.
                Shape(PrimitiveType.Cube, lamp, new Vector3(0, 0.03f, 0), new Vector3(0.075f, 0.06f, 0.075f), post);
                Shape(PrimitiveType.Cylinder, lamp, new Vector3(0, (float)H / 2, 0), new Vector3(0.04f, (float)H / 2, 0.04f), post);
                Shape(PrimitiveType.Cube, lamp, new Vector3(0, (float)H + 0.005f, 0), new Vector3(0.07f, 0.02f, 0.07f), post);
                Shape(PrimitiveType.Cube, lamp, new Vector3(0, (float)H + 0.105f, 0), new Vector3(0.12f, 0.025f, 0.12f), post);
                Shape(PrimitiveType.Sphere, lamp, new Vector3(0, (float)H + 0.135f, 0), Vector3.one * 0.035f, post);
                var bulb = Shape(PrimitiveType.Sphere, glowRoot, Coords.Map(at.x, at.y, H + 0.055), new Vector3(0.085f, 0.1f, 0.085f), bulbMaterial, false, "bulb");
                var pool = Shape(PrimitiveType.Quad, glowRoot, Coords.Map(at.x, at.y, 0.045), new Vector3(1.7f, 1.7f, 1), poolMaterial, false, "light pool");
                pool.transform.localRotation = Quaternion.Euler(90, 0, 0);
                lamps.Add(new Lamp { x = at.x, y = at.y, h = H + 0.055, phase = zooRng.Range(0, 100), bulb = bulb.GetComponent<Renderer>(), pool = pool.GetComponent<Renderer>() });
                solids.Add(new Rect(at.x - 0.04, at.y - 0.04, at.x + 0.04, at.y + 0.04));
            }
        }

        /// <summary>The zoo's name on both faces of the arch over its gate.</summary>
        void GateSign(Porchlight.Model.Zoo z, Airfield.Box beam, double height)
        {
            var dv = Menagerie.AlongV(z);
            var mid = Menagerie.ToWorld(z, beam.MidU, beam.MidV);
            foreach (int face in new[] { 1, -1 })
            {
                var go = new GameObject("zoo sign");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = Coords.Map(mid.x + dv.x * face * 0.045, mid.y + dv.y * face * 0.045, height);
                // The text reads facing out of its face.
                go.transform.localRotation = Quaternion.LookRotation(Coords.Map(-dv.x * face, -dv.y * face) - Coords.Map(0, 0), Vector3.up);
                var text = go.AddComponent<TextMeshPro>();
                text.font = library.font;
                text.text = "ZOO";
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.Center;
                text.enableAutoSizing = true;
                text.fontSizeMin = 0.1f;
                text.fontSizeMax = 2f;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.rectTransform.sizeDelta = new Vector2((float)((beam.u1 - beam.u0) * 0.8), 0.22f);
                go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        /// <summary>The pen's animals, each somewhere in it.</summary>
        void Stock(Porchlight.Model.Zoo z, Menagerie.Pen pen, List<(Vec2 at, double r)> keep)
        {
            var (x0, y0, x1, y1) = Menagerie.Rect(z, pen.box);
            const double inset = 0.25;
            var room = (x0 + inset, y0 + inset, x1 - inset, y1 - inset);
            foreach (var (animal, count) in pen.animals)
            {
                for (int k = 0; k < count; k++)
                {
                    var start = Roam(room, keep);
                    var walker = new Walker(animal, start.x, start.y) { heading = zooRng.Range(0, Math.PI * 2) };
                    var avatar = Avatar.Of(animal, zooRng.Next(), root.transform);
                    avatar.SnapYaw(walker.heading);
                    animals.Add(new ZooAnimal { walker = walker, avatar = avatar, pen = room, keep = keep, rest = zooRng.Range(0, 3), pace = Catalog.AnimalPace(animal) });
                }
            }
        }

        /// <summary>Somewhere in a pen, clear of its rocks and trees if it can be.</summary>
        Vec2 Roam((double x0, double y0, double x1, double y1) pen, List<(Vec2 at, double r)> keep)
        {
            var p = default(Vec2);
            for (int tries = 0; tries < 8; tries++)
            {
                p = new Vec2(zooRng.Range(pen.x0, pen.x1), zooRng.Range(pen.y0, pen.y1));
                if (!keep.Any(k => Geo.Dist(k.at, p) < k.r)) break;
            }
            return p;
        }

        /// <summary>The animals amble: to somewhere in their pen, a rest, somewhere else. And each pen's gate lets its own animal (you, if that's what you are) in, and anyone inside out.</summary>
        public void UpdateZoo(double dt, string look = null, Vec2? you = null)
        {
            UpdatePenGates(dt, look, you);
            foreach (var a in animals)
            {
                var w = a.walker;
                if (a.to == null)
                {
                    w.Moved(0, 0, dt);
                    a.rest -= dt;
                    if (a.rest <= 0) a.to = Roam(a.pen, a.keep);
                }
                else
                {
                    var to = a.to.Value;
                    double dx = to.x - w.x, dy = to.y - w.y, d = Numbers.Hypot(dx, dy), step = a.pace * dt;
                    if (d <= step)
                    {
                        w.Moved(dx, dy, dt);
                        w.x = to.x;
                        w.y = to.y;
                        a.to = null;
                        a.rest = zooRng.Range(1.5, 6);
                    }
                    else
                    {
                        w.Moved(dx / d * step, dy / d * step, dt);
                        w.x += dx / d * step;
                        w.y += dy / d * step;
                    }
                }
                a.avatar.Update(w, dt);
            }
        }
    }
}
