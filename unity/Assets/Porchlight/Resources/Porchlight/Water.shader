// The water surface (world.ts addWater): a shimmer of two sine waves over a
// shallow or deep blue, dimmed at night. Three's ShaderMaterial writes its
// colour straight to the screen with no sRGB conversion, so the colour here is
// the screen colour, converted to linear for Unity's sRGB frame buffer.

Shader "Porchlight/Water"
{
    Properties
    {
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Three.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 deep : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float deep : TEXCOORD0;
                float2 xz : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.deep = input.deep.x;
                o.xz = float2(positionWS.x, -positionWS.z);
                return o;
            }

            // three.js wrote this material's colour as raw sRGB: decode it to linear (sRGBToLinear).
            float3 ToLinear(float3 c)
            {
                return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c));
            }

            float4 frag(Varyings i) : SV_Target
            {
                float wave = 0.5 + 0.5 * sin(_PL_Time * 1.6 + i.xz.x * 0.55 + i.xz.y * 0.35);
                float ripple = 0.5 + 0.5 * sin(_PL_Time * 2.3 - i.xz.x * 1.7 + i.xz.y * 1.1);
                float3 c = lerp(float3(0.42, 0.70, 0.88), float3(0.25, 0.50, 0.76), i.deep);
                c += 0.05 * wave + 0.03 * ripple;
                c = lerp(c, c * float3(0.28, 0.34, 0.55), _PL_Night);
                return float4(ToLinear(saturate(c)), 1.0);
            }
            ENDHLSL
        }
    }
}
