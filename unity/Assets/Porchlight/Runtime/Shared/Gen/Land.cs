// Stage 7a½: the land (docs/city-research.md §2, docs/town-shops.md). Between
// the lots and the buildings, what the land is worth and what it's kept for:
//
// - The industrial wedge. Industry goes where transport is cheapest (bid-rent;
//   Hoyt's sectors), so it gets a wedge out along the main road to one gate,
//   where the county highway leaves, instead of being scattered round the edge.
//   The gate is the one furthest from the town's second centre (its offices
//   and airport frontage) with the most dry land in the wedge.
// - A land-value field over every tile: centrality (either centre), main-road
//   frontage, collector frontage, the corners where main roads meet, and
//   nearness to a park, less the industrial wedge. The buildings stage chooses
//   each building's kind by it, instead of by distance from the middle alone
//   (Vanegas et al.'s link between behaviour and geometry, without UrbanSim).
// - Park land. Each neighbourhood already has a park; once the buildings
//   stand, more blocks are kept green until the town has PARK_ACRES a 1,000
//   residents (MoreParks: below NRPA's 10 acres, which counts the
//   countryside's parks, and our towns stop at their own edge).
// - Car parks behind the high street: lots in the centre's blocks that back
//   onto a main road but front a side street, one for about every dozen lots on
//   the main roads there, kept apart.
//
// Pure: the same town gives the same land.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class LandStage : IStage
    {
        public string Name => "land";

        /// <summary>A town at least this big (radius, tiles: a big town, as for the ring road) has an industrial wedge; smaller, its workshops stay at the edge.</summary>
        public const double WEDGE_TOWN = TownPlan.BIG_TOWN;
        /// <summary>The wedge's half angle (radians: 25°) and where it starts (a share of the radius).</summary>
        public const double WEDGE_HALF = 25 * Math.PI / 180, WEDGE_FROM = 0.55;
        /// <summary>Park land kept in town: acres a thousand residents (an acre is about 253 tiles of 4 m).</summary>
        public const double PARK_ACRES = 4, ACRE_TILES = 4046.86 / 16;
        /// <summary>People a household, as zoning counts them.</summary>
        public const double PEOPLE_A_HOME = 2.5;
        /// <summary>A car park has at least this many tiles (a dozen bays), and at most this many.</summary>
        public const int CAR_PARK_MIN = 12, CAR_PARK_MAX = 60;
        /// <summary>The fewest bays a car park has, in a town of small lots.</summary>
        public const int CAR_PARK_SMALLEST = 6;
        /// <summary>A car park for about every this many lots on the centre's main roads, at most CAR_PARKS, this far apart (tiles).</summary>
        const int LOTS_A_CAR_PARK = 12, CAR_PARKS = 4;
        const double CAR_PARK_APART = 24;
        /// <summary>The land-value field's reaches (tiles): main-road and collector frontage, corners, parks.</summary>
        const double ARTERIAL_REACH = 6, COLLECTOR_REACH = 4, CORNER_REACH = 8, PARK_REACH = 12;

        public TownModel Run(TownModel model, Rng rng)
        {
            var next = model.With();
            var g = Parcels.Ground(model);
            next.industry = Wedge(model, g.water);
            next.parkBlocks = new List<int>();
            foreach (var hood in model.neighbourhoods) if (hood.parkBlockId >= 0) next.parkBlocks.Add(hood.parkBlockId);
            next.landValue = Value(model, g, next.industry, next.ParkBlockSet());
            next.carParks = CarParks(model, g, next.ParkBlockSet());
            return next;
        }

        // --- The industrial wedge ----------------------------------------------------------------

        public static IndustryWedge Wedge(TownModel m, byte[] water)
        {
            if (m.townRadius < WEDGE_TOWN || m.gates.Count == 0) return null;
            int w = m.width, h = m.height;
            var C = m.center;
            double R = m.townRadius;
            // Away from the offices: the second centre, or failing that the airport.
            Vec2? avoid = m.secondCentre;
            if (avoid == null && m.airport != null) avoid = new Vec2((m.airport.x0 + m.airport.x1) / 2.0, (m.airport.y0 + m.airport.y1) / 2.0);
            IndustryWedge best = null;
            double bestScore = double.NegativeInfinity;
            for (int k = 0; k < m.gates.Count; k++)
            {
                var gate = m.gates[k];
                var wedge = new IndustryWedge { gate = k, bearing = Math.Atan2(gate.y - C.y, gate.x - C.x), halfAngle = WEDGE_HALF, from = WEDGE_FROM };
                int dry = 0, all = 0;
                for (int y = 0; y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                    {
                        var at = new Vec2(x + 0.5, y + 0.5);
                        if (Geo.Dist(at, C) > R || !wedge.Contains(C, R, at)) continue;
                        all++;
                        if (water[Grid.Index(x, y, w)] == 0 && !InAirportOrZoo(m, x, y)) dry++;
                    }
                double score = all == 0 ? 0 : (double)dry / all;
                if (avoid != null)
                {
                    double a = Math.Abs(Angle(wedge.bearing - Math.Atan2(avoid.Value.y - C.y, avoid.Value.x - C.x)));
                    score += 0.5 * a / Math.PI;
                }
                if (score > bestScore + 1e-9)
                {
                    bestScore = score;
                    best = wedge;
                }
            }
            return best;
        }

        static bool InAirportOrZoo(TownModel m, int x, int y) =>
            (m.airport != null && x >= m.airport.x0 && x < m.airport.x1 && y >= m.airport.y0 && y < m.airport.y1)
            || (m.zoo != null && x >= m.zoo.x0 && x < m.zoo.x1 && y >= m.zoo.y0 && y < m.zoo.y1);

        static double Angle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        // --- Land value --------------------------------------------------------------------------

        /// <summary>
        /// The road nodes where main roads meet: an arterial running on through another arterial (three or more
        /// arterial legs), or an arterial meeting a collector.
        /// </summary>
        public static List<int> Corners(TownModel m, bool collectorsToo = true)
        {
            var legs = new Dictionary<int, (int art, int col, int all)>();
            foreach (var e in m.roads.edges)
                foreach (int n in new[] { e.from, e.to })
                {
                    legs.TryGetValue(n, out var c);
                    legs[n] = (c.art + (e.cls == RoadClass.Arterial ? 1 : 0), c.col + (e.cls == RoadClass.Collector ? 1 : 0), c.all + 1);
                }
            var o = new List<int>();
            foreach (var n in m.roads.nodes)
            {
                if (!legs.TryGetValue(n.id, out var c) || c.all < 3) continue;
                if (c.art >= 3 || (collectorsToo && c.art >= 1 && c.col >= 1)) o.Add(n.id);
            }
            return o;
        }

        public static float[] Value(TownModel m, Ground g, IndustryWedge wedge, HashSet<int> parks)
        {
            int w = m.width, h = m.height, n = w * h;
            var C = m.center;
            double R = Math.Max(1, m.townRadius);
            var arterial = new byte[n];
            var collector = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (!Parcels.IsStreetTile(g, i)) continue;
                int e = g.road.edgeId[i];
                if (e < 0 || e >= m.roads.edges.Count) continue;
                if (m.roads.edges[e].cls == RoadClass.Arterial) arterial[i] = 1;
                else if (m.roads.edges[e].cls == RoadClass.Collector) collector[i] = 1;
            }
            var corner = new byte[n];
            foreach (int id in Corners(m))
            {
                var at = m.roads.nodes[id].at;
                int x = (int)Math.Floor(at.x), y = (int)Math.Floor(at.y);
                if (Grid.InBounds(x, y, w, h)) corner[Grid.Index(x, y, w)] = 1;
            }
            var park = new byte[n];
            foreach (int b in parks)
                foreach (int t in Parcels.TilesIn(m.blocks[b].polygon, m.blocks[b].holes, w, h)) park[t] = 1;
            var dArt = Raster.DistanceFrom(arterial, w, h);
            var dCol = Raster.DistanceFrom(collector, w, h);
            var dCorner = Raster.DistanceFrom(corner, w, h);
            var dPark = Raster.DistanceFrom(park, w, h);
            double secondReach = R * TownPlan.SECOND_REACH;
            var o = new float[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    var at = new Vec2(x + 0.5, y + 0.5);
                    double central = Math.Max(0, 1 - Geo.Dist(at, C) / R);
                    if (m.secondCentre != null) central = Math.Max(central, 0.8 * Math.Max(0, 1 - Geo.Dist(at, m.secondCentre.Value) / secondReach));
                    double v = 0.05 + 0.45 * central
                        + 0.2 * Near(dArt[i], ARTERIAL_REACH)
                        + 0.1 * Near(dCol[i], COLLECTOR_REACH)
                        + 0.1 * Near(dCorner[i], CORNER_REACH)
                        + 0.1 * Near(dPark[i], PARK_REACH);
                    if (wedge != null && wedge.Contains(C, R, at)) v -= 0.3;
                    o[i] = (float)Math.Max(0, Math.Min(1, v));
                }
            return o;
        }

        static double Near(float d, double reach) => float.IsPositiveInfinity(d) ? 0 : Math.Max(0, 1 - d / reach);

        /// <summary>The land value at a point (0 off the map, or with no field).</summary>
        public static double At(TownModel m, Vec2 p)
        {
            if (m.landValue == null) return 0;
            int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
            return Grid.InBounds(x, y, m.width, m.height) ? m.landValue[Grid.Index(x, y, m.width)] : 0;
        }

        // --- Park land ---------------------------------------------------------------------------

        /// <summary>
        /// The blocks to keep green as well as the neighbourhoods' parks, until the town has PARK_ACRES for every 1,000
        /// of its residents: the blocks furthest from any park first, each of a park's size, outside the centre and the
        /// industrial wedge, never the plaza's or the church's. Called by the buildings stage once its buildings stand,
        /// so the residents are counted from them (`households`, a building's), less those on the blocks kept green.
        /// </summary>
        public static List<int> MoreParks(TownModel m, List<Building> list, Func<Building, int> households, HashSet<int> keep)
        {
            int w = m.width, h = m.height;
            var more = new List<int>();
            var set = m.ParkBlockSet();
            // Only a town with neighbourhoods big enough to have parks of their own gets more.
            if (set.Count == 0) return more;
            var tiles = new int[m.blocks.Count];
            var mids = new List<Vec2>();
            for (int k = 0; k < m.blocks.Count; k++)
            {
                var t = Parcels.TilesIn(m.blocks[k].polygon, m.blocks[k].holes, w, h);
                tiles[k] = t.Count;
                mids.Add(PlanStage.Middle(t, w, m.blocks[k]));
            }
            var homesIn = new double[m.blocks.Count];
            double residents = 0;
            foreach (var b in list)
            {
                double r = households(b) * PEOPLE_A_HOME;
                homesIn[m.lots[b.lotId].blockId] += r;
                residents += r;
            }
            double parkTiles = 0;
            foreach (int k in set) parkTiles += tiles[k];
            for (int round = 0; round < m.blocks.Count; round++)
            {
                if (parkTiles >= PARK_ACRES * residents / 1000 * ACRE_TILES) break;
                int best = -1;
                double bestD = 0;
                for (int k = 0; k < m.blocks.Count; k++)
                {
                    var b = m.blocks[k];
                    if (set.Contains(k) || keep.Contains(k) || b.neighbourhood < 0 || homesIn[k] <= 0) continue;
                    if (tiles[k] < PlanStage.PARK_MIN_TILES || tiles[k] > PlanStage.PARK_MAX_SHARE * Math.Max(1, m.neighbourhoods[b.neighbourhood].area)) continue;
                    if (b.transect >= Transect.T5 || (m.industry != null && m.industry.Contains(m.center, m.townRadius, mids[k]))) continue;
                    double d = double.PositiveInfinity;
                    foreach (int p in set) d = Math.Min(d, Geo.Dist(mids[k], mids[p]));
                    if (d > bestD + 1e-9)
                    {
                        bestD = d;
                        best = k;
                    }
                }
                if (best < 0) break;
                set.Add(best);
                more.Add(best);
                parkTiles += tiles[best];
                residents -= homesIn[best];
            }
            return more;
        }

        // --- Car parks ---------------------------------------------------------------------------

        static List<CarPark> CarParks(TownModel m, Ground g, HashSet<int> parks)
        {
            int w = m.width, h = m.height;
            var o = new List<CarPark>();
            var plaza = new HashSet<int>();
            foreach (var l in m.landmarks) if (l.kind == LandmarkKind.Plaza) foreach (int id in l.lotIds) plaza.Add(id);
            bool Arterial(int e) => e >= 0 && e < m.roads.edges.Count && m.roads.edges[e].cls == RoadClass.Arterial;
            // The centre's blocks with lots on a main road, and those lots.
            var frontsArterial = new Dictionary<int, bool>();
            var lotTiles = new Dictionary<int, List<int>>();
            var mainBlocks = new HashSet<int>();
            int onMain = 0;
            foreach (var lot in m.lots)
            {
                if (m.blocks[lot.blockId].transect < Transect.T5 || parks.Contains(lot.blockId)) continue;
                var t = Parcels.TilesIn(lot.polygon, null, w, h);
                lotTiles[lot.id] = t;
                bool art = false;
                foreach (var kv in Parcels.AdjacentRoadEdges(t, g, w, h)) if (Arterial(kv.Key)) art = true;
                frontsArterial[lot.id] = art;
                if (art)
                {
                    mainBlocks.Add(lot.blockId);
                    onMain++;
                }
            }
            if (onMain == 0) return o;
            int count = Math.Min(CAR_PARKS, Math.Max(1, (int)Math.Round(onMain / (double)LOTS_A_CAR_PARK)));
            // A town of small lots has smaller car parks: no fewer bays than most of the centre's lots have tiles.
            int least = Math.Max(CAR_PARK_SMALLEST, Math.Min(CAR_PARK_MIN, (int)(m.@params.lotMaxSize * TownPlan.Rules(Transect.T5).lotScale * 0.9)));
            // Behind the high street: in one of those blocks, on a side street, not the main road.
            var candidates = new List<(Lot lot, int edge, Vec2 mid)>();
            foreach (var lot in m.lots)
            {
                if (!lotTiles.TryGetValue(lot.id, out var t) || frontsArterial[lot.id] || plaza.Contains(lot.id) || !mainBlocks.Contains(lot.blockId)) continue;
                if (t.Count < least || t.Count > CAR_PARK_MAX) continue;
                int edge = -1, most = 0;
                foreach (var kv in Parcels.AdjacentRoadEdges(t, g, w, h))
                    if (kv.Key >= 0 && kv.Key < m.roads.edges.Count && m.roads.edges[kv.Key].kind != RoadKind.Path && kv.Value > most)
                    {
                        most = kv.Value;
                        edge = kv.Key;
                    }
                if (edge < 0) continue;
                candidates.Add((lot, edge, Geo.PolygonCentroid(lot.polygon)));
            }
            // Nearest the middle first, each far enough from the last.
            candidates.Sort((a, b) =>
            {
                int c = Geo.Dist(a.mid, m.center).CompareTo(Geo.Dist(b.mid, m.center));
                return c != 0 ? c : a.lot.id.CompareTo(b.lot.id);
            });
            foreach (var c in candidates)
            {
                if (o.Count >= count) break;
                bool apart = true;
                foreach (var p in o) if (Geo.Dist(Geo.PolygonCentroid(m.lots[p.lotId].polygon), c.mid) < CAR_PARK_APART) apart = false;
                if (!apart) continue;
                o.Add(new CarPark { id = o.Count, lotId = c.lot.id, edgeId = c.edge });
            }
            return o;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            if (model.landValue == null || model.landValue.Length != model.width * model.height) errors.Add("no land-value field");
            else foreach (float v in model.landValue) if (!(v >= 0 && v <= 1)) { errors.Add($"a land value of {v}"); break; }
            var plaza = new HashSet<int>();
            foreach (var l in model.landmarks) if (l.kind == LandmarkKind.Plaza) foreach (int id in l.lotIds) plaza.Add(id);
            var parks = model.ParkBlockSet();
            foreach (var c in model.carParks)
            {
                var lot = model.lots[c.lotId];
                if (plaza.Contains(lot.id)) errors.Add($"car park {c.id} is on the plaza");
                if (parks.Contains(lot.blockId)) errors.Add($"car park {c.id} is in a park");
            }
            foreach (int b in model.parkBlocks)
                foreach (var lot in model.lots)
                    if (lot.blockId == b && plaza.Contains(lot.id)) errors.Add($"park block {b} has the plaza in it");
            return errors;
        }
    }
}
