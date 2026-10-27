// Stage 1: heightmap from seeded fractal simplex noise, flattened toward the
// map edges so hills aren't cut off, with the water level set as a percentile.
// A port of gen/terrain.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public sealed class TerrainStage : IStage
    {
        const double PERSISTENCE = 0.4;
        /// <summary>Height per unit of noise at REFERENCE_SCALE (relief shrinks with feature size).</summary>
        const double RELIEF = 0.75;
        const double REFERENCE_SCALE = 96;

        static double Smoothstep(double t) => t * t * (3 - 2 * t);

        public string Name => "terrain";

        public TownModel Run(TownModel model, Rng rng)
        {
            int w = model.width, h = model.height;
            var p = model.@params;
            var noise = new SimplexNoise2D(rng.Next);
            var hm = new float[w * h];
            double relief = RELIEF * Math.Min(1, p.noiseScale / REFERENCE_SCALE);
            int octaves = (int)p.noiseOctaves;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double amp = 1;
                    double freq = 1 / p.noiseScale;
                    double sum = 0;
                    double norm = 0;
                    for (int o = 0; o < octaves; o++)
                    {
                        sum += amp * noise.Noise(x * freq, y * freq);
                        norm += amp;
                        amp *= PERSISTENCE;
                        freq *= 2;
                    }
                    hm[y * w + x] = (float)(0.5 + 0.5 * JsMath.Tanh((sum / norm) * relief * 2));
                }
            }

            double band = p.edgeFalloff * Math.Min(w, h);
            if (band > 0)
            {
                double edgeLevel = Percentile(hm, 0.5);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int d = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));
                        if (d >= band) continue;
                        double t = Smoothstep(d / band);
                        int i = y * w + x;
                        hm[i] = (float)Math.Min(1, Math.Max(0, edgeLevel + (hm[i] - edgeLevel) * t));
                    }
                }
            }

            double waterLevel = p.waterFraction > 0 ? Percentile(hm, p.waterFraction) : -1;
            var next = model.With();
            next.heightmap = hm;
            next.waterLevel = waterLevel;
            return next;
        }

        public List<string> Check(TownModel model)
        {
            var errors = new List<string>();
            int water = 0;
            foreach (var v in model.heightmap)
            {
                if (!(v >= 0 && v <= 1))
                {
                    errors.Add($"height {v} outside 0..1");
                    break;
                }
                if (v < model.waterLevel) water++;
            }
            double fraction = (double)water / model.heightmap.Length;
            if (Math.Abs(fraction - model.@params.waterFraction) > 0.02)
                errors.Add($"water fraction {Js.ToFixed(fraction, 3)} far from target {model.@params.waterFraction}");
            return errors;
        }

        /// <summary>Value below which q of the cells fall.</summary>
        public static double Percentile(float[] values, double q)
        {
            var sorted = (float[])values.Clone();
            Js.SortNumeric(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];
        }
    }
}
