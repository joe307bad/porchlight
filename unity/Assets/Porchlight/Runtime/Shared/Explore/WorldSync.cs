// The town's traffic and townsfolk, shared between everyone in it: the host
// packs where each car and person is (EncodeCars, EncodePeople) about ten
// times a second, and everyone else follows along (Receive, then Step every
// frame), easing toward each new position and carrying on in between at the
// speed and heading it was going. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public sealed class WorldSync
    {
        /// <summary>Position steps per tile on the wire.</summary>
        const double SCALE = 100;
        /// <summary>Speed steps per tile a second.</summary>
        const double SPEED_SCALE = 40;
        /// <summary>Bytes per car or person: x, y and heading (16 bits each), speed (8).</summary>
        const int RECORD = 7;
        /// <summary>How quickly the drawn position closes on the sent one (per second).</summary>
        const double EASE = 8;
        /// <summary>Further off than this (tiles), jump straight there.</summary>
        const double JUMP = 3;
        /// <summary>Stop carrying on after this long without word (s): the host has gone quiet.</summary>
        const double STALE = 1;

        struct Target
        {
            public double x, y, heading, speed;
        }

        Target[] cars = new Target[0];
        Target[] people = new Target[0];
        double since;
        bool any;

        /// <summary>How many townsfolk the host has (its list can grow: drivers put out of their cars).</summary>
        public int PeopleCount => people.Length;

        public bool HasWorld => any;

        public static byte[] EncodeCars(IReadOnlyList<TrafficCar> list)
        {
            var o = new byte[list.Count * RECORD];
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                Put(o, i * RECORD, c.x, c.y, c.heading, Math.Abs(c.speed));
            }
            return o;
        }

        public static byte[] EncodePeople(IReadOnlyList<Walker> list)
        {
            var o = new byte[list.Count * RECORD];
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                Put(o, i * RECORD, p.x, p.y, p.heading, p.speed);
            }
            return o;
        }

        static void Put(byte[] o, int at, double x, double y, double heading, double speed)
        {
            U16(o, at, Math.Max(0, Math.Min(65535, Numbers.RoundInt(x * SCALE))));
            U16(o, at + 2, Math.Max(0, Math.Min(65535, Numbers.RoundInt(y * SCALE))));
            double turn = heading / (2 * Math.PI);
            U16(o, at + 4, Numbers.RoundInt((turn - Math.Floor(turn)) * 65536) & 0xffff);
            o[at + 6] = (byte)Math.Max(0, Math.Min(255, Numbers.RoundInt(speed * SPEED_SCALE)));
        }

        static void U16(byte[] o, int at, int v)
        {
            o[at] = (byte)(v >> 8);
            o[at + 1] = (byte)v;
        }

        static Target[] Read(byte[] b, Target[] was)
        {
            int n = b == null ? 0 : b.Length / RECORD;
            var o = was.Length == n ? was : new Target[n];
            for (int i = 0; i < n; i++)
            {
                int at = i * RECORD;
                o[i] = new Target
                {
                    x = ((b[at] << 8) | b[at + 1]) / SCALE,
                    y = ((b[at + 2] << 8) | b[at + 3]) / SCALE,
                    heading = ((b[at + 4] << 8) | b[at + 5]) / 65536.0 * 2 * Math.PI,
                    speed = b[at + 6] / SPEED_SCALE,
                };
            }
            return o;
        }

        /// <summary>The host's latest.</summary>
        public void Receive(byte[] carBytes, byte[] peopleBytes)
        {
            cars = Read(carBytes, cars);
            people = Read(peopleBytes, people);
            since = 0;
            any = true;
        }

        /// <summary>Forget the host's town (leaving the room, or running it ourselves now).</summary>
        public void Clear()
        {
            cars = new Target[0];
            people = new Target[0];
            any = false;
        }

        /// <summary>One frame: move the traffic and townsfolk toward where the host has them.</summary>
        public void Step(double dt, IReadOnlyList<TrafficCar> traffic, IReadOnlyList<Walker> walkers)
        {
            if (!any) return;
            since += dt;
            bool going = since < STALE;
            double k = Math.Min(1, dt * EASE);
            for (int i = 0; i < Math.Min(cars.Length, traffic.Count); i++)
            {
                ref var t = ref cars[i];
                // Cars face (cos h, sin h).
                if (going)
                {
                    t.x += Math.Cos(t.heading) * t.speed * dt;
                    t.y += Math.Sin(t.heading) * t.speed * dt;
                }
                var c = traffic[i];
                Follow(ref c.x, ref c.y, t.x, t.y, k);
                c.heading += Orbit.ShortestTurn(c.heading, t.heading) * k;
                c.speed = going ? t.speed : 0;
            }
            for (int i = 0; i < Math.Min(people.Length, walkers.Count); i++)
            {
                ref var t = ref people[i];
                // People face (sin h, cos h).
                if (going)
                {
                    t.x += Math.Sin(t.heading) * t.speed * dt;
                    t.y += Math.Cos(t.heading) * t.speed * dt;
                }
                var w = walkers[i];
                Follow(ref w.x, ref w.y, t.x, t.y, k);
                w.heading += Orbit.ShortestTurn(w.heading, t.heading) * k;
                w.speed += ((going ? t.speed : 0) - w.speed) * k;
            }
        }

        static void Follow(ref double x, ref double y, double tx, double ty, double k)
        {
            if (Numbers.Hypot(tx - x, ty - y) > JUMP)
            {
                x = tx;
                y = ty;
                return;
            }
            x += (tx - x) * k;
            y += (ty - y) * k;
        }
    }
}
