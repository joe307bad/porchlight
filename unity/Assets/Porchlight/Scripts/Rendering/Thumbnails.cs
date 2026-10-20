using System.Collections.Generic;
using Porchlight.Shared.Content;
using Porchlight.Shared.Model;
using Porchlight.Shared.Rules;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>Small three-quarter pictures of each building type and furnishing, for the builder's cards. Taken once and kept.</summary>
    public static class Thumbnails
    {
        public const int Width = 336;
        public const int Height = 192;

        private static readonly Dictionary<string, RenderTexture> Taken = new Dictionary<string, RenderTexture>();

        public static RenderTexture Building(BuildingType type, Material material)
        {
            if (Taken.TryGetValue(type.Id, out var texture) && texture != null) return texture;

            var subject = new GameObject(type.Id);
            subject.transform.SetPositionAndRotation(Studio.Spot, Quaternion.Euler(0f, 200f, 0f));
            subject.AddComponent<MeshFilter>().sharedMesh = MeshCache.Building(type.Id);
            var renderer = subject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            // Fit the whole building, roof included, whatever its size.
            var bounds = renderer.bounds;
            var size = Mathf.Max(bounds.size.y * 1.25f, Mathf.Max(bounds.size.x, bounds.size.z) * 0.72f);
            texture = Studio.Photograph(subject, bounds.center, Quaternion.Euler(28f, 0f, 0f) * Vector3.back, size,
                Width, Height, LowPolyMeshes.Hex("#CFE1F7"), type.Id + " thumbnail");
            Taken[type.Id] = texture;
            return texture;
        }

        /// <summary>A piece of furniture on a patch of floor.</summary>
        public static RenderTexture Furniture(FurnitureType type, Material material)
        {
            var key = "furniture:" + type.Id;
            if (Taken.TryGetValue(key, out var texture) && texture != null) return texture;

            var mb = new MeshBuilder();
            var floor = LowPolyMeshes.Hex("#C9A27A");
            var size = Mathf.Max(type.Width, type.Depth) + 0.5f;
            mb.Box(new Vector3(0f, -0.04f, 0f), size, 0.04f, size, floor, floor);
            if (type.Placement == FurniturePlacement.DresserOrWall)
            {
                // Hung on a bit of wall.
                var wall = LowPolyMeshes.Hex("#F6EFE2");
                mb.Box(new Vector3(0f, 0f, -type.Depth * 0.5f - 0.05f), size, 2f, 0.1f, wall, wall);
            }

            InteriorMeshes.Furniture(mb, type, 0f, 0f, 0f, 0f, false, type.Placement == FurniturePlacement.DresserOrWall);
            return Photograph(key, mb.ToMesh(name: key), material, 180f + 25f);
        }

        /// <summary>A door or window in a stretch of wall.</summary>
        public static RenderTexture Opening(OpeningType type, Material material)
        {
            var key = "opening:" + type.Id;
            if (Taken.TryGetValue(key, out var texture) && texture != null) return texture;

            var mb = new MeshBuilder();
            var wall = new CottageInterior.WallLine("thumb", true, 0f, -1.1f, 1.1f, false);
            var openings = new List<OpeningDto> { new OpeningDto { Id = "o", TypeId = type.Id, WallId = "thumb", At = 0f } };
            var plaster = LowPolyMeshes.Hex("#F6EFE2");
            InteriorMeshes.WallWithOpenings(mb, wall, openings, 0f, 2.5f, plaster, plaster);
            var floor = LowPolyMeshes.Hex("#C9A27A");
            mb.Box(new Vector3(0f, -0.04f, 0f), 2.6f, 0.04f, 1.2f, floor, floor);
            if (type.IsDoor)
            {
                // The leaf, a little open.
                var leaf = InteriorMeshes.DoorLeaf(type.Width, InteriorMeshes.InsideDoorColor);
                var vertices = leaf.vertices;
                var turn = Quaternion.Euler(0f, 35f, 0f);
                for (var i = 0; i < vertices.Length; i++) vertices[i] = turn * vertices[i] + new Vector3(-type.Width * 0.5f, 0f, 0f);
                var colors = leaf.colors;
                var normals = leaf.normals;
                var triangles = leaf.triangles;
                for (var t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    mb.Triangle(vertices[a], vertices[b], vertices[c], colors[a], turn * normals[a]);
                }

                Object.Destroy(leaf);
            }

            return Photograph(key, mb.ToMesh(name: key), material, 180f + 20f);
        }

        /// <summary>Takes and keeps a picture of a temporary mesh, then throws the mesh away.</summary>
        private static RenderTexture Photograph(string key, Mesh mesh, Material material, float turn)
        {
            var subject = new GameObject(key);
            subject.transform.SetPositionAndRotation(Studio.Spot, Quaternion.Euler(0f, turn, 0f));
            subject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = subject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            var bounds = renderer.bounds;
            var size = Mathf.Max(bounds.size.y * 1.3f, Mathf.Max(bounds.size.x, bounds.size.z) * 0.75f);
            var texture = Studio.Photograph(subject, bounds.center, Quaternion.Euler(30f, 0f, 0f) * Vector3.back, size,
                Width, Height, LowPolyMeshes.Hex("#CFE1F7"), key + " thumbnail");
            Object.Destroy(mesh);
            Taken[key] = texture;
            return texture;
        }
    }
}
