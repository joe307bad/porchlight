// What the game tells the server about itself, for investigating lag and
// errors (server/src/multiplayer/telemetry.ts):
//
// - with each join, what it's played on: platform, OS, build, GPU, memory,
//   cores, window size and an anonymous install id (a random id made once and
//   kept in this browser or install; never from the hardware or the account);
// - once a minute, how it ran (Protocol.MSG_PERF): frame rate, slow frames,
//   the longest frame and memory.
//
// Nothing personal: no names, nothing typed, no hardware serials.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Porchlight.App
{
    public static class Telemetry
    {
        /// <summary>A frame longer than this (s) is a slow one: a visible hitch.</summary>
        const float SLOW = 0.05f;
        /// <summary>A frame longer than this (s) is the game in the background (a hidden tab, a sleeping laptop): not counted.</summary>
        const float BACKGROUND = 1f;

        /// <summary>The anonymous install id (FrontDoor sets it: kept in PlayerPrefs, or the browser's storage).</summary>
        public static string Device = "";

        static int frames, slow;
        static double seconds;
        static float worst;

        /// <summary>Every frame (PorchlightApp.Update).</summary>
        public static void Frame(float dt)
        {
            if (dt > BACKGROUND) return;
            frames++;
            seconds += dt;
            if (dt > SLOW) slow++;
            if (dt > worst) worst = dt;
        }

        /// <summary>
        /// The frames since the last call, as the perf message's body, and start
        /// again; null if there weren't any worth reporting (all in the background).
        /// </summary>
        public static Dictionary<string, object> TakePerf(bool hosting)
        {
            if (frames == 0 || seconds < 1)
            {
                Reset();
                return null;
            }
            var m = new Dictionary<string, object>
            {
                ["fps"] = Math.Round(frames / seconds, 1),
                ["slow"] = slow,
                ["worstMs"] = (int)Math.Round(worst * 1000),
                ["memMb"] = (int)(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576),
            };
            if (hosting) m["host"] = true;
            Reset();
            return m;
        }

        static void Reset()
        {
            frames = slow = 0;
            seconds = 0;
            worst = 0;
        }

        /// <summary>"web", "windows", "mac", "linux", "editor" or "other", as the server's ClientPlatform.</summary>
        public static string PlatformName
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WebGLPlayer: return "web";
                    case RuntimePlatform.WindowsPlayer: return "windows";
                    case RuntimePlatform.OSXPlayer: return "mac";
                    case RuntimePlatform.LinuxPlayer: return "linux";
                    case RuntimePlatform.WindowsEditor:
                    case RuntimePlatform.OSXEditor:
                    case RuntimePlatform.LinuxEditor: return "editor";
                    default: return "other";
                }
            }
        }

        /// <summary>What this is played on, for JoinOptions.client.</summary>
        public static Dictionary<string, object> Client() => new Dictionary<string, object>
        {
            ["platform"] = PlatformName,
            ["os"] = SystemInfo.operatingSystem,
            ["build"] = BuildInfo.Short,
            ["gpu"] = SystemInfo.graphicsDeviceName,
            ["memMb"] = SystemInfo.systemMemorySize,
            ["cores"] = SystemInfo.processorCount,
            ["screen"] = $"{Screen.width}x{Screen.height}",
            ["device"] = Device,
        };
    }
}
