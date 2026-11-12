using System;

namespace Wort.Generation
{
    /// <summary>
    /// 占位贴图（程序生成，128×128，可平铺，1 张 = 1m）：泥土、岩石、路面。以后换成美术贴图，接口不变。
    /// 预览工具和 Unity 共用同一份，结果一致。像素为 0xRRGGBB；岩石另给一张高度（0–1），融合时让石头从泥土里“透出来”。
    /// </summary>
    public static class TerrainTextures
    {
        public const int Size = 128;

        public static uint[] Dirt(out float[] height) => Make(0xD1A, out height, false);
        public static uint[] Rock(out float[] height) => Make(0x60C, out height, true);
        public static uint[] Road(out float[] height) => Make(0x40AD, out height, false, true);

        static uint[] Make(uint seed, out float[] height, bool rock, bool road = false)
        {
            int n = Size;
            var col = new uint[n * n];
            height = new float[n * n];
            // 可平铺的噪声：在格点上取哈希，双线性插值，坐标按 Size 取模
            float Noise(int cell, uint s, float x, float y)
            {
                float fx = x / cell, fy = y / cell;
                int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                float tx = fx - ix, ty = fy - iy;
                tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                int m = n / cell;
                float V(int a, int b) => (Hash.Of(((a % m) + m) % m, ((b % m) + m) % m, s) & 0xFFFF) / 65535f;
                float v00 = V(ix, iy), v10 = V(ix + 1, iy), v01 = V(ix, iy + 1), v11 = V(ix + 1, iy + 1);
                return (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
            }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float big = Noise(32, seed, x, y), mid = Noise(16, seed + 1, x, y), fine = Noise(4, seed + 2, x, y), grain = Noise(2, seed + 3, x, y);
                    float r, g, b, hgt;
                    if (road)
                    {
                        // 路面：踩实的浅黄褐土，比泥土稍浅、平整；只有零星小碎石，不要看成石头
                        float v = 0.5f * big + 0.3f * mid + 0.2f * grain;
                        float gravel = Noise(2, seed + 4, x, y) * 0.6f + Noise(8, seed + 5, x, y) * 0.4f;
                        hgt = 0.35f + 0.3f * v;
                        r = 0.52f + 0.14f * v; g = 0.40f + 0.11f * v; b = 0.27f + 0.08f * v;
                        if (gravel > 0.82f) { float k = 0.60f + 0.2f * fine; r = k * 0.95f; g = k * 0.86f; b = k * 0.72f; hgt = 0.7f; }
                    }
                    else if (!rock)
                    {
                        // 泥土：暖褐，大块深浅 + 细颗粒；零星小石子
                        float v = 0.55f * big + 0.25f * mid + 0.2f * grain;
                        float pebble = Noise(4, seed + 4, x, y) * 0.6f + Noise(16, seed + 5, x, y) * 0.4f;
                        hgt = v;
                        r = 0.40f + 0.20f * v; g = 0.28f + 0.15f * v; b = 0.18f + 0.09f * v;
                        if (pebble > 0.83f) { float k = 0.50f + 0.25f * fine; r = k * 0.80f; g = k * 0.72f; b = k * 0.62f; hgt = 0.85f; }
                    }
                    else
                    {
                        // 岩石：偏暖的灰，块面明暗 + 裂纹（mid 噪声的等值线）
                        float v = 0.45f * big + 0.35f * mid + 0.2f * fine;
                        float crack = Math.Abs(big - 0.5f) < 0.012f ? 0.7f : 1f;
                        hgt = v * crack;
                        float k = (0.40f + 0.26f * v) * crack;
                        r = k * 1.05f; g = k * 0.96f; b = k * 0.86f;
                    }
                    height[y * n + x] = hgt;
                    col[y * n + x] = (uint)(Clamp255(r) << 16 | Clamp255(g) << 8 | Clamp255(b));
                }
            return col;
        }

        /// <summary>
        /// 防平铺重复的采样坐标：在 1m 一张的坐标之外，再取一组 2.3m 一张、错开的坐标，两次采样按 0.5 平均（着色器里同样做）。
        /// </summary>
        public static void Uv2(double u, double v, out double u2, out double v2) { u2 = u * 0.43 + 0.31; v2 = v * 0.43 + 0.17; }

        static uint Clamp255(float v) { int i = (int)(v * 255 + 0.5f); return (uint)(i < 0 ? 0 : (i > 255 ? 255 : i)); }

        /// <summary>
        /// 融合：权重 w（0 泥土 … 1 岩石）加上两张贴图的高度差，得到岩石占比。高的那张先露出来，交界不是一条直的渐变。
        /// Unity 着色器里用同一个公式（Wort/TerrainBlend）。
        /// </summary>
        /// <summary>
        /// 路面融合：权重 w（0 泥土 … 1 路面，来自 <see cref="RoadSurface"/>）加上两张贴图的高度差，得到路面占比；
        /// 路边是一圈被踩出来的碎边，不是直线。Unity 着色器里用同一个公式（Wort/TerrainBlend）。
        /// </summary>
        public static float MixRoad(float w, float dirtH, float roadH)
        {
            float t = w + (roadH - dirtH) * 0.8f * (1 - Math.Abs(2 * w - 1));
            t = (t - 0.5f) / 0.15f + 0.5f;
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }

        public static float Mix(float w, float dirtH, float rockH)
        {
            float t = w + (rockH - dirtH) * 0.6f * (1 - Math.Abs(2 * w - 1));
            t = (t - 0.5f) / 0.12f + 0.5f; // 交界宽度
            return t < 0 ? 0 : (t > 1 ? 1 : t);
        }
    }
}
