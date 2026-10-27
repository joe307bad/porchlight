// MeshBasicMaterial: flat colour (times the vertex, instance and texture
// colour), with fog, and the blending, depth test and depth write each use
// asks for: opaque bulbs and the route line, additive light pools and river
// streaks, the orange x-ray (drawn only behind what's already there).

Shader "Porchlight/Unlit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Map", 2D) = "white" {}
        _SrcBlend ("Src blend", Float) = 1
        _DstBlend ("Dst blend", Float) = 0
        _Cull ("Cull", Float) = 2
        _ZTest ("ZTest", Float) = 4
        _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            Cull [_Cull]
            ZTest [_ZTest]
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ _MAP
            #pragma multi_compile_local _ _VERTEXCOLOR
            #pragma multi_compile_local _ _INSTCOLOR

            #include "Three.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _MainTex_ST;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            UNITY_INSTANCING_BUFFER_START(PLProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstColor)
            UNITY_INSTANCING_BUFFER_END(PLProps)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float viewDepth : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = input.uv;
                float4 c = _Color;
                #if defined(_VERTEXCOLOR)
                c *= float4(input.color.rgb, 1);
                #endif
                #if defined(_INSTCOLOR)
                c.rgb *= UNITY_ACCESS_INSTANCED_PROP(PLProps, _InstColor).rgb;
                #endif
                o.color = c;
                o.viewDepth = PL_ViewDepth(positionWS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 c = i.color;
                #if defined(_MAP)
                c *= SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                #endif
                c.rgb = PL_ApplyFog(c.rgb, i.viewDepth);
                return c;
            }
            ENDHLSL
        }
    }
}
