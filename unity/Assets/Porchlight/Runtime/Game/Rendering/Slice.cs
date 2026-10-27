// Cutting a placed model off below a height: the city kits' buildings have
// their own ground floors, but the town builds its own (doors, rooms and
// shopfronts), so each building shell keeps only what stands above them. Every
// triangle is clipped against the plane; nothing below it is drawn.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public static class Slice
    {
        /// <summary>Replace the meshes under `go` with copies cut off below world height `y`.</summary>
        public static void Above(GameObject go, float y)
        {
            foreach (var f in go.GetComponentsInChildren<MeshFilter>())
            {
                var src = f.sharedMesh;
                if (src == null || !src.isReadable) continue;
                var toWorld = f.transform.localToWorldMatrix;
                var toLocal = f.transform.worldToLocalMatrix;
                // The plane in the mesh's own space: points p with dot(n, p) >= d are kept.
                var n = toWorld.transpose.MultiplyVector(Vector3.up);
                // (toWorld^T applied to the world plane's normal gives the local one.)
                var origin = toLocal.MultiplyPoint3x4(new Vector3(0, y, 0));
                float d = Vector3.Dot(n, origin);
                f.sharedMesh = Cut(src, n, d);
            }
        }

        struct V
        {
            public Vector3 p, n;
            public Vector2 uv;
        }

        static Mesh Cut(Mesh src, Vector3 n, float d)
        {
            var pos = src.vertices;
            var nor = src.normals;
            var uvs = src.uv;
            bool hasNormals = nor.Length == pos.Length;
            bool hasUv = uvs.Length == pos.Length;
            var outPos = new List<Vector3>();
            var outNor = new List<Vector3>();
            var outUv = new List<Vector2>();
            var mesh = new Mesh { name = src.name + " (cut)", indexFormat = IndexFormat.UInt32 };
            var subs = new List<List<int>>();
            V At(int i) => new V { p = pos[i], n = hasNormals ? nor[i] : Vector3.up, uv = hasUv ? uvs[i] : Vector2.zero };
            int Emit(V v)
            {
                outPos.Add(v.p);
                outNor.Add(v.n);
                outUv.Add(v.uv);
                return outPos.Count - 1;
            }
            V Lerp(V a, V b, float t) => new V { p = Vector3.Lerp(a.p, b.p, t), n = Vector3.Lerp(a.n, b.n, t).normalized, uv = Vector2.Lerp(a.uv, b.uv, t) };
            var poly = new List<V>(4);
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                var o = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    var tri = new[] { At(tris[t]), At(tris[t + 1]), At(tris[t + 2]) };
                    poly.Clear();
                    for (int k = 0; k < 3; k++)
                    {
                        var a = tri[k];
                        var b = tri[(k + 1) % 3];
                        float da = Vector3.Dot(n, a.p) - d, db = Vector3.Dot(n, b.p) - d;
                        if (da >= 0) poly.Add(a);
                        if ((da >= 0) != (db >= 0)) poly.Add(Lerp(a, b, da / (da - db)));
                    }
                    if (poly.Count < 3) continue;
                    int first = Emit(poly[0]);
                    int prev = Emit(poly[1]);
                    for (int k = 2; k < poly.Count; k++)
                    {
                        int cur = Emit(poly[k]);
                        o.Add(first);
                        o.Add(prev);
                        o.Add(cur);
                        prev = cur;
                    }
                }
                subs.Add(o);
            }
            mesh.SetVertices(outPos);
            mesh.SetNormals(outNor);
            mesh.SetUVs(0, outUv);
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
