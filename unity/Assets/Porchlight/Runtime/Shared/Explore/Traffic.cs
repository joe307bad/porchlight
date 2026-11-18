// Moving traffic on every through road: cars keep to the right-hand lane,
// follow the road graph through junctions (U-turning at dead ends, slowly,
// in a swing out past the end and back in the other lane) and slow
// down behind the car ahead, or for the player in their lane, turning round
// if held up too long. A boat or two sails each lake's loop (Ports) among them.
// Pure state.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public struct CarPose
    {
        public int model;
        public double x, y, heading;
    }

    public sealed class TrafficCar
    {
        public int model;
        public double x, y;
        /// <summary>Direction of travel, radians in world space (atan2(dy, dx)).</summary>
        public double heading;
        public int road;
        public double s;
        public int dir;
        public double speed;
        /// <summary>Which side of the road it's on, easing from -1 to 1 after a U-turn.</summary>
        public double side;
        public double wait;
        /// <summary>Taken out of the traffic (the player got in).</summary>
        public bool taken;
        /// <summary>The lake loop a boat sails (layout.boatLoops), s along it; -1 for a car on the roads.</summary>
        public int loop = -1;
        /// <summary>The road it turns onto at the junction ahead, once it's close (-1: not chosen yet; Traffic.ROUND: a dead end, it turns round).</summary>
        public int next = -1;
        /// <summary>The road it just came off, and which way it drove it, while it's still rounding the corner (-1: none; Traffic.ROUND: coming out of a U-turn).</summary>
        public int prev = -1;
        public int prevDir;
    }

    public sealed class Traffic
    {
        const double CRUISE = 2.4;
        const double GAP = 1.6;
        const double CARS_PER_TILE = 1.0 / 12;
        const int MAX_CARS = 70;
        const double PATIENCE = 4;
        const double LANE_HALF = 0.42;
        const double SAIL = 1.6;
        /// <summary>A loop at least this long (tiles) gets a second boat.</summary>
        const double TWO_BOATS = 60;
        /// <summary>How far either side of a junction a car curves from one road onto the next (tiles).</summary>
        const double CORNER = 0.9;
        /// <summary>How far back from a dead end a car starts its U-turn (tiles), and how slow it goes round (against cruising).</summary>
        const double UTURN = 1.4;
        const double UTURN_SPEED = 0.25;
        /// <summary>A dead end shorter than this (tiles) is a stub, too short to turn round in: traffic keeps out.</summary>
        const double SHORTEST_DEAD_END = 4;
        /// <summary>TrafficCar.next / prev for a U-turn at a dead end.</summary>
        public const int ROUND = -2;

        public readonly List<TrafficCar> cars = new List<TrafficCar>();
        readonly List<RoadStroke> roads;
        readonly double[] lengths;
        readonly List<double>[] at;
        readonly Dictionary<int, List<int>> byNode = new Dictionary<int, List<int>>();
        /// <summary>The roads traffic drives (not paths, cul-de-sacs or stubs).</summary>
        readonly List<int> mains = new List<int>();
        readonly List<List<Vec2>> loops;
        readonly List<double>[] loopAt;
        readonly Rng rng;

        public Traffic(IsoLayout layout, Rng rng, Func<Rng, int> pickModel)
        {
            this.rng = rng;
            roads = layout.roads;
            at = new List<double>[roads.Count];
            lengths = new double[roads.Count];
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                var a = new List<double> { 0 };
                for (int k = 1; k < r.points.Count; k++) a.Add(a[k - 1] + Geo.Dist(r.points[k], r.points[k - 1]));
                at[i] = a;
                lengths[i] = a[a.Count - 1];
            }
            var dead = Layout.DeadEnds(roads);
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 1 || (dead.Contains(i) && (r.kind == RoadKind.Street || lengths[i] < SHORTEST_DEAD_END))) continue;
                mains.Add(i);
                foreach (int n in new[] { r.from, r.to })
                {
                    if (!byNode.TryGetValue(n, out var list)) byNode[n] = list = new List<int>();
                    list.Add(i);
                }
            }
            double total = 0;
            foreach (int i in mains) total += lengths[i];
            int count = (int)Math.Min(MAX_CARS, Math.Floor(total * CARS_PER_TILE));
            for (int k = 0; k < count && mains.Count > 0; k++)
            {
                int road = rng.Pick(mains);
                var car = new TrafficCar { model = pickModel(rng), road = road };
                car.s = rng.Range(0, lengths[road]);
                car.dir = rng.Chance(0.5) ? 1 : -1;
                car.speed = CRUISE;
                car.side = car.dir;
                cars.Add(car);
            }
            loops = layout.boatLoops;
            loopAt = new List<double>[loops.Count];
            for (int i = 0; i < loops.Count; i++)
            {
                var a = new List<double> { 0 };
                for (int k = 1; k < loops[i].Count; k++) a.Add(a[k - 1] + Geo.Dist(loops[i][k], loops[i][k - 1]));
                loopAt[i] = a;
                double length = a[a.Count - 1];
                int boats = length >= TWO_BOATS ? 2 : 1;
                double start = rng.Range(0, length);
                for (int k = 0; k < boats; k++)
                    cars.Add(new TrafficCar { model = Catalog.PickBoatModel(rng, true), loop = i, s = (start + length * k / boats) % length, dir = 1, speed = SAIL, side = 1 });
            }
            Update(0, null);
        }

        public List<CarPose> Positions()
        {
            var o = new List<CarPose>();
            foreach (var c in cars) if (!c.taken) o.Add(new CarPose { model = c.model, x = c.x, y = c.y, heading = c.heading });
            return o;
        }

        /// <summary>The car nearest `at` within `reach` tiles that's still in the traffic, or -1.</summary>
        public int Nearest(Vec2 p, double reach)
        {
            int best = -1;
            double bestD = reach;
            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                double d = Numbers.Hypot(c.x - p.x, c.y - p.y);
                if (!c.taken && d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        public TrafficCar Take(int i)
        {
            var c = cars[i];
            c.taken = true;
            return c;
        }

        /// <summary>
        /// Carry on from where another client's traffic had this car (it was
        /// running the town until now): onto the nearest road, the way it was heading.
        /// </summary>
        public void Adopt(TrafficCar car)
        {
            var here = new Vec2(car.x, car.y);
            if (car.loop >= 0)
            {
                car.s = Math.Max(0, Geo.ArcPosition(here, loops[car.loop]));
                car.wait = 0;
                return;
            }
            int best = -1;
            double bestD = double.PositiveInfinity;
            foreach (int i in mains)
            {
                double d = Geo.DistToPolyline(here, roads[i].points);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            if (best < 0) return;
            var r = roads[best];
            double s = Math.Min(lengths[best], Math.Max(0, Geo.ArcPosition(here, r.points)));
            var a = AutoDrive.PointAt(r.points, at[best], Math.Max(0, s - 0.1));
            var b = AutoDrive.PointAt(r.points, at[best], Math.Min(lengths[best], s + 0.1));
            double along = (b.x - a.x) * Math.Cos(car.heading) + (b.y - a.y) * Math.Sin(car.heading);
            car.road = best;
            car.s = s;
            car.dir = along >= 0 ? 1 : -1;
            car.side = car.dir;
            car.wait = 0;
            car.next = car.prev = -1;
        }

        public void Update(double dt, IReadOnlyList<Vec2> obstacles)
        {
            foreach (var car in cars)
            {
                if (car.taken) continue;
                if (car.loop >= 0)
                {
                    Sail(car, dt, obstacles);
                    continue;
                }
                double blocked = BlockedAhead(car, obstacles);
                double ahead = Math.Min(GapAhead(car), blocked);
                double target = ahead < GAP ? 0 : ahead < GAP * 2 ? CRUISE * ((ahead - GAP) / GAP) : CRUISE;
                if (TurningRound(car)) target = Math.Min(target, CRUISE * UTURN_SPEED);
                car.speed += (target - car.speed) * Math.Min(1, dt * 3);
                // Round the turn itself no faster than that, however it came in.
                if (InUTurn(car)) car.speed = Math.Min(car.speed, CRUISE * UTURN_SPEED);
                car.wait = blocked < GAP * 1.5 && car.speed < 0.2 ? car.wait + dt : 0;
                if (car.wait > PATIENCE)
                {
                    car.dir = -car.dir;
                    car.wait = 0;
                    car.next = car.prev = -1;
                }
                car.side += Numbers.Sign(car.dir - car.side) * Math.Min(Math.Abs(car.dir - car.side), dt * 2);
                car.s += car.dir * car.speed * dt;
                if (car.s < 0 || car.s > lengths[car.road]) TurnAtJunction(car);
                Place(car, dt);
            }
        }

        double GapAhead(TrafficCar car)
        {
            double best = double.PositiveInfinity;
            foreach (var o in cars)
            {
                if (o == car || o.taken || o.loop >= 0 || o.road != car.road || o.dir != car.dir) continue;
                double d = (o.s - car.s) * car.dir;
                if (d > 0 && d < best) best = d;
            }
            return best;
        }

        /// <summary>Round and round its loop, easing off for a boat (or the player) just ahead.</summary>
        void Sail(TrafficCar car, double dt, IReadOnlyList<Vec2> obstacles)
        {
            var a = loopAt[car.loop];
            double length = a[a.Count - 1];
            double target = BlockedAhead(car, obstacles) < GAP * 1.5 ? 0 : SAIL;
            car.speed += (target - car.speed) * Math.Min(1, dt * 1.5);
            car.s = ((car.s + car.speed * dt) % length + length) % length;
            var points = loops[car.loop];
            var p = AutoDrive.PointAt(points, a, car.s);
            var q = AutoDrive.PointAt(points, a, (car.s + 0.4) % length);
            if (Numbers.Hypot(q.x - p.x, q.y - p.y) > 1e-6)
            {
                double want = DetMath.Atan2(q.y - p.y, q.x - p.x);
                car.heading += dt != 0 ? Orbit.ShortestTurn(car.heading, want) * Math.Min(1, dt * 2) : Orbit.ShortestTurn(car.heading, want);
            }
            car.x = p.x;
            car.y = p.y;
        }

        /// <summary>The node at the end of the road the car is driving toward.</summary>
        int NodeAhead(int road, int dir) => dir == 1 ? roads[road].to : roads[road].from;

        /// <summary>A road to turn onto at `node` (ROUND at a dead end): never a stub too short to turn round at the end of.</summary>
        int PickNext(int road, int node)
        {
            var options = byNode.TryGetValue(node, out var l) ? l.FindAll(i => i != road && !Stub(i, node)) : new List<int>();
            return options.Count == 0 ? ROUND : rng.Pick(options);
        }

        /// <summary>A road that's a dead end beyond `node`, too short to turn round in.</summary>
        bool Stub(int road, int node)
        {
            int far = roads[road].from == node ? roads[road].to : roads[road].from;
            return lengths[road] < UTURN * 2 && !(byNode.TryGetValue(far, out var l) && l.Exists(i => i != road));
        }

        /// <summary>Partway round a U-turn.</summary>
        bool InUTurn(TrafficCar car)
        {
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            double reach = TurnReach(car.road);
            return (car.next == ROUND && lengths[car.road] - done < reach) || (car.prev == ROUND && done < reach);
        }

        /// <summary>Nearing a dead end it'll turn round at (slowing for it), or coming out of the turn.</summary>
        bool TurningRound(TrafficCar car)
        {
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            double reach = TurnReach(car.road);
            int node = NodeAhead(car.road, car.dir);
            bool dead = !(byNode.TryGetValue(node, out var l) && l.Exists(i => i != car.road));
            return (dead && lengths[car.road] - done < reach + 1.2) || (car.prev == ROUND && done < reach);
        }

        void TurnAtJunction(TrafficCar car)
        {
            int node = car.s > lengths[car.road] ? roads[car.road].to : roads[car.road].from;
            int next = car.next != -1 ? car.next : PickNext(car.road, node);
            car.next = -1;
            if (next == ROUND)
            {
                // Halfway round the U-turn: on round it goes, back the way it came in the other lane.
                car.s = Math.Min(lengths[car.road], Math.Max(0, car.s));
                car.dir = -car.dir;
                car.side = car.dir;
                car.prev = ROUND;
                return;
            }
            var nr = roads[next];
            car.prev = car.road;
            car.prevDir = car.dir;
            car.road = next;
            car.dir = nr.from == node ? 1 : -1;
            car.side = car.dir;
            car.s = car.dir == 1 ? 0 : lengths[next];
        }

        /// <summary>The middle of the car's lane `d` tiles along a road from the end it starts at (driving way `dir`, on side `side`), and the way along it.</summary>
        (Vec2 at, Vec2 along) Lane(int road, int dir, double side, double d)
        {
            var r = roads[road];
            double s = dir == 1 ? d : lengths[road] - d;
            s = Math.Max(0, Math.Min(lengths[road], s));
            var p = AutoDrive.PointAt(r.points, at[road], s);
            double s2 = Math.Max(0, Math.Min(lengths[road], s + 0.25 * dir));
            var q = AutoDrive.PointAt(r.points, at[road], s2);
            double tx = q.x - p.x, ty = q.y - p.y;
            double l = Numbers.Hypot(tx, ty);
            if (l < 1e-6)
            {
                // At the very end: look back along the road instead.
                q = AutoDrive.PointAt(r.points, at[road], Math.Max(0, Math.Min(lengths[road], s - 0.25 * dir)));
                tx = p.x - q.x;
                ty = p.y - q.y;
                l = Numbers.Hypot(tx, ty);
            }
            if (l < 1e-6) return (p, new Vec2(0, 0));
            tx /= l;
            ty /= l;
            double lane = (r.width / 4) * side * dir;
            return (new Vec2(p.x - ty * lane, p.y + tx * lane), new Vec2(tx, ty));
        }

        /// <summary>
        /// Where the car is drawn: along its lane, but through a junction on a curve
        /// from one road's lane into the next one's, rather than snapping round on the spot.
        /// </summary>
        void Place(TrafficCar car, double dt)
        {
            double len = lengths[car.road];
            double done = car.dir == 1 ? car.s : len - car.s;
            double rest = len - done;
            if (car.next == -1 && rest < Math.Max(CORNER, TurnReach(car.road))) car.next = PickNext(car.road, NodeAhead(car.road, car.dir));
            if (car.prev >= 0 && done >= CORNER) car.prev = -1;
            if (car.prev == ROUND && done >= TurnReach(car.road)) car.prev = -1;
            var (p, t) = Lane(car.road, car.dir, car.side, done);
            if (car.next == ROUND)
            {
                double reach = TurnReach(car.road);
                if (rest < reach) (p, t) = UTurn(car.road, car.dir, reach, (reach - rest) / (2 * reach));
            }
            else if (car.prev == ROUND)
            {
                double reach = TurnReach(car.road);
                if (done < reach) (p, t) = UTurn(car.road, -car.dir, reach, 0.5 + done / (2 * reach));
            }
            else if (car.next >= 0 && rest < CORNER)
            {
                int node = NodeAhead(car.road, car.dir);
                int nextDir = roads[car.next].from == node ? 1 : -1;
                double reach = Reach(car.road, car.next);
                if (rest < reach) (p, t) = Corner(car.road, car.dir, car.next, nextDir, reach, (reach - rest) / (2 * reach));
            }
            else if (car.prev >= 0)
            {
                double reach = Reach(car.prev, car.road);
                if (done < reach) (p, t) = Corner(car.prev, car.prevDir, car.road, car.dir, reach, 0.5 + done / (2 * reach));
            }
            if (t.x != 0 || t.y != 0)
            {
                double want = DetMath.Atan2(t.y, t.x);
                car.heading += dt != 0 ? Orbit.ShortestTurn(car.heading, want) * Math.Min(1, dt * 10) : Orbit.ShortestTurn(car.heading, want);
            }
            car.x = p.x;
            car.y = p.y;
        }

        /// <summary>How far back from a dead end the U-turn starts: shorter on short roads.</summary>
        double TurnReach(int road) => Math.Max(1e-3, Math.Min(UTURN, lengths[road] / 2));

        /// <summary>
        /// A point `t` (0 to 1) round the U-turn at the end of `road` a car drives toward
        /// (way `dir`): on from its lane `reach` before the end, round a tip just past the end
        /// (swinging wider than the lanes, as a car does), and back to the other lane `reach`
        /// from it. Two curves, meeting at the tip, so the way it faces never jumps.
        /// </summary>
        (Vec2 at, Vec2 along) UTurn(int road, int dir, double reach, double t)
        {
            double len = lengths[road];
            var (from, f) = Lane(road, dir, dir, len - reach);
            var to = Lane(road, -dir, -dir, reach).at;
            double gx = to.x - from.x, gy = to.y - from.y, gap = Numbers.Hypot(gx, gy);
            if (gap < 1e-9) return (from, f);
            gx /= gap;
            gy /= gap;
            var tip = new Vec2((from.x + to.x) / 2 + f.x * reach * 1.1, (from.y + to.y) / 2 + f.y * reach * 1.1);
            double a = reach * 0.6, b = Math.Max(gap / 2, reach * 0.45);
            return t < 0.5
                ? Bezier(from, new Vec2(from.x + f.x * a, from.y + f.y * a), new Vec2(tip.x - gx * b, tip.y - gy * b), tip, t * 2)
                : Bezier(tip, new Vec2(tip.x + gx * b, tip.y + gy * b), new Vec2(to.x + f.x * a, to.y + f.y * a), to, t * 2 - 1);
        }

        /// <summary>A point `t` (0 to 1) along a cubic Bézier curve, and the way along it there.</summary>
        static (Vec2 at, Vec2 along) Bezier(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double t)
        {
            double u = 1 - t;
            double b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
            var p = new Vec2(b0 * p0.x + b1 * p1.x + b2 * p2.x + b3 * p3.x, b0 * p0.y + b1 * p1.y + b2 * p2.y + b3 * p3.y);
            double dx = 3 * u * u * (p1.x - p0.x) + 6 * u * t * (p2.x - p1.x) + 3 * t * t * (p3.x - p2.x);
            double dy = 3 * u * u * (p1.y - p0.y) + 6 * u * t * (p2.y - p1.y) + 3 * t * t * (p3.y - p2.y);
            double l = Numbers.Hypot(dx, dy);
            return (p, l < 1e-9 ? new Vec2(0, 0) : new Vec2(dx / l, dy / l));
        }

        /// <summary>How far either side of the junction the curve between two roads starts: shorter on short roads.</summary>
        double Reach(int a, int b) => Math.Max(1e-3, Math.Min(CORNER, Math.Min(lengths[a], lengths[b]) / 2));

        /// <summary>A point `t` (0 to 1) along the curve from road `a`'s lane, `reach` before its end, to road `b`'s, `reach` past its start.</summary>
        (Vec2 at, Vec2 along) Corner(int a, int aDir, int b, int bDir, double reach, double t)
        {
            double lenA = lengths[a];
            var from = Lane(a, aDir, aDir, lenA - reach).at;
            var inEnd = Lane(a, aDir, aDir, lenA).at;
            var outStart = Lane(b, bDir, bDir, 0).at;
            var to = Lane(b, bDir, bDir, reach).at;
            var mid = new Vec2((inEnd.x + outStart.x) / 2, (inEnd.y + outStart.y) / 2);
            double u = 1 - t;
            var p = new Vec2(u * u * from.x + 2 * u * t * mid.x + t * t * to.x, u * u * from.y + 2 * u * t * mid.y + t * t * to.y);
            double dx = 2 * u * (mid.x - from.x) + 2 * t * (to.x - mid.x), dy = 2 * u * (mid.y - from.y) + 2 * t * (to.y - mid.y);
            double l = Numbers.Hypot(dx, dy);
            return (p, l < 1e-9 ? new Vec2(0, 0) : new Vec2(dx / l, dy / l));
        }

        static double BlockedAhead(TrafficCar car, IReadOnlyList<Vec2> obstacles)
        {
            double fx = Math.Cos(car.heading), fy = Math.Sin(car.heading);
            double best = double.PositiveInfinity;
            if (obstacles == null) return best;
            foreach (var o in obstacles)
            {
                double dx = o.x - car.x, dy = o.y - car.y;
                double along = dx * fx + dy * fy;
                if (along > 0 && along < GAP * 2.5 && Math.Abs(dx * fy - dy * fx) < LANE_HALF) best = Math.Min(best, along);
            }
            return best;
        }
    }
}
