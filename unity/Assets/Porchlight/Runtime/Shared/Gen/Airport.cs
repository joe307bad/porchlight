// Stage 3b: the airport. One per town, on dry ground out on the edge of town,
// away from its centre and the gates: a runway along its far side, a taxiway,
// an apron where the planes park, hangars and a car park along the side facing
// town. The main roads (next) go round it, and one comes in to the car park.
// Airfield says where each part of it is.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class AirportStage : IStage
    {
        /// <summary>Tiles kept clear between the airport and the map's edge.</summary>
        const int EDGE_MARGIN = 1;
        /// <summary>Tiles kept clear between the airport and a gate, or the town centre.</summary>
        const int CLEARANCE = 4;
        /// <summary>Where the airport's nearest edge may be, as a share of the town radius: on the outskirts.</summary>
        const double NEAREST_MIN = 0.95;
        const double NEAREST_MAX = 1.7;

        public string Name => "airport";

        struct Candidate
        {
            public int x0, y0, x1, y1;
            public bool alongX;
            public double score;
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var terrain = Raster.RoadTerrain(model);
            var water = terrain.water;
            var C = model.center;
            double R = model.townRadius;
            int length = Airfield.LengthFor(w, h);
            var bad = new byte[w * h];
            for (int i = 0; i < bad.Length; i++) bad[i] = water[i];
            var keepClear = new List<Vec2>(model.gates) { C };
            foreach (var q in keepClear)
                for (int y = (int)Math.Floor(q.y) - CLEARANCE; y <= (int)Math.Floor(q.y) + CLEARANCE; y++)
                    for (int x = (int)Math.Floor(q.x) - CLEARANCE; x <= (int)Math.Floor(q.x) + CLEARANCE; x++)
                        if (Grid.InBounds(x, y, w, h)) bad[Grid.Index(x, y, w)] = 1;
            var sat = SummedArea(bad, w, h);
            int Bad(int x0, int y0, int x1, int y1) => sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];

            var candidates = new List<Candidate>();
            foreach (bool alongX in new[] { true, false })
            {
                int rw = alongX ? length : Airfield.DEPTH, rh = alongX ? Airfield.DEPTH : length;
                for (int y0 = EDGE_MARGIN; y0 + rh <= h - EDGE_MARGIN; y0++)
                {
                    for (int x0 = EDGE_MARGIN; x0 + rw <= w - EDGE_MARGIN; x0++)
                    {
                        // Drawn for every spot, so the stream stays aligned.
                        double jitter = rng.Next();
                        if (Bad(x0, y0, x0 + rw, y0 + rh) != 0) continue;
                        double nx = Math.Max(x0, Math.Min(x0 + rw, C.x)), ny = Math.Max(y0, Math.Min(y0 + rh, C.y));
                        double nearest = Numbers.Hypot(nx - C.x, ny - C.y) / Math.Max(1, R);
                        // Anywhere on the outskirts will do, the nearer town the likelier; on a
                        // map too small for that, as far out as it goes.
                        bool outskirts = nearest >= NEAREST_MIN && nearest <= NEAREST_MAX;
                        double score = outskirts ? jitter - 0.4 * (nearest - NEAREST_MIN) : -10 - Math.Abs(nearest - NEAREST_MIN);
                        candidates.Add(new Candidate { x0 = x0, y0 = y0, x1 = x0 + rw, y1 = y0 + rh, alongX = alongX, score = score });
                    }
                }
            }
            Numbers.StableSort(candidates, (a, b) => b.score - a.score);

            var next = model.With();
            foreach (var c in candidates)
            {
                var airport = new Airport { x0 = c.x0, y0 = c.y0, x1 = c.x1, y1 = c.y1 };
                double mx = (c.x0 + c.x1) / 2.0, my = (c.y0 + c.y1) / 2.0;
                airport.front = c.alongX ? (C.y > my ? Facing.S : Facing.N) : (C.x > mx ? Facing.E : Facing.W);
                // The road comes in across the car park, as far from the apron as there's dry land outside.
                var park = Airfield.CarPark(airport);
                for (double u = Math.Floor(Airfield.Length(airport) - 3.0) + 0.5; u >= park.u0 + 2.5; u -= 1)
                {
                    var outside = Airfield.ToWorld(airport, u, Airfield.DEPTH + 0.5);
                    int sx = (int)Math.Floor(outside.x), sy = (int)Math.Floor(outside.y);
                    if (!Grid.InBounds(sx, sy, w, h) || water[Grid.Index(sx, sy, w)] != 0 || !Numbers.IsFinite(terrain.cost[Grid.Index(sx, sy, w)])) continue;
                    airport.entranceU = u;
                    next.airport = airport;
                    return next;
                }
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
            int w = model.width, h = model.height;
            var a = model.airport;
            if (a == null)
            {
                if (Math.Min(w, h) >= Airfield.ALWAYS_FROM) errors.Add("no airport");
                return errors;
            }
            if (a.x0 < 0 || a.y0 < 0 || a.x1 > w || a.y1 > h) errors.Add("the airport is off the map");
            var water = Raster.WaterMask(model);
            var mask = Airfield.Mask(a, w, h);
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0 && water[i] != 0)
                {
                    errors.Add($"airport tile {i % w},{i / w} is under water");
                    break;
                }
            }
            return errors;
        }

        /// <summary>Whether the road graph reaches the airport's car park from the town centre.</summary>
        public static List<string> CheckRoadIn(TownModel model)
        {
            var a = model.airport;
            var graph = model.roads;
            if (a == null || graph.nodes.Count == 0) return new List<string>();
            int hub = RoadGraphOps.NearestNode(graph, model.center);
            var entrance = Airfield.Entrance(a);
            int gate = RoadGraphOps.NearestNode(graph, entrance);
            if (gate < 0 || Geo.Dist(graph.nodes[gate].at, entrance) > 0.01 || !RoadGraphOps.ReachableNodes(graph, hub).Contains(gate))
                return new List<string> { "no road from the town to the airport" };
            return new List<string>();
        }
    }

    /// <summary>
    /// Where each part of an airport is, in its own terms: u runs along it (0 to
    /// its length), v across it from the far side (0) to the side facing town
    /// (DEPTH). Parts are boxes in (u, v); plane spots face the runway.
    /// </summary>
    public static class Airfield
    {
        public const int DEPTH = 17;
        public const int MIN_LENGTH = 28;
        public const int MAX_LENGTH = 40;
        /// <summary>Maps this big or bigger always have an airport (the checks insist).</summary>
        public const int ALWAYS_FROM = 64;
        /// <summary>Room each parked plane takes along a row (the widest wingspan, and a little).</summary>
        public const double PLANE_SPACING = 3.2;

        public struct Box
        {
            public double u0, v0, u1, v1;

            public Box(double u0, double v0, double u1, double v1)
            {
                this.u0 = u0;
                this.v0 = v0;
                this.u1 = u1;
                this.v1 = v1;
            }

            public double MidU => (u0 + u1) / 2;
            public double MidV => (v0 + v1) / 2;
        }

        public struct Spot
        {
            public Vec2 at;
            /// <summary>Which way it faces, radians in map space (atan2(dy, dx)).</summary>
            public double heading;
        }

        /// <summary>How long a town's airport is: longer on a bigger map.</summary>
        public static int LengthFor(int w, int h) => Math.Max(MIN_LENGTH, Math.Min(MAX_LENGTH, Numbers.RoundInt(Math.Min(w, h) * 0.3)));

        static bool NorthSouth(Airport a) => a.front == Facing.N || a.front == Facing.S;

        public static int Length(Airport a) => NorthSouth(a) ? a.x1 - a.x0 : a.y1 - a.y0;

        public static Vec2 ToWorld(Airport a, double u, double v) => a.front switch
        {
            Facing.S => new Vec2(a.x0 + u, a.y0 + v),
            Facing.N => new Vec2(a.x1 - u, a.y1 - v),
            Facing.E => new Vec2(a.x0 + v, a.y1 - u),
            _ => new Vec2(a.x1 - v, a.y0 + u),
        };

        /// <summary>The map direction u runs, and v (toward town).</summary>
        public static Vec2 AlongU(Airport a) => a.front switch
        {
            Facing.S => new Vec2(1, 0),
            Facing.N => new Vec2(-1, 0),
            Facing.E => new Vec2(0, -1),
            _ => new Vec2(0, 1),
        };

        public static Vec2 AlongV(Airport a) => a.front switch
        {
            Facing.S => new Vec2(0, 1),
            Facing.N => new Vec2(0, -1),
            Facing.E => new Vec2(1, 0),
            _ => new Vec2(-1, 0),
        };

        /// <summary>A box as a map rectangle (x0, y0, x1, y1).</summary>
        public static (double x0, double y0, double x1, double y1) Rect(Airport a, Box b)
        {
            var p = ToWorld(a, b.u0, b.v0);
            var q = ToWorld(a, b.u1, b.v1);
            return (Math.Min(p.x, q.x), Math.Min(p.y, q.y), Math.Max(p.x, q.x), Math.Max(p.y, q.y));
        }

        public static Box Runway(Airport a) => new Box(1, 1.5, Length(a) - 1, 4.5);

        public static Box Taxiway(Airport a) => new Box(3, 6, Length(a) - 3, 7.2);

        /// <summary>The two stretches joining the taxiway to the runway, at either end.</summary>
        public static Box[] Links(Airport a) => new[] { new Box(3, 4.5, 4.4, 6), new Box(Length(a) - 4.4, 4.5, Length(a) - 3, 6) };

        static double ApronEnd(Airport a) => Math.Floor(Length(a) * 0.72);

        public static Box Apron(Airport a) => new Box(2, 7.2, ApronEnd(a), 13.8);

        /// <summary>Hangars along the town side of the apron, their doors onto it: two, or three at a long airport.</summary>
        public static List<Box> Hangars(Airport a)
        {
            var o = new List<Box>();
            double from = 2, to = ApronEnd(a);
            int count = Length(a) >= 34 ? 3 : 2;
            double each = (to - from) / count;
            for (int k = 0; k < count; k++)
            {
                double mid = from + each * (k + 0.5);
                o.Add(new Box(mid - 2.75, 13.8, mid + 2.75, 16.8));
            }
            return o;
        }

        public static Box CarPark(Airport a) => new Box(ApronEnd(a) + 1, 10, Length(a) - 1, DEPTH - 0.2);

        /// <summary>The control tower, at the corner of the apron by the car park.</summary>
        public static Box Tower(Airport a) => new Box(ApronEnd(a) + 1.2, 7.8, ApronEnd(a) + 2.8, 9.4);

        /// <summary>Where the road in ends, in the car park (a tile's centre).</summary>
        public static Vec2 Entrance(Airport a) => ToWorld(a, a.entranceU, ENTRANCE_V);

        const double ENTRANCE_V = 12.5;

        /// <summary>The tiles the road in runs over, from the car park out to the airport's edge.</summary>
        public static List<int> EntranceLane(Airport a, int w, int h)
        {
            var o = new List<int>();
            for (double v = ENTRANCE_V; v < DEPTH; v += 1)
            {
                var p = ToWorld(a, a.entranceU, v);
                int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
                if (Grid.InBounds(x, y, w, h)) o.Add(Grid.Index(x, y, w));
            }
            return o;
        }

        /// <summary>Where planes park: two rows across the apron (the second staggered), facing the runway.</summary>
        public static List<Spot> PlaneSpots(Airport a)
        {
            var apron = Apron(a);
            var o = new List<Spot>();
            int per = Math.Max(1, (int)Math.Floor((apron.u1 - apron.u0) / PLANE_SPACING));
            double gap = (apron.u1 - apron.u0) / per;
            var facing = AlongV(a);
            double heading = DetMath.Atan2(-facing.y, -facing.x);
            for (int k = 0; k < per; k++) o.Add(new Spot { at = ToWorld(a, apron.u0 + gap * (k + 0.5), 9.1), heading = heading });
            for (int k = 0; k < per - 1; k++) o.Add(new Spot { at = ToWorld(a, apron.u0 + gap * (k + 1), 12.1), heading = heading });
            return o;
        }

        /// <summary>Tiles whose centres are inside the box.</summary>
        public static List<int> TilesIn(Airport a, Box b, int w, int h)
        {
            var (x0, y0, x1, y1) = Rect(a, b);
            var o = new List<int>();
            for (int y = Math.Max(0, (int)Math.Floor(y0)); y < Math.Min(h, (int)Math.Ceiling(y1)); y++)
                for (int x = Math.Max(0, (int)Math.Floor(x0)); x < Math.Min(w, (int)Math.Ceiling(x1)); x++)
                    if (x + 0.5 > x0 && x + 0.5 < x1 && y + 0.5 > y0 && y + 0.5 < y1) o.Add(Grid.Index(x, y, w));
            return o;
        }

        /// <summary>1 on the airport's tiles.</summary>
        public static byte[] Mask(Airport a, int w, int h)
        {
            var o = new byte[w * h];
            if (a == null) return o;
            for (int y = Math.Max(0, a.y0); y < Math.Min(h, a.y1); y++)
                for (int x = Math.Max(0, a.x0); x < Math.Min(w, a.x1); x++) o[Grid.Index(x, y, w)] = 1;
            return o;
        }
    }
}
