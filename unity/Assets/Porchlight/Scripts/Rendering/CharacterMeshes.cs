using System.Collections.Generic;
using Porchlight.Shared.Content;
using UnityEngine;

namespace Porchlight.Rendering
{
    /// <summary>
    /// Low-poly kids built in code from a <see cref="CharacterType"/>, in the same flat vertex-colour
    /// style as the town. Each character is three meshes so the limbs can swing: the body (torso,
    /// head, hair, face), one arm and one leg, each used for both sides. Limb meshes hang down from
    /// their pivot at the origin. Everything is modelled at <see cref="ModelHeight"/> and scaled to the
    /// character's height. The face points along local +Z.
    /// </summary>
    public static class CharacterMeshes
    {
        public const float ModelHeight = 1.25f;
        public const float HipHeight = 0.48f;
        public const float ShoulderHeight = 0.82f;
        public const float HipOffset = 0.075f;
        public const float ShoulderOffset = 0.175f;

        private const float WaistTop = 0.56f;
        private const float ShortsBottom = 0.35f;
        private static readonly Vector3 HeadCenter = new Vector3(0f, 1.02f, 0f);
        private static readonly Vector3 HeadRadii = new Vector3(0.2f, 0.19f, 0.18f);

        private static readonly Color Eye = LowPolyMeshes.Hex("#2E2A2A");
        private static readonly Color Mouth = LowPolyMeshes.Hex("#9C3F45");
        private static readonly Color Teeth = LowPolyMeshes.Hex("#FFFDF7");
        private static readonly Color Cheek = LowPolyMeshes.Hex("#F2A0A0");
        private static readonly Color FlowerYellow = LowPolyMeshes.Hex("#F2C94C");

        public sealed class Set
        {
            public Mesh Body;
            public Mesh Arm;
            public Mesh Leg;
        }

        private static readonly Dictionary<string, Set> Cache = new Dictionary<string, Set>();

        public static Set Get(CharacterType type)
        {
            if (Cache.TryGetValue(type.Id, out var set) && set.Body != null) return set;
            set = new Set { Body = Body(type), Arm = Arm(type), Leg = Leg(type) };
            Cache[type.Id] = set;
            return set;
        }

        private static readonly Vector3 SoftLight = new Vector3(0f, 0.8f, -0.3f);

        private static Mesh Body(CharacterType type)
        {
            var mb = new MeshBuilder { NormalBias = SoftLight };
            var skin = LowPolyMeshes.Hex(type.SkinColor);
            var shirt = LowPolyMeshes.Hex(type.ShirtColor);

            // Shorts around the hips, in bands from the waistband down.
            ForEachStripe(type, 0.44f, WaistTop, (bottom, top, color) =>
                mb.Box(new Vector3(0f, bottom, 0f), 0.3f, top - bottom, 0.2f, color, color));

            // Shirt, neck and head.
            mb.Box(new Vector3(0f, WaistTop - 0.02f, 0f), 0.28f, ShoulderHeight + 0.04f - WaistTop, 0.18f, shirt, shirt);
            DrawShirtPattern(mb, type);
            mb.Prism(new Vector3(0f, ShoulderHeight + 0.01f, 0f), 0.045f, 0.045f, 0.1f, skin);
            mb.Ball(HeadCenter, HeadRadii, skin, 10, 7);
            mb.Ball(HeadCenter + new Vector3(HeadRadii.x, -0.02f, -0.01f), Vector3.one * 0.035f, skin, 5, 3);
            mb.Ball(HeadCenter + new Vector3(-HeadRadii.x, -0.02f, -0.01f), Vector3.one * 0.035f, skin, 5, 3);
            Face(mb, type);

            if (type.Hair == HairStyle.CurlyTopKnot) CurlyTopKnot(mb, type);
            else ShortSprout(mb, type);

            return mb.ToMesh(name: type.Id + " body");
        }

