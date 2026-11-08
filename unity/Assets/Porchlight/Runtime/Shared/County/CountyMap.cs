// The county: Porchlight in the middle, three public cities east, south and
// west of it joined in a ring, and north of it a loop through the player's own
// towns. Every town sits in the middle of a 256-tile slot, and every gate is in
// the middle of a map edge (Compass), so where everything goes, every highway
// and which room owns which ground follows from the towns' keys alone. The
// server works out the same from server/src/county.ts (the same numbers; test
// vectors from `Checks county` keep the two together).
//
//   [Mine 1]═══════[Mine 2]═══════[Mine 3]        your own towns (private)
//       ╚════════╗    ║    ╔════════╝
//                junction (north)
//                     ║
//   [West]═════[Porchlight]═════[East]             public
//      ╚═══════════[South]═══════════╝
//
// Coordinates are world tiles, x east and y south (as in a town); a town's own
// tile (x, y) is world (x0 + x, y0 + y).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.County
{
    /// <summary>Where a town can be: Porchlight, the three public cities, and the account's own three slots.</summary>
    public enum Place { Porchlight = 0, East = 1, South = 2, West = 3, Mine1 = 4, Mine2 = 5, Mine3 = 6 }

    public enum SpaceKind { Town, Junction, Vacant, Highway }

    /// <summary>A town of the county: where it goes and what it is.</summary>
    public sealed class PlacedTown
    {
        public Place place;
        public string name;
        /// <summary>The town's key (its seed and params): also its room's name.</summary>
        public string key;
        public int width, height;
    }

    /// <summary>
    /// A piece of the county with its own tile grid: a town, the junction north of
    /// Porchlight, an empty slot of yours (a vacant plot the road runs through), or a
    /// stretch of highway. You drive in one at a time; next to each other they overlap
    /// a little, so the car is on both while it crosses.
    /// </summary>
    public sealed class Space
    {
        public int id;
        public SpaceKind kind;
        public string name;
        /// <summary>World tile of the grid's (0, 0) corner, and its size.</summary>
        public int x0, y0, width, height;
        /// <summary>The town's (Town), or which slot (Vacant).</summary>
        public Place place;
        public string townKey;
        /// <summary>In your own part of the county (north of the line).</summary>
        public bool isPrivate;
        /// <summary>Road spaces: the sides with a road out (Junction, Vacant).</summary>
        public List<Side> sides = new List<Side>();
        /// <summary>Highway: which, and the stretch of it (arc length, tiles) this space holds.</summary>
        public int highway = -1;
        public double s0, s1;

        public bool Contains(double x, double y) => x >= x0 && y >= y0 && x <= x0 + width && y <= y0 + height;
        public Vec2 ToLocal(Vec2 world) => new Vec2(world.x - x0, world.y - y0);
        public Vec2 ToWorld(Vec2 local) => new Vec2(local.x + x0, local.y + y0);
        public bool IsRoad => kind != SpaceKind.Town;
    }

    /// <summary>One end of a highway: a space and the side of it the road leaves by.</summary>
    public struct Port
    {
        public int space;
        public Side side;
    }

    public sealed class Highway
    {
        public int id;
        public string name;
        public Port a, b;
        /// <summary>The road's middle line, world tiles, a little under a tile apart, from a to b.</summary>
        public List<Vec2> points;
        /// <summary>Arc length at each point.</summary>
        public List<double> at;
        public double length;
        public bool isPrivate;
        /// <summary>Its stretches, from a to b (space ids).</summary>
        public List<int> segments = new List<int>();
    }

    public sealed class CountyMap
    {
        /// <summary>Every town sits in the middle of a slot this big (the biggest map there is).</summary>
        public const int SLOT = 256;
        /// <summary>The countryside between slots, east-west and north-south (tiles).</summary>
        public const int GAP = 400;
        /// <summary>The junction north of Porchlight: a crossroads this big.</summary>
        public const int JUNCTION = 48;
        /// <summary>An empty slot of yours: a plot this big in its middle, with the road through it.</summary>
        public const int VACANT = 64;
        /// <summary>A highway is cut into stretches at most this long (tiles), each a space of its own.</summary>
        public const double STRETCH = 180;
        /// <summary>Countryside either side of a highway's stretch, in its space (tiles).</summary>
        public const int VERGE = 14;
        /// <summary>
        /// How far a highway's first and last stretches reach into the spaces at its ends (tiles): a
        /// car can't quite reach a town's edge (it stops a little short), so the two overlap there,
        /// and the car is on both while it crosses.
        /// </summary>
        public const int INSET = 3;
        /// <summary>How far out from the road a highway still counts as its own room's (tiles); further, the countryside's nearest.</summary>
        public const double CORRIDOR = 24;
        /// <summary>The top of the row of your own slots (world y).</summary>
        public const int MINE_ROW = -(SLOT + GAP + SLOT);
        /// <summary>The junction's top edge (world y): half way between Porchlight and your slots.</summary>
        public const int JUNCTION_Y = (MINE_ROW + SLOT) / 2 - JUNCTION / 2;
        /// <summary>North of this (world y), it's your own part of the county: half way up the road to the junction.</summary>
        public const double PRIVATE_LINE = (JUNCTION_Y + JUNCTION) / 2.0;
        /// <summary>The room for the junction and your empty slots (with the account's id after it).</summary>
        public const string NORTH = "north";
        /// <summary>How far apart the points along a highway are (tiles).</summary>
        const double SAMPLE = 0.75;

        public readonly List<Space> spaces = new List<Space>();
        public readonly List<Highway> highways = new List<Highway>();
        /// <summary>The towns by where they are (only the ones there are).</summary>
        public readonly Dictionary<Place, Space> towns = new Dictionary<Place, Space>();
        public Space junction;
        /// <summary>The north room's name for this account ("north:" and its id).</summary>
        public string northRoom = NORTH;

        /// <summary>The corner of each place's slot (world tiles).</summary>
        public static (int x, int y) SlotOrigin(Place p)
        {
            const int step = SLOT + GAP;
            switch (p)
            {
                case Place.Porchlight: return (0, 0);
                case Place.East: return (step, 0);
                case Place.South: return (0, step);
                case Place.West: return (-step, 0);
                case Place.Mine1: return (-step, MINE_ROW);
                case Place.Mine2: return (0, MINE_ROW);
                default: return (step, MINE_ROW);
            }
        }

        public static bool IsMine(Place p) => p >= Place.Mine1;

        /// <summary>Which of your slots this is (0-2), or -1.</summary>
        public static int SlotOf(Place p) => IsMine(p) ? (int)p - (int)Place.Mine1 : -1;

        public static Place MineSlot(int slot) => (Place)((int)Place.Mine1 + slot);

        /// <summary>The sides each slot's road comes and goes by: for a town there, the gates that have highways.</summary>
        public static Side[] SidesOf(Place p)
        {
            switch (p)
            {
                case Place.Porchlight: return new[] { Side.North, Side.East, Side.South, Side.West };
                case Place.East: return new[] { Side.West, Side.South };
                case Place.South: return new[] { Side.North, Side.East, Side.West };
                case Place.West: return new[] { Side.East, Side.South };
                case Place.Mine1: return new[] { Side.South, Side.East };
                case Place.Mine2: return new[] { Side.South, Side.West, Side.East };
                default: return new[] { Side.South, Side.West };
            }
        }

        /// <summary>
        /// The county with these towns in it (Porchlight and the public cities, and
        /// whichever of your slots have a town). `account` names the north room.
        /// </summary>
        public static CountyMap Build(IEnumerable<PlacedTown> placed, string account = null)
        {
            var map = new CountyMap();
            if (!string.IsNullOrEmpty(account)) map.northRoom = NORTH + ":" + account;
            var byPlace = new Dictionary<Place, PlacedTown>();
            foreach (var t in placed) byPlace[t.place] = t;
            if (!byPlace.ContainsKey(Place.Porchlight)) throw new ArgumentException("the county needs Porchlight");

            foreach (Place p in Enum.GetValues(typeof(Place)))
            {
                var (sx, sy) = SlotOrigin(p);
                if (byPlace.TryGetValue(p, out var t))
                {
                    int w = Math.Max(16, Math.Min(SLOT, t.width)), h = Math.Max(16, Math.Min(SLOT, t.height));
                    var s = map.Add(new Space
                    {
                        kind = SpaceKind.Town, name = t.name, place = p, townKey = t.key, isPrivate = IsMine(p),
                        x0 = sx + (SLOT - w) / 2, y0 = sy + (SLOT - h) / 2, width = w, height = h,
                    });
                    map.towns[p] = s;
                }
                else if (IsMine(p))
                {
                    var s = map.Add(new Space
                    {
                        kind = SpaceKind.Vacant, name = "Vacant plot", place = p, isPrivate = true,
                        x0 = sx + (SLOT - VACANT) / 2, y0 = sy + (SLOT - VACANT) / 2, width = VACANT, height = VACANT,
                    });
                    s.sides.AddRange(SidesOf(p));
                }
            }
            // The junction: half way up from Porchlight's north gate to the slot north of it.
            var (px, _) = SlotOrigin(Place.Porchlight);
            int jy = JUNCTION_Y;
            map.junction = map.Add(new Space
            {
                kind = SpaceKind.Junction, name = "North junction", isPrivate = true,
                x0 = px + (SLOT - JUNCTION) / 2, y0 = jy, width = JUNCTION, height = JUNCTION,
            });
            map.junction.sides.AddRange(new[] { Side.North, Side.East, Side.South, Side.West });

            Space At(Place p) => map.SlotSpace(p);
            int J = map.junction.id;
            // Public: the spokes and the rim.
            map.Connect("Porchlight – East", At(Place.Porchlight), Side.East, At(Place.East), Side.West, false);
            map.Connect("Porchlight – South", At(Place.Porchlight), Side.South, At(Place.South), Side.North, false);
            map.Connect("Porchlight – West", At(Place.Porchlight), Side.West, At(Place.West), Side.East, false);
            map.Connect("East – South", At(Place.East), Side.South, At(Place.South), Side.East, false);
            map.Connect("South – West", At(Place.South), Side.West, At(Place.West), Side.South, false);
            // North: up to the junction (half public), and round your own towns.
            map.Connect("North road", At(Place.Porchlight), Side.North, map.spaces[J], Side.South, false);
            map.Connect("Junction – " + At(Place.Mine1).name, map.spaces[J], Side.West, At(Place.Mine1), Side.South, true);
            map.Connect("Junction – " + At(Place.Mine2).name, map.spaces[J], Side.North, At(Place.Mine2), Side.South, true);
            map.Connect("Junction – " + At(Place.Mine3).name, map.spaces[J], Side.East, At(Place.Mine3), Side.South, true);
            map.Connect(At(Place.Mine1).name + " – " + At(Place.Mine2).name, At(Place.Mine1), Side.East, At(Place.Mine2), Side.West, true);
            map.Connect(At(Place.Mine2).name + " – " + At(Place.Mine3).name, At(Place.Mine2), Side.East, At(Place.Mine3), Side.West, true);
            return map;
        }

        /// <summary>The town in a place, or the vacant plot in its stead.</summary>
        public Space SlotSpace(Place p)
        {
            if (towns.TryGetValue(p, out var t)) return t;
            foreach (var s in spaces) if (s.kind == SpaceKind.Vacant && s.place == p) return s;
            return null;
        }

        Space Add(Space s)
        {
            s.id = spaces.Count;
            spaces.Add(s);
            return s;
        }

        /// <summary>Where a space's road leaves by that side (world).</summary>
        public static Vec2 PortPoint(Space s, Side side)
        {
            var e = Compass.GateEdge(side, s.width, s.height);
            return new Vec2(s.x0 + e.x, s.y0 + e.y);
        }

        /// <summary>
        /// A highway from one space's side to another's: a cubic Bézier leaving each
        /// straight out of its gate, cut into stretches of at most STRETCH tiles.
        /// </summary>
        void Connect(string name, Space from, Side fromSide, Space to, Side toSide, bool isPrivate)
        {
            if (from == null || to == null) return;
            var p0 = PortPoint(from, fromSide);
            var p3 = PortPoint(to, toSide);
            var d0 = Compass.Outward(fromSide);
            var d3 = Compass.Outward(toSide);
            double chord = Geo.Dist(p0, p3);
            bool straight = Math.Abs(d0.x + d3.x) < 1e-9 && Math.Abs(d0.y + d3.y) < 1e-9 && Math.Abs((p3.x - p0.x) * d0.y - (p3.y - p0.y) * d0.x) < 1e-9;
            double k = straight ? chord / 3 : chord * 0.5523;
            var p1 = new Vec2(p0.x + d0.x * k, p0.y + d0.y * k);
            var p2 = new Vec2(p3.x + d3.x * k, p3.y + d3.y * k);
            var pts = SampleBezier(p0, p1, p2, p3, SAMPLE, highways.Count % 2 == 0 ? 1 : -1);
            var at = new List<double> { 0 };
            for (int i = 1; i < pts.Count; i++) at.Add(at[i - 1] + Geo.Dist(pts[i - 1], pts[i]));
            var hw = new Highway
            {
                id = highways.Count, name = name, a = new Port { space = from.id, side = fromSide }, b = new Port { space = to.id, side = toSide },
                points = pts, at = at, length = at[at.Count - 1], isPrivate = isPrivate,
            };
            highways.Add(hw);
            int n = Math.Max(1, (int)Math.Ceiling(hw.length / STRETCH));
            for (int k2 = 0; k2 < n; k2++)
            {
                double s0 = hw.length * k2 / n, s1 = hw.length * (k2 + 1) / n;
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach (var q in Stretch(hw, s0, s1))
                {
                    minX = Math.Min(minX, q.x); maxX = Math.Max(maxX, q.x);
                    minY = Math.Min(minY, q.y); maxY = Math.Max(maxY, q.y);
                }
                int x0 = (int)Math.Floor(minX - VERGE), y0 = (int)Math.Floor(minY - VERGE);
                int x1 = (int)Math.Ceiling(maxX + VERGE), y1 = (int)Math.Ceiling(maxY + VERGE);
                // Not into the spaces at either end: a highway's ground starts at their edge.
                if (k2 == 0) Clip(ref x0, ref y0, ref x1, ref y1, p0, fromSide);
                if (k2 == n - 1) Clip(ref x0, ref y0, ref x1, ref y1, p3, toSide);
                double mid = (s0 + s1) / 2;
                var seg = Add(new Space
                {
                    kind = SpaceKind.Highway, name = name, highway = hw.id, s0 = s0, s1 = s1,
                    isPrivate = isPrivate || PointAt(hw, mid).y < PRIVATE_LINE,
                    x0 = x0, y0 = y0, width = x1 - x0, height = y1 - y0,
                });
                hw.segments.Add(seg.id);
            }
        }

        static void Clip(ref int x0, ref int y0, ref int x1, ref int y1, Vec2 gate, Side side)
        {
            switch (side)
            {
                case Side.North: y1 = Math.Min(y1, (int)Math.Round(gate.y) + INSET); break;
                case Side.South: y0 = Math.Max(y0, (int)Math.Round(gate.y) - INSET); break;
                case Side.East: x0 = Math.Max(x0, (int)Math.Round(gate.x) - INSET); break;
                default: x1 = Math.Min(x1, (int)Math.Round(gate.x) + INSET); break;
            }
        }

        /// <summary>How far a highway winds either side of its curve, at most (tiles), and about how long each S is.</summary>
        public const double WIND = 48, WIND_LENGTH = 380;
        /// <summary>The tightest the wind alone may bend (tiles of radius): the curve and the fade at the ends bend it a little more.</summary>
        const double BEND = 110;

        /// <summary>
        /// One S of the wind, p in [0, 1): a smooth wave of parabolas, +1 at a quarter and -1 at three
        /// quarters. Only sums and products (no sines), so the server (county.ts) lays it out the same.
        /// </summary>
        public static double Wave(double p) => p < 0.5 ? 16 * p * (0.5 - p) : -16 * (p - 0.5) * (1 - p);

        /// <summary>
        /// Points along the curve about `step` apart (by arc length), first and last exactly on its ends.
        /// `wind` (+1 or -1, which way it starts; 0 for none): winding either side of the curve as it goes,
        /// straight out of its ends still.
        /// </summary>
        public static List<Vec2> SampleBezier(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double step, double wind = 0)
        {
            const int FINE = 4096;
            var fine = new List<Vec2>(FINE + 1);
            var acc = new List<double>(FINE + 1);
            for (int i = 0; i <= FINE; i++)
            {
                double t = (double)i / FINE, u = 1 - t;
                double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
                fine.Add(new Vec2(a * p0.x + b * p1.x + c * p2.x + d * p3.x, a * p0.y + b * p1.y + c * p2.y + d * p3.y));
                acc.Add(i == 0 ? 0 : acc[i - 1] + Geo.Dist(fine[i - 1], fine[i]));
            }
            if (wind != 0)
            {
                // Sideways off the curve: a whole number of S's, faded in and out (no turn at the gates).
                double length = acc[FINE];
                int waves = Math.Max(1, (int)Math.Floor(length / WIND_LENGTH + 0.5));
                double wave = length / waves;
                double amp = wind * Math.Min(WIND, wave * wave / (32 * BEND));
                var wound = new List<Vec2>(FINE + 1);
                for (int i = 0; i <= FINE; i++)
                {
                    var prev = fine[Math.Max(0, i - 1)];
                    var next = fine[Math.Min(FINE, i + 1)];
                    double tx = next.x - prev.x, ty = next.y - prev.y;
                    double len = Math.Sqrt(tx * tx + ty * ty);
                    double u = acc[i] / length;
                    double fade = 4 * u * (1 - u);
                    fade *= fade;
                    double phase = u * waves;
                    double off = amp * fade * Wave(phase - Math.Floor(phase));
                    wound.Add(len > 0 ? new Vec2(fine[i].x - ty / len * off, fine[i].y + tx / len * off) : fine[i]);
                }
                fine = wound;
                fine[0] = p0;
                fine[FINE] = p3;
                for (int i = 1; i <= FINE; i++) acc[i] = acc[i - 1] + Geo.Dist(fine[i - 1], fine[i]);
            }
            double total = acc[FINE];
            int n = Math.Max(1, (int)Math.Ceiling(total / step));
            var o = new List<Vec2>(n + 1) { p0 };
            int j = 0;
            for (int k = 1; k < n; k++)
            {
                double want = total * k / n;
                while (j < FINE - 1 && acc[j + 1] < want) j++;
                double span = acc[j + 1] - acc[j];
                double f = span > 0 ? (want - acc[j]) / span : 0;
                o.Add(new Vec2(fine[j].x + (fine[j + 1].x - fine[j].x) * f, fine[j].y + (fine[j + 1].y - fine[j].y) * f));
            }
            o.Add(p3);
            return o;
        }

        /// <summary>The highway's point at arc length s.</summary>
        public static Vec2 PointAt(Highway hw, double s)
        {
            if (s <= 0) return hw.points[0];
            if (s >= hw.length) return hw.points[hw.points.Count - 1];
            int lo = 0, hi = hw.at.Count - 1;
            while (hi - lo > 1)
            {
                int m = (lo + hi) / 2;
                if (hw.at[m] <= s) lo = m; else hi = m;
            }
            double f = (s - hw.at[lo]) / Math.Max(1e-9, hw.at[hi] - hw.at[lo]);
            return new Vec2(hw.points[lo].x + (hw.points[hi].x - hw.points[lo].x) * f, hw.points[lo].y + (hw.points[hi].y - hw.points[lo].y) * f);
        }

        /// <summary>The highway's line from s0 to s1, ends included.</summary>
        public static List<Vec2> Stretch(Highway hw, double s0, double s1)
        {
            var o = new List<Vec2> { PointAt(hw, s0) };
            for (int i = 0; i < hw.points.Count; i++) if (hw.at[i] > s0 + 1e-6 && hw.at[i] < s1 - 1e-6) o.Add(hw.points[i]);
            o.Add(PointAt(hw, s1));
            return o;
        }

        /// <summary>The nearest point of the highway: its arc length there and how far off it is.</summary>
        public static (double s, double dist) Nearest(Highway hw, Vec2 p)
        {
            double best = double.PositiveInfinity, bestS = 0;
            for (int i = 1; i < hw.points.Count; i++)
            {
                var a = hw.points[i - 1];
                var b = hw.points[i];
                double dx = b.x - a.x, dy = b.y - a.y;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2)) : 0;
                double qx = a.x + dx * t, qy = a.y + dy * t;
                double d = Numbers.Hypot(p.x - qx, p.y - qy);
                if (d < best)
                {
                    best = d;
                    bestS = hw.at[i - 1] + (hw.at[i] - hw.at[i - 1]) * t;
                }
            }
            return (bestS, best);
        }

        /// <summary>Your own part of the county (north of the line): your towns there, nobody else's.</summary>
        public static bool IsPrivateGround(Vec2 world) => world.y < PRIVATE_LINE;

        /// <summary>A town, the junction or a plot takes over this far inside its edge (tiles)...</summary>
        public const double ENTER = 1.5;
        /// <summary>...and keeps you till you're this close to it (a car stops 0.55 short of a town's edge).</summary>
        public const double STAY = 0.75;

        static bool Inside(Space s, Vec2 p, double inset) =>
            p.x >= s.x0 + inset && p.y >= s.y0 + inset && p.x <= s.x0 + s.width - inset && p.y <= s.y0 + s.height - inset;

        /// <summary>
        /// The space to be in at this point: a town, the junction or a plot you're well inside;
        /// else the stretch of highway you're on (where it overlaps a town's edge too); else the
        /// town you're at the edge of; else null (the countryside, where only a plane goes).
        /// `current` keeps you while it still holds you, so nothing flickers back and forth.
        /// </summary>
        public Space SpaceAt(Vec2 world, Space current = null)
        {
            if (current != null && current.kind != SpaceKind.Highway && Inside(current, world, STAY)) return current;
            foreach (var s in spaces) if (s.kind != SpaceKind.Highway && Inside(s, world, ENTER)) return s;
            Space best = null;
            double bestD = double.PositiveInfinity;
            foreach (var hw in highways)
            {
                var (sAt, d) = Nearest(hw, world);
                if (d >= bestD) continue;
                foreach (int id in hw.segments)
                {
                    var seg = spaces[id];
                    if (sAt >= seg.s0 - 1e-6 && sAt <= seg.s1 + 1e-6 && seg.Contains(world.x, world.y))
                    {
                        best = seg;
                        bestD = d;
                        break;
                    }
                }
            }
            if (best != null && current != null && current.kind == SpaceKind.Highway && current.Contains(world.x, world.y) && current.highway == best.highway && best.id != current.id)
            {
                // Along the same highway, hand over a little past the cut, not on it.
                var (sAt, _) = Nearest(highways[current.highway], world);
                if (sAt > current.s0 - HANDOVER && sAt < current.s1 + HANDOVER) return current;
            }
            if (best != null) return best;
            if (current != null && current.Contains(world.x, world.y)) return current;
            foreach (var s in spaces) if (s.kind != SpaceKind.Highway && s.Contains(world.x, world.y)) return s;
            return null;
        }

        /// <summary>How far past the cut between two stretches (tiles) you go before the next one takes over.</summary>
        public const double HANDOVER = 3;

        /// <summary>The room a space belongs to (its own town's, or the north room).</summary>
        public string RoomOf(Space s) => s.kind == SpaceKind.Town ? s.townKey : northRoom;

        /// <summary>
        /// The room whose ground this is: a town's own, the north room's (the junction
        /// and your plots), each highway's near half to the room at that end, and the
        /// countryside to the nearest of those in the same part of the county.
        /// </summary>
        public string RoomAt(Vec2 world)
        {
            foreach (var s in spaces) if (s.kind != SpaceKind.Highway && s.Contains(world.x, world.y)) return RoomOf(s);
            bool mine = IsPrivateGround(world);
            Highway near = null;
            double nearD = double.PositiveInfinity, nearS = 0;
            foreach (var hw in highways)
            {
                var (sAt, d) = Nearest(hw, world);
                if (d < nearD)
                {
                    near = hw;
                    nearD = d;
                    nearS = sAt;
                }
            }
            if (near != null && nearD <= CORRIDOR) return RoomOf(spaces[nearS < near.length / 2 ? near.a.space : near.b.space]);
            // The countryside: the nearest town or plot on this side of the line.
            Space best = null;
            double bestD = double.PositiveInfinity;
            foreach (var s in spaces)
            {
                if (s.kind == SpaceKind.Highway || s.isPrivate != mine) continue;
                double dx = Math.Max(0, Math.Max(s.x0 - world.x, world.x - (s.x0 + s.width)));
                double dy = Math.Max(0, Math.Max(s.y0 - world.y, world.y - (s.y0 + s.height)));
                double d = Numbers.Hypot(dx, dy);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best != null ? RoomOf(best) : RoomOf(towns[Place.Porchlight]);
        }

        /// <summary>Where a room's positions are measured from (world): its town's corner, or the county's for the north room.</summary>
        public Vec2 RoomOrigin(string room)
        {
            foreach (var s in spaces) if (s.kind == SpaceKind.Town && s.townKey == room) return new Vec2(s.x0, s.y0);
            return new Vec2(0, 0);
        }

        /// <summary>The spaces next to this one (sharing a highway end or a cut).</summary>
        public List<Space> Neighbours(Space s)
        {
            var o = new List<Space>();
            foreach (var hw in highways)
            {
                for (int k = 0; k < hw.segments.Count; k++)
                {
                    int id = hw.segments[k];
                    if (id == s.id)
                    {
                        o.Add(spaces[k == 0 ? hw.a.space : hw.segments[k - 1]]);
                        o.Add(spaces[k == hw.segments.Count - 1 ? hw.b.space : hw.segments[k + 1]]);
                    }
                }
                if (hw.a.space == s.id) o.Add(spaces[hw.segments[0]]);
                if (hw.b.space == s.id) o.Add(spaces[hw.segments[hw.segments.Count - 1]]);
            }
            return o;
        }

        /// <summary>How far this point is from a space's rectangle (0 inside).</summary>
        public static double DistanceTo(Space s, Vec2 world)
        {
            double dx = Math.Max(0, Math.Max(s.x0 - world.x, world.x - (s.x0 + s.width)));
            double dy = Math.Max(0, Math.Max(s.y0 - world.y, world.y - (s.y0 + s.height)));
            return Numbers.Hypot(dx, dy);
        }

        /// <summary>The towns that come with the game, besides Porchlight: their places and keys (server/towns/*.json).</summary>
        public static readonly PlacedTown[] Cities =
        {
            new PlacedTown { place = Place.East, name = "Lanternport", key = "seed=lanternport&width=224&height=192&waterFraction=0.18&riverCount=1&townRadius=0.45&roadDensity=0.95&buildingDensity=0.9&innerCity=0.6&towerFloors=12", width = 224, height = 192 },
            new PlacedTown { place = Place.South, name = "Ember Crossing", key = "seed=ember-crossing&width=256&height=256&waterFraction=0.04&riverCount=1&townRadius=0.5&roadDensity=1&culDeSacs=0.1&buildingDensity=1&innerCity=1&towerFloors=16&towerShare=0.8&driveways=0.3", width = 256, height = 256 },
            new PlacedTown { place = Place.West, name = "Juniper Falls", key = "seed=juniper-falls&width=144&height=160&noiseScale=64&waterFraction=0.1&rockSlope=0.04&townRadius=0.42&maxRoadSlope=0.06&roadDensity=0.7&buildingDensity=0.75&gardens=0.8", width = 144, height = 160 },
        };
    }
}
