// Lit surfaces as Three draws them: MeshLambertMaterial (vertex-coloured ground,
// roads, walls, posts) or MeshStandardMaterial (_STANDARD: the Kenney kits,
// people, facades), optionally textured, instance-coloured, clipped to stand on
// the modelled ground floors (_CLIPGROUND) and with night-time window glow
// (_WINDOWS). The ShadowCaster-style pass writes linear depth for Shadows.cs.

Shader "Porchlight/Lit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Map", 2D) = "white" {}
        _Roughness ("Roughness", Float) = 1
        _Emissive ("Emissive", Color) = (0, 0, 0, 0)
        _ReceiveShadow ("Receive shadow", Float) = 1
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
            #pragma multi_compile_local _ _STANDARD
            #pragma multi_compile_local _ _WINDOWS
            #pragma multi_compile_local _ _CLIPGROUND

            #include "Three.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _MainTex_ST;
                float _Roughness;
                float4 _Emissive;
                float _ReceiveShadow;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            UNITY_INSTANCING_BUFFER_START(PLProps)
                UNITY_DEFINE_INSTANCED_PROP(float4, _InstColor)
            UNITY_INSTANCING_BUFFER_END(PLProps)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float2 windowSeed : TEXCOORD1; // x: pane seed, y: across-the-wall axis
                float4 windowGrid : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float4 color : TEXCOORD3;
                #if defined(_WINDOWS)
                float4 window : TEXCOORD4;  // seed, local.xy, litUp
                float4 grid : TEXCOORD5;
                float3 building : TEXCOORD6;
                #endif
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionWS = positionWS;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                float4 c = float4(1, 1, 1, 1);
                #if defined(_VERTEXCOLOR)
                c *= float4(input.color.rgb, 1);
                #endif
                #if defined(_INSTCOLOR)
                c *= UNITY_ACCESS_INSTANCED_PROP(PLProps, _InstColor);
                #endif
                o.color = c;
                #if defined(_WINDOWS)
                // Three's model-space height and across-the-wall position (its z is ours negated).
                float across = input.windowSeed.y > 0.5 ? -input.positionOS.z : input.positionOS.x;
                o.window = float4(input.windowSeed.x, input.positionOS.y, across, abs(o.normalWS.y));
                o.grid = input.windowGrid;
                float3 origin = TransformObjectToWorld(float3(0, 0, 0));
                o.building = float3(origin.x, origin.y, -origin.z);
                #endif
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                #if defined(_CLIPGROUND)
                clip(i.positionWS.y - 0.8);
                #endif
                float4 diffuse = _Color * i.color;
                #if defined(_MAP)
                diffuse *= SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                #endif
                float3 n = normalize(i.normalWS);
                float shadow = _ReceiveShadow > 0.5 ? PL_Shadow(i.positionWS, n, i.positionCS.xy) : 1.0;
                #if defined(_STANDARD)
                float3 color = PL_Standard(diffuse.rgb, _Roughness, n, i.positionWS, shadow);
                #else
                float3 color = PL_Lambert(diffuse.rgb, n, i.positionWS, shadow);
                #endif
                float3 emissive = _Emissive.rgb;
                #if defined(_WINDOWS)
                float3 litPos = float3(i.positionWS.x, i.positionWS.y, -i.positionWS.z);
                emissive += PL_WindowGlow(i.window.x, i.grid, i.window.yz, litPos, i.building, i.window.w);
                #endif
                color += emissive;
                color = PL_ApplyFog(color, PL_ViewDepth(i.positionWS));
                return float4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "PorchlightDepth"
            Tags { "LightMode" = "PorchlightDepth" }
            Cull Front
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ _CLIPGROUND

            #include "Three.hlsl"

            float4x4 _PL_LightView; // world -> light space, depth along +z from the light
            float4x4 _PL_LightClip; // world -> the shadow map's clip space (GPU-ready, render-into-texture)
            float4 _PL_LightRange;  // x: near, y: far

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionWS = positionWS;
                o.positionCS = mul(_PL_LightClip, float4(positionWS, 1.0));
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                #if defined(_CLIPGROUND)
                clip(i.positionWS.y - 0.8);
                #endif
                float z = mul(_PL_LightView, float4(i.positionWS, 1.0)).z;
                return float4((z - _PL_LightRange.x) / (_PL_LightRange.y - _PL_LightRange.x), 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
