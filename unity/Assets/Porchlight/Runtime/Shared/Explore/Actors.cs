// People: the player (keyboard- or pad-driven, or auto-walking) and townsfolk
// walking the sidewalk graph. Pure state (position, heading, speed); the view
// poses a rigged character to match each one. Also the solid rectangles they
// can't walk through, and the streets-only mask.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Explore
{
    public class Walker
    {
        /// <summary>Direction faced, radians: the 3D model's turn about the vertical (0 = facing +y).</summary>
        public double heading;
        /// <summary>How fast they moved over the last step (tiles per second).</summary>
        public double speed;
        /// <summary>Height of their feet off the ground (tiles; above 0 mid-jump).</summary>
        public double lift;
        /// <summary>Upward speed mid-jump (tiles per second; negative coming down).</summary>
        public double rise;
        public readonly string look;
        public double x;
        public double y;
        /// <summary>Size relative to an average townsperson.</summary>
        public readonly double scale;

        public Walker(string look, double x, double y, double scale = 1)
        {
            this.look = look;
            this.x = x;
            this.y = y;
            this.scale = scale;
        }

        /// <summary>Record a move of (dx, dy) tiles over dt seconds: face the way walked.</summary>
        public void Moved(double dx, double dy, double dt)
        {
            double d = Numbers.Hypot(dx, dy);
            if (d > 1e-6) heading = DetMath.Atan2(dx, dy);
            speed = dt > 0 ? d / dt : 0;
        }
    }

    public sealed class Player : Walker
    {
        const double PLAYER_SPEED = 3.4;
        const double SPRINT = 1.8;
        const double EDGE = 0.2;
        public const double PLAYER_RADIUS = 0.1;
        const double AUTO_SPEED = 1.8;
        const double ACCEL = 14;
        const double BRAKE = 22;
        const double AIR_ACCEL = 4;
        const double JUMP_SPEED = 1.5;
        const double RUN_JUMP = 0.06;
        const double GRAVITY = 5.5;
        const double FALL_GRAVITY = 8.5;
        const double RELEASE_GRAVITY = 14;
        const double JUMP_BUFFER = 0.15;

        SidewalkWalk auto;
        public bool sprinting;
        public bool jumpHeld;
        double vx, vy;
        double buffered;

        public Player(string look, double x, double y, double scale = 1) : base(look, x, y, scale) { }

        /// <summary>Jump if their feet are on the ground; in the air, jump again as soon as they land.</summary>
        public void Jump()
        {
            if (lift > 0)
            {
                buffered = JUMP_BUFFER;
                return;
            }
            rise = JUMP_SPEED + RUN_JUMP * Numbers.Hypot(vx, vy);
            lift = 1e-6;
            jumpHeld = true;
        }

        public void Halt()
        {
            vx = 0;
            vy = 0;
        }

        public bool IsAuto => auto != null;

        public void StartAuto(SidewalkNetwork net, Rng rng)
        {
            if (net.paths.Count == 0) return;
            Halt();
            auto = SidewalkWalk.Join(net, rng, new Vec2(x, y));
            auto.Roam();
        }

        public List<Vec2> AutoRoute() => auto?.RoutePoints() ?? new List<Vec2>();

        public Vec2? AutoGoal() => auto?.GoalPoint();

        public Vec2? AutoHeading() => auto?.Heading();

        public Itinerary AutoItinerary() => auto?.Itinerary();

        public void StopAuto()
        {
            auto = null;
            Halt();
        }

        /// <summary>While auto-walking, a direction is a request: away turns you round, towards the road crosses it.</summary>
        public void Command(Vec2 d)
        {
            if (auto == null) return;
            var h = auto.Heading();
            var road = auto.TowardRoad();
            if (d.x * h.x + d.y * h.y < -0.5) auto.TurnAround();
            else if (d.x * road.x + d.y * road.y > 0.5) auto.CrossStreet();
        }

        /// <summary>Walk in a world direction (or stand still), only onto open tiles and clear of solids, sliding along them.</summary>
        public void Update(Vec2? dir, double dt, TownModel model, byte[] open, Solids solids = null)
        {
            buffered = Math.Max(0, buffered - dt);
            if (lift > 0)
            {
                double g = rise < 0 ? FALL_GRAVITY : jumpHeld ? GRAVITY : RELEASE_GRAVITY;
                rise -= g * dt;
                lift += rise * dt;
                if (lift <= 0)
                {
                    lift = 0;
                    rise = 0;
                    if (buffered > 0)
                    {
                        buffered = 0;
                        Jump();
                    }
                }
            }
            if (auto != null)
            {
                var next = auto.Advance(AUTO_SPEED * dt);
                Moved(next.x - x, next.y - y, dt);
                x = next.x;
                y = next.y;
                return;
            }
            double top = PLAYER_SPEED * (sprinting ? SPRINT : 1);
            double tx = dir != null ? dir.Value.x * top : 0;
            double ty = dir != null ? dir.Value.y * top : 0;
            bool air = lift > 0;
            bool slowing = tx * vx + ty * vy < vx * vx + vy * vy;
            double rate = (air ? AIR_ACCEL : slowing ? BRAKE : ACCEL) * dt;
            double ex = tx - vx;
            double ey = ty - vy;
            double gap = Numbers.Hypot(ex, ey);
            if (gap <= rate)
            {
                vx = tx;
                vy = ty;
            }
            else
            {
                vx += (ex / gap) * rate;
                vy += (ey / gap) * rate;
            }
            double dx = 0, dy = 0;
            if (vx != 0 || vy != 0)
            {
                double ClampX(double v) => Math.Min(model.width - EDGE, Math.Max(EDGE, v));
                double ClampY(double v) => Math.Min(model.height - EDGE, Math.Max(EDGE, v));
                double sx = vx * dt;
                double sy = vy * dt;
                bool moved = false;
                foreach (var (mx, my) in new[] { (sx, sy), (sx, 0.0), (0.0, sy) })
                {
                    if (mx == 0 && my == 0) continue;
                    double nx = ClampX(x + mx);
                    double ny = ClampY(y + my);
                    if (CanStand(nx, ny, open, model) && (solids == null || !solids.Hits(nx, ny, PLAYER_RADIUS)))
                    {
                        dx = nx - x;
                        dy = ny - y;
                        if (mx == 0) vx = 0;
                        if (my == 0) vy = 0;
                        moved = true;
                        break;
                    }
                }
                if (!moved) Halt();
                x += dx;
                y += dy;
            }
            Moved(dx, dy, dt);
        }

        /// <summary>True if the player could stand at (x, y): on open tiles and clear of solids.</summary>
        public static bool Standable(double x, double y, byte[] open, TownModel model, Solids solids = null) =>
            CanStand(x, y, open, model) && (solids == null || !solids.Hits(x, y, PLAYER_RADIUS));

        static bool CanStand(double x, double y, byte[] open, TownModel model)
        {
            double r = PLAYER_RADIUS;
            foreach (var (ox, oy) in new[] { (0.0, 0.0), (r, 0.0), (-r, 0.0), (0.0, r), (0.0, -r) })
            {
                int tx = (int)Math.Floor(x + ox);
                int ty = (int)Math.Floor(y + oy);
                if (tx < 0 || ty < 0 || tx >= model.width || ty >= model.height || open[Grid.Index(tx, ty, model.width)] == 0) return false;
            }
            return true;
        }
    }

    /// <summary>A townsperson strolling the sidewalks (now and then crossing the road), or heading for them.</summary>
    public sealed class Pedestrian : Walker
    {
        SidewalkWalk walk;
        readonly Rng rng;
        double pause;
        readonly double pace;
        double going;

        Pedestrian(string look, SidewalkWalk walk, Vec2 at, Rng rng, double scale) : base(look, at.x, at.y, scale)
        {
            this.walk = walk;
            this.rng = rng;
        }

        Pedestrian(string look, SidewalkWalk walk, Vec2 at, Rng rng, double scale, double pace) : this(look, walk, at, rng, scale)
        {
            this.pace = pace;
        }

        public static Pedestrian Create(string look, SidewalkNetwork net, Rng rng, Vec2? from = null)
        {
            var walk = from != null ? SidewalkWalk.Join(net, rng, from.Value) : SidewalkWalk.Random(net, rng);
            var at = walk.Position();
            double scale = rng.Range(0.85, 1.05);
            double pace = rng.Range(0.9, 1.5);
            return new Pedestrian(look, walk, at, rng, scale, pace);
        }

        /// <summary>Carry on from where they are now (another client was walking them until now).</summary>
        public void Rejoin(SidewalkNetwork net)
        {
            if (net.paths.Count == 0) return;
            walk = SidewalkWalk.Join(net, rng, new Vec2(x, y));
            pause = 0;
            going = speed;
        }

        public void Update(double dt)
        {
            if (pause > 0) pause -= dt;
            else if (!walk.IsCrossing)
            {
                if (rng.Chance(dt * 0.01)) pause = rng.Range(1, 4);
                else if (rng.Chance(dt * 0.02)) walk.CrossStreet();
            }
            double want = pause > 0 ? 0 : pace;
            going += Numbers.Sign(want - going) * Math.Min(Math.Abs(want - going), 3 * dt);
            if (going < 1e-3)
            {
                Moved(0, 0, dt);
                return;
            }
            var next = walk.Advance(going * dt);
            Moved(next.x - x, next.y - y, dt);
            x = next.x;
            y = next.y;
        }
    }

    /// <summary>An axis-aligned rectangle on the map.</summary>
    public struct Rect
    {
        public double x0, y0, x1, y1;

        public Rect(double x0, double y0, double x1, double y1)
        {
            this.x0 = x0;
            this.y0 = y0;
            this.x1 = x1;
            this.y1 = y1;
        }
    }

    /// <summary>Solid rectangles the player can't walk through (walls, furniture), bucketed by tile.</summary>
    public sealed class Solids
    {
        readonly Dictionary<int, List<Rect>> buckets = new Dictionary<int, List<Rect>>();
        readonly int width;

        public Solids(IEnumerable<Rect> rects, int width)
        {
            this.width = width;
            foreach (var r in rects) Add(r);
        }

        public void Add(Rect r)
        {
            for (int y = (int)Math.Floor(r.y0); y <= (int)Math.Floor(r.y1); y++)
            {
                for (int x = (int)Math.Floor(r.x0); x <= (int)Math.Floor(r.x1); x++)
                {
                    int k = y * width + x;
                    if (buckets.TryGetValue(k, out var list)) list.Add(r);
                    else buckets[k] = new List<Rect> { r };
                }
            }
        }

        /// <summary>True if a circle of radius r at (x, y) overlaps any solid.</summary>
        public bool Hits(double x, double y, double r)
        {
            for (int ty = (int)Math.Floor(y - r); ty <= (int)Math.Floor(y + r); ty++)
            {
                for (int tx = (int)Math.Floor(x - r); tx <= (int)Math.Floor(x + r); tx++)
                {
                    if (!buckets.TryGetValue(ty * width + tx, out var list)) continue;
                    foreach (var b in list)
                    {
                        double dx = x - Math.Max(b.x0, Math.Min(x, b.x1));
                        double dy = y - Math.Max(b.y0, Math.Min(y, b.y1));
                        if (dx * dx + dy * dy < r * r) return true;
                    }
                }
            }
            return false;
        }
    }

    public static class Streets
    {
        /// <summary>Tiles you can stand on in "streets only" walking: roads, bridges, paths and the plaza.</summary>
        public static byte[] Mask(TileGrid tiles)
        {
            var o = new byte[tiles.width * tiles.height];
            for (int i = 0; i < o.Length; i++)
            {
                byte ov = tiles.overlay[i];
                bool street = ov == Overlay.ROAD_MAIN || ov == Overlay.ROAD_STREET || ov == Overlay.BRIDGE || ov == Overlay.PATH || ov == Overlay.PLAZA;
                o[i] = street && tiles.buildingId[i] < 0 ? (byte)1 : (byte)0;
            }
            return o;
        }
    }
}
