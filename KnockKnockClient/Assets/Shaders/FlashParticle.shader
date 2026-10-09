Shader "Custom/FlashParticle"
{
    Properties
    {
        _BaseColor    ("Base Color", Color)              = (1, 1, 0.9, 1)
        _BaseMap      ("Texture", 2D)                    = "white" {}
        _Intensity    ("Core Brightness", Range(1, 8))   = 4.0
        _CoreRadius   ("Core Radius",     Range(0, 0.5)) = 0.12
        _RaySharpness ("Ray Sharpness",   Range(1, 20))  = 6.0
        _RayLength    ("Ray Length",      Range(0, 1))   = 0.9
        _RayIntensity ("Ray Intensity",   Range(0, 2))   = 0.8
        _AlphaCutoff  ("Alpha Cutoff",    Range(0, 1))   = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent+1"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FlashParticle"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
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
                float  _Intensity;
                float  _CoreRadius;
                float  _RaySharpness;
                float  _RayLength;
                float  _RayIntensity;
                float  _AlphaCutoff;
            CBUFFER_END

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

                float2 p    = input.uv - 0.5;
                float  dist = length(p);

                // Soft central glow — the main bright core
                float core = 1.0 - smoothstep(0.0, _CoreRadius, dist);
                core = pow(max(core, 0.0), 0.4); // gentle falloff so glow spreads wide

                // Two crossing rays: horizontal (small |y|) and vertical (small |x|)
                // exp gives a smooth streak that fades from the axis outward
                float rayFade = saturate(1.0 - dist / (0.5 * _RayLength));
                float rayH    = exp(-abs(p.y) * _RaySharpness * 8.0) * rayFade;
                float rayV    = exp(-abs(p.x) * _RaySharpness * 8.0) * rayFade;
                float rays    = saturate(max(rayH, rayV)) * _RayIntensity;

                float alpha = saturate(core + rays);

                // Bright core — additive blend makes it blow out to white at center
                float brightness = lerp(_Intensity, 1.0, smoothstep(0.0, _CoreRadius * 2.0, dist));
                col.rgb *= brightness;
                col.a   *= alpha;

                clip(col.a - _AlphaCutoff);
                return col;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Particles/Unlit"
}
