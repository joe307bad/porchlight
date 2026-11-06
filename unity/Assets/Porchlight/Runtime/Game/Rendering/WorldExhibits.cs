// The zoo's exhibits, each a habitat for who lives there and not just a floor
// in a fence: the lions' savanna with its pride rock and acacia, the tigers'
// jungle and stream, the giraffes' tall feeder and shelter, the elephants' mud
// wallow, the monkeys' climbing frame and tyre swing, the pandas' bamboo, and
// the penguins' and polar bears' snow, with ice floes on their pools. Bars keep
// the big cats in, a low wall and glass the cold-weather animals, rails the
// rest. Each has a gate on the path that opens for its own animal, and a sign;
// the paths get benches and a planter. Places in an exhibit are given as
// (a, g), its own shares: a from the side away from the path in (where a pool
// is) to the side nearest it, g back from its gate.

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
        /// <summary>A pen's gate: who it opens for, and its leaf swinging on its hinge.</summary>
        public sealed class PenGate
        {
            public Menagerie.Pen pen;
            public Solids.Door door;
            public Vec2 at;
            public Transform hinge;
            public Quaternion closed;
            public float swing, open;
        }

        public readonly List<PenGate> penGates = new List<PenGate>();

        const double GATE_OPENS_WITHIN = 1.1;

        /// <summary>A pen's gate opens as its own animal comes up to it (from outside or in), and for anyone at all to get out.</summary>
        void UpdatePenGates(double dt, string look, Vec2? you)
        {
            foreach (var g in penGates)
            {
                bool wants = false;
                if (you != null && Geo.Dist(you.Value, g.at) < GATE_OPENS_WITHIN)
                    wants = (look != null && g.pen.animals.Any(a => a.animal == look)) || Menagerie.InPen(model.zoo, g.pen, you.Value);
                g.door.open = wants;
                g.open = Mathf.MoveTowards(g.open, wants ? 1 : 0, (float)dt * 2.5f);
                float k = g.open * g.open * (3 - 2 * g.open);
                g.hinge.localRotation = Quaternion.AngleAxis(g.swing * k, Vector3.up) * g.closed;
            }
        }

        /// <summary>The look of a pen's fence.</summary>
        enum Fencing { Rails, Bars, Glass }

        /// <summary>Puts things in one pen, by its own shares.</summary>
        sealed class Exhibit
        {
            readonly TownWorld w;
            readonly Porchlight.Model.Zoo z;
            readonly MeshBuilder b;
            readonly Airfield.Box x;
            readonly bool gateHigh, outerLow;
            public readonly List<(Vec2 at, double r)> keep = new List<(Vec2, double)>();

            public Exhibit(TownWorld w, Porchlight.Model.Zoo z, MeshBuilder b, Menagerie.Pen pen)
            {
                this.w = w;
                this.z = z;
                this.b = b;
                x = pen.box;
                gateHigh = Menagerie.PenGateOut(pen) > 0;
                outerLow = x.MidU < Menagerie.LENGTH / 2.0;
            }

            double U(double a) => outerLow ? x.u0 + a * (x.u1 - x.u0) : x.u1 - a * (x.u1 - x.u0);
            double V(double g) => gateHigh ? x.v1 - g * (x.v1 - x.v0) : x.v0 + g * (x.v1 - x.v0);
            public Vec2 Map(double a, double g) => Menagerie.ToWorld(z, U(a), V(g));
            Airfield.Box Around(double a, double g, double hu, double hv) => new Airfield.Box(U(a) - hu, V(g) - hv, U(a) + hu, V(g) + hv);

            /// <summary>A box standing here, hu and hv either side of it, from h0 up to h1.</summary>
            public void Block(double a, double g, double hu, double hv, double h0, double h1, int color)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, Around(a, g, hu, hv));
                b.Box(x0, y0, x1, y1, h0, h1, color);
            }

            /// <summary>A flat many-sided patch on the floor.</summary>
            public void Patch(double a, double g, double r, double h, int color, int sides = 8) => b.Disc(Map(a, g), r, h, color, sides);

            /// <summary>Walked into by you, and kept off by the animals.</summary>
            public void Solid(double a, double g, double hu, double hv)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, Around(a, g, hu, hv));
                w.solids.Add(new Rect(x0, y0, x1, y1));
                keep.Add((Map(a, g), Math.Max(hu, hv) + 0.2));
            }

            /// <summary>A rock `size` across.</summary>
            public void Rock(double a, double g, double size)
            {
                var r = w.library.Get(w.zooRng.Pick(Catalog.BOULDERS));
                double s = size / Math.Max(r.size.x, r.size.z);
                var at = Map(a, g);
                Props.Place(r, w.props, at.x, at.y, w.zooRng.Range(0, Math.PI * 2), s, s, s);
                Solid(a, g, size * 0.36, size * 0.36);
            }

            /// <summary>One of the `kinds` of tree, `height` tall.</summary>
            public void Tree(string[] kinds, double a, double g, double height)
            {
                var t = w.library.Get(w.zooRng.Pick(kinds));
                double s = height / t.size.y;
                var at = Map(a, g);
                Props.Place(t, w.props, at.x, at.y, w.zooRng.Range(0, Math.PI * 2), s, s, s);
                Solid(a, g, 0.05, 0.05);
            }

            public void Bush(double a, double g, double size)
            {
                var m = w.library.Get(w.zooRng.Pick(Catalog.BUSHES));
                double s = size / Math.Max(m.size.x, m.size.z);
                var at = Map(a, g);
                Props.Place(m, w.props, at.x, at.y, w.zooRng.Range(0, Math.PI * 2), s, s, s, castShadows: false);
            }

            /// <summary>A flat-topped African tree, of blocks.</summary>
            public void Acacia(double a, double g, double height)
            {
                Block(a, g, 0.035, 0.035, 0, height, Z.post);
                Block(a, g, 0.34, 0.27, height, height + 0.06, Z.leaf);
                Block(a, g, 0.23, 0.19, height + 0.06, height + 0.12, Z.leafLight);
                Solid(a, g, 0.05, 0.05);
            }

            /// <summary>A fallen log, along the pen or across it.</summary>
            public void Log(double a, double g, double half, bool along)
            {
                double hu = along ? half : 0.05, hv = along ? 0.05 : half;
                Block(a, g, hu, hv, 0, 0.09, Z.post);
                Block(a, g, hu + (along ? 0.01 : -0.012), hv + (along ? -0.012 : 0.01), 0.02, 0.07, Z.rail);
                Solid(a, g, hu, hv);
            }

            /// <summary>A deck on four legs, `up` high.</summary>
            public void Deck(double a, double g, double hu, double hv, double up, int color)
            {
                double du = hu / (x.u1 - x.u0) * 0.9, dv = hv / (x.v1 - x.v0) * 0.9;
                foreach (int su in new[] { -1, 1 })
                    foreach (int sv in new[] { -1, 1 })
                        Block(a + su * du, g + sv * dv, 0.018, 0.018, 0, up, Z.post);
                Block(a, g, hu, hv, up, up + 0.025, color);
                Solid(a, g, hu, hv);
            }

            /// <summary>A stand of bamboo: canes of different heights, jointed.</summary>
            public void Bamboo(double a, double g, int canes)
            {
                for (int i = 0; i < canes; i++)
                {
                    double ca = a + w.zooRng.Range(-0.07, 0.07), cg = g + w.zooRng.Range(-0.05, 0.05), tall = w.zooRng.Range(0.5, 0.95);
                    Block(ca, cg, 0.012, 0.012, 0, tall, Z.bamboo);
                    for (double h = 0.14; h < tall; h += 0.16) Block(ca, cg, 0.016, 0.016, h, h + 0.012, Z.bambooDark);
                    Block(ca, cg, 0.05, 0.03, tall - 0.02, tall + 0.01, Z.leafLight);
                }
                Solid(a, g, 0.1, 0.1);
            }

            /// <summary>A heap of snow or stone: wide steps, each narrower.</summary>
            public void Heap(double a, double g, double r, double up, int color, int light)
            {
                const int STEPS = 3;
                for (int i = 0; i < STEPS; i++)
                {
                    double k = 1 - (double)i / STEPS;
                    Block(a, g, r * k, r * k * 0.85, up * i / STEPS, up * (i + 1) / STEPS, i == STEPS - 1 ? light : color);
                }
                Solid(a, g, r * 0.7, r * 0.6);
            }
        }

        /// <summary>A pen's floor, fence, gate and sign, and all that's in it; what the animals should keep off.</summary>
        List<(Vec2 at, double r)> BuildExhibit(Porchlight.Model.Zoo z, MeshBuilder b, Menagerie.Pen pen)
        {
            string first = pen.animals[0].animal;
            var e = new Exhibit(this, z, b, pen);
            int floor;
            Fencing fencing;
            switch (first.Substring(first.LastIndexOf('-') + 1))
            {
                case "lion": floor = Z.sand; fencing = Fencing.Bars; Savanna(e); break;
                case "tiger": floor = Z.jungle; fencing = Fencing.Bars; Jungle(e); break;
                case "giraffe": floor = Z.dirt; fencing = Fencing.Rails; Browse(e); break;
                case "elephant": floor = Z.dirt; fencing = Fencing.Rails; Wallow(e); break;
                case "monkey": floor = Z.grass; fencing = Fencing.Rails; Climbing(e); break;
                case "panda": floor = Z.grass; fencing = Fencing.Rails; BambooGrove(e); break;
                case "penguin": floor = Z.snow; fencing = Fencing.Glass; Rookery(e); break;
                default: floor = Z.snow; fencing = Fencing.Glass; Arctic(e); break;
            }
            var x = pen.box;
            var (fx0, fy0, fx1, fy1) = Menagerie.Rect(z, x);
            b.Rect(fx0, fy0, fx1, fy1, 0.005, floor);
            if (pen.pool)
            {
                var pool = Menagerie.Pool(pen);
                var (rx0, ry0, rx1, ry1) = Menagerie.Rect(z, new Airfield.Box(pool.u0 - 0.06, pool.v0 - 0.06, pool.u1 + 0.06, pool.v1 + 0.06));
                b.Rect(rx0, ry0, rx1, ry1, 0.012, Z.rim);
                var (px0, py0, px1, py1) = Menagerie.Rect(z, pool);
                b.Rect(px0, py0, px1, py1, 0.016, Z.pool);
            }
            PenFence(z, b, pen, fencing);
            PenSign(z, b, pen);
            return e.keep;
        }

        void Savanna(Exhibit e)
        {
            e.Patch(0.25, 0.68, 0.42, 0.008, Z.dryGrass);
            e.Patch(0.72, 0.42, 0.32, 0.008, Z.dryGrass, 7);
            e.Patch(0.28, 0.28, 0.24, 0.01, Z.pool, 7);
            // Pride rock: a great heap at the back, for the lions to look down from.
            e.Rock(0.68, 0.82, 1.0);
            e.Rock(0.4, 0.88, 0.62);
            e.Rock(0.88, 0.62, 0.5);
            e.Acacia(0.18, 0.62, 0.72);
            e.Log(0.68, 0.32, 0.22, true);
            e.Bush(0.1, 0.9, 0.35);
        }

        void Jungle(Exhibit e)
        {
            // A stream across the pen, in flat many-sided pools, with a log over it.
            for (double a = 0.04; a < 1; a += 0.13) e.Patch(a, 0.5 + 0.05 * Math.Sin(a * 9), 0.17, 0.009, Z.pool, 7);
            e.Log(0.55, 0.5, 0.24, false);
            e.Tree(Catalog.PALMS, 0.2, 0.8, 1.05);
            e.Tree(Catalog.TREES, 0.8, 0.85, 1.2);
            e.Tree(Catalog.PALMS, 0.82, 0.24, 0.9);
            e.Rock(0.45, 0.82, 0.55);
            foreach (var (a, g, s) in new[] { (0.08, 0.3, 0.42), (0.3, 0.68, 0.38), (0.62, 0.7, 0.45), (0.94, 0.62, 0.38), (0.08, 0.92, 0.45), (0.65, 0.2, 0.3), (0.3, 0.18, 0.28) })
                e.Bush(a, g, s);
        }

        void Browse(Exhibit e)
        {
            e.Patch(0.3, 0.3, 0.4, 0.008, Z.grass);
            e.Patch(0.72, 0.62, 0.45, 0.008, Z.grass, 7);
            e.Acacia(0.25, 0.72, 1.05);
            e.Acacia(0.78, 0.84, 0.95);
            // Hay up where only a giraffe could reach it.
            e.Block(0.68, 0.36, 0.03, 0.03, 0, 0.8, Z.post);
            e.Block(0.68, 0.36, 0.13, 0.13, 0.8, 0.83, Z.post);
            e.Block(0.68, 0.36, 0.11, 0.11, 0.83, 0.95, Z.hay);
            e.Solid(0.68, 0.36, 0.04, 0.04);
            // A shelter to stand under.
            foreach (var (a, g) in new[] { (0.1, 0.84), (0.1, 0.56), (0.42, 0.84), (0.42, 0.56) }) e.Block(a, g, 0.025, 0.025, 0, 1.0, Z.post);
            e.Block(0.26, 0.7, 0.42, 0.5, 1.0, 1.06, Z.roof);
            e.Rock(0.9, 0.28, 0.35);
        }

        void Wallow(Exhibit e)
        {
            // A great mud bath, wet in the middle, a pool by it and logs and boulders round about.
            e.Patch(0.42, 0.62, 0.62, 0.008, Z.mud, 9);
            e.Patch(0.42, 0.62, 0.36, 0.011, 0x5a432b, 8);
            e.Patch(0.8, 0.3, 0.3, 0.009, Z.pool, 7);
            e.Rock(0.12, 0.88, 0.8);
            e.Rock(0.9, 0.82, 0.65);
            e.Log(0.25, 0.26, 0.32, true);
            e.Tree(Catalog.TREES, 0.86, 0.56, 1.15);
            e.Bush(0.08, 0.34, 0.4);
        }

        void Climbing(Exhibit e)
        {
            e.Patch(0.5, 0.6, 0.6, 0.008, Z.jungle, 9);
            // A climbing frame: three tall posts, a high deck, beams and hanging ropes.
            double top = 0.72;
            foreach (var (a, g) in new[] { (0.22, 0.72), (0.62, 0.72), (0.42, 0.42) })
            {
                e.Block(a, g, 0.03, 0.03, 0, top + 0.08, Z.post);
                e.Solid(a, g, 0.04, 0.04);
            }
            e.Block(0.42, 0.72, 0.46, 0.02, top, top + 0.04, Z.rail);
            e.Block(0.42, 0.57, 0.02, 0.42, top - 0.14, top - 0.1, Z.rail);
            e.Deck(0.22, 0.86, 0.22, 0.18, 0.42, Z.rail);
            foreach (double a in new[] { 0.3, 0.42, 0.54 }) e.Block(a, 0.72, 0.007, 0.007, 0.22, top, Z.rope);
            // A tyre swinging from the far end.
            e.Block(0.62, 0.55, 0.007, 0.007, 0.2, top - 0.1, Z.rope);
            e.Block(0.62, 0.55, 0.07, 0.022, 0.1, 0.2, Z.tyre);
            e.Tree(Catalog.TREES, 0.86, 0.84, 1.25);
            e.Tree(Catalog.PALMS, 0.1, 0.34, 0.95);
            e.Bush(0.9, 0.34, 0.36);
        }

        void BambooGrove(Exhibit e)
        {
            e.Bamboo(0.14, 0.86, 9);
            e.Bamboo(0.42, 0.9, 7);
            e.Bamboo(0.86, 0.72, 9);
            e.Bamboo(0.1, 0.42, 6);
            // A deck to sit on, a low stone pond, and a tree for the koala.
            e.Deck(0.55, 0.56, 0.3, 0.22, 0.18, Z.rail);
            e.Patch(0.78, 0.25, 0.26, 0.01, Z.pool, 8);
            e.Tree(Catalog.TREES, 0.3, 0.62, 1.15);
            e.Rock(0.62, 0.86, 0.35);
        }

        void Rookery(Exhibit e)
        {
            // Floes on the pool; on the land a stack of ice to climb, and a stepped slide down to the water.
            e.Patch(0.28, 0.32, 0.13, 0.02, Z.snow, 6);
            e.Patch(0.22, 0.64, 0.17, 0.02, Z.ice, 6);
            e.Heap(0.8, 0.8, 0.34, 0.42, Z.ice, Z.snow);
            for (int i = 0; i < 4; i++) e.Block(0.68 - i * 0.05, 0.42, 0.06, 0.2, 0, 0.2 - i * 0.05, i % 2 == 0 ? Z.ice : Z.snow);
            e.Solid(0.62, 0.42, 0.14, 0.2);
            e.Rock(0.92, 0.3, 0.3);
        }

        void Arctic(Exhibit e)
        {
            e.Patch(0.3, 0.3, 0.2, 0.02, Z.snow, 6);
            e.Patch(0.2, 0.68, 0.14, 0.02, Z.ice, 6);
            e.Heap(0.74, 0.74, 0.46, 0.5, Z.snow, 0xffffff);
            // A cave of ice to shelter in.
            e.Block(0.7, 0.36, 0.03, 0.2, 0, 0.26, Z.ice);
            e.Block(0.92, 0.36, 0.03, 0.2, 0, 0.26, Z.ice);
            e.Block(0.81, 0.36, 0.3, 0.24, 0.26, 0.33, Z.snow);
            e.Solid(0.7, 0.36, 0.04, 0.2);
            e.Solid(0.92, 0.36, 0.04, 0.2);
            e.Rock(0.55, 0.9, 0.3);
        }

        /// <summary>A pen's four sides in its own kind of fence, with a gap in the one on the path for its gate, which swings.</summary>
        void PenFence(Porchlight.Model.Zoo z, MeshBuilder b, Menagerie.Pen pen, Fencing fencing)
        {
            const double t = 0.025, half = Menagerie.PEN_GATE_HALF;
            var x = pen.box;
            double gateV = Menagerie.PenGateV(pen), mid = x.MidU;
            double high = fencing == Fencing.Bars ? 0.4 : PEN_RAIL;
            // Each stretch of fence: its box along u or along v.
            var runs = new List<(double u0, double v0, double u1, double v1)>
            {
                (x.u0, x.v0 + x.v1 - gateV, x.u1, x.v0 + x.v1 - gateV),
                (x.u0, x.v0, x.u0, x.v1),
                (x.u1, x.v0, x.u1, x.v1),
                (x.u0, gateV, mid - half, gateV),
                (mid + half, gateV, x.u1, gateV),
            };
            void Box(double u0, double v0, double u1, double v1, double h0, double h1, int color)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, new Airfield.Box(u0, v0, u1, v1));
                b.Box(x0, y0, x1, y1, h0, h1, color);
            }
            foreach (var (u0, v0, u1, v1) in runs)
            {
                bool alongU = v0 == v1;
                double du = alongU ? 0 : t, dv = alongU ? t : 0;
                var (sx0, sy0, sx1, sy1) = Menagerie.Rect(z, new Airfield.Box(u0 - du, v0 - dv, u1 + du, v1 + dv));
                solids.Add(new Rect(sx0, sy0, sx1, sy1));
                double length = alongU ? u1 - u0 : v1 - v0;
                void Along(double h0, double h1, int color, double thin = 1) => Box(u0 - du * thin, v0 - dv * thin, u1 + du * thin, v1 + dv * thin, h0, h1, color);
                void Upright(double s, double r, double h0, double h1, int color)
                {
                    double u = alongU ? u0 + s : u0, v = alongU ? v0 : v0 + s;
                    Box(u - r, v - r, u + r, v + r, h0, h1, color);
                }
                switch (fencing)
                {
                    case Fencing.Bars:
                        Along(0, 0.05, Z.stone, 1.6);
                        Along(high - 0.03, high, Z.iron);
                        for (int k = 0, n = Math.Max(1, (int)Math.Round(length / 0.09)); k <= n; k++) Upright(length * k / n, 0.009, 0.05, high, Z.iron);
                        for (int k = 0, n = Math.Max(1, (int)Math.Round(length / 0.6)); k <= n; k++) Upright(length * k / n, 0.03, 0, high + 0.03, Z.stone);
                        break;
                    case Fencing.Glass:
                        Along(0, 0.1, Z.stone, 1.6);
                        Along(0.1, 0.115, Z.rim, 1.8);
                        for (int k = 0, n = Math.Max(1, (int)Math.Round(length / 0.6)); k <= n; k++) Upright(length * k / n, 0.018, 0.1, high + 0.04, Z.iron);
                        var pane = Menagerie.ToWorld(z, (u0 + u1) / 2, (v0 + v1) / 2);
                        var along = alongU ? Menagerie.AlongU(z) : Menagerie.AlongV(z);
                        var glass = Shape(PrimitiveType.Cube, props, Coords.Map(pane.x, pane.y, (0.115 + high + 0.03) / 2),
                            new Vector3((float)length, (float)(high + 0.03 - 0.115), 0.012f), Materials.Glass(), false, "pen glass");
                        glass.transform.localRotation = Quaternion.FromToRotation(Vector3.right, FireLook.Flat(along));
                        break;
                    default:
                        Along(PEN_RAIL * 0.45, PEN_RAIL * 0.55, Z.rail);
                        Along(PEN_RAIL * 0.88, PEN_RAIL, Z.rail);
                        for (int k = 0, n = Math.Max(1, (int)Math.Round(length / 0.5)); k <= n; k++) Upright(length * k / n, 0.03, 0, PEN_RAIL + 0.03, Z.post);
                        break;
                }
            }

            // Gateposts, and the gate: hung on one, swinging into the pen.
            foreach (double u in new[] { mid - half, mid + half }) Box(u - 0.04, gateV - 0.04, u + 0.04, gateV + 0.04, 0, high + 0.1, fencing == Fencing.Rails ? Z.post : Z.iron);
            var hingeAt = Menagerie.ToWorld(z, mid - half, gateV);
            var hinge = new GameObject("pen gate").transform;
            hinge.SetParent(root.transform, false);
            hinge.localPosition = Coords.Map(hingeAt.x, hingeAt.y);
            var dirU = FireLook.Flat(Menagerie.AlongU(z));
            var closed = Quaternion.FromToRotation(Vector3.right, dirU);
            hinge.localRotation = closed;
            var material = Materials.Lit(fencing == Fencing.Rails ? Z.rail : Z.iron);
            float wide = (float)(half * 2), tall = (float)high;
            foreach (float h in new[] { tall * 0.2f, tall * 0.92f }) Shape(PrimitiveType.Cube, hinge, new Vector3(wide / 2, h, 0), new Vector3(wide, 0.03f, 0.022f), material);
            int uprights = fencing == Fencing.Rails ? 3 : 7;
            for (int k = 0; k < uprights; k++) Shape(PrimitiveType.Cube, hinge, new Vector3(wide * (k + 0.5f) / uprights, tall * 0.56f, 0), new Vector3(0.018f, tall * 0.72f, 0.018f), material);
            var inward = FireLook.Flat(Menagerie.AlongV(z)) * -Menagerie.PenGateOut(pen);
            float swing = Vector3.Dot(Quaternion.AngleAxis(90, Vector3.up) * dirU, inward) > 0 ? 105 : -105;
            var (dx0, dy0, dx1, dy1) = Menagerie.Rect(z, new Airfield.Box(mid - half, gateV - t, mid + half, gateV + t));
            penGates.Add(new PenGate
            {
                pen = pen,
                door = solids.AddDoor(new Rect(dx0, dy0, dx1, dy1)),
                at = Menagerie.ToWorld(z, mid, gateV),
                hinge = hinge,
                closed = closed,
                swing = swing,
            });
        }

        /// <summary>A wooden sign by a pen's gate, on the path side, saying who lives there.</summary>
        void PenSign(Porchlight.Model.Zoo z, MeshBuilder b, Menagerie.Pen pen)
        {
            double gateV = Menagerie.PenGateV(pen), u = pen.box.MidU + Menagerie.PEN_GATE_HALF + 0.3;
            int outward = Menagerie.PenGateOut(pen);
            double v = gateV + outward * 0.08;
            void Box(double u0, double v0, double u1, double v1, double h0, double h1, int color)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, new Airfield.Box(u0, v0, u1, v1));
                b.Box(x0, y0, x1, y1, h0, h1, color);
            }
            Box(u - 0.015, v - 0.015, u + 0.015, v + 0.015, 0, 0.42, Z.post);
            Box(u - 0.27, v - 0.012, u + 0.27, v + 0.012, 0.3, 0.46, Z.arch);
            var at = Menagerie.ToWorld(z, u, v);
            var o = Menagerie.AlongV(z);
            var go = new GameObject("pen sign");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = Coords.Map(at.x + o.x * outward * 0.015, at.y + o.y * outward * 0.015, 0.38);
            go.transform.localRotation = Quaternion.LookRotation(FireLook.Flat(new Vec2(-o.x * outward, -o.y * outward)), Vector3.up);
            var text = go.AddComponent<TextMeshPro>();
            text.font = library.font;
            text.text = string.Join(" & ", pen.animals.Select(a => Catalog.AnimalName(a.animal)));
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.05f;
            text.fontSizeMax = 1f;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(0.48f, 0.12f);
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>Along the paths: benches facing the way across, and a planter where the paths meet.</summary>
        void PathSides(Porchlight.Model.Zoo z, MeshBuilder b)
        {
            void Box(double u0, double v0, double u1, double v1, double h0, double h1, int color)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, new Airfield.Box(u0, v0, u1, v1));
                b.Box(x0, y0, x1, y1, h0, h1, color);
            }
            void Solid(double u0, double v0, double u1, double v1)
            {
                var (x0, y0, x1, y1) = Menagerie.Rect(z, new Airfield.Box(u0, v0, u1, v1));
                solids.Add(new Rect(x0, y0, x1, y1));
            }
            var across = Menagerie.Paths()[1];
            foreach (double u in new[] { 2.85, 9.15 })
                foreach (int side in new[] { -1, 1 })
                {
                    double v = side < 0 ? across.v0 - 0.12 : across.v1 + 0.12;
                    Box(u - 0.2, v - 0.05, u + 0.2, v + 0.05, 0.08, 0.1, Z.rail);
                    Box(u - 0.2, v + side * 0.05 - 0.012, u + 0.2, v + side * 0.05 + 0.012, 0.1, 0.2, Z.rail);
                    foreach (double leg in new[] { -0.16, 0.16 }) Box(u + leg - 0.012, v - 0.04, u + leg + 0.012, v + 0.04, 0, 0.08, Z.post);
                    Solid(u - 0.2, v - 0.05, u + 0.2, v + 0.05);
                }
            // Where the paths meet: a low stone planter, a tree and flowers.
            double cu = Menagerie.LENGTH / 2.0, cv = Menagerie.DEPTH / 2.0;
            Box(cu - 0.26, cv - 0.26, cu + 0.26, cv + 0.26, 0, 0.1, Z.stone);
            Box(cu - 0.22, cv - 0.22, cu + 0.22, cv + 0.22, 0.1, 0.105, Z.dirt);
            Solid(cu - 0.26, cv - 0.26, cu + 0.26, cv + 0.26);
            var c = Menagerie.ToWorld(z, cu, cv);
            var tree = library.Get(zooRng.Pick(Catalog.TREES));
            double s = 0.95 / tree.size.y;
            Props.Place(tree, props, c.x, c.y, zooRng.Range(0, Math.PI * 2), s, s, s, lift: 0.1);
            foreach (var (fu, fv) in new[] { (-0.14, -0.14), (0.14, -0.14), (-0.14, 0.14), (0.14, 0.14) })
            {
                var f = library.Get(zooRng.Pick(Catalog.FLOWERS));
                double fs = 0.14 / Math.Max(f.size.x, f.size.z);
                var at = Menagerie.ToWorld(z, cu + fu, cv + fv);
                Props.Place(f, props, at.x, at.y, zooRng.Range(0, Math.PI * 2), fs, fs, fs, lift: 0.1, castShadows: false);
            }
        }
    }
}
