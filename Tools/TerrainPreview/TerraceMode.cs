using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// 体素器第一步（TerraceField）的预览：dotnet run -c Release -- --terrace [--seed N] [--out DIR]
    /// 输出：plan.png（参考图风格的台地 / 盆地分布）、height.png、shear_overview.png、views/*.png。
    /// </summary>
    static class TerraceMode
    {
        public static int Run(uint seed, string outDir, int chunks = 5, bool cliff = false)
        {
            Directory.CreateDirectory(outDir);
            var sw = Stopwatch.StartNew();
            var map = TerraceField.Generate(new TerraceFieldSettings { Seed = seed, Chunks = chunks }, out var cls);
            long tA = sw.ElapsedMilliseconds;
            var voxels = new RegionVoxels(map);
            var meshes = new List<(MeshData mesh, int ox, int oz)>();
            long triBase = 0, triDressed = 0;
            for (int cz = 0; cz < map.Settings.ChunksZ; cz++)
                for (int cx = 0; cx < map.Settings.ChunksX; cx++)
                    {
                    var mesh = ChunkMesher.Build(voxels, cx, cz);
                    triBase += mesh.TriangleCount;
                    int ox = cx * map.Settings.ChunkSize, oz = cz * map.Settings.ChunkSize;
                    if (cliff) mesh = CliffMesher.Build(voxels, cx, cz, seed);
                    triDressed += mesh.TriangleCount;
                    meshes.Add((mesh, ox, oz));
                }
            long tB = sw.ElapsedMilliseconds;
            Console.WriteLine($"terrace seed {seed}: 生成 {tA}ms, 体素 + 网格 {tB - tA}ms");
            Console.WriteLine($"  三角形：体素网格 {triBase}，显示网格 {triDressed}");
            foreach (var kv in map.Stats) Console.WriteLine($"  {kv.Key} = {kv.Value}");
            Console.WriteLine($"  checksum = 0x{map.Checksum():X8}");
            if (GenDebug.On)
            {
                var cnt = new int[5]; var hs = new SortedDictionary<int,int>();
                for (int i = 0; i < cls.Length; i++) if (map.Has(i, CellFlags.Unreachable)) { cnt[cls[i]]++; hs[map.Height[i]] = hs.GetValueOrDefault(map.Height[i]) + 1; }
                Console.WriteLine("  unreachable by class: " + string.Join(",", cnt) + " heights: " + string.Join(" ", hs));
                Console.Write(GenDebug.Log);
                Console.WriteLine($"  spawn h={map.Height[map.Index(map.SpawnX,map.SpawnZ)]}");
                var lab = Grid.Label(map.W, map.H, i => cls[i] == TerraceField.Ground, (a, b) => true, out var sz);
                var l2 = new List<int>(sz); l2.Sort(); l2.Reverse();
                Console.WriteLine("  ground comps: " + sz.Count + " top: " + string.Join(" ", l2.GetRange(0, Math.Min(6, l2.Count))));
                var ul = new SortedDictionary<int,int>();
                for (int i = 0; i < cls.Length; i++) if (map.Has(i, CellFlags.Unreachable) && cls[i]==0) ul[sz[lab[i]]] = ul.GetValueOrDefault(sz[lab[i]]) + 1;
                Console.WriteLine("  unreachable ground by comp size: " + string.Join(" ", ul));
            }

            if (GenDebug.On)
            {
                int b0 = 64;
                int bandH = (map.H - 2 * b0) / 3;
                for (int band = 0; band < 3; band++)
                {
                    int z0 = b0 + band * bandH, z1 = z0 + bandH;
                    var hist = new SortedDictionary<int, int>();
                    int gmin = int.MaxValue, gmax = int.MinValue;
                    for (int z = z0; z < z1; z++) for (int x = b0; x < map.W - b0; x++)
                    {
                        int i = map.Index(x, z); if (cls[i] == TerraceField.Border || map.HasWater(i)) continue;
                        hist[map.Height[i]] = hist.GetValueOrDefault(map.Height[i]) + 1;
                        if (cls[i] == TerraceField.Ground) { gmin = Math.Min(gmin, map.Height[i]); gmax = Math.Max(gmax, map.Height[i]); }
                    }
                    Console.WriteLine($"  band {(band==0?"南":band==1?"中":"北")}: 平地 {gmin}..{gmax}, 全部 " + string.Join(" ", hist));
                }
            }
            Plan(map, cls).Save(Path.Combine(outDir, "plan.png"));
            Maps.Height(map).Save(Path.Combine(outDir, "height.png"));
            Maps.Biome(map).Save(Path.Combine(outDir, "biome.png"));
            var r = new ShearRenderer(map, meshes);
            r.Render(0, 0, map.W, map.H, 2.0, null).Save(Path.Combine(outDir, "shear_overview.png"));
            // 局部放大（8 px/m）：可玩区中部、北侧外圈山体
            foreach (var (name, zx, zz, vw, ppm) in new[] { ("mid", map.W / 2, map.H / 2, 80, 8.0), ("north", map.W / 2, map.H - 40, 80, 8.0), ("close", map.W / 2 - 30, map.H - 52, 28, 24.0), ("mid16", map.W / 2 - 10, map.H / 2 + 10, 48, 16.0), ("border16", 70, map.H - 70, 48, 16.0), ("macro", map.W / 2 - 16, map.H - 49, 8, 96.0) })
            {
                int hz = map.Height[map.Index(zx, zz)];
                r.RenderView(zx - vw / 2, zz + hz - vw * 3 / 8, vw, vw * 3 / 4, ppm, null).Save(Path.Combine(outDir, $"zoom_{name}.png"));
            }
            return 0;
        }

        /// <summary>参考图风格：平地浅褐、台地深褐（二级更深）、盆地暗绿褐、外圈最深；高处南 / 东侧描黑边当崖影。</summary>
        static Image Plan(RegionMap map, byte[] cls)
        {
            const int S = 3;
            var img = new Image(map.W * S, map.H * S);
            var fx = new Fbm(77, 9, 2);
            for (int z = 0; z < map.H; z++)
                for (int x = 0; x < map.W; x++)
                {
                    int i = map.Index(x, z);
                    uint col = cls[i] switch
                    {
                        TerraceField.Plateau => 0x4E3626u,
                        TerraceField.Plateau2 => 0x3B281Bu,
                        TerraceField.Basin => 0x4F5340u,
                        TerraceField.Border => 0x2E2118u,
                        _ => 0x8A6C55u,
                    };
                    // 地表斑驳（植被感）
                    double n = fx.Sample(x, z);
                    if (cls[i] == TerraceField.Ground && n > 0.35) col = Image.Mix(col, 0x6E7A5A, 0.45);
                    col = Image.Scale(col, 1 + 0.03 * (map.Height[i] % 4));
                    if (map.HasWater(i)) col = 0x3F7FB0u;
                    if (map.Slope[i] >= 0) col = 0xE0A040u;
                    else if (map.Has(i, CellFlags.Ramp)) col = 0xB98A4Au;
                    if (map.Has(i, CellFlags.Unreachable)) col = Image.Mix(col, 0xFF2020, 0.6);
                    for (int py = 0; py < S; py++)
                        for (int px = 0; px < S; px++)
                            img.Set(x * S + px, (map.H - 1 - z) * S + py, col);
                    // 崖影：比南邻 / 东邻高 ≥ 2 时在本格南 / 东边描黑
                    if (z > 0 && map.Height[i] - map.Height[i - map.W] >= 2 && map.Slope[i - map.W] < 0)
                        for (int px = 0; px < S; px++) img.Set(x * S + px, (map.H - 1 - z) * S + S - 1, 0x0E0A08);
                    if (x + 1 < map.W && map.Height[i] - map.Height[i + 1] >= 2 && map.Slope[i + 1] < 0)
                        for (int py = 0; py < S; py++) img.Set(x * S + S - 1, (map.H - 1 - z) * S + py, 0x1A120C);
                }
            // chunk 网格
            for (int c = 0; c <= map.W; c += 64)
                for (int t = 0; t < map.H * S; t++)
                {
                    if (c * S < img.Width) img.Set(Math.Min(c * S, img.Width - 1), t, Image.Mix(img.Get(Math.Min(c * S, img.Width - 1), t), 0xFFFFFF, 0.25));
                    if (c * S < img.Height) img.Set(t, Math.Min(c * S, img.Height - 1), Image.Mix(img.Get(t, Math.Min(c * S, img.Height - 1)), 0xFFFFFF, 0.25));
                }
            img.Disc((map.SpawnX + 0.5) * S, (map.H - map.SpawnZ - 0.5) * S, 6, 0xFFFFFF);
            img.Disc((map.SpawnX + 0.5) * S, (map.H - map.SpawnZ - 0.5) * S, 4, 0xE02020);
            return img;
        }
    }
}
