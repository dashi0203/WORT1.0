using System;
using System.Collections.Generic;

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

        /// <summary>
        /// 世界坐标 (x, z) 处的路面权重：按格中心双线性插值，再整体往里收一点（减 0.15 再放大），
        /// 1 格的路看起来约 0.85m，比 1 格略窄、接近坡道上的路宽（负责人 2026-11-12）。
        /// </summary>
        public static float Weight(float[] mask, int W, int H, float x, float z)
        {
            float w = RawWeight(mask, W, H, x, z);
            w = (w - Shrink) / (1 - Shrink);
            return w < 0 ? 0 : w;
        }

        /// <summary>路面整体往里收的量（权重减去这个值再放大）。</summary>
        public const float Shrink = 0.15f;

        static float RawWeight(float[] mask, int W, int H, float x, float z)
        {
            float fx = x - 0.5f, fz = z - 0.5f;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            float tx = fx - ix, tz = fz - iz;
            float M(int a, int b) => a < 0 || b < 0 || a >= W || b >= H ? 0f : mask[b * W + a];
            return (M(ix, iz) * (1 - tx) + M(ix + 1, iz) * tx) * (1 - tz) + (M(ix, iz + 1) * (1 - tx) + M(ix + 1, iz + 1) * tx) * tz;
        }

        /// <summary>坡道口：坡道和外面地面的交界线中点、往外的方向、横向方向、坡道上路面的半宽。</summary>
        public struct Mouth { public float X, Z, Dx, Dz, Px, Pz, Half; }

        /// <summary>漏斗长度（m）：离坡道口这么远以内，外面的路逐渐收到坡道上的路宽。</summary>
        public const float FunnelLength = 4f;

        /// <summary>每条坡道的两个口（坡顶、坡底）。</summary>
        public static List<Mouth> BuildMouths(RegionMap m)
        {
            var list = new List<Mouth>();
            foreach (var r in m.Ramps)
            {
                int d = r.DownDir, p = (d + 1) & 3;
                float dx = Grid.DX4[d], dz = Grid.DZ4[d], px = Grid.DX4[p], pz = Grid.DZ4[p];
                float cx = r.TopX + 0.5f + px * (r.Width - 1) * 0.5f, cz = r.TopZ + 0.5f + pz * (r.Width - 1) * 0.5f;
                // 坡道上看得见的路面半宽：坡道半宽减去两侧的岩石边和泥土（和 CliffMesher 的路面边距一致，约 0.45m）
                float half = Math.Max(0.15f, r.Width * 0.5f - 0.45f);
                // 坡顶口：顶格和第一格坡道之间，往外 = 往上坡方向；坡底口：最后一格坡道和落脚格之间，往外 = 往下坡方向
                list.Add(new Mouth { X = cx + dx * 0.5f, Z = cz + dz * 0.5f, Dx = -dx, Dz = -dz, Px = px, Pz = pz, Half = half });
                list.Add(new Mouth { X = cx + dx * (r.Length + 0.5f), Z = cz + dz * (r.Length + 0.5f), Dx = dx, Dz = dz, Px = px, Pz = pz, Half = half });
            }
            return list;
        }

        /// <summary>
        /// 漏斗系数（0–1，乘在路面权重上）：在坡道口外 FunnelLength 以内，允许的路面半宽从坡道上的路宽逐渐放开，
        /// 超出允许宽度的部分平滑地淡掉；不在任何坡道口附近时为 1。
        /// </summary>
        public static float Funnel(List<Mouth> mouths, float x, float z)
        {
            if (mouths == null) return 1f;
            float f = 1f;
            foreach (var mo in mouths)
            {
                float rx = x - mo.X, rz = z - mo.Z;
                float a = rx * mo.Dx + rz * mo.Dz;
                if (a < -1.5f || a > FunnelLength) continue; // 坡口线后面 1.5m（坡道两旁）也按坡道路宽限制
                float o = Math.Abs(rx * mo.Px + rz * mo.Pz);
                if (o > mo.Half + 3.6f) continue; // 只管坡道口正前方那一片（最宽放到坡道路宽 + 3m）
                float t = a < 0 ? 0 : a / FunnelLength;
                float allowed = mo.Half + t * t * 3f;
                float k = (allowed + 0.12f - o) / 0.24f;
                k = k < 0 ? 0 : (k > 1 ? 1 : k);
                k = k * k * (3 - 2 * k);
                if (k < f) f = k;
            }
            return f;
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
