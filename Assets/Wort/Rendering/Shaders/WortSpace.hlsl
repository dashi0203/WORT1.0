// 空间呈现（GDD 第 7 节）：遮挡虚化、高度裁切（鹰眼视觉 / 室内地下共用）、最低层压暗、主角脚下的薄雾。
// 参数由 Wort.Rendering.SpatialView 每帧设置；场景里没有这个组件时全部关闭（各开关默认 0）。
// - 只在 ForwardLit / DepthOnly / DepthNormals 里裁；ShadowCaster 不裁 → 被裁掉的东西照常投影（负责人 2026-11-13）。
#ifndef WORT_SPACE_INCLUDED
#define WORT_SPACE_INCLUDED

#include "WortAO.hlsl"   // 高度场（WortCellHeight），遮挡虚化的黑边扰动要用

float4 _WortHoleCenter;   // xyz = 主角身体中心（世界），w = 圆的半径（米，屏幕平面里；0 = 关）
float4 _WortHoleParams;   // x = 时间，y = 波浪幅度（比例），z = 边缘渐变带宽（比例），w = 裁切高度：只裁高于主角脚下多少米的东西（和鹰眼视觉共用）
float4 _WortHoleEdge;     // x = 边界扰动（米：“在主角前面”的余量起伏幅度），y = 开关，z = 主角脚下高度（世界 y），w = 主角身高（米）
float4 _WortViewDir;      // xyz = 错切投影下真实视线方向（朝相机，单位向量），SpatialView 用投影矩阵的逆算
float4 _WortHoleCone;     // 截头圆锥：x = 主角处（小头）半径 / 大半径，y = 沿视线多远长到大半径（米）
float4 _WortCutParams;    // x = 裁切高度，y = 裁切高度的起伏幅度（米），z = 时间，w = 截面外扩（米，盖住侧墙鼓包）
float _WortCutOn;         // 1 = 裁切开（默认 0：场景里没有 SpatialView 时什么都不裁）
float4 _WortCutCircle;    // 裁切范围（世界水平圆，和遮挡虚化同一套：波浪边 + 不透明度渐变）：xy = 圆心 xz，z = 半径（≤ 0 = 不限范围），w = 波浪幅度（比例）
float4 _WortCutStyle;     // x = 圆边不透明度渐变带宽（半径的比例），y = 黑色横截面边缘扰动（米，往外参差地扩多少）
float4 _WortEagleDim;     // 鹰眼视觉的反选区压暗：x = 比裁切圆再大多少（米），y = 压暗程度，z = 往里渐变的宽度（米），w = 过渡（0–1）
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

// 裁切高度：只往下起伏（0 到 −_WortCutParams.y）。黑色横截面是裁切高度上的一块平面，
// 断口往上起伏会比横截面高、留下残边（负责人 2026-11-14），所以墙的断口永远不高过横截面，参差在横截面下面往下咬。
float WortCutHeight(float2 xz)
{
    float t = _WortCutParams.z;
    float n = WortNoise2(xz * 0.9 + t * 0.3) * 0.6 + WortNoise2(xz * 2.6 - t * 0.5) * 0.4;
    return _WortCutParams.x - _WortCutParams.y * n;
}

// 把点沿错切视线投到主角脚下的高度（返回 xz）。错切画面里高出 h 米的东西在屏幕上上移 h，
// 圆形范围按投影后的位置判断，圆在屏幕上才始终以主角为中心（否则地形越高，遮罩看起来越往上偏）。
float2 WortToFeetPlane(float3 P)
{
    float3 v = _WortViewDir.xyz;
    if (v.y < 0.05) return P.xz;
    return P.xz - v.xz * ((P.y - _WortHoleEdge.z) / v.y);
}

// 鹰眼视觉的圆形范围：返回 x = 离圆心距离 / 波浪半径（< 1 在圈内）。离得远时不算角度和波浪（画面上大部分像素走这里，结果仍连续）
float WortCutCircleX(float2 xz)
{
    float R = max(_WortCutCircle.z, 1e-3);
    float2 d = xz - _WortCutCircle.xy;
    float dist = length(d);
    if (dist > R * (1 + abs(_WortCutCircle.w)) + 0.01) return dist / R;   // 一定在圈外（> 1）
    return dist / (R * WortWave(atan2(d.y, d.x), _WortCutParams.z, _WortCutCircle.w));
}

