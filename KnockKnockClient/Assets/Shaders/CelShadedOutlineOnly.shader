Shader "Custom/CelShadedOutlineOnly"
{
    Properties
    {
        [Header(Outline)]
        _OutlineWidth ("Outline Width", Range(0, 0.1)) = 0.01
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _Alpha ("Alpha", Range(0, 1)) = 1.0

        [Header(Stencil)]
        // Must match the Outline Group Bit of the body material(s) this
        // outline belongs to (body = Bit7, hands = Bit6, etc.).
        [Enum(Bit1, 1, Bit2, 2, Bit3, 4, Bit4, 8, Bit5, 16, Bit6, 32, Bit7, 64, Bit8, 128)]
        _StencilBit ("Outline Group Bit", Float) = 64

        [Header(Inner Outline Depth Gap)]
        // Applies ONLY to outline drawn on top of this group's own body
        // (same stencil bit): own arm over torso, one character over
        // another. Silhouette outline against ground/props/other groups
        // uses NO depth gap and is unaffected by these settings.
        // Requires scene depth + normals (e.g. SSAO Depth Normals prepass
        // or Depth Texture in the URP Asset).
        _DepthGap ("Min Depth Gap", Range(0.01, 1.0)) = 0.1
        _GrazingBoost ("Grazing Angle Boost", Range(1, 20)) = 6

        [Header(Circular Cutout)]
        _CutoutRadius ("Cutout Radius", Float) = 2.0
        _EdgeSoftness ("Edge Softness", Float) = 0.5
        _CutoutAspect ("Cutout Aspect Ratio", Vector) = (1, 1, 0, 0)

        [Header(Pixel Build Up)]
        _BuildUpProgress ("Build Up Progress", Range(0, 1)) = 1
        _PixelSize ("Pixel Size (px)", Range(1, 64)) = 6
        _BuildUpScatter ("Edge Scatter", Range(0, 1)) = 0.1
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
            // Queue 2501: all opaques (all groups' bodies) have rendered
            // and written their stencil bits; depth/normals textures are
            // ready. Stencil buffer is still intact.
            "Queue" = "Transparent-499"
        }

        // NOTE ON PASS TAGS: Entities Graphics / BatchRendererGroup draws
        // only ONE pass per LightMode tag. The two passes below therefore
        // use two DIFFERENT tags that URP's transparent object pass both
        // accepts (SRPDefaultUnlit and UniversalForwardOnly), so both
        // passes render even through Hybrid batches.

        // ------------------------------------------------------------------
        // PASS 1: Silhouette outline. Draws only where this group's own
        // stencil bit is NOT set: background, ground, props, other stencil
        // groups (e.g. hands over body). No depth-gap test, so the outline
        // stays complete even where the character touches the ground or a
        // wall, and hand outlines show over the chest at any distance.
        // ------------------------------------------------------------------
        Pass
        {
            Name "OutlineSilhouette"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Off
            ZWrite On
            ZTest [_ZTest]
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref [_StencilBit]
                ReadMask [_StencilBit]
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment SilhouetteFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS          : SV_POSITION;
                float3 positionWS          : TEXCOORD0;
                float3 originalPositionWS  : TEXCOORD1;
                float4 screenPos           : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float4 _OutlineColor;
                float _Alpha;
                float _StencilBit;
                float _DepthGap;
                float _GrazingBoost;
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float , _OutlineWidth)
                UNITY_DOTS_INSTANCED_PROP(float4, _OutlineColor)
                UNITY_DOTS_INSTANCED_PROP(float , _Alpha)
                UNITY_DOTS_INSTANCED_PROP(float , _DepthGap)
                UNITY_DOTS_INSTANCED_PROP(float , _GrazingBoost)
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

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalOS = input.tangentOS.w == 0 ? input.tangentOS.xyz : input.normalOS;
                float3 normalWS = TransformObjectToWorldNormal(normalOS);

                output.originalPositionWS = positionWS;

                float3 viewDir = normalize(positionWS - _WorldSpaceCameraPos.xyz);
                float3 extrudeDir = normalize(normalWS - viewDir * dot(normalWS, viewDir) * 0.5);
                positionWS += extrudeDir * _OutlineWidth;
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.screenPos  = ComputeScreenPos(output.positionCS);

                return output;
            }

            float OutlineCommonAlpha(Varyings input)
            {
                // Build-up discard using original (pre-extrusion) world Y
                float2 screenPixels = (input.screenPos.xy / input.screenPos.w) * _ScreenParams.xy;
                float2 blockCoord   = floor(screenPixels / _PixelSize);
                float  buildRandom  = hash21(blockCoord);
                float  heightFactor = saturate((input.originalPositionWS.y - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) return 0.0;

                float cutoutAlpha = 1.0;
                for(int i = 0; i < _CutoutCount && i < 4; i++)
                {
                    if(_CutoutPositions[i].w > 0.5)
                    {
                        float3 cutoutPos = _CutoutPositions[i].xyz;
                        float3 cameraForward = normalize(_CameraForward);
                        float3 toPixel = input.originalPositionWS - cutoutPos;
                        float alongRay = dot(toPixel, cameraForward);
                        float3 projectedOnRay = cutoutPos + cameraForward * alongRay;
                        float2 perpendicular = float2(input.originalPositionWS.x - projectedOnRay.x, input.originalPositionWS.z - projectedOnRay.z);
                        perpendicular *= _CutoutAspect.xy;
                        float dist = length(perpendicular);
                        if(dist < _CutoutRadius)
                        {
                            return 0.0;
                        }
                        else if(dist < _CutoutRadius + _EdgeSoftness)
                        {
                            float edgeFade = smoothstep(_CutoutRadius, _CutoutRadius + _EdgeSoftness, dist);
                            cutoutAlpha = min(cutoutAlpha, edgeFade);
                        }
                    }
                }

                return _OutlineColor.a * _Alpha * cutoutAlpha;
            }

            half4 SilhouetteFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float finalAlpha = OutlineCommonAlpha(input);
                if(finalAlpha < 0.01) discard;

                return half4(_OutlineColor.rgb, finalAlpha);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // PASS 2: Inner outline. Draws only where this group's own stencil
        // bit IS set (on top of its own / same-bit bodies), gated by the
        // grazing-aware depth-gap test: own arm over torso and one
        // character over another get outlines; hair on the face and eye
        // sockets stay clean.
        // ------------------------------------------------------------------
        Pass
        {
            Name "OutlineInner"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Off
            ZWrite On
            ZTest [_ZTest]
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref [_StencilBit]
                ReadMask [_StencilBit]
                Comp Equal
            }

            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment InnerFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS          : SV_POSITION;
                float3 positionWS          : TEXCOORD0;
                float3 originalPositionWS  : TEXCOORD1;
                float4 screenPos           : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float4 _OutlineColor;
                float _Alpha;
                float _StencilBit;
                float _DepthGap;
                float _GrazingBoost;
                float _CutoutRadius;
                float _EdgeSoftness;
                float4 _CutoutAspect;
                float _BuildUpProgress;
                float _PixelSize;
                float _BuildUpScatter;
                float _WorldMinY;
                float _WorldMaxY;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float , _OutlineWidth)
                UNITY_DOTS_INSTANCED_PROP(float4, _OutlineColor)
                UNITY_DOTS_INSTANCED_PROP(float , _Alpha)
                UNITY_DOTS_INSTANCED_PROP(float , _DepthGap)
                UNITY_DOTS_INSTANCED_PROP(float , _GrazingBoost)
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

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalOS = input.tangentOS.w == 0 ? input.tangentOS.xyz : input.normalOS;
                float3 normalWS = TransformObjectToWorldNormal(normalOS);

                output.originalPositionWS = positionWS;

                float3 viewDir = normalize(positionWS - _WorldSpaceCameraPos.xyz);
                float3 extrudeDir = normalize(normalWS - viewDir * dot(normalWS, viewDir) * 0.5);
                positionWS += extrudeDir * _OutlineWidth;
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.screenPos  = ComputeScreenPos(output.positionCS);

                return output;
            }

            float OutlineCommonAlpha(Varyings input)
            {
                float2 screenPixels = (input.screenPos.xy / input.screenPos.w) * _ScreenParams.xy;
                float2 blockCoord   = floor(screenPixels / _PixelSize);
                float  buildRandom  = hash21(blockCoord);
                float  heightFactor = saturate((input.originalPositionWS.y - _WorldMinY) / max(_WorldMaxY - _WorldMinY, 0.001));
                float  revealValue  = lerp(heightFactor, buildRandom, _BuildUpScatter);
                if (revealValue > _BuildUpProgress) return 0.0;

                float cutoutAlpha = 1.0;
                for(int i = 0; i < _CutoutCount && i < 4; i++)
                {
                    if(_CutoutPositions[i].w > 0.5)
                    {
                        float3 cutoutPos = _CutoutPositions[i].xyz;
                        float3 cameraForward = normalize(_CameraForward);
                        float3 toPixel = input.originalPositionWS - cutoutPos;
                        float alongRay = dot(toPixel, cameraForward);
                        float3 projectedOnRay = cutoutPos + cameraForward * alongRay;
                        float2 perpendicular = float2(input.originalPositionWS.x - projectedOnRay.x, input.originalPositionWS.z - projectedOnRay.z);
                        perpendicular *= _CutoutAspect.xy;
                        float dist = length(perpendicular);
                        if(dist < _CutoutRadius)
                        {
                            return 0.0;
                        }
                        else if(dist < _CutoutRadius + _EdgeSoftness)
                        {
                            float edgeFade = smoothstep(_CutoutRadius, _CutoutRadius + _EdgeSoftness, dist);
                            cutoutAlpha = min(cutoutAlpha, edgeFade);
                        }
                    }
                }

                return _OutlineColor.a * _Alpha * cutoutAlpha;
            }

            half4 InnerFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // --- Grazing-aware depth gap test (own-body pixels only) ---
                float2 uvSS = input.screenPos.xy / input.screenPos.w;
                float rawSceneDepth = SampleSceneDepth(uvSS);
                float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                float fragEyeDepth  = input.screenPos.w; // perspective camera: clip w == eye depth

                float3 sceneNormalWS = SampleSceneNormals(uvSS);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float ndv = saturate(abs(dot(sceneNormalWS, viewDirWS)));
                float adaptiveGap = _DepthGap / max(ndv, 1.0 / _GrazingBoost);

                if (sceneEyeDepth - fragEyeDepth < adaptiveGap) discard;

                float finalAlpha = OutlineCommonAlpha(input);
                if(finalAlpha < 0.01) discard;

                return half4(_OutlineColor.rgb, finalAlpha);
            }
            ENDHLSL
        }

        // Explicit empty ShadowCaster: prevents stale shadow atlas entries
        // when outline objects are enabled/disabled at runtime.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                discard;
                return 0;
            }
            ENDHLSL
        }
    }
}
