// The town's roads as the town's life drives them: each road's two lanes (one each
// way), the movements from lane to lane through each junction (each with its own
// curve and length, so a car partway round a corner is somewhere), which movements
// can't happen at once, how each junction is controlled (free, stop signs or
// signals), the kerb spots along each lane, the driveways, every building's way in
// and the town's borders (the gates, where the highways come in). Made once with the
// town; read-only after. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Life
{
    /// <summary>Yield: a roundabout, where traffic entering gives way to traffic already going round.</summary>
    public enum Control { Free, Stop, AllWayStop, Signal, Yield }

    /// <summary>Which way a movement turns, as its driver sees it (they drive on the right).</summary>
    public enum Turn { Straight, Right, Left, UTurn }

    public sealed class Lane
    {
        public int id, road, dir;
        public int fromNode, toNode;
        /// <summary>The road's length (tiles).</summary>
        public double roadLength;
        /// <summary>Where along the road (from this lane's starting end) its queue starts and ends: the rest is the turns'.</summary>
        public double start, end;
        /// <summary>Speed limit (tiles a second).</summary>
        public double speed;
        /// <summary>Its road's rank (Rank): arterials 3, collectors 2, local streets 1.</summary>
        public int rank;
        public readonly List<int> outs = new List<int>();
        /// <summary>Its kerb spots (ids into RoadNet.spots), in order along it.</summary>
        public readonly List<int> spots = new List<int>();
        /// <summary>Ends at a border: a car at its end leaves the map.</summary>
        public bool exit;
        /// <summary>Starts at a border: cars come in here.</summary>
        public bool entry;
        /// <summary>
        /// Its place across the road (the junction design, docs/street-editor.md §6): slot 0 the kerb lane (where
        /// cars park and stop for doors), 1 the lane inside it on a road with two lanes each way.
        /// </summary>
        public int slot;
        /// <summary>How far out from the road's centre line its middle runs (tiles), toward its own side.</summary>
        public double offset;
        /// <summary>A turn pocket: only `pocketTurn` turns from it.</summary>
        public bool pocket;
        public Turn pocketTurn;
        /// <summary>The through lane past a pocket's fan (none of its turns go from here), or a pocket.</summary>
        public bool afterFan;
        /// <summary>The wrong way of a one-way road: nobody drives it.</summary>
        public bool closed;
        public double Span => Math.Max(0, end - start);
        /// <summary>The lane its road's spots, doors and driveways are along (LaneOf's).</summary>
        public bool Kerb => slot == 0 && !afterFan && !closed;
    }

    public sealed class Movement
    {
        public int id, node, from, to;
        /// <summary>Index among its junction's movements.</summary>
        public int local;
        public double length, speed;
        public bool uturn;
        /// <summary>Straight on, right, left or back.</summary>
        public Turn turn;
        /// <summary>The signal stage it goes in (0 or 1), at a signal.</summary>
        public int stage;
        /// <summary>From a stop sign onto a bigger road: routes avoid these.</summary>
        public bool unprotected;
        /// <summary>Must stop before going.</summary>
        public bool stops;
        /// <summary>At a signal (routes count the wait).</summary>
        public bool signal;
        /// <summary>A slip road's free right turn round a signal: it gives way to whatever it merges with, and never waits for a green.</summary>
        public bool slip;
        /// <summary>At a signal with a protected left: held for the first LEAD_MS of its green while the opposing lefts go.</summary>
        public bool held;
        /// <summary>Round a roundabout: the angle it joins the ring at, and how far round it goes (radians, the way traffic circulates).</summary>
        public double ringIn, ringSweep;
        public List<Vec2> points;
        public List<double> at;
    }

    public sealed class Junction
    {
        public int node;
        public Vec2 at;
        public Control control;
        public readonly List<int> movements = new List<int>();
        /// <summary>conflicts[a][b]: movements a and b (local indices) can't both be under way.</summary>
        public bool[][] conflicts;
        /// <summary>The roads that meet here.</summary>
        public readonly List<int> legs = new List<int>();
        /// <summary>Each leg's signal stage.</summary>
        public readonly List<int> legStage = new List<int>();
        /// <summary>
        /// Each leg's say at a stop-sign junction: its road's rank, and at a T of equal roads, a half more for the two
        /// that run straight through (the stem stops for them). Lower than the highest: it stops.
        /// </summary>
        public readonly List<double> legPriority = new List<double>();
        /// <summary>Where in the signal cycle it starts (ms).</summary>
        public long offset;
        /// <summary>
        /// Each signal stage's green (ms, its amber the last StreetFurniture.AMBER_MS of it): STAGE_MS each to start,
        /// then split by Webster's method from the traffic measured (Signals.cs). The cycle is the two together.
        /// </summary>
        public readonly long[] green = { RoadNet.STAGE_MS, RoadNet.STAGE_MS };
        /// <summary>A pocket's fan: where a lane splits into its through lane and its pocket (no real junction: no crossing, no signs).</summary>
        public bool fan;
        /// <summary>A roundabout (Control.Yield), its ring's radius.</summary>
        public bool roundabout;
        public double radius;
    }

    /// <summary>Where a car parks: at the kerb, in a house's driveway, or in a car park's bay.</summary>
    public enum SpotKind { Kerb, Driveway, CarPark }

    public sealed class ParkingSpot
    {
        public int id;
        public SpotKind kind;
        public int lane;
        /// <summary>Where along the lane's road a car stops for it (from the lane's start).</summary>
        public double s;
        /// <summary>Where the car stands parked, and where its driver gets in and out.</summary>
        public Vec2 at;
        /// <summary>A driveway's house (building id); -1 at the kerb or in a car park.</summary>
        public int building = -1;
        /// <summary>A car the town draws there anyway (Layout's parked cars): never free.</summary>
        public bool scenery;
    }

    /// <summary>A building's way in: its front door, and the road in front.</summary>
    public sealed class Access
    {
        public int building;
        public Vec2 door;
        public int road;
        /// <summary>Along the road (its own direction) to the point opposite the door.</summary>
        public double s;
        /// <summary>Its driveway spot, or -1.</summary>
        public int driveway = -1;
    }

    public sealed class Border
    {
        public int id, node;
        public Vec2 at;
        /// <summary>Cars come in on this lane and leave on that.</summary>
        public int entry, exit;
    }

    public sealed class RoadNet
    {
        /// <summary>The fastest a car turns (radians a second): sets how slowly it takes a tight bend in a lane.</summary>
        public const double SWING = 1.4;
        /// <summary>
        /// How hard a driver corners at a junction (tiles a second squared): 0.38 g, a little brisker than the 0.32 g
        /// side friction AASHTO's Green Book designs intersection curves for at 15 mph (a game's town is smaller and
        /// quicker than a real one; a tile's about 4 m: 55 km/h is 3.85 tiles a second).
        /// </summary>
        public const double CORNERING = 0.38 * 9.81 / 3.97;
        /// <summary>How far along a lane its bends are measured over (tiles: about a car's length).</summary>
        const double BEND = 1.2;
        /// <summary>A car's length, and the step down a lane, as its bends are measured (tiles).</summary>
        const double CAR = 1.1, STEP = 0.05;
        /// <summary>How far either side of a point a lane's way along is taken over (tiles).</summary>
        const double TANGENT = 0.6;
        /// <summary>Speed limits (tiles a second): about 61 km/h and 37 km/h (a fifth over real towns' 50 and 30, which look slow at a game's scale; cars look ahead to slow in time, Cars.Arrival).</summary>
        public const double MAIN_SPEED = 4.3, STREET_SPEED = 2.6;
        /// <summary>Round a corner at this share of the slower road's limit; a U-turn at this.</summary>
        public const double TURN_SHARE = 0.6, UTURN_SPEED = 0.6;
        /// <summary>How far either side of a junction a turn's curve starts (tiles).</summary>
        const double CORNER = 0.9;
        /// <summary>How far back from a dead end a U-turn starts (tiles).</summary>
        const double UTURN = 1.4;
        /// <summary>A dead end shorter than this is a stub: nobody drives it.</summary>
        const double SHORTEST_DEAD_END = 4;
        /// <summary>Kerb spots: the parking lane's slots (Layout.ParkCars'), from 2.5 tiles in, every 2.6.</summary>
        const double SPOT_FIRST = 2.5, SPOT_EVERY = 2.6;
        /// <summary>A turn of less than this (radians) is straight on.</summary>
        const double STRAIGHT_TURN = 0.5;
        /// <summary>How long a signal stage lasts.</summary>
        public const long STAGE_MS = 30_000;

        public readonly List<RoadStroke> roads;
        public readonly double[] lengths;
        readonly List<double>[] at;
        public readonly List<Lane> lanes = new List<Lane>();
        /// <summary>lane id of (road, dir): road * 2 + (dir > 0 ? 0 : 1); -1 if that road isn't driven.</summary>
        public readonly int[] laneOf;
        public readonly List<Movement> movements = new List<Movement>();
        public readonly Dictionary<int, Junction> junctions = new Dictionary<int, Junction>();
        public readonly Dictionary<int, int> degree = new Dictionary<int, int>();
        public readonly Dictionary<int, Vec2> nodeAt = new Dictionary<int, Vec2>();
        public readonly List<ParkingSpot> spots = new List<ParkingSpot>();
        public readonly Dictionary<int, Access> access = new Dictionary<int, Access>();
        public readonly List<Border> borders = new List<Border>();
        /// <summary>The junction design (null: one lane each way, controls by rank).</summary>
        readonly RoadDesign design;
        /// <summary>Fan junctions' node ids start here (the town's real nodes are below).</summary>
        public const int FAN_BASE = 1 << 24;
        /// <summary>A protected left's lead: the opposing traffic held at the start of its green (ms).</summary>
        public const long LEAD_MS = 7_000;

        public RoadNet(TownModel model, IsoLayout layout)
        {
            roads = layout.roads;
            design = model?.design != null && model.design.edges.Count == layout.roads.Count ? model.design : null;
            int n = roads.Count;
            lengths = new double[n];
            at = new List<double>[n];
            laneOf = new int[n * 2];
            for (int i = 0; i < laneOf.Length; i++) laneOf[i] = -1;
            for (int i = 0; i < n; i++)
            {
                var a = new List<double> { 0 };
                var pts = roads[i].points;
                for (int k = 1; k < pts.Count; k++) a.Add(a[k - 1] + Geo.Dist(pts[k], pts[k - 1]));
                at[i] = a;
                lengths[i] = a[a.Count - 1];
            }
            var gates = model?.gates ?? new List<Vec2>();
            // Which roads are driven: not paths, nothing too short, no stubs (but the way in from a gate).
            var allDeg = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                if (roads[i].kind == RoadKind.Path || lengths[i] < 1 || roads[i].points.Count < 2) continue;
                Bump(allDeg, roads[i].from);
                Bump(allDeg, roads[i].to);
            }
            var driven = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 1 || r.points.Count < 2) continue;
                bool deadEnd = allDeg[r.from] == 1 || allDeg[r.to] == 1;
                if (deadEnd && lengths[i] < SHORTEST_DEAD_END && !NearGate(r, gates)) continue;
                driven.Add(i);
                Bump(degree, r.from);
                Bump(degree, r.to);
                nodeAt[r.from] = r.points[0];
                nodeAt[r.to] = r.points[r.points.Count - 1];
            }
            // The borders: a dead end by each gate.
            foreach (var g in gates)
            {
                int best = -1;
                double bestD = 8;
                foreach (var kv in nodeAt)
                {
                    if (degree[kv.Key] != 1) continue;
                    double d = Geo.Dist(kv.Value, g);
                    if (d < bestD || (d == bestD && kv.Key < best))
                    {
                        bestD = d;
                        best = kv.Key;
                    }
                }
                if (best >= 0 && !borders.Exists(b => b.node == best)) borders.Add(new Border { id = borders.Count, node = best, at = nodeAt[best] });
            }
            var borderNodes = new HashSet<int>();
            foreach (var b in borders) borderNodes.Add(b.node);

            var fans = new List<(Lane t1, Lane t2, List<Lane> pockets)>();
            foreach (int i in driven)
            {
                var r = roads[i];
                var ed = design?.Edge(i);
                foreach (int dir in new[] { 1, -1 })
                {
                    int fromNode = dir > 0 ? r.from : r.to, toNode = dir > 0 ? r.to : r.from;
                    int count = ed == null ? 1 : dir > 0 ? ed.lanesFwd : ed.lanesBack;
                    var lane = new Lane
                    {
                        id = lanes.Count, road = i, dir = dir, fromNode = fromNode, toNode = toNode, roadLength = lengths[i],
                        speed = r.kind == RoadKind.Main ? MAIN_SPEED : STREET_SPEED, rank = Rank(r),
                        exit = borderNodes.Contains(toNode), entry = borderNodes.Contains(fromNode),
                        offset = KerbOffset(i), closed = count == 0,
                    };
                    lane.start = Trim(i, fromNode, borderNodes);
                    lane.end = lengths[i] - Trim(i, toNode, borderNodes);
                    laneOf[i * 2 + (dir > 0 ? 0 : 1)] = lane.id;
                    lanes.Add(lane);
                    for (int k = 1; k < count; k++)
                    {
                        lanes.Add(new Lane
                        {
                            id = lanes.Count, road = i, dir = dir, fromNode = fromNode, toNode = toNode, roadLength = lengths[i],
                            speed = lane.speed, rank = lane.rank, exit = lane.exit, entry = lane.entry, start = lane.start, end = lane.end,
                            slot = k, offset = KerbOffset(i) - k * DesignStage.LANE,
                        });
                    }
                    // Pockets at its end (a single lane's: two lanes each way share their lanes' turns instead).
                    if (ed == null || count != 1 || borderNodes.Contains(toNode) || degreeAll(allDeg, toNode) < 3) continue;
                    double longest = 0;
                    foreach (var pk in ed.pockets) if (pk.atTo == (dir > 0)) longest = Math.Max(longest, pk.length);
                    if (longest <= 0) continue;
                    double fanStart = lane.end - longest, fanEnd = fanStart - DesignStage.TAPER;
                    if (fanEnd < lane.start + 2) continue;
                    var t2 = new Lane
                    {
                        id = lanes.Count, road = i, dir = dir, fromNode = FAN_BASE + lane.id, toNode = toNode, roadLength = lengths[i],
                        speed = lane.speed, rank = lane.rank, start = fanStart, end = lane.end, offset = lane.offset, afterFan = true,
                    };
                    lanes.Add(t2);
                    var ps = new List<Lane>();
                    foreach (var pk in ed.pockets)
                    {
                        if (pk.atTo != (dir > 0)) continue;
                        var pl = new Lane
                        {
                            id = lanes.Count, road = i, dir = dir, fromNode = t2.fromNode, toNode = toNode, roadLength = lengths[i],
                            speed = lane.speed, rank = lane.rank, start = fanStart, end = lane.end, afterFan = true, pocket = true,
                            pocketTurn = pk.left ? Turn.Left : Turn.Right,
                            offset = pk.left ? Math.Max(0.1, lane.offset - 0.6) : lane.offset + Layout.PARKING * 0.9,
                        };
                        lanes.Add(pl);
                        ps.Add(pl);
                    }
                    lane.end = fanEnd;
                    lane.toNode = t2.fromNode;
                    fans.Add((lane, t2, ps));
                }
            }
            foreach (var b in borders)
            {
                foreach (var lane in lanes)
                {
                    if (lane.slot != 0) continue;
                    if (lane.fromNode == b.node) b.entry = lane.id;
                    if (lane.toNode == b.node) b.exit = lane.id;
                }
            }
            // A bendy road's taken slower: a car's length laid along the lane, stepped down it, may swing
            // round no faster than SWING, nor its nose (out on the outside of a tight corner, where the lane
            // sweeps further than the road's line does) go faster than the lane's limit.
            foreach (var lane in lanes)
            {
                double turn = 0, stretch = 1;
                Vec2 nose = LanePoint(lane, lane.start).at;
                double was = double.NaN;
                for (double d = lane.start + CAR; d <= lane.end + 1e-9; d += STEP)
                {
                    var front = LanePoint(lane, d).at;
                    var back = LanePoint(lane, d - CAR).at;
                    double h = DetMath.Atan2(front.y - back.y, front.x - back.x);
                    if (!double.IsNaN(was)) turn = Math.Max(turn, Math.Abs(Explore.Orbit.ShortestTurn(was, h)) / STEP);
                    if (d > lane.start + CAR) stretch = Math.Max(stretch, Geo.Dist(front, nose) / STEP);
                    was = h;
                    nose = front;
                }
                double limit = lane.speed / stretch;
                if (turn > 0) limit = Math.Min(limit, SWING / turn);
                lane.speed = Math.Min(lane.speed, Math.Max(UTURN_SPEED / 2, limit));
            }
            foreach (var (t1, t2, ps) in fans)
            {
                nodeAt[t2.fromNode] = LanePoint(t1, t1.end).at;
                degree[t2.fromNode] = 2;
            }
            MakeJunctions(model, borderNodes);
            foreach (var (t1, t2, ps) in fans)
            {
                var j = new Junction { node = t2.fromNode, at = nodeAt[t2.fromNode], control = Life.Control.Free, fan = true };
                j.legs.Add(t1.road);
                j.legStage.Add(0);
                j.legPriority.Add(t1.rank);
                AddMovement(j, t1, t2, uturn: false);
                foreach (var pl in ps) AddMovement(j, t1, pl, uturn: false);
                foreach (int id in j.movements) movements[id].turn = Turn.Straight;
                Conflicts(j);
                junctions[j.node] = j;
            }
            MakeSpots(model, layout);
            MakeAccess(model, layout);
        }

        static void Bump(Dictionary<int, int> d, int k) => d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;

        static int degreeAll(Dictionary<int, int> d, int k) => d.TryGetValue(k, out int v) ? v : 0;

        /// <summary>
        /// How far out from a road's centre line its kerb lane runs: a quarter of its width with one lane each way
        /// (each lane half the road), else half a lane in from its edge.
        /// </summary>
        public double KerbOffset(int road)
        {
            var r = roads[road];
            var d = design?.Edge(road);
            if (d == null || (Math.Max(d.lanesFwd, d.lanesBack) <= 1 && !d.divided && !d.OneWay)) return r.width / 4;
            return r.width / 2 - DesignStage.LANE / 2;
        }

        /// <summary>A roundabout's design at a node, or null.</summary>
        NodeDesign Ring(int node)
        {
            var nd = node >= 0 && node < FAN_BASE ? design?.Node(node) : null;
            return nd != null && nd.form == JunctionForm.Roundabout ? nd : null;
        }

        static bool NearGate(RoadStroke r, List<Vec2> gates)
        {
            foreach (var g in gates)
                if (Geo.Dist(r.points[0], g) < 8 || Geo.Dist(r.points[r.points.Count - 1], g) < 8) return true;
            return false;
        }

        /// <summary>
        /// A road's rank, by its functional class (FHWA): an arterial 3, a collector 2, a local street 1 (a main road's
        /// an arterial at least). A/B Street ranks its roads the same way, by their OSM highway type.
        /// </summary>
        public static int Rank(RoadStroke r) => r.kind == RoadKind.Main || r.cls == RoadClass.Arterial ? ARTERIAL : r.cls == RoadClass.Collector ? 2 : 1;

        public const int ARTERIAL = 3;

        /// <summary>How much of a road's end is the junction's (its turns' curves), or a dead end's U-turn's.</summary>
        double Trim(int road, int node, HashSet<int> borderNodes)
        {
            if (borderNodes.Contains(node)) return 0;
            double len = lengths[road];
            var ring = Ring(node);
            if (ring != null && degree[node] >= 3) return Math.Min(ring.radius + 0.8, len / 2);
            return degree[node] == 1 ? Math.Min(UTURN, len / 2) : Math.Min(CORNER, len / 2);
        }

        /// <summary>How far along a road each of its points is.</summary>
        public List<double> Arcs(int road) => at[road];

        public int LaneOf(int road, int dir) => laneOf[road * 2 + (dir > 0 ? 0 : 1)];

        /// <summary>Every lane of a road one way (its kerb lane, any beside it, past a fan its through lane and pockets), none closed.</summary>
        public IEnumerable<int> LanesOf(int road, int dir)
        {
            if (byRoad == null)
            {
                var all = new List<int>[roads.Count * 2];
                foreach (var l in lanes)
                {
                    if (l.closed) continue;
                    int k = l.road * 2 + (l.dir > 0 ? 0 : 1);
                    (all[k] = all[k] ?? new List<int>()).Add(l.id);
                }
                byRoad = all;
            }
            return byRoad[road * 2 + (dir > 0 ? 0 : 1)] ?? (IEnumerable<int>)Array.Empty<int>();
        }

        List<int>[] byRoad;

        public Lane Reverse(Lane l) => lanes[LaneOf(l.road, -l.dir)];

        // --- Geometry ---------------------------------------------------------------------------

        /// <summary>The middle of a lane `done` tiles along its road from the lane's starting end, and the way along it.</summary>
        public (Vec2 at, Vec2 along) LanePoint(Lane lane, double done) => LanePoint(lane.road, lane.dir, done, lane.offset - KerbOffset(lane.road));

        /// <summary>As LanePoint, `outward` tiles further out toward the kerb (0: the lane's middle).</summary>
        public (Vec2 at, Vec2 along) LanePoint(int road, int dir, double done, double outward)
        {
            var r = roads[road];
            double len = lengths[road];
            double s = dir == 1 ? done : len - done;
            s = Math.Max(0, Math.Min(len, s));
            var p = AutoDrive.PointAt(r.points, at[road], s);
            // The way along, taken across a span either side (not at a point), so the lane rounds a tight
            // corner of the road's line rather than looping back on itself on the inside of it.
            var ahead = AutoDrive.PointAt(r.points, at[road], Math.Max(0, Math.Min(len, s + TANGENT * dir)));
            var behind = AutoDrive.PointAt(r.points, at[road], Math.Max(0, Math.Min(len, s - TANGENT * dir)));
            double tx = ahead.x - behind.x, ty = ahead.y - behind.y;
            double l = Numbers.Hypot(tx, ty);
            if (l < 1e-6) return (p, new Vec2(1, 0));
            tx /= l;
            ty /= l;
            double off = KerbOffset(road) + outward;
            return (new Vec2(p.x - ty * off, p.y + tx * off), new Vec2(tx, ty));
        }

        /// <summary>
        /// A point `d` tiles round a movement's curve, and the way along it. The way turns smoothly from one of
        /// the curve's points to the next (blended between the way at each), rather than in a step at each:
        /// a car's facing, and so its front wheels, never flick round a corner.
        /// </summary>
        public (Vec2 at, Vec2 along) MovementPoint(Movement m, double d)
        {
            var pts = m.points;
            d = Math.Max(0, Math.Min(m.length, d));
            int k = 1;
            while (k < pts.Count - 1 && m.at[k] < d) k++;
            var a = pts[k - 1];
            var b = pts[k];
            double seg = m.at[k] - m.at[k - 1];
            double t = seg < 1e-9 ? 0 : (d - m.at[k - 1]) / seg;
            var ta = WayAt(pts, k - 1);
            var tb = WayAt(pts, k);
            double wx = ta.x + (tb.x - ta.x) * t, wy = ta.y + (tb.y - ta.y) * t, wl = Numbers.Hypot(wx, wy);
            var along = wl < 1e-9 ? Unit(a, b) : new Vec2(wx / wl, wy / wl);
            return (new Vec2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t), along);
        }

        /// <summary>The way along a curve at its kth point: halfway between the ways in and out of it.</summary>
        static Vec2 WayAt(List<Vec2> pts, int k)
        {
            var inWay = k > 0 ? Unit(pts[k - 1], pts[k]) : Unit(pts[k], pts[k + 1]);
            var outWay = k < pts.Count - 1 ? Unit(pts[k], pts[k + 1]) : inWay;
            double x = inWay.x + outWay.x, y = inWay.y + outWay.y, l = Numbers.Hypot(x, y);
            // A cusp (the tip of a U-turn, if ever exactly so): the way in.
            return l < 1e-9 ? inWay : new Vec2(x / l, y / l);
        }

        static Vec2 Unit(Vec2 a, Vec2 b)
        {
            double dx = b.x - a.x, dy = b.y - a.y, l = Numbers.Hypot(dx, dy);
            return l < 1e-9 ? new Vec2(1, 0) : new Vec2(dx / l, dy / l);
        }

        /// <summary>Points a turn's curve is drawn through (and a U-turn's each half): enough that a car's way round it barely bends at each.</summary>
        const int CURVE_POINTS = 16;

        // --- Junctions --------------------------------------------------------------------------

        void MakeJunctions(TownModel model, HashSet<int> borderNodes)
        {
            var inLanes = new Dictionary<int, List<Lane>>();
            var outLanes = new Dictionary<int, List<Lane>>();
            foreach (var l in lanes)
            {
                if (l.closed) continue;
                if (!inLanes.TryGetValue(l.toNode, out var a)) inLanes[l.toNode] = a = new List<Lane>();
                a.Add(l);
                if (!outLanes.TryGetValue(l.fromNode, out var b)) outLanes[l.fromNode] = b = new List<Lane>();
                b.Add(l);
            }
            var nodes = new List<int>(nodeAt.Keys);
            nodes.Sort();
            foreach (int node in nodes)
            {
                if (borderNodes.Contains(node) || node >= FAN_BASE || !inLanes.ContainsKey(node)) continue;
                var j = new Junction { node = node, at = nodeAt[node], offset = (long)(Layout.Hash(node, 77) * 2 * STAGE_MS) };
                foreach (var l in inLanes[node]) if (!j.legs.Contains(l.road)) j.legs.Add(l.road);
                if (outLanes.TryGetValue(node, out var outs0)) foreach (var l in outs0) if (!j.legs.Contains(l.road)) j.legs.Add(l.road);
                var ring = degree[node] >= 3 ? Ring(node) : null;
                if (ring != null)
                {
                    j.roundabout = true;
                    j.radius = ring.radius;
                }
                // Legs round the junction, by the way each road leaves it.
                var angle = new Dictionary<int, double>();
                foreach (int road in j.legs)
                {
                    var lane = lanes[LaneOf(road, roads[road].from == node ? 1 : -1)];
                    var (p, t) = LanePoint(lane, Math.Min(1, lengths[road] / 2));
                    angle[road] = DetMath.Atan2(t.y, t.x);
                }
                j.legs.Sort((a, b) => angle[a] != angle[b] ? angle[a].CompareTo(angle[b]) : a.CompareTo(b));
                var outHere = outLanes.TryGetValue(node, out var oh) ? oh : new List<Lane>();
                foreach (var l in inLanes[node])
                {
                    if (degree[node] == 1)
                    {
                        var back = Reverse(l);
                        if (!back.closed) AddMovement(j, l, back, uturn: true);
                        continue;
                    }
                    int before = j.movements.Count;
                    foreach (var o in outHere) if (o.road != l.road && Allowed(l, o, TurnOf(l, o))) AddMovement(j, l, o, uturn: false);
                    // Never a lane with no way on: any turn, if its own rules left it none.
                    if (j.movements.Count == before) foreach (var o in outHere) if (o.road != l.road && o.slot == 0) AddMovement(j, l, o, uturn: false);
                }
                Control(j);
                Conflicts(j);
                junctions[node] = j;
            }
        }

        /// <summary>Which way a turn from lane `a` into lane `b` goes (AddMovement's measure, from the lanes' ways at their ends).</summary>
        Turn TurnOf(Lane a, Lane b)
        {
            var fa = LanePoint(a, a.end).along;
            var fb = LanePoint(b, b.start).along;
            double angle = Math.Atan2(fa.x * fb.y - fa.y * fb.x, fa.x * fb.x + fa.y * fb.y);
            return Math.Abs(angle) < STRAIGHT_TURN ? Turn.Straight : angle > 0 ? Turn.Right : Turn.Left;
        }

        /// <summary>
        /// The lanes' turns (the MUTCD's lane-use rules, as A/B Street gives each lane its turns): a pocket only its
        /// own turn, and the through lane beside it not that turn; with two lanes, rights from the kerb lane and lefts
        /// from the inner one, straight on lane to lane; a turn into the nearest lane of the road it's going into.
        /// </summary>
        bool Allowed(Lane from, Lane to, Turn turn)
        {
            if (to.closed) return false;
            if (from.pocket) { if (turn != from.pocketTurn) return false; }
            else if (from.afterFan)
            {
                foreach (var l in lanes)
                    if (l.pocket && l.fromNode == from.fromNode && l.pocketTurn == turn) return false;
            }
            bool fromMany = Siblings(from) > 1, toMany = Siblings(to) > 1;
            if (fromMany)
            {
                if (from.slot == 0 && turn == Turn.Left) return false;
                if (from.slot > 0 && turn == Turn.Right) return false;
            }
            if (toMany)
            {
                if (turn == Turn.Right && to.slot != 0) return false;
                if (turn == Turn.Left && to.slot == 0) return false;
                if (turn == Turn.Straight && fromMany && to.slot != Math.Min(from.slot, Siblings(to) - 1)) return false;
            }
            return true;
        }

        /// <summary>How many lanes run beside this one (its road and way, not pockets, the same stretch).</summary>
        int Siblings(Lane l)
        {
            var d = design?.Edge(l.road);
            if (d == null || l.afterFan) return 1;
            return Math.Max(1, l.dir > 0 ? d.lanesFwd : d.lanesBack);
        }

        void AddMovement(Junction j, Lane from, Lane to, bool uturn)
        {
            var m = new Movement { id = movements.Count, node = j.node, from = from.id, to = to.id, uturn = uturn, local = j.movements.Count };
            var pts = new List<Vec2>();
            if (uturn)
            {
                // On from its lane before the end, round a tip just past the end (wider than the lanes, as a car
                // swings), and back to the other lane: two curves meeting at the tip.
                double reach = from.roadLength - from.end;
                var (a, f) = LanePoint(from, from.end);
                var b = LanePoint(to, to.start).at;
                double gx = b.x - a.x, gy = b.y - a.y, gap = Numbers.Hypot(gx, gy);
                if (gap < 1e-9) gap = 1e-9;
                gx /= gap;
                gy /= gap;
                var tip = new Vec2((a.x + b.x) / 2 + f.x * reach * 1.1, (a.y + b.y) / 2 + f.y * reach * 1.1);
                double k1 = reach * 0.6, k2 = Math.Max(gap / 2, reach * 0.45);
                for (int i = 0; i <= CURVE_POINTS; i++) pts.Add(Cubic(a, new Vec2(a.x + f.x * k1, a.y + f.y * k1), new Vec2(tip.x - gx * k2, tip.y - gy * k2), tip, i / (double)CURVE_POINTS));
                for (int i = 1; i <= CURVE_POINTS; i++) pts.Add(Cubic(tip, new Vec2(tip.x + gx * k2, tip.y + gy * k2), new Vec2(b.x + f.x * k1, b.y + f.y * k1), b, i / (double)CURVE_POINTS));
                m.speed = UTURN_SPEED;
                m.turn = Turn.UTurn;
            }
            else if (j.roundabout)
            {
                RingCurve(j, from, to, pts, m);
                var (_, fa) = LanePoint(from, from.end);
                var (_, fb) = LanePoint(to, to.start);
                double angle = Math.Atan2(fa.x * fb.y - fa.y * fb.x, fa.x * fb.x + fa.y * fb.y);
                m.turn = Math.Abs(angle) < STRAIGHT_TURN ? Turn.Straight : angle > 0 ? Turn.Right : Turn.Left;
                if (from.road == to.road) m.turn = Turn.UTurn;
                m.speed = Math.Min(from.speed, to.speed) * TURN_SHARE;
            }
            else
            {
                // A cubic from the end of one lane's queue to the start of the next's, leaving and arriving
                // along each lane, so a car's heading never jumps going round.
                var (a, fa) = LanePoint(from, from.end);
                var (b, fb) = LanePoint(to, to.start);
                var inEnd = LanePoint(from, from.roadLength).at;
                var outStart = LanePoint(to, 0).at;
                // Where the two lanes' lines would meet, roughly: as far along each as the corner is deep.
                double k1 = Math.Max(0.2, Geo.Dist(a, inEnd) + Geo.Dist(inEnd, outStart) / 2);
                double k2 = Math.Max(0.2, Geo.Dist(b, outStart) + Geo.Dist(inEnd, outStart) / 2);
                double gap = Geo.Dist(a, b);
                k1 = Math.Min(k1, gap * 0.75);
                k2 = Math.Min(k2, gap * 0.75);
                var p1 = new Vec2(a.x + fa.x * k1, a.y + fa.y * k1);
                var p2 = new Vec2(b.x - fb.x * k2, b.y - fb.y * k2);
                for (int i = 0; i <= CURVE_POINTS; i++) pts.Add(Cubic(a, p1, p2, b, i / (double)CURVE_POINTS));
                bool straight = fa.x * fb.x + fa.y * fb.y > 0.85;
                m.speed = Math.Min(from.speed, to.speed) * (straight ? 1 : TURN_SHARE);
                // Lane +1 drives on the side SideLane calls left of the road's direction (the tile frame is
                // mirrored): the driver's right is that side, so a turn that way is a right turn.
                double angle = Math.Atan2(fa.x * fb.y - fa.y * fb.x, fa.x * fb.x + fa.y * fb.y);
                m.turn = Math.Abs(angle) < STRAIGHT_TURN ? Turn.Straight : angle > 0 ? Turn.Right : Turn.Left;
            }
            m.points = pts;
            m.at = new List<double> { 0 };
            for (int i = 1; i < pts.Count; i++) m.at.Add(m.at[i - 1] + Geo.Dist(pts[i], pts[i - 1]));
            m.length = Math.Max(0.2, m.at[m.at.Count - 1]);
            // Round the curve no faster than a driver corners comfortably: v = sqrt(a / curvature), at the curve's
            // tightest a car's length (its wheels round it, not a point on it).
            double sharpest = 0, w = Math.Min(BEND, m.length);
            for (double d = 0; d + w <= m.length + 1e-9 && w > 1e-3; d += w / 4)
            {
                var a = MovementPoint(m, d).along;
                var b = MovementPoint(m, d + w).along;
                sharpest = Math.Max(sharpest, Math.Abs(DetMath.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y)) / w);
            }
            if (sharpest > 0) m.speed = Math.Min(m.speed, Math.Max(0.3, Math.Sqrt(CORNERING / sharpest)));
            from.outs.Add(m.id);
            movements.Add(m);
            j.movements.Add(m.id);
        }

        /// <summary>
        /// Round a roundabout: in from the lane's end onto the ring, round it the way traffic circulates (keeping
        /// right, anticlockwise as the town's drawn), and off onto the lane out. The ring's angles are kept for
        /// who gives way to whom (Conflicts, Cars.GivesWay).
        /// </summary>
        void RingCurve(Junction j, Lane from, Lane to, List<Vec2> pts, Movement m)
        {
            var c = j.at;
            double R = j.radius;
            var (a, fa) = LanePoint(from, from.end);
            var (b, fb) = LanePoint(to, to.start);
            double th0 = Math.Atan2(a.y - c.y, a.x - c.x), th1 = Math.Atan2(b.y - c.y, b.x - c.x);
            // Circulating: the tangent at r̂ is (r̂.y, −r̂.x), the way RoadNet's frame turns right (DesignStage's sign).
            double sweep = th0 - th1;
            while (sweep <= 0.15) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI + 0.15) sweep -= 2 * Math.PI;
            m.ringIn = th0;
            m.ringSweep = sweep;
            // Join the ring a little round from the entry, leave it a little before the exit.
            double join = Math.Min(0.35, sweep * 0.2), leave = Math.Min(0.35, sweep * 0.2);
            Vec2 On(double th) => new Vec2(c.x + Math.Cos(th) * R, c.y + Math.Sin(th) * R);
            Vec2 Tan(double th) => new Vec2(Math.Sin(th), -Math.Cos(th));
            var p0 = On(th0 - join);
            var t0 = Tan(th0 - join);
            double k = Math.Max(0.3, Geo.Dist(a, p0) * 0.5);
            for (int i = 0; i < CURVE_POINTS / 2; i++)
                pts.Add(Cubic(a, new Vec2(a.x + fa.x * k, a.y + fa.y * k), new Vec2(p0.x - t0.x * k, p0.y - t0.y * k), p0, i / (double)(CURVE_POINTS / 2)));
            int steps = Math.Max(2, (int)Math.Ceiling((sweep - join - leave) / 0.2));
            for (int i = 0; i <= steps; i++) pts.Add(On(th0 - join - (sweep - join - leave) * i / steps));
            var p1 = On(th0 - sweep + leave);
            var t1 = Tan(th0 - sweep + leave);
            k = Math.Max(0.3, Geo.Dist(p1, b) * 0.5);
            for (int i = 1; i <= CURVE_POINTS / 2; i++)
                pts.Add(Cubic(p1, new Vec2(p1.x + t1.x * k, p1.y + t1.y * k), new Vec2(b.x - fb.x * k, b.y - fb.y * k), b, i / (double)(CURVE_POINTS / 2)));
        }

        /// <summary>Does a ring movement, from where it joins to where it leaves, pass angle `th`?</summary>
        public static bool Passes(Movement m, double th)
        {
            double d = m.ringIn - th;
            while (d < 0) d += 2 * Math.PI;
            while (d >= 2 * Math.PI) d -= 2 * Math.PI;
            return d > 0.05 && d < m.ringSweep - 0.05;
        }

        static Vec2 Cubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double t)
        {
            double u = 1 - t;
            double b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
            return new Vec2(b0 * p0.x + b1 * p1.x + b2 * p2.x + b3 * p3.x, b0 * p0.y + b1 * p1.y + b2 * p2.y + b3 * p3.y);
        }

        /// <summary>
        /// How a junction is run (docs/town-life.md §2, from the MUTCD and A/B Street):
        /// - two roads meeting (a bend) or a dead end: free;
        /// - signals where arterials cross: three arterial legs or more, or two crossing two collectors or better (the
        ///   MUTCD's signal warrants are heavy traffic on both roads; the class stands in for the counts);
        /// - otherwise, roads of different ranks: the smaller roads stop for the bigger (MUTCD 2B.04-2B.06; A/B
        ///   Street's ControlStopSign: everything stops but the highest rank);
        /// - roads all of one rank: four legs or more, an all-way stop (MUTCD 2B.07, similar streets crossing); a T,
        ///   the stem stops for the two that run straight through.
        /// </summary>
        void Control(Junction j)
        {
            int legs = j.legs.Count;
            int arterials = 0, collectors = 0, top = 0, bottom = int.MaxValue;
            foreach (int r in j.legs)
            {
                int rank = Rank(roads[r]);
                if (rank == ARTERIAL) arterials++;
                else if (rank == 2) collectors++;
                top = Math.Max(top, rank);
                bottom = Math.Min(bottom, rank);
                j.legPriority.Add(rank);
            }
            var nd = j.node < FAN_BASE ? design?.Node(j.node) : null;
            if (legs <= 2) j.control = Life.Control.Free;
            else if (j.roundabout) j.control = Life.Control.Yield;
            else if (nd != null && nd.control != DesignControl.Auto && nd.control != DesignControl.Yield)
                j.control = nd.control == DesignControl.Free ? Life.Control.Free : nd.control == DesignControl.Stop ? Life.Control.Stop
                    : nd.control == DesignControl.AllWayStop ? Life.Control.AllWayStop : Life.Control.Signal;
            else if (arterials >= 3 || (arterials == 2 && collectors >= 2)) j.control = Life.Control.Signal;
            else if (top == bottom && legs >= 4) j.control = Life.Control.AllWayStop;
            else j.control = Life.Control.Stop;
            // Roads of one rank at a two-way stop: the straightest pair runs through, the rest stop.
            if (j.control == Life.Control.Stop && top == bottom)
            {
                var (a, b) = legs == 3 ? StraightestPair(j) : OppositePair(j);
                j.legPriority[a] += 0.5;
                j.legPriority[b] += 0.5;
            }
            // Signal stages: the legs round the junction take turns; at three legs the straightest pair goes together.
            for (int i = 0; i < legs; i++) j.legStage.Add(i % 2);
            if (legs == 3)
            {
                var (a, b) = StraightestPair(j);
                for (int i = 0; i < 3; i++) j.legStage[i] = i == a || i == b ? 0 : 1;
            }
            double most = 0;
            foreach (double p in j.legPriority) most = Math.Max(most, p);
            foreach (int id in j.movements)
            {
                var m = movements[id];
                var from = lanes[m.from];
                var to = lanes[m.to];
                int leg = j.legs.IndexOf(from.road);
                m.stage = leg >= 0 ? j.legStage[leg] : 0;
                double say = leg >= 0 ? j.legPriority[leg] : from.rank;
                m.stops = j.control == Life.Control.AllWayStop || (j.control == Life.Control.Stop && say < most);
                m.unprotected = j.control == Life.Control.Stop && from.rank < top && to.rank > from.rank;
                m.signal = j.control == Life.Control.Signal;
            }
            if (nd == null || j.control != Life.Control.Signal) return;
            // A slip road round the corner: its right turn's free of the signal.
            foreach (var slip in nd.slips)
                foreach (int id in j.movements)
                {
                    var m = movements[id];
                    if (m.turn == Turn.Right && lanes[m.from].road == slip.fromEdge && lanes[m.to].road == slip.toEdge)
                    {
                        m.slip = true;
                        m.signal = false;
                        m.stops = false;
                    }
                }
            // A protected left: the leg with the left pocket goes first, the way opposite held for the lead.
            if (!nd.protectedLeft) return;
            int pocketLeg = -1;
            foreach (int id in j.movements)
            {
                var from = lanes[movements[id].from];
                if (from.pocket && from.pocketTurn == Turn.Left) { pocketLeg = j.legs.IndexOf(from.road); break; }
            }
            if (pocketLeg < 0) return;
            var pd = LegDir(j, j.legs[pocketLeg]);
            int opposite = -1;
            double most_ = -0.5;
            for (int i = 0; i < legs; i++)
            {
                if (i == pocketLeg) continue;
                var d = LegDir(j, j.legs[i]);
                double dot = pd.x * d.x + pd.y * d.y;
                if (dot < most_) { most_ = dot; opposite = i; }
            }
            if (opposite < 0 || j.legStage[opposite] != j.legStage[pocketLeg]) return;
            foreach (int id in j.movements)
            {
                var m = movements[id];
                if (j.legs.IndexOf(lanes[m.from].road) == opposite && m.turn != Turn.Left) m.held = true;
            }
        }

        /// <summary>The two legs of a junction (any two) that run most nearly straight on into each other.</summary>
        (int a, int b) OppositePair(Junction j)
        {
            int ba = 0, bb = 1;
            double bestDot = double.PositiveInfinity;
            for (int a = 0; a < j.legs.Count; a++)
                for (int b = a + 1; b < j.legs.Count; b++)
                {
                    var da = LegDir(j, j.legs[a]);
                    var db = LegDir(j, j.legs[b]);
                    double dot = da.x * db.x + da.y * db.y;
                    if (dot < bestDot) { bestDot = dot; ba = a; bb = b; }
                }
            return (ba, bb);
        }

        /// <summary>The two legs of a three-legged junction that run most nearly straight on into each other.</summary>
        (int a, int b) StraightestPair(Junction j)
        {
            int best = 0;
            double bestDot = double.PositiveInfinity;
            for (int a = 0; a < j.legs.Count; a++)
            {
                int b = (a + 1) % j.legs.Count;
                var da = LegDir(j, j.legs[a]);
                var db = LegDir(j, j.legs[b]);
                double dot = da.x * db.x + da.y * db.y;
                if (dot < bestDot)
                {
                    bestDot = dot;
                    best = a;
                }
            }
            return (best, (best + 1) % j.legs.Count);
        }

        Vec2 LegDir(Junction j, int road)
        {
            var lane = lanes[LaneOf(road, roads[road].from == j.node ? 1 : -1)];
            return LanePoint(lane, Math.Min(1, lengths[road] / 2)).along;
        }

        /// <summary>Which movements can't be under way together: those into the same lane, and those whose curves cross (unless from the same lane: they queue).</summary>
        void Conflicts(Junction j)
        {
            int k = j.movements.Count;
            j.conflicts = new bool[k][];
            for (int a = 0; a < k; a++) j.conflicts[a] = new bool[k];
            for (int a = 0; a < k; a++)
            {
                var ma = movements[j.movements[a]];
                for (int b = a + 1; b < k; b++)
                {
                    var mb = movements[j.movements[b]];
                    bool c = j.roundabout
                        ? ma.to == mb.to || (ma.from != mb.from && (Passes(mb, ma.ringIn) || Passes(ma, mb.ringIn)))
                        : ma.to == mb.to || (ma.from != mb.from && Cross(ma.points, mb.points));
                    j.conflicts[a][b] = j.conflicts[b][a] = c;
                }
            }
        }

        static bool Cross(List<Vec2> p, List<Vec2> q)
        {
            for (int i = 1; i < p.Count; i++)
                for (int k = 1; k < q.Count; k++)
                    if (SegmentsCross(p[i - 1], p[i], q[k - 1], q[k])) return true;
            return false;
        }

        static bool SegmentsCross(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        static double Side(Vec2 a, Vec2 b, Vec2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        /// <summary>The signal stage showing green at a junction at `moment`, and when it ends.</summary>
        public static (int stage, long endsAt) Stage(Junction j, long moment) => Stage(j.green[0], j.green[1], j.offset, moment);

        /// <summary>
        /// The stage showing green at `moment` on a cycle of stage 0 for `g0` ms then stage 1 for `g1`, started at
        /// −`offset`, and when it ends. Pure: any two copies of the town with the same timing agree on it.
        /// </summary>
        public static (int stage, long endsAt) Stage(long g0, long g1, long offset, long moment)
        {
            long period = Math.Max(1, g0 + g1);
            long t = moment + offset;
            long start = (long)Math.Floor(t / (double)period) * period;
            long into = t - start;
            return into < g0 ? (0, start + g0 - offset) : (1, start + period - offset);
        }

        /// <summary>A signal's whole cycle (ms): both stages' greens.</summary>
        public static long Cycle(Junction j) => j.green[0] + j.green[1];

        /// <summary>
        /// The offset that has stage `stage` green at `moment` with `left` ms of it to go (no more than its green):
        /// a light changed early, or retimed without its stage ending sooner than it would have.
        /// </summary>
        public static long OffsetFor(Junction j, int stage, long left, long moment)
        {
            long period = Math.Max(1, Cycle(j));
            left = Math.Max(1, Math.Min(left, j.green[stage]));
            long into = (stage == 0 ? 0 : j.green[0]) + j.green[stage] - left;
            return ((into - moment) % period + period) % period;
        }

        // --- Parking ----------------------------------------------------------------------------

        /// <summary>A spot anyone may park in: the kerb or a car park (a driveway is its house's).</summary>
        public static bool Public(SpotKind k) => k != SpotKind.Driveway;

        void MakeSpots(TownModel model, IsoLayout layout)
        {
            int w = model?.width ?? 0, h = model?.height ?? 0;
            bool ByDriveway(Vec2 p)
            {
                if (layout.use == null) return false;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = (int)Math.Floor(p.x) + dx, y = (int)Math.Floor(p.y) + dy;
                        if (Grid.InBounds(x, y, w, h) && layout.use[Grid.Index(x, y, w)] == Use.DRIVEWAY) return true;
                    }
                return false;
            }
            bool Wet(Vec2 p)
            {
                if (model?.tiles == null) return false;
                int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
                return Grid.InBounds(x, y, w, h) && Terrain.IsWater(model.tiles.terrain[Grid.Index(x, y, w)]);
            }
            // Layout's parked cars, bucketed by tile, to find which slots they fill.
            var parked = new Dictionary<long, List<Vec2>>();
            foreach (var c in layout.cars ?? new List<ParkedCar>())
            {
                long key = ((long)Math.Floor(c.x) << 20) ^ (long)Math.Floor(c.y);
                if (!parked.TryGetValue(key, out var list)) parked[key] = list = new List<Vec2>();
                list.Add(new Vec2(c.x, c.y));
            }
            bool Occupied(Vec2 p)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        long key = ((long)(Math.Floor(p.x) + dx) << 20) ^ (long)(Math.Floor(p.y) + dy);
                        if (!parked.TryGetValue(key, out var list)) continue;
                        foreach (var q in list) if (Geo.Dist(p, q) < 0.4) return true;
                    }
                return false;
            }
            foreach (var lane in lanes)
            {
                if (!lane.Kerb) continue;
                var r = roads[lane.road];
                double len = lengths[lane.road];
                double outward = r.width / 2 - KerbOffset(lane.road) + Layout.PARKING / 2;
                for (double s = SPOT_FIRST; s < len - SPOT_FIRST; s += SPOT_EVERY)
                {
                    // The slot's place along the road is measured from the road's start; this lane may run the other way.
                    double done = lane.dir > 0 ? s : len - s;
                    if (done < lane.start || done > lane.end) continue;
                    var p = LanePoint(lane.road, lane.dir, done, outward).at;
                    var centre = LanePoint(lane.road, lane.dir, done, -KerbOffset(lane.road)).at;
                    if (Wet(p) || Wet(centre) || ByDriveway(p)) continue;
                    var spot = new ParkingSpot { id = spots.Count, kind = SpotKind.Kerb, lane = lane.id, s = done, at = p, scenery = Occupied(p) };
                    spots.Add(spot);
                }
                // In order along the lane.
            }
            // Car parks behind the high street (TownModel.carParks): a bay a paved tile, all entered from where the
            // lot meets its street, on that side.
            foreach (var cp in model?.carParks ?? new List<CarPark>())
            {
                if (layout.use == null || cp.lotId < 0 || cp.lotId >= model.lots.Count || cp.edgeId < 0 || cp.edgeId >= model.roads.edges.Count) continue;
                var bays = new List<int>();
                foreach (int t in Parcels.TilesIn(model.lots[cp.lotId].polygon, null, w, h)) if (layout.use[t] == Use.CAR_PARK) bays.Add(t);
                if (bays.Count == 0) continue;
                var street = model.roads.edges[cp.edgeId].points;
                int gate = bays[0];
                double gateD = double.PositiveInfinity;
                foreach (int t in bays)
                {
                    double d = Geo.DistToPolyline(new Vec2(t % w + 0.5, t / w + 0.5), street);
                    if (d < gateD)
                    {
                        gateD = d;
                        gate = t;
                    }
                }
                var mouth = new Vec2(gate % w + 0.5, gate / w + 0.5);
                int road = NearestRoad(mouth, out double rs);
                if (road < 0) continue;
                int side = SideLane(road, rs, mouth);
                if (side < 0) continue;
                var l = lanes[side];
                double done = l.dir > 0 ? rs : lengths[road] - rs;
                done = Math.Max(l.start, Math.Min(l.end, done));
                foreach (int t in bays)
                {
                    var at = new Vec2(t % w + 0.5, t / w + 0.5);
                    spots.Add(new ParkingSpot { id = spots.Count, kind = SpotKind.CarPark, lane = side, s = done, at = at, scenery = Occupied(at) });
                }
            }
            foreach (var lane in lanes) lane.spots.Clear();
            foreach (var s in spots) lanes[s.lane].spots.Add(s.id);
            foreach (var lane in lanes) lane.spots.Sort((a, b) => spots[a].s.CompareTo(spots[b].s));
        }

        // --- Buildings --------------------------------------------------------------------------

        void MakeAccess(TownModel model, IsoLayout layout)
        {
            if (layout.houses == null || lanes.Count == 0) return;
            int w = model?.width ?? 0, h = model?.height ?? 0;
            foreach (var house in layout.houses)
            {
                if (house.tiles == null || house.tiles.Count == 0) continue;
                // The middle of the front: the footprint's edge on the side it faces, half a tile out.
                var (fx, fy) = Grid.DIRS4[(int)house.facing];
                double cx = 0, cy = 0;
                foreach (var t in house.tiles)
                {
                    cx += t.x + 0.5;
                    cy += t.y + 0.5;
                }
                cx /= house.tiles.Count;
                cy /= house.tiles.Count;
                double reach = 0;
                foreach (var t in house.tiles) reach = Math.Max(reach, (t.x + 0.5 - cx) * fx + (t.y + 0.5 - cy) * fy);
                var door = new Vec2(cx + fx * (reach + 1), cy + fy * (reach + 1));
                int road = NearestRoad(door, out double s);
                if (road < 0) continue;
                var a = new Access { building = house.buildingId, door = door, road = road, s = s };
                // A driveway: its tiles beside the house.
                if (layout.use != null)
                {
                    double dx = 0, dy = 0;
                    int count = 0;
                    var seen = new HashSet<int>();
                    foreach (var t in house.tiles)
                        foreach (var (ox, oy) in Grid.DIRS4)
                        {
                            int x = t.x + ox, y = t.y + oy;
                            if (!Grid.InBounds(x, y, w, h)) continue;
                            int i = Grid.Index(x, y, w);
                            if (layout.use[i] != Use.DRIVEWAY || !seen.Add(i)) continue;
                            dx += x + 0.5;
                            dy += y + 0.5;
                            count++;
                        }
                    if (count > 0)
                    {
                        var p = new Vec2(dx / count, dy / count);
                        int dr = NearestRoad(p, out double ds);
                        if (dr >= 0)
                        {
                            // Stopped in the lane on the house's side, then in.
                            int lane = SideLane(dr, ds, p);
                            if (lane >= 0)
                            {
                                var l = lanes[lane];
                                double done = l.dir > 0 ? ds : lengths[dr] - ds;
                                done = Math.Max(l.start, Math.Min(l.end, done));
                                a.driveway = spots.Count;
                                spots.Add(new ParkingSpot { id = spots.Count, kind = SpotKind.Driveway, lane = lane, s = done, at = p, building = house.buildingId });
                            }
                        }
                    }
                }
                access[house.buildingId] = a;
            }
        }

        /// <summary>The driven road nearest `p`, and how far along it (its own direction).</summary>
        public int NearestRoad(Vec2 p, out double s)
        {
            int best = -1;
            double bestD = double.PositiveInfinity;
            s = 0;
            foreach (var lane in lanes)
            {
                if (lane.dir < 0) continue;
                double d = Geo.DistToPolyline(p, roads[lane.road].points);
                if (d < bestD)
                {
                    bestD = d;
                    best = lane.road;
                }
            }
            if (best >= 0) s = Math.Max(0, Math.Min(lengths[best], Geo.ArcPosition(p, roads[best].points)));
            return best;
        }

        /// <summary>The driven roads whose centrelines pass within `reach` of `p` plus their own half-width.</summary>
        public IEnumerable<int> RoadsNear(Vec2 p, double reach)
        {
            foreach (var lane in lanes)
            {
                if (lane.dir < 0) continue;
                if (Geo.DistToPolyline(p, roads[lane.road].points) <= roads[lane.road].width / 2 + reach) yield return lane.road;
            }
        }

        /// <summary>The lane of `road` on the same side as `p` (its kerb's lane).</summary>
        public int SideLane(int road, double s, Vec2 p)
        {
            int a = LaneOf(road, 1);
            if (a < 0) return -1;
            var (q, t) = LanePoint(lanes[a], s);
            var c = AutoDrive.PointAt(roads[road].points, at[road], s);
            // Lane +1 is to the left of the road's own direction (LanePoint's offset).
            double left = -(p.x - c.x) * t.y + (p.y - c.y) * t.x;
            int mine = left >= 0 ? a : LaneOf(road, -1), other = left >= 0 ? LaneOf(road, -1) : a;
            // The wrong way of a one-way road: the other side's lane.
            return mine >= 0 && lanes[mine].closed ? other : mine;
        }

        /// <summary>The two lanes of a building's road, its own side's first.</summary>
        public IEnumerable<int> LanesFor(Access a)
        {
            int mine = SideLane(a.road, a.s, a.door);
            if (mine < 0) yield break;
            yield return mine;
            yield return Reverse(lanes[mine]).id;
        }

        /// <summary>Where along a lane (from its start) is opposite a building's door.</summary>
        public double DoneFor(Lane lane, Access a) => lane.dir > 0 ? a.s : lengths[lane.road] - a.s;
    }
}
