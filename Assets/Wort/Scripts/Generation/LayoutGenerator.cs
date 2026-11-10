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
        /// <summary>出入口（坡道 / 台阶）及其周边：连通检查里允许跨区域。</summary>
        public bool[] GateZone;
        /// <summary>桥 / 隧道走廊：连通检查里允许跨区域。</summary>
        public bool[] SpanCorridor;
        /// <summary>桥面格所属的桥（Layout.Spans 下标），否则 -1。</summary>
        public int[] BridgeOf;
        public LayoutReport Report;

        public LayoutRegion RegionAt(int i) => Layout.Regions[RegionId[i]];
    }

    /// <summary>
    /// layout 管线（Docs/WorldGen.md 第 4 节）：
    /// layout → Biome 图 → 区域层级 → 区域内部噪声 → 水 → 地点 / 道路 → 出入口 → 桥和隧道 → 检查。
    /// 拓扑只由 layout 决定：区域之间的崖由层级差直接得出，只在出入口开口；内部噪声碰不到区域边界。
    /// </summary>
    public static class LayoutGenerator
    {
        /// <summary>开放区里，离结构区域（台地、盆地等）边界多近以内不放矮台地。</summary>
        const double TerraceBorderMargin = 8;
        /// <summary>开放区内部高度的 Biome 平均半径（米），用于开放区之间的平滑过渡。</summary>
        const int OpenSmoothRadius = 10;

        public static LayoutResult Generate(WorldLayout L)
        {
            var s = new RegionSettings { Seed = L.Seed, ChunksX = L.ChunksX, ChunksZ = L.ChunksZ, MinY = -8 };
            var map = new RegionMap(s);
            var res = new LayoutResult { Layout = L, Map = map };
            int W = map.W, H = map.H, N = W * H;

            // 1. 区域图（有机形状，1m 栅格化）
            res.RegionId = RasterizeRegions(L, s, W, H);
            var reg = res.RegionId;
            var open = new bool[N];
            for (int i = 0; i < N; i++) open[i] = L.Regions[reg[i]].IsOpen;

            // 2. Biome 图：由区域给出，边界渐变
            BuildBiomes(L, map, reg);

            // 3. 不放噪声的保护区：道路、地点、出入口、桥和隧道
            var keepFlat = new bool[N];
            res.GateZone = new bool[N];
            res.SpanCorridor = new bool[N];
            res.BridgeOf = new int[N];
            for (int i = 0; i < N; i++) res.BridgeOf[i] = -1;
            MarkProtected(L, s, W, H, keepFlat, res.GateZone, res.SpanCorridor);

            // 4. 区域层级 + 开放区内部噪声
            var distToStruct = Grid.DistanceInside(open, W, H);
            BuildHeights(L, map, reg, open, keepFlat, distToStruct);

            // 5. 结构区域边界：保证崖至少 2 层（不被噪声补出台阶）
            map.Stats["boundaryFixes"] = EnforceBoundaries(L, map, reg);

            // 6. 地点、道路
            StampSites(L, map, reg);
            StampRoads(L, map, reg);

            // 7. 出入口（坡道 / 台阶）
            map.Stats["gates"] = BuildGates(L, map, res.GateZone, reg);

            // 8. 桥和隧道（悬空层）
            BuildSpans(L, map, res);

            var home = L.Home != null ? L.Site(L.Home) : null;
            map.SpawnX = home != null ? home.X : W / 2;
            map.SpawnZ = home != null ? home.Z : H / 2;

            // 9. 检查
            res.Report = LayoutChecks.Run(res);
            return res;
        }

        // ---------------- 1. 区域图 ----------------

        static int[] RasterizeRegions(WorldLayout L, RegionSettings s, int W, int H)
        {
            var reg = new int[W * H];
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
                            double gx = x + 0.5, gz = z + 0.5;
                            if (sh.Organic(gx, gz) < 0) reg[i] = r.Index;
                        }
                }
            for (int i = 0; i < reg.Length; i++)
                if (reg[i] < 0) throw new FormatException("有格子不属于任何区域：layout 需要一个 fill 形状的底层区域");
            RemoveSlivers(L, reg, W, H);
            return reg;
        }

        /// <summary>
        /// 形状扭曲留下的碎块并入周围最多的区域：
        /// 任何区域小于 MinPiece 格的碎块（避免被崖圈死的小洞）；结构区域除最大一块以外的所有块（避免没有出入口的孤岛）。
        /// 开放区可以合法地分成几块（如被长台地隔开的南北两片森林），只去掉碎块。
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

        // ---------------- 2. Biome ----------------

        static void BuildBiomes(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H, N = W * H;
            var one = new float[BiomeTable.Count][];
            for (int b = 0; b < BiomeTable.Count; b++) one[b] = new float[N];
            for (int i = 0; i < N; i++) one[(int)L.Regions[reg[i]].Biome][i] = 1;
            for (int b = 0; b < BiomeTable.Count; b++)
            {
                var w = Grid.BoxBlur(Grid.BoxBlur(one[b], W, H, 8), W, H, 8);
                Array.Copy(w, map.Weights[b], N);
            }
            for (int i = 0; i < N; i++)
            {
                // 主导 Biome 直接取所在区域，权重只用于地表渐变
                map.Biome[i] = (byte)L.Regions[reg[i]].Biome;
            }
        }

        // ---------------- 3. 保护区 ----------------

        static void MarkProtected(WorldLayout L, RegionSettings s, int W, int H, bool[] keepFlat, bool[] gateZone, bool[] spanCorridor)
        {
            foreach (var r in L.Roads)
                StampPolyline(r.PX, r.PZ, r.Width * 0.5 + 1, W, H, i => keepFlat[i] = true);
            foreach (var site in L.Sites)
            {
                var t = site.T;
                Ellipse(site.X + 0.5, site.Z + 0.5, t.RX + t.Buffer, t.RZ + t.Buffer, W, H, i => keepFlat[i] = true);
            }
            foreach (var g in L.Gates)
            {
                double ex = g.X + 0.5 + Grid.DX4[g.Up] * 20, ez = g.Z + 0.5 + Grid.DZ4[g.Up] * 20;
                StampPolyline(new[] { g.X + 0.5, ex }, new[] { g.Z + 0.5, ez }, g.Width * 0.5 + 2, W, H, i => { keepFlat[i] = true; });
            }
            foreach (var sp in L.Spans)
            {
                double dx = Math.Sign(sp.X1 - sp.X0) * 3, dz = Math.Sign(sp.Z1 - sp.Z0) * 3;
                StampPolyline(new[] { sp.X0 + 0.5 - dx, sp.X1 + 0.5 + dx }, new[] { sp.Z0 + 0.5 - dz, sp.Z1 + 0.5 + dz },
                    sp.Width * 0.5 + 2, W, H, i => { keepFlat[i] = true; spanCorridor[i] = true; });
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

        // ---------------- 4. 高度 ----------------

        static void BuildHeights(WorldLayout L, RegionMap map, int[] reg, bool[] open, bool[] keepFlat, float[] distToStruct)
        {
            var s = map.Settings;
            int W = map.W, H = map.H, N = W * H;

            // 开放区的基准层级：只在开放区之间平均（开放区不制造崖；挨着结构区域时崖属于对方）
            var num = new float[N];
            var den = new float[N];
            for (int i = 0; i < N; i++) if (open[i]) { num[i] = L.Regions[reg[i]].AbsLevel; den[i] = 1; }
            for (int k = 0; k < 2; k++) { num = Grid.BoxBlur(num, W, H, OpenSmoothRadius); den = Grid.BoxBlur(den, W, H, OpenSmoothRadius); }

            var micro = new Fbm(Hash.Derive(s.Seed, "layout.micro"), 14, 3);
            var microBroad = new Fbm(Hash.Derive(s.Seed, "layout.microBroad"), 30, 3);
            var terrace = new Fbm(Hash.Derive(s.Seed, "layout.terrace"), 44, 3);
            var pond = new Fbm(Hash.Derive(s.Seed, "layout.pond"), 18, 3);
            var qMicro = new QuantileTable((x, z) => micro.Sample(x, z));
            var qMicroBroad = new QuantileTable((x, z) => microBroad.Sample(x, z));
            var qTerrace = new QuantileTable((x, z) => terrace.Sample(x, z));
            var qPond = new QuantileTable((x, z) => pond.Sample(x, z));

            // 到区域边界的距离：结构区域（台地、高原）顶面离边 5m 以外才放微起伏，崖边保持干净
            var inner = new bool[N];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    bool same = true;
                    for (int d = 0; d < 4 && same; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        if (reg[nz * W + nx] != reg[i]) same = false;
                    }
                    inner[i] = same;
                }
            var distToEdge = Grid.DistanceInside(inner, W, H);

            int ponds = 0, raised = 0, terraced = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    var r = L.Regions[reg[i]];
                    double gx = s.OriginX + x + 0.5, gz = s.OriginZ + z + 0.5;
                    if (!open[i])
                    {
                        int hs = r.AbsLevel;
                        // 台地、高原顶面：0–1 层轻微起伏；盆地、峡谷保持平整（空盆地不放东西）
                        bool top = r.Kind == RegionKind.Plateau || r.Kind == RegionKind.Highland;
                        if (top && !keepFlat[i] && distToEdge[i] >= 5 && microBroad.Sample(gx, gz) > qMicroBroad.Quantile(0.7)) { hs++; raised++; }
                        map.Height[i] = hs;
                        continue;
                    }
                    int baseLv = (int)Math.Floor(num[i] / Math.Max(1e-6f, den[i]) + 0.5);
                    var p = BiomeTable.Get(r.Biome);
                    int h = baseLv;
                    if (!keepFlat[i])
                    {
                        // 微起伏：0–1 层的台阶，覆盖率按 Biome 微起伏幅度
                        double cov = p.MicroAmp >= 1 ? 0.45 : 0.3;
                        bool up = p.MicroBroad
                            ? microBroad.Sample(gx, gz) > qMicroBroad.Quantile(1 - cov)
                            : micro.Sample(gx, gz) > qMicro.Quantile(1 - cov);
                        if (up) { h++; raised++; }
                        // 矮台地：1–2 层，叠一次；离结构区域边界 8m 以内不放
                        if (distToStruct[i] >= TerraceBorderMargin)
                        {
                            double c1 = p.TerraceCoverage * 0.6;
                            double t = terrace.Sample(gx, gz);
                            if (t > qTerrace.Quantile(1 - c1)) { h++; terraced++; }
                            if (t > qTerrace.Quantile(1 - c1 * 0.35)) h++;
                        }
                        // 沼泽池塘：比底面低 1 层的浅水
                        if (p.PondCoverage > 0 && distToStruct[i] >= 4 && pond.Sample(gx, gz) > qPond.Quantile(1 - p.PondCoverage))
                        {
                            h = baseLv - 1;
                            map.Water[i] = baseLv;
                            map.Flags[i] |= CellFlags.Pond;
                            ponds++;
                        }
                    }
                    map.Height[i] = h;
                }

            // 开放区内部高差不超过 1 层（矮台地叠成台阶；微起伏和台地边缘重合处不出 2 层）
            LipschitzClamp(map, open);
            map.Stats["pondCells"] = ponds;
            map.Stats["microRaised"] = raised;
            map.Stats["terraceCells"] = terraced;
        }

        static void LipschitzClamp(RegionMap map, bool[] open)
        {
            int W = map.W, H = map.H;
            var h = map.Height;
            for (int pass = 0; pass < 16; pass++)
            {
                bool changed = false;
                for (int dir = 0; dir < 2; dir++)
                    for (int zz = 0; zz < H; zz++)
                        for (int xx = 0; xx < W; xx++)
                        {
                            int x = dir == 0 ? xx : W - 1 - xx, z = dir == 0 ? zz : H - 1 - zz;
                            int i = z * W + x;
                            if (!open[i]) continue;
                            for (int d = 0; d < 4; d++)
                            {
                                int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                                if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                                int n = nz * W + nx;
                                if (!open[n]) continue;
                                if (h[i] > h[n] + 1) { h[i] = h[n] + 1; changed = true; }
                            }
                        }
                if (!changed) break;
            }
            // 水面跟着被压低的池底走
            for (int i = 0; i < h.Length; i++)
                if (map.Water[i] != RegionMap.NoWater && map.Water[i] <= h[i]) { map.Water[i] = RegionMap.NoWater; map.Flags[i] &= ~CellFlags.Pond; }
        }

        // ---------------- 5. 边界 ----------------

        /// <summary>
        /// 结构区域的边界（出入口以外）必须至少 2 层高差，按 layout 的层级方向：
        /// 设计上高的一侧至少高 2 层。噪声补出的台阶在这里改回去（改结构区域那一侧）。
        /// </summary>
        static int EnforceBoundaries(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H, fixes = 0;
            var h = map.Height;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    var ri = L.Regions[reg[i]];
                    if (ri.IsOpen) continue;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H) continue;
                        int n = nz * W + nx;
                        var rn = L.Regions[reg[n]];
                        if (rn == ri) continue;
                        if (ri.AbsLevel > rn.AbsLevel && h[i] < h[n] + 2) { h[i] = h[n] + 2; fixes++; }
                        else if (ri.AbsLevel < rn.AbsLevel && h[i] > h[n] - 2) { h[i] = h[n] - 2; fixes++; }
                    }
                }
            return fixes;
        }

        // ---------------- 6. 地点、道路 ----------------

        static void StampSites(WorldLayout L, RegionMap map, int[] reg)
        {
            int W = map.W, H = map.H;
            var s = map.Settings;
            foreach (var site in L.Sites)
            {
                var t = site.T;
                int c = site.Z * W + site.X;
                int level = map.Height[c];
                var warp = new Fbm(Hash.Derive(s.Seed, "layout.site." + site.Id), Math.Max(8, Math.Min(t.RX, t.RZ)), 2);
                double amp = Math.Max(0.5, Math.Min(4, Math.Min(t.RX, t.RZ) * 0.15));
                int x0 = Math.Max(0, (int)(site.X - t.RX - amp - 1)), x1 = Math.Min(W - 1, (int)(site.X + t.RX + amp + 1));
                int z0 = Math.Max(0, (int)(site.Z - t.RZ - amp - 1)), z1 = Math.Min(H - 1, (int)(site.Z + t.RZ + amp + 1));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = z * W + x;
                        if (reg[i] != reg[c]) continue;
                        double dx = (x - site.X) / t.RX, dz = (z - site.Z) / t.RZ;
                        double d = (Math.Sqrt(dx * dx + dz * dz) - 1) * Math.Min(t.RX, t.RZ) + amp * warp.Sample(x + 0.5, z + 0.5);
                        if (d >= 0) continue;
                        map.Flags[i] |= CellFlags.Site;
                        if (t.Terrain == "flat" || t.Terrain == "cliffBehind")
                        {
                            map.Height[i] = level;
                            map.Water[i] = RegionMap.NoWater;
                            map.Flags[i] &= ~CellFlags.Pond;
                        }
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
                    map.Water[i] = RegionMap.NoWater;
                    map.Flags[i] &= ~CellFlags.Pond;
                });
            }
        }

        // ---------------- 7. 出入口 ----------------

        /// <summary>
        /// 从 gate.at 沿上坡方向走，找到第一道 ≥ 2 层的崖；在崖脚低的一侧往回铺坡道（斜坡体素，每格升 1 层）
        /// 或台阶（每级 1 层），宽 gate.Width。
        /// </summary>
        static int BuildGates(WorldLayout L, RegionMap map, bool[] gateZone, int[] reg)
        {
            int W = map.W, H = map.H, built = 0;
            var h = map.Height;
            foreach (var g in L.Gates)
            {
                int dx = Grid.DX4[g.Up], dz = Grid.DZ4[g.Up];
                int px = Grid.DX4[(g.Up + 1) & 3], pz = Grid.DZ4[(g.Up + 1) & 3];
                // 在 at 前后 60 格内找离 at 最近的一道 ≥ 2 层的上坡崖（形状有机化后，at 可能落在崖的任一侧）
                int toRegion = g.To != null ? (L.Region(g.To) ?? throw new FormatException($"出入口 {g.Id} 的 to 区域 {g.To} 不存在")).Index : -1;
                int cx = g.X, cz = g.Z, top = int.MinValue, bestDist = int.MaxValue;
                for (int t = -60; t < 60; t++)
                {
                    int ax = g.X + dx * t, az = g.Z + dz * t, bx = ax + dx, bz = az + dz;
                    if (!map.InBounds(ax, az) || !map.InBounds(bx, bz)) continue;
                    int diff = h[bz * W + bx] - h[az * W + ax];
                    int dist = t < 0 ? -t : t;
                    if (toRegion >= 0 && reg[bz * W + bx] != toRegion) continue;
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
                        if (target < ground) break; // 碰到更高的地面
                        h[i] = target;
                        map.Water[i] = RegionMap.NoWater;
                        map.Flags[i] = (map.Flags[i] & ~CellFlags.Pond) | CellFlags.Gate | CellFlags.Ramp;
                        map.Slope[i] = g.Type == GateType.Ramp ? (sbyte)g.Up : (sbyte)-1;
                        if (target == ground) break; // 到坡脚了
                    }
                }
                map.Ramps.Add(new RampInfo { TopX = cx + dx, TopZ = cz + dz, DownDir = (g.Up + 2) & 3, Width = g.Width, Length = top - h[cz * W + cx] });
                // 出入口区域：坡道本身加周边 2 格
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

        // ---------------- 8. 桥和隧道 ----------------

        static void BuildSpans(WorldLayout L, RegionMap map, LayoutResult res)
        {
            int W = map.W, H = map.H;
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
                                // 桥头：地面压到桥面高度
                                h[i] = deck; map.Slope[i] = -1;
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
                            map.Water[i] = RegionMap.NoWater;
                            map.Flags[i] = (map.Flags[i] & ~CellFlags.Pond) | CellFlags.Road;
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
