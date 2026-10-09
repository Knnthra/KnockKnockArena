// Cel-shaded GLASS: the body shader's look, rendered as a real transparent.
//
// Why CelShadedBody cannot do glass by lowering _Alpha: it blends, but it sits
// in the Geometry queue with ZWrite On (plus a DepthOnly prepass). Blending can
// only show what is ALREADY in the framebuffer — in the Geometry queue the
// scene behind the pane has not been drawn yet, so the pane blends against the
// sky, and its depth write then Z-rejects everything behind it afterwards.
// Transparency needs both halves at once: draw AFTER every opaque
// (Queue=Transparent) and write no depth (ZWrite Off).
//
// Trimmed relative to the body: no stencil (glass must not join an outline
// group), no ShadowCaster (glass throwing a full hard shadow reads as a wall),
// no DepthOnly/DepthNormals (writing the pane into the depth texture would put
// it back in front of everything for SSAO and co.), no pixel build-up.
// Property names match the body shader, so values copy straight over.
Shader "Custom/CelShadedGlass"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.7, 0.9, 1.0, 1)
        _BaseMap ("Base Texture", 2D) = "white" {}

        [Header(Cel Shading)]
        _CelBands ("Cel Bands", Range(2, 10)) = 3
        _CelSmoothness ("Band Smoothness", Range(0, 0.5)) = 0.05
        _ShadowColor ("Shadow Color", Color) = (0.6, 0.5, 0.4, 1)
        _ShadowThreshold ("Shadow Threshold", Range(0, 1)) = 0.5

        [Header(Rim Light)]
        _RimColor ("Rim Color", Color) = (1, 1, 1, 1)
        _RimPower ("Rim Power", Range(0.1, 8)) = 3
        _RimIntensity ("Rim Intensity", Range(0, 1)) = 0.3
        _RimThreshold ("Rim Threshold", Range(0, 1)) = 0.7
        _RimFadeStart ("Rim Fade Start Distance", Float) = 15.0
        _RimFadeEnd   ("Rim Fade End Distance",   Float) = 35.0

        [Header(Surface)]
        _AmbientIntensity ("Ambient Intensity", Range(0, 2)) = 1.2
        _Saturation ("Saturation", Range(0, 2)) = 1.0
        _Alpha ("Alpha", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual   // opaque depth still hides glass behind walls
            Cull Off       // a pane is visible from both sides

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_instancing
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float3 viewDirWS : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _CelBands;
                float _CelSmoothness;
                float4 _ShadowColor;
                float _ShadowThreshold;
                float4 _RimColor;
                float _RimPower;
                float _RimIntensity;
                float _RimThreshold;
                float _RimFadeStart;
                float _RimFadeEnd;
                float _AmbientIntensity;
                float _Saturation;
                float _Alpha;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.viewDirWS = GetWorldSpaceViewDir(positionInputs.positionWS);

                return output;
            }

            float CelShade(float value, float bands, float smoothness)
            {
                float stepped = floor(value * bands) / bands;
                return lerp(stepped, value, smoothness);
            }

            float3 AdjustSaturation(float3 color, float saturation)
            {
                float luminance = dot(color, float3(0.299, 0.587, 0.114));
                return lerp(float3(luminance, luminance, luminance), color, saturation);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 albedo = baseMap * _BaseColor;

                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(input.viewDirWS);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 lightDir = normalize(mainLight.direction);

                float NdotL = dot(normalWS, lightDir);
                float lightIntensity = saturate(NdotL);

                float distToCamera = length(input.positionWS - _WorldSpaceCameraPos.xyz);
                float distSmooth = saturate(distToCamera / _RimFadeEnd) * 0.5;
                float effectiveSmoothness = _CelSmoothness + distSmooth;

                float celLighting = CelShade(lightIntensity, _CelBands, effectiveSmoothness);

                float shadowStep = step(_ShadowThreshold, mainLight.shadowAttenuation);
                shadowStep = lerp(shadowStep, mainLight.shadowAttenuation, effectiveSmoothness * 2.0);

                float3 lightingColor = lerp(_ShadowColor.rgb, float3(1, 1, 1), celLighting);
                lightingColor = lerp(_ShadowColor.rgb, lightingColor, shadowStep);

                float3 diffuse = albedo.rgb * lightingColor * mainLight.color;

                // The rim doubles as the glass's edge sheen: grazing angles catch
                // light the way a pane's edges do.
                float rimDot = 1.0 - saturate(dot(viewDirWS, normalWS));
                float rimIntensity = pow(rimDot, _RimPower);
                float rimStep = step(_RimThreshold, rimIntensity);
                rimStep = lerp(rimStep, rimIntensity, effectiveSmoothness * 2.0);
                float rimFade = 1.0 - smoothstep(_RimFadeStart, _RimFadeEnd, distToCamera);
                float3 rimLight = rimStep * _RimIntensity * _RimColor.rgb * rimFade;

                float3 ambient = albedo.rgb * unity_SHAr.rgb * _AmbientIntensity;

                #ifdef _ADDITIONAL_LIGHTS
                uint pixelLightCount = GetAdditionalLightsCount();
                for (uint lightIndex = 0u; lightIndex < pixelLightCount; ++lightIndex)
                {
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    float3 addLightDir = normalize(light.direction);
                    float addNdotL = dot(normalWS, addLightDir);
                    float addLightIntensity = saturate(addNdotL);
                    float addCelLighting = CelShade(addLightIntensity, _CelBands, _CelSmoothness);
                    float addShadowStep = step(0.5, light.shadowAttenuation);
                    addShadowStep = lerp(addShadowStep, light.shadowAttenuation, _CelSmoothness * 2.0);
                    diffuse += albedo.rgb * light.color * addCelLighting * addShadowStep * light.distanceAttenuation;
                }
                #endif

                float3 finalColor = diffuse + ambient + rimLight;
                finalColor = AdjustSaturation(finalColor, _Saturation);

                return half4(finalColor, albedo.a * _Alpha);
            }
            ENDHLSL
        }
    }
}
