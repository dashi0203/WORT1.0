using System.Collections.Generic;

namespace Wort.Generation
{
    public sealed class RegionSettings
    {
        public uint Seed = 1;
        public int ChunksX = 8;
        public int ChunksZ = 8;
        public int ChunkSize = 64;
        /// <summary>区域左下角的世界坐标（米）。噪声都按世界坐标取值。</summary>
        public int OriginX;
        public int OriginZ;
        /// <summary>地下最深处（层）。</summary>
        public int MinY = -16;

        public int Width => ChunksX * ChunkSize;
        public int Depth => ChunksZ * ChunkSize;
    }

    /// <summary>格子标记位。</summary>
    [System.Flags]
    public enum CellFlags : ushort
    {
        None = 0,
        Mesa = 1,          // 高台
        Spire = 2,         // 孤峰
        Ramp = 4,          // 坡道
        River = 8,         // 河道
        Pond = 16,         // 池塘 / 浅洼 / 湖
        Hidden = 32,       // 悬崖背面被遮挡的一条（尽量少放资源）
        Unreachable = 64,  // 连通性修复后仍走不到
        CliffEdge = 128,   // 与相邻格高差 ≥ 2
        Scree = 256,       // 孤峰脚下的碎石台阶
        Outlier = 512,     // 山地外缘的残丘
        Gully = 1024,      // 冲沟
        // 以下为 layout 管线（LayoutGenerator）使用
        Road = 2048,       // 道路 / 小径
        Trap = 4096,       // 检查：进得去、回不到住所
        Leak = 8192,       // 检查：layout 出入口以外多出来的通路
        Site = 16384,      // 关键地点占地
        Gate = 32768,      // 出入口（坡道 / 台阶）
    }

    public struct RampInfo
    {
        /// <summary>坡道顶端外侧第一格（高处那一侧的边缘格）。</summary>
        public int TopX, TopZ;
        /// <summary>下坡方向（Grid.DX4/DZ4 的索引，0=北 1=东 2=南 3=西）。</summary>
        public int DownDir;
        public int Width;
        public int Length;
    }

    public struct DropPoint
    {
        public int X, Z;
        /// <summary>水面落差（层）。≥ 2 视为瀑布候选。</summary>
        public int Drop;
    }

    /// <summary>阶段 A（区域级二维）的结果。坐标为区域内格子坐标，x 向东，z 向北。</summary>
    public sealed class RegionMap
    {
        public const int NoWater = int.MinValue;

        public readonly RegionSettings Settings;
        public readonly int W, H;

        /// <summary>主导 Biome。</summary>
        public readonly byte[] Biome;
        /// <summary>Biome 权重（渐变带），[biome][cell]。</summary>
        public readonly float[][] Weights;
        /// <summary>地面高度（层）：固体占据 y &lt; Height。</summary>
        public readonly int[] Height;
        /// <summary>水面高度（层），无水为 NoWater。水体占据 Height ≤ y &lt; Water。</summary>
        public readonly int[] Water;
        /// <summary>斜坡上坡方向（-1 无，0=北 1=东 2=南 3=西）。斜坡体素位于 y = Height。</summary>
        public readonly sbyte[] Slope;
        /// <summary>台地级数。</summary>
        public readonly byte[] Tier;
        public readonly CellFlags[] Flags;

        /// <summary>
        /// 悬空层（桥面、隧道顶）：每格最多一段，固体占据 SpanBottom ≤ y &lt; SpanTop。
        /// 没有悬空层时为 null（旧生成器不用）。
        /// </summary>
        public int[] SpanBottom, SpanTop;
        /// <summary>悬空层种类：1 = 桥（顶面是路），2 = 隧道顶（顶面是原地表）。</summary>
        public byte[] SpanKind;

        /// <summary>路面类型（0 无，1 小径，2 道路），和 <see cref="CellFlags.Road"/> 一起用：标记表示“这一格有路”（渲染只看它），
        /// 类型留给以后的贴图 / 宽度。不进 checksum。没有路时为 null。</summary>
        public byte[] RoadKind;

        public void EnsureRoadKind()
        {
            if (RoadKind == null) RoadKind = new byte[Height.Length];
        }

        public const byte RoadNone = 0, RoadPath = 1, RoadMain = 2;

        public readonly List<RampInfo> Ramps = new List<RampInfo>();
        public readonly List<DropPoint> Drops = new List<DropPoint>();
        public readonly List<int> RiverPath = new List<int>();

        public int SpawnX, SpawnZ;
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>();

        public RegionMap(RegionSettings s)
        {
            Settings = s;
            W = s.Width;
            H = s.Depth;
            int n = W * H;
            Biome = new byte[n];
            Weights = new float[BiomeTable.Count][];
            for (int b = 0; b < BiomeTable.Count; b++) Weights[b] = new float[n];
            Height = new int[n];
            Water = new int[n];
            Slope = new sbyte[n];
            Tier = new byte[n];
            Flags = new CellFlags[n];
            for (int i = 0; i < n; i++) { Water[i] = NoWater; Slope[i] = -1; }
        }

        public int Index(int x, int z) => z * W + x;
        public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < W && z < H;
        public bool HasWater(int i) => Water[i] != NoWater && Water[i] > Height[i];

        /// <summary>地表最高处（含水面）。</summary>
        public int SurfaceTop(int i) => HasWater(i) ? Water[i] : Height[i];

        public bool Has(int i, CellFlags f) => (Flags[i] & f) != 0;

        public bool HasSpan(int i) => SpanTop != null && SpanTop[i] > SpanBottom[i];

        public void EnsureSpans()
        {
            if (SpanTop != null) return;
            SpanBottom = new int[Height.Length];
            SpanTop = new int[Height.Length];
            SpanKind = new byte[Height.Length];
        }

        /// <summary>
        /// 结果校验和（FNV-1a，覆盖高度、水面、斜坡、标记）。
        /// 云端预览与 Unity 里的值相同，说明两边生成结果逐格一致。
        /// </summary>
        public uint Checksum()
        {
            uint h = 2166136261U;
            void Add(int v) { unchecked { for (int k = 0; k < 4; k++) { h ^= (byte)(v >> (8 * k)); h *= 16777619U; } } }
            for (int i = 0; i < Height.Length; i++)
            {
                Add(Height[i]); Add(Water[i]); Add(Slope[i]); Add((int)Flags[i]);
            }
            if (SpanTop != null)
                for (int i = 0; i < Height.Length; i++)
                    if (HasSpan(i)) { Add(i); Add(SpanBottom[i]); Add(SpanTop[i]); Add(SpanKind[i]); }
            return h;
        }

        /// <summary>区域内高度范围（含斜坡、水）。</summary>
        public void HeightRange(out int min, out int max)
        {
            min = int.MaxValue; max = int.MinValue;
            for (int i = 0; i < Height.Length; i++)
            {
                int top = Height[i] + (Slope[i] >= 0 ? 1 : 0);
                if (HasWater(i) && Water[i] > top) top = Water[i];
                if (HasSpan(i) && SpanTop[i] > top) top = SpanTop[i];
                if (Height[i] < min) min = Height[i];
                if (top > max) max = top;
            }
        }
    }
}
