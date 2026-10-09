Shader "Custom/LowPolySkybox"
{
    Properties
    {
        [Header(Sky Colors)]
        _ZenithColor ("Zenith Color", Color) = (0.25, 0.5, 0.9, 1)
        _HorizonColor ("Horizon Color", Color) = (0.6, 0.82, 1.0, 1)
        _GroundColor ("Ground Color", Color) = (0.35, 0.55, 0.75, 1)
        _HorizonSharpness ("Horizon Sharpness", Range(0.5, 10)) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            Name "LowPolySkybox"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDir    : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _ZenithColor;
                half4 _HorizonColor;
                half4 _GroundColor;
                half  _HorizonSharpness;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.viewDir = IN.positionOS.xyz;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.viewDir);

                float y = dir.y;
                float horizonMask = pow(saturate(1.0 - abs(y)), _HorizonSharpness);
                float skyMask = saturate(y);

                half3 skyColor = lerp(_GroundColor.rgb, _ZenithColor.rgb, skyMask);
                skyColor = lerp(skyColor, _HorizonColor.rgb, horizonMask);

                return half4(skyColor, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
