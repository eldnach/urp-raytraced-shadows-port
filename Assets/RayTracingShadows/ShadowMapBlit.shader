
Shader "ShadowBlit"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        ZWrite Off Cull Off
        Blend Zero SrcAlpha
        Pass
        {
            Name "ColorBlitPass"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag


            float _ShadowIntensity;
            float4 Frag(Varyings input) : SV_Target0
            {
                // this is needed so we account XR platform differences in how they handle texture arrays
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // sample the texture using the SAMPLE_TEXTURE2D_X_LOD
                float2 uv = input.texcoord.xy;
                half4 shadow = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearRepeat, uv, _BlitMipLevel);
                return half4(0.0, 0.0, 0.0, shadow.r);
            }
            ENDHLSL
        }
    }
}

/*

Shader "RayTracing/ShadowMapBlit"
{
    Properties
    {
        _MainTex("", 2D) = "" {}
    }

        Subshader
    {
        ZTest Always Cull Off ZWrite Off

        // Multiple frame buffer color by shadowmap value
        // Blend Zero SrcAlpha

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            sampler2D _MainTex;

            half4 frag(v2f i) : SV_Target
            {
                half shadowMap = saturate(tex2D(_MainTex, i.uv).r + 0.1);
                return half4(1, 0, 0, 1);
            }

            ENDCG
        }
    }

    Fallback off
}



*/
