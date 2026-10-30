// Keyboard, mouse wheel and controller through the Input System: key-down
// events for the keys the game uses (a held key repeats after half a second,
// as typing does), key-ups, and a controller polled once a frame with its own
// deadzones. Consoles and the web read the same pads.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Porchlight.App
{
    /// <summary>Controller buttons, in the standard gamepad layout's order.</summary>
    public static class Button
    {
        public const int A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, LT = 6, RT = 7, View = 8, Menu = 9;
        public const int LeftStick = 10, RightStick = 11, Up = 12, Down = 13, Left = 14, Right = 15;
        public const int Count = 17;
    }

    public sealed class PadState
    {
        /// <summary>Left and right sticks, x right and y down, 0..1 long after the deadzone.</summary>
        public Vec2 move;
        public Vec2 look;
        public double lt, rt;
        public bool[] now = new bool[Button.Count];
        public bool[] was = new bool[Button.Count];

        public bool Held(int b) => now[b];
        public bool Pressed(int b) => now[b] && !was[b];

        public static readonly PadState Idle = new PadState();
    }

    /// <summary>Turns a stick axis into one-off flicks: fires once past `fire`, then not until back under `rearm`.</summary>
    public sealed class Flick
    {
        bool armed = true;
        readonly double fire, rearm;

        public Flick(double fire = 0.7, double rearm = 0.3)
        {
            this.fire = fire;
            this.rearm = rearm;
        }

        public int Update(double v)
        {
            if (Math.Abs(v) < rearm) armed = true;
            if (!armed || Math.Abs(v) < fire) return 0;
            armed = false;
            return Math.Sign(v);
        }
    }

    public sealed class GameInput
    {
        const double DEADZONE = 0.2;
        const double TRIGGER_DEADZONE = 0.08;
        const double REPEAT_DELAY = 0.5;
        const double REPEAT_RATE = 1 / 30.0;

        /// <summary>
        /// Firefox on macOS reports a Bluetooth Xbox One S pad (id "045e-02fd-…") as a
        /// standard gamepad but with its own button numbers: for each of Button's,
        /// the standard index it comes in at (-1: it doesn't). It gives Y and LB one
        /// number (FIREFOX_Y_OR_LB: a tap is read as Y, a hold as LB), RB and View
        /// another (read as RB, the way to the nearest player), has no triggers, and
        /// puts Menu past the 16 buttons the Input System reads.
        /// </summary>
        static readonly int[] FirefoxXboxBluetooth = { 1, 2, 3, -1, -1, 8, -1, -1, -1, -1, 11, -1, 12, 13, 14, 15 };
        const int FIREFOX_Y_OR_LB = 5;
        /// <summary>Seconds Firefox's shared Y / LB button is held before it counts as LB.</summary>
        const double LB_HOLD = 0.4;

        /// <summary>The keys the game listens for.</summary>
        static readonly Key[] Keys =
        {
            Key.W, Key.A, Key.S, Key.D, Key.UpArrow, Key.DownArrow, Key.LeftArrow, Key.RightArrow,
            Key.Q, Key.E, Key.Z, Key.X, Key.PageUp, Key.PageDown, Key.F, Key.G, Key.B, Key.Space,
            Key.N, Key.R, Key.T, Key.V, Key.C, Key.M, Key.Escape, Key.Backquote, Key.LeftShift, Key.RightShift, Key.Enter, Key.Tab,
        };

        readonly Dictionary<Key, double> repeatAt = new Dictionary<Key, double>();
        PadState pad = PadState.Idle;
        bool[] padWas = new bool[Button.Count];
        double sharedDownAt = -1;
        bool sharedWasLB;

        /// <summary>Told of each key going down: (key, repeat).</summary>
        public event Action<Key, bool> KeyDown;
        public event Action<Key> KeyUp;
        /// <summary>The window lost focus: let go of everything.</summary>
        public event Action Blur;

        public bool Connected { get; private set; }

        public PadState Pad => pad;

        /// <summary>Whether a text field has the keyboard (then the game ignores keys).</summary>
        public Func<bool> Typing = () => false;

        bool focused = true;

        public void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (Application.isFocused != focused)
            {
                focused = Application.isFocused;
                if (!focused) Blur?.Invoke();
            }
            var kb = Keyboard.current;
            if (kb != null)
            {
                bool typing = Typing();
                foreach (var code in Keys)
                {
                    var k = kb[code];
                    if (k.wasPressedThisFrame)
                    {
                        repeatAt[code] = now + REPEAT_DELAY;
                        if (!typing || code == Key.Escape) KeyDown?.Invoke(code, false);
                    }
                    else if (k.isPressed && repeatAt.TryGetValue(code, out double at) && now >= at)
                    {
                        repeatAt[code] = now + REPEAT_RATE;
                        if (!typing) KeyDown?.Invoke(code, true);
                    }
                    if (k.wasReleasedThisFrame)
                    {
                        repeatAt.Remove(code);
                        KeyUp?.Invoke(code);
                    }
                }
            }
            PollPad();
        }

        public bool ShiftHeld
        {
            get
            {
                var kb = Keyboard.current;
                return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            }
        }

        /// <summary>The mouse wheel this frame, as a browser's deltaY (pixels, down positive).</summary>
        public double WheelDeltaY
        {
            get
            {
                var m = Mouse.current;
                if (m == null) return 0;
                // The Input System reports about 120 per notch (up positive); browsers about 100 (down positive).
                return -m.scroll.ReadValue().y * (100.0 / 120.0);
            }
        }

        public bool CtrlHeld
        {
            get
            {
                var kb = Keyboard.current;
                return kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            }
        }

        void PollPad()
        {
            var g = Gamepad.current;
            Connected = g != null;
            if (g == null)
            {
                padWas = new bool[Button.Count];
                pad = PadState.Idle;
                return;
            }
            var s = new PadState();
            ButtonControl[] controls =
            {
                g.buttonSouth, g.buttonEast, g.buttonWest, g.buttonNorth, g.leftShoulder, g.rightShoulder,
                g.leftTrigger, g.rightTrigger, g.selectButton, g.startButton, g.leftStickButton, g.rightStickButton,
                g.dpad.up, g.dpad.down, g.dpad.left, g.dpad.right,
            };
            bool firefoxXbox = (g.description.product ?? "").StartsWith("045e-02fd");
            for (int i = 0; i < controls.Length; i++)
            {
                int from = firefoxXbox ? FirefoxXboxBluetooth[i] : i;
                s.now[i] = from >= 0 && controls[from].isPressed;
            }
            if (firefoxXbox) SplitYAndLB(s, controls[FIREFOX_Y_OR_LB].isPressed);
            s.was = padWas;
            padWas = s.now;
            s.move = Stick(g.leftStick.ReadUnprocessedValue());
            s.look = Stick(g.rightStick.ReadUnprocessedValue());
            s.lt = firefoxXbox ? 0 : Level(g.leftTrigger.ReadUnprocessedValue());
            s.rt = firefoxXbox ? 0 : Level(g.rightTrigger.ReadUnprocessedValue());
            pad = s;
        }

        /// <summary>
        /// Firefox's one button for Y and LB as two: let go within LB_HOLD and it's a Y
        /// press (day or night), held on past that it's an LB press (each for one frame:
        /// the game only asks either whether it was just pressed).
        /// </summary>
        void SplitYAndLB(PadState s, bool down)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (down && sharedDownAt < 0)
            {
                sharedDownAt = now;
                sharedWasLB = false;
            }
            if (down && !sharedWasLB && now - sharedDownAt >= LB_HOLD)
            {
                s.now[Button.LB] = true;
                sharedWasLB = true;
            }
            if (!down && sharedDownAt >= 0)
            {
                if (!sharedWasLB) s.now[Button.Y] = true;
                sharedDownAt = -1;
            }
        }

        static double Level(float v) => v > TRIGGER_DEADZONE ? (v - TRIGGER_DEADZONE) / (1 - TRIGGER_DEADZONE) : 0;

        /// <summary>A stick with a round deadzone, rescaled so it still runs smoothly from 0 to 1 (y down, as the browser reports).</summary>
        static Vec2 Stick(Vector2 v)
        {
            double x = v.x, y = -v.y;
            double len = Numbers.Hypot(x, y);
            if (len < DEADZONE) return new Vec2(0, 0);
            double k = Math.Min(1, (len - DEADZONE) / (1 - DEADZONE)) / len;
            return new Vec2(x * k, y * k);
        }
    }
}
