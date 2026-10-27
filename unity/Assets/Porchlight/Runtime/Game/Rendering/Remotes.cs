// Other players in the town, as the server sends them: each drawn as the
// same rigged character the townsfolk use, posed from moves played back on
// their own clock a little in the past (Interp), with their name floating
// over them through buildings. At the wheel they're drawn as their car, and a
// passenger is out of sight inside it, their tag stacked over the car with
// the driver's. A port of render/iso/remotes.ts.

using System;
using System.Collections.Generic;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Net;
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

    /// <summary>A name tag: a camera-facing pill with the name on it, drawn over everything.</summary>
    public sealed class NameTag
    {
        const double TAG_WIDTH = 0.9;
        const int TAG_ORDER = 30;
        readonly Mesh mesh;
        readonly Material material;
        public readonly Drawable drawable;
        readonly Texture texture;
        readonly ITextMaker text;
        readonly Stage stage;

        public NameTag(string label, ITextMaker text, Stage stage)
        {
            this.text = text;
            this.stage = stage;
            texture = text.NameTag(label, out int w, out int h);
            double width = TAG_WIDTH * Math.Min(1.6, w / 200.0);
            double height = width * h / w;
            mesh = new Mesh { name = "tag" };
            mesh.SetVertices(new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            var size = new Vector2((float)width, (float)height);
            mesh.SetUVs(1, new[] { size, size, size, size });
            mesh.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000);
            material = Mats.BillboardMaterial(texture, Blending.Normal, false, TAG_ORDER);
            drawable = stage.Add(new Drawable(mesh, material, Matrix4x4.identity, false));
        }

        public void Place(double x, double h, double y) => drawable.Matrix = Matrix4x4.Translate(Space.Map(x, y, h));

        public void Dispose()
        {
            stage.Remove(drawable);
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(material);
            text.Release(texture);
        }
    }

    public sealed class RemotePlayers
    {
        const double PLAYER_SCALE = 1.05;
        const double CAR_LIFT = 0.035;
        const double TAG_HEIGHT = 0.62;
        const double CAR_TAG_HEIGHT = 0.45;
        const double TAG_STACK = 0.17;

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
            public List<Drawable> car;
            public RemoteState state;
        }

        readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
        readonly List<string> order = new List<string>();
        string me = "";
        readonly Transform root;
        readonly TownAssets assets;
        readonly Stage stage;
        readonly ITextMaker text;

        public RemotePlayers(Transform root, TownAssets assets, Stage stage, ITextMaker text)
        {
            this.root = root;
            this.assets = assets;
            this.stage = stage;
            this.text = text;
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
        public void Update(double dt, Vec2? myCar)
        {
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
                double moved = Js.Hypot(p.x - w.x, p.z - w.y);
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
                    r.tag = new NameTag(label, text, stage);
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
            var avatar = new Avatar(assets.Character(look), Avatar.PERSON_HEIGHT, new Rng(s.key).Next(), root, stage);
            avatar.SnapYaw(walker.heading);
            var r = new Remote
            {
                walker = walker,
                avatar = avatar,
                look = look,
                buffer = new Interp.SnapshotBuffer(),
                clock = new Interp.PlaybackClock(),
                lastT = double.NaN,
                tag = new NameTag(s.name, text, stage),
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
            if (r.car != null) foreach (var d in r.car) stage.Remove(d);
            remotes.Remove(key);
            order.Remove(key);
        }

        void PlaceCar(Remote r, bool driving)
        {
            int model = driving ? r.state.car - 1 : -1;
            if (r.car != null && r.carModel != model)
            {
                foreach (var d in r.car) stage.Remove(d);
                r.car = null;
            }
            if (model < 0 || model >= Catalog.CARS.Length) return;
            var prefab = assets.Get(Catalog.CARS[model]);
            if (r.car == null)
            {
                r.car = prefab.parts.Select(part => stage.Add(new Drawable(part.mesh, part.material, Matrix4x4.identity, true))).ToList();
                r.carModel = model;
            }
            double s = TownWorld.CarScale(prefab);
            var m = Space.M(TownWorld.Placement(prefab, r.walker.x, r.walker.y, r.walker.heading, s, s, s, CAR_LIFT));
            foreach (var d in r.car) d.Matrix = m;
        }
    }
}
