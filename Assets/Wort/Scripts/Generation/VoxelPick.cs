using System;

namespace Wort.Generation
{
    /// <summary>射线打到的体素面。</summary>
    public struct VoxelHit
    {
        /// <summary>被打到的那一格（柱子）。</summary>
        public int X, Z;
        /// <summary>被打到的方块所在的层（0 起，方块占 y ∈ [Y, Y+1)）。</summary>
        public int Y;
        /// <summary>打到的面：0..3 = 侧面，朝向按 Grid.DX4（北 东 南 西）；4 = 顶面。</summary>
        public int Face;
        /// <summary>打到的点（区域坐标）。</summary>
        public float PX, PY, PZ;
    }

    /// <summary>
    /// 高度场射线检测（Minecraft 式体素编辑用）：沿射线在 xz 平面上逐格走（2D DDA），
    /// 进入某格时如果射线已低于这一格的顶面 → 打到它的侧面（朝向 = 来的方向）；在格内下降到顶面 → 打到顶面。
    /// 地形是每格一根柱子（没有悬空），所以只要这两种情况。区域外不算。
    /// </summary>
    public static class VoxelPick
    {
        public static bool Raycast(RegionMap m, float ox, float oy, float oz, float dx, float dy, float dz, float maxDist, out VoxelHit hit)
        {
            hit = default;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9f) return false;
            dx /= len; dy /= len; dz /= len;
            // 先把起点推进到区域的 xz 包围盒里
            float tEnter = 0, tExit = maxDist;
            if (!Slab(ox, dx, 0, m.W, ref tEnter, ref tExit) || !Slab(oz, dz, 0, m.H, ref tEnter, ref tExit)) return false;
            float t = tEnter + 1e-4f;
            int cx = (int)Math.Floor(ox + dx * t), cz = (int)Math.Floor(oz + dz * t);
            if (cx < 0) cx = 0; if (cz < 0) cz = 0; if (cx >= m.W) cx = m.W - 1; if (cz >= m.H) cz = m.H - 1;
            int sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
            float tdx = Math.Abs(dx) > 1e-9f ? 1 / Math.Abs(dx) : float.MaxValue;
            float tdz = Math.Abs(dz) > 1e-9f ? 1 / Math.Abs(dz) : float.MaxValue;
            float nx = Math.Abs(dx) > 1e-9f ? ((dx > 0 ? cx + 1 - ox : ox - cx) / Math.Abs(dx)) : float.MaxValue;
            float nz = Math.Abs(dz) > 1e-9f ? ((dz > 0 ? cz + 1 - oz : oz - cz) / Math.Abs(dz)) : float.MaxValue;
            int enteredFace = -1; // 进入当前格时穿过的面（朝向来的方向），起点格为 −1
            float t0 = tEnter;
            for (int step = 0; step < 4096; step++)
            {
                float t1 = Math.Min(Math.Min(nx, nz), tExit);
                int h = m.Height[cz * m.W + cx];
                float y0 = oy + dy * t0;
                if (y0 < h && enteredFace >= 0)
                {
                    hit = new VoxelHit { X = cx, Z = cz, Y = (int)Math.Floor(y0), Face = enteredFace, PX = ox + dx * t0, PY = y0, PZ = oz + dz * t0 };
                    return true;
                }
                if (dy < 0)
                {
                    float th = (h - oy) / dy;
                    if (th >= t0 - 1e-5f && th <= t1 + 1e-5f && y0 >= h)
                    {
                        hit = new VoxelHit { X = cx, Z = cz, Y = h - 1, Face = 4, PX = ox + dx * th, PY = h, PZ = oz + dz * th };
                        return true;
                    }
                }
                if (t1 >= tExit) return false;
                // 走到下一格；穿过的面朝向来的方向（Grid.DX4 顺序：北 0、东 1、南 2、西 3）
                if (nx < nz) { cx += sx; t0 = nx; nx += tdx; enteredFace = sx > 0 ? 3 : 1; }
                else { cz += sz; t0 = nz; nz += tdz; enteredFace = sz > 0 ? 2 : 0; }
                if (cx < 0 || cz < 0 || cx >= m.W || cz >= m.H) return false;
            }
            return false;
        }

        static bool Slab(float o, float d, float lo, float hi, ref float t0, ref float t1)
        {
            if (Math.Abs(d) < 1e-9f) return o >= lo && o <= hi;
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) { float tmp = a; a = b; b = tmp; }
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t0 <= t1;
        }

        /// <summary>
        /// Minecraft 式“放一块”：打到顶面 → 这一格加高 1 层；打到侧面 → 面朝向的那一格填到这一层（高度至少到 Y + 1）。
        /// 返回要改的格和新高度（不改地图）。
        /// </summary>
        public static bool PlaceTarget(RegionMap m, VoxelHit hit, out int x, out int z, out int newHeight)
        {
            if (hit.Face == 4) { x = hit.X; z = hit.Z; newHeight = m.Height[z * m.W + x] + 1; return true; }
            x = hit.X + Grid.DX4[hit.Face]; z = hit.Z + Grid.DZ4[hit.Face];
            newHeight = 0;
            if (!m.InBounds(x, z)) return false;
            newHeight = Math.Max(m.Height[z * m.W + x], hit.Y + 1);
            return newHeight != m.Height[z * m.W + x];
        }

        /// <summary>
        /// Minecraft 式“挖一块”：打到顶面 → 这一格降 1 层；打到侧面 → 这一格降到被打到的那一层（这一块和它上面的都去掉，柱子不能悬空）。
        /// </summary>
        public static bool RemoveTarget(RegionMap m, VoxelHit hit, int minHeight, out int x, out int z, out int newHeight)
        {
            x = hit.X; z = hit.Z;
            int h = m.Height[z * m.W + x];
            newHeight = hit.Face == 4 ? h - 1 : hit.Y;
            if (newHeight < minHeight) newHeight = minHeight;
            return newHeight != h;
        }
    }
}