        private static Mesh Arm(CharacterType type)
        {
            var mb = new MeshBuilder { NormalBias = SoftLight };
            var skin = LowPolyMeshes.Hex(type.SkinColor);
            const float length = 0.36f;
            mb.Prism(new Vector3(0f, -length, 0f), 0.038f, 0.045f, length, skin);
            mb.Ball(new Vector3(0f, -length - 0.02f, 0f), Vector3.one * 0.045f, skin, 6, 4);

            if (type.Pattern == ShirtPattern.GiraffePanel)
            {
                // Cap sleeve.
                var shirt = LowPolyMeshes.Hex(type.ShirtColor);
                mb.Prism(new Vector3(0f, -0.11f, 0f), 0.058f, 0.062f, 0.13f, shirt);
            }

            return mb.ToMesh(name: type.Id + " arm");
        }

        private static Mesh Leg(CharacterType type)
        {
            var mb = new MeshBuilder { NormalBias = SoftLight };
            var skin = LowPolyMeshes.Hex(type.SkinColor);
            var length = HipHeight - 0.05f;
            mb.Prism(new Vector3(0f, -length, 0f), 0.048f, 0.06f, length, skin);

            ForEachStripe(type, ShortsBottom, HipHeight + 0.02f, (bottom, top, color) =>
                mb.Prism(new Vector3(0f, bottom - HipHeight, 0f), 0.078f, 0.078f, top - bottom, color));

            // Shoes, or bare feet.
            var foot = type.ShoeColor != null ? LowPolyMeshes.Hex(type.ShoeColor) : skin;
            var size = type.ShoeColor != null ? new Vector3(0.11f, 0.075f, 0.19f) : new Vector3(0.09f, 0.055f, 0.16f);
            mb.Box(new Vector3(0f, -HipHeight, 0.03f), size.x, size.y, size.z, foot, foot);
            if (type.ShoeColor != null)
            {
                mb.Box(new Vector3(0f, -HipHeight, 0.03f), size.x + 0.01f, 0.025f, size.z + 0.01f, Teeth, Teeth);
            }

            return mb.ToMesh(name: type.Id + " leg");
        }

        /// <summary>Calls back once per shorts stripe, clipped to the range from bottom to top.</summary>
        private static void ForEachStripe(CharacterType type, float clipBottom, float clipTop, System.Action<float, float, Color> draw)
        {
            var count = type.ShortsStripes.Count;
            var band = (WaistTop - ShortsBottom) / count;
            for (var i = 0; i < count; i++)
            {
                var top = Mathf.Min(WaistTop - i * band, clipTop);
                var bottom = Mathf.Max(WaistTop - (i + 1) * band, clipBottom);
                if (top - bottom > 0.001f) draw(bottom, top, LowPolyMeshes.Hex(type.ShortsStripes[i]));
            }
        }

        private static void DrawShirtPattern(MeshBuilder mb, CharacterType type)
        {
            var color = LowPolyMeshes.Hex(type.ShirtPatternColor);
            const float front = 0.091f;

            if (type.Pattern == ShirtPattern.GiraffePanel)
            {
                // A darker panel down the front, a little off centre like the real shirt, with the
                // paler lines of a giraffe print across it.
                const float panelX = 0.03f, panelW = 0.1f;
                var bottom = WaistTop + 0.01f;
                var top = ShoulderHeight + 0.02f;
                FrontQuad(mb, panelX, (bottom + top) * 0.5f, panelW, top - bottom, front, color);
                var net = Color.Lerp(color, LowPolyMeshes.Hex(type.ShirtColor), 0.6f);
                for (var row = 0; row < 4; row++)
                {
                    var y = bottom + 0.035f + row * 0.065f;
                    FrontQuad(mb, panelX + (row % 2 == 0 ? -0.012f : 0.012f), y, panelW, 0.009f, front + 0.002f, net);
                    FrontQuad(mb, panelX + (row % 2 == 0 ? 0.015f : -0.018f), y + 0.032f, 0.009f, 0.065f, front + 0.002f, net);
                }

                return;
            }

            // Tiny flowers, front and back.
            for (var row = 0; row < 5; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var x = -0.1f + col * 0.065f + (row % 2) * 0.03f;
                    if (x > 0.12f) continue;
                    var y = WaistTop + 0.03f + row * 0.055f;
                    var c = (row + col) % 3 == 0 ? FlowerYellow : color;
                    FrontQuad(mb, x, y, 0.018f, 0.018f, front, c);
                    FrontQuad(mb, x, y, 0.018f, 0.018f, -front, c);
                }
            }
        }

