// Smooth motion for other players. Each sends its position about 30 times a
// second, stamped with when it took it on its own clock; we keep them by that
// stamp and draw each player a little in the past on their own clock, between
// the two moves either side, so they glide rather than step. Pure.

using System;
using System.Collections.Generic;

namespace Porchlight.Net
{
    public struct Snap
    {
        public double t, x, y, z, yaw;
    }

    public static class Interp
    {
        public const double INTERP_DELAY = 120;
        public const double EXTRAPOLATE = 200;
        public const double KEEP = 1500;
        const double TAU = Math.PI * 2;

        public static double TurnBetween(double a, double b)
        {
            double d = (((b - a) % TAU) + TAU) % TAU;
            return d > Math.PI ? d - TAU : d;
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public sealed class SnapshotBuffer
        {
            List<Snap> snaps = new List<Snap>();

            public int Size => snaps.Count;

            public void Push(Snap s)
            {
                if (!Finite(s.t)) return;
                if (snaps.Count > 0)
                {
                    var last = snaps[snaps.Count - 1];
                    if (s.t < last.t) return;
                    if (s.t == last.t)
                    {
                        snaps[snaps.Count - 1] = s;
                        return;
                    }
                }
                snaps.Add(s);
                if (snaps.Count > 64) snaps.RemoveRange(0, snaps.Count - 64);
            }

            public Snap? Sample(double t)
            {
                if (snaps.Count == 0 || !Finite(t)) return null;
                var last = snaps[snaps.Count - 1];
                if (t - last.t > KEEP)
                {
                    snaps = new List<Snap> { last };
                    var r = last;
                    r.t = t;
                    return r;
                }
                if (t <= snaps[0].t)
                {
                    var r = snaps[0];
                    r.t = t;
                    return r;
                }
                if (t >= last.t)
                {
                    if (snaps.Count < 2)
                    {
                        var r = last;
                        r.t = t;
                        return r;
                    }
                    var prev = snaps[snaps.Count - 2];
                    double ahead = Math.Min(t - last.t, EXTRAPOLATE);
                    double span = last.t - prev.t;
                    double k = ahead / (span == 0 ? 1 : span);
                    return new Snap
                    {
                        t = t,
                        x = last.x + (last.x - prev.x) * k,
                        y = Math.Max(0, last.y + (last.y - prev.y) * k),
                        z = last.z + (last.z - prev.z) * k,
                        yaw = last.yaw,
                    };
                }
                int i = snaps.Count - 2;
                while (i > 0 && snaps[i].t > t) i--;
                var a = snaps[i];
                var b = snaps[i + 1];
                if (i > 0) snaps = snaps.GetRange(i, snaps.Count - i);
                double gap = b.t - a.t;
                double kk = (t - a.t) / (gap == 0 ? 1 : gap);
                return new Snap
                {
                    t = t,
                    x = a.x + (b.x - a.x) * kk,
                    y = a.y + (b.y - a.y) * kk,
                    z = a.z + (b.z - a.z) * kk,
                    yaw = a.yaw + TurnBetween(a.yaw, b.yaw) * kk,
                };
            }
        }

        const double WINDOW = 3000;
        const double JUMP = 400;
        const double CATCH_UP = 300;
        const double MAX_WARP = 0.15;

        /// <summary>Which moment of one player's own clock to show: steady, a touch faster or slower to stay on course.</summary>
        public sealed class PlaybackClock
        {
            readonly List<(double at, double delay)> arrivals = new List<(double, double)>();
            double? head;

            public void Observe(double sent, double now)
            {
                if (!Finite(sent) || !Finite(now)) return;
                arrivals.Add((now, now - sent));
                while (arrivals.Count > 1 && now - arrivals[0].at > WINDOW) arrivals.RemoveAt(0);
            }

            public double? Advance(double now, double dt)
            {
                if (arrivals.Count == 0) return null;
                double best = double.PositiveInfinity;
                foreach (var a in arrivals) best = Math.Min(best, a.delay);
                double ideal = now - best - INTERP_DELAY;
                if (head == null || Math.Abs(ideal - head.Value) > JUMP) head = ideal;
                else head = head.Value + dt * (1 + Math.Max(-MAX_WARP, Math.Min(MAX_WARP, (ideal - head.Value) / CATCH_UP)));
                return head;
            }
        }
    }
}
