using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Porchlight.Input
{
    public enum PKey
    {
        Q,
        E,
        Escape,
        Alpha1,
        Alpha2,
        Alpha3,
        Alpha4,
        Tab,
        R,
        Backspace,
        F,
    }

    /// <summary>
    /// One place for all input. Uses the Input System package when enabled (needed for Switch
    /// controllers), otherwise the legacy Input Manager, so the project runs whichever "Active Input
    /// Handling" setting is chosen. Gamepad bindings are already here for the Switch build.
    /// </summary>
    public static class PInput
    {
        /// <summary>Horizontal movement: x = strafe, y = forward. Arrow keys or the left stick.</summary>
        public static Vector2 Move()
        {
            var v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.upArrowKey.isPressed) v.y += 1f;
                if (k.downArrowKey.isPressed) v.y -= 1f;
                if (k.rightArrowKey.isPressed) v.x += 1f;
                if (k.leftArrowKey.isPressed) v.x -= 1f;
            }

            var g = Gamepad.current;
            if (g != null) v += g.leftStick.ReadValue();
#else
            if (UnityEngine.Input.GetKey(KeyCode.UpArrow)) v.y += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.DownArrow)) v.y -= 1f;
            if (UnityEngine.Input.GetKey(KeyCode.RightArrow)) v.x += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.LeftArrow)) v.x -= 1f;
#endif
            return Vector2.ClampMagnitude(v, 1f);
        }

        /// <summary>Altitude: Space/PageUp up and C/PageDown down; right/left trigger on a gamepad.</summary>
        public static float Vertical()
        {
            var v = 0f;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.spaceKey.isPressed || k.pageUpKey.isPressed) v += 1f;
                if (k.cKey.isPressed || k.pageDownKey.isPressed) v -= 1f;
            }

            var g = Gamepad.current;
            if (g != null) v += g.rightTrigger.ReadValue() - g.leftTrigger.ReadValue();
#else
            if (UnityEngine.Input.GetKey(KeyCode.Space) || UnityEngine.Input.GetKey(KeyCode.PageUp)) v += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.C) || UnityEngine.Input.GetKey(KeyCode.PageDown)) v -= 1f;
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>Mouse look while the right button is held, in pixels this frame.</summary>
        public static Vector2 MouseLookDelta()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.rightButton.isPressed ? m.delta.ReadValue() : Vector2.zero;
#else
            if (!UnityEngine.Input.GetMouseButton(1)) return Vector2.zero;
            // Legacy axes are pixel deltas scaled by 0.1 in the default Input Manager.
            return new Vector2(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y")) * 10f;
#endif
        }

        /// <summary>
        /// Keyboard look, -1..1 per axis: A/D turn left/right (x), W/S look up/down (y).
        /// The left hand looks while the right hand moves with the arrow keys.
        /// </summary>
        public static Vector2 KeyLook()
        {
            var v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return v;
            if (k.dKey.isPressed) v.x += 1f;
            if (k.aKey.isPressed) v.x -= 1f;
            if (k.wKey.isPressed) v.y += 1f;
            if (k.sKey.isPressed) v.y -= 1f;
#else
            if (UnityEngine.Input.GetKey(KeyCode.D)) v.x += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.A)) v.x -= 1f;
            if (UnityEngine.Input.GetKey(KeyCode.W)) v.y += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.S)) v.y -= 1f;
#endif
            return v;
        }

        /// <summary>Gamepad right stick, -1..1 per axis.</summary>
        public static Vector2 StickLook()
        {
#if ENABLE_INPUT_SYSTEM
            var g = Gamepad.current;
            return g != null ? g.rightStick.ReadValue() : Vector2.zero;
#else
            return Vector2.zero;
#endif
        }

        public static bool Fast()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            var g = Gamepad.current;
            return (k != null && (k.leftShiftKey.isPressed || k.rightShiftKey.isPressed))
                || (g != null && g.leftStickButton.isPressed);
#else
            return UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
#endif
        }

        /// <summary>Pointer position in screen pixels (origin bottom-left).</summary>
        public static Vector2 PointerPosition()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null ? m.position.ReadValue() : Vector2.zero;
#else
            return UnityEngine.Input.mousePosition;
