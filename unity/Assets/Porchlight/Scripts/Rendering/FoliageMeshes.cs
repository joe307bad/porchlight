using System;
using System.Collections.Generic;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// The plants that grow around town (<see cref="Foliage"/>), built in code like everything else and
    /// kept low-poly: ten trees, ten bushes and small trees, ten each of tall, medium and small flowers,
    /// and four grasses and weeds. Each look is built once, standing on its own origin, and the foliage
    /// view stamps copies of it about.
    /// </summary>
    public static class FoliageMeshes
    {
        private static readonly Dictionary<(FoliageKind, int), Mesh> Cache = new Dictionary<(FoliageKind, int), Mesh>();

        private static readonly Color Bark = LowPolyMeshes.Hex("#6B4F3A");
        private static readonly Color DarkBark = LowPolyMeshes.Hex("#4E3A2C");
        private static readonly Color Birch = LowPolyMeshes.Hex("#E8E4DA");
        private static readonly Color Stem = LowPolyMeshes.Hex("#4F8A3E");
        private static readonly Color Leaf = LowPolyMeshes.Hex("#5E9C46");

        public static Mesh Get(FoliageKind kind, int variant)
        {
            if (Cache.TryGetValue((kind, variant), out var mesh) && mesh != null) return mesh;

            var mb = new MeshBuilder { NormalBias = new Vector3(0f, 0.35f, 0f) };
            var random = new System.Random((int)kind * 1000 + variant * 37 + 11);
            switch (kind)
            {
                case FoliageKind.Tree: Tree(mb, variant, random); break;
                case FoliageKind.Bush: Bush(mb, variant, random); break;
                case FoliageKind.TallFlower: TallFlower(mb, variant, random); break;
                case FoliageKind.MediumFlower: MediumFlower(mb, variant, random); break;
                case FoliageKind.SmallFlower: SmallFlower(mb, variant, random); break;
                default: Grass(mb, variant, random); break;
            }

            mesh = mb.ToMesh(name: $"{kind} {variant}");
            Cache[(kind, variant)] = mesh;
            return mesh;
        }

        private static float Range(System.Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);

        /// <summary>A colour a little lighter or darker, so no two plants of a look are quite the same green.</summary>
        private static Color Vary(Color color, System.Random random, float amount = 0.08f)
        {
            var shade = 1f + Range(random, -amount, amount);
            return new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
        }

        private static Color Hex(string hex) => LowPolyMeshes.Hex(hex);

        private static void Trunk(MeshBuilder mb, float height, float radius, Color color) => mb.Prism(Vector3.zero, radius, radius * 0.7f, height, color, 6);

        private static void Cone(MeshBuilder mb, Vector3 bottom, float radius, float height, Color color, int sides = 7) => mb.Prism(bottom, radius, 0f, height, color, sides);

        private static void Blob(MeshBuilder mb, Vector3 center, Vector3 radii, Color color) => mb.Ball(center, radii, color, 7, 5);

        private static void Stalk(MeshBuilder mb, Vector3 bottom, float height, float width, Color color) => mb.Box(bottom, width, height, width, color, color);

        /// <summary>A flat leaf or blade, seen from both sides: from its base out to a tip.</summary>
        private static void Blade(MeshBuilder mb, Vector3 bottom, float headingDeg, float height, float width, float lean, Color color)
        {
            var heading = Quaternion.Euler(0f, headingDeg, 0f);
            var side = heading * Vector3.right * (width * 0.5f);
            var tip = bottom + Vector3.up * height + heading * Vector3.forward * lean;
            var facing = heading * Vector3.forward;
            mb.Triangle(bottom - side, bottom + side, tip, color, facing);
            mb.Triangle(bottom - side, bottom + side, tip, color, -facing);
        }

        /// <summary>A ring of petals round a centre, facing up (tilted by <paramref name="tilt"/>).</summary>
        private static void Bloom(MeshBuilder mb, Vector3 center, float radius, int petals, Color petal, Color middle, float tilt = 0f)
        {
            var turn = Quaternion.Euler(tilt, 0f, 0f);
            for (var i = 0; i < petals; i++)
            {
                var a0 = Mathf.PI * 2f * i / petals;
                var a1 = Mathf.PI * 2f * (i + 0.5f) / petals;
                var a2 = Mathf.PI * 2f * (i + 1f) / petals;
                var inner0 = turn * new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * (radius * 0.35f);
                var outer = turn * new Vector3(Mathf.Cos(a1), 0.12f, Mathf.Sin(a1)) * radius;
                var inner1 = turn * new Vector3(Mathf.Cos(a2), 0f, Mathf.Sin(a2)) * (radius * 0.35f);
                mb.Triangle(center + inner0, center + outer, center + inner1, petal, turn * Vector3.up);
                mb.Triangle(center + inner0, center + outer, center + inner1, petal, turn * Vector3.down);
            }

            mb.Prism(center - Vector3.up * 0.01f, radius * 0.38f, radius * 0.3f, radius * 0.18f, middle, 6);
        }

        // An icosahedron: twelve corners, twenty faces. Pushed in and out at random it makes a lumpy,
        // faceted clump of leaves rather than a smooth ball.
        private static readonly Vector3[] Ico = MakeIco();
        private static readonly int[] IcoFaces =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        private static Vector3[] MakeIco()
        {
            var t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var p = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (var i = 0; i < p.Length; i++) p[i] = p[i].normalized;
            return p;
        }

        /// <summary>A lumpy clump of leaves: a jittered icosahedron, each face its own shade of the colour.</summary>
        private static void Clump(MeshBuilder mb, Vector3 center, Vector3 radii, Color color, System.Random random, float jitter = 0.3f)
        {
            var corners = new Vector3[Ico.Length];
            for (var i = 0; i < Ico.Length; i++) corners[i] = center + Vector3.Scale(Ico[i] * (1f + Range(random, -jitter, jitter)), radii);
            for (var f = 0; f < IcoFaces.Length; f += 3)
            {
                Vector3 a = corners[IcoFaces[f]], b = corners[IcoFaces[f + 1]], c = corners[IcoFaces[f + 2]];
                mb.Triangle(a, b, c, Vary(color, random, 0.07f), (a + b + c) / 3f - center);
            }
        }

        /// <summary>A branch: a tapering five-sided limb from one point to another, at any angle.</summary>
        private static void Limb(MeshBuilder mb, Vector3 from, Vector3 to, float fromRadius, float toRadius, Color color, int sides = 5)
        {
            var axis = (to - from).normalized;
            var u = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var v = Vector3.Cross(axis, u);
            for (var i = 0; i < sides; i++)
            {
                var a0 = Mathf.PI * 2f * i / sides;
                var a1 = Mathf.PI * 2f * (i + 1) / sides;
                var d0 = u * Mathf.Cos(a0) + v * Mathf.Sin(a0);
                var d1 = u * Mathf.Cos(a1) + v * Mathf.Sin(a1);
                mb.Quad(from + d0 * fromRadius, to + d0 * toRadius, to + d1 * toRadius, from + d1 * fromRadius, color, d0 + d1);
            }
        }

        /// <summary>
        /// A broadleaf crown: branches forking up and out from the top of the trunk, each tipped with a
        /// few clumps of leaves, around a clump in the middle. Returns where the branch tips are (for fruit or blossom).
        /// </summary>
        private static List<Vector3> LeafyCrown(MeshBuilder mb, System.Random random, float trunkHeight, float spread, float height, int branches, Color leaves, Color bark, float limb = 0.12f)
        {
            var tips = new List<Vector3>();
            Clump(mb, new Vector3(0f, trunkHeight + height * 0.5f, 0f), new Vector3(spread * 0.5f, height * 0.42f, spread * 0.5f), Vary(leaves, random), random);
            for (var b = 0; b < branches; b++)
            {
                var angle = Mathf.PI * 2f * b / branches + Range(random, -0.35f, 0.35f);
                var start = new Vector3(0f, trunkHeight * Range(random, 0.7f, 1f), 0f);
                var reach = spread * Range(random, 0.55f, 0.85f);
                var end = start + new Vector3(Mathf.Cos(angle) * reach, height * Range(random, 0.35f, 0.75f), Mathf.Sin(angle) * reach);
                Limb(mb, start, end, limb, limb * 0.4f, bark);

                // A twig off the side, then leaves over both.
                var twigEnd = end + new Vector3(Mathf.Cos(angle + 1.2f) * reach * 0.35f, height * 0.18f, Mathf.Sin(angle + 1.2f) * reach * 0.35f);
                Limb(mb, Vector3.Lerp(start, end, 0.6f), twigEnd, limb * 0.5f, limb * 0.2f, bark, 4);
                var size = spread * Range(random, 0.3f, 0.42f);
                Clump(mb, end, new Vector3(size, size * 0.8f, size), Vary(leaves, random), random);
                Clump(mb, twigEnd, new Vector3(size * 0.75f, size * 0.65f, size * 0.75f), Vary(leaves, random), random);
                tips.Add(end);
                tips.Add(twigEnd);
            }

            return tips;
        }

        /// <summary>A bush: a few lumpy clumps heaped together, with stems showing at the bottom.</summary>
        private static List<Vector3> Shrub(MeshBuilder mb, System.Random random, Vector3 center, float radius, float height, int clumps, Color leaves)
        {
            var spots = new List<Vector3>();
            for (var i = 0; i < 3; i++) Limb(mb, center, center + new Vector3(Range(random, -0.3f, 0.3f), height * 0.5f, Range(random, -0.3f, 0.3f)) * 1f, 0.04f, 0.02f, Bark, 4);
            for (var i = 0; i < clumps; i++)
            {
                var a = Mathf.PI * 2f * i / clumps + Range(random, -0.4f, 0.4f);
                var r = i == 0 ? 0f : radius * Range(random, 0.35f, 0.6f);
                var at = center + new Vector3(Mathf.Cos(a) * r, height * Range(random, 0.4f, 0.65f), Mathf.Sin(a) * r);
                var size = radius * Range(random, 0.45f, 0.65f);
                Clump(mb, at, new Vector3(size, size * 0.8f, size), Vary(leaves, random), random);
                spots.Add(at);
            }

            return spots;
        }

        private static void Leaves(MeshBuilder mb, System.Random random, float height, int count, Color color, float size)
        {
            for (var i = 0; i < count; i++)
            {
                Blade(mb, new Vector3(0f, Range(random, 0f, height), 0f), Range(random, 0f, 360f), size, size * 0.45f, size * 0.9f, Vary(color, random));
            }
        }

        // Trees, 5–9 m.
        private static void Tree(MeshBuilder mb, int variant, System.Random random)
        {
            var green = Vary(Hex("#4F8A3E"), random);
            switch (variant)
            {
                case 0: // Pine: three tiers.
                {
                    var dark = Vary(Hex("#2F6B3A"), random);
                    Trunk(mb, 2f, 0.25f, Bark);
                    for (var t = 0; t < 3; t++) Cone(mb, new Vector3(0f, 1.6f + t * 1.6f, 0f), 2.2f - t * 0.55f, 2.8f, Vary(dark, random, 0.05f));
                    break;
                }

                case 1: // Tall, narrow fir.
                {
                    var dark = Vary(Hex("#2C5E3F"), random);
                    Trunk(mb, 1.6f, 0.22f, DarkBark);
                    for (var t = 0; t < 5; t++) Cone(mb, new Vector3(0f, 1.2f + t * 1.35f, 0f), 1.5f - t * 0.2f, 2.2f, Vary(dark, random, 0.05f), 6);
                    break;
                }

                case 2: // Oak: a broad, branching crown.
                    Trunk(mb, 2.6f, 0.35f, Bark);
                    LeafyCrown(mb, random, 2.6f, 3f, 3f, 6, green, Bark, 0.16f);
                    break;

                case 3: // Poplar: tall and thin.
                    Trunk(mb, 1.5f, 0.22f, Bark);
                    Blob(mb, new Vector3(0f, 4.6f, 0f), new Vector3(1.1f, 3.4f, 1.1f), Vary(Hex("#5C9A3F"), random));
                    break;

                case 4: // Birch: white trunk and branches, an airy yellow-green crown.
                    Trunk(mb, 3.4f, 0.18f, Birch);
                    LeafyCrown(mb, random, 3.4f, 1.8f, 3f, 5, Hex("#8DBF4E"), Birch, 0.08f);
                    break;

                case 5: // Maple in autumn, reds and oranges.
                    Trunk(mb, 2.4f, 0.3f, Bark);
                    LeafyCrown(mb, random, 2.4f, 2.6f, 2.8f, 5, Hex("#E07A2E"), Bark, 0.13f);
                    Clump(mb, new Vector3(0.6f, 5.2f, -0.4f), new Vector3(1f, 0.8f, 1f), Hex("#D9532B"), random);
                    break;

                case 6: // Apple tree, fruit hanging under the leaves.
                {
                    Trunk(mb, 2f, 0.28f, Bark);
                    var tips = LeafyCrown(mb, random, 2f, 2.4f, 2.2f, 5, green, Bark, 0.12f);
                    var apple = Hex("#D63A2F");
                    foreach (var tip in tips)
                    {
                        var a = Range(random, 0f, Mathf.PI * 2f);
                        mb.Ball(tip + new Vector3(Mathf.Cos(a) * 0.5f, -0.55f, Mathf.Sin(a) * 0.5f), Vector3.one * 0.15f, apple, 5, 3);
                    }

                    break;
                }

                case 7: // Cherry blossom: dark spreading branches, pink clouds.
                {
                    Trunk(mb, 2f, 0.26f, DarkBark);
                    var tips = LeafyCrown(mb, random, 2f, 2.8f, 2f, 6, Hex("#F4B6C8"), DarkBark, 0.12f);
                    foreach (var tip in tips) Clump(mb, tip + Vector3.up * 0.4f, Vector3.one * 0.45f, Hex("#F9D3DF"), random);
                    break;
                }

                case 8: // Spruce: many narrow tiers, very dark.
                {
                    var dark = Vary(Hex("#24503A"), random);
                    Trunk(mb, 1f, 0.24f, DarkBark);
                    for (var t = 0; t < 6; t++) Cone(mb, new Vector3(0f, 0.8f + t * 1.1f, 0f), 1.9f - t * 0.28f, 1.8f, Vary(dark, random, 0.04f), 8);
                    break;
                }

                default: // Willow: a dome with drooping fronds.
                {
                    var willow = Vary(Hex("#9CC25A"), random);
                    Trunk(mb, 2.6f, 0.32f, Bark);
                    LeafyCrown(mb, random, 2.6f, 2.4f, 1.8f, 6, willow, Bark, 0.12f);
                    for (var i = 0; i < 10; i++)
                    {
                        var a = Mathf.PI * 2f * i / 10f;
                        var at = new Vector3(Mathf.Cos(a) * 2f, 4f, Mathf.Sin(a) * 2f);
                        mb.Prism(at - Vector3.up * 2.6f, 0.05f, 0.35f, 2.6f, Vary(willow, random), 4);
                    }

                    break;
                }
            }
        }

        // Bushes and small trees, 1–3 m.
        private static void Bush(MeshBuilder mb, int variant, System.Random random)
        {
            var green = Vary(Hex("#4C8A3B"), random);
            switch (variant)
            {
                case 0: // Round bush.
                    Shrub(mb, random, Vector3.zero, 1.1f, 1.3f, 5, green);
                    break;

                case 1: // Berry bush.
                {
                    var spots = Shrub(mb, random, Vector3.zero, 1f, 1.2f, 5, green);
                    var berry = Hex("#5B2A86");
                    foreach (var spot in spots)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            var a = Range(random, 0f, Mathf.PI * 2f);
                            mb.Ball(spot + new Vector3(Mathf.Cos(a) * 0.45f, Range(random, -0.1f, 0.3f), Mathf.Sin(a) * 0.45f), Vector3.one * 0.08f, berry, 4, 3);
                        }
                    }

                    break;
                }

                case 2: // Little conifer.
                    Trunk(mb, 0.4f, 0.12f, DarkBark);
                    Cone(mb, new Vector3(0f, 0.3f, 0f), 0.8f, 2.2f, Vary(Hex("#2F6B3A"), random), 7);
                    break;

                case 3: // Topiary ball on a stem.
                    Trunk(mb, 1.2f, 0.07f, Bark);
                    Blob(mb, new Vector3(0f, 1.6f, 0f), new Vector3(0.6f, 0.6f, 0.6f), Vary(Hex("#3E7A36"), random));
                    break;

                case 4: // Hedge clump.
                    for (var i = 0; i < 4; i++) Clump(mb, new Vector3(i * 0.55f - 0.8f, 0.55f, Range(random, -0.2f, 0.2f)), new Vector3(0.55f, 0.55f, 0.5f), Vary(green, random), random, 0.22f);
                    break;

                case 5: // Lavender bush.
                {
                    Blob(mb, new Vector3(0f, 0.35f, 0f), new Vector3(0.7f, 0.4f, 0.7f), Vary(Hex("#7A9A6A"), random));
                    var purple = Hex("#8E6CC4");
                    for (var i = 0; i < 12; i++)
                    {
                        var a = Range(random, 0f, Mathf.PI * 2f);
                        var r = Range(random, 0f, 0.55f);
                        var at = new Vector3(Mathf.Cos(a) * r, 0.5f, Mathf.Sin(a) * r);
                        Stalk(mb, at, 0.4f, 0.03f, Stem);
                        Cone(mb, at + Vector3.up * 0.4f, 0.06f, 0.25f, Vary(purple, random), 4);
                    }

                    break;
                }

                case 6: // Young tree.
                    Trunk(mb, 1.5f, 0.08f, Bark);
                    LeafyCrown(mb, random, 1.5f, 1.1f, 1.2f, 4, Hex("#6AAA48"), Bark, 0.05f);
                    break;

                case 7: // Fern.
                    for (var i = 0; i < 9; i++) Blade(mb, Vector3.zero, i * 40f + Range(random, -10f, 10f), 0.9f, 0.35f, 0.8f, Vary(Hex("#4F9A45"), random));
                    break;

                case 8: // Dry shrub.
                {
                    var dry = Vary(Hex("#8C7A4A"), random);
                    for (var i = 0; i < 7; i++) Blade(mb, Vector3.zero, i * 51f, Range(random, 0.7f, 1.1f), 0.18f, 0.5f, dry);
                    Blob(mb, new Vector3(0f, 0.6f, 0f), new Vector3(0.5f, 0.4f, 0.5f), Vary(Hex("#7E8B4E"), random));
                    break;
                }

                default: // Flowering shrub.
                {
                    var spots = Shrub(mb, random, Vector3.zero, 1.1f, 1.3f, 5, green);
                    var white = Hex("#F6F2E8");
                    foreach (var spot in spots)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            var a = Range(random, 0f, Mathf.PI * 2f);
                            Bloom(mb, spot + new Vector3(Mathf.Cos(a) * 0.45f, Range(random, 0.1f, 0.4f), Mathf.Sin(a) * 0.45f), 0.1f, 5, white, Hex("#F5D547"));
                        }
                    }

                    break;
                }
            }
        }

        // Tall flowers, 0.8–1.5 m.
        private static void TallFlower(MeshBuilder mb, int variant, System.Random random)
        {
            var height = Range(random, 0.9f, 1.4f);
            Leaves(mb, random, height * 0.4f, 3, Leaf, 0.35f);
            switch (variant)
            {
                case 0: // Sunflower.
                    Stalk(mb, Vector3.zero, height, 0.05f, Stem);
                    Bloom(mb, new Vector3(0f, height, 0f), 0.32f, 12, Hex("#F4C430"), Hex("#5A3A1E"), 60f);
                    break;

                case 1: // Lupin: a spike of small cones.
                {
                    var color = Vary(Hex("#7B6FD6"), random);
                    Stalk(mb, Vector3.zero, height * 0.6f, 0.04f, Stem);
                    for (var t = 0; t < 5; t++) Cone(mb, new Vector3(0f, height * 0.55f + t * 0.1f, 0f), 0.12f - t * 0.015f, 0.18f, color, 5);
                    break;
                }

                case 2: // Hollyhock: discs up the stem.
                {
                    var color = Vary(Hex("#E8679A"), random);
                    Stalk(mb, Vector3.zero, height, 0.04f, Stem);
                    for (var t = 0; t < 4; t++) mb.Prism(new Vector3(0f, height * (0.45f + t * 0.15f), 0f), 0.12f, 0.12f, 0.04f, color, 6);
                    break;
                }

                case 3: // Foxglove: hanging bells.
                {
                    var color = Vary(Hex("#C45BAA"), random);
                    Stalk(mb, Vector3.zero, height, 0.04f, Stem);
                    for (var t = 0; t < 5; t++) mb.Prism(new Vector3(0.07f, height * (0.5f + t * 0.1f), 0f), 0.07f, 0.02f, 0.12f, color, 5);
                    break;
                }

                case 4: // Delphinium: blue spike.
                    Stalk(mb, Vector3.zero, height * 0.55f, 0.04f, Stem);
                    Cone(mb, new Vector3(0f, height * 0.5f, 0f), 0.14f, height * 0.55f, Vary(Hex("#3E6FD8"), random), 6);
                    break;

                case 5: // Allium: a purple ball.
                    Stalk(mb, Vector3.zero, height, 0.03f, Stem);
                    mb.Ball(new Vector3(0f, height + 0.08f, 0f), Vector3.one * 0.13f, Vary(Hex("#9B59B6"), random), 6, 4);
                    break;

                case 6: // Torch lily.
                    Stalk(mb, Vector3.zero, height * 0.7f, 0.04f, Stem);
                    Cone(mb, new Vector3(0f, height * 0.65f, 0f), 0.09f, 0.35f, Hex("#F2783A"), 6);
                    mb.Prism(new Vector3(0f, height * 0.6f, 0f), 0.08f, 0.09f, 0.08f, Hex("#F5D547"), 6);
                    break;

                case 7: // Gladiolus: flowers up one side.
                {
                    var color = Vary(Hex("#F25C54"), random);
                    Stalk(mb, Vector3.zero, height, 0.04f, Stem);
                    for (var t = 0; t < 5; t++) Bloom(mb, new Vector3(0.03f, height * (0.55f + t * 0.09f), 0f), 0.08f, 5, color, color, 70f);
                    break;
                }

                case 8: // Tall daisy (Shasta).
                    Stalk(mb, Vector3.zero, height, 0.035f, Stem);
                    Bloom(mb, new Vector3(0f, height, 0f), 0.16f, 10, Hex("#FBFBF5"), Hex("#F2C230"), 20f);
                    break;

                default: // Hollyhock in yellow, taller.
                {
                    var color = Vary(Hex("#F7D96B"), random);
                    Stalk(mb, Vector3.zero, height * 1.15f, 0.04f, Stem);
                    for (var t = 0; t < 5; t++) mb.Prism(new Vector3(0f, height * (0.5f + t * 0.14f), 0f), 0.11f, 0.11f, 0.04f, color, 6);
                    break;
                }
            }
        }

        // Medium flowers, 0.35–0.7 m: clumps of a few stems.
        private static void MediumFlower(MeshBuilder mb, int variant, System.Random random)
        {
            (string Petal, string Middle, int Petals, float Size, int Heads)[] looks =
            {
                ("#FBFBF5", "#F2C230", 9, 0.09f, 5),   // Daisies
                ("#D7263D", "#2B2B2B", 5, 0.1f, 4),    // Poppies
                ("#4A7BD9", "#2E4A8F", 8, 0.07f, 5),   // Cornflowers
                ("#F28C28", "#B5531A", 10, 0.08f, 4),  // Marigolds
                ("#E07BB5", "#7A3F1E", 10, 0.09f, 4),  // Coneflowers
                ("#F5E04A", "#E0B020", 5, 0.06f, 6),   // Buttercups
                ("#7E57C2", "#F5D547", 6, 0.1f, 3),    // Irises
                ("#FFFFFF", "#F5D547", 8, 0.1f, 4),    // Cosmos
                ("#F3A6B8", "#E58AA0", 12, 0.12f, 3),  // Peonies
                ("#FF9F68", "#C8553D", 6, 0.08f, 5),   // Zinnias
            };

            var look = looks[variant % looks.Length];
            var height = Range(random, 0.35f, 0.65f);
            Leaves(mb, random, 0.15f, 4, Leaf, 0.22f);
            for (var i = 0; i < look.Heads; i++)
            {
                var a = Mathf.PI * 2f * i / look.Heads + Range(random, -0.3f, 0.3f);
                var r = i == 0 ? 0f : Range(random, 0.08f, 0.2f);
                var at = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                var h = height * Range(random, 0.8f, 1.1f);
                Stalk(mb, at, h, 0.025f, Stem);
                Bloom(mb, at + Vector3.up * h, look.Size, look.Petals, Vary(Hex(look.Petal), random, 0.05f), Hex(look.Middle), Range(random, 0f, 30f));
            }
        }

        // Small flowers, 0.1–0.3 m: a leafy mound with little blooms.
        private static void SmallFlower(MeshBuilder mb, int variant, System.Random random)
        {
            string[] colors = { "#FFFFFF", "#F5E04A", "#E85D75", "#7E9CF0", "#B57BE0", "#FF9F68", "#F7C6D9", "#FFE7A3", "#5BC0EB", "#D7263D" };
            var color = Hex(colors[variant % colors.Length]);
            var mound = Vary(Hex("#5C9A44"), random);
            mb.Ball(new Vector3(0f, 0.04f, 0f), new Vector3(0.28f, 0.12f, 0.28f), mound, 6, 3);
            var blooms = 5 + variant % 4;
            for (var i = 0; i < blooms; i++)
            {
                var a = Range(random, 0f, Mathf.PI * 2f);
                var r = Range(random, 0f, 0.24f);
                var at = new Vector3(Mathf.Cos(a) * r, Range(random, 0.12f, 0.24f), Mathf.Sin(a) * r);
                if (variant % 2 == 0) Bloom(mb, at, 0.05f, 5, Vary(color, random, 0.05f), Hex("#F5D547"));
                else mb.Ball(at, Vector3.one * 0.04f, Vary(color, random, 0.05f), 4, 3);
            }
        }

        // Grass and weeds.
        private static void Grass(MeshBuilder mb, int variant, System.Random random)
        {
            switch (variant)
            {
                case 0: // Short tuft.
                {
                    var green = Vary(Hex("#6FAF4F"), random);
                    for (var i = 0; i < 9; i++) Blade(mb, new Vector3(Range(random, -0.12f, 0.12f), 0f, Range(random, -0.12f, 0.12f)), Range(random, 0f, 360f), Range(random, 0.2f, 0.35f), 0.07f, Range(random, 0.05f, 0.15f), Vary(green, random));
                    break;
                }

                case 1: // Tall grass.
                {
                    var green = Vary(Hex("#8DBE5A"), random);
                    for (var i = 0; i < 11; i++) Blade(mb, new Vector3(Range(random, -0.2f, 0.2f), 0f, Range(random, -0.2f, 0.2f)), Range(random, 0f, 360f), Range(random, 0.5f, 0.85f), 0.08f, Range(random, 0.15f, 0.3f), Vary(green, random));
                    break;
                }

                case 2: // Clover patch.
                {
                    var clover = Vary(Hex("#4E9A3E"), random);
                    for (var i = 0; i < 8; i++)
                    {
                        var at = new Vector3(Range(random, -0.3f, 0.3f), Range(random, 0.03f, 0.08f), Range(random, -0.3f, 0.3f));
                        mb.Prism(at, 0.07f, 0.07f, 0.02f, Vary(clover, random), 5);
                    }

                    mb.Ball(new Vector3(0.05f, 0.12f, 0f), Vector3.one * 0.05f, Hex("#F2F0F5"), 4, 3);
                    break;
                }

                default: // Dandelion.
                    for (var i = 0; i < 6; i++) Blade(mb, Vector3.zero, i * 60f, 0.12f, 0.1f, 0.22f, Vary(Hex("#5A9A3C"), random));
                    Stalk(mb, Vector3.zero, 0.25f, 0.02f, Stem);
                    Bloom(mb, new Vector3(0f, 0.25f, 0f), 0.06f, 10, Hex("#F7D23E"), Hex("#E8B81E"));
                    break;
            }
        }
    }
}
