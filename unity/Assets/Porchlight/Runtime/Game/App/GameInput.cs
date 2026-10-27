// Keyboard, mouse wheel and controller through the Input System, presented
// the way the browser presented them to the TypeScript game: key-down events
// (with the operating system's auto-repeat: a held key repeats after half a
// second), key-ups, and a controller polled once a frame with its own
// deadzones (gamepad.ts). Consoles and the web read the same pads.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Porchlight.App
{
    /// <summary>Standard-mapping controller buttons (gamepad.ts Button).</summary>
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

        /// <summary>The keys the game listens for, by the browser's KeyboardEvent.code.</summary>
        static readonly (string code, Key key)[] Keys =
        {
            ("KeyW", Key.W), ("KeyA", Key.A), ("KeyS", Key.S), ("KeyD", Key.D),
            ("ArrowUp", Key.UpArrow), ("ArrowDown", Key.DownArrow), ("ArrowLeft", Key.LeftArrow), ("ArrowRight", Key.RightArrow),
            ("KeyQ", Key.Q), ("KeyE", Key.E), ("KeyZ", Key.Z), ("KeyX", Key.X), ("PageUp", Key.PageUp), ("PageDown", Key.PageDown),
            ("KeyF", Key.F), ("KeyG", Key.G), ("KeyB", Key.B), ("Space", Key.Space), ("KeyN", Key.N), ("KeyR", Key.R),
            ("KeyT", Key.T), ("KeyV", Key.V), ("KeyC", Key.C), ("Escape", Key.Escape), ("Backquote", Key.Backquote),
            ("ShiftLeft", Key.LeftShift), ("ShiftRight", Key.RightShift), ("Enter", Key.Enter),
        };

        readonly Dictionary<string, double> repeatAt = new Dictionary<string, double>();
        PadState pad = PadState.Idle;
        bool[] padWas = new bool[Button.Count];

        /// <summary>Told of each key going down: (code, repeat).</summary>
        public event Action<string, bool> KeyDown;
        public event Action<string> KeyUp;
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
                foreach (var (code, key) in Keys)
                {
                    var k = kb[key];
                    if (k.wasPressedThisFrame)
                    {
                        repeatAt[code] = now + REPEAT_DELAY;
                        if (!typing || code == "Escape") KeyDown?.Invoke(code, false);
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
            for (int i = 0; i < controls.Length; i++) s.now[i] = controls[i].isPressed;
            s.was = padWas;
            padWas = s.now;
            s.move = Stick(g.leftStick.ReadUnprocessedValue());
            s.look = Stick(g.rightStick.ReadUnprocessedValue());
            s.lt = Level(g.leftTrigger.ReadUnprocessedValue());
            s.rt = Level(g.rightTrigger.ReadUnprocessedValue());
            pad = s;
        }

        static double Level(float v) => v > TRIGGER_DEADZONE ? (v - TRIGGER_DEADZONE) / (1 - TRIGGER_DEADZONE) : 0;

        /// <summary>A stick with a round deadzone, rescaled so it still runs smoothly from 0 to 1 (y down, as the browser reports).</summary>
        static Vec2 Stick(Vector2 v)
        {
            double x = v.x, y = -v.y;
            double len = Js.Hypot(x, y);
            if (len < DEADZONE) return new Vec2(0, 0);
            double k = Math.Min(1, (len - DEADZONE) / (1 - DEADZONE)) / len;
            return new Vec2(x * k, y * k);
        }
    }
}
