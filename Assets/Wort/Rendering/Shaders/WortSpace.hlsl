// 空间呈现（GDD 第 7 节）：遮挡虚化、高度裁切（鹰眼视觉 / 室内地下共用）、最低层压暗、主角脚下的薄雾。
// 参数由 Wort.Rendering.SpatialView 每帧设置；场景里没有这个组件时全部关闭（各开关为 0 / 裁切高度极大）。
// - 只在 ForwardLit / DepthOnly / DepthNormals 里裁；ShadowCaster 不裁 → 被裁掉的东西照常投影（负责人 2026-11-13）。
#ifndef WORT_SPACE_INCLUDED
#define WORT_SPACE_INCLUDED

float4 _WortHoleCenter;   // xyz = 主角身体中心（世界），w = 圆的半径（米，屏幕平面里；0 = 关）
float4 _WortHoleParams;   // x = 时间，y = 波浪幅度（比例），z = 边缘溶解带宽（比例），w = 高出主角中心多少才算“挡在前面”（米）
float4 _WortCutParams;    // x = 裁切高度，y = 边缘起伏幅度（米），z = 时间，w = 截面外扩（米，盖住侧墙鼓包）
float _WortCutOn;         // 1 = 裁切开（默认 0：场景里没有 SpatialView 时什么都不裁）
float4 _WortBottomParams; // x = 最低一层的高度，y = 渐变高度（米），z = 最多暗多少，w = 开关
float4 _WortFogParams;    // x = 雾的起点高度（主角脚下 − 2.5），y = 渐变厚度，z = 最大浓度，w = 开关
float4 _WortFogColor;     // rgb = 已经乘上当前光照的雾色，a = 时间

float WortHash2(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float WortNoise2(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    float a = WortHash2(i), b = WortHash2(i + float2(1, 0)), c = WortHash2(i + float2(0, 1)), d = WortHash2(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// 裁切高度（带一点起伏，截面边缘不是一刀切的直线）
float WortCutHeight(float2 xz)
{
    float t = _WortCutParams.z;
    float n = WortNoise2(xz * 1.7 + t * 0.3) * 0.65 + WortNoise2(xz * 4.3 - t * 0.5) * 0.35;
    return _WortCutParams.x + _WortCutParams.y * (n * 2 - 1);
}

// 遮挡虚化：主角几乎被挡住时，挡在前面的东西在屏幕上挖一个圆（边缘波浪线、随时间流动，再加一圈溶解）
void WortHoleClip(float3 P)
{
    float R = _WortHoleCenter.w;
    if (R <= 1e-3) return;
    float3 C = _WortHoleCenter.xyz;
    // 挡在前面 = 视空间里比主角中心更靠近相机（错切俯视下等价于：屏幕同一处、比主角高）
    float dp = -mul(UNITY_MATRIX_V, float4(P, 1)).z;
    float dc = -mul(UNITY_MATRIX_V, float4(C, 1)).z;
    if (dp > dc - _WortHoleParams.w) return;
    float4 pc = mul(UNITY_MATRIX_VP, float4(P, 1));
    float4 cc = mul(UNITY_MATRIX_VP, float4(C, 1));
    float4 rc = mul(UNITY_MATRIX_VP, float4(C + UNITY_MATRIX_V[0].xyz * R, 1));   // 相机右方 R 米处 → 屏幕上的半径
    float2 hs = _ScreenParams.xy * 0.5;
    float2 cn = cc.xy / cc.w;
    float2 d = (pc.xy / pc.w - cn) * hs;
    float rr = length((rc.xy / rc.w - cn) * hs);
    float t = _WortHoleParams.x;
    float th = atan2(d.y, d.x);
    float wave = 1 + _WortHoleParams.y * (0.6 * sin(5 * th + 1.7 * t) + 0.4 * sin(9 * th - 2.3 * t + 1.3));
    float x = length(d) / max(rr * wave, 1e-3);   // < 1 在圆内
    if (x >= 1) return;
    float band = max(_WortHoleParams.z, 1e-3);
    float n = WortNoise2(P.xz * 5 + t * 0.6) * 0.7 + WortNoise2(P.xz * 13 - t) * 0.3;   // 按世界坐标，溶解图案不跟着屏幕游动
    clip((x - (1 - band)) / band - n);
}

void WortHeightClip(float3 P)
{
    if (_WortCutOn < 0.5) return;
    clip(WortCutHeight(P.xz) - P.y);
}

// 裁切（ForwardLit / DepthOnly / DepthNormals 都调；ShadowCaster 不调）
void WortSpaceClip(float3 P)
{
    WortHeightClip(P);
    WortHoleClip(P);
}

// 最低一层压暗 + 主角脚下薄雾（光照算完以后调）
half3 WortSpaceShade(half3 color, float3 P)
{
    if (_WortBottomParams.w > 0.5)
    {
        float k = saturate((P.y - _WortBottomParams.x) / max(_WortBottomParams.y, 1e-3));
        color *= 1 - _WortBottomParams.z * (1 - k);
    }
    if (_WortFogParams.w > 0.5)
    {
        float f = smoothstep(_WortFogParams.x, _WortFogParams.x - max(_WortFogParams.y, 1e-3), P.y);
        float t = _WortFogColor.a;
        float n = WortNoise2(P.xz * 0.12 + float2(t * 0.05, t * 0.02)) * 0.6 + WortNoise2(P.xz * 0.37 - float2(t * 0.03, -t * 0.04)) * 0.4;
        f *= _WortFogParams.z * (0.7 + 0.6 * n);
        color = lerp(color, _WortFogColor.rgb, saturate(f));
    }
    return color;
}

#endif
