// Cars on the road, as queues. Each lane and each movement through a junction is a
// queue of cars, front first. A car's front goes along its segment by its interval
// (from s0 at t0 to s1 at t1, at the limit), but never closer than FOLLOWING behind
// the back of the car ahead: that's all the following there is. When its interval
// runs out at the end of a lane it asks the junction to let it round; refused, it
// waits without doing anything until the junction wakes it. Let round, it's on the
// movement's queue (and then the next lane's), its back still in the lane it left
// (the lane's "laggy head") until it's gone its own length on.
//
// A junction refuses a car whose movement would cross one under way, one that has
// to stop first or is shown red, or (don't block the box) one with no room in the
// lane it'd go into. Each refusal records which car it's waiting on; if that chain
// of waiting comes back round to the car asking, the town's gridlocked, and it's
// let into the lane anyway, which undoes the knot.
//
// Parking: spots along the kerbs and in driveways; a car pulls in (standing in the
// lane a moment), and pulls out again likewise, needing a gap to. A car whose street
// is full hunts for a spot, nearest first, but each car a little differently.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>The gap a car leaves behind the one ahead (tiles: a metre).</summary>
        public const double FOLLOWING = 0.25;
        /// <summary>How far short of a player standing in the lane a car stops (tiles).</summary>
        const double STAND_OFF = 1.2;
        /// <summary>The stop at a stop sign.</summary>
        const long STOP_MS = 500;
        /// <summary>Pulling into and out of a spot: at the kerb, and in a driveway.</summary>
        public const long PARK_KERB_MS = 15_000, UNPARK_KERB_MS = 10_000, DRIVEWAY_MS = 5_000;
        /// <summary>A refused car asks again after this long anyway, in case it was never woken.</summary>
        const long ASK_AGAIN_MS = 10_000;
        /// <summary>A car at the end of a turn with no space yet at the start of the lane beyond looks again this soon.</summary>
        const long LOOK_AGAIN_MS = 300;
        /// <summary>Waited this long at a junction, a car goes anyway (a last resort: the knot check should have freed it).</summary>
        const long GIVE_WAY_MS = 3 * TownClock.MINUTE;
        /// <summary>Held up by a player this long, a car turns round.</summary>
        const long PATIENCE_MS = 15_000;
        /// <summary>Not let out of its spot this many times (each a few seconds apart), a trip's given up.</summary>
        const int MOST_TRIES = 40;

        /// <summary>The spots' cars (-1: free).</summary>
        readonly int[] spotHolder;
        /// <summary>At each junction: the cars waiting to go, and those under way through it (with their movement).</summary>
        readonly Dictionary<int, List<int>> waitingAt = new Dictionary<int, List<int>>();
        readonly Dictionary<int, List<(int car, int local)>> underway = new Dictionary<int, List<(int, int)>>();
        /// <summary>Which cars each waiting car is waiting on.</summary>
        readonly Dictionary<int, List<int>> blockedBy = new Dictionary<int, List<int>>();

        long version = 1;
        double[] frontMemo;
        long[] frontStamp;

        int Lanes => net.lanes.Count;

        double SegLength(int seg) => seg < net.lanes.Count ? net.lanes[seg].Span : net.movements[seg - net.lanes.Count].length;

        double SegSpeed(int seg) => seg < net.lanes.Count ? net.lanes[seg].speed : net.movements[seg - net.lanes.Count].speed;

        // --- Where a car is ---------------------------------------------------------------------

        /// <summary>Where its interval has its front now: pulling away, at speed and braking as planned (SetInterval).</summary>
        double Lerp(Car c)
        {
            double to = double.IsNaN(c.stopAt) ? c.s1 : Math.Min(c.s1, c.stopAt);
            if (Now >= c.t1 || c.t1 <= c.t0) return to;
            double tau = (Now - c.t0) / 1000.0, d;
            if (tau < c.p1) d = c.va * tau + c.a1 * tau * tau / 2;
            else
            {
                double d1 = c.va * c.p1 + c.a1 * c.p1 * c.p1 / 2;
                if (tau < c.p1 + c.p2) d = d1 + c.vp * (tau - c.p1);
                else
                {
                    double u = Math.Min(c.p3, tau - c.p1 - c.p2);
                    d = d1 + c.vp * c.p2 + c.vp * u + c.a3 * u * u / 2;
                }
            }
            return Math.Min(to, c.s0 + Math.Max(0, d));
        }

        /// <summary>How fast its interval has it going at `t` (tiles/s): what it carries into the next.</summary>
        double PlanSpeed(Car c, long t)
        {
            if (t >= c.t1 || c.t1 <= c.t0) return c.vb;
            double tau = (t - c.t0) / 1000.0;
            if (tau < c.p1) return c.va + c.a1 * tau;
            if (tau < c.p1 + c.p2) return c.vp;
            return Math.Max(0, c.vp + c.a3 * Math.Min(c.p3, tau - c.p1 - c.p2));
        }

        /// <summary>Pulling away, tiles/s² (about 0.25 g: a driver in town, not a racer; a tile's 3.97 m).</summary>
        public const double ACCEL = 0.6;
        /// <summary>Braking, tiles/s² (about 0.3 g: comfortable, well short of an emergency stop).</summary>
        public const double BRAKE = 0.75;
        /// <summary>
        /// Stopping for a light that's changed, tiles/s²: 3.4 m/s², the deceleration ITE's guidance times amber
        /// lights for (firmer than BRAKE, still a driver's choice). A car coming up to a light keeps able to.
        /// </summary>
        const double AMBER = 3.4 / 3.97;
        /// <summary>A car that's sat at its interval's end longer than this (ms) is starting from rest.</summary>
        const long STILL_MS = 50;

        /// <summary>How far along its segment a car's front is now: its interval, held back behind what's ahead.</summary>
        public double Front(Car c)
        {
            if (c.seg < 0) return 0;
            if (c.id >= frontStamp.Length)
            {
                Array.Resize(ref frontStamp, c.id * 2 + 1);
                Array.Resize(ref frontMemo, c.id * 2 + 1);
            }
            if (frontStamp[c.id] == version) return frontMemo[c.id];
            frontStamp[c.id] = version;
            frontMemo[c.id] = c.s1;
            double p = Lerp(c);
            var q = queues[c.seg];
            int i = q.cars.IndexOf(c.id);
            if (i > 0)
            {
                var lead = cars[q.cars[i - 1]];
                p = Math.Min(p, Front(lead) - lead.length - FOLLOWING);
            }
            else if (i == 0 && q.laggy >= 0 && q.laggy != c.id)
            {
                var lag = cars[q.laggy];
                if (lag.seg >= 0) p = Math.Min(p, q.length + Front(lag) - lag.length - FOLLOWING);
            }
            if (q.blockFirst == c.id && !double.IsNaN(q.blockAt)) p = Math.Min(p, q.blockAt - STAND_OFF);
            if (q.held == c.id) p = Math.Min(p, q.heldAt);
            p = Math.Max(p, Math.Min(c.s0, -c.length));
            frontMemo[c.id] = p;
            return p;
        }

        void SetInterval(Car c, double from, double to)
        {
            // The speed it's doing now: what its last interval had it at, unless it's been standing (held at the end
            // of it, or behind the car ahead).
            double va = Now > c.t1 + STILL_MS ? 0 : PlanSpeed(c, Now);
            if (va > 0 && c.seg >= 0 && Front(c) < Lerp(c) - 0.02) va = 0;
            c.s0 = from;
            c.s1 = Math.Max(from, to);
            c.t0 = Now;
            // No faster than the turn it's still coming out of (its back's there): it swings round, not snaps.
            double speed = Math.Max(0.05, c.laggy >= 0 ? Math.Min(SegSpeed(c.seg), SegSpeed(c.laggy)) : SegSpeed(c.seg));
            // The car ahead stopped, or stopping: it brakes to stop behind it, rather than running up to its back.
            var q = c.seg >= 0 ? queues[c.seg] : null;
            int i = q != null ? q.cars.IndexOf(c.id) : -1;
            c.stopAt = double.NaN;
            if (i > 0)
            {
                var lead = cars[q.cars[i - 1]];
                double at = StopsAt(lead, c.seg);
                if (!double.IsNaN(at) && at - lead.length - FOLLOWING < c.s1 - 1e-6) c.stopAt = Math.Max(from, at - lead.length - FOLLOWING);
            }
            if (double.IsNaN(c.stopAt)) Drive(c, c.s1 - from, va, speed, Math.Min(speed, ExitSpeed(c)));
            else Drive(c, c.stopAt - from, va, speed, 0);
            c.t1 = Now + (long)Math.Ceiling((c.p1 + c.p2 + c.p3) * 1000);
            version++;
            Schedule(K_CAR, c.id, c.t1);
            // The car behind sees this one go, or stop: it plans again (and so on back down the queue).
            if (i >= 0 && i + 1 < q.cars.Count)
            {
                var f = cars[q.cars[i + 1]];
                if (!double.IsNaN(f.stopAt) || c.vb <= 1e-9) Refresh(f);
            }
        }

        /// <summary>Where a car on `seg` will be stood (its front): stopped now, or braking to stop. NaN: it's going on.</summary>
        double StopsAt(Car lead, int seg)
        {
            if (lead.seg != seg) return double.NaN;
            if (lead.state != CarState.Crossing || Now >= lead.t1) return Front(lead);
            if (lead.vb > 1e-9) return double.NaN;
            return double.IsNaN(lead.stopAt) ? lead.s1 : Math.Min(lead.s1, lead.stopAt);
        }

        /// <summary>
        /// The speed a car means to be doing at the end of its interval: as fast as the turn it'll take there, or
        /// stopped (a stop sign, a red light, where it parks, or short of the segment's end). A light that changes
        /// for it, or a turn it's let round without stopping after all, it drives off from a crawl.
        /// </summary>
        double ExitSpeed(Car c)
        {
            if (c.route == null || c.step >= c.route.Count - 1 || c.s1 < SegLength(c.seg) - 1e-6) return 0;
            int next = c.route[c.step + 1];
            double ahead = Arrival(c, c.step + 1);
            if (c.seg >= Lanes) return Math.Min(Math.Min(SegSpeed(next), Stoppable(RoomOn(next))), ahead);
            var m = net.movements[next - Lanes];
            double beyond = Math.Min(Stoppable(m.length + RoomOn(m.to)), ahead);
            if (m.stops) return 0;
            var j = net.junctions[m.node];
            if (j.control == Control.Signal)
            {
                var (stage, ends) = RoadNet.Stage(j, c.t1 > Now ? c.t1 : Now);
                if (stage != m.stage || ends - Now < 3000) return 0;
            }
            // What it can see from here: a turn under way across its own, people crossing, no room beyond. It
            // slows to stop rather than arriving at speed to be told to wait.
            if (Judge(c, m, j, out _, out _, out _, null, out _) != Verdict.Go) return 0;
            return Math.Min(m.speed, beyond);
        }

        /// <summary>How many of the segments on from where it's going onto a car looks at to judge how fast it can arrive.</summary>
        const int LOOK_AHEAD = 4;

        /// <summary>
        /// The fastest a car can come onto its route's `k`th segment and still slow, braking at BRAKE, for what's on
        /// past it: each segment's limit (a turn's swing), a stop sign, and where it parks. A short lane between a
        /// junction and a stop sign, say: it comes off the junction slow enough to stop by the end of it, rather than
        /// at full speed and braking hard. (Lights, and turns it's judged to wait for, it sees when it gets there.)
        /// </summary>
        double Arrival(Car c, int k)
        {
            double best = double.MaxValue, dist = 0;
            for (int s = k; s < c.route.Count && s < k + LOOK_AHEAD; s++)
            {
                int seg = c.route[s];
                best = Math.Min(best, Math.Sqrt(SegSpeed(seg) * SegSpeed(seg) + 2 * BRAKE * dist));
                if (s == c.route.Count - 1)
                {
                    best = Math.Min(best, Math.Sqrt(2 * BRAKE * (dist + Math.Max(0, c.goal))));
                    break;
                }
                dist += SegLength(seg);
                if (seg < Lanes && net.movements[c.route[s + 1] - Lanes].stops)
                {
                    best = Math.Min(best, Math.Sqrt(2 * BRAKE * dist));
                    break;
                }
                // A light ahead may be red by then: still able to stop for it, braking as firmly as for an amber.
                if (seg < Lanes && net.movements[c.route[s + 1] - Lanes].signal) best = Math.Min(best, Math.Sqrt(2 * AMBER * dist));
            }
            return best;
        }

        /// <summary>How far along `seg` a car coming onto it can go before the back of a car stopped (or stopping) on it.</summary>
        double RoomOn(int seg)
        {
            int last = LastOn(seg);
            if (last < 0) return double.PositiveInfinity;
            double at = StopsAt(cars[last], seg);
            return double.IsNaN(at) ? double.PositiveInfinity : Math.Max(0, at - cars[last].length - FOLLOWING);
        }

        /// <summary>The fastest a car can be going and still stop, braking at BRAKE, within `room` tiles.</summary>
        static double Stoppable(double room) => double.IsPositiveInfinity(room) ? double.MaxValue : Math.Sqrt(2 * BRAKE * room);

        /// <summary>
        /// Plan `d` tiles from `va` to `vb`, no faster than `top`: pull away at ACCEL, keep steady, brake at BRAKE.
        /// Too short to get up to speed, it peaks lower; too short even to brake (or pull away) at those rates, it
        /// changes speed evenly over the whole of it.
        /// </summary>
        static void Drive(Car c, double d, double va, double top, double vb)
        {
            // No faster at the end than pulling away gently gets it (it carries on speeding up after).
            vb = Math.Min(vb, Math.Sqrt(va * va + 2 * ACCEL * Math.Max(0, d)));
            c.va = va;
            c.vb = vb;
            c.p1 = c.p2 = c.p3 = c.a1 = c.a3 = 0;
            c.vp = top;
            if (d <= 1e-9) { c.vp = c.vb = va; return; }
            double r1 = top >= va ? ACCEL : -BRAKE, r3 = vb <= top ? -BRAKE : ACCEL;
            double d1 = (top * top - va * va) / (2 * r1), d3 = (vb * vb - top * top) / (2 * r3);
            if (d1 + d3 <= d)
            {
                c.a1 = r1; c.p1 = (top - va) / r1;
                c.p2 = (d - d1 - d3) / top;
                c.a3 = r3; c.p3 = (vb - top) / r3;
                return;
            }
            if (va <= top)
            {
                // Up and straight back down: the peak where pulling away meets braking.
                double peak = Math.Sqrt((d + va * va / (2 * ACCEL) + vb * vb / (2 * BRAKE)) / (1 / (2 * ACCEL) + 1 / (2 * BRAKE)));
                if (peak >= va && peak >= vb)
                {
                    c.vp = peak;
                    c.a1 = ACCEL; c.p1 = (peak - va) / ACCEL;
                    c.a3 = -BRAKE; c.p3 = (peak - vb) / BRAKE;
                    return;
                }
            }
            // Evenly from va to vb.
            double t = 2 * d / Math.Max(0.05, va + vb);
            c.vp = vb;
            c.a1 = (vb - va) / t;
            c.p1 = t;
        }

        /// <summary>A car held back gets a fresh interval from where it really is, so nothing jumps when what held it goes.</summary>
        void Refresh(Car c) => Refresh(c, double.NaN);

        /// <summary>
        /// As Refresh, going on from `p` (NaN: where it is now). Lifting what held a car back (the back of the car
        /// ahead clearing, a driver let in), its own interval has run on past where it was held: it goes on from where
        /// it was (as Front said before), not leaping to where its interval's got to.
        /// </summary>
        void Refresh(Car c, double p)
        {
            if (c.seg < 0 || c.state == CarState.Parking || c.state == CarState.Unparking || c.state == CarState.AtStop) return;
            if (double.IsNaN(p)) p = Front(c);
            if (p >= c.s1 - 1e-6)
            {
                Schedule(K_CAR, c.id, Now);
                return;
            }
            c.state = CarState.Crossing;
            SetInterval(c, p, c.s1);
        }

        void RefreshFront(int seg)
        {
            var q = queues[seg];
            if (q.cars.Count > 0) Refresh(cars[q.cars[0]]);
        }

        // --- Room in a lane ---------------------------------------------------------------------

        bool Room(int lane, Car c)
        {
            var q = queues[lane];
            return q.committed < 1e-9 || q.committed + c.length + FOLLOWING <= net.lanes[lane].Span + 1e-9;
        }

        void Commit(int lane, Car c)
        {
            var q = queues[lane];
            q.committed += c.length + FOLLOWING;
            q.lastIn = c.id;
        }

        void Uncommit(int lane, Car c)
        {
            var q = queues[lane];
            q.committed = Math.Max(0, q.committed - c.length - FOLLOWING);
            if (q.committed < 1e-9) q.committed = 0;
            if (q.lastIn == c.id) q.lastIn = q.cars.Count > 0 ? q.cars[q.cars.Count - 1] : -1;
            // Room now: whoever's waiting to come in.
            Wake(net.lanes[lane].fromNode);
        }

        // --- Events -----------------------------------------------------------------------------

        void OnCar(Car c)
        {
            switch (c.state)
            {
                case CarState.Parked:
                    if (wantsOut.Contains(c.id)) PullOut(c);
                    return;
                case CarState.Unparking:
                    c.state = CarState.Crossing;
                    SetInterval(c, c.s1, c.step == c.route.Count - 1 ? c.goal : SegLength(c.seg));
                    return;
                case CarState.Parking:
                    FinishParking(c);
                    return;
                case CarState.AtStop:
                    LeaveStop(c);
                    return;
                default:
                    Arrive(c);
                    return;
            }
        }

        /// <summary>A car's interval ran out (or it was woken): at the end of its segment, or held up short of it.</summary>
        void Arrive(Car c)
        {
            if (c.seg < 0) return;
            double p = Front(c);
            if (p < c.s1 - 0.02)
            {
                c.state = CarState.Queued;
                var q = queues[c.seg];
                if (q.blockFirst == c.id)
                {
                    if (c.heldSince < 0) c.heldSince = Now;
                    if (Now - c.heldSince >= PATIENCE_MS && TurnRound(c)) return;
                    Schedule(K_CAR, c.id, Now + 2000);
                }
                return;
            }
            c.heldSince = -1;
            bool last = c.step >= c.route.Count - 1;
            if (last)
            {
                Reached(c);
                return;
            }
            if (c.seg >= Lanes)
            {
                // Round the turn: into the lane (room was kept for it), once there's space at its start. (There
                // mightn't be: a car let in to undo a knot, or another turn's car into the same lane just ahead.)
                int next = c.route[c.step + 1];
                int behind = LastOn(next);
                if (behind >= 0 && Front(cars[behind]) - cars[behind].length < FOLLOWING - 1e-6)
                {
                    c.state = CarState.Queued;
                    Schedule(K_CAR, c.id, Now + LOOK_AGAIN_MS);
                    return;
                }
                Advance(c, next);
                return;
            }
            var queue = queues[c.seg];
            if (queue.cars.Count == 0 || queue.cars[0] != c.id)
            {
                c.state = CarState.Queued;
                return;
            }
            c.state = CarState.Waiting;
            if (c.arrived < 0) c.arrived = Now;
            Ask(c, c.route[c.step + 1] - Lanes);
        }

        /// <summary>Onto the next segment: off the front of this one (its back still here a while), onto the back of that.</summary>
        void Advance(Car c, int next)
        {
            if (c.seg < Lanes) Measured(c);
            var q = queues[c.seg];
            q.cars.Remove(c.id);
            if (c.laggy >= 0) ClearLaggy(c);
            q.laggy = c.id;
            c.laggy = c.seg;
            c.seg = next;
            c.step++;
            queues[next].cars.Add(c.id);
            c.state = CarState.Crossing;
            c.arrived = -1;
            c.stopped = false;
            bool last = c.step >= c.route.Count - 1;
            SetInterval(c, 0, last ? c.goal : SegLength(next));
            Schedule(K_CLEAR, c.id, Now + (long)Math.Ceiling((c.length + FOLLOWING) / Math.Max(0.05, SegSpeed(next)) * 1000));
            if (next < Lanes)
            {
                c.laneSince = Now;
                c.laneFrom = 0;
                stats.Flow(net.lanes[next].road, Now);
                if (last) OnLastLane(c);
            }
        }

        void OnClear(Car c)
        {
            if (c.laggy < 0) return;
            // Its back's out of the segment it left (the gap behind it is the next car's to keep).
            if (c.seg < 0 || Front(c) >= c.length - 1e-6)
            {
                bool slower = c.seg >= 0 && SegSpeed(c.laggy) < SegSpeed(c.seg);
                ClearLaggy(c);
                // Clear of it: up to this segment's speed for the rest of the interval.
                if (slower && c.state == CarState.Crossing && Front(c) < c.s1 - 1e-6) SetInterval(c, Front(c), c.s1);
                return;
            }
            double left = c.length - Front(c);
            Schedule(K_CLEAR, c.id, Now + Math.Max(250, (long)Math.Ceiling(left / Math.Max(0.05, Math.Min(SegSpeed(c.seg), SegSpeed(c.laggy))) * 1000)));
        }

        /// <summary>A car's back is clear of the segment it left.</summary>
        void ClearLaggy(Car c)
        {
            int seg = c.laggy;
            if (seg < 0) return;
            c.laggy = -1;
            Cancel(K_CLEAR, c.id);
            var q = queues[seg];
            // Where the car behind it is, before it's let go (its interval's run on, held back by this one's back).
            int behind = q.cars.Count > 0 && q.cars[0] != c.id ? q.cars[0] : -1;
            double was = behind >= 0 ? Front(cars[behind]) : double.NaN;
            if (q.laggy == c.id) q.laggy = -1;
            version++;
            if (seg < Lanes) Uncommit(seg, c);
            else
            {
                int node = net.movements[seg - Lanes].node;
                if (underway.TryGetValue(node, out var list)) list.RemoveAll(u => u.car == c.id);
                Wake(node);
            }
            if (behind >= 0) Refresh(cars[behind], was);
            else RefreshFront(seg);
        }

        // --- Junctions --------------------------------------------------------------------------

        void Ask(Car c, int mid)
        {
            var m = net.movements[mid];
            var j = net.junctions[m.node];
            if (!waitingAt.TryGetValue(j.node, out var waiting)) waitingAt[j.node] = waiting = new List<int>();
            if (!waiting.Contains(c.id)) waiting.Add(c.id);
            blockedBy.Remove(c.id);
            var verdict = Judge(c, m, j, out long again, out var by, out bool knot, null, out bool change);
            // Its light changes now: nobody's coming the way that was green (the junction's clock moves on).
            if (verdict == Verdict.Go && change) ChangeLight(j, m.stage);
            if (m.stops && !c.stopped && verdict != Verdict.StopFirst) c.stopped = true;
            if (by != null) blockedBy[c.id] = by;
            if (verdict != Verdict.Go)
            {
                Schedule(K_CAR, c.id, again);
                return;
            }
            if (by != null)
            {
                if (knot) stats.gridlockBroken++;
                else stats.waitedTooLong++;
            }
            var going = underway.TryGetValue(j.node, out var g) ? g : null;
            // Let round.
            waiting.Remove(c.id);
            blockedBy.Remove(c.id);
            if (going == null) underway[j.node] = going = new List<(int, int)>();
            going.Add((c.id, m.local));
            Commit(m.to, c);
            stats.Delay(j.node, Now - c.arrived, car: true);
            Advance(c, Lanes + mid);
        }

        /// <summary>What a junction says to a car at the front of its lane asking to take a turn.</summary>
        public enum Verdict
        {
            Go,
            /// <summary>A stop sign: it stands a moment first.</summary>
            StopFirst,
            /// <summary>A signal, and its turn's stage isn't green.</summary>
            Red,
            /// <summary>Green, but not for long enough to get round before it changes.</summary>
            TooLateOnGreen,
            /// <summary>A player's standing on the turn.</summary>
            PlayerOnTurn,
            /// <summary>Townsfolk are crossing the road it's turning into or out of.</summary>
            PeopleCrossing,
            /// <summary>A crossing turn's under way, or there's no room beyond.</summary>
            Blocked,
        }

        /// <summary>
        /// The junction's rules, in the order it asks them, for car `c` (at the front of its lane) wanting turn `m`:
        /// Go, or why not and when to ask again. Changes nothing: Ask acts on it, and the focus view (`explain`
        /// given) shows it, each question and its answer, so what's shown is the very rule that ran. `by`: the cars
        /// it's waiting on (crossing turns under way, whoever filled the lane beyond), when it got that far; `knot`:
        /// they wait on it in turn (gridlock), so it goes anyway.
        /// </summary>
        Verdict Judge(Car c, Movement m, Junction j, out long again, out List<int> by, out bool knot, List<Why> explain) => Judge(c, m, j, out again, out by, out knot, explain, out _);

        Verdict Judge(Car c, Movement m, Junction j, out long again, out List<int> by, out bool knot, List<Why> explain, out bool change)
        {
            again = Now;
            by = null;
            knot = false;
            change = false;
            if (m.stops)
            {
                bool served = c.stopped || Now >= c.arrived + STOP_MS;
                explain?.Add(new Why("Must it stop first?", served ? "Yes, and it has" : $"Yes: stopping until {TownClock.FormatSeconds(c.arrived + STOP_MS)}", served,
                    j.control == Control.AllWayStop ? "An all-way stop: two equal streets cross, and every car stops." : net.lanes[m.from].rank >= TopRank(j) ? "The end of a T: it stops for the street running through." : "A stop sign onto a bigger road."));
                if (!served)
                {
                    again = c.arrived + STOP_MS;
                    return Verdict.StopFirst;
                }
            }
            if (j.control == Control.Signal)
            {
                var (stage, ends) = RoadNet.Stage(j, Now);
                long through = (long)Math.Ceiling((m.length + c.length + FOLLOWING) / m.speed * 1000);
                bool green = stage == m.stage;
                explain?.Add(new Why("Is its light green?", green ? $"Yes, until {TownClock.FormatSeconds(ends)}" : $"No: green at {TownClock.FormatSeconds(ends)}", green,
                    $"Two stages of {RoadNet.STAGE_MS / 1000} s; its turn goes in stage {m.stage + 1}, now it's stage {stage + 1}."));
                if (!green && options.skipEmpty)
                {
                    // Nobody coming the way that's green, and it's been green a while: the light changes for it.
                    bool long_ = Now - (ends - RoadNet.STAGE_MS) >= MIN_GREEN_MS;
                    bool empty = long_ && NobodyComing(j, stage);
                    explain?.Add(new Why("Is anyone coming the way that's green?", !long_ ? $"Green only since {TownClock.FormatSeconds(ends - RoadNet.STAGE_MS)}: it stays at least {MIN_GREEN_MS / 1000} s" : empty ? "No: its light changes for it now" : "Yes", empty,
                        "A signal with nobody coming on its green changes for a car waiting the other way."));
                    if (empty)
                    {
                        green = true;
                        change = true;
                        ends = Now + RoadNet.STAGE_MS;
                    }
                }
                if (!green)
                {
                    again = ends + 1;
                    return Verdict.Red;
                }
                bool time = Now + through <= ends;
                explain?.Add(new Why("Can it get round before red?", time ? $"Yes: {through / 1000.0:0.0} s, {(ends - Now) / 1000.0:0.0} s left" : $"No: {through / 1000.0:0.0} s, {(ends - Now) / 1000.0:0.0} s left; its next green", time,
                    "Never start what can't be finished on this green."));
                if (!time)
                {
                    again = ends + RoadNet.STAGE_MS + 1;
                    return Verdict.TooLateOnGreen;
                }
            }
            bool player = !double.IsNaN(queues[Lanes + m.id].blockAt);
            explain?.Add(new Why("Is a player standing on the turn?", player ? "Yes: it waits for them to go" : "No", !player));
            if (player)
            {
                again = Now + ASK_AGAIN_MS;
                return Verdict.PlayerOnTurn;
            }
            bool crossing = CrossingInTheWay(j, m);
            explain?.Add(new Why("Are townsfolk crossing in its way?", crossing ? "Yes: it waits for them" : "No", !crossing));
            if (crossing)
            {
                again = Now + ASK_AGAIN_MS;
                return Verdict.PeopleCrossing;
            }
            List<int> crossers = null;
            if (underway.TryGetValue(j.node, out var going))
            {
                foreach (var (car, local) in going)
                {
                    if (car == c.id || !j.conflicts[m.local][local]) continue;
                    (by = by ?? new List<int>()).Add(car);
                    if (explain != null) (crossers = crossers ?? new List<int>()).Add(car);
                }
            }
            explain?.Add(new Why("Is a crossing turn under way?", crossers == null ? "No" : $"Yes: car{(crossers.Count > 1 ? "s" : "")} {string.Join(", ", crossers)}", crossers == null,
                "Turns whose paths cross can't both be under way."));
            bool room = Room(m.to, c);
            if (!room)
            {
                int filler = queues[m.to].lastIn;
                if (filler >= 0) (by = by ?? new List<int>()).Add(filler);
                else by = by ?? new List<int>();
            }
            if (explain != null)
            {
                var q = queues[m.to];
                explain.Add(new Why("Is there room for it beyond?", room ? "Yes" : $"No: car {q.lastIn} filled it", room,
                    $"Lane {m.to} holds {net.lanes[m.to].Span:0.0} tiles; {q.committed:0.0} are kept for the cars on it or let into it, and it needs {c.length + FOLLOWING:0.0}. Don't block the box."));
            }
            if (by == null) return Verdict.Go;
            foreach (int b in by) knot |= WaitsOn(b, c.id);
            bool tooLong = Now - c.arrived > GIVE_WAY_MS;
            explain?.Add(new Why("Gridlock: do they wait on it, all the way round?", knot ? "Yes: it goes anyway, and the knot comes undone" : "No", knot));
            if (!knot) explain?.Add(new Why($"Has it waited over {GIVE_WAY_MS / TownClock.MINUTE} minutes?", tooLong ? "Yes: it goes anyway" : $"No: {(Now - c.arrived) / 1000} s", tooLong));
            if (!knot && !tooLong)
            {
                again = Now + ASK_AGAIN_MS;
                return Verdict.Blocked;
            }
            return Verdict.Go;
        }

        /// <summary>The highest rank of a junction's roads.</summary>
        int TopRank(Junction j)
        {
            int top = 0;
            foreach (int r in j.legs) top = Math.Max(top, RoadNet.Rank(net.roads[r]));
            return top;
        }

        /// <summary>A light stays green at least this long before it changes for someone waiting the other way.</summary>
        const long MIN_GREEN_MS = 8_000;
        /// <summary>A car this near the line (seconds at its lane's limit) is coming.</summary>
        const double COMING_S = 4;

        /// <summary>Is nobody at, coming to (within COMING_S) or going through junction `j` on a turn of `stage`?</summary>
        bool NobodyComing(Junction j, int stage)
        {
            foreach (int id in j.movements)
            {
                var m = net.movements[id];
                if (m.stage != stage) continue;
                var q = queues[m.from];
                if (q.cars.Count > 0)
                {
                    var f = cars[q.cars[0]];
                    var lane = net.lanes[m.from];
                    if (lane.Span - Front(f) <= COMING_S * lane.speed) return false;
                }
                if (queues[Lanes + id].cars.Count > 0) return false;
            }
            return true;
        }

        /// <summary>Junction `j`'s light changes now to `stage`, green for a whole stage; those waiting there ask again.</summary>
        void ChangeLight(Junction j, int stage)
        {
            // Stage(j, t) = ((t + offset) / STAGE) mod 2: from now, `stage`, a stage's length.
            long period = 2 * RoadNet.STAGE_MS;
            j.offset = ((stage * RoadNet.STAGE_MS - Now) % period + period) % period;
            stats.lightsChanged++;
            Wake(j.node);
        }

        /// <summary>Why a waiting car hasn't gone (for the checks and the debug view): its turn, the room beyond, and who's in the way.</summary>
        public string WhyWaiting(Car c)
        {
            if (c.seg < 0 || c.seg >= Lanes || c.route == null || c.step + 1 >= c.route.Count) return $"car {c.id}: not at a junction";
            int mid = c.route[c.step + 1] - Lanes;
            var m = net.movements[mid];
            var j = net.junctions[m.node];
            var q = queues[m.to];
            var parts = new List<string> { $"car {c.id} at node {j.node} ({j.control}) turn {mid}{(m.uturn ? " U" : "")} into lane {m.to} (span {net.lanes[m.to].Span:0.00})" };
            parts.Add($"room: committed {q.committed:0.00} + {c.length + FOLLOWING:0.00} {(Room(m.to, c) ? "fits" : "doesn't fit")}, {q.cars.Count} on it [{string.Join(",", q.cars)}], lastIn {q.lastIn}");
            double held = 0;
            foreach (var o in cars)
            {
                if (o.seg == m.to || (o.laggy == m.to)) held += o.length + FOLLOWING;
                else if (o.seg >= Lanes && net.movements[o.seg - Lanes].to == m.to) held += o.length + FOLLOWING;
            }
            parts.Add($"actually held {held:0.00}");
            if (underway.TryGetValue(j.node, out var going))
            {
                var crossing = new List<string>();
                foreach (var g in going) if (j.conflicts[m.local][g.local]) crossing.Add($"{g.car}:{cars[g.car].state}/seg{cars[g.car].seg}");
                parts.Add($"underway [{string.Join(",", crossing)}]");
            }
            if (blockedBy.TryGetValue(c.id, out var by)) parts.Add($"blockedBy [{string.Join(",", by)}]");
            parts.Add($"waited {(Now - c.arrived) / 1000}s, next event {(events.When(Key(K_CAR, c.id)) - Now) / 1000}s");
            return string.Join("; ", parts);
        }

        /// <summary>Does `from` wait (through whoever it's waiting on, and they on, …) on `target`?</summary>
        bool WaitsOn(int from, int target)
        {
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(from);
            while (stack.Count > 0 && seen.Count < 4000)
            {
                int x = stack.Pop();
                if (x == target) return true;
                if (!seen.Add(x)) continue;
                foreach (int y in WaitingOn(x)) stack.Push(y);
            }
            return false;
        }

        /// <summary>Whoever car `x` is waiting on now: a junction's refusal, the car ahead, or the back of one gone on.</summary>
        IEnumerable<int> WaitingOn(int x)
        {
            if (blockedBy.TryGetValue(x, out var list))
            {
                foreach (int y in list) yield return y;
                yield break;
            }
            var c = cars[x];
            if (c.seg < 0) yield break;
            var q = queues[c.seg];
            int i = q.cars.IndexOf(x);
            if (i > 0) yield return q.cars[i - 1];
            else if (i == 0 && q.laggy >= 0) yield return q.laggy;
            // At the end of a turn, waiting for space on the lane beyond: on its last car.
            if (i == 0 && c.seg >= Lanes && c.state == CarState.Queued && c.step + 1 < c.route.Count)
            {
                int behind = LastOn(c.route[c.step + 1]);
                if (behind >= 0) yield return behind;
            }
        }

        /// <summary>The last car into a lane (on it, or with its back still there), or -1.</summary>
        int LastOn(int lane)
        {
            var q = queues[lane];
            return q.cars.Count > 0 ? q.cars[q.cars.Count - 1] : -1;
        }

        /// <summary>Everyone waiting at a junction asks again now: longest waiting first.</summary>
        void Wake(int node)
        {
            WakeWalkers(node);
            if (!waitingAt.TryGetValue(node, out var waiting) || waiting.Count == 0) return;
            waiting.RemoveAll(id => cars[id].state != CarState.Waiting);
            waiting.Sort((a, b) => cars[a].arrived != cars[b].arrived ? cars[a].arrived.CompareTo(cars[b].arrived) : a.CompareTo(b));
            for (int k = 0; k < waiting.Count; k++)
            {
                long at = Now + k;
                long due = events.When(Key(K_CAR, waiting[k]));
                if (due < 0 || due > at) Schedule(K_CAR, waiting[k], at);
            }
        }

        /// <summary>Every car and the one right behind it in the same queue (for the checks).</summary>
        public IEnumerable<(Car lead, Car follow)> Pairs()
        {
            foreach (var q in queues)
                for (int i = 1; i < q.cars.Count; i++) yield return (cars[q.cars[i - 1]], cars[q.cars[i]]);
        }

        /// <summary>Who car `id` is waiting on, for the debug view (/mp/sim/where).</summary>
        public List<int> BlockedBy(int id)
        {
            var o = new List<int>();
            foreach (int y in WaitingOn(id)) o.Add(y);
            return o;
        }

        // --- On and off the road ----------------------------------------------------------------

        readonly HashSet<int> wantsOut = new HashSet<int>();
        readonly Dictionary<int, int> tries = new Dictionary<int, int>();

        /// <summary>Is there a gap at `d` along `lane` for a car this long (and room in the lane, unless it's being let in)?</summary>
        bool Gap(int lane, double d, Car c, out int index)
        {
            var q = queues[lane];
            index = 0;
            if (!Room(lane, c) && q.letting != c.id) return false;
            if (q.laggy >= 0 && d > q.length - c.length - FOLLOWING * 2 && cars[q.laggy].seg >= 0) return false;
            for (int i = 0; i < q.cars.Count; i++)
            {
                var o = cars[q.cars[i]];
                double f = Front(o);
                if (f > d - c.length - FOLLOWING && f - o.length < d + FOLLOWING) return false;
                if (f >= d) index = i + 1;
            }
            return true;
        }

        /// <summary>Put a car on the road at `d` along `lane`, standing (it's off when its interval's set).</summary>
        bool PutOnRoad(Car c, int lane, double d)
        {
            if (!Gap(lane, d, c, out int index)) return false;
            var q = queues[lane];
            q.cars.Insert(index, c.id);
            Commit(lane, c);
            c.seg = lane;
            c.laggy = -1;
            c.s0 = c.s1 = d;
            c.t0 = c.t1 = Now;
            c.va = c.vp = c.vb = 0;
            c.stopAt = double.NaN;
            c.offMap = false;
            c.laneSince = Now;
            c.laneFrom = d;
            c.rerouted = -1;
            c.reroutes = 0;
            version++;
            TakeCarSlot(c);
            return true;
        }

        /// <summary>Off the road altogether (parked, gone off the map, or taken).</summary>
        void RemoveFromRoad(Car c)
        {
            Cancel(K_CAR, c.id);
            Cancel(K_CLEAR, c.id);
            StopLetting(c);
            wantsOut.Remove(c.id);
            if (c.laggy >= 0) ClearLaggy(c);
            if (c.seg >= 0)
            {
                int seg = c.seg;
                var q = queues[seg];
                int i = q.cars.IndexOf(c.id);
                // Where the car behind is held now: it goes on from there, not from where its interval's got to.
                int behind = i >= 0 && i + 1 < q.cars.Count ? q.cars[i + 1] : -1;
                double behindAt = behind >= 0 ? Front(cars[behind]) : double.NaN;
                q.cars.Remove(c.id);
                if (q.blockFirst == c.id) q.blockFirst = i < q.cars.Count && i >= 0 ? q.cars[i] : -1;
                c.seg = -1;
                version++;
                if (seg < Lanes) Uncommit(seg, c);
                else
                {
                    var m = net.movements[seg - Lanes];
                    if (underway.TryGetValue(m.node, out var list)) list.RemoveAll(u => u.car == c.id);
                    // It had room kept in the lane it was turning into.
                    Uncommit(m.to, c);
                    Wake(m.node);
                }
                if (behind >= 0) Refresh(cars[behind], behindAt);
            }
            foreach (var w in waitingAt.Values) w.Remove(c.id);
            blockedBy.Remove(c.id);
            c.state = CarState.Parked;
            FreeCarSlot(c);
        }

        /// <summary>Drive `c` from where it's parked (or a gate) to `dest`: off once there's a gap.</summary>
        bool Depart(Car c, Place from, Place dest)
        {
            c.dest = dest;
            c.hunts = 0;
            if (!Plan(c, from, dest)) return false;
            wantsOut.Add(c.id);
            tries[c.id] = 0;
            PullOut(c);
            return true;
        }

        /// <summary>Where a car starts (its spot, or a gate's lane), and the way to where it's going.</summary>
        bool Plan(Car c, Place from, Place dest)
        {
            int lane;
            double d;
            if (from.border)
            {
                lane = net.borders[from.id].entry;
                d = 0;
            }
            else if (c.spot >= 0)
            {
                var spot = net.spots[c.spot];
                lane = spot.lane;
                d = spot.s - net.lanes[lane].start;
            }
            else if (net.access.TryGetValue(c.parkedBy >= 0 ? c.parkedBy : from.id, out var a))
            {
                lane = net.SideLane(a.road, a.s, a.door);
                if (lane < 0) return false;
                d = net.DoneFor(net.lanes[lane], a) - net.lanes[lane].start;
            }
            else return false;
            d = Math.Max(0, Math.Min(net.lanes[lane].Span, d));
            var targets = new List<(int, double)>();
            int keep = -1;
            if (dest.border)
            {
                var b = net.borders[dest.id];
                targets.Add((b.exit, net.lanes[b.exit].Span));
            }
            else if (net.access.TryGetValue(dest.id, out var to))
            {
                // Home with a driveway: in it. Otherwise the street in front (a spot's found on the way in).
                var owner = c.owner >= 0 ? people[c.owner] : null;
                if (!options.infiniteParking && to.driveway >= 0 && owner != null && !owner.home.border && owner.home.id == dest.id && spotHolder[to.driveway] < 0)
                {
                    keep = to.driveway;
                    var s = net.spots[keep];
                    targets.Add((s.lane, s.s - net.lanes[s.lane].start));
                }
                else if (options.reserveSpots && !options.infiniteParking && c.bus < 0 && c.van < 0 && (keep = NearestFreeSpot(to.door)) >= 0)
                {
                    // A spot near the door, held from now: driven to, not looked for on arrival (no circling the block).
                    stats.reserved++;
                    var s = net.spots[keep];
                    targets.Add((s.lane, s.s - net.lanes[s.lane].start));
                }
                else
                {
                    foreach (int l in net.LanesFor(to))
                        targets.Add((l, Math.Max(0, Math.Min(net.lanes[l].Span, net.DoneFor(net.lanes[l], to) - net.lanes[l].start))));
                }
            }
            if (targets.Count == 0) return false;
            var route = router.Route(lane, d, targets, out int k, out double seconds);
            if (route == null) return false;
            c.route = route;
            c.routeS = seconds;
            c.routed = Now;
            c.step = 0;
            c.goal = targets[k].Item2;
            c.startLane = lane;
            c.startD = d;
            if (keep >= 0)
            {
                spotHolder[keep] = c.id;
                c.target = keep;
                c.reserved = net.spots[keep].kind == SpotKind.Kerb;
            }
            else
            {
                c.target = -1;
                c.reserved = false;
            }
            return true;
        }

        /// <summary>Out of its spot onto the road, if there's a gap; if not, again shortly.</summary>
        void PullOut(Car c)
        {
            if (!PutOnRoad(c, c.startLane, c.startD))
            {
                int n = tries.TryGetValue(c.id, out int t) ? t + 1 : 1;
                tries[c.id] = n;
                // A couple of tries and no gap: the next driver along holds back to let it in.
                if (options.letIn && n >= LET_IN_AFTER) LetIn(c);
                bool letting = queues[c.startLane].letting == c.id;
                // (Being let in, it tries every second: as soon as the cars past the one holding back are by.)
                if (n >= (letting ? 3 * MOST_TRIES : MOST_TRIES))
                {
                    StopLetting(c);
                    wantsOut.Remove(c.id);
                    if (c.target >= 0 && spotHolder[c.target] == c.id) spotHolder[c.target] = -1;
                    c.target = -1;
                    if (c.owner >= 0) Fail(people[c.owner], "no gap to pull out into");
                    return;
                }
                Schedule(K_CAR, c.id, Now + (letting ? 1000 : 3000));
                return;
            }
            StopLetting(c);
            wantsOut.Remove(c.id);
            tries.Remove(c.id);
            long wait = 0;
            if (c.spot >= 0)
            {
                wait = net.spots[c.spot].kind == SpotKind.Driveway ? DRIVEWAY_MS : UNPARK_KERB_MS;
                spotHolder[c.spot] = -1;
                c.spot = -1;
            }
            else if (c.parkedBy >= 0) wait = UNPARK_KERB_MS;
            c.parkedBy = -1;
            c.offMap = false;
            c.step = 0;
            if (wait > 0)
            {
                c.state = CarState.Unparking;
                Schedule(K_CAR, c.id, Now + wait);
            }
            else
            {
                c.state = CarState.Crossing;
                SetInterval(c, c.s1, c.route.Count == 1 ? c.goal : SegLength(c.seg));
            }
            if (c.route.Count == 1) OnLastLane(c);
        }

        /// <summary>Tries at pulling out before the next driver along lets it in.</summary>
        const int LET_IN_AFTER = 2;

        /// <summary>
        /// Car `c`, waiting at the kerb to pull out at `startD` along `startLane`: the first car coming along that can
        /// still stop short of it does (a car's length and a gap back), and holds there till it's out. The cars past it
        /// already go on, and leave the gap. (One car let in at a time on a lane.)
        /// </summary>
        void LetIn(Car c)
        {
            var q = queues[c.startLane];
            if (q.letting >= 0 && q.letting != c.id) return;
            double stop = c.startD - c.length - FOLLOWING;
            int held = -1;
            foreach (int id in q.cars)
            {
                if (Front(cars[id]) <= stop + 1e-6)
                {
                    held = id;
                    break;
                }
            }
            if (q.letting == c.id && q.held == held) return;
            if (q.letting != c.id) stats.letIn++;
            int was = q.held;
            double wasAt = was >= 0 && was != held && cars[was].seg == c.startLane ? Front(cars[was]) : double.NaN;
            q.letting = c.id;
            q.held = held;
            q.heldAt = Math.Max(0, stop);
            version++;
            if (was >= 0 && was != held && cars[was].seg == c.startLane) Refresh(cars[was], wasAt);
            if (held >= 0) Refresh(cars[held]);
        }

        /// <summary>Car `c`'s out (or given up): whoever held back for it goes on.</summary>
        void StopLetting(Car c)
        {
            if (c.startLane < 0 || c.startLane >= queues.Length) return;
            var q = queues[c.startLane];
            if (q.letting != c.id) return;
            int held = q.held;
            double was = held >= 0 && cars[held].seg == c.startLane ? Front(cars[held]) : double.NaN;
            q.letting = -1;
            q.held = -1;
            version++;
            if (held >= 0 && cars[held].seg == c.startLane) Refresh(cars[held], was);
        }

        /// <summary>On the last lane of its way: find a spot ahead to stop for, near the door, or hunt for one.</summary>
        void OnLastLane(Car c)
        {
            if (c.bus >= 0 || c.van >= 0 || c.dest.border || options.infiniteParking || c.target >= 0) return;
            if (!net.access.TryGetValue(c.dest.id, out var a)) return;
            var lane = net.lanes[c.seg];
            double door = net.DoneFor(lane, a) - lane.start;
            double here = Math.Max(Front(c), c.s0) + 0.5;
            int best = -1;
            double bestD = double.PositiveInfinity;
            foreach (int s in lane.spots)
            {
                if (!Free(s)) continue;
                double d = net.spots[s].s - lane.start;
                if (d < here) continue;
                double off = Math.Abs(d - door);
                if (off < bestD)
                {
                    bestD = off;
                    best = s;
                }
            }
            if (best >= 0)
            {
                Hold(c, best);
                c.goal = net.spots[best].s - lane.start;
                SetInterval(c, Front(c), c.goal);
                return;
            }
            // The street's full: hunt.
            c.hunts++;
            stats.hunts++;
            var way = router.Hunt(c.seg, c.id, Free, out int spot);
            if (way == null)
            {
                // Nowhere: put the car by the door anyway (as if there were room).
                stats.noSpot++;
                c.target = -1;
                c.goal = Math.Max(Front(c), door);
                c.huntFailed = true;
                SetInterval(c, Front(c), c.goal);
                return;
            }
            Hold(c, spot);
            c.route.RemoveRange(c.step, c.route.Count - c.step);
            c.route.AddRange(way);
            var sl = net.lanes[net.spots[spot].lane];
            c.goal = net.spots[spot].s - sl.start;
            SetInterval(c, Front(c), SegLength(c.seg));
        }

        /// <summary>A driver going somewhere holds the free kerb spot nearest its door within this (tiles) as they set off.</summary>
        const double RESERVE_REACH = 30;

        /// <summary>The free kerb spot nearest `door` within RESERVE_REACH, or -1.</summary>
        int NearestFreeSpot(Vec2 door)
        {
            if (spotGrid == null)
            {
                spotGrid = new Dictionary<long, List<int>>();
                for (int s = 0; s < net.spots.Count; s++)
                {
                    if (net.spots[s].kind != SpotKind.Kerb || net.spots[s].scenery) continue;
                    long key = SpotCell(net.spots[s].at);
                    if (!spotGrid.TryGetValue(key, out var list)) spotGrid[key] = list = new List<int>();
                    list.Add(s);
                }
            }
            int best = -1;
            double bestD = RESERVE_REACH;
            int cx = (int)Math.Floor(door.x / SPOT_CELL), cy = (int)Math.Floor(door.y / SPOT_CELL);
            int r = (int)Math.Ceiling(RESERVE_REACH / SPOT_CELL);
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (!spotGrid.TryGetValue(((long)(cx + dx) << 32) | (uint)(cy + dy), out var list)) continue;
                    foreach (int s in list)
                    {
                        if (spotHolder[s] >= 0) continue;
                        double d = Geo.Dist(door, net.spots[s].at);
                        if (d < bestD || (d == bestD && s < best))
                        {
                            bestD = d;
                            best = s;
                        }
                    }
                }
            return best;
        }

        const double SPOT_CELL = 10;
        Dictionary<long, List<int>> spotGrid;
        static long SpotCell(Vec2 p) => ((long)(int)Math.Floor(p.x / SPOT_CELL) << 32) | (uint)(int)Math.Floor(p.y / SPOT_CELL);

        bool Free(int spot)
        {
            var s = net.spots[spot];
            return s.kind == SpotKind.Kerb && !s.scenery && spotHolder[spot] < 0;
        }

        void Hold(Car c, int spot)
        {
            spotHolder[spot] = c.id;
            c.target = spot;
            c.reserved = false;
        }

        /// <summary>At the end of its way: a gate (off the map), a bus stop, or where it parks.</summary>
        void Reached(Car c)
        {
            if (c.bus >= 0)
            {
                AtStop(c);
                return;
            }
            if (c.van >= 0)
            {
                if (!VanOnward(c)) RemoveVan(c);
                return;
            }
            if (c.dest.border)
            {
                RemoveFromRoad(c);
                c.offMap = true;
                c.parkedBy = -1;
                if (c.owner >= 0) DriveDone(people[c.owner]);
                return;
            }
            c.state = CarState.Parking;
            bool drive = c.target >= 0 && net.spots[c.target].kind == SpotKind.Driveway;
            Schedule(K_CAR, c.id, Now + (drive ? DRIVEWAY_MS : PARK_KERB_MS));
        }

        void FinishParking(Car c)
        {
            RemoveFromRoad(c);
            if (c.target >= 0)
            {
                c.spot = c.target;
                spotHolder[c.spot] = c.id;
                c.parkedBy = c.dest.id;
            }
            else
            {
                c.spot = -1;
                c.parkedBy = c.dest.id;
            }
            c.target = -1;
            c.huntFailed = false;
            // It stays where it's parked, drawn, and its driver gets out and walks off from it.
            ShowParked(c, true);
            if (c.owner >= 0) DriveDone(people[c.owner]);
        }

        /// <summary>Where a parked car's driver gets in or out.</summary>
        Vec2 CarDoor(Car c)
        {
            int spot = c.spot >= 0 ? c.spot : c.target;
            if (spot >= 0) return net.spots[spot].at;
            if (c.seg >= 0) return CarPoint(c).at;
            int b = c.parkedBy >= 0 ? c.parkedBy : c.dest.id;
            if (net.access.TryGetValue(b, out var a))
            {
                int lane = net.SideLane(a.road, a.s, a.door);
                if (lane >= 0) return net.LanePoint(net.lanes[lane].road, net.lanes[lane].dir, net.DoneFor(net.lanes[lane], a), 0.6).at;
                return a.door;
            }
            return new Vec2(0, 0);
        }

        // --- Players in the street --------------------------------------------------------------

        /// <summary>Where the players are now: each stands in a lane (if on the road), and cars stop short of them.</summary>
        public void SetObstacles(IReadOnlyList<Vec2> at)
        {
            var want = new Dictionary<int, double>();
            if (at != null)
            {
                foreach (var p in at)
                {
                    // Every road they're on or beside: at a junction's mouth that's more than the nearest one (the
                    // end of the lane coming in is as near as the road across).
                    foreach (int road in net.RoadsNear(p, PASSING + 1))
                    {
                        double s = Math.Max(0, Math.Min(net.lengths[road], Geo.ArcPosition(p, net.roads[road].points)));
                        var centre = AutoDrive.PointAt(net.roads[road].points, net.Arcs(road), s);
                        bool onIt = Geo.Dist(centre, p) <= net.roads[road].width / 2 + 0.2;
                        int mine = onIt ? net.SideLane(road, s, p) : -1;
                        // Each lane whose cars would pass within reach of them: their own side's, and the other's
                        // too when they're near the middle of the road (a car's no narrower coming the other way).
                        foreach (int dir in new[] { 1, -1 })
                        {
                            int lane = net.LaneOf(road, dir);
                            if (lane < 0) continue;
                            var l = net.lanes[lane];
                            double along = l.dir > 0 ? s : net.lengths[road] - s;
                            if (lane != mine && Geo.Dist(net.LanePoint(road, dir, along, 0).at, p) > PASSING) continue;
                            double d = along - l.start;
                            // On the lane (or just before it); in the junction box the turns below are what's blocked.
                            if (d < -1 || d > l.Span + (lane == mine ? 1 : 0)) continue;
                            if (!want.TryGetValue(lane, out double was) || d < was) want[lane] = d;
                        }
                    }
                }
                // Stood in a junction: the turns that pass over them (no car starts one; one on it stops short).
                foreach (var p in at)
                    foreach (var j in net.junctions.Values)
                    {
                        if (Geo.Dist(j.at, p) > JUNCTION_REACH) continue;
                        foreach (int mid in j.movements)
                        {
                            double d = OnCurve(net.movements[mid], p);
                            if (d < 0) continue;
                            int seg = Lanes + mid;
                            if (!want.TryGetValue(seg, out double was) || d < was) want[seg] = d;
                        }
                    }
            }
            foreach (int lane in blocked)
            {
                if (want.ContainsKey(lane)) continue;
                var q = queues[lane];
                int first = q.blockFirst;
                q.blockAt = double.NaN;
                q.blockFirst = -1;
                version++;
                if (first >= 0) Refresh(cars[first]);
                // A turn clear again: those waiting to take it may.
                if (lane >= Lanes) Wake(net.movements[lane - Lanes].node);
            }
            blocked.Clear();
            foreach (var kv in want)
            {
                var q = queues[kv.Key];
                int first = -1;
                foreach (int id in q.cars)
                {
                    if (Front(cars[id]) <= kv.Value)
                    {
                        first = id;
                        break;
                    }
                }
                int was = q.blockFirst;
                double wasAt = q.blockAt;
                q.blockAt = kv.Value;
                q.blockFirst = first;
                blocked.Add(kv.Key);
                if (was != first || !(Math.Abs(wasAt - kv.Value) < 1e-3))
                {
                    version++;
                    if (was >= 0 && was != first) Refresh(cars[was]);
                    // Moved on further: the car behind may go on a little.
                    if (first >= 0 && (double.IsNaN(wasAt) || kv.Value > wasAt)) Refresh(cars[first]);
                }
            }
        }

        readonly List<int> blocked = new List<int>();
        /// <summary>How far from a junction's middle someone stood may be on one of its turns (tiles).</summary>
        const double JUNCTION_REACH = 4;
        /// <summary>Someone this near a lane's middle (tiles) is in its cars' way: half a car's width and a margin.</summary>
        const double PASSING = 0.7;
        /// <summary>How near a turn's curve someone stood is in its way (tiles).</summary>
        const double IN_THE_WAY = 0.5;

        /// <summary>How far round movement `m`'s curve the point nearest `p` is, if `p`'s in its way; else -1.</summary>
        static double OnCurve(Movement m, Vec2 p)
        {
            double best = IN_THE_WAY, along = -1;
            for (int k = 1; k < m.points.Count; k++)
            {
                var a = m.points[k - 1];
                var b = m.points[k];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2));
                double d = Numbers.Hypot(a.x + dx * t - p.x, a.y + dy * t - p.y);
                if (d < best)
                {
                    best = d;
                    along = m.at[k - 1] + (m.at[k] - m.at[k - 1]) * t;
                }
            }
            return along;
        }

        /// <summary>Held up too long by a player: round, back the other way, and a new way from there.</summary>
        bool TurnRound(Car c)
        {
            if (c.seg >= Lanes || c.bus >= 0) return false;
            var lane = net.lanes[c.seg];
            var back = net.Reverse(lane);
            double p = Front(c);
            double d = Math.Max(0, Math.Min(back.Span, lane.Span - p + c.length));
            var dest = c.dest;
            int target = c.target;
            var saved = (c.route, c.step, c.goal);
            RemoveFromRoad(c);
            if (target >= 0 && spotHolder[target] == c.id) spotHolder[target] = -1;
            c.target = -1;
            c.spot = -1;
            c.parkedBy = -1;
            c.startLane = back.id;
            c.startD = d;
            if (!PutOnRoad(c, back.id, d))
            {
                // No gap there either: back where it was, still waiting.
                PutOnRoad(c, lane.id, Math.Max(0, p));
                (c.route, c.step, c.goal) = saved;
                c.state = CarState.Queued;
                c.heldSince = Now;
                Schedule(K_CAR, c.id, Now + 2000);
                return true;
            }
            stats.turnedRound++;
            var targets = new List<(int, double)>();
            if (dest.border) targets.Add((net.borders[dest.id].exit, net.lanes[net.borders[dest.id].exit].Span));
            else if (net.access.TryGetValue(dest.id, out var a))
                foreach (int l in net.LanesFor(a)) targets.Add((l, Math.Max(0, Math.Min(net.lanes[l].Span, net.DoneFor(net.lanes[l], a) - net.lanes[l].start))));
            int k = -1;
            var route = targets.Count > 0 ? router.Route(back.id, d, targets, out k, out _) : null;
            if (route == null)
            {
                RemoveFromRoad(c);
                if (c.owner >= 0) Fail(people[c.owner], "turned round with nowhere to go");
                if (c.van >= 0) RemoveVan(c);
                return true;
            }
            c.route = route;
            c.step = 0;
            c.goal = targets[k].Item2;
            c.heldSince = -1;
            c.state = CarState.Crossing;
            SetInterval(c, d, route.Count == 1 ? c.goal : SegLength(c.seg));
            if (route.Count == 1) OnLastLane(c);
            return true;
        }
    }
}
