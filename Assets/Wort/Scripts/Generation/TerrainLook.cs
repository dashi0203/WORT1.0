using System;

namespace Wort.Generation
{
    /// <summary>崖面几何样式（每种 Biome 选一种，见 <see cref="TerrainLooks"/>）。</summary>
    public enum CliffStyle : byte
    {
        Mountain = 0, // 现在的做法：两层 Voronoi 外鼓（山地、荒原、海岸临时）
        Human = 1,    // 有棱角的大石块砌墙，顶沿一圈浅色路沿石
        Swamp = 2,    // 横向层理：一层层泥层，层缝发暗，层线沿墙是波浪
        Forest = 3,   // 密集的小圆石（枕头形），石缝发暗
    }

    /// <summary>一种 Biome 的地基外观：崖面样式 + 配色（贴图明暗在 A → B 之间取色）。</summary>
    public sealed class BiomeLook
    {
        public BiomeId Id;
        public CliffStyle Style;
        public uint DirtA, DirtB; // 顶面泥土：暗 → 亮
        public uint RockA, RockB; // 侧面岩石
        public uint RimA, RimB;   // 顶沿路沿石（只有人类区样式用）
        /// <summary>顶面边上岩石色往里渗多远（m）：≤ In 全岩石，≥ Out 全泥土。</summary>
        public float RimIn, RimOut;
    }

    /// <summary>
    /// 各 Biome 的地基外观（负责人 2026-11-12 参考图：人类区 / 沼泽 / 森林三款；山地沿用现在的做法，荒原 = 山地换泥土色，海岸临时用山地样式）。
    /// 颜色全部【暂定】占位，等看过预览再调。
    /// </summary>
    public static class TerrainLooks
    {
        public static readonly BiomeLook[] All =
        {
            new BiomeLook { Id = BiomeId.Human, Style = CliffStyle.Human,
                DirtA = 0x9A7024, DirtB = 0xCFA040, RockA = 0x4A3E36, RockB = 0x8E7E6E, RimA = 0xB4A494, RimB = 0xEADCCC, RimIn = 0.35f, RimOut = 0.55f },
            new BiomeLook { Id = BiomeId.MixedForest, Style = CliffStyle.Forest,
                DirtA = 0x9A4022, DirtB = 0xD4743C, RockA = 0x6E2818, RockB = 0xC4643A, RimIn = 0.02f, RimOut = 0.12f },
            new BiomeLook { Id = BiomeId.Conifer, Style = CliffStyle.Forest,
                DirtA = 0x5A4434, DirtB = 0x8E6C50, RockA = 0x40342E, RockB = 0x7C6656, RimIn = 0.02f, RimOut = 0.12f },
            new BiomeLook { Id = BiomeId.Swamp, Style = CliffStyle.Swamp,
                DirtA = 0x6C5034, DirtB = 0x8E8C50, RockA = 0x404822, RockB = 0x828C44, RimIn = 0.02f, RimOut = 0.1f },
            new BiomeLook { Id = BiomeId.Mountain, Style = CliffStyle.Mountain,
                DirtA = 0x66472E, DirtB = 0x9C6E46, RockA = 0x5E564E, RockB = 0xB0A290, RimIn = 0.12f, RimOut = 0.5f },
            new BiomeLook { Id = BiomeId.Moor, Style = CliffStyle.Mountain,
                DirtA = 0x4A3A38, DirtB = 0x7E6658, RockA = 0x5E564E, RockB = 0xB0A290, RimIn = 0.12f, RimOut = 0.5f },
            new BiomeLook { Id = BiomeId.Coast, Style = CliffStyle.Mountain,
                DirtA = 0xB09A6A, DirtB = 0xDCC896, RockA = 0x6A665E, RockB = 0xAAA498, RimIn = 0.12f, RimOut = 0.5f },
        };

        public static BiomeLook Get(BiomeId id) => All[(int)id];
        public const int StyleCount = 4;
    }

    /// <summary>
    /// 按位置查外观：Biome 权重（地图上的渐变带，按格中心双线性）→ 颜色平滑渐变；
    /// 崖面几何不能混（石块和层理混出来是一团糊），所以在过渡带里按“块”挑样式：
    /// 墙面按约 1.4m 的 Voronoi 分块，每块按该处各样式的权重（锐化后）抽签，整块用同一种样式；
    /// 两块样式不同时交界处往里收出一道缝。结果是一种石头逐渐被另一种替代，而不是两种形状互相糊在一起。
    /// 只取决于世界坐标，相邻墙算出的位置一致；不用 System.Random。
    /// </summary>
    public sealed class LookField
    {
        public readonly RegionMap Map;
        public const float PatchSize = 1.4f;

        public LookField(RegionMap map) { Map = map; }

