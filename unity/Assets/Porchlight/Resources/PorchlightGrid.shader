// The building grid: thin lines drawn over the town floor on one transparent quad. Lines are worked
// out per pixel from the world position, so they stay one or two pixels wide at any distance, and
// they fade out where they would get too dense to read. The first SubShader is for URP; the second is
// a fallback for the built-in pipeline. It lives in Resources/ so Resources.Load keeps it in builds.
Shader "Porchlight/Grid"
{
    Properties
    {
        _Color ("Line colour", Color) = (1, 1, 1, 0.28)
        _MajorColor ("Major line colour", Color) = (1, 1, 1, 0.55)
        _Spacing ("Spacing (m)", Float) = 4
        _MajorEvery ("Major line every", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }

        Pass
        {
            Name "Grid"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _MajorColor;
                float _Spacing;
                float _MajorEvery;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            // 1 on a line, 0 between lines, anti-aliased over about a pixel.
            float Lines(float2 p, out float density)
            {
                float2 perPixel = fwidth(p);
                float2 distance = abs(frac(p - 0.5) - 0.5) / max(perPixel, 1e-5);
                density = max(perPixel.x, perPixel.y);
                return 1 - saturate(min(distance.x, distance.y) - 0.25);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 p = input.positionWS.xz / _Spacing;
                float minorDensity, majorDensity;
                float minor = Lines(p, minorDensity);
                float major = Lines(p / _MajorEvery, majorDensity);

                // Fade a set of lines out once they're closer than a few pixels apart.
                minor *= saturate(1.5 - minorDensity * 6);
                major *= saturate(1.5 - majorDensity * 6);

                half4 color = _Color;
                color.a *= minor;
                if (major * _MajorColor.a > color.a) color = half4(_MajorColor.rgb, _MajorColor.a * major);
                return color;
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Offset -1, -1

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _MajorColor;
            float _Spacing;
            float _MajorEvery;

            struct Varyings { float4 pos : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vert(float4 vertex : POSITION)
            {
                Varyings output;
                output.positionWS = mul(unity_ObjectToWorld, vertex).xyz;
                output.pos = UnityObjectToClipPos(vertex);
                return output;
            }

            float Lines(float2 p, out float density)
            {
                float2 perPixel = fwidth(p);
                float2 distance = abs(frac(p - 0.5) - 0.5) / max(perPixel, 1e-5);
                density = max(perPixel.x, perPixel.y);
                return 1 - saturate(min(distance.x, distance.y) - 0.25);
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                float2 p = input.positionWS.xz / _Spacing;
                float minorDensity, majorDensity;
                float minor = Lines(p, minorDensity);
                float major = Lines(p / _MajorEvery, majorDensity);
                minor *= saturate(1.5 - minorDensity * 6);
                major *= saturate(1.5 - majorDensity * 6);

                fixed4 color = _Color;
                color.a *= minor;
                if (major * _MajorColor.a > color.a) color = fixed4(_MajorColor.rgb, _MajorColor.a * major);
                return color;
            }
            ENDCG
        }
    }
}
