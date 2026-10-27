// Three's world (right-handed, y up; the map's (x, y) is Three's (x, z)) in
// Unity's (left-handed, y up). A point maps by negating z, which keeps every
// distance and turns a mirror image back the right way round when drawn; a
// matrix M becomes F M F (F = diag(1, 1, -1)); triangles flip their winding.
// And colours: Three's hex colours are sRGB, converted to linear as Three's
// ColorManagement does.

using Porchlight.Core;
using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Space
    {
        /// <summary>A Three-space point (x, up, z) as a Unity position.</summary>
        public static Vector3 P(double x, double y, double z) => new Vector3((float)x, (float)y, (float)-z);

        /// <summary>A map point (x, y) at height h as a Unity position.</summary>
        public static Vector3 Map(double x, double y, double h = 0) => new Vector3((float)x, (float)h, (float)-y);

        /// <summary>A Three-space matrix as a Unity one (F M F).</summary>
        public static Matrix4x4 M(Mat4 m)
        {
            var e = m.e;
            var o = new Matrix4x4();
            // Column-major e[col*4+row]; F M F flips the sign of entries in row 2 or column 2 (not both).
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double v = e[col * 4 + row];
                    if ((row == 2) != (col == 2)) v = -v;
                    o[row, col] = (float)v;
                }
            }
            return o;
        }

        /// <summary>Three's sRGB to linear transfer.</summary>
        public static float SRGBToLinear(float c) => c < 0.04045f ? c * 0.0773993808f : Mathf.Pow(c * 0.9478672986f + 0.0521327014f, 2.4f);

        /// <summary>Color.setHex: an sRGB hex colour as a linear Unity colour.</summary>
        public static Color Hex(int hex) =>
            new Color(SRGBToLinear(((hex >> 16) & 255) / 255f), SRGBToLinear(((hex >> 8) & 255) / 255f), SRGBToLinear((hex & 255) / 255f), 1);

        /// <summary>Color.lerp, on linear values as Three does.</summary>
        public static Color Lerp(Color a, Color b, float t) => new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, 1);

        /// <summary>An sRGB 0..255 colour (as CSS writes them) as a Unity colour for UI, which converts itself.</summary>
        public static Color Css(int r, int g, int b, float a = 1) => new Color(r / 255f, g / 255f, b / 255f, a);

        public static Color CssHex(int hex, float a = 1) => new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, a);
    }
}