#endif
        }

        public static bool PrimaryPressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.leftButton.wasPressedThisFrame;
#else
            return UnityEngine.Input.GetMouseButtonDown(0);
#endif
        }

        public static bool PrimaryHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.leftButton.isPressed;
#else
            return UnityEngine.Input.GetMouseButton(0);
#endif
        }

        public static bool PrimaryReleasedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.leftButton.wasReleasedThisFrame;
#else
            return UnityEngine.Input.GetMouseButtonUp(0);
#endif
        }

        /// <summary>Either Ctrl key.</summary>
        public static bool Control()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && (k.leftCtrlKey.isPressed || k.rightCtrlKey.isPressed);
#else
            return UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
#endif
        }

        /// <summary>The right button. On a Mac, browsers report Ctrl+click as this too.</summary>
        public static bool SecondaryHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.rightButton.isPressed;
#else
            return UnityEngine.Input.GetMouseButton(1);
#endif
        }

        /// <summary>How far the pointer moved this frame, in pixels.</summary>
        public static Vector2 PointerDelta()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null ? m.delta.ReadValue() : Vector2.zero;
#else
            return new Vector2(UnityEngine.Input.GetAxis("Mouse X"), UnityEngine.Input.GetAxis("Mouse Y")) * 10f;
#endif
        }

        public static bool MiddlePressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.middleButton.wasPressedThisFrame;
#else
            return UnityEngine.Input.GetMouseButtonDown(2);
#endif
        }

        public static bool MiddleHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null && m.middleButton.isPressed;
#else
            return UnityEngine.Input.GetMouseButton(2);
#endif
        }

        /// <summary>
        /// Scroll wheel in notches: positive is up, about 1 per notch (the Input System's default
        /// uniform range), fractions from a trackpad.
        /// </summary>
        public static float Scroll()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            return m != null ? m.scroll.ReadValue().y : 0f;
#else
            return UnityEngine.Input.mouseScrollDelta.y;
#endif
        }

        public static bool KeyDown(PKey key)
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return false;
            switch (key)
            {
                case PKey.Q: return k.qKey.wasPressedThisFrame;
                case PKey.E: return k.eKey.wasPressedThisFrame;
                case PKey.Escape: return k.escapeKey.wasPressedThisFrame;
                case PKey.Alpha1: return k.digit1Key.wasPressedThisFrame;
                case PKey.Alpha2: return k.digit2Key.wasPressedThisFrame;
                case PKey.Alpha3: return k.digit3Key.wasPressedThisFrame;
                case PKey.Alpha4: return k.digit4Key.wasPressedThisFrame;
                case PKey.Tab: return k.tabKey.wasPressedThisFrame;
                case PKey.R: return k.rKey.wasPressedThisFrame;
                case PKey.Backspace: return k.backspaceKey.wasPressedThisFrame;
                case PKey.F: return k.fKey.wasPressedThisFrame;
                default: return false;
            }
#else
            switch (key)
            {
                case PKey.Q: return UnityEngine.Input.GetKeyDown(KeyCode.Q);
                case PKey.E: return UnityEngine.Input.GetKeyDown(KeyCode.E);
                case PKey.Escape: return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
                case PKey.Alpha1: return UnityEngine.Input.GetKeyDown(KeyCode.Alpha1);
                case PKey.Alpha2: return UnityEngine.Input.GetKeyDown(KeyCode.Alpha2);
                case PKey.Alpha3: return UnityEngine.Input.GetKeyDown(KeyCode.Alpha3);
                case PKey.Alpha4: return UnityEngine.Input.GetKeyDown(KeyCode.Alpha4);
                case PKey.Tab: return UnityEngine.Input.GetKeyDown(KeyCode.Tab);
                case PKey.R: return UnityEngine.Input.GetKeyDown(KeyCode.R);
                case PKey.Backspace: return UnityEngine.Input.GetKeyDown(KeyCode.Backspace);
                case PKey.F: return UnityEngine.Input.GetKeyDown(KeyCode.F);
                default: return false;
            }
