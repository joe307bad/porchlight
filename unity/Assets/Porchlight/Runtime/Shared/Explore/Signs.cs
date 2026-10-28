// Where the street name signs stand: a slim post on a corner of every
// junction where differently named streets meet, with a blade per street. The
// signs themselves are built in Rendering/WorldParts.cs.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public struct SignBlade
    {
        /// <summary>Street index (into StreetNames.streets).</summary>
        public int street;
        /// <summary>Unit direction the street runs from the junction (map space).</summary>
        public Vec2 along;
    }

    public sealed class Signpost
    {
        public Vec2 at;
        public List<SignBlade> blades;
    }

    public static class Signs
    {
        public const double BLADE_LENGTH = 0.44;
        public const double BLADE_HEIGHT = 0.086;
        public const double BLADE_THICK = 0.012;
        public const double BLADE_Y = 0.6;
        public const double BLADE_STEP = 0.1;
        public const double POST_RADIUS = 0.014;
        public const double BLADE_GAP = 0.004;
        const double SIGN_SPACING = 1.6;

        struct End
        {
            public int road;
            public Vec2 dir;
        }

        public static List<Signpost> Plan(TownModel model, IsoLayout layout, StreetNames names)
        {
            var roads = layout.roads;
            var ends = new Dictionary<int, List<End>>();
            var order = new List<int>();
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                var rev = new List<Vec2>(r.points);
                rev.Reverse();
                foreach (var (node, pts) in new[] { (r.from, r.points), (r.to, rev) })
                {
                    if (!ends.TryGetValue(node, out var list))
                    {
                        ends[node] = list = new List<End>();
                        order.Add(node);
                    }
                    list.Add(new End { road = i, dir = StreetNaming.Leaving(pts) });
                }
            }
            var nodeAt = new Dictionary<int, Vec2>();
            foreach (var n in model.roads.nodes) nodeAt[n.id] = n.at;
            var o = new List<Signpost>();
            foreach (int node in order)
            {
                var here = ends[node];
                if (!nodeAt.TryGetValue(node, out var centre) || here.Count < 2) continue;
                var blades = new List<SignBlade>();
                foreach (var e in here)
                {
                    int street = names.ofRoad[e.road];
                    if (!blades.Exists(b => b.street == street)) blades.Add(new SignBlade { street = street, along = e.dir });
                }
                if (blades.Count < 2) continue;
                var at = CornerSpot(model, layout, centre, here);
                if (at == null) continue;
                bool crowded = false;
                foreach (var s in o) if (Geo.Dist(s.at, at.Value) < SIGN_SPACING) crowded = true;
                if (crowded) continue;
                if (blades.Count > 3) blades.RemoveRange(3, blades.Count - 3);
                o.Add(new Signpost { at = at.Value, blades = blades });
            }
            return o;
        }

        static Vec2? CornerSpot(TownModel model, IsoLayout layout, Vec2 centre, List<End> here)
        {
            double Angle(Vec2 d) => DetMath.Atan2(d.y, d.x);
            var sorted = new List<End>(here);
            Numbers.StableSort(sorted, (a, b) => Angle(a.dir) - Angle(b.dir));
            double width = double.NegativeInfinity;
            foreach (var e in here) width = Math.Max(width, layout.roads[e.road].width);
            double reach = width / 2 + Layout.PARKING + Layout.SIDEWALK * 0.75;
            var wedges = new List<(double from, double gap)>();
            for (int k = 0; k < sorted.Count; k++)
            {
                var e = sorted[k];
                var next = sorted[(k + 1) % sorted.Count];
                double gap = Angle(next.dir) - Angle(e.dir);
                if (gap <= 0) gap += Math.PI * 2;
                wedges.Add((Angle(e.dir), gap));
            }
            Numbers.StableSort(wedges, (a, b) => a.gap - b.gap);
            foreach (var w in wedges)
            {
                double mid = w.from + w.gap / 2;
                double r = w.gap < Math.PI ? Math.Min(3, reach / Math.Sin(w.gap / 2)) : reach;
                var at = new Vec2(centre.x + Math.Cos(mid) * r, centre.y + Math.Sin(mid) * r);
                if (Clear(model, layout, at)) return at;
            }
            return null;
        }

        static bool Clear(TownModel model, IsoLayout layout, Vec2 at)
        {
            int tx = (int)Math.Floor(at.x), ty = (int)Math.Floor(at.y);
            if (!Grid.InBounds(tx, ty, model.width, model.height)) return false;
            int t = Grid.Index(tx, ty, model.width);
            byte ter = model.tiles.terrain[t];
            if (Terrain.IsWater(ter) || layout.use[t] == Use.HOUSE || layout.use[t] == Use.SHED) return false;
            foreach (var r in layout.roads) if (!(Geo.DistToPolyline(at, r.points) > r.width / 2 + 0.08)) return false;
            return true;
        }
    }
}
