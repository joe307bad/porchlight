// What changed in a town since it was last sent, for the players in it: every client
// carries each car and person on along their heading at their speed between messages
// (WorldSync.Step), so a car still where that puts it needn't be sent again. Each tick
// only those it would now have wrong are (more than a fifth of a tile off, turned,
// sped up or slowed, or not sent for two seconds), as WorldSync's own 7-byte records
// with their place in the list. A player arriving gets the whole town first (the full
// records); after that these.
//
//   u16 cars, u16 people          (how many the town has: a client of another count waits for the next full)
//   u16 n, n × (u16 index, record) for cars
//   u16 n, n × (u16 index, record) for people
//
// big-endian, as the records are. What's predicted is worked out from the records as
// sent (to their hundredths of a tile), as a client has them.

using System;
using System.Collections.Generic;

namespace Porchlight.Sim
{
    public sealed class Delta
    {
        public const int RECORD = 7;
        /// <summary>Tiles a client's carried-on position may be off before it's sent again.</summary>
        const double OFF = 0.2;
        /// <summary>Radians it may be turned.</summary>
        const double TURNED = 0.12;
        /// <summary>Tiles a second its speed may be off.</summary>
        const double SPEED = 0.12;
        /// <summary>Seconds anything goes unsent at most.</summary>
        const double REFRESH = 2;

        struct Sent
        {
            public double x, y, heading, speed, at;
        }

        Sent[] cars = new Sent[0], people = new Sent[0];

        /// <summary>The delta from what was last sent to these records (now, in seconds), and what's sent kept as the new base.</summary>
        public byte[] Next(byte[] carBytes, byte[] peopleBytes, double now)
        {
            int nc = carBytes.Length / RECORD, np = peopleBytes.Length / RECORD;
            var o = new List<byte>(8 + (nc + np) * 2);
            U16(o, nc);
            U16(o, np);
            Changed(o, carBytes, ref cars, now, car: true);
            Changed(o, peopleBytes, ref people, now, car: false);
            return o.ToArray();
        }

        /// <summary>Forget what was sent: the next delta is everything.</summary>
        public void Reset()
        {
            cars = new Sent[0];
            people = new Sent[0];
        }

        static void Changed(List<byte> o, byte[] b, ref Sent[] sent, double now, bool car)
        {
            int n = b.Length / RECORD;
            bool all = sent.Length != n;
            if (all) sent = new Sent[n];
            int countAt = o.Count;
            U16(o, 0);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var r = Read(b, i * RECORD);
                ref var s = ref sent[i];
                if (!all && !Stale(s, r, now, car)) continue;
                s = r;
                s.at = now;
                U16(o, i);
                for (int k = 0; k < RECORD; k++) o.Add(b[i * RECORD + k]);
                count++;
            }
            o[countAt] = (byte)(count >> 8);
            o[countAt + 1] = (byte)count;
        }

        /// <summary>Would a client carrying `s` on to now have `r` wrong?</summary>
        static bool Stale(in Sent s, in Sent r, double now, bool car)
        {
            double dt = now - s.at;
            if (dt >= REFRESH) return true;
            // Cars face (cos h, sin h); people (sin h, cos h) (WorldSync.Step).
            double px = s.x + (car ? Math.Cos(s.heading) : Math.Sin(s.heading)) * s.speed * dt;
            double py = s.y + (car ? Math.Sin(s.heading) : Math.Cos(s.heading)) * s.speed * dt;
            if (Math.Abs(px - r.x) > OFF || Math.Abs(py - r.y) > OFF) return true;
            double turn = Math.Abs(Math.IEEERemainder(r.heading - s.heading, 2 * Math.PI));
            return turn > TURNED || Math.Abs(r.speed - s.speed) > SPEED;
        }

        static Sent Read(byte[] b, int at) => new Sent
        {
            x = ((b[at] << 8) | b[at + 1]) / 100.0,
            y = ((b[at + 2] << 8) | b[at + 3]) / 100.0,
            heading = ((b[at + 4] << 8) | b[at + 5]) / 65536.0 * 2 * Math.PI,
            speed = b[at + 6] / 40.0,
        };

        static void U16(List<byte> o, int v)
        {
            o.Add((byte)(v >> 8));
            o.Add((byte)v);
        }
    }
}
