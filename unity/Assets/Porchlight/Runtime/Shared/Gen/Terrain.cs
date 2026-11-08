// Stage 1: heightmap from seeded fractal simplex noise, flattened toward the
// map edges so hills aren't cut off, with the water level set as a percentile.

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
                    hm[y * w + x] = (float)(0.5 + 0.5 * DetMath.Tanh((sum / norm) * relief * 2));
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

            GatePads(hm, w, h, p.waterFraction > 0 ? Percentile(hm, p.waterFraction) : -1, p.maxRoadSlope);
            double waterLevel = p.waterFraction > 0 ? Percentile(hm, p.waterFraction) : -1;
            var next = model.With();
            next.heightmap = hm;
            next.waterLevel = waterLevel;
            return next;
        }

        /// <summary>How far in from its edge a gate's pad reaches (tiles), at least.</summary>
        const int PAD_DEPTH = 10;
        /// <summary>Half the pad's width.</summary>
        const int PAD_HALF = 3;
        /// <summary>How far beyond the pad the ground eases back to what it was (tiles): at least this, more where it's far off.</summary>
        const int PAD_FADE = 8;
        const int PAD_FADE_MAX = 28;
        /// <summary>How far over the water the pad sits.</summary>
        const double PAD_FREEBOARD = 0.03;

        /// <summary>
        /// Level, dry ground behind each gate (Compass), so a gate is never in a lake or on
        /// a slope too steep for its road. The pad sits at the ground's own mean height
        /// there (or just over the water), and eases back slowly enough that the slope
        /// stays well under what a road can climb.
        /// </summary>
        static void GatePads(float[] hm, int w, int h, double waterLevel, double maxRoadSlope)
        {
            int depth = Math.Max(PAD_DEPTH, Math.Min(w, h) / 10);
            var original = (float[])hm.Clone();
            foreach (var side in Compass.Sides)
            {
                var (gx, gy) = Compass.GateTile(side, w, h);
                var o = Compass.Outward(side);
                int ix = -(int)o.x, iy = -(int)o.y; // inward
                double Off(int x, int y)
                {
                    int dx = x - gx, dy = y - gy;
                    int along = dx * ix + dy * iy;
                    if (along < 0) return double.PositiveInfinity;
                    int across = Math.Abs(dx * iy - dy * ix);
                    return Math.Max(0, across - PAD_HALF) + Math.Max(0, along - depth);
                }
                int reach = depth + PAD_FADE_MAX;
                int x0 = Math.Max(0, gx - reach), x1 = Math.Min(w - 1, gx + reach);
                int y0 = Math.Max(0, gy - reach), y1 = Math.Min(h - 1, gy + reach);
                double sum = 0;
                int count = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if (Off(x, y) == 0)
                        {
                            sum += original[y * w + x];
                            count++;
                        }
                double level = Math.Min(1, Math.Max(count > 0 ? sum / count : 0.5, waterLevel + PAD_FREEBOARD));
                // Ease back over enough tiles that the steepest step (smoothstep's is 1.5x the mean) stays at half a road's limit.
                double far = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if (Off(x, y) <= PAD_FADE_MAX) far = Math.Max(far, Math.Abs(original[y * w + x] - level));
                int fade = Math.Max(PAD_FADE, Math.Min(PAD_FADE_MAX, (int)Math.Ceiling(1.5 * far / (0.5 * maxRoadSlope))));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double off = Off(x, y);
                        if (off >= fade) continue;
                        double t = 1 - Smoothstep(off / fade);
                        int i = y * w + x;
                        hm[i] = (float)(hm[i] + (level - hm[i]) * t);
                    }
                }
            }
        }

        /// <summary>True within r tiles (either way) of a gate's tile.</summary>
        public static bool NearGate(int x, int y, int w, int h, int r)
        {
            foreach (var side in Compass.Sides)
            {
                var (gx, gy) = Compass.GateTile(side, w, h);
                if (Math.Abs(x - gx) <= r && Math.Abs(y - gy) <= r) return true;
            }
            return false;
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
                errors.Add($"water fraction {Numbers.ToFixed(fraction, 3)} far from target {model.@params.waterFraction}");
            return errors;
        }

        /// <summary>Value below which q of the cells fall.</summary>
        public static double Percentile(float[] values, double q)
        {
            var sorted = (float[])values.Clone();
            Numbers.SortNumeric(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];
        }
    }
}
