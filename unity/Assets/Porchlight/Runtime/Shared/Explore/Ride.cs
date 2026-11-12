// Riding in someone else's car: where you sit, from where you last saw it. Pure,
// so it's checked without an engine (Tools~/Checks RideCheck.cs).
//
// You sit where you see the car. Driving from one room of the county into the
// next, its driver leaves the room you're in a moment before you do (they're
// ahead of you: you see them a little in the past), so for a while you can't see
// it at all: you roll on the way it was going, at the speed it was going, which
// carries you over into the next room too, where you see it again. A web build
// can take a while getting there (joining the next room; a town ahead still
// being built, which you wait at the edge of), and a slow frame can be most of a
// second: none of that is the car gone. Only a car out of sight while you're
// settled in a room is: then you get out.

using System;
using Porchlight.Core;

namespace Porchlight.Explore
{
    public sealed class RideFollow
    {
        /// <summary>
        /// Faster than anything goes (tiles a second): a boosted car (Vehicle.TOP_SPEED × CAR_BOOST, 17.7) and a
        /// margin. A move between sightings faster than this is the view shifting (into the next space), not the car.
        /// </summary>
        public const double FASTEST = Vehicle.TOP_SPEED * Vehicle.CAR_BOOST * 1.4;
        /// <summary>How long (ms) the car can be out of sight while you're settled in a room before you're out of it.</summary>
        public const double LOST_AFTER = 2000;
        /// <summary>...and however long between rooms or waiting at a space being built, out of sight this long (ms) in all, you're out.</summary>
        public const double LOST_AT_MOST = 15000;
        /// <summary>How long (ms) you roll on after the car's last sighting: far enough to carry you over into the next room, then you wait there.</summary>
        public const double ROLL_ON = 3000;

        Vec2 seen, velocity;
        double seenAt = double.NaN;
        double? lostAt;
        /// <summary>Out of sight while settled in a room (ms), and when that was last counted.</summary>
        double lostSettled, countedAt;

        /// <summary>The car's out of sight just now (you're rolling on from where you saw it).</summary>
        public bool Lost => lostAt != null;

        /// <summary>The way you're rolling (tiles a second), from the car's last sightings.</summary>
        public Vec2 Velocity => velocity;

        /// <summary>Getting in, or out: forget the car.</summary>
        public void Reset()
        {
            seenAt = double.NaN;
            velocity = new Vec2(0, 0);
            lostAt = null;
            lostSettled = 0;
        }

        /// <summary>The car seen at `at` (now, ms): where you sit (there).</summary>
        public Vec2 Seen(Vec2 at, double now)
        {
            lostAt = null;
            lostSettled = 0;
            if (double.IsNaN(seenAt))
            {
                seen = at;
                seenAt = now;
                velocity = new Vec2(0, 0);
                return at;
            }
            double since = (now - seenAt) / 1000;
            if (since > 0.02)
            {
                var v = new Vec2((at.x - seen.x) / since, (at.y - seen.y) / since);
                // A jump faster than anything goes is the view shifting under it: keep the way it was going.
                if (Math.Sqrt(v.x * v.x + v.y * v.y) <= FASTEST) velocity = v;
                seen = at;
                seenAt = now;
            }
            return at;
        }

        /// <summary>
        /// The car out of sight (now, ms): where you sit, rolling on from where you last saw it; null once it's
        /// been gone too long (get out). `settled`: you're in a room and not waiting for the space ahead to be
        /// built, so the car should be in sight; only then does the time run out (bar LOST_AT_MOST).
        /// </summary>
        public Vec2? Unseen(double now, bool settled)
        {
            if (double.IsNaN(seenAt)) return null;
            if (lostAt == null)
            {
                lostAt = now;
                countedAt = now;
            }
            if (settled) lostSettled += Math.Max(0, now - countedAt);
            countedAt = now;
            if (lostSettled > LOST_AFTER || now - lostAt.Value > LOST_AT_MOST) return null;
            double rolled = Math.Min(Math.Max(0, now - seenAt), ROLL_ON) / 1000;
            return new Vec2(seen.x + velocity.x * rolled, seen.y + velocity.y * rolled);
        }

        /// <summary>The view's moved into the next space (everything in it by -dx, -dy): so has where you last saw the car.</summary>
        public void Shift(double dx, double dy)
        {
            if (double.IsNaN(seenAt)) return;
            seen = new Vec2(seen.x - dx, seen.y - dy);
        }
    }
}
