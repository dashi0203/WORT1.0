// 世界空间环境光遮蔽（替代屏幕空间 SSAO；负责人 2026-11-13：SSAO 随相机跳变、屏幕边缘发黑）。
// 只乘环境光（直射光默认不乘，强光下几乎看不出，阴影里 / 阴天才明显）。参数由 Wort.Rendering.WorldAO 设置。
// - 地形：按体素高度场（_WortHeightTex，1 格 1 像素，R = 高度，G = 坡向 + 1）看周围 8 格比这一点高多少、离多近；
//   墙面另外加“离墙脚多高”。崖脚、内拐角、墙根自然变暗，固定在地面上，不随相机变化。
// - 物体：AOOccluder 登记的胶囊（主角等），按“挡住多少天空”估算（半径² / 距离² × 朝向），会动的物体也能用。
#ifndef WORT_AO_INCLUDED
#define WORT_AO_INCLUDED

TEXTURE2D(_WortHeightTex);
float4 _WortHeightRect;   // x0, z0（世界），宽、深（格）
float4 _WortAOParams;     // x = 开关，y = 地形强度，z = 地形半径（m），w = 物体强度
float4 _WortAOParams2;    // x = 调试显示（0 关，1 合成，2 只看地形，3 只看物体），y = 直射光也乘 AO 的比例
float4 _WortOccA[16];     // 胶囊下端中心 xyz，半径 w
float4 _WortOccB[16];     // 胶囊上端中心 xyz
float _WortOccCount;

// 格 c 在 q（世界 xz）处的高度：普通格 = 高度；坡道格 = 坡面在 q 处的高度
float WortCellHeight(int2 c, float2 q)
{
    int2 size = int2(_WortHeightRect.zw);
    c = clamp(c, int2(0, 0), size - 1);
    float2 hv = LOAD_TEXTURE2D(_WortHeightTex, uint2(c)).rg;
    float h = hv.r;
    int s = (int)round(hv.g) - 1;
    if (s >= 0)
    {
        float2 l = saturate(q - (_WortHeightRect.xy + float2(c)));
        h += s == 0 ? l.y : s == 1 ? l.x : s == 2 ? 1 - l.y : 1 - l.x;
    }
    return h;
}

// 地形 AO：1 = 不遮，越小越暗（已按强度换算）
float WortTerrainAO(float3 P, float3 n)
{
    if (_WortAOParams.x < 0.5 || _WortAOParams.y <= 0 || _WortHeightRect.z < 1) return 1;
    float R = max(_WortAOParams.z, 0.05);
    bool wall = abs(n.y) < 0.5;
    float2 org = _WortHeightRect.xy;
    // 参考格：顶面 = 自己这格；墙面 = 墙前面那格（墙往外鼓时点已经在那一格里，再往外推一点保险）
    int2 c0 = (int2)floor(P.xz + n.xz * 0.02 - org);
    float occ = 0;
    [unroll] for (int dz = -1; dz <= 1; dz++)
    [unroll] for (int dx = -1; dx <= 1; dx++)
    {
        if (dx == 0 && dz == 0) continue;
        if (wall && dot(float2(dx, dz), n.xz) < -0.5) continue;   // 墙背后的格（墙自己）不算
        int2 c = c0 + int2(dx, dz);
        float2 bmin = org + float2(c);
        float2 q = clamp(P.xz, bmin, bmin + 1);
        float d = length(q - P.xz);
        float v = saturate(WortCellHeight(c, q) - P.y);           // 比这一点高多少（1m 封顶）
        occ += v * (1 - smoothstep(0, R, d)) * (dx != 0 && dz != 0 ? 0.5 : 1);
    }
    if (wall)
    {
        float hf = WortCellHeight(c0, P.xz);                       // 墙脚那格地面
        occ += 0.6 * (1 - smoothstep(0, R, P.y - hf));
    }
    return 1 - _WortAOParams.y * (1 - exp(-occ));                 // 强度 = 最多暗多少
}

// 物体 AO（胶囊）
float WortObjectAO(float3 P, float3 n)
{
    if (_WortAOParams.x < 0.5 || _WortAOParams.w <= 0) return 1;
    float ao = 1;
    int cnt = (int)_WortOccCount;
    for (int k = 0; k < 16; k++)
    {
        if (k >= cnt) break;
        float3 a = _WortOccA[k].xyz, b = _WortOccB[k].xyz;
        float r = _WortOccA[k].w;
        float3 ab = b - a;
        float t = saturate(dot(P - a, ab) / max(dot(ab, ab), 1e-5));
        float3 di = a + ab * t - P;
        float l2 = max(dot(di, di), 1e-4);
        if (l2 > r * r * 25) continue;
        float l = sqrt(l2);
        float facing = saturate(dot(n, di / l) * 0.75 + 0.25);
        float o = saturate(r * r / l2) * facing;
        ao *= 1 - _WortAOParams.w * o;
    }
    return ao;
}

// 合成：返回乘在环境光上的系数；debugOut = 调试显示时要输出的灰度（< 0 表示不显示调试）
float WortAO(float3 P, float3 n, out float debugOut)
{
    float t = WortTerrainAO(P, n);
    float o = WortObjectAO(P, n);
    float mode = _WortAOParams2.x;
    debugOut = mode < 0.5 ? -1 : mode < 1.5 ? t * o : mode < 2.5 ? t : o;
    return t * o;
}

// 直射光乘多少（默认 1 = 不乘）
float WortAODirect(float ao)
{
    return lerp(1, ao, saturate(_WortAOParams2.y));
}

#endif
