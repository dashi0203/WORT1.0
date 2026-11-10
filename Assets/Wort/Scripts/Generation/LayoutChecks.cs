using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>检查结果（Docs/WorldGen.md 第 9 节）。只报错，不修改 layout 区域。</summary>
    public sealed class LayoutReport
    {
        public int WalkableNodes;
        public int Unreachable;
        public int Traps;
        public int Leaks;
        public int HurtEdges, LethalEdges;
        public int MinHeight, MaxHeight, Over32;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Errors = new List<string>();
        /// <summary>地点两两之间：直线距离、步行距离（米，-1 = 走不到）。</summary>
        public readonly List<(string a, string b, double straight, double walk)> Distances = new List<(string, string, double, double)>();

        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// 三维可站立表面图：每格最多两个节点——地面（id = i）和悬空层顶面（id = N + i）。
    /// 高度用半层为单位（斜坡格算半层），往上最多 1 层，往下按坠落规则：
    /// 1–3 层无伤，4–5 层受伤，6 层以上不算可走（6 层概率摔死、7 层以上必死）。
    /// 连通检查只用无伤的边。
    /// </summary>
    public sealed class SurfaceGraph
    {
        public const int MaxSafeDrop = 3;
        public const int MaxHurtDrop = 5;

        readonly RegionMap map;
        readonly LayoutResult res;
        public readonly int W, H, N;

        public SurfaceGraph(LayoutResult r)
        {
            res = r;
            map = r.Map;
            W = map.W; H = map.H; N = W * H;
        }

        public int Cell(int node) => node < N ? node : node - N;
        public bool IsTop(int node) => node >= N;

        public bool Valid(int node)
        {
            int i = Cell(node);
            if (IsTop(node)) return map.HasSpan(i);
            if (map.HasWater(i) && map.Water[i] - map.Height[i] > 1) return false; // 深水
            if (map.HasSpan(i) && map.SpanBottom[i] - map.Height[i] < 2) return false; // 头顶空间不够
            return true;
        }

        /// <summary>站立高度（半层）。</summary>
        public int Surface2(int node)
        {
            int i = Cell(node);
            if (IsTop(node)) return 2 * map.SpanTop[i];
            return 2 * map.Height[i] + (map.Slope[i] >= 0 ? 1 : 0);
        }

        /// <summary>
        /// 从 node 往方向 d 走一格：落到邻格里不高于“当前 + 1 层”的最高表面。
        /// 返回目标节点（-1 = 走不过去），drop = 下落层数（≤ 0 为平走或上坡）。
        /// </summary>
        public int Move(int node, int d, out int drop)
        {
            drop = 0;
            int i = Cell(node);
            int x = i % W + Grid.DX4[d], z = i / W + Grid.DZ4[d];
            if (x < 0 || z < 0 || x >= W || z >= H) return -1;
            int j = z * W + x;
            int hu = Surface2(node);
            int best = -1, bestS = int.MinValue;
            int g = j, t = N + j;
            if (Valid(g)) { int sg = Surface2(g); if (sg <= hu + 2) { best = g; bestS = sg; } }
            if (Valid(t)) { int st = Surface2(t); if (st <= hu + 2 && st > bestS) { best = t; bestS = st; } }
            if (best < 0) return -1;
            // 身体（2 层高）横穿时不能撞到邻格的悬空层
            if (best == g && map.HasSpan(j))
            {
                int f = Math.Max(hu, bestS);
                if (2 * map.SpanBottom[j] < f + 4 && 2 * map.SpanTop[j] > f) return -1;
            }
            // 有护栏的桥：桥面只能沿桥的方向上下桥
            if (!Railed(node, best, d)) return -1;
            drop = (hu - bestS) / 2;
            return best;
        }

        bool Railed(int from, int to, int d)
        {
            int bf = IsTop(from) ? res.BridgeOf[Cell(from)] : -1;
            int bt = IsTop(to) ? res.BridgeOf[Cell(to)] : -1;
            if (bf == bt) return true;
            foreach (int b in new[] { bf, bt })
            {
                if (b < 0) continue;
                var sp = res.Layout.Spans[b];
                if (!sp.Railing) continue;
                bool ns = sp.X0 == sp.X1;
                bool along = ns ? (d == 0 || d == 2) : (d == 1 || d == 3);
                if (!along) return false;
            }
            return true;
        }

        /// <summary>无伤可走的边。</summary>
        public int SafeMove(int node, int d)
        {
            int v = Move(node, d, out int drop);
            return v >= 0 && drop <= MaxSafeDrop ? v : -1;
        }

        public bool[] Forward(int start)
        {
            var seen = new bool[2 * N];
            var q = new Queue<int>();
            seen[start] = true; q.Enqueue(start);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int v = SafeMove(u, d);
                    if (v < 0 || seen[v]) continue;
                    seen[v] = true; q.Enqueue(v);
                }
            }
            return seen;
        }

        /// <summary>能回到 target 的所有节点。</summary>
        public bool[] Backward(int target)
        {
            var seen = new bool[2 * N];
            var q = new Queue<int>();
            seen[target] = true; q.Enqueue(target);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                int cv = Cell(v), x = cv % W, z = cv / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    int k = nz * W + nx;
                    int back = (d + 2) & 3;
                    foreach (int u in new[] { k, N + k })
                    {
                        if (seen[u] || !Valid(u)) continue;
                        if (SafeMove(u, back) == v) { seen[u] = true; q.Enqueue(u); }
                    }
                }
            }
            return seen;
        }

        /// <summary>步行距离（米），8 方向，斜走要求两条直角路都走得通。-1 = 走不到。</summary>
        public double WalkDistance(int from, int to)
        {
            var dist = new int[2 * N];
            for (int i = 0; i < dist.Length; i++) dist[i] = int.MaxValue;
            var heap = new MinHeap();
            dist[from] = 0; heap.Push(0, from);
            while (heap.Count > 0)
            {
                heap.Pop(out int du, out int u);
                if (du != dist[u]) continue;
                if (u == to) return du / 10.0;
                for (int d = 0; d < 4; d++)
                {
                    int v = SafeMove(u, d);
                    if (v >= 0 && du + 10 < dist[v]) { dist[v] = du + 10; heap.Push(du + 10, v); }
                    // 斜走：d 再转 90°
                    int a = v;
                    if (a < 0) continue;
                    int d2 = (d + 1) & 3;
                    int w1 = SafeMove(a, d2);
                    int b = SafeMove(u, d2);
                    if (w1 < 0 || b < 0 || SafeMove(b, d) != w1) continue;
                    if (du + 14 < dist[w1]) { dist[w1] = du + 14; heap.Push(du + 14, w1); }
                }
            }
            return -1;
        }

        sealed class MinHeap
        {
            readonly List<long> items = new List<long>();
            public int Count => items.Count;

            public void Push(int key, int value)
            {
                items.Add(((long)key << 32) | (uint)value);
                int c = items.Count - 1;
                while (c > 0)
                {
                    int p = (c - 1) / 2;
                    if (items[p] <= items[c]) break;
                    (items[p], items[c]) = (items[c], items[p]);
                    c = p;
                }
            }

            public void Pop(out int key, out int value)
            {
                long top = items[0];
                key = (int)(top >> 32); value = (int)(uint)top;
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int c = 0;
                while (true)
                {
                    int l = 2 * c + 1, r = l + 1, m = c;
                    if (l < items.Count && items[l] < items[m]) m = l;
                    if (r < items.Count && items[r] < items[m]) m = r;
                    if (m == c) break;
                    (items[m], items[c]) = (items[c], items[m]);
                    c = m;
                }
            }
        }
    }

    public static class LayoutChecks
    {
        public static LayoutReport Run(LayoutResult res)
        {
            var rep = new LayoutReport();
            var map = res.Map;
            var L = res.Layout;
            var g = new SurfaceGraph(res);
            int N = g.N, W = g.W;

            // 高度范围（Docs/WorldGen.md 第 2 节：−8 到 +56，设计目标 ≤ 32）
            map.HeightRange(out rep.MinHeight, out rep.MaxHeight);
            for (int i = 0; i < N; i++) if (map.Height[i] > 32 || map.HasSpan(i) && map.SpanTop[i] > 32) rep.Over32++;
            if (rep.MinHeight < -8 || rep.MaxHeight > 56) rep.Errors.Add($"高度超出 −8..+56：{rep.MinHeight}..{rep.MaxHeight}");
            rep.Lines.Add($"高度范围 {rep.MinHeight}..{rep.MaxHeight}，超过 32m 的格子 {rep.Over32}");

            foreach (var kv in map.Stats)
                if (kv.Key.StartsWith("gateNotFound.")) rep.Errors.Add($"出入口 {kv.Key.Substring(13)} 没找到要连接的崖");

            // 有向连通：从住所出发能到哪里；哪里能回到住所
            var home = L.Home != null ? L.Site(L.Home) : null;
            if (home == null) { rep.Errors.Add("layout 没有指定 home"); return rep; }
            int start = home.Z * W + home.X;
            var fwd = g.Forward(start);
            var back = g.Backward(start);
            for (int node = 0; node < 2 * N; node++)
            {
                if (!g.Valid(node)) continue;
                rep.WalkableNodes++;
                int c = g.Cell(node);
                if (!fwd[node]) { rep.Unreachable++; map.Flags[c] |= CellFlags.Unreachable; }
                else if (!back[node]) { rep.Traps++; map.Flags[c] |= CellFlags.Trap; }
            }
            rep.Lines.Add($"可站立节点 {rep.WalkableNodes}：从住所无伤走不到 {rep.Unreachable}，走得到但回不来 {rep.Traps}");
            // 走不到的节点按区域列出：没有出入口的结构区域（如分割用的长台地）本来就上不去
            var perRegion = new int[L.Regions.Count];
            for (int node = 0; node < 2 * N; node++)
                if (g.Valid(node) && !fwd[node]) perRegion[res.RegionId[g.Cell(node)]]++;
            for (int k = 0; k < perRegion.Length; k++)
                if (perRegion[k] > 0) rep.Lines.Add($"  走不到：区域 {L.Regions[k].Id}（{L.Regions[k].Kind}）{perRegion[k]} 个节点");
            if (rep.Traps > 0) rep.Errors.Add($"有 {rep.Traps} 个节点进得去、回不到住所");

            // 多余通路：从设计上低的区域无伤走进设计上高的区域，且不在出入口 / 桥 / 隧道
            for (int i = 0; i < N; i++)
            {
                if (!g.Valid(i)) continue;
                var ri = res.RegionAt(i);
                int x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (!map.InBounds(nx, nz)) continue;
                    int j = nz * W + nx;
                    var rj = res.RegionAt(j);
                    if (rj == ri || ri.IsOpen && rj.IsOpen) continue;
                    if (ri.AbsLevel >= rj.AbsLevel) continue;
                    if (res.GateZone[i] || res.GateZone[j] || res.SpanCorridor[i] || res.SpanCorridor[j]) continue;
                    int v = g.Move(i, d, out int drop);
                    if (v == j && drop <= 0)
                    {
                        rep.Leaks++;
                        map.Flags[i] |= CellFlags.Leak; map.Flags[j] |= CellFlags.Leak;
                    }
                }
            }
            rep.Lines.Add($"出入口以外多出来的通路 {rep.Leaks} 处");
            if (rep.Leaks > 0) rep.Errors.Add($"有 {rep.Leaks} 处出入口以外的通路");

            // 跳下代价统计（只统计地面之间）
            for (int i = 0; i < N; i++)
                for (int d = 0; d < 4; d++)
                {
                    int v = g.Move(i, d, out int drop);
                    if (v < 0 && drop == 0) continue;
                    if (drop >= 4 && drop <= SurfaceGraph.MaxHurtDrop) rep.HurtEdges++;
                }
            for (int i = 0; i < N; i++)
            {
                int x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (!map.InBounds(nx, nz)) continue;
                    if (map.Height[i] - map.Height[nz * W + nx] >= 7) rep.LethalEdges++;
                }
            }
            rep.Lines.Add($"跳下受伤的崖边 {rep.HurtEdges} 格边，7 层以上（必死）{rep.LethalEdges} 格边");

            // 地点：地形要求、能否从住所走到
            foreach (var s in L.Sites)
            {
                int c = s.Z * W + s.X;
                string reach = fwd[c] && back[c] ? "可往返" : (fwd[c] ? "回不来" : "走不到");
                string extra = "";
                if (s.T.Terrain == "cliffBehind")
                {
                    int best = 0, dx = Grid.DX4[s.T.CliffDir], dz = Grid.DZ4[s.T.CliffDir];
                    int h0 = map.Height[c];
                    for (int k = 1; k <= (int)Math.Max(s.T.RX, s.T.RZ) + 6; k++)
                    {
                        int x = s.X + dx * k, z = s.Z + dz * k;
                        if (!map.InBounds(x, z)) break;
                        best = Math.Max(best, map.Height[map.Index(x, z)] - h0);
                    }
                    extra = $"，背后崖高 {best} 层";
                    if (best < s.T.CliffMin) rep.Errors.Add($"地点 {s.Id} 背后的崖只有 {best} 层，要求至少 {s.T.CliffMin} 层");
                }
                rep.Lines.Add($"地点 {s.Id}（{s.Template}）{reach}{extra}");
                if (!(fwd[c] && back[c])) rep.Errors.Add($"地点 {s.Id} {reach}");
            }

            // 步行距离约束
            foreach (var con in L.Constraints)
            {
                if (con.Type != "minWalk") continue;
                var a = L.Site(con.A); var b = L.Site(con.B);
                if (a == null || b == null) { rep.Errors.Add($"约束引用了不存在的地点 {con.A} / {con.B}"); continue; }
                double straight = Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Z - b.Z) * (double)(a.Z - b.Z));
                double walk = g.WalkDistance(a.Z * W + a.X, b.Z * W + b.X);
                rep.Distances.Add((a.Id, b.Id, straight, walk));
                string ok = walk >= con.Meters ? "满足" : "不满足";
                rep.Lines.Add($"步行距离 {a.Id} → {b.Id}：直线 {straight:F0}m，步行 {walk:F0}m（要求 ≥ {con.Meters:F0}m，{ok}）");
                if (walk < con.Meters) rep.Errors.Add($"{a.Id} → {b.Id} 步行 {walk:F0}m，少于 {con.Meters:F0}m");
            }

            // 桥：提醒补护栏
            foreach (var sp in L.Spans)
                if (sp.Type == SpanType.Bridge)
                    rep.Lines.Add($"桥 {sp.Id}：railing = {(sp.Railing ? "true（Unity 里记得补护栏模型）" : "false（可从桥面跳下）")}");

            return rep;
        }
    }
}
