using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Builds flat-shaded, vertex-coloured meshes. Faces don't share vertices, which gives the faceted
    /// low-poly look, and colour lives in the vertices so the whole town can use one material.
    /// That keeps draw calls and state changes low on the Switch.
    /// </summary>
    public sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _triangles = new List<int>();

        /// <summary>
        /// Added to every normal before it's stored. Tilting normals up keeps the undersides of rounded
        /// shapes (chins, curls) from going dark, since the flat shader has only a little ambient light.
        /// </summary>
        public Vector3 NormalBias { get; set; }

        /// <summary>
        /// Adds a quad. Corners go around the edge in order; <paramref name="outward"/> is roughly the
        /// way the face should point, and the winding is fixed up to match, so callers don't have to
        /// think about it.
        /// </summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3 outward)
        {
            var normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, outward) < 0f)
            {
                (b, d) = (d, b);
                normal = -normal;
            }

            normal = (normal + NormalBias).normalized;
            var i = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            for (var k = 0; k < 4; k++)
            {
                _normals.Add(normal);
                _colors.Add(color);
            }

            _triangles.Add(i);
            _triangles.Add(i + 1);
            _triangles.Add(i + 2);
            _triangles.Add(i);
            _triangles.Add(i + 2);
            _triangles.Add(i + 3);
        }

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color, Vector3 outward)
        {
            var normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, outward) < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            normal = (normal + NormalBias).normalized;
            var i = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            for (var k = 0; k < 3; k++)
            {
                _normals.Add(normal);
                _colors.Add(color);
            }

            _triangles.Add(i);
            _triangles.Add(i + 1);
            _triangles.Add(i + 2);
        }

        /// <summary>A box resting on <paramref name="bottomCenter"/>. No bottom face, since it's never seen.</summary>
        public void Box(Vector3 bottomCenter, float width, float height, float depth, Color sides, Color top)
        {
            Sides(bottomCenter, width * 0.5f, depth * 0.5f, 0f, height, sides);

            var y = bottomCenter.y + height;
            float x0 = bottomCenter.x - width * 0.5f, x1 = bottomCenter.x + width * 0.5f;
            float z0 = bottomCenter.z - depth * 0.5f, z1 = bottomCenter.z + depth * 0.5f;
            Quad(new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), top, Vector3.up);
        }

        /// <summary>The four walls of a box between heights y0 and y1 above <paramref name="center"/>.</summary>
        public void Sides(Vector3 center, float halfWidth, float halfDepth, float y0, float y1, Color color)
        {
            float x0 = center.x - halfWidth, x1 = center.x + halfWidth;
            float z0 = center.z - halfDepth, z1 = center.z + halfDepth;
            float b = center.y + y0, t = center.y + y1;

            Quad(new Vector3(x0, b, z1), new Vector3(x0, t, z1), new Vector3(x1, t, z1), new Vector3(x1, b, z1), color, Vector3.forward);
            Quad(new Vector3(x0, b, z0), new Vector3(x0, t, z0), new Vector3(x1, t, z0), new Vector3(x1, b, z0), color, Vector3.back);
            Quad(new Vector3(x1, b, z0), new Vector3(x1, t, z0), new Vector3(x1, t, z1), new Vector3(x1, b, z1), color, Vector3.right);
            Quad(new Vector3(x0, b, z0), new Vector3(x0, t, z0), new Vector3(x0, t, z1), new Vector3(x0, b, z1), color, Vector3.left);
        }

        /// <summary>
        /// A faceted ellipsoid. <paramref name="keepFace"/> gets each face's direction from the centre
        /// (on the unit sphere) and can drop faces, e.g. to leave a hole for a face under the hair.
        /// </summary>
        public void Ball(Vector3 center, Vector3 radii, Color color, int sides = 8, int rings = 5, System.Func<Vector3, bool> keepFace = null)
        {
            Vector3 Point(int ring, int side)
            {
                var lat = Mathf.PI * ring / rings - Mathf.PI * 0.5f;
                var lon = Mathf.PI * 2f * side / sides;
                return new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Cos(lon));
            }

            Vector3 World(Vector3 unit) => center + Vector3.Scale(unit, radii);

            for (var r = 0; r < rings; r++)
            {
                for (var s = 0; s < sides; s++)
                {
                    Vector3 a = Point(r, s), b = Point(r, s + 1), c = Point(r + 1, s + 1), d = Point(r + 1, s);
                    var direction = (a + b + c + d).normalized;
                    if (keepFace != null && !keepFace(direction)) continue;

                    var outward = Vector3.Scale(direction, new Vector3(1f / radii.x, 1f / radii.y, 1f / radii.z));
                    if (r == 0) Triangle(World(a), World(c), World(d), color, outward);
                    else if (r == rings - 1) Triangle(World(a), World(b), World(c), color, outward);
                    else Quad(World(a), World(b), World(c), World(d), color, outward);
                }
            }
        }

        /// <summary>An upright, capped prism (a low-poly cylinder or cone) resting on <paramref name="bottomCenter"/>.</summary>
        public void Prism(Vector3 bottomCenter, float bottomRadius, float topRadius, float height, Color color, int sides = 6)
        {
            var top = bottomCenter + Vector3.up * height;
            for (var i = 0; i < sides; i++)
            {
                var a0 = Mathf.PI * 2f * i / sides;
                var a1 = Mathf.PI * 2f * (i + 1) / sides;
                var d0 = new Vector3(Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                var d1 = new Vector3(Mathf.Sin(a1), 0f, Mathf.Cos(a1));
                Quad(bottomCenter + d0 * bottomRadius, top + d0 * topRadius, top + d1 * topRadius, bottomCenter + d1 * bottomRadius, color, d0 + d1);
                Triangle(top, top + d0 * topRadius, top + d1 * topRadius, color, Vector3.up);
                Triangle(bottomCenter, bottomCenter + d0 * bottomRadius, bottomCenter + d1 * bottomRadius, color, Vector3.down);
            }
        }

        public Mesh ToMesh(Mesh target = null, string name = null)
        {
            var mesh = target != null ? target : new Mesh();
            mesh.Clear();
            if (name != null) mesh.name = name;
            mesh.indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
