// The areas an editor regenerated (Skeleton.regions, docs/street-editor.md §13): which tiles each covers, and
// the random stream a stage uses there instead of its own.
//
// An area is the blocks picked, as they were outlined when picked, with the streets between them: the outlines
// filled in, then closed (grown and shrunk again by a street's width), so a street between two picked blocks is
// inside it and a street along its edge isn't. The streets stage plans what's inside afresh; the lots and
// buildings stages draw from a stream named by the area's salt there; the plan, buildings and design stages give
// it what was wanted (a park, a school, shops, houses, a roundabout). Everything outside is as it was.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    public static class Regions
    {
        /// <summary>How far (tiles) the picked blocks are grown to take in the streets between them, then shrunk back.</summary>
        public const int CLOSE = 2;

        sealed class Areas
        {
            public int w, h;
            public byte[][] masks;
            /// <summary>Each area's distance (tiles) from each tile.</summary>
            public float[][] near;
            /// <summary>The last area each tile is in, or -1.</summary>
            public int[] owner;
            public Vec2[] middles;
        }

        static Areas Of(TownModel m)
        {
            var s = m.@params?.skeleton;
            if (s == null || s.regions.Count == 0) return null;
            if (s.areas is Areas a && a.w == m.width && a.h == m.height) return a;
            a = Build(s, m.width, m.height);
            s.areas = a;
            return a;
        }

        static Areas Build(Skeleton s, int w, int h)
        {
            var a = new Areas { w = w, h = h, masks = new byte[s.regions.Count][], near = new float[s.regions.Count][], owner = new int[w * h], middles = new Vec2[s.regions.Count] };
            for (int i = 0; i < a.owner.Length; i++) a.owner[i] = -1;
            for (int r = 0; r < s.regions.Count; r++)
            {
                var filled = new byte[w * h];
                foreach (var poly in s.regions[r].blocks)
                    foreach (int t in Parcels.TilesIn(poly, null, w, h)) filled[t] = 1;
                // Closed: grown by CLOSE, then what's within CLOSE of the outside taken off again.
                var grown = new byte[w * h];
                var d = Raster.DistanceFrom(filled, w, h);
                for (int i = 0; i < grown.Length; i++) grown[i] = d[i] <= CLOSE ? (byte)1 : (byte)0;
                var outside = new byte[w * h];
                for (int i = 0; i < outside.Length; i++) outside[i] = grown[i] == 0 ? (byte)1 : (byte)0;
                var d2 = Raster.DistanceFrom(outside, w, h);
                var mask = new byte[w * h];
                double sx = 0, sy = 0;
                int n = 0;
                for (int i = 0; i < mask.Length; i++)
                {
                    int x = i % w, y = i / w;
                    // The map's edge counts as inside: nothing past it to shrink back from.
                    bool keep = filled[i] != 0 || (grown[i] != 0 && d2[i] > CLOSE && x > 0 && y > 0 && x < w - 1 && y < h - 1);
                    if (!keep) continue;
                    mask[i] = 1;
                    a.owner[i] = r;
                    sx += x + 0.5;
                    sy += y + 0.5;
                    n++;
                }
                a.masks[r] = mask;
                a.near[r] = Raster.DistanceFrom(mask, w, h);
                a.middles[r] = n > 0 ? new Vec2(sx / n, sy / n) : new Vec2(w / 2.0, h / 2.0);
            }
            return a;
        }

        public static int Count(TownModel m) => m.@params?.skeleton?.regions.Count ?? 0;

        public static SkeletonRegion Get(TownModel m, int r) => m.@params.skeleton.regions[r];

        /// <summary>The area's tiles (1 inside).</summary>
        public static byte[] Mask(TownModel m, int r) => Of(m).masks[r];

        public static Vec2 Middle(TownModel m, int r) => Of(m).middles[r];

        /// <summary>The last area the tile under `at` is in, or -1.</summary>
        public static int At(TownModel m, Vec2 at)
        {
            var a = Of(m);
            if (a == null) return -1;
            int x = (int)Math.Floor(at.x), y = (int)Math.Floor(at.y);
            return Grid.InBounds(x, y, a.w, a.h) ? a.owner[Grid.Index(x, y, a.w)] : -1;
        }

        /// <summary>What a stage's stream at `at` is named: its own name, or with the salt of the area there.</summary>
        public static string Stream(TownModel m, string stage, Vec2 at)
        {
            int r = At(m, at);
            return r < 0 ? stage : stage + "#" + Get(m, r).salt.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + r.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>In an area an editor wanted houses in and not shops: no shop goes there (not even a corner store).</summary>
        public static bool HousesOnly(TownModel m, Vec2 at)
        {
            int r = At(m, at);
            return r >= 0 && Get(m, r).want.houses && !Get(m, r).want.shops;
        }

        /// <summary>How far (tiles) `at` is from the area: 0 inside.</summary>
        public static double Distance(TownModel m, int r, Vec2 at)
        {
            var a = Of(m);
            int x = (int)Math.Floor(at.x), y = (int)Math.Floor(at.y);
            return Grid.InBounds(x, y, a.w, a.h) ? a.near[r][Grid.Index(x, y, a.w)] : double.PositiveInfinity;
        }

        /// <summary>
        /// A road the area's re-plan clears: mostly inside it, both ends at it (on the roads round its edge, or inside),
        /// so a road that runs on out of it (to the airport, say) stays.
        /// </summary>
        public static bool Clears(TownModel m, int r, List<Vec2> pts) =>
            pts.Count >= 2 && Distance(m, r, pts[0]) <= END_REACH && Distance(m, r, pts[pts.Count - 1]) <= END_REACH && ShareInside(m, r, pts) >= INSIDE;

        /// <summary>Of a road the area clears: the share of it inside, and how far out (tiles) its ends may be (on the roads round the edge).</summary>
        public const double INSIDE = 0.5, END_REACH = 3.5;

        /// <summary>A share (0..1) of the polyline's length inside the area.</summary>
        public static double ShareInside(TownModel m, int r, List<Vec2> pts)
        {
            var mask = Mask(m, r);
            int w = m.width, h = m.height, inside = 0, all = 0;
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                double len = Geo.Dist(pts[k], pts[k + 1]);
                int steps = Math.Max(1, (int)Math.Ceiling(len * 2));
                for (int s = 0; s < steps; s++)
                {
                    var q = Geo.Lerp(pts[k], pts[k + 1], (s + 0.5) / steps);
                    int x = (int)Math.Floor(q.x), y = (int)Math.Floor(q.y);
                    all++;
                    if (Grid.InBounds(x, y, w, h) && mask[Grid.Index(x, y, w)] != 0) inside++;
                }
            }
            return all == 0 ? 0 : inside / (double)all;
        }
    }
}
