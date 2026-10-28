// The airport and the docks. The airport is laid out in its own (u along the
// runway, v toward town: Airfield) frame: a runway with its markings and edge
// lights, a taxiway joined to it at either end, the apron the planes park on,
// round-roofed hangars along its town side, the control tower and the car
// park. The docks are plank piers on posts, out from the shore.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Rect = Porchlight.Explore.Rect;

namespace Porchlight.Rendering
{
    public sealed partial class TownWorld
    {
        static class P
        {
            public const int runway = 0x4f5257, taxiway = 0x5d6066, apron = 0xb4b0a6, carPark = 0x6b6b6b, paint = 0xf2f0e8, taxiPaint = 0xe8c547;
            public const int hangar = 0xc9ccd0, hangarRoof = 0xa9adb3, hangarDoor = 0x6d737b, tower = 0xd8d3c8, towerRoof = 0x4a4e55;
            public const int edgeLight = 0xfff2c0, endLight = 0x5cff7a, sockPole = 0xe9e9e9, sock = 0xf07a2a;
            public const int deck = 0xa07a52, post = 0x6b5238;
        }

        const double HANGAR_HEIGHT = 2.1;
        const double TOWER_HEIGHT = 2.9;
        const double MARK = 0.004;

        void AddAirport()
        {
            var a = model.airport;
            if (a == null) return;
            var b = new MeshBuilder(palette);
            V3 At(double u, double v, double h)
            {
                var p = Airfield.ToWorld(a, u, v);
                return v3(p.x, h, p.y);
            }
            var du = Airfield.AlongU(a);
            var dv = Airfield.AlongV(a);
            var alongU = new V3(du.x, 0, du.y);
            var alongV = new V3(dv.x, 0, dv.y);
            var back = new V3(-du.x, 0, -du.y);
            var away = new V3(-dv.x, 0, -dv.y);
            void Flat(double u0, double v0, double u1, double v1, double h, int color, int glow = 0) =>
                b.Quad(At(u0, v0, h), At(u1, v0, h), At(u1, v1, h), At(u0, v1, h), color, null, glow);
            void Block(double u0, double v0, double u1, double v1, double h0, double h1, int color, int? top = null, int glow = 0)
            {
                Flat(u0, v0, u1, v1, h1, top ?? color, glow);
                b.Quad(At(u0, v0, h0), At(u1, v0, h0), At(u1, v0, h1), At(u0, v0, h1), color, away, glow);
                b.Quad(At(u0, v1, h0), At(u1, v1, h0), At(u1, v1, h1), At(u0, v1, h1), color, alongV, glow);
                b.Quad(At(u0, v0, h0), At(u0, v1, h0), At(u0, v1, h1), At(u0, v0, h1), color, back, glow);
                b.Quad(At(u1, v0, h0), At(u1, v1, h0), At(u1, v1, h1), At(u1, v0, h1), color, alongU, glow);
            }
            void Surface(Airfield.Box x, double h, int color) => Flat(x.u0, x.v0, x.u1, x.v1, h, color);

            // The paved surfaces, each a whisker above the last where they meet.
            var runway = Airfield.Runway(a);
            Surface(Airfield.Taxiway(a), 0.008, P.taxiway);
            foreach (var link in Airfield.Links(a)) Surface(link, 0.008, P.taxiway);
            Surface(runway, 0.010, P.runway);
            Surface(Airfield.Apron(a), 0.009, P.apron);
            var park = Airfield.CarPark(a);
            Surface(park, 0.008, P.carPark);

            // Runway markings: a dashed centreline and piano keys at either end.
            double mid = runway.MidV, top = 0.010 + MARK;
            for (double u = runway.u0 + 3; u + 1 <= runway.u1 - 3; u += 1.8) Flat(u, mid - 0.05, u + 1, mid + 0.05, top, P.paint);
            foreach (double end in new[] { runway.u0 + 0.4, runway.u1 - 1.6 })
                for (int k = 0; k < 6; k++)
                {
                    double v = runway.v0 + 0.3 + k * ((runway.v1 - runway.v0 - 0.6) / 6);
                    Flat(end, v + 0.1, end + 1.2, v + 0.3, top, P.paint);
                }
            // The taxiway's yellow line, on round to the runway at both ends.
            var taxi = Airfield.Taxiway(a);
            Flat(taxi.u0 + 0.7, taxi.MidV - 0.03, taxi.u1 - 0.7, taxi.MidV + 0.03, 0.008 + MARK, P.taxiPaint);
            foreach (var link in Airfield.Links(a)) Flat(link.MidU - 0.03, link.v0, link.MidU + 0.03, link.v1 + 0.6, 0.008 + MARK, P.taxiPaint);
            // Bays in the car park, along its two long edges.
            for (double u = park.u0 + 0.22; u <= park.u1 - 0.2; u += 0.75)
            {
                Flat(u - 0.02, park.v0, u + 0.02, park.v0 + 1.3, 0.008 + MARK, P.paint);
                Flat(u - 0.02, park.v1 - 1.3, u + 0.02, park.v1, 0.008 + MARK, P.paint);
            }
            // Lights down both edges of the runway, green across its ends.
            for (double u = runway.u0 + 0.3; u <= runway.u1 - 0.3; u += 2)
                foreach (double v in new[] { runway.v0 + 0.08, runway.v1 - 0.08 })
                    Block(u - 0.05, v - 0.05, u + 0.05, v + 0.05, 0, 0.07, P.edgeLight, glow: P.edgeLight);
            foreach (double u in new[] { runway.u0 + 0.12, runway.u1 - 0.12 })
                for (double v = runway.v0 + 0.3; v <= runway.v1 - 0.3; v += 0.6)
                    Block(u - 0.05, v - 0.05, u + 0.05, v + 0.05, 0, 0.07, P.endLight, glow: P.endLight);

            // Round-roofed hangars, their wide doors onto the apron.
            const int ARCH = 10;
            foreach (var hg in Airfield.Hangars(a))
            {
                double half = (hg.u1 - hg.u0) / 2, c = hg.MidU;
                double U(int k) => c - Math.Cos(Math.PI * k / ARCH) * half;
                double H(int k) => Math.Sin(Math.PI * k / ARCH) * HANGAR_HEIGHT;
                for (int k = 0; k < ARCH; k++)
                {
                    double tk = Math.PI * (k + 0.5) / ARCH;
                    var facing = new V3(-Math.Cos(tk) * du.x, Math.Sin(tk), -Math.Cos(tk) * du.y);
                    b.Quad(At(U(k), hg.v0, H(k)), At(U(k + 1), hg.v0, H(k + 1)), At(U(k + 1), hg.v1, H(k + 1)), At(U(k), hg.v1, H(k)), P.hangarRoof, facing);
                    // The ends: the back closed, the front a door with a frame round it.
                    b.Quad(At(U(k), hg.v1, 0), At(U(k + 1), hg.v1, 0), At(U(k + 1), hg.v1, H(k + 1)), At(U(k), hg.v1, H(k)), P.hangar, alongV);
                    b.Quad(At(U(k), hg.v0, 0), At(U(k + 1), hg.v0, 0), At(U(k + 1), hg.v0, H(k + 1)), At(U(k), hg.v0, H(k)), P.hangar, away);
                }
                double door = half * 0.72;
                Flat(c - door, hg.v0 - 0.02, c + door, hg.v0 - 0.01, 0, P.hangarDoor);
                b.Quad(At(c - door, hg.v0 - 0.015, 0), At(c + door, hg.v0 - 0.015, 0), At(c + door, hg.v0 - 0.015, HANGAR_HEIGHT * 0.66), At(c - door, hg.v0 - 0.015, HANGAR_HEIGHT * 0.66), P.hangarDoor, away);
                AddBlock(a, hg, HANGAR_HEIGHT);
            }

            // The control tower: a stalk, a glass cab that lights up at night, and its roof.
            var t = Airfield.Tower(a);
            double inset = 0.35;
            Block(t.u0 + inset, t.v0 + inset, t.u1 - inset, t.v1 - inset, 0, TOWER_HEIGHT - 0.75, P.tower);
            Block(t.u0 + 0.1, t.v0 + 0.1, t.u1 - 0.1, t.v1 - 0.1, TOWER_HEIGHT - 0.75, TOWER_HEIGHT - 0.7, P.tower);
            Block(t.u0 + 0.16, t.v0 + 0.16, t.u1 - 0.16, t.v1 - 0.16, TOWER_HEIGHT - 0.7, TOWER_HEIGHT - 0.12, GLASS, glow: 0xffe9b0);
            Block(t.u0 + 0.04, t.v0 + 0.04, t.u1 - 0.04, t.v1 - 0.04, TOWER_HEIGHT - 0.12, TOWER_HEIGHT, P.towerRoof);
            AddBlock(a, t, TOWER_HEIGHT);

            // A windsock by the runway's near end.
            double su = runway.u0 + 1.2, sv = runway.v1 + 0.9;
            Block(su - 0.03, sv - 0.03, su + 0.03, sv + 0.03, 0, 1.0, P.sockPole);
            Block(su + 0.03, sv - 0.07, su + 0.55, sv + 0.07, 0.86, 0.98, P.sock);

            b.Build("airport", still);
        }

