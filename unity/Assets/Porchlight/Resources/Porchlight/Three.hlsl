// Three.js r186's lighting, as the TypeScript game renders with it: a
// hemisphere light, the sun (with Three's PCF shadow filter: five Vogel-disk
// taps of a bilinear depth comparison, rotated per pixel), a point light for
// the room you're in, linear fog, and MeshLambertMaterial's and
// MeshStandardMaterial's BRDFs (Standard with the DFG lookup and
// multiple-scattering compensation). Every colour is linear; the frame buffer
// does the sRGB conversion Three does in its shaders.
//
// World space here is Unity's: Three's with z negated (see Space.cs).

#ifndef PORCHLIGHT_THREE_INCLUDED
#define PORCHLIGHT_THREE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

#define PL_RECIPROCAL_PI 0.3183098861837907
#define PL_PI2 6.283185307179586
#define PL_EPSILON 1e-6

// Set every frame by Lighting.cs. Colours are linear and already scaled by intensity.
float3 _PL_HemiSky;
float3 _PL_HemiGround;
float3 _PL_SunColor;
float3 _PL_SunDir;
float3 _PL_PointPos;
float3 _PL_PointColor;
float4 _PL_PointParams; // x: cutoff distance, y: decay
float4 _PL_Fog;         // x: near, y: far, z: 1 when on
float3 _PL_FogColor;
float4x4 _PL_ShadowMatrix; // world -> (u, v, depth 0..1)
float4 _PL_ShadowParams;   // x: bias, y: normal bias, z: map size, w: radius (texels)
float _PL_ShadowOn;
float _PL_Night;
float _PL_Time;

TEXTURE2D(_PL_ShadowMap);
TEXTURE2D(_PL_DFG);
SAMPLER(sampler_PL_DFG);

float pl_pow2(float x) { return x * x; }

float PL_FogFactor(float viewDepth)
{
    return _PL_Fog.z > 0.5 ? smoothstep(_PL_Fog.x, _PL_Fog.y, viewDepth) : 0.0;
}

float3 PL_ApplyFog(float3 color, float viewDepth)
{
    return lerp(color, _PL_FogColor, PL_FogFactor(viewDepth));
}

float PL_ViewDepth(float3 positionWS)
{
    return -mul(UNITY_MATRIX_V, float4(positionWS, 1.0)).z;
}

/// Three's geometryViewDir, in world space: toward the camera.
float3 PL_ViewDir(float3 positionWS)
{
    if (unity_OrthoParams.w > 0.5) return normalize(UNITY_MATRIX_V[2].xyz);
    return normalize(_WorldSpaceCameraPos - positionWS);
}

float PL_InterleavedGradientNoise(float2 position)
{
    return frac(52.9829189 * frac(dot(position, float2(0.06711056, 0.00583715))));
}

float2 PL_VogelDiskSample(int sampleIndex, int samplesCount, float phi)
{
    const float goldenAngle = 2.399963229728653;
    float r = sqrt((float(sampleIndex) + 0.5) / float(samplesCount));
    float theta = float(sampleIndex) * goldenAngle + phi;
    return float2(cos(theta), sin(theta)) * r;
}

