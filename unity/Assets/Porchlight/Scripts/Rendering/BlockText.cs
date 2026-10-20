using System.Collections.Generic;
using UnityEngine;

namespace Porchlight.Rendering
{
    public enum TextAlign
    {
        Center,
        Left,
        Right,
    }

    /// <summary>
    /// Chunky monospace lettering made of little blocks. Each letter is drawn as strokes on a 4 x 6
    /// grid (chamfered corners, real diagonals), and the strokes are filled in with blocks on a finer
    /// grid, so diagonals become small even steps rather than big pixels. Text lies flat on the XZ
    /// plane, reading along +X with the top of each letter towards +Z, so it can sit on the ground in
    /// perspective (or, with <see cref="AddStanding"/>, stand up on a wall). Only the characters the
    /// menus and house numbers use are defined.
    /// </summary>
    public static class BlockText
    {
        /// <summary>Letters are 5 units wide (strokes are 1 unit thick) and 7 tall, with a 1 unit gap.</summary>
        public const int Advance = 6;

        private const int GlyphUnitsWide = 5;
        private const int GlyphUnitsTall = 7;
        private const float HalfStroke = 0.5f;

        private static readonly Dictionary<char, float[][]> Strokes = new Dictionary<char, float[][]>
        {
            ['A'] = S(P(0, 0, 0, 4.5f, 1.5f, 6, 2.5f, 6, 4, 4.5f, 4, 0), P(0, 3, 4, 3)),
            ['B'] = S(P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3), P(3, 3, 4, 2, 4, 1, 3, 0, 0, 0)),
            ['C'] = S(P(4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0, 3, 0, 4, 1)),
            ['D'] = S(P(0, 0, 0, 6, 2.5f, 6, 4, 4.5f, 4, 1.5f, 2.5f, 0, 0, 0)),
            ['E'] = S(P(4, 6, 0, 6, 0, 0, 4, 0), P(0, 3, 3, 3)),
            ['F'] = S(P(4, 6, 0, 6, 0, 0), P(0, 3, 3, 3)),
            ['G'] = S(P(4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0, 3, 0, 4, 1, 4, 3, 2, 3)),
            ['H'] = S(P(0, 0, 0, 6), P(4, 0, 4, 6), P(0, 3, 4, 3)),
            ['I'] = S(P(0.5f, 6, 3.5f, 6), P(2, 6, 2, 0), P(0.5f, 0, 3.5f, 0)),
            ['J'] = S(P(1.5f, 6, 4, 6), P(3, 6, 3, 1, 2, 0, 1, 0, 0, 1)),
            ['K'] = S(P(0, 0, 0, 6), P(4, 6, 0, 2), P(1.5f, 3.5f, 4, 0)),
            ['L'] = S(P(0, 6, 0, 0, 4, 0)),
            ['M'] = S(P(0, 0, 0, 6, 2, 3, 4, 6, 4, 0)),
            ['N'] = S(P(0, 0, 0, 6, 4, 0, 4, 6)),
            ['O'] = S(P(1, 0, 3, 0, 4, 1, 4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0)),
            ['P'] = S(P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3)),
            ['Q'] = S(P(1, 0, 3, 0, 4, 1, 4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0), P(2.5f, 1.5f, 4, 0)),
            ['R'] = S(P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3), P(2, 3, 4, 0)),
            ['S'] = S(P(4, 5, 3, 6, 1, 6, 0, 5, 0, 4, 1, 3, 3, 3, 4, 2, 4, 1, 3, 0, 1, 0, 0, 1)),
            ['T'] = S(P(0, 6, 4, 6), P(2, 6, 2, 0)),
            ['U'] = S(P(0, 6, 0, 1, 1, 0, 3, 0, 4, 1, 4, 6)),
            ['V'] = S(P(0, 6, 2, 0, 4, 6)),
            ['W'] = S(P(0, 6, 0, 0, 2, 3, 4, 0, 4, 6)),
            ['X'] = S(P(0, 6, 4, 0), P(4, 6, 0, 0)),
            ['Y'] = S(P(0, 6, 2, 3, 4, 6), P(2, 3, 2, 0)),
            ['Z'] = S(P(0, 6, 4, 6, 0, 0, 4, 0)),
            ['0'] = S(P(1, 0, 3, 0, 4, 1, 4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0), P(1, 1.5f, 3, 4.5f)),
            ['1'] = S(P(0.5f, 4.5f, 2, 6, 2, 0), P(0.5f, 0, 3.5f, 0)),
            ['2'] = S(P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 0, 0, 4, 0)),
            ['3'] = S(P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 3, 3, 1.5f, 3), P(3, 3, 4, 2, 4, 1, 3, 0, 1, 0, 0, 1)),
            ['4'] = S(P(3, 0, 3, 6, 0, 2, 4, 2)),
            ['5'] = S(P(4, 6, 0, 6, 0, 3.5f, 3, 3.5f, 4, 2.5f, 4, 1, 3, 0, 1, 0, 0, 1)),
            ['6'] = S(P(4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0, 3, 0, 4, 1, 4, 2.5f, 3, 3.5f, 0, 3.5f)),
            ['7'] = S(P(0, 6, 4, 6, 1.5f, 0)),
            ['8'] = S(P(1, 3, 0, 4, 0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 3, 3, 1, 3, 0, 2, 0, 1, 1, 0, 3, 0, 4, 1, 4, 2, 3, 3)),
            ['9'] = S(P(0, 1, 1, 0, 3, 0, 4, 1, 4, 5, 3, 6, 1, 6, 0, 5, 0, 3.5f, 1, 2.5f, 4, 2.5f)),
            ['>'] = S(P(0.5f, 5.5f, 3.5f, 3, 0.5f, 0.5f)),
            ['+'] = S(P(0, 3, 4, 3), P(2, 5, 2, 1)),
            ['/'] = S(P(0, 0, 4, 6)),
            ['.'] = S(P(2, 0)),
            ['\''] = S(P(2, 6, 2, 4.5f)),
            ['?'] = S(P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 2, 2.5f, 2, 1.5f), P(2, 0)),
            ['!'] = S(P(2, 6, 2, 1.5f), P(2, 0)),
            ['-'] = S(P(0.5f, 3, 3.5f, 3)),
            [' '] = S(),
        };

        // Filled-in letters, per level of detail, built on first use.
        private static readonly Dictionary<(char, int), bool[,]> Cells = new Dictionary<(char, int), bool[,]>();

        /// <summary>Width of <paramref name="text"/> in metres at <paramref name="unit"/> metres per letter unit.</summary>
        public static float Width(string text, float unit) => (text.Length * Advance - 1) * unit;

        public static float Height(float unit) => GlyphUnitsTall * unit;

        /// <summary>
        /// Adds <paramref name="text"/> with its vertical middle on <paramref name="anchor"/>.z, standing
        /// on <paramref name="anchor"/>.y, and either centred on or starting at <paramref name="anchor"/>.x.
        /// Blocks are <paramref name="depth"/> tall, with <paramref name="top"/> on top and
        /// <paramref name="sides"/> around. <paramref name="detail"/> is blocks per letter unit.
        /// </summary>
        public static void Add(MeshBuilder mb, string text, Vector3 anchor, float unit, float depth, Color top, Color sides,
            TextAlign align = TextAlign.Center, int detail = 3)
        {
            text = text.ToUpperInvariant();
            var width = Width(text, unit);
            var left = align == TextAlign.Center ? anchor.x - width * 0.5f : align == TextAlign.Right ? anchor.x - width : anchor.x;
            var topZ = anchor.z + Height(unit) * 0.5f;
            var cell = unit / detail;

            for (var i = 0; i < text.Length; i++)
            {
                var cells = Glyph(text[i], detail);
                var glyphLeft = left + i * Advance * unit;
                for (var row = 0; row < cells.GetLength(1); row++)
                {
                    // One block per run of filled cells in the row keeps the mesh small.
                    var z = topZ - (row + 0.5f) * cell;
                    for (var col = 0; col < cells.GetLength(0);)
                    {
                        if (!cells[col, row])
                        {
                            col++;
                            continue;
                        }

                        var start = col;
                        while (col < cells.GetLength(0) && cells[col, row]) col++;
                        var x = glyphLeft + (start + col) * 0.5f * cell;
                        mb.Box(new Vector3(x, anchor.y, z), (col - start) * cell, depth, cell, sides, top);
                    }
                }
            }
        }

        /// <summary>
        /// Adds <paramref name="text"/> standing upright, centred on <paramref name="center"/>, as if
        /// painted on a wall. It reads along <paramref name="right"/> (an X or Z axis, either way) and
        /// sticks out <paramref name="depth"/> towards whoever is reading it. One colour throughout.
        /// </summary>
        public static void AddStanding(MeshBuilder mb, string text, Vector3 center, Vector3 right, float unit, float depth, Color color, int detail = 3)
        {
            text = text.ToUpperInvariant();
            var outward = Vector3.Cross(Vector3.up, right);
            var alongX = Mathf.Abs(right.x) > 0.5f;
            var left = -Width(text, unit) * 0.5f;
            var top = Height(unit) * 0.5f;
            var cell = unit / detail;

            for (var i = 0; i < text.Length; i++)
            {
                var cells = Glyph(text[i], detail);
                var glyphLeft = left + i * Advance * unit;
                for (var row = 0; row < cells.GetLength(1); row++)
                {
                    var y = top - (row + 1) * cell;
                    for (var col = 0; col < cells.GetLength(0);)
                    {
                        if (!cells[col, row])
                        {
                            col++;
                            continue;
                        }

                        var start = col;
                        while (col < cells.GetLength(0) && cells[col, row]) col++;
                        var u = glyphLeft + (start + col) * 0.5f * cell;
                        var run = (col - start) * cell;
                        var bottom = center + right * u + Vector3.up * y + outward * (depth * 0.5f);
                        mb.Box(bottom, alongX ? run : depth, cell, alongX ? depth : run, color, color);
                    }
                }
            }
        }

        /// <summary>A letter as a grid of filled cells, column by row from the top left.</summary>
        private static bool[,] Glyph(char c, int detail)
        {
            if (!Strokes.ContainsKey(c)) c = ' ';
            if (Cells.TryGetValue((c, detail), out var cells)) return cells;

            cells = new bool[GlyphUnitsWide * detail, GlyphUnitsTall * detail];
            foreach (var line in Strokes[c])
            {
                // A single point is a dot; otherwise each pair of neighbouring points is a stroke.
                if (line.Length == 2) Fill(cells, detail, new Vector2(line[0], line[1]), new Vector2(line[0], line[1]));
                for (var p = 0; p + 3 < line.Length; p += 2)
                {
                    Fill(cells, detail, new Vector2(line[p], line[p + 1]), new Vector2(line[p + 2], line[p + 3]));
                }
            }

            Cells[(c, detail)] = cells;
            return cells;
        }

        /// <summary>Fills every cell whose centre is inside the stroke from a to b, which has square ends.</summary>
        private static void Fill(bool[,] cells, int detail, Vector2 a, Vector2 b)
        {
            var along = b - a;
            var length = along.magnitude;
            var dir = length > 1e-4f ? along / length : Vector2.right;
            var across = new Vector2(-dir.y, dir.x);

            for (var col = 0; col < cells.GetLength(0); col++)
            {
                for (var row = 0; row < cells.GetLength(1); row++)
                {
                    // Cell centre in letter units: x from the left stroke edge, y up from the bottom.
                    var p = new Vector2((col + 0.5f) / detail - HalfStroke, GlyphUnitsTall - HalfStroke - (row + 0.5f) / detail);
                    var d = p - a;
                    var t = Vector2.Dot(d, dir);
                    if (t >= -HalfStroke && t <= length + HalfStroke && Mathf.Abs(Vector2.Dot(d, across)) <= HalfStroke) cells[col, row] = true;
                }
            }
        }

        private static float[] P(params float[] points) => points;

        private static float[][] S(params float[][] lines) => lines;
    }
}
