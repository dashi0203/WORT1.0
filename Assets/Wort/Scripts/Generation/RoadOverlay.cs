using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 道路覆盖层（手刷的道路）：一串“格 → 类型”的修改，生成地形之后叠加上去。
    /// 类型 0 = 擦掉这一格的路，1 = 小路，2 = 次干道，3 = 主干道。同一格以最后一次修改为准。
    /// 以后正式的 chunk 编辑工具沿用这一层。纯 C#，Unity 和预览共用。
    /// </summary>
    public static class RoadOverlay
    {
        /// <summary>把修改叠加到地图上；返回实际改动的格（用来决定重建哪些 chunk）。水格不铺路。</summary>
        public static List<int> Apply(RegionMap m, IList<int> xs, IList<int> zs, IList<byte> kinds)
        {
            var changed = new List<int>();
            int n = Math.Min(xs.Count, Math.Min(zs.Count, kinds.Count));
            if (n == 0) return changed;
            m.EnsureRoadKind();
            for (int k = 0; k < n; k++)
            {
                int x = xs[k], z = zs[k];
                if (!m.InBounds(x, z)) continue;
                int i = m.Index(x, z);
                byte kind = kinds[k];
                bool had = (m.Flags[i] & CellFlags.Road) != 0;
                if (kind == RegionMap.RoadNone)
                {
                    if (!had && m.RoadKind[i] == 0) continue;
                    m.Flags[i] &= ~CellFlags.Road;
                    m.RoadKind[i] = 0;
                }
                else
                {
                    if (m.HasWater(i)) continue;
                    if (had && m.RoadKind[i] == kind) continue;
                    m.Flags[i] |= CellFlags.Road;
                    m.RoadKind[i] = kind;
                }
                changed.Add(i);
            }
            return changed;
        }

        /// <summary>笔刷覆盖的格：格中心到笔刷中心 (px, pz) 的距离 ≤ 宽度 / 2（宽 1 = 所在的 1 格，宽 2 约 5 格，宽 3 约 9 格）。</summary>
        public static List<(int x, int z)> Footprint(float px, float pz, int width)
        {
            var cells = new List<(int x, int z)>();
            float r = width * 0.5f, r2 = r * r + 1e-4f;
            int x0 = (int)Math.Floor(px - r), x1 = (int)Math.Floor(px + r), z0 = (int)Math.Floor(pz - r), z1 = (int)Math.Floor(pz + r);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - px, dz = z + 0.5f - pz;
                    if (dx * dx + dz * dz <= r2) cells.Add((x, z));
                }
            if (cells.Count == 0) cells.Add(((int)Math.Floor(px), (int)Math.Floor(pz)));
            return cells;
        }

        /// <summary>
        /// 屏幕射线找地面格：沿射线每 0.1m 走一步，第一次走到某格地面（斜坡按坡面高度）以下就算碰到。
        /// 射线用区域坐标（x 东、z 北、y 向上，1 格 = 1m）。找不到返回 false。
        /// </summary>
        public static bool Pick(RegionMap m, float ox, float oy, float oz, float dx, float dy, float dz, float maxDist,
            out float hitX, out float hitZ)
        {
            hitX = hitZ = 0;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6f) return false;
            dx /= len; dy /= len; dz /= len;
            for (float t = 0; t <= maxDist; t += 0.1f)
            {
                float x = ox + dx * t, y = oy + dy * t, z = oz + dz * t;
                int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
                if (!m.InBounds(cx, cz)) continue;
                int i = m.Index(cx, cz), up = m.Slope[i];
                float top = m.Height[i];
                if (up >= 0) top += (x - (cx + 0.5f)) * Grid.DX4[up] + (z - (cz + 0.5f)) * Grid.DZ4[up] + 0.5f;
                if (m.HasWater(i) && m.Water[i] > top) top = m.Water[i];
                if (y <= top) { hitX = x; hitZ = z; return true; }
            }
            return false;
        }
    }
}
