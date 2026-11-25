// The junctions' traffic signals and stop signs in the game: the models from
// Tools~/streets/signals.py, stood where StreetFurniture says (the kerb at each
// lane's stop line, the arm reaching over the lane, the faces looking back down
// it at the cars coming), and each signal's lamps lit as the cars obey them:
// where the server runs the town, as it says they stand (the end of each delta,
// WorldSync.Showing: they change early for waiting cars); running it here, from
// the town's own stages (RoadNet.Stage). Red, then green, amber
// for its last seconds. Manage draws its lights at the same posts.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Life;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Porchlight.Rendering
{
    public sealed class Signage
    {
        /// <summary>Tiles a metre of the models: a touch under true scale (a tile is 4 m), as the street lamps are.</summary>
        const float SCALE = 0.2f;
        /// <summary>Where the head over the lane hangs from the pole (m, out along the arm): signals.py's ARM_HEAD_Y.</summary>
        const float ARM_HEAD = 3.6f;

        /// <summary>
        /// The lenses' middles in the model (m, Unity's terms: the faces look along -x), red, amber, green, for the
        /// head over the lane then the head on the pole. In step with signals.py's ARM_HEAD_*, POLE_HEAD_*, LENS_STEP.
        /// </summary>
        static readonly Vector3[] LENSES =
        {
            new Vector3(-0.17f, 4.98f, 3.6f), new Vector3(-0.17f, 4.65f, 3.6f), new Vector3(-0.17f, 4.32f, 3.6f),
            new Vector3(-0.44f, 3.53f, 0), new Vector3(-0.44f, 3.2f, 0), new Vector3(-0.44f, 2.87f, 0),
        };

        static readonly Color[] LIT = { new Color(1f, 0.12f, 0.08f), new Color(1f, 0.62f, 0.05f), new Color(0.15f, 1f, 0.45f) };

        sealed class Post
        {
            public Junction junction;
            /// <summary>Its junction's node (Bind finds it again in a fresh RoadNet of the same town).</summary>
            public int node;
            public int stage;
            /// <summary>Its junction's place among the town's signals (its lights on the wire).</summary>
            public int light;
            /// <summary>The lamps: red, amber, green over the lane, then the same on the pole.</summary>
            public Renderer[] lamps;
            public int showing = -1;
        }

        readonly GameObject root;
        readonly List<Post> posts = new List<Post>();
        /// <summary>The models' meshes, combined (static batching): a town has hundreds of stop signs. Freed with it.</summary>
        readonly List<Mesh> combined = new List<Mesh>();
        readonly List<GameObject> still = new List<GameObject>();
        float glowAt = -1;

        public int Signals => posts.Count;
        public int StopSigns { get; private set; }

        /// <summary>Posts and signs stood a step (MakeSteps), and batched in shares this big: a big town's hundreds at once stalled the frame you drove into it.</summary>
        const int A_STEP = 16;
        const int A_BATCH = 64;

        public Signage(RoadNet net, ModelLibrary library, Transform parent) : this(parent)
        {
            foreach (var _ in MakeSteps(net, library)) { }
        }

        Signage(Transform parent)
        {
            root = new GameObject("signage");
            root.transform.SetParent(parent, false);
        }

        /// <summary>The town's signals and stop signs stood a few a step (made before you get to its town: CountyDrive).</summary>
        public static Signage Stepwise(RoadNet net, ModelLibrary library, Transform parent, out IEnumerable<string> steps)
        {
            var s = new Signage(parent);
            steps = s.MakeSteps(net, library);
            return s;
        }

        IEnumerable<string> MakeSteps(RoadNet net, ModelLibrary library)
        {
            var signal = library.Get(Catalog.TRAFFIC_SIGNAL);
            var stop = library.Get(Catalog.STOP_SIGN);
            // Each lens its own colour, dark until lit (glowing with SetGlow).
            var lens = new Material[3];
            for (int c = 0; c < 3; c++) lens[c] = Materials.Glowing(LIT[c] * 0.22f);
            int n = 0;
            foreach (var p in StreetFurniture.Signals(net))
            {
                // Stretched along the arm, so the head hangs over the lane's middle however wide the road.
                float stretch = Mathf.Clamp((float)p.reach / (ARM_HEAD * SCALE), 0.7f, 1.6f);
                var t = Stand(signal, p.at, p.inward, p.along, new Vector3(SCALE, SCALE, SCALE * stretch));
                var lamps = new Renderer[LENSES.Length];
                for (int k = 0; k < LENSES.Length; k++)
                {
                    var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Object.Destroy(bulb.GetComponent<Collider>());
                    bulb.name = "lamp";
                    bulb.transform.SetParent(t, false);
                    bulb.transform.localPosition = LENSES[k];
                    bulb.transform.localScale = new Vector3(0.05f, 0.22f, 0.22f / stretch);
                    var r = bulb.GetComponent<Renderer>();
                    r.sharedMaterial = lens[k % 3];
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lamps[k] = r;
                }
                posts.Add(new Post { junction = net.junctions[p.node], node = p.node, stage = p.stage, light = p.light, lamps = lamps });
                if (++n % A_STEP == 0) yield return "signals" + n;
                if (still.Count >= A_BATCH)
                {
                    Batch();
                    yield return "batching signals";
                }
            }
            var stops = StreetFurniture.Stops(net);
            foreach (var s in stops)
            {
                Stand(stop, s.at, s.inward, s.along, Vector3.one * SCALE);
                if (++n % A_STEP == 0) yield return "stops" + n;
                if (still.Count >= A_BATCH)
                {
                    Batch();
                    yield return "batching stops";
                }
            }
            StopSigns = stops.Count;
            Batch();
        }

        /// <summary>
        /// The posts and signs stood since the last batch never move: one batch of them, less the lamps (each lit on
        /// its own). In shares, not the whole town's at once, so no one step is long.
        /// </summary>
        void Batch()
        {
            var parts = still.SelectMany(g => g.GetComponentsInChildren<MeshFilter>()).Select(f => f.gameObject).ToArray();
            still.Clear();
            if (parts.Length > 0) StaticBatchingUtility.Combine(parts, root);
            foreach (var g in parts)
            {
                var m = g.GetComponent<MeshFilter>().sharedMesh;
                if (m != null && m.name.StartsWith("Combined Mesh", StringComparison.Ordinal) && !combined.Contains(m)) combined.Add(m);
            }
        }

        /// <summary>Lit from now on by this road network's junctions: a fresh one of the same town (its own traffic's, after the town's been left and come back to).</summary>
        public void Bind(RoadNet net)
        {
            foreach (var p in posts) p.junction = net.junctions[p.node];
        }

        /// <summary>
        /// A model on the kerb at `at`: its +z (the arm) turned to `inward`, and its faces (-x) to look back down the
        /// lane, against `along` (mirrored if turning alone would have them look the way the cars go).
        /// </summary>
        Transform Stand(ModelLibrary.Model m, Vec2 at, Vec2 inward, Vec2 along, Vector3 scale)
        {
            var holder = new GameObject(m.name).transform;
            holder.SetParent(root.transform, false);
            holder.localPosition = Coords.Map(at.x, at.y);
            holder.localRotation = Coords.Yaw(DetMath.Atan2(inward.x, inward.y));
            // Turned so +z is `inward`, the model's -x looks along (inward.y, -inward.x) on the map.
            bool mirror = inward.y * along.x - inward.x * along.y > 0;
            holder.localScale = new Vector3(mirror ? -scale.x : scale.x, scale.y, scale.z);
            still.Add(Object.Instantiate(m.prefab, holder, false));
            return holder;
        }

        /// <summary>
        /// Lights each lamp showing: as `following` (the server's word) has it, else at `moment` (the town's clock,
        /// ms) by the town's own stages. Brighter at night.
        /// </summary>
        public void Update(long moment, double dusk, WorldSync following = null)
        {
            float glow = 1.2f + 2.2f * (float)dusk;
            bool relight = Mathf.Abs(glow - glowAt) > 0.05f;
            if (relight) glowAt = glow;
            foreach (var p in posts)
            {
                // The server's word on it if there is one; else the town's own (running it here, or an older server).
                int showing = following?.Showing(p.light, p.stage) ?? -1;
                if (showing < 0) showing = StreetFurniture.Showing(p.junction, p.stage, moment);
                if (showing == p.showing && !relight) continue;
                p.showing = showing;
                for (int k = 0; k < p.lamps.Length; k++)
                {
                    // Lamps are red, amber, green top to bottom; Showing is 0 red, 1 amber, 2 green.
                    int colour = k % 3;
                    Materials.SetGlow(p.lamps[k], LIT[colour], colour == showing ? glowAt : 0);
                }
            }
        }

        public void Dispose()
        {
            Object.Destroy(root);
            foreach (var m in combined) Object.Destroy(m);
        }
    }
}
