// What each building is for: homes, work, or both. The first step towards
// townsfolk who live somewhere, work somewhere else and drive between the two
// (docs/land-use.md). Pure, and a function of the town alone (its seed and
// settings, through the model and its layout), so every town has the same
// zoning wherever it's made: the game, the simulation, or the Checks tool.
//
// Each building's use starts from what it looks like: houses are homes,
// workshops industry, the shops along the high street shops with flats over
// them, the towers in the middle offices, the church and the hall civic. Each
// then holds homes and jobs for its floor area, at planning densities (Toronto
// City Planning's floor space per worker, 2024). Last, the town is balanced:
// a town of towers has far more jobs than its people could fill, and a town of
// houses far fewer, so the towers furthest from the middle become flats (the
// middle outbids them for offices: Alonso's bid-rent), or, short of jobs, the
// flats over the middle's shops become offices and the houses along the main
// roads nearest the middle get a shop downstairs (Hoyt's commercial strips),
// until there's about a job for every working resident (jobs-housing balance).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    /// <summary>What a building is for. (Stored and sent as its number: add to the end.)</summary>
    public enum Zone { Residential, MixedUse, Retail, Office, Industrial, Civic }

    public sealed class ZonedBuilding
    {
        public int buildingId;
        /// <summary>What the building is (a school, a supermarket, a fire station...).</summary>
        public BuildingKind kind;
        public Zone zone;
        /// <summary>Households it houses.</summary>
        public int homes;
        /// <summary>People who work there.</summary>
        public int jobs;
        public int floors;
        /// <summary>Floor area, square metres (every floor).</summary>
        public int area;
        /// <summary>The tiles it stands on, as a box: x0..x1, y0..y1 exclusive.</summary>
        public int x0, y0, x1, y1;
    }

    public sealed class TownZoning
    {
        public List<ZonedBuilding> buildings = new List<ZonedBuilding>();
        public int homes;
        public int residents;
        /// <summary>Residents with a job to go to.</summary>
        public int workers;
        public int jobs;

        /// <summary>Jobs a household: about Zoning.JOBS_PER_HOME is a balanced town.</summary>
        public double JobsPerHome => homes == 0 ? 0 : (double)jobs / homes;

        public int Count(Zone z)
        {
            int n = 0;
            foreach (var b in buildings) if (b.zone == z) n++;
            return n;
        }
    }

    public static class Zoning
    {
        /// <summary>A tile is 4 m square.</summary>
        public const int TILE_M2 = 16;
        /// <summary>A house up to this floor area is one household's; more, and it's flats.</summary>
        public const int SINGLE_FAMILY_M2 = 320;
        /// <summary>Gross floor area a flat takes, its share of stairs and halls included.</summary>
        public const int FLAT_M2 = 100;
        /// <summary>Floor space a worker, square metres (Toronto City Planning, 2024 medians).</summary>
        public const int OFFICE_M2_A_JOB = 29, RETAIL_M2_A_JOB = 28, INDUSTRIAL_M2_A_JOB = 85, HALL_M2_A_JOB = 30, CHURCH_M2_A_JOB = 300;
        /// <summary>People a household (the US average is about 2.5).</summary>
        public const double PEOPLE_A_HOME = 2.5;
        /// <summary>Of them, at work (about 60% of adults are employed; a household has 1.2 or so workers).</summary>
        public const double WORKERS_A_HOME = 1.2;
        /// <summary>A balanced town: a job for every worker who lives in it.</summary>
        public const double JOBS_PER_HOME = WORKERS_A_HOME;
        /// <summary>The band planners count as balanced, jobs a household (US EPA EnviroAtlas): outside it, people commute in or out.</summary>
        public const double BALANCED_LOW = 0.75, BALANCED_HIGH = 1.5;
        /// <summary>The most of a town's houses that may get a shop downstairs.</summary>
        const double MAX_CORNER_SHOPS = 0.2;

        public static TownZoning Plan(TownModel model) => Plan(model, Layout.Build(model));

        public static TownZoning Plan(TownModel model, IsoLayout layout)
        {
            var C = model.center;
            double R = Math.Max(1, model.townRadius);
            var byId = new Dictionary<int, Building>();
            foreach (var b in model.buildings) byId[b.id] = b;
            var town = new TownZoning();
            var from = new Dictionary<ZonedBuilding, double>();
            var houses = new Dictionary<ZonedBuilding, House>();
            foreach (var h in layout.houses)
            {
                if (h.tiles == null || h.tiles.Count == 0) continue;
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
                double cx = 0, cy = 0;
                foreach (var t in h.tiles)
                {
                    x0 = Math.Min(x0, t.x);
                    y0 = Math.Min(y0, t.y);
                    x1 = Math.Max(x1, t.x + 1);
                    y1 = Math.Max(y1, t.y + 1);
                    cx += t.x + 0.5;
                    cy += t.y + 0.5;
                }
                int floors = Math.Max(1, h.floors);
                var z = new ZonedBuilding { buildingId = h.buildingId, kind = h.kind, floors = floors, area = h.tiles.Count * TILE_M2 * floors, x0 = x0, y0 = y0, x1 = x1, y1 = y1 };
                Use(z, StartingZone(h.kind, h.style), h);
                town.buildings.Add(z);
                from[z] = Numbers.Hypot(cx / h.tiles.Count - C.x, cy / h.tiles.Count - C.y) / R;
                houses[z] = h;
            }
            Total(town);
            Balance(model, town, from, houses);
            Total(town);
            return town;
        }

        static Zone StartingZone(BuildingKind kind, HouseStyle style) => kind switch
        {
            BuildingKind.Supermarket or BuildingKind.PetrolStation => Zone.Retail,
            BuildingKind.FireStation or BuildingKind.Clinic or BuildingKind.School => Zone.Civic,
            _ => StartingZone(style),
        };

        static Zone StartingZone(HouseStyle style) => style switch
        {
            HouseStyle.House => Zone.Residential,
            HouseStyle.Workshop => Zone.Industrial,
            HouseStyle.Shop => Zone.MixedUse,
            HouseStyle.Office => Zone.Office,
            _ => Zone.Civic,
        };

        /// <summary>Puts a building to this use: its homes and jobs for its floor area.</summary>
        static void Use(ZonedBuilding b, Zone zone, House house)
        {
            b.zone = zone;
            int ground = house.tiles.Count * TILE_M2;
            int upper = b.area - ground;
            switch (zone)
            {
                case Zone.Residential:
                    b.homes = b.area <= SINGLE_FAMILY_M2 ? 1 : Math.Max(1, b.area / FLAT_M2);
                    b.jobs = 0;
                    break;
                case Zone.MixedUse:
                    // A shop on the street, flats (or, a cottage, the shopkeeper's own home) over it.
                    b.jobs = Math.Max(1, ground / RETAIL_M2_A_JOB);
                    b.homes = Math.Max(1, upper / FLAT_M2);
                    break;
                case Zone.Retail:
                    b.jobs = Math.Max(1, ground / RETAIL_M2_A_JOB + upper / OFFICE_M2_A_JOB);
                    b.homes = 0;
                    break;
                case Zone.Office:
                    // Shops or a lobby on the ground floor, offices above.
                    b.jobs = Math.Max(1, ground / RETAIL_M2_A_JOB + upper / OFFICE_M2_A_JOB);
                    b.homes = 0;
                    break;
                case Zone.Industrial:
                    b.jobs = Math.Max(1, b.area / INDUSTRIAL_M2_A_JOB);
                    b.homes = 0;
                    break;
                default:
                    b.jobs = Math.Max(2, b.area / (house.style == HouseStyle.Church ? CHURCH_M2_A_JOB : HALL_M2_A_JOB));
                    b.homes = 0;
                    break;
            }
        }

        static void Total(TownZoning town)
        {
            town.homes = town.jobs = 0;
            foreach (var b in town.buildings)
            {
                town.homes += b.homes;
                town.jobs += b.jobs;
            }
            town.residents = (int)Math.Round(town.homes * PEOPLE_A_HOME);
            town.workers = (int)Math.Round(town.homes * WORKERS_A_HOME);
        }

        /// <summary>How far off balance: 0 balanced, the same either way (twice the jobs as bad as half).</summary>
        static double Off(int homes, int jobs) => homes == 0 ? double.PositiveInfinity : Math.Abs(Math.Log(Math.Max(1, jobs) / (homes * JOBS_PER_HOME)));

        static void Balance(TownModel model, TownZoning town, Dictionary<ZonedBuilding, double> from, Dictionary<ZonedBuilding, House> houses)
        {
            int homes = town.homes, jobs = town.jobs;
            // One change at a time, while it brings the town nearer a job a worker.
            bool Try(ZonedBuilding b, Zone zone)
            {
                int h0 = b.homes, j0 = b.jobs;
                var z0 = b.zone;
                Use(b, zone, houses[b]);
                int h = homes - h0 + b.homes, j = jobs - j0 + b.jobs;
                if (Off(h, j) < Off(homes, jobs))
                {
                    homes = h;
                    jobs = j;
                    return true;
                }
                Use(b, z0, houses[b]);
                return false;
            }
            int Outward(ZonedBuilding a, ZonedBuilding b) => from[a] != from[b] ? from[a].CompareTo(from[b]) : a.buildingId.CompareTo(b.buildingId);

            if (jobs > homes * JOBS_PER_HOME)
            {
                // Too much work: the outermost towers first become flats.
                var towers = town.buildings.FindAll(b => b.zone == Zone.Office);
                towers.Sort((a, b) => -Outward(a, b));
                foreach (var b in towers)
                {
                    if (jobs <= homes * JOBS_PER_HOME) break;
                    Try(b, Zone.Residential);
                }
                return;
            }
            // Too little: the middle's shops take their upper floors for offices,
            var shops = town.buildings.FindAll(b => b.zone == Zone.MixedUse && b.floors >= 2);
            shops.Sort(Outward);
            foreach (var b in shops)
            {
                if (jobs >= homes * JOBS_PER_HOME) return;
                Try(b, Zone.Retail);
            }
            // then the houses on the main roads, nearest the middle first, open a shop downstairs.
            var g = Parcels.Ground(model);
            var strip = town.buildings.FindAll(b => b.zone == Zone.Residential && b.homes == 1 && OnMainRoad(houses[b], g, model.width, model.height, model.@params.frontSetback));
            strip.Sort(Outward);
            int most = (int)(town.Count(Zone.Residential) * MAX_CORNER_SHOPS), opened = 0;
            foreach (var b in strip)
            {
                if (jobs >= homes * JOBS_PER_HOME || opened >= most) return;
                if (Try(b, Zone.MixedUse)) opened++;
            }
        }

        /// <summary>Whether a house's front looks onto a main road.</summary>
        static bool OnMainRoad(House h, Ground g, int w, int hgt, double setback)
        {
            var (dx, dy) = Grid.DIRS4[(int)h.facing];
            int reach = (int)Math.Ceiling(setback) + 3;
            foreach (var t in h.tiles)
            {
                for (int k = 1; k <= reach; k++)
                {
                    int x = t.x + dx * k, y = t.y + dy * k;
                    if (x < 0 || y < 0 || x >= w || y >= hgt) break;
                    byte kind = g.road.kind[Grid.Index(x, y, w)];
                    if (kind == Raster.ROAD_MAIN) return true;
                    if (kind != Raster.ROAD_NONE) break;
                }
            }
            return false;
        }

        /// <summary>Each building as Manage draws it: [zone, x0, y0, x1, y1, homes, jobs, kind (BuildingKind)], tiles, the town's frame.</summary>
        public static List<int[]> Encode(TownZoning town)
        {
            var o = new List<int[]>(town.buildings.Count);
            foreach (var b in town.buildings) o.Add(new[] { (int)b.zone, b.x0, b.y0, b.x1, b.y1, b.homes, b.jobs, (int)b.kind });
            return o;
        }
    }
}
