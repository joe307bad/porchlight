// A car that comes after you (a mission's police). It drives as you would, at
// the wheel of a Vehicle: the shortest way to where you are, worked out again
// every moment as you move (along the streets, and near you across anywhere a
// car can go, with a DriveGrid: yards, lawns, car parks, never a building), and
// straight at you once it's close with nothing in the way. Close by, it pulls up and stays put (for its driver to get out)
// till you're well away. One that's stuck (nose in a hedge) backs off and goes again; one
// that gets no nearer for long has lost you, and is sent in again. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Chaser
    {
        /// <summary>How often (seconds) it works out the way to you again.</summary>
        const double REPLAN = 0.8;
        /// <summary>Straight at you from this close (tiles), with open ground between.</summary>
        const double DIRECT = 6;
        /// <summary>Across open ground from this close (tiles); further off, by the streets (a search across the whole town is too slow).</summary>
        const double OFF_ROAD = 25;
        /// <summary>It steers for the point this far (tiles) along its way.</summary>
        const double LOOK_AHEAD = 1.3;
        /// <summary>Held up this long at full throttle and it's stuck; it backs off for this long.</summary>
        const double STUCK_AFTER = 0.5;
        const double BACK_OFF = 0.8;
        /// <summary>Turning harder than this (radians), it slows for the corner.</summary>
        const double SHARP = 0.9;
        /// <summary>No nearer you (by a tile) for this long, and it's lost you: it's sent in again from somewhere else.</summary>
        const double LOST_AFTER = 6;
        /// <summary>Pulled up, it stays put till you're this much further off (tiles) than it stopped.</summary>
        const double PULL_AWAY = 1.5;

        /// <summary>Where it may cut across, off the streets (null: it keeps to them till it's close).</summary>
        public DriveGrid grid;

        /// <summary>It pulls up this far (tiles) from you; 0 never (on its way somewhere near you).</summary>
        public double pullUp = 1.6;

        public readonly Vehicle car;
        List<Vec2> way = new List<Vec2>();
        double replan;
        double stuck;
        double backing;
        double backSteer;
        int stucks;
        double closest = double.PositiveInfinity;
        double sinceCloser;
        bool pulling;

        /// <summary>Chasers made so far: each plans at its own moment, so a crowd of them never plans all in one frame.</summary>
        static int made;

        public Chaser(Vehicle car)
        {
            this.car = car;
            // Spread round the cycle by the golden ratio: however many there are, each a moment of its own.
            replan = (made++ * 0.618034 % 1) * REPLAN;
        }

        /// <summary>The way it means to go, from where it is.</summary>
        public IReadOnlyList<Vec2> Way => way;

        /// <summary>It's got no nearer you for a while (stuck, or left behind): better sent in again from somewhere else.</summary>
        public bool Lost => sinceCloser > LOST_AFTER;

        /// <summary>Stopped close by you, pulled up.</summary>
        public bool PulledUp => pulling && Math.Abs(car.speed) < 0.15;

        /// <summary>Sent in again: the car is somewhere new.</summary>
        public void Restart(Vehicle.Spot at)
        {
            car.x = at.x;
            car.y = at.y;
            car.heading = at.heading;
            car.speed = 0;
            way.Clear();
            backing = 0;
            stuck = 0;
            pulling = false;
            closest = double.PositiveInfinity;
            sinceCloser = 0;
        }

        /// <summary>
        /// One step after `target`, at up to `top` tiles a second (past a car's top speed,
        /// with its boost). `open`: where a car may be.
        /// </summary>
        public void Step(Vec2 target, double top, double dt, IReadOnlyList<RoadStroke> roads, Func<double, double, bool> open)
        {
            var at = new Vec2(car.x, car.y);
            double d = Geo.Dist(at, target);
            if (d < closest - 1)
            {
                closest = d;
                sinceCloser = 0;
            }
            else sinceCloser += dt;
            // Close enough to stop by you (from the speed it's going): it brakes, and stays put.
            double stopping = car.speed > 0 ? car.speed * car.speed / (2 * Vehicle.BRAKE) : 0;
            if (pullUp > 0 && d < pullUp + stopping) pulling = true;
            else if (d > pullUp + PULL_AWAY + stopping) pulling = false;
            if (pulling)
            {
                closest = d;
                sinceCloser = 0;
                backing = 0;
                stuck = 0;
                way.Clear();
                car.Drive(car.speed > 0.05 ? -1 : 0, 0, dt, open);
                return;
            }
            replan -= dt;
            if (replan <= 0)
            {
                replan = REPLAN;
                way = d < DIRECT && Clear(at, target, open) ? new List<Vec2> { target }
                    : (d < OFF_ROAD ? grid?.Route(at, target) : null) ?? StreetRoute.Between(roads, at, target)?.points ?? new List<Vec2> { target };
                // Its last point is you, as you were: keep it up to date between plans.
            }
            // Till its turn to plan, straight for you.
            else if (way.Count == 0) way.Add(target);
            if (way.Count > 0) way[way.Count - 1] = target;
            // What's behind it on the way is passed.
            if (way.Count > 1)
            {
                int seg = Geo.ClosestOnPolyline(at, way).seg;
                if (seg > 0) way.RemoveRange(0, seg);
            }

            bool boost = top > Vehicle.TOP_SPEED;
            double flatOut = Vehicle.TOP_SPEED * (boost ? Vehicle.BOOST_SPEED : 1);
            if (backing > 0)
            {
                backing -= dt;
                car.Drive(-1, backSteer, dt, open);
                return;
            }
            var aim = Ahead(at, LOOK_AHEAD);
            double want = DetMath.Atan2(aim.y - at.y, aim.x - at.x);
            double off = Orbit.ShortestTurn(car.heading, want);
            double steer = Math.Max(-1, Math.Min(1, off * 2.5));
            double throttle = Math.Min(1, top / flatOut);
            if (Math.Abs(off) > SHARP) throttle *= 0.45;
            car.Drive(throttle, steer, dt, open, boost);
            if (throttle > 0.2 && Math.Abs(car.speed) < 0.25) stuck += dt;
            else stuck = 0;
            if (stuck > STUCK_AFTER)
            {
                stuck = 0;
                backing = BACK_OFF;
                // Backing with the wheel the other way swings the nose round toward where it was going;
                // stuck again, it tries the other way round.
                backSteer = (off > 0 ? -1 : 1) * (stucks++ % 2 == 0 ? 1 : -1);
                replan = 0;
            }
        }

        /// <summary>The point `ahead` tiles on along its way from the nearest point of it (or the way's end): off the street, it steers to join it further along.</summary>
        Vec2 Ahead(Vec2 at, double ahead)
        {
            if (way.Count == 1) return way[0];
            var hit = Geo.ClosestOnPolyline(at, way);
            var from = hit.point;
            for (int k = hit.seg + 1; k < way.Count; k++)
            {
                var p = way[k];
                double d = Geo.Dist(from, p);
                if (d >= ahead && d > 1e-9) return Geo.Lerp(from, p, ahead / d);
                ahead -= d;
                from = p;
            }
            return from;
        }

        /// <summary>Open ground for a car all the way from a to b.</summary>
        static bool Clear(Vec2 a, Vec2 b, Func<double, double, bool> open)
        {
            double d = Geo.Dist(a, b);
            if (d < 1e-6) return true;
            double sx = -(b.y - a.y) / d * Vehicle.CAR_HALF_WIDTH, sy = (b.x - a.x) / d * Vehicle.CAR_HALF_WIDTH;
            for (double s = 0; s <= d; s += 0.25)
            {
                var p = Geo.Lerp(a, b, s / d);
                if (!open(p.x, p.y) || !open(p.x + sx, p.y + sy) || !open(p.x - sx, p.y - sy)) return false;
            }
            return true;
        }

        /// <summary>
        /// Up to `count` places for chasers to wait all over the town, in parking lanes clear of
        /// `taken`: plenty tried at random along the streets (the longer, the likelier), then
        /// each the one furthest from those already chosen and from `you`.
        /// </summary>
        public static List<Vehicle.Spot> Spread(Vec2 you, int count, IReadOnlyList<RoadStroke> roads, Func<double, double, bool> open, IReadOnlyList<Vec2> taken, Rng rng)
        {
            var lanes = new Lanes(roads, null, 0);
            var tried = new List<Vehicle.Spot>();
            var near = new List<Vec2>(taken);
            for (int i = 0; i < count * 40 && tried.Count < count * 4; i++)
            {
                var spot = lanes.Sample(rng);
                if (spot == null) break;
                var s = spot.Value;
                var p = new Vec2(s.x, s.y);
                if (Crowded(near, p) || !Vehicle.Fits(p.x, p.y, s.heading, open) || !Vehicle.ClearOfRoads(p, s.heading, roads, Layout.PARKING / 2)) continue;
                tried.Add(s);
                near.Add(p);
            }
            var chosen = new List<Vehicle.Spot>();
            var from = new List<Vec2> { you };
            while (chosen.Count < count && tried.Count > 0)
            {
                int best = 0;
                double far = -1;
                for (int i = 0; i < tried.Count; i++)
                {
                    var p = new Vec2(tried[i].x, tried[i].y);
                    double d = double.PositiveInfinity;
                    foreach (var f in from) d = Math.Min(d, Geo.Dist(f, p));
                    if (d > far)
                    {
                        far = d;
                        best = i;
                    }
                }
                chosen.Add(tried[best]);
                from.Add(new Vec2(tried[best].x, tried[best].y));
                tried.RemoveAt(best);
            }
            return chosen;
        }

        /// <summary>
        /// Somewhere for a chaser to start: in a parking lane `min` to `max` tiles from
        /// `you`, clear of `taken` (and so of the others).
        /// </summary>
        public static Vehicle.Spot? StartNear(Vec2 you, double min, double max, IReadOnlyList<RoadStroke> roads, Func<double, double, bool> open, IReadOnlyList<Vec2> taken, Rng rng)
        {
            // Only the streets that come within reach, so most of what it tries is.
            var lanes = new Lanes(roads, you, max);
            for (int tries = 0; tries < 80; tries++)
            {
                var spot = lanes.Sample(rng);
                if (spot == null) return null;
                var s = spot.Value;
                var p = new Vec2(s.x, s.y);
                double d = Geo.Dist(p, you);
                if (d < min || d > max || Crowded(taken, p) || !Vehicle.Fits(s.x, s.y, s.heading, open) || !Vehicle.ClearOfRoads(p, s.heading, roads, Layout.PARKING / 2)) continue;
                // With room to pull out ahead (turned round, if that's where the room is).
                foreach (double heading in new[] { s.heading, s.heading + Math.PI })
                {
                    bool room = true;
                    for (double ahead = 0.4; ahead <= 1.6 && room; ahead += 0.4)
                        room = Vehicle.Fits(s.x + Math.Cos(heading) * ahead, s.y + Math.Sin(heading) * ahead, heading, open);
                    if (room) return new Vehicle.Spot { x = s.x, y = s.y, heading = heading };
                }
            }
            return null;
        }

        /// <summary>Another car's this close (a car's length): no room here.</summary>
        static bool Crowded(IReadOnlyList<Vec2> cars, Vec2 p)
        {
            foreach (var q in cars)
                if (Geo.Dist(q, p) < 1.3) return true;
            return false;
        }

        /// <summary>
        /// The parking lanes along the streets (within `reach` of `about`, if that's given),
        /// to pick places in at random, the longer stretches the likelier.
        /// </summary>
        sealed class Lanes
        {
            readonly List<(RoadStroke road, Vec2 a, Vec2 b, double len)> stretches = new List<(RoadStroke, Vec2, Vec2, double)>();
            // How far along all the stretches each one ends, to find where a distance lands.
            readonly List<double> ends = new List<double>();
            double total;

            public Lanes(IReadOnlyList<RoadStroke> roads, Vec2? about, double reach)
            {
                foreach (var r in roads)
                {
                    if (r.kind == RoadKind.Path) continue;
                    for (int k = 1; k < r.points.Count; k++)
                    {
                        double len = Geo.Dist(r.points[k - 1], r.points[k]);
                        if (len < 1e-6) continue;
                        if (about != null && Geo.DistToSegment(about.Value, r.points[k - 1], r.points[k]) > reach + r.width) continue;
                        stretches.Add((r, r.points[k - 1], r.points[k], len));
                        total += len;
                        ends.Add(total);
                    }
                }
            }

            /// <summary>Somewhere in a parking lane, facing along it; null if there are no streets.</summary>
            public Vehicle.Spot? Sample(Rng rng)
            {
                if (total <= 0) return null;
                double along = rng.Next() * total;
                int j = ends.BinarySearch(along);
                if (j < 0) j = ~j;
                j = Math.Min(j, stretches.Count - 1);
                var s = stretches[j];
                double at = Math.Max(0, Math.Min(s.len, s.len - (ends[j] - along)));
                double tx = (s.b.x - s.a.x) / s.len, ty = (s.b.y - s.a.y) / s.len;
                int side = rng.Next() < 0.5 ? 1 : -1;
                double off = s.road.width / 2 + Layout.PARKING / 2;
                return new Vehicle.Spot { x = s.a.x + tx * at - ty * off * side, y = s.a.y + ty * at + tx * off * side, heading = DetMath.Atan2(ty * side, tx * side) };
            }
        }
    }
}
