// Is the town planned like a real one? (docs/city-research.md, section 1)
//
//   dotnet run -c Release --project "unity/Tools~/Checks" -- plan [key ...]
//
// For the county's towns, a few big towns and a few small ones (or the keys given):
//
// - Road hierarchy (FHWA): every road classed; the arterials reach every gate from the centre; in the
//   big towns the length shares are FHWA's for an urban area: about 70% local.
// - Ring: a big town has one, and a trip from one gate to the next can go round the middle on
//   arterials without coming within half the ring's radius of the centre (and no longer than through
//   it, much); a small town has none. The ring keeps 200–800 m from the middle.
// - Eras: the core nearest the middle, then the garden suburbs, then post-war; the core a regular grid
//   of Siksna's small blocks (~15 × 20 tiles centre to centre) with no cul-de-sacs, and the post-war
//   edge the most cul-de-sacs a block.
// - Transect: the zones in order out from the middle; lots narrower and houses taller nearer it; front
//   yards only in T3; shops in T3 and T4 only on the high street or as corner stores (city-research §2).
// - Neighbourhoods: every block in one, each about 400 m across; each big enough has a park block near
//   its middle (green, nothing built on it) and a school near its middle, and in a big town a corner
//   store at a neighbourhood's entrance, on a main road or collector.
// - A second centre: a big town with an airport has one, with T5 blocks round it that the main centre
//   alone would leave less built up, and offices among them; a small town has none.
// - And all of it the same again from the same key.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class TownPlanCheck
{
    /// <summary>FHWA's shares of urban road length [P1], with room for a small town's few roads: local 67–76%, arterials 11–17%, collectors 14–26%.</summary>
    const double LOCAL_MIN = 0.6, LOCAL_MAX = 0.8, ARTERIAL_MIN = 0.08, ARTERIAL_MAX = 0.25, COLLECTOR_MIN = 0.08, COLLECTOR_MAX = 0.3;

    public static int Run(string[] keys)
    {
        var towns = new List<(string name, string key, bool strict)>();
        if (keys.Length > 0) towns.AddRange(keys.Select(k => (k, k, false)));
        else
        {
            // The county's towns: Porchlight and Ember Crossing are big and dry, held to FHWA's shares and neighbourhoods
            // kept within the arterials; Lanternport is cut up by its lakes and Juniper Falls small, so theirs are only reported.
            foreach (var t in CountyCheck.PublicTowns()) towns.Add((t.name, t.key, t.name == "Porchlight" || t.name == "Ember Crossing"));
            for (int i = 0; i < 4; i++) towns.Add(($"big-{i}", $"seed=plan-big-{i}&width=256&height=256&townRadius=0.5&waterFraction=0.04&roadDensity={0.85 + 0.05 * (i % 3)}&culDeSacs=0.5", true));
            for (int i = 0; i < 3; i++) towns.Add(($"small-{i}", $"seed=plan-small-{i}", false));
        }
        var fails = new List<string>();
        if (keys.Length == 0)
        {
            // A hamlet (a 128 m town) is no neighbourhood unit: it keeps its homes, with no park or school taken out of them.
            const string HAMLET = "seed=aaaaaaaaaaaa&width=32&height=32";
            var (hs, hp) = Program.ParseKey(HAMLET);
            var hamlet = Generator.Generate(hs, hp);
            int homes = hamlet.buildings.Count(b => b.kind == BuildingKind.House), schools = hamlet.buildings.Count(b => b.kind == BuildingKind.School);
            int parks = hamlet.neighbourhoods.Count(n => n.parkBlockId >= 0);
            if (schools > 0 || parks > 0 || homes == 0) fails.Add($"hamlet: {schools} schools, {parks} parks, {homes} houses");
        }
        Console.WriteLine("town            ring  2nd   art  col  local  hoods (park, school, corners)  core/garden/post-war blocks  T3/T4/T5/T6 blocks  core block  culs a block (core, post-war)");
        foreach (var (name, key, strict) in towns)
        {
            var (seed, p) = Program.ParseKey(key);
            var m = Generator.Generate(seed, p, check: true);
            var layout = Layout.Build(m);
            var found = Check(name, m, layout, strict).ToList();
            // The same again.
            var again = Generator.Generate(seed, p);
            if (Fingerprint.Of(Plan(again)) != Fingerprint.Of(Plan(m))) found.Add($"{name}: planned differently the second time");
            fails.AddRange(found);
        }
        foreach (var f in fails) Console.WriteLine("FAIL " + f);
        Console.WriteLine(fails.Count == 0 ? $"{towns.Count} towns planned like real ones" : $"{fails.Count} failures");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>Everything the plan decides, to compare.</summary>
    static object Plan(TownModel m) => new
    {
        classes = m.roads.edges.Select(e => (int)e.cls).ToArray(),
        eras = m.blocks.Select(b => (int)b.era).ToArray(),
        zones = m.blocks.Select(b => (int)b.transect).ToArray(),
        hoods = m.blocks.Select(b => b.neighbourhood).ToArray(),
        parks = m.neighbourhoods.Select(n => n.parkBlockId).ToArray(),
        schools = m.neighbourhoods.Select(n => n.schoolBuildingId).ToArray(),
        corners = m.neighbourhoods.SelectMany(n => n.cornerShopIds).ToArray(),
        parkBlocks = m.parkBlocks.ToArray(),
        carParks = m.carParks.Select(c => c.lotId).ToArray(),
        services = new[] { m.services.fireStation, m.services.clinic, m.services.petrolStation }.Concat(m.services.supermarkets).Concat(m.services.cornerStores).ToArray(),
        kinds = m.buildings.Select(b => (int)b.kind).ToArray(),
        industry = m.industry == null ? -1 : m.industry.gate,
        second = m.secondCentre == null ? "none" : $"{m.secondCentre.Value.x},{m.secondCentre.Value.y}",
    };

    static IEnumerable<string> Check(string name, TownModel m, IsoLayout layout, bool strict)
    {
        int w = m.width, h = m.height;
        var C = m.center;
        double R = m.townRadius;
        bool big = TownPlan.IsBig(m);
        var g = m.roads;
        var mids = m.blocks.Select(b => PlanStage.Middle(Parcels.TilesIn(b.polygon, b.holes, w, h), w, b)).ToList();
        var blockTiles = m.blocks.Select(b => Parcels.TilesIn(b.polygon, b.holes, w, h).Count).ToList();

        // --- Road hierarchy ---
        foreach (var e in g.edges)
        {
            if (e.kind == RoadKind.Main && e.cls == RoadClass.Local) yield return $"{name}: main road {e.id} is local";
            if (e.kind == RoadKind.Path && e.cls != RoadClass.Local) yield return $"{name}: footpath {e.id} is {Names.Of(e.cls)}";
            if (e.kind == RoadKind.Street && e.cls == RoadClass.Arterial) yield return $"{name}: street {e.id} is an arterial";
        }
        // The arterials' junction nearest the centre (the centre's own node may be merged away into a bend).
        int hub = -1;
        foreach (var e in g.edges.Where(e => e.cls == RoadClass.Arterial))
            foreach (int n in new[] { e.from, e.to })
                if (hub < 0 || Geo.Dist(g.nodes[n].at, C) < Geo.Dist(g.nodes[hub].at, C)) hub = n;
        if (hub < 0) yield return $"{name}: no arterials";
        var byArterial = Reach(g, hub, e => e.cls == RoadClass.Arterial, null);
        foreach (var gate in m.gates)
        {
            int n = RoadGraphOps.NearestNode(g, gate);
            if (!byArterial.ContainsKey(n)) yield return $"{name}: no arterial from the centre to the gate at {gate.x},{gate.y}";
        }
        var shares = StreetsStage.ClassShares(m);
        int collectors = g.edges.Count(e => e.cls == RoadClass.Collector && e.kind == RoadKind.Street);
        if (m.blocks.Count >= 20 && collectors == 0) yield return $"{name}: {m.blocks.Count} blocks and no collector streets";
        if (strict)
        {
            if (shares.local < LOCAL_MIN || shares.local > LOCAL_MAX) yield return $"{name}: local streets are {shares.local:P0} of the road length, not about 70% (FHWA)";
            if (shares.arterial < ARTERIAL_MIN || shares.arterial > ARTERIAL_MAX) yield return $"{name}: arterials are {shares.arterial:P0} of the road length (FHWA: 11–17%)";
            if (shares.collector < COLLECTOR_MIN || shares.collector > COLLECTOR_MAX) yield return $"{name}: collectors are {shares.collector:P0} of the road length (FHWA: 14–26%)";
        }

        // --- Ring ---
        double ring = TownPlan.RingRadius(m);
        bool bypass = true;
        double worst = 0;
        for (int k = 0; k < m.gates.Count; k++)
        {
            int a = RoadGraphOps.NearestNode(g, m.gates[k]), b = RoadGraphOps.NearestNode(g, m.gates[(k + 1) % m.gates.Count]);
            // Round the middle, kept out of a disc half the ring's radius (or, a small town, a fifth of its own).
            double keepOut = big ? ring / 2 : R / 5;
            var round = Reach(g, a, e => e.cls == RoadClass.Arterial, q => Geo.Dist(q, C) >= keepOut);
            var through = Reach(g, a, e => e.cls == RoadClass.Arterial, null);
            if (!round.ContainsKey(b)) bypass = false;
            else if (through.ContainsKey(b)) worst = Math.Max(worst, round[b] / through[b]);
        }
        if (big)
        {
            if (!bypass) yield return $"{name}: no way round the middle from one gate to the next on arterials (no ring)";
            else if (worst > 1.6) yield return $"{name}: the way round the middle is {worst:0.00} times the way through it";
            if (ring < 50 || ring > 200) yield return $"{name}: the ring is {ring * 4:0} m from the middle, not 200–800 m (FHWA)";
        }
        else if (bypass && m.gates.Count == 4) yield return $"{name}: a small town with a way round its middle on arterials";

        // --- Eras ---
        var eraCount = new int[3];
        var eraDist = new double[3];
        for (int k = 0; k < m.blocks.Count; k++)
        {
            int e = (int)m.blocks[k].era;
            eraCount[e]++;
            eraDist[e] += Geo.Dist(mids[k], C);
            if (m.blocks[k].era != TownPlan.EraAt(m, mids[k])) yield return $"{name}: block {k}'s era isn't its place's";
        }
        for (int e = 0; e + 1 < 3; e++)
            if (eraCount[e] > 0 && eraCount[e + 1] > 0 && eraDist[e] / eraCount[e] >= eraDist[e + 1] / eraCount[e + 1])
                yield return $"{name}: {(Era)e} blocks are no nearer the middle than {(Era)(e + 1)} ones";
        // The core's blocks: centre to centre (the block plus a street's width), the short side and the long.
        double street = 2 * (Math.Ceiling(Raster.RoadHalfWidth(RoadKind.Street, m.@params) - 1e-9) - 1) + 1;
        var core = new List<(double s, double l)>();
        for (int k = 0; k < m.blocks.Count; k++)
        {
            if (m.blocks[k].era != Era.Core) continue;
            var box = Geo.PolygonBounds(m.blocks[k].polygon);
            double bw = box.x1 - box.x0 + street, bh = box.y1 - box.y0 + street;
            // Only whole blocks: a street all round (not cut by the ring, a main road at an angle, or water).
            if (Math.Abs(Geo.PolygonArea(m.blocks[k].polygon)) < 0.9 * (bw - street) * (bh - street)) continue;
            core.Add((Math.Min(bw, bh), Math.Max(bw, bh)));
        }
        string coreSize = "-";
        double scale = m.@params.streetSpacing / 9;
        if (core.Count >= 4)
        {
            double s = core.Select(c => c.s).OrderBy(v => v).ElementAt(core.Count / 2), l = core.Select(c => c.l).OrderBy(v => v).ElementAt(core.Count / 2);
            coreSize = $"{s:0}x{l:0}";
            if (s < 11 * scale || s > 19 * scale || l < 15 * scale || l > 27 * scale) yield return $"{name}: the core's blocks are {coreSize} tiles (median, centre to centre), not about 15 x 20 (Siksna)";
        }
        // Cul-de-sacs a block in each era.
        // (A street that runs down to the water and stops at the shore doesn't count: there's nothing to join it to.)
        var culs = new int[3];
        var water = Raster.WaterMask(m);
        foreach (var bulb in Raster.Bulbs(g.edges)) if (!StreetsStage.NearWater(bulb.at, water, w, h)) culs[(int)TownPlan.EraAt(m, bulb.at)]++;
        double CulsPer(int e) => eraCount[e] == 0 ? 0 : (double)culs[e] / eraCount[e];
        if (culs[(int)Era.Core] > 0) yield return $"{name}: {culs[(int)Era.Core]} cul-de-sacs in the gridded core";
        if (m.@params.culDeSacs > 0 && eraCount[2] >= 10 && eraCount[1] >= 10 && CulsPer(2) < CulsPer(0)) yield return $"{name}: fewer cul-de-sacs a block post-war ({CulsPer(2):0.00}) than in the core ({CulsPer(0):0.00})";

        // --- Transect ---
        var zoneCount = new int[7];
        var zoneDist = new double[7];
        var zoneLot = new double[7];
        var zoneLots = new int[7];
        for (int k = 0; k < m.blocks.Count; k++)
        {
            int z = (int)m.blocks[k].transect;
            zoneCount[z]++;
            zoneDist[z] += Centrality(m, mids[k]);
            if (m.blocks[k].transect != TownPlan.TransectAt(m, mids[k])) yield return $"{name}: block {k}'s zone isn't its place's";
        }
        foreach (var lot in m.lots)
        {
            int z = (int)m.blocks[lot.blockId].transect;
            zoneLot[z] += Math.Abs(Geo.PolygonArea(lot.polygon));
            zoneLots[z]++;
        }
        for (int z = 3; z < 6; z++)
            for (int y = z + 1; y <= 6; y++)
            {
                if (zoneCount[z] < 3 || zoneCount[y] < 3) continue;
                if (zoneDist[y] / zoneCount[y] >= zoneDist[z] / zoneCount[z]) yield return $"{name}: T{y} blocks are no nearer a centre than T{z} ones";
                if (zoneLots[z] >= 10 && zoneLots[y] >= 10 && zoneLot[y] / zoneLots[y] >= zoneLot[z] / zoneLots[z]) yield return $"{name}: T{y}'s lots ({zoneLot[y] / zoneLots[y]:0.0} tiles) are no smaller than T{z}'s ({zoneLot[z] / zoneLots[z]:0.0})";
            }
        var corner = new HashSet<int>(m.neighbourhoods.SelectMany(n => n.cornerShopIds).Concat(m.services.highStreet).Concat(m.services.cornerStores));
        var front = Parcels.Ground(m);
        foreach (var b in m.buildings)
        {
            var zone = m.blocks[m.lots[b.lotId].blockId].transect;
            var rule = TownPlan.Rules(zone);
            if (b.kind == BuildingKind.House && (b.floors < rule.houseFloorsMin || b.floors > rule.houseFloorsMax)) yield return $"{name}: a {b.floors}-floor house in {zone}";
            if (b.kind == BuildingKind.Shop && rule.shopShare == 0 && !corner.Contains(b.id)) yield return $"{name}: shop {b.id} in {zone}, not at a corner or on the high street";
            // On the sidewalk outside T3: the front looks straight onto the street.
            if (!rule.setback && m.@params.frontSetback > 0 && Yard(m, b, front) > 1) yield return $"{name}: building {b.id} in {zone} has a front yard";
        }

        // --- Neighbourhoods ---
        int parks = 0, schools = 0, withCorners = 0, bounded = 0;
        var label = PlanStage.Regions(m, out _);
        int Piece(int k)
        {
            // The piece of land (between the arterials) most of the block's tiles are in.
            var votes = Parcels.TilesIn(m.blocks[k].polygon, m.blocks[k].holes, w, h).Select(t => label[t]).Where(l => l >= 0).GroupBy(l => l).OrderByDescending(x => x.Count()).ThenBy(x => x.Key);
            return votes.Any() ? votes.First().Key : -1;
        }
        foreach (var hood in m.neighbourhoods)
        {
            double across = PlanStage.Across(hood.blockIds, mids);
            // About 400 m: never more than 600 m between its blocks' middles.
            if (across > 150) yield return $"{name}: neighbourhood {hood.id} is {across * 4:0} m across, not about 400 m";
            // Bounded by arterials: all its blocks in one piece of the land they cut.
            if (hood.blockIds.Select(Piece).Distinct().Count() == 1) bounded++;
            if (!PlanStage.Served(hood))
            {
                if (hood.parkBlockId >= 0 || hood.schoolBuildingId >= 0) yield return $"{name}: neighbourhood {hood.id} is too small for a park and a school, and has one";
                continue;
            }
            if (hood.parkBlockId < 0) yield return $"{name}: neighbourhood {hood.id} ({hood.blockIds.Count} blocks) has no park";
            else
            {
                parks++;
                // Near its middle: nearer than most of its blocks.
                var blockD = hood.blockIds.Select(k => Geo.Dist(mids[k], hood.middle)).OrderBy(v => v).ToList();
                if (Geo.Dist(mids[hood.parkBlockId], hood.middle) > blockD[blockD.Count / 2]) yield return $"{name}: neighbourhood {hood.id}'s park is {Geo.Dist(mids[hood.parkBlockId], hood.middle):0} tiles from its middle, further than most of its blocks";
                foreach (var lot in m.lots.Where(l => l.blockId == hood.parkBlockId))
                    foreach (int t in Parcels.TilesIn(lot.polygon, null, w, h))
                        if (layout.use[t] == Use.HOUSE || layout.use[t] == Use.COMMUNITY_GARDEN) { yield return $"{name}: neighbourhood {hood.id}'s park has a {(layout.use[t] == Use.HOUSE ? "house" : "garden")} in it"; goto parkDone; }
                parkDone:;
            }
            if (hood.schoolBuildingId < 0) yield return $"{name}: neighbourhood {hood.id} ({hood.blockIds.Count} blocks) has no school";
            else
            {
                schools++;
                var at = Geo.PolygonCentroid(m.buildings[hood.schoolBuildingId].footprint);
                // Near its middle: among the quarter of its buildings nearest it.
                var mine = new HashSet<int>(hood.blockIds);
                var buildingD = m.buildings.Where(b => mine.Contains(m.lots[b.lotId].blockId)).Select(b => Geo.Dist(Geo.PolygonCentroid(b.footprint), hood.middle)).OrderBy(v => v).ToList();
                if (Geo.Dist(at, hood.middle) > buildingD[buildingD.Count / 4]) yield return $"{name}: neighbourhood {hood.id}'s school is {Geo.Dist(at, hood.middle):0} tiles from its middle, not among the buildings nearest it";
                if (layout.houses.All(x => x.buildingId != hood.schoolBuildingId)) yield return $"{name}: neighbourhood {hood.id}'s school isn't in the street scene";
            }
            if (hood.cornerShopIds.Count > 0) withCorners++;
            foreach (int s in hood.cornerShopIds)
            {
                var lot = m.lots[m.buildings[s].lotId];
                // At a neighbourhood's entrance (Gibbs): on the main road or the collector into it.
                bool onMain = Parcels.AdjacentRoadEdges(Parcels.TilesIn(lot.polygon, null, w, h), front, w, h).Any(kv => kv.Key >= 0 && g.edges[kv.Key].cls != RoadClass.Local);
                if (!onMain) yield return $"{name}: corner shop {s} isn't on an arterial or collector";
                if (m.blocks[lot.blockId].neighbourhood != hood.id) yield return $"{name}: corner shop {s} isn't in its neighbourhood";
            }
        }
        if (big && withCorners == 0) yield return $"{name}: no neighbourhood has a corner store";
        if (strict && bounded * 2 < m.neighbourhoods.Count) yield return $"{name}: only {bounded} of {m.neighbourhoods.Count} neighbourhoods keep within the arterials";

        // --- Second centre ---
        string second = "-";
        if (big && m.airport != null && m.secondCentre == null) yield return $"{name}: a big town with an airport and no second centre";
        if (!big && m.secondCentre != null) yield return $"{name}: a small town with a second centre";
        if (m.secondCentre != null)
        {
            var S = m.secondCentre.Value;
            double d = Geo.Dist(S, C) / R;
            second = $"{d:0.00}R";
            if (d < 0.44 || d > 0.81) yield return $"{name}: the second centre is {d:0.00} of the way out";
            // Blocks round it it makes T5, that the main centre alone wouldn't.
            var alone = m.With();
            alone.secondCentre = null;
            int lifted = 0, offices = 0;
            for (int k = 0; k < m.blocks.Count; k++)
                if (Geo.Dist(mids[k], S) < R * TownPlan.SECOND_REACH * m.@params.downtown && m.blocks[k].transect == Transect.T5 && TownPlan.TransectAt(alone, mids[k]) < Transect.T5) lifted++;
            foreach (var house in layout.houses)
                if (house.style == HouseStyle.Office && Geo.Dist(new Vec2(house.x + 0.5, house.y + 0.5), S) < R * TownPlan.SECOND_REACH * m.@params.downtown && TownPlan.TransectAt(alone, new Vec2(house.x + 0.5, house.y + 0.5)) < Transect.T5) offices++;
            second += $" ({lifted} T5 blocks, {offices} offices)";
            if (lifted == 0) yield return $"{name}: the second centre makes no block round it T5";
            if (m.@params.towerShare > 0 && offices == 0) yield return $"{name}: no offices round the second centre";
        }

        Console.WriteLine($"{name,-15} {(ring > 0 ? $"{ring * 4:0}m" : "-"),5} {second,-24} {shares.arterial,4:P0} {shares.collector,4:P0} {shares.local,5:P0}  {m.neighbourhoods.Count} ({parks}, {schools}, {withCorners}; {bounded} bounded)  {eraCount[0]}/{eraCount[1]}/{eraCount[2]}  {zoneCount[3]}/{zoneCount[4]}/{zoneCount[5]}/{zoneCount[6]}  {coreSize}  {CulsPer(0):0.00}, {CulsPer(2):0.00}");
    }

    /// <summary>How central a place is: its distance from the nearer centre, as a share of that centre's reach.</summary>
    static double Centrality(TownModel m, Vec2 at)
    {
        double d = Geo.Dist(at, m.center) / m.townRadius;
        if (m.secondCentre != null) d = Math.Min(d, Geo.Dist(at, m.secondCentre.Value) / (m.townRadius * TownPlan.SECOND_REACH));
        return d;
    }

    /// <summary>How many tiles of its own lot lie between a building's front and the street it faces.</summary>
    static int Yard(TownModel m, Building b, Ground ground)
    {
        int w = m.width;
        var (dx, dy) = Grid.DIRS4[(int)b.facing];
        int most = 0;
        var own = new HashSet<int>(b.tiles.Select(t => Grid.Index(t.x, t.y, w)));
        foreach (var t in b.tiles)
        {
            if (own.Contains(Grid.Index(t.x + dx, t.y + dy, w))) continue;
            int k = 1;
            for (; k < 8; k++)
            {
                int x = t.x + dx * k, y = t.y + dy * k;
                if (x < 0 || y < 0 || x >= w || y >= m.height || Parcels.IsStreetTile(ground, Grid.Index(x, y, w))) break;
            }
            most = Math.Max(most, k - 1);
        }
        return most;
    }

    /// <summary>The shortest distance along the roads `use` allows from `from` to every node it reaches (nodes `ok`, if given, only).</summary>
    static Dictionary<int, double> Reach(RoadGraph g, int from, Func<RoadEdge, bool> use, Func<Vec2, bool> ok)
    {
        var best = new Dictionary<int, double> { [from] = 0 };
        var open = new SortedSet<(double d, int n)> { (0, from) };
        var adj = new Dictionary<int, List<RoadEdge>>();
        foreach (var e in g.edges)
        {
            if (!use(e)) continue;
            if (ok != null && !e.points.All(ok)) continue;
            foreach (int n in new[] { e.from, e.to })
            {
                if (!adj.TryGetValue(n, out var l)) adj[n] = l = new List<RoadEdge>();
                l.Add(e);
            }
        }
        while (open.Count > 0)
        {
            var (d, n) = open.Min;
            open.Remove(open.Min);
            if (d > best[n]) continue;
            if (!adj.TryGetValue(n, out var list)) continue;
            foreach (var e in list)
            {
                int o = e.from == n ? e.to : e.from;
                double nd = d + Geo.PolylineLength(e.points);
                if (!best.TryGetValue(o, out var was) || nd < was)
                {
                    best[o] = nd;
                    open.Add((nd, o));
                }
            }
        }
        return best;
    }
}
