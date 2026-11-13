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
    /// Down a chimney from your seat on the roof, or back up it to your seat: walking across the roof
    /// between your seat and the chimney, hopping up onto its top, and dropping down inside it (out of sight
    /// once you're in), or climbing out of it and back. Where you are as it goes: map x, y, the height of
    /// what you're stood on, how high off it you are, which way you face, how fast you walk, and whether
    /// you're to be seen.
    /// </summary>
    public sealed class ChimneyTrip
    {
        /// <summary>How fast you walk across a roof (tiles a second), how long a hop up or down takes, and the drop (seconds).</summary>
        public const double WALK = 1.1, HOP = 0.45, DROP = 0.55;
        /// <summary>How far from the chimney's middle you stand to hop up onto it, how high over its top the hop goes, and how far below its top you drop out of sight (tiles).</summary>
        public const double BESIDE = 0.24, HOP_HEIGHT = 0.16, DOWN_INTO = 0.6;

        public readonly bool down;
        readonly Vec2 seat, beside, chimney;
        readonly double roof, rim;
        readonly double walk;
        public double time;

        /// <summary>
        /// `down`: from your seat (on a roof `roof` up) to the chimney (its top `rim` up) and down it; else up out
        /// of it and back to your seat.
        /// </summary>
        public ChimneyTrip(bool down, Vec2 seat, double roof, Vec2 chimney, double rim)
        {
            this.down = down;
            this.seat = seat;
            this.chimney = chimney;
            this.roof = roof;
            this.rim = Math.Max(roof, rim);
            double d = Geo.Dist(seat, chimney);
            var dir = d < 1e-6 ? new Vec2(1, 0) : new Vec2((chimney.x - seat.x) / d, (chimney.y - seat.y) / d);
            double toBeside = Math.Max(0, d - BESIDE);
            beside = new Vec2(seat.x + dir.x * toBeside, seat.y + dir.y * toBeside);
            walk = toBeside / WALK;
        }

        public double Duration => walk + HOP + DROP;

        public bool Done => time >= Duration;

        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) Step(double dt)
        {
            time = Math.Min(Duration, time + Math.Max(0, dt));
            return At(time);
        }

        /// <summary>Where you are `t` seconds into it.</summary>
        public (Vec2 at, double floor, double lift, double heading, double pace, bool shown) At(double t)
        {
            t = Math.Max(0, Math.Min(Duration, t));
            // Up is down played backwards (the way you face turned round as you walk).
            double s = down ? t : Duration - t;
            double toChimney = DetMath.Atan2(chimney.x - seat.x, chimney.y - seat.y);
            double facing = down ? toChimney : toChimney + Math.PI;
            if (s < walk)
            {
                double k = walk <= 0 ? 1 : s / walk;
                var at = new Vec2(seat.x + (beside.x - seat.x) * k, seat.y + (beside.y - seat.y) * k);
                return (at, roof, 0, facing, WALK, true);
            }
            s -= walk;
            if (s < HOP)
            {
                // Up onto the chimney's top in an arc (or down off it, coming back).
                double k = s / HOP;
                var at = new Vec2(beside.x + (chimney.x - beside.x) * k, beside.y + (chimney.y - beside.y) * k);
                double lift = (rim - roof) * k + HOP_HEIGHT * Math.Sin(Math.PI * k);
                return (at, roof, lift, facing, 0, true);
            }
            s -= HOP;
            {
                // Down inside it (or up out of it, coming back): out of sight once in.
                double k = Math.Min(1, s / DROP);
                double lift = (rim - roof) - DOWN_INTO * k * k;
                bool shown = lift > rim - roof - 0.12;
                return (chimney, roof, lift, facing, 0, shown);
            }
        }
    }
}
