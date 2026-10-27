// Townsfolk and the player as RG Poly's rigged Cartoon City people. A
// CharacterAsset is one person's glTF read once (bones, skinned mesh, bind
// poses); each Avatar is its own copy, posed by a Mixer playing idle, walk and
// run blended by speed with the legs kept in step, a lean into speeding up and
// into turns, and jump, fall and landing poses with a squash and stretch. The
// player also carries an x-ray copy drawn in flat orange only where something
// hides them. A port of render/iso/characters.ts.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class CharacterAsset
    {
        public string name;
        public Glb glb;
        public List<int> roots;
        public Mesh mesh;
        public int[] joints;
        public int meshNode;
        public Material[] materials;
        /// <summary>How tall the model stands at rest by where its head starts, and its lowest point (Three space).</summary>
        public double headY;
        public double minY;
        public double maxY;
        public PeopleClips clips;

        public static CharacterAsset Create(string name, Glb glb, Texture2D atlas, PeopleClips clips)
        {
            var a = new CharacterAsset { name = name, glb = glb, roots = glb.Roots(), clips = clips };
            int nodes = glb.List("nodes").Count;
            var world = new Mat4[nodes];
            var parent = new int[nodes];
            for (int i = 0; i < nodes; i++) parent[i] = -1;
            for (int i = 0; i < nodes; i++) foreach (int c in glb.Children(i)) parent[c] = i;
            Mat4 World(int i)
            {
                if (world[i] != null) return world[i];
                var local = glb.LocalMatrix(i);
                world[i] = parent[i] < 0 ? local : Mat4.Multiply(World(parent[i]), local);
                return world[i];
            }
            a.meshNode = -1;
            for (int i = 0; i < nodes; i++) if (Json.Has(glb.Item("nodes", i), "skin")) a.meshNode = i;
            var meshDef = glb.Item("meshes", (int)Json.Num(glb.Item("nodes", a.meshNode), "mesh"));
            var skin = glb.Item("skins", (int)Json.Num(glb.Item("nodes", a.meshNode), "skin"));
            var jointList = Json.Arr(skin, "joints");
            a.joints = new int[jointList.Count];
            for (int j = 0; j < a.joints.Length; j++) a.joints[j] = (int)(double)jointList[j];
            var inv = glb.ReadFloats((int)Json.Num(skin, "inverseBindMatrices"), out _);
            var invBind = new Mat4[a.joints.Length];
            for (int j = 0; j < a.joints.Length; j++)
            {
                var e = new double[16];
                for (int k = 0; k < 16; k++) e[k] = inv[j * 16 + k];
                invBind[j] = new Mat4(e);
            }

            // One mesh, a submesh per primitive.
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var submeshes = new List<int[]>();
            var mats = new List<Material>();
            double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity;
            a.minY = double.PositiveInfinity;
            a.maxY = double.NegativeInfinity;
            var skinMats = new Mat4[a.joints.Length];
            for (int j = 0; j < a.joints.Length; j++) skinMats[j] = Mat4.Multiply(World(a.joints[j]), invBind[j]);
            foreach (var p in Json.Arr(meshDef, "primitives"))
            {
                var attrs = Json.Obj(p, "attributes");
                int baseVertex = positions.Count;
                var pos = glb.ReadFloats((int)Json.Num(attrs, "POSITION"), out _);
                var nrm = glb.ReadFloats((int)Json.Num(attrs, "NORMAL"), out _);
                var uv = glb.ReadFloats((int)Json.Num(attrs, "TEXCOORD_0"), out _);
                var js = glb.ReadFloats((int)Json.Num(attrs, "JOINTS_0"), out _);
                var ws = glb.ReadFloats((int)Json.Num(attrs, "WEIGHTS_0"), out _);
                int n = pos.Length / 3;
                for (int i = 0; i < n; i++)
                {
                    positions.Add(new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]));
                    normals.Add(new Vector3(nrm[i * 3], nrm[i * 3 + 1], -nrm[i * 3 + 2]));
                    uvs.Add(new Vector2(uv[i * 2], 1 - uv[i * 2 + 1]));
                    double sum = ws[i * 4] + ws[i * 4 + 1] + ws[i * 4 + 2] + ws[i * 4 + 3];
                    double k = sum > 0 ? 1 / sum : 1;
                    weights.Add(new BoneWeight
                    {
                        boneIndex0 = (int)js[i * 4],
                        boneIndex1 = (int)js[i * 4 + 1],
                        boneIndex2 = (int)js[i * 4 + 2],
                        boneIndex3 = (int)js[i * 4 + 3],
                        weight0 = (float)(ws[i * 4] * k),
                        weight1 = (float)(ws[i * 4 + 1] * k),
                        weight2 = (float)(ws[i * 4 + 2] * k),
                        weight3 = (float)(ws[i * 4 + 3] * k),
                    });
                    // The rest pose's skinned position, for the model's bounds (SkinnedMesh.computeBoundingBox).
                    double sx = 0, sy = 0, sz = 0;
                    for (int c = 0; c < 4; c++)
                    {
                        double w = ws[i * 4 + c] * k;
                        if (w == 0) continue;
                        skinMats[(int)js[i * 4 + c]].ApplyToPoint(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2], out double px, out double py, out double pz);
                        sx += px * w;
                        sy += py * w;
                        sz += pz * w;
                    }
                    x0 = Math.Min(x0, sx);
                    x1 = Math.Max(x1, sx);
                    a.minY = Math.Min(a.minY, sy);
                    a.maxY = Math.Max(a.maxY, sy);
                }
                var idx = glb.ReadInts((int)Json.Num(p, "indices"));
                var tris = new int[idx.Length];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    tris[t] = idx[t] + baseVertex;
                    tris[t + 1] = idx[t + 2] + baseVertex;
                    tris[t + 2] = idx[t + 1] + baseVertex;
                }
                submeshes.Add(tris);
                var mm = ModelData.ReadMaterial(glb, Json.Has(p, "material") ? (int)Json.Num(p, "material") : -1);
                var m = Mats.Standard(new Color((float)mm.r, (float)mm.g, (float)mm.b, 1), atlas, 1);
                Mats.SetReceiveShadow(m, false);
                m.name = $"{name}:{mm.name}";
                mats.Add(m);
            }
            var mesh = new Mesh { name = name, indexFormat = positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = weights.ToArray();
            mesh.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s, false);
            var bindposes = new Matrix4x4[a.joints.Length];
            for (int j = 0; j < a.joints.Length; j++) bindposes[j] = Space.M(invBind[j]);
            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();
            a.mesh = mesh;
            a.materials = mats.ToArray();

            int head = -1;
            for (int i = 0; i < nodes; i++) if (Clip.Sanitize(glb.NodeName(i)) == "Head") head = i;
            a.headY = head >= 0 ? World(head).e[13] : double.NaN;
            return a;
        }
    }

    /// <summary>One rigged person in the scene.</summary>
    public sealed class Avatar : IPoseTarget
    {
        public const double PERSON_HEIGHT = 0.36;
        const double NATURAL_WALK = 0.53;
        const double NATURAL_RUN = 1.1;
        const double WALK_TOP = 1.7;
        const double RUN_FROM = 2.5;
        const double CADENCE = 0.55;
        const double RUN_LEAN = 0.12;
        const double ACCEL_LEAN = 0.012;
        const double BANK = 0.05;
        const double MAX_TILT = 0.3;
        public const int XRAY_ORDER = 10;
        public const int PLAYER_ORDER = 20;
        const double FEET = 0.035;
        const double HEAD_SHARE = 0.71;

        public readonly GameObject root;
        readonly Transform body;
        readonly Transform model;
        readonly Transform[] nodes;
        readonly Dictionary<string, int> byName = new Dictionary<string, int>();
        readonly SkinnedMeshRenderer renderer;
        SkinnedMeshRenderer xray;
        readonly Stage stage;
        readonly Mixer mixer;
        readonly Porchlight.Core.Action idle, walk, run, jump, fall, crouch;

        // The root's and body's pose in Three's terms.
        double rootYaw;
        double bodyX, bodyZ;
        double moveWeight, runWeight, stride, airWeight, fallWeight;
        bool wasAir;
        double lastRise;
        double sinceLanding = double.PositiveInfinity;
        double impact;
        double sinceTakeoff = double.PositiveInfinity;
        double lastSpeed;
        double? lastHeading;
        double accel, turn;
        bool visible = true;

        public Avatar(CharacterAsset asset, double height, double phase, Transform parent, Stage stage)
        {
            this.stage = stage;
            root = new GameObject("person " + asset.name);
            root.transform.SetParent(parent, false);
            body = new GameObject("body").transform;
            body.SetParent(root.transform, false);
            model = new GameObject("model").transform;
            model.SetParent(body, false);

            var glb = asset.glb;
            int count = glb.List("nodes").Count;
            nodes = new Transform[count];
            void Build(int i, Transform parentT)
            {
                var go = new GameObject(glb.NodeName(i));
                var t = go.transform;
                t.SetParent(parentT, false);
                glb.NodeTRS(i, out var tr, out var r, out var s);
                t.localPosition = Space.P(tr[0], tr[1], tr[2]);
                t.localRotation = new Quaternion((float)-r.x, (float)-r.y, (float)r.z, (float)r.w);
                t.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
                nodes[i] = t;
                var key = Clip.Sanitize(glb.NodeName(i));
                if (!byName.ContainsKey(key)) byName[key] = i;
                foreach (int c in glb.Children(i)) Build(c, t);
            }
            foreach (int r in asset.roots) Build(r, model);

            var bones = new Transform[asset.joints.Length];
            for (int j = 0; j < bones.Length; j++) bones[j] = nodes[asset.joints[j]];
            renderer = nodes[asset.meshNode].gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = asset.mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];
            renderer.sharedMaterials = asset.materials;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = false;
            renderer.updateWhenOffscreen = false;
            renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 8);
            stage.AddRenderer(renderer);

            // Sized by where the head starts, so a tall hat or a quiff doesn't make anyone smaller.
            double h = !double.IsNaN(asset.headY) ? asset.headY / HEAD_SHARE : Math.Max(1e-3, asset.maxY - asset.minY);
            double k = height / h;
            model.localScale = new Vector3((float)k, (float)k, (float)k);
            model.localPosition = new Vector3(0, (float)(-asset.minY * height / h), 0);
            // MODEL_TURN: a half turn about y (Three's q = (0, 1, 0, 0), ours the same).
            model.localRotation = new Quaternion(0, -1, 0, 0);

            mixer = new Mixer(this);
            Porchlight.Core.Action Act(string name)
            {
                var clip = asset.clips.Get(name);
                if (clip == null) return null;
                var a = mixer.Play(clip);
                a.weight = 0;
                return a;
            }
            idle = Act("idle");
            walk = Act("walk");
            run = Act("sprint");
            jump = Act("jump");
            fall = Act("fall");
            crouch = Act("crouch");
            foreach (var a in new[] { jump, crouch })
            {
                if (a == null) continue;
                a.loopOnce = true;
                a.clampWhenFinished = true;
            }
            if (idle != null) idle.weight = 1;
            stride = phase;
            if (idle != null) idle.time = phase * idle.clip.duration;
        }

        // IPoseTarget: bones by Three's sanitised name, poses in Three's space.
        public int Find(string bone) => byName.TryGetValue(bone, out int i) ? i : -1;

        public void Get(int bone, bool position, double[] v)
        {
            var t = nodes[bone];
            if (position)
            {
                var p = t.localPosition;
                v[0] = p.x;
                v[1] = p.y;
                v[2] = -p.z;
            }
            else
            {
                var q = t.localRotation;
                v[0] = -q.x;
                v[1] = -q.y;
                v[2] = q.z;
                v[3] = q.w;
            }
        }

        public void Set(int bone, bool position, double[] v)
        {
            var t = nodes[bone];
            if (position) t.localPosition = new Vector3((float)v[0], (float)v[1], (float)-v[2]);
            else t.localRotation = new Quaternion((float)-v[0], (float)-v[1], (float)v[2], (float)v[3]);
        }

        public bool Visible
        {
            get => visible;
            set
            {
                if (visible == value) return;
                visible = value;
                root.SetActive(value);
            }
        }

        static double Smoothstep(double a, double b, double x)
        {
            double t = Math.Min(1, Math.Max(0, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        static double Ease(double from, double to, double rate, double dt) => from + (to - from) * (1 - Math.Exp(-rate * dt));

        /// <summary>Pose the model where the walker is.</summary>
        public void Update(Walker w, double dt)
        {
            double k = Math.Max(1e-3, dt);
            root.transform.localPosition = Space.Map(w.x, w.y, FEET + w.lift);
            root.transform.localScale = Vector3.one * (float)w.scale;
            rootYaw += Orbit.ShortestTurn(rootYaw, w.heading) * Math.Min(1, dt * (12 + w.speed * 2));
            SetYaw(root.transform, rootYaw);

            double pace = w.speed / w.scale;
            accel = Ease(accel, (pace - lastSpeed) / k, 8, dt);
            lastSpeed = pace;
            double turned = lastHeading == null ? 0 : Orbit.ShortestTurn(lastHeading.Value, w.heading);
            lastHeading = w.heading;
            turn = Ease(turn, turned / k, 10, dt);

            bool air = w.lift > 0;
            if (air && !wasAir)
            {
                jump?.Restart();
                sinceTakeoff = 0;
            }
            if (!air && wasAir)
            {
                sinceLanding = 0;
                impact = Math.Min(1, -lastRise / 2.2);
                crouch?.Restart();
            }
            wasAir = air;
            lastRise = w.rise;
            sinceLanding += dt;
            sinceTakeoff += dt;
            airWeight = Ease(airWeight, air ? 1 : 0, air ? 20 : 12, dt);
            fallWeight = Ease(fallWeight, air && w.rise < 0.2 && fall != null ? 1 : 0, 10, dt);
            double landing = crouch != null ? Math.Max(0, 1 - sinceLanding / 0.22) * (0.35 + 0.65 * impact) : 0;

            bool moving = pace > 0.05;
            moveWeight = Ease(moveWeight, moving ? 1 : 0, moving ? 10 : 7, dt);
            runWeight = Ease(runWeight, run != null ? Smoothstep(WALK_TOP, RUN_FROM, pace) : 0, 6, dt);
            if (moving)
            {
                double walkRate = (1 / (walk?.clip.duration ?? 1)) * Math.Pow(pace / NATURAL_WALK, CADENCE);
                double runRate = (1 / (run?.clip.duration ?? 1)) * Math.Pow(pace / NATURAL_RUN, CADENCE);
                stride = (stride + dt * Math.Min(6, walkRate + (runRate - walkRate) * runWeight)) % 1;
            }
            double ground = (1 - airWeight) * (1 - landing);
            if (walk != null)
            {
                walk.time = stride * walk.clip.duration;
                walk.weight = ground * moveWeight * (1 - runWeight);
                walk.timeScale = 0;
            }
            if (run != null)
            {
                run.time = stride * run.clip.duration;
                run.weight = ground * moveWeight * runWeight;
                run.timeScale = 0;
            }
            if (idle != null) idle.weight = ground * (1 - moveWeight);
            if (jump != null) jump.weight = airWeight * (1 - fallWeight);
            if (fall != null) fall.weight = airWeight * fallWeight;
            if (crouch != null) crouch.weight = (1 - airWeight) * landing;
            mixer.Update(dt);

            double runLean = moveWeight * Smoothstep(0.3, RUN_FROM, pace);
            double lean = Math.Max(-MAX_TILT, Math.Min(MAX_TILT, runLean * RUN_LEAN + accel * ACCEL_LEAN * (1 - airWeight)));
            double bank = Math.Max(-MAX_TILT, Math.Min(MAX_TILT, -turn * BANK * runLean * (1 - airWeight)));
            bodyX = Ease(bodyX, lean, 10, dt);
            bodyZ = Ease(bodyZ, bank, 10, dt);
            body.localRotation = EulerXZ(bodyX, bodyZ);
            double stretch = 0.12 * Math.Max(0, 1 - sinceTakeoff / 0.18);
            double squash = 0.2 * impact * Math.Sin(Math.PI * Math.Min(1, sinceLanding / 0.2));
            double sy = 1 + stretch - squash;
            float side = (float)(1 / Math.Sqrt(sy));
            body.localScale = new Vector3(side, (float)sy, side);
        }

        /// <summary>A Three rotation about y by `yaw`, in Unity's terms.</summary>
        public static void SetYaw(Transform t, double yaw)
        {
            var q = Quat.AboutY(yaw);
            t.localRotation = new Quaternion(0, (float)-q.y, 0, (float)q.w);
        }

        public void SnapYaw(double yaw)
        {
            rootYaw = yaw;
            SetYaw(root.transform, yaw);
        }

        /// <summary>Three's Euler(x, 0, z, 'XYZ') as a Unity rotation.</summary>
        static Quaternion EulerXZ(double x, double z)
        {
            double c1 = Math.Cos(x / 2), s1 = Math.Sin(x / 2);
            double c3 = Math.Cos(z / 2), s3 = Math.Sin(z / 2);
            // c2 = 1, s2 = 0
            double qx = s1 * c3;
            double qy = -s1 * s3;
            double qz = c1 * s3;
            double qw = c1 * c3;
            return new Quaternion((float)-qx, (float)-qy, (float)qz, (float)qw);
        }

        /// <summary>The x-ray copy: drawn in orange only where something already drawn is in front, before the person themself.</summary>
        public void AddXray(Material xrayMaterial)
        {
            var go = new GameObject("xray");
            go.transform.SetParent(renderer.transform, false);
            xray = go.AddComponent<SkinnedMeshRenderer>();
            xray.sharedMesh = renderer.sharedMesh;
            xray.bones = renderer.bones;
            xray.rootBone = renderer.rootBone;
            var mats = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = xrayMaterial;
            xray.sharedMaterials = mats;
            xray.shadowCastingMode = ShadowCastingMode.Off;
            xray.updateWhenOffscreen = false;
            xray.localBounds = new Bounds(Vector3.zero, Vector3.one * 8);
            var own = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < own.Length; i++)
            {
                own[i] = new Material(renderer.sharedMaterials[i]) { renderQueue = (int)RenderQueue.Geometry + PLAYER_ORDER };
            }
            renderer.sharedMaterials = own;
        }

        public bool XrayVisible
        {
            set
            {
                if (xray != null) xray.enabled = value;
            }
        }

        public void Dispose()
        {
            stage.RemoveRenderer(renderer);
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }
}
