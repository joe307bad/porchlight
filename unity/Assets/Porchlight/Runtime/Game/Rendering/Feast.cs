// Thanksgiving dinner on a house's dining table (the `feast` unlock, earned by
// the turkey mission): the roast turkey on its platter in the middle, long way
// along the table, and the bowls (mash, cranberry, green beans, stuffing) and
// the pumpkin pie round it. Sized to the table, so it fits a round one as well
// as a long one.

using System;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Feast
    {
        /// <summary>The dining tables a house's room is furnished with (Interiors.FurnishRoom's default room).</summary>
        static readonly string[] TABLES = { "table", "tableRound", "tableCloth", "tableCross", "tableCrossCloth", "tableGlass" };

        public static bool IsDiningTable(string furniture) => Array.IndexOf(TABLES, furniture) >= 0;

        /// <summary>The platter's length, a bowl's and the pie's width, as shares of the table's shorter side.</summary>
        const double PLATTER = 0.46, BOWL = 0.17, PIE = 0.22;

        /// <summary>
        /// The feast laid on a table (the piece of furniture, as drawn, under parent); hidden until shown.
        /// Measured from the table's own meshes, not its model's recorded size: a model whose file scales it
        /// (the cross tables are 0.42 of their meshes) is drawn smaller than that, and the dishes floated over it.
        /// </summary>
        public static GameObject Lay(ModelLibrary library, Transform parent, GameObject table)
        {
            var root = new GameObject("feast");
            root.transform.SetParent(parent, false);
            var drawn = Props.MeshBox(table, parent).Value;
            // Map terms (Coords.Map: map y is -z).
            double x0 = drawn.min.x, x1 = drawn.max.x, y0 = -drawn.max.z, y1 = -drawn.min.z;
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            double wx = x1 - x0, wy = y1 - y0;
            double small = Math.Min(wx, wy);
            double top = drawn.max.y;
            // Long way along the table's longer side.
            double along = wy >= wx ? 0 : Math.PI / 2;
            void Put(string name, double x, double y, double rot, double across)
            {
                var m = library.Get(name);
                double s = across / Math.Max(m.size.x, m.size.z);
                Props.Place(m, root.transform, x, y, rot, s, s, s, top, castShadows: false);
            }
            Put(Catalog.FEAST[0], cx, cy, along, small * PLATTER);
            // The rest round it, on an ellipse inside the table's edge (a round table's too).
            int n = Catalog.FEAST.Length - 1;
            for (int k = 0; k < n; k++)
            {
                double a = (k + 0.5) / n * Math.PI * 2 + along;
                string name = Catalog.FEAST[k + 1];
                double across = small * (name.EndsWith("pie") ? PIE : BOWL);
                double rx = wx / 2 - across * 0.62 - small * 0.05, ry = wy / 2 - across * 0.62 - small * 0.05;
                Put(name, cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry, a, across);
            }
            root.SetActive(false);
            return root;
        }
    }
}
