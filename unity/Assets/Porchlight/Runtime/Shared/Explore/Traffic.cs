// Moving traffic on every through road: cars keep to the right-hand lane,
// follow the road graph through junctions (U-turning at the map edge) and slow
// down behind the car ahead, or for the player in their lane, turning round
// if held up too long. Pure state. A port of render/iso/traffic.ts.

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
    }

    public sealed class Traffic
    {
        const double CRUISE = 2.4;
        const double GAP = 1.6;
        const double CARS_PER_TILE = 1.0 / 12;
        const int MAX_CARS = 70;
        const double PATIENCE = 4;
        const double LANE_HALF = 0.42;

        public readonly List<TrafficCar> cars = new List<TrafficCar>();
        readonly List<RoadStroke> roads;
        readonly double[] lengths;
        readonly List<double>[] at;
        readonly Dictionary<int, List<int>> byNode = new Dictionary<int, List<int>>();
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
            var mains = new List<int>();
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 1 || (r.kind == RoadKind.Street && dead.Contains(i))) continue;
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
                double d = Js.Hypot(c.x - p.x, c.y - p.y);
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

        public void Update(double dt, IReadOnlyList<Vec2> obstacles)
        {
            foreach (var car in cars)
            {
                if (car.taken) continue;
                double blocked = BlockedAhead(car, obstacles);
                double ahead = Math.Min(GapAhead(car), blocked);
                double target = ahead < GAP ? 0 : ahead < GAP * 2 ? CRUISE * ((ahead - GAP) / GAP) : CRUISE;
                car.speed += (target - car.speed) * Math.Min(1, dt * 3);
                car.wait = blocked < GAP * 1.5 && car.speed < 0.2 ? car.wait + dt : 0;
                if (car.wait > PATIENCE)
                {
                    car.dir = -car.dir;
                    car.wait = 0;
                }
                car.side += Js.Sign(car.dir - car.side) * Math.Min(Math.Abs(car.dir - car.side), dt * 2);
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
                if (o == car || o.taken || o.road != car.road || o.dir != car.dir) continue;
                double d = (o.s - car.s) * car.dir;
                if (d > 0 && d < best) best = d;
            }
            return best;
        }

        void TurnAtJunction(TrafficCar car)
        {
            var r = roads[car.road];
            int node = car.s > lengths[car.road] ? r.to : r.from;
            var options = byNode.TryGetValue(node, out var l) ? l.FindAll(i => i != car.road) : new List<int>();
            if (options.Count == 0)
            {
                car.s = Math.Min(lengths[car.road], Math.Max(0, car.s));
                car.dir = -car.dir;
                return;
            }
            int next = rng.Pick(options);
            var nr = roads[next];
            car.road = next;
            car.dir = nr.from == node ? 1 : -1;
            car.side = car.dir;
            car.s = car.dir == 1 ? 0 : lengths[next];
        }

        void Place(TrafficCar car, double dt)
        {
            var r = roads[car.road];
            var p = AutoDrive.PointAt(r.points, at[car.road], car.s);
            var q = AutoDrive.PointAt(r.points, at[car.road], Math.Max(0, Math.Min(lengths[car.road], car.s + 0.25 * car.dir)));
            double tx = q.x - p.x, ty = q.y - p.y;
            double l = Js.Hypot(tx, ty);
            if (l > 1e-6)
            {
                tx /= l;
                ty /= l;
                double want = JsMath.Atan2(ty, tx);
                car.heading += dt != 0 ? Orbit.ShortestTurn(car.heading, want) * Math.Min(1, dt * 4) : Orbit.ShortestTurn(car.heading, want);
            }
            else
            {
                tx = Math.Cos(car.heading);
                ty = Math.Sin(car.heading);
            }
            double lane = (r.width / 4) * car.side;
            double ux = tx * car.dir, uy = ty * car.dir;
            car.x = p.x - uy * lane;
            car.y = p.y + ux * lane;
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
