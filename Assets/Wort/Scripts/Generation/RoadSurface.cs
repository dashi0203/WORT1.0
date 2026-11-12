using System;

namespace Wort.Generation
{
    /// <summary>
    /// 路面遮罩（《世界生成规格》5.5.1）：每格一个 0–1 的路面权重，网格顶点按格中心双线性取值，
    /// 着色器再用路面 / 泥土两张贴图的高度差决定交界（<see cref="TerrainTextures.MixRoad"/>），边缘自然、不用拼块。
    /// - 道路格（<see cref="CellFlags.Road"/>）和坡道格（<see cref="CellFlags.Ramp"/>）= 1；
    /// - 坡道两头（坡顶、坡底）各外溢 1 格，权重 <see cref="Spill"/>，在这一格里淡出。
    /// 只有朝上的面（法线 y &gt; 0.6，含 45° 斜坡）显示路面，崖壁不显示。
    /// </summary>
    public static class RoadSurface
    {
        public const float Spill = 0.5f;

        public static float[] BuildMask(RegionMap m)
        {
            int W = m.W, H = m.H;
            var mask = new float[W * H];
            for (int i = 0; i < mask.Length; i++)
                if ((m.Flags[i] & (CellFlags.Road | CellFlags.Ramp)) != 0) mask[i] = 1f;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x, up = m.Slope[i];
                    if (up < 0 || (m.Flags[i] & CellFlags.Ramp) == 0) continue;
                    // 坡顶：往上那格与斜坡顶同高；坡底：往下那格与斜坡底同高
                    for (int e = 0; e < 2; e++)
                    {
                        int d = e == 0 ? up : (up + 2) & 3;
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int n = nz * W + nx;
                        if ((m.Flags[n] & CellFlags.Ramp) != 0 || m.Slope[n] >= 0) continue;
                        if (m.Height[n] == m.Height[i] + (e == 0 ? 1 : 0) && mask[n] < Spill) mask[n] = Spill;
                    }
                }
            return mask;
        }

        /// <summary>世界坐标 (x, z) 处的路面权重：按格中心双线性插值。</summary>
        public static float Weight(float[] mask, int W, int H, float x, float z)
        {
            float fx = x - 0.5f, fz = z - 0.5f;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = fx - ix, tz = fz - iz;
            float M(int a, int b) => a < 0 || b < 0 || a >= W || b >= H ? 0f : mask[b * W + a];
            return (M(ix, iz) * (1 - tx) + M(ix + 1, iz) * tx) * (1 - tz) + (M(ix, iz + 1) * (1 - tx) + M(ix + 1, iz + 1) * tx) * tz;
        }

        /// <summary>这一格或它周围 8 格有没有路面（有的话顶面不能和别的平地合并，要逐格细分写权重）。</summary>
        public static bool Near(float[] mask, int W, int H, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int a = x + dx, b = z + dz;
                    if (a >= 0 && b >= 0 && a < W && b < H && mask[b * W + a] > 0) return true;
                }
            return false;
        }
    }
}
