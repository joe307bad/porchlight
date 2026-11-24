// Are the shops and services in realistic places? (docs/city-research.md §2, docs/town-shops.md)
// Run with `zoning`, after each town's zoning:
//
// - Park land: acres per 1,000 residents (as zoning counts them), at least LandStage.PARK_ACRES less a margin
//   for the estimate it was planned from.
// - Corner stores: households per store about Gibbs's 1,000 (500–2,000).
// - The fire station: every building within ISO's 1.5 road miles (2.4 km) of it, and within NFPA 1710's
//   240 s at a side street's speed.
// - Every corner store, supermarket, petrol station, clinic and fire station fronts a main road or a
//   collector; the high street's shops front a main road; a supermarket in every town of 300 households.
// - Industry in its wedge: most workshops in it.
// - Land value: shops stand on dearer land than houses, workshops on cheaper.
// - Car parks: paved, unbuilt, behind a main road; and the plaza is the market place.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

static class ShopsCheck
{
    /// <summary>A side street's speed (tiles a second: about 37 km/h), for NFPA's 240 s.</summary>
    const double STREET_SPEED = 2.6;
    /// <summary>ISO's 1.5 road miles, in tiles of 4 m.</summary>
    const double ISO_TILES = 2414 / 4.0;

    public static IEnumerable<string> Check(string name, TownModel m, IsoLayout layout, TownZoning z, bool strict)
    {
        int w = m.width, h = m.height;
        var sv = m.services;
        var g = Parcels.Ground(m);
        List<RoadClass> Fronts(Building b) => Parcels.AdjacentRoadEdges(Parcels.TilesIn(m.lots[b.lotId].polygon, null, w, h), g, w, h)
            .Where(kv => kv.Key >= 0 && kv.Key < m.roads.edges.Count).Select(kv => m.roads.edges[kv.Key].cls).ToList();
        bool Main(Building b) => Fronts(b).Any(c => c != RoadClass.Local);

        // Park land.
        int parkTiles = m.ParkBlockSet().Sum(k => Parcels.TilesIn(m.blocks[k].polygon, m.blocks[k].holes, w, h).Count);
        double acres = parkTiles / LandStage.ACRE_TILES;
        double perThousand = z.residents == 0 ? 0 : acres / (z.residents / 1000.0);
        // Corner stores.
        int stores = sv.cornerStores.Count;
        double perStore = stores == 0 ? 0 : z.homes / (double)stores;
        // The fire station's furthest building by road.
        double furthest = double.NaN;
        if (sv.fireStation >= 0)
        {
            var roads = new Retail.Roads(m);
            var d = roads.From(RoadGraphOps.NearestNode(m.roads, Geo.PolygonCentroid(m.buildings[sv.fireStation].footprint)), out _);
            furthest = 0;
            foreach (var b in m.buildings)
            {
                double x = d[RoadGraphOps.NearestNode(m.roads, Geo.PolygonCentroid(b.footprint))];
                if (!double.IsPositiveInfinity(x)) furthest = Math.Max(furthest, x);
            }
        }
        // Workshops in the wedge.
        var workshops = m.buildings.Where(b => b.kind == BuildingKind.Workshop).ToList();
        int inWedge = m.industry == null ? 0 : workshops.Count(b => m.industry.Contains(m.center, m.townRadius, Geo.PolygonCentroid(b.footprint)));
        // Land value by kind.
        double Mean(BuildingKind k)
        {
            var v = m.buildings.Where(b => b.kind == k).Select(b => LandStage.At(m, Geo.PolygonCentroid(b.footprint))).ToList();
            return v.Count == 0 ? double.NaN : v.Average();
        }
        double house = Mean(BuildingKind.House), shop = Mean(BuildingKind.Shop), work = Mean(BuildingKind.Workshop);

        Console.WriteLine($"  {name}: planned for {sv.households} households; park blocks {m.parkBlocks.Count} ({m.neighbourhoods.Count(n => n.parkBlockId >= 0)} the neighbourhoods'); parks {acres:0.0} acres, {perThousand:0.0} a 1,000 residents; {stores} corner stores ({perStore:0} households each); "
            + $"{sv.supermarkets.Count} supermarkets; petrol {(sv.petrolStation >= 0 ? "yes" : "no")}; clinic {(sv.clinic >= 0 ? "yes" : "no")}; "
            + $"fire station {(sv.fireStation >= 0 ? $"{furthest * 4:0} m from the furthest building" : "none")}; high street {sv.highStreet.Count} shops; "
            + $"{m.carParks.Count} car parks; workshops {inWedge}/{workshops.Count} in the wedge; land value house {house:0.00} shop {shop:0.00} workshop {work:0.00}");

        if (z.residents > 0 && m.ParkBlockSet().Count > 0 && strict && perThousand < LandStage.PARK_ACRES * 0.6)
            yield return $"{name}: {perThousand:0.0} acres of park a 1,000 residents, under {LandStage.PARK_ACRES * 0.6:0.0}";
        if (strict && stores > 0 && (perStore < 500 || perStore > 2000)) yield return $"{name}: {perStore:0} households a corner store, not about 1,000";
        if (z.homes >= Retail.CORNER_TOWN * 2 && stores == 0) yield return $"{name}: {z.homes} households and no corner store";
        if (z.homes >= Retail.SUPERMARKET_TOWN * 2 && sv.supermarkets.Count == 0) yield return $"{name}: {z.homes} households and no supermarket";
        if (z.homes >= Retail.CORNER_TOWN * 2 && sv.fireStation < 0) yield return $"{name}: {z.homes} households and no fire station";
        if (!double.IsNaN(furthest))
        {
            if (furthest > ISO_TILES) yield return $"{name}: a building {furthest * 4:0} m from the fire station by road, over ISO's 2.4 km";
            if (furthest / STREET_SPEED > 240) yield return $"{name}: a building {furthest / STREET_SPEED:0} s from the fire station, over NFPA's 240 s";
        }
        foreach (var (id, what) in sv.cornerStores.Select(i => (i, "corner store")).Concat(sv.supermarkets.Select(i => (i, "supermarket")))
                     .Concat(new[] { (sv.petrolStation, "petrol station"), (sv.clinic, "clinic"), (sv.fireStation, "fire station") }))
        {
            if (id < 0) continue;
            if (!Main(m.buildings[id])) yield return $"{name}: the {what} {id} isn't on a main road or collector";
            if (layout.houses.All(x => x.buildingId != id)) yield return $"{name}: the {what} {id} isn't in the street scene";
        }
        if (sv.petrolStation >= 0 && !Fronts(m.buildings[sv.petrolStation]).Contains(RoadClass.Arterial)) yield return $"{name}: the petrol station isn't on a main road";
        foreach (int id in sv.highStreet)
            if (!Fronts(m.buildings[id]).Contains(RoadClass.Arterial)) yield return $"{name}: high-street shop {id} isn't on a main road";
        if (m.industry != null && workshops.Count >= 10 && inWedge * 10 < workshops.Count * 6) yield return $"{name}: only {inWedge} of {workshops.Count} workshops in the industrial wedge";
        if (!double.IsNaN(shop) && !double.IsNaN(house) && shop <= house) yield return $"{name}: shops stand on land worth {shop:0.00}, no more than houses' {house:0.00}";
        if (!double.IsNaN(work) && !double.IsNaN(house) && m.industry != null && work >= house) yield return $"{name}: workshops stand on land worth {work:0.00}, no less than houses' {house:0.00}";
        var built = new HashSet<int>(m.buildings.Select(b => b.lotId));
        foreach (var c in m.carParks)
        {
            if (built.Contains(c.lotId)) yield return $"{name}: car park {c.id} has a building on it";
            var tiles = Parcels.TilesIn(m.lots[c.lotId].polygon, null, w, h);
            if (tiles.Count(t => layout.use[t] == Use.CAR_PARK) * 2 < tiles.Count) yield return $"{name}: car park {c.id} isn't paved";
        }
        if (strict && m.blocks.Any(b => b.transect >= Transect.T5) && sv.highStreet.Count + m.buildings.Count(b => b.kind == BuildingKind.Shop) > 20 && m.carParks.Count == 0)
            yield return $"{name}: a centre of shops and no car park";
        if (m.landmarks.Any(l => l.kind == LandmarkKind.Plaza) && !m.landmarks.Any(l => l.kind == LandmarkKind.Market)) yield return $"{name}: a plaza and no market";
    }
}
