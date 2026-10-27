// Driving: the player can get into any car and drive it. Arcade handling:
// steering bites harder the faster you go, the car coasts to a stop when you
// let go, and stops dead against buildings, sheds, water and the map's edge.
// Auto walk at the wheel becomes auto drive. Pure. A port of
// render/iso/driving.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Vehicle
    {
        public const double TOP_SPEED = 5.2;
        const double REVERSE_SPEED = 1.6;
        const double BOOST_SPEED = 1.7;
        const double BOOST_ACCEL = 1.6;
        const double ACCEL = 3.2;
        const double BRAKE = 7;
        const double DRAG = 1.4;
        const double STEER_RATE = 2.1;
        const double FULL_STEER_SPEED = 1.6;
        public const double CAR_HALF_LENGTH = 0.5;
        public const double CAR_HALF_WIDTH = 0.22;

        /// <summary>Forward speed (tiles per second, negative reversing).</summary>
        public double speed;
        /// <summary>Index into the car model list.</summary>
        public readonly int model;
        public double x;
        public double y;
        /// <summary>Direction the car points, radians in map space (atan2(dy, dx)).</summary>
        public double heading;

        public Vehicle(int model, double x, double y, double heading)
        {
            this.model = model;
            this.x = x;
            this.y = y;
            this.heading = heading;
        }

        public Vec2 Forward => new Vec2(Math.Cos(heading), Math.Sin(heading));

        /// <summary>One step of driving: throttle +1 / -1 / 0, steer -1 (left) to +1 (right).</summary>
        public void Drive(double throttle, double steer, double dt, Func<double, double, bool> open, bool boost = false)
        {
            double top = TOP_SPEED * (boost ? BOOST_SPEED : 1);
            if (throttle > 0 && speed <= top * throttle)
            {
                speed += (speed < 0 ? BRAKE : ACCEL * (boost ? BOOST_ACCEL : 1)) * throttle * dt;
                speed = Math.Min(top, speed);
            }
            else if (throttle < 0) speed -= (speed > 0 ? BRAKE : ACCEL * 0.6) * -throttle * dt;
            else speed -= Js.Sign(speed) * Math.Min(Math.Abs(speed), DRAG * dt);
            speed = Math.Max(-REVERSE_SPEED, speed);
            double bite = Math.Min(1, Math.Abs(speed) / FULL_STEER_SPEED) * Js.Sign(speed);
            double h = heading + steer * STEER_RATE * bite * dt;
            double step = speed * dt;
            double nx = x + Math.Cos(h) * step;
            double ny = y + Math.Sin(h) * step;
            if (Fits(nx, ny, h, open))
            {
                x = nx;
                y = ny;
                heading = h;
            }
            else if (Fits(x, y, h, open))
            {
                heading = h;
                speed = 0;
            }
            else speed = 0;
        }

        static readonly (int, int)[] Corners = { (1, 1), (1, -1), (-1, 1), (-1, -1), (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>True if a car at (x, y) pointing along `heading` stands wholly on open ground.</summary>
        public static bool Fits(double x, double y, double heading, Func<double, double, bool> open)
        {
            double fx = Math.Cos(heading), fy = Math.Sin(heading);
            double rx = -fy, ry = fx;
            foreach (var (a, b) in Corners)
            {
                double px = x + fx * CAR_HALF_LENGTH * a + rx * CAR_HALF_WIDTH * b;
                double py = y + fy * CAR_HALF_LENGTH * a + ry * CAR_HALF_WIDTH * b;
                if (!open(px, py)) return false;
            }
            return true;
        }

        /// <summary>Where a car may go: anywhere on the map but buildings, sheds and open water.</summary>
        public static Func<double, double, bool> Drivable(TownModel model, IsoLayout layout)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            var ok = new byte[w * h];
            for (int i = 0; i < ok.Length; i++)
            {
                byte use = layout.use[i];
                bool wet = Terrain.IsWater(tiles.terrain[i]);
                byte over = tiles.overlay[i];
                ok[i] = use == Use.HOUSE || use == Use.SHED || (wet && over == Overlay.NONE) ? (byte)0 : (byte)1;
            }
            return (x, y) =>
            {
                int tx = (int)Math.Floor(x), ty = (int)Math.Floor(y);
                return x > 0.05 && y > 0.05 && x < w - 0.05 && y < h - 0.05 && ok[Grid.Index(tx, ty, w)] == 1;
            };
        }

        /// <summary>Where the driver steps out: beside the driver's door if there's room, else the other side, behind or in front.</summary>
        public static Vec2? ExitSpot(Vehicle car, Func<double, double, bool> canStand)
        {
            var f = car.Forward;
            var left = new Vec2(f.y, -f.x);
            double side = CAR_HALF_WIDTH + 0.28;
            double end = CAR_HALF_LENGTH + 0.28;
            foreach (var (a, b) in new[] { (0.0, side), (0.0, -side), (-end, 0.0), (end, 0.0), (0.0, side + 0.4), (0.0, -side - 0.4) })
            {
                var p = new Vec2(car.x + f.x * a + left.x * b, car.y + f.y * a + left.y * b);
                if (canStand(p.x, p.y)) return p;
            }
            return null;
        }

        static bool ClearOfRoads(Vec2 p, double heading, IReadOnlyList<RoadStroke> roads, double sidewalk)
        {
            var f = new Vec2(Math.Cos(heading), Math.Sin(heading));
            var ends = new[] { p, new Vec2(p.x + f.x * CAR_HALF_LENGTH, p.y + f.y * CAR_HALF_LENGTH), new Vec2(p.x - f.x * CAR_HALF_LENGTH, p.y - f.y * CAR_HALF_LENGTH) };
            foreach (var r in roads)
            {
                if (r.kind == RoadKind.Path) continue;
                double kerb = r.width / 2;
                if (!(Geo.DistToPolyline(p, r.points) > kerb + sidewalk / 2 - 0.03)) return false;
                foreach (var e in ends) if (!(Geo.DistToPolyline(e, r.points) > kerb + 0.1)) return false;
            }
            return true;
        }

        public struct Spot
        {
            public double x, y, heading;
        }

        /// <summary>A spot to leave a car near `near`: up on the pavement beside the kerb of the nearest road, pointing along it.</summary>
        public static Spot? CarSpotNear(Vec2 near, IReadOnlyList<RoadStroke> roads, double sidewalk, Func<double, double, bool> open, IReadOnlyList<Vec2> taken)
        {
            Spot? best = null;
            double bestD = double.PositiveInfinity;
            foreach (var r in roads)
            {
                if (r.kind == RoadKind.Path) continue;
                double off = r.width / 2 + sidewalk / 2;
                for (int k = 1; k < r.points.Count; k++)
                {
                    var a = r.points[k - 1];
                    var b = r.points[k];
                    double len = Geo.Dist(a, b);
                    if (len < 1e-6) continue;
                    double tx = (b.x - a.x) / len;
                    double ty = (b.y - a.y) / len;
                    for (double t = 0; t <= len; t += 0.25)
                    {
                        foreach (int side in new[] { 1, -1 })
                        {
                            var p = new Vec2(a.x + tx * t - ty * off * side, a.y + ty * t + tx * off * side);
                            double d = Geo.Dist(p, near);
                            if (d >= bestD || d < 0.75) continue;
                            double heading = JsMath.Atan2(ty * side, tx * side);
                            if (!Fits(p.x, p.y, heading, open)) continue;
                            bool crowded = false;
                            foreach (var q in taken) if (Geo.Dist(q, p) < 1.3) { crowded = true; break; }
                            if (crowded) continue;
                            if (!ClearOfRoads(p, heading, roads, sidewalk)) continue;
                            bestD = d;
                            best = new Spot { x = p.x, y = p.y, heading = heading };
                        }
                    }
                }
            }
            return best;
        }
    }

    public struct Stretch
    {
        public int road;
        public double length;
        public Vec2 start;
        public Vec2 end;
    }

    /// <summary>
    /// Auto drive: the car drives itself in the right-hand lane, planning a route
    /// over the road graph to a far-off junction and, on arriving, picking another.
    /// </summary>
    public sealed class AutoDrive
    {
        public const double AUTO_DRIVE_SPEED = 4.4;
        const double CORNER_SPEED = 2;
        const double CORNER_BRAKING = 2.4;
        const double FOLLOW_GAP = 1.5;
        const double LANE_HALF = 0.42;

        int road;
        double s;
        int dir = 1;
        double speed;
        List<int> route = new List<int>();
        int? goal;
        readonly List<double>[] at;
        readonly double[] lengths;
        readonly Dictionary<int, List<int>> byNode = new Dictionary<int, List<int>>();
        readonly List<int> nodeOrder = new List<int>();
        readonly IReadOnlyList<RoadStroke> roads;
        readonly Rng rng;

        public AutoDrive(IReadOnlyList<RoadStroke> roads, Rng rng, Vehicle car)
        {
            this.roads = roads;
            this.rng = rng;
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
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 0.5) continue;
                foreach (int n in new[] { r.from, r.to })
                {
                    if (!byNode.TryGetValue(n, out var list))
                    {
                        byNode[n] = list = new List<int>();
                        nodeOrder.Add(n);
                    }
                    list.Add(i);
                }
            }
            double best = double.PositiveInfinity;
            var carAt = new Vec2(car.x, car.y);
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 0.5) continue;
                double d = Geo.DistToPolyline(carAt, r.points);
                if (d < best)
                {
                    best = d;
                    road = i;
                }
            }
            s = Geo.ArcPosition(carAt, roads[road].points);
            var t = Tangent(road, s);
            var fw = car.Forward;
            dir = t.x * fw.x + t.y * fw.y >= 0 ? 1 : -1;
            speed = Math.Max(0, car.speed);
            PickGoal(null);
        }

        List<int> Ways(int node) => byNode.TryGetValue(node, out var l) ? l : new List<int>();

        /// <summary>Drive on for dt seconds and steer `car` to match; `obstacles` are other cars it mustn't run into.</summary>
        public void Step(Vehicle car, double dt, IReadOnlyList<Vec2> obstacles)
        {
            double target = AUTO_DRIVE_SPEED;
            double left = dir > 0 ? lengths[road] - s : s;
            if (left < CORNER_BRAKING)
            {
                double sharp = TurnAhead();
                if (sharp > 0.25) target = Math.Min(target, CORNER_SPEED + (AUTO_DRIVE_SPEED - CORNER_SPEED) * Math.Max(0, left / CORNER_BRAKING) * (1 - sharp));
            }
            var f = car.Forward;
            if (obstacles != null)
            {
                foreach (var o in obstacles)
                {
                    double dx = o.x - car.x, dy = o.y - car.y;
                    double along = dx * f.x + dy * f.y;
                    if (along <= 0 || along > FOLLOW_GAP * 2 || Math.Abs(dx * f.y - dy * f.x) > LANE_HALF) continue;
                    target = Math.Min(target, along < FOLLOW_GAP ? 0 : (AUTO_DRIVE_SPEED * (along - FOLLOW_GAP)) / FOLLOW_GAP);
                }
            }
            speed += Math.Max(-7 * dt, Math.Min(3.6 * dt, target - speed));
            s += dir * speed * dt;
            for (int guard = 0; guard < 4 && (s < 0 || s > lengths[road]); guard++) AtJunction();

            var r = roads[road];
            var p = PointAt(r.points, at[road], s);
            var t = Tangent(road, s);
            double tx = t.x * dir, ty = t.y * dir;
            double lane = r.width / 4;
            var want = new Vec2(p.x - ty * lane, p.y + tx * lane);
            double k = Math.Min(1, dt * 6);
            car.x += (want.x - car.x) * k;
            car.y += (want.y - car.y) * k;
            double turn = JsMath.Atan2(ty, tx) - car.heading;
            while (turn > Math.PI) turn -= Math.PI * 2;
            while (turn < -Math.PI) turn += Math.PI * 2;
            car.heading += turn * k;
            car.speed = speed;
        }

        public Vec2? GoalPoint() => goal == null ? (Vec2?)null : NodeAt(goal.Value);

        public List<Vec2> RoutePoints()
        {
            var r = roads[road];
            var pts = new List<Vec2> { PointAt(r.points, at[road], s) };
            var ahead = new List<Vec2>();
            for (int k = 0; k < r.points.Count; k++) if (dir > 0 ? at[road][k] > s : at[road][k] < s) ahead.Add(r.points[k]);
            if (dir < 0) ahead.Reverse();
            pts.AddRange(ahead);
            int node = dir > 0 ? r.to : r.from;
            foreach (int i in route)
            {
                var q = roads[i];
                if (q.from == node) pts.AddRange(q.points);
                else
                {
                    var rev = new List<Vec2>(q.points);
                    rev.Reverse();
                    pts.AddRange(rev);
                }
                node = q.from == node ? q.to : q.from;
            }
            return pts;
        }

        public List<Stretch> Stretches()
        {
            var o = new List<Stretch>();
            Vec2 Along(int rd, double ss, int d)
            {
                var t = Tangent(rd, ss);
                return new Vec2(t.x * d, t.y * d);
            }
            double len = lengths[road];
            o.Add(new Stretch
            {
                road = road,
                length = dir > 0 ? len - s : s,
                start = Along(road, s, dir),
                end = Along(road, dir > 0 ? len - 0.3 : 0.3, dir),
            });
            int node = dir > 0 ? roads[road].to : roads[road].from;
            foreach (int i in route)
            {
                var q = roads[i];
                int d = q.from == node ? 1 : -1;
                double l = lengths[i];
                o.Add(new Stretch { road = i, length = l, start = Along(i, d > 0 ? 0.3 : l - 0.3, d), end = Along(i, d > 0 ? l - 0.3 : 0.3, d) });
                node = d > 0 ? q.to : q.from;
            }
            return o;
        }

        void AtJunction()
        {
            var r = roads[road];
            int node = s > lengths[road] ? r.to : r.from;
            if (route.Count == 0) PickGoal(node);
            int next;
            if (route.Count > 0)
            {
                next = route[0];
                route.RemoveAt(0);
            }
            else
            {
                var options = Ways(node).FindAll(i => i != road);
                next = options.Count > 0 ? rng.Pick(options) : road;
            }
            double over = Math.Max(Math.Max(0, s - lengths[road]), -s);
            if (next == road && Ways(node).Count <= 1)
            {
                dir = -dir;
                s = dir > 0 ? over : lengths[road] - over;
                return;
            }
            var q = roads[next];
            road = next;
            dir = q.from == node ? 1 : -1;
            s = dir > 0 ? over : lengths[next] - over;
        }

        double TurnAhead()
        {
            var r = roads[road];
            int node = dir > 0 ? r.to : r.from;
            if (route.Count == 0) return Ways(node).Count <= 1 ? 1 : 0.5;
            int next = route[0];
            var inT = Tangent(road, dir > 0 ? lengths[road] - 0.3 : 0.3);
            var a = new Vec2(inT.x * dir, inT.y * dir);
            var q = roads[next];
            int outDir = q.from == node ? 1 : -1;
            var outT = Tangent(next, outDir > 0 ? 0.3 : lengths[next] - 0.3);
            var b = new Vec2(outT.x * outDir, outT.y * outDir);
            return (1 - (a.x * b.x + a.y * b.y)) / 2;
        }

        void PickGoal(int? from)
        {
            var r = roads[road];
            int start = from ?? (dir > 0 ? r.to : r.from);
            var here = NodeAt(start);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int g = start;
                double far = -1;
                for (int k = 0; k < 12; k++)
                {
                    int n = rng.Pick(nodeOrder);
                    double d = Geo.Dist(NodeAt(n), here);
                    if (d > far)
                    {
                        far = d;
                        g = n;
                    }
                }
                var planned = Plan(start, g);
                if (planned != null && planned.Count > 0)
                {
                    goal = g;
                    route = planned;
                    return;
                }
            }
            goal = null;
            route = new List<int>();
        }

        List<int> Plan(int start, int goalNode)
        {
            var cost = new Dictionary<int, double> { [start] = 0 };
            var via = new Dictionary<int, (int road, int from)>();
            var heap = new MinHeap();
            var ids = new Dictionary<int, int>();
            var back = new List<int>();
            int Key(int n)
            {
                if (!ids.TryGetValue(n, out int id))
                {
                    id = ids.Count;
                    ids[n] = id;
                }
                return id;
            }
            void SetBack(int id, int n)
            {
                while (back.Count <= id) back.Add(0);
                back[id] = n;
            }
            Key(start);
            SetBack(0, start);
            heap.Push(0, 0);
            while (heap.Size > 0)
            {
                int node = back[heap.Pop()];
                if (node == goalNode) break;
                foreach (int i in Ways(node))
                {
                    var q = roads[i];
                    int other = q.from == node ? q.to : q.from;
                    double c = cost[node] + lengths[i];
                    if (c < (cost.TryGetValue(other, out double oc) ? oc : double.PositiveInfinity))
                    {
                        cost[other] = c;
                        via[other] = (i, node);
                        int id = Key(other);
                        SetBack(id, other);
                        heap.Push(id, c);
                    }
                }
            }
            if (!via.ContainsKey(goalNode)) return null;
            var o = new List<int>();
            for (int n = goalNode; n != start; n = via[n].from) o.Add(via[n].road);
            o.Reverse();
            return o;
        }

        Vec2 NodeAt(int node)
        {
            int i = byNode.TryGetValue(node, out var l) && l.Count > 0 ? l[0] : 0;
            var r = roads[i];
            return r.from == node ? r.points[0] : r.points[r.points.Count - 1];
        }

        Vec2 Tangent(int rd, double ss)
        {
            var r = roads[rd];
            double len = lengths[rd];
            var a = PointAt(r.points, at[rd], Math.Max(0, ss - 0.15));
            var b = PointAt(r.points, at[rd], Math.Min(len, ss + 0.15));
            double l = Js.Hypot(b.x - a.x, b.y - a.y);
            if (l == 0 || double.IsNaN(l)) l = 1;
            return new Vec2((b.x - a.x) / l, (b.y - a.y) / l);
        }

        public static Vec2 PointAt(IReadOnlyList<Vec2> points, IReadOnlyList<double> at, double s)
        {
            int k = 1;
            while (k < at.Count - 1 && at[k] < s) k++;
            var a = points[k - 1];
            var b = points[k];
            double f = Math.Max(0, Math.Min(1, (s - at[k - 1]) / Math.Max(1e-9, at[k] - at[k - 1])));
            return new Vec2(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f);
        }
    }
}