        /// <summary>A flat decal on the front (z &gt; 0) or back (z &lt; 0) of the body.</summary>
        private static void FrontQuad(MeshBuilder mb, float x, float y, float width, float height, float z, Color color)
        {
            float x0 = x - width * 0.5f, x1 = x + width * 0.5f, y0 = y - height * 0.5f, y1 = y + height * 0.5f;
            mb.Quad(new Vector3(x0, y0, z), new Vector3(x0, y1, z), new Vector3(x1, y1, z), new Vector3(x1, y0, z), color, new Vector3(0f, 0f, Mathf.Sign(z)));
        }

        /// <summary>A decal hugging the front of the head at (x, y).</summary>
        private static void FaceQuad(MeshBuilder mb, float x, float y, float width, float height, Color color, float lift = 0.006f)
        {
            FrontQuad(mb, x, y, width, height, FaceZ(x, y) + lift, color);
        }

        /// <summary>How far forward the head's surface is at (x, y).</summary>
        private static float FaceZ(float x, float y)
        {
            var nx = x / HeadRadii.x;
            var ny = (y - HeadCenter.y) / HeadRadii.y;
            return HeadCenter.z + HeadRadii.z * Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
        }

        private static void Face(MeshBuilder mb, CharacterType type)
        {
            const float eyeX = 0.065f;
            var eyeY = HeadCenter.y + 0.01f;
            FaceQuad(mb, -eyeX, eyeY, 0.03f, 0.042f, Eye);
            FaceQuad(mb, eyeX, eyeY, 0.03f, 0.042f, Eye);
            FaceQuad(mb, -eyeX + 0.007f, eyeY + 0.01f, 0.01f, 0.01f, Teeth, 0.009f);
            FaceQuad(mb, eyeX + 0.007f, eyeY + 0.01f, 0.01f, 0.01f, Teeth, 0.009f);
            FaceQuad(mb, -0.11f, HeadCenter.y - 0.045f, 0.04f, 0.022f, Cheek);
            FaceQuad(mb, 0.11f, HeadCenter.y - 0.045f, 0.04f, 0.022f, Cheek);

            // A big open smile.
            var mouthY = HeadCenter.y - 0.075f;
            FaceQuad(mb, 0f, mouthY, 0.085f, 0.028f, Mouth);
            FaceQuad(mb, -0.05f, mouthY + 0.012f, 0.02f, 0.016f, Mouth);
            FaceQuad(mb, 0.05f, mouthY + 0.012f, 0.02f, 0.016f, Mouth);
            FaceQuad(mb, 0f, mouthY + 0.006f, 0.07f, 0.012f, Teeth, 0.009f);

            if (type.GlassesColor == null) return;

            // Rectangular frames with a bridge, and arms back to the ears.
            var frame = LowPolyMeshes.Hex(type.GlassesColor);
            const float lensW = 0.085f, lensH = 0.062f, rim = 0.012f, lift = 0.022f;
            foreach (var side in new[] { -1f, 1f })
            {
                var cx = side * eyeX;
                FaceQuad(mb, cx, eyeY + lensH * 0.5f, lensW + rim, rim, frame, lift);
                FaceQuad(mb, cx, eyeY - lensH * 0.5f, lensW + rim, rim, frame, lift);
                FaceQuad(mb, cx - lensW * 0.5f, eyeY, rim, lensH, frame, lift);
                FaceQuad(mb, cx + lensW * 0.5f, eyeY, rim, lensH, frame, lift);

                // Arm from the outer rim back to the ear.
                var outer = cx + side * lensW * 0.5f;
                var ay = eyeY + lensH * 0.5f - 0.01f;
                var az = FaceZ(outer, ay) + lift;
                mb.Quad(new Vector3(outer, ay - 0.006f, az), new Vector3(outer, ay + 0.006f, az),
                    new Vector3(side * 0.205f, ay + 0.006f, -0.02f), new Vector3(side * 0.205f, ay - 0.006f, -0.02f), frame, new Vector3(side, 0f, 0.3f));
            }

            FaceQuad(mb, 0f, eyeY + 0.012f, eyeX * 2f - lensW, rim, frame, lift);
        }

