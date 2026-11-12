using System;
using System.Collections.Generic;
using System.Linq;

namespace Wort.Generation
{
    /// <summary>关卡生成器的可调参数（《随机区域与关卡生成》第 12 节）。</summary>
    public sealed class RandomRegionSettings
    {
        /// <summary>接缝带向内宽度（m），≤ 6【已定】。</summary>
        public int SeamWidth = 6;
        /// <summary>房间平均面积（m²），决定房间数【暂定】。</summary>
        public double AvgRoomArea = 500;
        /// <summary>房间数浮动（±比例）【暂定】。</summary>
        public double RoomCountJitter = 0.2;
        /// <summary>房间最短边（m）【已定 8】。</summary>
        public int MinSide = 6;
        /// <summary>房间最长边（m）【暂定】。</summary>
        public int MaxSide = 50;
        /// <summary>房间大小差异度 0（均匀）– 1（悬殊）【暂定】。</summary>
        public double SizeVariance = 0.8;
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
        /// <summary>每个房间出现浅水的概率。负责人 2026-11-12：房间要平整，水域和其他房间内容以后由内容规则加，地形生成不再做（代码留着给“整间沼泽房”等内容规则用）。</summary>
        public double WaterChance = 0;
        /// <summary>校验失败时最多换几次种子。</summary>
        public int MaxAttempts = 24;
        /// <summary>房间形状：true = 自然地形（Voronoi 多边形房间）；false = 地牢（矩形房间）（负责人 2026-11-11）。</summary>
        public bool NaturalRooms = true;
        /// <summary>房间区占（接缝带以内）面积的比例，其余是山体【暂定】。</summary>
        public double RoomAreaShare = 0.62;
        /// <summary>主角跳跃：水平最远（格）、最多能跳上几层（GDD 第 5 节【已定】：2–3.2 格、1 层多）。</summary>
        public double JumpReach = 3.2;
        public int JumpUp = 1;
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
        /// <summary>刷新后的格子分类（区域里的格子都是 Ground，外面沿用烘焙）。</summary>
        public byte[] Classes;
        public bool[] InRegion, Seam, Wall, Corridor, Grass, Rock, NoStand;
        public int[] RoomOf;
        public List<RegionRoom> Rooms = new List<RegionRoom>();
        public List<(int a, int b)> Edges = new List<(int a, int b)>();
        public List<RegionEntrance> Entrances = new List<RegionEntrance>();
        public List<(int x, int z, double r)> Danger = new List<(int x, int z, double r)>();
        public List<(int x, int z)> Rewards = new List<(int x, int z)>();
        public (int x, int z)? OneWay;
        public int Attempts;
        /// <summary>这次刷新的刷新种子（和世界种子组成刷新 ID）；AttemptSeed 是最终采用的那次重试的种子。</summary>
        public uint RefreshSeed, AttemptSeed;
        /// <summary>之前失败的重试及原因。</summary>
        public List<string> AttemptLog = new List<string>();
        /// <summary>小径（1 格）铺不通的段（连通测试）。空 = 全部铺通。</summary>
        public List<string> RoadFails = new List<string>();
        public bool Ok;
        public List<string> Report = new List<string>();
    }

    /// <summary>
    /// 随机区域的关卡生成器（《随机区域与关卡生成》）：在烘焙好的固定世界里，把指定的随机 chunk 重新生成一遍。
    /// 只改随机区域里的地表；固定格子一格不动。纯 C#、确定性（刷新种子 = 世界种子 + 区域编号 + 刷新次数；刷新 ID = 世界种子:刷新种子，可复制重放）。
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
            if (src.RoadKind != null) { m.EnsureRoadKind(); Array.Copy(src.RoadKind, m.RoadKind, src.RoadKind.Length); }
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

