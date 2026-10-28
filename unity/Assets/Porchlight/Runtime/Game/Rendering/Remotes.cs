// Other players in the town, as the server sends them: each drawn as the
// same rigged character the townsfolk use, posed from moves played back on
// their own clock a little in the past (Interp), with their name on a badge
// of their own colour floating over them through buildings. At the wheel they're drawn as their car, and a
// passenger is out of sight inside it, their tag stacked over the car with
// the driver's. Their cars are drawn as yours is (CarView).

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Net;
using TMPro;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>What the room says about another player (server/src/multiplayer/schema.ts).</summary>
    public sealed class RemoteState
    {
        public string key;
        public string name;
        public double x, y, z;
        public double rotY;
        public int anim;
        public int car;
        public string ride;
        public string appearance;
        public bool away;
        public double t;
    }

    public struct RemoteDriver
    {
        public string key;
        public string name;
        public double x, y, heading;
        public int car;
        public int riders;
    }

    /// <summary>
    /// A name tag over someone: their name on a rectangle of their own colour
    /// with a point at the bottom aimed down at them, turned to face the camera, on the
    /// Labels layer (which the renderer draws over everything). The colour comes
    /// from their key, so everyone sees the same player in the same colour.
    /// </summary>
    public sealed class NameTag
    {
        public const string LABELS_LAYER = "Labels";
        const float FONT_SIZE = 1.1f;
        const float PAD_X = 0.26f, PAD_Y = 0.12f;
        /// <summary>Half the width of the point under the box, and how far down it reaches.</summary>
        const float POINT_WIDTH = 0.16f, POINT_DEPTH = 0.2f;
        const float SCALE = 0.32f;

        readonly GameObject root;
        readonly TextMeshPro text;

        public NameTag(string label, string key, Transform parent)
        {
            root = new GameObject("tag " + label);
            root.transform.SetParent(parent, false);
            var content = new GameObject("badge");
            content.transform.SetParent(root.transform, false);
            var colour = ColourFor(key);
            var textGo = new GameObject("name");
            textGo.transform.SetParent(content.transform, false);
            text = textGo.AddComponent<TextMeshPro>();
            text.font = ModelLibrary.Instance.font;
            text.text = label;
            text.fontSize = FONT_SIZE;
            text.fontStyle = FontStyles.Bold;
            text.color = IsLight(colour) ? Coords.Hex(0x1c1b18) : Coords.Hex(0xffffff);
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(4, 0.4f);
            text.sortingOrder = 2;
            var size = text.GetPreferredValues(label);
            float w = size.x + PAD_X, h = size.y + PAD_Y;
            Badge(content.transform, "plate", Badge(w, h), colour, 0.01f, 1);
            // The point's tip sits on the spot the tag is placed at.
            content.transform.localPosition = new Vector3(0, h / 2 + POINT_DEPTH, 0);
            int layer = LayerMask.NameToLayer(LABELS_LAYER);
            if (layer >= 0) foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            root.transform.localScale = Vector3.one * SCALE;
        }

        /// <summary>A bright colour of this player's own (the same on every screen).</summary>
        public static Color ColourFor(string key)
        {
            uint hash = 2166136261;
            foreach (char ch in key ?? "") hash = (hash ^ ch) * 16777619;
            float hue = (hash % 3600) / 3600f;
            return Color.HSVToRGB(hue, 0.68f, 0.96f);
        }

        static bool IsLight(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b > 0.62f;

        static void Badge(Transform parent, string name, Mesh mesh, Color colour, float z, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, 0, z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Materials.Unlit(colour);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingOrder = order;
        }

        /// <summary>A rectangle with a triangle pointing down from the middle of its bottom edge, centred on the rectangle, drawn from both sides.</summary>
        static Mesh Badge(float w, float h)
        {
            float x = w / 2, y = h / 2;
            var verts = new List<Vector3>
            {
                new Vector3(-x, y), new Vector3(x, y), new Vector3(x, -y), new Vector3(-x, -y),
                new Vector3(-POINT_WIDTH, -y), new Vector3(POINT_WIDTH, -y), new Vector3(0, -y - POINT_DEPTH),
            };
            var front = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 };
            var tris = new List<int>(front);
            for (int i = 0; i < front.Length; i += 3) tris.AddRange(new[] { front[i], front[i + 2], front[i + 1] });
            var mesh = new Mesh { name = "name badge" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Place(double x, double h, double y)
        {
            root.transform.localPosition = Coords.Map(x, y, h);
            var cam = Camera.main;
            if (cam != null) root.transform.rotation = cam.transform.rotation;
        }

        public void Dispose()
        {
            foreach (var f in root.GetComponentsInChildren<MeshFilter>(true)) UnityEngine.Object.Destroy(f.sharedMesh);
            UnityEngine.Object.Destroy(root);
        }
    }

    public sealed class RemotePlayers
    {
        const double PLAYER_SCALE = 1.05;
        const double CAR_LIFT = 0.035;
        /// <summary>Where a tag's point sits: just over their head, or over their car.</summary>
        const double TAG_HEIGHT = 0.56;
        const double CAR_TAG_HEIGHT = 0.4;
        /// <summary>Each passenger's tag above the last (a badge and its point).</summary>
        const double TAG_STACK = 0.24;

        sealed class Remote
        {
            public Walker walker;
            public Avatar avatar;
            public string look;
            public Interp.SnapshotBuffer buffer;
            public Interp.PlaybackClock clock;
            public double lastT;
            public NameTag tag;
            public string tagText;
            public int carModel = -1;
            public CarView car;
            public RemoteState state;
        }

        readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
        readonly List<string> order = new List<string>();
        string me = "";
        readonly Transform root;
        readonly ModelLibrary library;
        double dusk;

        public RemotePlayers(Transform root, ModelLibrary library)
        {
            this.root = root;
            this.library = library;
        }

        static double Now => Time.realtimeSinceStartupAsDouble * 1000;

        /// <summary>A patch arrived: everyone in this client's view (but you).</summary>
        public void Sync(List<RemoteState> players, string me)
        {
            this.me = me;
            double now = Now;
            var seen = new HashSet<string>();
            foreach (var s in players)
            {
                seen.Add(s.key);
                remotes.TryGetValue(s.key, out var r);
                string look = Catalog.IsCharacter(s.appearance) ? s.appearance : Catalog.CHARACTERS[0];
                if (r != null && r.look != look)
                {
                    Drop(s.key);
                    r = null;
                }
                if (r == null) r = Spawn(s, look);
                r.state = s;
                if (s.t != r.lastT)
                {
                    r.lastT = s.t;
                    r.clock.Observe(s.t, now);
                    r.buffer.Push(new Snap { t = s.t, x = s.x, y = s.y, z = s.z, yaw = Protocol.YawFromWire(s.rotY) });
                }
            }
            foreach (var key in remotes.Keys.ToList()) if (!seen.Contains(key)) Drop(key);
        }

        /// <summary>Pose everyone for this frame. `myCar`: where your car is, while you're driving.</summary>
        public void Update(double dt, Vec2? myCar, double dusk)
        {
            this.dusk = dusk;
            double now = Now;
            var seat = new Dictionary<string, int>();
            var taken = new Dictionary<string, int>();
            foreach (var key in remotes.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var r = remotes[key];
                if (r.state.anim != Protocol.ANIM_RIDE || string.IsNullOrEmpty(r.state.ride)) continue;
                int n = (taken.TryGetValue(r.state.ride, out int c) ? c : 0) + 1;
                taken[r.state.ride] = n;
                seat[key] = n;
            }
            foreach (var key in order)
            {
                var r = remotes[key];
                double? t = r.clock.Advance(now, dt * 1000);
                Snap? sample = t == null ? null : r.buffer.Sample(t.Value);
                if (sample == null) continue;
                var p = sample.Value;
                var w = r.walker;
                double k = Math.Max(1e-3, dt);
                double moved = Numbers.Hypot(p.x - w.x, p.z - w.y);
                w.speed += (moved / k - w.speed) * Math.Min(1, dt * 10);
                w.rise = (p.y - w.lift) / k;
                w.x = p.x;
                w.y = p.z;
                w.lift = p.y > 0.004 ? p.y : 0;
                w.heading += Interp.TurnBetween(w.heading, p.yaw);
                bool driving = r.state.anim == Protocol.ANIM_DRIVE && r.state.car > 0;
                bool riding = r.state.anim == Protocol.ANIM_RIDE;
                r.avatar.Visible = !driving && !riding;
                if (r.avatar.Visible) r.avatar.Update(w, dt);
                PlaceCar(r, driving);
                double height = driving ? CAR_TAG_HEIGHT : riding ? CAR_TAG_HEIGHT + TAG_STACK * (seat.TryGetValue(key, out int st) ? st : 1) : TAG_HEIGHT;
                r.tag.Place(w.x, height + w.lift, w.y);
                string label = r.state.away ? $"{r.state.name} (away)" : r.state.name;
                if (label != r.tagText)
                {
                    r.tag.Dispose();
                    r.tag = new NameTag(label, key, root);
                    r.tagText = label;
                    r.tag.Place(w.x, height + w.lift, w.y);
                }
            }
            foreach (var key in order)
            {
                var r = remotes[key];
                if (r.state.anim != Protocol.ANIM_RIDE || string.IsNullOrEmpty(r.state.ride)) continue;
                Vec2? car = r.state.ride == me ? myCar : remotes.TryGetValue(r.state.ride, out var d) ? new Vec2(d.walker.x, d.walker.y) : (Vec2?)null;
                if (car == null) continue;
                r.walker.x = car.Value.x;
                r.walker.y = car.Value.y;
                double height = CAR_TAG_HEIGHT + TAG_STACK * (seat.TryGetValue(key, out int st) ? st : 1);
                r.tag.Place(car.Value.x, height + r.walker.lift, car.Value.y);
            }
        }

        /// <summary>Where this player is drawn now, if they're in view.</summary>
        public Vec2? PositionOf(string key) => remotes.TryGetValue(key, out var r) ? new Vec2(r.walker.x, r.walker.y) : (Vec2?)null;

        public List<RemoteDriver> Drivers()
        {
            var o = new List<RemoteDriver>();
            foreach (var key in order)
            {
                var r = remotes[key];
                if (r.state.anim != Protocol.ANIM_DRIVE || r.state.car <= 0) continue;
                int riders = 0;
                foreach (var x in remotes.Values) if (x.state.anim == Protocol.ANIM_RIDE && x.state.ride == key) riders++;
                o.Add(new RemoteDriver { key = key, name = r.state.name, x = r.walker.x, y = r.walker.y, heading = r.walker.heading, car = r.state.car, riders = riders });
            }
            return o;
        }

        public void Clear()
        {
            foreach (var key in remotes.Keys.ToList()) Drop(key);
        }

        Remote Spawn(RemoteState s, string look)
        {
            var walker = new Walker(look, s.x, s.z, PLAYER_SCALE) { heading = Protocol.YawFromWire(s.rotY) };
            var avatar = new Avatar(library.Character(look), Avatar.PERSON_HEIGHT, new Rng(s.key).Next(), root);
            avatar.SnapYaw(walker.heading);
            var r = new Remote
            {
                walker = walker,
                avatar = avatar,
                look = look,
                buffer = new Interp.SnapshotBuffer(),
                clock = new Interp.PlaybackClock(),
                lastT = double.NaN,
                tag = new NameTag(s.name, s.key, root),
                tagText = s.name,
                state = s,
            };
            remotes[s.key] = r;
            order.Add(s.key);
            return r;
        }

        void Drop(string key)
        {
            if (!remotes.TryGetValue(key, out var r)) return;
            r.avatar.Dispose();
            r.tag.Dispose();
            r.car?.Dispose();
            remotes.Remove(key);
            order.Remove(key);
        }

        void PlaceCar(Remote r, bool driving)
        {
            int model = driving ? r.state.car - 1 : -1;
            if (r.car != null && r.carModel != model)
            {
                r.car.Dispose();
                r.car = null;
            }
            if (model < 0 || model >= Catalog.CARS.Length) return;
            if (r.car == null)
            {
                r.car = new CarView(library.Get(Catalog.CARS[model]), root);
                r.carModel = model;
            }
            r.car.Place(r.walker.x, r.walker.y, r.walker.heading, r.walker.lift);
            r.car.Lights(dusk);
        }
    }
}
