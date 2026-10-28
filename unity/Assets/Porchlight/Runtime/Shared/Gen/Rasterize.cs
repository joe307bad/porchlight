// Stage 8: paint everything into the tile grid the 3D view uses. Terrain comes
// from the heightmap and water, then the airport, roads and bridges, the
// plaza, and buildings. A tile is walkable if a character could stand on it and reach it
// from spawn.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class RasterizeStage : IStage
    {
        const double DEEP_WATER = 0.04;
        const double DEEP_RIVER_WIDTH = 2.5;
        const double BEACH_HEIGHT = 0.015;

        public string Name => "rasterize";

        static bool WalkableTerrain(byte t) => t == Terrain.GRASS || t == Terrain.DIRT || t == Terrain.SAND;

        static bool WalkableOverlay(byte o) =>
            o == Overlay.ROAD_MAIN || o == Overlay.ROAD_STREET || o == Overlay.PATH || o == Overlay.BRIDGE || o == Overlay.PLAZA || o == Overlay.AIRPORT || o == Overlay.RUNWAY;

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var hm = model.heightmap;
            double waterLevel = model.waterLevel;
            var p = model.@params;
            var tiles = TileGrid.Create(w, h);
            var lake = Raster.LakeMask(model);
            var river = Raster.RiverMask(model);
            var water = new byte[w * h];
            for (int i = 0; i < water.Length; i++) water[i] = (byte)(lake[i] | river[i]);
            var rock = Raster.RockMask(Raster.SlopeField(model, water), p);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    if (lake[i] != 0) tiles.terrain[i] = hm[i] < waterLevel - DEEP_WATER ? Terrain.WATER_DEEP : Terrain.WATER_SHALLOW;
                    else if (river[i] != 0) tiles.terrain[i] = Terrain.WATER_SHALLOW;
                    else if (rock[i] != 0) tiles.terrain[i] = Terrain.ROCK;
                    else if (hm[i] < waterLevel + BEACH_HEIGHT && BesideWater(x, y, w, h, water)) tiles.terrain[i] = Terrain.SAND;
                    else tiles.terrain[i] = Terrain.GRASS;
                }
            }
            foreach (var r in model.rivers)
            {
                for (int k = 0; k < r.points.Count; k++)
                {
                    if (r.widthAt[k] < DEEP_RIVER_WIDTH) continue;
                    int x = (int)Math.Floor(r.points[k].x), y = (int)Math.Floor(r.points[k].y);
                    if (Grid.InBounds(x, y, w, h)) tiles.terrain[Grid.Index(x, y, w)] = Terrain.WATER_DEEP;
                }
            }

            // The airport, under its road in.
            if (model.airport != null)
            {
                foreach (int i in Airfield.TilesIn(model.airport, new Airfield.Box(0, 0, Airfield.Length(model.airport), Airfield.DEPTH), w, h))
                {
                    tiles.terrain[i] = Terrain.GRASS;
                    tiles.overlay[i] = Overlay.AIRPORT;
                }
                foreach (int i in Airfield.TilesIn(model.airport, Airfield.Runway(model.airport), w, h)) tiles.overlay[i] = Overlay.RUNWAY;
            }

            var road = Raster.RoadRaster(model);
            var spans = new List<(int edge, double lo, double hi)>();
            foreach (var b in model.bridges)
            {
                var pts = model.roads.edges[b.roadEdgeId].points;
                double a = Geo.ArcPosition(b.from, pts);
                double c = Geo.ArcPosition(b.to, pts);
                spans.Add((b.roadEdgeId, Math.Min(a, c), Math.Max(a, c)));
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    if (road.kind[i] == 0) continue;
                    if (water[i] == 0)
                    {
                        tiles.overlay[i] = road.kind[i] == Raster.ROAD_MAIN ? Overlay.ROAD_MAIN : road.kind[i] == Raster.ROAD_STREET ? Overlay.ROAD_STREET : Overlay.PATH;
                        if (road.kind[i] == Raster.ROAD_PATH) tiles.terrain[i] = Terrain.DIRT;
                        continue;
                    }
                    var edge = model.roads.edges[road.edgeId[i]];
                    var c = new Vec2(x + 0.5, y + 0.5);
                    double at = Geo.ArcPosition(c, edge.points);
                    bool near = Geo.ClosestOnPolyline(c, edge.points).d < Raster.RoadHalfWidth(edge.kind, p);
                    if (!near) continue;
                    foreach (var s in spans)
                    {
                        if (s.edge == edge.id && at >= s.lo - 0.5 && at <= s.hi + 0.5)
                        {
                            tiles.overlay[i] = Overlay.BRIDGE;
                            break;
                        }
                    }
                }
            }

            foreach (var lm in model.landmarks)
            {
                if (lm.kind != LandmarkKind.Plaza) continue;
                foreach (int lotId in lm.lotIds)
                    foreach (int t in Parcels.TilesIn(model.lots[lotId].polygon, null, w, h))
                        tiles.overlay[t] = Overlay.PLAZA;
            }

            foreach (var b in model.buildings) foreach (var t in b.tiles) tiles.buildingId[Grid.Index(t.x, t.y, w)] = b.id;

            ComputeWalkable(tiles);
            int spawnTile = PickSpawn(model, tiles);
            PruneUnreachable(tiles, spawnTile);
            var next = model.With();
            next.tiles = tiles;
            next.spawn = new Vec2((spawnTile % w) + 0.5, spawnTile / w + 0.5);
            return next;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            int w = model.width, h = model.height;
            var t = model.tiles;
            int n = w * h;
            int sx = (int)Math.Floor(model.spawn.x), sy = (int)Math.Floor(model.spawn.y);
            if (!Grid.InBounds(sx, sy, w, h))
            {
                errors.Add("spawn is outside the map");
                return errors;
            }
            int start = Grid.Index(sx, sy, w);
            if (t.walkable[start] == 0) errors.Add("spawn is not walkable");
            var seen = Flood(t, start);
            for (int i = 0; i < n; i++)
            {
                if (t.walkable[i] != 0 && seen[i] == 0)
                {
                    errors.Add($"walkable tile {i % w},{i / w} can't be reached from spawn");
                    break;
                }
            }
            foreach (var b in model.buildings)
            {
                foreach (var p in b.tiles)
                {
                    int i = Grid.Index(p.x, p.y, w);
                    if (t.buildingId[i] != b.id) errors.Add($"tile {p.x},{p.y} should belong to building {b.id}");
                    if (t.walkable[i] != 0) errors.Add($"building tile {p.x},{p.y} is walkable");
                }
            }
            foreach (var e in t.elevation) if (e != 0) { errors.Add("the tile grid should be flat (elevation 0)"); break; }
            int buildingTiles = 0;
            for (int i = 0; i < n; i++) if (t.buildingId[i] >= 0) buildingTiles++;
            int expected = 0;
            foreach (var b in model.buildings) expected += b.tiles.Count;
            if (buildingTiles != expected) errors.Add($"{buildingTiles} building tiles, expected {expected}");
            foreach (var e in model.roads.edges)
            {
                foreach (var p in e.points)
                {
                    int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y);
                    if (!Grid.InBounds(x, y, w, h)) continue;
                    int i = Grid.Index(x, y, w);
                    bool wet = Terrain.IsWater(t.terrain[i]);
                    if (!wet && t.overlay[i] == Overlay.NONE) errors.Add($"road {e.id} vertex at {x},{y} has no road tile");
                }
            }
            if (errors.Count > 5) errors.RemoveRange(5, errors.Count - 5);
            return errors;
        }

        static bool BesideWater(int x, int y, int w, int h, byte[] water)
        {
            foreach (var (dx, dy) in Grid.DIRS4)
            {
                int nx = x + dx, ny = y + dy;
                if (Grid.InBounds(nx, ny, w, h) && water[Grid.Index(nx, ny, w)] != 0) return true;
            }
            return false;
        }

        public static void ComputeWalkable(TileGrid tiles)
        {
            for (int i = 0; i < tiles.walkable.Length; i++)
            {
                bool onOverlay = WalkableOverlay(tiles.overlay[i]);
                bool onGround = WalkableTerrain(tiles.terrain[i]);
                tiles.walkable[i] = tiles.buildingId[i] == -1 && (onOverlay || onGround) ? (byte)1 : (byte)0;
            }
        }

        static int PickSpawn(TownModel model, TileGrid tiles)
        {
            int w = model.width, h = model.height;
            int start = Grid.Index((int)Math.Floor(model.center.x), (int)Math.Floor(model.center.y), w);
            Func<int, bool> good = i => tiles.walkable[i] == 1 && tiles.overlay[i] != Overlay.NONE;
            Func<int, bool> any = i => tiles.walkable[i] == 1;
            foreach (var accept in new[] { good, any })
            {
                var seen = new byte[w * h];
                var queue = new List<int> { start };
                seen[start] = 1;
                for (int head = 0; head < queue.Count; head++)
                {
                    int i = queue[head];
                    if (accept(i)) return i;
                    int x = i % w;
                    int y = (i - x) / w;
                    foreach (var (dx, dy) in Grid.DIRS4)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (!Grid.InBounds(nx, ny, w, h)) continue;
                        int j = Grid.Index(nx, ny, w);
                        if (seen[j] == 0)
                        {
                            seen[j] = 1;
                            queue.Add(j);
                        }
                    }
                }
            }
            return start;
        }

        static byte[] Flood(TileGrid tiles, int start)
        {
            int w = tiles.width, h = tiles.height;
            var seen = new byte[w * h];
            if (tiles.walkable[start] == 0) return seen;
            var queue = new List<int> { start };
            seen[start] = 1;
            for (int head = 0; head < queue.Count; head++)
            {
                int i = queue[head];
                int x = i % w;
                int y = (i - x) / w;
                foreach (var (dx, dy) in Grid.DIRS4)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!Grid.InBounds(nx, ny, w, h)) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (seen[j] == 0 && tiles.walkable[j] != 0)
                    {
                        seen[j] = 1;
                        queue.Add(j);
                    }
                }
            }
            return seen;
        }

        static void PruneUnreachable(TileGrid tiles, int spawn)
        {
            var seen = Flood(tiles, spawn);
            for (int i = 0; i < seen.Length; i++) if (seen[i] == 0) tiles.walkable[i] = 0;
        }
    }
}
