using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// 用法：dotnet run -c Release -- [--seed N] [--out DIR]
    ///       dotnet run -c Release -- --layout ../../Assets/Wort/Data/Layout/Test8x8.json [--out DIR]（layout 管线，见 LayoutMode）
    /// 输出：biome.png / height.png（俯视）、shear_overview.png、shear_*.png（1080p 相机视野，orthoSize 9.5，shear 1）。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            uint seed = 20261109;
            string outDir = "out";
            string layout = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--seed") seed = uint.Parse(args[i + 1]);
                if (args[i] == "--out") outDir = args[i + 1];
                if (args[i] == "--layout") layout = args[i + 1];
            }
            if (layout != null) return LayoutMode.Run(layout, outDir);
            Directory.CreateDirectory(outDir);

            var sw = Stopwatch.StartNew();
            var settings = new RegionSettings { Seed = seed };
            var map = RegionGenerator.Generate(settings);
            long tA = sw.ElapsedMilliseconds;
            var voxels = new RegionVoxels(map);
            long tB = sw.ElapsedMilliseconds;
            var meshes = new List<(MeshData mesh, int ox, int oz)>();
            int tris = 0;
            for (int cz = 0; cz < settings.ChunksZ; cz++)
                for (int cx = 0; cx < settings.ChunksX; cx++)
                {
                    var m = ChunkMesher.Build(voxels, cx, cz);
                    tris += m.TriangleCount;
                    meshes.Add((m, cx * settings.ChunkSize, cz * settings.ChunkSize));
                }
            long tC = sw.ElapsedMilliseconds;

            Console.WriteLine($"seed {seed}: 阶段A {tA}ms, 体素化 {tB - tA}ms, 网格 {tC - tB}ms, 三角形 {tris}");
            foreach (var kv in map.Stats) Console.WriteLine($"  {kv.Key} = {kv.Value}");
            Console.WriteLine($"  checksum = 0x{map.Checksum():X8}");
            Console.WriteLine($"  spawn = ({map.SpawnX}, {map.SpawnZ}) h={map.Height[map.Index(map.SpawnX, map.SpawnZ)]}");

            Maps.Biome(map).Save(Path.Combine(outDir, "biome.png"));
            Maps.Height(map).Save(Path.Combine(outDir, "height.png"));

            var r = new ShearRenderer(map, meshes);
            r.Render(0, 0, map.W, map.H, 2.0, null).Save(Path.Combine(outDir, "shear_overview.png"));

            // 山地局部放大：以最大的山嘴为中心，220m 宽，5 px/m
            {
                // 选冲沟、孤峰、崖边最多的 220m 窗口
                int bestX = 0, bestScore = -1, minZ = map.H;
                for (int x0c = 0; x0c <= map.W - 220; x0c += 16)
                {
                    int sc = 0;
                    for (int z = 0; z < map.H; z++)
                        for (int x = x0c; x < x0c + 220; x++)
                        {
                            int i = map.Index(x, z);
                            if (map.Has(i, CellFlags.Spire)) sc += 4;
                            if (map.Has(i, CellFlags.Gully)) sc += 1;
                        }
                    if (sc > bestScore) { bestScore = sc; bestX = x0c; }
                }
                for (int x = bestX; x < bestX + 220; x++)
                    for (int z = 0; z < map.H; z++)
                        if (map.Biome[map.Index(x, z)] == (byte)BiomeId.Mountain) { minZ = Math.Min(minZ, z); break; }
                double x0 = bestX;
                double z0 = Math.Max(0, minZ - 50);
                r.Render(x0, z0, 220, Math.Min(map.H - z0, 200), 5.0, null).Save(Path.Combine(outDir, "shear_mountain_zoom.png"));
                Console.WriteLine($"  mountain zoom x {x0}..{x0 + 220}, z {z0}..");
            }

            // 1080p 相机视野：orthoSize 9.5 → 竖直 19m，1080px → 56.84 px/m
            double ppm = 1080 / 19.0;
            double viewW = 1920 / ppm, viewH = 19.0;
            foreach (var (name, x, z) in PickViews(map))
            {
                int h = map.Height[map.Index(x, z)];
                // 相机中心在角色所在地面：屏幕纵向坐标 v = z + y
                double v = z + 0.5 + h;
                var img = r.RenderView(x + 0.5 - viewW / 2, v - viewH / 2, viewW, viewH, ppm, (x, z));
                img.Save(Path.Combine(outDir, "shear_" + name + ".png"));
                Console.WriteLine($"  view {name} at ({x},{z}) h={h}");
            }
            return 0;
        }

        static IEnumerable<(string, int, int)> PickViews(RegionMap map)
        {
            // 人类区 / 混交林里各挑一条坡道，站在坡脚
            foreach (var (name, a, b) in new[] { ("1_human_ramp", BiomeId.Human, BiomeId.Human), ("2_forest_ramp", BiomeId.MixedForest, BiomeId.Conifer) })
            {
                int best = -1; double bestW = 0;
                for (int i = 0; i < map.Ramps.Count; i++)
                {
                    var rp = map.Ramps[i];
                    int c = map.Index(rp.TopX, rp.TopZ);
                    double w = map.Weights[(int)a][c] + (a != b ? map.Weights[(int)b][c] : 0);
                    if (w > bestW) { bestW = w; best = i; }
                }
                if (best < 0) continue;
                var rp2 = map.Ramps[best];
                int fx = rp2.TopX + Grid.DX4[rp2.DownDir] * (rp2.Length + 2);
                int fz = rp2.TopZ + Grid.DZ4[rp2.DownDir] * (rp2.Length + 2);
                yield return (name, Grid.Clamp(fx, 0, map.W - 1), Grid.Clamp(fz, 0, map.H - 1));
            }

            // 最大的瀑布落差点，站在下游
            int bd = -1;
            for (int i = 0; i < map.Drops.Count; i++) if (bd < 0 || map.Drops[i].Drop > map.Drops[bd].Drop) bd = i;
            if (bd >= 0)
            {
                var d = map.Drops[bd];
                int x = d.X + 3, z = d.Z - 4;
                yield return ("3_waterfall", Grid.Clamp(x, 0, map.W - 1), Grid.Clamp(z, 0, map.H - 1));
            }

            // 山地外缘大悬崖带：山地权重 0.5 附近、南侧的落脚点
            var mw = map.Weights[(int)BiomeId.Mountain];
            for (int z = 0; z < map.H - 1; z++)
                for (int x = map.W / 2; x < map.W; x++)
                {
                    int i = map.Index(x, z), n = map.Index(x, z + 1);
                    if (map.Height[n] - map.Height[i] >= 5 && mw[n] > 0.4 && !map.HasWater(i))
                    {
                        yield return ("4_mountain_edge", x, Math.Max(0, z - 5));
                        yield break;
                    }
                }
        }
    }

    static class Maps
    {
        public static readonly uint[] BiomeColor =
        {
            0xC9D86B, // 人类区
            0x4F8F3A, // 混交林
            0x2F5E4A, // 针叶林
            0x6B7A4A, // 沼泽
            0x9A8E80, // 山地
            0xA2875E, // 荒原
            0xE0D19A, // 海岸
        };

        public const int Scale = 2;

        public static Image Biome(RegionMap map)
        {
            var img = new Image(map.W * Scale, map.H * Scale);
            for (int z = 0; z < map.H; z++)
                for (int x = 0; x < map.W; x++)
                {
                    int i = map.Index(x, z);
                    double r = 0, g = 0, b = 0;
                    for (int k = 0; k < BiomeTable.Count; k++)
                    {
                        double w = map.Weights[k][i];
                        uint c = BiomeColor[k];
                        r += w * ((c >> 16) & 255); g += w * ((c >> 8) & 255); b += w * (c & 255);
                    }
                    uint col = (uint)((int)r << 16 | (int)g << 8 | (int)b);
                    if (map.HasWater(i)) col = Image.Mix(col, 0x3F7FB0, 0.75);
                    Fill(img, x, z, map.H, col);
                }
            ChunkGrid(img, map);
            Spawn(img, map);
            return img;
        }

        public static Image Height(RegionMap map)
        {
            map.HeightRange(out int min, out int max);
            var img = new Image(map.W * Scale, map.H * Scale);
            for (int z = 0; z < map.H; z++)
                for (int x = 0; x < map.W; x++)
                {
                    int i = map.Index(x, z);
                    int h = map.Height[i];
                    uint col = Ramp((h - min) / (double)Math.Max(1, max - min));
                    // 简单阴影：西北光
                    int hw = x > 0 ? map.Height[i - 1] : h, hn = z < map.H - 1 ? map.Height[i + map.W] : h;
                    double shade = 1 + 0.08 * Math.Clamp((h - hw) + (h - hn), -3, 3);
                    col = Image.Scale(col, shade);
                    if (map.Slope[i] >= 0) col = Image.Mix(col, 0xFFFFFF, 0.18);
                    if (map.HasWater(i)) col = map.Has(i, CellFlags.River) ? 0x2F6FB8u : 0x3F86B8u;
                    if (map.Has(i, CellFlags.Hidden)) col = Image.Mix(col, 0xD040D0, ((x + z) & 3) == 0 ? 0.65 : 0.2);
                    if (map.Has(i, CellFlags.Road)) col = Image.Mix(col, 0xD8CFB0, 0.55);
                    if (map.HasSpan(i)) col = map.SpanKind[i] == 1 ? 0x8B5A2Bu : Image.Mix(col, 0x202020, ((x + z) & 3) == 0 ? 0.7 : 0.35);
                    if (map.Has(i, CellFlags.Unreachable)) col = Image.Mix(col, 0xFF2020, 0.45);
                    if (map.Has(i, CellFlags.Trap)) col = Image.Mix(col, 0xFF00FF, 0.7);
                    if (map.Has(i, CellFlags.Leak)) col = 0x00E5FFu;
                    if (map.Has(i, CellFlags.Gully) && !map.HasWater(i)) col = Image.Mix(col, 0x30D0E0, 0.35);
                    if (map.Has(i, CellFlags.Scree)) col = 0xC8C8C8u;
                    if (map.Has(i, CellFlags.Spire)) col = 0xFFFFFFu;
                    if (map.Has(i, CellFlags.Ramp)) col = map.Slope[i] >= 0 ? 0xFF9A1Fu : 0xFFC860u;
                    Fill(img, x, z, map.H, col);
                    // 悬崖线：与南/东邻居高差 ≥ 2 画黑线
                    if (z > 0 && Math.Abs(map.Height[i - map.W] - h) >= 2) HLine(img, x, z, map.H, 0x111111);
                    if (x > 0 && Math.Abs(map.Height[i - 1] - h) >= 2) VLine(img, x, z, map.H, 0x111111);
                }
            foreach (var d in map.Drops)
            {
                double cx = (d.X + 0.5) * Scale, cy = (map.H - d.Z - 0.5) * Scale;
                img.Disc(cx, cy, d.Drop >= 2 ? 5 : 3, 0xFFFFFF);
                img.Disc(cx, cy, d.Drop >= 2 ? 3.5 : 2, 0xE02020);
            }
            ChunkGrid(img, map);
            Spawn(img, map);
            return img;
        }

        /// <summary>高度色带：低 → 高（绿 → 黄 → 褐 → 灰白）。</summary>
        static uint Ramp(double t)
        {
            uint[] stops = { 0x3E7A3A, 0x7FA650, 0xD6C878, 0xB0835A, 0x8A7F78, 0xEDEBE6 };
            t = Math.Clamp(t, 0, 1) * (stops.Length - 1);
            int k = Math.Min(stops.Length - 2, (int)t);
            return Image.Mix(stops[k], stops[k + 1], t - k);
        }

        static void Fill(Image img, int x, int z, int h, uint c) => img.FillRect(x * Scale, (h - 1 - z) * Scale, Scale, Scale, c);
        static void HLine(Image img, int x, int z, int h, uint c) { for (int k = 0; k < Scale; k++) img.Set(x * Scale + k, (h - z) * Scale - 1, c); }
        static void VLine(Image img, int x, int z, int h, uint c) { for (int k = 0; k < Scale; k++) img.Set(x * Scale, (h - 1 - z) * Scale + k, c); }

        static void ChunkGrid(Image img, RegionMap map)
        {
            int cs = map.Settings.ChunkSize;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    if ((x / Scale) % cs == 0 && x % Scale == 0 || ((img.Height - 1 - y) / Scale) % cs == 0 && (img.Height - 1 - y) % Scale == 0)
                        img.Blend(x, y, 0xFFFFFF, 0.35);
        }

        static void Spawn(Image img, RegionMap map)
        {
            double cx = (map.SpawnX + 0.5) * Scale, cy = (map.H - map.SpawnZ - 0.5) * Scale;
            img.Disc(cx, cy, 7, 0x000000);
            img.Disc(cx, cy, 5, 0xFFFFFF);
            img.Disc(cx, cy, 3, 0xE02020);
        }
    }

    /// <summary>
    /// 软件光栅化，模拟 WortShearCamera：正交相机垂直朝下 + 垂直错切（shear = 1）。
    /// 屏幕坐标 u = x，v = z + y；离相机越近 = y 越大。
    /// </summary>
    sealed class ShearRenderer
    {
        const double Shear = 1.0;
        readonly RegionMap map;
        readonly List<(MeshData mesh, int ox, int oz)> meshes;
        static readonly double LX, LY, LZ;

        static ShearRenderer()
        {
            // 主光：从南偏东上方照来（-z 为南）
            double x = 0.35, y = 0.85, z = -0.45, l = Math.Sqrt(x * x + y * y + z * z);
            LX = x / l; LY = y / l; LZ = z / l;
        }

        public ShearRenderer(RegionMap map, List<(MeshData, int, int)> meshes)
        {
            this.map = map;
            this.meshes = meshes;
        }

        public Image Render(double u0, double v0, double uw, double vh, double ppm, (int x, int z)? marker)
        {
            map.HeightRange(out _, out int max);
            return RenderView(u0, v0, uw, vh + max, ppm, marker);
        }

        public Image RenderView(double u0, double v0, double uw, double vh, double ppm, (int x, int z)? marker)
        {
            int w = (int)Math.Round(uw * ppm), h = (int)Math.Round(vh * ppm);
            var img = new Image(w, h, 0x1A2430);
            var depth = new float[w * h];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;

            map.HeightRange(out int minY, out int maxY);
            foreach (var (mesh, ox, oz) in meshes)
            {
                // 粗裁剪：chunk 不在视野内就跳过
                if (ox + 64 < u0 || ox > u0 + uw) continue;
                if (oz + 64 + 200 < v0 || oz - 20 > v0 + vh) continue;
                var p = mesh.Positions;
                var n = mesh.Normals;
                for (int mat = 0; mat < mesh.Indices.Length; mat++)
                {
                    var idx = mesh.Indices[mat];
                    if (idx.Count == 0) continue;
                    uint baseColor = VoxelPalette.Color((VoxelMaterial)mat);
                    for (int t = 0; t < idx.Count; t += 3)
                    {
                        int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                        double nx = n[a * 3], ny = n[a * 3 + 1], nz = n[a * 3 + 2];
                        double lambert = Math.Max(0, nx * LX + ny * LY + nz * LZ);
                        double k = 0.5 + 0.6 * lambert;
                        // 预览专用：顶面按高度略微提亮，便于分辨台地层级
                        if (ny > 0.99) k *= 0.82 + 0.36 * Math.Clamp((p[a * 3 + 1] - minY) / (double)Math.Max(1, maxY - minY), 0, 1);
                        uint col = Image.Scale(baseColor, k);
                        Raster(img, depth, u0, v0, ppm, h,
                            p[a * 3] + ox, p[a * 3 + 1], p[a * 3 + 2] + oz,
                            p[b * 3] + ox, p[b * 3 + 1], p[b * 3 + 2] + oz,
                            p[c * 3] + ox, p[c * 3 + 1], p[c * 3 + 2] + oz, col);
                    }
                }
            }

            // 预览专用：深度突变处描暗边，便于分辨台地层次（Unity 里靠光照和阴影）
            var src = (byte[])img.Rgb.Clone();
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int k = y * w + x;
                    float d = depth[k];
                    if (float.IsNegativeInfinity(d)) continue;
                    float dn = depth[k - w], ds = depth[k + w], dw = depth[k - 1], de = depth[k + 1];
                    if (dn - d > 0.5f || ds - d > 0.5f || dw - d > 0.5f || de - d > 0.5f)
                    {
                        uint c = (uint)(src[k * 3] << 16 | src[k * 3 + 1] << 8 | src[k * 3 + 2]);
                        img.Set(x, y, Image.Scale(c, 0.55));
                    }
                }

            if (marker.HasValue)
            {
                // 人物参照：屏幕上约 1 格宽、1.5 格高（GDD 第 3 节）
                var (mx, mz) = marker.Value;
                int gh = map.Height[map.Index(mx, mz)];
                double cu = mx + 0.5, cv = mz + 0.5 + gh;
                Billboard(img, depth, u0, v0, ppm, h, cu - 0.5, cv, 1.0, 1.5, gh + 0.01f, 0xE03030);
            }
            return img;
        }

        static void Billboard(Image img, float[] depth, double u0, double v0, double ppm, int h,
            double u, double v, double w, double hgt, float z, uint col)
        {
            int x0 = (int)((u - u0) * ppm), x1 = (int)((u + w - u0) * ppm);
            int y1 = h - 1 - (int)((v - v0) * ppm), y0 = h - 1 - (int)((v + hgt - v0) * ppm);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                    bool border = y == y0 || y == y1 || x == x0 || x == x1 - 1 || y == y0 + 1 || x == x0 + 1;
                    img.Set(x, y, border ? 0x000000u : col);
                }
        }

        static void Raster(Image img, float[] depth, double u0, double v0, double ppm, int h,
            double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, uint col)
        {
            // 投影
            double pax = (ax - u0) * ppm, pay = h - (az + ay * Shear - v0) * ppm;
            double pbx = (bx - u0) * ppm, pby = h - (bz + by * Shear - v0) * ppm;
            double pcx = (cx - u0) * ppm, pcy = h - (cz + cy * Shear - v0) * ppm;
            double area = (pbx - pax) * (pcy - pay) - (pby - pay) * (pcx - pax);
            if (Math.Abs(area) < 1e-9) return;
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(pax, Math.Min(pbx, pcx))));
            int maxX = Math.Min(img.Width - 1, (int)Math.Ceiling(Math.Max(pax, Math.Max(pbx, pcx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(pay, Math.Min(pby, pcy))));
            int maxY = Math.Min(img.Height - 1, (int)Math.Ceiling(Math.Max(pay, Math.Max(pby, pcy))));
            if (minX > maxX || minY > maxY) return;
            double inv = 1.0 / area;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double w0 = ((pbx - px) * (pcy - py) - (pby - py) * (pcx - px)) * inv;
                    double w1 = ((pcx - px) * (pay - py) - (pcy - py) * (pax - px)) * inv;
                    double w2 = 1 - w0 - w1;
                    const double eps = -1e-7;
                    if (w0 < eps || w1 < eps || w2 < eps) continue;
                    // 深度：y 越大越近；同高时偏南（z 小）更近
                    float d = (float)(w0 * (ay - az * 1e-4) + w1 * (by - bz * 1e-4) + w2 * (cy - cz * 1e-4));
                    int k = y * img.Width + x;
                    if (d <= depth[k]) continue;
                    depth[k] = d;
                    img.Set(x, y, col);
                }
        }
    }
}