#endif
        }

        // Menu navigation that works with a keyboard or a controller (the Switch has no mouse).

        public static bool NavUp()
        {
#if ENABLE_INPUT_SYSTEM
            var g = Gamepad.current;
            return (Keyboard.current?.upArrowKey.wasPressedThisFrame ?? false)
                || (g != null && (g.dpad.up.wasPressedThisFrame || g.leftStick.up.wasPressedThisFrame));
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.UpArrow);
#endif
        }

        public static bool NavDown()
        {
#if ENABLE_INPUT_SYSTEM
            var g = Gamepad.current;
            return (Keyboard.current?.downArrowKey.wasPressedThisFrame ?? false)
                || (g != null && (g.dpad.down.wasPressedThisFrame || g.leftStick.down.wasPressedThisFrame));
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.DownArrow);
#endif
        }

        public static bool NavLeft()
        {
#if ENABLE_INPUT_SYSTEM
            var g = Gamepad.current;
            return (Keyboard.current?.leftArrowKey.wasPressedThisFrame ?? false)
                || (g != null && (g.dpad.left.wasPressedThisFrame || g.leftStick.left.wasPressedThisFrame));
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow);
#endif
        }

        public static bool NavRight()
        {
#if ENABLE_INPUT_SYSTEM
            var g = Gamepad.current;
            return (Keyboard.current?.rightArrowKey.wasPressedThisFrame ?? false)
                || (g != null && (g.dpad.right.wasPressedThisFrame || g.leftStick.right.wasPressedThisFrame));
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.RightArrow);
#endif
        }

#if ENABLE_INPUT_SYSTEM
        // Nintendo puts confirm (A) on the right and back (B) on the bottom; everyone else does the opposite.
#if UNITY_SWITCH
        private static UnityEngine.InputSystem.Controls.ButtonControl ConfirmButton(Gamepad g) => g.buttonEast;
        private static UnityEngine.InputSystem.Controls.ButtonControl BackButton(Gamepad g) => g.buttonSouth;
#else
        private static UnityEngine.InputSystem.Controls.ButtonControl ConfirmButton(Gamepad g) => g.buttonSouth;
        private static UnityEngine.InputSystem.Controls.ButtonControl BackButton(Gamepad g) => g.buttonEast;
#endif
#endif

        /// <summary>Confirm: Enter, or A on a Switch / the bottom face button elsewhere.</summary>
        public static bool Submit()
        {
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return (k != null && (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame))
                || (Gamepad.current != null && ConfirmButton(Gamepad.current).wasPressedThisFrame);
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter);
#endif
        }

        public static bool Back()
        {
#if ENABLE_INPUT_SYSTEM
            return (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false)
                || (Gamepad.current != null && BackButton(Gamepad.current).wasPressedThisFrame);
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        /// <summary>Jump: Space, or A on a Switch / the bottom face button elsewhere.</summary>
        public static bool Jump()
        {
#if ENABLE_INPUT_SYSTEM
            return (Keyboard.current?.spaceKey.wasPressedThisFrame ?? false)
                || (Gamepad.current != null && ConfirmButton(Gamepad.current).wasPressedThisFrame);
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Space);
#endif
        }

        /// <summary>Leave the town: Escape, or + (Start) on a controller, so B can't be pressed by accident.</summary>
        public static bool Pause()
        {
#if ENABLE_INPUT_SYSTEM
            return (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false)
                || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    
#if ENABLE_INPUT_SYSTEM
        private static readonly System.Text.StringBuilder Typed = new System.Text.StringBuilder();
        private static Keyboard _typingKeyboard;

        private static void OnTextInput(char c) => Typed.Append(c);
#endif

        /// <summary>
        /// Printable characters typed since the last call, for text fields. Call it every frame while
        /// a field has focus; it starts listening on the first call, so earlier keys are ignored.
        /// </summary>
        public static string TypedText()
        {
            string text;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k != _typingKeyboard)
            {
                if (_typingKeyboard != null) _typingKeyboard.onTextInput -= OnTextInput;
                if (k != null) k.onTextInput += OnTextInput;
                _typingKeyboard = k;
                Typed.Clear();
            }

            text = Typed.ToString();
            Typed.Clear();
#else
            text = UnityEngine.Input.inputString;
#endif
            // Keep printable characters only; Enter, Escape and Backspace are handled as keys.
            var printable = new System.Text.StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (!char.IsControl(c)) printable.Append(c);
            }

            return printable.ToString();
        }
    }
}
