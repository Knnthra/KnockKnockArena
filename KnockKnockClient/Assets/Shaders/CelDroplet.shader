Shader "Custom/CelDroplet"
{
    Properties
    {
        _BaseColor     ("Base Color", Color)              = (0.9, 0.1, 0.2, 1)
        _BaseMap       ("Texture", 2D)                    = "white" {}
        _OutlineColor  ("Outline Color", Color)           = (0, 0, 0, 1)
        _OutlineWidth  ("Outline Width", Range(0, 0.3))   = 0.08
        _AlphaCutoff   ("Alpha Cutoff", Range(0, 1))      = 0.1

        [Header(Shape)]
        [Toggle] _SquareMode ("Square Shape", Float) = 0
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
            Name "CelDroplet"
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
                float4 _OutlineColor;
                float  _OutlineWidth;
                float  _AlphaCutoff;
                float  _SquareMode;
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

                half4 fill    = _BaseColor * input.color * texColor;
                half4 outline = half4(_OutlineColor.rgb, fill.a);

                // Circle mode: distance from center
                float circleDist = length(input.uv - 0.5) * 2.0;
                clip(1.0 - circleDist - _AlphaCutoff);
                float circleOutline = step(1.0 - circleDist, _OutlineWidth * 2.0);

                // Square mode: distance from nearest edge
                float2 edgeDist = 0.5 - abs(input.uv - 0.5);
                float minEdge = min(edgeDist.x, edgeDist.y);
                float squareOutline = step(minEdge, _OutlineWidth);

                float isOutline = lerp(circleOutline, squareOutline, _SquareMode);

                return lerp(fill, outline, isOutline);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Particles/Unlit"
}
