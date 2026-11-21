// People on foot. A walk is its pieces (Walkways): each a stretch walked at the
// person's pace, start to end, as one interval, so a walker costs nothing between
// the corner they set off from and the next. Walkers pass through each other, but a
// crowded sidewalk (1.5 people a square metre or more) halves their pace.
//
// Crossing at a junction is a request, as a car's turn is: not while a car is
// under way over that road there, not against the lights (walkers go while the
// road they cross is shown red, and only if they'll be over before it changes), and
// not while a car wanting that road has already waited longer than the crossing
// takes and a little more (so walkers can't keep cars waiting for ever). A car
// likewise waits while someone's crossing in front of it.

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
                    Schedule(K_WALK, p.id, retry > 0 ? retry : Now + ASK_AGAIN_MS);
                    return;
                }
                if (waitingToCross.TryGetValue(piece.node, out var waiting)) waiting.Remove(p.id);
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
                else if (piece.node >= 0 && waitingToCross.TryGetValue(piece.node, out var w)) w.Remove(p.id);
            }
            p.walk = null;
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
                if (Now + over > ends) return ends + RoadNet.STAGE_MS + 1;
            }
            if (underway.TryGetValue(j.node, out var going))
                foreach (var (car, local) in going)
                    if (Touches(net.movements[j.movements[local]], piece.road)) return -1;
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

        /// <summary>Where someone out is drawn, and the way they face.</summary>
        public (Vec2 at, Vec2 along) PersonPoint(Person p)
        {
            if (p.walk != null && p.piece < p.walk.Count)
            {
                var piece = p.walk[p.piece];
                if (p.w0 < 0) return Walkways.PointOn(piece, 0);
                double d = (Now - p.w0) / 1000.0 * p.wSpeed;
                return Walkways.PointOn(piece, d);
            }
            return (p.standing, new Vec2(0, 1));
        }
    }
}