/// A LessEqual depth comparison filtered bilinearly, as a sampler2DShadow with LinearFilter does.
float PL_ShadowTap(float2 uv, float z)
{
    float size = _PL_ShadowParams.z;
    float2 t = uv * size - 0.5;
    float2 f = frac(t);
    int2 i0 = (int2)floor(t);
    int maxI = (int)size - 1;
    int2 a = clamp(i0, 0, maxI);
    int2 b = clamp(i0 + 1, 0, maxI);
    float d00 = LOAD_TEXTURE2D(_PL_ShadowMap, int2(a.x, a.y)).r;
    float d10 = LOAD_TEXTURE2D(_PL_ShadowMap, int2(b.x, a.y)).r;
    float d01 = LOAD_TEXTURE2D(_PL_ShadowMap, int2(a.x, b.y)).r;
    float d11 = LOAD_TEXTURE2D(_PL_ShadowMap, int2(b.x, b.y)).r;
    float c00 = z <= d00 ? 1.0 : 0.0;
    float c10 = z <= d10 ? 1.0 : 0.0;
    float c01 = z <= d01 ? 1.0 : 0.0;
    float c11 = z <= d11 ? 1.0 : 0.0;
    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

/// Three's getShadow for a directional light with PCFShadowMap.
float PL_Shadow(float3 positionWS, float3 normalWS, float2 fragCoord)
{
    if (_PL_ShadowOn < 0.5) return 1.0;
    float4 sc = mul(_PL_ShadowMatrix, float4(positionWS + normalWS * _PL_ShadowParams.y, 1.0));
    sc.xyz /= sc.w;
    sc.z += _PL_ShadowParams.x;
    bool inFrustum = sc.x >= 0.0 && sc.x <= 1.0 && sc.y >= 0.0 && sc.y <= 1.0;
    if (!(inFrustum && sc.z <= 1.0)) return 1.0;
    float radius = _PL_ShadowParams.w / _PL_ShadowParams.z;
    float phi = PL_InterleavedGradientNoise(fragCoord) * PL_PI2;
    float shadow = 0.0;
    [unroll] for (int k = 0; k < 5; k++) shadow += PL_ShadowTap(sc.xy + PL_VogelDiskSample(k, 5, phi) * radius, sc.z);
    return shadow * 0.2;
}

float3 PL_HemiIrradiance(float3 n)
{
    float w = 0.5 * n.y + 0.5;
    return lerp(_PL_HemiGround, _PL_HemiSky, w);
}

float PL_DistanceAttenuation(float lightDistance, float cutoffDistance, float decayExponent)
{
    float distanceFalloff = 1.0 / max(pow(lightDistance, decayExponent), 0.01);
    if (cutoffDistance > 0.0) distanceFalloff *= pl_pow2(saturate(1.0 - pl_pow2(pl_pow2(lightDistance / cutoffDistance))));
    return distanceFalloff;
}

/// MeshLambertMaterial: BRDF_Lambert for the sun, the room light and the hemisphere.
float3 PL_Lambert(float3 albedo, float3 n, float3 positionWS, float shadow)
{
    float3 brdf = albedo * PL_RECIPROCAL_PI;
    float3 color = saturate(dot(n, _PL_SunDir)) * _PL_SunColor * shadow * brdf;
    float3 lv = _PL_PointPos - positionWS;
    float3 pc = _PL_PointColor * PL_DistanceAttenuation(length(lv), _PL_PointParams.x, _PL_PointParams.y);
    color += saturate(dot(n, normalize(lv))) * pc * brdf;
    color += PL_HemiIrradiance(n) * brdf;
    return color;
}

float3 PL_FSchlick(float3 f0, float f90, float dotVH)
{
    float fresnel = exp2((-5.55473 * dotVH - 6.98316) * dotVH);
    return f0 * (1.0 - fresnel) + (f90 * fresnel);
}

float PL_VGGXSmithCorrelated(float alpha, float dotNL, float dotNV)
{
    float a2 = pl_pow2(alpha);
    float gv = dotNL * sqrt(a2 + (1.0 - a2) * pl_pow2(dotNV));
    float gl = dotNV * sqrt(a2 + (1.0 - a2) * pl_pow2(dotNL));
    return 0.5 / max(gv + gl, PL_EPSILON);
}

float PL_DGGX(float alpha, float dotNH)
{
    float a2 = pl_pow2(alpha);
    float denom = pl_pow2(dotNH) * (a2 - 1.0) + 1.0;
    return PL_RECIPROCAL_PI * a2 / pl_pow2(denom);
}

void PL_DirectPhysical(float3 L, float3 lightColor, float3 n, float3 v, float3 albedo, float roughness, float3 msc, inout float3 diffuse, inout float3 specular)
{
    float dotNL = saturate(dot(n, L));
    float3 irradiance = dotNL * lightColor;
    float alpha = pl_pow2(roughness);
    float3 h = normalize(L + v);
    float dotNV = saturate(dot(n, v));
    float dotNH = saturate(dot(n, h));
    float dotVH = saturate(dot(v, h));
    float3 F = PL_FSchlick(float3(0.04, 0.04, 0.04), 1.0, dotVH);
    float vis = PL_VGGXSmithCorrelated(alpha, dotNL, dotNV);
    float d = PL_DGGX(alpha, dotNH);
    specular += irradiance * (F * (vis * d)) * msc;
    diffuse += irradiance * (albedo * PL_RECIPROCAL_PI) * (1.0 - F);
}

/// MeshStandardMaterial with metalness 0, no environment map.
float3 PL_Standard(float3 albedo, float roughnessFactor, float3 n, float3 positionWS, float shadow)
{
    float3 nv = mul((float3x3)UNITY_MATRIX_V, n);
    float3 dxy = max(abs(ddx(nv)), abs(ddy(nv)));
    float geometryRoughness = max(max(dxy.x, dxy.y), dxy.z);
    float roughness = min(max(roughnessFactor, 0.0525) + geometryRoughness, 1.0);
    float3 v = PL_ViewDir(positionWS);
    float dotNVms = saturate(dot(n, v));
    float2 dfg = SAMPLE_TEXTURE2D(_PL_DFG, sampler_PL_DFG, float2(roughness, dotNVms)).rg;
    float ess = dfg.x + dfg.y;
    float3 msc = 1.0 + 0.04 * (1.0 / ess - 1.0);

    float3 diffuse = 0;
    float3 specular = 0;
    PL_DirectPhysical(_PL_SunDir, _PL_SunColor * shadow, n, v, albedo, roughness, msc, diffuse, specular);
    float3 lv = _PL_PointPos - positionWS;
    float3 pc = _PL_PointColor * PL_DistanceAttenuation(length(lv), _PL_PointParams.x, _PL_PointParams.y);
    PL_DirectPhysical(normalize(lv), pc, n, v, albedo, roughness, msc, diffuse, specular);

    // Indirect diffuse (the hemisphere light), with the energy the specular lobe keeps taken out.
    float fssEss = 0.04 * dfg.x + dfg.y;
    float ems = 1.0 - ess;
    float favg = 0.04 + (1.0 - 0.04) * 0.047619;
    float fms = fssEss * favg / (1.0 - ems * favg);
    float3 indirect = PL_HemiIrradiance(n) * (albedo * PL_RECIPROCAL_PI) * (1.0 - fssEss - fms * ems);
    return diffuse + specular + indirect;
}

float PL_LitHash(float3 p)
{
    return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453);
}

