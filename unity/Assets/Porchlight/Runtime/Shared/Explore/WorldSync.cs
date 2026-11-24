// The town's traffic and townsfolk, shared between everyone in it: the host
// packs where each car and person is (EncodeCars, EncodePeople) about ten
// times a second, and everyone else follows along (Receive, then Step every
// frame). Where the server runs the town (sim/), it sends the whole town once
// (Receive) and then only the cars and people a client carrying them on would
// have wrong (ReceiveDelta). Pure.
//
// Cars are drawn a moment behind (DELAY), on a smooth curve through where they
// were said to be, each word's place, facing and speed (a cubic Hermite from one
// to the next), so a car goes round a corner in one even sweep rather than being
// guessed ahead and pulled back. Past the last word, a car carries straight on at
// its speed, as the server expects (Delta.cs). People are eased toward the latest
// word, carried on in between. A delta from the server also ends with the town's
// traffic lights (StreetFurniture.EncodeLights): each signal's green stage, its timing, and when
// it ends, shown the same moment behind as the cars (Showing).

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
            public bool hidden;
        }

        /// <summary>
        /// A car as a word had it, and when (s, on this follower's clock of words). Or as it was drawn at a moment
        /// (`drawn`), with how it was moving and turning then, to go on from without a jolt.
        /// </summary>
        struct Key
        {
            public double x, y, heading, speed, t;
            public bool drawn;
            public double vx, vy, spin;
        }

        /// <summary>A car's last few words: drawn between them, a moment behind.</summary>
        sealed class Track
        {
            public readonly Key[] k = new Key[KEYS];
            public int n;
        }

        /// <summary>How far behind the latest word cars are drawn (s): two of the server's ticks, so there's nearly always a word ahead to draw toward.</summary>
        public const double DELAY = 0.2;
        /// <summary>Words kept a car.</summary>
        const int KEYS = 6;

        /// <summary>An empty slot on the wire (nobody out in it): x and y both 0xFFFF.</summary>
        public const int HIDDEN = 0xFFFF;

        Target[] cars = new Target[0];
        Target[] people = new Target[0];
        Track[] tracks = new Track[0];
        double since;
        bool any;
        /// <summary>This follower's clock (s, by the frames drawn); when the last word came by it, as evened out; and the usual gap between words.</summary>
        double clock, wordAt = double.NaN, lastArrival, gap = 0.1;

        /// <summary>Each signal's green stage as last heard, and when it ends (ms, this follower's clock).</summary>
        int[] green = new int[0];
        double[] greenEnds = new double[0];
        /// <summary>Each signal's two stages' greens (ms), as last heard: stage 0's then stage 1's.</summary>
        long[] greens = new long[0];

        /// <summary>How many of the town's signals the server has said how they stand.</summary>
        public int Lights => green.Length;

        /// <summary>
        /// What signal `light`'s lamps on `stage` show as the cars are drawn (DELAY behind the latest word): 0 red,
        /// 1 amber, 2 green; -1 if the server hasn't said.
        /// </summary>
        public int Showing(int light, int stage)
        {
            if (light < 0 || light >= green.Length) return -1;
            return Porchlight.Life.StreetFurniture.Showing(green[light], greenEnds[light], stage, (clock - DELAY) * 1000, greens[2 * light], greens[2 * light + 1]);
        }

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
            return new Target { x = x / SCALE, y = y / SCALE, heading = heading, speed = b[at + 6] / SPEED_SCALE };
        }

        /// <summary>The host's latest.</summary>
        public void Receive(byte[] carBytes, byte[] peopleBytes)
        {
            cars = Read(carBytes, cars);
            people = Read(peopleBytes, people);
            double t = Word();
            if (tracks.Length != cars.Length)
            {
                var o = new Track[cars.Length];
                for (int i = 0; i < o.Length; i++) o[i] = i < tracks.Length ? tracks[i] : new Track();
                tracks = o;
            }
            for (int i = 0; i < cars.Length; i++) Heard(i, t);
            since = 0;
            any = true;
        }

        /// <summary>
        /// When a word just come is taken to be from, on this follower's clock: words leave the server evenly (a
        /// tick apart) but arrive unevenly, so each is put a usual gap after the last, drawn a little toward when it
        /// actually came (and never after).
        /// </summary>
        double Word()
        {
            double arrival = clock;
            if (double.IsNaN(wordAt)) wordAt = arrival;
            else
            {
                double g = arrival - lastArrival;
                if (g > 0 && g < 1) gap += (g - gap) * 0.1;
                double due = wordAt + gap;
                wordAt = Math.Max(wordAt + 1e-3, Math.Min(arrival, due + (arrival - due) * 0.2));
            }
            lastArrival = arrival;
            return wordAt;
        }

        /// <summary>A word on car i, from time t: kept to draw toward.</summary>
        void Heard(int i, double t)
        {
            var tr = tracks[i];
            ref var c = ref cars[i];
            if (c.hidden)
            {
                tr.n = 0;
                return;
            }
            var key = new Key { x = c.x, y = c.y, heading = c.heading, speed = c.speed, t = t };
            double now = clock - DELAY;
            if (tr.n == 0)
            {
                tr.k[0] = key;
                tr.n = 1;
                return;
            }
            // Words already drawn past go (keeping the one being drawn from).
            int drop = 0;
            while (drop + 1 < tr.n && tr.k[drop + 1].t <= now) drop++;
            if (drop > 0)
            {
                for (int j = drop; j < tr.n; j++) tr.k[j - drop] = tr.k[j];
                tr.n -= drop;
            }
            // Somewhere else altogether (the slot's another car's now, put out as the last parked): straight there.
            var last = tr.k[tr.n - 1];
            if (Numbers.Hypot(key.x - last.x, key.y - last.y) > JUMP + Math.Max(last.speed, key.speed) * Math.Max(0, t - last.t))
            {
                tr.k[0] = key;
                tr.k[0].t = Math.Min(t, now);
                tr.n = 1;
                return;
            }
            if (tr.k[tr.n - 1].t <= now)
            {
                // Carried on past its last word: from where it's drawn now to this one, so it never jumps.
                tr.k[0] = Sample(tr, now);
                tr.n = 1;
            }
            if (key.t <= tr.k[tr.n - 1].t) key.t = tr.k[tr.n - 1].t + 1e-3;
            if (tr.n == KEYS)
            {
                for (int j = 1; j < tr.n; j++) tr.k[j - 1] = tr.k[j];
                tr.n--;
            }
            tr.k[tr.n++] = key;
        }

        /// <summary>
        /// Where a car is at time t: between the words either side of t (cubic Hermites through their places and
        /// facings, each leaving and arriving as it was going there), or carried straight on from its last.
        /// </summary>
        Key Sample(Track tr, double t)
        {
            var a = tr.k[0];
            if (tr.n == 1 || t <= a.t)
            {
                if (tr.n == 1 && t > a.t) return Ahead(tr, 0, t);
                // Not yet to its first word (it's only just come out): there, standing.
                return new Key { x = a.x, y = a.y, heading = a.heading, t = t, drawn = true };
            }
            int j = 0;
            while (j + 1 < tr.n && tr.k[j + 1].t < t) j++;
            if (j == tr.n - 1) return Ahead(tr, j, t);
            a = tr.k[j];
            var b = tr.k[j + 1];
            double d = b.t - a.t, u = (t - a.t) / d;
            double u2 = u * u, u3 = u2 * u;
            double h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
            double g00 = 6 * u2 - 6 * u, g10 = 3 * u2 - 4 * u + 1, g01 = -6 * u2 + 6 * u, g11 = 3 * u2 - 2 * u;
            // Where: through each word's place, leaving and arriving the way it was moving there.
            var (avx, avy) = Velocity(tr, j);
            var (bvx, bvy) = Velocity(tr, j + 1);
            double ax = avx * d, ay = avy * d, bx = bvx * d, by = bvy * d;
            double vx = (g00 * a.x + g10 * ax + g01 * b.x + g11 * bx) / d;
            double vy = (g00 * a.y + g10 * ay + g01 * b.y + g11 * by) / d;
            // Facing: from each word's to the next, turning at the rate it was turning at each (a car's middle
            // doesn't go quite the way it faces round a corner: its back wheels trail).
            double turn = Orbit.ShortestTurn(a.heading, b.heading);
            double wa = Spin(tr, j), wb = Spin(tr, j + 1);
            double heading = a.heading + h10 * wa * d + h01 * turn + h11 * wb * d;
            return new Key
            {
                x = h00 * a.x + h10 * ax + h01 * b.x + h11 * bx,
                y = h00 * a.y + h10 * ay + h01 * b.y + h11 * by,
                heading = heading,
                speed = Numbers.Hypot(vx, vy),
                t = t,
                drawn = true,
                vx = vx,
                vy = vy,
                spin = g10 * wa + g01 * turn / d + g11 * wb,
            };
        }

        /// <summary>
        /// How a car was moving at its jth word (tiles/s): from the word before it to the word after; at its last,
        /// along its facing at its speed (as it's carried on from there); as drawn, if it's where it was drawn.
        /// </summary>
        static (double x, double y) Velocity(Track tr, int j)
        {
            var k = tr.k[j];
            if (k.drawn) return (k.vx, k.vy);
            int a = Math.Max(0, j - 1), b = j + 1;
            double dt = b < tr.n ? tr.k[b].t - tr.k[a].t : 0;
            if (b >= tr.n || dt < 1e-6) return (Math.Cos(k.heading) * k.speed, Math.Sin(k.heading) * k.speed);
            return ((tr.k[b].x - tr.k[a].x) / dt, (tr.k[b].y - tr.k[a].y) / dt);
        }

        /// <summary>How fast a car was turning at its jth word (rad/s): from the words either side of it (none at its last: it's carried straight on from there).</summary>
        static double Spin(Track tr, int j)
        {
            var k = tr.k[j];
            if (k.drawn) return k.spin;
            int a = Math.Max(0, j - 1), b = j + 1;
            double dt = b < tr.n ? tr.k[b].t - tr.k[a].t : 0;
            return b >= tr.n || dt < 1e-6 ? 0 : Orbit.ShortestTurn(tr.k[a].heading, tr.k[b].heading) / dt;
        }

        /// <summary>
        /// A car carried straight on from its last word to time t (for as long as the host's still talking), stopping
        /// short of you if you're in its way (the server's car stops for you too; its word just hasn't come yet).
        /// </summary>
        Key Ahead(Track tr, int j, double t)
        {
            var last = tr.k[j];
            var (vx, vy) = Velocity(tr, j);
            double speed = Numbers.Hypot(vx, vy), way = speed > 1e-9 ? Math.Atan2(vy, vx) : last.heading;
            // On for as long as the host's talking (a car on a steady way isn't sent again for a while: Delta.cs), and
            // a little past its last word of anything.
            double go = Math.Max(0, Math.Min(t, wordAt + STALE) - last.t) * speed;
            if (You != null)
            {
                double dx = You.Value.x - last.x, dy = You.Value.y - last.y;
                double c = Math.Cos(way), s = Math.Sin(way);
                double ahead = dx * c + dy * s, side = -dx * s + dy * c;
                if (ahead > 0 && Math.Abs(side) < WAIT_SIDE) go = Math.Min(go, Math.Max(0, ahead - WAIT_AHEAD));
            }
            go = Math.Max(0, go);
            bool moving = go >= (t - last.t) * speed - 1e-9;
            return new Key
            {
                x = last.x + Math.Cos(way) * go,
                y = last.y + Math.Sin(way) * go,
                heading = last.heading,
                speed = moving ? speed : 0,
                t = t,
                drawn = true,
                vx = moving ? vx : 0,
                vy = moving ? vy : 0,
            };
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
            touched.Clear();
            if (!Patch(d, ref at, cars, touched) || !Patch(d, ref at, people, null)) return false;
            double t = Word();
            foreach (int i in touched) Heard(i, t);
            // The lights, if the server sent them (an older one doesn't): kept on this follower's clock. Then their
            // timing, if it sent that (an older one's are all STAGE_MS a stage).
            if (at < d.Length)
            {
                int n = d[at];
                if (at + 1 + 3 * n <= d.Length)
                {
                    if (green.Length != n)
                    {
                        green = new int[n];
                        greenEnds = new double[n];
                        greens = new long[2 * n];
                    }
                    for (int k = 0; k < n; k++)
                    {
                        int r = at + 1 + 3 * k;
                        green[k] = d[r];
                        greenEnds[k] = t * 1000 + ((d[r + 1] << 8) | d[r + 2]);
                        greens[2 * k] = greens[2 * k + 1] = Porchlight.Life.RoadNet.STAGE_MS;
                    }
                    int g = at + 1 + 3 * n;
                    if (g < d.Length && d[g] == n && g + 1 + 2 * n <= d.Length)
                        for (int k = 0; k < 2 * n; k++) greens[k] = Math.Max(1, (int)d[g + 1 + k]) * 1000L;
                }
            }
            since = 0;
            return true;
        }

        readonly List<int> touched = new List<int>();

        static bool Patch(byte[] d, ref int at, Target[] into, List<int> touched)
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
                touched?.Add(i);
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

        /// <summary>Forget the host's town (leaving the room, or running it ourselves now).</summary>
        public void Clear()
        {
            cars = new Target[0];
            people = new Target[0];
            tracks = new Track[0];
            green = new int[0];
            greenEnds = new double[0];
            any = false;
            wordAt = double.NaN;
        }

        /// <summary>One frame: move the traffic and townsfolk toward where the host has them.</summary>
        public void Step(double dt, IReadOnlyList<TrafficCar> traffic, IReadOnlyList<Walker> walkers)
        {
            if (!any) return;
            since += dt;
            clock += dt;
            bool going = since < STALE;
            double k = Math.Min(1, dt * EASE);
            double now = clock - DELAY;
            for (int i = 0; i < Math.Min(cars.Length, traffic.Count); i++)
            {
                var c = traffic[i];
                c.hidden = cars[i].hidden || tracks[i].n == 0;
                if (c.hidden) continue;
                // Cars face (cos h, sin h).
                var at = Sample(tracks[i], now);
                c.x = at.x;
                c.y = at.y;
                c.heading = at.heading;
                c.speed = going ? at.speed : 0;
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
