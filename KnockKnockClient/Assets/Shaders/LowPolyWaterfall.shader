Shader "Custom/LowPolyWaterfall"
{
    Properties
    {
        [Header(Colors)]
        _Transparency ("Transparency", Range(0, 1)) = 0.8
        _ShallowColor ("Shallow Color", Color) = (0.3, 0.8, 0.9, 1)
        _DeepColor ("Deep Color", Color) = (0.1, 0.3, 0.5, 1)
        _FoamColor ("Foam Color", Color) = (1, 1, 1, 1)

        [Header(Top Face Waves)]
        _WaveSpeed ("Wave Speed", Float) = 1
        _WaveScale ("Wave Scale", Float) = 0.1

        [Header(Shore Foam (top and sides))]
        _ShoreFoamAmount ("Shore Foam Amount", Float) = 1
        _ShoreFoamScale ("Shore Foam Scale", Float) = 1
        _ShoreFoamSpeed ("Shore Foam Speed", Float) = 1
        _ShoreFoamCutoff ("Shore Foam Cutoff", Float) = 0.5

        [Header(Top Face Flow Foam)]
        _FlowFoamAmount ("Flow Foam Amount", Range(0, 1)) = 0.5
        _FlowFoamDirection ("Flow Foam Direction", Range(0, 1)) = 0
        _FlowFoamSpeed ("Flow Foam Speed", Float) = 1
        _FlowFoamSize ("Flow Foam Size", Float) = 5

        [Header(Top Face Depth Fade)]
        _DepthFadeDistance ("Depth Fade Distance", Float) = 5
        _DepthFadeFalloff ("Depth Fade Falloff", Range(0.1, 5)) = 1

        [Header(Waterfall Sides)]
        _FallSpeed ("Fall Speed", Float) = 2.0
        _FallStreamAmount ("Fall Stream Amount", Range(0, 1)) = 0.5
        _FallStreamSize ("Fall Stream Size", Float) = 1.0
        _TopFoamRange ("Top Foam Range", Float) = 0.3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float fogCoord : TEXCOORD3;
                float3 positionOS : TEXCOORD4;
                float3 normalOS : TEXCOORD5;
            };

            CBUFFER_START(UnityPerMaterial)
                half _Transparency;
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FoamColor;
                half _WaveSpeed;
                half _WaveScale;
                half _ShoreFoamAmount;
                half _ShoreFoamScale;
                half _ShoreFoamSpeed;
                half _ShoreFoamCutoff;
                half _FlowFoamAmount;
                half _FlowFoamDirection;
                half _FlowFoamSpeed;
                half _FlowFoamSize;
                half _DepthFadeDistance;
                half _DepthFadeFalloff;
                half _FallSpeed;
                half _FallStreamAmount;
                half _FallStreamSize;
                half _TopFoamRange;
            CBUFFER_END

            // Cell hash — square pixel-art edges, precision-safe at large coordinates
            float Hash2D(float2 cell)
            {
                cell  = fmod(cell, 289.0);
                cell  = frac(cell * float2(0.1031, 0.1030));
                cell += dot(cell, cell.yx + 33.33);
                return frac((cell.x + cell.y) * cell.x);
            }

            float2 GetFlowDirection(float direction)
            {
                float angle = (direction * 2.0 - 1.0) * PI;
                return normalize(float2(cos(angle), sin(angle)));
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                // No vertex displacement — top and sides share edges; displacing only one creates a seam.
                // The top face water effect is achieved entirely through the fragment shader.

                output.positionOS = input.positionOS.xyz;
                output.normalOS = input.normalOS;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv;
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.fogCoord = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 finalColor;

                if (input.normalOS.y > 0.5)
                {
                    // ===== TOP FACE: same logic as LowPolyWater =====

                    float2 screenUV = input.screenPos.xy / input.screenPos.w;
                    float screenPosA = input.screenPos.w;

                    float sceneDepthRaw = SampleSceneDepth(screenUV);
                    float sceneEyeDepth = LinearEyeDepth(sceneDepthRaw, _ZBufferParams);

                    float3 viewDir = input.positionWS - _WorldSpaceCameraPos;
                    float3 worldPosScene = _WorldSpaceCameraPos + viewDir * (sceneEyeDepth / screenPosA);
                    float verticalDepth = max(0, input.positionWS.y - worldPosScene.y);
                    float depthFade = pow(saturate(verticalDepth / _DepthFadeDistance), _DepthFadeFalloff);
                    half4 waterColor = lerp(_ShallowColor, _DeepColor, depthFade);

                    float edgeMask = saturate((sceneEyeDepth - screenPosA) / _ShoreFoamAmount) * _ShoreFoamCutoff;
                    float2 shoreFoamCoord = input.positionWS.xz * _ShoreFoamScale + float2(_Time.y * _ShoreFoamSpeed, 0);
                    float edgeFoam = step(edgeMask, Hash2D(floor(shoreFoamCoord)));

                    float2 flowDir = GetFlowDirection(_FlowFoamDirection);
                    float2 flowCoord = input.positionWS.xz / _FlowFoamSize + flowDir * (_Time.y * _FlowFoamSpeed);
                    float flowFoam = step(1.0 - _FlowFoamAmount, Hash2D(floor(flowCoord)));

                    finalColor = lerp(waterColor, _FoamColor, saturate(edgeFoam + flowFoam));
                    finalColor.a = _Transparency;
                }
                else if (abs(input.normalOS.y) < 0.5)
                {
                    // ===== SIDE FACES: waterfall =====
                    float fallTime = _Time.y * _FallSpeed;

                    // Compute the horizontal position along this specific face.
                    // cross(up, faceNormal) gives the face's right-tangent direction,
                    // which is correct for all four axis-aligned sides.
                    float3 faceRight = normalize(cross(float3(0, 1, 0), input.normalOS));
                    float faceU = dot(input.positionWS, faceRight); // position along face width

                    // Distance from the top of the cube in object space (0 = at top edge, grows downward)
                    float distFromTop = 0.5 - input.positionOS.y;

                    // Foam bias: 1 at the top edge, fades to 0 over _TopFoamRange distance.
                    // This is used to make the streams denser at the top, so the spill foam
                    // visually feeds into the falling streams rather than being a separate layer.
                    float topFoamBias = saturate(1.0 - distFromTop / _TopFoamRange);

                    // Primary streams: tight horizontal scale, elongated vertical → tall thin streams.
                    // Threshold drops near the top (dense white) and rises to normal density below.
                    float2 streamCoord = float2(faceU * 4.0, input.positionWS.y * 0.35 + fallTime) / _FallStreamSize;
                    float streamThreshold = lerp(1.0 - _FallStreamAmount, 0.02, topFoamBias);
                    float streamFoam = step(streamThreshold, Hash2D(floor(streamCoord)));

                    // Secondary turbulence: wider, slower — also controlled by _FallStreamAmount
                    float2 turbCoord = float2(faceU * 1.5, input.positionWS.y * 0.25 + fallTime * 0.5) / _FallStreamSize;
                    float turbBaseThreshold = 1.0 - _FallStreamAmount * 0.8; // slightly sparser than primary
                    float turbThreshold = lerp(turbBaseThreshold, 0.1, topFoamBias);
                    float turbFoam = step(turbThreshold, Hash2D(floor(turbCoord))) * lerp(0.5, 1.0, topFoamBias);

                    // Object intersection foam — same depth technique as top-face shore foam.
                    // Where a world object pokes into the waterfall face, sceneEyeDepth ≈ screenPosA
                    // and edgeMask approaches 0, letting foam cells through.
                    float2 screenUV  = input.screenPos.xy / input.screenPos.w;
                    float screenPosA = input.screenPos.w;
                    float sceneDepthRaw  = SampleSceneDepth(screenUV);
                    float sceneEyeDepth  = LinearEyeDepth(sceneDepthRaw, _ZBufferParams);
                    float edgeMask       = saturate((sceneEyeDepth - screenPosA) / _ShoreFoamAmount) * _ShoreFoamCutoff;
                    float2 edgeFoamCoord = float2(faceU, input.positionWS.y) * _ShoreFoamScale
                                          + float2(_Time.y * _ShoreFoamSpeed, 0);
                    float edgeFoam = step(edgeMask, Hash2D(floor(edgeFoamCoord)));

                    float totalFoam = saturate(streamFoam + turbFoam + edgeFoam);

                    // Colour: shallow (lighter) at top, deep (darker) at bottom
                    float yFade = saturate(0.5 - input.positionOS.y);
                    half4 waterColor = lerp(_ShallowColor, _DeepColor, yFade);

                    finalColor = lerp(waterColor, _FoamColor, totalFoam);
                    finalColor.a = _Transparency;
                }
                else
                {
                    // Bottom face: invisible
                    finalColor = half4(0, 0, 0, 0);
                }

                finalColor.rgb = MixFog(finalColor.rgb, input.fogCoord);
                return finalColor;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
