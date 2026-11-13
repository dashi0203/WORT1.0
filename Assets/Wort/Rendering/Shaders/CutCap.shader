// 高度裁切的横切面（鹰眼视觉 / 以后的室内地下裁切）：裁切高度处一块水平面，按地形高度场判断“这里是不是实心的”，
// 实心处画黑色，其余丢掉。边缘：比格线往外扩 _WortCutParams.w（盖住侧墙鼓包）并按噪声溶解；裁切高度的起伏和地形裁切用同一个函数，截面和断口对上。
// 地形网格是空心的（背面 / 北面不全），不能用“画背面”的做法，所以用高度场。由 Wort.Rendering.SpatialView 生成平面并开关。
Shader "Wort/CutCap"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "WortAO.hlsl"
        #include "WortSpace.hlsl"
        struct CA { float4 positionOS : POSITION; };
        struct CV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
        CV cvert(CA v)
        {
            CV o;
            float3 w = TransformObjectToWorld(v.positionOS.xyz);
            w.y = _WortCutParams.x;
            o.positionWS = w;
            o.positionCS = TransformWorldToHClip(w);
            return o;
        }
        void CapClip(float3 P)
        {
            if (_WortCutOn < 0.5 || _WortHeightRect.z < 1) { clip(-1); return; }
            float2 org = _WortHeightRect.xy;
            int2 c0 = (int2)floor(P.xz - org);
            float dil = max(_WortCutParams.w, 0.02);
            float s = 0;
            [unroll] for (int dz = -1; dz <= 1; dz++)
            [unroll] for (int dx = -1; dx <= 1; dx++)
            {
                int2 c = c0 + int2(dx, dz);
                float2 bmin = org + float2(c);
                float2 q = clamp(P.xz, bmin, bmin + 1);
                float d = length(q - P.xz);
                if (WortCellHeight(c, q) > WortCutHeight(q))
                    s = max(s, 1 - smoothstep(dil * 0.4, dil, d));
            }
            float n = WortNoise2(P.xz * 6 + _WortCutParams.z * 0.4) * 0.7 + WortNoise2(P.xz * 17) * 0.3;
            clip(s - n * 0.9 - 0.05);
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off ZWrite On
            HLSLPROGRAM
            #pragma vertex cvert
            #pragma fragment cfrag
            half4 cfrag(CV i) : SV_Target { CapClip(i.positionWS); return half4(0, 0, 0, 1); }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex cvert
            #pragma fragment dfrag
            half dfrag(CV i) : SV_Target { CapClip(i.positionWS); return i.positionCS.z; }
            ENDHLSL
        }
    }
}
