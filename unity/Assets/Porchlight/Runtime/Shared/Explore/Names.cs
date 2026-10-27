// Street names for the explore view. Road edges are joined into streets
// where they carry straight on through a junction, and each street gets a name
// from the town's seed: the High Street, Roads and Avenues, side streets after
// trees and flowers, Closes and Courts, Crescents and Drives, Walks; Church,
// Market and Bridge streets where they pass those. A port of render/iso/names.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Street
    {
        public string name;
        public RoadKind kind;
        /// <summary>Road indices (into IsoLayout.roads) making up this street.</summary>
        public List<int> roads;
    }

    public sealed class StreetNames
    {
        public List<Street> streets;
        /// <summary>Street index for each road.</summary>
        public List<int> ofRoad;
    }

    public static class StreetNaming
    {
        const double LOOK = 1.2;
        const double STRAIGHT = -0.77;

        static readonly string[] MAIN_PREFIXES = { "Station", "Mill", "King", "Queen", "Long", "North", "West", "East", "South", "Abbey", "Castle", "Victoria", "Albert", "Park", "Old", "New", "Harbour", "Farm", "Manor", "Chapel" };
        static readonly string[] MAIN_SUFFIXES = { "Road", "Road", "Road", "Avenue", "Street" };
        static readonly string[] STREET_PREFIXES =
        {
            "Elm", "Oak", "Maple", "Birch", "Willow", "Chestnut", "Hazel", "Rowan", "Holly", "Ash", "Linden", "Juniper", "Cedar", "Laurel",
            "Primrose", "Bluebell", "Clover", "Heather", "Foxglove", "Poppy", "Daisy", "Hawthorn", "Alder", "Beech", "Cherry", "Plum", "Pear",
            "Apple", "Orchard", "Meadow", "Brook", "Spring", "Fern", "Ivy", "Lavender", "Rose", "Thistle", "Sycamore", "Poplar", "Larch",
            "Magnolia", "Mulberry", "Quince", "Sorrel", "Tansy", "Yarrow", "Wren", "Robin", "Finch", "Lark", "Swallow", "Heron", "Kestrel",
            "Badger", "Fox", "Otter", "Honey", "Clay", "Flint", "Copper",
        };
        static readonly string[] STREET_SUFFIXES = { "Street", "Street", "Lane", "Lane", "Road", "Way", "Grove", "Avenue", "Row", "Terrace", "Gardens" };
        static readonly string[] DEAD_END_SUFFIXES = { "Close", "Close", "Court", "Mews" };
        static readonly string[] WINDING_SUFFIXES = { "Crescent", "Drive" };
        static readonly string[] PATH_SUFFIXES = { "Walk", "Walk", "Path", "Passage" };

        public static StreetNames Name(TownModel model, IReadOnlyList<RoadStroke> roads)
        {
            var ofRoadGroup = JoinRoads(roads);
            var groups = new Dictionary<int, List<int>>();
            var groupOrder = new List<int>();
            for (int r = 0; r < ofRoadGroup.Count; r++)
            {
                int g = ofRoadGroup[r];
                if (!groups.TryGetValue(g, out var list))
                {
                    groups[g] = list = new List<int>();
                    groupOrder.Add(g);
                }
                list.Add(r);
            }
            var streets = new List<Street>();
            var index = new Dictionary<int, int>();
            foreach (int g in groupOrder)
            {
                index[g] = streets.Count;
                streets.Add(new Street { name = "", kind = roads[groups[g][0]].kind, roads = groups[g] });
            }
            var ofRoad = new List<int>();
            foreach (int g in ofRoadGroup) ofRoad.Add(index[g]);
            GiveNames(model, roads, streets, new Rng($"{model.seed}::streets"));
            return new StreetNames { streets = streets, ofRoad = ofRoad };
        }

        struct End
        {
            public int road;
            public Vec2 dir;
        }

        static List<int> JoinRoads(IReadOnlyList<RoadStroke> roads)
        {
            var parent = new int[roads.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) => parent[i] == i ? i : (parent[i] = Find(parent[i]));
            var atNode = new Dictionary<int, List<End>>();
            var nodeOrder = new List<int>();
            for (int i = 0; i < roads.Count; i++)
            {
                var r = roads[i];
                var rev = new List<Vec2>(r.points);
                rev.Reverse();
                foreach (var (node, pts) in new[] { (r.from, r.points), (r.to, rev) })
                {
                    if (!atNode.TryGetValue(node, out var list))
                    {
                        atNode[node] = list = new List<End>();
                        nodeOrder.Add(node);
                    }
                    list.Add(new End { road = i, dir = Leaving(pts) });
                }
            }
            foreach (int node in nodeOrder)
            {
                var ends = atNode[node];
                var pairs = new List<(int a, int b, double dot)>();
                for (int a = 0; a < ends.Count; a++)
                {
                    for (int b = a + 1; b < ends.Count; b++)
                    {
                        var ra = roads[ends[a].road];
                        var rb = roads[ends[b].road];
                        if (ends[a].road == ends[b].road || ra.kind != rb.kind) continue;
                        double dot = ends[a].dir.x * ends[b].dir.x + ends[a].dir.y * ends[b].dir.y;
                        if (dot < STRAIGHT) pairs.Add((a, b, dot));
                    }
                }
                Js.StableSort(pairs, (p, q) => p.dot - q.dot);
                var used = new HashSet<int>();
                foreach (var p in pairs)
                {
                    if (used.Contains(p.a) || used.Contains(p.b)) continue;
                    used.Add(p.a);
                    used.Add(p.b);
                    parent[Find(ends[p.a].road)] = Find(ends[p.b].road);
                }
            }
            var o = new List<int>(roads.Count);
            for (int i = 0; i < roads.Count; i++) o.Add(Find(i));
            return o;
        }

        /// <summary>Unit direction a polyline leaves its first point in, judged a little way along.</summary>
        public static Vec2 Leaving(IReadOnlyList<Vec2> pts)
        {
            var a = pts[0];
            var b = pts[pts.Count - 1];
            foreach (var p in pts)
            {
                if (Geo.Dist(a, p) >= LOOK)
                {
                    b = p;
                    break;
                }
            }
            double l = Geo.Dist(a, b);
            if (l == 0 || double.IsNaN(l)) l = 1;
            return new Vec2((b.x - a.x) / l, (b.y - a.y) / l);
        }

        static void GiveNames(TownModel model, IReadOnlyList<RoadStroke> roads, List<Street> streets, Rng rng)
        {
            var used = new HashSet<string>();
            var usedSpecial = new HashSet<string>();
            var mainPool = rng.Shuffle(new List<string>(MAIN_PREFIXES));
            var streetPool = rng.Shuffle(new List<string>(STREET_PREFIXES));
            var degree = new Dictionary<int, int>();
            foreach (var r in roads)
                foreach (int n in new[] { r.from, r.to })
                    degree[n] = (degree.TryGetValue(n, out int d) ? d : 0) + 1;
            var bridged = new HashSet<int>();
            foreach (var b in model.bridges) bridged.Add(model.roads.edges.FindIndex(e => e.id == b.roadEdgeId));
            Landmark church = model.landmarks.Find(l => l.kind == LandmarkKind.Church);
            Landmark plaza = model.landmarks.Find(l => l.kind == LandmarkKind.Plaza || l.kind == LandmarkKind.Market);
            bool Near(Street s, Vec2? at, double d)
            {
                if (at == null) return false;
                foreach (int r in s.roads) if (Geo.DistToPolyline(at.Value, roads[r].points) < d) return true;
                return false;
            }
            double Length(Street s)
            {
                double sum = 0;
                foreach (int r in s.roads) sum += PolyLength(roads[r].points);
                return sum;
            }

            var mains = streets.FindAll(s => s.kind == RoadKind.Main);
            var central = mains.FindAll(s => Near(s, model.center, Math.Max(6, model.townRadius * 0.3)));
            Street high = null;
            foreach (var s in central.Count > 0 ? central : mains) if (high == null || Length(s) > Length(high)) high = s;

            bool Claim(Street s, string name)
            {
                if (used.Contains(name)) return false;
                used.Add(name);
                s.name = name;
                return true;
            }
            var order = new List<Street>(streets);
            Js.StableSort(order, (a, b) => Length(b) - Length(a));
            foreach (var s in order)
            {
                if (s == high && Claim(s, "High Street")) continue;
                var kind = s.kind;
                bool deadEnd = false;
                foreach (int r in s.roads) if (degree[roads[r].from] == 1 || degree[roads[r].to] == 1) deadEnd = true;
                deadEnd = deadEnd && Length(s) < 14;
                var suffixes = kind == RoadKind.Path ? PATH_SUFFIXES : kind == RoadKind.Main ? MAIN_SUFFIXES : deadEnd ? DEAD_END_SUFFIXES : Winding(s, roads) ? WINDING_SUFFIXES : STREET_SUFFIXES;
                string suffix = rng.Pick(suffixes);
                var special = new List<string>();
                if (Near(s, church?.at, 4)) special.Add("Church");
                if (Near(s, plaza?.at, 5)) special.Add("Market");
                foreach (int r in s.roads) if (bridged.Contains(r)) { special.Add("Bridge"); break; }
                string landmark = special.Find(p => !usedSpecial.Contains(p));
                if (landmark != null && Claim(s, $"{landmark} {suffix}"))
                {
                    usedSpecial.Add(landmark);
                    continue;
                }
                var pool = kind == RoadKind.Main ? mainPool : streetPool;
                bool named = false;
                while (pool.Count > 0 && !named)
                {
                    string first = pool[0];
                    pool.RemoveAt(0);
                    named = Claim(s, $"{first} {suffix}");
                }
                string[] endings = { "field", "wood", "ley", "brook", "hill", "mead" };
                for (int k = 0; !named; k++)
                {
                    string b = rng.Pick(STREET_PREFIXES);
                    named = Claim(s, $"{b}{endings[k % 6]} {suffix}");
                }
            }
        }

        static bool Winding(Street s, IReadOnlyList<RoadStroke> roads)
        {
            double turn = 0;
            foreach (int r in s.roads)
            {
                var pts = roads[r].points;
                double own = 0;
                for (int k = 1; k < pts.Count - 1; k++)
                {
                    double a = JsMath.Atan2(pts[k].y - pts[k - 1].y, pts[k].x - pts[k - 1].x);
                    double b = JsMath.Atan2(pts[k + 1].y - pts[k].y, pts[k + 1].x - pts[k].x);
                    double d = b - a;
                    while (d > Math.PI) d -= Math.PI * 2;
                    while (d < -Math.PI) d += Math.PI * 2;
                    own += d;
                }
                turn += Math.Abs(own);
            }
            return Math.Abs(turn) > 1.75;
        }

        static double PolyLength(List<Vec2> pts)
        {
            double s = 0;
            for (int k = 1; k < pts.Count; k++) s += Geo.Dist(pts[k - 1], pts[k]);
            return s;
        }
    }
}
