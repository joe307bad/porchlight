// Where the town's shops and services go (docs/city-research.md §2,
// docs/town-shops.md), once the buildings stand. Each is an existing building
// turned to its use, so nothing moves and every invariant of the buildings
// still holds:
//
// - The fire station first: the building on a main road or collector nearest
//   the road node from which the furthest of the town's road nodes is nearest
//   by road (ISO's rating gives full credit within 1.5 road miles; NFPA 1710 a
//   240 s drive). One covers a town our size.
// - A doctor's office (a clinic) on a main road or collector just off the
//   middle of town.
// - Supermarkets by threshold (Gibbs, for CNU): one from about 6,000
//   households, and one in any town of SUPERMARKET_TOWN households or more for
//   the game's sake. A big building on a main road, away from the core, where
//   the Huff model says it takes the most custom.
// - The petrol station at the busiest junction of main roads outside the
//   centre: the one most of the shortest drives between the gates and the
//   centres go through.
// - Corner stores: one for about every 1,000 households, each by a
//   neighbourhood's entrance (a collector meeting a main road), where the Huff
//   model (P(shop j) ∝ size_j / distance², λ = 2) says it draws the most custom
//   the high street, the supermarkets and the stores already placed don't.
//
// Pure: the same buildings give the same shops.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public static class Retail
    {
        /// <summary>Households for a supermarket (Gibbs's neighbourhood centre), and the smallest town given one anyway.</summary>
        public const int SUPERMARKET_HOUSEHOLDS = 6000, SUPERMARKET_TOWN = 300;
        /// <summary>Households for a corner store (Gibbs), and the smallest town given one.</summary>
        public const int CORNER_HOUSEHOLDS = 1000, CORNER_TOWN = 150;
        /// <summary>The smallest town (households) with a petrol station, and with a fire station of its own (a volunteer one).</summary>
        public const int PETROL_TOWN = 150, FIRE_TOWN = 100;
        /// <summary>The Huff model's distance decay, and the nearest (tiles) a shop counts as, so one next door doesn't take everything.</summary>
        public const double HUFF_LAMBDA = 2, HUFF_NEAREST = 4;
        /// <summary>The Huff model's sizes: a corner store 1, a high-street shop 1 each, a supermarket this.</summary>
        public const double SUPERMARKET_SIZE = 12;
        /// <summary>A supermarket's building has at least this many tiles (fewer if the town has none so big), the fire station this.</summary>
        const int SUPERMARKET_TILES = 12, FIRE_TILES = 6, CLINIC_TILES = 4;
        /// <summary>A corner store or the petrol station is no further than this (tiles, to its middle) from its junction.</summary>
        public const double CORNER_REACH = 10, PETROL_REACH = 12;
        /// <summary>Road nodes the fire station's site is looked for among (those nearest the town's buildings' middle).</summary>
        const int FIRE_CANDIDATES = 24;

        static bool Civic(BuildingKind k) => k != BuildingKind.House && k != BuildingKind.Shop && k != BuildingKind.Workshop;

        /// <summary>Households a building holds, as zoning will count them (a house is one; flats over a shop off the centre).</summary>
        public static int Households(TownModel m, Building b)
        {
            int area = b.tiles.Count * 16 * Math.Max(1, b.floors);
            var t = m.blocks[m.lots[b.lotId].blockId].transect;
            switch (b.kind)
            {
                case BuildingKind.House: return area <= 320 ? 1 : Math.Max(1, area / 100);
                case BuildingKind.Shop: return t >= Transect.T5 ? 0 : Math.Max(1, (area - b.tiles.Count * 16) / 100);
                default: return 0;
            }
        }

        /// <summary>The road edges each building's lot fronts.</summary>
        static List<int>[] Fronts(TownModel m, List<Building> list, Ground g)
        {
            var o = new List<int>[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                o[i] = new List<int>();
                foreach (var kv in Parcels.AdjacentRoadEdges(Parcels.TilesIn(m.lots[list[i].lotId].polygon, null, m.width, m.height), g, m.width, m.height))
                    if (kv.Key >= 0 && kv.Key < m.roads.edges.Count) o[i].Add(kv.Key);
            }
            return o;
        }

        public static TownServices Place(TownModel m, List<Building> list, Ground g) => Stepwise.Drain<TownServices>(done => PlaceSteps(m, list, g, done));

        /// <summary>The shops and services, a share at a time (Place, all at once).</summary>
        public static IEnumerable<string> PlaceSteps(TownModel m, List<Building> list, Ground g, Action<TownServices> done)
        {
            var s = new TownServices();
            if (list.Count == 0)
            {
                done(s);
                yield break;
            }
            var C = m.downtown;
            double R = Math.Max(1, m.townRadius);
            var fronts = Fronts(m, list, g);
            yield return "/fronts";
            var mid = new Vec2[list.Count];
            var zone = new Transect[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                mid[i] = Geo.PolygonCentroid(list[i].footprint);
                zone[i] = m.blocks[m.lots[list[i].lotId].blockId].transect;
                s.households += Households(m, list[i]);
            }
            bool On(int i, RoadClass cls) => fronts[i].Exists(e => m.roads.edges[e].cls == cls);
            bool Main(int i) => On(i, RoadClass.Arterial) || On(i, RoadClass.Collector);
            bool Free(int i) => !Civic(list[i].kind);
            void Make(int i, BuildingKind kind, int floors)
            {
                list[i].kind = kind;
                list[i].floors = floors;
            }

            // The high street: shops on the main roads, and the centre's shops.
            var shopsAt = new List<(Vec2 at, double size)>();
            for (int i = 0; i < list.Count; i++)
                if (list[i].kind == BuildingKind.Shop)
                {
                    if (zone[i] < Transect.T5 && On(i, RoadClass.Arterial)) s.highStreet.Add(list[i].id);
                    shopsAt.Add((mid[i], 1));
                }

            // --- The fire station ---
            var graph = new Roads(m);
            int fireNode = s.households >= FIRE_TOWN ? graph.MostCentral(list, FIRE_CANDIDATES) : -1;
            if (fireNode >= 0)
            {
                int best = Nearest(list, mid, m.roads.nodes[fireNode].at, i => Free(i) && Main(i) && list[i].tiles.Count >= FIRE_TILES && zone[i] < Transect.T6, double.PositiveInfinity);
                if (best >= 0)
                {
                    Make(best, BuildingKind.FireStation, 2);
                    s.fireStation = list[best].id;
                }
            }

            yield return "/fire";
            // --- The clinic: just off the middle, on a main road ---
            {
                int best = -1;
                double bestD = double.PositiveInfinity;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!Free(i) || !Main(i) || list[i].tiles.Count < CLINIC_TILES || zone[i] == Transect.T6) continue;
                    double d = Math.Abs(Geo.Dist(mid[i], C) - 0.2 * R);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                if (best >= 0 && s.households >= CORNER_TOWN)
                {
                    Make(best, BuildingKind.Clinic, 2);
                    s.clinic = list[best].id;
                }
            }

            // Who shops: each home's households, where it is.
            var homes = new List<(Vec2 at, double w)>();
            for (int i = 0; i < list.Count; i++)
            {
                int hh = Households(m, list[i]);
                if (hh > 0) homes.Add((mid[i], hh));
            }

            // --- Supermarkets ---
            int supermarkets = s.households >= SUPERMARKET_HOUSEHOLDS ? s.households / SUPERMARKET_HOUSEHOLDS : s.households >= SUPERMARKET_TOWN ? 1 : 0;
            int bigTiles = SUPERMARKET_TILES;
            {
                int biggest = 0;
                for (int i = 0; i < list.Count; i++) if (Free(i) && Main(i)) biggest = Math.Max(biggest, list[i].tiles.Count);
                bigTiles = Math.Min(bigTiles, Math.Max(4, biggest));
            }
            for (int k = 0; k < supermarkets; k++)
            {
                int best = BestByHuff(homes, shopsAt, list.Count, i =>
                {
                    if (!Free(i) || !On(i, RoadClass.Arterial) && !On(i, RoadClass.Collector) || list[i].tiles.Count < bigTiles || zone[i] == Transect.T6) return false;
                    double d = Geo.Dist(mid[i], C) / R;
                    return d >= 0.15 && d <= 0.85;
                }, mid, SUPERMARKET_SIZE, i => 1 + LandStage.At(m, mid[i]));
                if (best < 0) break;
                yield return "/supermarket";
                Make(best, BuildingKind.Supermarket, 1);
                s.supermarkets.Add(list[best].id);
                shopsAt.Add((mid[best], SUPERMARKET_SIZE));
            }

            yield return "/petrol";
            // --- The petrol station ---
            foreach (int node in s.households >= PETROL_TOWN ? graph.Busiest(m) : new List<int>())
            {
                int best = Nearest(list, mid, m.roads.nodes[node].at, i => Free(i) && On(i, RoadClass.Arterial) && zone[i] < Transect.T5, PETROL_REACH);
                if (best < 0) continue;
                Make(best, BuildingKind.PetrolStation, 1);
                s.petrolStation = list[best].id;
                break;
            }

            // --- Corner stores ---
            // (Planned households run under what zoning makes, which turns the outer towers into flats, by a third in a
            // big town: the next store from halfway to it.)
            int stores = s.households >= CORNER_TOWN ? Math.Max(1, (int)Math.Floor(s.households / (double)CORNER_HOUSEHOLDS + 0.5)) : 0;
            var entrances = new List<Vec2>();
            foreach (int n in LandStage.Corners(m)) entrances.Add(m.roads.nodes[n].at);
            var atEntrance = new bool[list.Count];
            for (int i = 0; i < list.Count; i++)
                foreach (var at in entrances)
                    if (Geo.Dist(mid[i], at) <= CORNER_REACH) { atEntrance[i] = true; break; }
            for (int k = 0; k < stores; k++)
            {
                int best = BestByHuff(homes, shopsAt, list.Count, i => atEntrance[i] && Main(i) && zone[i] < Transect.T5 && !Regions.HousesOnly(m, mid[i]) && (list[i].kind == BuildingKind.House || list[i].kind == BuildingKind.Shop && !s.highStreet.Contains(list[i].id)),
                    mid, 1, i => 1 + 0.5 * LandStage.At(m, mid[i]));
                if (best < 0) break;
                yield return "/store";
                Make(best, BuildingKind.Shop, 2);
                s.cornerStores.Add(list[best].id);
                shopsAt.Add((mid[best], 1));
            }
            done(s);
        }

        /// <summary>The building passing `ok` nearest `at` (within `reach`), or -1.</summary>
        static int Nearest(List<Building> list, Vec2[] mid, Vec2 at, Func<int, bool> ok, double reach)
        {
            int best = -1;
            double bestD = reach;
            for (int i = 0; i < list.Count; i++)
            {
                if (!ok(i)) continue;
                double d = Geo.Dist(mid[i], at);
                if (d < bestD || (best < 0 && d <= bestD))
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>A shop's pull on a home (Huff's attractiveness over distance^λ).</summary>
        public static double Pull(double size, double d) => size / Math.Pow(Math.Max(HUFF_NEAREST, d), HUFF_LAMBDA);

        /// <summary>
        /// The building passing `ok` where a new shop of `size` would take the most custom from the homes (by the
        /// Huff model, against the shops there are), times `bonus`; -1 if none passes.
        /// </summary>
        static int BestByHuff(List<(Vec2 at, double w)> homes, List<(Vec2 at, double size)> shops, int count, Func<int, bool> ok, Vec2[] mid, double size, Func<int, double> bonus)
        {
            // Each home's pull from the shops already there.
            var pulled = new double[homes.Count];
            for (int h = 0; h < homes.Count; h++)
                foreach (var s in shops) pulled[h] += Pull(s.size, Geo.Dist(homes[h].at, s.at));
            int best = -1;
            double bestGain = 0;
            for (int i = 0; i < count; i++)
            {
                if (!ok(i)) continue;
                double gain = 0;
                for (int h = 0; h < homes.Count; h++)
                {
                    double p = Pull(size, Geo.Dist(homes[h].at, mid[i]));
                    gain += homes[h].w * p / (pulled[h] + p);
                }
                gain *= bonus(i);
                if (gain > bestGain + 1e-9)
                {
                    bestGain = gain;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>The town's roads as a graph to drive: shortest ways by length, main roads preferred.</summary>
        public sealed class Roads
        {
            readonly TownModel m;
            readonly List<(int to, double cost)>[] adj;

            public Roads(TownModel m)
            {
                this.m = m;
                adj = new List<(int, double)>[m.roads.nodes.Count];
                for (int i = 0; i < adj.Length; i++) adj[i] = new List<(int, double)>();
                foreach (var e in m.roads.edges)
                {
                    if (e.kind == RoadKind.Path || e.from < 0 || e.to < 0 || e.from >= adj.Length || e.to >= adj.Length) continue;
                    double len = 0;
                    for (int k = 1; k < e.points.Count; k++) len += Geo.Dist(e.points[k - 1], e.points[k]);
                    adj[e.from].Add((e.to, len));
                    adj[e.to].Add((e.from, len));
                }
            }

            /// <summary>Road distance (tiles) from `from` to every node; and each node's predecessor on the way.</summary>
            public double[] From(int from, out int[] prev, Func<int, int, double, double> cost = null)
            {
                int n = adj.Length;
                var d = new double[n];
                prev = new int[n];
                for (int i = 0; i < n; i++)
                {
                    d[i] = double.PositiveInfinity;
                    prev[i] = -1;
                }
                if (from < 0 || from >= n) return d;
                d[from] = 0;
                var open = new SortedSet<(double, int)> { (0, from) };
                while (open.Count > 0)
                {
                    var (du, u) = open.Min;
                    open.Remove(open.Min);
                    if (du > d[u]) continue;
                    foreach (var (v, len) in adj[u])
                    {
                        double nd = du + (cost == null ? len : cost(u, v, len));
                        if (nd < d[v] - 1e-9)
                        {
                            if (!double.IsPositiveInfinity(d[v])) open.Remove((d[v], v));
                            d[v] = nd;
                            prev[v] = u;
                            open.Add((nd, v));
                        }
                    }
                }
                return d;
            }

            /// <summary>The node on a main road or collector, near the buildings' middle, whose furthest node with a building near is nearest by road.</summary>
            public int MostCentral(List<Building> list, int candidates)
            {
                var classes = new Dictionary<int, RoadClass>();
                foreach (var e in m.roads.edges)
                    foreach (int n in new[] { e.from, e.to })
                        if (!classes.TryGetValue(n, out var c) || e.cls > c) classes[n] = e.cls;
                double x = 0, y = 0;
                foreach (var b in list)
                {
                    var c = Geo.PolygonCentroid(b.footprint);
                    x += c.x;
                    y += c.y;
                }
                var middle = new Vec2(x / list.Count, y / list.Count);
                // The nodes that serve the buildings: each building's nearest.
                var served = new HashSet<int>();
                foreach (var b in list) served.Add(RoadGraphOps.NearestNode(m.roads, Geo.PolygonCentroid(b.footprint)));
                var pool = new List<int>();
                foreach (var kv in classes) if (kv.Value != RoadClass.Local && adj[kv.Key].Count > 0) pool.Add(kv.Key);
                pool.Sort((a, b) =>
                {
                    int c = Geo.Dist(m.roads.nodes[a].at, middle).CompareTo(Geo.Dist(m.roads.nodes[b].at, middle));
                    return c != 0 ? c : a.CompareTo(b);
                });
                int best = -1;
                double bestFar = double.PositiveInfinity;
                for (int k = 0; k < Math.Min(candidates, pool.Count); k++)
                {
                    var d = From(pool[k], out _);
                    double far = 0;
                    foreach (int s in served) if (!double.IsPositiveInfinity(d[s])) far = Math.Max(far, d[s]);
                    if (far < bestFar - 1e-9)
                    {
                        bestFar = far;
                        best = pool[k];
                    }
                }
                return best;
            }

            /// <summary>
            /// Junctions of main roads outside the centre (two or more arterial legs, in T3 or T4), busiest first: by
            /// how many of the shortest drives between the gates and the centres pass through (main roads preferred,
            /// as drivers prefer them).
            /// </summary>
            public List<int> Busiest(TownModel model)
            {
                var ends = new List<int>();
                foreach (var gate in model.gates) ends.Add(RoadGraphOps.NearestNode(model.roads, gate));
                ends.Add(RoadGraphOps.NearestNode(model.roads, model.downtown));
                if (model.secondCentre != null) ends.Add(RoadGraphOps.NearestNode(model.roads, model.secondCentre.Value));
                var cls = new Dictionary<long, RoadClass>();
                var arterialLegs = new int[adj.Length];
                foreach (var e in model.roads.edges)
                {
                    cls[Key(e.from, e.to)] = e.cls;
                    if (e.cls == RoadClass.Arterial)
                    {
                        if (e.from >= 0 && e.from < adj.Length) arterialLegs[e.from]++;
                        if (e.to >= 0 && e.to < adj.Length) arterialLegs[e.to]++;
                    }
                }
                double Cost(int u, int v, double len) =>
                    len * (cls.TryGetValue(Key(u, v), out var c) ? c == RoadClass.Arterial ? 1 : c == RoadClass.Collector ? 1.3 : 2 : 2);
                var passes = new int[adj.Length];
                for (int a = 0; a < ends.Count; a++)
                {
                    From(ends[a], out var prev, Cost);
                    for (int b = 0; b < ends.Count; b++)
                    {
                        if (b == a || ends[b] == ends[a]) continue;
                        for (int v = prev[ends[b]]; v >= 0 && v != ends[a]; v = prev[v]) passes[v]++;
                    }
                }
                var o = new List<int>();
                for (int n = 0; n < adj.Length; n++)
                    if (arterialLegs[n] >= 2 && adj[n].Count >= 3 && passes[n] > 0 && TownPlan.TransectAt(model, model.roads.nodes[n].at) < Transect.T5) o.Add(n);
                o.Sort((a, b) =>
                {
                    int c = passes[b].CompareTo(passes[a]);
                    if (c != 0) return c;
                    c = Geo.Dist(model.roads.nodes[a].at, model.downtown).CompareTo(Geo.Dist(model.roads.nodes[b].at, model.downtown));
                    return c != 0 ? c : a.CompareTo(b);
                });
                return o;
            }

            static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }
    }
}