/// The night-time window glow (windows.ts), in Three's coordinates: building and
/// litPos are world positions, local is (model height, across-the-wall position).
float3 PL_WindowGlow(float seed, float4 grid, float2 local, float3 litPos, float3 building, float litUp)
{
    float3 glow = 0;
    if (_PL_Night > 0.01 && seed > 0.5 && litUp < 0.35)
    {
        float3 key = float3(seed * 97.0, floor(building.x * 10.0), floor(building.z * 10.0));
        if (seed > 2.5)
        {
            float floorN = grid.y > 0.0 ? floor((local.x - grid.x) / grid.y) : floor(local.x / 0.35);
            float bayN = grid.w > 0.0 ? floor((local.y - grid.z) / grid.w) : 0.0;
            key += float3(floorN * 13.0, bayN * 7.0, 0.0);
        }
        float h = PL_LitHash(key);
        float chance = lerp(0.15, 0.36, clamp(litPos.y / 3.5, 0.0, 1.0));
        float lit = step(1.0 - chance, h);
        float3 warm = lerp(float3(1.0, 0.72, 0.38), float3(1.0, 0.86, 0.6), PL_LitHash(key + 3.0));
        bool tall = seed > 2.5 || litPos.y > 2.2;
        if (tall)
        {
            if (PL_LitHash(key + 29.0) < 0.03) glow += float3(0.35, 0.55, 1.0) * _PL_Night * 0.35;
            else glow += warm * lit * _PL_Night * 0.35;
        }
        else
        {
            float f = PL_LitHash(key + 17.0);
            if (f < 0.12)
            {
                float t = _PL_Time * (6.0 + f * 40.0) + h * 50.0;
                float nn = frac(sin(floor(t) * 91.7 + h * 13.0) * 4375.5);
                lit *= lerp(0.25, 1.0, step(0.3, nn)) * (0.85 + 0.15 * sin(t * 3.1));
            }
            glow += warm * lit * _PL_Night * 0.7;
        }
    }
    return glow;
}

#endif