        /// <summary>
        /// 随机区域的格子：所给 chunk 里的全部格子（只除去河 / 水体）。chunk 里原来的地形（包括伸进来的外圈高地）
        /// 都会被重新生成；和外面固定 chunk 的融合只靠接缝带（负责人 2026-11-11）。
        /// </summary>
        public static bool[] RegionMask(RegionMap m, byte[] cls, IList<(int cx, int cz)> chunks)
        {
            int W = m.W, cs = m.Settings.ChunkSize;
            var mask = new bool[W * m.H];
            foreach (var (cx, cz) in chunks)
                for (int z = cz * cs; z < (cz + 1) * cs; z++)
                    for (int x = cx * cs; x < (cx + 1) * cs; x++)
                    {
                        int i = z * W + x;
                        if (m.HasWater(i) || m.Has(i, CellFlags.River)) continue;
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

        /// <summary>
        /// 连通测试用的固定世界道路（次干道，2 格）：从出生点到每个候选出入口各铺一条路（只走固定世界，不进随机区域，不靠跳跃）。
        /// 直接改 baked（之后的刷新都带着这些路）。返回报告行。
        /// </summary>
        public static List<string> ConnectFixed(RegionMap baked, byte[] cls, IList<(int cx, int cz)> chunks, IList<RegionEntrance> candidates)
        {
            var lines = new List<string>();
            var region = RegionMask(baked, cls, chunks);
            var allowed = new bool[region.Length];
            for (int i = 0; i < allowed.Length; i++) allowed[i] = !region[i];
            int spawn = baked.Index(baked.SpawnX, baked.SpawnZ);
            int ok = 0;
            foreach (var e in candidates)
            {
                var path = RoadNet.Find(baked, cls, spawn, e.Z * baked.W + e.X, allowed);
                if (path == null) { lines.Add($"固定世界道路：出生点 → 候选出入口 ({e.X},{e.Z}) 铺不通"); continue; }
                RoadNet.Paint(baked, path, 2, allowed, RegionMap.RoadMain);
                ok++;
                lines.Add($"固定世界道路：出生点 → 候选出入口 ({e.X},{e.Z}) 铺通，{path.Count} 格");
            }
            return lines;
        }

        /// <summary>某次刷新的刷新种子：世界种子 + 区域编号 + 第几次刷新。和世界种子一起组成可复制的刷新 ID（见 <see cref="FormatId"/>）。</summary>
        public static uint RefreshSeed(uint worldSeed, int regionId, int refreshIndex)
            => Hash.Of(Hash.Of(worldSeed, (uint)regionId * 7919u + 17u), (uint)refreshIndex * 104729u + 1u);

        /// <summary>刷新 ID：“世界种子:刷新种子（8 位十六进制）”，如 <c>3:9F2A61C0</c>。</summary>
        public static string FormatId(uint worldSeed, uint refreshSeed) => $"{worldSeed}:{refreshSeed:X8}";

        /// <summary>解析刷新 ID（容许空格、0x 前缀、小写）。</summary>
        public static bool TryParseId(string id, out uint worldSeed, out uint refreshSeed)
        {
            worldSeed = 0; refreshSeed = 0;
            if (string.IsNullOrWhiteSpace(id)) return false;
            var parts = id.Trim().Split(':');
            if (parts.Length != 2) return false;
            string hex = parts[1].Trim();
            if (hex.StartsWith("0x") || hex.StartsWith("0X")) hex = hex.Substring(2);
            return uint.TryParse(parts[0].Trim(), out worldSeed)
                && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out refreshSeed);
        }

        public static RandomRegionResult Refresh(RegionMap baked, byte[] cls, IList<(int cx, int cz)> chunks,
            IList<RegionEntrance> candidates, uint worldSeed, int regionId, int refreshIndex, RandomRegionSettings s)
            => RefreshWithSeed(baked, cls, chunks, candidates, RefreshSeed(worldSeed, regionId, refreshIndex), s);

        /// <summary>按刷新种子生成（重放某个刷新 ID 用）。每次重试的种子都从刷新种子派生，失败原因记在 <c>AttemptLog</c>。</summary>
        public static RandomRegionResult RefreshWithSeed(RegionMap baked, byte[] cls, IList<(int cx, int cz)> chunks,
            IList<RegionEntrance> candidates, uint refreshSeed, RandomRegionSettings s)
        {
            RandomRegionResult last = null;
            var log = new List<string>();
            for (int attempt = 0; attempt < s.MaxAttempts; attempt++)
            {
                uint seed = Hash.Of(refreshSeed, (uint)attempt);
                var r = TryOnce(baked, cls, chunks, candidates, seed, s);
                r.Attempts = attempt + 1;
                r.RefreshSeed = refreshSeed;
                r.AttemptSeed = seed;
                if (!r.Ok)
                {
                    string why = r.Report.Find(l => l.StartsWith("[失败]")) ?? (r.Report.Count > 0 ? r.Report[r.Report.Count - 1] : "未知原因");
                    log.Add($"第 {attempt + 1} 次（种子 {seed:X8}）失败：{why}");
                }
                r.AttemptLog = log;
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
            // 计算窗口：随机区域外扩 12 格（接缝、跳跃半径都在里面）；只在窗口里循环，世界再大耗时也不变
            int wx0 = W, wz0 = H, wx1 = 0, wz1 = 0;
            for (int z = 0; z < H; z++) for (int x = 0; x < W; x++) if (region[z * W + x]) { if (x < wx0) wx0 = x; if (z < wz0) wz0 = z; if (x > wx1) wx1 = x; if (z > wz1) wz1 = z; }
            wx0 = Math.Max(0, wx0 - 12); wz0 = Math.Max(0, wz0 - 12); wx1 = Math.Min(W - 1, wx1 + 12); wz1 = Math.Min(H - 1, wz1 + 12);
            var win = new int[(wx1 - wx0 + 1) * (wz1 - wz0 + 1)];
            { int k = 0; for (int z = wz0; z <= wz1; z++) for (int x = wx0; x <= wx1; x++) win[k++] = z * W + x; }
            // 区域里的格子重新生成，不再算外圈（Border）；外面的格子保持原分类
            var clsBaked = cls;
            cls = (byte[])cls.Clone();
            foreach (int i in win) if (region[i]) cls[i] = TerraceField.Ground;
            R.Classes = cls;

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
            var distOut = Grid.DistanceInside(region, W, H); // 到区域外最近格的距离
            var seam = new bool[N];
            var inner = new bool[N];
            var fSeam = new Fbm(Hash.Of(seed, 11), 9, 2);
            var fSeamBig = new Fbm(Hash.Of(seed, 12), 22, 2);
            var fSeamMid = new Fbm(Hash.Of(seed, 16), 11, 2);
            for (int z = wz0; z <= wz1; z++)
                for (int x = wx0; x <= wx1; x++)
                {
                    int i = z * W + x;
                    if (!region[i]) continue;
                    // 接缝带内缘用噪声弄弯：3.5–6m
                    // 接缝带内缘弯曲：1.5–6m，大小两层噪声（不出方框）
                    // 接缝带宽窄不一：1–6m（大小两层噪声），有的地方山体几乎贴着 chunk 边
                    // 宽度 0.5–6m：中等波长为主（不出等宽长条），山体有的地方贴着 chunk 边、有的地方凹进去成小湾
                    double sv = 0.45 + 0.8 * fSeamMid.Sample(x, z) + 0.35 * fSeamBig.Sample(x, z) + 0.2 * fSeam.Sample(x, z);
                    sv = sv < 0 ? 0 : (sv > 1 ? 1 : sv);
                    double sw = 0.5 + (s.SeamWidth - 0.5) * sv;
                    if (distOut[i] <= sw) seam[i] = true; else inner[i] = true;
                }
            inner = Grid.RemoveSmall(inner, W, H, 40);
            // 房间区整体轮廓：四角磨圆（圆形开运算），不再是方的；磨掉的部分并进接缝带（融合区）
            // 房间区外轮廓：大尺度噪声团块（可凹可凸、可以偏向一边），其余是山体；山体像自然的山包（负责人 2026-11-11）
            var mass = new bool[N];
            {
                int ix0 = W, iz0 = H, ix1 = 0, iz1 = 0, cntIn = 0;
                foreach (int i in win) if (inner[i]) { int x = i % W, z = i / W; ix0 = Math.Min(ix0, x); iz0 = Math.Min(iz0, z); ix1 = Math.Max(ix1, x); iz1 = Math.Max(iz1, z); cntIn++; }
                double ccx = ix0 + (ix1 - ix0) * (0.3 + 0.4 * rng.Next()), ccz = iz0 + (iz1 - iz0) * (0.3 + 0.4 * rng.Next());
                double rad = 0.5 * Math.Min(ix1 - ix0, iz1 - iz0) + 1;
                var fBA = new Fbm(Hash.Of(seed, 14), 30, 3);
                var fBB = new Fbm(Hash.Of(seed, 15), 12, 2);
                var bv = new double[N];
                var vals = new List<double>();
                foreach (int i in win)
                {
                    if (!inner[i]) continue;
                    int x = i % W, z = i / W;
                    double dx = x - ccx, dz = z - ccz;
                    bv[i] = 1 - 0.9 * Math.Sqrt(dx * dx + dz * dz) / rad + 0.75 * fBA.Sample(x, z) + 0.2 * fBB.Sample(x, z);
                    vals.Add(bv[i]);
                }
                vals.Sort();
                double thr = vals.Count > 0 ? vals[(int)((1 - s.RoomAreaShare) * (vals.Count - 1))] : 0;
                var blob = new bool[N];
                foreach (int i in win) blob[i] = inner[i] && bv[i] > thr;
                blob = Grid.OpenRound(blob, W, H, 3.0);
                var bl = Grid.Label(W, H, i => blob[i], (a2, b2) => true, out var bsz);
                int bigC = 0; for (int c = 1; c < bsz.Count; c++) if (bsz[c] > bsz[bigC]) bigC = c;
                foreach (int i in win)
                {
                    bool keep = blob[i] && bl[i] == bigC;
                    if (inner[i] && !keep) mass[i] = true;
                    inner[i] = keep;
                }
            }
            foreach (int i in win) if (region[i] && !inner[i] && !mass[i]) seam[i] = true;
            R.Seam = seam;

            // ---------- 3. 切房间（按房间区的总面积） ----------
            int area = 0, bx0 = W, bz0 = H, bx1 = 0, bz1 = 0;
            foreach (int i in win)
                if (inner[i])
                {
                    area++;
                    int x = i % W, z = i / W;
                    bx0 = Math.Min(bx0, x); bz0 = Math.Min(bz0, z); bx1 = Math.Max(bx1, x + 1); bz1 = Math.Max(bz1, z + 1);
                }
            if (area == 0) { R.Report.Add("区域里没有可切的面积"); return R; }
            int target = Math.Max(2, (int)Math.Floor(area / s.AvgRoomArea * (1 + s.RoomCountJitter * (2 * rng.Next() - 1)) + 0.5));
            var rects = new List<Rect>();
            var wid = new int[N];
            var edgeDist = new double[N];
            for (int i = 0; i < N; i++) { wid[i] = -1; edgeDist[i] = double.MaxValue; }
            var fWx = new Fbm(Hash.Of(seed, 21), 18, 3);
            var fWz = new Fbm(Hash.Of(seed, 22), 18, 3);
            var fWx2 = new Fbm(Hash.Of(seed, 23), 6, 2);
            var fWz2 = new Fbm(Hash.Of(seed, 24), 6, 2);
            if (s.NaturalRooms)
            {
                // 自然地形：Voronoi。撒点（最小间距），再做 0–2 次 Lloyd 松弛（差异度越低越均匀），最后在扭曲坐标里取最近点
                double minD = Math.Max(s.MinSide, 0.55 * Math.Sqrt(area / (double)target));
                var px = new List<double>(); var pz = new List<double>();
                for (int tries = 0; tries < target * 80 && px.Count < target; tries++)
                {
                    double x = bx0 + rng.Next() * (bx1 - bx0), z = bz0 + rng.Next() * (bz1 - bz0);
                    int i = (int)z * W + (int)x;
                    if (!inner[i]) continue;
                    bool far = true;
                    for (int k = 0; k < px.Count; k++) { double dx = x - px[k], dz = z - pz[k]; if (dx * dx + dz * dz < minD * minD) { far = false; break; } }
                    if (far) { px.Add(x); pz.Add(z); }
                }
                if (px.Count < 2) { R.Report.Add("撒不下房间种子"); return R; }
                int lloyd = (int)Math.Floor(2 * (1 - s.SizeVariance) + 0.5);
                for (int it = 0; it < lloyd; it++)
                {
                    var sumX = new double[px.Count]; var sumZ = new double[px.Count]; var cnt = new int[px.Count];
                    for (int z = bz0; z < bz1; z++)
                        for (int x = bx0; x < bx1; x++)
                        {
                            if (!inner[z * W + x]) continue;
                            int best = 0; double bd = double.MaxValue;
                            for (int k = 0; k < px.Count; k++) { double dx = x + 0.5 - px[k], dz = z + 0.5 - pz[k], dd = dx * dx + dz * dz; if (dd < bd) { bd = dd; best = k; } }
                            sumX[best] += x + 0.5; sumZ[best] += z + 0.5; cnt[best]++;
                        }
                    for (int k = 0; k < px.Count; k++) if (cnt[k] > 0) { px[k] = sumX[k] / cnt[k]; pz[k] = sumZ[k] / cnt[k]; }
                }
                for (int k = 0; k < px.Count; k++) rects.Add(new Rect { X0 = int.MaxValue, Z0 = int.MaxValue, X1 = int.MinValue, Z1 = int.MinValue });
                for (int z = bz0; z < bz1; z++)
                    for (int x = bx0; x < bx1; x++)
                    {
                        int i = z * W + x;
                        if (!inner[i]) continue;
                        double wx = x + 0.5 + 5.0 * fWx.Sample(x, z) + 1.3 * fWx2.Sample(x, z);
                        double wz = z + 0.5 + 5.0 * fWz.Sample(x, z) + 1.3 * fWz2.Sample(x, z);
                        int k1 = 0; double d1 = double.MaxValue, d2 = double.MaxValue;
                        for (int k = 0; k < px.Count; k++)
                        {
                            double dx = wx - px[k], dz = wz - pz[k], dd = dx * dx + dz * dz;
                            if (dd < d1) { d2 = d1; d1 = dd; k1 = k; } else if (dd < d2) d2 = dd;
                        }
                        wid[i] = k1;
                        edgeDist[i] = (Math.Sqrt(d2) - Math.Sqrt(d1)) * 0.5; // 到 Voronoi 边的近似距离
                        var r = rects[k1];
                        if (x < r.X0) r.X0 = x; if (z < r.Z0) r.Z0 = z; if (x + 1 > r.X1) r.X1 = x + 1; if (z + 1 > r.Z1) r.Z1 = z + 1;
                        rects[k1] = r;
                    }
                for (int k = 0; k < rects.Count; k++) if (rects[k].X0 == int.MaxValue) rects[k] = new Rect { X0 = bx0, Z0 = bz0, X1 = bx0, Z1 = bz0 };
            }
            else
            {
                // 地牢：矩形（递归横切 / 竖切）
                rects.Add(new Rect { X0 = bx0, Z0 = bz0, X1 = bx1, Z1 = bz1 });
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
                int cut = 0;
                for (int tryCut = 0; tryCut < 8; tryCut++)
                {
                    double tt = 0.5 + (rng.Next() - 0.5) * (0.3 + 0.7 * s.SizeVariance);
                    cut = s.MinSide + (int)Math.Floor((len - 2 * s.MinSide) * tt + 0.5);
                    // 刀口不能落在出入口通道的延长线上（两侧各留 4m），否则通道会顺着墙走
                    int line = (vertical ? r0.X0 : r0.Z0) + cut;
                    bool hit = false;
                    foreach (var e in R.Entrances)
                    {
                        bool corridorAlongCut = vertical ? (e.Dir == 0 || e.Dir == 2) : (e.Dir == 1 || e.Dir == 3);
                        if (!corridorAlongCut) continue;
                        int axis = vertical ? e.X : e.Z;
                        if (Math.Abs(line - axis) <= 4) { hit = true; break; }
                    }
                    if (!hit) break;
                }
                Rect a = r0, b = r0;
                if (vertical) { a.X1 = r0.X0 + cut; b.X0 = r0.X0 + cut; }
                else { a.Z1 = r0.Z0 + cut; b.Z0 = r0.Z0 + cut; }
                rects[pick] = a; rects.Add(b);
            }
            // 矩形编号（墙线用扭曲坐标取，弄弯）
            int RectAt(double x, double z)
            {
                for (int k = 0; k < rects.Count; k++)
                    if (x >= rects[k].X0 && x < rects[k].X1 && z >= rects[k].Z0 && z < rects[k].Z1) return k;
                return -1;
            }
            for (int z = bz0; z < bz1; z++)
                for (int x = bx0; x < bx1; x++)
                {
                    int i = z * W + x;
                    if (!inner[i]) continue;
                    double wx = x + 0.5 + 5.0 * fWx.Sample(x, z) + 1.3 * fWx2.Sample(x, z);
                    double wz = z + 0.5 + 5.0 * fWz.Sample(x, z) + 1.3 * fWz2.Sample(x, z);
                    int k = RectAt(wx, wz);
                    if (k < 0) k = RectAt(x + 0.5, z + 0.5);
                    wid[i] = k;
                    if (k >= 0)
                    {
                        // 到本矩形“内部边”（和别的矩形相邻的边）的距离，在扭曲坐标里算
                        var rk = rects[k];
                        double dIn = double.MaxValue;
                        if (rk.X0 > bx0) dIn = Math.Min(dIn, wx - rk.X0);
                        if (rk.X1 < bx1) dIn = Math.Min(dIn, rk.X1 - wx);
                        if (rk.Z0 > bz0) dIn = Math.Min(dIn, wz - rk.Z0);
                        if (rk.Z1 < bz1) dIn = Math.Min(dIn, rk.Z1 - wz);
                        edgeDist[i] = dIn;
                    }
                }

            }

            // ---------- 4. 窄墙：像自然地形——宽 1–4m 起伏变化，沿扭曲后的矩形边；偶尔长出岩块；外圈一道墙把房间和接缝带隔开 ----------
            var wall = new bool[N];
            var fWall = new Fbm(Hash.Of(seed, 31), 9, 2);
            var fBlob = new Fbm(Hash.Of(seed, 32), 5, 2);
            var distSeam = Grid.DistanceInside(inner, W, H); // 到房间区外（接缝带）的距离
            for (int z = bz0; z < bz1; z++)
                for (int x = bx0; x < bx1; x++)
                {
                    int i = z * W + x;
                    if (!inner[i]) continue;
                    double half = 0.6 + 1.5 * (fWall.Sample(x, z) + 1) * 0.5;      // 半宽 0.6–2.1 → 宽约 1–4m
                    if (edgeDist[i] < half) wall[i] = true;
                    double halfOut = 0.8 + 1.4 * (fWall.Sample(x + 91, z - 37) + 1) * 0.5;
                    if (distSeam[i] <= halfOut + 0.5) wall[i] = true;
                    // 岩块：墙边 3m 内偶尔鼓出来
                    if (!wall[i] && (edgeDist[i] < half + 3 || distSeam[i] < halfOut + 3) && fBlob.Sample(x, z) > 0.55) wall[i] = true;
                }
            // 墙必须连续：相邻矩形之间如果扭曲后缝没盖住，按旧办法补一格
            for (int z = bz0; z < bz1; z++)
                for (int x = bx0; x < bx1; x++)
                {
                    int i = z * W + x;
                    if (!inner[i] || wall[i]) continue;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (inner[n] && !wall[n] && wid[n] != wid[i]) { wall[i] = true; break; }
                    }
                }
            // 房间的角磨圆：每个矩形的房间格做一次圆形开运算，被削掉的角并进墙
            {
                var free = new bool[N];
                foreach (int i in win) free[i] = inner[i] && !wall[i];
                for (int k = 0; k < rects.Count; k++)
                {
                    var one = new bool[N];
                    var rk = rects[k];
                    for (int z = Math.Max(0, rk.Z0 - 4); z < Math.Min(H, rk.Z1 + 4); z++)
                        for (int x = Math.Max(0, rk.X0 - 4); x < Math.Min(W, rk.X1 + 4); x++)
                        { int i = z * W + x; one[i] = free[i] && wid[i] == k; }
                    var opened = Grid.OpenRound(one, W, H, 2.5);
                    for (int z = Math.Max(0, rk.Z0 - 4); z < Math.Min(H, rk.Z1 + 4); z++)
                        for (int x = Math.Max(0, rk.X0 - 4); x < Math.Min(W, rk.X1 + 4); x++)
                        { int i = z * W + x; if (one[i] && !opened[i]) wall[i] = true; }
                }
            }
            // 房间格 = 房间区里不是墙的格；按矩形编号分房间，太小的（< 30 格）并进墙
            var roomOf = new int[N];
            for (int i = 0; i < N; i++) roomOf[i] = -1;
            var lab = Grid.Label(W, H, i => inner[i] && !wall[i], (p, q) => wid[p] == wid[q], out var lsz);
            // 每个矩形取最大的一块当房间
            var bestComp = new int[rects.Count];
            for (int k = 0; k < rects.Count; k++) bestComp[k] = -1;
            foreach (int i in win)
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
            foreach (int i in win)
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
            foreach (int i in win)
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
                for (int step = 0; step < 80; step++)
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

            // ---------- 8. 地面高度场：以出入口为准（负责人 2026-11-11）——各出入口外侧固定地面高度按距离插值，再叠平缓起伏；
            //            量化成整数层，相邻格最多差 1 层，等高线是弯的 ----------
            var fFloor = new Fbm(Hash.Of(seed, 43), 24, 2);
            var Ff = new double[N];
            var fl = new int[N];
            foreach (int i in win)
            {
                if (!region[i]) continue;
                int x = i % W, z = i / W;
                double sw = 0, sh = 0;
                foreach (var e in R.Entrances)
                {
                    double dx = x - e.X, dz = z - e.Z, wgt = 1.0 / (dx * dx + dz * dz + 36.0);
                    sw += wgt; sh += wgt * m.Height[e.Z * W + e.X];
                }
                Ff[i] = (sw > 0 ? sh / sw : baked.Height[i]) + 1.3 * fFloor.Sample(x, z);
                fl[i] = (int)Math.Floor(Ff[i] + 0.5);
            }
            // 相邻格最多差 1 层（几遍松弛）
            for (int pass = 0; pass < 4; pass++)
                foreach (int i in win)
                {
                    if (!region[i]) continue;
                    int x = i % W, z = i / W;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (n < 0 || n >= N || !region[n]) continue;
                        if (fl[i] > fl[n] + 1) fl[i] = fl[n] + 1;
                    }
                }
            int baseH;
            {
                long sum = 0; int cnt = 0;
                foreach (int i in win) if (inner[i]) { sum += fl[i]; cnt++; }
                baseH = cnt > 0 ? (int)Math.Floor(sum / (double)cnt + 0.5) : m.Height[R.Entrances[openIdx[0]].Z * W + R.Entrances[openIdx[0]].X];
            }
            var floor = new int[nr];
            {
                var fs = new long[nr]; var fc = new int[nr];
                foreach (int i in win) if (roomOf[i] >= 0) { fs[roomOf[i]] += fl[i]; fc[roomOf[i]]++; }
                for (int k = 0; k < nr; k++) floor[k] = fc[k] > 0 ? (int)Math.Floor(fs[k] / (double)fc[k] + 0.5) : baseH;
            }
            // 房间平地化（负责人 2026-11-12）：每个房间整块一个整数层（高度场在房间里的平均值），不要等高线和小起伏——
            // 平整的房间留给机关 / 怪物 / 小生态，房间里的起伏以后由内容规则加。相邻（有连接）的房间最多差 1 层，开口处做成一级台阶。
            for (int pass = 0; pass < 8; pass++)
            {
                bool changed = false;
                foreach (var (a, b) in R.Edges)
                {
                    if (floor[a] > floor[b] + 1) { floor[a] = floor[b] + 1; changed = true; }
                    if (floor[b] > floor[a] + 1) { floor[b] = floor[a] + 1; changed = true; }
                }
                if (!changed) break;
            }
            foreach (int i in win) if (roomOf[i] >= 0) fl[i] = floor[roomOf[i]]; // 之后的墙高、开口、水塘、石块都以房间基准层为准
            var depth = GraphDist(nr, R.Edges, entRoom[openIdx[0]]);
            for (int k = 0; k < nr; k++) { R.Rooms[k].Floor = floor[k]; R.Rooms[k].Depth = depth[k]; }

