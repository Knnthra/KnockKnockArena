// The death voxels: opaque little cubes carrying their colour as the particle
// vertex colour, with a fixed-direction fake lambert so each cube reads as a 3D
// block instead of a flat sprite. Deliberately opaque — the effect ends by the
// cubes SHRINKING to nothing (size over lifetime), not by alpha fading, so it
// needs no transparency sorting at all.
Shader "Custom/VoxelDeath"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "Forward"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float4 color      : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Two-step cel shade off a fixed light direction: enough to make the
                // cube faces separate, cheap enough to not care about the count.
                half3 normalWS = normalize(input.normalWS);
                half lambert = saturate(dot(normalWS, normalize(half3(0.4h, 0.8h, 0.25h))));
                half shade = lambert > 0.45h ? 1.0h : 0.72h;
                return half4(input.color.rgb * shade, 1.0h);
            }
            ENDHLSL
        }
    }
}
