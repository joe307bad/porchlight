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
