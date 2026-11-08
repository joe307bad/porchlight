// Stage 3: pick the town center and the gates where main roads leave the map.
// The center scores well when the ground is flat, dry, near (not on) a river,
// and not too far from the middle of the map. There's a gate in the middle of
// each edge (Compass), where the highways to the next towns come in.

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
            var wet = BoxMean(i => water[i], w, h, Numbers.RoundInt(townRadius / 2));
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
                    if (water[i] != 0 || !Numbers.IsFinite(cost[i])) continue;
                    if (x < margin || y < margin || x >= w - margin || y >= h - margin) continue;
                    double score = -flat[i] / p.maxRoadSlope - 2 * wet[i] - (2 * Numbers.Hypot(x + 0.5 - w / 2.0, y + 0.5 - h / 2.0)) / half;
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

            // The gates: one in the middle of each edge (Compass), where the highways
            // come in. The terrain stage keeps a dry, level pad behind each.
            var gates = new List<Vec2>();
            foreach (var side in Compass.Sides) gates.Add(Compass.GateCenter(side, w, h));

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
            if (model.gates.Count != 4) errors.Add($"{model.gates.Count} gates, not one on each side");
            var water = Raster.WaterMask(model);
            for (int g = 0; g < model.gates.Count && g < 4; g++)
            {
                var (gx, gy) = Compass.GateTile(Compass.Sides[g], w, h);
                if ((int)Math.Floor(model.gates[g].x) != gx || (int)Math.Floor(model.gates[g].y) != gy) errors.Add($"the {Compass.Name(Compass.Sides[g])} gate isn't in the middle of its edge");
                else if (water[Grid.Index(gx, gy, w)] != 0) errors.Add($"the {Compass.Name(Compass.Sides[g])} gate is in water");
            }
            return errors;
        }
    }
}
