using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 碰撞盒合并：一个 chunk 里同高的格子按矩形贪心合并，每个矩形一个碰撞盒（从世界最低处到格顶）。
    /// 碰撞仍是 1m 体素格（只看 Height），斜坡另用 CliffMesher.SlopesOnly 的小网格。
    /// </summary>
    public static class ColliderBoxes
    {
        public struct Box { public int X0, Z0, X1, Z1, Top; } // 世界格坐标，X1 / Z1 不含

        public static List<Box> Build(RegionMap map, int cx, int cz)
        {
            int size = map.Settings.ChunkSize, ox = cx * size, oz = cz * size;
            var list = new List<Box>();
            var done = new bool[size * size];
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    if (done[lz * size + lx] || ox + lx >= map.W || oz + lz >= map.H) continue;
                    int h = map.Height[map.Index(ox + lx, oz + lz)];
                    bool Same(int ix, int iz) => ox + ix < map.W && oz + iz < map.H && !done[iz * size + ix] && map.Height[map.Index(ox + ix, oz + iz)] == h;
                    int w = 1; while (lx + w < size && Same(lx + w, lz)) w++;
                    int d = 1;
                    for (; lz + d < size; d++) { bool ok = true; for (int i = 0; i < w; i++) if (!Same(lx + i, lz + d)) { ok = false; break; } if (!ok) break; }
                    for (int j = 0; j < d; j++) for (int i = 0; i < w; i++) done[(lz + j) * size + lx + i] = true;
                    list.Add(new Box { X0 = ox + lx, Z0 = oz + lz, X1 = ox + lx + w, Z1 = oz + lz + d, Top = h });
                }
            return list;
        }
    }
}
