// The town's traffic and townsfolk on their way somewhere, as the game draws them:
// the town's life (Life/TownLife.cs: people with homes and jobs going about their
// day, their cars queueing at junctions and parking, the bus) seen through a fixed
// set of slots. A car on the road, or a person out walking or waiting for the bus,
// has a slot while they're out, and gives it back when they're parked or indoors;
// an empty slot is hidden. The slots' cars each keep one model, and the walkers one
// look, so nothing about a slot changes but where it is and whether it's shown.
// A boat or two sails each lake's loop (Ports) after the cars. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Life;
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
        public double speed;
        /// <summary>Taken out of the traffic (the player got in).</summary>
        public bool taken;
        /// <summary>An empty slot: nobody's driving it now (not drawn).</summary>
        public bool hidden;
        /// <summary>The lake loop a boat sails (layout.boatLoops); -1 for a car on the roads.</summary>
        public int loop = -1;
        /// <summary>How far round its loop a boat is.</summary>
        public double s;
    }

    public sealed class Traffic
    {
        const double GAP = 1.6;
        const double LANE_HALF = 0.42;
        const double SAIL = 1.6;
        /// <summary>A loop at least this long (tiles) gets a second boat.</summary>
        const double TWO_BOATS = 60;
        /// <summary>The model the bus is drawn as (the catalog has no bus yet: the delivery van stands in).</summary>
        public const string BUS_MODEL = "cars/delivery";

        /// <summary>The car slots (TownLife.carSlots, buses last), then the boats.</summary>
        public readonly List<TrafficCar> cars = new List<TrafficCar>();
        /// <summary>The walkers' slots (TownLife.personSlots): townsfolk on their way somewhere.</summary>
        public readonly List<Pedestrian> walkers = new List<Pedestrian>();
        public TownLife life { get; private set; }
        readonly int slots;
        readonly List<List<Vec2>> loops;
        readonly List<double>[] loopAt;
        readonly TownModel model;
        readonly IsoLayout layout;
        readonly TownZoning zoning;
        readonly LifeOptions options;
        double carry;
        /// <summary>Who each car slot showed last (a slot handed to another car isn't eased from the last one).</summary>
        readonly int[] drawn;
        /// <summary>Where each car slot's back wheels are.</summary>
        readonly Vec2[] rear;

        public Traffic(TownModel model, IsoLayout layout, TownZoning zoning, Rng rng, Func<Rng, int> pickModel, LifeOptions options = null)
        {
            this.model = model;
            this.layout = layout;
            this.zoning = zoning ?? Zoning.Plan(model, layout);
            this.options = options;
            life = new TownLife(model, layout, this.zoning, options);
            slots = life.carSlots.Length;
            drawn = new int[slots];
            rear = new Vec2[slots];
            int bus = Array.IndexOf(Catalog.CARS, BUS_MODEL);
            for (int i = 0; i < slots; i++)
                cars.Add(new TrafficCar { model = i >= life.busSlotsFrom ? bus : pickModel(rng), hidden = true });
            var looks = new Rng($"{model.seed}::walkers");
            for (int i = 0; i < life.personSlots.Length; i++) walkers.Add(Pedestrian.Puppet(Catalog.RandomLook(looks), looks.Range(0.85, 1.05)));
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
                    cars.Add(new TrafficCar { model = Catalog.PickBoatModel(rng, true), loop = i, s = (start + length * k / boats) % length, speed = SAIL });
            }
            foreach (var c in cars) if (c.loop >= 0) Sail(c, 0, null);
        }

        /// <summary>The town clock where its life has got to (ms since 1970).</summary>
        public long Clock => life.Now;

        /// <summary>Start the town's life at `moment` (if it hasn't started: Update starts it at the time now).</summary>
        public void StartAt(long moment)
        {
            if (!life.Started) life.Start(moment);
            Pose(0);
        }

        /// <summary>Start again at `moment`, from everyone's day (taking over a town someone else was running).</summary>
        public void Restart(long moment)
        {
            life = new TownLife(model, layout, zoning, options);
            for (int i = 0; i < slots; i++) if (cars[i].taken) life.TakeSlot(i);
            life.Start(moment);
            Pose(0);
        }

        public List<CarPose> Positions()
        {
            var o = new List<CarPose>();
            foreach (var c in cars) if (!c.taken && !c.hidden) o.Add(new CarPose { model = c.model, x = c.x, y = c.y, heading = c.heading });
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
                if (!c.taken && !c.hidden && d < bestD)
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
            if (i < slots) life.TakeSlot(i);
            return c;
        }

        /// <summary>Every taken car's let go: their slots are the town's again.</summary>
        public void ReleaseAll()
        {
            foreach (var c in cars) c.taken = false;
            life.ReleaseSlots();
        }

        /// <summary>Kept for the game's hand-over: the town's life carries on from its own clock (Restart), not from where a car was.</summary>
        public void Adopt(TrafficCar car) { }

        public void Update(double dt, IReadOnlyList<Vec2> obstacles)
        {
            if (!life.Started) life.Start(TownClock.Now());
            carry += dt * 1000;
            long step = (long)Math.Floor(carry);
            carry -= step;
            life.SetObstacles(obstacles);
            life.RunUntil(life.Now + step);
            Pose(dt);
            foreach (var c in cars) if (c.loop >= 0 && !c.taken) Sail(c, dt, obstacles);
        }

        /// <summary>Run the town's life up to `moment` (the server's tick: by the clock, not by steps).</summary>
        public void RunTo(long moment, IReadOnlyList<Vec2> obstacles, double dt)
        {
            if (!life.Started) life.Start(moment);
            life.SetObstacles(obstacles);
            if (moment > life.Now) life.RunUntil(moment);
            Pose(dt);
            foreach (var c in cars) if (c.loop >= 0 && !c.taken) Sail(c, dt, obstacles);
        }

        /// <summary>The slots as the town's life has them now.</summary>
        void Pose(double dt)
        {
            life.Draw();
            for (int i = 0; i < slots; i++)
            {
                var c = cars[i];
                if (c.taken) continue;
                var p = life.carSlots[i];
                if (!p.shown)
                {
                    c.hidden = true;
                    c.speed = 0;
                    continue;
                }
                bool same = !c.hidden && drawn[i] == p.who;
                // The car's front is where the town's life has it; its back wheels follow the front as a car's
                // do (pulled after it, never sideways), so it swings round a tight corner the way a car would.
                double wheelbase = Math.Max(0.4, life.cars[p.who].length - 0.3);
                double fx = p.x + Math.Cos(p.heading) * wheelbase / 2, fy = p.y + Math.Sin(p.heading) * wheelbase / 2;
                double rx, ry;
                if (same)
                {
                    double bx = fx - rear[i].x, by = fy - rear[i].y, bl = Numbers.Hypot(bx, by);
                    if (bl < 1e-6 || bl > wheelbase * 2) { rx = p.x - (fx - p.x); ry = p.y - (fy - p.y); }
                    else { rx = fx - bx / bl * wheelbase; ry = fy - by / bl * wheelbase; }
                }
                else
                {
                    rx = p.x - (fx - p.x);
                    ry = p.y - (fy - p.y);
                }
                rear[i] = new Vec2(rx, ry);
                double x = (fx + rx) / 2, y = (fy + ry) / 2;
                c.speed = !same || dt <= 0 ? 0 : Numbers.Hypot(x - c.x, y - c.y) / dt;
                c.hidden = false;
                c.x = x;
                c.y = y;
                c.heading = DetMath.Atan2(fy - ry, fx - rx);
                drawn[i] = p.who;
            }
            for (int i = 0; i < walkers.Count; i++)
            {
                var w = walkers[i];
                if (w.following) continue;
                var p = life.personSlots[i];
                if (!p.shown)
                {
                    w.hidden = true;
                    w.speed = 0;
                    continue;
                }
                bool was = w.hidden;
                w.hidden = false;
                double dx = p.x - w.x, dy = p.y - w.y;
                w.x = p.x;
                w.y = p.y;
                if (was || dt <= 0) w.speed = 0;
                else w.Moved(dx, dy, dt);
                w.heading = p.heading;
            }
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
