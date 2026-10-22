using System;
using System.Collections.Generic;
using Porchlight.Input;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.App
{
    /// <summary>
    /// The bar along the bottom fifth of the screen while in a town. It isn't an overlay: the town
    /// camera draws only the top four fifths, and this bar's own camera draws the rest, looking
    /// straight down at a strip of grass far from the town, where an <see cref="IHudContent"/>
    /// lays out its panels. It redraws only when the content's state, the hovered item or the window
    /// width changes.
    ///
    /// A second one can stand down the right-hand side of the town instead (<see cref="Init"/> with
    /// side set), drawn over the town at the same scale as the bar, for vertical lists like layers.
    /// </summary>
    public sealed class HudBar : MonoBehaviour
    {
        /// <summary>How much of the screen height the bar takes.</summary>
        public const float Fraction = 0.2f;

        /// <summary>The layer the bar's objects live on, so only its own camera sees them.</summary>
        public const int Layer = 30;

        private const string Grass = "#4E6E40";
        private const float BaseHeight = 12f;
        private const float MinWidth = 102f;

        /// <summary>How wide the side panel is, in bar units, and the most of the screen it may take.</summary>
        private const float SideWidth = 22f;
        private const float MaxSideFraction = 0.35f;

        private static readonly Vector3 Origin = new Vector3(0f, -3000f, 0f);

        /// <summary>The side panel's strip of grass, far enough along that the bar's camera never sees it.</summary>
        private static readonly Vector3 SideOrigin = new Vector3(5000f, -3000f, 0f);

        private static readonly List<HudBar> Shown = new List<HudBar>();

        private bool _side;

        private Camera _camera;
        private MeshFilter _shapes;
        private MeshFilter _text;
        private readonly List<MeshFilter> _images = new List<MeshFilter>();
        private readonly Dictionary<Texture, Material> _imageMaterials = new Dictionary<Texture, Material>();
        private List<(Rect Rect, Action Action)> _hits = new List<(Rect, Action)>();
        private IHudContent _content;
        private string _drawnKey;

        /// <summary>True when the pointer is over the bar (or a side panel), so the town shouldn't react to it.</summary>
        public static bool PointerInBar()
        {
            var pointer = PInput.PointerPosition();
            foreach (var bar in Shown)
            {
                if (bar.PixelRect().Contains(pointer)) return true;
            }

            return false;
        }

        public void Init(Material flatMaterial, int mainCameraDepth, bool side = false)
        {
            MenuStyle.Init();
            _side = side;
            transform.position = side ? SideOrigin : Origin;

            _camera = new GameObject("Bar camera").AddComponent<Camera>();
            _camera.transform.SetParent(transform, false);
            _camera.transform.SetLocalPositionAndRotation(Vector3.up * 50f, Quaternion.Euler(90f, 0f, 0f));
            _camera.orthographic = true;
            _camera.rect = new Rect(0f, 0f, 1f, Fraction);
            _camera.depth = mainCameraDepth + 1;
            _camera.cullingMask = 1 << Layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = LowPolyMeshes.Hex(Grass);
            _camera.nearClipPlane = 1f;
            _camera.farClipPlane = 100f;

            // Plain grass like the plaza menus, a shade darker so small white text stays readable.
            var mb = new MeshBuilder();
            mb.Quad(new Vector3(-600f, -0.1f, -60f), new Vector3(-600f, -0.1f, 60f), new Vector3(600f, -0.1f, 60f), new Vector3(600f, -0.1f, -60f), LowPolyMeshes.Hex(Grass), Vector3.up);
            Child("Grass", flatMaterial).sharedMesh = mb.ToMesh(name: "bar grass");


            _shapes = Child("Shapes", flatMaterial);
            _text = FlatText.CreateRenderer(transform, "Text");
            _text.gameObject.layer = Layer;

            FlatText.Rebuilt += Invalidate;
            gameObject.SetActive(false);
        }

        private void OnDestroy() => FlatText.Rebuilt -= Invalidate;

        public void Show(IHudContent content)
        {
            _content = content;
            _drawnKey = null;
            if (!Shown.Contains(this)) Shown.Add(this);
            Place();
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            _content = null;
            Shown.Remove(this);
            gameObject.SetActive(false);
        }

        private void OnDisable() => Shown.Remove(this);

        /// <summary>Bar units per screen pixel: the bar is <see cref="BaseHeight"/> units tall.</summary>
        private static float UnitsPerPixel() => BaseHeight / Mathf.Max(1f, Screen.height * Fraction);

        /// <summary>The side panel's share of the screen: from the top of the bar up, at the right.</summary>
        private static Rect SideRect()
        {
            var width = Mathf.Min(MaxSideFraction, SideWidth / UnitsPerPixel() / Mathf.Max(1f, Screen.width));
            return new Rect(1f - width, Fraction, width, 1f - Fraction);
        }

        private Rect PixelRect()
        {
            var r = _side ? SideRect() : new Rect(0f, 0f, 1f, Fraction);
            return new Rect(r.x * Screen.width, r.y * Screen.height, r.width * Screen.width, r.height * Screen.height);
        }

        private void Place()
        {
            if (_camera != null) _camera.rect = _side ? SideRect() : new Rect(0f, 0f, 1f, Fraction);
        }

        private void Invalidate() => _drawnKey = null;

        private void LateUpdate()
        {
            if (_content == null) return;

            // Keep the bar a fixed height in units, unless the window is too narrow for the content.
            // The side panel uses the same scale, so its text matches the bar's.
            Place();
            var pixels = PixelRect();
            float width, height;
            if (_side)
            {
                width = pixels.width * UnitsPerPixel();
                height = pixels.height * UnitsPerPixel();
            }
            else
            {
                var aspect = Screen.width / Mathf.Max(1f, Screen.height * Fraction);
                height = Mathf.Max(BaseHeight, MinWidth / aspect);
                width = height * aspect;
            }

            _camera.orthographicSize = height * 0.5f;

            Vector2? pointer = null;
            var p = PInput.PointerPosition();
            if (pixels.Contains(p))
            {
                pointer = new Vector2(((p.x - pixels.x) / Mathf.Max(1f, pixels.width) - 0.5f) * width, ((p.y - pixels.y) / Mathf.Max(1f, pixels.height) - 0.5f) * height);
            }

            // Clicks go to what's on screen now, before anything is redrawn.
            var hovered = -1;
            for (var i = 0; pointer.HasValue && i < _hits.Count; i++)
            {
                if (_hits[i].Rect.Contains(pointer.Value)) hovered = i;
            }

            if (hovered >= 0 && PInput.PrimaryPressedThisFrame()) _hits[hovered].Action();
            if (_content == null) return;

            var key = $"{_content.StateKey}|{hovered}|{width:0.0}|{height:0.0}";
            if (key != _drawnKey) Draw(width, height, pointer, key);
        }

        private void Draw(float width, float height, Vector2? pointer, string key)
        {
            var canvas = new HudCanvas(width, height, pointer);
            _content.Draw(canvas);
            canvas.Shapes.ToMesh(_shapes.sharedMesh);
            canvas.Text.ToMesh(_text.sharedMesh);
            _hits = canvas.Hits;

            for (var i = 0; i < canvas.Images.Count; i++)
            {
                if (i == _images.Count) _images.Add(Child("Image", null));
                var (texture, rect, uv, y) = canvas.Images[i];
                if (!_imageMaterials.TryGetValue(texture, out var material)) _imageMaterials[texture] = material = Studio.ImageMaterial(texture);

                var image = _images[i];
                image.GetComponent<MeshRenderer>().sharedMaterial = material;
                if (image.sharedMesh != null) Destroy(image.sharedMesh);
                image.sharedMesh = Studio.ImageQuad(rect.width, rect.height, uv);
                image.transform.localPosition = new Vector3(rect.center.x, y, rect.center.y);
                image.gameObject.SetActive(true);
            }

            for (var i = canvas.Images.Count; i < _images.Count; i++)
            {
                _images[i].gameObject.SetActive(false);
            }

            _drawnKey = key;
        }

        private MeshFilter Child(string name, Material material)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(transform, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = new Mesh { name = name };
            return filter;
        }
    }
}