// 屏幕像素抖动阈值（0–1），用来在不透明材质上做“半透明渐变”（interleaved gradient noise）
float WortDither(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// —— 遮挡虚化（负责人 2026-11-14 第 N 轮：全部改成连续的几何判断，主角怎么走都平滑，没有按格子的直线 / 跳变）——
// 裁掉同时满足下面三条的东西：
// 1. 在沿视线的截头圆锥里（屏幕上以主角为中心的圆，主角那头小、往相机方向变大；波浪边 + 边缘一圈渐变）；
// 2. 在主角前面：沿错切视线（_WortViewDir）比主角身体中心更靠近相机，超过一点余量（余量按噪声起伏，边界参差）——
//    身后的不裁，身旁同一深度的基本不裁；
// 3. 高于“主角脚下 + 裁切高度”（和鹰眼视觉同一个参数），断口只往下起伏（不会比横截面高）。
// 被裁开的实心格由 CutCap（遮挡虚化模式）在“脚下 + 裁切高度”处补黑色横截面，边缘按噪声往外参差（和鹰眼视觉同款）。

// 屏幕上以主角为中心的圆：返回 x = 到圆心的距离 / 波浪半径（< 1 在圈内）。截头圆锥：沿视线往相机方向 _WortHoleCone.y 米内，半径从 _WortHoleCone.x × R 长到 R
float WortHoleScreenX(float3 P)
{
    float R = _WortHoleCenter.w;
    float3 C = _WortHoleCenter.xyz;
    float4 pc = mul(UNITY_MATRIX_VP, float4(P, 1));
    float4 cc = mul(UNITY_MATRIX_VP, float4(C, 1));
    float4 rc = mul(UNITY_MATRIX_VP, float4(C + UNITY_MATRIX_V[0].xyz * R, 1));   // 相机右方 R 米处 → 屏幕上的半径
    float2 hs = _ScreenParams.xy * 0.5;
    float2 cn = cc.xy / cc.w;
    float2 d = (pc.xy / pc.w - cn) * hs;
    float rr = length((rc.xy / rc.w - cn) * hs);
    float along = dot(P - C, _WortViewDir.xyz);                                  // 沿视线离主角多远（朝相机为正）
    rr *= lerp(saturate(_WortHoleCone.x), 1, saturate(along / max(_WortHoleCone.y, 1e-3)));
    return length(d) / max(rr * WortWave(atan2(d.y, d.x), _WortHoleParams.x, _WortHoleParams.y), 1e-3);
}

// 是否在主角前面（规则 2）：沿视线离主角身体中心的距离 > 余量（余量按噪声在 0.15 到 0.15 + 边界扰动 之间起伏）
bool WortHoleInFront(float3 P)
{
    float along = dot(P - _WortHoleCenter.xyz, _WortViewDir.xyz);
    float margin = 0.15 + _WortHoleEdge.x * WortNoise2(P.xz * 1.7 + _WortHoleParams.x * 0.2);
    return along > margin;
}

// 断口高度（规则 3）：脚下 + 裁切高度，只往下起伏（和鹰眼视觉同一个幅度 _WortCutParams.y）
float WortHoleCutHeight(float2 xz)
{
    float t = _WortCutParams.z;
    float n = WortNoise2(xz * 0.9 + t * 0.3) * 0.6 + WortNoise2(xz * 2.6 - t * 0.5) * 0.4;
    return _WortHoleEdge.z + _WortHoleParams.w - _WortCutParams.y * n;
}

void WortHoleClip(float3 P)
{
    if (_WortHoleCenter.w <= 1e-3) return;
    if (P.y <= WortHoleCutHeight(P.xz)) return;   // 规则 3
    float x = WortHoleScreenX(P);
    if (x >= 1) return;                            // 规则 1
    if (!WortHoleInFront(P)) return;               // 规则 2
    float band = max(_WortHoleParams.z, 1e-3);
    float alpha = saturate(smoothstep(1 - band, 1, x) + (WortDissolveNoise(P.xz, _WortHoleParams.x) - 0.5) * 0.35 * band);
    float4 pc = mul(UNITY_MATRIX_VP, float4(P, 1));
    float2 pixel = (pc.xy / pc.w * 0.5 + 0.5) * _ScreenParams.xy;
    clip(alpha - WortDither(pixel) * 0.999 - 0.0005);
}

// 鹰眼视觉 / 高度裁切（和遮挡虚化同一套）：高于裁切高度、在圈内的裁掉；圈边一圈按距离从不透明渐变到透明（屏幕抖动）
void WortHeightClip(float3 P, float4 pc)
{
    if (_WortCutOn < 0.5) return;
    float x = 0;
    if (_WortCutCircle.z > 0)
    {
        x = WortCutCircleX(WortToFeetPlane(P));
        if (x >= 1) return;                       // 圈外：不裁（先判断，省掉裁切高度的噪声）
    }
    if (P.y <= WortCutHeight(P.xz)) return;       // 裁切面以下：不裁
    if (_WortCutCircle.z <= 0) { clip(-1); return; }
    float band = max(_WortCutStyle.x, 1e-3);
    float alpha = saturate(smoothstep(1 - band, 1, x) + (WortDissolveNoise(P.xz, _WortCutParams.z) - 0.5) * 0.35 * band);
    float2 pixel = (pc.xy / pc.w * 0.5 + 0.5) * _ScreenParams.xy;
    clip(alpha - WortDither(pixel) * 0.999 - 0.0005);
}

// 裁切（ForwardLit / DepthOnly / DepthNormals 都调；ShadowCaster 不调）
void WortSpaceClip(float3 P)
{
    float4 pc = mul(UNITY_MATRIX_VP, float4(P, 1));
    WortHeightClip(P, pc);
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
    if (_WortEagleDim.w > 1e-3 && _WortCutCircle.z > 0)
    {
        // 鹰眼视觉：裁切圆 + _WortEagleDim.x 米以外整片压暗；圆边往里渐变 _WortEagleDim.z 米，只带一点点扰动
        float dist = length(WortToFeetPlane(P) - _WortCutCircle.xy);
        float r = _WortCutCircle.z + _WortEagleDim.x + (WortNoise2(P.xz * 0.9 + _WortCutParams.z * 0.2) - 0.5) * 0.25;
        float k = smoothstep(r - max(_WortEagleDim.z, 1e-3), r, dist);
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
