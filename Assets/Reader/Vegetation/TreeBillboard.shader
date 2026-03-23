// TreeBillboard.shader
// URP camera-facing billboard for vegetation LOD.
//
// The billboard quad uses unit mesh vertices:
//   X: -0.5 to +0.5  (left/right)
//   Y:  0.0 to +1.0  (base to top)
//   Z:  0.0          (flat)
// The shader expands these in world space toward the camera's right vector
// and world up, so the billboard always faces the camera regardless of rotation.
//
// Passes: ForwardLit (alpha-clip, main light, fog)  +  ShadowCaster
//
// Notes:
//   - Object pivot must be at the TREE BASE (world Y = terrain surface)
//   - Enable GPU Instancing on the material for batching
//   - _Width and _Height are set per-instance by VegetationChunk.Build
//   - No normal maps — cost vs quality tradeoff for billboard distance

Shader "Universal Render Pipeline/Custom/TreeBillboard"
{
    Properties
    {
        [MainTexture] _MainTex ("Billboard Texture (RGBA)", 2D) = "white" {}
        _Cutoff    ("Alpha Cutoff",   Range(0, 1)) = 0.45
        _Width     ("World Width  (m)",  Float)    = 3.0
        _Height    ("World Height (m)",  Float)    = 7.0
        _Brightness("Brightness",    Range(0.5, 2)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "TransparentCutout"
            "Queue"          = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector"= "True"
        }

        // No back-face culling — see the billboard from either side
        Cull Off

        // ── ForwardLit ────────────────────────────────────────────────────
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Cutoff;
                float  _Width;
                float  _Height;
                float  _Brightness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 worldPos    : TEXCOORD1;
                float  fogFactor   : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                // World position of the object pivot (tree base)
                float3 pivotWS = TransformObjectToWorld(float3(0, 0, 0));

                // Camera right vector from view matrix (row 0)
                // Using UNITY_MATRIX_V is correct in URP for the current camera.
                float3 camRight = float3(UNITY_MATRIX_V[0].x,
                                         UNITY_MATRIX_V[0].y,
                                         UNITY_MATRIX_V[0].z);

                // Expand quad in world space
                // positionOS.x : -0.5 .. +0.5 → horizontal spread via camRight
                // positionOS.y :  0.0 .. +1.0 → vertical spread via world up
                float3 worldPos = pivotWS
                    + camRight          * (IN.positionOS.x * _Width)
                    + float3(0, 1, 0)   * (IN.positionOS.y * _Height);

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.worldPos    = worldPos;
                OUT.fogFactor   = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                clip(col.a - _Cutoff);

                // Main directional light — flat diffuse, no normal (billboard has none)
                Light mainLight = GetMainLight(
                    TransformWorldToShadowCoord(IN.worldPos));
                half3 lighting = mainLight.color
                    * mainLight.distanceAttenuation
                    * mainLight.shadowAttenuation;

                // Ambient contribution (SH) — avoids pitch-black shaded side
                half3 ambient = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w) * 0.35h;

                col.rgb *= (lighting + ambient) * _Brightness;
                col.rgb  = MixFog(col.rgb, IN.fogFactor);

                return col;
            }
            ENDHLSL
        }

        // ── ShadowCaster ──────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Cutoff;
                float  _Width;
                float  _Height;
                float  _Brightness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vertShadow(Attributes IN)
            {
                Varyings OUT;

                float3 pivotWS = TransformObjectToWorld(float3(0, 0, 0));
                float3 camRight = float3(UNITY_MATRIX_V[0].x,
                                          UNITY_MATRIX_V[0].y,
                                          UNITY_MATRIX_V[0].z);

                float3 worldPos = pivotWS
                    + camRight        * (IN.positionOS.x * _Width)
                    + float3(0, 1, 0) * (IN.positionOS.y * _Height);

                // Apply shadow bias
                float3 lightDir = normalize(_MainLightPosition.xyz);
                worldPos = ApplyShadowBias(worldPos, float3(0, 1, 0), lightDir);

                OUT.positionHCS = TransformWorldToHClip(worldPos);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
