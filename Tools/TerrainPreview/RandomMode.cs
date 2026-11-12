using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// 随机 chunk 刷新原型：dotnet run -c Release -- --random [--seed 3] [--refresh 6] [--out DIR]
    /// 重放某一次刷新：--rid 3:9F2A61C0（控制台和图片文件名里都有这个 ID）。
    /// 固定世界 = TerraceField（5×5，种子 seed），随机区域 = 中间 3×3 的右上角 chunk（cx 3, cz 3）。
    /// 输出 refresh_k.png（区域 + 外圈 16m）、world_k.png（整张俯视）、控制台打印每次的结构与校验。
    /// </summary>
    static class RandomMode
    {
        public static int Run(uint seed, int refreshes, string outDir, uint? replaySeed = null)
        {
            Directory.CreateDirectory(outDir);
            var chunks = new List<(int, int)> { (3, 3) };
            var baked = TerraceField.Generate(new TerraceFieldSettings { Seed = seed, RandomChunks = chunks }, out var cls);
            var cands = RandomRegion.AutoCandidates(baked, cls, chunks, seed);
            Console.WriteLine($"固定世界：TerraceField seed {seed}；随机 chunk (3,3)；候选出入口 {cands.Count} 个：" + string.Join(" ", cands.ConvertAll(e => $"({e.X},{e.Z})→{"北东南西"[e.Dir]}")));
            var s = new RandomRegionSettings();
            int fails = 0;
            for (int k = 0; k < refreshes; k++)
            {
                var sw = Stopwatch.StartNew();
                uint rs = replaySeed ?? RandomRegion.RefreshSeed(seed, 1, k);
                var r = RandomRegion.RefreshWithSeed(baked, cls, chunks, cands, rs, s);
                long ms = sw.ElapsedMilliseconds;
                if (!r.Ok) fails++;
                string id = RandomRegion.FormatId(seed, rs);
                Console.WriteLine($"刷新 {k + 1}  ID {id}：{(r.Ok ? "通过" : "失败")}，尝试 {r.Attempts} 次，{ms}ms，出入口 " + string.Join(" ", r.Entrances.ConvertAll(e => e.Open ? "开" : "封")) + $"，checksum 0x{r.Map.Checksum():X8}");
                foreach (var line in r.AttemptLog) Console.WriteLine("  " + line);
                if (GenDebug.On) File.WriteAllBytes(Path.Combine(outDir, $"heights_{k + 1}.bin"), System.Linq.Enumerable.SelectMany(r.Map.Height, v => BitConverter.GetBytes(v)).ToArray()); // 每格高度（int32 小端），清理 / 重构时逐格比对用
                foreach (var line in r.Report) Console.WriteLine("  " + line);
                if (GenDebug.On && GenDebug.Log.Length > 0) { Console.Write(GenDebug.Log); GenDebug.Log.Clear(); }
                Draw(r, baked, r.Classes ?? cls).Save(Path.Combine(outDir, $"refresh_{k + 1}_{rs:X8}.png"));
                Relief(r, r.Classes ?? cls).Save(Path.Combine(outDir, $"relief_{k + 1}_{rs:X8}.png"));
                Shear(r).Save(Path.Combine(outDir, $"shear_{k + 1}_{rs:X8}.png"));
            }
            return fails == 0 ? 0 : 2;
        }

        static Image Draw(RandomRegionResult r, RegionMap baked, byte[] cls)
        {
            var m = r.Map;
            const int S = 7, x0 = 192 - 16, z0 = 192 - 16, size = 64 + 32;
            var img = new Image(size * S, size * S);
            int W = m.W;
            int Py(int z) => (z0 + size - 1 - z) * S; // 北在上
            for (int z = z0; z < z0 + size; z++)
                for (int x = x0; x < x0 + size; x++)
                {
                    int i = z * W + x;
                    bool reg = r.InRegion[i];
                    int h = m.Height[i];
                    uint col;
                    if (cls[i] == TerraceField.Border) col = 0x2E2118;
                    else if (reg && r.NoStand != null && r.NoStand[i] && !m.Has(i, CellFlags.Spire)) col = Image.Scale(0x5C6A44, 0.78 + 0.06 * (h % 5)); // 山体 / 墙：林木覆盖的山包，按高度深浅
                    else
                    {
                        // 地面：按高度微调明暗
                        col = Image.Scale(0x8A6C55, 0.86 + 0.05 * (h % 4));
                        if (reg && r.Grass[i]) col = Image.Mix(col, ((x * 7 + z * 13) & 3) == 0 ? 0x3E8A3Au : 0x5C9A48u, 0.55);
                        if (reg && r.Rock[i]) col = 0x8C8A84;
                        if (reg && m.Has(i, CellFlags.Scree)) col = 0x9A8A70;
                        if (reg && m.Has(i, CellFlags.Spire)) col = 0xB8B4AA;
                        if (reg && r.Seam[i]) col = Image.Mix(col, 0x6E5A48, 0.45);
                        if (reg && r.Corridor[i]) col = 0xC9A878;
                    }
                    if (m.HasWater(i)) col = m.Has(i, CellFlags.River) ? 0x2F6FB8u : 0x4A8FC8u;
                    if (!reg && cls[i] != TerraceField.Border) col = Image.Scale(col, 0.75); // 固定世界压暗一点
                    for (int py = 0; py < S; py++)
                        for (int px = 0; px < S; px++)
                            img.Set((x - x0) * S + px, Py(z) + py, col);
                    // 崖影：比南 / 东邻高 ≥ 2
                    if (z > 0 && h - m.Height[i - W] >= 2) for (int px = 0; px < S; px++) img.Set((x - x0) * S + px, Py(z) + S - 1, 0x120C08);
                    if (x + 1 < W && h - m.Height[i + 1] >= 2) for (int py = 0; py < S; py++) img.Set((x - x0) * S + S - 1, Py(z) + py, 0x1A120C);
                }
            // chunk 边框
            for (int t = 0; t < size * S; t++)
            {
                foreach (int c in new[] { 192, 256 })
                {
                    int px = (c - x0) * S, py = (z0 + size - c) * S;
                    if (px >= 0 && px < img.Width) img.Blend(px, t, 0xFFFFFF, 0.5);
                    if (py >= 0 && py < img.Height) img.Blend(t, py, 0xFFFFFF, 0.5);
                }
            }
            // 连接图：房间中心之间的细线
            foreach (var (a, b) in r.Edges)
            {
                var ra = r.Rooms[a]; var rb = r.Rooms[b];
                double ax = ((ra.X0 + ra.X1) / 2.0 - x0) * S, ay = (z0 + size - (ra.Z0 + ra.Z1) / 2.0) * S;
                double bx = ((rb.X0 + rb.X1) / 2.0 - x0) * S, by = (z0 + size - (rb.Z0 + rb.Z1) / 2.0) * S;
                int n = (int)(Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)));
                for (int t = 0; t <= n; t += 2) img.Disc(ax + (bx - ax) * t / Math.Max(1, n), ay + (by - ay) * t / Math.Max(1, n), 1.2, 0xF0F0F0);
            }
            // 危险区（红色半透明）
            foreach (var (x, z, rr) in r.Danger)
            {
                double cx = (x + 0.5 - x0) * S, cy = (z0 + size - z - 0.5) * S, R = rr * S;
                for (int y = (int)(cy - R); y <= cy + R; y++)
                    for (int xx = (int)(cx - R); xx <= cx + R; xx++)
                        if ((xx - cx) * (xx - cx) + (y - cy) * (y - cy) <= R * R) img.Blend(xx, y, 0xE02020, 0.35);
            }
            // 奖励（星星：黄色十字星）
            foreach (var (x, z) in r.Rewards)
            {
                double cx = (x + 0.5 - x0) * S, cy = (z0 + size - z - 0.5) * S;
                Star(img, cx, cy, 13, 0x2A1A00); Star(img, cx, cy, 10, 0xFFD23A);
            }
            // 单向落差
            if (r.OneWay.HasValue)
            {
                var (x, z) = r.OneWay.Value;
                img.Disc((x + 0.5 - x0) * S, (z0 + size - z - 0.5) * S, 9, 0xFFFFFF);
                img.Disc((x + 0.5 - x0) * S, (z0 + size - z - 0.5) * S, 6.5, 0xC040C0);
            }
            // 出入口：开 = 绿，封 = 红叉
            foreach (var e in r.Entrances)
            {
                double cx = (e.X + 0.5 - x0) * S, cy = (z0 + size - e.Z - 0.5) * S;
                if (e.Open) { img.Disc(cx, cy, 11, 0xFFFFFF); img.Disc(cx, cy, 8, 0x30C040); }
                else
                    for (int t = -10; t <= 10; t++)
                        for (int w = -2; w <= 2; w++) { img.Set((int)cx + t, (int)cy + t + w, 0xE02020); img.Set((int)cx + t, (int)cy - t + w, 0xE02020); }
            }
            return img;
        }

        const int X0 = 192 - 16, Z0 = 192 - 16, Size = 64 + 32;

        /// <summary>台地图：按高度上色（越高越深），高差 ≥ 2 描黑边，1 层描浅边。</summary>
        static Image Relief(RandomRegionResult r, byte[] cls)
        {
            var m = r.Map;
            const int S = 7;
            int W = m.W, lo = int.MaxValue, hi = int.MinValue;
            for (int z = Z0; z < Z0 + Size; z++) for (int x = X0; x < X0 + Size; x++) { int h = m.Height[z * W + x]; lo = Math.Min(lo, h); hi = Math.Max(hi, h); }
            var img = new Image(Size * S, Size * S);
            for (int z = Z0; z < Z0 + Size; z++)
                for (int x = X0; x < X0 + Size; x++)
                {
                    int i = z * W + x, h = m.Height[i];
                    double t = (h - lo) / (double)Math.Max(1, hi - lo);
                    uint col = Image.Mix(0xD8C3A0, 0x2A1C12, Math.Sqrt(t));
                    if (m.HasWater(i)) col = 0x4A8FC8;
                    int py0 = (Z0 + Size - 1 - z) * S, px0 = (x - X0) * S;
                    for (int py = 0; py < S; py++) for (int px = 0; px < S; px++) img.Set(px0 + px, py0 + py, col);
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Grid.DX4[d], nz = z + Grid.DZ4[d];
                        if (!m.InBounds(nx, nz)) continue;
                        int dh = h - m.Height[nz * W + nx];
                        if (dh <= 0) continue;
                        uint lc = dh >= 2 ? 0x000000u : 0x8A7458u;
                        for (int t2 = 0; t2 < S; t2++)
                        {
                            if (d == 0) img.Set(px0 + t2, py0, lc);
                            else if (d == 2) img.Set(px0 + t2, py0 + S - 1, lc);
                            else if (d == 1) img.Set(px0 + S - 1, py0 + t2, lc);
                            else img.Set(px0, py0 + t2, lc);
                        }
                    }
                }
            for (int t = 0; t < Size * S; t++)
                foreach (int c in new[] { 192, 256 })
                {
                    int px = (c - X0) * S, py = (Z0 + Size - c) * S;
                    if (px >= 0 && px < img.Width) img.Blend(px, t, 0x40A0FF, 0.6);
                    if (py >= 0 && py < img.Height) img.Blend(t, py, 0x40A0FF, 0.6);
                }
            foreach (var e in r.Entrances)
            {
                double cx = (e.X + 0.5 - X0) * S, cy = (Z0 + Size - e.Z - 0.5) * S;
                img.Disc(cx, cy, 8, 0xFFFFFF); img.Disc(cx, cy, 5.5, e.Open ? 0x30C040u : 0xE02020u);
            }
            return img;
        }

        /// <summary>错切相机（与游戏一致：从南往北看，shear 1），区域 + 外圈 16m，6 px/m。</summary>
        static Image Shear(RandomRegionResult r)
        {
            var m = r.Map;
            var voxels = new RegionVoxels(m);
            var meshes = new List<(MeshData mesh, int ox, int oz)>();
            for (int cz = 2; cz <= 4; cz++)
                for (int cx = 2; cx <= 4; cx++)
                    meshes.Add((ChunkMesher.Build(voxels, cx, cz), cx * 64, cz * 64));
            var sr = new ShearRenderer(m, meshes);
            int maxH = 0;
            for (int z = Z0; z < Z0 + Size; z++) for (int x = X0; x < X0 + Size; x++) maxH = Math.Max(maxH, m.Height[z * m.W + x]);
            return sr.RenderView(X0, Z0, Size, Size + maxH, 6.0, null);
        }

        static void Star(Image img, double cx, double cy, double r, uint c)
        {
            for (int y = (int)(cy - r); y <= cy + r; y++)
                for (int x = (int)(cx - r); x <= cx + r; x++)
                {
                    double dx = Math.Abs(x - cx), dy = Math.Abs(y - cy);
                    // 四角星：|dx|·|dy| 小的十字 + 中心圆
                    if (dx * dy <= r * 1.1 || dx * dx + dy * dy <= r * r * 0.18) img.Set(x, y, c);
                }
        }
    }
}
