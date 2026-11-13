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
        /// <summary>每顶点 4 个数：种类（1 顶面 / 2 正面 / 4 侧面 / 0 投影面）、顶面露边掩码或侧面朝向、崖顶高、崖脚高。</summary>
        public readonly List<float> Data1 = new List<float>();
        /// <summary>每顶点 4 个数：顶面 = 格的世界 x、z；正面 = 折线的世界 z；侧面 = 折线的世界 x、这一列正面的世界 z。</summary>
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
        const int Sub = 4;                    // 鼓起侧面每格沿边细分
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
                    int hn = HeightAt(map, x, z + 1), he = HeightAt(map, x + 1, z), hs = HeightAt(map, x, z - 1), hw = HeightAt(map, x - 1, z);
                    int mask = (hn < h ? 1 : 0) | (he < h ? 2 : 0) | (hs < h ? 4 : 0) | (hw < h ? 8 : 0);
                    // 内拐角：四邻都不低、只有斜对角更低（东北 16、西北 32、东南 64、西南 128）。着色器按到角点的距离把顶岩边绕过去
                    if (hn >= h && he >= h && HeightAt(map, x + 1, z + 1) < h) mask |= 16;
                    if (hn >= h && hw >= h && HeightAt(map, x - 1, z + 1) < h) mask |= 32;
                    if (hs >= h && he >= h && HeightAt(map, x + 1, z - 1) < h) mask |= 64;
                    if (hs >= h && hw >= h && HeightAt(map, x - 1, z - 1) < h) mask |= 128;

                    // 顶面：靠露边的格一格一块（着色器按这一格的掩码算倒角、泥土内收）；四周同高的格留给后面合并成大矩形
                    if (mask != 0)
                    {
                        var top = m.Group(set, (mask & 1) != 0 ? ProcCliffMesh.Part.TopClip : ProcCliffMesh.Part.Top);
                        Quad(m, top,
                            lx, h, lz, lx, h, lz + 1, lx + 1, h, lz + 1, lx + 1, h, lz,
                            0, 1, 0, 1, 0, 0, -1,
                            1, mask, h, 0, x, z);
                    }
                    else interior[lz * size + lx] = true;

                    bool front = hs < h;
                    if (front)
                    {
                        // 正面：平面 z = lz；东西两头露边时端点跟着侧面鼓出量走
                        var wall = m.Group(set, ProcCliffMesh.Part.Wall);
                        int rows = Math.Max(1, (int)Math.Round((h - hs) / Row));
                        for (int r = 0; r < rows; r++)
                        {
                            double y0 = hs + (h - hs) * (double)r / rows, y1 = hs + (h - hs) * (double)(r + 1) / rows;
                            double xl0 = lx - Out(map, x, z, -1, y0, h), xl1 = lx - Out(map, x, z, -1, y1, h);
                            double xr0 = lx + 1 + Out(map, x, z, 1, y0, h), xr1 = lx + 1 + Out(map, x, z, 1, y1, h);
                            Quad(m, wall,
                                xl0, y0, lz, xl1, y1, lz, xr1, y1, lz, xr0, y0, lz,
                                0, 0, -1, 1, 0, 0, -1,
                                2, 0, h, hs, oz + lz, 0);
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
                                // 北边露边（顶面北边会被透明裁剪往里收）：鼓起在这一列的北头最后 0.35m 收为 0，免得侧面伸出裁剪后的轮廓
                                double ta = hn < h ? NorthTaper(z + 1 - wz0) : 1, tb = hn < h ? NorthTaper(z + 1 - wz1) : 1;
                                double a0 = edge + side * Bulge(side, y0, wz0, h) * ta, a1 = edge + side * Bulge(side, y1, wz0, h) * ta;
                                double b0 = edge + side * Bulge(side, y0, wz1, h) * tb, b1 = edge + side * Bulge(side, y1, wz1, h) * tb;
                                // 朝外看：东面右手边是 +z，西面右手边是 −z（Unity 顺时针为正面）
                                if (side > 0)
                                    QuadAuto(m, wall, a0, y0, z0, a1, y1, z0, b1, y1, z1, b0, y0, z1, side, 4, side, h, hb, ox + edge, fz);
                                else
                                    QuadAuto(m, wall, b0, y0, z1, b1, y1, z1, a1, y1, z0, a0, y0, z0, side, 4, side, h, hb, ox + edge, fz);
                            }
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
                    Quad(m, m.Group(set, ProcCliffMesh.Part.Top),
                        lx, h, lz, lx, h, lz + d, lx + w, h, lz + d, lx + w, h, lz,
                        0, 1, 0, 1, 0, 0, -1,
                        1, 0, h, 0, ox + lx, oz + lz);
                }
        }

        /// <summary>从 (x, z) 往南找到这一列不再同高的地方：正面所在的世界 z。</summary>
        static double FrontZ(RegionMap map, int x, int z, int h)
        {
            int k = z;
            while (k > 0 && HeightAt(map, x, k - 1) >= h) k--;
            return k;
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
            int hn = HeightAt(map, x + side, z);
            if (hn >= y - 1e-6 || hn >= h) return 0;
            return Bulge(side, y, z, h);
        }

        /// <summary>侧面鼓出量（m）：世界坐标值噪声，相邻面自动连续；崖顶 h 处为 0，往下线性起步（顶上约 45° 朝上）再变缓。</summary>
        static double NorthTaper(double distToNorthEdge)
        {
            double t = distToNorthEdge / 0.35;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return t * t * (3 - 2 * t);
        }

        public static double Bulge(int side, double y, double z, int h)
        {
            double t = (h - y) / 0.35;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            t = t * (2 - t);
            return (Bul0 + (Bul1 - Bul0) * ValueNoise2(y, z, side > 0 ? SeedE : SeedW, 0.4)) * t;
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

        /// <summary>加一个四边形（a→b→c→d 为正面顺时针），法线 / 切线给定，四个顶点同一组数据。</summary>
        static void Quad(ProcCliffMesh m, List<int> idx,
            double ax, double ay, double az, double bx, double by, double bz,
            double cx, double cy, double cz, double dx, double dy, double dz,
            double nx, double ny, double nz, double tx, double ty, double tz, double tw,
            double k, double d1, double d2, double d3, double e0, double e1)
        {
            int b = m.VertexCount;
            Vert(m, ax, ay, az); Vert(m, bx, by, bz); Vert(m, cx, cy, cz); Vert(m, dx, dy, dz);
            for (int i = 0; i < 4; i++)
            {
                m.Normals.Add((float)nx); m.Normals.Add((float)ny); m.Normals.Add((float)nz);
                m.Tangents.Add((float)tx); m.Tangents.Add((float)ty); m.Tangents.Add((float)tz); m.Tangents.Add((float)tw);
                m.Data1.Add((float)k); m.Data1.Add((float)d1); m.Data1.Add((float)d2); m.Data1.Add((float)d3);
                m.Data2.Add((float)e0); m.Data2.Add((float)e1); m.Data2.Add(0); m.Data2.Add(0);
            }
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            idx.Add(b); idx.Add(b + 2); idx.Add(b + 3);
        }

        /// <summary>侧面四边形：法线按几何算（朝外 side 一侧），切线沿 z。</summary>
        static void QuadAuto(ProcCliffMesh m, List<int> idx,
            double ax, double ay, double az, double bx, double by, double bz,
            double cx, double cy, double cz, double dx, double dy, double dz,
            int side, double k, double d1, double d2, double d3, double edgeWorld, double frontZ)
        {
            // 法线 = (b − a) × (d − a) 的反向（Unity 左手、顺时针为正面），保证朝外
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = dx - ax, vy = dy - ay, vz = dz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) { nx = side; ny = 0; nz = 0; len = 1; }
            nx /= len; ny /= len; nz /= len;
            if (nx * side < 0) { nx = -nx; ny = -ny; nz = -nz; }
            Quad(m, idx, ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz, nx, ny, nz, 0, 0, side, 1, k, d1, d2, d3, edgeWorld, frontZ);
        }

        static void Vert(ProcCliffMesh m, double x, double y, double z)
        {
            m.Positions.Add((float)x); m.Positions.Add((float)y); m.Positions.Add((float)z);
        }
    }
}
