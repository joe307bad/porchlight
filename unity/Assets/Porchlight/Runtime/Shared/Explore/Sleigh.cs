// Flying a mission's sleigh (or any plane a mission gives you): rings hung in
// the sky to fly through (or near), the story taking the controls and
// bringing you down as a pilot would (round onto a straight run-in, gliding
// down it, flaring and sliding to a stop on a roof or in the street), a leap
// out of your seat onto a chimney's top and down it (and back up it to your
// seat), and a leap out onto the pavement. Pure: the game (MissionPlay) puts
// the plane and you where these say.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Explore
{
    /// <summary>
    /// A ring hung in the sky: its middle (map x, y and height, tiles), the way through it (level,
    /// a unit vector on the map) and its radius. Flown through either way.
    /// </summary>
    public struct SkyRing
    {
        public double x, y, h;
        public double ax, ay;
        public double radius;

        /// <summary>
        /// A ring `size` tiles across, its middle at (x, y) `h` up, facing someone coming from (fromX, fromY): the
        /// way through it points from them to it (any way at all if they're right under it).
        /// </summary>
        public static SkyRing Facing(double x, double y, double h, double size, double fromX, double fromY)
        {
            double dx = x - fromX, dy = y - fromY, d = Numbers.Hypot(dx, dy);
            if (d < 1e-6)
            {
                dx = 1;
                dy = 0;
                d = 1;
            }
            return new SkyRing { x = x, y = y, h = h, ax = dx / d, ay = dy / d, radius = size / 2 };
        }

        /// <summary>How far a point is in front of the ring's face (tiles, along the way through; negative behind).</summary>
        public double Side(double px, double py) => (px - x) * ax + (py - y) * ay;

        /// <summary>
        /// Whether a move from (x0, y0, h0) to (x1, y1, h1) passes through the ring, either way: it crosses the
        /// ring's face within `slack` times its radius of its middle (a little over 1 is kind on a near miss).
        /// </summary>
        public bool Through(double x0, double y0, double h0, double x1, double y1, double h1, double slack = 1.15)
        {
            double a = Side(x0, y0), b = Side(x1, y1);
            if (a == 0 && b == 0) return false;
            if (a * b > 0) return false;
            double t = a == b ? 0 : a / (a - b);
            double px = x0 + (x1 - x0) * t, py = y0 + (y1 - y0) * t, ph = h0 + (h1 - h0) * t;
            double off = Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y) + (ph - h) * (ph - h));
            return off <= radius * slack;
        }
        /// <summary>How near the ring counts as through it (tiles past its rim, any way: you needn't thread it).</summary>
        public const double NEAR = 1.5;

        /// <summary>
        /// Whether a move from (x0, y0, h0) to (x1, y1, h1) reaches the ring: through it (Through), or anywhere along the
        /// way within NEAR tiles of its rim (the nearest the move comes to its middle).
        /// </summary>
        public bool Reached(double x0, double y0, double h0, double x1, double y1, double h1, double near = NEAR)
        {
            if (Through(x0, y0, h0, x1, y1, h1)) return true;
            double mx = x1 - x0, my = y1 - y0, mh = h1 - h0;
            double len = mx * mx + my * my + mh * mh;
            double k = len < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - x0) * mx + (y - y0) * my + (h - h0) * mh) / len));
            double px = x0 + mx * k - x, py = y0 + my * k - y, ph = h0 + mh * k - h;
            return Math.Sqrt(px * px + py * py + ph * ph) <= radius + near;
        }
    }

    /// <summary>
    /// The story bringing a plane down, as a pilot would: from where it is, the way it's going and as fast, round
    /// in the tightest smooth turns it can make (a Dubins path: arcs of one radius and the straight between them)
    /// onto a straight run-in lined up with its spot, coming down and slowing as it goes; then down the run-in,
    /// gliding lower, flaring level just as it touches down, and sliding on its runners (or wheels) to a stop on
    /// its spot. Kept clear of whatever's under its way (`floor`) until it's nearly down. Position, heading and
    /// speed carry straight on from yours as it takes over, so there's no jolt; it's done once it's stopped.
    /// </summary>
    public sealed class Landing
    {
        /// <summary>How fast it's going as it touches down, and down the run-in (tiles a second).</summary>
        public const double TOUCH_SPEED = 0.9, APPROACH_SPEED = 3.2;
        /// <summary>How tight it turns (tiles), how long the straight run-in is, and how far it slides once down (tiles).</summary>
        public const double RADIUS = 2.4, FINAL = 4.5, SLIDE = 0.5;
        /// <summary>How high over its spot the run-in starts, a share of its length; the steepest it climbs or dives to keep clear of something.</summary>
        public const double GLIDE = 0.3, SLOPE = 0.6;
        /// <summary>How far it goes from its old speed to its approach speed (tiles, as a decay); how far out it pays no heed to the floor (tiles before it touches down).</summary>
        const double SLOW_OVER = 2.5, CLEAR_TILL = 1.0;
        /// <summary>How finely the way is worked out (tiles between samples).</summary>
        const double STEP = 0.05;

        readonly double[] xs, ys, hs, ds;
        readonly double v0, vGate;
        /// <summary>Where the run-in starts (map x, y).</summary>
        readonly double gx, gy;
        /// <summary>How long its way is (tiles), where the run-in starts along it, and where it touches down.</summary>
        public readonly double length, runIn, touch;
        /// <summary>Where it's to stand: the spot, how high, and which way (a map heading, as Vehicle's).</summary>
        public readonly double toX, toY, toH, toHeading;
        /// <summary>How long it takes (seconds).</summary>
        public readonly double duration;
        /// <summary>How far along its way it is (tiles), and how long it's been at it (seconds).</summary>
        public double along, time;

        /// <summary>
        /// From a plane at (x, y) `h` up, heading `heading` at `speed`, to (toX, toY) `toH` up facing `toHeading`,
        /// touching down `slide` tiles short of it. `floor` says how low it may be at a point facing a way (null: anywhere).
        /// </summary>
        public Landing(double x, double y, double h, double heading, double speed, double toX, double toY, double toH, double toHeading,
            double slide = SLIDE, Func<double, double, double, double> floor = null)
        {
            this.toX = toX;
            this.toY = toY;
            this.toH = toH;
            this.toHeading = toHeading;
            slide = Math.Max(0, slide);
            double fx = Math.Cos(toHeading), fy = Math.Sin(toHeading);
            gx = toX - fx * (slide + FINAL);
            gy = toY - fy * (slide + FINAL);
            var turns = Dubins.Shortest(x, y, heading, gx, gy, toHeading, RADIUS);
            runIn = turns.Length;
            touch = runIn + FINAL;
            length = touch + slide;
            int n = Math.Max(2, (int)Math.Ceiling(length / STEP) + 1);
            xs = new double[n];
            ys = new double[n];
            hs = new double[n];
            ds = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = Math.Min(length, i * STEP);
                if (s < runIn)
                {
                    var (px, py, pd) = turns.At(s);
                    xs[i] = px;
                    ys[i] = py;
                    ds[i] = pd;
                }
                else
                {
                    double u = s - runIn;
                    xs[i] = gx + fx * u;
                    ys[i] = gy + fy * u;
                    ds[i] = toHeading;
                }
            }
            // Down the way: to the top of the run-in (no higher than it is now, unless the spot is), down it to touch down
            // (level at both ends: the flare), then on the spot's height.
            double gateH = Math.Max(toH, Math.Min(toH + FINAL * GLIDE, h));
            double[] floorAt = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = Math.Min(length, i * STEP);
                double down;
                if (s < runIn) down = h + (gateH - h) * Smooth(s / runIn);
                else if (s < touch) down = toH + (gateH - toH) * Smooth(1 - (s - runIn) / FINAL);
                else down = toH;
                hs[i] = down;
                floorAt[i] = floor != null && s < touch - CLEAR_TILL ? floor(xs[i], ys[i], ds[i]) : double.NegativeInfinity;
                if (double.IsNaN(floorAt[i]) || double.IsInfinity(floorAt[i])) floorAt[i] = double.NegativeInfinity;
            }
            // Clear of what's under it: up over it, no steeper than SLOPE either side.
            double rise = SLOPE * STEP;
            for (int i = 1; i < n; i++) floorAt[i] = Math.Max(floorAt[i], floorAt[i - 1] - rise);
            for (int i = n - 2; i >= 0; i--) floorAt[i] = Math.Max(floorAt[i], floorAt[i + 1] - rise);
            // Taking over from just as high as it is, whatever's under it.
            double blend = Math.Min(1.5, length * 0.5);
            for (int i = 0; i < n; i++)
            {
                double s = Math.Min(length, i * STEP);
                double want = Math.Max(hs[i], floorAt[i]);
                hs[i] = h + (want - h) * Smooth(blend <= 0 ? 1 : s / blend);
            }
            hs[n - 1] = toH;
            v0 = Math.Max(0.3, speed);
            vGate = APPROACH_SPEED + (v0 - APPROACH_SPEED) * Math.Exp(-runIn / SLOW_OVER);
            // How long it takes, stepped as it will be.
            var copy = 0.0;
            double t = 0;
            for (int k = 0; k < 20000 && copy < length - 1e-9; k++)
            {
                copy = Advance(copy, 1.0 / 60);
                t += 1.0 / 60;
            }
            duration = t;
        }

        static double Smooth(double k)
        {
            k = Math.Max(0, Math.Min(1, k));
            return k * k * (3 - 2 * k);
        }

        /// <summary>How fast it goes `s` tiles along: slowing from its own speed to the approach's, down the run-in to touch-down speed, then sliding to a stop.</summary>
        public double SpeedAt(double s)
        {
            if (s < runIn) return APPROACH_SPEED + (v0 - APPROACH_SPEED) * Math.Exp(-s / SLOW_OVER);
            if (s < touch) return TOUCH_SPEED + (vGate - TOUCH_SPEED) * Smooth(1 - (s - runIn) / FINAL);
            double left = length - s, slide = length - touch;
            return slide <= 1e-9 ? TOUCH_SPEED : Math.Max(0.15, TOUCH_SPEED * Math.Sqrt(Math.Max(0, left) / slide));
        }

        double Advance(double s, double dt)
        {
            double mid = Math.Min(length, s + SpeedAt(s) * dt / 2);
            return Math.Min(length, s + SpeedAt(mid) * dt);
        }

        public bool Done => along >= length - 1e-9;

        /// <summary>How far through it is (0 to 1).</summary>
        public double Progress => length <= 0 ? 1 : Math.Min(1, along / length);

        /// <summary>Whether it's down (touched down, sliding to its stop or stopped).</summary>
        public bool Down => along >= touch - 1e-9;

        /// <summary>Where it is `t` (0 to 1) of the way along: map x, y, height, heading, and speed (tiles a second).</summary>
        public (double x, double y, double h, double heading, double speed) At(double t)
        {
            double s = Math.Max(0, Math.Min(1, t)) * length;
            double f = s / STEP;
            int i = Math.Min(xs.Length - 2, (int)Math.Floor(f));
            double k = Math.Max(0, Math.Min(1, f - i));
            // The last sample is at the very end (it may be nearer the one before than a step).
            if (i == xs.Length - 2)
            {
                double s0 = i * STEP, gap = length - s0;
                k = gap <= 1e-12 ? 1 : Math.Max(0, Math.Min(1, (s - s0) / gap));
            }
            double h = s >= touch ? toH : hs[i] + (hs[i + 1] - hs[i]) * k;
            // Down the run-in, on its line exactly.
            if (s >= runIn)
            {
                double u = s - runIn;
                return (gx + Math.Cos(toHeading) * u, gy + Math.Sin(toHeading) * u, h, toHeading, SpeedAt(s));
            }
            double x = xs[i] + (xs[i + 1] - xs[i]) * k;
            double y = ys[i] + (ys[i + 1] - ys[i]) * k;
            double heading = ds[i] + Orbit.ShortestTurn(ds[i], ds[i + 1]) * k;
            return (x, y, h, heading, SpeedAt(s));
        }

        /// <summary>On by dt seconds; where it is now.</summary>
        public (double x, double y, double h, double heading, double speed) Step(double dt)
        {
            time += Math.Max(0, dt);
            along = Advance(along, Math.Max(0, dt));
            if (Done)
            {
                along = length;
                return (toX, toY, toH, toHeading, 0);
            }
            return At(along / length);
        }
    }

    /// <summary>
    /// The shortest way from one place and heading to another for something that turns no tighter than a radius
    /// (Dubins): an arc, a straight (or a third arc) and an arc. Map terms: a heading points along (cos, sin).
    /// </summary>
    public sealed class Dubins
    {
        /// <summary>Each part: which way it turns (+1 towards a bigger heading, -1 the other, 0 straight) and how long it is (tiles).</summary>
        readonly (int turn, double length)[] parts;
        readonly double x0, y0, h0, radius;
        public double Length { get; }

        Dubins(double x, double y, double heading, double radius, (int, double)[] parts)
        {
            x0 = x;
            y0 = y;
            h0 = heading;
            this.radius = radius;
            this.parts = parts;
            double l = 0;
            foreach (var p in parts) l += p.Item2;
            Length = l;
        }

        static double Mod(double a)
        {
            double t = Math.PI * 2;
            a %= t;
            return a < 0 ? a + t : a;
        }

        /// <summary>From (x, y, heading), `turn` (+1, -1, 0) for `s` tiles.</summary>
        static (double x, double y, double h) Go(double x, double y, double h, int turn, double s, double r)
        {
            if (turn == 0) return (x + Math.Cos(h) * s, y + Math.Sin(h) * s, h);
            double phi = s / r * turn;
            return (x + turn * r * (Math.Sin(h + phi) - Math.Sin(h)), y - turn * r * (Math.Cos(h + phi) - Math.Cos(h)), h + phi);
        }

        /// <summary>Where it is `s` tiles along: map x, y and heading.</summary>
        public (double x, double y, double heading) At(double s)
        {
            double x = x0, y = y0, h = h0;
            s = Math.Max(0, s);
            foreach (var (turn, length) in parts)
            {
                double go = Math.Min(s, length);
                (x, y, h) = Go(x, y, h, turn, go, radius);
                s -= go;
                if (s <= 0) break;
            }
            return (x, y, h);
        }

        /// <summary>The shortest of the six kinds of way that gets there (checked by going it); a straight line if, somehow, none does.</summary>
        public static Dubins Shortest(double x, double y, double heading, double toX, double toY, double toHeading, double radius)
        {
            double dx = toX - x, dy = toY - y;
            double d = Numbers.Hypot(dx, dy) / radius;
            double theta = Mod(Math.Atan2(dy, dx));
            double a = Mod(heading - theta), b = Mod(toHeading - theta);
            double sa = Math.Sin(a), sb = Math.Sin(b), ca = Math.Cos(a), cb = Math.Cos(b), cab = Math.Cos(a - b), dd = d * d;
            var found = new List<(int, int, int, double, double, double)>();
            // LSL
            {
                double p2 = 2 + dd - 2 * cab + 2 * d * (sa - sb);
                if (p2 >= 0)
                {
                    double t1 = Math.Atan2(cb - ca, d + sa - sb);
                    found.Add((1, 0, 1, Mod(t1 - a), Math.Sqrt(p2), Mod(b - t1)));
                }
            }
            // RSR
            {
                double p2 = 2 + dd - 2 * cab + 2 * d * (sb - sa);
                if (p2 >= 0)
                {
                    double t1 = Math.Atan2(ca - cb, d - sa + sb);
                    found.Add((-1, 0, -1, Mod(a - t1), Math.Sqrt(p2), Mod(t1 - b)));
                }
            }
            // LSR
            {
                double p2 = -2 + dd + 2 * cab + 2 * d * (sa + sb);
                if (p2 >= 0)
                {
                    double p = Math.Sqrt(p2);
                    double t0 = Math.Atan2(-ca - cb, d + sa + sb) - Math.Atan2(-2, p);
                    found.Add((1, 0, -1, Mod(t0 - a), p, Mod(t0 - b)));
                }
            }
            // RSL
            {
                double p2 = -2 + dd + 2 * cab - 2 * d * (sa + sb);
                if (p2 >= 0)
                {
                    double p = Math.Sqrt(p2);
                    double t0 = Math.Atan2(ca + cb, d - sa - sb) - Math.Atan2(2, p);
                    found.Add((-1, 0, 1, Mod(a - t0), p, Mod(b - t0)));
                }
            }
            // RLR
            {
                double c = (6 - dd + 2 * cab + 2 * d * (sa - sb)) / 8;
                if (Math.Abs(c) <= 1)
                {
                    double phi = Math.Atan2(ca - cb, d - sa + sb);
                    double p = Mod(Math.PI * 2 - Math.Acos(c));
                    double t = Mod(a - phi + p / 2);
                    found.Add((-1, 1, -1, t, p, Mod(a - b - t + p)));
                }
            }
            // LRL
            {
                double c = (6 - dd + 2 * cab + 2 * d * (sb - sa)) / 8;
                if (Math.Abs(c) <= 1)
                {
                    double phi = Math.Atan2(ca - cb, d + sa - sb);
                    double p = Mod(Math.PI * 2 - Math.Acos(c));
                    double t = Mod(-a - phi + p / 2);
                    found.Add((1, -1, 1, t, p, Mod(b - a - t + p)));
                }
            }
            Dubins best = null;
            foreach (var (k1, k2, k3, t, p, q) in found)
            {
                // The middle's a straight (its length in radii) or an arc (its angle).
                var way = new Dubins(x, y, heading, radius, new[] { (k1, t * radius), (k2, p * radius), (k3, q * radius) });
                if (best != null && way.Length >= best.Length) continue;
                var end = way.At(way.Length);
                if (Numbers.Hypot(end.x - toX, end.y - toY) > 1e-6 * Math.Max(1, radius) || Math.Abs(Orbit.ShortestTurn(end.heading, toHeading)) > 1e-6) continue;
                best = way;
            }
            if (best != null) return best;
            double dist = Numbers.Hypot(dx, dy);
            return new Dubins(x, y, Math.Atan2(dy, dx), radius, new[] { (0, dist) });
        }
    }

    /// <summary>
    /// A roof's surface, sampled: how high it is at each point of a grid over the building (tiles; NaN where
    /// there's no roof). Where the sleigh's set down, where its reindeer stand, and what Santa walks on.
    /// </summary>
    public sealed class RoofMap
    {
        public readonly double x0, y0, x1, y1, step;
        public readonly int nx, ny;
        readonly double[] h;
        /// <summary>The highest it goes (its ridge, on a pitched roof).</summary>
        public readonly double Top;

        /// <summary>Over (x0, y0) to (x1, y1), every `step` tiles, `sample` saying how high the roof is at each point (NaN: none there).</summary>
        public RoofMap(double x0, double y0, double x1, double y1, double step, Func<double, double, double> sample)
        {
            this.x0 = x0;
            this.y0 = y0;
            this.x1 = x1;
            this.y1 = y1;
            this.step = step;
            nx = Math.Max(2, (int)Math.Ceiling((x1 - x0) / step) + 1);
            ny = Math.Max(2, (int)Math.Ceiling((y1 - y0) / step) + 1);
            h = new double[nx * ny];
            Top = double.NaN;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double v = sample(Math.Min(x1, x0 + i * step), Math.Min(y1, y0 + j * step));
                    h[j * nx + i] = v;
                    if (!double.IsNaN(v) && !(v <= Top)) Top = v;
                }
        }

        /// <summary>How high the roof is at (x, y): between the samples round it that have roof (NaN off the roof).</summary>
        public double At(double x, double y)
        {
            if (x < x0 - 1e-9 || y < y0 - 1e-9 || x > x1 + 1e-9 || y > y1 + 1e-9) return double.NaN;
            double fx = Math.Max(0, Math.Min(nx - 1, (x - x0) / step)), fy = Math.Max(0, Math.Min(ny - 1, (y - y0) / step));
            int i = Math.Min(nx - 2, (int)fx), j = Math.Min(ny - 2, (int)fy);
            double u = fx - i, v = fy - j;
            double sum = 0, weight = 0;
            void Add(int a, int b, double w)
            {
                double s = h[b * nx + a];
                if (double.IsNaN(s) || w <= 0) return;
                sum += s * w;
                weight += w;
            }
            Add(i, j, (1 - u) * (1 - v));
            Add(i + 1, j, u * (1 - v));
            Add(i, j + 1, (1 - u) * v);
            Add(i + 1, j + 1, u * v);
            // Right on a sample with roof, or between ones that all have it: theirs; next to its edge, the nearest with any.
            if (weight > 0.25) return sum / weight;
            return double.NaN;
        }

        /// <summary>
        /// Its ridge: the way it runs (a map heading along x or y, as the town's buildings are square to the map),
        /// its middle (on the ridge, halfway along the roof), and how long the roof is that way (tiles). A roof
        /// with no clear ridge (flat, or a hip's point) runs the longer way.
        /// </summary>
        public (Vec2 middle, double along, double length) Ridge()
        {
            double tolerance = 0.04;
            double lowest = double.PositiveInfinity;
            foreach (var v in h) if (!double.IsNaN(v)) lowest = Math.Min(lowest, v);
            if (double.IsNaN(Top)) return (new Vec2((x0 + x1) / 2, (y0 + y1) / 2), x1 - x0 >= y1 - y0 ? 0 : Math.PI / 2, Math.Max(x1 - x0, y1 - y0));
            tolerance = Math.Max(tolerance, (Top - lowest) * 0.06);
            // The highest samples: how far they spread each way, and where their middle is.
            double sx = 0, sy = 0;
            int n = 0;
            double hx0 = double.PositiveInfinity, hx1 = double.NegativeInfinity, hy0 = double.PositiveInfinity, hy1 = double.NegativeInfinity;
            double rx0 = double.PositiveInfinity, rx1 = double.NegativeInfinity, ry0 = double.PositiveInfinity, ry1 = double.NegativeInfinity;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double v = h[j * nx + i];
                    if (double.IsNaN(v)) continue;
                    double x = Math.Min(x1, x0 + i * step), y = Math.Min(y1, y0 + j * step);
                    rx0 = Math.Min(rx0, x);
                    rx1 = Math.Max(rx1, x);
                    ry0 = Math.Min(ry0, y);
                    ry1 = Math.Max(ry1, y);
                    if (v < Top - tolerance) continue;
                    sx += x;
                    sy += y;
                    n++;
                    hx0 = Math.Min(hx0, x);
                    hx1 = Math.Max(hx1, x);
                    hy0 = Math.Min(hy0, y);
                    hy1 = Math.Max(hy1, y);
                }
            double spreadX = hx1 - hx0, spreadY = hy1 - hy0;
            bool alongX = Math.Abs(spreadX - spreadY) > step * 1.5 ? spreadX > spreadY : rx1 - rx0 >= ry1 - ry0;
            double cx = sx / n, cy = sy / n;
            // Halfway along the roof, on the ridge.
            var middle = alongX ? new Vec2((rx0 + rx1) / 2, cy) : new Vec2(cx, (ry0 + ry1) / 2);
            return (middle, alongX ? 0 : Math.PI / 2, alongX ? rx1 - rx0 : ry1 - ry0);
        }
    }

    /// <summary>
    /// Where the sleigh's set down on a roof, and Santa's way down its chimney: the sleigh along the ridge, its
    /// runners resting on the roof (as high as the highest of the roof under them), the sleigh itself on the
    /// roof and the team running on along the ridge; the chimney beside Santa's seat, a little behind it on the
    /// roof's slope (the street's side, where it can), its top just over the ridge; and where he lands
    /// jumping out of his seat, by the sleigh's side towards it.
    /// </summary>
    public sealed class RoofStop
    {
        /// <summary>How far in from the end of the roof the back of the sleigh stays (tiles).</summary>
        public const double END_MARGIN = 0.08;
        /// <summary>The chimney: how far behind the seat and out to the side of the ridge (tiles), and how far its top stands over the roof round it, and over the ridge.</summary>
        public const double CHIMNEY_BACK = 0.3, CHIMNEY_OUT = 0.3, CHIMNEY_UP = 0.2, OVER_RIDGE = 0.06;
        /// <summary>Where Santa lands jumping out of his seat: this far out to the side (tiles), clear of the sleigh.</summary>
        public const double JUMP_OUT = 0.2;
        /// <summary>The chimney stays this far in from the roof's edges (tiles).</summary>
        public const double EDGE = 0.15;

        /// <summary>The sleigh's middle, the way it faces (a map heading, as Vehicle's) and how high it's set (its altitude).</summary>
        public Vec2 middle;
        public double heading, altitude;
        /// <summary>Where Santa sits, how high his seat is, where he lands jumping out, the chimney and how high its top is.</summary>
        public Vec2 seat, jumpTo, chimney;
        public double seatHeight, rim;

        /// <summary>
        /// On `roof`, the sleigh coming from (fromX, fromY) (it faces whichever way along the ridge is nearer the way it's
        /// coming), the chimney on the `side` (a map direction: the street's) where it can be. `lift` is how far over its
        /// altitude the bottom of the model is drawn; the rest are Catalog's SLEIGH_* (tiles).
        /// </summary>
        public static RoofStop Place(RoofMap roof, double fromX, double fromY, Vec2 side,
            double seatBack, double seatHeight, double runnerFrom, double runnerTo, double gauge, double runnerLow, double lift)
        {
            var (ridge, along, length) = roof.Ridge();
            // Along the ridge, the way that's nearer the way it's coming in.
            double wantX = ridge.x - fromX, wantY = ridge.y - fromY;
            if (Numbers.Hypot(wantX, wantY) < 1e-6) wantX = 1;
            double heading = Math.Cos(along) * wantX + Math.Sin(along) * wantY >= 0 ? along : along + Math.PI;
            var f = new Vec2(Math.Cos(heading), Math.Sin(heading));
            var right = new Vec2(f.y, -f.x);
            // The team centred on the roof, unless that leaves the back of the sleigh off its end: then on, just enough.
            double on = Math.Max(0, runnerTo - length / 2 + END_MARGIN);
            var middle = new Vec2(ridge.x + f.x * on, ridge.y + f.y * on);
            var seat = new Vec2(middle.x - f.x * seatBack, middle.y - f.y * seatBack);
            // Down onto the highest of the roof under its runners.
            double under = double.NegativeInfinity;
            for (int k = 0; k <= 4; k++)
            {
                double u = runnerFrom + (runnerTo - runnerFrom) * k / 4;
                foreach (double s in new[] { -gauge, gauge })
                {
                    double v = roof.At(middle.x - f.x * u + right.x * s, middle.y - f.y * u + right.y * s);
                    if (!double.IsNaN(v)) under = Math.Max(under, v);
                }
            }
            if (double.IsNegativeInfinity(under)) under = roof.Top;
            double altitude = under - lift - runnerLow;
            // The chimney: behind the seat, out to the street's side (or the other, or on the ridge, where there's roof).
            double sideSign = Geo.Dot(right, side) >= 0 ? 1 : -1;
            Vec2 chimney = seat;
            double best = double.NegativeInfinity;
            foreach (double sgn in new[] { sideSign, -sideSign, 0 })
                foreach (double back in new[] { CHIMNEY_BACK, CHIMNEY_BACK * 0.5, 0 })
                {
                    double outward = sgn == 0 ? 0 : CHIMNEY_OUT;
                    var c = new Vec2(seat.x - f.x * back + right.x * outward * sgn, seat.y - f.y * back + right.y * outward * sgn);
                    c = new Vec2(Math.Max(roof.x0 + EDGE, Math.Min(roof.x1 - EDGE, c.x)), Math.Max(roof.y0 + EDGE, Math.Min(roof.y1 - EDGE, c.y)));
                    if (double.IsNaN(roof.At(c.x, c.y))) continue;
                    // The first with roof under it, preferring one clear of the sleigh.
                    double clear = Math.Abs(Geo.Dot(new Vec2(c.x - seat.x, c.y - seat.y), right));
                    double score = (sgn == sideSign ? 2 : sgn == 0 ? 0 : 1) + Math.Min(clear, 0.25) * 4 + back;
                    if (score <= best) continue;
                    best = score;
                    chimney = c;
                }
            double here = roof.At(chimney.x, chimney.y);
            if (double.IsNaN(here)) here = under;
            double rim = Math.Max(here + CHIMNEY_UP, roof.Top + OVER_RIDGE);
            // Out of his seat to the chimney's side of the sleigh.
            double towards = Geo.Dot(new Vec2(chimney.x - seat.x, chimney.y - seat.y), right);
            double jumpSign = Math.Abs(towards) < 1e-6 ? sideSign : Math.Sign(towards);
            var jumpTo = new Vec2(seat.x + right.x * JUMP_OUT * jumpSign - f.x * 0.05, seat.y + right.y * JUMP_OUT * jumpSign - f.y * 0.05);
            if (double.IsNaN(roof.At(jumpTo.x, jumpTo.y))) jumpTo = new Vec2(seat.x - f.x * (runnerTo - seatBack + 0.1), seat.y - f.y * (runnerTo - seatBack + 0.1));
            if (double.IsNaN(roof.At(jumpTo.x, jumpTo.y))) jumpTo = seat;
            return new RoofStop
            {
                middle = middle,
                heading = heading,
                altitude = altitude,
                seat = seat,
                seatHeight = altitude + lift + seatHeight,
                jumpTo = jumpTo,
                chimney = chimney,
                rim = rim,
            };
        }
    }

    /// <summary>
    /// Where someone is as the story moves them (a jump, a walk): map x, y, the height of the ground (or roof) under
    /// them, how high off it they are, which way they face (Walker's heading: along (sin, cos)), how fast they walk,
    /// whether they're to be seen, and how fast they're going up (tiles a second; down is less than 0).
    /// </summary>
    public struct WalkerPose
    {
        public Vec2 at;
        public double floor, lift, heading, pace, rise;
        public bool shown;

        public WalkerPose(Vec2 at, double floor, double lift, double heading, double pace, bool shown, double rise)
        {
            this.at = at;
            this.floor = floor;
            this.lift = lift;
            this.heading = heading;
            this.pace = pace;
            this.shown = shown;
            this.rise = rise;
        }

        /// <summary>How high their feet are.</summary>
        public double Up => floor + lift;
    }

    /// <summary>A leap: a parabola from one height to another over (0 to 1) of it, at its highest `apex` (over both ends).</summary>
    public static class Leap
    {
        /// <summary>How high it is `k` of the way through (0 to 1), and how fast it's rising then (per unit of k).</summary>
        public static (double up, double rate) At(double from, double to, double apex, double k)
        {
            k = Math.Max(0, Math.Min(1, k));
            double a = Math.Max(apex - from, Math.Max(to - from, 0) + 1e-6), d = to - from;
            // up = from + B k - C k^2, through `to` at 1, with its top at `apex`.
            double c = 2 * a - d + 2 * Math.Sqrt(Math.Max(0, a * (a - d)));
            double b = c + d;
            return (from + b * k - c * k * k, b - 2 * c * k);
        }

        /// <summary>A walker's heading from one point to another (or `otherwise`, if they're the same).</summary>
        public static double Facing(Vec2 from, Vec2 to, double otherwise) => Geo.Dist(from, to) < 1e-6 ? otherwise : DetMath.Atan2(to.x - from.x, to.y - from.y);
    }

    /// <summary>
    /// Out of the sleigh's seat onto the pavement: a turn to face the way out, a leap up and out over its side in an
    /// arc, down onto the pavement (knees bending: the landing), a moment's settle, a walk to where you're to stand,
    /// and a turn to face the way you're to go.
    /// </summary>
    public sealed class Hop
    {
        /// <summary>How long the turn, the leap, the settle and the last turn take (seconds); how fast you walk (tiles a second).</summary>
        public const double TURN = 0.25, LEAP = 0.7, SETTLE = 0.3, FACE = 0.3, WALK = 0.75;
        /// <summary>How high over your seat the leap goes (tiles).</summary>
        public const double HEIGHT = 0.3;

        public readonly Vec2 from, landAt, to;
        public readonly double fromHeight, facing;
        readonly Func<double, double, double> ground;
        readonly double walk;
        public double time;

        /// <summary>From your seat (`from`, its top `fromHeight` up) out to `landAt`, then on to `to`, to face `facing` (a walker's heading). `ground`: how high it is at a point.</summary>
        public Hop(Vec2 from, double fromHeight, Vec2 landAt, Vec2 to, double facing, Func<double, double, double> ground)
        {
            this.from = from;
            this.fromHeight = fromHeight;
            this.landAt = landAt;
            this.to = to;
            this.facing = facing;
            this.ground = ground;
            walk = Geo.Dist(landAt, to) / WALK;
        }

        public double Duration => TURN + LEAP + SETTLE + walk + FACE;

        public bool Done => time >= Duration;

        double Ground(Vec2 p)
        {
            double g = ground?.Invoke(p.x, p.y) ?? 0;
            return double.IsNaN(g) ? 0 : g;
        }

        public WalkerPose Step(double dt)
        {
            time = Math.Min(Duration, time + Math.Max(0, dt));
            return At(time);
        }

        public WalkerPose At(double t)
        {
            t = Math.Max(0, Math.Min(Duration, t));
            double out_ = Leap.Facing(from, landAt, facing);
            double on = Leap.Facing(landAt, to, out_);
            if (t < TURN)
            {
                // Up out of your seat, turning to the way out.
                return new WalkerPose(from, fromHeight, 0, out_, 0, true, 0);
            }
            if ((t -= TURN) < LEAP)
            {
                double k = t / LEAP;
                double land = Ground(landAt);
                var (up, rate) = Leap.At(fromHeight, land, Math.Max(fromHeight, land) + HEIGHT, k);
                var at = new Vec2(from.x + (landAt.x - from.x) * k, from.y + (landAt.y - from.y) * k);
                double floor = Math.Min(Math.Min(fromHeight, land), up);
                return new WalkerPose(at, floor, up - floor, out_, 0, true, rate / LEAP);
            }
            if ((t -= LEAP) < SETTLE) return new WalkerPose(landAt, Ground(landAt), 0, out_, 0, true, 0);
            if ((t -= SETTLE) < walk)
            {
                double k = walk <= 0 ? 1 : t / walk;
                var at = new Vec2(landAt.x + (to.x - landAt.x) * k, landAt.y + (to.y - landAt.y) * k);
                return new WalkerPose(at, Ground(at), 0, on, WALK, true, 0);
            }
            return new WalkerPose(to, Ground(to), 0, facing, 0, true, 0);
        }
    }

    /// <summary>
    /// Down a chimney from your seat in the sleigh on the roof, or back up it to your seat: a turn towards the
    /// chimney, a leap out of your seat in a high arc right onto its top (landing on it, knees bending), a moment
    /// stood there turned to the street, then a little hop and down inside it feet first (out of sight once you're
    /// in); or popping up out of it and all of that backwards, ending with the leap back into your seat. Where you
    /// are as it goes (WalkerPose).
    /// </summary>
    public sealed class ChimneyTrip
    {
        /// <summary>How long the turn, the stand on top and the drop take; the leap, this long and as long again a tile across (seconds).</summary>
        public const double TURN = 0.3, PERCH = 0.6, DROP = 0.65, LEAP = 0.6, LEAP_PER_TILE = 0.3;
        /// <summary>How high over the higher end the leap goes, and the hop before the drop (tiles); how far below the top you drop to.</summary>
        public const double LEAP_HEIGHT = 0.35, HOP_HEIGHT = 0.1, DOWN_INTO = 0.5;
        /// <summary>Under its top by this much (tiles), you're out of sight inside it.</summary>
        public const double HIDDEN = 0.14;

        public readonly bool down;
        readonly Vec2 seat, chimney;
        readonly double seatHeight, rim, perched, leap;
        public double time;

        /// <summary>
        /// `down`: from your seat (its top `seatHeight` up) to the chimney (its top `rim` up) and down it, turned to face
        /// `perched` (a walker's heading: the street's way, say) on its top; else up out of it and back to your seat.
        /// </summary>
        public ChimneyTrip(bool down, Vec2 seat, double seatHeight, Vec2 chimney, double rim, double perched)
        {
            this.down = down;
            this.seat = seat;
            this.seatHeight = seatHeight;
            this.chimney = chimney;
            this.rim = rim;
            this.perched = perched;
            leap = LEAP + LEAP_PER_TILE * Geo.Dist(seat, chimney);
        }

        public double Duration => TURN + leap + PERCH + DROP;

        /// <summary>When you're on the chimney's top (seconds in): the leap's done.</summary>
        public double OnTop => down ? TURN + leap : DROP;

        public bool Done => time >= Duration;

        public WalkerPose Step(double dt)
        {
            time = Math.Min(Duration, time + Math.Max(0, dt));
            return At(time);
        }

        /// <summary>Where you are `t` seconds into it.</summary>
        public WalkerPose At(double t)
        {
            t = Math.Max(0, Math.Min(Duration, t));
            // Up is down played backwards (going up where it went down, facing back the way it came).
            double s = down ? t : Duration - t;
            double sign = down ? 1 : -1;
            double toChimney = Leap.Facing(seat, chimney, perched);
            double turn = down ? 0 : Math.PI;
            if (s < TURN) return new WalkerPose(seat, seatHeight, 0, toChimney + turn, 0, true, 0);
            if ((s -= TURN) < leap)
            {
                double k = s / leap;
                var (up, rate) = Leap.At(seatHeight, rim, Math.Max(seatHeight, rim) + LEAP_HEIGHT, k);
                var at = new Vec2(seat.x + (chimney.x - seat.x) * k, seat.y + (chimney.y - seat.y) * k);
                double floor = Math.Min(Math.Min(seatHeight, rim), up);
                return new WalkerPose(at, floor, up - floor, toChimney + turn, 0, true, sign * rate / leap);
            }
            if ((s -= leap) < PERCH) return new WalkerPose(chimney, rim, 0, perched, 0, true, 0);
            s -= PERCH;
            {
                double k = Math.Min(1, s / DROP);
                double bottom = rim - DOWN_INTO;
                var (up, rate) = Leap.At(rim, bottom, rim + HOP_HEIGHT, k);
                return new WalkerPose(chimney, bottom, up - bottom, perched, 0, up > rim - HIDDEN, sign * rate / DROP);
            }
        }
    }
}
