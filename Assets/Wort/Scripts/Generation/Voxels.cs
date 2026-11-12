using System;

namespace Wort.Generation
{
    /// <summary>体素材质。第一个实验只用纯色。</summary>
    public enum VoxelMaterial : byte
    {
        Air = 0,
        Meadow,        // 人类区草地
        ForestFloor,   // 混交林地表
        Needles,       // 针叶林地表
        Moss,          // 沼泽苔地
        MountainGrass, // 山地草甸
        Heath,         // 荒原石楠
        Sand,          // 海岸
        Soil,          // 表层侧面 / 浅层土
        Rock,          // 崖面岩石
        DeepRock,      // 深层岩
        Mud,           // 池底
        Gravel,        // 河床
        Water,
        Count
    }

    /// <summary>
    /// 体素形状（GDD 第 5 节）：整格、斜坡 ×4。半墙原型阶段不做。
    /// 斜坡按上坡方向命名，与 Grid.DX4/DZ4 的方向索引一致（+1 偏移）。
    /// </summary>
    public enum VoxelShape : byte
    {
        Full = 0,
        SlopeN = 1,
        SlopeE = 2,
        SlopeS = 3,
        SlopeW = 4,
    }

    public static class VoxelPalette
    {
        /// <summary>sRGB 颜色 0xRRGGBB。Unity 侧用同一张表建纯色材质。</summary>
        public static uint Color(VoxelMaterial m)
        {
            switch (m)
            {
                case VoxelMaterial.Meadow: return 0x8DB654;
                case VoxelMaterial.ForestFloor: return 0x5E8A3A;
                case VoxelMaterial.Needles: return 0x4C6B3C;
                case VoxelMaterial.Moss: return 0x6E7F45;
                case VoxelMaterial.MountainGrass: return 0x8A9468;
                case VoxelMaterial.Heath: return 0x8C7A5A;
                case VoxelMaterial.Sand: return 0xD8C98E;
                case VoxelMaterial.Soil: return 0x7A5C3E;
                case VoxelMaterial.Rock: return 0x8C8A84;
                case VoxelMaterial.DeepRock: return 0x5F5D5A;
                case VoxelMaterial.Mud: return 0x4E4332;
                case VoxelMaterial.Gravel: return 0x8E8676;
                case VoxelMaterial.Water: return 0x3F7FB0;
                default: return 0xFF00FF;
            }
        }

        /// <summary>侧面显示的材质：地表草类的侧面是土。</summary>
        public static VoxelMaterial Side(VoxelMaterial m)
        {
            switch (m)
            {
                case VoxelMaterial.Meadow:
                case VoxelMaterial.ForestFloor:
                case VoxelMaterial.Needles:
                case VoxelMaterial.Moss:
                case VoxelMaterial.Heath:
                    return VoxelMaterial.Soil;
                case VoxelMaterial.MountainGrass:
                    return VoxelMaterial.Rock;
                default:
                    return m;
            }
        }

        public static VoxelMaterial Surface(BiomeId b)
        {
            switch (b)
            {
                case BiomeId.Human: return VoxelMaterial.Meadow;
                case BiomeId.MixedForest: return VoxelMaterial.ForestFloor;
                case BiomeId.Conifer: return VoxelMaterial.Needles;
                case BiomeId.Swamp: return VoxelMaterial.Moss;
                case BiomeId.Mountain: return VoxelMaterial.MountainGrass;
                case BiomeId.Moor: return VoxelMaterial.Heath;
                default: return VoxelMaterial.Sand;
            }
        }

        public static bool IsSolid(VoxelMaterial m) => m != VoxelMaterial.Air && m != VoxelMaterial.Water;
    }

    /// <summary>一个 chunk 的体素：一整根柱子，从 MinY 到 MinY + SizeY。</summary>
    public sealed class VoxelChunk
    {
        public readonly int ChunkX, ChunkZ, Size, MinY, SizeY;
        public readonly byte[] Material;
        public readonly byte[] Shape;

        public VoxelChunk(int chunkX, int chunkZ, int size, int minY, int sizeY)
        {
            ChunkX = chunkX; ChunkZ = chunkZ; Size = size; MinY = minY; SizeY = sizeY;
            Material = new byte[size * size * sizeY];
            Shape = new byte[Material.Length];
        }

        public int Index(int x, int y, int z) => ((y - MinY) * Size + z) * Size + x;

        public VoxelMaterial GetMaterial(int x, int y, int z)
        {
            if (y < MinY || y >= MinY + SizeY) return VoxelMaterial.Air;
            return (VoxelMaterial)Material[Index(x, y, z)];
        }

        public VoxelShape GetShape(int x, int y, int z)
        {
            if (y < MinY || y >= MinY + SizeY) return VoxelShape.Full;
            return (VoxelShape)Shape[Index(x, y, z)];
        }

        public void Set(int x, int y, int z, VoxelMaterial m, VoxelShape s = VoxelShape.Full)
        {
            int i = Index(x, y, z);
            Material[i] = (byte)m;
            Shape[i] = (byte)s;
        }
    }

