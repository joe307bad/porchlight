// Moving traffic on every through road: cars keep to the right-hand lane,
// follow the road graph through junctions (U-turning at dead ends, slowly,
// in a swing out past the end and back in the other lane), and mind the
// junctions' rules (Junctions): each car asks its junction for leave at the
// stop line (a red light, a stop sign, a car with right of way coming, no room
// beyond) and waits there until it's given. Following: the Intelligent Driver
// Model, easing up behind whatever is ahead (the car in front, even round the
// corner past the junction; a stop line; you; a walker on a crossing) at
// braking a real car could manage. A car held up by something in its way
// (not a junction, never a walker) too long turns round. A boat or two sails
// each lake's loop (Ports) among them.
//
// Where each car goes: to somewhere (a house, the shops, out of town), the
// quickest way there round the roads that are open, roads busier than their
// share counting longer; or (the sleepy-village setting) wherever the road
// takes it. The town's settings (ResolvedTraffic) say how many cars, how fast,
// which roads are closed and who goes first where.
// Pure state, run only by the host.

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
        /// <summary>Off the roads for now (fewer cars wanted): kept, out of sight, in case more are wanted again.</summary>
        public bool idle;
        /// <summary>The lake loop a boat sails (layout.boatLoops), s along it; -1 for a car on the roads.</summary>
        public int loop = -1;
        /// <summary>The road it turns onto at the junction ahead, once it's close (-1: not chosen yet; Traffic.ROUND: it turns round).</summary>
        public int next = -1;
        /// <summary>The road it just came off, and which way it drove it, while it's still rounding the corner (-1: none; Traffic.ROUND: coming out of a U-turn).</summary>
        public int prev = -1;
        public int prevDir;
        /// <summary>The junction (node) it has leave to drive through, and by which movement; -1: none.</summary>
        public int granted = -1, grantMove = -1;
        /// <summary>Seconds held at the stop line; seconds stood still at it (a stop sign wants a full stop).</summary>
        public double held, stopped;
        /// <summary>The step it reached the stop line (an all-way stop goes in turn).</summary>
        public long arrival = -1;
        /// <summary>At a yellow: 0 not decided, 1 going on, -1 stopping.</summary>
        public int yellow;
        /// <summary>Its own pace and headway (times the road's, times the usual), drawn once.</summary>
        public double pace = 1, headway = 1;
        /// <summary>Where it's going (a node), or -1 wandering.</summary>
        public int dest = -1;
        /// <summary>The crossing it last decided about stopping for a waiting walker at, whether it'll stop, and where that crossing is.</summary>
        public int yieldFor = -1;
        public bool yields;
        public Vec2 yieldA, yieldB;
        /// <summary>Seconds it's been held up by walkers (they never make it turn round).</summary>
        public double walkerWait;
        /// <summary>Why it's held at its junction, if it is (for the checks and the traffic view).</summary>
        public string why;
    }

    public sealed class Traffic
    {
        /// <summary>Cruising speed before RoadSense gave roads their own (tiles/s, about 35 km/h): turns and U-turns go at shares of it.</summary>
        const double CRUISE = 2.4;
        const double CARS_PER_TILE = ResolvedTraffic.DENSITY;
        /// <summary>The most cars a town keeps, of which the settings say how many are on the roads.</summary>
        const int POOL = 120;
        const double LANE_HALF = 0.42;
        const double SAIL = 1.6;
        /// <summary>A loop at least this long (tiles) gets a second boat.</summary>
        const double TWO_BOATS = 60;
        /// <summary>How far either side of a junction a car curves from one road onto the next (tiles).</summary>
        public const double CORNER = 0.9;
        /// <summary>How far back from a dead end a car starts its U-turn (tiles), and how slow it goes round (against cruising).</summary>
        const double UTURN = 1.4;
        const double UTURN_SPEED = 0.25;
        /// <summary>A dead end shorter than this (tiles) is a stub, too short to turn round in: traffic keeps out.</summary>
        const double SHORTEST_DEAD_END = 4;
        /// <summary>TrafficCar.next / prev for a U-turn at a dead end.</summary>
        public const int ROUND = -2;

        // Following (the Intelligent Driver Model, at 4 m a tile): accelerating, comfortable and
        // hardest braking (tiles/s²), the gap kept standing (tiles, bumper to bumper), the time gap (s).
        public const double ACCEL = 1.0, BRAKE = 1.5, EMERGENCY = 4.0, STANDING_GAP = 0.5, TIME_GAP = 0.9;
        /// <summary>A car's length (tiles): centre to centre, a car stands STANDING_GAP + this behind the next.</summary>
        public const double CAR_LENGTH = 1.0;
        /// <summary>How fast a car takes a turn at a junction (tiles/s).</summary>
        const double TURN_SPEED = 1.3;
        /// <summary>How far from the junction it chooses which way to go (tiles).</summary>
        const double CHOOSE = 4;
        /// <summary>The stop line: this far back from the junction's middle (the box, then the crosswalk, then 1 m).</summary>
        public const double STOP_BACK = 1.45;
        const string NO_ROOM = "no room beyond";
        /// <summary>Held this long for want of room, a car looks for another way out (s).</summary>
        const double REROUTE = 10;
        /// <summary>Where a held car's middle stands, short of the stop line (tiles).</summary>
        const double LINE_SHORT = 0.35;
        /// <summary>A car counts as stopped at the line below this speed (tiles/s), and a stop sign wants it stopped this long (s).</summary>
        const double STOPPED = 0.1, DWELL = 0.6;
        /// <summary>A car with right of way this many seconds off, beyond the time to get across, still counts as coming (SUMO's jmTimegapMinor).</summary>
        const double TIME_GAP_MINOR = 1;
        /// <summary>Held this long at a junction with nothing in it (a ring of cars each giving way to the next), a car goes anyway.</summary>
        const double UNJAM = 6;
        /// <summary>Room wanted beyond the junction before going into it (tiles): a car's length and a gap.</summary>
        const double KEEP_CLEAR = CORNER + CAR_LENGTH + STANDING_GAP + 0.2;

        public readonly List<TrafficCar> cars = new List<TrafficCar>();
        readonly IsoLayout layout;
        readonly List<RoadStroke> roads;
        readonly double[] lengths;
        readonly List<double>[] at;
        readonly List<int>[] byNode;
        /// <summary>The roads traffic drives (not paths, cul-de-sacs or stubs).</summary>
        readonly List<int> mains = new List<int>();
        readonly bool[] driven;
        readonly List<List<Vec2>> loops;
        readonly List<double>[] loopAt;
        readonly Rng rng;
        /// <summary>The town's junctions as the traffic sees them, by node id (null where fewer than three roads meet).</summary>
        public Junction[] junctions;
        public ResolvedTraffic settings;
        /// <summary>How many of the cars should be on the roads (the rest idle).</summary>
        int wanted;
        int poolCars;

        /// <summary>The clock signals keep (ms): the shared clock if the game sets one, else this traffic's own.</summary>
        public Func<long> clock;
        long simMs;
        long tick;
        public long Now => clock != null ? clock() : simMs;
        /// <summary>Night: signals flash, if the town lets them.</summary>
        public bool night;
        /// <summary>The hour (0 to 24), for where people drive to.</summary>
        public double hour = 12;
        /// <summary>Crossings walkers have the right of way on now (Crosswalks): segments a car mustn't drive over.</summary>
        public List<(Vec2 a, Vec2 b)> closedCrossings = new List<(Vec2, Vec2)>();

        // Each step: the cars on each road going each way, nearest the start first.
        readonly List<int>[] lanes;
        readonly Router router;

        public Traffic(IsoLayout layout, Rng rng, Func<Rng, int> pickModel, ResolvedTraffic settings = null)
        {
            this.layout = layout;
            this.rng = rng;
            roads = layout.roads;
            this.settings = settings ?? ResolvedTraffic.Defaults(layout);
            at = new List<double>[roads.Count];
            lengths = new double[roads.Count];
            lanes = new List<int>[roads.Count * 2];
            for (int i = 0; i < lanes.Length; i++) lanes[i] = new List<int>();
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                var a = new List<double> { 0 };
                for (int k = 1; k < r.points.Count; k++) a.Add(a[k - 1] + Geo.Dist(r.points[k], r.points[k - 1]));
                at[i] = a;
                lengths[i] = a[a.Count - 1];
            }
            int maxNode = layout.nodes.Count;
            foreach (var r in roads) maxNode = Math.Max(maxNode, Math.Max(r.from, r.to) + 1);
            byNode = new List<int>[maxNode];
            for (int i = 0; i < maxNode; i++) byNode[i] = new List<int>();
            driven = new bool[roads.Count];
            var dead = Layout.DeadEnds(roads);
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                if (r.kind == RoadKind.Path || lengths[i] < 1 || (dead.Contains(i) && (r.kind == RoadKind.Street || lengths[i] < SHORTEST_DEAD_END))) continue;
                mains.Add(i);
                driven[i] = true;
                byNode[r.from].Add(i);
                if (r.to != r.from) byNode[r.to].Add(i);
            }
            junctions = layout.nodes.Count > 0 ? Junctions.Build(layout, this.settings) : new Junction[0];
            router = new Router(this);
            double total = 0;
            foreach (int i in mains) total += lengths[i];
            poolCars = (int)Math.Min(POOL, Math.Floor(total * CARS_PER_TILE * 2));
            int usual = (int)Math.Min(ResolvedTraffic.MAX_CARS, Math.Floor(total * CARS_PER_TILE));
            poolCars = Math.Max(poolCars, usual);
            for (int k = 0; k < poolCars && mains.Count > 0; k++)
            {
                int road = rng.Pick(mains);
                var car = new TrafficCar { model = pickModel(rng), road = road };
                car.s = rng.Range(0, lengths[road]);
                car.dir = rng.Chance(0.5) ? 1 : -1;
                car.side = car.dir;
                cars.Add(car);
            }
            Spread();
            // Each car's own pace and headway, drawn after the rest so the cars themselves are as they always were.
            foreach (var car in cars)
            {
                car.pace = rng.Range(0.9, 1.1);
                car.headway = rng.Range(0.8, 1.2);
                car.speed = Speed(car.road) * car.pace;
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
            Want(total);
            Update(0, null);
        }

        /// <summary>
        /// Cars put down at random can land on each other: move any within SPACING of one
        /// already down in its lane to the first open place along the roads (the same every time).
        /// </summary>
        void Spread()
        {
            const double SPACING = 3;
            var down = new List<TrafficCar>();
            bool Free(int road, int dir, double s)
            {
                foreach (var o in down)
                    if (o.road == road && o.dir == dir && Math.Abs(o.s - s) < SPACING) return false;
                // Keep clear of the junctions' boxes too.
                return s > STOP_BACK + 0.5 && s < lengths[road] - STOP_BACK - 0.5;
            }
            for (int k = 0; k < cars.Count; k++)
            {
                var c = cars[k];
                if (!Free(c.road, c.dir, c.s))
                {
                    bool placed = false;
                    for (int tries = 0; tries < mains.Count && !placed; tries++)
                    {
                        int road = mains[(k * 7919 + tries) % mains.Count];
                        for (double s = STOP_BACK + 1; s < lengths[road] - STOP_BACK - 0.5 && !placed; s += SPACING)
                            foreach (int dir in new[] { c.dir, -c.dir })
                                if (!placed && Free(road, dir, s))
                                {
                                    c.road = road;
                                    c.s = s;
                                    c.dir = dir;
                                    c.side = dir;
                                    placed = true;
                                }
                    }
                }
                down.Add(c);
            }
        }

        /// <summary>The town's settings changed: the junctions' controls, which roads are open, how many cars.</summary>
        public void Apply(ResolvedTraffic s)
        {
            settings = s ?? ResolvedTraffic.Defaults(layout);
            var old = junctions;
            junctions = layout.nodes.Count > 0 ? Junctions.Build(layout, settings) : new Junction[0];
            foreach (var c in cars)
            {
                // Leave given at the old junctions stands (they're in it); a car about to choose chooses again.
                if (c.granted >= 0 && (c.granted >= junctions.Length || junctions[c.granted] == null)) c.granted = -1;
                if (c.granted < 0 && c.next >= 0 && c.prev < 0) c.next = -1;
                if (settings.wander) c.dest = -1;
            }
            double total = 0;
            foreach (int i in mains) total += lengths[i];
            Want(total);
            router.Invalidate();
        }

        void Want(double total)
        {
            wanted = (int)Math.Min(Math.Min(poolCars, settings.maxCars), Math.Floor(total * CARS_PER_TILE * settings.density));
            int n = 0;
            foreach (var c in cars)
            {
                if (c.loop >= 0) continue;
                bool was = c.idle;
                c.idle = n++ >= wanted;
                if (c.idle && !was) Park(c);
                if (!c.idle && was) Unpark(c);
            }
        }

        static void Park(TrafficCar c)
        {
            c.x = c.y = -10000;
            c.speed = 0;
            c.granted = c.next = c.prev = -1;
        }

        void Unpark(TrafficCar c)
        {
            if (mains.Count == 0) return;
            // Back onto the road it was on, somewhere nobody's within a car's length.
            c.s = Math.Min(lengths[c.road], Math.Max(0, c.s));
            c.speed = 0;
            c.wait = c.held = 0;
            c.next = c.prev = -1;
            Place(c, 0);
        }

        public List<CarPose> Positions()
        {
            var o = new List<CarPose>();
            foreach (var c in cars) if (!c.taken && !c.idle) o.Add(new CarPose { model = c.model, x = c.x, y = c.y, heading = c.heading });
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
                if (!c.taken && !c.idle && d < bestD)
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
            c.granted = -1;
            return c;
        }

        /// <summary>
        /// Carry on from where another client's traffic had this car (it was
        /// running the town until now): onto the nearest road, the way it was heading.
        /// </summary>
        public void Adopt(TrafficCar car)
        {
            var here = new Vec2(car.x, car.y);
            car.granted = car.grantMove = -1;
            car.held = car.stopped = 0;
            car.arrival = -1;
            car.yellow = 0;
            car.yieldFor = -1;
            if (car.idle) return;
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

        /// <summary>Its road's speed for it (tiles/s): RoadSense's, the town's setting, any override.</summary>
        double Speed(int road)
        {
            double v = roads[road].speed > 0 ? roads[road].speed : CRUISE;
            return v * settings.cruise * (road < settings.speed.Length ? settings.speed[road] : 1);
        }

        /// <summary>
        /// One step. `obstacles`: what cars stop for and, held up by it too long, turn round
        /// for (you, other players, cars left at the kerb); `walkers`: who they stop for and wait for.
        /// </summary>
        public void Update(double dt, IReadOnlyList<Vec2> obstacles, IReadOnlyList<Vec2> walkers = null)
        {
            simMs += (long)Math.Round(dt * 1000);
            tick++;
            long now = Now;
            SortLanes();
            router.Step(dt);
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car.taken || car.idle) continue;
                if (car.loop >= 0)
                {
                    Sail(car, dt, obstacles);
                    continue;
                }
                Drive(i, car, dt, now, obstacles, walkers);
            }
        }

        void Drive(int index, TrafficCar car, double dt, long now, IReadOnlyList<Vec2> obstacles, IReadOnlyList<Vec2> walkers)
        {
            double len = lengths[car.road];
            double done = car.dir == 1 ? car.s : len - car.s;
            double rest = len - done;
            int node = NodeAhead(car.road, car.dir);
            var j = node < junctions.Length ? junctions[node] : null;
            // At a junction it chooses early, to ask for leave; elsewhere (a bend, a dead end) as it gets there.
            if (car.next == -1 && rest < Math.Max(j != null ? CHOOSE : 0, Math.Max(CORNER, TurnReach(car.road)))) car.next = PickNext(car, car.road, node);
            // Out the far side of the junction it had leave for.
            if (car.granted >= 0 && car.granted != node && (car.prev < 0 || done >= CORNER)) car.granted = car.grantMove = -1;

            double v = car.speed;
            double v0 = Speed(car.road) * car.pace;
            double T = TIME_GAP * car.headway;
            // Slow for the corner ahead, and round it.
            int move = -1;
            if (car.next >= 0)
            {
                if (j != null) move = j.Move(j.LegIn(car.road, car.dir), j.LegOut(car.next, roads[car.next].from == node ? 1 : -1));
                bool turning = j == null ? Bends(car.road, car.dir, car.next, node) : move >= 0 && j.moves[move].turn != Turn.Straight;
                if (turning) v0 = Math.Min(v0, Math.Sqrt(TURN_SPEED * TURN_SPEED + 2 * BRAKE * Math.Max(0, rest - CORNER)));
            }
            if (car.prev >= 0 && done < CORNER) v0 = Math.Min(v0, Math.Max(TURN_SPEED, Speed(car.road) * 0.6));
            if (TurningRound(car)) v0 = Math.Min(v0, CRUISE * UTURN_SPEED);
            else if (car.next == ROUND || DeadAhead(car))
            {
                // Braking for the U-turn ahead, to be at its speed as it starts.
                double u = CRUISE * UTURN_SPEED;
                v0 = Math.Min(v0, Math.Sqrt(u * u + 2 * BRAKE * Math.Max(0, rest - TurnReach(car.road) - 1.2)));
            }

            // What's ahead: the car in front (on this road or past the junction), the stop line, whatever's in the way.
            double accel = Idm(v, v0, double.PositiveInfinity, 0, T);
            var (gap, leadV, leader) = Leader(car, done, rest);
            if (gap < double.PositiveInfinity) accel = Math.Min(accel, Idm(v, v0, gap - CAR_LENGTH, leadV, T));
            if (j != null && car.next >= 0 && car.granted != node)
            {
                double stopBack = StopBack(car.road);
                double toLine = rest - stopBack;
                // Only the first car in the lane asks for leave (or the next, once the one ahead has it): the rest wait their turn.
                bool first = !(gap < rest) || (leader != null && leader.granted == node);
                bool may = first && MayEnter(index, car, j, move, toLine, now, dt);
                if (!first) NotYet(car);
                if (may)
                {
                    car.granted = node;
                    car.grantMove = move;
                    car.held = car.stopped = 0;
                    car.arrival = -1;
                    car.yellow = 0;
                }
                // Held: draw up with its front at the line (IDM stands STANDING_GAP short of what it's given).
                else accel = Math.Min(accel, Idm(v, v0, toLine - LINE_SHORT + STANDING_GAP, 0, T));
            }
            double blocked = BlockedAhead(car, obstacles, null);
            if (blocked < double.PositiveInfinity) accel = Math.Min(accel, Idm(v, v0, blocked - CAR_LENGTH / 2 - 0.2, 0, T));
            double forWalkers = BlockedAhead(car, walkers, closedCrossings);
            if (car.yields && car.yieldFor >= 0)
            {
                // Stopping to let a walker waiting at the kerb across.
                var own = new List<(Vec2, Vec2)> { (car.yieldA, car.yieldB) };
                forWalkers = Math.Min(forWalkers, BlockedAhead(car, null, own));
            }
            if (forWalkers < double.PositiveInfinity) accel = Math.Min(accel, Idm(v, v0, forWalkers - CAR_LENGTH / 2 - 0.3, 0, T));
            accel = Math.Max(-EMERGENCY, accel);
            v = Math.Max(0, v + accel * dt);
            // Hard stop rather than run into anything (a step too long to brake within).
            double hardest = Math.Min(blocked - CAR_LENGTH / 2, forWalkers - CAR_LENGTH / 2);
            if (gap < double.PositiveInfinity) hardest = Math.Min(hardest, gap - CAR_LENGTH);
            if (hardest < 0.05 && v * dt > Math.Max(0, hardest)) v = 0;
            // Round the turn itself no faster than that, however it came in.
            if (InUTurn(car)) v = Math.Min(v, CRUISE * UTURN_SPEED);
            car.speed = v;
            car.wait = blocked < 2.4 && v < 0.2 ? car.wait + dt : 0;
            car.walkerWait = forWalkers < 2.4 && v < 0.2 ? car.walkerWait + dt : 0;
            if (car.wait > settings.patience)
            {
                car.dir = -car.dir;
                car.wait = 0;
                car.next = car.prev = -1;
                car.granted = -1;
            }
            car.side += Numbers.Sign(car.dir - car.side) * Math.Min(Math.Abs(car.dir - car.side), dt * 2);
            car.s += car.dir * car.speed * dt;
            if (car.s < 0 || car.s > lengths[car.road]) TurnAtJunction(car);
            Place(car, dt);
        }

        /// <summary>IDM's acceleration at speed v toward v0, `gap` tiles (bumper to bumper) behind something going `leadV`.</summary>
        static double Idm(double v, double v0, double gap, double leadV, double T)
        {
            double free = 1 - Math.Pow(v / Math.Max(0.05, v0), 4);
            if (double.IsPositiveInfinity(gap)) return ACCEL * free;
            double want = STANDING_GAP + Math.Max(0, v * T + v * (v - leadV) / (2 * Math.Sqrt(ACCEL * BRAKE)));
            double s = Math.Max(0.02, gap);
            return ACCEL * (free - (want / s) * (want / s));
        }

        /// <summary>Distance back from the junction's middle to the stop line on this road: shorter on short roads.</summary>
        double StopBack(int road) => Math.Max(0.3, Math.Min(STOP_BACK, lengths[road] / 2 - 0.1));

        /// <summary>Whether going from `road` onto `next` at `node` (with no junction model, two roads meeting) bends.</summary>
        bool Bends(int road, int dir, int next, int node)
        {
            var a = Lane(road, dir, dir, Math.Max(0, lengths[road] - 1)).along;
            int nd = roads[next].from == node ? 1 : -1;
            var b = Lane(next, nd, nd, Math.Min(1, lengths[next])).along;
            return Geo.Dot(a, b) < 0.8;
        }

        // ------------------------------------------------------------ junctions

        /// <summary>Whether the car (`toLine` short of its stop line) may go on into junction `j` by movement `m` now.</summary>
        bool MayEnter(int index, TrafficCar car, Junction j, int m, double toLine, long now, double dt)
        {
            if (m < 0) return true;
            double v = car.speed;
            // Far enough off that there's nothing to decide yet.
            double braking = v * v / (2 * BRAKE);
            if (toLine > braking + 2.5)
            {
                NotYet(car);
                return false;
            }
            // Too close to stop any more (it came on at speed, or started right here): it goes, unless
            // something's crossing in front of it, when it stops dead.
            if (toLine < v * v / (2 * EMERGENCY) - 0.05 || toLine < -0.2)
            {
                if (!CrossingInBox(car, j, m)) return true;
                car.speed = 0;
                return false;
            }
            bool atLine = toLine < 0.8 && v < STOPPED;
            if (atLine)
            {
                car.stopped += dt;
                if (car.arrival < 0) car.arrival = tick;
            }
            car.held += dt;
            var mv = j.moves[m];
            int nextRoad = car.next;
            int nextDir = roads[nextRoad].from == j.node ? 1 : -1;
            // Room beyond: never into the box with nowhere to go.
            if (!RoomBeyond(car, nextRoad, nextDir, j))
            {
                car.why = NO_ROOM;
                // Held long enough for want of room (a ring of full roads): another way out, if one has room.
                if (car.held > REROUTE) Reroute(car, j);
                return false;
            }
            // A car that's waited longer for room on the same road gets the next gap.
            if (Starved(index, car, j, nextRoad)) { car.why = "letting a car that's waited longer go first"; return false; }
            // Nothing in the box it would cross.
            if (CrossingInBox(car, j, m)) { car.why = "car crossing in the junction"; return false; }
            var control = j.control;
            bool flashing = false;
            if (j.IsSignal)
            {
                var lamp = j.Light(m, now, night);
                switch (lamp)
                {
                    case Lamp.Red:
                        car.why = "red light";
                        return false;
                    case Lamp.Yellow:
                        // A turning car waiting at the line on a permitted green clears out now, its foes stopping.
                        if (atLine && car.stopped > DWELL) return true;
                        if (car.yellow == 0) car.yellow = toLine < braking + 0.3 ? 1 : -1;
                        car.why = "stopping for the yellow";
                        return car.yellow > 0;
                    case Lamp.Green:
                        return true;
                    case Lamp.Permitted:
                        car.why = "turning across oncoming traffic";
                        return Clear(index, car, j, m, toLine, false);
                    case Lamp.FlashRed:
                        control = JunctionControl.Stop;
                        flashing = true;
                        break;
                    case Lamp.FlashYellow:
                        control = JunctionControl.Priority;
                        flashing = true;
                        break;
                }
            }
            car.yellow = 0;
            bool mustStop = control == JunctionControl.AllWayStop || (control == JunctionControl.Stop && (j.legs[mv.from].minor || flashing));
            car.why = "stopping";
            if (mustStop && car.stopped < DWELL) return false;
            car.why = "giving way";
            if (control == JunctionControl.AllWayStop)
            {
                // In turn: none who got to their line first (with a movement that crosses this one) still waiting.
                for (int i = 0; i < cars.Count; i++)
                {
                    var o = cars[i];
                    if (o == car || o.arrival < 0 || o.granted == j.node || o.taken || o.idle) continue;
                    if (NodeAhead(o.road, o.dir) != j.node || o.next < 0) continue;
                    int on = j.Move(j.LegIn(o.road, o.dir), j.LegOut(o.next, roads[o.next].from == j.node ? 1 : -1));
                    if (on < 0 || !j.Foes(m, on)) continue;
                    if (o.arrival < car.arrival || (o.arrival == car.arrival && i < index)) return Unjam(index, car, j);
                }
                return true;
            }
            return Clear(index, car, j, m, toLine, true) || Unjam(index, car, j);
        }

        /// <summary>Choose another road out of `j` with room on it, if there is one (it'll ask again next step).</summary>
        void Reroute(TrafficCar car, Junction j)
        {
            foreach (int r in Options(car.road, j.node))
            {
                if (r == car.next) continue;
                int d = roads[r].from == j.node ? 1 : -1;
                if (!RoomBeyond(car, r, d, j)) continue;
                car.next = r;
                car.held = Math.Min(car.held, REROUTE / 2);
                car.dest = -1;
                return;
            }
        }

        /// <summary>Another car at this junction, wanting onto the same road, held a good while longer than this one.</summary>
        bool Starved(int index, TrafficCar car, Junction j, int nextRoad)
        {
            foreach (var o in cars)
            {
                if (o == car || o.taken || o.idle || o.loop >= 0 || o.granted == j.node || o.next != nextRoad) continue;
                if (NodeAhead(o.road, o.dir) != j.node || o.road == car.road) continue;
                // Only behind one held for want of room (and not itself deferring), so it never goes round in a ring.
                if (o.why == NO_ROOM && o.held > 3 && o.held > car.held + 2) return true;
            }
            return false;
        }

        /// <summary>A car with leave through `j` by a movement that crosses `m`.</summary>
        bool CrossingInBox(TrafficCar car, Junction j, int m)
        {
            foreach (var o in cars)
                if (o != car && o.granted == j.node && !o.taken && !o.idle && j.Foes(m, o.grantMove)) return true;
            return false;
        }

        static void NotYet(TrafficCar car)
        {
            car.held = 0;
            car.stopped = 0;
            car.arrival = -1;
        }

        /// <summary>No car this one gives way to coming within the time it takes to get across (and a second).</summary>
        bool Clear(int index, TrafficCar car, Junction j, int m, double toLine, bool byRank)
        {
            // How long it takes to get across from here, speeding up as it goes.
            double d = Math.Max(0, toLine) + StopBack(car.road) + CORNER + CAR_LENGTH;
            double v = car.speed;
            double across = (-v + Math.Sqrt(v * v + 2 * ACCEL * d)) / ACCEL;
            for (int i = 0; i < cars.Count; i++)
            {
                var o = cars[i];
                if (o == car || o.taken || o.idle || o.loop >= 0 || o.granted == j.node) continue;
                if (NodeAhead(o.road, o.dir) != j.node) continue;
                int legIn = j.LegIn(o.road, o.dir);
                if (legIn < 0 || legIn == j.moves[m].from) continue;
                double oRest = (o.dir == 1 ? lengths[o.road] - o.s : o.s);
                double tta = oRest / Math.Max(o.speed, 0.3);
                if (tta > across + TIME_GAP_MINOR + 4) continue;
                // Its movement if it's chosen; otherwise any it might make.
                bool yieldTo = false;
                if (o.next >= 0)
                {
                    int on = j.Move(legIn, j.LegOut(o.next, roads[o.next].from == j.node ? 1 : -1));
                    yieldTo = on >= 0 && (byRank ? j.Yields(m, on) : j.Foes(m, on));
                }
                else
                {
                    for (int n = 0; n < j.moves.Count && !yieldTo; n++)
                        if (j.moves[n].from == legIn && (byRank ? j.Yields(m, n) : j.Foes(m, n))) yieldTo = true;
                }
                if (!yieldTo) continue;
                // A car stopped at its own line, waiting its turn, isn't coming (unless it's next to go).
                if (o.speed < STOPPED && oRest - StopBack(o.road) < 1 && o.held > 0.3 && !byRank) continue;
                if (tta < across + TIME_GAP_MINOR) return false;
            }
            return true;
        }

        /// <summary>Held long enough with nothing crossing in the box: go anyway (the first such car at this junction, each step).</summary>
        bool Unjam(int index, TrafficCar car, Junction j)
        {
            if (car.held < UNJAM) return false;
            for (int i = 0; i < index; i++)
            {
                var o = cars[i];
                if (o.taken || o.idle || o.loop >= 0 || o.granted == j.node) continue;
                if (NodeAhead(o.road, o.dir) == j.node && o.held >= UNJAM && o.next >= 0) return false;
            }
            return true;
        }

        /// <summary>Room on `road` (going `dir`) past the junction for one more car, counting those already let in to it.</summary>
        bool RoomBeyond(TrafficCar car, int road, int dir, Junction j)
        {
            var lane = lanes[road * 2 + (dir == 1 ? 1 : 0)];
            double rear = lengths[road];
            foreach (int k in lane)
            {
                var o = cars[k];
                if (o == car) continue;
                double d = dir == 1 ? o.s : lengths[road] - o.s;
                rear = Math.Min(rear, d);
            }
            int coming = 0;
            foreach (var o in cars)
                if (o != car && o.granted == j.node && o.next == road && !o.taken && !o.idle && o.prev < 0) coming++;
            // A road too short to hold the car and the ones let in ahead of it must be empty.
            double need = Math.Min(KEEP_CLEAR * (1 + coming), lengths[road] - 0.05);
            return rear >= need && (coming == 0 || lengths[road] >= KEEP_CLEAR * (1 + coming));
        }

        /// <summary>The cars on each road each way, sorted by how far along they are.</summary>
        void SortLanes()
        {
            foreach (var l in lanes) l.Clear();
            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (c.taken || c.idle || c.loop >= 0) continue;
                lanes[c.road * 2 + (c.dir == 1 ? 1 : 0)].Add(i);
            }
            for (int r = 0; r < roads.Count; r++)
            {
                lanes[r * 2 + 1].Sort((a, b) => cars[a].s != cars[b].s ? cars[a].s.CompareTo(cars[b].s) : a.CompareTo(b));
                lanes[r * 2].Sort((a, b) => cars[a].s != cars[b].s ? cars[b].s.CompareTo(cars[a].s) : a.CompareTo(b));
            }
        }

        /// <summary>The car ahead in its lane (centre to centre, tiles) and how fast it goes; past the junction too, on the road it'll take.</summary>
        (double gap, double speed, TrafficCar who) Leader(TrafficCar car, double done, double rest)
        {
            var lane = lanes[car.road * 2 + (car.dir == 1 ? 1 : 0)];
            double best = double.PositiveInfinity, speed = 0;
            TrafficCar who = null;
            foreach (int k in lane)
            {
                var o = cars[k];
                if (o == car) continue;
                double d = (o.s - car.s) * car.dir;
                if (d > 0 && d < best)
                {
                    best = d;
                    speed = o.speed;
                    who = o;
                }
            }
            if (car.next == ROUND)
            {
                // Turning round: a car just come out of the same U-turn, in the other lane, is ahead.
                foreach (int k in lanes[car.road * 2 + (car.dir == 1 ? 0 : 1)])
                {
                    var o = cars[k];
                    if (o == car || o.prev != ROUND) continue;
                    double od = o.dir == 1 ? o.s : lengths[o.road] - o.s;
                    double d = rest + od;
                    if (d < best)
                    {
                        best = d;
                        speed = 0;
                        who = o;
                    }
                }
            }
            if (best < double.PositiveInfinity || car.next < 0) return (best, speed, who);
            int node = NodeAhead(car.road, car.dir);
            int nd = roads[car.next].from == node ? 1 : -1;
            foreach (int k in lanes[car.next * 2 + (nd == 1 ? 1 : 0)])
            {
                var o = cars[k];
                if (o == car) continue;
                double d = rest + (nd == 1 ? o.s : lengths[car.next] - o.s);
                if (d < best)
                {
                    best = d;
                    speed = o.speed;
                    who = o;
                }
            }
            return (best, speed, who);
        }

        /// <summary>Round and round its loop, easing off for a boat (or the player) just ahead.</summary>
        void Sail(TrafficCar car, double dt, IReadOnlyList<Vec2> obstacles)
        {
            var a = loopAt[car.loop];
            double length = a[a.Count - 1];
            double target = BlockedAlongHeading(car, obstacles) < 2.4 ? 0 : SAIL;
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

        /// <summary>Whether a car may drive onto `road` from `node` (open, and not the wrong way up a one-way road).</summary>
        bool Open(int road, int node) => settings.MayLeave(layout, road, node);

        /// <summary>The roads a car could turn onto at `node` coming off `road`: never a stub too short to turn round at the end of, nor a closed road.</summary>
        List<int> Options(int road, int node)
        {
            var o = new List<int>();
            foreach (int i in byNode[node]) if (i != road && !Stub(i, node) && Open(i, node)) o.Add(i);
            return o;
        }

        /// <summary>A road to turn onto at `node` (ROUND for a U-turn): toward where it's going, or anywhere open.</summary>
        int PickNext(TrafficCar car, int road, int node)
        {
            var options = Options(road, node);
            if (options.Count == 0) return ROUND;
            if (!settings.wander)
            {
                int routed = router.Next(car, road, node, options);
                if (routed >= 0) return routed;
            }
            return rng.Pick(options);
        }

        /// <summary>A road that's a dead end beyond `node`, too short to turn round in.</summary>
        bool Stub(int road, int node)
        {
            int far = roads[road].from == node ? roads[road].to : roads[road].from;
            return lengths[road] < UTURN * 2 && !byNode[far].Exists(i => i != road);
        }

        /// <summary>Partway round a U-turn.</summary>
        bool InUTurn(TrafficCar car)
        {
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            double reach = TurnReach(car.road);
            return (car.next == ROUND && lengths[car.road] - done < reach) || (car.prev == ROUND && done < reach);
        }

        /// <summary>Nearing a dead end (or a junction with every way on closed) it'll turn round at, or coming out of the turn.</summary>
        bool TurningRound(TrafficCar car)
        {
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            double reach = TurnReach(car.road);
            int node = NodeAhead(car.road, car.dir);
            bool dead = car.next == ROUND || !byNode[node].Exists(i => i != car.road);
            return (dead && lengths[car.road] - done < reach + 1.2) || (car.prev == ROUND && done < reach);
        }

        /// <summary>Driving toward a dead end.</summary>
        bool DeadAhead(TrafficCar car) => !byNode[NodeAhead(car.road, car.dir)].Exists(i => i != car.road);

        void TurnAtJunction(TrafficCar car)
        {
            int node = car.s > lengths[car.road] ? roads[car.road].to : roads[car.road].from;
            int next = car.next != -1 ? car.next : PickNext(car, car.road, node);
            car.next = -1;
            car.yieldFor = -1;
            car.yields = false;
            if (next == ROUND)
            {
                // Halfway round the U-turn: on round it goes, back the way it came in the other lane.
                car.s = Math.Min(lengths[car.road], Math.Max(0, car.s));
                car.dir = -car.dir;
                car.side = car.dir;
                car.prev = ROUND;
                car.granted = -1;
                return;
            }
            var nr = roads[next];
            car.prev = car.road;
            car.prevDir = car.dir;
            car.road = next;
            car.dir = nr.from == node ? 1 : -1;
            car.side = car.dir;
            car.s = car.dir == 1 ? 0 : lengths[next];
            router.Arrived(car, node);
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
            if (car.next == -1 && rest < Math.Max(CORNER, TurnReach(car.road))) car.next = PickNext(car, car.road, NodeAhead(car.road, car.dir));
            if (car.prev >= 0 && done >= CORNER) car.prev = -1;
            if (car.prev == ROUND && done >= TurnReach(car.road)) car.prev = -1;
            var (p, t) = PoseAt(car, done, 0);
            if (t.x != 0 || t.y != 0)
            {
                double want = DetMath.Atan2(t.y, t.x);
                car.heading += dt != 0 ? Orbit.ShortestTurn(car.heading, want) * Math.Min(1, dt * 10) : Orbit.ShortestTurn(car.heading, want);
            }
            car.x = p.x;
            car.y = p.y;
        }

        /// <summary>Where the car will be `ahead` tiles further on (0: where it is): along its lane, round the corner or U-turn, onto the next road.</summary>
        (Vec2 at, Vec2 along) PoseAt(TrafficCar car, double done, double ahead)
        {
            double len = lengths[car.road];
            double d = done + ahead;
            double rest = len - d;
            if (car.next == ROUND)
            {
                double reach = TurnReach(car.road);
                if (rest < reach) return UTurn(car.road, car.dir, reach, Math.Min(1, (reach - rest) / (2 * reach)));
            }
            else if (ahead == 0 && car.prev == ROUND)
            {
                double reach = TurnReach(car.road);
                if (d < reach) return UTurn(car.road, -car.dir, reach, 0.5 + d / (2 * reach));
            }
            else if (car.next >= 0)
            {
                int node = NodeAhead(car.road, car.dir);
                int nextDir = roads[car.next].from == node ? 1 : -1;
                double reach = Reach(car.road, car.next);
                if (rest < reach && rest > -reach) return Corner(car.road, car.dir, car.next, nextDir, reach, (reach - rest) / (2 * reach));
                if (rest <= -reach) return Lane(car.next, nextDir, nextDir, Math.Min(lengths[car.next], -rest));
            }
            if (ahead == 0 && car.prev >= 0)
            {
                double reach = Reach(car.prev, car.road);
                if (d < reach) return Corner(car.prev, car.prevDir, car.road, car.dir, reach, 0.5 + d / (2 * reach));
            }
            return Lane(car.road, car.dir, car.side, Math.Min(len, d));
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

        /// <summary>
        /// How far along the way it'll drive (round the corner, onto the next road) the car
        /// comes within half a lane of anything in `points`, or across any of `segments`:
        /// looking out as far as it needs to stop, and a little more.
        /// </summary>
        double BlockedAhead(TrafficCar car, IReadOnlyList<Vec2> points, List<(Vec2 a, Vec2 b)> segments)
        {
            bool anyPoints = points != null && points.Count > 0, anySegs = segments != null && segments.Count > 0;
            if (!anyPoints && !anySegs) return double.PositiveInfinity;
            double v = car.speed;
            double range = Math.Max(2.5, Math.Min(7, STANDING_GAP + v * TIME_GAP + v * v / (2 * BRAKE) + CAR_LENGTH * 1.5));
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            // Only what's near at all.
            var near = new List<Vec2>();
            if (anyPoints)
                foreach (var o in points)
                    if (Math.Abs(o.x - car.x) < range + 1 && Math.Abs(o.y - car.y) < range + 1) near.Add(o);
            var segs = new List<(Vec2 a, Vec2 b)>();
            if (anySegs)
                foreach (var sg in segments)
                    if (Geo.DistToSegment(new Vec2(car.x, car.y), sg.a, sg.b) < range + 1) segs.Add(sg);
            if (near.Count == 0 && segs.Count == 0) return double.PositiveInfinity;
            var last = new Vec2(car.x, car.y);
            for (double d = 0.25; d <= range + 1e-9; d += 0.25)
            {
                var p = PoseAt(car, done, d).at;
                foreach (var o in near)
                    if (Numbers.Hypot(o.x - p.x, o.y - p.y) < LANE_HALF) return d;
                foreach (var sg in segs)
                    if (Geo.SegmentIntersection(last, p, sg.a, sg.b) != null) return d;
                last = p;
            }
            return double.PositiveInfinity;
        }

        /// <summary>Straight ahead along the way it faces (boats).</summary>
        static double BlockedAlongHeading(TrafficCar car, IReadOnlyList<Vec2> obstacles)
        {
            double fx = Math.Cos(car.heading), fy = Math.Sin(car.heading);
            double best = double.PositiveInfinity;
            if (obstacles == null) return best;
            foreach (var o in obstacles)
            {
                double dx = o.x - car.x, dy = o.y - car.y;
                double along = dx * fx + dy * fy;
                if (along > 0 && along < 4 && Math.Abs(dx * fy - dy * fx) < LANE_HALF) best = Math.Min(best, along);
            }
            return best;
        }

        // ------------------------------------------------------------ for the rest of the game

        /// <summary>How far along the way it'll drive (out to `range` tiles) the car first crosses segment ab, or infinity.</summary>
        public double PathTo(TrafficCar car, Vec2 a, Vec2 b, double range)
        {
            if (car.taken || car.idle || car.loop >= 0) return double.PositiveInfinity;
            var seg = new List<(Vec2, Vec2)> { (a, b) };
            double done = car.dir == 1 ? car.s : lengths[car.road] - car.s;
            var last = new Vec2(car.x, car.y);
            for (double d = 0.25; d <= range + 1e-9; d += 0.25)
            {
                var p = PoseAt(car, done, d).at;
                if (Geo.SegmentIntersection(last, p, a, b) != null) return d;
                last = p;
            }
            return double.PositiveInfinity;
        }

        /// <summary>
        /// Where cars coming into a junction by `leg` stop: the middle of their lane at the stop line,
        /// the way they drive, and the lane's width (for drawing the stop bar and the signal head).
        /// </summary>
        public (Vec2 at, Vec2 along, double width) StopLine(JunctionLeg leg)
        {
            int dir = leg.InDir;
            var (p, along) = Lane(leg.road, dir, dir, lengths[leg.road] - StopBack(leg.road));
            return (p, along, roads[leg.road].width / 2);
        }

        /// <summary>How hard braking (comfortably) a car needs to stop from speed v (tiles).</summary>
        public static double StoppingDistance(double v) => v * v / (2 * BRAKE);

        public double LengthOf(int road) => lengths[road];

        public IReadOnlyList<RoadStroke> Roads => roads;

        public IsoLayout Streets => layout;

        /// <summary>Where a car is on its road: how far it's come and how far it has to go to the junction.</summary>
        public (double done, double rest) Along(TrafficCar c)
        {
            double len = lengths[c.road];
            double done = c.dir == 1 ? c.s : len - c.s;
            return (done, len - done);
        }

        /// <summary>The cars going each way on each road, at the last step (index road * 2 + 1 for from→to).</summary>
        public List<int> LaneCars(int road, int dir) => lanes[road * 2 + (dir == 1 ? 1 : 0)];

        /// <summary>The roads a car drives at `node`.</summary>
        public List<int> RoadsAt(int node) => node >= 0 && node < byNode.Length ? byNode[node] : new List<int>();

        public bool Driven(int road) => driven[road];

        /// <summary>How busy each road is now: cars on it for each car it holds bumper to bumper at its speed (0 empty, 1 full).</summary>
        public double Congestion(int road) => router.Congestion(road);

        /// <summary>Moved on its own road, how fast each car goes against how fast it could (1: all free-flowing).</summary>
        public double Flow()
        {
            double sum = 0;
            int n = 0;
            foreach (var c in cars)
            {
                if (c.taken || c.idle || c.loop >= 0) continue;
                sum += Math.Min(1, c.speed / Math.Max(0.1, Speed(c.road) * c.pace));
                n++;
            }
            return n == 0 ? 1 : sum / n;
        }

        // ------------------------------------------------------------ routing

        /// <summary>
        /// Where the cars are going and the way there: a destination for each (homes,
        /// the shops and workshops, out of town by a gate, mixed by the hour), and the
        /// quickest way to it over open roads, a road's time counted longer the fuller it is
        /// (so they spread out rather than all piling down one road).
        /// </summary>
        sealed class Router
        {
            readonly Traffic t;
            readonly List<int> homes = new List<int>(), work = new List<int>(), edge = new List<int>();
            double[] congestion;
            double since;
            /// <summary>How much longer a full road counts (1 + this × how full).</summary>
            const double CROWDING = 2;
            /// <summary>Seconds a junction costs, besides the road (stopping, waiting).</summary>
            const double JUNCTION = 1.5;

            public Router(Traffic t)
            {
                this.t = t;
                congestion = new double[t.roads.Count];
                var layout = t.layout;
                int N = t.byNode.Length;
                // Destinations: the ends of the roads nearest each house, shop and workshop, and the gates.
                var nearestNode = new Func<Vec2, int>(p =>
                {
                    int best = -1;
                    double bestD = double.PositiveInfinity;
                    for (int n = 0; n < N; n++)
                    {
                        if (t.byNode[n].Count == 0) continue;
                        double d = Numbers.Hypot(layout.nodes[n].at.x - p.x, layout.nodes[n].at.y - p.y);
                        if (d < bestD) { bestD = d; best = n; }
                    }
                    return best;
                });
                if (layout.houses != null && layout.nodes.Count > 0)
                {
                    var seenHome = new HashSet<int>();
                    var seenWork = new HashSet<int>();
                    for (int i = 0; i < layout.houses.Count; i += Math.Max(1, layout.houses.Count / 200))
                    {
                        var h = layout.houses[i];
                        int n = nearestNode(new Vec2(h.x + 0.5, h.y + 0.5));
                        if (n < 0) continue;
                        bool isWork = h.kind == BuildingKind.Shop || h.kind == BuildingKind.Workshop || h.kind == BuildingKind.Hall;
                        if (isWork ? seenWork.Add(n) : seenHome.Add(n)) (isWork ? work : homes).Add(n);
                    }
                }
                for (int n = 0; n < N && n < layout.nodes.Count; n++)
                    if (layout.nodes[n].control == JunctionControl.Gate && t.byNode[n].Count > 0) edge.Add(n);
            }

            public void Invalidate()
            {
                foreach (var c in t.cars) if (t.settings.wander) c.dest = -1;
            }

            public double Congestion(int road) => road >= 0 && road < congestion.Length ? congestion[road] : 0;

            public void Step(double dt)
            {
                since += dt;
                if (since < 1) return;
                since = 0;
                for (int r = 0; r < congestion.Length; r++)
                {
                    int n = t.lanes[r * 2].Count + t.lanes[r * 2 + 1].Count;
                    double holds = Math.Max(1, 2 * t.lengths[r] / (CAR_LENGTH + STANDING_GAP + Math.Max(1, t.Speed(r)) * TIME_GAP));
                    congestion[r] = Math.Min(1, n / holds);
                }
            }

            /// <summary>Somewhere to go: a mix by the hour of homes, work and out of town.</summary>
            int Pick(TrafficCar car)
            {
                double h = t.hour;
                // Mornings out to work, evenings home, the town edge now and then all day.
                double toWork = h >= 6 && h < 10 ? 0.6 : h >= 10 && h < 16 ? 0.4 : h >= 16 && h < 20 ? 0.2 : 0.15;
                double toEdge = 0.15;
                double r = t.rng.Next();
                var list = r < toEdge && edge.Count > 0 ? edge : r < toEdge + toWork && work.Count > 0 ? work : homes.Count > 0 ? homes : work.Count > 0 ? work : edge;
                return list.Count == 0 ? -1 : t.rng.Pick(list);
            }

            public void Arrived(TrafficCar car, int node)
            {
                if (node == car.dest) car.dest = -1;
            }

            /// <summary>Of `options` at `node`, the road on the quickest way to the car's destination (-1: no way, or no destination).</summary>
            public int Next(TrafficCar car, int road, int node, List<int> options)
            {
                if (car.dest < 0 || car.dest == node) car.dest = Pick(car);
                if (car.dest < 0) return -1;
                var cost = Costs(car.dest);
                int best = -1;
                double bestC = double.PositiveInfinity;
                foreach (int r in options)
                {
                    var rr = t.roads[r];
                    int far = rr.from == node ? rr.to : rr.from;
                    double c = cost[far] + RoadTime(r);
                    if (c < bestC - 1e-9)
                    {
                        bestC = c;
                        best = r;
                    }
                }
                if (best < 0 || double.IsInfinity(bestC))
                {
                    // No way there from here (closed off): somewhere else.
                    car.dest = -1;
                    return -1;
                }
                return best;
            }

            double RoadTime(int r) => t.lengths[r] / Math.Max(0.3, t.Speed(r)) * (1 + CROWDING * congestion[r]) + JUNCTION;

            // Times to each destination from every node, kept a while (they change only with how busy the roads are).
            readonly Dictionary<int, (double[] cost, double at)> cache = new Dictionary<int, (double[], double)>();
            double clock;

            double[] Costs(int dest)
            {
                clock = t.simMs / 1000.0;
                if (cache.TryGetValue(dest, out var hit) && clock - hit.at < 5) return hit.cost;
                int N = t.byNode.Length;
                var cost = new double[N];
                for (int i = 0; i < N; i++) cost[i] = double.PositiveInfinity;
                cost[dest] = 0;
                var done = new bool[N];
                var frontier = new SortedSet<(double c, int n)> { (0, dest) };
                while (frontier.Count > 0)
                {
                    var top = frontier.Min;
                    frontier.Remove(top);
                    int n = top.n;
                    if (done[n]) continue;
                    done[n] = true;
                    foreach (int r in t.byNode[n])
                    {
                        var rr = t.roads[r];
                        int m = rr.from == n ? rr.to : rr.from;
                        // Backwards from the destination: a car drives r from m to n.
                        if (!t.Open(r, m)) continue;
                        double c = cost[n] + RoadTime(r);
                        if (c < cost[m] - 1e-9)
                        {
                            if (!double.IsInfinity(cost[m])) frontier.Remove((cost[m], m));
                            cost[m] = c;
                            frontier.Add((c, m));
                        }
                    }
                }
                if (cache.Count > 64) cache.Clear();
                cache[dest] = (cost, clock);
                return cost;
            }
        }
    }
}
