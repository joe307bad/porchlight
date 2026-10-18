using System.Collections.Generic;
using Porchlight.Rendering;
using Porchlight.Shared.Content;
using UnityEngine;

namespace Porchlight.Avatars
{
    /// <summary>
    /// Head-and-shoulders pictures of the characters, for the character select cards and the walking
    /// bar. Each is taken once and kept.
    /// </summary>
    public static class Portraits
    {
        public const int Width = 768;
        public const int Height = 484;

        private static readonly Dictionary<string, RenderTexture> Taken = new Dictionary<string, RenderTexture>();

        public static RenderTexture Get(CharacterType type, Material material)
        {
            if (Taken.TryGetValue(type.Id, out var texture) && texture != null) return texture;

            // Face -Z, towards the sun, so the face is lit; frame from the chest up, a little to one side.
            var rig = CharacterRig.Create(type, material);
            rig.transform.SetPositionAndRotation(Studio.Spot, Quaternion.Euler(0f, 180f, 0f));
            rig.Tick(0f, true, 0f);

            texture = Studio.Photograph(rig.gameObject, Studio.Spot + Vector3.up * (type.Height * 0.8f),
                Quaternion.Euler(6f, 18f, 0f) * Vector3.back, type.Height * 0.6f, Width, Height,
                LowPolyMeshes.Hex("#CFE1F7"), type.Id + " portrait");
            Taken[type.Id] = texture;
            return texture;
        }
    }
}
