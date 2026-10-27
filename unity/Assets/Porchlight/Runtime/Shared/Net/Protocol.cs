// What the game client and the city room say to each other: a copy of
// server/src/multiplayer/protocol.ts. Change
// them together. Units are the town's: 1 unit = 1 tile; the ground is x (east)
// and z (south: the map's y); y is height off the ground.

using System;

namespace Porchlight.Net
{
    public static class Protocol
    {
        public const string CITY_ROOM = "city";

        public const string MSG_MOVE = "move";
        public const string MSG_TELEPORT = "teleport";
        public const string MSG_LOOK = "look";

        public const string MSG_SPAWN = "spawn";
        public const string MSG_CORRECT = "correct";
        public const string MSG_DOTS = "dots";

        public const int CLOSE_SIGNED_IN_ELSEWHERE = 4100;
        public const int CLOSE_KICKED = 4101;

        public const int ANIM_IDLE = 0;
        public const int ANIM_WALK = 1;
        public const int ANIM_RUN = 2;
        public const int ANIM_JUMP = 3;
        public const int ANIM_DRIVE = 4;
        public const int ANIM_RIDE = 5;

        public const int CAR_SEATS = 4;

        /// <summary>How often clients send moves (ms): about 30 a second.</summary>
        public const int SEND_MS = 33;

        public static int QuantizeYaw(double yaw) => (int)Math.Floor((yaw / (2 * Math.PI)) * 65536 + 0.5) & 0xffff;

        public static double YawFromWire(double q) => (q / 65536) * 2 * Math.PI;
    }
}
