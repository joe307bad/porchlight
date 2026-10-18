using System.Collections.Generic;
using Porchlight.Shared.Content;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    /// <summary>Small three-quarter pictures of each building type, for the builder's cards. Taken once and kept.</summary>
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
    }
}
