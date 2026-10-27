// Static models baked down to model-space geometry, one part per material,
// as render/iso/assets.ts's flatten() does with Three: every mesh is
// transformed by its node's world matrix (in doubles, stored as floats), and
// meshes sharing a material are merged. Coordinates are glTF's (Three's):
// right-handed, y up; the game converts when it builds engine meshes.

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public sealed class ModelMaterial
    {
        public string name;
        /// <summary>Linear RGBA base colour factor.</summary>
        public double r = 1, g = 1, b = 1, a = 1;
        public double metalness = 1;
        public double roughness = 1;
        /// <summary>Base colour texture: the image's index in the file (or -1), and its uri (external images).</summary>
        public int image = -1;
        public string imageUri;
        public int magFilter = -1;
        public int minFilter = -1;
        public int wrapS = 10497;
        public int wrapT = 10497;
    }

    public sealed class ModelPart
    {
        public int material;
        public float[] pos;
        public float[] normal;
        public float[] uv;
        /// <summary>Triangle indices, or null when every three vertices make a triangle.</summary>
        public int[] index;
        /// <summary>Per-vertex window seeds etc. (buildings only; see WindowTags).</summary>
        public Porchlight.Explore.WindowTags.Result windows;

        public int VertexCount => pos.Length / 3;

        /// <summary>BufferGeometry.toNonIndexed: one vertex per triangle corner.</summary>
        public ModelPart NonIndexed()
        {
            if (index == null) return this;
            int n = index.Length;
            var o = new ModelPart { material = material, pos = new float[n * 3], normal = normal != null ? new float[n * 3] : null, uv = uv != null ? new float[n * 2] : null };
            for (int k = 0; k < n; k++)
            {
                int i = index[k];
                Array.Copy(pos, i * 3, o.pos, k * 3, 3);
                if (normal != null) Array.Copy(normal, i * 3, o.normal, k * 3, 3);
                if (uv != null) Array.Copy(uv, i * 2, o.uv, k * 2, 2);
            }
            return o;
        }
    }

    public sealed class ModelData
    {
        public string name;
        public List<ModelPart> parts = new List<ModelPart>();
        public List<ModelMaterial> materials = new List<ModelMaterial>();
        /// <summary>Bounding box of the whole model (model units).</summary>
        public double minX, minY, minZ, sizeX, sizeY, sizeZ;

        public static ModelMaterial ReadMaterial(Glb glb, int index)
        {
            var mat = new ModelMaterial();
            if (index < 0) return mat;
            var m = glb.Item("materials", index);
            mat.name = Json.Str(m, "name");
            var pbr = Json.Obj(m, "pbrMetallicRoughness");
            if (pbr != null)
            {
                var f = Json.Arr(pbr, "baseColorFactor");
                if (f != null)
                {
                    mat.r = (double)f[0];
                    mat.g = (double)f[1];
                    mat.b = (double)f[2];
                    mat.a = (double)f[3];
                }
                mat.metalness = Json.Num(pbr, "metallicFactor", 1);
                mat.roughness = Json.Num(pbr, "roughnessFactor", 1);
                var tex = Json.Obj(pbr, "baseColorTexture");
                if (tex != null)
                {
                    var t = glb.Item("textures", (int)Json.Num(tex, "index"));
                    mat.image = (int)Json.Num(t, "source", -1);
                    if (mat.image >= 0) glb.ImageBytes(mat.image, out mat.imageUri);
                    if (Json.Has(t, "sampler"))
                    {
                        var s = glb.Item("samplers", (int)Json.Num(t, "sampler"));
                        mat.magFilter = (int)Json.Num(s, "magFilter", -1);
                        mat.minFilter = (int)Json.Num(s, "minFilter", -1);
                        mat.wrapS = (int)Json.Num(s, "wrapS", 10497);
                        mat.wrapT = (int)Json.Num(s, "wrapT", 10497);
                    }
                }
            }
            return mat;
        }

        struct Geom
        {
            public float[] pos, normal, uv;
            public int[] index;
        }

        /// <summary>Bake a loaded scene's meshes into model space, merged per material (flatten in assets.ts).</summary>
        public static ModelData Flatten(string name, Glb glb)
        {
            var data = new ModelData { name = name };
            var groups = new Dictionary<int, List<Geom>>();
            var order = new List<int>();
            void Visit(int node, Mat4 parent)
            {
                var world = Mat4.Multiply(parent, glb.LocalMatrix(node));
                var nodeDef = glb.Item("nodes", node);
                if (Json.Has(nodeDef, "mesh"))
                {
                    var mesh = glb.Item("meshes", (int)Json.Num(nodeDef, "mesh"));
                    var normalMatrix = world.NormalMatrix();
                    foreach (var p in Json.Arr(mesh, "primitives"))
                    {
                        var attrs = Json.Obj(p, "attributes");
                        var g = new Geom();
                        var pos = glb.ReadFloats((int)Json.Num(attrs, "POSITION"), out _);
                        for (int i = 0; i < pos.Length; i += 3)
                        {
                            world.ApplyToPoint(pos[i], pos[i + 1], pos[i + 2], out double x, out double y, out double z);
                            pos[i] = (float)x;
                            pos[i + 1] = (float)y;
                            pos[i + 2] = (float)z;
                        }
                        g.pos = pos;
                        if (Json.Has(attrs, "NORMAL"))
                        {
                            var nrm = glb.ReadFloats((int)Json.Num(attrs, "NORMAL"), out _);
                            var e = normalMatrix;
                            for (int i = 0; i < nrm.Length; i += 3)
                            {
                                double x = nrm[i], y = nrm[i + 1], z = nrm[i + 2];
                                double nx = e[0] * x + e[3] * y + e[6] * z;
                                double ny = e[1] * x + e[4] * y + e[7] * z;
                                double nz = e[2] * x + e[5] * y + e[8] * z;
                                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                                double k = len == 0 ? 1 : 1 / len;
                                nrm[i] = (float)(nx * k);
                                nrm[i + 1] = (float)(ny * k);
                                nrm[i + 2] = (float)(nz * k);
                            }
                            g.normal = nrm;
                        }
                        if (Json.Has(attrs, "TEXCOORD_0")) g.uv = glb.ReadFloats((int)Json.Num(attrs, "TEXCOORD_0"), out _);
                        if (Json.Has(p, "indices")) g.index = glb.ReadInts((int)Json.Num(p, "indices"));
                        int material = Json.Has(p, "material") ? (int)Json.Num(p, "material") : -1;
                        if (!groups.TryGetValue(material, out var list))
                        {
                            groups[material] = list = new List<Geom>();
                            order.Add(material);
                        }
                        list.Add(g);
                    }
                }
                foreach (int child in glb.Children(node)) Visit(child, world);
            }
            foreach (int root in glb.Roots()) Visit(root, new Mat4());

            foreach (int material in order)
            {
                var geoms = groups[material];
                int matIndex = data.materials.Count;
                data.materials.Add(ReadMaterial(glb, material));
                if (geoms.Count == 1)
                {
                    var g = geoms[0];
                    data.parts.Add(new ModelPart { material = matIndex, pos = g.pos, normal = g.normal, uv = g.uv, index = g.index });
                    continue;
                }
                // mergeGeometries of normalised (indexed; position, normal, uv only) geometries, if their attributes match.
                bool hasNormal = geoms[0].normal != null, hasUv = geoms[0].uv != null;
                bool same = true;
                foreach (var g in geoms) if ((g.normal != null) != hasNormal || (g.uv != null) != hasUv) same = false;
                if (!same)
                {
                    foreach (var g in geoms)
                        data.parts.Add(new ModelPart { material = matIndex, pos = g.pos, normal = g.normal, uv = g.uv, index = g.index ?? Sequence(g.pos.Length / 3) });
                    continue;
                }
                var pos = new List<float>();
                var nrm = new List<float>();
                var uvs = new List<float>();
                var idx = new List<int>();
                foreach (var g in geoms)
                {
                    int baseVertex = pos.Count / 3;
                    pos.AddRange(g.pos);
                    if (hasNormal) nrm.AddRange(g.normal);
                    if (hasUv) uvs.AddRange(g.uv);
                    var gi = g.index ?? Sequence(g.pos.Length / 3);
                    foreach (int i in gi) idx.Add(i + baseVertex);
                }
                data.parts.Add(new ModelPart
                {
                    material = matIndex,
                    pos = pos.ToArray(),
                    normal = hasNormal ? nrm.ToArray() : null,
                    uv = hasUv ? uvs.ToArray() : null,
                    index = idx.ToArray(),
                });
            }

            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, z0 = double.PositiveInfinity;
            double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity, z1 = double.NegativeInfinity;
            foreach (var p in data.parts)
            {
                for (int i = 0; i < p.pos.Length; i += 3)
                {
                    x0 = Math.Min(x0, p.pos[i]);
                    y0 = Math.Min(y0, p.pos[i + 1]);
                    z0 = Math.Min(z0, p.pos[i + 2]);
                    x1 = Math.Max(x1, p.pos[i]);
                    y1 = Math.Max(y1, p.pos[i + 1]);
                    z1 = Math.Max(z1, p.pos[i + 2]);
                }
            }
            data.minX = x0;
            data.minY = y0;
            data.minZ = z0;
            data.sizeX = x1 - x0;
            data.sizeY = y1 - y0;
            data.sizeZ = z1 - z0;
            return data;
        }

        static int[] Sequence(int n)
        {
            var o = new int[n];
            for (int i = 0; i < n; i++) o[i] = i;
            return o;
        }
    }
}
