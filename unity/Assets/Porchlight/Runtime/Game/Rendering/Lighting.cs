// The lights the Porchlight shaders read (Three.hlsl): hemisphere, sun, the
// room's point light and fog, and the sun's shadow map, rendered as Three's
// DirectionalLightShadow does: an orthographic camera at the light looking at
// its target, near 1, far 220, a 2048-texel map, bias -0.0004, normal bias
// 0.02, filtered with Three's PCF.

using UnityEngine;
using UnityEngine.Rendering;

namespace Porchlight.Rendering
{
    public sealed class Lighting
    {
        public const int ShadowMapSize = 2048;
        const float Near = 1;
        const float Far = 220;
        const float Bias = -0.0004f;
        const float NormalBias = 0.02f;
        const float Radius = 1;

        public Color hemiSky = Space.Hex(0xe2efff);
        public Color hemiGround = Space.Hex(0x7d8a5a);
        public float hemiIntensity = 1.2f;
        public Color sunColor = Space.Hex(0xfff1d8);
        public float sunIntensity = 2.6f;
        /// <summary>Where the sun is and what it looks at (Three space).</summary>
        public Vector3 sunPosition;
        public Vector3 sunTarget;
        /// <summary>Half the shadow camera's width (tiles).</summary>
        public float shadowHalf = 20;
        public bool shadows = true;
        public Vector3 pointPosition;
        public Color pointColor = Space.Hex(0xffd9a0);
        public float pointIntensity;
        public float pointDistance = 6;
        public float pointDecay = 1.4f;
        public bool fog;
        public float fogNear = 22;
        public float fogFar = 60;
        public Color fogColor;
        public float night;
        public float time;

        RenderTexture shadowMap;
        readonly CommandBuffer cmd = new CommandBuffer { name = "Porchlight shadows" };

        static readonly int HemiSky = Shader.PropertyToID("_PL_HemiSky");
        static readonly int HemiGround = Shader.PropertyToID("_PL_HemiGround");
        static readonly int SunColor = Shader.PropertyToID("_PL_SunColor");
        static readonly int SunDir = Shader.PropertyToID("_PL_SunDir");
        static readonly int PointPos = Shader.PropertyToID("_PL_PointPos");
        static readonly int PointColor = Shader.PropertyToID("_PL_PointColor");
        static readonly int PointParams = Shader.PropertyToID("_PL_PointParams");
        static readonly int Fog = Shader.PropertyToID("_PL_Fog");
        static readonly int FogColor = Shader.PropertyToID("_PL_FogColor");
        static readonly int ShadowMatrix = Shader.PropertyToID("_PL_ShadowMatrix");
        static readonly int ShadowParams = Shader.PropertyToID("_PL_ShadowParams");
        static readonly int ShadowOn = Shader.PropertyToID("_PL_ShadowOn");
        static readonly int ShadowMapId = Shader.PropertyToID("_PL_ShadowMap");
        static readonly int LightView = Shader.PropertyToID("_PL_LightView");
        static readonly int LightRange = Shader.PropertyToID("_PL_LightRange");
        static readonly int LightClip = Shader.PropertyToID("_PL_LightClip");
        static readonly int Dfg = Shader.PropertyToID("_PL_DFG");
        static readonly int Night = Shader.PropertyToID("_PL_Night");
        static readonly int Time = Shader.PropertyToID("_PL_Time");

        static Vector3 Rgb(Color c, float k) => new Vector3(c.r * k, c.g * k, c.b * k);

        /// <summary>Set the shaders' lights and render the shadow map from the stage's casters.</summary>
        public void Apply(Stage stage)
        {
            Shader.SetGlobalVector(HemiSky, Rgb(hemiSky, hemiIntensity));
            Shader.SetGlobalVector(HemiGround, Rgb(hemiGround, hemiIntensity));
            Shader.SetGlobalVector(SunColor, Rgb(sunColor, sunIntensity));
            var toSun = Space.P(sunPosition.x - sunTarget.x, sunPosition.y - sunTarget.y, sunPosition.z - sunTarget.z).normalized;
            Shader.SetGlobalVector(SunDir, toSun);
            Shader.SetGlobalVector(PointPos, Space.P(pointPosition.x, pointPosition.y, pointPosition.z));
            Shader.SetGlobalVector(PointColor, Rgb(pointColor, pointIntensity));
            Shader.SetGlobalVector(PointParams, new Vector4(pointDistance, pointDecay, 0, 0));
            Shader.SetGlobalVector(Fog, new Vector4(fogNear, fogFar, fog ? 1 : 0, 0));
            Shader.SetGlobalVector(FogColor, Rgb(fogColor, 1));
            Shader.SetGlobalTexture(Dfg, DfgLut.Texture);
            Shader.SetGlobalFloat(Night, night);
            Shader.SetGlobalFloat(Time, time);
            RenderShadows(stage);
        }

        void RenderShadows(Stage stage)
        {
            Shader.SetGlobalFloat(ShadowOn, shadows ? 1 : 0);
            if (!shadows) return;
            if (shadowMap == null)
            {
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ? RenderTextureFormat.RFloat : RenderTextureFormat.RHalf;
                shadowMap = new RenderTexture(ShadowMapSize, ShadowMapSize, 24, format, RenderTextureReadWrite.Linear)
                {
                    name = "Porchlight shadow map",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                shadowMap.Create();
            }
            var light = Space.P(sunPosition.x, sunPosition.y, sunPosition.z);
            var target = Space.P(sunTarget.x, sunTarget.y, sunTarget.z);
            var forward = (target - light).normalized;
            var rotation = Quaternion.LookRotation(forward, Vector3.up);
            // World -> light space: x right, y up, z along the light toward its target.
            var lightView = Matrix4x4.TRS(light, rotation, Vector3.one).inverse;
            var cameraView = Matrix4x4.Scale(new Vector3(1, 1, -1)) * lightView;
            var projection = Matrix4x4.Ortho(-shadowHalf, shadowHalf, -shadowHalf, shadowHalf, Near, Far);
            var range = Far - Near;
            var toMap = new Matrix4x4(
                new Vector4(0.5f / shadowHalf, 0, 0, 0),
                new Vector4(0, 0.5f / shadowHalf, 0, 0),
                new Vector4(0, 0, 1 / range, 0),
                new Vector4(0.5f, 0.5f, -Near / range, 1));

            cmd.Clear();
            cmd.SetRenderTarget(shadowMap);
            cmd.ClearRenderTarget(true, true, Color.white);
            cmd.SetViewProjectionMatrices(cameraView, projection);
            cmd.SetGlobalMatrix(LightView, lightView);
            cmd.SetGlobalMatrix(LightClip, GL.GetGPUProjectionMatrix(projection, true) * cameraView);
            cmd.SetGlobalVector(LightRange, new Vector4(Near, Far, 0, 0));
            stage.DrawShadowCasters(cmd);
            Graphics.ExecuteCommandBuffer(cmd);

            Shader.SetGlobalMatrix(ShadowMatrix, toMap * lightView);
            Shader.SetGlobalVector(ShadowParams, new Vector4(Bias, NormalBias, ShadowMapSize, Radius));
            Shader.SetGlobalTexture(ShadowMapId, shadowMap);
        }

        public void Dispose()
        {
            if (shadowMap != null) shadowMap.Release();
            cmd.Release();
        }
    }
}