        /// <summary>A building at the airport: solid to walk into, and a box for the camera (and planes) to keep above.</summary>
        void AddBlock(Porchlight.Model.Airport a, Airfield.Box x, double height)
        {
            var (x0, y0, x1, y1) = Airfield.Rect(a, x);
            solids.Add(new Rect(x0, y0, x1, y1));
            boxes.Add(new Box { x0 = x0, y0 = y0, x1 = x1, y1 = y1, top = height });
        }

        /// <summary>Plank piers on posts, from the shore out over the water.</summary>
        void AddDocks()
        {
            if (layout.docks.Count == 0) return;
            var b = new MeshBuilder(palette);
            const double DECK = 0.05, THICK = 0.04, HALF = 0.42;
            foreach (var dock in layout.docks)
            {
                var (fx, fy) = Grid.DIRS4[(int)dock.facing];
                var first = dock.tiles[0];
                var last = dock.tiles[dock.tiles.Count - 1];
                // From the shore's edge to the end of the last tile, the middle of the tiles across.
                double ax = first.x + 0.5 - fx * 0.5, ay = first.y + 0.5 - fy * 0.5;
                double bx = last.x + 0.5 + fx * 0.5, by = last.y + 0.5 + fy * 0.5;
                double rx = -fy * HALF, ry = fx * HALF;
                b.Box(Math.Min(ax, bx) - Math.Abs(rx), Math.Min(ay, by) - Math.Abs(ry), Math.Max(ax, bx) + Math.Abs(rx), Math.Max(ay, by) + Math.Abs(ry), DECK - THICK, DECK, P.post, P.deck);
                // Planks across, a shade apart.
                double length = Numbers.Hypot(bx - ax, by - ay);
                for (double s = 0.06; s < length; s += 0.16)
                {
                    double px = ax + fx * s, py = ay + fy * s;
                    b.Box(Math.Min(px - rx, px + rx) - Math.Abs(fx) * 0.05, Math.Min(py - ry, py + ry) - Math.Abs(fy) * 0.05,
                        Math.Max(px - rx, px + rx) + Math.Abs(fx) * 0.05, Math.Max(py - ry, py + ry) + Math.Abs(fy) * 0.05, DECK, DECK + 0.006, P.deck);
                }
                // Posts along both sides, down to the bed.
                for (double s = 0.3; s < length; s += 1)
                    foreach (int side in new[] { 1, -1 })
                    {
                        double px = ax + fx * s + rx * side, py = ay + fy * s + ry * side;
                        b.Box(px - 0.04, py - 0.04, px + 0.04, py + 0.04, BED_Y, DECK + 0.08, P.post);
                    }
            }
            b.Build("docks", still);
        }

        static V3 v3(double x, double h, double y) => new V3(x, h, y);
    }
}