        /// <summary>世界点 (x, z) 处各 Biome 的权重（双线性，和为 1）。</summary>
        public void BiomeWeights(double x, double z, float[] w)
        {
            var m = Map;
            double fx = x - 0.5, fz = z - 0.5;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = (float)(fx - ix), tz = (float)(fz - iz);
            Array.Clear(w, 0, w.Length);
            float sum = 0;
            for (int q = 0; q < 4; q++)
            {
                int qx = ix + (q & 1), qz = iz + (q >> 1);
                if (qx < 0) qx = 0; if (qz < 0) qz = 0; if (qx >= m.W) qx = m.W - 1; if (qz >= m.H) qz = m.H - 1;
                float k = ((q & 1) == 1 ? tx : 1 - tx) * ((q >> 1) == 1 ? tz : 1 - tz);
                int i = qz * m.W + qx;
                bool any = false;
                for (int b = 0; b < w.Length; b++) { float v = m.Weights[b][i]; if (v > 0) { w[b] += v * k; sum += v * k; any = true; } }
                if (!any) { w[m.Biome[i]] += k; sum += k; }
            }
            if (sum > 0) for (int b = 0; b < w.Length; b++) w[b] /= sum;
        }

        [ThreadStatic] static float[] bw;
        [ThreadStatic] static float[] sw;

        /// <summary>各崖面样式的权重（同一样式的 Biome 相加）。</summary>
        public void StyleWeights(double x, double z, float[] s)
        {
            var w = bw ?? (bw = new float[BiomeTable.Count]);
            BiomeWeights(x, z, w);
            Array.Clear(s, 0, s.Length);
            for (int b = 0; b < w.Length; b++) s[(int)TerrainLooks.All[b].Style] += w[b];
        }

        /// <summary>顶面边上岩石色渗入的范围（按 Biome 权重平滑混合）。</summary>
        public void Rim(double x, double z, out float rimIn, out float rimOut)
        {
            var w = bw ?? (bw = new float[BiomeTable.Count]);
            BiomeWeights(x, z, w);
            rimIn = 0; rimOut = 0;
            for (int b = 0; b < w.Length; b++) { rimIn += w[b] * TerrainLooks.All[b].RimIn; rimOut += w[b] * TerrainLooks.All[b].RimOut; }
        }

        /// <summary>
        /// 墙面上一点（沿墙坐标 along、高度 y；墙所在平面 = 法线 (nx, nz) 上的 plane）的样式。
        /// edge = 到“样式不同的相邻块”交界的距离（m）；两边样式相同时为很大的数。
        /// </summary>
        public CliffStyle WallStyle(float nx, float nz, float plane, float along, float y, out float edge)
        {
            float tx = -nz, tz = nx;
            uint seed = (uint)((int)Math.Floor(plane * 4 + 0.5f) * 7919 + (nx > 0.5f ? 1 : nx < -0.5f ? 2 : nz > 0.5f ? 3 : 4) * 104729);
            float G = PatchSize;
            int gu = (int)Math.Floor(along / G), gv = (int)Math.Floor(y / G);
            float d1 = 1e9f, d2 = 1e9f; int k1u = 0, k1v = 0, k2u = 0, k2v = 0; float c1 = 0, c2 = 0;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    uint hh = Hash.Of(gu + i, gv + j, seed);
                    float cu = (gu + i + 0.15f + 0.7f * (hh & 0xFFFF) / 65535f) * G, cv = (gv + j + 0.15f + 0.7f * (hh >> 16) / 65535f) * G;
                    float du = along - cu, dv = y - cv, d = du * du + dv * dv;
                    if (d < d1) { d2 = d1; k2u = k1u; k2v = k1v; c2 = c1; d1 = d; k1u = gu + i; k1v = gv + j; c1 = cu; }
                    else if (d < d2) { d2 = d; k2u = gu + i; k2v = gv + j; c2 = cu; }
                }
            var s1 = PatchStyle(nx, nz, tx, tz, plane, c1, k1u, k1v, seed);
            var s2 = PatchStyle(nx, nz, tx, tz, plane, c2, k2u, k2v, seed);
            edge = s1 == s2 ? 1e9f : ((float)Math.Sqrt(d2) - (float)Math.Sqrt(d1)) * 0.5f;
            return s1;
        }

        CliffStyle PatchStyle(float nx, float nz, float tx, float tz, float plane, float centerAlong, int ku, int kv, uint seed)
        {
            var s = sw ?? (sw = new float[TerrainLooks.StyleCount]);
            double x = nx * plane + tx * centerAlong, z = nz * plane + tz * centerAlong;
            StyleWeights(x - nx * 0.5, z - nz * 0.5, s); // 墙所在格（墙面往格内半格）的权重
            // 锐化：过渡带中间那一段才真正混排，两端基本是单一样式
            float sum = 0;
            for (int k = 0; k < s.Length; k++) { float v = s[k] * s[k] * s[k]; s[k] = v; sum += v; }
            float r = (Hash.Of(ku, kv, seed ^ 0x51ED) & 0xFFFF) / 65536f * sum;
            for (int k = 0; k < s.Length; k++) { r -= s[k]; if (r < 0) return (CliffStyle)k; }
            for (int k = s.Length - 1; k >= 0; k--) if (s[k] > 0) return (CliffStyle)k;
            return CliffStyle.Mountain;
        }

        /// <summary>某一点权重最大的样式（顶沿路沿石、顶面渗色用）。</summary>
        public CliffStyle MainStyle(double x, double z)
        {
            var s = sw ?? (sw = new float[TerrainLooks.StyleCount]);
            StyleWeights(x, z, s);
            int best = 0; for (int k = 1; k < s.Length; k++) if (s[k] > s[best]) best = k;
            return (CliffStyle)best;
        }
    }
}
