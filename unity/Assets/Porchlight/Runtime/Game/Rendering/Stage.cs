// Everything the town draws, submitted each frame: static meshes and
// instanced copies of models (as Three's Mesh and InstancedMesh), with the
// skinned people as ordinary renderers. The same list draws the sun's shadow
// map (Shadows.cs), so what casts a shadow is decided per item, as Three's
// castShadow is.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Drawable
    {
        public Mesh mesh;
        public int submesh;
        public Material material;
        /// <summary>Copies of the mesh (Unity space), and how many of them are drawn.</summary>
        public Matrix4x4[] matrices;
        public int count;
        /// <summary>Per-copy colour (Three's instanceColor), or null.</summary>
        public Vector4[] colors;
        public bool castShadow;
        public bool visible = true;

        public Drawable(Mesh mesh, Material material, Matrix4x4 matrix, bool castShadow, int submesh = 0)
        {
            this.mesh = mesh;
            this.material = material;
            matrices = new[] { matrix };
            count = 1;
            this.castShadow = castShadow;
            this.submesh = submesh;
        }

        public Drawable(Mesh mesh, Material material, Matrix4x4[] matrices, int count, bool castShadow, Vector4[] colors = null)
        {
            this.mesh = mesh;
            this.material = material;
            this.matrices = matrices;
            this.count = count;
            this.castShadow = castShadow;
            this.colors = colors;
        }

        public Matrix4x4 Matrix
        {
            get => matrices[0];
            set => matrices[0] = value;
        }
    }

    public sealed class Stage
    {
        const int Batch = 1023;
        static readonly int InstColor = Shader.PropertyToID("_InstColor");

        readonly List<Drawable> items = new List<Drawable>();
        /// <summary>Skinned people: drawn by Unity as renderers, added to the shadow map here.</summary>
        readonly List<Renderer> renderers = new List<Renderer>();
        readonly Matrix4x4[] batchMatrices = new Matrix4x4[Batch];
        readonly Vector4[] batchColors = new Vector4[Batch];
        readonly MaterialPropertyBlock props = new MaterialPropertyBlock();

        public IReadOnlyList<Drawable> Items => items;
        public IReadOnlyList<Renderer> Renderers => renderers;

        public Drawable Add(Drawable d)
        {
            items.Add(d);
            return d;
        }

        public void Remove(Drawable d) => items.Remove(d);

        public void AddRenderer(Renderer r) => renderers.Add(r);

        public void RemoveRenderer(Renderer r) => renderers.Remove(r);

        public void Clear()
        {
            items.Clear();
            renderers.Clear();
        }

        /// <summary>Submit everything visible to the camera for this frame.</summary>
        public void Draw(Camera camera)
        {
            foreach (var d in items)
            {
                if (!d.visible || d.count <= 0 || d.mesh == null) continue;
                if (d.count == 1 && d.colors == null)
                {
                    Graphics.DrawMesh(d.mesh, d.matrices[0], d.material, 0, camera, d.submesh, null, ShadowCastingMode.Off, false);
                    continue;
                }
                for (int start = 0; start < d.count; start += Batch)
                {
                    int n = Mathf.Min(Batch, d.count - start);
                    System.Array.Copy(d.matrices, start, batchMatrices, 0, n);
                    props.Clear();
                    if (d.colors != null)
                    {
                        System.Array.Copy(d.colors, start, batchColors, 0, n);
                        props.SetVectorArray(InstColor, batchColors);
                    }
                    Graphics.DrawMeshInstanced(d.mesh, d.submesh, d.material, batchMatrices, n, props, ShadowCastingMode.Off, false, 0, camera);
                }
            }
        }

        /// <summary>Record every shadow caster into a command buffer already set up for the light.</summary>
        public void DrawShadowCasters(CommandBuffer cmd)
        {
            foreach (var d in items)
            {
                if (!d.visible || !d.castShadow || d.count <= 0 || d.mesh == null) continue;
                if (d.count == 1)
                {
                    cmd.DrawMesh(d.mesh, d.matrices[0], d.material, d.submesh, Mats.DepthPass);
                    continue;
                }
                for (int start = 0; start < d.count; start += Batch)
                {
                    int n = Mathf.Min(Batch, d.count - start);
                    var m = new Matrix4x4[n];
                    System.Array.Copy(d.matrices, start, m, 0, n);
                    cmd.DrawMeshInstanced(d.mesh, d.submesh, d.material, Mats.DepthPass, m, n);
                }
            }
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                var mats = r.sharedMaterials;
                for (int s = 0; s < mats.Length; s++) if (mats[s] != null) cmd.DrawRenderer(r, mats[s], s, Mats.DepthPass);
            }
        }
    }
}
