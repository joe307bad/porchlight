using Porchlight.Input;
using UnityEngine;

namespace Porchlight.App
{
    /// <summary>
    /// Helpers for the prototype's IMGUI screens. IMGUI is code-only, so the scaffold needs no authored
    /// assets. Replace it with UI Toolkit before shipping: IMGUI allocates every frame and has no gamepad
    /// navigation. Everything is laid out at 720p and scaled up for larger screens.
    /// </summary>
    public static class Ui
    {
        public static GUIStyle Title;
        public static GUIStyle Hint;
        public static GUIStyle Small;
        public static GUIStyle Wrap;
        public static GUIStyle ListButton;

        public static float Scale => Mathf.Clamp(Screen.height / 720f, 1f, 3f);
        public static float Width => Screen.width / Scale;
        public static float Height => Screen.height / Scale;

        /// <summary>Call at the top of every OnGUI.</summary>
        public static void Begin()
        {
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
            if (Title != null) return;

            Title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            Hint = new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };
            Small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            Wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
            ListButton = new GUIStyle(GUI.skin.button) { richText = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 6, 6) };
        }

        /// <summary>Pointer position in the same scaled, top-left-origin space as IMGUI rects.</summary>
        public static Vector2 PointerLogical()
        {
            var p = PInput.PointerPosition();
            return new Vector2(p.x / Scale, (Screen.height - p.y) / Scale);
        }

        public static bool PointerIn(Rect logicalRect) => logicalRect.Contains(PointerLogical());
    }
}
