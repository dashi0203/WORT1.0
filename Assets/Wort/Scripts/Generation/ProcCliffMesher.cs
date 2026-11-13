using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>程序化崖壁的网格数据（chunk 局部坐标，米）。子网格按（套件, 部分）分组。</summary>
    public sealed class ProcCliffMesh
    {
        public enum Part { Top = 0, TopClip = 1, Wall = 2, Shadow = 3 }
        public const int PartCount = 4;

        public readonly List<float> Positions = new List<float>();
        public readonly List<float> Normals = new List<float>();
        public readonly List<float> Tangents = new List<float>();   // xyzw
        /// <summary>每顶点 4 个数：种类（1 顶面 / 2 正面 / 4 侧面 / 0 投影面）、顶面掩码（1–8 露边、16–128 内拐角、256–2048 更高的邻居、4096–8192 北沿东／西内角收边）或侧面朝向、崖顶高、崖脚高。</summary>
        public readonly List<float> Data1 = new List<float>();
        /// <summary>每顶点 4 个数：顶面 = 格的世界 x、z；正面 = 折线的世界 z、这一段西头 x、东头 x、西邻高 × 1024 + 东邻高；侧面 = 折线的世界 x、这一列正面的世界 z、崖脚那格的露边掩码。正面的崖脚格露边掩码放在 Data1.y。</summary>
        public readonly List<float> Data2 = new List<float>();
        /// <summary>键 = 套件编号 × PartCount + 部分。</summary>
        public readonly SortedDictionary<int, List<int>> Indices = new SortedDictionary<int, List<int>>();

        public int VertexCount => Positions.Count / 3;
        public int TriangleCount { get { int n = 0; foreach (var l in Indices.Values) n += l.Count / 3; return n; } }

        public List<int> Group(int set, Part part)
        {
            int key = set * PartCount + (int)part;
            if (!Indices.TryGetValue(key, out var l)) Indices[key] = l = new List<int>();
            return l;
        }
    }

    /// <summary>
    /// 程序化崖壁（负责人 2026-11-13，预览定形见 Tools/TilesetPreview/proc_cliff.py）：
    /// - 只生成镜头看得见的面：顶面（每格平的 1m 方块，不变形）和朝南的正面（平面）；
    /// - 只鼓“看得见的体素层”的东西侧面：这一列最上面那块（顶层）和南面露出的那块（前排）；鼓起在崖顶处收为 0，往下鼓出（人脚下方）；
    /// - 看不见的北 / 东 / 西竖面各生成一块平的“只投影”面（Part.Shadow），保证阴影正确；
    /// - 北面露边的格放进 TopClip（着色器透明裁剪北边轮廓），其余顶面放 Top；
    /// - 外观（顶岩 / 泥土内收、平滑倒角、墙顶融合、崖脚融合）全在着色器 Wort/ProcCliff 里，网格只提供位置和每顶点数据。
    /// 只影响外观，不改玩法和 checksum；碰撞照旧按体素格。纯 C#，噪声只用整数哈希和加减乘。
    /// </summary>
    public static class ProcCliffMesher
    {
        const double TopOut = 0.45;           // 崖顶处的鼓出量 = 最大鼓出量 × 0.45（约 3–14cm，沿边随噪声变化）
        const int Sub = 2;                    // 鼓起侧面每格沿边细分（负责人 2026-11-13：4 → 2）
        const double Bul0 = 0.06, Bul1 = 0.30;  // 侧面鼓出量（m）
        const double Row = 0.25;              // 竖向细分（m）
        const uint SeedE = 0x51A3u, SeedW = 0x7C21u;

        /// <param name="sets">每格的套件编号（行优先 z * W + x），null = 全部 0。</param>
        public static ProcCliffMesh Build(RegionMap map, byte[] sets, int cx, int cz)
        {
            var m = new ProcCliffMesh();
            int size = map.Settings.ChunkSize;
            int ox = cx * size, oz = cz * size;
            var interior = new bool[size * size];
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    int h = HeightAt(map, x, z);
                    int set = sets != null && sets.Length == map.W * map.H ? sets[z * map.W + x] : 0;
                    if (SlopeAt(map, x, z) >= 0) { EmitSlope(m, map, set, x, z, lx, lz, ox, oz); continue; }
                    // 邻格朝我这一边的高度（坡道：上坡那头算高一层，坡顶的台地看到坡道不生成墙）
                    int hn = EdgeH(map, x, z + 1, 2), he = EdgeH(map, x + 1, z, 3), hs = EdgeH(map, x, z - 1, 0), hw = EdgeH(map, x - 1, z, 1);
                    int mask = (hn < h ? 1 : 0) | (he < h ? 2 : 0) | (hs < h ? 4 : 0) | (hw < h ? 8 : 0);
                    // 内拐角：四邻都不低、只有斜对角更低（东北 16、西北 32、东南 64、西南 128）。着色器按到角点的距离把顶岩边绕过去
                    if (hn >= h && he >= h && HeightAt(map, x + 1, z + 1) < h) mask |= 16;
                    if (hn >= h && hw >= h && HeightAt(map, x - 1, z + 1) < h) mask |= 32;
                    if (hs >= h && he >= h && HeightAt(map, x + 1, z - 1) < h) mask |= 64;
                    if (hs >= h && hw >= h && HeightAt(map, x - 1, z - 1) < h) mask |= 128;
                    // 哪边邻居更高（北 256、东 512、南 1024、西 2048）：崖脚前面的地面往外倾（内凹圆角的地面一侧）
                    mask |= (hn > h ? 256 : 0) | (he > h ? 512 : 0) | (hs > h ? 1024 : 0) | (hw > h ? 2048 : 0);

                    // 北沿只在接回未裁剪的内拐角处收边，连续的北沿共用同一条噪声。
                    if (hn < h && he >= h && EdgeH(map, x + 1, z + 1, 2) >= h) mask |= 4096;
                    if (hn < h && hw >= h && EdgeH(map, x - 1, z + 1, 2) >= h) mask |= 8192;

                    // 顶面：靠露边的格一格一块（着色器按这一格的掩码算倒角、泥土内收）；四周同高的格留给后面合并成大矩形
                    if (mask != 0)
                    {
                        var top = m.Group(set, ProcCliffMesh.Part.Top);   // 北边不再裁剪（改成往外伸），TopClip 不再使用
                        // 东西边和邻格有高低差时（那边有侧面 / 顶面窄条，顶点在半格），这条边加中点，避免 T 形接缝
                        bool midW = hw != h, midE = he != h;
                        var ring = new List<(double, double)> { (lx, lz) };
                        if (midW) ring.Add((lx, lz + 0.5));
                        ring.Add((lx, lz + 1));
                        if (hn < h) ring.Add((lx + 0.5, lz + 1));   // 北边往外伸的窄条顶点在半格
                        ring.Add((lx + 1, lz + 1));
                        if (midE) ring.Add((lx + 1, lz + 0.5));
                        ring.Add((lx + 1, lz));
                        TopFan(m, top, ring, lx + 0.5, lz + 0.5, h, mask, x, z);
                    }
                    else interior[lz * size + lx] = true;

                    bool front = hs < h;
                    // 正面：同高、同崖脚、同套件的一排连成一段（只在段的西头生成），东西两头露边时端点跟着侧面鼓出量走
                    // 崖脚前面那格的露边（1 北 2 东 4 南 8 西）：着色器据此让崖脚融合到“地面实际的样子”（窄脊上是顶岩，不是泥土）
                    int footMask = ExposedMask(map, x, z - 1);
                    bool SameRun(int xx) => xx >= ox && xx < ox + size && SlopeAt(map, xx, z) < 0 && (footMask & 10) == 0 && (ExposedMask(map, xx, z - 1) & 10) == 0 &&
                                            HeightAt(map, xx, z) == h && EdgeH(map, xx, z - 1, 0) == hs &&
                                            (sets == null || sets.Length != map.W * map.H || sets[z * map.W + xx] == set);
                    if (front && !SameRun(x - 1))
                    {
                        int xe = x;
                        while (SameRun(xe + 1)) xe++;
                        int hwS = EdgeH(map, x - 1, z, 1), heE = EdgeH(map, xe + 1, z, 3);
                        var wall = m.Group(set, ProcCliffMesh.Part.Wall);
                        int rows = Math.Max(1, (int)Math.Round((h - hs) / Row));
                        double l0 = lx, r1 = xe - ox + 1;
                        for (int r = 0; r < rows; r++)
                        {
                            double y0 = hs + (h - hs) * (double)r / rows, y1 = hs + (h - hs) * (double)(r + 1) / rows;
                            double ol0 = Out(map, x, z, -1, y0, h), ol1 = Out(map, x, z, -1, y1, h);
                            double or0 = Out(map, xe, z, 1, y0, h), or1 = Out(map, xe, z, 1, y1, h);
                            // 列：西头鼓出部分、每格一列（整数 x，和顶面 / 地面的格顶点对上）、东头鼓出部分
                            // Data2：折线 z、这一段的西头 x、东头 x、西邻高 × 1024 + 东邻高（着色器做竖直转角的平滑法线）
                            void Col(double a0, double a1, double b0, double b1) =>
                                Quad(m, wall, a0, y0, lz, a1, y1, lz, b1, y1, lz, b0, y0, lz,
                                    0, 0, -1, 1, 0, 0, -1,
                                    2, footMask, h, hs, oz + lz, x, xe + 1, hwS * 1024 + heE);
                            if (ol0 > 1e-6 || ol1 > 1e-6) Col(l0 - ol0, l0 - ol1, l0, l0);
                            for (double cxl = l0; cxl < r1 - 1e-6; cxl += 1) Col(cxl, cxl, cxl + 1, cxl + 1);
                            if (or0 > 1e-6 || or1 > 1e-6) Col(r1, r1, r1 + or0, r1 + or1);
                        }
                    }

                    // 东 / 西侧面：前排整个高度、顶层最上 1m；被挡住的不生成
                    for (int side = -1; side <= 1; side += 2)
                    {
                        int hb = side > 0 ? he : hw;
                        if (hb >= h) continue;
                        double lo = front ? hb : Math.Max(hb, h - 1);
                        int rows = Math.Max(1, (int)Math.Round((h - lo) / Row));
                        double edge = side > 0 ? lx + 1 : lx;
                        var wall = m.Group(set, ProcCliffMesh.Part.Wall);
                        double fz = FrontZ(map, x, z, h);   // 这一列正面所在的世界 z：侧面的崖壁贴图从正面转角接着铺
                        for (int r = 0; r < rows; r++)
                        {
                            double y0 = lo + (h - lo) * (double)r / rows, y1 = lo + (h - lo) * (double)(r + 1) / rows;
                            for (int c = 0; c < Sub; c++)
                            {
                                double z0 = lz + (double)c / Sub, z1 = lz + (double)(c + 1) / Sub;
                                double wz0 = oz + z0, wz1 = oz + z1;
                                double a0 = edge + side * SideOut(map, x, z, side, y0, wz0, h, hb);
                                double a1 = edge + side * SideOut(map, x, z, side, y1, wz0, h, hb);
                                double b0 = edge + side * SideOut(map, x, z, side, y0, wz1, h, hb);
                                double b1 = edge + side * SideOut(map, x, z, side, y1, wz1, h, hb);
                                // 朝外看：东面右手边是 +z，西面右手边是 −z（Unity 顺时针为正面）
                                if (side > 0)
                                    QuadAuto(m, wall, a0, y0, z0, a1, y1, z0, b1, y1, z1, b0, y0, z1, side, 4, side, h, hb, ox + edge, fz, ExposedMask(map, x + side, z));
                                else
                                    QuadAuto(m, wall, b0, y0, z1, b1, y1, z1, a1, y1, z0, a0, y0, z0, side, 4, side, h, hb, ox + edge, fz, ExposedMask(map, x + side, z));
                            }
                            if (r == rows - 1)
                            {
                                // 顶面往外接一条同高的窄条，到侧面顶边（鼓出后的位置）：顶面本身不变形，轮廓跟着鼓包走
                                var topG = m.Group(set, ProcCliffMesh.Part.Top);
                                for (int c = 0; c < Sub; c++)
                                {
                                    double z0 = lz + (double)c / Sub, z1 = lz + (double)(c + 1) / Sub;
                                    double o0 = SideOut(map, x, z, side, h, oz + z0, h, hb), o1 = SideOut(map, x, z, side, h, oz + z1, h, hb);
                                    if (o0 < 1e-4 && o1 < 1e-4) continue;
                                    if (side > 0)
                                        Quad(m, topG, edge, h, z0, edge, h, z1, edge + o1, h, z1, edge + o0, h, z0, 0, 1, 0, 1, 0, 0, -1, 1, mask, h, 0, x, z);
                                    else
                                        Quad(m, topG, edge - o0, h, z0, edge - o1, h, z1, edge, h, z1, edge, h, z0, 0, 1, 0, 1, 0, 0, -1, 1, mask, h, 0, x, z);
                                }
                            }
                        }
                    }

                    // 北边露边：顶面往北伸出一条同高的窄条（负责人 2026-11-13：咬进去的假边缘和侧面鼓包对不上，改成往外伸），东西也露边时补角
                    if (hn < h)
                    {
                        var topN = m.Group(set, ProcCliffMesh.Part.Top);
                        for (int c = 0; c < Sub; c++)
                        {
                            double x0 = lx + (double)c / Sub, x1 = lx + (double)(c + 1) / Sub;
                            double o0 = NorthOut(map, x, z, h, ox + x0), o1 = NorthOut(map, x, z, h, ox + x1);
                            if (o0 < 1e-4 && o1 < 1e-4) continue;
                            Quad(m, topN, x0, h, lz + 1, x0, h, lz + 1 + o0, x1, h, lz + 1 + o1, x1, h, lz + 1, 0, 1, 0, 1, 0, 0, -1, 1, mask, h, 0, x, z);
                        }
                        for (int side = -1; side <= 1; side += 2)
                        {
                            int hb = side > 0 ? he : hw;
                            if (hb >= h) continue;
                            double edge = side > 0 ? lx + 1 : lx;
                            double oS = SideOut(map, x, z, side, h, oz + lz + 1, h, hb), oN = NorthOut(map, x, z, h, ox + edge);
                            if (oS < 1e-4 || oN < 1e-4) continue;
                            if (side > 0)
                                Quad(m, topN, edge, h, lz + 1, edge, h, lz + 1 + oN, edge + oS, h, lz + 1 + oN, edge + oS, h, lz + 1, 0, 1, 0, 1, 0, 0, -1, 1, mask, h, 0, x, z);
                            else
                                Quad(m, topN, edge - oS, h, lz + 1, edge - oS, h, lz + 1 + oN, edge, h, lz + 1 + oN, edge, h, lz + 1, 0, 1, 0, 1, 0, 0, -1, 1, mask, h, 0, x, z);
                        }
                    }

                    // 只投影的面：北 / 东 / 西露边各一块平的竖面（看不见，只进阴影贴图）
                    var sh = m.Group(set, ProcCliffMesh.Part.Shadow);
                    if (hn < h) Quad(m, sh, lx + 1, hn, lz + 1, lx + 1, h, lz + 1, lx, h, lz + 1, lx, hn, lz + 1, 0, 0, 1, -1, 0, 0, 1, 0, 0, h, hn, 0, 0);
                    if (he < h) Quad(m, sh, lx + 1, he, lz, lx + 1, h, lz, lx + 1, h, lz + 1, lx + 1, he, lz + 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, h, he, 0, 0);
                    if (hw < h) Quad(m, sh, lx, hw, lz + 1, lx, h, lz + 1, lx, h, lz, lx, hw, lz, -1, 0, 0, 0, 0, -1, 1, 0, 0, h, hw, 0, 0);
                }
            MergeInterior(m, map, sets, ox, oz, size, interior);
            return m;
        }

        /// <summary>
        /// 四周同高的顶面格（没有露边）按“同高、同套件”贪心合并成尽量大的矩形（先沿 x 延伸，再整行沿 z 延伸）。
        /// 这些格在着色器里只有泥土（离露边远），合并不影响外观；平地从每格 2 个三角形降到每块矩形 2 个。
        /// </summary>
        static void MergeInterior(ProcCliffMesh m, RegionMap map, byte[] sets, int ox, int oz, int size, bool[] interior)
        {
            bool setsOk = sets != null && sets.Length == map.W * map.H;
            int Key(int lx, int lz) => HeightAt(map, ox + lx, oz + lz) * 256 + (setsOk ? sets[(oz + lz) * map.W + ox + lx] : 0);
            var used = new bool[size * size];
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int i = lz * size + lx;
                    if (!interior[i] || used[i]) continue;
                    int key = Key(lx, lz);
                    int w = 1;
                    while (lx + w < size && interior[i + w] && !used[i + w] && Key(lx + w, lz) == key) w++;
                    int d = 1;
                    for (; lz + d < size; d++)
                    {
                        bool ok = true;
                        for (int k = 0; k < w && ok; k++)
                        {
                            int j = (lz + d) * size + lx + k;
                            ok = interior[j] && !used[j] && Key(lx + k, lz + d) == key;
                        }
                        if (!ok) break;
                    }
                    for (int a = 0; a < d; a++)
                        for (int k = 0; k < w; k++) used[(lz + a) * size + lx + k] = true;
                    int h = key / 256, set = key % 256;
                    // 边上每 1m 一个顶点（中心扇形），和旁边一格一块的顶点对上，没有 T 形接缝（之前外扩 2mm 会和旁边的格重叠闪烁）
                    var ring = new List<(double, double)>();
                    for (int a = 0; a < d; a++) ring.Add((lx, lz + a));
                    for (int a = 0; a < w; a++) ring.Add((lx + a, lz + d));
                    for (int a = d; a > 0; a--) ring.Add((lx + w, lz + a));
                    for (int a = w; a > 0; a--) ring.Add((lx + a, lz));
                    TopFan(m, m.Group(set, ProcCliffMesh.Part.Top), ring, lx + w * 0.5, lz + d * 0.5, h, 0, ox + lx, oz + lz);
                }
        }

        /// <summary>从 (x, z) 往南找到这一列不再同高的地方：正面所在的世界 z。</summary>
        static double FrontZ(RegionMap map, int x, int z, int h)
        {
            int k = z;
            while (k > 0 && HeightAt(map, x, k - 1) >= h) k--;
            return k;
        }

        /// <summary>格 (x, z) 的露边掩码（1 北 2 东 4 南 8 西）。坡道格当作没有露边。</summary>
        static int ExposedMask(RegionMap map, int x, int z)
        {
            if (SlopeAt(map, x, z) >= 0) return 0;
            int h = HeightAt(map, x, z);
            return (EdgeH(map, x, z + 1, 2) < h ? 1 : 0) | (EdgeH(map, x + 1, z, 3) < h ? 2 : 0) |
                   (EdgeH(map, x, z - 1, 0) < h ? 4 : 0) | (EdgeH(map, x - 1, z, 1) < h ? 8 : 0);
        }

        static int SlopeAt(RegionMap map, int x, int z)
        {
            if (map.Slope == null || x < 0 || z < 0 || x >= map.W || z >= map.H) return -1;
            return map.Slope[z * map.W + x];
        }

        /// <summary>格 (x, z) 朝 dir（0 北 1 东 2 南 3 西）那条边的高度：坡道的上坡那头 = 高度 + 1，其余 = 高度。</summary>
        static int EdgeH(RegionMap map, int x, int z, int dir)
        {
            int h = HeightAt(map, x, z), up = SlopeAt(map, x, z);
            return up >= 0 && dir == up ? h + 1 : h;
        }

        /// <summary>
        /// 坡道格（斜坡体素在 y = 高度，往上坡方向升 1 层）：一块倾斜的坡面（顶面，着色器按几何法线倾斜）；
        /// 四边里旁边更低的地方补墙（顶边顺着坡斜着走，每顶点带自己的崖顶高）：朝南 = 正面，朝东西 = 侧面，朝北 = 只投影。
        /// </summary>
        static void EmitSlope(ProcCliffMesh m, RegionMap map, int set, int x, int z, int lx, int lz, int ox, int oz)
        {
            int h = HeightAt(map, x, z), up = SlopeAt(map, x, z);
            int ux = Grid.DX4[up], uz = Grid.DZ4[up];
            double Y(double cx, double cz) => h + 0.5 + (cx - 0.5) * ux + (cz - 0.5) * uz;   // 角点 (0/1, 0/1) 的坡面高度
            double nl = Math.Sqrt(ux * ux + 1 + uz * uz);
            var top = m.Group(set, ProcCliffMesh.Part.Top);
            // 坡面的露边（1 北 2 东 4 南 8 西）：旁边比坡面这条边低就算露边，着色器照常画顶岩边和倒角（负责人 2026-11-13：斜坡没有顶岩石边）
            int sm = 0;
            for (int d = 0; d < 4; d++)
            {
                int nb = EdgeH(map, x + Grid.DX4[d], z + Grid.DZ4[d], (d + 2) & 3);
                int edgeTop = d == ((up + 2) & 3) ? h : h + 1;   // 坡底那条边高 h，其余（坡顶、两侧的高端）到 h + 1
                if (nb < edgeTop && !(d == up && nb >= h + 1)) sm |= d == 0 ? 1 : d == 1 ? 2 : d == 2 ? 4 : 8;
            }
            Quad(m, top,
                lx, Y(0, 0), lz, lx, Y(0, 1), lz + 1, lx + 1, Y(1, 1), lz + 1, lx + 1, Y(1, 0), lz,
                -ux / nl, 1 / nl, -uz / nl, 1, 0, 0, -1,
                1, sm, h + 1, 0, x, z);
            const int NoCorner = 1023 * 1024 + 1023;
            for (int d = 0; d < 4; d++)
            {
                int hb = EdgeH(map, x + Grid.DX4[d], z + Grid.DZ4[d], (d + 2) & 3);
                // 这条边两个端点（从外面看左、右）的局部角点
                double c0x, c0z, c1x, c1z;
                switch (d)
                {
                    case 0: c0x = 1; c0z = 1; c1x = 0; c1z = 1; break;   // 北：从北看右手是 −x
                    case 1: c0x = 1; c0z = 0; c1x = 1; c1z = 1; break;   // 东
                    case 2: c0x = 0; c0z = 0; c1x = 1; c1z = 0; break;   // 南
                    default: c0x = 0; c0z = 1; c1x = 0; c1z = 0; break;  // 西
                }
                double t0 = Y(c0x, c0z), t1 = Y(c1x, c1z);
                if (t0 <= hb + 1e-6 && t1 <= hb + 1e-6) continue;
                double b0 = Math.Min(hb, t0), b1 = Math.Min(hb, t1);
                double ax = lx + c0x, az = lz + c0z, bx = lx + c1x, bz = lz + c1z;
                var grp = m.Group(set, d == 0 ? ProcCliffMesh.Part.Shadow : ProcCliffMesh.Part.Wall);
                double k = d == 2 ? 2 : d == 0 ? 0 : 4, sgn = d == 1 ? 1 : d == 3 ? -1 : 0;
                double nx = Grid.DX4[d], nz = Grid.DZ4[d];
                double e0 = d == 2 ? oz + lz : d == 1 ? ox + lx + 1 : d == 3 ? ox + lx : 0;
                double e1 = d == 2 ? x : z - 10, e2 = d == 2 ? x + 1 : 0, e3 = d == 2 ? NoCorner : 0;   // 坡道墙不做竖直转角平滑
                void V(double px, double py, double pz, double yt)
                {
                    Vert(m, px, py, pz);
                    m.Normals.Add((float)nx); m.Normals.Add(0); m.Normals.Add((float)nz);
                    m.Tangents.Add((float)-nz); m.Tangents.Add(0); m.Tangents.Add((float)nx); m.Tangents.Add(1);
                    m.Data1.Add((float)k); m.Data1.Add((float)sgn); m.Data1.Add((float)yt); m.Data1.Add(hb);
                    m.Data2.Add((float)e0); m.Data2.Add((float)e1); m.Data2.Add((float)e2); m.Data2.Add((float)e3);
                }
                // 按 0.25m 一行竖向细分（和旁边普通墙的行对上，没有 T 形接缝）
                int rowsS = Math.Max(1, (int)Math.Ceiling((Math.Max(t0, t1) - hb) / Row - 1e-6));
                for (int r = 0; r < rowsS; r++)
                {
                    double f0 = (double)r / rowsS, f1 = (double)(r + 1) / rowsS;
                    double ya0 = b0 + (t0 - b0) * f0, ya1 = b0 + (t0 - b0) * f1, yb0 = b1 + (t1 - b1) * f0, yb1 = b1 + (t1 - b1) * f1;
                    int vi = m.VertexCount;
                    V(ax, ya0, az, t0); V(ax, ya1, az, t0); V(bx, yb1, bz, t1); V(bx, yb0, bz, t1);
                    grp.Add(vi); grp.Add(vi + 1); grp.Add(vi + 2);
                    grp.Add(vi); grp.Add(vi + 2); grp.Add(vi + 3);
                }
            }
        }

        /// <summary>地图外当作和边上那格同高（不画边界墙，和 TilePlacer 一致）。</summary>
        static int HeightAt(RegionMap map, int x, int z)
        {
            x = x < 0 ? 0 : x >= map.W ? map.W - 1 : x;
            z = z < 0 ? 0 : z >= map.H ? map.H - 1 : z;
            return map.Height[z * map.W + x];
        }

        /// <summary>正面在东(1) / 西(-1) 头、高度 y 处往外伸多少：旁边这一格在这个高度没有体素时，跟着侧面鼓出量走。</summary>
        static double Out(RegionMap map, int x, int z, int side, double y, int h)
        {
            int hb = EdgeH(map, x + side, z, side > 0 ? 3 : 1);
            return SideOut(map, x, z, side, y, z, h, hb);
        }

        static double SideOut(RegionMap map, int x, int z, int side, double y, double wz, int h, int hb)
        {
            if (y <= hb || hb >= h) return 0;
            int sideEdge = side > 0 ? 3 : 1;
            // 南端那条线上、低于前一格顶面的部分，和前一格（更矮、同一侧也露出）的侧面是同一条线：用前一格的鼓出量，两边对上（不留三角缝）
            int hS = EdgeH(map, x, z - 1, 0);
            if (Math.Abs(wz - z) < 1e-9 && hS < h && hS > hb && y <= hS + 1e-9 && SlopeAt(map, x, z - 1) < 0)
            {
                int hbS = EdgeH(map, x + side, z - 1, sideEdge);
                if (hbS < hS) return SideOut(map, x, z - 1, side, y, wz, hS, hbS);
            }
            // 对角邻格在这层封住转角时，侧面在折点回到格线；正面端点也用同一个偏移。
            bool southClosed = EdgeH(map, x + side, z - 1, sideEdge) >= y;
            bool northClosed = EdgeH(map, x + side, z + 1, sideEdge) >= y;

            double taper = EdgeTaper(y - hb);
            // 南边露边（这一格有正面）：侧面鼓包在南端 0.35m 内收为 0，转角落回格线，正面不再往两边拉宽成同一平面的“挤出”（负责人 2026-11-13）
            bool southOpen = EdgeH(map, x, z - 1, 0) < h;
            if (southClosed || southOpen) taper *= EdgeTaper(wz - z);
            if (northClosed) taper *= EdgeTaper(z + 1 - wz);
            return Bulge(side, y, wz, h) * taper;
        }

        const uint SeedN = 0x3A91u;
        /// <summary>北边往外伸多少（世界 x 的函数，同一条北沿上相邻格连续）：和侧面崖顶处一样是最大鼓出量的 TopOut 倍；
        /// 东北 / 西北那格不低时（那里是它的侧墙，南端收为 0），这一端 0.35m 内收为 0，和那里的侧面对上。</summary>
        static double NorthOut(RegionMap map, int x, int z, int h, double wx)
        {
            double o = (Bul0 + (Bul1 - Bul0) * ValueNoise2(h, wx, SeedN, 0.4)) * TopOut;
            if (EdgeH(map, x + 1, z + 1, 2) >= h) o *= EdgeTaper(x + 1 - wx);   // 东北那格不低：那里是它的西侧墙（南端收为 0），北沿也收为 0
            if (EdgeH(map, x - 1, z + 1, 2) >= h) o *= EdgeTaper(wx - x);
            return o;
        }

        static double EdgeTaper(double distance)
        {
            double t = Math.Min(1, Math.Max(0, distance / 0.35));
            return t * t * (3 - 2 * t);
        }

        public static double Bulge(int side, double y, double z, int h)
        {
            double t = (h - y) / 0.35;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            t = t * (2 - t);
            // 崖顶处也鼓出一截（TopOut 比例），顶面东西边用一条同高的窄条接过去，从上往下看侧边轮廓是弯的（负责人 2026-11-13：顶上和原竖直面重合，侧边直线太明显）
            return (Bul0 + (Bul1 - Bul0) * ValueNoise2(y, z, side > 0 ? SeedE : SeedW, 0.4)) * (TopOut + (1 - TopOut) * t);
        }

        /// <summary>二维值噪声 [0,1)，格距 period（m），平滑插值。</summary>
        public static double ValueNoise2(double a, double b, uint seed, double period)
        {
            a /= period; b /= period;
            int i = (int)Math.Floor(a), j = (int)Math.Floor(b);
            double fa = a - i, fb = b - j;
            fa = fa * fa * (3 - 2 * fa); fb = fb * fb * (3 - 2 * fb);
            double v00 = Hash.Unit(Hash.Of(i, j, seed)), v10 = Hash.Unit(Hash.Of(i + 1, j, seed));
            double v01 = Hash.Unit(Hash.Of(i, j + 1, seed)), v11 = Hash.Unit(Hash.Of(i + 1, j + 1, seed));
            return (v00 * (1 - fa) + v10 * fa) * (1 - fb) + (v01 * (1 - fa) + v11 * fa) * fb;
        }

        /// <summary>顶面多边形：中心 + 一圈边界顶点（从上往下看顺时针），扇形三角化；所有顶点同一组数据。</summary>
        static void TopFan(ProcCliffMesh m, List<int> idx, List<(double x, double z)> ring, double cx, double cz, double h, int mask, double cellX, double cellZ)
        {
            int b = m.VertexCount;
            void V(double vx, double vz)
            {
                Vert(m, vx, h, vz);
                m.Normals.Add(0); m.Normals.Add(1); m.Normals.Add(0);
                m.Tangents.Add(1); m.Tangents.Add(0); m.Tangents.Add(0); m.Tangents.Add(-1);
                m.Data1.Add(1); m.Data1.Add(mask); m.Data1.Add((float)h); m.Data1.Add(0);
                m.Data2.Add((float)cellX); m.Data2.Add((float)cellZ); m.Data2.Add(0); m.Data2.Add(0);
            }
            V(cx, cz);
            foreach (var (vx, vz) in ring) V(vx, vz);
            int n = ring.Count;
            for (int i = 0; i < n; i++) { idx.Add(b); idx.Add(b + 1 + i); idx.Add(b + 1 + (i + 1) % n); }
        }

        /// <summary>加一个四边形（a→b→c→d 为正面顺时针），法线 / 切线给定，四个顶点同一组数据。</summary>
        static void Quad(ProcCliffMesh m, List<int> idx,
            double ax, double ay, double az, double bx, double by, double bz,
            double cx, double cy, double cz, double dx, double dy, double dz,
            double nx, double ny, double nz, double tx, double ty, double tz, double tw,
            double k, double d1, double d2, double d3, double e0, double e1, double e2 = 0, double e3 = 0)
        {
            int b = m.VertexCount;
            Vert(m, ax, ay, az); Vert(m, bx, by, bz); Vert(m, cx, cy, cz); Vert(m, dx, dy, dz);
            for (int i = 0; i < 4; i++)
            {
                m.Normals.Add((float)nx); m.Normals.Add((float)ny); m.Normals.Add((float)nz);
                m.Tangents.Add((float)tx); m.Tangents.Add((float)ty); m.Tangents.Add((float)tz); m.Tangents.Add((float)tw);
                m.Data1.Add((float)k); m.Data1.Add((float)d1); m.Data1.Add((float)d2); m.Data1.Add((float)d3);
                m.Data2.Add((float)e0); m.Data2.Add((float)e1); m.Data2.Add((float)e2); m.Data2.Add((float)e3);
            }
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
        }

        /// <summary>侧面四边形：法线按几何算（朝外 side 一侧），切线沿 z。</summary>
        static void QuadAuto(ProcCliffMesh m, List<int> idx,
            double ax, double ay, double az, double bx, double by, double bz,
            double cx, double cy, double cz, double dx, double dy, double dz,
            int side, double k, double d1, double d2, double d3, double edgeWorld, double frontZ, int footMask)
        {
            // 法线 = (b − a) × (d − a) 的反向（Unity 左手、顺时针为正面），保证朝外
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = dx - ax, vy = dy - ay, vz = dz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) { nx = side; ny = 0; nz = 0; len = 1; }
            nx /= len; ny /= len; nz /= len;
            if (nx * side < 0) { nx = -nx; ny = -ny; nz = -nz; }
            Quad(m, idx, ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz, nx, ny, nz, 0, 0, side, 1, k, d1, d2, d3, edgeWorld, frontZ, footMask);
        }

        static void Vert(ProcCliffMesh m, double x, double y, double z)
        {
            m.Positions.Add((float)x); m.Positions.Add((float)y); m.Positions.Add((float)z);
        }
    }
}
