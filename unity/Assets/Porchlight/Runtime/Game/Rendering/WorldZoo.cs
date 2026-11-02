// The zoo, laid out in its own (u along its front, v out through the gate:
// Menagerie) frame: the fence round it (the yards' fence model) with a gap for
// the gate, posts either side under an arch with its name on, a path in and
// one across, and pens of rails between them, each floored for its animals
// (sand, dirt or snow, the penguins' and polar bears' round a pool), a rock or
// a bush in each. The fences are solid to walk into. The animals amble about
// their pens (Kenney's Cube Pets, playing their own walk and idle): drawn
// here, stepped each frame by the view.

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
            public const int rail = 0x8a6440, post = 0x6b4b30, pillar = 0xc2b49a, arch = 0x2f5d3a;
        }

        const double PEN_RAIL = 0.22;
        const double PILLAR_HEIGHT = 0.95;

        /// <summary>One of the zoo's animals, ambling about its pen.</summary>
        public sealed class ZooAnimal
        {
            public Walker walker;
            public Avatar avatar;
            /// <summary>Where it may go (map rectangle), and where it's going.</summary>
            public (double x0, double y0, double x1, double y1) pen;
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

            // The pens: a floor for who lives there, rails all round, and a pool or a rock.
            foreach (var pen in Menagerie.Pens())
            {
                var x = pen.box;
                string first = pen.animals[0].animal;
                bool cold = pen.pool;
                bool big = first.EndsWith("giraffe") || first.EndsWith("elephant");
                Flat(x, 0.005, cold ? Z.snow : big ? Z.dirt : Z.sand);
                if (pen.pool)
                {
                    var pool = Menagerie.Pool(pen);
                    Flat(new Airfield.Box(pool.u0 - 0.06, pool.v0 - 0.06, pool.u1 + 0.06, pool.v1 + 0.06), 0.012, Z.rim);
                    Flat(pool, 0.016, Z.pool);
                }
                const double t = 0.025;
                foreach (var side in new[]
                {
                    new Airfield.Box(x.u0, x.v0 - t, x.u1, x.v0 + t), new Airfield.Box(x.u0, x.v1 - t, x.u1, x.v1 + t),
                    new Airfield.Box(x.u0 - t, x.v0, x.u0 + t, x.v1), new Airfield.Box(x.u1 - t, x.v0, x.u1 + t, x.v1),
                })
                {
                    Block(new Airfield.Box(side.u0, side.v0, side.u1, side.v1), PEN_RAIL * 0.45, PEN_RAIL * 0.55, Z.rail);
                    Block(new Airfield.Box(side.u0, side.v0, side.u1, side.v1), PEN_RAIL * 0.88, PEN_RAIL, Z.rail);
                    Solid(side);
                }
                for (double u = x.u0; u <= x.u1 + 1e-6; u += (x.u1 - x.u0) / Math.Max(1, Math.Round((x.u1 - x.u0) / 0.5)))
                    foreach (double v in new[] { x.v0, x.v1 }) Block(new Airfield.Box(u - 0.03, v - 0.03, u + 0.03, v + 0.03), 0, PEN_RAIL + 0.03, Z.post);
                for (double v = x.v0; v <= x.v1 + 1e-6; v += (x.v1 - x.v0) / Math.Max(1, Math.Round((x.v1 - x.v0) / 0.5)))
                    foreach (double u in new[] { x.u0, x.u1 }) Block(new Airfield.Box(u - 0.03, v - 0.03, u + 0.03, v + 0.03), 0, PEN_RAIL + 0.03, Z.post);
                Decorate(z, pen);
                Stock(z, pen);
            }

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

        /// <summary>A rock, a bush or a tree in a pen, away from its pool.</summary>
        void Decorate(Porchlight.Model.Zoo z, Menagerie.Pen pen)
        {
            var x = pen.box;
            string first = pen.animals[0].animal;
            bool tall = first.EndsWith("giraffe") || first.EndsWith("monkey") || first.EndsWith("koala");
            var pool = pen.pool ? Menagerie.Pool(pen) : (Airfield.Box?)null;
            double u = pool != null ? (pool.Value.MidU < x.MidU ? x.u1 - 0.45 : x.u0 + 0.45) : x.u0 + 0.5;
            var at = Menagerie.ToWorld(z, u, x.v0 + 0.5);
            if (tall)
            {
                var t = library.Get(zooRng.Pick(Catalog.TREES));
                double s = 1.6 / t.size.y;
                Props.Place(t, props, at.x, at.y, zooRng.Range(0, Math.PI * 2), s, s, s);
            }
            else
            {
                var r = library.Get(zooRng.Pick(Catalog.BOULDERS));
                double s = 0.45 / Math.Max(r.size.x, r.size.z);
                Props.Place(r, props, at.x, at.y, zooRng.Range(0, Math.PI * 2), s, s, s);
            }
            var bush = library.Get(zooRng.Pick(Catalog.BUSHES));
            var b = Menagerie.ToWorld(z, x.MidU, x.v1 - 0.3);
            double bs = 0.35 / Math.Max(bush.size.x, bush.size.z);
            Props.Place(bush, props, b.x, b.y, zooRng.Range(0, Math.PI * 2), bs, bs, bs, castShadows: false);
        }

        /// <summary>The pen's animals, each somewhere in it.</summary>
        void Stock(Porchlight.Model.Zoo z, Menagerie.Pen pen)
        {
            var (x0, y0, x1, y1) = Menagerie.Rect(z, pen.box);
            const double inset = 0.25;
            var room = (x0 + inset, y0 + inset, x1 - inset, y1 - inset);
            foreach (var (animal, count) in pen.animals)
            {
                for (int k = 0; k < count; k++)
                {
                    var walker = new Walker(animal, zooRng.Range(room.Item1, room.Item3), zooRng.Range(room.Item2, room.Item4)) { heading = zooRng.Range(0, Math.PI * 2) };
                    var avatar = new Avatar(library.Character(animal), Avatar.HeightOf(animal), zooRng.Next(), root.transform);
                    avatar.SnapYaw(walker.heading);
                    animals.Add(new ZooAnimal { walker = walker, avatar = avatar, pen = room, rest = zooRng.Range(0, 3), pace = Catalog.AnimalPace(animal) });
                }
            }
        }

        /// <summary>The animals amble: to somewhere in their pen, a rest, somewhere else.</summary>
        public void UpdateZoo(double dt)
        {
            foreach (var a in animals)
            {
                var w = a.walker;
                if (a.to == null)
                {
                    w.Moved(0, 0, dt);
                    a.rest -= dt;
                    if (a.rest <= 0) a.to = new Vec2(zooRng.Range(a.pen.x0, a.pen.x1), zooRng.Range(a.pen.y0, a.pen.y1));
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
