using System;
using System.Collections.Generic;
using System.IO;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// 各 Biome 地基外观的样板（负责人 2026-11-12 参考图）：dotnet run -c Release -- --looks [--out DIR]
    /// 上排 7 个 2 层高的十字台（人类区、沼泽、混交林、针叶林、山地、荒原、海岸临时），每个所在的一列是单一 Biome；
    /// 下排一条长台地横穿 人类区 → 混交林 → 沼泽 → 山地，Biome 权重按 x 线性渐变（过渡带约 35m），看崖面样式怎么过渡。
    /// 输出：overview.png、cross_*.png（每个十字台特写）、strip_*.png（过渡带分段特写）。
    /// </summary>
    static class LooksMode
    {
        static readonly BiomeId[] Order = { BiomeId.Human, BiomeId.Swamp, BiomeId.MixedForest, BiomeId.Conifer, BiomeId.Mountain, BiomeId.Moor, BiomeId.Coast };
        static readonly string[] Names = { "human", "swamp", "mixed", "conifer", "mountain", "moor", "coast" };

        public static int Run(uint seed, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var rs = new RegionSettings { Seed = seed, ChunksX = 2, ChunksZ = 1, MinY = -4 };
            var map = new RegionMap(rs);
            int W = map.W, H = map.H;
            void SetW(int i, BiomeId b, float w) { map.Weights[(int)b][i] = w; }

            // 下排过渡带：权重按 x 在 4 个 Biome 中心之间线性渐变
            var strip = new[] { BiomeId.Human, BiomeId.MixedForest, BiomeId.Swamp, BiomeId.Mountain };
            var sx = new[] { 12.0, 47.0, 82.0, 117.0 };
            for (int z = 0; z < H; z++)
                for (int x = 0; x < W; x++)
                {
                    int i = z * W + x;
                    if (z >= 28)
                    {
                        int col = Math.Min(Order.Length - 1, Math.Max(0, (x - 2) / 18));
                        SetW(i, Order[col], 1); map.Biome[i] = (byte)Order[col];
                        continue;
                    }
                    double fx = Math.Clamp(x + 0.5, sx[0], sx[3]);
                    int k = 0; while (k < 2 && fx > sx[k + 1]) k++;
                    float t = (float)((fx - sx[k]) / (sx[k + 1] - sx[k]));
                    SetW(i, strip[k], 1 - t); SetW(i, strip[k + 1], t);
                    map.Biome[i] = (byte)(t < 0.5f ? strip[k] : strip[k + 1]);
                }

            // 地面 0 层；十字台 2 层（臂宽 4 格，总 12 格）
            var crossC = new List<(int x, int z)>();
            for (int c = 0; c < Order.Length; c++)
            {
                int x0 = 5 + c * 18, z0 = 38;
                crossC.Add((x0 + 6, z0 + 6));
                for (int j = 0; j < 12; j++)
                    for (int i = 0; i < 12; i++)
                        if ((i >= 4 && i < 8) || (j >= 4 && j < 8)) map.Height[(z0 + j) * W + x0 + i] = 2;
            }
            // 过渡带：长台地 2 层（z 10–17），中间几处 3 层的小台阶和凹口，看拐角
            for (int z = 10; z < 18; z++)
                for (int x = 4; x < 124; x++)
                {
                    int i = z * W + x;
                    map.Height[i] = 2;
                    if (z < 12 && (x % 20) >= 8 && (x % 20) < 11) map.Height[i] = 0; // 南面凹口
                    if (z >= 14 && (x % 30) >= 12 && (x % 30) < 20) map.Height[i] = 4; // 上面再叠一级
                }
            map.SpawnX = 2; map.SpawnZ = 2;

            ShearRenderer.Look = new LookField(map);
            var voxels = new RegionVoxels(map);
            var meshes = new List<(MeshData mesh, int ox, int oz)>();
            long tris = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int cx = 0; cx < rs.ChunksX; cx++)
            {
                var mesh = CliffMesher.Build(voxels, cx, 0, seed, ShearRenderer.Look);
                tris += mesh.TriangleCount;
                meshes.Add((mesh, cx * rs.ChunkSize, 0));
            }
            Console.WriteLine($"looks: 网格 {sw.ElapsedMilliseconds}ms，三角形 {tris}");
            // 对照：不带外观（现在的做法）的三角形数
            long trisOld = 0;
            for (int cx = 0; cx < rs.ChunksX; cx++) trisOld += CliffMesher.Build(new RegionVoxels(map), cx, 0, seed).TriangleCount;
            Console.WriteLine($"  对照（现在的做法）三角形 {trisOld}");

            var r = new ShearRenderer(map, meshes);
            r.RenderView(0, 0, W, H + 4, 8.0, null).Save(Path.Combine(outDir, "overview.png"));
            for (int c = 0; c < Order.Length; c++)
            {
                var (mx, mz) = crossC[c];
                // 十字台特写：14m × 11m，带 2 层高度的错切
                r.RenderView(mx - 7.5, mz + 2 - 6.5, 15, 13, 48.0, null).Save(Path.Combine(outDir, $"cross_{c + 1}_{Names[c]}.png"));
            }
            for (int k = 0; k < 4; k++)
                r.RenderView(4 + k * 30, 6, 30, 16, 32.0, null).Save(Path.Combine(outDir, $"strip_{k + 1}.png"));
            return 0;
        }
    }
}
