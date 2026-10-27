// Moving cars and their lights: the traffic drawn instanced per car-model
// part (fleet.ts), and at night two headlights and two tail lights on every
// moving car, each with a halo, and a pool of light on the road ahead
// (headlights.ts).

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using UnityEngine;

namespace Porchlight.Rendering
{
    public sealed class Fleet
    {
        sealed class Group
        {
            public Prefab prefab;
            public List<Drawable> drawables = new List<Drawable>();
            public List<int> cars = new List<int>();
        }

        static readonly Matrix4x4 Gone = Matrix4x4.Scale(Vector3.zero);
        readonly Traffic traffic;
        readonly Stage stage;
        readonly List<Group> groups = new List<Group>();

        public Fleet(Traffic traffic, TownAssets assets, Stage stage)
        {
            this.traffic = traffic;
            this.stage = stage;
            for (int model = 0; model < Catalog.CARS.Length; model++)
            {
                var g = new Group();
                for (int i = 0; i < traffic.cars.Count; i++) if (traffic.cars[i].model == model) g.cars.Add(i);
                if (g.cars.Count == 0) continue;
                g.prefab = assets.Get(Catalog.CARS[model]);
                foreach (var part in g.prefab.parts) g.drawables.Add(stage.Add(new Drawable(part.mesh, part.material, new Matrix4x4[g.cars.Count], g.cars.Count, true)));
                groups.Add(g);
            }
            Update();
        }

        public void Update()
        {
            foreach (var g in groups)
            {
                double s = TownWorld.CarScale(g.prefab);
                for (int k = 0; k < g.cars.Count; k++)
                {
                    var c = traffic.cars[g.cars[k]];
                    var m = c.taken ? Gone : Space.M(TownWorld.Placement(g.prefab, c.x, c.y, JsMath.Atan2(Math.Cos(c.heading), Math.Sin(c.heading)), s, s, s));
                    foreach (var d in g.drawables) d.matrices[k] = m;
                }
            }
        }

        public void Dispose()
        {
            foreach (var g in groups) foreach (var d in g.drawables) stage.Remove(d);
        }
    }

    public struct LitCar
    {
        public double x, y, heading;
        public CarShape shape;
    }

    public sealed class Headlights
    {
        const int MAX_LIGHTS = 80;
        readonly Drawable lamps;
        readonly Drawable beams;
        readonly Halos halos;
        readonly Stage stage;
        readonly Mesh lampMesh, beamMesh;

        public Headlights(Stage stage)
        {
            this.stage = stage;
            lampMesh = Primitives.ToMesh(Primitives.Sphere(0.028, 8, 5), "car lamp");
            // After the cars, so the lamps on the driven car don't count as hiding it from the x-ray.
            var lampMat = Mats.Basic(Color.white, instanceColors: true, renderOrder: 21);
            lamps = stage.Add(new Drawable(lampMesh, lampMat, new Matrix4x4[MAX_LIGHTS * 4], 0, false, new Vector4[MAX_LIGHTS * 4]));
            beamMesh = Primitives.ToMesh(Primitives.RotateX(Primitives.Plane(CarLamps.BEAM_WIDTH, CarLamps.BEAM_LENGTH), -Math.PI / 2), "beam");
            var beamMat = Mats.Basic(Color.white, Blending.Additive, false, map: Primitives.Glow, instanceColors: true, renderOrder: 6);
            beams = stage.Add(new Drawable(beamMesh, beamMat, new Matrix4x4[MAX_LIGHTS], 0, false, new Vector4[MAX_LIGHTS]));
            halos = new Halos(MAX_LIGHTS * 4, 0.32, 7, stage);
        }

        public void Update(IReadOnlyList<LitCar> cars, double dusk)
        {
            bool on = dusk > 0.02;
            lamps.visible = beams.visible = halos.drawable.visible = on;
            if (!on) return;
            int n = Math.Min(cars.Count, MAX_LIGHTS);
            var head = Space.Hex(0xfff4dc) * (float)(0.25 + dusk * 0.75);
            var tail = Space.Hex(0xff2a1a) * (float)(0.3 + dusk * 0.7);
            float poolK = (float)(dusk * 0.5);
            var pool = new Vector4(1 * poolK, 0.93f * poolK, 0.78f * poolK, 1);
            for (int i = 0; i < n; i++)
            {
                var c = cars[i];
                var l = CarLamps.Of(c.x, c.y, c.heading, c.shape);
                var all = new[] { l.front0, l.front1, l.back0, l.back1 };
                for (int k = 0; k < 4; k++)
                {
                    int j = i * 4 + k;
                    var p = all[k];
                    var at = Space.Map(p.x, p.y, p.h);
                    lamps.matrices[j] = Matrix4x4.Translate(at);
                    var col = k < 2 ? head : tail;
                    lamps.colors[j] = new Vector4(col.r, col.g, col.b, 1);
                    float d = (float)dusk;
                    if (k < 2) halos.Set(j, at, d, d * 0.95f, d * 0.82f);
                    else halos.Set(j, at, d * 0.7f, d * 0.08f, d * 0.04f);
                }
                var m = Mat4.Compose(l.beam.x, 0.045, l.beam.y, Quat.AboutY(JsMath.Atan2(-l.forward.x, -l.forward.y)), 1, 1, 1);
                beams.matrices[i] = Space.M(m);
                beams.colors[i] = pool;
            }
            lamps.count = n * 4;
            beams.count = n;
            halos.Commit(n * 4);
        }

        public void Dispose()
        {
            stage.Remove(lamps);
            stage.Remove(beams);
            stage.Remove(halos.drawable);
            UnityEngine.Object.Destroy(lampMesh);
            UnityEngine.Object.Destroy(beamMesh);
        }
    }
}