            // ---------- 9. 写高度：房间地面（平地）；墙 = 两侧较高地面 + 2–3 层 ----------
            int[] h = m.Height;
            var fRise = new Fbm(Hash.Of(seed, 42), 15, 2);
            var distWall = Grid.DistanceInside(Invert(wall, inner), W, H);
            var distWall2 = Grid.DistanceInside(wall, W, H); // 墙格到墙外的距离（墙有多厚）
            foreach (int i in win)
            {
                if (!region[i]) continue;
                m.Slope[i] = -1;
                m.Water[i] = RegionMap.NoWater;
                m.Flags[i] &= ~(CellFlags.Ramp | CellFlags.Pond | CellFlags.CliffEdge | CellFlags.Unreachable | CellFlags.Road);
                if (roomOf[i] >= 0) h[i] = fl[i]; // 房间是平地（基准层）
            }
            if (GenDebug.On)
            {
                // 调试：每个房间里地面的组成——高度场本身的层数（等高线台阶）和小起伏（+1 的小包）
                for (int k = 0; k < nr; k++)
                {
                    var levels = new SortedDictionary<int, int>(); int bumps = 0, cells = 0;
                    foreach (int i in win)
                    {
                        if (roomOf[i] != k) continue;
                        cells++;
                        levels[fl[i]] = levels.TryGetValue(fl[i], out int c0) ? c0 + 1 : 1;
                        if (h[i] > fl[i]) bumps++;
                    }
                    GenDebug.Log.AppendLine($"  [房间地面] 房间 {k}：{cells} 格；高度场分层 " + string.Join(" ", levels.Select(kv => $"{kv.Key}层×{kv.Value}")) + $"；小起伏 +1 的格 {bumps}");
                }
            }
            foreach (int i in win)
            {
                if (!wall[i] || !inner[i]) continue;
                int x = i % W, z = i / W, hi = int.MinValue;
                for (int dz = -3; dz <= 3; dz++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n >= 0 && n < N && roomOf[n] >= 0) hi = Math.Max(hi, fl[n]);
                    }
                if (hi == int.MinValue) hi = fl[i];
                // 比墙脚高 2–3 层，墙顶高低不平（宽的地方中间再高 1 层）
                h[i] = hi + 2 + (distWall2[i] >= 3 ? 1 : 0);
            }
            // 外围山体（有机轮廓切掉的那一圈）：从房间一侧往外每 ~2.5m 升 1 层，起点比房间高 2–3 层，最高 +6
            {
                foreach (int i in win)
                {
                    if (!mass[i]) continue;
                    int x = i % W, z = i / W;
                    // 山包：Voronoi 单元一个包，单元中心高、边上低（鞍部）；离房间越远整体越高一点
                    // 大尺度山包：只差 0–1 层（顶面成片，不碎）
                    int hill = Hill(x, z, 16.0, Hash.Of(seed, 44)) >= 2 ? 1 : 0;
                    h[i] = fl[i] + 2 + hill;
                    wall[i] = true;
                }
            }
            // 接缝带：贴外圈不可通行的地方沿用外圈高度（崖面往里退），贴固定平地的地方从邻居高度渐变到房间地面
            var near = NearestOutside(region, W, H, s.SeamWidth + 3);
            foreach (int i in win)
            {
                if (!seam[i]) continue;
                int o = near[i];
                if (o < 0) { h[i] = fl[i] + 3; continue; }
                int hn = m.Height[o];
                if (clsBaked[o] == TerraceField.Border)
                {
                    // 贴着不可通行高地：从高地高度逐级降下来，每级 2 层，分界弯曲；最低一级仍比房间地面高 2 层（从房间爬不上来）
                    // 距离加噪声扭曲，台阶线不和 chunk 边平行
                    double dw = distOut[i] + 2.5 * fSeamBig.Sample(i % W * 1.3, i / W * 1.3) + 1.2 * fSeam.Sample(i % W, i / W);
                    double tt = dw / s.SeamWidth; tt = tt < 0 ? 0 : (tt > 1 ? 1 : tt);
                    int lowest = fl[i] + 3; // 和贴平地那一侧的脊顶一样高，连成一圈
                    double raw = lowest + (hn - lowest) * (1 - tt);
                    // 等高线错落：细噪声把台阶边打碎
                    int bsteps = (int)Math.Floor((raw - lowest) / 2.0 + 0.8 * fSeam.Sample(x0s(i, W) * 2.1, z0s(i, W) * 2.1));
                    h[i] = Math.Min(hn, lowest + 2 * Math.Max(0, bsteps));
                    continue;
                }
                // 接缝带只做融合：从外面固定地面的高度平滑过渡到房间区边上的地面高度（下面再松弛成每格最多 1 层）
                {
                    double t = Math.Min(1.0, distOut[i] / s.SeamWidth);
                    t = t * t * (3 - 2 * t);
                    h[i] = (int)Math.Floor(hn + (fl[i] - hn) * t + 0.5);
                }
            }
            // 融合区每格最多 1 层（贴外圈高地的台阶不动）
            for (int pass = 0; pass < 8; pass++)
                foreach (int i in win)
                {
                    if (!seam[i] || (near[i] >= 0 && clsBaked[near[i]] == TerraceField.Border)) continue;
                    int x = i % W, z = i / W, lo = int.MinValue, hi2 = int.MaxValue;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (n < 0 || n >= N) continue;
                        bool walkNb = (!region[n] && clsBaked[n] != TerraceField.Border && !m.HasWater(n)) || (seam[n] && !(near[n] >= 0 && clsBaked[near[n]] == TerraceField.Border));
                        if (!walkNb) continue;
                        lo = Math.Max(lo, h[n] - 1); hi2 = Math.Min(hi2, h[n] + 1);
                    }
                    if (lo != int.MinValue && h[i] < lo) h[i] = lo;
                    if (hi2 != int.MaxValue && h[i] > hi2) h[i] = hi2;
                }
            // 房间区外沿那道墙要比旁边能走进来的融合区高 ≥ 2 层（从融合区爬不上墙）
            foreach (int i in win)
            {
                if (!wall[i] || !(inner[i] || mass[i])) continue;
                int x = i % W, z = i / W, hs = int.MinValue;
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n >= 0 && n < N && seam[n] && !(near[n] >= 0 && clsBaked[near[n]] == TerraceField.Border)) hs = Math.Max(hs, h[n]);
                    }
                if (hs != int.MinValue && h[i] < hs + 2) h[i] = hs + 2;
            }

            // ---------- 10. 通路：在连接边对应的墙上开口，宽度随机（≥ 3m，可以很宽） ----------
            var oneWayCands = new List<(int a, int b)>();
            var openings = new Dictionary<long, List<int>>(); // 每条连接边开口的格（调试 / 最后核对用）
            foreach (var (a, b) in R.Edges)
            {
                int ra = Math.Min(a, b), rb = Math.Max(a, b);
                if (!sepCells.TryGetValue(((long)ra << 32) | (uint)rb, out var cells)) { if (GenDebug.On) GenDebug.Log.AppendLine($"  [开口] 房间 {ra}–{rb} 没有分隔墙格，没开口"); continue; }
                var before = new List<int>(); foreach (int i in cells) if (wall[i]) before.Add(i);
                OpenWall(m, cells, roomOf, fl, ra, rb, s, ref rng, wall, W, N, -1);
                var op = new List<int>(); foreach (int i in before) if (!wall[i]) op.Add(i);
                openings[((long)ra << 32) | (uint)rb] = op;
                if (IsOnCycle(nr, R.Edges, a, b)) oneWayCands.Add((a, b));
            }
            // 单向落差：环上一条地面差 1 层的边，开口处做成“上 1 层的坎、再跳下 2 层”
            (int a, int b) oneWayEdge = (-1, -1);
            if (oneWayCands.Count > 0 && rng.Next() < s.OneWayChance)
            {
                var (a, b) = oneWayCands[rng.Range(0, oneWayCands.Count)];
                oneWayEdge = (Math.Min(a, b), Math.Max(a, b));
                int ra = Math.Min(a, b), rb = Math.Max(a, b);
                var cells = sepCells[((long)ra << 32) | (uint)rb];
                int lo = int.MaxValue, hiF = int.MinValue;
                foreach (int i in cells)
                {
                    if (wall[i]) continue;
                    int x = i % W, z = i / W;
                    for (int dz = -3; dz <= 3; dz++)
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int n = (z + dz) * W + x + dx;
                            if (n < 0 || n >= N || roomOf[n] < 0) continue;
                            lo = Math.Min(lo, fl[n]); hiF = Math.Max(hiF, fl[n]);
                        }
                }
                if (hiF != int.MinValue && hiF - lo >= 1)
                {
                    int ledge = hiF + 1;
                    int cx = 0, cz = 0, cnt = 0;
                    foreach (int i in cells)
                        if (!wall[i]) { h[i] = ledge; cx += i % W; cz += i / W; cnt++; }
                    if (cnt > 0) R.OneWay = (cx / cnt, cz / cnt);
                }
            }

            // ---------- 11. 出入口通道：开放的从固定侧往里铺 3–4m 宽，每格最多 1 层；关闭的不开 ----------
            var corridor = new bool[N];
            for (int k = 0; k < R.Entrances.Count; k++)
            {
                var e = R.Entrances[k];
                if (!e.Open || entRoom[k] < 0) continue;
                int fh = m.Height[e.Z * W + e.X];
                var path = entPath[k];
                int rh;
                {
                    int lastI = path.Count > 0 ? path[path.Count - 1] : e.Z * W + e.X;
                    int nx2 = lastI % W + Grid.DX4[e.Dir], nz2 = lastI / W + Grid.DZ4[e.Dir];
                    int ni = nz2 * W + nx2;
                    rh = region[ni] ? fl[ni] : floor[entRoom[k]];
                }
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
                        corridor[i] = true; wall[i] = false; seam[i] = false; mass[i] = false;
                        h[i] = want;
                    }
                }
            }
            R.Corridor = corridor;

            // ---------- 11b. 落差大的地方的过渡：山脚碎石（零散或成片，+1 层，顶部比崖顶至少低 2 层）；台阶边上贴着的石柱 ----------
            {
                var fScree = new Fbm(Hash.Of(seed, 61), 5, 2);
                var hc = (int[])h.Clone();
                foreach (int i in win)
                {
                    if (roomOf[i] < 0 || m.HasWater(i) || corridor[i]) continue;
                    int x = i % W, z = i / W, top = int.MinValue;
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int n = (z + dz) * W + x + dx;
                            if (n >= 0 && n < N && region[n] && roomOf[n] < 0 && !corridor[n]) top = Math.Max(top, hc[n]);
                        }
                    if (top == int.MinValue || top - hc[i] < 3) continue;  // 只在高差 ≥ 3 的崖脚
                    if (fScree.Sample(x, z) < -0.05) continue;
                    if (hc[i] + 1 > top - 2) continue;
                    h[i] = hc[i] + 1; m.Flags[i] |= CellFlags.Scree;
                }
                // 山体朝外的崖脚（接缝带里能走进来的部分）：偶尔长 1 层高的小台地，打破光滑的长崖面。
                // 只放在崖比崖脚高 ≥ 3 层的地方；之后的跳跃安全会再把附近的崖顶抬到比它高 ≥ 2 层（负责人 2026-11-11）
                var fLedge = new Fbm(Hash.Of(seed, 62), 4, 2);
                foreach (int i in win)
                {
                    if (!seam[i] || corridor[i] || m.HasWater(i)) continue;
                    if (near[i] >= 0 && clsBaked[near[i]] == TerraceField.Border) continue;
                    int x = i % W, z = i / W, top = int.MinValue;
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int n = (z + dz) * W + x + dx;
                            if (n >= 0 && n < N && region[n] && (mass[n] || (wall[n] && !corridor[n]))) top = Math.Max(top, hc[n]);
                        }
                    if (top == int.MinValue || top - hc[i] < 3) continue;
                    if (fLedge.Sample(x, z) < 0.25) continue; // 偶尔、成小簇
                    h[i] = hc[i] + 1; m.Flags[i] |= CellFlags.Scree;
                }
                // 崖脚贴着 chunk 外面的固定地面时（接缝带太窄），在山体最外一圈里偶尔挖出 1 层高的小台地（固定格不动）
                foreach (int i in win)
                {
                    if (!(mass[i] || (wall[i] && !corridor[i])) || !region[i]) continue;
                    if (seam[i]) continue;
                    int x = i % W, z = i / W, foot = int.MinValue;
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (n < 0 || n >= N) continue;
                        bool outsideWalk = !region[n] && clsBaked[n] != TerraceField.Border && !m.HasWater(n);
                        bool seamWalk = seam[n] && !(near[n] >= 0 && clsBaked[near[n]] == TerraceField.Border);
                        if (outsideWalk || seamWalk) foot = Math.Max(foot, hc[n]);
                    }
                    if (foot == int.MinValue || hc[i] - foot < 3) continue;
                    if (fLedge.Sample(x * 1.3 + 11, z * 1.3) < 0.3) continue;
                    h[i] = foot + 1; mass[i] = false; wall[i] = false; m.Flags[i] |= CellFlags.Scree;
                }
                // 石柱：外围山体 / 贴高地的接缝带里，台阶边上随机立 1–2m 粗的柱子，比所在台阶高 2–3 层（看得到、上不去，不影响通行）
                int pillars = 0;
                for (int tries = 0; tries < 400 && pillars < 5; tries++)
                {
                    int x = bx0 - 6 + rng.Range(0, Math.Max(1, bx1 - bx0 + 12)), z = bz0 - 6 + rng.Range(0, Math.Max(1, bz1 - bz0 + 12));
                    if (!m.InBounds(x, z)) continue;
                    int i = z * W + x;
                    bool host = mass[i] || (seam[i] && near[i] >= 0 && clsBaked[near[i]] == TerraceField.Border);
                    if (!host) continue;
                    // 要在台阶边上：旁边有低 ≥ 2 的格
                    bool edge = false;
                    for (int d = 0; d < 4; d++) { int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d]; if (region[n] && h[n] <= h[i] - 2) edge = true; }
                    if (!edge) continue;
                    double rr = 1.1 + rng.Next() * 0.7;
                    int ph = h[i] + 2;
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            if (dx * dx + dz * dz > rr * rr) continue;
                            int n = (z + dz) * W + x + dx;
                            if (n < 0 || n >= N || !(mass[n] || seam[n]) || corridor[n]) continue;
                            if (h[n] < ph) { h[n] = ph; m.Flags[n] |= CellFlags.Spire; }
                        }
                    pillars++;
                }
            }

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
                            if (wet) { h[i] = fl[i] - 1; m.Water[i] = fl[i]; m.Flags[i] |= CellFlags.Pond; continue; }
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
                        if (roomOf[i] != k || distWall[i] < 5 || distOpen[i] < 5 || m.HasWater(i)) continue;
                        double rr = 1 + rng.Next() * 1.2;
                        int rh = fl[i] + (rng.Next() < 0.5 ? 1 : 2);
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
            foreach (int i in win)
                if (region[i] && wall[i] && inner[i])
                {
                    for (int b = 0; b < m.Weights.Length; b++) m.Weights[b][i] = 0;
                    m.Biome[i] = (byte)BiomeId.Conifer; m.Weights[(int)BiomeId.Conifer][i] = 1;
                }

            // ---------- 12b. 跳跃安全（GDD 第 5 节）：墙 / 山体 / 贴高地的台阶，顶面比周围 JumpReach+1 格内所有能站的地方高 ≥ JumpUp+1 ----------
            var noStand = new bool[N];
            var ringSeamM = new bool[N];
            foreach (int i in win)
            {
                if (!region[i]) continue;
                bool ringSeam = seam[i] && near[i] >= 0 && clsBaked[near[i]] == TerraceField.Border;
                ringSeamM[i] = ringSeam;
                if ((wall[i] && !corridor[i]) || mass[i] || ringSeam) noStand[i] = true;
            }
            var req = new int[N];
            {
                int rr = (int)Math.Ceiling(s.JumpReach) + 1;
                var hc = (int[])h.Clone();
                for (int i = 0; i < N; i++) req[i] = int.MinValue;
                foreach (int i in win)
                {
                    if (!noStand[i]) continue;
                    int x = i % W, z = i / W, top = int.MinValue;
                    for (int dz = -rr; dz <= rr; dz++)
                        for (int dx = -rr; dx <= rr; dx++)
                        {
                            if (dx * dx + dz * dz > rr * rr) continue;
                            int nx = x + dx, nz = z + dz;
                            if (!m.InBounds(nx, nz)) continue;
                            int n = nz * W + nx;
                            if (noStand[n] || (!region[n] && clsBaked[n] == TerraceField.Border)) continue;
                            if (!TerraceField.Walkable(m, cls, n)) continue;
                            top = Math.Max(top, hc[n] + (m.Slope[n] >= 0 ? 1 : 0));
                        }
                    req[i] = top == int.MinValue ? int.MinValue : top + s.JumpUp + 1;
                    if (top != int.MinValue && h[i] < req[i]) h[i] = req[i];
                }
                // 墙顶 / 山体顶平滑：5×5 平均取整（不低于跳跃安全要求），再去掉孤立的单格凸起 / 凹坑 → 顶面成片
                for (int pass = 0; pass < 3; pass++)
                {
                    var hp = (int[])h.Clone();
                    foreach (int i in win)
                    {
                        if (!noStand[i] || m.Has(i, CellFlags.Spire) || ringSeamM[i]) continue; // 贴外圈的台阶不平滑（否则沿 chunk 边出直崖）
                        int x = i % W, z = i / W, sum = 0, cnt = 0;
                        for (int dz = -2; dz <= 2; dz++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                int n = (z + dz) * W + x + dx;
                                if (n < 0 || n >= N || !noStand[n] || m.Has(n, CellFlags.Spire)) continue;
                                sum += hp[n]; cnt++;
                            }
                        int v = (int)Math.Floor(sum / (double)cnt + 0.5);
                        if (req[i] != int.MinValue && v < req[i]) v = req[i];
                        h[i] = v;
                    }
                }
                for (int pass = 0; pass < 2; pass++)
                    foreach (int i in win)
                    {
                        if (!noStand[i] || m.Has(i, CellFlags.Spire) || ringSeamM[i]) continue;
                        int x = i % W, z = i / W, hiN = int.MinValue, loN = int.MaxValue, nn = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                            if (n < 0 || n >= N || !noStand[n]) continue;
                            hiN = Math.Max(hiN, h[n]); loN = Math.Min(loN, h[n]); nn++;
                        }
                        if (nn < 2) continue;
                        if (h[i] > hiN) h[i] = Math.Max(hiN, req[i] == int.MinValue ? hiN : req[i]);
                        else if (h[i] < loN) h[i] = loN;
                    }
            }
            // 削掉孤立的高格：比 8 个邻居都高的墙 / 山体格（含石柱单格），压到邻居最高值（不低于跳跃安全要求）
            for (int pass = 0; pass < 2; pass++)
                foreach (int i in win)
                {
                    if (!noStand[i] || ringSeamM[i]) continue;
                    int x = i % W, z = i / W, mx = int.MinValue, higherOrEq = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int n = (z + dz) * W + x + dx;
                            if (n < 0 || n >= N) continue;
                            mx = Math.Max(mx, h[n]);
                            if (h[n] >= h[i]) higherOrEq++;
                        }
                    if (higherOrEq == 0 && mx != int.MinValue)
                    {
                        int v = mx;
                        if (req[i] != int.MinValue && v < req[i]) v = req[i];
                        h[i] = v;
                    }
                }
            // 贴外圈不可通行高地的山体：从外圈高度往里每格最多降 1 层（外圈的山坡顺势延伸进来，不沿 chunk 边出直崖）；只往上补，不压低
            for (int pass = 0; pass < 10; pass++)
            {
                bool changed = false;
                foreach (int i in win)
                {
                    if (!noStand[i]) continue;
                    int x = i % W, z = i / W, best = h[i];
                    // 降坡起点往里错开 0–4 格（噪声），等高线不和 chunk 边平行
                    bool hold = fSeamBig.Sample(x * 1.6 + 7, z * 1.6 - 3) > 0.1 && distOut[i] <= 2 + 3 * (fSeam.Sample(x * 0.7, z * 0.7) + 1);
                    for (int d = 0; d < 4; d++)
                    {
                        int n = (z + Grid.DZ4[d]) * W + x + Grid.DX4[d];
                        if (n < 0 || n >= N) continue;
                        bool src = (!region[n] && clsBaked[n] == TerraceField.Border) || noStand[n];
                        if (!src) continue;
                        int cand = h[n] - (hold && !region[n] ? 0 : (hold ? 0 : 1));
                        if (hold && region[n] && !noStand[n]) cand = h[n] - 1;
                        if (cand > best) best = cand;
                    }
                    if (best > h[i]) { h[i] = best; changed = true; }
                }
                if (!changed) break;
            }
            R.NoStand = noStand;

            if (GenDebug.On)
            {
                // 调试：最终每个房间里比房间基准高的格是什么造成的
                for (int k = 0; k < nr; k++)
                {
                    int f0 = R.Rooms[k].Floor, cells = 0, up1 = 0, up2 = 0, rocks = 0, spires = 0, scree = 0, ns = 0, down = 0;
                    foreach (int i in win)
                    {
                        if (roomOf[i] != k) continue;
                        cells++;
                        int dh = m.Height[i] - f0;
                        if (rock[i]) rocks++;
                        else if (m.Has(i, CellFlags.Spire)) spires++;
                        else if (m.Has(i, CellFlags.Scree)) scree++;
                        else if (noStand[i]) ns++;
                        else if (dh == 1) up1++;
                        else if (dh >= 2) up2++;
                        else if (dh < 0) down++;
                    }
                    var sd = new SortedDictionary<int, int>();
                    foreach (int i in win) if (roomOf[i] == k && m.Has(i, CellFlags.Scree)) { int dd = (int)distWall[i]; sd[dd] = sd.TryGetValue(dd, out int c1) ? c1 + 1 : 1; }
                    GenDebug.Log.AppendLine($"  [房间最终] 房间 {k} 碎石离墙距离（格）：" + string.Join(" ", sd.Select(kv => $"{kv.Key}:{kv.Value}")));
                    GenDebug.Log.AppendLine($"  [房间最终] 房间 {k}（基准 {f0} 层，{cells} 格）：高 1 层 {up1}，高 ≥2 层 {up2}，低于基准 {down}，石块 {rocks}，石柱 {spires}，碎石 {scree}，不能站（墙 / 抬高）{ns}");
                }
            }

            // ---------- 12c. 开口核对：连接图的每条边，不靠跳跃从一个房间中心走到另一个；走不通就挖开 ----------
            //            （斜墙 / 厚墙上，开口只清掉了分隔线那一条，两侧剩下的墙格会把开口堵死）
            {
                var walk = new bool[N];
                foreach (int i in win) walk[i] = region[i] && !noStand[i];
                var center = RoomCenters(m, roomOf, distWall, rock, walk, win, nr);
                foreach (var (a, b) in R.Edges)
                {
                    if (center[a] < 0 || center[b] < 0) continue;
                    if ((Math.Min(a, b), Math.Max(a, b)) == oneWayEdge) continue; // 故意做的单向落差，不挖
                    if (RoadNet.Find(m, cls, center[a], center[b], walk) != null) continue;
                    var carved = CarveOpening(m, cls, roomOf, fl, noStand, R.Wall, region, center[a], center[b], a, b, W, N);
                    foreach (int i in carved) walk[i] = true;
                    bool fixedOk = RoadNet.Find(m, cls, center[a], center[b], walk) != null;
                    R.Report.Add($"开口核对：房间 {a}–{b} 不靠跳跃走不通，挖开 {carved.Count} 格" + (fixedOk ? "后走通" : "后仍走不通"));
                    if (!fixedOk) { R.Report.Add($"[失败] 房间 {a}–{b} 的开口走不通"); R.Ok = false; return R; }
                }
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

            // ---------- 15. 小径（连通测试）：开放出入口 → 所在房间，再沿连接图把相邻房间的中心连起来；不靠跳跃 ----------
            {
                var walk = new bool[N];
                foreach (int i in win) walk[i] = region[i] && !noStand[i];
                var center = RoomCenters(m, roomOf, distWall, rock, walk, win, nr);
                var reached = new bool[nr];
                var fails = new List<string>();
                int ok2 = 0, tried = 0;
                foreach (int k in openIdx)
                {
                    var e = R.Entrances[k];
                    int ei = e.Z * W + e.X, c = center[entRoom[k]];
                    tried++;
                    var wk = (bool[])walk.Clone(); wk[ei] = true;
                    var path = c >= 0 ? RoadNet.Find(m, cls, ei, c, wk) : null;
                    if (path == null) { fails.Add($"出入口 ({e.X},{e.Z}) → 房间 {entRoom[k]}"); continue; }
                    RoadNet.Paint(m, path, 1, wk);
                    reached[entRoom[k]] = true; ok2++;
                }
                var okEdges = new List<(int a, int b)>();
                foreach (var (a, b) in R.Edges)
                {
                    if ((Math.Min(a, b), Math.Max(a, b)) == oneWayEdge) { R.Report.Add($"小径：房间 {a}–{b} 是单向落差，不铺"); continue; }
                    tried++;
                    var path = center[a] >= 0 && center[b] >= 0 ? RoadNet.Find(m, cls, center[a], center[b], walk) : null;
                    if (path == null)
                    {
                        fails.Add($"房间 {a} → 房间 {b}");
                        if (GenDebug.On)
                        {
                            // 两个房间之间相邻格的高差分布（找出卡住的地方）
                            var hist = new SortedDictionary<int, int>();
                            foreach (int i in win)
                            {
                                if (roomOf[i] != a || !walk[i]) continue;
                                int ix = i % W, iz = i / W;
                                for (int d = 0; d < 4; d++)
                                {
                                    int n = (iz + Grid.DZ4[d]) * W + ix + Grid.DX4[d];
                                    if (n < 0 || n >= N || !walk[n] || roomOf[n] == a) continue;
                                    int dh = m.Height[n] - m.Height[i];
                                    hist[dh] = hist.TryGetValue(dh, out int c0) ? c0 + 1 : 1;
                                }
                            }
                            var rr = RoadNet.Reach(m, cls, center[a], walk);
                            int ra = 0, ta = 0, rb2 = 0, tb = 0;
                            foreach (int i in win) { if (roomOf[i] == a && walk[i]) { ta++; if (rr[i]) ra++; } if (roomOf[i] == b && walk[i]) { tb++; if (rr[i]) rb2++; } }
                            GenDebug.Log.AppendLine($"  [路] 从房间 {a} 中心 ({center[a] % W},{center[a] / W}) 按路能到：房间 {a} {ra}/{ta} 格，房间 {b} {rb2}/{tb} 格；房间 {b} 中心 ({center[b] % W},{center[b] / W})");
                            {
                                int bi = -1, bj = -1, bd = int.MaxValue;
                                var la = new List<int>(); var lb = new List<int>();
                                foreach (int i in win) { if (roomOf[i] == a) la.Add(i); else if (roomOf[i] == b) lb.Add(i); }
                                foreach (int i in la) foreach (int j in lb) { int dd = Math.Abs(i % W - j % W) + Math.Abs(i / W - j / W); if (dd < bd) { bd = dd; bi = i; bj = j; } }
                                int mx = (bi % W + bj % W) / 2, mz = (bi / W + bj / W) / 2;
                                var sb = new System.Text.StringBuilder($"  [路] 房间 {a}/{b} 最近处 ({mx},{mz})，距离 {bd}；高度（a/b = 房间，# 不能站，~ 水，. 其他）：\n");
                                for (int zz = mz + 5; zz >= mz - 5; zz--)
                                {
                                    sb.Append("     ");
                                    for (int xx = mx - 7; xx <= mx + 7; xx++)
                                    {
                                        int i = zz * W + xx;
                                        char tag = roomOf[i] == a ? 'a' : roomOf[i] == b ? 'b' : noStand[i] ? '#' : m.HasWater(i) ? '~' : '.';
                                        sb.Append($"{m.Height[i],3}{tag}");
                                    }
                                    sb.Append('\n');
                                }
                                GenDebug.Log.Append(sb);
                            }
                            if (openings.TryGetValue(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b), out var opc))
                            {
                                var ob = new System.Text.StringBuilder($"  [路] 开口 {opc.Count} 格：");
                                foreach (int i in opc) ob.Append($"({i % W},{i / W}) h{m.Height[i]} wall{(R.Wall[i] ? 1 : 0)} ns{(noStand[i] ? 1 : 0)} room{roomOf[i]}; ");
                                GenDebug.Log.AppendLine(ob.ToString());
                            }
                            else GenDebug.Log.AppendLine("  [路] 这条边没有开口记录");
                            GenDebug.Log.AppendLine($"  [路] 房间 {a}（地面 {R.Rooms[a].Floor}）→ 房间 {b}（地面 {R.Rooms[b].Floor}）铺不通；房间 {a} 边上走得到的邻格高差：" + string.Join(" ", hist.Select(kv => $"{kv.Key:+0;-0;0}×{kv.Value}")));
                        }
                        continue;
                    }
                    RoadNet.Paint(m, path, 1, walk);
                    okEdges.Add((a, b)); ok2++;
                }
                // 按路能走到的房间：从出入口所在房间沿铺通的连接扩散
                for (bool grew = true; grew;)
                {
                    grew = false;
                    foreach (var (a, b) in okEdges)
                        if (reached[a] != reached[b]) { reached[a] = reached[b] = true; grew = true; }
                }
                int nReached = 0; foreach (bool v in reached) if (v) nReached++;
                R.RoadFails = fails;
                R.Report.Add($"小径（连通测试，不算跳跃）：铺通 {ok2}/{tried} 段，按路能走到的房间 {nReached}/{nr}"
                    + (fails.Count > 0 ? "；铺不通：" + string.Join("，", fails) + (R.OneWay.HasValue ? "（本次有单向落差，经过它的那段本来就走不回来）" : "") : ""));
            }
            R.Report.Insert(0, $"房间 {nr}（目标 {target}，可切面积 {area} m²），连接 {R.Edges.Count}，3 通 {deg3}，4 通 {deg4}，死路房 {dead}，"
                + (R.Edges.Count >= nr ? "有环" : "无环") + $"，开放出入口 {openIdx.Count}/{R.Entrances.Count}"
                + (pathLen >= 0 ? $"，出入口之间隔 {pathLen} 个房间" : "") + (R.OneWay.HasValue ? "，有单向落差" : "")
                + $"，危险区 {R.Danger.Count}，奖励 {R.Rewards.Count}");
            return R;
        }

        /// <summary>
        /// 房间提示用的分区（负责人 2026-11-12）：0 = 不是房间平地；1 = 房间的平整区域（基准层上能站的平地，不含水、石块、墙）；
        /// 2 = 平整区域再往里收 1 格（四周 8 格都是同一房间的平地）——以后凸起台地只能长在这里。
        /// </summary>
        public static byte[] RoomZones(RandomRegionResult r)
        {
            var m = r.Map;
            int W = m.W, H = m.H, N = W * H;
            var zone = new byte[N];
            if (r.RoomOf == null) return zone;
            bool Flat(int i)
            {
                int k = r.RoomOf[i];
                if (k < 0 || k >= r.Rooms.Count) return false;
                if (m.HasWater(i) || m.Slope[i] >= 0 || m.Height[i] != r.Rooms[k].Floor) return false;
                if ((r.Rock != null && r.Rock[i]) || (r.NoStand != null && r.NoStand[i])) return false;
                return true;
            }
            for (int i = 0; i < N; i++) if (Flat(i)) zone[i] = 1;
            for (int z = 1; z < H - 1; z++)
                for (int x = 1; x < W - 1; x++)
                {
                    int i = z * W + x;
                    if (zone[i] == 0) continue;
                    bool inner = true;
                    for (int dz = -1; dz <= 1 && inner; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int n = i + dz * W + dx;
                            if (zone[n] == 0 || r.RoomOf[n] != r.RoomOf[i]) { inner = false; break; }
                        }
                    if (inner) zone[i] = 2;
                }
            return zone;
        }

        /// <summary>每个房间离墙最远的可站格（同距离取序号小的），作为房间中心。</summary>
        static int[] RoomCenters(RegionMap m, int[] roomOf, float[] distWall, bool[] rock, bool[] walk, int[] win, int nr)
        {
            var center = new int[nr]; var best = new float[nr];
            for (int k = 0; k < nr; k++) { center[k] = -1; best[k] = -1; }
            foreach (int i in win)
            {
                int k = roomOf[i];
                if (k < 0 || m.HasWater(i) || rock[i] || !walk[i]) continue;
                if (distWall[i] > best[k] || (distWall[i] == best[k] && i < center[k])) { best[k] = distWall[i]; center[k] = i; }
            }
            return center;
        }

        /// <summary>
        /// 在两个房间之间挖开一条 3 格宽的通道：A* 找穿墙最少的路线（墙格代价 20），把路线上的墙 / 不能站的格连同两侧各 1 格清成地面，
        /// 地面高度取附近房间 a / b 的地面（和开口相同的取法）。只动区域里的格。返回挖开的格。
        /// </summary>
        static List<int> CarveOpening(RegionMap m, byte[] cls, int[] roomOf, int[] fl, bool[] noStand, bool[] wallOut, bool[] region,
            int from, int to, int ra, int rb, int W, int N)
        {
            var dist = new float[N]; var prev = new int[N]; var done = new bool[N];
            for (int i = 0; i < N; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            var open = new SortedSet<(float, int)>();
            dist[from] = 0; open.Add((0, from));
            int tx = to % W, tz = to / W;
            while (open.Count > 0)
            {
                var (_, c) = open.Min; open.Remove(open.Min);
                if (done[c]) continue;
                done[c] = true;
                if (c == to) break;
                int cx = c % W, cz = c / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + Grid.DX4[d], nz = cz + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= m.H) continue;
                    int n = nz * W + nx;
                    if (!region[n] || done[n]) continue;
                    int r = roomOf[n];
                    if (r >= 0 && r != ra && r != rb) continue; // 不穿过别的房间
                    float g = dist[c] + (noStand[n] ? 20 : 1);
                    if (g < dist[n]) { dist[n] = g; prev[n] = c; open.Add((g + Math.Abs(nx - tx) + Math.Abs(nz - tz), n)); }
                }
            }
            var carved = new List<int>();
            if (prev[to] < 0) return carved;
            var mark = new HashSet<int>();
            for (int c = to; c >= 0 && c != from; c = prev[c])
            {
                if (!noStand[c]) continue;
                int cx = c % W, cz = c / W;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int n = (cz + dz) * W + cx + dx;
                        if (n >= 0 && n < N && region[n] && noStand[n]) mark.Add(n);
                    }
            }
            foreach (int i in mark)
            {
                int x = i % W, z = i / W, best = int.MaxValue, bf = int.MinValue;
                for (int dz = -4; dz <= 4; dz++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n < 0 || n >= N) continue;
                        int r = roomOf[n];
                        if (r != ra && r != rb) continue;
                        int dd = dx * dx + dz * dz;
                        if (dd < best) { best = dd; bf = fl[n]; }
                    }
                if (bf == int.MinValue) continue;
                m.Height[i] = bf;
                m.Slope[i] = -1;
                noStand[i] = false;
                if (wallOut != null) wallOut[i] = false;
                carved.Add(i);
            }
            return carved;
        }

        static int x0s(int i, int W) => i % W;
        static int z0s(int i, int W) => i / W;

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

        static void OpenWall(RegionMap m, List<int> cells, int[] roomOf, int[] fl, int ra, int rb, RandomRegionSettings s, ref Rng rng, bool[] wall, int W, int N, int ledge)
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
            foreach (int i in cells)
            {
                int p = alongX ? i % W : i / W;
                if (p < start || p >= start + width) continue;
                wall[i] = false;
                // 开口里的地面：靠近哪个房间就取哪个房间的地面，差 1 层就是一级台阶
                int x = i % W, z = i / W, best = int.MaxValue, bf = m.Height[i];
                for (int dz = -3; dz <= 3; dz++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (n < 0 || n >= N) continue;
                        int r = roomOf[n];
                        if (r != ra && r != rb) continue;
                        int dd = dx * dx + dz * dz;
                        if (dd < best) { best = dd; bf = fl[n]; }
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
        internal static bool[] Reach(RegionMap m, byte[] cls, IEnumerable<int> starts, bool[] blocked, bool reverse,
            int rx0 = 0, int rz0 = 0, int rx1 = int.MaxValue, int rz1 = int.MaxValue)
        {
            int W = m.W, H = m.H;
            // 只在 [rx0, rx1] × [rz0, rz1] 里搜（校验用：随机区域外扩一个 chunk）
            if (rx1 > W - 1) rx1 = W - 1; if (rz1 > H - 1) rz1 = H - 1;
            var seen = new bool[W * H];
            var st = new Stack<int>();
            foreach (int s0 in starts) { if (s0 < 0) continue; seen[s0] = true; st.Push(s0); }
            while (st.Count > 0)
            {
                int i = st.Pop(), x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < rx0 || nz < rz0 || nx > rx1 || nz > rz1) continue;
                    int n = nz * W + nx;
                    if (seen[n] || (blocked != null && blocked[n])) continue;
                    bool ok = reverse
                        ? (TerraceField.Walkable(m, cls, i) && TerraceField.CanMove(m, cls, n, i, TerraceField.Opp(d)))
                        : TerraceField.CanMove(m, cls, i, n, d);
                    if (!ok) continue;
                    seen[n] = true; st.Push(n);
                }
                // 跳跃（GDD 第 5 节）：水平 ≤ 3.2 格、最多高 1 层、落差 ≤ 6，中间没有比起跳点高 1 层以上的东西挡着
                if (!TerraceField.Walkable(m, cls, i)) continue;
                for (int k = 0; k < JumpOffsets.Length; k++)
                {
                    var (jx, jz) = JumpOffsets[k];
                    int nx = x + jx, nz = z + jz;
                    if (nx < rx0 || nz < rz0 || nx > rx1 || nz > rz1) continue;
                    int n = nz * W + nx;
                    if (seen[n] || (blocked != null && blocked[n])) continue;
                    if (!TerraceField.Walkable(m, cls, n)) continue;
                    int from = reverse ? n : i, to = reverse ? i : n;
                    int hf = m.Height[from] + (m.Slope[from] >= 0 ? 1 : 0), ht = m.Height[to] + (m.Slope[to] >= 0 ? 1 : 0);
                    if (ht > hf + 1 || ht < hf - 6) continue;
                    // 中间格的遮挡
                    bool clear = true;
                    int steps = Math.Max(Math.Abs(jx), Math.Abs(jz)) * 2;
                    for (int t = 1; t < steps && clear; t++)
                    {
                        int cx = x + (int)Math.Floor(jx * t / (double)steps + 0.5), cz = z + (int)Math.Floor(jz * t / (double)steps + 0.5);
                        int c = cz * W + cx;
                        if (c == i || c == n) continue;
                        if (m.Height[c] > hf + 1) clear = false;
                    }
                    if (!clear) continue;
                    seen[n] = true; st.Push(n);
                }
            }
            return seen;
        }

        /// <summary>跳跃落点偏移：水平距离在 (1.5, 3.2] 之间（相邻格由走路处理）。</summary>
        static readonly (int, int)[] JumpOffsets = BuildJumpOffsets(3.2);
        static (int, int)[] BuildJumpOffsets(double reach)
        {
            var l = new List<(int, int)>();
            int r = (int)Math.Ceiling(reach);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dz * dz;
                    if (d2 <= 2 || d2 > reach * reach) continue;
                    l.Add((dx, dz));
                }
            return l.ToArray();
        }

        /// <summary>Voronoi 山包：格子大小 cell，每格一个抖动点；离最近点越近越高，返回 0–2 层。</summary>
        static int Hill(int x, int z, double cell, uint seed)
        {
            int gx = (int)Math.Floor(x / cell), gz = (int)Math.Floor(z / cell);
            double best = double.MaxValue;
            for (int oz = -1; oz <= 1; oz++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int cx = gx + ox, cz = gz + oz;
                    uint hsh = Hash.Of(cx, cz, seed);
                    double px = (cx + 0.15 + 0.7 * Hash.Unit(hsh)) * cell, pz = (cz + 0.15 + 0.7 * Hash.Unit(Hash.Mix(hsh))) * cell;
                    double dx = x + 0.5 - px, dz = z + 0.5 - pz, d = dx * dx + dz * dz;
                    if (d < best) best = d;
                }
            double f = 1 - Math.Sqrt(best) / (cell * 0.6);
            int v = (int)Math.Floor(2.6 * f + 0.5);
            return v < 0 ? 0 : (v > 2 ? 2 : v);
        }

        static bool Validate(RegionMap m, byte[] cls, RandomRegionResult R, bool[] region, bool[] inner, bool[] corridor, out List<string> lines)
        {
            lines = new List<string>();
            int W = m.W, N = W * m.H;
            var opens = new List<int>();
            var closed = new List<int>();
            foreach (var e in R.Entrances) (e.Open ? opens : closed).Add(e.Z * W + e.X);
            bool ok = true;
            // 搜索范围：随机区域外扩一个 chunk
            int vx0 = W, vz0 = m.H, vx1 = 0, vz1 = 0;
            for (int i = 0; i < N; i++) if (region[i]) { int x = i % W, z = i / W; if (x < vx0) vx0 = x; if (z < vz0) vz0 = z; if (x > vx1) vx1 = x; if (z > vz1) vz1 = z; }
            int cs = m.Settings.ChunkSize;
            vx0 = Math.Max(0, vx0 - cs); vz0 = Math.Max(0, vz0 - cs); vx1 += cs; vz1 += cs;
            var fwd = Reach(m, cls, new[] { opens[0] }, null, false, vx0, vz0, vx1, vz1);
            var back = Reach(m, cls, new[] { opens[0] }, null, true, vx0, vz0, vx1, vz1);
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
            if (trapped > 0)
            {
                ok = false; lines.Add($"[失败] {trapped} 格进得去出不来");
                if (GenDebug.On)
                    for (int i = 0; i < N; i++)
                        if (region[i] && fwd[i] && !back[i])
                        {
                            int x = i % W, z = i / W;
                            var sb = new System.Text.StringBuilder($"  trap ({x},{z}) room={R.RoomOf[i]} wall={R.Wall[i]} seam={R.Seam[i]} corr={corridor[i]} water={m.HasWater(i)} flags={m.Flags[i]}\n");
                            for (int dz = 2; dz >= -2; dz--) { for (int dx = -2; dx <= 2; dx++) sb.Append($"{m.Height[(z + dz) * W + x + dx],4}"); sb.Append('\n'); }
                            lines.Add(sb.ToString());
                            break;
                        }
            }
            // 房间都要能到（个别被石块围住的角落格不算，按房间算）
            var roomReached = new bool[R.Rooms.Count];
            for (int i = 0; i < N; i++) if (R.RoomOf[i] >= 0 && fwd[i]) roomReached[R.RoomOf[i]] = true;
            int lostRooms = 0; foreach (bool b in roomReached) if (!b) lostRooms++;
            if (lostRooms > 0) { ok = false; lines.Add($"[失败] {lostRooms} 个房间走不到"); }
            // 2b. 墙 / 山体 / 贴高地的台阶，任何一格都不能被够到（含跳跃）
            if (R.NoStand != null)
            {
                int onWall = 0;
                for (int i = 0; i < N; i++) if (R.NoStand[i] && fwd[i]) onWall++;
                if (onWall > 0) { ok = false; lines.Add($"[失败] {onWall} 格墙顶 / 山体能被够到"); }
            }
            // 3. 封口：不走出入口通道，从外面进不了房间
            var block = new bool[N];
            for (int i = 0; i < N; i++) block[i] = corridor[i];
            var starts = new List<int>(opens); starts.AddRange(closed);
            var outside = Reach(m, cls, starts, block, false, vx0, vz0, vx1, vz1);
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