        private static void CurlyTopKnot(MeshBuilder mb, CharacterType type)
        {
            var hair = LowPolyMeshes.Hex(type.HairColor);
            var light = Color.Lerp(hair, Color.white, 0.18f);

            // A cap over the back and top, swept back from the face into the knot.
            mb.Ball(HeadCenter + new Vector3(0f, 0.015f, -0.012f), HeadRadii * 1.1f, hair, 10, 7,
                d => !(d.z > 0.3f && d.y < 0.5f) && (d.y > -0.55f || d.z < 0f));

            // Shoulder-length curls, round the sides and back.
            for (var level = 0; level < 4; level++)
            {
                var y = HeadCenter.y - 0.02f - level * 0.065f;
                var spread = 0.2f + level * 0.012f;
                for (var i = 0; i <= 8; i++)
                {
                    var heading = Mathf.Lerp(78f, 282f, i / 8f) + (level % 2) * 9f;
                    var dir = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
                    var size = 0.06f + 0.012f * ((i + level) % 2);
                    mb.Ball(new Vector3(0f, y, -0.04f) + dir * spread, Vector3.one * size, (i + level) % 3 == 0 ? light : hair, 6, 4);
                }
            }

            // The top knot and its scrunchie, with a couple of loose curls.
            var knot = HeadCenter + new Vector3(0f, 0.25f, -0.03f);
            mb.Ball(knot, new Vector3(0.075f, 0.065f, 0.075f), hair, 7, 5);
            mb.Ball(knot + new Vector3(0.05f, 0.04f, 0.01f), Vector3.one * 0.04f, light, 5, 4);
            mb.Ball(knot + new Vector3(-0.045f, 0.035f, -0.02f), Vector3.one * 0.035f, hair, 5, 4);
            mb.Prism(knot + Vector3.down * 0.075f, 0.045f, 0.045f, 0.025f, LowPolyMeshes.Hex(type.HairTieColor), 7);
        }

        private static void ShortSprout(MeshBuilder mb, CharacterType type)
        {
            var hair = LowPolyMeshes.Hex(type.HairColor);
            var light = Color.Lerp(hair, Color.white, 0.2f);

            // A short, soft cap that stops around the ears, open for the face.
            mb.Ball(HeadCenter + new Vector3(0f, 0.02f, -0.01f), HeadRadii * 1.08f, hair, 10, 7,
                d => !(d.z > 0.25f && d.y < 0.55f) && d.y > -0.3f);

            // Fine, straight hair falling to the nape, and down past the ears at the sides.
            mb.Ball(HeadCenter + new Vector3(0f, -0.07f, -0.05f), new Vector3(0.2f, 0.15f, 0.16f), hair, 10, 6, d => d.z < 0.1f);
            foreach (var side in new[] { -1f, 1f })
            {
                mb.Ball(HeadCenter + new Vector3(side * 0.18f, -0.06f, -0.01f), new Vector3(0.045f, 0.11f, 0.1f), hair, 6, 5);
            }

            // The sprout on top: a little fountain of hair over a yellow tie.
            var top = HeadCenter + new Vector3(0f, 0.22f, 0.02f);
            mb.Prism(top + Vector3.down * 0.01f, 0.03f, 0.03f, 0.03f, LowPolyMeshes.Hex(type.HairTieColor), 6);
            mb.Ball(top + new Vector3(0f, 0.065f, 0f), new Vector3(0.028f, 0.055f, 0.028f), light, 5, 4);
            mb.Ball(top + new Vector3(0.025f, 0.055f, -0.01f), new Vector3(0.02f, 0.045f, 0.02f), hair, 5, 3);
            mb.Ball(top + new Vector3(-0.025f, 0.05f, 0.01f), new Vector3(0.02f, 0.04f, 0.02f), hair, 5, 3);
        }
    }
}
