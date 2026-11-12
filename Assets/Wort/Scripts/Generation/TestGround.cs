namespace Wort.Generation
{
    /// <summary>
    /// 空白测试地（负责人 2026-11-12：测体素地基用，手搭测试地形）：chunks × chunks 个 chunk 的平地，高度逐格由编辑资源给出。
    /// 没有斜坡、没有水；Biome 全是人类区；出生点在中间。
    /// </summary>
    public static class TestGround
    {
        /// <param name="heights">每格高度（行优先，z * W + x）；null 或长度不对时全部为 baseHeight。</param>
        public static RegionMap Create(int chunks, int baseHeight, byte[] heights, uint seed = 1)
        {
            var rs = new RegionSettings { Seed = seed, ChunksX = chunks, ChunksZ = chunks, MinY = -4 };
            var map = new RegionMap(rs);
            int n = map.W * map.H;
            bool ok = heights != null && heights.Length == n;
            for (int i = 0; i < n; i++)
            {
                map.Height[i] = ok ? heights[i] : baseHeight;
                map.Biome[i] = (byte)BiomeId.Human;
                map.Weights[(int)BiomeId.Human][i] = 1;
            }
            map.SpawnX = map.W / 2;
            map.SpawnZ = map.H / 2;
            map.HeightRange(out _, out int max);
            map.Stats["maxHeight"] = max;
            return map;
        }
    }
}
