Shader "Custom/FireParticle"
{
    Properties
    {
        _BaseColor     ("Base Color", Color)               = (1, 0.4, 0, 1)
        _BaseMap       ("Texture", 2D)                     = "white" {}

        [Header(Shape)]
        _NoiseScale    ("Noise Scale",    Range(1, 10))    = 3.0
        _NoisePower    ("Noise Distortion", Range(0, 0.4)) = 0.12
        _Falloff       ("Edge Falloff",   Range(0.5, 4))   = 1.2
        _AlphaCutoff   ("Alpha Cutoff",   Range(0, 1))     = 0.05

        [Header(Fire)]
        _CoreBrightness("Core Brightness", Range(1, 4))    = 2.0
        _CoreRadius    ("Core Radius",    Range(0, 1))     = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FireParticle"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _NoiseScale;
                float  _NoisePower;
                float  _Falloff;
                float  _AlphaCutoff;
                float  _CoreBrightness;
                float  _CoreRadius;
            CBUFFER_END

            float2 Hash2(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)),
                           dot(p, float2(269.5, 183.3)));
                return -1.0 + 2.0 * frac(sin(p) * 43758.5453);
            }

            float GradNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(dot(Hash2(i + float2(0,0)), f - float2(0,0)),
                         dot(Hash2(i + float2(1,0)), f - float2(1,0)), u.x),
                    lerp(dot(Hash2(i + float2(0,1)), f - float2(0,1)),
                         dot(Hash2(i + float2(1,1)), f - float2(1,1)), u.x),
                    u.y);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color      = input.color;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 col      = _BaseColor * input.color * texColor;

                // Noise distorts edges so they look like fire instead of circles
                float n = GradNoise(input.uv * _NoiseScale);
                float2 centered = input.uv - 0.5;
                float  dist     = length(centered + n * _NoisePower) * 2.0;

                float alpha = 1.0 - smoothstep(0.0, 1.0, dist);
                alpha = pow(max(alpha, 0.0), _Falloff);

                // Bright hot core — center is white/yellow, edges are orange
                float coreMask  = 1.0 - smoothstep(0.0, _CoreRadius, dist);
                col.rgb        += coreMask * _CoreBrightness * half3(1.0, 0.8, 0.3);

                col.a *= alpha;
                clip(col.a - _AlphaCutoff);
                return col;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Particles/Unlit"
}
