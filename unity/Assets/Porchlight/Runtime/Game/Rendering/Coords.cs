// Where the town sits in Unity's world. The town's map is x east and y south
// (tiles; see Model/Types.cs), so a map point (x, y) at height h is the world
// point (x, h, -y): north is +z, as Unity draws a top-down view. Headings in
// the explore logic are angles where a thing faces map direction
// (sin r, cos r); Yaw turns one into a rotation about Unity's up axis for a
// model whose front is +z.

using UnityEngine;

namespace Porchlight.Rendering
{
    public static class Coords
    {
        /// <summary>A map point (x, y) at height h, in world space.</summary>
        public static Vector3 Map(double x, double y, double h = 0) => new Vector3((float)x, (float)h, (float)-y);

        /// <summary>The rotation that turns a model's +z front to face map direction (sin r, cos r).</summary>
        public static Quaternion Yaw(double r) => Quaternion.Euler(0, 180 - (float)(r * Mathf.Rad2Deg), 0);

        /// <summary>A 0xRRGGBB colour, as Unity colours are written (sRGB; Unity converts for lighting).</summary>
        public static Color Rgb(int r, int g, int b, float a = 1) => new Color(r / 255f, g / 255f, b / 255f, a);

        public static Color Hex(int hex, float a = 1) => new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, a);
    }
}
