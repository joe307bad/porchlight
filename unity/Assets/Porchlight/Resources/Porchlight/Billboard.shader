// Camera-facing squares: the lamp and car-light halos (Three's PointsMaterial,
// which is a screen-aligned square of a size in world terms here) and the
// players' name tags (SpriteMaterial). Each quad's four vertices share their
// centre (POSITION); TEXCOORD0 is the corner (0..1), TEXCOORD1 the size
// (width, height) in world units, COLOR the tint.

Shader "Porchlight/Billboard"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Map", 2D) = "white" {}
        _SrcBlend ("Src blend", Float) = 5
        _DstBlend ("Dst blend", Float) = 1
        _ZTest ("ZTest", Float) = 4
        _Scale ("Size scale", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            Cull Off
            ZTest [_ZTest]
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Three.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _MainTex_ST;
                float _Scale;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 corner : TEXCOORD0;
                float2 size : TEXCOORD1;
                float4 color : COLOR;
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
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float4 view = mul(UNITY_MATRIX_V, float4(positionWS, 1.0));
                view.xy += (input.corner - 0.5) * input.size * _Scale;
                o.positionCS = mul(UNITY_MATRIX_P, view);
                o.uv = input.corner;
                o.color = _Color * input.color;
                o.viewDepth = -view.z;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                c.rgb = PL_ApplyFog(c.rgb, i.viewDepth);
                return c;
            }
            ENDHLSL
        }
    }
}
