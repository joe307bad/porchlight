// Stage 3c: the zoo. One per town, on dry ground inside the town but clear of
// its centre, the gates and the airport: a fenced rectangle with its gate on
// the side facing the middle of town, a path in from the gate and one across,
// and pens either side of them, each with its animals (the penguins' and the
// polar bears' round a pool). The roads and streets (next) go round it, a tile
// clear, and no lot is laid out on it. Menagerie says where each part of it is.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class ZooStage : IStage
    {
        /// <summary>Tiles kept clear between the zoo and the map's edge.</summary>
        const int EDGE_MARGIN = 3;
        /// <summary>Tiles kept clear between the zoo and a gate, the town centre or the airport.</summary>
        const int CLEARANCE = 4;
        /// <summary>Dry ground all round it, this far out: room for the street round it.</summary>
        const int DRY_MARGIN = 2;
        /// <summary>Where its nearest edge may be, as a share of the town radius: in town, short of the middle.</summary>
        const double NEAREST_MIN = 0.3;
        const double NEAREST_MAX = 0.8;

        public string Name => "zoo";

        struct Candidate
        {
            public int x0, y0, x1, y1;
            public bool alongX;
            public double score;
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var water = Raster.WaterMask(model);
            var C = model.center;
            double R = model.townRadius;
            var bad = new byte[w * h];
            for (int i = 0; i < bad.Length; i++) bad[i] = water[i];
            void Clear(double qx, double qy)
            {
                for (int y = (int)Math.Floor(qy) - CLEARANCE; y <= (int)Math.Floor(qy) + CLEARANCE; y++)
                    for (int x = (int)Math.Floor(qx) - CLEARANCE; x <= (int)Math.Floor(qx) + CLEARANCE; x++)
                        if (Grid.InBounds(x, y, w, h)) bad[Grid.Index(x, y, w)] = 1;
            }
            foreach (var q in model.gates) Clear(q.x, q.y);
            Clear(C.x, C.y);
            var a = model.airport;
            if (a != null)
                for (int y = a.y0 - CLEARANCE; y < a.y1 + CLEARANCE; y++)
                    for (int x = a.x0 - CLEARANCE; x < a.x1 + CLEARANCE; x++)
                        if (Grid.InBounds(x, y, w, h)) bad[Grid.Index(x, y, w)] = 1;
            var sat = SummedArea(bad, w, h);
            int Bad(int x0, int y0, int x1, int y1) => sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];

            var candidates = new List<Candidate>();
            foreach (bool alongX in new[] { true, false })
            {
                int rw = alongX ? Menagerie.LENGTH : Menagerie.DEPTH, rh = alongX ? Menagerie.DEPTH : Menagerie.LENGTH;
                for (int y0 = EDGE_MARGIN; y0 + rh <= h - EDGE_MARGIN; y0++)
                {
                    for (int x0 = EDGE_MARGIN; x0 + rw <= w - EDGE_MARGIN; x0++)
                    {
                        // Drawn for every spot, so the stream stays aligned.
                        double jitter = rng.Next();
                        if (Bad(x0 - DRY_MARGIN, y0 - DRY_MARGIN, x0 + rw + DRY_MARGIN, y0 + rh + DRY_MARGIN) != 0) continue;
                        double nx = Math.Max(x0, Math.Min(x0 + rw, C.x)), ny = Math.Max(y0, Math.Min(y0 + rh, C.y));
                        double nearest = Numbers.Hypot(nx - C.x, ny - C.y) / Math.Max(1, R);
                        bool inTown = nearest >= NEAREST_MIN && nearest <= NEAREST_MAX;
                        double score = inTown ? jitter : -10 - Math.Abs(nearest - (NEAREST_MIN + NEAREST_MAX) / 2);
                        candidates.Add(new Candidate { x0 = x0, y0 = y0, x1 = x0 + rw, y1 = y0 + rh, alongX = alongX, score = score });
                    }
                }
            }
            var next = model.With();
            Numbers.StableSort(candidates, (p, q) => q.score - p.score);
            // The first whose road up to the gate can be built (not on ground too steep for one).
            var cost = Raster.RoadTerrain(model).cost;
            foreach (var c in candidates)
            {
                double mx = (c.x0 + c.x1) / 2.0, my = (c.y0 + c.y1) / 2.0;
                var zoo = new Zoo
                {
                    x0 = c.x0, y0 = c.y0, x1 = c.x1, y1 = c.y1,
                    front = c.alongX ? (C.y > my ? Facing.S : Facing.N) : (C.x > mx ? Facing.E : Facing.W),
                };
                var end = Menagerie.RoadEnd(zoo);
                int ex = (int)Math.Floor(end.x), ey = (int)Math.Floor(end.y);
                if (!Grid.InBounds(ex, ey, w, h) || float.IsPositiveInfinity(cost[Grid.Index(ex, ey, w)])) continue;
                next.zoo = zoo;
                return next;
            }
            return next;
        }

        static int[] SummedArea(byte[] v, int w, int h)
        {
            var sat = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += v[Grid.Index(x, y, w)];
                    sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row;
                }
            }
            return sat;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            var z = model.zoo;
            if (z == null) return errors;
            int w = model.width, h = model.height;
            if (z.x0 < 0 || z.y0 < 0 || z.x1 > w || z.y1 > h) errors.Add("the zoo is off the map");
            var water = Raster.WaterMask(model);
            var mask = Menagerie.Mask(z, w, h, 0);
            var airport = Airfield.Mask(model.airport, w, h);
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 0) continue;
                if (water[i] != 0) errors.Add($"zoo tile {i % w},{i / w} is under water");
                if (airport[i] != 0) errors.Add($"zoo tile {i % w},{i / w} is on the airport");
                if (errors.Count > 0) break;
            }
            return errors;
        }
    }

    /// <summary>
    /// Where each part of a zoo is, in its own terms: u runs along it (0 to
    /// LENGTH), v across it from the back (0) to the gate's side (DEPTH). Parts
    /// are boxes in (u, v), as at the airport (Airfield.Box).
    /// </summary>
    public static class Menagerie
    {
        public const int LENGTH = 12;
        public const int DEPTH = 10;
        /// <summary>How far in from the zoo's edge its fence stands.</summary>
        public const double FENCE_IN = 0.15;
        /// <summary>The gate: the gap in the fence, either side of the middle of the front.</summary>
        public const double GATE_HALF = 0.8;
        const double PATH_HALF = 0.6;
        const double MID = LENGTH / 2.0;
        const double CROSS = DEPTH / 2.0;

        /// <summary>A pen: its box, its animals (Catalog.ANIMALS names) and how many of each, and whether it has a pool.</summary>
        public struct Pen
        {
            public Airfield.Box box;
            public (string animal, int count)[] animals;
            public bool pool;
        }

        public static Vec2 ToWorld(Zoo z, double u, double v) => z.front switch
        {
            Facing.S => new Vec2(z.x0 + u, z.y0 + v),
            Facing.N => new Vec2(z.x1 - u, z.y1 - v),
            Facing.E => new Vec2(z.x0 + v, z.y1 - u),
            _ => new Vec2(z.x1 - v, z.y0 + u),
        };

        /// <summary>The map direction u runs, and v (out through the gate).</summary>
        public static Vec2 AlongU(Zoo z) => z.front switch
        {
            Facing.S => new Vec2(1, 0),
            Facing.N => new Vec2(-1, 0),
            Facing.E => new Vec2(0, -1),
            _ => new Vec2(0, 1),
        };

        public static Vec2 AlongV(Zoo z) => z.front switch
        {
            Facing.S => new Vec2(0, 1),
            Facing.N => new Vec2(0, -1),
            Facing.E => new Vec2(1, 0),
            _ => new Vec2(-1, 0),
        };

        /// <summary>A box as a map rectangle (x0, y0, x1, y1).</summary>
        public static (double x0, double y0, double x1, double y1) Rect(Zoo z, Airfield.Box b)
        {
            var p = ToWorld(z, b.u0, b.v0);
            var q = ToWorld(z, b.u1, b.v1);
            return (Math.Min(p.x, q.x), Math.Min(p.y, q.y), Math.Max(p.x, q.x), Math.Max(p.y, q.y));
        }

        /// <summary>Whether a map point is inside the zoo's fence.</summary>
        public static bool Inside(Zoo z, Vec2 p) =>
            z != null && p.x > z.x0 + FENCE_IN && p.x < z.x1 - FENCE_IN && p.y > z.y0 + FENCE_IN && p.y < z.y1 - FENCE_IN;

        /// <summary>The middle of the gate, on the fence's line.</summary>
        public static Vec2 Gate(Zoo z) => ToWorld(z, MID, DEPTH - FENCE_IN);

        /// <summary>Where the road up to the gate ends: just past the tile kept clear round the zoo.</summary>
        public static Vec2 RoadEnd(Zoo z) => ToWorld(z, MID, DEPTH + 1.5);

        /// <summary>Where a visitor stands just inside, on the path in from the gate, by the penguins.</summary>
        public static Vec2 Visitor(Zoo z) => ToWorld(z, MID, DEPTH - 2.6);

        /// <summary>The path in from the gate, and the one across the middle.</summary>
        public static Airfield.Box[] Paths() => new[]
        {
            new Airfield.Box(MID - PATH_HALF, 0.9, MID + PATH_HALF, DEPTH),
            new Airfield.Box(0.9, CROSS - PATH_HALF, LENGTH - 0.9, CROSS + PATH_HALF),
        };

        /// <summary>Two pens each side of the path in, each side of the path across: eight, the penguins' by the gate.</summary>
        public static List<Pen> Pens()
        {
            const double gap = 0.25;
            double l0 = 0.55, l1 = MID - PATH_HALF - gap, r0 = MID + PATH_HALF + gap, r1 = LENGTH - 0.55;
            double b0 = 0.55, b1 = CROSS - PATH_HALF - gap, f0 = CROSS + PATH_HALF + gap, f1 = DEPTH - 1.1;
            double lm = (l0 + l1) / 2, rm = (r0 + r1) / 2;
            Pen P(double u0, double v0, double u1, double v1, bool pool, params (string, int)[] animals) =>
                new Pen { box = new Airfield.Box(u0, v0, u1, v1), animals = animals, pool = pool };
            return new List<Pen>
            {
                P(lm + gap / 2, f0, l1, f1, true, ("animals/animal-penguin", 4)),
                P(l0, f0, lm - gap / 2, f1, true, ("animals/animal-polar", 2)),
                P(r0, f0, rm - gap / 2, f1, false, ("animals/animal-monkey", 3), ("animals/animal-parrot", 2)),
                P(rm + gap / 2, f0, r1, f1, false, ("animals/animal-panda", 2), ("animals/animal-koala", 1)),
                P(lm + gap / 2, b0, l1, b1, false, ("animals/animal-lion", 2)),
                P(l0, b0, lm - gap / 2, b1, false, ("animals/animal-tiger", 2)),
                P(r0, b0, rm - gap / 2, b1, false, ("animals/animal-giraffe", 2)),
                P(rm + gap / 2, b0, r1, b1, false, ("animals/animal-elephant", 2)),
            };
        }

        /// <summary>A pen's pool: the half of it furthest from the path in.</summary>
        public static Airfield.Box Pool(Pen p)
        {
            var b = p.box;
            bool left = b.MidU < MID;
            double inset = 0.25;
            return left
                ? new Airfield.Box(b.u0 + inset, b.v0 + inset, b.MidU, b.v1 - inset)
                : new Airfield.Box(b.MidU, b.v0 + inset, b.u1 - inset, b.v1 - inset);
        }

        /// <summary>1 on the zoo's tiles, and `margin` tiles round them.</summary>
        public static byte[] Mask(Zoo z, int w, int h, int margin)
        {
            var o = new byte[w * h];
            if (z == null) return o;
            for (int y = Math.Max(0, z.y0 - margin); y < Math.Min(h, z.y1 + margin); y++)
                for (int x = Math.Max(0, z.x0 - margin); x < Math.Min(w, z.x1 + margin); x++) o[Grid.Index(x, y, w)] = 1;
            return o;
        }

        /// <summary>The zoo's own tiles.</summary>
        public static List<int> Tiles(Zoo z, int w, int h)
        {
            var o = new List<int>();
            if (z == null) return o;
            for (int y = Math.Max(0, z.y0); y < Math.Min(h, z.y1); y++)
                for (int x = Math.Max(0, z.x0); x < Math.Min(w, z.x1); x++) o.Add(Grid.Index(x, y, w));
            return o;
        }
    }
}
