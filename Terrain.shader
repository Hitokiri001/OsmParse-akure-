Shader "Universal Render Pipeline/Custom/Terrain"
{
    Properties
    {
        [Header(Terrain Textures)]
        [Space(8)]
        _GrassTexture("Grass / Cropland", 2D) = "white" {}
        _ForestTexture("Forest / Shrubland", 2D) = "white" {}
        _UrbanTexture("Urban / Bare Rock", 2D) = "white" {}
        _WaterTexture("Water", 2D) = "white" {}

        [Header(Splatmap)]
        [Space(8)]
        [NoScaleOffset]
        _Splatmap("Splatmap (R=Grass G=Forest B=Urban A=Water)", 2D) = "red" {}

        [Header(Settings)]
        [Space(8)]
        _Tiling("Texture Tiling", Float) = 0.05
        _Smoothness("Smoothness", Range(0, 1)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
        }

        // -----------------------------------------------------------------------
        // Pass 1 - Forward Lit
        // Handles all real-time lighting, receives shadows
        // -----------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5

            // URP keywords - required for shadows, fog, additional lights to work
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Textures declared outside CBUFFER - this is correct for URP
            TEXTURE2D(_GrassTexture);  SAMPLER(sampler_GrassTexture);
            TEXTURE2D(_ForestTexture); SAMPLER(sampler_ForestTexture);
            TEXTURE2D(_UrbanTexture);  SAMPLER(sampler_UrbanTexture);
            TEXTURE2D(_WaterTexture);  SAMPLER(sampler_WaterTexture);
            TEXTURE2D(_Splatmap);      SAMPLER(sampler_Splatmap);

            // All float/vector properties must live inside UnityPerMaterial
            // for SRP batcher compatibility - reduces draw call overhead
            CBUFFER_START(UnityPerMaterial)
                float _Tiling;
                float _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1; // splatmap coords - 0 to 1 across chunk
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv1         : TEXCOORD0; // splatmap lookup
                float3 positionWS  : TEXCOORD1; // world position for XZ tiling
                float3 normalWS    : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                float  fogFactor   : TEXCOORD4;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs    = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS  = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normalInputs.normalWS;
                OUT.uv1         = IN.uv1;
                OUT.shadowCoord = GetShadowCoord(posInputs);
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // Sample the WorldCover splatmap using UV1
                // UV1 is set by TerrainMesher to go 0,0 at chunk corner to 1,1 at opposite corner
                // SplatmapLoader.cs assigns the correct PNG per chunk at runtime
                // R = Grass/Cropland  G = Forest/Shrubland  B = Urban/Bare  A = Water
                half4 splat = SAMPLE_TEXTURE2D(_Splatmap, sampler_Splatmap, IN.uv1);

                // Sample all 4 terrain textures using world XZ position
                // World space tiling means textures tile seamlessly across chunk boundaries
                // with no visible seam at UV edges - chunks share the same world coordinate space
                float2 tiledUV = IN.positionWS.xz * _Tiling;

                half3 grass  = SAMPLE_TEXTURE2D(_GrassTexture,  sampler_GrassTexture,  tiledUV).rgb;
                half3 forest = SAMPLE_TEXTURE2D(_ForestTexture, sampler_ForestTexture, tiledUV).rgb;
                half3 urban  = SAMPLE_TEXTURE2D(_UrbanTexture,  sampler_UrbanTexture,  tiledUV).rgb;
                half3 water  = SAMPLE_TEXTURE2D(_WaterTexture,  sampler_WaterTexture,  tiledUV).rgb;

                // Blend terrain textures weighted by splatmap channels
                half3 albedo = grass  * splat.r
                             + forest * splat.g
                             + urban  * splat.b
                             + water  * splat.a;

                // Normalise - splatmap channels should sum to 1 but
                // PNG compression can shift them slightly so we correct for it
                float weightSum = splat.r + splat.g + splat.b + splat.a;
                albedo /= max(weightSum, 0.001);

                // URP PBR lighting via InputData and SurfaceData
                // UniversalFragmentPBR handles main light, additional lights,
                // shadows, ambient GI and fog in one call
                InputData inputData = (InputData)0;
                inputData.positionWS              = IN.positionWS;
                inputData.normalWS                = normalize(IN.normalWS);
                inputData.viewDirectionWS         = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord             = IN.shadowCoord;
                inputData.fogCoord                = IN.fogFactor;
                inputData.vertexLighting          = half3(0, 0, 0);
                inputData.bakedGI                 = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(float2(0, 0));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo;
                surfaceData.metallic   = 0.0;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion  = 1.0;
                surfaceData.alpha      = 1.0;
                surfaceData.normalTS   = half3(0, 0, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                color.rgb = MixFog(color.rgb, IN.fogFactor);

                return color;
            }

            ENDHLSL
        }

        // -----------------------------------------------------------------------
        // Pass 2 - Shadow Caster
        // Terrain needs to cast shadows onto roads and other objects
        // -----------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Tiling;
                float _Smoothness;
            CBUFFER_END

            ENDHLSL
        }

        // -----------------------------------------------------------------------
        // Pass 3 - Depth Only
        // Required for SSAO, depth of field and camera depth texture to work
        // -----------------------------------------------------------------------
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Tiling;
                float _Smoothness;
            CBUFFER_END

            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
