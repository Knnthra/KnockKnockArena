Shader "Custom/CelShadedParticle"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.75, 0.7, 0.6, 0.8)
        _BaseMap ("Texture", 2D) = "white" {}

        [Header(Cel Style)]
        _AlphaSteps ("Alpha Steps", Range(1, 5)) = 2
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.1

        [Header(Outline Ring)]
        _OutlineColor ("Outline Color", Color) = (0.15, 0.12, 0.1, 1)
        _OutlineWidth ("Outline Width", Range(0, 0.3)) = 0.08
        _OutlineThreshold ("Outline Start", Range(0, 1)) = 0.3

        [Header(Shape)]
        [Toggle] _SoftMode ("Soft Mode (smoke/clouds)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "CelParticle"
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
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _AlphaSteps;
                float _AlphaCutoff;
                float4 _OutlineColor;
                float _OutlineWidth;
                float _OutlineThreshold;
                float _SoftMode;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                // Combine base color with vertex color (particle system color/alpha)
                half4 col = _BaseColor * input.color * texColor;

                // Distance from center of particle (UVs go 0-1, center is 0.5)
                float2 centered = input.uv - 0.5;
                float dist = length(centered) * 2.0; // 0 at center, 1 at edge

                // Soft circular shape
                float circleAlpha = saturate(1.0 - dist);

                // Step the alpha for cel-shaded hard edges
                float stepped = floor(circleAlpha * _AlphaSteps) / _AlphaSteps;

                // Soft mode: smooth radial falloff instead of hard circle (for smoke/clouds)
                float softAlpha = circleAlpha * circleAlpha;
                float finalAlpha = lerp(stepped, softAlpha, _SoftMode);

                // Dark outline ring at the edge (only in hard mode)
                float outerEdge = step(_OutlineThreshold, circleAlpha);
                float innerEdge = step(_OutlineThreshold + _OutlineWidth, circleAlpha);
                float outlineRing = (outerEdge - innerEdge) * (1.0 - _SoftMode);

                // Mix outline color into the ring area
                col.rgb = lerp(col.rgb, _OutlineColor.rgb, outlineRing * _OutlineColor.a);

                // Final alpha: stepped alpha with cutoff
                col.a *= finalAlpha;
                clip(col.a - _AlphaCutoff);

                return col;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Particles/Unlit"
}
