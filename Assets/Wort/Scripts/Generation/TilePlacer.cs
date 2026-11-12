using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>一块模块化瓦片的摆放（Docs/TilesetSpec.md）。</summary>
    public struct TilePlacement
    {
        /// <summary>用哪一套瓦片（TestGround 里每格记的套件编号）。</summary>
        public byte Set;
        /// <summary>瓦片名，与 Tileset 里的网格 / Prefab 同名：Top_E{E}_C{C}、{Front|Side}_{Crown|Repeat}_{A|B}。</summary>
        public string Name;
        /// <summary>原点位置（世界格坐标；顶面 y = 格高度，墙件 y = 这一层的底）。</summary>
        public int X, Y, Z;
        /// <summary>绕 y 轴转 Rot × 90°（Unity 的正方向：从上往下看顺时针）。</summary>
        public byte Rot;
    }

    /// <summary>
    /// 按体素高度选模块化瓦片（Docs/TilesetSpec.md 第 1 节，样板 Art/Tilesets/Limestone）：
    /// - 每格一块顶面 Top_E{E}_C{C}：E = 低于本格的四邻（东 1、北 2、西 4、南 8），C = 四邻都不低、但斜邻低于本格时补的凹角（东北 1、西北 2、西南 4、东南 8）；
    /// - 每个低于本格的四邻方向，从邻格高度到本格高度每层一块墙件：最上一层 Crown，以下 Repeat；朝南（镜头看得见）用 Front，其余用 Side；A / B 按位置哈希。
    ///   墙件原点在左下角、沿本地 +X、正面朝本地 −Z：南面不转，西面转 90°，北面 180°，东面 270°。
    /// 墙件属于高的那一格（用它的套件）。地图外当作同高（不画边界墙）。斜坡和水暂不处理（负责人 2026-11-12）。
    /// </summary>
    public static class TilePlacer
    {
        static readonly int[] EBit = { 2, 1, 8, 4 }; // Grid.DX4 顺序：北 东 南 西

        static int H(RegionMap m, int x, int z, int self)
        {
            if (!m.InBounds(x, z)) return self;
            return m.Height[z * m.W + x];
        }

        public static string TopName(int e, int c) => $"Top_E{e:X}_C{c:X}";

        /// <summary>格 (x, z) 的顶面遮罩。</summary>
        public static void TopMask(RegionMap m, int x, int z, out int e, out int c)
        {
            int h = m.Height[z * m.W + x];
            e = 0; c = 0;
            for (int s = 0; s < 4; s++)
                if (H(m, x + Grid.DX4[s], z + Grid.DZ4[s], h) < h) e |= EBit[s];
            bool Low(int dx, int dz) => H(m, x + dx, z + dz, h) < h;
            if ((e & (1 | 2)) == 0 && Low(1, 1)) c |= 1;   // 东北
            if ((e & (4 | 2)) == 0 && Low(-1, 1)) c |= 2;  // 西北
            if ((e & (4 | 8)) == 0 && Low(-1, -1)) c |= 4; // 西南
            if ((e & (1 | 8)) == 0 && Low(1, -1)) c |= 8;  // 东南
        }

        /// <summary>chunk (cx, cz) 里所有格的瓦片。sets = 每格的套件编号（null 时都用 0）。</summary>
        public static void Chunk(RegionMap m, byte[] sets, int cx, int cz, List<TilePlacement> outList)
        {
            int size = m.Settings.ChunkSize;
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = cx * size + lx, z = cz * size + lz;
                    if (!m.InBounds(x, z)) continue;
                    Cell(m, sets, x, z, outList);
                }
        }

        public static void Cell(RegionMap m, byte[] sets, int x, int z, List<TilePlacement> outList)
        {
            int i = z * m.W + x, h = m.Height[i];
            byte set = sets != null ? sets[i] : (byte)0;
            TopMask(m, x, z, out int e, out int c);
            outList.Add(new TilePlacement { Set = set, Name = TopName(e, c), X = x, Y = h, Z = z, Rot = 0 });
            for (int s = 0; s < 4; s++)
            {
                int hn = H(m, x + Grid.DX4[s], z + Grid.DZ4[s], h);
                if (hn >= h) continue;
                bool south = s == 2;
                // 原点（左下角）与旋转：南 0°，西 90°，北 180°，东 270°
                int ox, oz; byte rot;
                switch (s)
                {
                    case 2: ox = x; oz = z; rot = 0; break;          // 南
                    case 3: ox = x; oz = z + 1; rot = 1; break;      // 西
                    case 0: ox = x + 1; oz = z + 1; rot = 2; break;  // 北
                    default: ox = x + 1; oz = z; rot = 3; break;     // 东
                }
                for (int y = hn; y < h; y++)
                {
                    bool crown = y == h - 1;
                    bool b = (Hash.Of(x * 4 + s, y, (uint)(z * 131 + 7)) & 1) == 1;
                    string name = (south ? "Front_" : "Side_") + (crown ? "Crown_" : "Repeat_") + (b ? "B" : "A");
                    outList.Add(new TilePlacement { Set = set, Name = name, X = ox, Y = y, Z = oz, Rot = rot });
                }
            }
        }
    }
}
