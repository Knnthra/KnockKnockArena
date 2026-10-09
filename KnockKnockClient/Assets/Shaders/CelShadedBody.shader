Shader "Custom/CelShadedBody"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 0.8, 0.6, 1)
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
        _Alpha ("Alpha", Range(0, 1)) = 1.0

        [Header(Circular Cutout)]
        _CutoutRadius ("Cutout Radius", Float) = 2.0
        _EdgeSoftness ("Edge Softness", Float) = 0.5
        _CutoutAspect ("Cutout Aspect Ratio", Vector) = (1, 1, 0, 0)

        [Header(Stencil)]
        // Renderers sharing the same bit form one outline group: the
        // outline is drawn around the group's combined silhouette.
        // Give the hands a different bit than the body so the hand
        // outline can draw on top of the body.
        [Enum(Bit1, 1, Bit2, 2, Bit3, 4, Bit4, 8, Bit5, 16, Bit6, 32, Bit7, 64, Bit8, 128)]
        _StencilBit ("Outline Group Bit", Float) = 64

        [Header(Pixel Build Up)]
        _BuildUpProgress ("Build Up Progress", Range(0, 1)) = 1
        _PixelSize ("Pixel Size (px)", Range(1, 64)) = 6
        _BuildUpScatter ("Edge Scatter", Range(0, 1)) = 0.1
        _BuildUpEdgeGlow ("Build Up Edge Glow", Color) = (0, 2, 2, 1)
        _BuildUpEdgeWidth ("Build Up Edge Width", Range(0, 0.5)) = 0.15
        _WorldMinY ("World Min Y", Float) = 0
        _WorldMaxY ("World Max Y", Float) = 1

        [Header(Depth)]
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Z Test", Float) = 4
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest [_ZTest]
            Cull Off

            // Stamp this renderer's group bit on every covered pixel.
            // Read/Write masks confine us to that single bit so we
            // neither disturb nor react to stencil use by other
            // systems (deferred, decals, SSAO, other groups).
            Stencil
            {
                Ref [_StencilBit]
                ReadMask [_StencilBit]
                WriteMask [_StencilBit]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5
            // Canonical URP (Unity 6) keyword set — the main-light shadow keywords
            // MUST live on ONE line (they are mutually exclusive variants), and the
            // Forward+ cluster keyword must be present, or URP silently picks a
            // variant without shadow sampling when the global keywords don't match.
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
                float4 screenPos : TEXCOORD4;
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
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _StencilBit;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float4 _BuildUpEdgeGlow;
                float _BuildUpEdgeWidth;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                UNITY_DOTS_INSTANCED_PROP(float , _CelBands)
                UNITY_DOTS_INSTANCED_PROP(float , _CelSmoothness)
                UNITY_DOTS_INSTANCED_PROP(float4, _ShadowColor)
                UNITY_DOTS_INSTANCED_PROP(float , _ShadowThreshold)
                UNITY_DOTS_INSTANCED_PROP(float4, _RimColor)
                UNITY_DOTS_INSTANCED_PROP(float , _RimPower)
                UNITY_DOTS_INSTANCED_PROP(float , _RimIntensity)
                UNITY_DOTS_INSTANCED_PROP(float , _RimThreshold)
                UNITY_DOTS_INSTANCED_PROP(float , _RimFadeStart)
                UNITY_DOTS_INSTANCED_PROP(float , _RimFadeEnd)
                UNITY_DOTS_INSTANCED_PROP(float , _AmbientIntensity)
                UNITY_DOTS_INSTANCED_PROP(float , _Saturation)
                UNITY_DOTS_INSTANCED_PROP(float , _Alpha)
                UNITY_DOTS_INSTANCED_PROP(float , _CutoutRadius)
                UNITY_DOTS_INSTANCED_PROP(float , _EdgeSoftness)
                UNITY_DOTS_INSTANCED_PROP(float4, _CutoutAspect)
                UNITY_DOTS_INSTANCED_PROP(float , _BuildUpProgress)
                UNITY_DOTS_INSTANCED_PROP(float , _PixelSize)
                UNITY_DOTS_INSTANCED_PROP(float , _BuildUpScatter)
                UNITY_DOTS_INSTANCED_PROP(float , _WorldMinY)
                UNITY_DOTS_INSTANCED_PROP(float , _WorldMaxY)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #endif

            float4 _CutoutPositions[4];
            float _CutoutCount;
            float3 _CameraForward;

            float hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 74.27);
                return frac(p.x * p.y);
            }

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
                output.screenPos = ComputeScreenPos(positionInputs.positionCS);

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

                // --- Pixel build-up: bottom to top with scattered edge ---
                float2 screenPixels = (input.screenPos.xy / input.screenPos.w) * _ScreenParams.xy;
                float2 blockCoord   = floor(screenPixels / _PixelSize);
                float  buildRandom  = hash21(blockCoord);
                float  heightFactor = saturate((input.positionWS.y - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) discard;

                // Sample base texture
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 albedo = baseMap * _BaseColor;

                // Normalize vectors
                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(input.viewDirWS);

                // Get main light
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 lightDir = normalize(mainLight.direction);

                // Basic Lambert lighting
                float NdotL = dot(normalWS, lightDir);
                float lightIntensity = saturate(NdotL);

                // Distance-based smoothness
                float distToCamera = length(input.positionWS - _WorldSpaceCameraPos.xyz);
                float distSmooth = saturate(distToCamera / _RimFadeEnd) * 0.5;
                float effectiveSmoothness = _CelSmoothness + distSmooth;

                float celLighting = CelShade(lightIntensity, _CelBands, effectiveSmoothness);

                float shadowStep = step(_ShadowThreshold, mainLight.shadowAttenuation);
                shadowStep = lerp(shadowStep, mainLight.shadowAttenuation, effectiveSmoothness * 2.0);

                float3 lightingColor = lerp(_ShadowColor.rgb, float3(1, 1, 1), celLighting);
                lightingColor = lerp(_ShadowColor.rgb, lightingColor, shadowStep);

                float3 diffuse = albedo.rgb * lightingColor * mainLight.color;

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

                float cutoutAlpha = 1.0;
                for(int i = 0; i < _CutoutCount && i < 4; i++)
                {
                    if(_CutoutPositions[i].w > 0.5)
                    {
                        float3 cutoutPos = _CutoutPositions[i].xyz;
                        float3 cameraForward = normalize(_CameraForward);
                        float3 toPixel = input.positionWS - cutoutPos;
                        float alongRay = dot(toPixel, cameraForward);
                        float3 projectedOnRay = cutoutPos + cameraForward * alongRay;
                        float2 perpendicular = float2(input.positionWS.x - projectedOnRay.x, input.positionWS.z - projectedOnRay.z);
                        perpendicular *= _CutoutAspect.xy;
                        float dist = length(perpendicular);
                        if(dist < _CutoutRadius)
                        {
                            cutoutAlpha = 0.0;
                            break;
                        }
                        else if(dist < _CutoutRadius + _EdgeSoftness)
                        {
                            float edgeFade = smoothstep(_CutoutRadius, _CutoutRadius + _EdgeSoftness, dist);
                            cutoutAlpha = min(cutoutAlpha, edgeFade);
                        }
                    }
                }

                float finalAlpha = albedo.a * _Alpha * cutoutAlpha;
                if(finalAlpha < 0.01) discard;

                return half4(finalColor, finalAlpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  worldY     : TEXCOORD0;
            };

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
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _StencilBit;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float4 _BuildUpEdgeGlow;
                float _BuildUpEdgeWidth;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            float3 _LightDirection;

            float hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 74.27);
                return frac(p.x * p.y);
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                output.worldY = positionWS.y;
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                float2 blockCoord  = floor(input.positionCS.xy / _PixelSize);
                float  buildRandom = hash21(blockCoord);
                float  heightFactor = saturate((input.worldY - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) discard;
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _StencilBit;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float4 _BuildUpEdgeGlow;
                float _BuildUpEdgeWidth;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  worldY     : TEXCOORD0;
            };

            float hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 74.27);
                return frac(p.x * p.y);
            }

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.worldY = positionWS.y;
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target
            {
                float2 blockCoord   = floor(input.positionCS.xy / _PixelSize);
                float  buildRandom  = hash21(blockCoord);
                float  heightFactor = saturate((input.worldY - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) discard;
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _StencilBit;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float4 _BuildUpEdgeGlow;
                float _BuildUpEdgeWidth;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float  worldY     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 74.27);
                return frac(p.x * p.y);
            }

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.normalWS = normalInputs.normalWS;
                output.worldY = positionInputs.positionWS.y;

                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 blockCoord   = floor(input.positionCS.xy / _PixelSize);
                float  buildRandom  = hash21(blockCoord);
                float  heightFactor = saturate((input.worldY - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) discard;

                float3 normalWS = normalize(input.normalWS);
                return half4(normalWS, 0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
