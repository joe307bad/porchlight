// Flying a mission's sleigh (or any plane a mission gives you): rings hung in
// the sky to fly through, the gentle hand-over as the story takes the
// controls and brings you down (on a roof, or in the street), and the trip
// across a roof and down a chimney (and back up it to your seat). Pure: the
// game (MissionPlay) puts the plane and you where these say.

using System;
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
    }

    /// <summary>
    /// The story bringing a plane down: from where it is, the way it's going and as fast, along a smooth curve
    /// to a spot (on a roof, or the street), lower and slower as it comes, facing the way it's to stand.
    /// Position and velocity carry straight on from yours as it takes over, so there's no jolt, only a turn
    /// and a glide; it's done once it's there.
    /// </summary>
    public sealed class Landing
    {
        /// <summary>How fast it's going as it touches down (tiles a second), and how fast it glides on average.</summary>
        public const double TOUCH_SPEED = 0.8, GLIDE = 3.5;
        /// <summary>The shortest and longest a landing takes (seconds).</summary>
        public const double MIN_TIME = 4.5, MAX_TIME = 9;

        readonly double x0, y0, h0, mx0, my0;
        readonly double x1, y1, h1, mx1, my1;
        /// <summary>Where it's to stand: the spot, how high, and which way (a map heading, as Vehicle's).</summary>
        public readonly double toX, toY, toH, toHeading;
        /// <summary>How long it takes (seconds).</summary>
        public readonly double duration;
        public double time;

        /// <summary>
        /// From a plane at (x, y) `h` up, heading `heading` at `speed`, to (toX, toY) `toH` up facing `toHeading`.
        /// </summary>
        public Landing(double x, double y, double h, double heading, double speed, double toX, double toY, double toH, double toHeading)
        {
            x0 = x;
            y0 = y;
            h0 = h;
            x1 = toX;
            y1 = toY;
            h1 = toH;
            this.toX = toX;
            this.toY = toY;
            this.toH = toH;
            this.toHeading = toHeading;
            double chord = Numbers.Hypot(toX - x, toY - y);
            duration = Math.Max(MIN_TIME, Math.Min(MAX_TIME, chord / GLIDE));
            // Hermite tangents are velocities times the duration.
            double v = Math.Max(0, speed);
            mx0 = Math.Cos(heading) * v * duration;
            my0 = Math.Sin(heading) * v * duration;
            mx1 = Math.Cos(toHeading) * TOUCH_SPEED * duration;
            my1 = Math.Sin(toHeading) * TOUCH_SPEED * duration;
        }

        public bool Done => time >= duration;

        /// <summary>How far through it is (0 to 1).</summary>
        public double Progress => Math.Min(1, time / duration);

        /// <summary>Where it is `t` (0 to 1) of the way through: map x, y, height, heading, and speed (tiles a second).</summary>
        public (double x, double y, double h, double heading, double speed) At(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            double t2 = t * t, t3 = t2 * t;
            double a = 2 * t3 - 3 * t2 + 1, b = t3 - 2 * t2 + t, c = -2 * t3 + 3 * t2, d = t3 - t2;
            double x = a * x0 + b * mx0 + c * x1 + d * mx1;
            double y = a * y0 + b * my0 + c * y1 + d * my1;
            // Down smoothly: level as it takes over, level as it touches down.
            double h = h0 + (h1 - h0) * (3 * t2 - 2 * t3);
            double da = 6 * t2 - 6 * t, db = 3 * t2 - 4 * t + 1, dc = -6 * t2 + 6 * t, dd = 3 * t2 - 2 * t;
            double vx = (da * x0 + db * mx0 + dc * x1 + dd * mx1) / duration;
            double vy = (da * y0 + db * my0 + dc * y1 + dd * my1) / duration;
            double speed = Numbers.Hypot(vx, vy);
            double heading = t >= 1 || speed < 1e-6 ? toHeading : Math.Atan2(vy, vx);
            return (x, y, h, heading, speed);
        }

        /// <summary>On by dt seconds; where it is now.</summary>
        public (double x, double y, double h, double heading, double speed) Step(double dt)
        {
            time = Math.Min(duration, time + Math.Max(0, dt));
            var at = At(time / duration);
            return Done ? (toX, toY, toH, toHeading, 0) : at;
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
    /// A jump from one place to another: out of the sleigh's seat onto a roof or the pavement, say. An arc from
    /// (from, fromHeight) to (to, toHeight), over `height` higher than the straight line at its middle. Where you
    /// are as it goes: map x, y, the height of the ground (or roof) under you, how high off it you are, which way
    /// you face, and that you're to be seen.
    /// </summary>
    public sealed class Hop
    {
        public readonly Vec2 from, to;
        public readonly double fromHeight, toHeight, height, duration;
        readonly Func<double, double, double> ground;
        public double time;

        public Hop(Vec2 from, double fromHeight, Vec2 to, double toHeight, double height, double duration, Func<double, double, double> ground)
        {
            this.from = from;
            this.to = to;
            this.fromHeight = fromHeight;
            this.toHeight = toHeight;
            this.height = height;
            this.duration = Math.Max(1e-3, duration);
            this.ground = ground;
        }

        public bool Done => time >= duration;

        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) Step(double dt)
        {
            time = Math.Min(duration, time + Math.Max(0, dt));
            return At(time);
        }

        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) At(double t)
        {
            double k = Math.Max(0, Math.Min(1, t / duration));
            var at = new Vec2(from.x + (to.x - from.x) * k, from.y + (to.y - from.y) * k);
            double up = fromHeight + (toHeight - fromHeight) * k + height * Math.Sin(Math.PI * k);
            double floor = ground?.Invoke(at.x, at.y) ?? double.NaN;
            if (double.IsNaN(floor)) floor = Math.Min(fromHeight, toHeight);
            floor = Math.Min(floor, up);
            double heading = Geo.Dist(from, to) < 1e-6 ? 0 : DetMath.Atan2(to.x - from.x, to.y - from.y);
            return (at, floor, up - floor, heading, 0, true);
        }
    }

    /// <summary>
    /// Down a chimney from your seat in the sleigh on the roof, or back up it to your seat: a jump out of your
    /// seat onto the roof beside the sleigh, a walk across the roof to the chimney, a hop up onto its top, and
    /// the drop down inside it (out of sight once you're in); or climbing out of it and all of that backwards,
    /// ending with a jump up into your seat. Where you are as it goes: map x, y, the height of the roof under
    /// you, how high off it you are, which way you face, how fast you walk, and whether you're to be seen.
    /// </summary>
    public sealed class ChimneyTrip
    {
        /// <summary>How fast you walk across a roof (tiles a second); how long the jump out of your seat, the hop up onto the chimney and the drop take (seconds).</summary>
        public const double WALK = 0.7, JUMP = 0.6, HOP = 0.45, DROP = 0.6;
        /// <summary>How far from the chimney's middle you stand to hop up onto it, how high over the line the jump and the hop go, and how far below its top you drop out of sight (tiles).</summary>
        public const double BESIDE = 0.16, JUMP_HEIGHT = 0.2, HOP_HEIGHT = 0.12, DOWN_INTO = 0.45;
        /// <summary>Under its top by this much (tiles), you're out of sight inside it.</summary>
        public const double HIDDEN = 0.12;

        public readonly bool down;
        readonly Vec2 seat, jumpTo, beside, chimney;
        readonly double seatHeight, rim;
        readonly Func<double, double, double> roof;
        readonly double walk;
        public double time;

        /// <summary>
        /// `down`: from your seat (its top `seatHeight` up) out onto the roof at `jumpTo`, to the chimney (its top `rim`
        /// up) and down it; else up out of it and back to your seat. `roof` says how high the roof is at a point (NaN
        /// off it).
        /// </summary>
        public ChimneyTrip(bool down, Vec2 seat, double seatHeight, Vec2 jumpTo, Vec2 chimney, double rim, Func<double, double, double> roof)
        {
            this.down = down;
            this.seat = seat;
            this.seatHeight = seatHeight;
            this.jumpTo = jumpTo;
            this.chimney = chimney;
            this.roof = roof;
            double d = Geo.Dist(jumpTo, chimney);
            var dir = d < 1e-6 ? new Vec2(1, 0) : new Vec2((chimney.x - jumpTo.x) / d, (chimney.y - jumpTo.y) / d);
            double toBeside = Math.Max(0, d - BESIDE);
            beside = new Vec2(jumpTo.x + dir.x * toBeside, jumpTo.y + dir.y * toBeside);
            walk = toBeside / WALK;
            this.rim = Math.Max(RoofAt(chimney), rim);
        }

        /// <summary>How high the roof is at a point (or, off it, the nearest height we know: the seat's).</summary>
        double RoofAt(Vec2 p)
        {
            double h = roof?.Invoke(p.x, p.y) ?? double.NaN;
            return double.IsNaN(h) ? seatHeight : h;
        }

        public double Duration => JUMP + walk + HOP + DROP;

        public bool Done => time >= Duration;

        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) Step(double dt)
        {
            time = Math.Min(Duration, time + Math.Max(0, dt));
            return At(time);
        }

        static double Facing(Vec2 from, Vec2 to, double otherwise) => Geo.Dist(from, to) < 1e-6 ? otherwise : DetMath.Atan2(to.x - from.x, to.y - from.y);

        /// <summary>Where you are `t` seconds into it.</summary>
        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) At(double t)
        {
            t = Math.Max(0, Math.Min(Duration, t));
            // Up is down played backwards (the way you face turned round).
            double s = down ? t : Duration - t;
            double toChimney = Facing(jumpTo, chimney, Facing(seat, chimney, 0));
            double turn = down ? 0 : Math.PI;
            Vec2 at;
            double up, pace = 0, heading;
            bool shown = true;
            if (s < JUMP)
            {
                // Out of your seat in an arc, down onto the roof by the sleigh (or up into your seat, coming back).
                double k = s / JUMP;
                at = new Vec2(seat.x + (jumpTo.x - seat.x) * k, seat.y + (jumpTo.y - seat.y) * k);
                up = seatHeight + (RoofAt(jumpTo) - seatHeight) * k + JUMP_HEIGHT * Math.Sin(Math.PI * k);
                heading = Facing(seat, jumpTo, toChimney) + turn;
            }
            else if ((s -= JUMP) < walk)
            {
                double k = walk <= 0 ? 1 : s / walk;
                at = new Vec2(jumpTo.x + (beside.x - jumpTo.x) * k, jumpTo.y + (beside.y - jumpTo.y) * k);
                up = RoofAt(at);
                pace = WALK;
                heading = toChimney + turn;
            }
            else if ((s -= walk) < HOP)
            {
                // Up onto the chimney's top in an arc (or down off it, coming back).
                double k = s / HOP;
                at = new Vec2(beside.x + (chimney.x - beside.x) * k, beside.y + (chimney.y - beside.y) * k);
                double from = RoofAt(beside);
                up = from + (rim - from) * k + HOP_HEIGHT * Math.Sin(Math.PI * k);
                heading = toChimney + turn;
            }
            else
            {
                // Down inside it (or up out of it, coming back): out of sight once in.
                s -= HOP;
                double k = Math.Min(1, s / DROP);
                at = chimney;
                up = rim - DOWN_INTO * k * k;
                heading = toChimney + turn;
                shown = up > rim - HIDDEN;
            }
            double floor = Math.Min(RoofAt(at), up);
            return (at, floor, up - floor, heading, pace, shown);
        }
    }
}
