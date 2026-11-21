// The town's traffic and townsfolk, shared between everyone in it: the host
// packs where each car and person is (EncodeCars, EncodePeople) about ten
// times a second, and everyone else follows along (Receive, then Step every
// frame), easing toward each new position and carrying on in between at the
// speed and heading it was going. Where the server runs the town (sim/), it
// sends the whole town once (Receive) and then only the cars and people a
// client carrying them on like this would have wrong (ReceiveDelta). Pure.

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
        /// <summary>How quickly a car's drawn facing closes on the sent one (per second).</summary>
        const double FACE = 25;
        /// <summary>Further off than this (tiles), jump straight there.</summary>
        const double JUMP = 3;
        /// <summary>Stop carrying on after this long without word (s): the host has gone quiet.</summary>
        const double STALE = 1;

        struct Target
        {
            public double x, y, heading, speed;
            public bool hidden;
            /// <summary>The heading as last sent, how long ago (s), and how fast it was turning then (rad/s).</summary>
            public double sent, age, turn;
            public bool known;
        }

        /// <summary>Carry on turning as it was for this long without word (s), then go straight.</summary>
        const double TURN_FOR = 0.5;
        /// <summary>The fastest anything turns, rad/s (a car round the tightest corner at a walk).</summary>
        const double MOST_TURN = 3;
        /// <summary>Records closer together than this (s) say little about turning.</summary>
        const double LEAST_AGE = 0.05;

        /// <summary>An empty slot on the wire (nobody out in it): x and y both 0xFFFF.</summary>
        public const int HIDDEN = 0xFFFF;

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
                if (c.hidden && !c.taken) PutHidden(o, i * RECORD);
                else Put(o, i * RECORD, c.x, c.y, c.heading, Math.Abs(c.speed));
            }
            return o;
        }

        public static byte[] EncodePeople(IReadOnlyList<Walker> list)
        {
            var o = new byte[list.Count * RECORD];
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p.hidden) PutHidden(o, i * RECORD);
                else Put(o, i * RECORD, p.x, p.y, p.heading, p.speed);
            }
            return o;
        }

        static void PutHidden(byte[] o, int at)
        {
            U16(o, at, HIDDEN);
            U16(o, at + 2, HIDDEN);
            U16(o, at + 4, 0);
            o[at + 6] = 0;
        }

        static void Put(byte[] o, int at, double x, double y, double heading, double speed)
        {
            // Never the hidden sentinel by accident: the far corner's a hundredth of a tile in.
            U16(o, at, Math.Max(0, Math.Min(HIDDEN - 1, Numbers.RoundInt(x * SCALE))));
            U16(o, at + 2, Math.Max(0, Math.Min(HIDDEN - 1, Numbers.RoundInt(y * SCALE))));
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
                o[i] = Decode(b, at, o[i]);
            }
            return o;
        }

        /// <summary>A record: where, or (the hidden sentinel) nobody, keeping where it was last.</summary>
        static Target Decode(byte[] b, int at, Target was)
        {
            int x = (b[at] << 8) | b[at + 1], y = (b[at + 2] << 8) | b[at + 3];
            if (x == HIDDEN && y == HIDDEN) return new Target { x = was.x, y = was.y, heading = was.heading, hidden = true };
            double heading = ((b[at + 4] << 8) | b[at + 5]) / 65536.0 * 2 * Math.PI;
            // Turning since its last record: it'll be carried on round at that rate, not straight off the bend.
            double turn = was.known && !was.hidden ? Orbit.ShortestTurn(was.sent, heading) / Math.Max(LEAST_AGE, was.age) : 0;
            return new Target
            {
                x = x / SCALE,
                y = y / SCALE,
                heading = heading,
                speed = b[at + 6] / SPEED_SCALE,
                sent = heading,
                turn = Math.Max(-MOST_TURN, Math.Min(MOST_TURN, turn)),
                known = true,
            };
        }

        /// <summary>The host's latest.</summary>
        public void Receive(byte[] carBytes, byte[] peopleBytes)
        {
            cars = Read(carBytes, cars);
            people = Read(peopleBytes, people);
            since = 0;
            any = true;
        }

        /// <summary>
        /// The server's latest: only what changed (sim/src/Delta.cs: u16 cars, u16 people, then for
        /// each, u16 n and n × (u16 index, record); big-endian). The rest carry on as they were. False
        /// if it's of a town of other counts than this one has (the whole town's due: wait for it).
        /// </summary>
        public bool ReceiveDelta(byte[] d)
        {
            if (d == null || d.Length < 4) return false;
            int nc = (d[0] << 8) | d[1], np = (d[2] << 8) | d[3];
            if (!any || nc != cars.Length || np != people.Length) return false;
            int at = 4;
            if (!Patch(d, ref at, cars) || !Patch(d, ref at, people)) return false;
            since = 0;
            return true;
        }

        static bool Patch(byte[] d, ref int at, Target[] into)
        {
            if (at + 2 > d.Length) return false;
            int n = (d[at] << 8) | d[at + 1];
            at += 2;
            if (at + n * (2 + RECORD) > d.Length) return false;
            for (int k = 0; k < n; k++, at += 2 + RECORD)
            {
                int i = (d[at] << 8) | d[at + 1];
                if (i >= into.Length) continue;
                int r = at + 2;
                into[i] = Decode(d, r, into[i]);
            }
            return true;
        }

        /// <summary>
        /// Where you are (tiles), if on foot in the street: a car carried on toward you waits short of
        /// you rather than through you, until the server's word on it comes (it stops for you there too).
        /// </summary>
        public Vec2? You;
        /// <summary>A car waits for you this far ahead of it (tiles), and this far either side.</summary>
        const double WAIT_AHEAD = 1.6, WAIT_SIDE = 0.6;

        bool Waits(in Target t)
        {
            if (You == null) return false;
            double dx = You.Value.x - t.x, dy = You.Value.y - t.y;
            double ahead = dx * Math.Cos(t.heading) + dy * Math.Sin(t.heading);
            double side = -dx * Math.Sin(t.heading) + dy * Math.Cos(t.heading);
            return ahead > 0 && ahead < WAIT_AHEAD && Math.Abs(side) < WAIT_SIDE;
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
                var c = traffic[i];
                bool appears = c.hidden && !t.hidden;
                c.hidden = t.hidden;
                if (t.hidden) continue;
                if (appears)
                {
                    // Out on the road just now: straight there, not eased from where the slot was last.
                    c.x = t.x;
                    c.y = t.y;
                    c.heading = t.heading;
                }
                // Cars face (cos h, sin h): on round a bend as it was turning, a little while, then straight on.
                t.age += dt;
                if (going && !Waits(t))
                {
                    if (t.age <= TURN_FOR) t.heading += t.turn * dt;
                    t.x += Math.Cos(t.heading) * t.speed * dt;
                    t.y += Math.Sin(t.heading) * t.speed * dt;
                }
                Follow(ref c.x, ref c.y, t.x, t.y, k);
                // Its facing follows more closely than where it is: carried round with the turn, there's nothing to
                // smooth, and lagging it shows a car sliding round a corner sideways.
                c.heading += Orbit.ShortestTurn(c.heading, t.heading) * Math.Min(1, dt * FACE);
                c.speed = going ? t.speed : 0;
            }
            for (int i = 0; i < Math.Min(people.Length, walkers.Count); i++)
            {
                ref var t = ref people[i];
                var w = walkers[i];
                bool appears = w.hidden && !t.hidden;
                w.hidden = t.hidden;
                if (t.hidden) continue;
                if (appears)
                {
                    w.x = t.x;
                    w.y = t.y;
                    w.heading = t.heading;
                }
                // People face (sin h, cos h).
                if (going)
                {
                    t.x += Math.Sin(t.heading) * t.speed * dt;
                    t.y += Math.Cos(t.heading) * t.speed * dt;
                }
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
