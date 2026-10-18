using Porchlight.App;
using UnityEngine;

namespace Porchlight.Input
{
    /// <summary>
    /// Tells a left click from a left drag, so the builder can place things on a click and the
    /// camera can pan on a drag. A click happens on release, only if the pointer barely moved.
    /// Presses that start on the bottom bar are ignored; the bar handles those itself.
    /// Read it every frame while it matters (the builder and the build camera both do).
    /// </summary>
    public static class PointerGesture
    {
        /// <summary>How far the pointer must move, in pixels, before a press counts as a drag.</summary>
        public const float DragThreshold = 6f;

        private static int _frame = -1;
        private static bool _down;
        private static bool _ignored;
        private static bool _dragging;
        private static bool _dragStarted;
        private static bool _clicked;
        private static Vector2 _start;

        /// <summary>The left button is held and has moved far enough to be a drag.</summary>
        public static bool Dragging
        {
            get
            {
                Refresh();
                return _dragging;
            }
        }

        /// <summary>This frame is the one where a press turned into a drag.</summary>
        public static bool DragStarted
        {
            get
            {
                Refresh();
                return _dragStarted;
            }
        }

        /// <summary>The left button was released this frame without dragging.</summary>
        public static bool Clicked
        {
            get
            {
                Refresh();
                return _clicked;
            }
        }

        /// <summary>Where the current or last press began, in screen pixels.</summary>
        public static Vector2 PressPosition
        {
            get
            {
                Refresh();
                return _start;
            }
        }

        private static void Refresh()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _clicked = false;
            _dragStarted = false;

            var pointer = PInput.PointerPosition();
            if (PInput.PrimaryPressedThisFrame())
            {
                _down = true;
                _dragging = false;
                _start = pointer;
                _ignored = HudBar.PointerInBar();
            }

            if (_down && !_dragging && !_ignored && (pointer - _start).sqrMagnitude > DragThreshold * DragThreshold)
            {
                _dragging = true;
                _dragStarted = true;
            }

            if (_down && (PInput.PrimaryReleasedThisFrame() || !PInput.PrimaryHeld()))
            {
                _clicked = !_dragging && !_ignored;
                _down = false;
                _dragging = false;
            }
        }
    }
}
