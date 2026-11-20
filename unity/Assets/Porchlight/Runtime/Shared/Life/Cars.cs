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

        double Lerp(Car c)
        {
            if (Now >= c.t1 || c.t1 <= c.t0) return c.s1;
            return c.s0 + (c.s1 - c.s0) * (Now - c.t0) / (double)(c.t1 - c.t0);
        }

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
            p = Math.Max(p, Math.Min(c.s0, -c.length));
            frontMemo[c.id] = p;
            return p;
        }

        void SetInterval(Car c, double from, double to)
        {
            c.s0 = from;
            c.s1 = Math.Max(from, to);
            c.t0 = Now;
            // No faster than the turn it's still coming out of (its back's there): it swings round, not snaps.
            double speed = Math.Max(0.05, c.laggy >= 0 ? Math.Min(SegSpeed(c.seg), SegSpeed(c.laggy)) : SegSpeed(c.seg));
            c.t1 = Now + (long)Math.Ceiling((c.s1 - from) / speed * 1000);
            version++;
            Schedule(K_CAR, c.id, c.t1);
        }

        /// <summary>A car held back gets a fresh interval from where it really is, so nothing jumps when what held it goes.</summary>
        void Refresh(Car c)
        {
            if (c.seg < 0 || c.state == CarState.Parking || c.state == CarState.Unparking || c.state == CarState.AtStop) return;
            double p = Front(c);
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
            if (q.laggy == c.id) q.laggy = -1;
            version++;
            if (seg < Lanes) Uncommit(seg, c);
            else
            {
                int node = net.movements[seg - Lanes].node;
                if (underway.TryGetValue(node, out var list)) list.RemoveAll(u => u.car == c.id);
                Wake(node);
            }
            RefreshFront(seg);
        }

        // --- Junctions --------------------------------------------------------------------------

        void Ask(Car c, int mid)
        {
            var m = net.movements[mid];
            var j = net.junctions[m.node];
            if (!waitingAt.TryGetValue(j.node, out var waiting)) waitingAt[j.node] = waiting = new List<int>();
            if (!waiting.Contains(c.id)) waiting.Add(c.id);
            blockedBy.Remove(c.id);
            var verdict = Judge(c, m, j, out long again, out var by, out bool knot, null);
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
        Verdict Judge(Car c, Movement m, Junction j, out long again, out List<int> by, out bool knot, List<Why> explain)
        {
            again = Now;
            by = null;
            knot = false;
            if (m.stops)
            {
                bool served = c.stopped || Now >= c.arrived + STOP_MS;
                explain?.Add(new Why("Must it stop first?", served ? "Yes, and it has" : $"Yes: stopping until {TownClock.FormatSeconds(c.arrived + STOP_MS)}", served,
                    j.control == Control.AllWayStop ? "An all-way stop: every car stops." : "A stop sign onto a bigger road."));
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

        /// <summary>Is there a gap at `d` along `lane` for a car this long (and room in the lane)?</summary>
        bool Gap(int lane, double d, Car c, out int index)
        {
            var q = queues[lane];
            index = 0;
            if (!Room(lane, c)) return false;
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
            c.offMap = false;
            version++;
            TakeCarSlot(c);
            return true;
        }

        /// <summary>Off the road altogether (parked, gone off the map, or taken).</summary>
        void RemoveFromRoad(Car c)
        {
            Cancel(K_CAR, c.id);
            Cancel(K_CLEAR, c.id);
            wantsOut.Remove(c.id);
            if (c.laggy >= 0) ClearLaggy(c);
            if (c.seg >= 0)
            {
                int seg = c.seg;
                var q = queues[seg];
                int i = q.cars.IndexOf(c.id);
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
                if (i >= 0 && i < q.cars.Count) Refresh(cars[q.cars[i]]);
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
            }
            else c.target = -1;
            return true;
        }

        /// <summary>Out of its spot onto the road, if there's a gap; if not, again shortly.</summary>
        void PullOut(Car c)
        {
            if (!PutOnRoad(c, c.startLane, c.startD))
            {
                int n = tries.TryGetValue(c.id, out int t) ? t + 1 : 1;
                tries[c.id] = n;
                if (n >= MOST_TRIES)
                {
                    wantsOut.Remove(c.id);
                    if (c.target >= 0 && spotHolder[c.target] == c.id) spotHolder[c.target] = -1;
                    c.target = -1;
                    if (c.owner >= 0) Fail(people[c.owner], "no gap to pull out into");
                    return;
                }
                Schedule(K_CAR, c.id, Now + 3000);
                return;
            }
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

        /// <summary>On the last lane of its way: find a spot ahead to stop for, near the door, or hunt for one.</summary>
        void OnLastLane(Car c)
        {
            if (c.bus >= 0 || c.dest.border || options.infiniteParking || c.target >= 0) return;
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

        bool Free(int spot)
        {
            var s = net.spots[spot];
            return s.kind == SpotKind.Kerb && !s.scenery && spotHolder[spot] < 0;
        }

        void Hold(Car c, int spot)
        {
            spotHolder[spot] = c.id;
            c.target = spot;
        }

        /// <summary>At the end of its way: a gate (off the map), a bus stop, or where it parks.</summary>
        void Reached(Car c)
        {
            if (c.bus >= 0)
            {
                AtStop(c);
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
                    int road = net.NearestRoad(p, out double s);
                    if (road < 0) continue;
                    var centre = AutoDrive.PointAt(net.roads[road].points, net.Arcs(road), s);
                    if (Geo.Dist(centre, p) > net.roads[road].width / 2 + 0.2) continue;
                    int lane = net.SideLane(road, s, p);
                    if (lane < 0) continue;
                    var l = net.lanes[lane];
                    double d = (l.dir > 0 ? s : net.lengths[road] - s) - l.start;
                    if (d < -1 || d > l.Span + 1) continue;
                    if (!want.TryGetValue(lane, out double was) || d < was) want[lane] = d;
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
