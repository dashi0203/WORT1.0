using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    public sealed class TerraceFieldSettings
    {
        public uint Seed = 20261111;
        /// <summary>世界边长（chunk）。5×5 = 外圈 1 圈不可通行 + 中间 3×3 可玩。</summary>
        public int Chunks = 5;
        public int BorderChunks = 1;
        /// <summary>可玩区内台地覆盖率（参考图约 40–50%）。</summary>
        public double PlateauCoverage = 0.55;
        /// <summary>可玩区内盆地覆盖率。</summary>
        public double BasinCoverage = 0.07;
        /// <summary>小于这个面积（格）的盆地剔除（负责人 2026-11-11：小于 4 格）。</summary>
        public int MinBasinArea = 4;
        /// <summary>小于这个面积的台地可以上不去（不强制放坡道）。</summary>
        public int MinRampedPlateau = 60;
        /// <summary>坡道宽（格）。</summary>
        public int RampWidth = 2;
    }

    /// <summary>
    /// 体素器第一步：随机台地 / 盆地分布（Docs/WorldGen.md 第 6 节，负责人 2026-11-11）。
    /// - 外圈不可通行：多级高地台阶，内缘有机，任何地方都爬不上去；
    /// - 底面北高南低，1 层等高台阶（能跳上）；
    /// - 台地 / 盆地：大片 + 中块 + 碎岛，边缘有机；最窄通道 3m；盆地小于 4 格剔除；
    /// - 河：北缓南曲，切穿台地成河谷；外圈里是深水峡口；
    /// - 坡道：单格斜坡 + 平台交替（不出现连续两格斜坡），保证所有平地都能走到。
    /// 只用整数与 double 加减乘除，不用 System.Random / Math.Sin 等。
    /// </summary>
    public static class TerraceField
    {
        public static System.Text.StringBuilder Debug;
        /// <summary>荒原最低海拔（层）。</summary>
        public const int MoorMinHeight = 8;
        public const byte Ground = 0, Plateau = 1, Plateau2 = 2, Basin = 3, Border = 4;

        public static RegionMap Generate(TerraceFieldSettings s) => Generate(s, out _);

        public static RegionMap Generate(TerraceFieldSettings s, out byte[] classes)
        {
            var rs = new RegionSettings { Seed = s.Seed, ChunksX = s.Chunks, ChunksZ = s.Chunks, MinY = -8 };
            var map = new RegionMap(rs);
            int W = map.W, H = map.H, N = W * H;
            int b0 = s.BorderChunks * rs.ChunkSize;
            uint seed = s.Seed;

            // ---------- 底面：北高南低 ----------
            // 阶梯式：可玩区从南到北 4 级，每级 1 层（能跳上去），分界线用域扭曲的噪声弄成有机曲线；
            // 南级平地 1 层、北级 5 层（负责人 2026-11-11：南约 −1~8，北约 3~12）。外圈沿用两端的级。
            var fG = new Fbm(Hash.Derive(seed, "tf.ground"), 70, 3);
            var fGw = new Fbm(Hash.Derive(seed, "tf.groundW"), 50, 2);
            var G = new int[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    double zw = z + 14 * fGw.Sample(x, z);
                    double t = (zw - b0) / (H - 2.0 * b0);
                    t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    G[z * W + x] = 1 + (int)Math.Floor(t * 4.999 + 0.3 * fG.Sample(x, z));
                }

            // ---------- 外圈：内缘有机，向可玩区内伸 3–12m ----------
            var cls = new byte[N];
            classes = cls;
            var fEdge = new Fbm(Hash.Derive(seed, "tf.edge"), 34, 4);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int e = Math.Min(Math.Min(x - b0, W - b0 - 1 - x), Math.Min(z - b0, H - b0 - 1 - z));
                    double ev = fEdge.Sample(x, z) * 1.6 + 0.35; ev = ev < 0 ? 0 : (ev > 1 ? 1 : ev);
                    double thick = 3 + 20 * ev;
                    if (e < thick) cls[z * W + x] = Border;
                }

            // ---------- 沼泽大区（测试用：可玩区西南一片，边缘有机）。以后由规划器的 Biome 大区给出 ----------
            double play = W - 2.0 * b0;
            // 小而不规则（负责人 2026-11-11：大片沼泽没用）：中心约 30m 的衰减 + 扭曲噪声 + 细噪声，边缘分叉、带零星卫星小块
            var fSw = new Fbm(Hash.Derive(seed, "tf.swamp"), 22, 3);
            var fSwF = new Fbm(Hash.Derive(seed, "tf.swampF"), 9, 2);
            var fSwX = new Fbm(Hash.Derive(seed, "tf.swampX"), 30, 2);
            var fSwZ = new Fbm(Hash.Derive(seed, "tf.swampZ"), 30, 2);
            double scx = b0 + play * (0.16 + 0.12 * Hash.Unit(Hash.Derive(seed, "tf.swx")));
            double scz = b0 + play * (0.18 + 0.12 * Hash.Unit(Hash.Derive(seed, "tf.swz")));
            var swamp = new bool[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (cls[i] == Border) continue;
                    double wx = x + 16 * fSwX.Sample(x, z), wz = z + 16 * fSwZ.Sample(x, z);
                    double dx = wx - scx, dz = wz - scz;
                    double v = 1 - Math.Sqrt(dx * dx + dz * dz) / 32.0 + 0.6 * fSw.Sample(wx, wz) + 0.25 * fSwF.Sample(x, z);
                    swamp[i] = v > 0.35;
                }
            swamp = Grid.RemoveSmall(Grid.OpenRound(swamp, W, H, 2.0), W, H, 60);

            // ---------- 台地候选：域扭曲 + 大中两层噪声，按覆盖率取阈值 ----------
            var fWx = new Fbm(Hash.Derive(seed, "tf.wx"), 70, 2);
            var fWz = new Fbm(Hash.Derive(seed, "tf.wz"), 70, 2);
            var fBig = new Fbm(Hash.Derive(seed, "tf.big"), 110, 4);
            var fMid = new Fbm(Hash.Derive(seed, "tf.mid"), 32, 3);
            var fFine = new Fbm(Hash.Derive(seed, "tf.fine"), 11, 2);
            var pn = new double[N];
            var vals = new List<double>();
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    double wx = x + 24 * fWx.Sample(x, z), wz = z + 24 * fWz.Sample(x, z);
                    pn[i] = 0.7 * fBig.Sample(wx, wz) + 0.4 * fMid.Sample(wx, wz) + 0.2 * fFine.Sample(x, z);
                    if (cls[i] != Border) vals.Add(pn[i]);
                }
            double thrP = QuantileOf(vals, 1 - s.PlateauCoverage);
            var plateau = new bool[N];
            for (int i = 0; i < N; i++) plateau[i] = cls[i] != Border && pn[i] > thrP && !swamp[i]; // 沼泽里不长台地

            // ---------- 河：北缓南曲 ----------
            var path = RiverPath(seed, W, H, b0);
            var pathMask = new bool[N];
            var pathIndexAt = new int[N];
            for (int i = 0; i < N; i++) pathIndexAt[i] = -1;
            for (int k = 0; k < path.Count; k++) { pathMask[path[k]] = true; pathIndexAt[path[k]] = k; }
            var notPath = new bool[N];
            for (int i = 0; i < N; i++) notPath[i] = !pathMask[i];
            var dRiver = Grid.DistanceInside(notPath, W, H);
            var nearest = NearestSource(pathIndexAt, W, H, 14);
            double HalfWidth(int i)
            {
                int z = i / W;
                double t = 1 - z / (double)(H - 1); // 0 北 → 1 南
                return 2.5 + 1.0 * t;
            }
            for (int i = 0; i < N; i++)
            {
                double hw = HalfWidth(i);
                if (dRiver[i] <= hw + 3) plateau[i] = false;              // 河谷切穿台地
                if (cls[i] == Border && dRiver[i] <= hw) cls[i] = Ground; // 外圈里的峡口（下面设为深水）
            }
            var riverMask = new bool[N];
            for (int i = 0; i < N; i++) riverMask[i] = dRiver[i] <= HalfWidth(i);

            // ---------- 清理：去尖刺、保证通道 ≥ 3m、去碎块 ----------
            plateau = Grid.OpenRound(plateau, W, H, 1.0);
            var basin = new bool[N];
            for (int pass = 0; pass < 2; pass++)
            {
                EnforceCorridor(plateau, basin, cls, riverMask, W, H);
                plateau = Grid.RemoveSmall(plateau, W, H, 4);
            }

            // ---------- 开路：被台地围住的平地，沿带噪声的最短路切一条 4–6m 的通道接到主平地 ----------
            ConnectGround(plateau, basin, cls, W, H, seed);
            plateau = Grid.RemoveSmall(Grid.OpenRound(plateau, W, H, 1.5), W, H, 4);
            EnforceCorridor(plateau, basin, cls, riverMask, W, H);

            // ---------- 二级台地：大块台地内部再叠一级 ----------
            var distP = Grid.DistanceInside(plateau, W, H);
            var fT2 = new Fbm(Hash.Derive(seed, "tf.t2"), 45, 3);
            var tier2 = new bool[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    tier2[i] = plateau[i] && distP[i] > 5 && fT2.Sample(x, z) > 0.05;
                }
            tier2 = Grid.RemoveSmall(Grid.OpenRound(tier2, W, H, 2.0), W, H, 80);
            for (int i = 0; i < N; i++) if (tier2[i] && distP[i] <= 3.5) tier2[i] = false; // 与台地边至少隔 3m

            // ---------- 盆地：只在平地上 ----------
            var fB1 = new Fbm(Hash.Derive(seed, "tf.b1"), 55, 3);
            var fB2 = new Fbm(Hash.Derive(seed, "tf.b2"), 18, 2);
            var bn = new double[N];
            var gvals = new List<double>();
            int interior = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (cls[i] == Border) continue;
                    interior++;
                    if (plateau[i] || swamp[i] || dRiver[i] <= HalfWidth(i) + 5) continue;
                    bn[i] = fB1.Sample(x, z) + 0.4 * fB2.Sample(x, z);
                    gvals.Add(bn[i]);
                }
            double frac = Math.Min(0.9, s.BasinCoverage * interior / Math.Max(1, gvals.Count));
            double thrB = QuantileOf(gvals, 1 - frac);
            for (int i = 0; i < N; i++)
                basin[i] = cls[i] != Border && !plateau[i] && !swamp[i] && dRiver[i] > HalfWidth(i) + 5 && bn[i] > thrB;
            basin = Grid.OpenRound(basin, W, H, 1.0);
            for (int pass = 0; pass < 2; pass++)
            {
                EnforceCorridor(plateau, basin, cls, riverMask, W, H);
                basin = Grid.RemoveSmall(basin, W, H, s.MinBasinArea);
            }
            ConnectGround(plateau, basin, cls, W, H, seed);
            basin = Grid.RemoveSmall(basin, W, H, s.MinBasinArea);
            for (int i = 0; i < N; i++) if (!plateau[i]) tier2[i] = false;

            for (int i = 0; i < N; i++)
            {
                if (cls[i] == Border) continue;
                cls[i] = tier2[i] ? Plateau2 : plateau[i] ? Plateau : basin[i] ? Basin : Ground;
            }

            // ---------- 高度 ----------
            int[] h = map.Height;
            // 河水面：沿河从北往南单调不升
            var wl = new int[path.Count];
            int run = int.MaxValue;
            for (int k = 0; k < path.Count; k++)
            {
                run = Math.Min(run, G[path[k]]);
                wl[k] = run;
                if (k > 0 && wl[k] < wl[k - 1]) map.Drops.Add(new DropPoint { X = path[k] % W, Z = path[k] / W, Drop = wl[k - 1] - wl[k] });
            }
            foreach (var p in path) map.RiverPath.Add(p);

            for (int i = 0; i < N; i++) h[i] = G[i];
            // 沼泽：底面只比森林低 1 层（能直接走进去）；水洼深 1 层（能涉水）；湿地小岛高 1 层
            var fPond = new Fbm(Hash.Derive(seed, "tf.pond"), 14, 2);
            var distSw = Grid.DistanceInside(swamp, W, H);
            var swampIsland = new bool[N];
            int pondCells = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (!swamp[i] || cls[i] != Ground) continue;
                    h[i] = G[i] - 1;
                    double v = fPond.Sample(x, z);
                    if (v > 0.22 && distSw[i] > 4) { map.Water[i] = h[i]; h[i] -= 1; map.Flags[i] |= CellFlags.Pond; pondCells++; }
                    else if (v < -0.42 && distSw[i] > 3) { h[i] += 1; swampIsland[i] = true; }
                }
            // 河岸：离水面每 2m 最多升 1 层（河谷两侧能走上来）
            for (int i = 0; i < N; i++)
            {
                if (nearest[i] < 0) continue;
                double hw = HalfWidth(i), d = dRiver[i];
                int w = wl[nearest[i]];
                if (d <= hw)
                {
                    bool inBorder = IsBorderChunk(i, W, H, b0);
                    h[i] = w - (inBorder ? 2 : 1);
                    map.Water[i] = w;
                    map.Flags[i] |= CellFlags.River;
                }
                else if (cls[i] == Ground && d <= hw + 10)
                {
                    int cap = w + (int)Math.Floor((d - hw) * 0.5);
                    if (h[i] > cap) h[i] = cap;
                }
            }
            // 盆地：每块深 2 或 3 层；边缘比外面至少低 2 层
            var basinLab = Grid.Label(W, H, i => cls[i] == Basin, (a, b) => true, out var basinSizes);
            for (int i = 0; i < N; i++)
                if (cls[i] == Basin)
                {
                    // 南部高差缓：南边盆地深 2 层，往北逐渐有 3 层的
                    double latB = (i / W - b0) / play; latB = latB < 0 ? 0 : (latB > 1 ? 1 : latB);
                    int depth = Hash.Unit(Hash.Of((uint)basinLab[i], Hash.Derive(seed, "tf.bd"))) < 0.7 * latB ? 3 : 2;
                    h[i] = G[i] - depth;
                }
            for (int i = 0; i < N; i++)
                if (cls[i] == Basin)
                {
                    int lo = int.MaxValue;
                    ForN8(i, W, H, n => { if (cls[n] == Ground && !map.HasWater(n)) lo = Math.Min(lo, h[n]); });
                    if (lo != int.MaxValue && h[i] > lo - 2) h[i] = lo - 2;
                }
            // 台地：每块高 2–4 层（小块偶尔 1 层）；边缘比外面至少高 2 层
            var platLab = Grid.Label(W, H, i => cls[i] == Plateau || cls[i] == Plateau2, (a, b) => true, out var platSizes);
            var platOff = new int[platSizes.Count];
            var platZ = new double[platSizes.Count];
            for (int i = 0; i < N; i++) if (platLab[i] >= 0) platZ[platLab[i]] += i / W;
            for (int c = 0; c < platSizes.Count; c++)
            {
                double r = Hash.Unit(Hash.Of((uint)c, Hash.Derive(seed, "tf.po")));
                // 南部高差缓（负责人 2026-11-11）：南边台地 2–3 层，北边 3–4 层；小块 1–2 / 1–3 层
                double lat = (platZ[c] / platSizes[c] - b0) / play; lat = lat < 0 ? 0 : (lat > 1 ? 1 : lat);
                platOff[c] = platSizes[c] < 150 ? 1 + (int)Math.Floor(r * (1.6 + lat) ) : 2 + (int)Math.Floor(r * 0.999 + lat * 1.2);
                if (platOff[c] > 4) platOff[c] = 4;
            }
            for (int i = 0; i < N; i++)
                if (cls[i] == Plateau || cls[i] == Plateau2) h[i] = G[i] + platOff[platLab[i]];
            for (int i = 0; i < N; i++)
                if ((cls[i] == Plateau || cls[i] == Plateau2) && platOff[platLab[i]] >= 2)
                {
                    int hi = int.MinValue;
                    ForN8(i, W, H, n => { if (cls[n] == Ground || cls[n] == Basin) hi = Math.Max(hi, h[n]); });
                    if (hi != int.MinValue && h[i] < hi + 2) h[i] = hi + 2;
                }
            for (int i = 0; i < N; i++)
                if (cls[i] == Plateau2)
                {
                    double lat2 = (i / W - b0) / play;
                    h[i] += lat2 < 0.45 ? 2 : 3; // 南部二级台地只再高 2 层
                }
            for (int i = 0; i < N; i++)
                if (cls[i] == Plateau2)
                {
                    int hi = int.MinValue;
                    ForN8(i, W, H, n => { if (cls[n] != Plateau2 && cls[n] != Border) hi = Math.Max(hi, h[n]); });
                    if (hi != int.MinValue && h[i] < hi + 2) h[i] = hi + 2;
                }
            // 外圈：多级高地，比相邻可玩格至少高 4 层
            var borderMask = new bool[N];
            for (int i = 0; i < N; i++) borderMask[i] = cls[i] == Border;
            var distB = Grid.DistanceInside(borderMask, W, H);
            var fTier = new Fbm(Hash.Derive(seed, "tf.tier"), 26, 4);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (cls[i] != Border) continue;
                    int tier = (int)Math.Floor((distB[i] + 16 * fTier.Sample(x, z)) / 13.0);
                    tier = tier < 0 ? 0 : (tier > 3 ? 3 : tier);
                    h[i] = G[i] + 8 + 3 * tier;
                }
            for (int i = 0; i < N; i++)
                if (cls[i] == Border)
                {
                    int hi = int.MinValue;
                    ForN8(i, W, H, n => { if (cls[n] != Border) hi = Math.Max(hi, Math.Max(h[n], map.HasWater(n) ? map.Water[n] : int.MinValue)); });
                    if (hi != int.MinValue && h[i] < hi + 4) h[i] = hi + 4;
                }

            // ---------- 坡道与连通 ----------
            int spawn = FindSpawn(cls, map, W, H, b0, swamp);
            map.SpawnX = spawn % W; map.SpawnZ = spawn / W;
            PlaceRamps(map, cls, s, spawn, out int unreachable, out int failedPlateaus);

            // ---------- Biome：大区（测试用自动生成）+ 小斑块 + 河漫滩湿地（Docs/WorldGen.md 第 4 节 Biome 规则） ----------
            var bio = new byte[N];
            var fCon = new Fbm(Hash.Derive(seed, "tf.con"), 42, 3);
            var fMoor = new Fbm(Hash.Derive(seed, "tf.moor"), 16, 2);
            var fWet = new Fbm(Hash.Derive(seed, "tf.wet"), 13, 2);
            var fHum = new Fbm(Hash.Derive(seed, "tf.hum"), 18, 2);
            var fFlood = new Fbm(Hash.Derive(seed, "tf.flood"), 12, 2);
            // 1) 大区：外圈针叶林；沼泽大区；出生点附近一小块人类区；其余森林（针叶林斑块随纬度从约 0 增到约 50%）
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (cls[i] == Border) { bio[i] = (byte)BiomeId.Conifer; continue; }
                    if (swamp[i]) { bio[i] = (byte)(swampIsland[i] && fCon.Sample(x, z) > -0.1 ? BiomeId.MixedForest : BiomeId.Swamp); continue; }
                    int ddx = x - map.SpawnX, ddz = z - map.SpawnZ;
                    if ((cls[i] == Ground || cls[i] == Basin) && Math.Sqrt(ddx * ddx + ddz * ddz) < 24 + 10 * fHum.Sample(x, z)) { bio[i] = (byte)BiomeId.Human; continue; }
                    double lat = (z - b0) / play; lat = lat < 0 ? 0 : (lat > 1 ? 1 : lat);
                    double conBias = (cls[i] == Plateau || cls[i] == Plateau2) ? 0.15 : 0;
                    bio[i] = (byte)(fCon.Sample(x, z) + conBias > 0.55 - 0.6 * lat ? BiomeId.Conifer : BiomeId.MixedForest);
                }
            // 1b) 孤立的小块森林（混交林里的针叶林、针叶林里的混交林）长大到 150–300 格，按噪声不均匀地长，边缘不规则（负责人 2026-11-11）
            GrowSmallPatches(bio, cls, W, H, b0, (byte)BiomeId.Conifer, (byte)BiomeId.MixedForest, Hash.Derive(seed, "tf.growC"));
            GrowSmallPatches(bio, cls, W, H, b0, (byte)BiomeId.MixedForest, (byte)BiomeId.Conifer, Hash.Derive(seed, "tf.growM"));
            // 针叶林 / 混交林交界平滑两遍（5×5 多数决），去掉锯齿和花边
            for (int pass = 0; pass < 2; pass++)
            {
                var nb = (byte[])bio.Clone();
                for (int z = 2; z < H - 2; z++)
                    for (int x = 2; x < W - 2; x++)
                    {
                        int i = z * W + x;
                        if (cls[i] == Border || (bio[i] != (byte)BiomeId.Conifer && bio[i] != (byte)BiomeId.MixedForest)) continue;
                        int con = 0, mix = 0;
                        for (int dz = -2; dz <= 2; dz++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                byte v = bio[i + dz * W + dx];
                                if (v == (byte)BiomeId.Conifer) con++; else if (v == (byte)BiomeId.MixedForest) mix++;
                            }
                        if (con >= 15) nb[i] = (byte)BiomeId.Conifer; else if (mix >= 15) nb[i] = (byte)BiomeId.MixedForest;
                    }
                bio = nb;
            }

            // 2) 小斑块：二级台地顶多荒原；森林里约 6% 小块荒原、约 5% 迷你湿地（低洼的盆地更容易是湿地）
            var moorP = new bool[N];
            var wetP = new bool[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (cls[i] == Border || swamp[i]) continue;
                    bool forest = bio[i] == (byte)BiomeId.MixedForest || bio[i] == (byte)BiomeId.Conifer;
                    // 荒原要有海拔：8 层以上才出现，越高越容易（南部地势低，荒原自然少）
                    int hm = h[i];
                    if (hm >= MoorMinHeight)
                    {
                        double ease = 0.12 * (hm - MoorMinHeight);
                        if (cls[i] == Plateau2 && fMoor.Sample(x, z) > 0.1 - ease) moorP[i] = true;
                        else if (forest && fMoor.Sample(x, z) > 0.5 - ease) moorP[i] = true;
                    }
                    if ((forest || bio[i] == (byte)BiomeId.Human) && fWet.Sample(x, z) + (cls[i] == Basin ? 0.55 : 0) > 0.6) wetP[i] = true;
                }
            // 斑块最小 18 格（负责人 2026-11-11）
            moorP = Grid.RemoveSmall(Grid.OpenRound(moorP, W, H, 1.5), W, H, 18);
            wetP = Grid.RemoveSmall(Grid.OpenRound(wetP, W, H, 1.5), W, H, 18);
            for (int i = 0; i < N; i++) { if (moorP[i]) bio[i] = (byte)BiomeId.Moor; if (wetP[i]) bio[i] = (byte)BiomeId.Swamp; }
            // 3) 河漫滩湿地：下游（可玩区南半段）每约 70m 一段，段内随机位置放一小块（沿河 6–14m、往外 2–5m，约 18–70 格），随机选一侧，边缘不规则，小于 18 格剔除
            int floodCells = 0;
            var floodP = new bool[N];
            double zFlood = b0 + play * 0.55;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    int k = nearest[i];
                    if (k < 0 || z > zFlood || cls[i] != Ground || map.HasWater(i)) continue;
                    double out0 = dRiver[i] - HalfWidth(i);
                    if (out0 <= 0) continue;
                    uint segH = Hash.Of((uint)(k / 70), Hash.Derive(seed, "tf.fseg"));
                    int segStart = (k / 70) * 70 + (int)(Hash.Unit(segH) * 50);
                    int segLen = 6 + (int)(Hash.Unit(Hash.Mix(segH)) * 9);
                    if (k < segStart || k >= segStart + segLen) continue;
                    double width = 3.5 + 1.5 * fFlood.Sample(x, z);
                    if (out0 > width) continue;
                    if (h[i] > wl[k] + 1) continue; // 只在贴水的低岸
                    int a = path[Math.Max(0, k - 4)], b = path[Math.Min(path.Count - 1, k + 4)];
                    double tx = b % W - a % W, tz = b / W - a / W;
                    double cross = tx * (z - path[k] / W) - tz * (x - path[k] % W);
                    int side = Hash.Unit(Hash.Of((uint)(k / 70), Hash.Derive(seed, "tf.fside"))) < 0.5 ? 1 : -1;
                    if (cross * side <= 0) continue;
                    floodP[i] = true;
                }
            floodP = Grid.RemoveSmall(floodP, W, H, 18);
            for (int i = 0; i < N; i++) if (floodP[i]) { bio[i] = (byte)BiomeId.Swamp; floodCells++; }
            int swampCells = 0;
            for (int i = 0; i < N; i++)
            {
                map.Biome[i] = bio[i];
                map.Weights[bio[i]][i] = 1;
                map.Tier[i] = cls[i];
                if (bio[i] == (byte)BiomeId.Swamp && !IsBorderChunk(i, W, H, b0)) swampCells++;
            }

            // ---------- 统计与自检 ----------
            int cp = 0, cp2 = 0, cb = 0, consecutive = 0, ramps = 0, cliffs = 0;
            for (int i = 0; i < N; i++)
            {
                if (IsBorderChunk(i, W, H, b0)) continue;
                if (cls[i] == Plateau) cp++;
                if (cls[i] == Plateau2) cp2++;
                if (cls[i] == Basin) cb++;
            }
            for (int i = 0; i < N; i++)
            {
                if (map.Slope[i] >= 0)
                {
                    ramps++;
                    int d = map.Slope[i], x = i % W, z = i / W;
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (map.InBounds(nx, nz) && map.Slope[nz * W + nx] == d) consecutive++;
                }
                int xx = i % W, zz = i / W;
                if (xx + 1 < W && Math.Abs(h[i] - h[i + 1]) >= 2) { map.Flags[i] |= CellFlags.CliffEdge; cliffs++; }
                if (zz + 1 < H && Math.Abs(h[i] - h[i + W]) >= 2) map.Flags[i] |= CellFlags.CliffEdge;
            }
            int playArea = (W - 2 * b0) * (H - 2 * b0);
            map.Stats["plateauPercent"] = (cp + cp2) * 100 / playArea;
            map.Stats["plateau2Percent"] = cp2 * 100 / playArea;
            map.Stats["basinPercent"] = cb * 100 / playArea;
            map.Stats["plateauBlocks"] = platSizes.Count;
            int keptBasins = 0;
            foreach (var sz in basinSizes) if (sz >= s.MinBasinArea) keptBasins++;
            map.Stats["basinBlocks"] = keptBasins;
            map.Stats["ramps"] = map.Ramps.Count;
            map.Stats["slopeCells"] = ramps;
            map.Stats["consecutiveSlopes(应为0)"] = consecutive;
            map.Stats["unreachableGround(应为0)"] = unreachable;
            map.Stats["plateausWithoutRamp(≥" + s.MinRampedPlateau + "格)"] = failedPlateaus;
            map.Stats["riverDrops"] = map.Drops.Count;
            map.Stats["swampBiomePercent"] = swampCells * 100 / playArea;
            int swR = 0; for (int i = 0; i < N; i++) if (swamp[i]) swR++;
            map.Stats["swampRegionPercent"] = swR * 100 / playArea;
            map.Stats["floodplainCells"] = floodCells;
            map.Stats["pondCells"] = pondCells;
            return map;
        }

        // =====================================================================

        static bool IsBorderChunk(int i, int W, int H, int b0)
        {
            int x = i % W, z = i / W;
            return x < b0 || z < b0 || x >= W - b0 || z >= H - b0;
        }

        static void ForN8(int i, int W, int H, Action<int> f)
        {
            int x = i % W, z = i / W;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    f(nz * W + nx);
                }
        }

        static double QuantileOf(List<double> v, double p)
        {
            if (v.Count == 0) return double.PositiveInfinity;
            var a = v.ToArray();
            Array.Sort(a);
            int k = (int)(p * (a.Length - 1));
            if (k < 0) k = 0; if (k >= a.Length) k = a.Length - 1;
            return a[k];
        }

        /// <summary>
        /// 平地（非台地、非盆地、非外圈、非河）窄于 3m 的部分并入旁边的台地或盆地，保证通道最窄 3m。
        /// </summary>
        static void EnforceCorridor(bool[] plateau, bool[] basin, byte[] cls, bool[] river, int W, int H)
        {
            int N = W * H;
            var ground = new bool[N];
            for (int i = 0; i < N; i++) ground[i] = cls[i] != Border && !plateau[i] && !basin[i];
            var open = Grid.OpenRound(ground, W, H, 1.5);
            for (int i = 0; i < N; i++)
            {
                if (!ground[i] || open[i] || river[i]) continue;
                int nb = 0, np = 0;
                ForN8(i, W, H, n => { if (basin[n]) nb++; if (plateau[n] || cls[n] == Border) np++; });
                if (nb > np) basin[i] = true; else plateau[i] = true;
            }
        }

        /// <summary>
        /// 平地（非台地、非外圈）连通：最大一片为主；其余小于 30 格的并入台地，其余沿 Dijkstra 最短路（代价带噪声，路线弯曲）
        /// 切穿台地接到主平地，通道半径 2–3m。反复直到全连通。
        /// </summary>
        static void ConnectGround(bool[] plateau, bool[] basin, byte[] cls, int W, int H, uint seed)
        {
            int N = W * H;
            var fc = new Fbm(Hash.Derive(seed, "tf.carve"), 11, 3);
            var cost = new double[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                    cost[z * W + x] = 1 + 7 * (fc.Sample(x, z) + 1) * 0.5;
            var fr = new Fbm(Hash.Derive(seed, "tf.carveR"), 9, 2);
            var fwig = new Fbm(Hash.Derive(seed, "tf.carveW"), 23, 2);
            for (int iter = 0; iter < 40; iter++)
            {
                var lab = Grid.Label(W, H, i => cls[i] != Border && !plateau[i] && !basin[i], (a, b) => true, out var sizes);
                if (sizes.Count <= 1) return;
                int main = 0;
                for (int c = 1; c < sizes.Count; c++) if (sizes[c] > sizes[main]) main = c;
                bool any = false;
                for (int i = 0; i < N; i++)
                    if (lab[i] >= 0 && lab[i] != main && sizes[lab[i]] < 30) { plateau[i] = true; }
                // Dijkstra 从主平地出发，只走台地 / 平地格
                var dist = new double[N];
                var par = new int[N];
                for (int i = 0; i < N; i++) { dist[i] = double.MaxValue; par[i] = -1; }
                var heap = new MinHeap();
                for (int i = 0; i < N; i++) if (lab[i] == main) { dist[i] = 0; heap.Push(0, i); }
                while (heap.Count > 0)
                {
                    heap.Pop(out double d, out int i);
                    if (d > dist[i]) continue;
                    int x = i % W, z = i / W;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + Grid.DX4[k], nz = z + Grid.DZ4[k];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int n = nz * W + nx;
                        if (cls[n] == Border) continue;
                        double nd = d + (plateau[n] || basin[n] ? cost[n] : 0.01);
                        if (nd < dist[n]) { dist[n] = nd; par[n] = i; heap.Push(nd, n); }
                    }
                }
                // 每个还没连上的平地块：取离主平地最近的一格，沿 par 切回去
                var best = new int[sizes.Count];
                for (int c = 0; c < best.Length; c++) best[c] = -1;
                for (int i = 0; i < N; i++)
                {
                    int c = lab[i];
                    if (c < 0 || c == main || sizes[c] < 30) continue;
                    if (best[c] < 0 || dist[i] < dist[best[c]]) best[c] = i;
                }
                for (int c = 0; c < best.Length; c++)
                {
                    int i = best[c];
                    if (i < 0 || dist[i] == double.MaxValue) continue;
                    any = true;
                    // 最短路 → 每 7 格取一个点、横向偏移 → 沿折线切，宽度随位置变化（避免横平竖直的“管子”）
                    var route = new List<int>();
                    while (i >= 0) { route.Add(i); if (lab[i] == main) break; i = par[i]; }
                    var pts = new List<(double x, double z)>();
                    for (int k = 0; k < route.Count; k += 7)
                    {
                        int a = route[k], b = route[Math.Min(route.Count - 1, k + 7)], pa = route[Math.Max(0, k - 7)];
                        double tx = b % W - pa % W, tz = b / W - pa / W, tl = Math.Sqrt(tx * tx + tz * tz);
                        double off = (k == 0) ? 0 : 4.0 * fwig.Sample(a % W, a / W);
                        pts.Add(tl > 0 ? (a % W - tz / tl * off, a / W + tx / tl * off) : (a % W, a / W));
                    }
                    { int last = route[route.Count - 1]; pts.Add((last % W, last / W)); }
                    for (int k = 0; k + 1 < pts.Count; k++)
                    {
                        var (x0, z0) = pts[k]; var (x1, z1) = pts[k + 1];
                        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                        int steps = Math.Max(1, (int)Math.Ceiling(len * 2));
                        for (int j = 0; j <= steps; j++)
                        {
                            double px = x0 + (x1 - x0) * j / steps, pz = z0 + (z1 - z0) * j / steps;
                            double r = 1.6 + 1.8 * (fr.Sample(px, pz) + 1) * 0.5;
                            int ri = (int)Math.Ceiling(r);
                            int cx = (int)Math.Floor(px + 0.5), cz = (int)Math.Floor(pz + 0.5);
                            for (int dz = -ri; dz <= ri; dz++)
                                for (int dx = -ri; dx <= ri; dx++)
                                {
                                    if (dx * dx + dz * dz > r * r) continue;
                                    int nx = cx + dx, nz = cz + dz;
                                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                                    int n = nz * W + nx;
                                    if (cls[n] != Border) { plateau[n] = false; basin[n] = false; }
                                }
                        }
                    }
                }
                if (!any) return;
            }
        }

        sealed class MinHeap
        {
            readonly List<double> k = new List<double>();
            readonly List<int> v = new List<int>();
            public int Count => k.Count;
            public void Push(double key, int val)
            {
                k.Add(key); v.Add(val);
                int i = k.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (k[p] <= k[i]) break;
                    Swap(i, p); i = p;
                }
            }
            public void Pop(out double key, out int val)
            {
                key = k[0]; val = v[0];
                int last = k.Count - 1;
                k[0] = k[last]; v[0] = v[last];
                k.RemoveAt(last); v.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < k.Count && k[l] < k[m]) m = l;
                    if (r < k.Count && k[r] < k[m]) m = r;
                    if (m == i) break;
                    Swap(i, m); i = m;
                }
            }
            void Swap(int a, int b)
            {
                double tk = k[a]; k[a] = k[b]; k[b] = tk;
                int tv = v[a]; v[a] = v[b]; v[b] = tv;
            }
        }

        /// <summary>
        /// 孤立小斑块（biome = patch，周围是 host）面积小于 150 格时，从斑块边缘按噪声门限向 host 里生长，
        /// 直到 150–300 格（每块随机）。噪声门限让边缘长得不均匀，形状不规则。不长进外圈。
        /// </summary>
        static void GrowSmallPatches(byte[] bio, byte[] cls, int W, int H, int b0, byte patch, byte host, uint seed)
        {
            int N = W * H;
            var lab = Grid.Label(W, H, i => bio[i] == patch && cls[i] != Border, (a, b) => true, out var sizes);
            var noise = new Fbm(seed, 14, 2);
            for (int c = 0; c < sizes.Count; c++)
            {
                if (sizes[c] >= 150) continue;
                int target = 150 + (int)(Hash.Unit(Hash.Of((uint)c, seed)) * 150);
                var frontier = new List<int>();
                for (int i = 0; i < N; i++) if (lab[i] == c) frontier.Add(i);
                int area = sizes[c];
                // 只有四周全是 host（或本斑块）的才算孤立小块
                bool isolated = true;
                foreach (var i in frontier)
                {
                    int x = i % W, z = i / W;
                    for (int d = 0; d < 4 && isolated; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int n = nz * W + nx;
                        if (lab[n] != c && bio[n] != host && cls[n] != Border) isolated = false;
                    }
                    if (!isolated) break;
                }
                if (!isolated) continue;
                double gate = 0.15;
                for (int round = 0; round < 60 && area < target; round++)
                {
                    var next = new List<int>();
                    foreach (var i in frontier)
                    {
                        int x = i % W, z = i / W;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                            if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                            int n = nz * W + nx;
                            if (bio[n] != host || cls[n] == Border || IsBorderChunk(n, W, H, b0)) continue;
                            if (noise.Sample(nx, nz) < gate) continue;
                            bio[n] = patch; lab[n] = c; area++; next.Add(n);
                            if (area >= target) break;
                        }
                        if (area >= target) break;
                    }
                    if (next.Count == 0) { gate -= 0.15; next = frontier; }
                    else frontier.AddRange(next);
                    if (gate < -1.2) break;
                }
            }
        }

        /// <summary>河中心线：从北边界到南边界，北段波长长、摆幅小，往南波长变短、摆幅变大。返回按北→南排列的格子序列。</summary>
        static List<int> RiverPath(uint seed, int W, int H, int b0)
        {
            var n1 = new Perlin2(Hash.Derive(seed, "tf.river1"));
            var n2 = new Perlin2(Hash.Derive(seed, "tf.river2"));
            double cx = W * 0.5 + 40 * (Hash.Unit(Hash.Derive(seed, "tf.rx")) - 0.5);
            double phase = 0;
            var pts = new List<(double x, double z)>();
            int zTop = H - b0, zBot = b0;
            for (int z = H - 1; z >= 0; z--)
            {
                // 曲率按可玩区算：可玩区北端最缓，南端最曲（外圈里沿用两端的值）
                double t = (zTop - z) / (double)(zTop - zBot);
                t = t < 0 ? 0 : (t > 1 ? 1 : t);
                phase += z < zBot ? 1.0 / 70 : 1.0 / 170 + t * (1.0 / 26 - 1.0 / 170); // 南边外圈里放缓，不出锯齿
                double amp = 4 + 30 * t;
                double m = 0.8 * n1.Sample(phase, 0.37) + 0.35 * n2.Sample(phase * 2.3, 5.1);
                double x = cx + amp * m;
                double lo = b0 + 18, hi = W - b0 - 19;
                if (x < lo) x = lo; if (x > hi) x = hi;
                pts.Add((x, z));
            }
            var cells = new List<int>();
            var seen = new HashSet<int>();
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                var (x0, z0) = pts[k]; var (x1, z1) = pts[k + 1];
                double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
                int steps = Math.Max(1, (int)Math.Ceiling(len * 2));
                for (int j = 0; j <= steps; j++)
                {
                    double t = j / (double)steps;
                    int x = (int)Math.Floor(x0 + (x1 - x0) * t + 0.5), z = (int)Math.Floor(z0 + (z1 - z0) * t + 0.5);
                    if (x < 0 || z < 0 || x >= W || z >= H) continue;
                    int i = z * W + x;
                    if (seen.Add(i)) cells.Add(i);
                }
            }
            return cells;
        }

        /// <summary>多源 BFS：每格最近的河中心线格序号（距离上限 maxD）。</summary>
        static int[] NearestSource(int[] src, int W, int H, int maxD)
        {
            int N = W * H;
            var res = new int[N];
            var dist = new int[N];
            var q = new Queue<int>();
            for (int i = 0; i < N; i++)
            {
                res[i] = src[i];
                dist[i] = src[i] >= 0 ? 0 : int.MaxValue;
                if (src[i] >= 0) q.Enqueue(i);
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

        static int FindSpawn(byte[] cls, RegionMap map, int W, int H, int b0, bool[] avoid)
        {
            int tx = W / 2, tz = b0 + 40, best = -1, bestD = int.MaxValue;
            var lab = Grid.Label(W, H, i => cls[i] == Ground, (a, b) => true, out var sizes);
            int main = 0;
            for (int c = 1; c < sizes.Count; c++) if (sizes[c] > sizes[main]) main = c;
            for (int i = 0; i < W * H; i++)
            {
                if (cls[i] != Ground || lab[i] != main || avoid[i] || map.Water[i] != RegionMap.NoWater || IsBorderChunk(i, W, H, b0)) continue;
                // 周围 5×5 都是同高平地
                int x = i % W, z = i / W, ok = 1;
                for (int dz = -2; dz <= 2 && ok == 1; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int n = (z + dz) * W + x + dx;
                        if (cls[n] != Ground || map.Height[n] != map.Height[i] || map.Water[n] != RegionMap.NoWater) { ok = 0; break; }
                    }
                if (ok == 0) continue;
                int d = (x - tx) * (x - tx) + (z - tz) * (z - tz);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---------------- 通行与坡道 ----------------

        static int Opp(int d) => (d + 2) & 3;

        /// <summary>从格 i 向方向 d 那条边上的站立高度。斜坡：上坡侧 h+1，下坡侧 h，侧边按 h+1。</summary>
        static int EdgeH(RegionMap m, int i, int d)
        {
            int s = m.Slope[i];
            if (s < 0) return m.Height[i];
            return d == Opp(s) ? m.Height[i] : m.Height[i] + 1;
        }

        static bool Walkable(RegionMap m, byte[] cls, int i)
        {
            if (cls[i] == Border) return false;
            if (m.Water[i] != RegionMap.NoWater && m.Water[i] - m.Height[i] >= 2) return false;
            return true;
        }

        /// <summary>a → b（方向 d）能不能走：往上最多 1 层，往下最多 6 层（7 层以上必死）。</summary>
        static bool CanMove(RegionMap m, byte[] cls, int a, int b, int d)
        {
            if (!Walkable(m, cls, b)) return false;
            int diff = EdgeH(m, b, Opp(d)) - EdgeH(m, a, d);
            return diff <= 1 && diff >= -6;
        }

        static bool[] Reach(RegionMap m, byte[] cls, int start, bool reverse)
        {
            int W = m.W, H = m.H;
            var seen = new bool[W * H];
            if (start < 0) return seen;
            var st = new Stack<int>();
            seen[start] = true; st.Push(start);
            while (st.Count > 0)
            {
                int i = st.Pop(), x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    int n = nz * W + nx;
                    if (seen[n]) continue;
                    bool ok = reverse ? (Walkable(m, cls, i) && CanMove(m, cls, n, i, Opp(d))) : CanMove(m, cls, i, n, d);
                    if (!ok) continue;
                    seen[n] = true; st.Push(n);
                }
            }
            return seen;
        }

        static bool[] Good(RegionMap m, byte[] cls, int spawn)
        {
            var f = Reach(m, cls, spawn, false);
            var b = Reach(m, cls, spawn, true);
            var g = new bool[f.Length];
            for (int i = 0; i < g.Length; i++) g[i] = f[i] && b[i];
            return g;
        }

        /// <summary>双向区：相邻两格高差 ≤ 1（按边高）就算同一区。</summary>
        static int[] Zones(RegionMap m, byte[] cls, out List<int> sizes)
        {
            int W = m.W;
            return Grid.Label(m.W, m.H, i => Walkable(m, cls, i), (a, b) =>
            {
                int d = b == a + 1 ? 1 : b == a - 1 ? 3 : b == a + W ? 0 : 2;
                return CanMove(m, cls, a, b, d) && CanMove(m, cls, b, a, Opp(d));
            }, out sizes);
        }

        struct Cand { public int Top, Dir, Diff; public uint Key; }

        static void PlaceRamps(RegionMap m, byte[] cls, TerraceFieldSettings s, int spawn, out int unreachable, out int failedPlateaus)
        {
            int W = m.W, H = m.H, N = W * H;
            var tried = new HashSet<long>();
            uint rseed = Hash.Derive(s.Seed, "tf.ramp");
            for (int round = 0; round < 400; round++)
            {
                var good = Good(m, cls, spawn);
                var zone = Zones(m, cls, out var sizes);
                // 找一个还没连上、该连的区
                int target = -1;
                var zoneCls = new byte[sizes.Count];
                var zoneHasGood = new bool[sizes.Count];
                var zoneMin = new int[sizes.Count];
                for (int z = 0; z < zoneMin.Length; z++) zoneMin[z] = -1;
                for (int i = 0; i < N; i++)
                {
                    if (zone[i] < 0) continue;
                    if (zoneMin[zone[i]] < 0) { zoneMin[zone[i]] = i; zoneCls[zone[i]] = cls[i]; }
                    if (good[i]) zoneHasGood[zone[i]] = true;
                }
                long KeyOf(int z) => ((long)zoneMin[z] << 20) ^ sizes[z];
                // 按区编号的确定性顺序：先平地 / 盆地，再台地
                for (int pass = 0; pass < 2 && target < 0; pass++)
                    for (int z = 0; z < sizes.Count; z++)
                    {
                        if (zoneHasGood[z] || tried.Contains(KeyOf(z))) continue;
                        bool isPlat = zoneCls[z] == Plateau || zoneCls[z] == Plateau2;
                        if (pass == 0 && isPlat) continue;
                        if (isPlat && sizes[z] < s.MinRampedPlateau) continue;
                        if (!isPlat && sizes[z] < 4) continue;
                        target = z; break;
                    }
                if (target < 0) break;
                long key = KeyOf(target);
                // 候选：区边上一格 a（属 target）与相邻好格 b，高差 2–6
                var cands = new List<Cand>();
                for (int a = 0; a < N; a++)
                {
                    if (zone[a] != target) continue;
                    int x = a % W, z = a / W;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (!m.InBounds(nx, nz)) continue;
                        int b = nz * W + nx;
                        if (!good[b] || m.Slope[a] >= 0 || m.Slope[b] >= 0) continue;
                        int diff = m.Height[a] - m.Height[b];
                        int ad = Math.Abs(diff);
                        if (ad < 2 || ad > 6) continue;
                        int top = diff > 0 ? a : b;
                        int down = diff > 0 ? d : Opp(d);
                        if (down == 0) continue; // 不朝北下（错切视角下会被台地挡住）
                        cands.Add(new Cand { Top = top, Dir = down, Diff = ad, Key = Hash.Of((uint)top, rseed + (uint)down) });
                    }
                }
                cands.Sort((p, q) => p.Diff != q.Diff ? p.Diff.CompareTo(q.Diff) : p.Key.CompareTo(q.Key));
                bool placed = false;
                int fGeom = 0, fLost = 0;
                int goodCount = 0;
                for (int i = 0; i < N; i++) if (good[i]) goodCount++;
                foreach (var c in cands)
                {
                    if (!TryRamp(m, cls, c, s.RampWidth, out var undo) && !TryRampInner(m, cls, c, s.RampWidth, out undo)) { fGeom++; continue; }
                    var g2 = Good(m, cls, spawn);
                    bool lost = false;
                    int newCount = 0;
                    for (int i = 0; i < N; i++) { if (g2[i]) newCount++; if (good[i] && !g2[i]) { lost = true; break; } }
                    if (lost || newCount <= goodCount) { fLost++; Undo(m, undo); continue; }
                    placed = true; break;
                }
                if (placed) tried.Clear();
                if (!placed)
                {
                    Debug?.AppendLine($"round {round} zone cls={zoneCls[target]} size={sizes[target]} cands={cands.Count} geomFail={fGeom} lostOrNoGain={fLost}");
                    tried.Add(key);
                    // 连不上的小盆地 / 小坑：抬到只低 1 层（能跳出来）
                    if (zoneCls[target] == Basin || (zoneCls[target] == Ground && sizes[target] < 200))
                    {
                        bool changed = false;
                        for (int i = 0; i < N; i++)
                        {
                            if (zone[i] != target) continue;
                            int hi = int.MinValue;
                            ForN8(i, W, H, n => { if (zone[n] != target && Walkable(m, cls, n) && good[n]) hi = Math.Max(hi, m.Height[n]); });
                            if (hi != int.MinValue && m.Height[i] < hi - 1) { m.Height[i] = hi - 1; changed = true; }
                        }
                        if (changed) tried.Remove(key);
                    }
                    if (cands.Count == 0 && zoneCls[target] != Plateau && zoneCls[target] != Plateau2 && sizes[target] < 200)
                    {
                        // 四周没有可接的邻居：填平并入旁边的台地
                        bool changed = false;
                        for (int pass2 = 0; pass2 < 2; pass2++)
                            for (int i = 0; i < N; i++)
                            {
                                if (zone[i] != target) continue;
                                int hi = int.MinValue; byte hc = Plateau;
                                ForN8(i, W, H, n => { if (zone[n] != target && Walkable(m, cls, n) && m.Height[n] > hi) { hi = m.Height[n]; hc = cls[n]; } });
                                if (hi != int.MinValue && m.Height[i] < hi) { m.Height[i] = hi; changed = true; }
                                if (hi != int.MinValue) cls[i] = hc == Ground || hc == Basin ? Plateau : hc;
                            }
                        if (changed) tried.Remove(key);
                    }
                }
            }
            // 大台地补第二个坡道
            AddSecondRamps(m, cls, s, spawn);

            var gF = Good(m, cls, spawn);
            var zf = Zones(m, cls, out var szf);
            unreachable = 0; failedPlateaus = 0;
            var counted = new HashSet<int>();
            for (int i = 0; i < N; i++)
            {
                if (zf[i] < 0 || gF[i]) continue;
                bool isPlat = cls[i] == Plateau || cls[i] == Plateau2;
                if (isPlat)
                {
                    if (szf[zf[i]] >= s.MinRampedPlateau && counted.Add(zf[i])) failedPlateaus++;
                    continue;
                }
                if (szf[zf[i]] < 4) continue;
                unreachable++;
                m.Flags[i] |= CellFlags.Unreachable;
            }
        }

        static void AddSecondRamps(RegionMap m, byte[] cls, TerraceFieldSettings s, int spawn)
        {
            int W = m.W, N = W * m.H;
            var zone = Zones(m, cls, out var sizes);
            var good = Good(m, cls, spawn);
            uint rseed = Hash.Derive(s.Seed, "tf.ramp2");
            for (int zid = 0; zid < sizes.Count; zid++)
            {
                if (sizes[zid] < 1500) continue;
                int any = -1;
                for (int i = 0; i < N; i++) if (zone[i] == zid) { any = i; break; }
                if (cls[any] != Plateau && cls[any] != Plateau2) continue;
                var tops = new List<int>();
                foreach (var r in m.Ramps) { int t = r.TopZ * W + r.TopX; if (zone[t] == zid) tops.Add(t); }
                var cands = new List<Cand>();
                for (int a = 0; a < N; a++)
                {
                    if (zone[a] != zid || m.Slope[a] >= 0) continue;
                    int x = a % W, z = a / W;
                    bool far = true;
                    foreach (var t in tops) { int dx = t % W - x, dz = t / W - z; if (dx * dx + dz * dz < 45 * 45) { far = false; break; } }
                    if (!far) continue;
                    for (int d = 1; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (!m.InBounds(nx, nz)) continue;
                        int b = nz * W + nx;
                        if (!good[b] || zone[b] == zid || m.Slope[b] >= 0) continue;
                        int diff = m.Height[a] - m.Height[b];
                        if (diff < 2 || diff > 6) continue;
                        cands.Add(new Cand { Top = a, Dir = d, Diff = diff, Key = Hash.Of((uint)a, rseed + (uint)d) });
                    }
                }
                cands.Sort((p, q) => p.Diff != q.Diff ? p.Diff.CompareTo(q.Diff) : p.Key.CompareTo(q.Key));
                foreach (var c in cands)
                {
                    if (!TryRamp(m, cls, c, s.RampWidth, out var undo)) continue;
                    var g2 = Good(m, cls, spawn);
                    bool lost = false;
                    for (int i = 0; i < N; i++) if (good[i] && !g2[i]) { lost = true; break; }
                    if (lost) { Undo(m, undo); continue; }
                    good = g2;
                    break;
                }
            }
        }

        struct Saved { public int I, H; public sbyte S; public CellFlags F; }

        /// <summary>
        /// 在低的一侧铺坡道：从高格往外，斜坡、平台、斜坡、平台……（单格斜坡，不连续），宽 width 格。
        /// 高差 d 时占地 2d−1 格，外面还要 1 格同高平地落脚。
        /// </summary>
        static bool TryRamp(RegionMap m, byte[] cls, Cand c, int width, out List<Saved> undo)
        {
            undo = null;
            int W = m.W;
            int tx = c.Top % W, tz = c.Top / W;
            int T = m.Height[c.Top];
            int low = T - c.Diff;
            int px = Grid.DX4[(c.Dir + 1) & 3], pz = Grid.DZ4[(c.Dir + 1) & 3];
            int dx = Grid.DX4[c.Dir], dz = Grid.DZ4[c.Dir];
            int L = 2 * c.Diff - 1;
            for (int k = 0; k < width; k++)
            {
                int hx = tx + px * k, hz = tz + pz * k;
                if (!m.InBounds(hx, hz)) return false;
                int hi = hz * W + hx;
                if (m.Height[hi] != T || m.Slope[hi] >= 0 || cls[hi] == Border) return false;
                for (int j = 1; j <= L + 1; j++)
                {
                    int x = hx + dx * j, z = hz + dz * j;
                    if (!m.InBounds(x, z)) return false;
                    int i = z * W + x;
                    if (m.Height[i] != low || m.Slope[i] >= 0 || m.Water[i] != RegionMap.NoWater || cls[i] == Border) return false;
                    if ((m.Flags[i] & CellFlags.Ramp) != 0) return false;
                }
            }
            undo = new List<Saved>();
            for (int k = 0; k < width; k++)
            {
                int hx = tx + px * k, hz = tz + pz * k;
                for (int j = 1; j <= L; j++)
                {
                    int x = hx + dx * j, z = hz + dz * j, i = z * W + x;
                    undo.Add(new Saved { I = i, H = m.Height[i], S = m.Slope[i], F = m.Flags[i] });
                    int step = (j - 1) / 2;            // 第几级
                    m.Height[i] = T - 1 - step;
                    m.Slope[i] = (j % 2 == 1) ? (sbyte)Opp(c.Dir) : (sbyte)-1; // 奇数格是斜坡，偶数格是平台
                    m.Flags[i] |= CellFlags.Ramp;
                }
            }
            m.Ramps.Add(new RampInfo { TopX = tx, TopZ = tz, DownDir = c.Dir, Width = width, Length = L });
            return true;
        }

        /// <summary>备选：坡道挖进高的一侧（从崖边往里：斜坡、平台、斜坡……，最后接回台面）。</summary>
        static bool TryRampInner(RegionMap m, byte[] cls, Cand c, int width, out List<Saved> undo)
        {
            undo = null;
            int W = m.W;
            int T = m.Height[c.Top], low = T - c.Diff;
            int up = Opp(c.Dir);
            int dx = Grid.DX4[up], dz = Grid.DZ4[up];
            int px = Grid.DX4[(c.Dir + 1) & 3], pz = Grid.DZ4[(c.Dir + 1) & 3];
            int tx = c.Top % W, tz = c.Top / W;
            int L = 2 * c.Diff - 1;
            for (int k = 0; k < width; k++)
            {
                int ex = tx + px * k, ez = tz + pz * k;
                // 崖脚外侧一格必须是同高的低处
                int lx = ex - dx, lz = ez - dz;
                if (!m.InBounds(lx, lz)) return false;
                int li = lz * W + lx;
                if (m.Height[li] != low || m.Slope[li] >= 0 || m.Water[li] != RegionMap.NoWater || cls[li] == Border) return false;
                for (int j = 0; j <= L; j++)
                {
                    int x = ex + dx * j, z = ez + dz * j;
                    if (!m.InBounds(x, z)) return false;
                    int i = z * W + x;
                    if (m.Height[i] != T || m.Slope[i] >= 0 || cls[i] == Border || (m.Flags[i] & CellFlags.Ramp) != 0) return false;
                }
            }
            undo = new List<Saved>();
            for (int k = 0; k < width; k++)
            {
                int ex = tx + px * k, ez = tz + pz * k;
                for (int j = 0; j < L; j++)
                {
                    int x = ex + dx * j, z = ez + dz * j, i = z * W + x;
                    undo.Add(new Saved { I = i, H = m.Height[i], S = m.Slope[i], F = m.Flags[i] });
                    int step = (j + 1) / 2;
                    m.Height[i] = low + step;
                    m.Slope[i] = (j % 2 == 0) ? (sbyte)up : (sbyte)-1; // 偶数格是斜坡，奇数格是平台
                    m.Flags[i] |= CellFlags.Ramp;
                }
            }
            m.Ramps.Add(new RampInfo { TopX = tx + dx * L, TopZ = tz + dz * L, DownDir = c.Dir, Width = width, Length = L });
            return true;
        }

        static void Undo(RegionMap m, List<Saved> undo)
        {
            for (int k = undo.Count - 1; k >= 0; k--)
            {
                var u = undo[k];
                m.Height[u.I] = u.H; m.Slope[u.I] = u.S; m.Flags[u.I] = u.F;
            }
            m.Ramps.RemoveAt(m.Ramps.Count - 1);
        }
    }
}
