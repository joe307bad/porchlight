using System;
using System.Collections.Generic;
using System.Text;
using Porchlight.Input;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace Porchlight.App
{
    /// <summary>
    /// Diagnostics in the top-left corner, toggled with F3 (remembered between visits): frame rate and
    /// frame times, what the GPU is being asked to draw, memory, the screen, and whatever the app adds
    /// through <see cref="Describe"/> (mode, town size, plant count, camera...). Drawing counts come from
    /// Unity's profiler counters, which only development builds and the editor have; release builds
    /// show "n/a" for those. Uses IMGUI on purpose: it's for us, not players, and needs no art.
    /// </summary>
    public sealed class DebugOverlay : MonoBehaviour
    {
        private const string VisiblePref = "Porchlight.Debug";

        /// <summary>Seconds over which the frame numbers are averaged.</summary>
        private const float Window = 0.5f;

        /// <summary>Lines about the app's own state, asked for twice a second while showing.</summary>
        public Func<IEnumerable<string>> Describe { get; set; }

        private bool _visible;
        private float _elapsed;
        private int _frames;
        private float _worst;
        private string _text = "";
        private GUIStyle _style;
        private Texture2D _backing;

        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _batches;
        private ProfilerRecorder _setPasses;
        private ProfilerRecorder _triangles;
        private ProfilerRecorder _vertices;

        private void OnEnable()
        {
            _visible = PlayerPrefs.GetInt(VisiblePref, 0) == 1;
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPasses = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _vertices = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        }

        private void OnDisable()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _setPasses.Dispose();
            _triangles.Dispose();
            _vertices.Dispose();
        }

        private void OnDestroy()
        {
            if (_backing != null) Destroy(_backing);
        }

        private void Update()
        {
            if (PInput.KeyDown(PKey.F3))
            {
                _visible = !_visible;
                PlayerPrefs.SetInt(VisiblePref, _visible ? 1 : 0);
                PlayerPrefs.Save();
                _elapsed = Window;
            }

            if (!_visible) return;

            var dt = Time.unscaledDeltaTime;
            _elapsed += dt;
            _frames++;
            _worst = Mathf.Max(_worst, dt);
            if (_elapsed < Window) return;

            var fps = _frames / _elapsed;
            var text = new StringBuilder();
            text.AppendLine($"{fps:0} fps   avg {1000f / Mathf.Max(fps, 0.01f):0.0} ms   worst {_worst * 1000f:0.0} ms   (F3 hides)");
            text.AppendLine($"Draw calls {Count(_drawCalls)}   batches {Count(_batches)}   set-pass {Count(_setPasses)}");
            text.AppendLine($"Triangles {Count(_triangles)}   vertices {Count(_vertices)}");
            text.AppendLine($"Memory {Megabytes(Profiler.GetTotalAllocatedMemoryLong())} allocated   {Megabytes(Profiler.GetTotalReservedMemoryLong())} reserved   managed {Megabytes(GC.GetTotalMemory(false))}");
            text.AppendLine($"Screen {Screen.width}×{Screen.height}   {Application.platform}   Unity {Application.unityVersion}");
            if (Describe != null)
            {
                foreach (var line in Describe()) text.AppendLine(line);
            }

            _text = text.ToString().TrimEnd();
            _elapsed = 0f;
            _frames = 0;
            _worst = 0f;
        }

        private static string Count(ProfilerRecorder recorder) => recorder.Valid && recorder.LastValue > 0 ? recorder.LastValue.ToString("N0") : "n/a";

        private static string Megabytes(long bytes) => bytes > 0 ? $"{bytes / (1024f * 1024f):0} MB" : "n/a";

        private void OnGUI()
        {
            if (!_visible || string.IsNullOrEmpty(_text)) return;

            if (_style == null)
            {
                _backing = new Texture2D(1, 1);
                _backing.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f));
                _backing.Apply();
                _style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, padding = new RectOffset(10, 10, 8, 8) };
                _style.normal.background = _backing;
                _style.normal.textColor = Color.white;
            }

            // Readable at any window size.
            _style.fontSize = Mathf.Clamp(Screen.height / 55, 11, 22);
            var content = new GUIContent(_text);
            var size = _style.CalcSize(content);
            GUI.Label(new Rect(8f, 8f, size.x, size.y), content, _style);
        }
    }
}
