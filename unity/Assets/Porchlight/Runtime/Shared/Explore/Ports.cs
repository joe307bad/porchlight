// Boats and planes in the street scene. Each lake big enough gets a dock (two
// on a big one): a pier out from the shore near a road, with boats moored
// along it, and a loop round its open water, clear of the shore, the docks and
// the bridges, for boats to sail. The airport gets its hangars and tower, a
// row or two of planes on the apron, and cars in its car park. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public sealed class Dock
    {
        /// <summary>The pier's tiles, from the shore out.</summary>
        public List<TilePos> tiles;
        /// <summary>Which way it runs out over the water.</summary>
        public Facing facing;
    }

    public static class Ports
    {
        /// <summary>Water tiles a lake needs for a dock and boats.</summary>
        const int LAKE_MIN = 60;
        /// <summary>A lake this big gets a second dock.</summary>
        const int BIG_LAKE = 400;
        /// <summary>How far apart two docks on one lake are, at least (tiles).</summary>
        const int DOCK_SPACING = 14;
        /// <summary>A pier's length (tiles).</summary>
        public const int PIER = 3;
        /// <summary>Furthest from a road (walking over land, tiles) a dock goes.</summary>
        const int ROAD_REACH = 12;
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

        static void AddDocks(TownModel model, IsoLayout layout)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            bool Wet(int i) => Terrain.IsWater(tiles.terrain[i]);
            var lake = Components(w, h, Wet);
            var fromRoad = RoadDistance(model, layout);
            var moorings = new HashSet<int>();
            var rng = new Rng($"{model.seed}::docks");
            var lakes = new List<List<int>>();
            for (int id = 0; id < lake.count; id++) lakes.Add(new List<int>());
            for (int i = 0; i < w * h; i++) if (lake.of[i] >= 0) lakes[lake.of[i]].Add(i);

            for (int id = 0; id < lakes.Count; id++)
            {
                if (lakes[id].Count < LAKE_MIN) continue;
                var placed = new List<int>();
                int wanted = lakes[id].Count >= BIG_LAKE ? 2 : 1;
                for (int n = 0; n < wanted; n++)
                {
                    int bestShore = -1, bestDir = -1;
                    double bestScore = double.PositiveInfinity;
                    foreach (int t in lakes[id])
                    {
                        int tx = t % w, ty = t / w;
                        for (int d = 0; d < 4; d++)
                        {
                            var (dx, dy) = Grid.DIRS4[d];
                            int sx = tx - dx, sy = ty - dy;
                            if (!Grid.InBounds(sx, sy, w, h)) continue;
                            int shore = Grid.Index(sx, sy, w);
                            if (fromRoad[shore] > ROAD_REACH || !PierFits(model, layout, lake.of, id, sx, sy, dx, dy)) continue;
                            bool crowded = false;
                            foreach (int q in placed) if (Math.Abs(q % w - sx) + Math.Abs(q / w - sy) < DOCK_SPACING) crowded = true;
                            if (crowded) continue;
                            double score = fromRoad[shore] + Layout.Hash(shore, d) * 3;
                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestShore = shore;
                                bestDir = d;
                            }
                        }
                    }
                    if (bestShore < 0) break;
                    placed.Add(bestShore);
                    var (fx, fy) = Grid.DIRS4[bestDir];
                    var dock = new Dock { tiles = new List<TilePos>(), facing = (Facing)bestDir };
                    int bx = bestShore % w, by = bestShore / w;
                    for (int k = 1; k <= PIER; k++)
                    {
                        dock.tiles.Add(new TilePos(bx + fx * k, by + fy * k));
                        layout.use[Grid.Index(bx + fx * k, by + fy * k, w)] = Use.DOCK;
                    }
                    layout.docks.Add(dock);
                    // Boats moored either side, bows out to the water: two at least.
                    double heading = DetMath.Atan2(fy, fx);
                    int moored = 0;
                    for (int k = PIER; k >= 1; k--)
                    {
                        foreach (int side in new[] { 1, -1 })
                        {
                            if (!rng.Chance(0.55) && moored >= 2) continue;
                            if (moored >= 2 && k == 1) continue;
                            int mx = bx + fx * k - fy * side, my = by + fy * k + fx * side;
                            moorings.Add(Grid.Index(mx, my, w));
                            layout.cars.Add(new ParkedCar { x = mx + 0.5, y = my + 0.5, angle = heading, variant = rng.Int(0, 999), vehicle = Catalog.PickBoatModel(rng) + 1 });
                            moored++;
                        }
                    }
                }
            }

            // Loops for boats to sail: round the open water, clear of the shore, the docks, their moorings and the bridges.
            var blocked = new byte[w * h];
            for (int i = 0; i < blocked.Length; i++)
                blocked[i] = !Wet(i) || tiles.overlay[i] != Overlay.NONE || layout.use[i] == Use.DOCK || moorings.Contains(i) ? (byte)1 : (byte)0;
            var clear = Raster.DistanceFrom(blocked, w, h);
            for (int id = 0; id < lakes.Count; id++)
            {
                if (lakes[id].Count < LAKE_MIN) continue;
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

        /// <summary>A pier from shore tile (sx, sy) out along (dx, dy): its tiles open water of this lake, room to moor a boat either side, and water off its end.</summary>
        static bool PierFits(TownModel model, IsoLayout layout, int[] lakeOf, int lake, int sx, int sy, int dx, int dy)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            int shore = Grid.Index(sx, sy, w);
            byte use = layout.use[shore];
            bool land = !Terrain.IsWater(tiles.terrain[shore]) && tiles.walkable[shore] != 0 && tiles.overlay[shore] == Overlay.NONE;
            if (!land || !(use == Use.NONE || use == Use.GREEN || use == Use.YARD)) return false;
            bool Water(int x, int y)
            {
                if (!Grid.InBounds(x, y, w, h)) return false;
                int i = Grid.Index(x, y, w);
                return lakeOf[i] == lake && tiles.overlay[i] == Overlay.NONE && layout.use[i] == Use.NONE;
            }
            for (int k = 1; k <= PIER + 1; k++)
            {
                int x = sx + dx * k, y = sy + dy * k;
                if (!Water(x, y)) return false;
                if (k <= PIER && (!Water(x - dy, y + dx) || !Water(x + dy, y - dx))) return false;
            }
            return true;
        }

        /// <summary>4-connected components of the tiles `inside` holds: each tile's component (or -1), and how many.</summary>
        static (int[] of, int count) Components(int w, int h, Func<int, bool> inside)
        {
            var of = new int[w * h];
            for (int i = 0; i < of.Length; i++) of[i] = -1;
            int count = 0;
            for (int i = 0; i < of.Length; i++)
            {
                if (of[i] >= 0 || !inside(i)) continue;
                var queue = new List<int> { i };
                of[i] = count;
                for (int head = 0; head < queue.Count; head++)
                {
                    int c = queue[head];
                    int cx = c % w, cy = c / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (!Grid.InBounds(nx, ny, w, h)) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (of[j] < 0 && inside(j))
                        {
                            of[j] = count;
                            queue.Add(j);
                        }
                    }
                }
                count++;
            }
            return (of, count);
        }

        /// <summary>Steps over dry land (4-connected) from the nearest road, street or path; Infinity where none reaches.</summary>
        static float[] RoadDistance(TownModel model, IsoLayout layout)
        {
            int w = model.width, h = model.height;
            var tiles = model.tiles;
            var d = new float[w * h];
            var queue = new List<int>();
            for (int i = 0; i < d.Length; i++)
            {
                byte o = tiles.overlay[i];
                bool road = o == Overlay.ROAD_MAIN || o == Overlay.ROAD_STREET || o == Overlay.PATH;
                d[i] = road ? 0 : float.PositiveInfinity;
                if (road) queue.Add(i);
            }
            for (int head = 0; head < queue.Count; head++)
            {
                int c = queue[head];
                int cx = c % w, cy = c / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (!Grid.InBounds(nx, ny, w, h)) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (!float.IsPositiveInfinity(d[j]) || Terrain.IsWater(tiles.terrain[j]) || layout.use[j] == Use.HOUSE) continue;
                    d[j] = d[c] + 1;
                    queue.Add(j);
                }
            }
            return d;
        }
    }
}
