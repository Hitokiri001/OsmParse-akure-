Shader "Universal Render Pipeline/Custom/OSM Terrain"
{
    Properties
    {
        [Header(Terrain Textures)]
        [Space(8)]
        _GrassTexture ("Grass / Cropland",   2D) = "white" {}
        _ForestTexture("Forest / Shrubland", 2D) = "white" {}
        _UrbanTexture ("Urban / Bare Rock",  2D) = "white" {}
        _WaterTexture ("Water",              2D) = "white" {}

        [Header(Normal Maps)]
        [Space(8)]
        [Normal] _GrassNormal ("Grass Normal Map",   2D) = "bump" {}
        [Normal] _ForestNormal("Forest Normal Map",  2D) = "bump" {}
        [Normal] _UrbanNormal ("Urban Normal Map",   2D) = "bump" {}
        [Normal] _WaterNormal ("Water Normal Map",   2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 1.0

        [Header(Splatmap)]
        [Space(8)]
        [NoScaleOffset]
        _Splatmap("Splatmap (R=Grass G=Forest B=Urban A=Water)", 2D) = "red" {}

        [Header(Settings)]
        [Space(8)]
        _Tiling    ("Texture Tiling", Float)      = 0.05
        _Smoothness("Smoothness",     Range(0,1)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
        }

        // -------------------------------------------------------------------
        // HLSLINCLUDE — shared across ALL passes
        // CBUFFER must be identical in every pass for SRP Batcher to work
        // -------------------------------------------------------------------
        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Albedo textures
        TEXTURE2D(_GrassTexture);  SAMPLER(sampler_GrassTexture);
        TEXTURE2D(_ForestTexture); SAMPLER(sampler_ForestTexture);
        TEXTURE2D(_UrbanTexture);  SAMPLER(sampler_UrbanTexture);
        TEXTURE2D(_WaterTexture);  SAMPLER(sampler_WaterTexture);

        // Normal maps — "bump" default returns (0,0,1) when no texture assigned
        TEXTURE2D(_GrassNormal);   SAMPLER(sampler_GrassNormal);
        TEXTURE2D(_ForestNormal);  SAMPLER(sampler_ForestNormal);
        TEXTURE2D(_UrbanNormal);   SAMPLER(sampler_UrbanNormal);
        TEXTURE2D(_WaterNormal);   SAMPLER(sampler_WaterNormal);

        TEXTURE2D(_Splatmap);      SAMPLER(sampler_Splatmap);

        CBUFFER_START(UnityPerMaterial)
            float4 _GrassTexture_ST;
            float4 _ForestTexture_ST;
            float4 _UrbanTexture_ST;
            float4 _WaterTexture_ST;
            float4 _GrassNormal_ST;
            float4 _ForestNormal_ST;
            float4 _UrbanNormal_ST;
            float4 _WaterNormal_ST;
            float  _Tiling;
            float  _Smoothness;
            float  _NormalStrength;
        CBUFFER_END

        ENDHLSL

        // -------------------------------------------------------------------
        // Pass 1 — Forward Lit
        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest  LEqual
            Cull   Back

            HLSLPROGRAM
            #pragma target 3.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv1         : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs    = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS  = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normalInputs.normalWS;
                OUT.tangentWS   = normalInputs.tangentWS;
                OUT.bitangentWS = normalInputs.bitangentWS;
                OUT.uv1         = IN.uv1;
                OUT.shadowCoord = GetShadowCoord(posInputs);
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // Sample WorldCover splatmap via UV1 (0-1 across chunk)
                half4 splat = SAMPLE_TEXTURE2D(_Splatmap, sampler_Splatmap, IN.uv1);

                // World XZ tiling — seamless across chunk boundaries
                float2 tiledUV = IN.positionWS.xz * _Tiling;

                // --- Albedo ---
                half3 grass  = SAMPLE_TEXTURE2D(_GrassTexture,  sampler_GrassTexture,  tiledUV).rgb;
                half3 forest = SAMPLE_TEXTURE2D(_ForestTexture, sampler_ForestTexture, tiledUV).rgb;
                half3 urban  = SAMPLE_TEXTURE2D(_UrbanTexture,  sampler_UrbanTexture,  tiledUV).rgb;
                half3 water  = SAMPLE_TEXTURE2D(_WaterTexture,  sampler_WaterTexture,  tiledUV).rgb;

                half3 albedo = grass  * splat.r
                             + forest * splat.g
                             + urban  * splat.b
                             + water  * splat.a;

                albedo /= max(splat.r + splat.g + splat.b + splat.a, 0.001);

                // --- Normal maps ---
                // UnpackNormal converts the DXT5nm/BC5 normal map encoding to XYZ tangent space
                // We blend the four layer normals by the same splatmap weights as albedo
                half3 nGrass  = UnpackNormal(SAMPLE_TEXTURE2D(_GrassNormal,  sampler_GrassNormal,  tiledUV));
                half3 nForest = UnpackNormal(SAMPLE_TEXTURE2D(_ForestNormal, sampler_ForestNormal, tiledUV));
                half3 nUrban  = UnpackNormal(SAMPLE_TEXTURE2D(_UrbanNormal,  sampler_UrbanNormal,  tiledUV));
                half3 nWater  = UnpackNormal(SAMPLE_TEXTURE2D(_WaterNormal,  sampler_WaterNormal,  tiledUV));

                half3 blendedNormalTS = nGrass  * splat.r
                                      + nForest * splat.g
                                      + nUrban  * splat.b
                                      + nWater  * splat.a;

                blendedNormalTS /= max(splat.r + splat.g + splat.b + splat.a, 0.001);

                // Apply normal strength — lerp toward flat normal (0,0,1) to reduce intensity
                blendedNormalTS = lerp(half3(0, 0, 1), blendedNormalTS, _NormalStrength);
                blendedNormalTS = normalize(blendedNormalTS);

                // Transform tangent space normal to world space using TBN matrix
                float3x3 TBN       = float3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                float3   normalWS  = normalize(mul(blendedNormalTS, TBN));

                // --- URP PBR lighting ---
                InputData inputData = (InputData)0;
                inputData.positionWS              = IN.positionWS;
                inputData.normalWS                = normalWS;
                inputData.viewDirectionWS         = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord             = IN.shadowCoord;
                inputData.fogCoord                = IN.fogFactor;
                inputData.vertexLighting          = half3(0, 0, 0);
                inputData.bakedGI                 = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(float2(0, 0));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo;
                surfaceData.normalTS   = blendedNormalTS;
                surfaceData.metallic   = 0.0;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion  = 1.0;
                surfaceData.alpha      = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb   = MixFog(color.rgb, IN.fogFactor);

                return color;
            }

            ENDHLSL
        }

        // -------------------------------------------------------------------
        // Pass 2 — Shadow Caster
        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest  LEqual
            ColorMask 0
            Cull   Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float invNdotL = 1.0 - saturate(dot(lightDir, normalWS));
                positionWS    += normalWS * (invNdotL * 0.005);

                OUT.positionCS = TransformWorldToHClip(positionWS);

                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target { return 0; }

            ENDHLSL
        }

        // -------------------------------------------------------------------
        // Pass 3 — Depth Only
        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5

            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes { float4 positionOS : POSITION; };
            struct DepthVaryings   { float4 positionCS : SV_POSITION; };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target { return 0; }

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
