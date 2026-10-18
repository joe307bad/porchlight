using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Geometry;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Placeholder art made in code from the shared catalog. When real models arrive, swap
    /// <see cref="MeshCache.Building"/> for a lookup from type id to prefab. Nothing else needs to change.
    /// </summary>
    public static class LowPolyMeshes
    {
        private static readonly Color Plinth = Hex("#8E8A82");
        private static readonly Color Door = Hex("#5A4636");
        private static readonly Color Window = Hex("#3E5C76");
        private static readonly Color Trunk = Hex("#6B4F3A");
        private static readonly Color Leaves = Hex("#4F8A3E");
        private static readonly Color Chimney = Hex("#7A5248");
        private static readonly Color GrassA = Hex("#9DC183");
        private static readonly Color GrassB = Hex("#93B97A");
        private static readonly Color Outskirts = Hex("#7FA06A");

        public static Mesh Building(BuildingType type)
        {
            var mb = new MeshBuilder();
            var wall = Hex(type.WallColor);
            var roof = Hex(type.RoofColor);
            float hw = type.Width * 0.5f, hd = type.Depth * 0.5f, h = type.Height;

            if (type.Decoration == Decoration.Trees)
            {
                // Parks: a grass slab with trees.
                mb.Box(Vector3.zero, type.Width, h, type.Depth, wall, roof);
                for (var i = -1; i <= 1; i++)
                {
                    for (var j = -1; j <= 1; j++)
                    {
                        if ((i + j) % 2 != 0) continue;
                        var height = 4.5f + 1.2f * Mathf.Abs(Mathf.Sin(i * 12.9898f + j * 78.233f));
                        Tree(mb, new Vector3(i * hw * 0.55f, h, j * hd * 0.55f), height);
                    }
                }

                return mb.ToMesh(name: type.Id);
            }

            mb.Box(Vector3.zero, type.Width + 0.4f, 0.25f, type.Depth + 0.4f, Plinth, Plinth);
            mb.Box(new Vector3(0f, 0.25f, 0f), type.Width, h - 0.25f, type.Depth, wall, type.Roof == RoofStyle.Flat ? roof : wall);

            // Front door, on local +Z.
            const float inset = 0.03f;
            mb.Quad(new Vector3(-0.6f, 0.25f, hd + inset), new Vector3(-0.6f, 2.3f, hd + inset),
                new Vector3(0.6f, 2.3f, hd + inset), new Vector3(0.6f, 0.25f, hd + inset), Door, Vector3.forward);

            if (type.WindowBands)
            {
                // One dark band per 3 m floor, skipping the ground floor.
                for (var y = 3.6f; y + 1.4f < h - 0.4f; y += 3f)
                {
                    mb.Sides(Vector3.zero, hw + inset, hd + inset, y, y + 1.3f, Window);
                }
            }
            else if (type.Roof == RoofStyle.Gabled)
            {
                // A window either side of the door.
                foreach (var x in new[] { -hw * 0.55f, hw * 0.55f })
                {
                    mb.Quad(new Vector3(x - 0.6f, 1.2f, hd + inset), new Vector3(x - 0.6f, 2.2f, hd + inset),
                        new Vector3(x + 0.6f, 2.2f, hd + inset), new Vector3(x + 0.6f, 1.2f, hd + inset), Window, Vector3.forward);
                }
            }

            if (type.Roof == RoofStyle.Gabled) GabledRoof(mb, hw, hd, h, wall, roof);

            if (type.Decoration == Decoration.Chimney)
            {
                var chimneyHeight = type.Roof == RoofStyle.Gabled ? Mathf.Min(type.Width, type.Depth) * 0.45f : 2.5f;
                mb.Box(new Vector3(hw * 0.5f, h - 0.5f, -hd * 0.3f), 1.1f, chimneyHeight + 0.5f, 1.1f, Chimney, Chimney);
            }

            return mb.ToMesh(name: type.Id);
        }

        /// <summary>Stand-in for building types this client doesn't know yet (from a newer catalog).</summary>
        public static Mesh Unknown()
        {
            var mb = new MeshBuilder();
            mb.Box(Vector3.zero, 6f, 6f, 6f, new Color(1f, 0f, 1f), new Color(1f, 0f, 1f));
            return mb.ToMesh(name: "unknown");
        }

        private static void GabledRoof(MeshBuilder mb, float hw, float hd, float wallTop, Color wall, Color roof)
        {
            // Ridge runs along local X, which is why gabled types are wider than deep.
            const float overhang = 0.35f;
            float x0 = -hw - overhang, x1 = hw + overhang, z0 = -hd - overhang, z1 = hd + overhang;
            var ridge = wallTop + Mathf.Min(hw, hd) * 0.8f;
            var eave = wallTop;

            mb.Quad(new Vector3(x0, eave, z1), new Vector3(x1, eave, z1), new Vector3(x1, ridge, 0f), new Vector3(x0, ridge, 0f), roof, new Vector3(0f, 1f, 1f));
            mb.Quad(new Vector3(x0, eave, z0), new Vector3(x1, eave, z0), new Vector3(x1, ridge, 0f), new Vector3(x0, ridge, 0f), roof, new Vector3(0f, 1f, -1f));

            // Gable ends, filled in wall colour so the roof doesn't look hollow from the side.
            mb.Triangle(new Vector3(-hw, eave, -hd), new Vector3(-hw, eave, hd), new Vector3(-hw, ridge, 0f), wall, Vector3.left);
            mb.Triangle(new Vector3(hw, eave, -hd), new Vector3(hw, eave, hd), new Vector3(hw, ridge, 0f), wall, Vector3.right);
        }

        private static void Tree(MeshBuilder mb, Vector3 basePosition, float height)
        {
            var trunkHeight = height * 0.3f;
            mb.Box(basePosition, 0.5f, trunkHeight, 0.5f, Trunk, Trunk);

            const int sides = 6;
            const float radius = 1.7f;
            var apex = basePosition + Vector3.up * height;
            var ringY = basePosition.y + trunkHeight;
            for (var i = 0; i < sides; i++)
            {
                var a0 = i * Mathf.PI * 2f / sides;
                var a1 = (i + 1) * Mathf.PI * 2f / sides;
                var p0 = new Vector3(basePosition.x + Mathf.Cos(a0) * radius, ringY, basePosition.z + Mathf.Sin(a0) * radius);
                var p1 = new Vector3(basePosition.x + Mathf.Cos(a1) * radius, ringY, basePosition.z + Mathf.Sin(a1) * radius);
                var outward = (p0 + p1) * 0.5f - new Vector3(basePosition.x, ringY, basePosition.z) + Vector3.up * 0.5f;
                mb.Triangle(p0, p1, apex, Leaves * (0.9f + 0.1f * (i % 2)), outward);
            }
        }

        /// <summary>Patchwork grass for the buildable area, following the terrain, plus darker countryside around it so the edge isn't a cliff.</summary>
        public static Mesh Ground(TownDto town)
        {
            var mb = new MeshBuilder();
            var half = town.Size * 0.5f;

            // A frame around the town, not a sheet under it, so ground dug below 0 isn't covered up.
            const float outskirtScale = 3f;
            var o = half * outskirtScale;
            Quad(-o, -o, o, -half);
            Quad(-o, half, o, o);
            Quad(-o, -half, -half, half);
            Quad(half, -half, o, half);
            void Quad(float x0, float z0, float x1, float z1) =>
                mb.Quad(new Vector3(x0, -0.05f, z0), new Vector3(x0, -0.05f, z1), new Vector3(x1, -0.05f, z1), new Vector3(x1, -0.05f, z0), Outskirts, Vector3.up);

            // Patches of a lighter and darker green, about 16 m across.
            const float tile = 16f;
            var tiles = Mathf.Max(1, Mathf.RoundToInt(town.Size / tile));
            var step = town.Size / tiles;
            Color Patch(float x, float z)
            {
                var i = Mathf.FloorToInt((x + half) / step + 0.001f);
                var j = Mathf.FloorToInt((z + half) / step + 0.001f);
                return ((i * 7 + j * 13) % 5 == 0) ? GrassB : GrassA;
            }

            if (TownTerrain.IsShaped(town))
            {
                TerrainMeshes.Surface(mb, town, 0f, Patch);
                return mb.ToMesh(name: "ground");
            }

            // Flat: one quad per patch is enough.
            for (var i = 0; i < tiles; i++)
            {
                for (var j = 0; j < tiles; j++)
                {
                    float x0 = -half + i * step, z0 = -half + j * step;
                    mb.Quad(new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z0 + step), new Vector3(x0 + step, 0f, z0 + step), new Vector3(x0 + step, 0f, z0), Patch(x0, z0), Vector3.up);
                }
            }

            return mb.ToMesh(name: "ground");
        }

        /// <summary>All roads as one mesh, lying on the ground: a strip per segment plus a disc per node to fill the joints.</summary>
        public static void Roads(TownDto town, Mesh target)
        {
            var mb = new MeshBuilder();
            var nodeWidths = new Dictionary<string, (Vec2 Position, float Width, Color Color)>();

            foreach (var road in TownRules.Roads(town))
            {
                var color = Hex(road.Type.Color);
                TerrainMeshes.Strip(mb, town, road.A, road.B, road.Type.Width, color, 0.04f);

                Remember(nodeWidths, road.Segment.StartNodeId, road.A, road.Type.Width, color);
                Remember(nodeWidths, road.Segment.EndNodeId, road.B, road.Type.Width, color);
            }

            foreach (var node in nodeWidths.Values)
            {
                TerrainMeshes.Disc(mb, town, node.Position, node.Width * 0.5f, node.Color, 0.045f);
            }

            mb.ToMesh(target, "roads");
        }

        private static void Remember(Dictionary<string, (Vec2, float, Color)> nodes, string id, Vec2 position, float width, Color color)
        {
            if (!nodes.TryGetValue(id, out var existing) || existing.Item2 < width) nodes[id] = (position, width, color);
        }

        /// <summary>Parses #RRGGBB. Vertex colours aren't converted by Unity, so convert to linear here when the project is linear.</summary>
        public static Color Hex(string hex)
        {
            if (!ColorUtility.TryParseHtmlString(hex, out var color)) color = Color.magenta;
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
        }
    }

    public static class MeshCache
    {
        private static readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();

        public static Mesh Building(string typeId)
        {
            if (Meshes.TryGetValue(typeId ?? "", out var mesh) && mesh != null) return mesh;

            mesh = Catalog.TryGetBuilding(typeId, out var type) ? LowPolyMeshes.Building(type) : LowPolyMeshes.Unknown();
            Meshes[typeId ?? ""] = mesh;
            return mesh;
        }
    }
}
