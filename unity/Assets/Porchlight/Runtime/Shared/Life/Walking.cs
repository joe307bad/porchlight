// People on foot. A walk is its pieces (Walkways): each a stretch walked at the
// person's pace, start to end, as one interval, so a walker costs nothing between
// the corner they set off from and the next. Walkers pass through each other, but a
// crowded sidewalk (1.5 people a square metre or more) halves their pace.
//
// Crossing at a junction is a request, as a car's turn is: not while a car is
// under way over that road there, not against the lights (walkers go while the
// road they cross is shown red, and only if they'll be over before it changes), and
// not in front of a car coming up at speed to go on round without stopping, and
// not while a car wanting that road has already waited longer than the crossing
// takes and a little more (so walkers can't keep cars waiting for ever). A car
// likewise waits while someone's crossing in front of it.
//
// Nobody stands on anyone else. Those waiting at a kerb take a spot each in a crowd
// on the sidewalk there (Walkways.CrowdSpot), the frontmost free, and keep it till
// they go; they walk to it from the corner, face the crossing, and step out from it.
// On the move each keeps to a lane of their own a little to one side of the
// sidewalk's middle (by their id), easing in and out of it at a piece's ends.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed partial class TownLife
    {
        /// <summary>People a square metre at which walkers slow to half their pace.</summary>
        public const double CROWDED = 1.5;
        /// <summary>A car that's waited this much longer than a crossing takes goes first.</summary>
        const long WALKER_YIELD_MS = 3000;

        /// <summary>At each junction, who's crossing which road now.</summary>
        readonly Dictionary<int, List<(int person, int road)>> crossingNow = new Dictionary<int, List<(int, int)>>();
        /// <summary>At each junction, who's waiting to cross.</summary>
        readonly Dictionary<int, List<int>> waitingToCross = new Dictionary<int, List<int>>();
        /// <summary>How many are walking each sidewalk.</summary>
        readonly Dictionary<int, int> onSidewalk = new Dictionary<int, int>();
        /// <summary>At each sidewalk end a crossing sets off from, who stands in each crowd spot (-1: nobody).</summary>
        readonly KerbCrowds crowds = new KerbCrowds();
        /// <summary>Walkers' own lanes are up to this far either side of the way's middle (tiles), in steps of a seventh.</summary>
        const double LANE_SPREAD = 0.135;
        /// <summary>Walkers ease into their lane over this much of a piece at each end (tiles).</summary>
        const double LANE_EASE = 0.25;

        void StartWalk(Person p, Vec2 from, Vec2 to)
        {
            var pieces = Geo.Dist(from, to) < 1e-6 ? null : walkways.Route(from, to);
            if (pieces == null || pieces.Count == 0)
            {
                NextLeg(p);
                return;
            }
            p.walk = pieces;
            p.piece = 0;
            p.w0 = -1;
            TakePersonSlot(p);
            BeginPiece(p);
        }

        void BeginPiece(Person p)
        {
            var piece = p.walk[p.piece];
            if (piece.node >= 0)
            {
                if (p.crossWait < 0) p.crossWait = Now;
                long retry = MayCross(p, piece);
                if (retry != 0)
                {
                    if (!waitingToCross.TryGetValue(piece.node, out var w)) waitingToCross[piece.node] = w = new List<int>();
                    if (!w.Contains(p.id)) w.Add(p.id);
                    JoinCrowd(p, piece);
                    Schedule(K_WALK, p.id, retry > 0 ? retry : Now + ASK_AGAIN_MS);
                    return;
                }
                if (waitingToCross.TryGetValue(piece.node, out var waiting)) waiting.Remove(p.id);
                if (p.crowdSpot >= 0)
                {
                    p.crowdFrom = WaitPoint(p, piece);
                    p.fromCrowd = true;
                    LeaveCrowd(p, piece);
                }
                if (!crossingNow.TryGetValue(piece.node, out var now)) crossingNow[piece.node] = now = new List<(int, int)>();
                now.Add((p.id, piece.road));
                p.crossing = piece.node;
                stats.Delay(piece.node, Now - p.crossWait, car: false);
                p.crossWait = -1;
            }
            if (piece.overMain) stats.mainCrossings++;
            int count = 0;
            if (piece.path >= 0)
            {
                onSidewalk.TryGetValue(piece.path, out count);
                onSidewalk[piece.path] = count + 1;
            }
            double crowd = walkways.Crowding(piece.path, count + 1);
            if (crowd >= CROWDED) stats.crowded++;
            p.wSpeed = p.pace * (crowd >= CROWDED ? 0.5 : 1);
            p.w0 = Now;
            Schedule(K_WALK, p.id, Now + (long)Math.Ceiling(piece.length / p.wSpeed * 1000));
        }

        void OnWalk(Person p)
        {
            if (p.walk == null)
            {
                if (WaitingForBus(p)) Fail(p, "no bus came");
                return;
            }
            if (p.w0 < 0)
            {
                // Still waiting to cross: ask again.
                BeginPiece(p);
                return;
            }
            EndPiece(p);
            p.piece++;
            p.w0 = -1;
            if (p.piece >= p.walk.Count)
            {
                p.walk = null;
                NextLeg(p);
                return;
            }
            BeginPiece(p);
        }

        void EndPiece(Person p)
        {
            var piece = p.walk[p.piece];
            if (piece.path >= 0 && onSidewalk.TryGetValue(piece.path, out int n)) onSidewalk[piece.path] = Math.Max(0, n - 1);
            p.fromCrowd = false;
            if (p.crossing >= 0)
            {
                int node = p.crossing;
                p.crossing = -1;
                if (crossingNow.TryGetValue(node, out var list)) list.RemoveAll(x => x.person == p.id);
                Wake(node);
            }
        }

        /// <summary>Off their feet (into a car, a bus or a building, or put somewhere): forget the walk.</summary>
        void StopWalking(Person p)
        {
            Cancel(K_WALK, p.id);
            if (p.walk != null && p.piece < p.walk.Count)
            {
                var piece = p.walk[p.piece];
                if (p.w0 >= 0) EndPiece(p);
                else
                {
                    if (piece.node >= 0 && waitingToCross.TryGetValue(piece.node, out var w)) w.Remove(p.id);
                    LeaveCrowd(p, piece);
                }
            }
            p.walk = null;
            p.crowdSpot = -1;
            p.fromCrowd = false;
            p.crossWait = -1;
            p.crossing = -1;
        }

        /// <summary>0: may cross now. Otherwise when to ask again (-1: when woken, or a while).</summary>
        long MayCross(Person p, WalkPiece piece)
        {
            if (!net.junctions.TryGetValue(piece.node, out var j)) return 0;
            // A last resort: nobody waits at a kerb for ever.
            if (Now - p.crossWait > GIVE_WAY_MS) return 0;
            long over = (long)Math.Ceiling(piece.length / p.pace * 1000);
            if (j.control == Control.Signal)
            {
                int leg = j.legs.IndexOf(piece.road);
                int green = leg >= 0 ? j.legStage[leg] : -1;
                var (stage, ends) = RoadNet.Stage(j, Now);
                if (stage == green) return ends + 1;
                if (Now + over > ends) return ends + j.green[1 - stage] + 1;
            }
            if (underway.TryGetValue(j.node, out var going))
                foreach (var (car, local) in going)
                    if (Touches(net.movements[j.movements[local]], piece.road)) return -1;
            // Not in front of a car coming up to the junction at speed, meaning to go on round without stopping (it
            // couldn't stop short in time, and would stop dead at the line): after it's gone. Twice the crossing's time,
            // for a crowded kerb's half pace.
            foreach (int mi in j.movements)
            {
                var m = net.movements[mi];
                if (!Touches(m, piece.road) || m.from < 0 || m.from >= Lanes) continue;
                var q = queues[m.from];
                if (q.cars.Count == 0) continue;
                var c = cars[q.cars[0]];
                if (c.seg != m.from || c.vb <= 1e-9 || c.t1 <= Now || c.t1 > Now + 2 * over + WALKER_YIELD_MS) continue;
                if (c.s1 < SegLength(c.seg) - 1e-6 || c.step + 1 >= c.route.Count || c.route[c.step + 1] != Lanes + mi) continue;
                return c.t1 + 1;
            }
            if (waitingAt.TryGetValue(j.node, out var waiting))
            {
                foreach (int id in waiting)
                {
                    var c = cars[id];
                    if (c.state != CarState.Waiting || c.seg < 0 || c.seg >= Lanes || c.step + 1 >= c.route.Count) continue;
                    var m = net.movements[c.route[c.step + 1] - Lanes];
                    if (Touches(m, piece.road) && Now - c.arrived > over + WALKER_YIELD_MS) return -1;
                }
            }
            return 0;
        }

        bool Touches(Movement m, int road) => net.lanes[m.from].road == road || net.lanes[m.to].road == road;

        /// <summary>Is someone crossing a road this movement uses?</summary>
        bool CrossingInTheWay(Junction j, Movement m)
        {
            if (!crossingNow.TryGetValue(j.node, out var list) || list.Count == 0) return false;
            foreach (var (_, road) in list) if (Touches(m, road)) return true;
            return false;
        }

        /// <summary>Walkers waiting at a junction to cross ask again (after the cars woken with them).</summary>
        void WakeWalkers(int node)
        {
            if (!waitingToCross.TryGetValue(node, out var list)) return;
            for (int k = 0; k < list.Count; k++) Schedule(K_WALK, list[k], Now + 1 + k);
        }

        /// <summary>Into the crowd at the kerb a crossing sets off from: the frontmost spot free.</summary>
        void JoinCrowd(Person p, WalkPiece piece)
        {
            if (p.crowdSpot >= 0 || piece.from < 0) return;
            p.crowdSpot = crowds.Join(piece.from, p.id);
        }

        void LeaveCrowd(Person p, WalkPiece piece)
        {
            if (p.crowdSpot < 0) return;
            crowds.Leave(piece.from, p.crowdSpot, p.id);
            p.crowdSpot = -1;
        }

        /// <summary>Who stands in each spot of the crowd at a sidewalk end (-1: nobody), or null: for the checks.</summary>
        public IReadOnlyList<int> CrowdAt(int end) => crowds.At(end);

        /// <summary>Where someone waiting at a kerb stands: walking at their pace from the corner to their spot in the crowd.</summary>
        Vec2 WaitPoint(Person p, WalkPiece piece)
        {
            var corner = piece.points[0];
            var spot = walkways.CrowdSpot(piece.from, p.crowdSpot);
            double far = Geo.Dist(corner, spot);
            double walked = p.crossWait < 0 ? far : (Now - p.crossWait) / 1000.0 * p.pace;
            return far <= walked ? spot : Geo.Lerp(corner, spot, walked / far);
        }

        /// <summary>How far to the left of the way's middle someone walks (tiles): one of seven lanes, by their id.</summary>
        static double LaneOf(int id)
        {
            uint h = unchecked((uint)id * 2654435761u);
            return ((int)((h >> 16) % 7) - 3) * (LANE_SPREAD / 3);
        }

        /// <summary>Where someone out is drawn, and the way they face.</summary>
        public (Vec2 at, Vec2 along) PersonPoint(Person p)
        {
            if (p.browseSince >= 0 && p.trip == null && market != null) return MarketPoint(p);
            if (p.walk != null && p.piece < p.walk.Count)
            {
                var piece = p.walk[p.piece];
                if (p.w0 < 0)
                {
                    var start = Walkways.PointOn(piece, 0);
                    if (piece.node >= 0 && p.crowdSpot >= 0) return (WaitPoint(p, piece), start.along);
                    return start;
                }
                double d = Math.Max(0, Math.Min(piece.length, (Now - p.w0) / 1000.0 * p.wSpeed));
                var (at, along) = Walkways.PointOn(piece, d);
                // Their own lane: square to the way (over a short chord, so it turns smoothly at a bend), eased in and out at the ends.
                var a = Walkways.PointOn(piece, d - 0.1).at;
                var b = Walkways.PointOn(piece, d + 0.1).at;
                double dx = b.x - a.x, dy = b.y - a.y, l = Numbers.Hypot(dx, dy);
                if (l > 1e-9)
                {
                    along = new Vec2(dx / l, dy / l);
                    double ease = Math.Min(1, Math.Min(d, piece.length - d) / LANE_EASE);
                    double lane = LaneOf(p.id) * ease;
                    at = new Vec2(at.x - along.y * lane, at.y + along.x * lane);
                }
                // Stepping out of the crowd: from their spot, closing in on their lane over the first steps.
                if (p.fromCrowd && piece.node >= 0)
                {
                    double offX = p.crowdFrom.x - piece.points[0].x, offY = p.crowdFrom.y - piece.points[0].y;
                    double over = Math.Max(0.3, Numbers.Hypot(offX, offY));
                    double left = Math.Max(0, 1 - d / over);
                    at = new Vec2(at.x + offX * left, at.y + offY * left);
                }
                return (at, along);
            }
            return (p.standing, new Vec2(0, 1));
        }
    }

    /// <summary>
    /// Who stands where in the crowd at each kerb: by sidewalk end, a spot each (Walkways.CrowdSpot), the frontmost
    /// free to whoever comes, kept till they go.
    /// </summary>
    public sealed class KerbCrowds
    {
        readonly Dictionary<int, List<int>> crowds = new Dictionary<int, List<int>>();

        /// <summary>`who` joins the crowd at sidewalk end `end`: the spot they take.</summary>
        public int Join(int end, int who)
        {
            if (!crowds.TryGetValue(end, out var spots)) crowds[end] = spots = new List<int>();
            int k = spots.IndexOf(-1);
            if (k < 0)
            {
                k = spots.Count;
                spots.Add(who);
            }
            else spots[k] = who;
            return k;
        }

        /// <summary>`who` leaves spot `k` at sidewalk end `end`, if it's theirs.</summary>
        public void Leave(int end, int k, int who)
        {
            if (end < 0 || !crowds.TryGetValue(end, out var spots) || k < 0 || k >= spots.Count || spots[k] != who) return;
            spots[k] = -1;
            while (spots.Count > 0 && spots[spots.Count - 1] < 0) spots.RemoveAt(spots.Count - 1);
        }

        /// <summary>Who stands in each spot at sidewalk end `end` (-1: nobody), or null if nobody's waited there.</summary>
        public IReadOnlyList<int> At(int end) => crowds.TryGetValue(end, out var spots) ? spots : null;
    }
}
