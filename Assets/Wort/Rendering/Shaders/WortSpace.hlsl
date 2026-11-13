// 空间呈现（GDD 第 7 节）：遮挡虚化、高度裁切（鹰眼视觉 / 室内地下共用）、最低层压暗、主角脚下的薄雾。
// 参数由 Wort.Rendering.SpatialView 每帧设置；场景里没有这个组件时全部关闭（各开关默认 0）。
// - 只在 ForwardLit / DepthOnly / DepthNormals 里裁；ShadowCaster 不裁 → 被裁掉的东西照常投影（负责人 2026-11-13）。
#ifndef WORT_SPACE_INCLUDED
#define WORT_SPACE_INCLUDED

#include "WortAO.hlsl"   // 高度场（WortCellHeight），遮挡虚化的黑边扰动要用

float4 _WortHoleCenter;   // xyz = 主角身体中心（世界），w = 圆的半径（米，屏幕平面里；0 = 关）
float4 _WortHoleParams;   // x = 时间，y = 波浪幅度（比例），z = 边缘渐变带宽（比例），w = 高出主角身体中心多少才裁（米；SpatialView 按“高于脚下 0.5m”换算，可以是负数）
float4 _WortHoleEdge;     // x = 洞里黑色区域边缘往地面啃进去多少（米，和鹰眼视觉同款扰动），y = 开关
float4 _WortCutParams;    // x = 裁切高度，y = 裁切高度的起伏幅度（米），z = 时间，w = 截面外扩（米，盖住侧墙鼓包）
float _WortCutOn;         // 1 = 裁切开（默认 0：场景里没有 SpatialView 时什么都不裁）
float4 _WortCutCircle;    // 裁切范围（世界水平圆，和遮挡虚化同一套波浪 + 溶解）：xy = 圆心 xz，z = 半径（≤ 0 = 不限范围），w = 波浪幅度（比例）
float4 _WortCutStyle;     // x = 圆边参差的深度（米：边界往里不规则地缩进多少），y / z = 蓝色外扩边最细 / 最粗（像素），w = 外扩边开关
float4 _WortCutRimColor;  // rgb = 外扩边颜色（线性，可以大于 1）
float4 _WortEagleDim;     // 鹰眼视觉的反选区压暗：x = 比裁切圆再大多少（米），y = 压暗程度，z = 边缘软化宽度（米），w = 过渡（0–1）
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

// 圆的波浪边（遮挡虚化和鹰眼视觉共用）：半径倍率，th = 角度，t = 时间，amp = 幅度
float WortWave(float th, float t, float amp)
{
    return 1 + amp * (0.6 * sin(5 * th + 1.7 * t) + 0.4 * sin(9 * th - 2.3 * t + 1.3));
}

// 溶解用的噪声（按世界坐标，图案不跟着屏幕游动）
float WortDissolveNoise(float2 xz, float t)
{
    return WortNoise2(xz * 5 + t * 0.6) * 0.7 + WortNoise2(xz * 13 - t) * 0.3;
}

// 裁切高度（带起伏，截面边缘不是一刀切的直线）
float WortCutHeight(float2 xz)
{
    float t = _WortCutParams.z;
    float n = WortNoise2(xz * 0.9 + t * 0.3) * 0.6 + WortNoise2(xz * 2.6 - t * 0.5) * 0.4;
    return _WortCutParams.x + _WortCutParams.y * (n * 2 - 1);
}

