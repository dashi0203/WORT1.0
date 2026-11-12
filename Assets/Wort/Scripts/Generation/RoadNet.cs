using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 道路寻路与铺设（《世界生成规格》5.5）：在体素顶面上找一条不靠跳跃的路（相邻格顶面高差 ≤ 1 层，斜坡只能顺着坡走），
    /// 把经过的格标成 <see cref="CellFlags.Road"/>，由 <see cref="RoadSurface"/> 画成路面。
    /// 也用来做连通测试：铺不通就说明两点之间走不过去（不算跳跃 / 单向落差）。
    /// 纯 C#、确定性（A*，同代价按格序号取小的）。
    /// </summary>
    public static class RoadNet
    {
        /// <summary>格 i 朝方向 d 那条边上的顶面高度；斜坡的侧边不能走（返回 int.MinValue）。</summary>
        static int Edge(RegionMap m, int i, int d)
        {
            int up = m.Slope[i], h = m.Height[i];
            if (up < 0) return h;
            if (up == d) return h + 1;
            if (((up + 2) & 3) == d) return h;
            return int.MinValue;
        }

        /// <summary>
        /// 从 from 到 to 找路；allowed 为 null 时全图可走（不可通行的外圈除外：cls 为 TerraceField.Border 的格）。
        /// 深水不走，浅水（1 层）+8。代价：每格 1，每上下 1 层 +3，贴着崖（任一邻格高差 ≥ 2）+1.5。找不到返回 null。
        /// </summary>
        public static List<int> Find(RegionMap m, byte[] cls, int from, int to, bool[] allowed)
        {
            int W = m.W, H = m.H, N = W * H;
            var g = new float[N];
            var prev = new int[N];
            var closed = new bool[N];
            for (int i = 0; i < N; i++) { g[i] = float.MaxValue; prev[i] = -1; }
            int tx = to % W, tz = to / W;
            var heap = new List<(float f, int i)>();
            void Push(float f, int i)
            {
                heap.Add((f, i));
                int c = heap.Count - 1;
                while (c > 0) { int p = (c - 1) / 2; if (Less(heap[c], heap[p])) { (heap[c], heap[p]) = (heap[p], heap[c]); c = p; } else break; }
            }
            (float f, int i) Pop()
            {
                var top = heap[0]; heap[0] = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
                int c = 0;
                while (true)
                {
                    int l = 2 * c + 1, r = l + 1, s = c;
                    if (l < heap.Count && Less(heap[l], heap[s])) s = l;
                    if (r < heap.Count && Less(heap[r], heap[s])) s = r;
                    if (s == c) break;
                    (heap[c], heap[s]) = (heap[s], heap[c]); c = s;
                }
                return top;
            }
            // 浅水（水面比格高最多 1 层）能涉水过去，代价高；深水不能走
            bool Deep(int i) => m.HasWater(i) && m.Water[i] - m.Height[i] > 1;
            bool Ok(int i) => (allowed == null || allowed[i]) && !Deep(i) && (cls == null || cls[i] != TerraceField.Border);
            if (!Ok(from) || !Ok(to)) return null;
            g[from] = 0;
            Push(Math.Abs(from % W - tx) + Math.Abs(from / W - tz), from);
            while (heap.Count > 0)
            {
                var (_, a) = Pop();
                if (closed[a]) continue;
                closed[a] = true;
                if (a == to) break;
                int ax = a % W, az = a / W;
                for (int d = 0; d < 4; d++)
                {
                    int bx = ax + Grid.DX4[d], bz = az + Grid.DZ4[d];
                    if (bx < 0 || bz < 0 || bx >= W || bz >= H) continue;
                    int b = bz * W + bx;
                    if (closed[b] || !Ok(b)) continue;
                    int ea = Edge(m, a, d), eb = Edge(m, b, (d + 2) & 3);
                    if (ea == int.MinValue || eb == int.MinValue) continue;
                    int dh = Math.Abs(ea - eb);
                    if (dh > 1) continue;
                    float cost = 1 + 3 * dh + (m.HasWater(b) ? 8 : 0);
                    for (int e = 0; e < 4; e++)
                    {
                        int cx = bx + Grid.DX4[e], cz = bz + Grid.DZ4[e];
                        if (cx >= 0 && cz >= 0 && cx < W && cz < H && Math.Abs(m.Height[cz * W + cx] - m.Height[b]) >= 2) { cost += 1.5f; break; }
                    }
                    float ng = g[a] + cost;
                    if (ng < g[b])
                    {
                        g[b] = ng; prev[b] = a;
                        Push(ng + Math.Abs(bx - tx) + Math.Abs(bz - tz), b);
                    }
                }
            }
            if (prev[to] < 0 && from != to) return null;
            var path = new List<int>();
            for (int c = to; c >= 0; c = prev[c]) { path.Add(c); if (c == from) break; }
            path.Reverse();
            return path;
        }

        /// <summary>调试：从 from 出发按道路规则能走到的格。</summary>
        public static bool[] Reach(RegionMap m, byte[] cls, int from, bool[] allowed)
        {
            int W = m.W, H = m.H, N = W * H;
            var seen = new bool[N];
            bool Ok(int i) => (allowed == null || allowed[i]) && !(m.HasWater(i) && m.Water[i] - m.Height[i] > 1) && (cls == null || cls[i] != TerraceField.Border);
            if (!Ok(from)) return seen;
            var q = new Queue<int>(); q.Enqueue(from); seen[from] = true;
            while (q.Count > 0)
            {
                int a = q.Dequeue(), ax = a % W, az = a / W;
                for (int d = 0; d < 4; d++)
                {
                    int bx = ax + Grid.DX4[d], bz = az + Grid.DZ4[d];
                    if (bx < 0 || bz < 0 || bx >= W || bz >= H) continue;
                    int b = bz * W + bx;
                    if (seen[b] || !Ok(b)) continue;
                    int ea = Edge(m, a, d), eb = Edge(m, b, (d + 2) & 3);
                    if (ea == int.MinValue || eb == int.MinValue || Math.Abs(ea - eb) > 1) continue;
                    seen[b] = true; q.Enqueue(b);
                }
            }
            return seen;
        }

        static bool Less((float f, int i) a, (float f, int i) b) => a.f < b.f || (a.f == b.f && a.i < b.i);

        /// <summary>
        /// 把路径标成道路。width = 2 时，每格再加上行进方向右手边的一格（同样高、不是斜坡侧边、不是水、在 allowed 里）。
        /// </summary>
        public static void Paint(RegionMap m, List<int> path, int width, bool[] allowed)
        {
            int W = m.W;
            for (int k = 0; k < path.Count; k++)
            {
                int i = path[k];
                if (!m.HasWater(i)) m.Flags[i] |= CellFlags.Road; // 涉水的格不画路面
                if (width < 2) continue;
                int j = path[Math.Min(k + 1, path.Count - 1)], p = path[Math.Max(k - 1, 0)];
                int dx = j % W - p % W, dz = j / W - p / W;
                if (dx == 0 && dz == 0) continue;
                int rx = dz > 0 ? 1 : (dz < 0 ? -1 : 0), rz = dx > 0 ? -1 : (dx < 0 ? 1 : 0); // 右手边
                if (rx != 0 && rz != 0) continue;
                int x = i % W + rx, z = i / W + rz;
                if (x < 0 || z < 0 || x >= W || z >= m.H) continue;
                int n = z * W + x;
                if ((allowed != null && !allowed[n]) || m.HasWater(n) || m.Height[n] != m.Height[i] || m.Slope[n] != m.Slope[i]) continue;
                m.Flags[n] |= CellFlags.Road;
            }
        }
    }
}
