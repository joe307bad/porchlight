// A reader for binary glTF (.glb): the JSON header and binary chunk, with
// accessors read out as plain arrays, and the node hierarchy, meshes,
// materials, skins and animations the town's models use. Engine-free; the
// game turns what it reads into meshes and textures.

using System;
using System.Collections.Generic;
using System.Text;

namespace Porchlight.Core
{
    public sealed class Glb
    {
        public readonly Dictionary<string, object> json;
        public readonly byte[] bin;

        public Glb(byte[] data)
        {
            if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67) throw new FormatException("not a glb file");
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                int length = (int)BitConverter.ToUInt32(data, offset);
                uint type = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;
                if (type == 0x4E4F534A) json = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(data, offset, length));
                else if (type == 0x004E4942)
                {
                    bin = new byte[length];
                    Buffer.BlockCopy(data, offset, bin, 0, length);
                }
                offset += length;
            }
            if (json == null) throw new FormatException("glb has no JSON chunk");
        }

        public List<object> List(string key) => Json.Arr(json, key) ?? new List<object>();

        public Dictionary<string, object> Item(string key, int index) => (Dictionary<string, object>)List(key)[index];

        static int Components(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            "MAT3" => 9,
            "MAT2" => 4,
            _ => throw new FormatException("accessor type " + type),
        };

        static int ComponentSize(int componentType) => componentType switch
        {
            5120 => 1,
            5121 => 1,
            5122 => 2,
            5123 => 2,
            5125 => 4,
            5126 => 4,
            _ => throw new FormatException("component type " + componentType),
        };

        /// <summary>An accessor's values as floats (normalized integers scaled as glTF says), with its component count.</summary>
        public float[] ReadFloats(int accessorIndex, out int components)
        {
            var acc = Item("accessors", accessorIndex);
            components = Components(Json.Str(acc, "type"));
            int count = (int)Json.Num(acc, "count");
            int ctype = (int)Json.Num(acc, "componentType");
            bool normalized = acc.TryGetValue("normalized", out var n) && n is bool b && b;
            var o = new float[count * components];
            if (!Json.Has(acc, "bufferView")) return o;
            var view = Item("bufferViews", (int)Json.Num(acc, "bufferView"));
            int baseOffset = (int)Json.Num(view, "byteOffset", 0) + (int)Json.Num(acc, "byteOffset", 0);
            int csize = ComponentSize(ctype);
            int stride = (int)Json.Num(view, "byteStride", 0);
            if (stride == 0) stride = csize * components;
            for (int i = 0; i < count; i++)
            {
                int p = baseOffset + i * stride;
                for (int c = 0; c < components; c++)
                {
                    int q = p + c * csize;
                    float v;
                    switch (ctype)
                    {
                        case 5126: v = BitConverter.ToSingle(bin, q); break;
                        case 5121: v = normalized ? bin[q] / 255f : bin[q]; break;
                        case 5120: v = normalized ? Math.Max((sbyte)bin[q] / 127f, -1f) : (sbyte)bin[q]; break;
                        case 5123: v = normalized ? BitConverter.ToUInt16(bin, q) / 65535f : BitConverter.ToUInt16(bin, q); break;
                        case 5122: v = normalized ? Math.Max(BitConverter.ToInt16(bin, q) / 32767f, -1f) : BitConverter.ToInt16(bin, q); break;
                        default: v = BitConverter.ToUInt32(bin, q); break;
                    }
                    o[i * components + c] = v;
                }
            }
            return o;
        }

        public int[] ReadInts(int accessorIndex)
        {
            var acc = Item("accessors", accessorIndex);
            int components = Components(Json.Str(acc, "type"));
            int count = (int)Json.Num(acc, "count");
            int ctype = (int)Json.Num(acc, "componentType");
            var view = Item("bufferViews", (int)Json.Num(acc, "bufferView"));
            int baseOffset = (int)Json.Num(view, "byteOffset", 0) + (int)Json.Num(acc, "byteOffset", 0);
            int csize = ComponentSize(ctype);
            int stride = (int)Json.Num(view, "byteStride", 0);
            if (stride == 0) stride = csize * components;
            var o = new int[count * components];
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    int q = baseOffset + i * stride + c * csize;
                    o[i * components + c] = ctype switch
                    {
                        5121 => bin[q],
                        5120 => (sbyte)bin[q],
                        5123 => BitConverter.ToUInt16(bin, q),
                        5122 => BitConverter.ToInt16(bin, q),
                        _ => (int)BitConverter.ToUInt32(bin, q),
                    };
                }
            }
            return o;
        }

        /// <summary>The bytes of an image embedded in a buffer view (null for an external uri).</summary>
        public byte[] ImageBytes(int imageIndex, out string uri)
        {
            var image = Item("images", imageIndex);
            uri = Json.Str(image, "uri");
            if (!Json.Has(image, "bufferView")) return null;
            var view = Item("bufferViews", (int)Json.Num(image, "bufferView"));
            int offset = (int)Json.Num(view, "byteOffset", 0);
            int length = (int)Json.Num(view, "byteLength");
            var o = new byte[length];
            Buffer.BlockCopy(bin, offset, o, 0, length);
            return o;
        }

        /// <summary>A node's local matrix (its matrix, or its translation, rotation and scale).</summary>
        public Mat4 LocalMatrix(int nodeIndex)
        {
            var node = Item("nodes", nodeIndex);
            var m = Json.Arr(node, "matrix");
            if (m != null)
            {
                var a = new double[16];
                for (int i = 0; i < 16; i++) a[i] = (double)m[i];
                return new Mat4(a);
            }
            NodeTRS(nodeIndex, out var t, out var r, out var s);
            return Mat4.Compose(t[0], t[1], t[2], r, s[0], s[1], s[2]);
        }

        public void NodeTRS(int nodeIndex, out double[] t, out Quat r, out double[] s)
        {
            var node = Item("nodes", nodeIndex);
            t = new double[] { 0, 0, 0 };
            s = new double[] { 1, 1, 1 };
            r = Quat.Identity;
            var tt = Json.Arr(node, "translation");
            if (tt != null) for (int i = 0; i < 3; i++) t[i] = (double)tt[i];
            var ss = Json.Arr(node, "scale");
            if (ss != null) for (int i = 0; i < 3; i++) s[i] = (double)ss[i];
            var rr = Json.Arr(node, "rotation");
            if (rr != null) r = new Quat((double)rr[0], (double)rr[1], (double)rr[2], (double)rr[3]);
        }

        public List<int> Children(int nodeIndex)
        {
            var o = new List<int>();
            var c = Json.Arr(Item("nodes", nodeIndex), "children");
            if (c != null) foreach (var x in c) o.Add((int)(double)x);
            return o;
        }

        /// <summary>The default scene's root nodes.</summary>
        public List<int> Roots()
        {
            int scene = (int)Json.Num(json, "scene", 0);
            var o = new List<int>();
            var scenes = List("scenes");
            if (scenes.Count == 0) return o;
            var nodes = Json.Arr(scenes[scene], "nodes");
            if (nodes != null) foreach (var x in nodes) o.Add((int)(double)x);
            return o;
        }

        public string NodeName(int nodeIndex) => Json.Str(Item("nodes", nodeIndex), "name") ?? "";
    }
}
