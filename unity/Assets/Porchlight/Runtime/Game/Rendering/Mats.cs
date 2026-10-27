// Materials as Three makes them: MeshLambertMaterial, MeshStandardMaterial and
// MeshBasicMaterial (with its blending and depth options), all on the
// Porchlight shaders. Render order is Three's: opaque things draw in
// renderOrder order before anything transparent.

using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public enum Blending { Opaque, Normal, Additive }

    public static class Mats
    {
        static Shader lit, unlit, billboard, water;

        public static Shader Lit => lit ??= Load("Porchlight/Lit");
        public static Shader Unlit => unlit ??= Load("Porchlight/Unlit");
        public static Shader Billboard => billboard ??= Load("Porchlight/Billboard");
        public static Shader Water => water ??= Load("Porchlight/Water");

        static Shader Load(string name)
        {
            var s = Resources.Load<Shader>(name);
            if (s == null) s = Shader.Find(name);
            if (s == null) throw new System.Exception("missing shader " + name);
            return s;
        }

        /// <summary>The pass of Lit that writes shadow-map depth.</summary>
        public const int DepthPass = 1;

        public static Material Lambert(Color color, bool vertexColors = false, Texture map = null, bool instanceColors = false, int renderOrder = 0)
        {
            var m = new Material(Lit) { name = "Lambert", enableInstancing = true };
            m.SetColor("_Color", color);
            if (vertexColors) m.EnableKeyword("_VERTEXCOLOR");
            if (instanceColors) m.EnableKeyword("_INSTCOLOR");
            if (map != null)
            {
                m.SetTexture("_MainTex", map);
                m.EnableKeyword("_MAP");
            }
            m.SetColor("_Emissive", Color.black);
            m.renderQueue = (int)RenderQueue.Geometry + renderOrder;
            return m;
        }

        public static Material Standard(Color color, Texture map, double roughness, bool vertexColors = false, bool windows = false, bool clipGround = false, int renderOrder = 0)
        {
            var m = new Material(Lit) { name = "Standard", enableInstancing = true };
            m.SetColor("_Color", color);
            m.SetFloat("_Roughness", (float)roughness);
            m.EnableKeyword("_STANDARD");
            if (vertexColors) m.EnableKeyword("_VERTEXCOLOR");
            if (windows) m.EnableKeyword("_WINDOWS");
            if (clipGround) m.EnableKeyword("_CLIPGROUND");
            if (map != null)
            {
                m.SetTexture("_MainTex", map);
                m.EnableKeyword("_MAP");
            }
            m.SetColor("_Emissive", Color.black);
            m.renderQueue = (int)RenderQueue.Geometry + renderOrder;
            return m;
        }

        public static void SetReceiveShadow(Material m, bool on) => m.SetFloat("_ReceiveShadow", on ? 1 : 0);

        public static Material Basic(Color color, Blending blending = Blending.Opaque, bool depthWrite = true, CompareFunction depthTest = CompareFunction.LessEqual,
            Texture map = null, bool vertexColors = false, bool instanceColors = false, int renderOrder = 0)
        {
            var m = new Material(Unlit) { name = "Basic", enableInstancing = true };
            m.SetColor("_Color", color);
            ApplyBlending(m, blending);
            m.SetFloat("_ZWrite", depthWrite ? 1 : 0);
            m.SetFloat("_ZTest", (float)depthTest);
            if (vertexColors) m.EnableKeyword("_VERTEXCOLOR");
            if (instanceColors) m.EnableKeyword("_INSTCOLOR");
            if (map != null)
            {
                m.SetTexture("_MainTex", map);
                m.EnableKeyword("_MAP");
            }
            m.renderQueue = (blending == Blending.Opaque ? (int)RenderQueue.Geometry : (int)RenderQueue.Transparent) + renderOrder;
            return m;
        }

        /// <summary>Three's non-premultiplied blending: NormalBlending and AdditiveBlending.</summary>
        static void ApplyBlending(Material m, Blending blending)
        {
            switch (blending)
            {
                case Blending.Opaque:
                    m.SetFloat("_SrcBlend", (float)BlendMode.One);
                    m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    break;
                case Blending.Normal:
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    break;
                default:
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)BlendMode.One);
                    break;
            }
        }

        public static Material BillboardMaterial(Texture map, Blending blending, bool depthTest, int renderOrder)
        {
            var m = new Material(Billboard) { name = "Billboard" };
            m.SetTexture("_MainTex", map);
            ApplyBlending(m, blending);
            m.SetFloat("_ZTest", depthTest ? (float)CompareFunction.LessEqual : (float)CompareFunction.Always);
            m.renderQueue = (int)RenderQueue.Transparent + renderOrder;
            return m;
        }

        public static Material WaterMaterial() => new Material(Water) { name = "Water" };
    }
}
