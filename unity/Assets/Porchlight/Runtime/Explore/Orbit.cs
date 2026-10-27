// The explore camera's geometry. The camera looks down on the player from one
// of 12 fixed headings around them, 30° apart; tilt stops; screen to world
// directions; the sight-line box test; and Follow, which tracks a moving
// target without lag but eases over jumps. Pure. A port of render/iso/orbit.ts.
// World space: x right, y down the map; yaw is measured from +y toward +x.

using System;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public struct Offset3
    {
        public double x, up, y;
    }

    /// <summary>A box standing on the ground: x0..x1, y0..y1 on the map, rising to `top`.</summary>
    public struct Box
    {
        public double x0, y0, x1, y1, top;
    }

    public static class Orbit
    {
        public const int CAMERA_STOPS = 12;
        static readonly double STEP = (Math.PI * 2) / CAMERA_STOPS;
        static readonly double OFFSET = Math.PI / 12;
        public static readonly double PITCH = Math.PI / 6;
        public const int DEFAULT_STOP = 1;

        public static readonly double[] ISO_TILTS = { 10, 20, 30, 45, 60, 80 };
        public static readonly double[] CHASE_TILTS = { 2, 10, 17, 28, 40, 55, 70 };
        public const int DEFAULT_ISO_TILT = 2;
        public const int DEFAULT_CHASE_TILT = 2;

        public static double TiltPitch(double[] tilts, int k) => (tilts[ClampTilt(tilts, k)] * Math.PI) / 180;

        public static int ClampTilt(double[] tilts, int k) => Math.Min(tilts.Length - 1, Math.Max(0, k));

        public static double StopYaw(int k) => OFFSET + Mod(k, CAMERA_STOPS) * STEP;

        public static int NearestStop(double yaw) => (int)Mod(Js.Round((yaw - OFFSET) / STEP), CAMERA_STOPS);

        /// <summary>The signed turn (radians, in -π..π) that takes `from` to `to` the short way round.</summary>
        public static double ShortestTurn(double from, double to)
        {
            double d = Mod(to - from, Math.PI * 2);
            return d > Math.PI ? d - Math.PI * 2 : d;
        }

        public static Offset3 CameraOffset(double yaw, double pitch) =>
            new Offset3 { x = Math.Sin(yaw) * Math.Cos(pitch), up = Math.Sin(pitch), y = Math.Cos(yaw) * Math.Cos(pitch) };

        public static void ScreenAxes(double yaw, out Vec2 right, out Vec2 up)
        {
            right = new Vec2(Math.Cos(yaw), -Math.Sin(yaw));
            up = new Vec2(-Math.Sin(yaw), -Math.Cos(yaw));
        }

        /// <summary>A screen-space direction (right, down) as a unit world direction on the ground, or null.</summary>
        public static Vec2? ScreenToWorld(Vec2 input, double yaw)
        {
            double len = Js.Hypot(input.x, input.y);
            if (len < 1e-9) return null;
            ScreenAxes(yaw, out var right, out var up);
            double ix = input.x / len, iy = input.y / len;
            return new Vec2(right.x * ix - up.x * iy, right.y * ix - up.y * iy);
        }

        public static int StopLookingAlong(Vec2 dir) => NearestStop(JsMath.Atan2(-dir.x, -dir.y));

        public static int StopBehind(int current, Vec2 dir, double margin = Math.PI / 36)
        {
            if (Js.Hypot(dir.x, dir.y) < 1e-6) return current;
            double ideal = JsMath.Atan2(-dir.x, -dir.y);
            double off = Math.Abs(ShortestTurn(StopYaw(current), ideal));
            return off > STEP / 2 + margin ? NearestStop(ideal) : current;
        }

        /// <summary>How far along the line from a point toward the camera it first enters the box, or Infinity.</summary>
        public static double RayBoxDistance(Vec2 p, double h, Offset3 o, Box b)
        {
            double near = 0, far = double.PositiveInfinity;
            bool Slab(double origin, double d, double lo, double hi)
            {
                if (Math.Abs(d) < 1e-12) return origin >= lo && origin <= hi;
                double t0 = (lo - origin) / d;
                double t1 = (hi - origin) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                near = Math.Max(near, t0);
                far = Math.Min(far, t1);
                return near <= far;
            }
            return Slab(p.x, o.x, b.x0, b.x1) && Slab(p.y, o.y, b.y0, b.y1) && Slab(h, o.up, 0, b.top) ? near : double.PositiveInfinity;
        }

        public static bool RayHitsBox(Vec2 p, double h, Offset3 o, Box b) => RayBoxDistance(p, h, o, b) < double.PositiveInfinity;

        public static double ChaseYaw(double heading) => heading + Math.PI;

        public static double EaseBy(double rate, double dt) => 1 - Math.Exp(-rate * dt);

        static double Mod(double a, double n) => ((a % n) + n) % n;
    }

    /// <summary>
    /// A camera point that follows a moving target with no lag while it moves
    /// smoothly, and eases over a jump instead of cutting.
    /// </summary>
    public sealed class Follow
    {
        public double ax, ay, az;
        double lx, ly, lz;
        double gx, gy, gz;
        bool started;
        readonly double rate;
        readonly double maxSpeed;

        public Follow(double rate, double maxSpeed = 15)
        {
            this.rate = rate;
            this.maxSpeed = maxSpeed;
        }

        public void Update(double tx, double ty, double tz, double dt, bool snap = false)
        {
            if (snap || !started)
            {
                gx = gy = gz = 0;
                started = true;
            }
            else
            {
                double dx = tx - lx, dy = ty - ly, dz = tz - lz;
                if (Js.Hypot(dx, dy, dz) > maxSpeed * Math.Max(dt, 1.0 / 240) + 0.02)
                {
                    gx -= dx;
                    gy -= dy;
                    gz -= dz;
                }
                double k = Math.Exp(-rate * dt);
                gx *= k;
                gy *= k;
                gz *= k;
            }
            lx = tx;
            ly = ty;
            lz = tz;
            ax = tx + gx;
            ay = ty + gy;
            az = tz + gz;
        }
    }
}
