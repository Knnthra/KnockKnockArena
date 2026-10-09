Shader "Custom/CelShadedTerrain"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.4, 0.7, 0.3, 1)
        _BaseMap ("Base Texture", 2D) = "white" {}

        [Header(Cel Shading)]
        _CelBands ("Cel Bands", Range(2, 10)) = 3
        _CelSmoothness ("Band Smoothness", Range(0, 0.5)) = 0.05
        _ShadowColor ("Shadow Color", Color) = (0.3, 0.45, 0.25, 1)
        _ShadowThreshold ("Shadow Threshold", Range(0, 1)) = 0.5

        [Header(Rim Light)]
        _RimColor ("Rim Color", Color) = (1, 1, 1, 1)
        _RimPower ("Rim Power", Range(0.1, 8)) = 3
        _RimIntensity ("Rim Intensity", Range(0, 1)) = 0.3
        _RimThreshold ("Rim Threshold", Range(0, 1)) = 0.7

        [Header(Surface)]
        _AmbientIntensity ("Ambient Intensity", Range(0, 2)) = 1.2
        _Saturation ("Saturation", Range(0, 2)) = 1.0
        _Alpha ("Alpha", Range(0, 1)) = 1.0

        [Header(Patches)]
        _PatchScale ("Patch Scale (world units)", Float) = 2.0
        _PatchDensity ("Patch Density", Range(0, 1)) = 0.4
        _PatchBrightness ("Patch Brightness (1 = base, 0 = black, 1.5 = lighter)", Range(0, 1.5)) = 0.75

        [Header(Outline)]
        _OutlineWidth ("Outline Width", Range(0, 0.1)) = 0.01
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)

        [Header(Vertical Clip)]
        _ClipY ("Clip Y Position", Float) = -1000
        _ClipAbove ("Clip Above (1) or Below (0)", Float) = 0

        [Header(Burn Effect)]
        _BurnDarkness ("Burn Darkness", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        // ── Pass 1: Lit geometry ───────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Off

            Stencil
            {
                Ref 2
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
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
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float3 viewDirWS  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _CelBands;
                float  _CelSmoothness;
                float4 _ShadowColor;
                float  _ShadowThreshold;
                float4 _RimColor;
                float  _RimPower;
                float  _RimIntensity;
                float  _RimThreshold;
                float  _AmbientIntensity;
                float  _Saturation;
                float  _Alpha;
                float  _PatchScale;
                float  _PatchDensity;
                float  _PatchBrightness;
                float  _ClipY;
                float  _ClipAbove;
                float  _BurnDarkness;
            CBUFFER_END

            // ---- Cell hash noise (hard square edges, pixel-art style) --------
            float Hash2D(float2 cell)
            {
                cell  = fmod(cell, 289.0);
                cell  = frac(cell * float2(0.1031, 0.1030));
                cell += dot(cell, cell.yx + 33.33);
                return frac((cell.x + cell.y) * cell.x);
            }

            // ---- Cel shading helpers -----------------------------------------
            float CelShade(float value, float bands, float smoothness)
            {
                float stepped = floor(value * bands) / bands;
                return lerp(stepped, value, smoothness);
            }

            float3 AdjustSaturation(float3 color, float saturation)
            {
                float lum = dot(color, float3(0.299, 0.587, 0.114));
                return lerp(float3(lum, lum, lum), color, saturation);
            }

            // ---- Vertex ------------------------------------------------------
            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS   = normInputs.normalWS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.viewDirWS  = GetWorldSpaceViewDir(posInputs.positionWS);

                return output;
            }

            // ---- Fragment ----------------------------------------------------
            half4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Vertical clip (used for burn/dissolve transitions)
                if (_ClipAbove > 0.5)
                    clip(_ClipY - input.positionWS.y);
                else
                    clip(input.positionWS.y - _ClipY);

                // Base colour
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 albedo  = baseMap * _BaseColor;

                // ---- Patch noise (pixel-art square cells) --------------------
                // Top/bottom faces (abs(normalWS.y) > 0.5): project down onto XZ plane.
                // Side faces: x+z gives the horizontal axis on any axis-aligned vertical
                // face (the normal axis is constant, so only the other component varies),
                // and y covers the vertical extent.
                float3 n = abs(normalize(input.normalWS));
                float2 patchUV = n.y > 0.5
                    ? input.positionWS.xz
                    : float2(input.positionWS.x + input.positionWS.z, input.positionWS.y);

                float noise   = Hash2D(floor(patchUV / max(_PatchScale, 0.001)));
                float isPatch = step(1.0 - _PatchDensity, noise);

                // _PatchBrightness: 1 = same as base, 0 = black, >1 = lighter.
                albedo.rgb *= lerp(1.0, _PatchBrightness, isPatch);

                // ---- Lighting ------------------------------------------------
                float3 normalWS  = normalize(input.normalWS) * (isFrontFace ? 1.0 : -1.0);
                float3 viewDirWS = normalize(input.viewDirWS);

                Light  mainLight  = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 lightDir   = normalize(mainLight.direction);

                float NdotL        = dot(normalWS, lightDir);
                float celLighting  = CelShade(saturate(NdotL), _CelBands, _CelSmoothness);

                float shadowStep = step(_ShadowThreshold, mainLight.shadowAttenuation);
                shadowStep = lerp(shadowStep, mainLight.shadowAttenuation, _CelSmoothness * 2.0);

                float3 lightingColor = lerp(_ShadowColor.rgb, float3(1, 1, 1), celLighting);
                lightingColor        = lerp(_ShadowColor.rgb, lightingColor, shadowStep);

                float3 diffuse = albedo.rgb * lightingColor * mainLight.color;

                // Rim light
                float rimDot       = 1.0 - saturate(dot(viewDirWS, normalWS));
                float rimIntensity = pow(rimDot, _RimPower);
                float rimStep      = step(_RimThreshold, rimIntensity);
                rimStep  = lerp(rimStep, rimIntensity, _CelSmoothness * 2.0);
                float3 rimLight = rimStep * _RimIntensity * _RimColor.rgb;

                // Ambient
                float3 ambient = albedo.rgb * unity_SHAr.rgb * _AmbientIntensity;

                // Additional lights
                #ifdef _ADDITIONAL_LIGHTS
                uint pixelLightCount = GetAdditionalLightsCount();
                for (uint i = 0u; i < pixelLightCount; ++i)
                {
                    Light light         = GetAdditionalLight(i, input.positionWS);
                    float addNdotL      = dot(normalWS, normalize(light.direction));
                    float addCel        = CelShade(saturate(addNdotL), _CelBands, _CelSmoothness);
                    float addShadow     = lerp(step(0.5, light.shadowAttenuation),
                                               light.shadowAttenuation, _CelSmoothness * 2.0);
                    diffuse += albedo.rgb * light.color * addCel * addShadow * light.distanceAttenuation;
                }
                #endif

                float3 finalColor = diffuse + ambient + rimLight;
                finalColor = AdjustSaturation(finalColor, _Saturation);
                finalColor = lerp(finalColor, float3(0, 0, 0), _BurnDarkness);

                return half4(finalColor, albedo.a * _Alpha);
            }
            ENDHLSL
        }

        // ── Pass 2: Outline ───────────────────────────────────────────────────
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float  _OutlineWidth;
                float4 _OutlineColor;
                float  _Alpha;
            CBUFFER_END

            Varyings OutlineVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // Use baked smooth normal from tangent.xyz if present (w == 0), else vertex normal
                float3 normalOS   = input.tangentOS.w == 0 ? input.tangentOS.xyz : input.normalOS;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(normalOS);
                positionWS       += normalWS * _OutlineWidth;

                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 OutlineFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return half4(_OutlineColor.rgb, _OutlineColor.a * _Alpha);
            }
            ENDHLSL
        }

        // ── Pass 3: Shadow caster ─────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            float3 _LightDirection;

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // ── Pass 4: Depth only ────────────────────────────────────────────────
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // ── Pass 5: Depth normals ─────────────────────────────────────────────
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return half4(normalize(input.normalWS), 0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