    /// <summary>阶段 B：chunk 级三维——把区域二维结果体素化（地表、悬崖面、斜坡、地下岩层）。</summary>
    public sealed class RegionVoxels
    {
        public readonly RegionMap Map;
        public readonly VoxelChunk[] Chunks;
        public readonly int MinY, SizeY;
        /// <summary>路面遮罩（RoadSurface.BuildMask），第一次画网格时算好缓存。</summary>
        public float[] RoadMask;
        /// <summary>坡道口（RoadSurface.BuildMouths），外面的路接坡道时收窄用；和 RoadMask 一起缓存。</summary>
        public System.Collections.Generic.List<RoadSurface.Mouth> RoadMouths;

        public RegionVoxels(RegionMap map)
        {
            Map = map;
            var s = map.Settings;
            map.HeightRange(out _, out int max);
            MinY = s.MinY;
            SizeY = max + 2 - MinY;
            Chunks = new VoxelChunk[s.ChunksX * s.ChunksZ];
            var surfaceNoise = new Fbm(Hash.Derive(s.Seed, "voxel.surfaceMix"), 7, 2);
            for (int cz = 0; cz < s.ChunksZ; cz++)
                for (int cx = 0; cx < s.ChunksX; cx++)
                    Chunks[cz * s.ChunksX + cx] = Build(cx, cz, surfaceNoise);
        }

        public VoxelChunk GetChunk(int cx, int cz) => Chunks[cz * Map.Settings.ChunksX + cx];

        /// <summary>按区域格子坐标取体素；区域外夹到边缘（不生成外围裙边）。</summary>
        public void Get(int x, int y, int z, out VoxelMaterial m, out VoxelShape s)
        {
            int size = Map.Settings.ChunkSize;
            x = Grid.Clamp(x, 0, Map.W - 1);
            z = Grid.Clamp(z, 0, Map.H - 1);
            if (y < MinY) { m = VoxelMaterial.DeepRock; s = VoxelShape.Full; return; }
            var c = GetChunk(x / size, z / size);
            m = c.GetMaterial(x % size, y, z % size);
            s = c.GetShape(x % size, y, z % size);
        }

        VoxelChunk Build(int cx, int cz, Fbm surfaceNoise)
        {
            var s = Map.Settings;
            int size = s.ChunkSize;
            var chunk = new VoxelChunk(cx, cz, size, MinY, SizeY);
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = cx * size + lx, z = cz * size + lz;
                    int i = Map.Index(x, z);
                    int h = Map.Height[i];
                    var biome = PickSurfaceBiome(i, s.OriginX + x + 0.5, s.OriginZ + z + 0.5, surfaceNoise);
                    var surface = VoxelPalette.Surface(biome);
                    if (Map.Has(i, CellFlags.Scree) || Map.Has(i, CellFlags.Road)) surface = VoxelMaterial.Gravel;
                    bool water = Map.HasWater(i);
                    if (water) surface = Map.Has(i, CellFlags.River) ? VoxelMaterial.Gravel : VoxelMaterial.Mud;
                    bool rocky = biome == BiomeId.Mountain;
                    for (int y = MinY; y < h; y++)
                    {
                        int depth = h - 1 - y;
                        VoxelMaterial m;
                        if (depth == 0) m = surface;
                        else if (depth <= 1 && !rocky) m = VoxelMaterial.Soil;
                        else if (depth <= 12) m = VoxelMaterial.Rock;
                        else m = VoxelMaterial.DeepRock;
                        chunk.Set(lx, y, lz, m);
                    }
                    int slope = Map.Slope[i];
                    if (slope >= 0 && h >= MinY && h < MinY + SizeY)
                        chunk.Set(lx, h, lz, surface, (VoxelShape)(slope + 1));
                    if (water)
                        for (int y = h; y < Map.Water[i]; y++) chunk.Set(lx, y, lz, VoxelMaterial.Water);
                    if (Map.HasSpan(i))
                    {
                        // 悬空层：桥面（路面是碎石）或隧道顶（岩石，顶面是原地表）
                        int top = Map.SpanTop[i];
                        for (int y = Map.SpanBottom[i]; y < top; y++)
                        {
                            if (y < MinY || y >= MinY + SizeY) continue;
                            var m = y == top - 1 ? (Map.SpanKind[i] == 1 ? VoxelMaterial.Gravel : VoxelPalette.Surface(biome)) : VoxelMaterial.Rock;
                            chunk.Set(lx, y, lz, m);
                        }
                    }
                }
            return chunk;
        }

        /// <summary>渐变带里按权重 + 低频噪声挑地表 Biome，形成斑块状过渡。</summary>
        BiomeId PickSurfaceBiome(int i, double gx, double gz, Fbm noise)
        {
            double r = noise.Sample(gx, gz) * 0.5 + 0.5;
            if (r < 0) r = 0; else if (r > 0.9999) r = 0.9999;
            double sum = 0;
            for (int b = 0; b < BiomeTable.Count; b++) sum += Map.Weights[b][i];
            if (sum <= 0) return (BiomeId)Map.Biome[i];
            double acc = 0;
            for (int b = 0; b < BiomeTable.Count; b++)
            {
                acc += Map.Weights[b][i] / sum;
                if (r < acc) return (BiomeId)b;
            }
            return (BiomeId)Map.Biome[i];
        }
    }
}
