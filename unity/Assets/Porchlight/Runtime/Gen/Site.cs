// Stage 3: pick the town center and the gates where main roads leave the map.
// The center scores well when the ground is flat, dry, near (not on) a river,
// and not too far from the middle of the map. Gates are spread around the map
// edge on dry tiles a road can reach from the center. A port of gen/site.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class SiteStage : IStage
    {
        const int FLAT_RADIUS = 6;
        const double IDEAL_RIVER_DISTANCE = 6;

        public string Name => "site";

        /// <summary>Summed-area table for fast box means.</summary>
        static float[] BoxMean(Func<int, double> field, int w, int h, int r)
        {
            var sat = new double[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                double row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += field(y * w + x);
                    sat[(y + 1) * (w + 1) + x + 1] = sat[y * (w + 1) + x + 1] + row;
                }
            }
            var o = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r);
                    int y0 = Math.Max(0, y - r);
                    int x1 = Math.Min(w, x + r + 1);
                    int y1 = Math.Min(h, y + r + 1);
                    double sum = sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
                    o[y * w + x] = (float)(sum / ((x1 - x0) * (y1 - y0)));
                }
            }
            return o;
        }

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var t = Raster.RoadTerrain(model);
            var water = t.water;
            var slope = t.slope;
            var cost = t.cost;
            double townRadius = p.townRadius * Math.Min(w, h);

            var flat = BoxMean(i => slope[i], w, h, FLAT_RADIUS);
            var wet = BoxMean(i => water[i], w, h, Js.RoundInt(townRadius / 2));
            var rivers = Raster.RiverMask(model);
            var riverDist = model.rivers.Count > 0 ? Raster.DistanceFrom(rivers, w, h) : null;
            double margin = Math.Min(townRadius * 0.6, Math.Min(w, h) / 4.0);
            double half = Math.Min(w, h) / 2.0;

            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = Grid.Index(x, y, w);
                    double jitter = rng.Next() * 0.15; // drawn for every cell so the stream stays aligned
                    if (water[i] != 0 || !Js.IsFinite(cost[i])) continue;
                    if (x < margin || y < margin || x >= w - margin || y >= h - margin) continue;
                    double score = -flat[i] / p.maxRoadSlope - 2 * wet[i] - (2 * Js.Hypot(x + 0.5 - w / 2.0, y + 0.5 - h / 2.0)) / half;
                    if (riverDist != null) score -= Math.Min(1, Math.Abs(riverDist[i] - IDEAL_RIVER_DISTANCE) / 12);
                    score += jitter;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
            }
            if (best < 0) best = Grid.Index(w / 2, h / 2, w);
            var center = new Vec2((best % w) + 0.5, best / w + 0.5);

            var reach = new byte[w * h];
            var queue = new List<int> { best };
            reach[best] = 1;
            for (int head = 0; head < queue.Count; head++)
            {
                int c = queue[head];
                int cx = c % w;
                int cy = (c - cx) / w;
                foreach (var (dx, dy) in Grid.DIRS8)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = Grid.Index(nx, ny, w);
                    if (reach[j] == 0 && Js.IsFinite(cost[j]))
                    {
                        reach[j] = 1;
                        queue.Add(j);
                    }
                }
            }
            var edgeCells = new List<int>();
            for (int x = 0; x < w; x++)
            {
                edgeCells.Add(Grid.Index(x, 0, w));
                edgeCells.Add(Grid.Index(x, h - 1, w));
            }
            for (int y = 1; y < h - 1; y++)
            {
                edgeCells.Add(Grid.Index(0, y, w));
                edgeCells.Add(Grid.Index(w - 1, y, w));
            }
            var open = new List<int>();
            foreach (int c in edgeCells) if (reach[c] != 0 && water[c] == 0) open.Add(c);

            var gates = new List<Vec2>();
            double minSpacing = (2.0 * (w + h)) / (p.gateCount * 2.5);
            double baseAngle = rng.Range(0, Math.PI * 2);
            for (int g = 0; g < p.gateCount && open.Count > 0; g++)
            {
                double angle = baseAngle + (g * Math.PI * 2) / p.gateCount + rng.Range(-0.3, 0.3);
                var ideal = RayToEdge(center, angle, w, h);
                int pick = -1;
                double pickD = double.PositiveInfinity;
                foreach (int c in open)
                {
                    var pt = new Vec2((c % w) + 0.5, c / w + 0.5);
                    bool tooClose = false;
                    foreach (var q in gates)
                    {
                        if (Js.Hypot(q.x - pt.x, q.y - pt.y) < minSpacing)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) continue;
                    double d = Js.Hypot(pt.x - ideal.x, pt.y - ideal.y);
                    if (d < pickD)
                    {
                        pickD = d;
                        pick = c;
                    }
                }
                if (pick >= 0) gates.Add(new Vec2((pick % w) + 0.5, pick / w + 0.5));
            }

            var next = model.With();
            next.center = center;
            next.townRadius = townRadius;
            next.gates = gates;
            next.fields = new DebugFields { flow = model.fields.flow, roadCost = cost };
            return next;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            int w = model.width, h = model.height;
            int cx = (int)Math.Floor(model.center.x);
            int cy = (int)Math.Floor(model.center.y);
            if (model.heightmap[Grid.Index(cx, cy, w)] < model.waterLevel) errors.Add("town center is in water");
            if (model.gates.Count < 1) errors.Add("no gate could be placed: no map edge is reachable by road");
            foreach (var g in model.gates)
            {
                int gx = (int)Math.Floor(g.x), gy = (int)Math.Floor(g.y);
                if (gx != 0 && gy != 0 && gx != w - 1 && gy != h - 1) errors.Add($"gate {gx},{gy} is not on the map edge");
            }
            return errors;
        }

        static Vec2 RayToEdge(Vec2 from, double angle, int w, int h)
        {
            double dx = Math.Cos(angle);
            double dy = Math.Sin(angle);
            double t = double.PositiveInfinity;
            if (dx > 1e-9) t = Math.Min(t, (w - from.x) / dx);
            if (dx < -1e-9) t = Math.Min(t, -from.x / dx);
            if (dy > 1e-9) t = Math.Min(t, (h - from.y) / dy);
            if (dy < -1e-9) t = Math.Min(t, -from.y / dy);
            return new Vec2(from.x + dx * t, from.y + dy * t);
        }
    }
}