// 鹰眼视觉的圆形范围（米，> 0 = 在圈内）：波浪边 + 连续的参差噪声（往里缩 0 到 _WortCutStyle.x 米）。
// 地形裁切、黑色横截面、蓝色外扩边都用这一个函数，三者的边界完全对齐。
float WortCutCircleSigned(float2 xz)
{
    float R = _WortCutCircle.z;
    float2 d = xz - _WortCutCircle.xy;
    float dist = length(d);
    float rMax = R * (1 + _WortCutCircle.w);
    if (dist > rMax + 0.5) return rMax - dist;   // 离圆远：不算角度和噪声（画面上大部分像素走这里）
    float t = _WortCutParams.z;
    float r = R * WortWave(atan2(d.y, d.x), t, _WortCutCircle.w);
    float rag = WortNoise2(xz * 2.2 + t * 0.25) * 0.65 + WortNoise2(xz * 6.1 - t * 0.4) * 0.35;
    return r - _WortCutStyle.x * rag - dist;
}

// 鹰眼视觉的裁切量（米，> 0 = 该裁）：在圈内且高于裁切高度。两项取小 = 两个条件同时满足
float WortCutSigned(float3 P)
{
    if (_WortCutCircle.z <= 0) return P.y - WortCutHeight(P.xz);
    float inCircle = WortCutCircleSigned(P.xz);
    if (inCircle < -0.3) return inCircle;    // 圈外较远：不用算裁切高度（0.3m 以内要算，否则圈外比裁切面低的地面也会描上蓝边）
    return min(P.y - WortCutHeight(P.xz), inCircle);
}

