// Turn-by-turn directions for auto walk and auto drive: the planned route read
// out as steps on named streets, the way a map app would. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public enum StepKind { Start, Join, Straight, SlightLeft, SlightRight, Left, Right, Uturn, Cross, Arrive }

    public sealed class Step
    {
        public StepKind kind;
        public string text;
        public int street;
        /// <summary>How far to walk after it, before the next step (tiles).</summary>
        public double distance;
    }

    public static class Directions
    {
        /// <summary>About how many metres one tile is.</summary>
        public const double METRES_PER_TILE = 4;

        enum Via { Start, Link, Cross }

        struct WalkStretch
        {
            public int path;
            public double s0, s1;
            public int dir;
            public Via via;
        }

        public static List<Step> ForWalk(SidewalkNetwork net, StreetNames names, Itinerary it)
        {
            var stretches = WalkStretches(net, it);
            int StreetOf(int path) => names.ofRoad[net.paths[path].road];
            string Name(int street) => street >= 0 && street < names.streets.Count ? names.streets[street].name : "the street";
            var steps = new List<Step>();
            for (int i = 0; i < stretches.Count; i++)
            {
                var st = stretches[i];
                int street = StreetOf(st.path);
                double len = Math.Abs(st.s1 - st.s0);
                var last = steps.Count > 0 ? steps[steps.Count - 1] : null;
                if (st.via == Via.Start)
                {
                    var d = HeadingAt(net, st, true);
                    steps.Add(new Step
                    {
                        kind = it.joining ? StepKind.Join : StepKind.Start,
                        text = it.joining ? $"Make your way to {Name(street)}, then head {Compass(d)}" : $"Head {Compass(d)} on {Name(street)}",
                        street = street,
                        distance = len,
                    });
                    continue;
                }
                if (st.via == Via.Cross)
                {
                    steps.Add(new Step { kind = StepKind.Cross, text = $"Cross {Name(street)}", street = street, distance = len });
                    continue;
                }
                var before = HeadingAt(net, stretches[i - 1], false);
                var after = HeadingAt(net, st, true);
                var turn = TurnKind(before, after);
                if (street == last.street && turn != StepKind.Uturn)
                {
                    last.distance += len;
                    continue;
                }
                steps.Add(new Step { kind = turn, text = TurnText(turn, street, last.street, Name(street)), street = street, distance = len });
            }
            int goalStreet = StreetOf(it.goal);
            steps.Add(new Step { kind = StepKind.Arrive, text = $"Arrive at {it.destination ?? "your destination"} on {Name(goalStreet)}", street = goalStreet, distance = 0 });
            for (int k = steps.Count - 2; k >= 1; k--)
            {
                var a = steps[k - 1];
                var b = steps[k];
                if (b.kind == StepKind.Cross && a.street == b.street && a.distance < 0.6 && a.kind != StepKind.Start && a.kind != StepKind.Join)
                {
                    a.text += " and cross to the other side";
                    a.distance += b.distance;
                    steps.RemoveAt(k);
                }
            }
            return steps;
        }

        static string TurnText(StepKind turn, int street, int lastStreet, string name)
        {
            if (turn == StepKind.Uturn) return street == lastStreet ? $"Turn round at the end of {name}" : $"Turn round onto {name}";
            if (turn == StepKind.Straight) return $"Continue onto {name}";
            bool slight = turn == StepKind.SlightLeft || turn == StepKind.SlightRight;
            bool left = turn == StepKind.SlightLeft || turn == StepKind.Left;
            return $"{(slight ? "Bear" : "Turn")} {(left ? "left" : "right")} onto {name}";
        }

        static List<WalkStretch> WalkStretches(SidewalkNetwork net, Itinerary it)
        {
            double Len(int path) => net.paths[path].Length;
            var o = new List<WalkStretch>();
            int path = it.start.path;
            double s = it.start.s;
            int dir = it.start.dir;
            var via = Via.Start;
            foreach (var leg in it.legs)
            {
                if (leg.cross)
                {
                    o.Add(new WalkStretch { path = path, s0 = s, s1 = s, dir = dir, via = via });
                    var here = SidewalkWalk.PointAlong(net.paths[path], s);
                    s = Geo.ArcPosition(here, net.paths[leg.path].points);
                    via = Via.Cross;
                }
                else
                {
                    o.Add(new WalkStretch { path = path, s0 = s, s1 = dir > 0 ? Len(path) : 0, dir = dir, via = via });
                    s = leg.dir > 0 ? 0 : Len(leg.path);
                    via = Via.Link;
                }
                path = leg.path;
                dir = leg.dir;
            }
            o.Add(new WalkStretch { path = path, s0 = s, s1 = path == it.goal ? (double.IsNaN(it.goalS) ? Len(path) / 2 : it.goalS) : dir > 0 ? Len(path) : 0, dir = dir, via = via });
            return o;
        }

        static Vec2 HeadingAt(SidewalkNetwork net, WalkStretch st, bool start)
        {
            var p = net.paths[st.path];
            double total = p.Length;
            int sign = st.s1 == st.s0 ? st.dir : st.s1 > st.s0 ? 1 : -1;
            double s = start ? st.s0 : st.s1;
            var a = SidewalkWalk.PointAlong(p, Math.Max(0, Math.Min(total, s - 0.4 * sign)));
            var b = SidewalkWalk.PointAlong(p, Math.Max(0, Math.Min(total, s + 0.4 * sign)));
            double l = Numbers.Hypot(b.x - a.x, b.y - a.y);
            if (l == 0 || double.IsNaN(l)) l = 1;
            return new Vec2((b.x - a.x) / l, (b.y - a.y) / l);
        }

        /// <summary>What kind of turn takes you from heading a to heading b (map y runs south, so clockwise is right).</summary>
        public static StepKind TurnKind(Vec2 a, Vec2 b)
        {
            double angle = (DetMath.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y) * 180) / Math.PI;
            double m = Math.Abs(angle);
            if (m < 30) return StepKind.Straight;
            if (m > 150) return StepKind.Uturn;
            bool right = angle > 0;
            if (m < 60) return right ? StepKind.SlightRight : StepKind.SlightLeft;
            return right ? StepKind.Right : StepKind.Left;
        }

        /// <summary>Compass point of a map direction (north is up the map, -y).</summary>
        public static string Compass(Vec2 d)
        {
            string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            double deg = (DetMath.Atan2(d.x, -d.y) * 180) / Math.PI;
            return points[(int)((Numbers.Round(deg / 45) + 8) % 8)];
        }

        /// <summary>A walking distance in tiles as shown to the player: metres, rounded like a map app would.</summary>
        public static string FormatDistance(double tiles)
        {
            double m = tiles * METRES_PER_TILE;
            if (m < 10) return "";
            if (m < 100) return $"{Numbers.NumberToString(Numbers.Round(m / 5) * 5)} m";
            if (m < 1000) return $"{Numbers.NumberToString(Numbers.Round(m / 10) * 10)} m";
            return $"{Numbers.ToFixed(m / 1000, 1)} km";
        }

        /// <summary>Auto drive's route as steps.</summary>
        public static List<Step> ForDrive(StreetNames names, List<Stretch> stretches)
        {
            string Name(int street) => street >= 0 && street < names.streets.Count ? names.streets[street].name : "the road";
            var steps = new List<Step>();
            for (int i = 0; i < stretches.Count; i++)
            {
                var st = stretches[i];
                int street = names.ofRoad[st.road];
                if (i == 0)
                {
                    steps.Add(new Step { kind = StepKind.Start, text = $"Head {Compass(st.start)} on {Name(street)}", street = street, distance = st.length });
                    continue;
                }
                var last = steps[steps.Count - 1];
                var turn = TurnKind(stretches[i - 1].end, st.start);
                if (street == last.street && turn != StepKind.Uturn)
                {
                    last.distance += st.length;
                    continue;
                }
                steps.Add(new Step { kind = turn, text = TurnText(turn, street, last.street, Name(street)), street = street, distance = st.length });
            }
            if (stretches.Count > 0)
            {
                int street = names.ofRoad[stretches[stretches.Count - 1].road];
                steps.Add(new Step { kind = StepKind.Arrive, text = $"Arrive at your destination on {Name(street)}", street = street, distance = 0 });
            }
            return steps;
        }

        public static string Icon(StepKind k) => k switch
        {
            StepKind.Start => "↑",
            StepKind.Join => "↗",
            StepKind.Straight => "↑",
            StepKind.SlightLeft => "↖",
            StepKind.SlightRight => "↗",
            StepKind.Left => "↰",
            StepKind.Right => "↱",
            StepKind.Uturn => "⤺",
            StepKind.Cross => "⇅",
            _ => "⚑",
        };
    }
}
