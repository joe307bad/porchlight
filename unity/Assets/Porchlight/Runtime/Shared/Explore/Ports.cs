// Boats and planes in the street scene. The docks (DocksStage) get their piers
// and moored boats, and each lake big enough a loop round its open water,
// clear of the shore, the docks, their boats and the bridges, for boats to
// sail. The airport gets its hangars and tower, a row or two of planes on the
// apron, and cars in its car park. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public static class Ports
    {
        /// <summary>How far (tiles) a sailing loop keeps from the shore, docks and bridges.</summary>
        const int CLEARANCE = 3;
        const int SMOOTHING = 3;
        const double SHORTEST_LOOP = 12;
        /// <summary>Planes on the apron: at least, and at most.</summary>
        const int PLANES_MIN = 8;
        const int PLANES_MAX = 10;

        public static void Add(TownModel model, IsoLayout layout)
        {
            if (model.airport != null) AddAirport(model, layout);
            AddDocks(model, layout);
        }

        // The airport -----------------------------------------------------------------------------

        static void AddAirport(TownModel model, IsoLayout layout)
        {
            var a = model.airport;
            int w = model.width, h = model.height;
            var buildings = new List<Airfield.Box>(Airfield.Hangars(a)) { Airfield.Tower(a) };
            foreach (var b in buildings) foreach (int t in Airfield.TilesIn(a, b, w, h)) layout.use[t] = Use.HANGAR;
            var rng = new Rng($"{model.seed}::planes");
            var spots = Airfield.PlaneSpots(a);
            int count = Math.Min(spots.Count, rng.Int(PLANES_MIN, PLANES_MAX));
            // Every spot in the front row first, then the back's, a few left empty here and there.
            for (int k = 0; k < count; k++)
            {
                var spot = spots[k];
                layout.cars.Add(new ParkedCar { x = spot.at.x, y = spot.at.y, angle = spot.heading, variant = rng.Int(0, 999), vehicle = Catalog.PickPlaneModel(rng) + 1 });
            }
            // Cars nose in along the car park's two edges, clear of the road through it.
            var park = Airfield.CarPark(a);
            var across = Airfield.AlongV(a);
            foreach (var (v, sign) in new[] { (park.v0 + 0.7, -1), (park.v1 - 0.7, 1) })
            {
                for (double u = park.u0 + 0.6; u <= park.u1 - 0.6; u += 0.75)
                {
                    if (Math.Abs(u - a.entranceU) < 2.3 || !rng.Chance(0.45)) continue;
                    var at = Airfield.ToWorld(a, u, v);
                    int model0 = Catalog.PARKED[rng.Int(0, Catalog.PARKED.Length - 1)];
                    layout.cars.Add(new ParkedCar { x = at.x, y = at.y, angle = DetMath.Atan2(across.y * sign, across.x * sign), variant = rng.Int(0, 999), vehicle = model0 + 1 });
                }
            }
        }

        // Docks and lakes -------------------------------------------------------------------------

        /// <summary>The dry tiles the docks start from (Layout keeps sheds and gardens off them).</summary>
        public static HashSet<int> Landings(TownModel model)
        {
            var o = new HashSet<int>();
            foreach (var d in model.docks) o.Add(Grid.Index(d.shore.x, d.shore.y, model.width));
            return o;
        }

        static void AddDocks(TownModel model, IsoLayout layout)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            bool Wet(int i) => Terrain.IsWater(tiles.terrain[i]);
            var rng = new Rng($"{model.seed}::boats");
            foreach (var dock in model.docks)
            {
                foreach (var t in dock.tiles) layout.use[Grid.Index(t.x, t.y, w)] = Use.DOCK;
                // No fence between the yard and its pier.
                layout.fences.RemoveAll(f => f.x == dock.shore.x && f.y == dock.shore.y && f.side == dock.facing);
                var (fx, fy) = Grid.DIRS4[(int)dock.facing];
                double heading = DetMath.Atan2(fy, fx);
                foreach (var m in dock.boats)
                    layout.cars.Add(new ParkedCar { x = m.x, y = m.y, angle = heading, variant = rng.Int(0, 999), vehicle = Catalog.PickBoatModel(rng) + 1 });
            }

            // Loops for boats to sail: round the open water, clear of the shore, the docks, their boats and the bridges.
            var moored = Piers.BoatTiles(model);
            var blocked = new byte[w * h];
            for (int i = 0; i < blocked.Length; i++)
                blocked[i] = !Wet(i) || tiles.overlay[i] != Overlay.NONE || layout.use[i] == Use.DOCK || moored.Contains(i) ? (byte)1 : (byte)0;
            var clear = Raster.DistanceFrom(blocked, w, h);
            var lake = Piers.Lakes(tiles);
            var lakes = new List<List<int>>();
            for (int id = 0; id < lake.count; id++) lakes.Add(new List<int>());
            for (int i = 0; i < w * h; i++) if (lake.of[i] >= 0) lakes[lake.of[i]].Add(i);
            for (int id = 0; id < lakes.Count; id++)
            {
                if (lakes[id].Count < Piers.LAKE_MIN) continue;
                var open = new byte[w * h];
                int count = 0;
                foreach (int t in lakes[id])
                {
                    if (clear[t] < CLEARANCE) continue;
                    open[t] = 1;
                    count++;
                }
                if (count == 0) continue;
                List<Vec2> longest = null;
                double best = 0;
                foreach (var ring in Geo.TraceCellOutlines(open, w, h))
                {
                    double length = Geo.PolylineLength(ring);
                    if (length > best)
                    {
                        best = length;
                        longest = ring;
                    }
                }
                if (longest == null || best < SHORTEST_LOOP) continue;
                var loop = Geo.RemoveCollinear(longest);
                loop.Add(loop[0]);
                loop = Geo.Chaikin(loop, SMOOTHING);
                if (Geo.Dist(loop[0], loop[loop.Count - 1]) > 1e-9) loop.Add(loop[0]);
                layout.boatLoops.Add(loop);
            }
        }
    }
}
