// See-through flat shading for the terrain builder's shapes, so the ground and other shapes show
// through them. Like Porchlight/Flat Vertex Color, but blended by the colour's alpha (vertex colour
// times _Tint), with more ambient light so the shapes stay bright. It doesn't write depth, so
// overlapping shapes are drawn back to front by Unity's transparent sorting.
// The first SubShader is for URP; the second is a fallback for the built-in pipeline.
// It lives in Resources/ so Resources.Load keeps it in player builds.
Shader "Porchlight/Glass"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 0.5)
        _Ambient ("Ambient", Color) = (0.62, 0.64, 0.68, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half4 _Ambient;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float3 normalWS : TEXCOORD0;
                half fogFactor : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color * _Tint;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                Light light = GetMainLight();
                half ndl = saturate(dot(normalize(input.normalWS), light.direction));
                half3 color = input.color.rgb * (light.color * ndl * 0.6 + _Ambient.rgb);
                color = MixFog(color, input.fogFactor);
                return half4(color, input.color.a);
            }
            ENDHLSL
        }
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            fixed4 _Tint;
            fixed4 _Ambient;

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float3 normalWS : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.pos = UnityObjectToClipPos(input.vertex);
                output.normalWS = UnityObjectToWorldNormal(input.normal);
                output.color = input.color * _Tint;
                UNITY_TRANSFER_FOG(output, output.pos);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                half ndl = saturate(dot(normalize(input.normalWS), _WorldSpaceLightPos0.xyz));
                fixed4 color = fixed4(input.color.rgb * (_LightColor0.rgb * ndl * 0.6 + _Ambient.rgb), input.color.a);
                UNITY_APPLY_FOG(input.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }
}
