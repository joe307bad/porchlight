// How a town is planned, as a real one is (docs/city-research.md, section 1):
//
// - Eras. Towns grow outward and each ring is built the way its day built
//   them: an old gridded core of small blocks (Siksna's ~60 × 80 m, about
//   15 × 20 tiles), garden suburbs round it, and post-war "loops and
//   lollipops" (Southworth & Owens) at the edge.
// - The transect (SmartCode): T3 sub-urban to T6 urban core, each with its
//   own setback, heights and lot sizes (Rules), from how central a place is.
//   The town's own settings place the zones: `downtown` is the centre (T5,
//   with T6 at its heart) and `innerCity` is general urban (T4, no front
//   yards), so a town keeps the character its key gives it.
// - Multiple nuclei (Harris & Ullman): a big town has a second centre, by its
//   airport or its waterfront, with a T5 centre of its own.
// - A ring road in a big town, so arterials are no more than 200–800 m apart
//   (FHWA) and a trip across town needn't go through the middle.
//
// Pure functions of the town so far: every stage that needs to know asks here.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    /// <summary>What a transect zone builds: the SmartCode's table, in the generator's terms.</summary>
    public struct TransectRule
    {
        /// <summary>Front yards (frontSetback), or buildings on the sidewalk.</summary>
        public bool setback;
        /// <summary>Lots are cut no bigger than lotMaxSize times this: narrower lots nearer the middle.</summary>
        public double lotScale;
        public int houseFloorsMin, houseFloorsMax;
        /// <summary>The share of buildings that are shops, offices and towers, times towerShare.</summary>
        public double shopShare;
        /// <summary>Unbuilt lots may be community gardens (never in the urban zones).</summary>
        public bool gardens;
        /// <summary>Some lots left open as courtyards among the towers (the `courtyards` setting).</summary>
        public bool courtyards;
    }

    public static class TownPlan
    {
        /// <summary>A town at least this big (its radius, in tiles: 320 m) has a ring road and a second centre.</summary>
        public const double BIG_TOWN = 80;
        /// <summary>The ring runs at this share of the town's radius, but never nearer the middle than RING_NEAREST.</summary>
        public const double RING_SHARE = 0.5;
        /// <summary>200 m: FHWA's closest spacing of arterials downtown.</summary>
        public const double RING_NEAREST = 50;
        /// <summary>The old core reaches this share of the town's radius, the garden suburbs this far (then post-war).</summary>
        public const double CORE_REACH = 0.3, GARDEN_REACH = 0.65;
        /// <summary>How far (a share of the radius) the eras' and zones' edges wander either way.</summary>
        public const double ERA_WOBBLE = 0.08, ZONE_WOBBLE = 0.06;
        /// <summary>The second centre's reach, as a share of the town's radius (its downtown is this much smaller).</summary>
        public const double SECOND_REACH = 0.5;
        /// <summary>A core block, centre to centre (tiles), for the default street spacing (9): Siksna's 60 × 80 m.</summary>
        public const double CORE_DEPTH = 15, CORE_LENGTH = 20;
        const double WOBBLE_CELL = 24;

        public static bool IsBig(TownModel m) => m.townRadius >= BIG_TOWN;

        /// <summary>The ring road's radius (tiles), or 0 for a town too small for one.</summary>
        public static double RingRadius(TownModel m) => IsBig(m) ? Math.Min(m.townRadius * 0.75, Math.Max(RING_NEAREST, m.townRadius * RING_SHARE)) : 0;

        /// <summary>A smooth wobble in [-1, 1] over the map, the same for the same seed (value noise, cells WOBBLE_CELL across).</summary>
        public static double Wobble(string seed, int salt, Vec2 at)
        {
            uint s = unchecked(Rng.HashString(seed ?? "") ^ ((uint)salt * 0x9E3779B9u));
            double gx = at.x / WOBBLE_CELL, gy = at.y / WOBBLE_CELL;
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            double fx = gx - ix, fy = gy - iy;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            double a = Lattice(s, ix, iy), b = Lattice(s, ix + 1, iy), c = Lattice(s, ix, iy + 1), d = Lattice(s, ix + 1, iy + 1);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        static double Lattice(uint s, int x, int y)
        {
            unchecked
            {
                uint h = s ^ ((uint)x * 374761393u) ^ ((uint)y * 668265263u);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / 4294967295.0 * 2 - 1;
            }
        }

        /// <summary>When the land at `at` was built: by its distance from the (first) centre, the old core in the middle.</summary>
        public static Era EraAt(TownModel m, Vec2 at)
        {
            if (m.townRadius <= 0) return Era.Core;
            double share = Geo.Dist(at, m.downtown) / m.townRadius + ERA_WOBBLE * Wobble(m.seed, 1, at);
            return share < CORE_REACH ? Era.Core : share < GARDEN_REACH ? Era.GardenSuburb : Era.PostWar;
        }

        /// <summary>The transect zone at `at`: the more central of what the main centre and the second make it.</summary>
        public static Transect TransectAt(TownModel m, Vec2 at)
        {
            var p = m.@params;
            if (m.townRadius <= 0) return Transect.T6;
            double wobble = ZONE_WOBBLE * Wobble(m.seed, 2, at);
            var zone = Zone(Geo.Dist(at, m.downtown) / m.townRadius + wobble, p);
            if (m.secondCentre != null)
            {
                // The second centre is a T5 centre, never a core.
                var second = Zone(Geo.Dist(at, m.secondCentre.Value) / (m.townRadius * SECOND_REACH) + wobble, p);
                if (second > Transect.T5) second = Transect.T5;
                if (second > zone) zone = second;
            }
            return zone;
        }

        static Transect Zone(double share, GenParams p)
        {
            if (share < p.downtown / 2) return Transect.T6;
            if (share < p.downtown || p.downtown >= 1) return Transect.T5;
            if (share < p.innerCity || p.innerCity >= 1) return Transect.T4;
            return Transect.T3;
        }

        /// <summary>The transect table: what each zone builds.</summary>
        public static TransectRule Rules(Transect t) => t switch
        {
            Transect.T6 => new TransectRule { setback = false, lotScale = 0.45, houseFloorsMin = 2, houseFloorsMax = 2, shopShare = 1, gardens = false, courtyards = true },
            Transect.T5 => new TransectRule { setback = false, lotScale = 0.7, houseFloorsMin = 1, houseFloorsMax = 2, shopShare = 1, gardens = false, courtyards = true },
            Transect.T4 => new TransectRule { setback = false, lotScale = 0.85, houseFloorsMin = 1, houseFloorsMax = 2, shopShare = 0, gardens = false, courtyards = false },
            _ => new TransectRule { setback = true, lotScale = 1, houseFloorsMin = 1, houseFloorsMax = 1, shopShare = 0, gardens = true, courtyards = false },
        };

        /// <summary>
        /// A big town's second centre: just outside its airport's gate, towards the town (or, with no airport near,
        /// its waterfront: the shore with the most water off it); null in a smaller town, or with neither.
        /// </summary>
        public static Vec2? SecondCentre(TownModel m)
        {
            if (!IsBig(m)) return null;
            int w = m.width, h = m.height;
            var C = m.center;
            double R = m.townRadius;
            Vec2? Within(Vec2 at)
            {
                // Pulled in along the line from the middle to between 0.45 and 0.8 of the way out.
                double d = Geo.Dist(at, C);
                if (d < 1e-9) return null;
                double r = Math.Max(0.45 * R, Math.Min(0.8 * R, d));
                return new Vec2(C.x + (at.x - C.x) / d * r, C.y + (at.y - C.y) / d * r);
            }
            var water = Raster.WaterMask(m);
            bool Dry(Vec2 q)
            {
                int x = (int)Math.Floor(q.x), y = (int)Math.Floor(q.y);
                return Grid.InBounds(x, y, w, h) && water[Grid.Index(x, y, w)] == 0;
            }
            if (m.airport != null)
            {
                var lane = Airfield.EntranceLane(m.airport, w, h);
                if (lane.Count > 0)
                {
                    int gate = lane[lane.Count - 1];
                    var at = Within(new Vec2(gate % w + 0.5, gate / w + 0.5));
                    if (at != null && Geo.Dist(at.Value, new Vec2(gate % w + 0.5, gate / w + 0.5)) < 0.5 * R && Dry(at.Value)) return at;
                }
            }
            // The waterfront: of the dry tiles on a shore between 0.45 and 0.8 of the way out, the one with the most water within 6.
            const int LOOK = 6;
            var sat = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    sat[(y + 1) * (w + 1) + x + 1] = water[y * w + x] + sat[y * (w + 1) + x + 1] + sat[(y + 1) * (w + 1) + x] - sat[y * (w + 1) + x];
            int best = -1, bestWet = 0;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (water[i] != 0 || (water[i - 1] == 0 && water[i + 1] == 0 && water[i - w] == 0 && water[i + w] == 0)) continue;
                    double d = Numbers.Hypot(x + 0.5 - C.x, y + 0.5 - C.y) / R;
                    if (d < 0.45 || d > 0.8) continue;
                    int x0 = Math.Max(0, x - LOOK), y0 = Math.Max(0, y - LOOK), x1 = Math.Min(w, x + LOOK + 1), y1 = Math.Min(h, y + LOOK + 1);
                    int wet = sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
                    if (wet > bestWet)
                    {
                        bestWet = wet;
                        best = i;
                    }
                }
            // A real bay: at least a quarter of the 13 × 13 round it water.
            if (best < 0 || bestWet < (2 * LOOK + 1) * (2 * LOOK + 1) / 4) return null;
            return new Vec2(best % w + 0.5, best / w + 0.5);
        }
    }
}
