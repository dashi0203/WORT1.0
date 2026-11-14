// 高度裁切的横切面（鹰眼视觉 / 以后的室内地下裁切；鹰眼视觉时只在圆形范围内）：裁切高度处一块水平面，按地形高度场判断“这里是不是实心的”，
// 实心处画黑色，其余丢掉。圆和地形裁切用同一个函数（WortCutCircleX）；格线处外扩一点并按噪声参差（和遮挡虚化的黑边同款）。
// 地形网格是空心的（背面 / 北面不全），不能用“画背面”的做法，所以用高度场。由 Wort.Rendering.SpatialView 生成平面并开关。
Shader "Wort/CutCap"
{
    Properties
    {
        [Enum(EagleVision, 0, Occlusion, 1)] _CapMode ("Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "WortAO.hlsl"
        #include "WortSpace.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _CapMode;   // 0 = 鹰眼视觉（裁切高度、圈内所有实心格）；1 = 遮挡虚化（脚下 + 余量、只在挡住主角的柱子上）
        CBUFFER_END
        struct CA { float4 positionOS : POSITION; };
        struct CV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
        CV cvert(CA v)
        {
            CV o;
            float3 w = TransformObjectToWorld(v.positionOS.xyz);
            w.y = _CapMode > 0.5 ? _WortHoleEdge.z + _WortHoleParams.w : _WortCutParams.x;   // 遮挡虚化：脚下 + 裁切高度（断口只往下起伏，不会高过它）
            o.positionWS = w;
            o.positionCS = TransformWorldToHClip(w);
            return o;
        }
        void CapClip(float3 P)
        {
            if (_CapMode > 0.5)
            {
                // 遮挡虚化：柱顶高于横截面的实心格（格线外扩 = 一点点 + 噪声参差，和鹰眼视觉同款），并且“它上面的柱顶被裁掉了”。
                // 判断用同一个屏幕像素上方的柱顶那一点 T（沿视线往相机方向走到柱顶高度），和地形裁切的判断点一致：
                // 原来用横截面自己那一点（低得多），主角北侧 / 旁边的柱子柱顶被裁了，横截面却判成“在主角后面”没画（负责人 2026-11-14）。
                if (_WortHoleCenter.w <= 1e-3 || _WortHeightRect.z < 1) { clip(-1); return; }
                float2 orgH = _WortHeightRect.xy;
                int2 ch = (int2)floor(P.xz - orgH);
                float capY = _WortHoleEdge.z + _WortHoleParams.w;
                float dilH = max(_WortCutParams.w, 0.005) + _WortCutStyle.y * WortNoise2(P.xz * 2.4 + _WortHoleParams.x * 0.25);
                float3 v = _WortViewDir.xyz;
                bool shown = false;
                [unroll] for (int hz = -1; hz <= 1; hz++)
                [unroll] for (int hx = -1; hx <= 1; hx++)
                {
                    int2 c = ch + int2(hx, hz);
                    float2 bmin = orgH + float2(c);
                    float2 q = clamp(P.xz, bmin, bmin + 1);
                    if (length(q - P.xz) > dilH) continue;
                    float H = WortCellHeight(c, q);
                    if (H <= capY) continue;
                    float3 T = v.y > 0.05 ? P + v * ((H - capY) / v.y) : float3(P.x, H, P.z);
                    if (WortHoleScreenX(T) < 1 && WortHoleInFront(T)) shown = true;
                }
                clip(shown ? 1 : -1);
                return;
            }
            if (_WortCutOn < 0.5 || _WortHeightRect.z < 1) { clip(-1); return; }
            // 先判断圆形范围（便宜），圆外直接丢掉，不做后面的高度场查询
            if (_WortCutCircle.z > 0) clip(1 - WortCutCircleX(WortToFeetPlane(P)));   // 和地形裁切同一个圆（波浪边）；圆边渐变由地形那边的抖动透出来
            float2 org = _WortHeightRect.xy;
            int2 c0 = (int2)floor(P.xz - org);
            // 外扩 = 盖接缝的一点点 + 参差扰动（和遮挡虚化洞里黑边往地面啃的那一圈同一个样子）
            float dil = max(_WortCutParams.w, 0.005) + _WortCutStyle.y * WortNoise2(P.xz * 2.4 + _WortCutParams.z * 0.25);
            float cutH = WortCutHeight(P.xz);   // 一个像素只算一次（起伏很缓，邻格用同一个值）
            float s = 0;
            [unroll] for (int dz = -1; dz <= 1; dz++)
            [unroll] for (int dx = -1; dx <= 1; dx++)
            {
                int2 c = c0 + int2(dx, dz);
                float2 bmin = org + float2(c);
                float2 q = clamp(P.xz, bmin, bmin + 1);
                if (length(q - P.xz) <= dil && WortCellHeight(c, q) > cutH) s = 1;
            }
            clip(s - 0.5);   // 实心格往外 _WortCutParams.w（默认 0.06m，只盖接缝，不伸出墙面）
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
