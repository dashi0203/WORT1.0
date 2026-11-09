using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 阶段 A：区域级二维生成（GDD 6.3）。
    /// 1 读取输入（第一个实验由程序生成 Biome 布局与宏观高度）
    /// 2 Biome 权重图  3 宏观高度  4 微起伏  5 台地 / 高台 / 孤峰
    /// 6 水系  7 量化与清理  8 坡道与连通性
    /// 所有噪声都是世界坐标的函数；区域只是切块的位置。
    /// </summary>
    public sealed class RegionGenerator
    {
        // ---- 可调常量（第一版起点） ----
        const int BiomeBlurRadius = 16;        // 3 次盒式模糊 → 过渡带约 40m
        const int TerraceMinHalfWidth = 4;     // 台地顶部最窄 ≈ 8m
        const int TerraceGapClose = 2;         // 台地间通道 < 4m 的填平
        const int TerraceMinArea = 80;
        const int TierInset = 3;               // 上一级台地比下一级至少内缩 3m
        static readonly double[] TierCoverageFactor = { 1.0, 0.38, 0.15, 0.06 };
        const int MaxTiers = 4;
        const int MinRampArea = 40;            // 小于此面积的封闭区直接填平
        const int RampSpacing = 24;
        const int MaxCliffForRamp = 8;
        const int HiddenScan = 24;

        readonly RegionSettings s;
        readonly RegionMap map;
        readonly int W, H;
        readonly uint seed;

        RegionGenerator(RegionSettings settings)
        {
            s = settings;
            map = new RegionMap(settings);
            W = map.W;
            H = map.H;
            seed = settings.Seed;
        }

        public static RegionMap Generate(RegionSettings settings)
        {
            var g = new RegionGenerator(settings);
            g.Run();
            return g.map;
        }

        double WX(int x) => s.OriginX + x + 0.5;
        double WZ(int z) => s.OriginZ + z + 0.5;
        Fbm Noise(string name, double wavelength, int octaves) => new Fbm(Hash.Derive(seed, name), wavelength, octaves);

        // 中间结果
        double[] macro;      // 连续宏观高度（层）
        double[] micro;      // 连续微起伏（层）
        int[] lift;          // 山地大悬崖带抬升（层）
        bool[][] tiers;      // 各级台地
        int[] terraceOffset;

        void Run()
        {
            BuildBiomeLayout();
            BuildWeights();
            BuildMacro();
            BuildMicro();
            BuildTerraces();
            Quantize();
            BuildMesasAndSpires();
            BuildPonds();
            BuildRiver();
            FlattenHiddenStrips();
            ConnectAndRamp();
            ExtraRamps();
            AssignSlopes();
            MarkHiddenAndEdges();
            ChooseSpawn();
            CollectStats();
        }

        // ------------------------------------------------------------------
        // 1. Biome 区域图（正式版由策划画；第一个实验由程序自由发挥）
        // 北边山地，往南依次针叶林、混交林；中南部人类区；西南沼泽。
        // ------------------------------------------------------------------
        void BuildBiomeLayout()
        {
            var warpX = Noise("layout.warpX", 120, 3);
            var warpZ = Noise("layout.warpZ", 120, 3);
            var line = Noise("layout.line", 150, 3);
            var blob = Noise("layout.blob", 90, 2);
            uint lh = Hash.Derive(seed, "layout.jitter");
            double J(int k) => Hash.Unit(Hash.Of(lh, (uint)k)) * 2 - 1;

            double humanCx = W * (0.58 + 0.06 * J(1)), humanCz = H * (0.32 + 0.05 * J(2)), humanR = W * 0.27;
            double swampCx = W * (0.16 + 0.04 * J(3)), swampCz = H * (0.20 + 0.05 * J(4)), swampR = W * 0.19;

            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    double gx = WX(x), gz = WZ(z);
                    // Fbm 的典型幅度约 ±0.4，系数取大一些才有明显的扭曲
                    double lx = x + warpX.Sample(gx, gz) * 150;
                    double lz = z + warpZ.Sample(gx, gz) * 110;
                    double mountainLine = H * 0.76 + 80 * line.Sample(gx, 61.7);
                    double coniferLine = mountainLine - (60 + 60 * line.Sample(gx + 900, 133.1));
                    double b = blob.Sample(gx, gz);
                    BiomeId id;
                    if (lz > mountainLine) id = BiomeId.Mountain;
                    else if (lz > coniferLine) id = BiomeId.Conifer;
                    else if (Dist(lx, lz, swampCx, swampCz) < swampR * (1 + 1.0 * b)) id = BiomeId.Swamp;
                    else if (Dist(lx, lz, humanCx, humanCz) < humanR * (1 + 0.9 * b)) id = BiomeId.Human;
                    else id = BiomeId.MixedForest;
                    map.Biome[z * W + x] = (byte)id;
                }
        }

        static double Dist(double ax, double az, double bx, double bz)
        {
            double dx = ax - bx, dz = az - bz;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        // ------------------------------------------------------------------
        // 2. Biome 权重图：边界模糊成渐变带
        // ------------------------------------------------------------------
        void BuildWeights()
        {
            int n = W * H;
            for (int b = 0; b < BiomeTable.Count; b++)
            {
                var one = new float[n];
                for (int i = 0; i < n; i++) one[i] = map.Biome[i] == b ? 1f : 0f;
                for (int pass = 0; pass < 3; pass++) one = Grid.BoxBlur(one, W, H, BiomeBlurRadius);
                map.Weights[b] = one;
            }
        }

        double Blend(int i, Func<BiomeParams, double> f)
        {
            double v = 0, sum = 0;
            for (int b = 0; b < BiomeTable.Count; b++)
            {
                double w = map.Weights[b][i];
                if (w <= 0) continue;
                v += w * f(BiomeTable.All[b]);
                sum += w;
            }
            return sum > 0 ? v / sum : 0;
        }

        // ------------------------------------------------------------------
        // 3. 宏观高度 + 山地外缘大悬崖带
        // ------------------------------------------------------------------
        void BuildMacro()
        {
            int n = W * H;
            macro = new double[n];
            lift = new int[n];
            var low = Noise("macro.low", 230, 2);
            var band = Noise("macro.band", 90, 2);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    macro[i] = Blend(i, p => p.MacroBase) + 1.3 * low.Sample(WX(x), WZ(z));
                }

            // 山地外缘：4–8 层的线状大悬崖带，越往里再抬升
            var mountain = new bool[n];
            for (int i = 0; i < n; i++) mountain[i] = map.Weights[(int)BiomeId.Mountain][i] >= 0.5f;
            var depth = Grid.DistanceInside(mountain, W, H);
            uint bh = Hash.Derive(seed, "macro.bandHeight");
            int band1 = 5 + (int)(Hash.Unit(Hash.Of(bh, 1)) * 3);   // 5–7
            int band2 = 4 + (int)(Hash.Unit(Hash.Of(bh, 2)) * 2);   // 4–5
            int band3 = 3 + (int)(Hash.Unit(Hash.Of(bh, 3)) * 2);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (!mountain[i]) continue;
                    double nb = band.Sample(WX(x), WZ(z));
                    int l = band1;
                    if (depth[i] > 36 + 14 * nb) l += band2;
                    if (depth[i] > 80 + 16 * nb) l += band3;
                    lift[i] = l;
                }
        }

        // ------------------------------------------------------------------
        // 4. 微起伏：世界坐标噪声，幅度按 Biome 权重混合
        // ------------------------------------------------------------------
        void BuildMicro()
        {
            micro = new double[W * H];
            var fine = Noise("micro.fine", 22, 2);
            var broad = Noise("micro.broad", 56, 2);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    double gx = WX(x), gz = WZ(z);
                    double nf = fine.Sample(gx, gz), nb = broad.Sample(gx, gz);
                    micro[i] = Blend(i, p => p.MicroAmp * (p.MicroBroad ? nb : nf));
                }
        }

        // ------------------------------------------------------------------
        // 5. 台地：低频块状噪声（加扰动）按混合后的覆盖率取阈值，逐级内缩叠层
        // ------------------------------------------------------------------
        void BuildTerraces()
        {
            int n = W * H;
            var t = Noise("terrace.field", 100, 3);
            var warpX = Noise("terrace.warpX", 55, 2);
            var warpZ = Noise("terrace.warpZ", 55, 2);
            Func<double, double, double> field = (gx, gz) =>
                t.Sample(gx + 18 * warpX.Sample(gx, gz), gz + 18 * warpZ.Sample(gx, gz));
            var q = new QuantileTable(field);
            var stackNoise = Noise("terrace.stack", 150, 2);

            var T = new double[n];
            var cov = new double[n];
            var stack = new double[n];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    double gx = WX(x), gz = WZ(z);
                    T[i] = field(gx, gz);
                    cov[i] = Blend(i, p => p.TerraceCoverage);
                    stack[i] = Blend(i, p => p.StackLevels) + 0.45 * stackNoise.Sample(gx, gz);
                }

            tiers = new bool[MaxTiers][];
            for (int k = 0; k < MaxTiers; k++)
            {
                var m = new bool[n];
                for (int i = 0; i < n; i++)
                    m[i] = T[i] > q.Quantile(1 - cov[i] * TierCoverageFactor[k]) && stack[i] >= k + 0.5;
                if (k > 0)
                {
                    var inner = Grid.Erode(tiers[k - 1], W, H, TierInset);
                    for (int i = 0; i < n; i++) m[i] &= inner[i];
                }
                m = Grid.Open(m, W, H, TerraceMinHalfWidth);
                m = Grid.Close(m, W, H, TerraceGapClose);
                if (k > 0) for (int i = 0; i < n; i++) m[i] &= tiers[k - 1][i];
                m = Grid.RemoveSmall(m, W, H, TerraceMinArea);
                tiers[k] = m;
            }

            terraceOffset = new int[n];
            var steps = new Fbm[MaxTiers];
            for (int k = 0; k < MaxTiers; k++) steps[k] = Noise("terrace.step" + k, 180, 1);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    int off = 0, tier = 0;
                    for (int k = 0; k < MaxTiers; k++)
                    {
                        if (!tiers[k][i]) break;
                        off += steps[k].Sample(WX(x), WZ(z)) > 0 ? 3 : 2; // 普通台地 2–3 层
                        tier = k + 1;
                    }
                    int cap = (int)Math.Floor(Blend(i, p => p.MaxTerraceHeight) + 0.5);
                    terraceOffset[i] = Math.Min(off, cap);
                    map.Tier[i] = (byte)tier;
                }
        }

        // ------------------------------------------------------------------
        // 7（先做）. 量化到整数层并清理锯齿
        // ------------------------------------------------------------------
        void Quantize()
        {
            var h = map.Height;
            for (int i = 0; i < h.Length; i++)
                h[i] = (int)Math.Floor(macro[i] + micro[i] + 0.5) + terraceOffset[i] + lift[i];

            // 修锯齿：3 个以上邻居与本格差 1 且一致 → 跟随邻居；去掉单格凸起 / 小坑
            for (int pass = 0; pass < 3; pass++)
            {
                var src = (int[])h.Clone();
                for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = z * W + x;
                        int c = src[i];
                        int up = 0, down = 0, nb = 0;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                            if (!map.InBounds(nx, nz)) continue;
                            nb++;
                            int v = src[nz * W + nx];
                            if (v == c + 1) up++;
                            else if (v == c - 1) down++;
                        }
                        if (up >= 3 || (nb < 4 && up == nb)) h[i] = c + 1;
                        else if (down >= 3 || (nb < 4 && down == nb)) h[i] = c - 1;
                    }
            }
        }

        // ------------------------------------------------------------------
        // 5（续）. 高台与孤峰：更高频、更稀疏的噪声叠在台地上或附近
        // ------------------------------------------------------------------
        void BuildMesasAndSpires()
        {
            int n = W * H;
            var mesaN = Noise("mesa", 34, 2);
            var spireN = Noise("spire", 18, 2);
            var qm = new QuantileTable((x, z) => mesaN.Sample(x, z));
            var qs = new QuantileTable((x, z) => spireN.Sample(x, z));
            var nearTier = Grid.Dilate(tiers[0], W, H, 8);
            double mesaThr = qm.Quantile(1 - 0.03);
            double spireThr = qs.Quantile(1 - 0.012);

            var mesa = new bool[n];
            var spire = new bool[n];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    double gx = WX(x), gz = WZ(z);
                    if (nearTier[i] && Blend(i, p => p.AllowMesa ? 1 : 0) >= 0.5 && mesaN.Sample(gx, gz) > mesaThr)
                        mesa[i] = true;
                    if (map.Weights[(int)BiomeId.Mountain][i] >= 0.7f && spireN.Sample(gx, gz) > spireThr)
                        spire[i] = true;
                }
            mesa = Grid.Open(mesa, W, H, 2);
            int mesas = RaiseBlobs(mesa, 40, 300, CellFlags.Mesa, (ringMin, ringMax, inMax, k) => Math.Max(inMax, ringMin + 4 + (int)(k & 1)));
            int spires = RaiseBlobs(spire, 12, 70, CellFlags.Spire, (ringMin, ringMax, inMax, k) => Math.Max(inMax, ringMax) + 6 + (int)(k % 3));
            map.Stats["mesas"] = mesas;
            map.Stats["spires"] = spires;
        }

        int RaiseBlobs(bool[] mask, int minArea, int maxArea, CellFlags flag, Func<int, int, int, uint, int> top)
        {
            var lab = Grid.Label(W, H, i => mask[i], (a, b) => true, out var sizes);
            var cells = new List<int>[sizes.Count];
            for (int i = 0; i < lab.Length; i++)
                if (lab[i] >= 0) (cells[lab[i]] ??= new List<int>()).Add(i);
            int count = 0;
            for (int c = 0; c < sizes.Count; c++)
            {
                if (sizes[c] < minArea || sizes[c] > maxArea) continue;
                int ringMin = int.MaxValue, ringMax = int.MinValue, inMax = int.MinValue;
                foreach (int i in cells[c])
                {
                    inMax = Math.Max(inMax, map.Height[i]);
                    int x = i % W, z = i / W;
                    for (int dz = -2; dz <= 2; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (!map.InBounds(nx, nz)) continue;
                            int j = nz * W + nx;
                            if (lab[j] == c) continue;
                            ringMin = Math.Min(ringMin, map.Height[j]);
                            ringMax = Math.Max(ringMax, map.Height[j]);
                        }
                }
                if (ringMin == int.MaxValue) continue;
                int t = top(ringMin, ringMax, inMax, Hash.Of(seed ^ (uint)flag, (uint)c));
                foreach (int i in cells[c])
                {
                    map.Height[i] = t;
                    map.Flags[i] |= flag;
                }
                count++;
            }
            return count;
        }

        // ------------------------------------------------------------------
        // 6. 水系：池塘 / 浅洼（按 Biome），人类区湖泊，从山地流出的河流
        // ------------------------------------------------------------------
        void BuildPonds()
        {
            int n = W * H;
            var pondN = Noise("pond", 20, 2);
            var lakeN = Noise("lake", 70, 2);
            var qp = new QuantileTable((x, z) => pondN.Sample(x, z));
            var ql = new QuantileTable((x, z) => lakeN.Sample(x, z));
            var pond = new bool[n];
            var lake = new bool[n];
            double lakeThr = ql.Quantile(1 - 0.06);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (map.Tier[i] > 0 || map.Flags[i] != CellFlags.None) continue;
                    double gx = WX(x), gz = WZ(z);
                    double cov = Blend(i, p => p.PondCoverage);
                    if (cov > 0 && pondN.Sample(gx, gz) > qp.Quantile(1 - cov)) pond[i] = true;
                    if (map.Weights[(int)BiomeId.Human][i] > 0.6f && lakeN.Sample(gx, gz) > lakeThr) lake[i] = true;
                }

            // 人类区只留最大的一个湖
            var lab = Grid.Label(W, H, i => lake[i], (a, b) => true, out var sizes);
            int best = -1;
            for (int c = 0; c < sizes.Count; c++) if (best < 0 || sizes[c] > sizes[best]) best = c;
            for (int i = 0; i < n; i++) if (lake[i] && lab[i] == best) pond[i] = true;

            pond = Grid.Open(pond, W, H, 1);
            lab = Grid.Label(W, H, i => pond[i], (a, b) => true, out sizes);
            var groups = new List<int>[sizes.Count];
            for (int i = 0; i < n; i++) if (lab[i] >= 0) (groups[lab[i]] ??= new List<int>()).Add(i);
            int ponds = 0;
            foreach (var g in groups)
            {
                if (g == null || g.Count < 10) continue;
                int minH = int.MaxValue;
                foreach (int i in g) minH = Math.Min(minH, map.Height[i]);
                // 只取最低一层的部分，保证水面平整；水面比岸低 1 层（沼泽“水面在 −1 层”）
                var sub = new bool[n];
                foreach (int i in g) if (map.Height[i] == minH) sub[i] = true;
                sub = Grid.Open(sub, W, H, 1);
                int level = minH - 1;
                int cells = 0;
                foreach (int i in g)
                {
                    if (!sub[i]) continue;
                    map.Water[i] = level;
                    map.Height[i] = level - 1;
                    map.Flags[i] |= CellFlags.Pond;
                    cells++;
                }
                if (cells == 0) continue;
                // 中心更深
                var inside = new bool[n];
                foreach (int i in g) inside[i] = sub[i];
                var dist = Grid.DistanceInside(inside, W, H);
                foreach (int i in g) if (sub[i] && dist[i] >= 3) map.Height[i] = level - 2;
                ponds++;
            }
            map.Stats["ponds"] = ponds;
        }

        void BuildRiver()
        {
            int n = W * H;
            var mountain = new bool[n];
            for (int i = 0; i < n; i++) mountain[i] = map.Weights[(int)BiomeId.Mountain][i] >= 0.5f;
            var depth = Grid.DistanceInside(mountain, W, H);

            // 源头：山地内 25–45m 深处，东西方向居中，按种子挑选
            uint rh = Hash.Derive(seed, "river.source");
            int source = -1; uint bestKey = uint.MaxValue;
            for (int z = 0; z < H; z++)
                for (int x = W / 5; x < W * 4 / 5; x++)
                {
                    int i = z * W + x;
                    if (!mountain[i] || depth[i] < 25 || depth[i] > 45) continue;
                    if (map.Flags[i] != CellFlags.None) continue;
                    uint key = Hash.Of(x, z, rh);
                    if (key < bestKey) { bestKey = key; source = i; }
                }
            if (source < 0) { map.Stats["river"] = 0; return; }

            // Dijkstra 到南边界：下坡免费，上坡很贵，尽量绕开台地，噪声让河道弯曲
            // 河道倾向沿低频噪声的零等值线走（|n| 小的地方便宜），形成自然弯曲
            var meander = Noise("river.meander", 130, 3);
            var cost = new double[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) { cost[i] = double.PositiveInfinity; prev[i] = -1; }
            var heap = new MinHeap(n);
            cost[source] = 0;
            heap.Push(source, 0);
            int target = -1;
            while (heap.Count > 0)
            {
                int i = heap.Pop(out double c);
                if (c > cost[i]) continue;
                int x = i % W, z = i / W;
                if (z == 0) { target = i; break; }
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (!map.InBounds(nx, nz)) continue;
                    int j = nz * W + nx;
                    double m = Math.Abs(meander.Sample(WX(nx), WZ(nz)));
                    double step = 1 + 30 * m + 0.1 * Math.Max(0, map.Height[j]);
                    int rise = map.Height[j] - map.Height[i];
                    if (rise > 0) step += 3 * rise;
                    if (map.Tier[j] > 0) step += 1.5;
                    if ((map.Flags[j] & (CellFlags.Mesa | CellFlags.Spire)) != 0) step += 200;
                    if (map.HasWater(j)) step *= 0.5;
                    double nc = c + step;
                    if (nc < cost[j]) { cost[j] = nc; prev[j] = i; heap.Push(j, nc); }
                }
            }
            if (target < 0) { map.Stats["river"] = 0; return; }

            var path = new List<int>();
            for (int i = target; i >= 0; i = prev[i]) path.Add(i);
            path.Reverse();

            // 下切：水面沿程单调不升；落差处标记
            var ground = (int[])map.Height.Clone();
            int level = ground[source] - 1;
            for (int p = 0; p < path.Count; p++)
            {
                int i = path[p];
                int here = map.HasWater(i) ? Math.Min(ground[i] - 1, map.Water[i]) : ground[i] - 1;
                int next = Math.Min(level, here);
                if (p > 0 && level - next >= 1)
                    map.Drops.Add(new DropPoint { X = i % W, Z = i / W, Drop = level - next });
                level = next;
                map.RiverPath.Add(i);
                int x = i % W, z = i / W;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (!map.InBounds(nx, nz)) continue;
                        int j = nz * W + nx;
                        if (map.Height[j] < level - 1 && !map.HasWater(j)) continue; // 不让水悬在更低的地面上
                        if (map.HasWater(j) && map.Water[j] <= level) continue;
                        map.Water[j] = level;
                        map.Height[j] = Math.Min(map.Height[j], level - 1);
                        map.Flags[j] |= CellFlags.River;
                    }
            }
            map.Stats["river"] = path.Count;
        }

        // ------------------------------------------------------------------
        // 悬崖背面：南高北低、崖面朝北的悬崖，台地顶面会盖住北侧约“崖高”宽的一条。
        // 这条里不生成能跳上去的 1m 台阶、不放坡。
        // ------------------------------------------------------------------
        bool[] ComputeHidden()
        {
            var hidden = new bool[W * H];
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    int top = map.SurfaceTop(i);
                    for (int d = 1; d <= HiddenScan && z - d >= 0; d++)
                    {
                        int rise = map.SurfaceTop(i - d * W) - top;
                        if (rise >= 2 && rise >= d) { hidden[i] = true; break; }
                    }
                }
            return hidden;
        }

        void FlattenHiddenStrips()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                var hidden = ComputeHidden();
                var src = (int[])map.Height.Clone();
                int changed = 0;
                for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = z * W + x;
                        if (!hidden[i] || map.HasWater(i) || map.Flags[i] != CellFlags.None) continue;
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                            if (!map.InBounds(nx, nz)) continue;
                            int j = nz * W + nx;
                            if (hidden[j] && !map.HasWater(j) && src[j] == src[i] - 1) { map.Height[i] = src[i] - 1; changed++; break; }
                        }
                    }
                if (changed == 0) break;
            }
        }

        // ------------------------------------------------------------------
        // 8. 坡道与连通性：小块封闭区填平，大块在笔直崖边开 2–3 格宽坡道
        // ------------------------------------------------------------------
        int WalkHeight(int i) => map.HasWater(i) ? map.Water[i] : map.Height[i];

        int[] LabelWalk(out List<int> sizes)
        {
            return Grid.Label(W, H, i => true, (a, b) => Math.Abs(WalkHeight(a) - WalkHeight(b)) <= 1, out sizes);
        }

        bool[] hiddenCache;

        void ConnectAndRamp()
        {
            int n = W * H;
            hiddenCache = ComputeHidden();
            var failed = new HashSet<long>();
            int flattened = 0, ramps = 0;
            for (int iter = 0; iter < 400; iter++)
            {
                var lab = LabelWalk(out var sizes);
                int main = 0;
                for (int c = 1; c < sizes.Count; c++) if (sizes[c] > sizes[main]) main = c;

                // 统计各连通域的特殊格
                var special = new int[sizes.Count];
                var water = new int[sizes.Count];
                var first = new int[sizes.Count];
                for (int c = 0; c < first.Length; c++) first[c] = -1;
                for (int i = 0; i < n; i++)
                {
                    if (first[lab[i]] < 0) first[lab[i]] = i;
                    if ((map.Flags[i] & (CellFlags.Mesa | CellFlags.Spire)) != 0) special[lab[i]]++;
                    if (map.HasWater(i)) water[lab[i]]++;
                }

                var order = new List<int>();
                for (int c = 0; c < sizes.Count; c++)
                {
                    if (c == main) continue;
                    if (special[c] * 2 >= sizes[c]) continue;   // 高台、孤峰：基本没有坡道
                    if (water[c] * 2 >= sizes[c]) continue;     // 水面
                    order.Add(c);
                }
                if (order.Count == 0) break;
                order.Sort((a, b) => sizes[b].CompareTo(sizes[a]));

                bool progress = false;
                foreach (int c in order)
                {
                    long key = first[c];
                    if (failed.Contains(key)) continue;
                    if (sizes[c] < MinRampArea)
                    {
                        if (FlattenComponent(lab, c)) { flattened++; progress = true; break; }
                        failed.Add(key);
                        continue;
                    }
                    if (TryPlaceRamp((h, l) => (lab[h] == c) != (lab[l] == c), (h, l) => lab[h] == main || lab[l] == main, false))
                    {
                        ramps++; progress = true; break;
                    }
                    failed.Add(key);
                }
                if (!progress) break;
            }
            map.Stats["rampsConnect"] = ramps;
            map.Stats["flattened"] = flattened;
        }

        bool FlattenComponent(int[] lab, int c)
        {
            var counts = new Dictionary<int, int>();
            var cells = new List<int>();
            for (int i = 0; i < lab.Length; i++)
            {
                if (lab[i] != c) continue;
                if (map.HasWater(i) || map.Flags[i] != CellFlags.None) return false;
                cells.Add(i);
                int x = i % W, z = i / W;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                    if (!map.InBounds(nx, nz)) continue;
                    int j = nz * W + nx;
                    if (lab[j] == c) continue;
                    int h = WalkHeight(j);
                    counts[h] = counts.TryGetValue(h, out int k) ? k + 1 : 1;
                }
            }
            if (counts.Count == 0) return false;
            int bestH = 0, bestN = -1;
            foreach (var kv in counts) if (kv.Value > bestN || (kv.Value == bestN && kv.Key < bestH)) { bestH = kv.Key; bestN = kv.Value; }
            foreach (int i in cells) map.Height[i] = bestH;
            return true;
        }

        /// <summary>
        /// 在高格 h 与低格 l = h + dir 之间的笔直崖边上放坡道。坡道占低处一侧，
        /// 不朝北（朝北的崖面在屏幕上不可见，属于悬崖背面）。
        /// </summary>
        bool TryPlaceRamp(Func<int, int, bool> edgeFilter, Func<int, int, bool> bonus, bool spaced)
        {
            int bestScore = int.MinValue, bestH = -1, bestDir = 0, bestW = 0, bestLen = 0;
            uint rh = Hash.Derive(seed, "ramp.pick." + map.Ramps.Count);
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int h = z * W + x;
                    for (int dir = 1; dir < 4; dir++) // 1=东 2=南 3=西，不往北下坡
                    {
                        int lx = x + Grid.DX4[dir], lz = z + Grid.DZ4[dir];
                        if (!map.InBounds(lx, lz)) continue;
                        int l = lz * W + lx;
                        int drop = map.Height[h] - map.Height[l];
                        if (drop < 2 || drop > MaxCliffForRamp) continue;
                        if (map.HasWater(h) || map.HasWater(l)) continue;
                        if (!edgeFilter(h, l)) continue;
                        for (int width = 3; width >= 2; width--)
                        {
                            if (!RampFits(x, z, dir, width, drop)) continue;
                            int near = NearestRamp(x, z);
                            if (spaced && near < RampSpacing) continue;
                            int score = width * 10 + (bonus(h, l) ? 25 : 0) + (int)(Hash.Of(x, z, rh) & 7)
                                        - (near < 16 ? 30 : 0) - drop;
                            if (score > bestScore) { bestScore = score; bestH = h; bestDir = dir; bestW = width; bestLen = drop; }
                            break;
                        }
                    }
                }
            if (bestH < 0) return false;
            ApplyRamp(bestH % W, bestH / W, bestDir, bestW, bestLen);
            return true;
        }

        bool RampFits(int x, int z, int dir, int width, int drop)
        {
            int px = Grid.DX4[(dir + 1) & 3], pz = Grid.DZ4[(dir + 1) & 3];
            int dx = Grid.DX4[dir], dz = Grid.DZ4[dir];
            int ht = map.Height[z * W + x], hl = ht - drop;
            // 高处一侧：含两侧各 1 格，共 width + 2 格笔直等高
            for (int j = -1; j <= width; j++)
            {
                int cx = x + px * j, cz = z + pz * j;
                if (!map.InBounds(cx, cz)) return false;
                int c = cz * W + cx;
                if (map.Height[c] != ht || map.HasWater(c)) return false;
                if ((map.Flags[c] & (CellFlags.Ramp | CellFlags.Spire)) != 0) return false;
            }
            // 低处一侧：drop 格坡道 + 1 格落脚，全部与低处等高
            for (int j = 0; j < width; j++)
                for (int k = 1; k <= drop + 1; k++)
                {
                    int cx = x + px * j + dx * k, cz = z + pz * j + dz * k;
                    if (!map.InBounds(cx, cz)) return false;
                    int c = cz * W + cx;
                    if (map.Height[c] != hl || map.HasWater(c) || map.Flags[c] != CellFlags.None) return false;
                    if (hiddenCache[c]) return false;
                }
            return true;
        }

        void ApplyRamp(int x, int z, int dir, int width, int drop)
        {
            int px = Grid.DX4[(dir + 1) & 3], pz = Grid.DZ4[(dir + 1) & 3];
            int dx = Grid.DX4[dir], dz = Grid.DZ4[dir];
            int ht = map.Height[z * W + x];
            int up = (dir + 2) & 3;
            for (int j = 0; j < width; j++)
                for (int k = 1; k <= drop; k++)
                {
                    int c = (z + pz * j + dz * k) * W + (x + px * j + dx * k);
                    map.Height[c] = ht - k;
                    map.Slope[c] = (sbyte)up;
                    map.Flags[c] |= CellFlags.Ramp;
                }
            map.Ramps.Add(new RampInfo { TopX = x, TopZ = z, DownDir = dir, Width = width, Length = drop });
        }

        int NearestRamp(int x, int z)
        {
            int best = int.MaxValue;
            foreach (var r in map.Ramps)
                best = Math.Min(best, Math.Max(Math.Abs(r.TopX - x), Math.Abs(r.TopZ - z)));
            return best;
        }

        /// <summary>按 Biome 的“每块台地的坡道”数补足坡道。</summary>
        void ExtraRamps()
        {
            int n = W * H;
            var lab = Grid.Label(W, H, i => map.Tier[i] > 0 && (map.Flags[i] & (CellFlags.Mesa | CellFlags.Spire)) == 0,
                (a, b) => map.Tier[a] == map.Tier[b], out var sizes);
            var sumX = new long[sizes.Count];
            var sumZ = new long[sizes.Count];
            for (int i = 0; i < n; i++) if (lab[i] >= 0) { sumX[lab[i]] += i % W; sumZ[lab[i]] += i / W; }
            int added = 0;
            for (int c = 0; c < sizes.Count; c++)
            {
                if (sizes[c] < 150) continue;
                int cx = (int)(sumX[c] / sizes[c]), cz = (int)(sumZ[c] / sizes[c]);
                double want = Blend(cz * W + cx, p => p.RampsPerTerrace);
                int desired = (int)Math.Floor(want + Hash.Unit(Hash.Of(seed, (uint)c * 7919U + 3)));
                int have = 0;
                foreach (var r in map.Ramps) if (lab[r.TopZ * W + r.TopX] == c) have++;
                int cc = c;
                for (; have < desired; have++)
                {
                    if (!TryPlaceRamp((h, l) => lab[h] == cc && lab[l] != cc, (h, l) => false, true)) break;
                    added++;
                }
            }
            map.Stats["rampsExtra"] = added;
        }

        // ------------------------------------------------------------------
        // 地表过渡：只有一个方向高 1 层时放斜坡（悬崖背面那一条不放）
        // ------------------------------------------------------------------
        void AssignSlopes()
        {
            var hidden = ComputeHidden();
            int count = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (map.Slope[i] >= 0 || map.HasWater(i) || hidden[i]) continue;
                    if ((map.Flags[i] & (CellFlags.Mesa | CellFlags.Spire)) != 0) continue;
                    int h = map.Height[i];
                    int upDir = -1, ups = 0;
                    bool bad = false;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (!map.InBounds(nx, nz)) continue;
                        int j = nz * W + nx;
                        int nh = map.Height[j];
                        if (nh == h + 1 && !map.HasWater(j)) { ups++; upDir = d; }
                        else if (nh > h + 1) bad = true;
                    }
                    if (bad || ups != 1) continue;
                    int bx = x - Grid.DX4[upDir], bz = z - Grid.DZ4[upDir];
                    if (map.InBounds(bx, bz) && map.Height[bz * W + bx] > h) continue;
                    map.Slope[i] = (sbyte)upDir;
                    count++;
                }
            map.Stats["slopes"] = count;
        }

        void MarkHiddenAndEdges()
        {
            var hidden = ComputeHidden();
            int hiddenCount = 0;
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (hidden[i]) { map.Flags[i] |= CellFlags.Hidden; hiddenCount++; }
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (!map.InBounds(nx, nz)) continue;
                        if (Math.Abs(map.Height[nz * W + nx] - map.Height[i]) >= 2) { map.Flags[i] |= CellFlags.CliffEdge; break; }
                    }
                }
            map.Stats["hiddenCells"] = hiddenCount;
        }

        void ChooseSpawn()
        {
            var lab = LabelWalk(out var sizes);
            int main = 0;
            for (int c = 1; c < sizes.Count; c++) if (sizes[c] > sizes[main]) main = c;
            int unreachable = 0;
            for (int i = 0; i < lab.Length; i++)
            {
                if (lab[i] == main || map.HasWater(i)) continue;
                if ((map.Flags[i] & (CellFlags.Mesa | CellFlags.Spire)) != 0) continue;
                map.Flags[i] |= CellFlags.Unreachable;
                unreachable++;
            }
            map.Stats["unreachableCells"] = unreachable;

            double best = double.NegativeInfinity;
            var human = map.Weights[(int)BiomeId.Human];
            for (int z = 8; z < H - 8; z++)
                for (int x = 8; x < W - 8; x++)
                {
                    int i = z * W + x;
                    if (lab[i] != main || map.HasWater(i) || map.Slope[i] >= 0 || map.Flags[i] != CellFlags.None) continue;
                    double score = human[i] * 100 - Dist(x, z, W * 0.55, H * 0.35) * 0.05;
                    if (score > best) { best = score; map.SpawnX = x; map.SpawnZ = z; }
                }
        }

        void CollectStats()
        {
            map.HeightRange(out int min, out int max);
            map.Stats["minHeight"] = min;
            map.Stats["maxHeight"] = max;
            map.Stats["ramps"] = map.Ramps.Count;
            map.Stats["drops"] = map.Drops.Count;
            int falls = 0;
            foreach (var d in map.Drops) if (d.Drop >= 2) falls++;
            map.Stats["waterfalls"] = falls;
            for (int k = 0; k < MaxTiers; k++)
            {
                int c = 0;
                foreach (bool b in tiers[k]) if (b) c++;
                map.Stats["tier" + (k + 1) + "Cells"] = c;
            }
        }
    }

    /// <summary>简单二叉堆（netstandard2.1 没有 PriorityQueue）。</summary>
    sealed class MinHeap
    {
        int[] items;
        double[] keys;
        public int Count { get; private set; }

        public MinHeap(int capacity) { items = new int[Math.Max(16, capacity)]; keys = new double[items.Length]; }

        public void Push(int item, double key)
        {
            if (Count == items.Length) { Array.Resize(ref items, Count * 2); Array.Resize(ref keys, Count * 2); }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (keys[p] <= key) break;
                items[i] = items[p]; keys[i] = keys[p]; i = p;
            }
            items[i] = item; keys[i] = key;
        }

        public int Pop(out double key)
        {
            int top = items[0]; key = keys[0];
            int last = items[--Count]; double lk = keys[Count];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= Count) break;
                if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                if (keys[c] >= lk) break;
                items[i] = items[c]; keys[i] = keys[c]; i = c;
            }
            items[i] = last; keys[i] = lk;
            return top;
        }
    }
}
