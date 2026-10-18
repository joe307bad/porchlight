using Porchlight.Input;
using Porchlight.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.App
{
    /// <summary>
    /// The look shared by the menus that lie on the plaza: Material Design 3 colour roles, type
    /// scale and elevation, with square corners instead of rounded ones. Sizes are in metres on the
    /// ground. Menus are laid out in plaza coordinates: x to the right, z up the screen, inside
    /// <see cref="ContentLeft"/>..<see cref="ContentRight"/> and <see cref="ContentBottom"/>..<see cref="ContentTop"/>.
    /// </summary>
    public static class MenuStyle
    {
        public const float ContentLeft = -28f;
        public const float ContentRight = 28f;
        public const float ContentTop = 21f;
        public const float ContentBottom = -19f;

        /// <summary>Panels rest just above the grid.</summary>
        public const float Floor = 0.07f;

        /// <summary>How far the focused panel lifts: elevation level 2.</summary>
        public const float Raise = 0.6f;

        public const float PanelThickness = 0.15f;
        public const float Rim = 0.35f;

        // Type scale. Headers use block lettering (units per letter pixel); the rest is Inter, em size in metres.
        public const float Headline = 0.62f;
        public const float Subtitle = 2f;
        public const float TitleLarge = 1.55f;
        public const float Title = 1.3f;
        public const float Body = 1.05f;
        public const float Label = 0.85f;

        // Where the line under a header and the key hints along the bottom go.
        public const float SubtitleZ = 13.8f;
        public const float HintZ = -18f;

        // Colour roles. Set in Init, because Unity won't report the colour space during static setup.
        public static Color Primary;
        public static Color OnPrimary;
        public static Color OnPrimaryMuted;
        public static Color PrimaryShadow;
        public static Color PrimaryHover;
        public static Color Surface;
        public static Color OnSurface;
        public static Color OnSurfaceMuted;
        public static Color OnBackground;
        public static Color Warning;

        private static bool _ready;

        public static void Init()
        {
            if (_ready) return;
            _ready = true;
            Primary = LowPolyMeshes.Hex("#2B6CC4");
            OnPrimary = LowPolyMeshes.Hex("#F5F9FF");
            OnPrimaryMuted = LowPolyMeshes.Hex("#C7DAF5");
            PrimaryShadow = LowPolyMeshes.Hex("#1A4787");
            PrimaryHover = LowPolyMeshes.Hex("#3F82DA");
            Surface = LowPolyMeshes.Hex("#F5F9FF");
            OnSurface = LowPolyMeshes.Hex("#1B2A41");
            OnSurfaceMuted = LowPolyMeshes.Hex("#4D607D");
            OnBackground = LowPolyMeshes.Hex("#FFFFFF");
            Warning = LowPolyMeshes.Hex("#FFE08A");
        }

        /// <summary>
        /// A sharp rectangle with a light outline, centred on (x, z). Resting panels are filled with
        /// the primary colour; the focused one turns to surface white and lifts, with a dark slab
        /// underneath for its shadow. Returns the height of its top face, for whatever sits on it.
        /// </summary>
        public static float Panel(MeshBuilder mb, float x, float z, float width, float depth, bool focused)
        {
            var y = focused ? Raise : Floor;
            if (focused) mb.Box(new Vector3(x + 0.5f, Floor, z - 0.5f), width, Raise, depth, PrimaryShadow, PrimaryShadow);

            mb.Box(new Vector3(x, y, z), width, PanelThickness, depth, PrimaryShadow, focused ? Surface : Primary);
            var top = y + PanelThickness;
            float hw = width * 0.5f, hd = depth * 0.5f;
            mb.Box(new Vector3(x, top, z + hd - Rim * 0.5f), width, Rim, Rim, Primary, OnPrimary);
            mb.Box(new Vector3(x, top, z - hd + Rim * 0.5f), width, Rim, Rim, Primary, OnPrimary);
            mb.Box(new Vector3(x - hw + Rim * 0.5f, top, z), Rim, Rim, depth, Primary, OnPrimary);
            mb.Box(new Vector3(x + hw - Rim * 0.5f, top, z), Rim, Rim, depth, Primary, OnPrimary);
            return top;
        }

        /// <summary>Header lettering: white blocks with blue sides, standing just off the floor.</summary>
        public static void Header(MeshBuilder mb, string text, float x, float z, float unit = Headline, TextAlign align = TextAlign.Left)
        {
            BlockText.Add(mb, text, new Vector3(x, Floor, z), unit, 0.4f, OnPrimary, Primary, align);
        }

        /// <summary>
        /// Where the camera goes for a menu. The title screen looks down at the plaza at an angle,
        /// with the town around it; the menus after it look straight down, so they read like a page.
        /// Either way the whole menu area fits on screen.
        /// </summary>
        public static Pose CameraPose(Camera camera, bool angled)
        {
            var target = new Vector3(0f, 0f, (ContentTop + ContentBottom) * 0.5f);
            var halfV = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            var halfH = Mathf.Atan(Mathf.Tan(halfV) * camera.aspect);
            var halfWidth = (ContentRight - ContentLeft) * 0.5f + 1f;
            var halfDepth = (ContentTop - ContentBottom) * 0.5f + 1f;

            if (!angled)
            {
                // Straight down, with +Z at the top of the screen.
                var down = Quaternion.Euler(90f, 0f, 0f);
                var height = Mathf.Max(halfWidth / Mathf.Tan(halfH), halfDepth / Mathf.Tan(halfV)) * 1.15f;
                return new Pose(target + Vector3.up * height, down);
            }

            const float pitch = 52f;
            var rotation = Quaternion.Euler(pitch, -12f, 0f);
            var depthOnScreen = (ContentTop - ContentBottom) * 0.5f * Mathf.Sin(pitch * Mathf.Deg2Rad);
            var distance = Mathf.Max(halfWidth / Mathf.Tan(halfH), depthOnScreen / Mathf.Tan(halfV)) * 2.05f;
            return new Pose(target - rotation * Vector3.forward * distance, rotation);
        }

        /// <summary>Where the pointer meets the menu floor, in world space.</summary>
        public static bool PointerOnFloor(out Vector3 point)
        {
            point = default;
            var camera = Camera.main;
            if (camera == null) return false;
            var ray = camera.ScreenPointToRay(PInput.PointerPosition());
            if (!new Plane(Vector3.up, Vector3.up * 0.3f).Raycast(ray, out var distance)) return false;
            point = ray.GetPoint(distance);
            return true;
        }

        public static bool Contains(Vector3 point, float x, float z, float width, float depth) =>
            Mathf.Abs(point.x - x) <= width * 0.5f && Mathf.Abs(point.z - z) <= depth * 0.5f;

        public static MeshFilter MeshChild(Transform parent, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
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
