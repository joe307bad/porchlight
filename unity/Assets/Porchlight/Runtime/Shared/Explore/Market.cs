// The market in the plaza (the town's Market landmark): where its stalls stand on
// market day (TownCalendar), where each stallholder stands behind theirs, the
// aisle shoppers browse along, and the way in from the street. The stalls stand on
// the plaza's inner tiles (those with plaza all round: the plaza's planters,
// statues and parasols only stand on its edge), a tile and a half apart, facing the
// plaza's middle, nearest the middle first. The game draws them (MarketStalls) and
// the town's life sends its stallholders and shoppers to them. Pure: the same
// from the same town on every machine.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class MarketStall
    {
        /// <summary>Its middle (tiles), and the way its front faces (radians, as Coords.Yaw takes it: atan2(x, y)).</summary>
        public Vec2 at;
        public double heading;
        /// <summary>Where a shopper stands at its front, and its stallholder behind it.</summary>
        public Vec2 front, keeper;
        /// <summary>What it sells (0-3: vegetables, fruit, flowers, odds and ends), for the drawing.</summary>
        public int kind;
    }

    public sealed class MarketPlace
    {
        /// <summary>The most stalls a market has.</summary>
        public const int MOST_STALLS = 12;
        /// <summary>Stalls stand at least this far apart (tiles, middle to middle).</summary>
        const double SPACING = 1.4;

        public readonly List<MarketStall> stalls = new List<MarketStall>();
        /// <summary>The way in: a plaza tile beside the street, nearest the plaza's middle.</summary>
        public Vec2 door;
        /// <summary>The plaza's middle.</summary>
        public Vec2 middle;

        /// <summary>How many stalls are up when `share` of the pitches are taken (TownDay.stalls): at least two.</summary>
        public int Up(double share) => stalls.Count == 0 ? 0 : Math.Max(Math.Min(2, stalls.Count), (int)Math.Round(share * stalls.Count));

        /// <summary>The town's market, or null if it has no Market landmark or no room in its plaza for a stall.</summary>
        public static MarketPlace Of(TownModel model)
        {
            if (model?.tiles?.overlay == null || model.landmarks == null) return null;
            bool any = false;
            Vec2 well = new Vec2(-1, -1);
            foreach (var l in model.landmarks)
            {
                if (l.kind == LandmarkKind.Market) any = true;
                if (l.kind == LandmarkKind.Well) well = l.at;
            }
            if (!any) return null;
            int w = model.width, h = model.height;
            var overlay = model.tiles.overlay;
            bool Plaza(int x, int y) => Grid.InBounds(x, y, w, h) && overlay[Grid.Index(x, y, w)] == Overlay.PLAZA;
            var inner = new List<(int x, int y)>();
            double mx = 0, my = 0;
            int n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Plaza(x, y)) continue;
                    mx += x + 0.5;
                    my += y + 0.5;
                    n++;
                    bool all = true;
                    foreach (var (dx, dy) in Grid.DIRS4) if (!Plaza(x + dx, y + dy)) all = false;
                    if (all && !(Math.Floor(well.x) == x && Math.Floor(well.y) == y)) inner.Add((x, y));
                }
            if (n == 0) return null;
            var m = new MarketPlace { middle = new Vec2(mx / n, my / n) };
            var mid = m.middle;
            // Nearest the middle first (ties by place, so it's the same everywhere).
            inner.Sort((a, b) =>
            {
                double da = Geo.Dist(new Vec2(a.x + 0.5, a.y + 0.5), mid), db = Geo.Dist(new Vec2(b.x + 0.5, b.y + 0.5), mid);
                return da != db ? da.CompareTo(db) : a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x);
            });
            // The middle tile itself is left for the shoppers in a plaza with room: the stalls stand round it.
            foreach (var (x, y) in inner)
            {
                if (m.stalls.Count >= MOST_STALLS) break;
                var at = new Vec2(x + 0.5, y + 0.5);
                if (inner.Count > 6 && Geo.Dist(at, mid) < 0.9) continue;
                bool room = true;
                foreach (var s in m.stalls) if (Geo.Dist(s.at, at) < SPACING) room = false;
                if (!room) continue;
                double dx = mid.x - at.x, dy = mid.y - at.y, l = Math.Max(1e-9, Numbers.Hypot(dx, dy));
                dx /= l;
                dy /= l;
                m.stalls.Add(new MarketStall
                {
                    at = at,
                    heading = DetMath.Atan2(dx, dy),
                    front = new Vec2(at.x + dx * 0.75, at.y + dy * 0.75),
                    keeper = new Vec2(at.x - dx * 0.45, at.y - dy * 0.45),
                    kind = m.stalls.Count % 4,
                });
            }
            if (m.stalls.Count == 0) return null;
            // The way in: the plaza tile beside a street nearest the middle.
            double best = double.PositiveInfinity;
            m.door = mid;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!Plaza(x, y)) continue;
                    bool street = false;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        if (!Grid.InBounds(x + dx, y + dy, w, h)) continue;
                        byte o = overlay[Grid.Index(x + dx, y + dy, w)];
                        if (o == Overlay.ROAD_MAIN || o == Overlay.ROAD_STREET) street = true;
                    }
                    var at = new Vec2(x + 0.5, y + 0.5);
                    double d = Geo.Dist(at, mid);
                    if (street && d < best)
                    {
                        best = d;
                        m.door = at;
                    }
                }
            return m;
        }

        /// <summary>
        /// Where a shopper browsing since `since` is at `now` (ms), and the way they face: stall to stall along the
        /// fronts, at a stroll, stopping a while at each, starting at a stall of their own (`who`).
        /// </summary>
        public (Vec2 at, Vec2 along) Browse(int who, long since, long now, int up)
        {
            int count = Math.Max(1, Math.Min(up, stalls.Count));
            double t = Math.Max(0, (now - since) / 1000.0);
            int k = (int)(Layout.Hash(who, 4517) * count) % count;
            var at = stalls[k].front;
            for (int step = 0; step < 400; step++)
            {
                // A while at the stall (20 s to a minute and a half)...
                double stay = 20 + 70 * Layout.Hash(who * 31 + step, 4519);
                var face = new Vec2(stalls[k].at.x - at.x, stalls[k].at.y - at.y);
                if (t < stay) return (at, Unit(face));
                t -= stay;
                // ...then on to another, at a stroll.
                int next = count < 2 ? k : (k + 1 + (int)(Layout.Hash(who * 37 + step, 4523) * (count - 1))) % count;
                var to = stalls[next].front;
                double walk = Geo.Dist(at, to) / STROLL;
                if (t < walk)
                {
                    double f = walk <= 0 ? 1 : t / walk;
                    return (new Vec2(at.x + (to.x - at.x) * f, at.y + (to.y - at.y) * f), Unit(new Vec2(to.x - at.x, to.y - at.y)));
                }
                t -= walk;
                at = to;
                k = next;
            }
            return (at, new Vec2(0, 1));
        }

        /// <summary>A browsing shopper's pace (tiles a second): a slow stroll.</summary>
        public const double STROLL = 0.15;

        static Vec2 Unit(Vec2 v)
        {
            double l = Numbers.Hypot(v.x, v.y);
            return l < 1e-9 ? new Vec2(0, 1) : new Vec2(v.x / l, v.y / l);
        }
    }
}
