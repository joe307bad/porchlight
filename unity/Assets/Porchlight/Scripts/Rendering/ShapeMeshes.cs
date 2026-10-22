using System.Collections.Generic;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The terrain builder's shapes as unit meshes (see <see cref="TerrainShapes"/> for their exact
    /// geometry), which a transform stretches and turns, plus the box and corner handles drawn around
    /// the selected one. Shapes are drawn see-through (<see cref="GlassMaterial"/>), each in its own
    /// bright colour (<see cref="ColorFor"/>), so the unit meshes are white and the colour is a tint.
    /// </summary>
    public static class ShapeMeshes
    {
        /// <summary>How see-through shapes are: their alpha, and the selected one's.</summary>
        public const float Alpha = 0.5f;
        public const float SelectedAlpha = 0.72f;

        public static readonly Color Outline = LowPolyMeshes.Hex("#FFFFFF");
        public static readonly Color Handle = LowPolyMeshes.Hex("#F2C14E");
        public static readonly Color HandleHover = LowPolyMeshes.Hex("#E4572E");

        /// <summary>The colours the bottom bar's cards show each kind in.</summary>
        public static readonly Dictionary<string, Color> CardColors = new Dictionary<string, Color>
        {
            [TerrainShapes.Prism] = LowPolyMeshes.Hex("#FF8FB1"),
            [TerrainShapes.Sphere] = LowPolyMeshes.Hex("#6FD3FF"),
            [TerrainShapes.Cube] = LowPolyMeshes.Hex("#FFD166"),
        };

        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();

        /// <summary>The corners of the unit box, in the order <see cref="BoxEdges"/> refers to them.</summary>
        public static readonly Vector3[] BoxCorners =
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        };

        private static readonly (int, int)[] BoxEdges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
        };

        /// <summary>A material for see-through shapes. Tint it per shape with a property block (<see cref="Tint"/>).</summary>
        public static Material GlassMaterial()
        {
            var shader = Resources.Load<Shader>("PorchlightGlass");
            if (shader == null)
            {
                Debug.LogError("PorchlightGlass shader missing from Resources; shapes will be opaque.");
                return TownView.CreateMaterial();
            }

            return new Material(shader) { name = "Terrain shape glass" };
        }

        /// <summary>Sets a renderer's tint without a material per shape.</summary>
        public static void Tint(MeshRenderer renderer, MaterialPropertyBlock block, Color color)
        {
            block.SetColor("_Tint", color);
            renderer.SetPropertyBlock(block);
        }

        /// <summary>
        /// A bright colour picked from a shape's id, so a shape keeps its colour from one visit to the
        /// next without storing it. Uses its own hash, since string hashes can differ between runs.
        /// </summary>
        public static Color ColorFor(string id, float alpha)
        {
            var hash = 2166136261u;
            foreach (var c in id ?? "")
            {
                hash ^= c;
                hash *= 16777619u;
            }

            var color = Color.HSVToRGB((hash % 360u) / 360f, 0.5f + (hash >> 9) % 20u / 100f, 1f);
            color.a = alpha;
            return color;
        }

        public static Mesh Unit(string kind)
        {
            kind = TerrainShapes.Canonical(kind) ?? TerrainShapes.Cube;
            if (Cache.TryGetValue(kind, out var mesh) && mesh != null) return mesh;

            var mb = new MeshBuilder();
            Add(mb, kind, Color.white, Matrix4x4.identity);
            mesh = mb.ToMesh(name: "shape " + kind);
            Cache[kind] = mesh;
            return mesh;
        }

        /// <summary>A shape of the given kind, from its unit form moved by <paramref name="transform"/>. The bar's cards draw them this way too.</summary>
        public static void Add(MeshBuilder mb, string kind, Color color, Matrix4x4 transform)
        {
            Vector3 P(float x, float y, float z) => transform.MultiplyPoint3x4(new Vector3(x, y, z));
            Vector3 D(float x, float y, float z) => transform.MultiplyVector(new Vector3(x, y, z));

            switch (TerrainShapes.Canonical(kind))
            {
                case TerrainShapes.Sphere:
                    var center = P(0f, 0f, 0f);
                    var radii = new Vector3(D(0.5f, 0f, 0f).magnitude, D(0f, 0.5f, 0f).magnitude, D(0f, 0f, 0.5f).magnitude);
                    mb.Ball(center, radii, color, sides: 14, rings: 9);
                    break;

                case TerrainShapes.Prism:
                {
                    // Two triangle ends and three rectangles: the floor and the two slopes up to the ridge.
                    var t = TerrainShapes.UnitTriangle;
                    Vector3 Front(int i) => P(t[i].X, t[i].Y, 0.5f);
                    Vector3 Back(int i) => P(t[i].X, t[i].Y, -0.5f);
                    mb.Triangle(Front(0), Front(1), Front(2), color, D(0f, 0f, 1f));
                    mb.Triangle(Back(0), Back(1), Back(2), color, D(0f, 0f, -1f));
                    mb.Quad(Back(0), Back(1), Front(1), Front(0), color, D(0f, -1f, 0f));
                    mb.Quad(Back(0), Back(2), Front(2), Front(0), color, D(-1f, 0.5f, 0f));
                    mb.Quad(Back(1), Back(2), Front(2), Front(1), color, D(1f, 0.5f, 0f));
                    break;
                }

                default:
                    for (var axis = 0; axis < 3; axis++)
                    {
                        for (var sign = -1; sign <= 1; sign += 2)
                        {
                            var n = Vector3.zero;
                            n[axis] = sign;
                            var u = Vector3.zero;
                            u[(axis + 1) % 3] = 0.5f;
                            var v = Vector3.zero;
                            v[(axis + 2) % 3] = 0.5f;
                            var f = n * 0.5f;
                            mb.Quad(P(f.x - u.x - v.x, f.y - u.y - v.y, f.z - u.z - v.z), P(f.x + u.x - v.x, f.y + u.y - v.y, f.z + u.z - v.z),
                                P(f.x + u.x + v.x, f.y + u.y + v.y, f.z + u.z + v.z), P(f.x - u.x + v.x, f.y - u.y + v.y, f.z - u.z + v.z), color, D(n.x, n.y, n.z));
                        }
                    }

                    break;
            }
        }

        /// <summary>
        /// The box around a selected shape, in world space: its edges as thin beams and a handle on each
        /// corner, <paramref name="handleSize"/> across. <paramref name="hovered"/> is the handle under
        /// the pointer (or -1).
        /// </summary>
        public static void SelectionBox(MeshBuilder mb, Vector3[] corners, float lineWidth, float handleSize, int hovered)
        {
            foreach (var (a, b) in BoxEdges) Beam(mb, corners[a], corners[b], lineWidth, Outline);
            for (var i = 0; i < corners.Length; i++)
            {
                var size = i == hovered ? handleSize * 1.4f : handleSize;
                mb.Box(corners[i] - Vector3.up * (size * 0.5f), size, size, size, i == hovered ? HandleHover : Handle, i == hovered ? HandleHover : Handle);
            }
        }

        /// <summary>A square beam from a to b, <paramref name="width"/> thick.</summary>
        public static void Beam(MeshBuilder mb, Vector3 a, Vector3 b, float width, Color color)
        {
            var along = b - a;
            if (along.sqrMagnitude < 1e-8f) return;
            var direction = along.normalized;
            var across = Vector3.Cross(direction, Mathf.Abs(direction.y) > 0.9f ? Vector3.right : Vector3.up).normalized * (width * 0.5f);
            var up = Vector3.Cross(direction, across).normalized * (width * 0.5f);
            var sides = new[] { across + up, across - up, -across - up, -across + up };
            for (var k = 0; k < 4; k++)
            {
                var s0 = sides[k];
                var s1 = sides[(k + 1) % 4];
                mb.Quad(a + s0, b + s0, b + s1, a + s1, color, s0 + s1);
            }
        }
    }
}
