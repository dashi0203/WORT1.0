// 水面（TerrainLab 的水面子网格；原来是 URP Lit 纯色）。外观照旧：纯色 + 一点高光；接入和地形同一套空间呈现：
// 鹰眼视觉的裁切（WortSpace.hlsl；遮挡虚化不影响水面，负责人 2026-11-14）、云影（主光源 cookie）、世界空间 AO、最低层压暗、脚下薄雾。
Shader "Wort/Water"
{
    Properties
    {
        _BaseColor ("Color", Color) = (0.2, 0.45, 0.7, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
        CBUFFER_END
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WortAO.hlsl"
            #include "WortSpace.hlsl"
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float fog : TEXCOORD2; };
            V vert(A v)
            {
                V o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                float3 P = i.positionWS;
                WortHeightClip(P, mul(UNITY_MATRIX_VP, float4(P, 1)));   // 只做鹰眼视觉的裁切；遮挡虚化不影响水面（负责人 2026-11-14）
                float3 n = normalize(i.normalWS);
                float aoDbg; float wao = WortAO(P, n, aoDbg);
                if (aoDbg >= 0) return half4(aoDbg.xxx, 1);
                Light light = GetMainLight(TransformWorldToShadowCoord(P), P, half4(1, 1, 1, 1));
                float3 vdir = normalize(GetWorldSpaceViewDir(P));
                float3 h = normalize(light.direction + vdir);
                float spec = pow(saturate(dot(n, h)), lerp(8, 256, _Smoothness)) * _Smoothness;
                half3 direct = light.color * light.shadowAttenuation * WortAODirect(wao);
                half3 color = _BaseColor.rgb * (direct * saturate(dot(n, light.direction)) + SampleSH(n) * wao) + direct * spec * 0.5;
                color = WortSpaceShade(color, P);
                color = MixFog(color, i.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex dvert
            #pragma fragment dfrag
            #include "WortSpace.hlsl"
            struct DA { float4 positionOS : POSITION; };
            struct DV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
            DV dvert(DA v) { DV o; float3 w = TransformObjectToWorld(v.positionOS.xyz); o.positionWS = w; o.positionCS = TransformWorldToHClip(w); return o; }
            half dfrag(DV i) : SV_Target { WortHeightClip(i.positionWS, mul(UNITY_MATRIX_VP, float4(i.positionWS, 1))); return i.positionCS.z; }
            ENDHLSL
        }
    }
}
