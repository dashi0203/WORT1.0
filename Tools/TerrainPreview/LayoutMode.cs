using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// layout 管线的预览（Docs/WorldGen.md 第 10 节）。
    /// 输出：layout.png（区域类型 + 出入口 + 道路 + 地点 + 桥 / 隧道）、height.png、biome.png、shear_overview.png、
    /// 以及每个验证点的 1080p 相机视野 view_*.png；控制台打印检查报告和 checksum。
    /// </summary>
    static class LayoutMode
    {
        static readonly uint[] KindColor =
        {
            0x7DB25A, // 开放区
            0xC9A15A, // 台地区
            0x5A86C9, // 盆地
            0x8E5AC9, // 峡谷
            0xB8B0A4, // 高原
        };

        public static int Run(string layoutPath, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string templatesPath = Path.Combine(Path.GetDirectoryName(layoutPath) ?? ".", "SiteTemplates.json");
            var sw = Stopwatch.StartNew();
            var L = WorldLayout.Parse(File.ReadAllText(layoutPath), File.Exists(templatesPath) ? File.ReadAllText(templatesPath) : null);
            var res = LayoutGenerator.Generate(L);
            var map = res.Map;
            long tA = sw.ElapsedMilliseconds;
            var voxels = new RegionVoxels(map);
            var meshes = new List<(MeshData mesh, int ox, int oz)>();
            int tris = 0;
            for (int cz = 0; cz < map.Settings.ChunksZ; cz++)
                for (int cx = 0; cx < map.Settings.ChunksX; cx++)
                {
                    var m = ChunkMesher.Build(voxels, cx, cz);
                    tris += m.TriangleCount;
                    meshes.Add((m, cx * map.Settings.ChunkSize, cz * map.Settings.ChunkSize));
                }
            long tB = sw.ElapsedMilliseconds;

            Console.WriteLine($"layout {L.Name}（seed {L.Seed}）：生成 + 检查 {tA}ms，体素 + 网格 {tB - tA}ms，三角形 {tris}");
            foreach (var kv in map.Stats) Console.WriteLine($"  {kv.Key} = {kv.Value}");
            Console.WriteLine($"  checksum = 0x{map.Checksum():X8}");
            Console.WriteLine("检查：");
            foreach (var line in res.Report.Lines) Console.WriteLine("  " + line);
            if (res.Report.Ok) Console.WriteLine("  全部通过");
            foreach (var e in res.Report.Errors) Console.WriteLine("  [错误] " + e);

            LayoutImage(res).Save(Path.Combine(outDir, "layout.png"));
            Maps.Height(map).Save(Path.Combine(outDir, "height.png"));
            Maps.Biome(map).Save(Path.Combine(outDir, "biome.png"));
            var r = new ShearRenderer(map, meshes);
            r.Render(0, 0, map.W, map.H, 2.0, null).Save(Path.Combine(outDir, "shear_overview.png"));

            double ppm = 1080 / 19.0;
            double viewW = 1920 / ppm, viewH = 19.0;
            foreach (var (name, x, z) in Views(L, map))
            {
                int h = map.Height[map.Index(x, z)];
                double v = z + 0.5 + h;
                r.RenderView(x + 0.5 - viewW / 2, v - viewH / 2, viewW, viewH, ppm, (x, z)).Save(Path.Combine(outDir, "view_" + name + ".png"));
                Console.WriteLine($"  view {name} at ({x},{z}) h={h}");
            }
            // 中景：每处 80m × 60m，8 px/m，看结构关系
            foreach (var (name, x, z) in Views(L, map))
            {
                int h = map.Height[map.Index(x, z)];
                r.RenderView(x - 40, z + h - 30, 80, 60, 8.0, (x, z)).Save(Path.Combine(outDir, "mid_" + name + ".png"));
            }
            return res.Report.Ok ? 0 : 2;
        }

        static IEnumerable<(string, int, int)> Views(WorldLayout L, RegionMap map)
        {
            foreach (var sp in L.Spans)
            {
                if (sp.Type == SpanType.Bridge)
                {
                    // 站在桥下的峡谷底
                    int x = sp.X0 + 4, z = (sp.Z0 + sp.Z1) / 2;
                    for (int dz = -8; dz <= 8; dz++)
                        if (map.Height[map.Index(x, z + dz)] < map.Height[map.Index(x, z)]) { z += dz; break; }
                    yield return ("bridge_" + sp.Id, x, z);
                }
                else
                {
                    yield return ("tunnel_" + sp.Id, Math.Min(sp.X0, sp.X1) + 8, sp.Z0 - 4);
                }
            }
            for (int k = 0; k < L.Gates.Count && k < map.Ramps.Count; k++)
            {
                // 坡道中段的侧面，能同时看到坡道和崖
                var rp = map.Ramps[k];
                int up = (rp.DownDir + 2) & 3;
                int back = rp.Length / 2 + 1;
                int x = rp.TopX - Grid.DX4[up] * back + Grid.DX4[(up + 1) & 3] * 4;
                int z = rp.TopZ - Grid.DZ4[up] * back + Grid.DZ4[(up + 1) & 3] * 4;
                yield return ("gate_" + L.Gates[k].Id.Replace('.', '_'), Grid.Clamp(x, 0, map.W - 1), Grid.Clamp(z, 0, map.H - 1));
            }
            foreach (var s in L.Sites)
                if (s.Template == "cave" || s.Template == "ruinHut")
                    yield return ("site_" + s.Id, s.X, s.Z - 3);
        }

        static Image LayoutImage(LayoutResult res)
        {
            var map = res.Map;
            var L = res.Layout;
            const int S = Maps.Scale;
            var img = new Image(map.W * S, map.H * S);
            for (int z = 0; z < map.H; z++)
                for (int x = 0; x < map.W; x++)
                {
                    int i = map.Index(x, z);
                    var reg = res.RegionAt(i);
                    uint col = KindColor[(int)reg.Kind];
                    // 同类型的不同区域用明暗区分
                    col = Image.Scale(col, 0.85 + 0.3 * ((reg.Index * 37 % 7) / 6.0));
                    if (map.Has(i, CellFlags.Road)) col = 0xE8E0C8;
                    if (map.Has(i, CellFlags.Site)) col = Image.Mix(col, 0xFFFFFF, 0.55);
                    if (map.Has(i, CellFlags.Gate)) col = 0xFF8A00;
                    if (map.HasSpan(i)) col = map.SpanKind[i] == 1 ? 0x8B5A2Bu : 0x303030u;
                    if (map.Has(i, CellFlags.Trap)) col = 0xFF00FF;
                    if (map.Has(i, CellFlags.Leak)) col = 0x00E5FF;
                    img.FillRect(x * S, (map.H - 1 - z) * S, S, S, col);
                    // 区域边界描黑
                    bool edge = x > 0 && res.RegionId[i - 1] != res.RegionId[i] || z > 0 && res.RegionId[i - map.W] != res.RegionId[i];
                    if (edge) img.FillRect(x * S, (map.H - 1 - z) * S, S, S, 0x101010);
                }
            foreach (var s in L.Sites)
            {
                double cx = (s.X + 0.5) * S, cy = (map.H - s.Z - 0.5) * S;
                img.Disc(cx, cy, 5, 0x000000);
                img.Disc(cx, cy, 3, s.Id == L.Home ? 0xE02020u : 0xFFFFFFu);
            }
            return img;
        }
    }
}
