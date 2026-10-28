// Opening up a building's ground floor (Library runs it on every building once
// its windows are split off). The kits' doors are panels laid on solid walls,
// so each door of the room behind the street front becomes a part of its own,
// pivoted where it swings (or, a garage door, where it tips up from) over a
// hole cut through the wall behind it; the glass in that room's walls becomes a
// part of its own, which the town makes see-through when the room is shown; and
// what was found is kept with the model: the room, its doors and windows, the
// rest of the glass, and the walls at walking height. A building with no door
// on its room gets one, in the middle of the room's front wall.
// Everything here is in the prefab root's space: the kits' fronts face -z.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.EditorTools
{
    public static class Buildings
    {
        /// <summary>How tall the kits' ground floors are.</summary>
        const float STOREY = 0.4f;
        /// <summary>Where walls are taken to stop people walking through them.</summary>
        const float WAIST = 0.2f;
        /// <summary>How far a door or window may stand off its wall's line.</summary>
        const float ON_WALL = 0.05f;
        const float FLAT = 0.005f;

        struct Vert
        {
            public Vector3 p, n;
            public Vector2 uv;
        }

        sealed class Tri
        {
            public int sub;
            public Vert a, b, c;
            public Vector3 Normal => Vector3.Cross(b.p - a.p, c.p - a.p).normalized;
            public bool Upright => Mathf.Abs(Normal.y) < 0.35f;
            public IEnumerable<Vector3> Points => new[] { a.p, b.p, c.p };
        }

        /// <summary>A flat upright panel: a door leaf or a pane of glass.</summary>
        sealed class Panel
        {
            public List<Tri> tris = new List<Tri>();
            /// <summary>True when it lies in a front or back wall (z constant), false in a side wall (x constant).</summary>
            public bool facesZ;
            public float plane, a0, a1, y0, y1;
            /// <summary>+1 if it faces +z (or +x), -1 if -z (or -x).</summary>
            public int outward;
            public bool glass;
        }

        public static ModelLibrary.Ground Open(GameObject go, string name, Material glassMaterial, string outDir)
        {
            var filters = go.GetComponentsInChildren<MeshFilter>();
            if (filters.Length != 1) throw new Exception($"{name}: a building should be one mesh, not {filters.Length}");
            var f = filters[0];
            var r = f.GetComponent<MeshRenderer>();
            var materials = r.sharedMaterials;
            var toRoot = go.transform.worldToLocalMatrix * f.transform.localToWorldMatrix;
            if (toRoot.determinant <= 0) throw new Exception($"{name}: mirrored mesh");
            var tris = Read(f.sharedMesh, toRoot, materials, glassMaterial);

            var panels = Panels(tris, materials, glassMaterial);
            var leaves = Leaves(panels);
            var inLeaf = new HashSet<Tri>(leaves.SelectMany(l => l.tris));
            var segs = Section(tris, WAIST);

            // The room: the biggest clear rectangle with the front door in its front wall (or anywhere, without one).
            var front = leaves.Where(l => l.facesZ && l.outward < 0).OrderBy(l => l.plane).FirstOrDefault();
            Vector4? found = front != null ? BestRect(segs, rect => SideOf(front, rect) == 0) : null;
            var room = found ?? BestRect(segs, null) ?? throw new Exception($"{name}: no room found on the ground floor");

            var doors = leaves.Where(l => SideOf(l, room) >= 0).ToList();
            if (doors.Count == 0) doors.Add(MadeDoor(room));
            foreach (var l in leaves.Except(doors)) inLeaf.ExceptWith(l.tris);
            foreach (var d in doors) TakeFittings(d, room, tris, inLeaf);
            var rest = tris.Where(t => !inLeaf.Contains(t)).ToList();
            foreach (var d in doors) rest = CutHole(rest, d, room);

            var ground = new ModelLibrary.Ground { room = room, storey = STOREY };
            var groundGlass = new List<Tri>();
            foreach (var p in panels.Where(p => p.glass && !p.tris.Any(inLeaf.Contains)))
            {
                int side = SideOf(p, room);
                // Glass behind a doorway (where one had to be made) is cut through like the wall.
                if (doors.Any(d => d.facesZ == p.facesZ && Mathf.Abs(d.plane - p.plane) < 0.04f && d.a0 < p.a1 && p.a0 < d.a1)) continue;
                if (side >= 0 && p.y1 <= STOREY + 0.02f)
                {
                    ground.windows.Add(new ModelLibrary.Pane { side = side, plane = p.plane, a0 = p.a0, a1 = p.a1, y0 = p.y0, y1 = p.y1 });
                    groundGlass.AddRange(p.tris);
                }
                else ground.panes.Add(new ModelLibrary.Pane { side = Facing(p), plane = p.plane, a0 = p.a0, a1 = p.a1, y0 = p.y0, y1 = p.y1 });
            }
            var inGlass = new HashSet<Tri>(groundGlass);
            rest = rest.Where(t => !inGlass.Contains(t)).ToList();
            ground.walls = Merge(Section(rest.Concat(groundGlass), WAIST));

            string stem = $"{outDir}/{name.Replace('/', '-')}";
            var shell = f.sharedMesh;
            if (!AssetDatabase.GetAssetPath(shell).StartsWith(outDir))
            {
                // Still the imported mesh (no windows were split off): edit a copy of it.
                shell = UnityEngine.Object.Instantiate(shell);
                AssetDatabase.DeleteAsset($"{stem}-0.asset");
                AssetDatabase.CreateAsset(shell, $"{stem}-0.asset");
                f.sharedMesh = shell;
            }
            Write(shell, rest, materials.Length, toRoot.inverse, Vector3.zero);
            EditorUtility.SetDirty(shell);
            for (int k = 0; k < doors.Count; k++)
            {
                var d = doors[k];
                bool lifts = d.a1 - d.a0 > 0.3f;
                var partner = doors.FirstOrDefault(o => o != d && o.facesZ == d.facesZ && Mathf.Abs(o.plane - d.plane) < 0.02f
                    && (Mathf.Abs(o.a0 - d.a1) < 0.05f || Mathf.Abs(o.a1 - d.a0) < 0.05f));
                float hinge = partner != null && partner.a0 < d.a0 ? d.a1 : d.a0;
                float along = lifts ? (d.a0 + d.a1) / 2 : hinge;
                float up = lifts ? d.y1 : 0;
                var pivot = d.facesZ ? new Vector3(along, up, d.plane) : new Vector3(d.plane, up, along);
                string part = "door " + k;
                var mesh = new Mesh { name = $"{name} {part}", indexFormat = IndexFormat.UInt32 };
                Write(mesh, d.tris, materials.Length, Matrix4x4.identity, pivot);
                Part(go, part, mesh, pivot, materials, $"{stem}-{part.Replace(' ', '-')}.asset");
                ground.doors.Add(new ModelLibrary.GroundDoor { part = part, side = SideOf(d, room), a0 = d.a0, a1 = d.a1, top = d.y1, lifts = lifts });
            }
            if (groundGlass.Count > 0)
            {
                var mesh = new Mesh { name = $"{name} ground glass", indexFormat = IndexFormat.UInt32 };
                Write(mesh, groundGlass.Select(t => new Tri { sub = 0, a = t.a, b = t.b, c = t.c }).ToList(), 1, Matrix4x4.identity, Vector3.zero);
                Part(go, ModelLibrary.Ground.GLASS, mesh, Vector3.zero, new[] { glassMaterial }, $"{stem}-ground-glass.asset");
            }
            return ground;
        }

        static void Part(GameObject go, string name, Mesh mesh, Vector3 at, Material[] materials, string path)
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            var part = new GameObject(name);
            part.transform.SetParent(go.transform, false);
            part.transform.localPosition = at;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = part.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        // Reading and writing meshes ----------------------------------------------------------

        static List<Tri> Read(Mesh mesh, Matrix4x4 toRoot, Material[] materials, Material glass)
        {
            var pos = mesh.vertices;
            var nor = mesh.normals;
            var uv = mesh.uv;
            Vert V(int i) => new Vert
            {
                p = toRoot.MultiplyPoint3x4(pos[i]),
                n = nor.Length == pos.Length ? toRoot.MultiplyVector(nor[i]).normalized : Vector3.up,
                uv = uv.Length == pos.Length ? uv[i] : Vector2.zero,
            };
            var o = new List<Tri>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var ix = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < ix.Length; t += 3) o.Add(new Tri { sub = s, a = V(ix[t]), b = V(ix[t + 1]), c = V(ix[t + 2]) });
            }
            return o;
        }

        /// <summary>Fill `mesh` with `tris` (in root space, less `origin`), keeping their submeshes.</summary>
        static void Write(Mesh mesh, List<Tri> tris, int subs, Matrix4x4 fromRoot, Vector3 origin)
        {
            var pos = new List<Vector3>();
            var nor = new List<Vector3>();
            var uv = new List<Vector2>();
            var ix = Enumerable.Range(0, subs).Select(_ => new List<int>()).ToArray();
            foreach (var t in tris)
            {
                foreach (var v in new[] { t.a, t.b, t.c })
                {
                    ix[t.sub].Add(pos.Count);
                    pos.Add(fromRoot.MultiplyPoint3x4(v.p - origin));
                    nor.Add(fromRoot.MultiplyVector(v.n).normalized);
                    uv.Add(v.uv);
                }
            }
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetNormals(nor);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = subs;
            for (int s = 0; s < subs; s++) mesh.SetTriangles(ix[s], s, false);
            mesh.RecalculateBounds();
        }

        // Finding doors and glass --------------------------------------------------------------

        /// <summary>The flat upright panels: faces of one colour joined at their corners, lying in one plane facing one of the four ways.</summary>
        static List<Panel> Panels(List<Tri> tris, Material[] materials, Material glass)
        {
            var parent = Enumerable.Range(0, tris.Count).ToArray();
            int Find(int a)
            {
                while (parent[a] != a) a = parent[a] = parent[parent[a]];
                return a;
            }
            var seen = new Dictionary<(int, int, int, int, int), int>();
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                var mid = (t.a.uv + t.b.uv + t.c.uv) / 3;
                int cell = Mathf.FloorToInt(mid.x * 16) * 100 + Mathf.FloorToInt(mid.y * 4) + t.sub * 10000;
                foreach (var p in t.Points)
                {
                    var key = (cell, 0, Mathf.RoundToInt(p.x * 1e4f), Mathf.RoundToInt(p.y * 1e4f), Mathf.RoundToInt(p.z * 1e4f));
                    if (seen.TryGetValue(key, out int j)) parent[Find(i)] = Find(j);
                    else seen[key] = i;
                }
            }
            var o = new List<Panel>();
            foreach (var g in Enumerable.Range(0, tris.Count).GroupBy(Find))
            {
                var ts = g.Select(i => tris[i]).ToList();
                if (!ts.All(t => t.Upright)) continue;
                var pts = ts.SelectMany(t => t.Points).ToList();
                var lo = new Vector3(pts.Min(p => p.x), pts.Min(p => p.y), pts.Min(p => p.z));
                var hi = new Vector3(pts.Max(p => p.x), pts.Max(p => p.y), pts.Max(p => p.z));
                bool facesZ = hi.z - lo.z < FLAT;
                if (!facesZ && hi.x - lo.x >= FLAT) continue;
                float facing = ts.Sum(t => facesZ ? t.Normal.z : t.Normal.x);
                o.Add(new Panel
                {
                    tris = ts,
                    facesZ = facesZ,
                    plane = facesZ ? lo.z : lo.x,
                    a0 = facesZ ? lo.x : lo.z,
                    a1 = facesZ ? hi.x : hi.z,
                    y0 = lo.y,
                    y1 = hi.y,
                    outward = facing >= 0 ? 1 : -1,
                    glass = materials[ts[0].sub] == glass,
                });
            }
            return o;
        }

        /// <summary>
        /// Door leaves: panels standing on the ground, door-sized (garage doors are wide,
        /// double doors come in narrow pairs). A door's own glass goes with it.
        /// </summary>
        static List<Panel> Leaves(List<Panel> panels)
        {
            var leaves = panels.Where(p => p.y0 < 0.04f && p.y1 - p.y0 > 0.18f && p.y1 - p.y0 < 0.4f && p.a1 - p.a0 > 0.07f && p.a1 - p.a0 < 0.6f).ToList();
            foreach (var g in leaves.Where(l => l.glass).ToList())
            {
                var door = leaves.FirstOrDefault(l => !l.glass && l.facesZ == g.facesZ && Mathf.Abs(l.plane - g.plane) < 0.04f && g.a0 >= l.a0 - 0.01f && g.a1 <= l.a1 + 0.01f);
                if (door == null) continue;
                door.tris.AddRange(g.tris);
                leaves.Remove(g);
            }
            // And the glass set into a door (it doesn't reach the ground).
            foreach (var g in panels.Where(p => p.glass && !leaves.Contains(p)))
            {
                var door = leaves.FirstOrDefault(l => !l.glass && l.facesZ == g.facesZ && Mathf.Abs(l.plane - g.plane) < 0.04f
                    && g.a0 >= l.a0 - 0.01f && g.a1 <= l.a1 + 0.01f && g.y1 <= l.y1 + 0.01f);
                door?.tris.AddRange(g.tris);
            }
            return leaves;
        }

        /// <summary>
        /// What's fixed to a door goes with it: its knob and handle, the reveal around its
        /// window. Anything wholly within its outline, between the wall and just in front of it.
        /// </summary>
        static void TakeFittings(Panel door, Vector4 room, List<Tri> tris, HashSet<Tri> taken)
        {
            float wall = door.facesZ ? (door.outward < 0 ? room.y : room.w) : (door.outward < 0 ? room.x : room.z);
            float front = (door.plane - wall) * door.outward + 0.03f;
            foreach (var t in tris)
            {
                if (taken.Contains(t)) continue;
                bool inside = t.Points.All(p =>
                {
                    float lat = door.facesZ ? p.x : p.z;
                    float depth = ((door.facesZ ? p.z : p.x) - wall) * door.outward;
                    return lat > door.a0 + 0.002f && lat < door.a1 - 0.002f && p.y > -0.001f && p.y < door.y1 - 0.002f && depth > -0.01f && depth < front;
                });
                if (!inside) continue;
                door.tris.Add(t);
                taken.Add(t);
            }
        }

        /// <summary>Which of the room's walls a panel is in (0 front, 1 back, 2 left, 3 right), or -1.</summary>
        static int SideOf(Panel p, Vector4 room)
        {
            int side;
            if (p.facesZ)
            {
                if (p.a0 < room.x - 0.01f || p.a1 > room.z + 0.01f) return -1;
                side = Mathf.Abs(p.plane - room.y) < ON_WALL ? 0 : Mathf.Abs(p.plane - room.w) < ON_WALL ? 1 : -1;
            }
            else
            {
                if (p.a0 < room.y - 0.01f || p.a1 > room.w + 0.01f) return -1;
                side = Mathf.Abs(p.plane - room.x) < ON_WALL ? 2 : Mathf.Abs(p.plane - room.z) < ON_WALL ? 3 : -1;
            }
            if (side < 0) return -1;
            bool outward = side % 2 == 0 ? p.outward < 0 : p.outward > 0;
            return outward ? side : -1;
        }

        /// <summary>Which way a panel faces, numbered as room sides are.</summary>
        static int Facing(Panel p) => (p.facesZ ? 0 : 2) + (p.outward > 0 ? 1 : 0);

        /// <summary>A plain door in the middle of the room's front wall, coloured from the kit's dark grey.</summary>
        static Panel MadeDoor(Vector4 room)
        {
            float mid = (room.x + room.z) / 2, z = room.y - 0.012f;
            float x0 = mid - 0.07f, x1 = mid + 0.07f, top = 0.27f;
            var uv = new Vector2(3.5f / 16, 0.375f);
            Vert V(float x, float y) => new Vert { p = new Vector3(x, y, z), n = Vector3.back, uv = uv };
            var a = V(x0, 0);
            var b = V(x0, top);
            var c = V(x1, top);
            var d = V(x1, 0);
            var door = new Panel { facesZ = true, plane = z, a0 = x0, a1 = x1, y0 = 0, y1 = top, outward = -1 };
            door.tris.Add(new Tri { a = a, b = b, c = c });
            door.tris.Add(new Tri { a = a, b = c, c = d });
            return door;
        }

        // The room ----------------------------------------------------------------------------

        /// <summary>Where the upright faces cross height y, as segments on the ground (x, z).</summary>
        static List<(Vector2 a, Vector2 b)> Section(IEnumerable<Tri> tris, float y)
        {
            var o = new List<(Vector2, Vector2)>();
            var cut = new List<Vector2>(2);
            foreach (var t in tris)
            {
                if (!t.Upright) continue;
                cut.Clear();
                var ps = new[] { t.a.p, t.b.p, t.c.p };
                for (int k = 0; k < 3; k++)
                {
                    var p = ps[k];
                    var q = ps[(k + 1) % 3];
                    if ((p.y - y) * (q.y - y) >= 0) continue;
                    float s = (y - p.y) / (q.y - p.y);
                    cut.Add(new Vector2(p.x + s * (q.x - p.x), p.z + s * (q.z - p.z)));
                }
                if (cut.Count == 2) o.Add((cut[0], cut[1]));
            }
            return o;
        }

        /// <summary>Whether a segment runs through the inside of rectangle (x0, z0, x1, z1).</summary>
        static bool Crosses((Vector2 a, Vector2 b) s, Vector4 r)
        {
            const float e = 0.01f;
            float x0 = r.x + e, z0 = r.y + e, x1 = r.z - e, z1 = r.w - e;
            float t0 = 0, t1 = 1, dx = s.b.x - s.a.x, dz = s.b.y - s.a.y;
            foreach (var (p, q) in new[] { (-dx, s.a.x - x0), (dx, x1 - s.a.x), (-dz, s.a.y - z0), (dz, z1 - s.a.y) })
            {
                if (Mathf.Abs(p) < 1e-9f)
                {
                    if (q < 0) return false;
                    continue;
                }
                float k = q / p;
                if (p < 0) t0 = Mathf.Max(t0, k);
                else t1 = Mathf.Min(t1, k);
            }
            return t0 < t1;
        }

        /// <summary>The biggest rectangle between walls (no wall through it), at least 0.3 each way, that `need` allows.</summary>
        static Vector4? BestRect(List<(Vector2 a, Vector2 b)> segs, Func<Vector4, bool> need)
        {
            var xs = segs.Where(s => Mathf.Abs(s.a.x - s.b.x) < 0.002f).SelectMany(s => new[] { s.a.x, s.b.x }).Select(v => (float)Math.Round(v, 3)).Distinct().OrderBy(v => v).ToArray();
            var zs = segs.Where(s => Mathf.Abs(s.a.y - s.b.y) < 0.002f).SelectMany(s => new[] { s.a.y, s.b.y }).Select(v => (float)Math.Round(v, 3)).Distinct().OrderBy(v => v).ToArray();
            float best = 0;
            Vector4? o = null;
            foreach (float x0 in xs)
                foreach (float x1 in xs.Where(x => x > x0 + 0.3f))
                    foreach (float z0 in zs)
                        foreach (float z1 in zs.Where(z => z > z0 + 0.3f))
                        {
                            float area = (x1 - x0) * (z1 - z0);
                            if (area <= best) continue;
                            var r = new Vector4(x0, z0, x1, z1);
                            if (need != null && !need(r)) continue;
                            if (segs.Any(s => Crosses(s, r))) continue;
                            best = area;
                            o = r;
                        }
            return o;
        }

        /// <summary>Upright walls as segments, those along x or z joined where they meet or overlap.</summary>
        static List<Vector4> Merge(List<(Vector2 a, Vector2 b)> segs)
        {
            var o = new List<Vector4>();
            foreach (bool alongX in new[] { true, false })
            {
                var lines = segs.Where(s => alongX ? Mathf.Abs(s.a.y - s.b.y) < 0.002f : Mathf.Abs(s.a.x - s.b.x) < 0.002f && Mathf.Abs(s.a.y - s.b.y) >= 0.002f)
                    .Select(s => alongX
                        ? (at: (float)Math.Round(s.a.y, 3), from: Mathf.Min(s.a.x, s.b.x), to: Mathf.Max(s.a.x, s.b.x))
                        : (at: (float)Math.Round(s.a.x, 3), from: Mathf.Min(s.a.y, s.b.y), to: Mathf.Max(s.a.y, s.b.y)))
                    .GroupBy(l => l.at);
                foreach (var line in lines)
                {
                    float from = float.NaN, to = float.NaN;
                    void Flush()
                    {
                        if (float.IsNaN(from)) return;
                        o.Add(alongX ? new Vector4(from, line.Key, to, line.Key) : new Vector4(line.Key, from, line.Key, to));
                    }
                    foreach (var l in line.OrderBy(l => l.from))
                    {
                        if (!float.IsNaN(from) && l.from <= to + 0.01f) to = Mathf.Max(to, l.to);
                        else
                        {
                            Flush();
                            from = l.from;
                            to = l.to;
                        }
                    }
                    Flush();
                }
            }
            foreach (var s in segs)
                if (Mathf.Abs(s.a.y - s.b.y) >= 0.002f && Mathf.Abs(s.a.x - s.b.x) >= 0.002f) o.Add(new Vector4(s.a.x, s.a.y, s.b.x, s.b.y));
            return o;
        }

        // Cutting doorways --------------------------------------------------------------------

        /// <summary>
        /// Take a door's shape out of every wall face behind it (and anything else in its
        /// plane, between the room's wall line and the door), up to the door's top.
        /// </summary>
        static List<Tri> CutHole(List<Tri> tris, Panel door, Vector4 room)
        {
            float wall = door.facesZ ? (door.outward < 0 ? room.y : room.w) : (door.outward < 0 ? room.x : room.z);
            float reach = (door.plane - wall) * door.outward + 0.01f;
            float A0 = door.a0 - 0.002f, A1 = door.a1 + 0.002f, T = door.y1 + 0.002f;
            float Lat(Vert v) => door.facesZ ? v.p.x : v.p.z;
            var o = new List<Tri>();
            foreach (var t in tris)
            {
                var n = t.Normal;
                bool parallel = Mathf.Abs(door.facesZ ? n.z : n.x) > 0.9f;
                float depth = ((door.facesZ ? t.a.p.z + t.b.p.z + t.c.p.z : t.a.p.x + t.b.p.x + t.c.p.x) / 3 - wall) * door.outward;
                var vs = new[] { t.a, t.b, t.c };
                if (!parallel || depth < -0.03f || depth > reach || vs.Max(Lat) <= A0 || vs.Min(Lat) >= A1 || vs.Min(v => v.p.y) >= T)
                {
                    o.Add(t);
                    continue;
                }
                var poly = vs.ToList();
                Emit(o, t.sub, Clip(poly, v => Lat(v) - A0));
                Emit(o, t.sub, Clip(poly, v => A1 - Lat(v)));
                Emit(o, t.sub, Clip(Clip(Clip(poly, v => A0 - Lat(v)), v => Lat(v) - A1), v => T - v.p.y));
            }
            return o;
        }

        /// <summary>The part of a convex polygon where f(v) &lt;= 0.</summary>
        static List<Vert> Clip(List<Vert> poly, Func<Vert, float> f)
        {
            var o = new List<Vert>();
            for (int k = 0; k < poly.Count; k++)
            {
                var a = poly[k];
                var b = poly[(k + 1) % poly.Count];
                float fa = f(a), fb = f(b);
                if (fa <= 0) o.Add(a);
                if ((fa <= 0) != (fb <= 0))
                {
                    float s = fa / (fa - fb);
                    o.Add(new Vert { p = Vector3.Lerp(a.p, b.p, s), n = Vector3.Lerp(a.n, b.n, s).normalized, uv = Vector2.Lerp(a.uv, b.uv, s) });
                }
            }
            return o;
        }

        static void Emit(List<Tri> o, int sub, List<Vert> poly)
        {
            for (int k = 2; k < poly.Count; k++)
            {
                var t = new Tri { sub = sub, a = poly[0], b = poly[k - 1], c = poly[k] };
                if (Vector3.Cross(t.b.p - t.a.p, t.c.p - t.a.p).sqrMagnitude > 1e-12f) o.Add(t);
            }
        }
    }
}
