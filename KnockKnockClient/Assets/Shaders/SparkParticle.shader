Shader "Custom/SparkParticle"
{
    Properties
    {
        _BaseColor   ("Base Color", Color)           = (1, 0.9, 0.1, 1)
        _BaseMap     ("Texture", 2D)                 = "white" {}
        _Intensity   ("Brightness", Range(1, 5))     = 3.0
        _Sharpness   ("Sharpness", Range(1, 8))      = 4.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1))   = 0.05
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
            Name "SparkParticle"
            Tags { "LightMode" = "UniversalForward" }

            // Additive so sparks brighten the scene and glow
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
                float  _Sharpness;
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

                float2 centered = input.uv - 0.5;
                float  dist     = length(centered) * 2.0;

                // Sharp bright core that falls off quickly — tight hot dot
                float alpha      = 1.0 - smoothstep(0.0, 1.0, dist);
                alpha            = pow(max(alpha, 0.0), _Sharpness);

                // Intense bright center
                float brightness = lerp(_Intensity, 1.0, smoothstep(0.0, 0.4, dist));
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
