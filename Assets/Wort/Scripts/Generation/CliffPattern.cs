using System;

namespace Wort.Generation
{
    /// <summary>
    /// 崖面正面的程序化高度图（负责人 2026-11-12：正面靠法线，几何只稍微挤出）。
    /// 输入墙面坐标 (u 沿墙, v 世界高度，单位 m)、在墙上的相对高度 rel（0 崖脚 … 1 崖顶），
    /// 输出往外的高度（m，约 0–0.12）、暗部 ao（1 = 不压暗）、石块色调 tone（0–1）。
    /// 着色器逐像素算它的梯度得到法线（预览与 Unity 用同一套公式）；网格只在 0.1m 的顶点上按它挤出一点。
    /// - 人类区：双层噪声。大 Voronoi 切出大石块（越往上越扁），石块之间是深缝；每块内部再用细 Voronoi 切成小刻面，每个刻面一个随机倾斜的平面。
    /// - 森林：圆石，Voronoi 0.42m，每块中间鼓成枕头，石缝较浅。
    /// - 沼泽：扁平的细胞（横 1.5m、竖 0.3m 的拉长 Voronoi），一片片扁板，上缘圆、各片前后倾斜不同；越往上越平缓。
    /// 只用加减乘除和开方，结果在 .NET 与 Unity 一致。
    /// </summary>
    public static class CliffPattern
    {
        static float Unit(uint h) => (h & 0xFFFF) / 65535f;
        static float Sat(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        static float Smooth(float a, float b, float v) { float t = Sat((v - a) / (b - a)); return t * t * (3 - 2 * t); }

        /// <summary>
        /// 拉伸 Voronoi：坐标 (u / su, v / sv)。返回最近点的哈希 id、最近点（m）、到相邻块交界的近似距离 eM（m）。
        /// </summary>
        static uint Vor(float u, float v, float su, float sv, uint seed, out float cu, out float cv, out float eM)
        {
            float fu = u / su, fv = v / sv;
            int gu = (int)Math.Floor(fu), gv = (int)Math.Floor(fv);
            float d1 = 1e9f, d2 = 1e9f; uint id = 0; float pu1 = 0, pv1 = 0, pu2 = 0, pv2 = 0;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    uint hh = Hash.Of(gu + i, gv + j, seed);
                    float pu = gu + i + 0.1f + 0.8f * Unit(hh), pv = gv + j + 0.1f + 0.8f * Unit(hh >> 16);
                    float du = fu - pu, dv = fv - pv, d = du * du + dv * dv; // 在拉伸后的坐标里比较，细胞才是扁的
                    if (d < d1) { d2 = d1; pu2 = pu1; pv2 = pv1; d1 = d; id = hh; pu1 = pu; pv1 = pv; }
                    else if (d < d2) { d2 = d; pu2 = pu; pv2 = pv; }
                }
            cu = pu1 * su; cv = pv1 * sv;
            // 到两点中垂线的距离（精确的交界距离）
            // 交界距离：在拉伸坐标里到中垂线的距离，再按交界方向换回米
            float mx = (pu1 + pu2) * 0.5f, my = (pv1 + pv2) * 0.5f, nxv = pu2 - pu1, nyv = pv2 - pv1;
            float l = (float)Math.Sqrt(nxv * nxv + nyv * nyv);
            float es = l < 1e-6f ? 0 : ((mx - fu) * nxv + (my - fv) * nyv) / l;
            if (es < 0) es = 0;
            // 交界法线方向在米制下的尺度：沿 u 的分量乘 su，沿 v 的乘 sv
            float ku = l < 1e-6f ? 0 : nxv / l, kv = l < 1e-6f ? 0 : nyv / l;
            eM = es / (float)Math.Sqrt(ku * ku / (su * su) + kv * kv / (sv * sv));
            return id;
        }

        /// <summary>一面墙（法线 (nx, nz)、所在平面坐标 plane）的花纹种子：同一平面上的墙共用，网格和着色器算出来一致。</summary>
        public static uint PlaneSeed(float nx, float nz, float plane)
            => (uint)((int)Math.Floor(plane * 2 + 0.5f) * 7919 + (nx > 0.5f ? 1 : nx < -0.5f ? 2 : nz > 0.5f ? 3 : 4) * 104729 + 1);

        public static float Height(CliffStyle st, float u, float v, float rel, uint seed, out float ao, out float tone)
        {
            rel = Sat(rel);
            switch (st)
            {
                case CliffStyle.Human:
                {
                    // 大石块：宽约 0.75m、高约 0.5m，越往上越扁、越宽
                    float su = 0.75f * (1 + 0.25f * rel), sv = 0.52f * (1 - 0.4f * rel);
                    uint id = Vor(u, v, su, sv, seed + 11, out _, out _, out float e);
                    float prof = Smooth(0.01f, 0.1f, e); // 石块边缘圆，缝宽约 2cm
                    // 小刻面：细 Voronoi 约 0.2m，每个刻面一个倾斜平面（随机挤出）
                    uint fid = Vor(u, v, 0.2f, 0.2f, seed + 12 + (id & 0xFF), out float fu, out float fv, out _);
                    float gu = (Unit(fid >> 3) - 0.5f) * 0.9f, gv = (Unit(fid >> 9) - 0.5f) * 0.9f;
                    float facet = 0.02f * Unit(fid) + gu * (u - fu) + gv * (v - fv);
                    float h = 0.05f + 0.035f * Unit(id >> 5) * (1 - 0.5f * rel) + facet;
                    if (h < 0.015f) h = 0.015f;
                    h *= prof;
                    ao = (0.25f + 0.75f * Smooth(0.005f, 0.07f, e)) * (0.8f + 0.2f * Sat(h / 0.08f));
                    tone = Unit(id >> 13);
                    return h;
                }
                case CliffStyle.Forest:
                {
                    uint id = Vor(u, v, 0.42f, 0.38f, seed + 21, out _, out _, out float e);
                    float t = Sat(e / 0.15f);
                    float pillow = (float)Math.Sqrt(t * (2 - t)); // 圆顶
                    float h = (0.045f + 0.02f * Unit(id >> 5)) * pillow;
                    ao = 0.55f + 0.45f * Smooth(0, 0.05f, e); // 石缝较浅（负责人：森林沟壑太深）
                    tone = Unit(id >> 13);
                    return h;
                }
                case CliffStyle.Swamp:
                {
                    // 扁板：横 1.5m、竖 0.3m 的拉长 Voronoi，全程扁平；每片上缘圆、整片前后倾斜（前后方向的起伏），越往上越平缓
                    float calm = 1 - 0.55f * rel;
                    uint id = Vor(u, v, 1.5f, 0.3f, seed + 31, out float cu, out float cv, out float e);
                    float dome = Smooth(0, 0.09f, e);
                    float lean = (Unit(id >> 7) - 0.3f) * 0.25f * (v - cv) + (Unit(id >> 11) - 0.5f) * 0.06f * (u - cu);
                    float h = (0.035f + 0.04f * Unit(id >> 5) * calm + lean) * dome;
                    if (h < 0) h = 0;
                    ao = (0.4f + 0.6f * Smooth(0, 0.035f + 0.04f * (1 - calm), e)) * (0.9f + 0.1f * calm);
                    tone = Unit(id >> 13);
                    return h;
                }
                default:
                    ao = 1; tone = 0.5f;
                    return 0;
            }
        }
    }
}
