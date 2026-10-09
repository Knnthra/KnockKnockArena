Shader "Custom/BulletGlow"
{
    Properties
    {
        _GlowColor   ("Glow Color",   Color) = (1, 0.4, 0, 1)
        _CoreColor   ("Core Color",   Color) = (1, 0.95, 0.7, 1)
        _Intensity   ("Intensity",    Float) = 6.0
        _FresnelPower("Fresnel Power",Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
        }

        Pass
        {
            Name "BulletGlowPass"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _GlowColor;
                float4 _CoreColor;
                float  _Intensity;
                float  _FresnelPower;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _GlowColor)
                UNITY_DOTS_INSTANCED_PROP(float4, _CoreColor)
                UNITY_DOTS_INSTANCED_PROP(float,  _Intensity)
                UNITY_DOTS_INSTANCED_PROP(float,  _FresnelPower)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewDirWS  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = pos.positionCS;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS  = GetWorldSpaceViewDir(pos.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 N = normalize(IN.normalWS);
                float3 V = normalize(IN.viewDirWS);

                float fresnel = pow(1.0 - saturate(dot(N, V)), _FresnelPower);
                float3 color  = lerp(_CoreColor.rgb, _GlowColor.rgb, fresnel) * _Intensity;
                float  alpha  = 1.0 - fresnel * 0.4;

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
