using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>关卡生成器的可调参数（《随机区域与关卡生成》第 12 节）。</summary>
    public sealed class RandomRegionSettings
    {
        /// <summary>接缝带向内宽度（m），≤ 6【已定】。</summary>
        public int SeamWidth = 6;
        /// <summary>房间平均面积（m²），决定房间数【暂定】。</summary>
        public double AvgRoomArea = 800;
        /// <summary>房间数浮动（±比例）【暂定】。</summary>
        public double RoomCountJitter = 0.2;
        /// <summary>房间最短边（m）【已定 8】。</summary>
        public int MinSide = 8;
        /// <summary>房间最长边（m）【暂定】。</summary>
        public int MaxSide = 40;
        /// <summary>房间大小差异度 0（均匀）– 1（悬殊）【暂定】。</summary>
        public double SizeVariance = 0.5;
        /// <summary>两个房间共享边至少多长才算相邻（m）【暂定】。</summary>
        public int MinSharedEdge = 6;
        /// <summary>通路最窄（m）【已定 3】。</summary>
        public int MinOpening = 3;
        /// <summary>加一条边成环的概率【暂定】。</summary>
        public double LoopChance = 0.15;
        /// <summary>做一处单向落差的概率（需要有环上的边）【暂定】。</summary>
        public double OneWayChance = 0.6;
        /// <summary>开放出入口之间路程短的方案仍被选中的概率（“进去就看到出口”要少）【已定为软约束】。</summary>
        public double ShortPathChance = 0.12;
        /// <summary>每次刷新生成几个候选连接方案来打分。</summary>
        public int GraphCandidates = 10;
        /// <summary>危险区 / 奖励密度（每 1000 m²）【暂定】。</summary>
        public double DangerMin = 0.5, DangerMax = 1.0, RewardMin = 0.25, RewardMax = 0.75;
        /// <summary>每个房间出现浅水的概率【暂定】。</summary>
        public double WaterChance = 0.4;
        /// <summary>校验失败时最多换几次种子。</summary>
        public int MaxAttempts = 24;
    }

    /// <summary>候选出入口：放在随机区域外面（固定 chunk 一侧）紧贴共享边的格子上，Dir = 朝区域里走的方向。</summary>
    public struct RegionEntrance
    {
        public int X, Z, Dir;
        public bool Open;
    }

    public sealed class RegionRoom
    {
        public int X0, Z0, X1, Z1; // 矩形，[X0, X1) × [Z0, Z1)
        public int Floor;
        public int Area;
        public int Depth;
        public bool Water;
    }

    public sealed class RandomRegionResult
    {
        public RegionMap Map;
        public bool[] InRegion, Seam, Wall, Corridor, Grass, Rock;
        public int[] RoomOf;
        public List<RegionRoom> Rooms = new List<RegionRoom>();
        public List<(int a, int b)> Edges = new List<(int a, int b)>();
        public List<RegionEntrance> Entrances = new List<RegionEntrance>();
        public List<(int x, int z, double r)> Danger = new List<(int x, int z, double r)>();
        public List<(int x, int z)> Rewards = new List<(int x, int z)>();
        public (int x, int z)? OneWay;
        public int Attempts;
        public bool Ok;
        public List<string> Report = new List<string>();
    }

    /// <summary>
    /// 随机区域的关卡生成器（《随机区域与关卡生成》）：在烘焙好的固定世界里，把指定的随机 chunk 重新生成一遍。
    /// 只改随机区域里的地表；固定格子一格不动。纯 C#、确定性（种子 = 世界种子 + 区域编号 + 刷新次数）。
    /// </summary>
    public static class RandomRegion
    {
        struct Rng
        {
            uint s;
            public Rng(uint seed) { s = seed; }
            public double Next() { s = Hash.Mix(s + 0x9e3779b9U); return Hash.Unit(s); }
            public int Range(int lo, int hiExclusive) => lo + (int)(Next() * (hiExclusive - lo));
            public uint Bits() { s = Hash.Mix(s + 0x9e3779b9U); return s; }
        }

        struct Rect { public int X0, Z0, X1, Z1; public int W => X1 - X0; public int H => Z1 - Z0; }

        public static RegionMap Clone(RegionMap src)
        {
            var m = new RegionMap(src.Settings);
            Array.Copy(src.Biome, m.Biome, src.Biome.Length);
            for (int b = 0; b < src.Weights.Length; b++) Array.Copy(src.Weights[b], m.Weights[b], src.Weights[b].Length);
            Array.Copy(src.Height, m.Height, src.Height.Length);
            Array.Copy(src.Water, m.Water, src.Water.Length);
            Array.Copy(src.Slope, m.Slope, src.Slope.Length);
            Array.Copy(src.Tier, m.Tier, src.Tier.Length);
            Array.Copy(src.Flags, m.Flags, src.Flags.Length);
            if (src.SpanTop != null)
            {
                m.EnsureSpans();
                Array.Copy(src.SpanBottom, m.SpanBottom, src.SpanBottom.Length);
                Array.Copy(src.SpanTop, m.SpanTop, src.SpanTop.Length);
                Array.Copy(src.SpanKind, m.SpanKind, src.SpanKind.Length);
            }
            m.SpawnX = src.SpawnX; m.SpawnZ = src.SpawnZ;
            return m;
        }

        /// <summary>随机区域的格子：所给 chunk 里，除去外圈不可通行、河 / 水体。</summary>
        public static bool[] RegionMask(RegionMap m, byte[] cls, IList<(int cx, int cz)> chunks)
        {
            int W = m.W, cs = m.Settings.ChunkSize;
            var mask = new bool[W * m.H];
            foreach (var (cx, cz) in chunks)
                for (int z = cz * cs; z < (cz + 1) * cs; z++)
                    for (int x = cx * cs; x < (cx + 1) * cs; x++)
                    {
                        int i = z * W + x;
                        if (cls[i] == TerraceField.Border || m.HasWater(i) || m.Has(i, CellFlags.River)) continue;
                        mask[i] = true;
                    }
            return mask;
        }

        /// <summary>
        /// 原型用：在每条“随机 chunk / 固定 chunk”的共享边上自动挑一个候选出入口（固定侧是能走到的平地，旁边 3 格同高）。
        /// 进引擎后由策划手放。
        /// </summary>
        public static List<RegionEntrance> AutoCandidates(RegionMap m, byte[] cls, IList<(int cx, int cz)> chunks, uint seed)
        {
            int W = m.W, cs = m.Settings.ChunkSize;
            var inChunk = new HashSet<(int, int)>(chunks);
            int spawn = m.SpawnZ * W + m.SpawnX;
            var good = Reach(m, cls, new[] { spawn }, null, false);
            var res = new List<RegionEntrance>();
            foreach (var (cx, cz) in chunks)
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + Grid.DX4[d], nz = cz + Grid.DZ4[d];
                    if (inChunk.Contains((nx, nz))) continue;
                    // 固定邻居必须不是外圈 chunk
                    int b0 = cs;
                    if (nx * cs < b0 || nz * cs < b0 || (nx + 1) * cs > m.W - b0 || (nz + 1) * cs > m.H - b0) continue;
                    var cands = new List<(int i, uint key)>();
                    for (int t = 10; t < cs - 10; t++)
                    {
                        int x, z;
                        // 共享边外侧一格（固定 chunk 里）
                        if (d == 0) { x = cx * cs + t; z = (cz + 1) * cs; }
                        else if (d == 2) { x = cx * cs + t; z = cz * cs - 1; }
                        else if (d == 1) { x = (cx + 1) * cs; z = cz * cs + t; }
                        else { x = cx * cs - 1; z = cz * cs + t; }
                        int i = z * W + x;
                        if (!good[i] || cls[i] == TerraceField.Border || m.HasWater(i) || m.Slope[i] >= 0) continue;
                        // 沿边方向 ±1 格高差 ≤ 1（站得住）
                        int px = Grid.DX4[(d + 1) & 3], pz = Grid.DZ4[(d + 1) & 3];
                        int a = (z + pz) * W + x + px, b = (z - pz) * W + x - px;
                        if (Math.Abs(m.Height[a] - m.Height[i]) > 1 || Math.Abs(m.Height[b] - m.Height[i]) > 1) continue;
                        // 平地优先
                        uint key = Hash.Of((uint)i, seed) >> 1;
                        if (cls[i] != TerraceField.Ground) key |= 0x80000000u;
                        cands.Add((i, key));
                    }
                    if (cands.Count == 0) continue;
                    cands.Sort((p, q) => p.key.CompareTo(q.key));
                    int pick = cands[0].i;
                    res.Add(new RegionEntrance { X = pick % W, Z = pick / W, Dir = TerraceField.Opp(d) });
                }
            return res;
        }

        public static RandomRegionResult Refresh(RegionMap baked, byte[] cls, IList<(int cx, int cz)> chunks,
            IList<RegionEntrance> candidates, uint worldSeed, int regionId, int refreshIndex, RandomRegionSettings s)
        {
            RandomRegionResult last = null;
            for (int attempt = 0; attempt < s.MaxAttempts; attempt++)
            {
                uint seed = Hash.Of(Hash.Of(worldSeed, (uint)regionId * 7919u + 17u), (uint)refreshIndex * 104729u + (uint)attempt);
                var r = TryOnce(baked, cls, chunks, candidates, seed, s);
                r.Attempts = attempt + 1;
                last = r;
                if (r.Ok) return r;
            }
            return last;
        }

        // =====================================================================

        static RandomRegionResult TryOnce(RegionMap baked, byte[] cls, IList<(int cx, int cz)> chunks,
            IList<RegionEntrance> candidates, uint seed, RandomRegionSettings s)
        {
            var R = new RandomRegionResult();
            var m = Clone(baked);
            R.Map = m;
            int W = m.W, H = m.H, N = W * H;
            var rng = new Rng(seed);
            var region = RegionMask(m, cls, chunks);
            R.InRegion = region;

            // ---------- 1. 抽开放的出入口 ----------
            int nc = candidates.Count;
            int open = nc == 0 ? 0 : Math.Min(3, Math.Min(nc, rng.Next() < 0.45 ? 1 : (rng.Next() < 0.8 ? 2 : 3)));
            var order = new List<int>();
            for (int k = 0; k < nc; k++) order.Add(k);
            for (int k = nc - 1; k > 0; k--) { int j = rng.Range(0, k + 1); int t = order[k]; order[k] = order[j]; order[j] = t; }
            for (int k = 0; k < nc; k++)
            {
                var e = candidates[k];
                e.Open = order.IndexOf(k) < open;
                R.Entrances.Add(e);
            }

            // ---------- 2. 接缝带：离区域边 ≤ SeamWidth 的格；房间区 = 其余 ----------
            var notRegion = new bool[N];
            for (int i = 0; i < N; i++) notRegion[i] = !region[i];
            var distOut = Grid.DistanceInside(region, W, H); // 到区域外最近格的距离
            var seam = new bool[N];
            var inner = new bool[N];
            var fSeam = new Fbm(Hash.Of(seed, 11), 9, 2);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (!region[i]) continue;
                    // 接缝带内缘用噪声弄弯：3.5–6m
                    double sw = s.SeamWidth - 1.25 * (1 + fSeam.Sample(x, z));
                    if (distOut[i] <= sw) seam[i] = true; else inner[i] = true;
                }
            inner = Grid.RemoveSmall(inner, W, H, 40);
            for (int i = 0; i < N; i++) if (region[i] && !inner[i]) seam[i] = true;
            R.Seam = seam;

            // ---------- 3. 切房间（按房间区的总面积） ----------
            int area = 0, bx0 = W, bz0 = H, bx1 = 0, bz1 = 0;
            for (int i = 0; i < N; i++)
                if (inner[i])
                {
                    area++;
                    int x = i % W, z = i / W;
                    bx0 = Math.Min(bx0, x); bz0 = Math.Min(bz0, z); bx1 = Math.Max(bx1, x + 1); bz1 = Math.Max(bz1, z + 1);
                }
            if (area == 0) { R.Report.Add("区域里没有可切的面积"); return R; }
            int target = Math.Max(2, (int)Math.Floor(area / s.AvgRoomArea * (1 + s.RoomCountJitter * (2 * rng.Next() - 1)) + 0.5));
            var rects = new List<Rect> { new Rect { X0 = bx0, Z0 = bz0, X1 = bx1, Z1 = bz1 } };
            for (int guard = 0; guard < 400; guard++)
            {
                int pick = -1;
                // 先切超过最长边的
                for (int k = 0; k < rects.Count && pick < 0; k++)
                    if ((rects[k].W > s.MaxSide && rects[k].W >= 2 * s.MinSide) || (rects[k].H > s.MaxSide && rects[k].H >= 2 * s.MinSide)) pick = k;
                if (pick < 0)
                {
                    if (rects.Count >= target) break;
                    var splittable = new List<int>();
                    for (int k = 0; k < rects.Count; k++) if (rects[k].W >= 2 * s.MinSide || rects[k].H >= 2 * s.MinSide) splittable.Add(k);
                    if (splittable.Count == 0) break;
                    // 差异度低：总切最大的（大小均匀）；差异度高：随机切（大小悬殊）
                    if (rng.Next() < 1 - s.SizeVariance)
                    {
                        pick = splittable[0];
                        foreach (int k in splittable) if (rects[k].W * rects[k].H > rects[pick].W * rects[pick].H) pick = k;
                    }
                    else pick = splittable[rng.Range(0, splittable.Count)];
                }
                var r0 = rects[pick];
                bool canV = r0.W >= 2 * s.MinSide, canH = r0.H >= 2 * s.MinSide;
                bool vertical = canV && (!canH || r0.W > r0.H || (r0.W == r0.H && rng.Next() < 0.5));
                int len = vertical ? r0.W : r0.H;
                double tt = 0.5 + (rng.Next() - 0.5) * (0.3 + 0.7 * s.SizeVariance);
                int cut = s.MinSide + (int)Math.Floor((len - 2 * s.MinSide) * tt + 0.5);
                Rect a = r0, b = r0;
                if (vertical) { a.X1 = r0.X0 + cut; b.X0 = r0.X0 + cut; }
                else { a.Z1 = r0.Z0 + cut; b.Z0 = r0.Z0 + cut; }
                rects[pick] = a; rects.Add(b);
            }
            // 矩形编号（墙线用扭曲坐标取，弄弯）
            var fWx = new Fbm(Hash.Of(seed, 21), 10, 2);
            var fWz = new Fbm(Hash.Of(seed, 22), 10, 2);
            int RectAt(double x, double z)
            {
                for (int k = 0; k < rects.Count; k++)
                    if (x >= rects[k].X0 && x < rects[k].X1 && z >= rects[k].Z0 && z < rects[k].Z1) return k;
                return -1;
            }
            var wid = new int[N];
            for (int i = 0; i < N; i++) wid[i] = -1;
            for (int z = bz0; z < bz1; z++)
                for (int x = bx0; x < bx1; x++)
                {
                    int i = z * W + x;
                    if (!inner[i]) continue;
                    int k = RectAt(x + 0.5 + 1.6 * fWx.Sample(x, z), z + 0.5 + 1.6 * fWz.Sample(x, z));
                    if (k < 0) k = RectAt(x + 0.5, z + 0.5);
                    wid[i] = k;
                }

            // ---------- 4. 窄墙：1–3m，沿（扭曲后的）矩形边；外圈一道墙把房间和接缝带隔开 ----------
            var wall = new bool[N];
            var fWall = new Fbm(Hash.Of(seed, 31), 7, 2);
            for (int z = bz0; z < bz1; z++)
                for (int x = bx0; x < bx1; x++)
                {
                    int i = z * W + x;
                    if (!inner[i]) continue;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        int n = nz * W + nx;
                        bool edge = !inner[n] || wid[n] > wid[i];
                        if (!edge) continue;
                        wall[i] = true;
                        double v = fWall.Sample(x, z);
                        // 加粗：2 格或 3 格
                        if (v > 0.15 && inner[n]) wall[n] = true;
                        if (v > 0.5)
                        {
                            int bxx = x - Grid.DX4[d], bzz = z - Grid.DZ4[d];
                            int bi = bzz * W + bxx;
                            if (inner[bi]) wall[bi] = true;
                        }
                    }
                }
            // 房间格 = 房间区里不是墙的格；按矩形编号分房间，太小的（< 30 格）并进墙
            var roomOf = new int[N];
            for (int i = 0; i < N; i++) roomOf[i] = -1;
            var lab = Grid.Label(W, H, i => inner[i] && !wall[i], (p, q) => wid[p] == wid[q], out var lsz);
            // 每个矩形取最大的一块当房间
            var bestComp = new int[rects.Count];
            for (int k = 0; k < rects.Count; k++) bestComp[k] = -1;
            for (int i = 0; i < N; i++)
            {
                if (lab[i] < 0) continue;
                int k = wid[i];
                if (bestComp[k] < 0 || lsz[lab[i]] > lsz[bestComp[k]]) bestComp[k] = lab[i];
            }
            var rectToRoom = new int[rects.Count];
            for (int k = 0; k < rects.Count; k++)
            {
                rectToRoom[k] = -1;
                if (bestComp[k] < 0 || lsz[bestComp[k]] < 30) continue;
                rectToRoom[k] = R.Rooms.Count;
                R.Rooms.Add(new RegionRoom { X0 = rects[k].X0, Z0 = rects[k].Z0, X1 = rects[k].X1, Z1 = rects[k].Z1, Area = lsz[bestComp[k]] });
            }
            for (int i = 0; i < N; i++)
            {
                if (lab[i] < 0) continue;
                int k = wid[i];
                if (rectToRoom[k] >= 0 && lab[i] == bestComp[k]) roomOf[i] = rectToRoom[k];
                else wall[i] = true; // 碎块并进墙
            }
            R.RoomOf = roomOf;
            R.Wall = wall;
            int nr = R.Rooms.Count;
            if (nr < 1) { R.Report.Add("切不出房间"); return R; }

            // ---------- 5. 相邻关系：墙两侧各是哪个房间、能开口的长度 ----------
            // 分隔格：墙格，半径 2 内同时有两个不同房间的格
            var sepCells = new Dictionary<long, List<int>>();
            for (int i = 0; i < N; i++)
            {
                if (!wall[i] || !inner[i]) continue;
                int x = i % W, z = i / W;
                // 贴着接缝带的外圈墙不能开（否则房间会和接缝带连通）
                bool touchesOut = false;
                for (int dz = -1; dz <= 1 && !touchesOut; dz++)
                    for (int dx = -1; dx <= 1; dx++) { int n = (z + dz) * W + x + dx; if (n >= 0 && n < N && !inner[n]) { touchesOut = true; break; } }
                if (touchesOut) continue;
                int ra = -1, rb = -1;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n < 0 || n >= N) continue;
                        int r = roomOf[n];
                        if (r < 0) continue;
                        if (ra < 0) ra = r; else if (r != ra && (rb < 0 || r < rb)) rb = r;
                    }
                if (ra < 0 || rb < 0) continue;
                if (ra > rb) { int t = ra; ra = rb; rb = t; }
                long key = ((long)ra << 32) | (uint)rb;
                if (!sepCells.TryGetValue(key, out var list)) sepCells[key] = list = new List<int>();
                list.Add(i);
            }
            var adj = new List<int>[nr];
            for (int k = 0; k < nr; k++) adj[k] = new List<int>();
            var adjKeys = new List<long>();
            foreach (var kv in sepCells)
            {
                int ra = (int)(kv.Key >> 32), rb = (int)(kv.Key & 0xffffffff);
                if (SpanLength(kv.Value, W) < s.MinSharedEdge) continue;
                adj[ra].Add(rb); adj[rb].Add(ra);
                adjKeys.Add(kv.Key);
            }

            // ---------- 6. 出入口接哪个房间：从出入口往里直走，碰到的第一个房间 ----------
            var entRoom = new int[R.Entrances.Count];
            var entPath = new List<int>[R.Entrances.Count];
            for (int k = 0; k < R.Entrances.Count; k++)
            {
                var e = R.Entrances[k];
                entRoom[k] = -1;
                var path = new List<int>();
                int x = e.X, z = e.Z;
                for (int step = 0; step < 40; step++)
                {
                    x += Grid.DX4[e.Dir]; z += Grid.DZ4[e.Dir];
                    if (!m.InBounds(x, z)) break;
                    int i = z * W + x;
                    if (!region[i]) break;
                    if (roomOf[i] >= 0) { entRoom[k] = roomOf[i]; break; }
                    // 通道两侧一格碰到房间也算到了（避免顺着两个房间之间的墙一直走）
                    int ppx = Grid.DX4[(e.Dir + 1) & 3], ppz = Grid.DZ4[(e.Dir + 1) & 3];
                    int sa = (z + ppz) * W + x + ppx, sb = (z - ppz) * W + x - ppx;
                    path.Add(i);
                    if (roomOf[sa] >= 0 || roomOf[sb] >= 0)
                    {
                        // 再往前一格进房间
                        int fx = x + Grid.DX4[e.Dir], fz = z + Grid.DZ4[e.Dir];
                        int fi = fz * W + fx;
                        entRoom[k] = roomOf[fi] >= 0 ? roomOf[fi] : (roomOf[sa] >= 0 ? roomOf[sa] : roomOf[sb]);
                        if (roomOf[fi] < 0 && region[fi]) path.Add(fi);
                        break;
                    }
                }
                entPath[k] = path;
            }
            var openIdx = new List<int>();
            for (int k = 0; k < R.Entrances.Count; k++) if (R.Entrances[k].Open && entRoom[k] >= 0) openIdx.Add(k);
            if (openIdx.Count == 0) { R.Report.Add("开放的出入口接不到房间"); return R; }

            // ---------- 7. 连接图：从第一个开放出入口的房间做随机深度优先（链为主、偶有岔路），可加一条成环 ----------
            List<(int a, int b)> bestEdges = null;
            int bestScore = int.MinValue;
            var allCands = new List<(List<(int, int)> edges, int score)>();
            for (int c = 0; c < s.GraphCandidates; c++)
            {
                var edges = new List<(int a, int b)>();
                var deg = new int[nr];
                var seen = new bool[nr];
                int root = entRoom[openIdx[0]];
                var stack = new List<int> { root };
                seen[root] = true;
                while (stack.Count > 0)
                {
                    int u = stack[stack.Count - 1];
                    var nexts = new List<int>();
                    foreach (int v in adj[u]) if (!seen[v]) nexts.Add(v);
                    if (nexts.Count == 0 || deg[u] >= 3) { stack.RemoveAt(stack.Count - 1); continue; }
                    int w = nexts[rng.Range(0, nexts.Count)];
                    seen[w] = true; edges.Add((u, w)); deg[u]++; deg[w]++;
                    stack.Add(w);
                }
                bool allSeen = true;
                for (int k = 0; k < nr; k++) if (!seen[k]) allSeen = false;
                if (!allSeen) continue; // 有房间连不上（形状太怪），换方案
                if (rng.Next() < s.LoopChance)
                {
                    var extra = new List<(int, int)>();
                    for (int u = 0; u < nr; u++)
                        foreach (int v in adj[u])
                            if (u < v && deg[u] < 3 && deg[v] < 3 && !edges.Contains((u, v)) && !edges.Contains((v, u))) extra.Add((u, v));
                    if (extra.Count > 0) { var e = extra[rng.Range(0, extra.Count)]; edges.Add(e); deg[e.Item1]++; deg[e.Item2]++; }
                }
                int four = 0, three = 0;
                for (int k = 0; k < nr; k++) { if (deg[k] >= 4) four++; if (deg[k] == 3) three++; }
                if (four > 0) continue;
                // 打分：开放出入口房间之间的路程（房间数）尽量长；3 通少
                int dist = 0;
                if (openIdx.Count >= 2)
                {
                    var dd = GraphDist(nr, edges, entRoom[openIdx[0]]);
                    dist = int.MaxValue;
                    for (int k = 1; k < openIdx.Count; k++) dist = Math.Min(dist, dd[entRoom[openIdx[k]]]);
                }
                else
                {
                    var dd = GraphDist(nr, edges, entRoom[openIdx[0]]);
                    foreach (int v in dd) dist = Math.Max(dist, v);
                }
                int score = dist * 10 - three * 3;
                allCands.Add((edges, score));
                if (score > bestScore) { bestScore = score; bestEdges = edges; }
            }
            if (bestEdges == null) { R.Report.Add("连接图生成失败"); return R; }
            // 软约束：偶尔放行一个路程短的方案
            if (allCands.Count > 1 && rng.Next() < s.ShortPathChance) bestEdges = allCands[rng.Range(0, allCands.Count)].edges;
            R.Edges = bestEdges;

            // ---------- 8. 房间地面：从第一个出入口房间往外随机游走，相邻相连的房间最多差 1 层 ----------
            var ent0 = R.Entrances[openIdx[0]];
            int baseH = m.Height[ent0.Z * W + ent0.X];
            var floor = new int[nr];
            var depth = GraphDist(nr, R.Edges, entRoom[openIdx[0]]);
            {
                var done = new bool[nr];
                var q = new Queue<int>();
                int root = entRoom[openIdx[0]];
                floor[root] = baseH; done[root] = true; q.Enqueue(root);
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    foreach (var (a, b) in R.Edges)
                    {
                        int v = a == u ? b : (b == u ? a : -1);
                        if (v < 0 || done[v]) continue;
                        double r = rng.Next();
                        int f = floor[u] + (r < 0.25 ? -1 : (r < 0.75 ? 0 : 1));
                        if (f > baseH + 2) f = baseH + 2; if (f < baseH - 2) f = baseH - 2;
                        floor[v] = f; done[v] = true; q.Enqueue(v);
                    }
                }
                for (int pass = 0; pass < 6; pass++)
                    foreach (var (a, b) in R.Edges)
                    {
                        if (floor[a] - floor[b] > 1) floor[a] = floor[b] + 1;
                        if (floor[b] - floor[a] > 1) floor[b] = floor[a] + 1;
                    }
            }
            for (int k = 0; k < nr; k++) { R.Rooms[k].Floor = floor[k]; R.Rooms[k].Depth = depth[k]; }

            // ---------- 9. 写高度：房间地面 + 小起伏；墙 = 两侧较高地面 + 2–3 层 ----------
            int[] h = m.Height;
            var fBump = new Fbm(Hash.Of(seed, 41), 6, 2);
            var fRise = new Fbm(Hash.Of(seed, 42), 15, 2);
            var distWall = Grid.DistanceInside(Invert(wall, inner), W, H);
            for (int i = 0; i < N; i++)
            {
                if (!region[i]) continue;
                m.Slope[i] = -1;
                m.Water[i] = RegionMap.NoWater;
                m.Flags[i] &= ~(CellFlags.Ramp | CellFlags.Pond | CellFlags.CliffEdge | CellFlags.Unreachable);
                if (roomOf[i] >= 0)
                {
                    int x = i % W, z = i / W;
                    h[i] = floor[roomOf[i]] + (distWall[i] >= 3 && fBump.Sample(x, z) > 0.5 ? 1 : 0);
                }
            }
            for (int i = 0; i < N; i++)
            {
                if (!wall[i] || !inner[i]) continue;
                int x = i % W, z = i / W, hi = int.MinValue;
                for (int dz = -3; dz <= 3; dz++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n >= 0 && n < N && roomOf[n] >= 0) hi = Math.Max(hi, floor[roomOf[n]]);
                    }
                if (hi == int.MinValue) hi = baseH;
                h[i] = hi + (fRise.Sample(x, z) > 0 ? 3 : 2); // 比墙脚高 2–3 层（房间里的小起伏离墙 ≥ 3m）
            }
            // 接缝带：贴外圈不可通行的地方沿用外圈高度（崖面往里退），贴固定平地的地方从邻居高度渐变到房间地面
            var near = NearestOutside(region, W, H, s.SeamWidth + 3);
            for (int i = 0; i < N; i++)
            {
                if (!seam[i]) continue;
                int o = near[i];
                if (o < 0) { h[i] = baseH; continue; }
                int hn = m.Height[o];
                if (cls[o] == TerraceField.Border) { h[i] = hn; continue; }
                double t = Math.Min(1.0, distOut[i] / s.SeamWidth);
                // 从邻居高度渐变到房间地面高度（不抬到墙顶，免得从接缝带走上墙）
                h[i] = hn + (int)Math.Floor((baseH - hn) * t + 0.5);
            }
            // 接缝带每步最多 1 层（防止出现贴着固定世界的乱崖）：从外往里平滑
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < N; i++)
                {
                    if (!seam[i] || (near[i] >= 0 && cls[near[i]] == TerraceField.Border)) continue;
                    int x = i % W, z = i / W, lo = int.MaxValue;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (n < 0 || n >= N || (!seam[n] && region[n])) continue;
                        if (!region[n] && cls[n] == TerraceField.Border) continue;
                        lo = Math.Min(lo, h[n]);
                    }
                    if (lo != int.MaxValue && h[i] > lo + 1) h[i] = lo + 1;
                }

            // 墙要比旁边的接缝带也高 ≥ 2 层（接缝带一侧爬不上墙）
            for (int i = 0; i < N; i++)
            {
                if (!wall[i] || !inner[i]) continue;
                int x = i % W, z = i / W, hs = int.MinValue;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n >= 0 && n < N && seam[n]) hs = Math.Max(hs, h[n]);
                    }
                if (hs != int.MinValue && h[i] < hs + 2) h[i] = hs + 2;
            }

            // ---------- 10. 通路：在连接边对应的墙上开口，宽度随机（≥ 3m，可以很宽） ----------
            var oneWayCands = new List<(int a, int b)>();
            foreach (var (a, b) in R.Edges)
            {
                int ra = Math.Min(a, b), rb = Math.Max(a, b);
                if (!sepCells.TryGetValue(((long)ra << 32) | (uint)rb, out var cells)) continue;
                OpenWall(m, cells, roomOf, floor, ra, rb, s, ref rng, wall, W, N, -1);
                if (Math.Abs(floor[ra] - floor[rb]) == 1 && IsOnCycle(nr, R.Edges, a, b)) oneWayCands.Add((a, b));
            }
            // 单向落差：环上一条地面差 1 层的边，开口处做成“上 1 层的坎、再跳下 2 层”
            if (oneWayCands.Count > 0 && rng.Next() < s.OneWayChance)
            {
                var (a, b) = oneWayCands[rng.Range(0, oneWayCands.Count)];
                int hiR = floor[a] > floor[b] ? a : b;
                int ra = Math.Min(a, b), rb = Math.Max(a, b);
                var cells = sepCells[((long)ra << 32) | (uint)rb];
                int ledge = floor[hiR] + 1;
                int cx = 0, cz = 0, cnt = 0;
                foreach (int i in cells)
                    if (!wall[i]) { h[i] = ledge; cx += i % W; cz += i / W; cnt++; }
                if (cnt > 0) R.OneWay = (cx / cnt, cz / cnt);
            }

            // ---------- 11. 出入口通道：开放的从固定侧往里铺 3–4m 宽，每格最多 1 层；关闭的不开 ----------
            var corridor = new bool[N];
            for (int k = 0; k < R.Entrances.Count; k++)
            {
                var e = R.Entrances[k];
                if (!e.Open || entRoom[k] < 0) continue;
                int fh = m.Height[e.Z * W + e.X];
                int rh = floor[entRoom[k]];
                var path = entPath[k];
                int px = Grid.DX4[(e.Dir + 1) & 3], pz = Grid.DZ4[(e.Dir + 1) & 3];
                int half = 1 + (rng.Next() < 0.5 ? 1 : 0);
                for (int j = 0; j < path.Count; j++)
                {
                    // 高度从固定侧渐变到房间地面，每格最多 1 层
                    int want = fh + Math.Sign(rh - fh) * Math.Min(Math.Abs(rh - fh), j + 1); // 每格最多 1 层
                    for (int w = -half; w <= half; w++)
                    {
                        int x = path[j] % W + px * w, z = path[j] / W + pz * w;
                        if (!m.InBounds(x, z)) continue;
                        int i = z * W + x;
                        if (!region[i]) continue;
                        corridor[i] = true; wall[i] = false; seam[i] = false;
                        h[i] = want;
                    }
                }
            }
            R.Corridor = corridor;

            // ---------- 12. 房间内部：浅水（1 层）、石块（1–2 层）、草丛 ----------
            var grass = new bool[N];
            var rock = new bool[N];
            var fWater = new Fbm(Hash.Of(seed, 51), 13, 2);
            var fGrass = new Fbm(Hash.Of(seed, 52), 6, 2);
            var distOpen = Grid.DistanceInside(Invert(OpeningMask(m, roomOf, wall, inner, corridor, N), null), W, H);
            for (int k = 0; k < nr; k++)
            {
                var room = R.Rooms[k];
                bool water = rng.Next() < s.WaterChance;
                room.Water = water;
                int shape = rng.Range(0, 2); // 0 = 弯曲水道，1 = 水塘（中间可有小岛）
                for (int z = room.Z0; z < room.Z1; z++)
                    for (int x = room.X0; x < room.X1; x++)
                    {
                        int i = z * W + x;
                        if (roomOf[i] != k) continue;
                        if (water && distWall[i] >= 2)
                        {
                            double v = fWater.Sample(x, z);
                            bool wet = shape == 0 ? Math.Abs(v) < 0.09 : (v > 0.3 && v < 0.62);
                            if (wet) { h[i] = floor[k] - 1; m.Water[i] = floor[k]; m.Flags[i] |= CellFlags.Pond; continue; }
                        }
                        if (fGrass.Sample(x, z) > 0.28) grass[i] = true;
                    }
                // 石块：0–2 个，离墙 ≥ 3、离开口 ≥ 4
                int rocks = rng.Range(0, 3);
                for (int t = 0; t < rocks; t++)
                {
                    for (int tryk = 0; tryk < 12; tryk++)
                    {
                        int x = room.X0 + rng.Range(0, Math.Max(1, room.X1 - room.X0)), z = room.Z0 + rng.Range(0, Math.Max(1, room.Z1 - room.Z0));
                        int i = z * W + x;
                        if (roomOf[i] != k || distWall[i] < 4 || distOpen[i] < 5 || m.HasWater(i)) continue;
                        double rr = 1 + rng.Next() * 1.2;
                        int rh = floor[k] + (rng.Next() < 0.5 ? 1 : 2);
                        for (int dz = -2; dz <= 2; dz++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                if (dx * dx + dz * dz > rr * rr) continue;
                                int n = (z + dz) * W + x + dx;
                                if (roomOf[n] != k || m.HasWater(n)) continue;
                                h[n] = rh; rock[n] = true; grass[n] = false;
                            }
                        break;
                    }
                }
            }
            R.Grass = grass; R.Rock = rock;

            // Biome：房间沿用所在大区；墙顶按针叶林（以后接植物层）
            for (int i = 0; i < N; i++)
                if (region[i] && wall[i] && inner[i])
                {
                    for (int b = 0; b < m.Weights.Length; b++) m.Weights[b][i] = 0;
                    m.Biome[i] = (byte)BiomeId.Conifer; m.Weights[(int)BiomeId.Conifer][i] = 1;
                }

            // ---------- 13. 校验 ----------
            var ok = Validate(m, cls, R, region, inner, corridor, out var lines);
            R.Report.AddRange(lines);
            R.Ok = ok;
            if (!ok) return R;

            // ---------- 14. 危险区与奖励（按面积；越深越危险，奖励放深处 / 死路 / 岛） ----------
            PlaceContent(R, s, ref rng, area, roomOf, distWall, distOpen, W);
            int deg3 = 0, deg4 = 0;
            var dg = new int[nr];
            foreach (var (a, b) in R.Edges) { dg[a]++; dg[b]++; }
            foreach (int v in dg) { if (v == 3) deg3++; if (v >= 4) deg4++; }
            int dead = 0; foreach (int v in dg) if (v == 1) dead++;
            int pathLen = -1;
            if (openIdx.Count >= 2) pathLen = depth[entRoom[openIdx[1]]];
            R.Report.Insert(0, $"房间 {nr}（目标 {target}，可切面积 {area} m²），连接 {R.Edges.Count}，3 通 {deg3}，4 通 {deg4}，死路房 {dead}，"
                + (R.Edges.Count >= nr ? "有环" : "无环") + $"，开放出入口 {openIdx.Count}/{R.Entrances.Count}"
                + (pathLen >= 0 ? $"，出入口之间隔 {pathLen} 个房间" : "") + (R.OneWay.HasValue ? "，有单向落差" : "")
                + $"，危险区 {R.Danger.Count}，奖励 {R.Rewards.Count}");
            return R;
        }

        static bool[] Invert(bool[] a, bool[] within)
        {
            var r = new bool[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = !a[i];
            return r;
        }

        static bool[] OpeningMask(RegionMap m, int[] roomOf, bool[] wall, bool[] inner, bool[] corridor, int N)
        {
            // 开口格 = 原来是墙、现在被打开的格（高度不高于两侧）+ 出入口通道
            var r = new bool[N];
            for (int i = 0; i < N; i++) if (corridor[i] || (inner[i] && roomOf[i] < 0 && !wall[i])) r[i] = true;
            return r;
        }

        static int SpanLength(List<int> cells, int W)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (int i in cells) { int x = i % W, z = i / W; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); }
            return Math.Max(maxX - minX, maxZ - minZ) + 1;
        }

        static void OpenWall(RegionMap m, List<int> cells, int[] roomOf, int[] floor, int ra, int rb, RandomRegionSettings s, ref Rng rng, bool[] wall, int W, int N, int ledge)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (int i in cells) { int x = i % W, z = i / W; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); }
            bool alongX = maxX - minX >= maxZ - minZ;
            int lo = alongX ? minX : minZ, hi = alongX ? maxX : maxZ;
            int span = hi - lo + 1;
            // 两端各留 2 格不开（墙角）
            int usable = span - 4;
            if (usable < s.MinOpening) usable = Math.Min(span, s.MinOpening);
            double u = rng.Next();
            int width = s.MinOpening + (int)Math.Floor((usable - s.MinOpening) * u * u * u + 0.5); // 多数窄，偶尔很宽
            if (width < s.MinOpening) width = s.MinOpening;
            int start = lo + 2 + rng.Range(0, Math.Max(1, usable - width + 1));
            if (span - 4 < s.MinOpening) start = lo + Math.Max(0, (span - width) / 2);
            int fl = Math.Min(floor[ra], floor[rb]);
            int fh = Math.Max(floor[ra], floor[rb]);
            foreach (int i in cells)
            {
                int p = alongX ? i % W : i / W;
                if (p < start || p >= start + width) continue;
                wall[i] = false;
                // 开口里的地面：靠近哪个房间就取哪个房间的地面，差 1 层就是一级台阶
                int x = i % W, z = i / W, best = int.MaxValue, bf = fl;
                for (int dz = -3; dz <= 3; dz++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n < 0 || n >= N) continue;
                        int r = roomOf[n];
                        if (r != ra && r != rb) continue;
                        int dd = dx * dx + dz * dz;
                        if (dd < best) { best = dd; bf = floor[r]; }
                    }
                m.Height[i] = bf;
            }
        }

        static bool IsOnCycle(int n, List<(int a, int b)> edges, int a, int b)
        {
            // 去掉这条边后 a、b 仍连通 → 在环上
            var adj = new List<int>[n];
            for (int k = 0; k < n; k++) adj[k] = new List<int>();
            bool skipped = false;
            foreach (var (p, q) in edges)
            {
                if (!skipped && ((p == a && q == b) || (p == b && q == a))) { skipped = true; continue; }
                adj[p].Add(q); adj[q].Add(p);
            }
            var seen = new bool[n];
            var st = new Stack<int>(); st.Push(a); seen[a] = true;
            while (st.Count > 0) { int u = st.Pop(); if (u == b) return true; foreach (int v in adj[u]) if (!seen[v]) { seen[v] = true; st.Push(v); } }
            return false;
        }

        static int[] GraphDist(int n, List<(int a, int b)> edges, int root)
        {
            var d = new int[n];
            for (int k = 0; k < n; k++) d[k] = int.MaxValue / 4;
            d[root] = 0;
            var q = new Queue<int>(); q.Enqueue(root);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                foreach (var (a, b) in edges)
                {
                    int v = a == u ? b : (b == u ? a : -1);
                    if (v < 0 || d[v] <= d[u] + 1) continue;
                    d[v] = d[u] + 1; q.Enqueue(v);
                }
            }
            return d;
        }

        /// <summary>每个区域格最近的区域外格（多源 BFS，上限 maxD）。</summary>
        static int[] NearestOutside(bool[] region, int W, int H, int maxD)
        {
            int N = W * H;
            var res = new int[N];
            var dist = new int[N];
            var q = new Queue<int>();
            for (int i = 0; i < N; i++)
            {
                res[i] = -1; dist[i] = int.MaxValue;
                if (!region[i]) { res[i] = i; dist[i] = 0; q.Enqueue(i); }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                if (dist[i] >= maxD) continue;
                int x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    int n = nz * W + nx;
                    if (dist[n] != int.MaxValue) continue;
                    dist[n] = dist[i] + 1; res[n] = res[i]; q.Enqueue(n);
                }
            }
            return res;
        }

        /// <summary>可达性：forward = 从 starts 出发能到的格；reverse = 能走到 starts 的格。blocked 里的格不能走。</summary>
        internal static bool[] Reach(RegionMap m, byte[] cls, IEnumerable<int> starts, bool[] blocked, bool reverse)
        {
            int W = m.W, H = m.H;
            var seen = new bool[W * H];
            var st = new Stack<int>();
            foreach (int s0 in starts) { if (s0 < 0) continue; seen[s0] = true; st.Push(s0); }
            while (st.Count > 0)
            {
                int i = st.Pop(), x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    int n = nz * W + nx;
                    if (seen[n] || (blocked != null && blocked[n])) continue;
                    bool ok = reverse
                        ? (TerraceField.Walkable(m, cls, i) && TerraceField.CanMove(m, cls, n, i, TerraceField.Opp(d)))
                        : TerraceField.CanMove(m, cls, i, n, d);
                    if (!ok) continue;
                    seen[n] = true; st.Push(n);
                }
            }
            return seen;
        }

        static bool Validate(RegionMap m, byte[] cls, RandomRegionResult R, bool[] region, bool[] inner, bool[] corridor, out List<string> lines)
        {
            lines = new List<string>();
            int W = m.W, N = W * m.H;
            var opens = new List<int>();
            var closed = new List<int>();
            foreach (var e in R.Entrances) (e.Open ? opens : closed).Add(e.Z * W + e.X);
            bool ok = true;
            var fwd = Reach(m, cls, new[] { opens[0] }, null, false);
            var back = Reach(m, cls, new[] { opens[0] }, null, true);
            // 1. 开放出入口互通
            foreach (int o in opens) if (!fwd[o] || !back[o]) { ok = false; lines.Add("[失败] 开放的出入口之间不通"); break; }
            // 2. 能走到的地方都能出去
            int trapped = 0, roomCells = 0, unreached = 0;
            for (int i = 0; i < N; i++)
            {
                if (!region[i]) continue;
                if (R.RoomOf[i] >= 0) { roomCells++; if (!fwd[i] && TerraceField.Walkable(m, cls, i)) unreached++; }
                if (fwd[i] && !back[i]) trapped++;
            }
            if (trapped > 0) { ok = false; lines.Add($"[失败] {trapped} 格进得去出不来"); }
            // 房间都要能到（个别被石块围住的角落格不算，按房间算）
            var roomReached = new bool[R.Rooms.Count];
            for (int i = 0; i < N; i++) if (R.RoomOf[i] >= 0 && fwd[i]) roomReached[R.RoomOf[i]] = true;
            int lostRooms = 0; foreach (bool b in roomReached) if (!b) lostRooms++;
            if (lostRooms > 0) { ok = false; lines.Add($"[失败] {lostRooms} 个房间走不到"); }
            // 3. 封口：不走出入口通道，从外面进不了房间
            var block = new bool[N];
            for (int i = 0; i < N; i++) block[i] = corridor[i];
            var starts = new List<int>(opens); starts.AddRange(closed);
            var outside = Reach(m, cls, starts, block, false);
            int leak = 0;
            for (int i = 0; i < N; i++) if (outside[i] && R.RoomOf[i] >= 0) leak++;
            if (leak > 0) { ok = false; lines.Add($"[失败] 不经出入口也能进房间（{leak} 格）"); }
            // 4. 连续斜坡（这里不放斜坡，检查保险）
            int consec = 0;
            for (int i = 0; i < N; i++)
            {
                if (!region[i] || m.Slope[i] < 0) continue;
                int d = m.Slope[i], x = i % W + Grid.DX4[d], z = i / W + Grid.DZ4[d];
                if (m.InBounds(x, z) && m.Slope[z * W + x] == d) consec++;
            }
            if (consec > 0) { ok = false; lines.Add($"[失败] 连续斜坡 {consec}"); }
            if (ok) lines.Add($"校验通过：出入口互通、无困死格、封口有效、所有房间可达（房间格 {roomCells}，零星走不到的角落 {unreached}）");
            return ok;
        }

        static void PlaceContent(RandomRegionResult R, RandomRegionSettings s, ref Rng rng, int area, int[] roomOf, float[] distWall, float[] distOpen, int W)
        {
            var m = R.Map;
            int nr = R.Rooms.Count;
            var deg = new int[nr];
            foreach (var (a, b) in R.Edges) { deg[a]++; deg[b]++; }
            double k1000 = area / 1000.0;
            int nReward = Math.Max(1, (int)Math.Floor(k1000 * (s.RewardMin + (s.RewardMax - s.RewardMin) * rng.Next()) + 0.5));
            int nDanger = Math.Max(1, (int)Math.Floor(k1000 * (s.DangerMin + (s.DangerMax - s.DangerMin) * rng.Next()) + 0.5));
            // 奖励：按“深度 + 死路 + 有水（岛）”排序的房间，放在离开口最远的格
            var rooms = new List<int>();
            for (int k = 0; k < nr; k++) rooms.Add(k);
            rooms.Sort((p, q) =>
            {
                double sp = R.Rooms[p].Depth * 2 + (deg[p] == 1 ? 3 : 0) + (R.Rooms[p].Water ? 1 : 0);
                double sq = R.Rooms[q].Depth * 2 + (deg[q] == 1 ? 3 : 0) + (R.Rooms[q].Water ? 1 : 0);
                return sq.CompareTo(sp);
            });
            for (int t = 0; t < nReward && t < rooms.Count; t++)
            {
                int k = rooms[t], best = -1; double bv = -1;
                var room = R.Rooms[k];
                for (int z = room.Z0; z < room.Z1; z++)
                    for (int x = room.X0; x < room.X1; x++)
                    {
                        int i = z * W + x;
                        if (roomOf[i] != k || m.HasWater(i) || R.Rock[i] || distWall[i] < 2) continue;
                        double v = distOpen[i] + 0.3 * distWall[i];
                        if (v > bv) { bv = v; best = i; }
                    }
                if (best >= 0) R.Rewards.Add((best % W, best / W));
            }
            // 危险区：先守奖励（奖励旁 3–6m），再放在深处房间的开口附近
            foreach (var (x, z) in R.Rewards)
            {
                if (R.Danger.Count >= nDanger) break;
                int k = roomOf[z * W + x];
                // 奖励旁 3–5m、在同一个房间里、离墙 ≥ 2 的一点
                int best = -1; double bv = double.MaxValue;
                for (int tries = 0; tries < 40; tries++)
                {
                    int dx = rng.Range(-5, 6), dz = rng.Range(-5, 6);
                    int i = (z + dz) * W + x + dx;
                    if (i < 0 || i >= roomOf.Length || roomOf[i] != k || distWall[i] < 2) continue;
                    double v = Math.Abs(Math.Sqrt(dx * dx + dz * dz) - 4);
                    if (v < bv) { bv = v; best = i; }
                }
                if (best >= 0) R.Danger.Add((best % W, best / W, Math.Min(2.5 + 2.5 * rng.Next(), distWall[best] + 1.5)));
            }
            var deep = new List<int>(rooms);
            int guard = 0;
            while (R.Danger.Count < nDanger && guard++ < 50)
            {
                int k = deep[Math.Min(deep.Count - 1, rng.Range(0, Math.Max(1, deep.Count / 2 + 1)))];
                var room = R.Rooms[k];
                // 房间里离开口近的一点（窄口处）
                int best = -1; double bv = double.MaxValue;
                for (int tries = 0; tries < 30; tries++)
                {
                    int x = room.X0 + rng.Range(0, Math.Max(1, room.X1 - room.X0)), z = room.Z0 + rng.Range(0, Math.Max(1, room.Z1 - room.Z0));
                    int i = z * W + x;
                    if (roomOf[i] != k || distWall[i] < 2) continue;
                    double v = Math.Abs(distOpen[i] - 6);
                    if (v < bv) { bv = v; best = i; }
                }
                if (best >= 0) R.Danger.Add((best % W, best / W, Math.Min(3 + 3 * rng.Next(), distWall[best] + 1.5)));
            }
        }
    }
}