// 屏幕像素抖动阈值（0–1），用来在不透明材质上做“半透明渐变”（interleaved gradient noise）
float WortDither(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// 遮挡虚化：主角几乎被挡住时，挡在前面的东西在屏幕上挖一个圆。
// - 圆边：波浪线（随时间流动）；边缘一圈按距离从不透明渐变到透明（抖动），再叠一点世界坐标噪声，边不规整。
// - 洞里能看到被挖开的崖体内部（黑色空处），它和地面的分界是体素格线（直角）：洞里贴着“被挖的高柱子”的地面，
//   按噪声往里啃掉一圈（和鹰眼视觉同款扰动），直角边变成参差的边。
void WortHoleClip(float3 P)
{
    float3 nrm = normalize(cross(ddy(P), ddx(P)));   // 屏幕导数求面法线：放在所有分支之前（导数不能在分支里算）
    float R = _WortHoleCenter.w;
    if (R <= 1e-3) return;
    float3 C = _WortHoleCenter.xyz;
    float4 pc = mul(UNITY_MATRIX_VP, float4(P, 1));
    float4 cc = mul(UNITY_MATRIX_VP, float4(C, 1));
    float4 rc = mul(UNITY_MATRIX_VP, float4(C + UNITY_MATRIX_V[0].xyz * R, 1));   // 相机右方 R 米处 → 屏幕上的半径
    float2 hs = _ScreenParams.xy * 0.5;
    float2 pn = pc.xy / pc.w, cn = cc.xy / cc.w;
    float2 d = (pn - cn) * hs;
    float rr = length((rc.xy / rc.w - cn) * hs);
    float t = _WortHoleParams.x;
    float x = length(d) / max(rr * WortWave(atan2(d.y, d.x), t, _WortHoleParams.y), 1e-3);   // < 1 在圆内
    if (x >= 1) return;
    float band = max(_WortHoleParams.z, 1e-3);
    float fade = smoothstep(1 - band, 1, x);   // 0 = 圆心（全透），1 = 圆边（不透明）

    // 挡在前面 = 视空间里比主角中心更靠近相机（错切俯视下等价于：屏幕同一处、比主角高 → 在主角南侧、主角和相机之间）
    float dp = -mul(UNITY_MATRIX_V, float4(P, 1)).z;
    float dc = -mul(UNITY_MATRIX_V, float4(C, 1)).z;
    if (dp < dc - _WortHoleParams.w)
    {
        float n = WortDissolveNoise(P.xz, t);
        float alpha = saturate(fade + (n - 0.5) * 0.35 * band);   // 渐变 + 一点噪声
        float2 pixel = (pn * 0.5 + 0.5) * _ScreenParams.xy;
        clip(alpha - WortDither(pixel) * 0.999 - 0.0005);
        return;
    }

    // 洞里、主角这一侧的地面：贴着被挖开的高柱子（高过主角中心 + 余量，即被挖掉的那些）按噪声啃掉一圈
    float bite = _WortHoleEdge.x * (1 - fade);
    if (_WortHoleEdge.y < 0.5 || bite <= 1e-3 || _WortHeightRect.z < 1) return;
    if (abs(nrm.y) < 0.7) return;   // 只啃顶面（地面），墙不动
    float cutTop = C.y + _WortHoleParams.w;
    float2 org = _WortHeightRect.xy;
    int2 c0 = (int2)floor(P.xz - org);
    float dmin = 1e3;
    [unroll] for (int dz = -1; dz <= 1; dz++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        if (dx == 0 && dz == 0) continue;
        int2 c = c0 + int2(dx, dz);
        float2 bmin = org + float2(c);
        float2 q = clamp(P.xz, bmin, bmin + 1);
        if (WortCellHeight(c, q) > max(cutTop, P.y + 0.6)) dmin = min(dmin, length(q - P.xz));
    }
    if (dmin > bite) return;
    clip(dmin - bite * WortDissolveNoise(P.xz * 0.8, t * 0.5));
}

// 鹰眼视觉 / 高度裁切：返回裁切量（没开时返回 −1e6），边缘溶解带里按噪声留一部分
float WortHeightClip(float3 P)
{
    if (_WortCutOn < 0.5) return -1e6;
    float s = WortCutSigned(P);
    clip(-s);   // 边界本身已经是参差的连续曲线（不再碎点溶解），横截面和蓝边用同一条边界
    return s;
}

// 裁切（ForwardLit / DepthOnly / DepthNormals 都调；ShadowCaster 不调）。返回鹰眼视觉的裁切量，给 WortCutRim 用
float WortSpaceClip(float3 P)
{
    float s = WortHeightClip(P);
    WortHoleClip(P);
    return s;
}

// 蓝色外扩边：裁切边界外侧留下的像素，离边界 3–5 像素以内（宽度沿边浮动）涂成边色
half3 WortCutRim(half3 color, float s, float3 P)
{
    if (_WortCutOn < 0.5 || _WortCutStyle.w < 0.5 || s < -1e5) return color;
    float px = -s / max(fwidth(s), 1e-5);   // 离裁切边界多少像素（> 0 = 外侧，留下的部分）
    if (px > _WortCutStyle.z + 1) return color;   // 离边界远：不算噪声
    float t = _WortCutParams.z;
    float w = lerp(_WortCutStyle.y, _WortCutStyle.z, WortNoise2(P.xz * 1.3 + t * 0.8));
    float k = 1 - smoothstep(w - 1, w, px);
    return lerp(color, _WortCutRimColor.rgb, saturate(k));
}

// 最低一层压暗 + 主角脚下薄雾（光照算完以后调）
half3 WortSpaceShade(half3 color, float3 P)
{
    if (_WortBottomParams.w > 0.5)
    {
        float k = saturate((P.y - _WortBottomParams.x) / max(_WortBottomParams.y, 1e-3));
        color *= 1 - _WortBottomParams.z * (1 - k);
    }
    if (_WortEagleDim.w > 1e-3 && _WortCutCircle.z > 0)
    {
        // 鹰眼视觉：裁切圆再往外 _WortEagleDim.x 米以外整片压暗（圆边同一套波浪）
        float2 d = P.xz - _WortCutCircle.xy;
        float dist = length(d);
        float r = (_WortCutCircle.z + _WortEagleDim.x) * WortWave(atan2(d.y, d.x), _WortCutParams.z, _WortCutCircle.w);
        float k = smoothstep(r, r + max(_WortEagleDim.z, 1e-3), dist);
        color *= 1 - _WortEagleDim.y * _WortEagleDim.w * k;
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
