namespace Porchlight.Shared.Geometry
{
    public static class Segments
    {
        public static Vec2 ClosestPoint(Vec2 point, Vec2 a, Vec2 b)
        {
            var ab = b - a;
            var lengthSquared = ab.LengthSquared;
            if (lengthSquared < 1e-8f)
            {
                return a;
            }

            var t = Vec2.Dot(point - a, ab) / lengthSquared;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return a + ab * t;
        }

        public static float Distance(Vec2 point, Vec2 a, Vec2 b) => Vec2.Distance(point, ClosestPoint(point, a, b));
    }
}
