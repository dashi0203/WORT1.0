using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>LayoutGenerator 的结果：地形（RegionMap）+ 区域图 + 检查报告。</summary>
    public sealed class LayoutResult
    {
        public WorldLayout Layout;
        public RegionMap Map;
        /// <summary>每格所属区域（WorldLayout.Regions 的下标）。</summary>
        public int[] RegionId;
        /// <summary>手写出入口（坡道 / 台阶）及其周边。</summary>
        public bool[] GateZone;
        /// <summary>桥 / 隧道走廊。</summary>
        public bool[] SpanCorridor;
        /// <summary>桥面格所属的桥（Layout.Spans 下标），否则 -1。</summary>
        public int[] BridgeOf;
        public LayoutReport Report;

        public LayoutRegion RegionAt(int i) => Layout.Regions[RegionId[i]];
    }

    /// <summary>
    /// layout 管线（Docs/WorldGen.md 第 4 节）。自然地形（宏观走势、台地群、河、山前悬崖带、坡道与连通性）
    /// 沿用 RegionGenerator；layout 负责它管不了的部分：
    /// Biome 图、海、峡谷、盆地、台地区、关键地点、桥和隧道，以及可选的手写出入口和道路。
    /// 峡谷、盆地需要的坡道默认由 RegionGenerator 的连通性修复自动放。
    /// </summary>
    public static class LayoutGenerator
    {
        public static LayoutResult Generate(WorldLayout L)
        {
            var s = new RegionSettings { Seed = L.Seed, ChunksX = L.ChunksX, ChunksZ = L.ChunksZ, MinY = -8 };
            int W = s.Width, H = s.Depth, N = W * H;
            var res = new LayoutResult { Layout = L };

            // 1. 区域图（有机形状，1m 栅格化）
            res.RegionId = RasterizeRegions(L, s, W, H, out var baseBiome);
            var reg = res.RegionId;

            // 2. Biome 图
            var biome = new byte[N];
            var sea = new bool[N];
            for (int i = 0; i < N; i++)
            {
                var r = L.Regions[reg[i]];
                sea[i] = r.Kind == RegionKind.Sea;
                biome[i] = sea[i] ? (byte)BiomeId.Coast : r.InheritBiome && !r.IsOpen ? baseBiome[i] : (byte)r.Biome;
            }
            // 山前大悬崖带只按 layout 的山地大区算；然后在大区里撒不规则的小斑块
            var mountainZone = new bool[N];
            for (int i = 0; i < N; i++) mountainZone[i] = biome[i] == (byte)BiomeId.Mountain;
            ApplyBiomePatches(L, s, reg, biome, W, H);

            // 3. 保留区：结构区域外扩 4m、桥和隧道、手写出入口
            var reserved = new bool[N];
            for (int i = 0; i < N; i++) reserved[i] = !L.Regions[reg[i]].IsOpen;
            reserved = Grid.Dilate(reserved, W, H, 4);
            res.GateZone = new bool[N];
            res.SpanCorridor = new bool[N];
            res.BridgeOf = new int[N];
            for (int i = 0; i < N; i++) res.BridgeOf[i] = -1;
            MarkProtected(L, W, H, reserved, res.SpanCorridor);

            // 河从山里往南流：只在南半部的海结束（东海岸一直到北边，不让河横着流进东北角）
            var riverEnd = new bool[N];
            for (int i = 0; i < N; i++) riverEnd[i] = sea[i] && i / W < H / 2;

            var home = L.Home != null ? L.Site(L.Home) : null;
            var hooks = new TerrainHooks
            {
                Biome = biome,
                Reserved = reserved,
                RiverEnd = riverEnd,
                SpawnX = home != null ? home.X : -1,
                SpawnZ = home != null ? home.Z : -1,
                SkipRiver = L.Regions.Exists(r => r.River), // 河由峡谷给出
                NoSlopes = true,                             // 斜坡由策划手动放；坡道是台阶
                BiomeBlurRadius = 13,                        // Biome 过渡带约 32m
                MountainMask = mountainZone,
                MacroBase = LayoutMacroBase(),
                Organic = true,                              // 台地等不做块量化、不出直角
                Stamp = map =>
                {
                    res.Map = map;
                    StampStructures(L, map, reg);
                    StampSeaCliffs(map, reg, sea, L);
                    StampSites(L, map, reg);
                    StampRoads(L, map, reg);
                    map.Stats["gates"] = BuildGates(L, map, res.GateZone, reg);
                    BuildSpans(L, map, res);
                    FloodSea(map, sea);
                },
            };

            // 4. 自然地形 + 印章 + 坡道与连通性
            res.Map = RegionGenerator.Generate(s, hooks);

            // 5. 自然地形里的小陷坑（跳得进、爬不出，< 64 格）填到周围最常见的高度
            res.Map.Stats["trapPitsFilled"] = FillTrapPits(res);

            // 6. 检查
            res.Report = LayoutChecks.Run(res);
            return res;
        }

        static int FillTrapPits(LayoutResult res)
        {
            var map = res.Map;
            var L = res.Layout;
            int W = map.W, H = map.H, filled = 0;
            var home = L.Home != null ? L.Site(L.Home) : null;
            if (home == null) return 0;
            for (int round = 0; round < 4; round++)
            {
                var g = new SurfaceGraph(res);
                int start = home.Z * W + home.X;
                var fwd = g.Forward(start);
                var back = g.Backward(start);
                var lab = Grid.Label(W, H, i => g.Valid(i) && fwd[i] && !back[i] && !map.HasSpan(i), (a, b) => true, out var sizes);
                int changed = 0;
                var cells = new List<int>[sizes.Count];
                for (int i = 0; i < lab.Length; i++) if (lab[i] >= 0) (cells[lab[i]] ??= new List<int>()).Add(i);
                for (int c = 0; c < sizes.Count; c++)
                {
                    if (sizes[c] >= 64) continue;
                    bool natural = true;
                    foreach (int i in cells[c])
                        if (!res.RegionAt(i).IsOpen || map.Has(i, CellFlags.Site) || map.HasWater(i)) { natural = false; break; }
                    if (!natural) continue;
                    var counts = new Dictionary<int, int>();
                    foreach (int i in cells[c])
                    {
                        int x = i % W, z = i / W;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                            if (!map.InBounds(nx, nz)) continue;
                            int j = nz * W + nx;
                            if (lab[j] == c || !back[j]) continue; // 只参考能回去的邻居
                            int hj = map.HasWater(j) ? map.Water[j] : map.Height[j];
                            counts.TryGetValue(hj, out int k);
                            counts[hj] = k + 1;
                        }
                    }
                    int best = int.MinValue, bestN = -1;
                    foreach (var kv in counts) if (kv.Value > bestN || kv.Value == bestN && kv.Key < best) { best = kv.Key; bestN = kv.Value; }
                    if (bestN < 0) continue;
                    foreach (int i in cells[c]) { map.Height[i] = best; map.Slope[i] = -1; }
                    filled++; changed++;
                }
                if (changed == 0) break;
            }
            return filled;
        }

        // ---------------- 1. 区域图 ----------------

        static int[] RasterizeRegions(WorldLayout L, RegionSettings s, int W, int H, out byte[] baseBiome)
        {
            var reg = new int[W * H];
            baseBiome = new byte[W * H];
            for (int i = 0; i < reg.Length; i++) reg[i] = -1;
            int maxDepth = 0;
            foreach (var r in L.Regions) maxDepth = Math.Max(maxDepth, r.Depth);
            for (int depth = 0; depth <= maxDepth; depth++)
                foreach (var r in L.Regions)
                {
                    if (r.Depth != depth) continue;
                    var sh = r.Shape;
                    int parent = r.Parent != null ? L.Region(r.Parent).Index : -1;
                    sh.Init(Hash.Derive(s.Seed, "layout.shape." + r.Id));
                    int x0 = Math.Max(0, (int)Math.Floor(sh.MinX)), x1 = Math.Min(W - 1, (int)Math.Ceiling(sh.MaxX));
                    int z0 = Math.Max(0, (int)Math.Floor(sh.MinZ)), z1 = Math.Min(H - 1, (int)Math.Ceiling(sh.MaxZ));
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int i = z * W + x;
                            if (parent >= 0 && reg[i] != parent) continue;
                            if (sh.Organic(x + 0.5, z + 0.5) >= 0) continue;
                            reg[i] = r.Index;
                            if (r.IsOpen) baseBiome[i] = (byte)r.Biome;
                        }
                }
            for (int i = 0; i < reg.Length; i++)
                if (reg[i] < 0) throw new FormatException("有格子不属于任何区域：layout 需要一个 fill 形状的底层区域");
            RemoveSlivers(L, reg, W, H);
            return reg;
        }

        /// <summary>
        /// 形状扭曲留下的碎块并入周围最多的区域：
        /// 任何区域小于 MinPiece 格的碎块（避免被崖圈死的小洞）；结构区域除最大一块以外的所有块（避免孤岛）。
        /// 开放区可以合法地分成几块，只去掉碎块。
        /// </summary>
        const int MinPiece = 48;

        static void RemoveSlivers(WorldLayout L, int[] reg, int W, int H)
        {
            var lab = Grid.Label(W, H, i => true, (a, b) => reg[a] == reg[b], out var sizes);
            var largest = new int[L.Regions.Count];
            var largestSize = new int[L.Regions.Count];
            for (int k = 0; k < largest.Length; k++) largest[k] = -1;
            for (int i = 0; i < reg.Length; i++)
            {
                int c = lab[i];
                if (sizes[c] > largestSize[reg[i]] || sizes[c] == largestSize[reg[i]] && c < largest[reg[i]]) { largestSize[reg[i]] = sizes[c]; largest[reg[i]] = c; }
            }
            var counts = new Dictionary<int, int>();
            var members = new List<int>[sizes.Count];
            for (int i = 0; i < reg.Length; i++)
            {
                int c = lab[i];
                bool drop = sizes[c] < MinPiece || !L.Regions[reg[i]].IsOpen && c != largest[reg[i]];
                if (drop) (members[c] ??= new List<int>()).Add(i);
            }
            for (int c = 0; c < members.Length; c++)
            {
                var cells = members[c];
                if (cells == null) continue;
                counts.Clear();
                int self = reg[cells[0]];
                foreach (int i in cells)
                {
                    int x = i % W, z = i / W;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int r = reg[nz * W + nx];
                        if (r == self) continue;
                        counts.TryGetValue(r, out int n);
                        counts[r] = n + 1;
                    }
                }
                int best = -1, bestN = -1;
                foreach (var kv in counts)
                    if (kv.Value > bestN || kv.Value == bestN && kv.Key < best) { best = kv.Key; bestN = kv.Value; }
                if (best >= 0) foreach (int i in cells) reg[i] = best;
            }
        }

        // ---------------- 2b. Biome 小斑块 ----------------

        /// <summary>
        /// 大区由 layout 画，大区里按规则撒不规则的小斑块（Docs/WorldGen.md）：
        /// 森林往北针叶林越来越多、针叶林带里夹混交林；人类区夹林地；山地外缘夹针叶林；
        /// 森林深处偶有小山包、小块 moorland；低处和大河两岸有迷你湿地；大湿地里有林地小岛。
        /// 斑块形状用两层坐标扭曲的噪声，去掉窄于 10m 的部分和小于约 25m 见方的碎块。只改开放区。
        /// </summary>
        static void ApplyBiomePatches(WorldLayout L, RegionSettings s, int[] reg, byte[] biome, int W, int H)
        {
            int N = W * H;
            var baseB = (byte[])biome.Clone();
            var open = new bool[N];
            for (int i = 0; i < N; i++) open[i] = L.Regions[reg[i]].IsOpen && L.Regions[reg[i]].Kind != RegionKind.Sea;

            // 到有河峡谷的距离、到山地大区外的距离
            var notRiver = new bool[N];
            var mountain = new bool[N];
            for (int i = 0; i < N; i++) { notRiver[i] = !L.Regions[reg[i]].River; mountain[i] = baseB[i] == (byte)BiomeId.Mountain; }
            var dRiver = Grid.DistanceInside(notRiver, W, H);
            var dMountainEdge = Grid.DistanceInside(mountain, W, H);

            byte F = (byte)BiomeId.MixedForest, C = (byte)BiomeId.Conifer, Hm = (byte)BiomeId.Human,
                 M = (byte)BiomeId.Mountain, Mo = (byte)BiomeId.Moor, Wt = (byte)BiomeId.Swamp;
            double Zn(int i) => (i / W) / (double)(H - 1);

            void Rule(string name, double wavelength, byte to, Func<int, double> coverage)
            {
                var mask = PatchMask(s, name, wavelength, W, H, i => open[i] ? coverage(i) : 0);
                for (int i = 0; i < N; i++) if (mask[i]) biome[i] = to;
            }

            Rule("patch.conifer", 70, C, i => baseB[i] == F ? 0.5 * Clamp01((Zn(i) - 0.3) / 0.45) : 0);
            Rule("patch.mixed", 70, F, i => baseB[i] == C ? 0.35 * Clamp01((0.85 - Zn(i)) / 0.4) : 0);
            Rule("patch.humanWood", 50, F, i => baseB[i] == Hm ? 0.2 : 0);
            Rule("patch.mountainWood", 50, C, i => baseB[i] == M && dMountainEdge[i] < 70 ? 0.3 : 0);
            Rule("patch.moor", 45, Mo, i => (baseB[i] == F || baseB[i] == C) && Zn(i) > 0.35 ? 0.06 : 0);
            Rule("patch.hill", 45, M, i => (baseB[i] == F || baseB[i] == C) && Zn(i) > 0.4 ? 0.045 : 0);
            Rule("patch.wetIsland", 35, F, i => baseB[i] == Wt ? 0.1 : 0);
            Rule("patch.miniWetland", 30, Wt, i => baseB[i] == F || baseB[i] == C || baseB[i] == Hm ? 0.05 : 0);

            // 河漫滩湿地：只在下游变浅的河段（深度 ≤ 约 3 层），沿河一侧，每隔约 100m 换边，带宽 15–45m，边缘不规则
            foreach (var r in L.Regions)
            {
                if (!r.River || r.Shape.Type != "stroke") continue;
                var sh = r.Shape;
                var sideN = new Fbm(Hash.Derive(s.Seed, "floodplain.side." + r.Id), 1, 1);
                var bandN = new Fbm(Hash.Derive(s.Seed, "floodplain.band." + r.Id), 40, 2);
                var edgeN = new Fbm(Hash.Derive(s.Seed, "floodplain.edge." + r.Id), 14, 2);
                var m = new bool[N];
                for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = z * W + x;
                        if (!open[i] || dRiver[i] > 60) continue;
                        double gx = x + 0.5, gz = z + 0.5;
                        sh.StrokeInfo(gx, gz, out double t, out _, out double dist, out int side, out double len);
                        if (-r.Level * Taper(t) > 3.2 || t > 0.97) continue;
                        // 每 100m 一段，按段号的哈希决定这段在哪一侧
                        int seg = (int)(t * len / 100);
                        int want = (Hash.Of((uint)seg, Hash.Derive(s.Seed, "floodplain." + r.Id)) & 1) == 0 ? 1 : -1;
                        if (side != want) continue;
                        double band = 30 + 15 * bandN.Sample(gx, gz) * 2;
                        double over = dist - sh.HalfWidthAt(gx, gz);
                        if (over < 0 || over > band + 6 * edgeN.Sample(gx, gz)) continue;
                        m[i] = true;
                    }
                m = Grid.OpenRound(m, W, H, 4);
                m = Grid.RemoveSmall(m, W, H, 300);
                for (int i = 0; i < N; i++) if (m[i]) biome[i] = Wt;
            }
        }

        /// <summary>
        /// layout 模式的宏观高度：湿地只比森林低 1 层（BiomeTable 里是 0，比混交林低 4 层，
        /// 32m 的过渡带会把它变成一圈圈等高线的“火山口”）。低处由池塘和迷你湿地提供。
        /// </summary>
        static double[] LayoutMacroBase()
        {
            var m = new double[BiomeTable.Count];
            for (int b = 0; b < m.Length; b++) m[b] = BiomeTable.All[b].MacroBase;
            m[(int)BiomeId.Swamp] = BiomeTable.Get(BiomeId.MixedForest).MacroBase - 1;
            return m;
        }

        static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        static bool[] PatchMask(RegionSettings s, string name, double wl, int W, int H, Func<int, double> coverage)
        {
            int N = W * H;
            var f = new Fbm(Hash.Derive(s.Seed, name), wl, 3);
            var w1x = new Fbm(Hash.Derive(s.Seed, name + ".w1x"), wl * 2, 2);
            var w1z = new Fbm(Hash.Derive(s.Seed, name + ".w1z"), wl * 2, 2);
            var w2x = new Fbm(Hash.Derive(s.Seed, name + ".w2x"), wl * 0.5, 2);
            var w2z = new Fbm(Hash.Derive(s.Seed, name + ".w2z"), wl * 0.5, 2);
            Func<double, double, double> field = (x, z) =>
            {
                double qx = x + wl * 0.6 * w1x.Sample(x, z), qz = z + wl * 0.6 * w1z.Sample(x, z);
                double rx = qx + wl * 0.25 * w2x.Sample(qx, qz), rz = qz + wl * 0.25 * w2z.Sample(qx, qz);
                return f.Sample(rx, rz);
            };
            var q = new QuantileTable(field);
            var m = new bool[N];
            bool any = false;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    double c = coverage(i);
                    if (c <= 0) continue;
                    any = true;
                    if (field(s.OriginX + x + 0.5, s.OriginZ + z + 0.5) > q.Quantile(1 - Math.Min(0.95, c))) m[i] = true;
                }
            if (!any) return m;
            m = Grid.OpenRound(m, W, H, 5);
            return Grid.RemoveSmall(m, W, H, 600);
        }

        // ---------------- 3. 保留区 ----------------

        static void MarkProtected(WorldLayout L, int W, int H, bool[] reserved, bool[] spanCorridor)
        {
            // 地点和道路不保留：保留区里没有台地，会在台地群中间留下一个坑。地点由印章压平并把外圈收成台阶。
            foreach (var g in L.Gates)
            {
                double ex = g.X + 0.5 + Grid.DX4[g.Up] * 20, ez = g.Z + 0.5 + Grid.DZ4[g.Up] * 20;
                StampPolyline(new[] { g.X + 0.5, ex }, new[] { g.Z + 0.5, ez }, g.Width * 0.5 + 2, W, H, i => reserved[i] = true);
            }
            foreach (var sp in L.Spans)
            {
                double dx = Math.Sign(sp.X1 - sp.X0) * 3, dz = Math.Sign(sp.Z1 - sp.Z0) * 3;
                StampPolyline(new[] { sp.X0 + 0.5 - dx, sp.X1 + 0.5 + dx }, new[] { sp.Z0 + 0.5 - dz, sp.Z1 + 0.5 + dz },
                    sp.Width * 0.5 + 2, W, H, i => { reserved[i] = true; spanCorridor[i] = true; });
            }
        }

        static void StampPolyline(double[] px, double[] pz, double radius, int W, int H, Action<int> f)
        {
            for (int k = 0; k + 1 < px.Length; k++)
            {
                double ax = px[k], az = pz[k], bx = px[k + 1], bz = pz[k + 1];
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - radius)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, bx) + radius));
                int z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, bz) - radius)), z1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(az, bz) + radius));
                double vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double cx = x + 0.5, cz = z + 0.5;
                        double t = len2 > 0 ? ((cx - ax) * vx + (cz - az) * vz) / len2 : 0;
                        if (t < 0) t = 0; else if (t > 1) t = 1;
                        double qx = ax + vx * t - cx, qz = az + vz * t - cz;
                        if (qx * qx + qz * qz <= radius * radius) f(z * W + x);
                    }
            }
        }

        static void Ellipse(double cx, double cz, double rx, double rz, int W, int H, Action<int> f)
        {
            int x0 = Math.Max(0, (int)Math.Floor(cx - rx)), x1 = Math.Min(W - 1, (int)Math.Ceiling(cx + rx));
            int z0 = Math.Max(0, (int)Math.Floor(cz - rz)), z1 = Math.Min(H - 1, (int)Math.Ceiling(cz + rz));
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    double dx = (x + 0.5 - cx) / rx, dz = (z + 0.5 - cz) / rz;
                    if (dx * dx + dz * dz <= 1) f(z * W + x);
                }
        }

        // ---------------- 印章：海、峡谷、盆地、台地区 ----------------

        static void ClearWater(RegionMap map, int i)
        {
            map.Water[i] = RegionMap.NoWater;
            map.Flags[i] &= ~(CellFlags.Pond | CellFlags.River);
        }

        /// <summary>
        /// 结构区域按“相对周围地形”的层级压进去：
        /// 峡谷 = 周围地面平滑值 + level（负），谷底沿程每格最多变 1 层；盆地 = 外圈最低 + level，整块平；
        /// 台地区 = 外圈最高 + level；海 = 0 层水面，海底 −3 到 −6。边界保证至少 2 层的崖。
        /// </summary>
        static void StampStructures(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H, N = W * H;
            var h = map.Height;
            var surf = new float[N];
            for (int i = 0; i < N; i++) surf[i] = h[i];
            surf = Grid.BoxBlur(Grid.BoxBlur(surf, W, H, 10), W, H, 10);

            foreach (var r in L.Regions)
            {
                if (r.IsOpen) continue;
                var cells = new List<int>();
                for (int i = 0; i < N; i++) if (reg[i] == r.Index) cells.Add(i);
                if (cells.Count == 0) continue;
                var inR = new bool[N];
                foreach (int i in cells) inR[i] = true;

                int ringMin = int.MaxValue, ringMax = int.MinValue;
                foreach (int i in cells)
                {
                    int x = i % W, z = i / W;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int j = nz * W + nx;
                        if (inR[j] || !L.Regions[reg[j]].IsOpen) continue; // 只看开放区：盆地可以融进旁边的峡谷
                        ringMin = Math.Min(ringMin, h[j]);
                        ringMax = Math.Max(ringMax, h[j]);
                    }
                }
                if (ringMin == int.MaxValue) { ringMin = ringMax = 0; }

                switch (r.Kind)
                {
                    case RegionKind.Sea:
                    {
                        var d = Grid.DistanceInside(inR, W, H);
                        foreach (int i in cells)
                        {
                            h[i] = -3 - Math.Min(3, (int)(d[i] / 10));
                            map.Water[i] = 0;
                            map.Slope[i] = -1; map.Tier[i] = 0;
                            map.Flags[i] &= ~(CellFlags.Pond | CellFlags.River);
                        }
                        break;
                    }
                    case RegionKind.Canyon:
                        StampCanyon(r, map, cells, surf);
                        break;
                    case RegionKind.Basin:
                    {
                        int floor = ringMin + r.Level;
                        foreach (int i in cells) { h[i] = floor; map.Slope[i] = -1; map.Tier[i] = 0; ClearWater(map, i); }
                        RimAndSmooth(map, cells, inR, true, L, reg);
                        break;
                    }
                    default: // Plateau / Highland：比外圈最高处再高 level
                    {
                        int top = ringMax + Math.Max(2, r.Level);
                        foreach (int i in cells) { h[i] = top; map.Slope[i] = -1; ClearWater(map, i); }
                        RimAndSmooth(map, cells, inR, false, L, reg);
                        break;
                    }
                }
                foreach (int i in cells) if (h[i] < map.Settings.MinY) h[i] = map.Settings.MinY;
            }
        }

        /// <summary>
        /// 下沉区域（峡谷、盆地）：与外面相邻的格至少比外面低 2 层（崖），内部相邻格高差 ≤ 1（谷底可沿程走）。
        /// 抬升区域反之。
        /// </summary>
        static void RimAndSmooth(RegionMap map, List<int> cells, bool[] inR, bool lower, WorldLayout L, int[] reg)
        {
            int W = map.W, H = map.H;
            var h = map.Height;
            for (int pass = 0; pass < 64; pass++)
            {
                bool changed = false;
                foreach (int i in cells)
                {
                    int x = i % W, z = i / W;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int j = nz * W + nx;
                        if (!inR[j] && !L.Regions[reg[j]].IsOpen) continue; // 挨着别的结构区域（如峡谷）不加崖
                        if (lower)
                        {
                            int lim = inR[j] ? h[j] + 1 : h[j] - 2;
                            if (h[i] > lim) { h[i] = lim; changed = true; }
                        }
                        else
                        {
                            int lim = inR[j] ? h[j] - 1 : h[j] + 2;
                            if (h[i] < lim) { h[i] = lim; changed = true; }
                        }
                    }
                }
                if (!changed) break;
            }
        }

        /// <summary>
        /// 峡谷：谷底沿线条从北（第一个控制点）往南单调下降，每米最多降 1 层，随周围地形一起北高南低；
        /// 深度 = |level| × 系数：北端切进山里时 0.6 → 1，中段 1，后 45% 逐渐变浅到 0.15，南端缓缓融进地形。
        /// 有河时谷底中间是河道：深段（设计深度 ≥ 3.5 层）水深 2 层过不去，变浅后水深 1 层可以涉水。
        /// </summary>
        static void StampCanyon(LayoutRegion r, RegionMap map, List<int> cells, float[] surf)
        {
            int W = map.W;
            var h = map.Height;
            var sh = r.Shape;
            double total = 0;
            for (int k = 0; k + 1 < sh.PX.Length; k++)
            {
                double vx = sh.PX[k + 1] - sh.PX[k], vz = sh.PZ[k + 1] - sh.PZ[k];
                total += Math.Sqrt(vx * vx + vz * vz);
            }
            int B = Math.Max(2, (int)total);
            var tArr = new double[cells.Count];
            var aArr = new double[cells.Count];
            var sum = new double[B];
            var cnt = new int[B];
            for (int c = 0; c < cells.Count; c++)
            {
                int i = cells[c];
                sh.StrokeInfo(i % W + 0.5, i / W + 0.5, out tArr[c], out aArr[c]);
                int b = (int)(tArr[c] * (B - 1) + 0.5);
                sum[b] += surf[i]; cnt[b]++;
            }
            // 每段的平均地面 → 谷底目标，空段沿用前一段
            var target = new double[B];
            double last = double.NaN;
            for (int b = 0; b < B; b++)
            {
                double t = b / (double)(B - 1);
                double mean = cnt[b] > 0 ? sum[b] / cnt[b] : last;
                if (double.IsNaN(mean)) mean = 0;
                last = mean;
                target[b] = mean + r.Level * Taper(t);
            }
            for (int b = 0; b < B; b++) if (cnt[b] == 0 && b > 0) target[b] = target[b - 1];
            // 从北往南单调不升；再从南往北，相邻段最多差 1 层（谷底可以沿程走）
            for (int b = 1; b < B; b++) target[b] = Math.Min(target[b], target[b - 1]);
            for (int b = B - 2; b >= 0; b--) target[b] = Math.Min(target[b], target[b + 1] + 1);
            // 谷底高于海面至少 1 层，只有最后 5%（入海口）降到 0 层，不让海水倒灌整条峡谷
            for (int b = 0; b < B; b++) target[b] = Math.Max(target[b], b / (double)(B - 1) > 0.95 ? 0 : 1);
            int minFloor = map.Settings.MinY + 3;
            int river = 0;
            for (int c = 0; c < cells.Count; c++)
            {
                int i = cells[c];
                int b = (int)(tArr[c] * (B - 1) + 0.5);
                int floor = Math.Max(minFloor, (int)Math.Floor(target[b]));
                map.Slope[i] = -1; map.Tier[i] = 0;
                if (r.River && aArr[c] < r.RiverWidth)
                {
                    double depth = -r.Level * Taper(tArr[c]);
                    int dr = r.RiverDeep && depth >= 3.5 ? 2 : 1;
                    h[i] = floor - dr;
                    map.Water[i] = floor;
                    map.Flags[i] = (map.Flags[i] & ~CellFlags.Pond) | CellFlags.River;
                    river++;
                }
                else
                {
                    h[i] = floor;
                    ClearWater(map, i);
                }
            }
            map.Stats["canyonRiver." + r.Id] = river;
        }

        static double Taper(double t)
        {
            if (t < 0.08) return 0.6 + 0.4 * t / 0.08;
            if (t <= 0.55) return 1;
            return 1 - 0.85 * (t - 0.55) / 0.45;
        }

        /// <summary>
        /// 海崖（GDD 6.1：海岸 30–50% 是海崖）：按低频噪声挑约 40% 的海岸线，离海 12m 以内抬到 4–6 层，
        /// 往内陆每 2.5m 降 1 层（台阶可走），临海一侧直接落到海里。
        /// </summary>
        static void StampSeaCliffs(RegionMap map, int[] reg, bool[] sea, WorldLayout L)
        {
            var regionOpen = new bool[L.Regions.Count];
            for (int k = 0; k < regionOpen.Length; k++) regionOpen[k] = L.Regions[k].IsOpen;
            int W = map.W, H = map.H, N = W * H;
            var land = new bool[N];
            bool any = false;
            for (int i = 0; i < N; i++) { land[i] = !sea[i]; any |= sea[i]; }
            if (!any) return;
            var dSea = Grid.DistanceInside(land, W, H);
            var s = map.Settings;
            var pick = new Fbm(Hash.Derive(s.Seed, "coast.cliff"), 70, 2);
            var hgt = new Fbm(Hash.Derive(s.Seed, "coast.cliffH"), 40, 2);
            var q = new QuantileTable((x, z) => pick.Sample(x, z));
            double thr = q.Quantile(0.6);
            int cells = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (sea[i] || dSea[i] > 12 || map.HasWater(i) || map.Has(i, CellFlags.Site)) continue;
                    if (reg[i] >= 0 && !regionOpen[reg[i]]) continue; // 峡谷入海口不起海崖
                    double gx = s.OriginX + x + 0.5, gz = s.OriginZ + z + 0.5;
                    if (pick.Sample(gx, gz) <= thr) continue;
                    int top = 4 + (int)Math.Floor(Math.Max(0, Math.Min(0.999, hgt.Sample(gx, gz) + 0.5)) * 3); // 4–6
                    int target = top - (int)(Math.Max(0, dSea[i] - 4) / 2.5);
                    if (target > map.Height[i]) { map.Height[i] = target; map.Slope[i] = -1; map.Tier[i] = 0; cells++; }
                }
            map.Stats["seaCliffCells"] = cells;
        }

        /// <summary>和海相连、低于 0 层的地面灌成海水（峡谷入海口等）。</summary>
        static void FloodSea(RegionMap map, bool[] sea)
        {
            int W = map.W, H = map.H;
            var q = new Queue<int>();
            var seen = new bool[W * H];
            for (int i = 0; i < sea.Length; i++) if (sea[i]) { seen[i] = true; q.Enqueue(i); }
            int flooded = 0;
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                    int j = nz * W + nx;
                    if (seen[j] || map.Height[j] >= 0) continue;
                    seen[j] = true;
                    map.Water[j] = 0;
                    map.Slope[j] = -1;
                    map.Flags[j] &= ~CellFlags.Pond;
                    flooded++;
                    q.Enqueue(j);
                }
            }
            map.Stats["seaFlooded"] = flooded;
        }

        // ---------------- 地点、道路 ----------------

        static void StampSites(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H;
            var s = map.Settings;
            foreach (var site in L.Sites)
            {
                var t = site.T;
                int c = site.Z * W + site.X;
                var warp = new Fbm(Hash.Derive(s.Seed, "layout.site." + site.Id), Math.Max(8, Math.Min(t.RX, t.RZ)), 2);
                double amp = Math.Max(0.5, Math.Min(4, Math.Min(t.RX, t.RZ) * 0.15));
                int x0 = Math.Max(0, (int)(site.X - t.RX - amp - 1)), x1 = Math.Min(W - 1, (int)(site.X + t.RX + amp + 1));
                int z0 = Math.Max(0, (int)(site.Z - t.RZ - amp - 1)), z1 = Math.Min(H - 1, (int)(site.Z + t.RZ + amp + 1));
                // 地点高度 = 占地内最常见的地面高度（不看水面，避免中心恰好是个小池塘）
                var hist = new Dictionary<int, int>();
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = z * W + x;
                        if (reg[i] != reg[c] || map.HasWater(i)) continue;
                        double ex = (x - site.X) / t.RX, ez = (z - site.Z) / t.RZ;
                        if (ex * ex + ez * ez > 1) continue;
                        hist.TryGetValue(map.Height[i], out int k);
                        hist[map.Height[i]] = k + 1;
                    }
                int level = map.HasWater(c) ? map.Water[c] : map.Height[c], levelN = -1;
                foreach (var kv in hist) if (kv.Value > levelN || kv.Value == levelN && kv.Key < level) { level = kv.Key; levelN = kv.Value; }
                // 外面一圈（buffer + 6m）往地点高度收：离占地边缘每多 2m 允许多差 1 层，保证能走上走下
                int ring = (int)Math.Ceiling(t.Buffer) + 6;
                for (int z = Math.Max(0, z0 - ring); z <= Math.Min(H - 1, z1 + ring); z++)
                    for (int x = Math.Max(0, x0 - ring); x <= Math.Min(W - 1, x1 + ring); x++)
                    {
                        int i = z * W + x;
                        if (!L.Regions[reg[i]].IsOpen || map.HasWater(i)) continue;
                        double dx = (x - site.X) / t.RX, dz = (z - site.Z) / t.RZ;
                        double d = (Math.Sqrt(dx * dx + dz * dz) - 1) * Math.Min(t.RX, t.RZ) + amp * warp.Sample(x + 0.5, z + 0.5);
                        if (d < 0 || d > ring) continue;
                        int allow = 1 + (int)(d / 2);
                        int hv = map.Height[i];
                        int nh = Math.Max(level - allow, Math.Min(level + allow, hv));
                        if (nh != hv) { map.Height[i] = nh; map.Slope[i] = -1; }
                    }
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = z * W + x;
                        if (reg[i] != reg[c]) continue;
                        double dx = (x - site.X) / t.RX, dz = (z - site.Z) / t.RZ;
                        double d = (Math.Sqrt(dx * dx + dz * dz) - 1) * Math.Min(t.RX, t.RZ) + amp * warp.Sample(x + 0.5, z + 0.5);
                        if (d >= 0) continue;
                        map.Flags[i] |= CellFlags.Site;
                        map.Height[i] = level;
                        map.Slope[i] = -1;
                        ClearWater(map, i);
                    }
            }
        }

        static void StampRoads(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H;
            foreach (var r in L.Roads)
            {
                // 道路只铺在它的控制点所在的区域里：过桥的路不会铺到桥下的峡谷底
                var allowed = new HashSet<int>();
                for (int k = 0; k < r.PX.Length; k++)
                {
                    int x = Grid.Clamp((int)r.PX[k], 0, W - 1), z = Grid.Clamp((int)r.PZ[k], 0, H - 1);
                    allowed.Add(reg[z * W + x]);
                }
                StampPolyline(r.PX, r.PZ, r.Width * 0.5, W, H, i =>
                {
                    if (!allowed.Contains(reg[i])) return;
                    map.Flags[i] |= CellFlags.Road;
                    ClearWater(map, i);
                });
            }
        }

        // ---------------- 手写出入口（可选；默认由连通性修复自动放坡道） ----------------

        static int BuildGates(WorldLayout L, RegionMap map, bool[] gateZone, int[] reg)
        {
            int W = map.W, H = map.H, built = 0;
            var h = map.Height;
            foreach (var g in L.Gates)
            {
                int dx = Grid.DX4[g.Up], dz = Grid.DZ4[g.Up];
                int px = Grid.DX4[(g.Up + 1) & 3], pz = Grid.DZ4[(g.Up + 1) & 3];
                int toRegion = g.To != null ? (L.Region(g.To) ?? throw new FormatException($"出入口 {g.Id} 的 to 区域 {g.To} 不存在")).Index : -1;
                // 在 at 前后 60 格内找离 at 最近的一道 ≥ 2 层的上坡崖
                int cx = g.X, cz = g.Z, top = int.MinValue, bestDist = int.MaxValue;
                for (int t = -60; t < 60; t++)
                {
                    int ax = g.X + dx * t, az = g.Z + dz * t, bx = ax + dx, bz = az + dz;
                    if (!map.InBounds(ax, az) || !map.InBounds(bx, bz)) continue;
                    if (toRegion >= 0 && reg[bz * W + bx] != toRegion) continue;
                    int diff = h[bz * W + bx] - h[az * W + ax];
                    int dist = t < 0 ? -t : t;
                    if (diff >= 2 && dist < bestDist) { bestDist = dist; cx = ax; cz = az; top = h[bz * W + bx]; }
                }
                if (top == int.MinValue) { map.Stats["gateNotFound." + g.Id] = 1; continue; }
                built++;
                int half = g.Width / 2;
                for (int o = -half; o <= g.Width - 1 - half; o++)
                {
                    for (int j = 0; j < 80; j++)
                    {
                        int x = cx - dx * j + px * o, z = cz - dz * j + pz * o;
                        if (x < 0 || z < 0 || x >= W || z >= H) break;
                        int i = z * W + x;
                        int target = top - 1 - j;
                        int ground = h[i];
                        if (target < ground) break;
                        h[i] = target;
                        ClearWater(map, i);
                        map.Flags[i] |= CellFlags.Gate | CellFlags.Ramp;
                        map.Slope[i] = -1; // 不用斜坡：手写出入口也是台阶
                        if (target == ground) break;
                    }
                }
                map.Ramps.Add(new RampInfo { TopX = cx + dx, TopZ = cz + dz, DownDir = (g.Up + 2) & 3, Width = g.Width, Length = top - h[cz * W + cx] });
                for (int j = -2; j < 60; j++)
                    for (int o = -half - 2; o <= g.Width - 1 - half + 2; o++)
                    {
                        int x = cx - dx * j + px * o, z = cz - dz * j + pz * o;
                        if (x < 0 || z < 0 || x >= W || z >= H) continue;
                        int i = z * W + x;
                        if (j > 2 && !map.Has(i, CellFlags.Gate)) continue;
                        gateZone[i] = true;
                    }
            }
            return built;
        }

        // ---------------- 桥和隧道 ----------------

        static void BuildSpans(WorldLayout L, RegionMap map, LayoutResult res)
        {
            var h = map.Height;
            int bridges = 0, tunnels = 0;
            for (int si = 0; si < L.Spans.Count; si++)
            {
                var sp = L.Spans[si];
                if (sp.X0 != sp.X1 && sp.Z0 != sp.Z1) throw new FormatException($"桥 / 隧道 {sp.Id} 目前只支持南北或东西走向");
                map.EnsureSpans();
                bool ns = sp.X0 == sp.X1;
                int a0 = ns ? Math.Min(sp.Z0, sp.Z1) : Math.Min(sp.X0, sp.X1);
                int a1 = ns ? Math.Max(sp.Z0, sp.Z1) : Math.Max(sp.X0, sp.X1);
                int c = ns ? sp.X0 : sp.Z0;
                int half = sp.Width / 2;
                int hA = h[map.Index(sp.X0, sp.Z0)], hB = h[map.Index(sp.X1, sp.Z1)];
                if (sp.Type == SpanType.Bridge)
                {
                    bridges++;
                    int deck = Math.Max(hA, hB);
                    for (int a = a0; a <= a1; a++)
                        for (int o = -half; o <= sp.Width - 1 - half; o++)
                        {
                            int x = ns ? c + o : a, z = ns ? a : c + o;
                            if (!map.InBounds(x, z)) continue;
                            int i = map.Index(x, z);
                            if (h[i] >= deck - 1)
                            {
                                h[i] = deck; map.Slope[i] = -1;
                                ClearWater(map, i);
                                map.Flags[i] |= CellFlags.Road;
                            }
                            else
                            {
                                map.SpanBottom[i] = deck - 1;
                                map.SpanTop[i] = deck;
                                map.SpanKind[i] = 1;
                                res.BridgeOf[i] = si;
                            }
                        }
                }
                else
                {
                    tunnels++;
                    int floor = Math.Min(hA, hB);
                    for (int a = a0; a <= a1; a++)
                        for (int o = -half; o <= sp.Width - 1 - half; o++)
                        {
                            int x = ns ? c + o : a, z = ns ? a : c + o;
                            if (!map.InBounds(x, z)) continue;
                            int i = map.Index(x, z);
                            int orig = h[i];
                            h[i] = floor; map.Slope[i] = -1;
                            ClearWater(map, i);
                            map.Flags[i] |= CellFlags.Road;
                            if (orig - (floor + sp.Clear) >= 1)
                            {
                                map.SpanBottom[i] = floor + sp.Clear;
                                map.SpanTop[i] = orig;
                                map.SpanKind[i] = 2;
                            }
                        }
                }
            }
            map.Stats["bridges"] = bridges;
            map.Stats["tunnels"] = tunnels;
        }
    }
}
