using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wort.Generation;

namespace Wort.Tools
{
    /// <summary>
    /// 模块化瓦片选件验证：dotnet run -c Release -- --tiles [--out DIR]
    /// 在空白测试地（TestGround）上搭几种典型形状，用 TilePlacer 选瓦片，输出 tiles.json（每块：名字、位置、旋转），
    /// 再用 Tools/TilesetPreview/render_scene.py --placements 按真实瓦片拼起来渲染。也打印用到的瓦片名，检查有没有套件里缺的。
    /// </summary>
    static class TilesMode
    {
        public static int Run(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var map = TestGround.Create(1, 4, null);
            void Rect(int x0, int z0, int x1, int z1, int h) { for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) map.Height[map.Index(x, z)] = h; }
            // 1) 十字台 2 层（臂宽 2）
            Rect(2, 6, 9, 7, 6); Rect(5, 3, 6, 10, 6);
            // 2) 3 层台地，南面一个凹口，上面再叠一级，西北角缺一块
            Rect(13, 3, 22, 10, 7); Rect(16, 3, 17, 4, 4); Rect(19, 7, 21, 9, 8); Rect(13, 10, 14, 10, 4);
            // 3) 单根柱子 3 层、1 层的小台阶、两块只在角上相接的台地
            Rect(26, 4, 26, 4, 7); Rect(25, 8, 27, 9, 5); Rect(29, 3, 31, 5, 6); Rect(32, 6, 34, 8, 6);
            // 4) 下凹 2 层的坑
            Rect(3, 13, 8, 16, 2);
            // 射线检测自查（VoxelPick）：期望值写在后面
            void Ray(string what, float ox, float oy, float oz, float dx, float dy, float dz, string expect)
            {
                string got = VoxelPick.Raycast(map, ox, oy, oz, dx, dy, dz, 500, out var h) ? $"X{h.X} Z{h.Z} Y{h.Y} 面{h.Face}" : "没打到";
                Console.WriteLine($"  射线 {what}: {got}  {(got == expect ? "✓" : "✗ 期望 " + expect)}");
            }
            Ray("正上往下打十字台", 5.5f, 20, 6.5f, 0, -1, 0, "X5 Z6 Y5 面4");
            Ray("从南边水平打十字台南臂", 5.5f, 5.5f, 0.5f, 0, 0, 1, "X5 Z3 Y5 面2");
            Ray("从东边水平打（先碰到 x 32–34 那块）", 40, 4.5f, 6.5f, -1, 0, 0, "X34 Z6 Y4 面1");
            Ray("斜着往下打平地", 1.5f, 10, 18.5f, 0.3f, -1, 0.1f, "X3 Z19 Y3 面4");
            Ray("在坑里往南打坑壁（朝北的面）", 5.5f, 3, 15.5f, 0, 0, -1, "X5 Z12 Y3 面0");
            Ray("斜着往下打十字台东臂侧面", 12.5f, 6.6f, 6.5f, -1, -0.4f, 0, "X9 Z6 Y5 面1");
            Ray("斜着往下、越过侧面落到顶面", 14, 9.5f, 6.5f, -1, -0.8f, 0, "X9 Z6 Y5 面4");
            if (VoxelPick.Raycast(map, 5.5f, 20, 6.5f, 0, -1, 0, 500, out var hh))
            {
                VoxelPick.PlaceTarget(map, hh, out int px, out int pz, out int ph);
                VoxelPick.RemoveTarget(map, hh, 0, out int rx, out int rz, out int rh);
                Console.WriteLine($"  放块 → ({px},{pz}) 高 {ph}（期望 (5,6) 7）；挖块 → ({rx},{rz}) 高 {rh}（期望 (5,6) 5）");
            }
            if (VoxelPick.Raycast(map, 5.5f, 5.5f, 0.5f, 0, 0, 1, 500, out hh))
            {
                VoxelPick.PlaceTarget(map, hh, out int px, out int pz, out int ph);
                VoxelPick.RemoveTarget(map, hh, 0, out int rx, out int rz, out int rh);
                Console.WriteLine($"  侧面放块 → ({px},{pz}) 高 {ph}（期望 (5,2) 6）；侧面挖块 → ({rx},{rz}) 高 {rh}（期望 (5,3) 5）");
            }
            var list = new List<TilePlacement>();
            TilePlacer.Chunk(map, null, 0, 0, list);
            var names = new SortedDictionary<string, int>();
            var sb = new StringBuilder("[\n");
            for (int k = 0; k < list.Count; k++)
            {
                var p = list[k];
                if (!names.ContainsKey(p.Name)) names[p.Name] = 0;
                names[p.Name]++;
                // 只导出搭了东西的区域附近（平地太多，渲染慢）
                if (p.X > 37 || p.Z > 19) continue;
                sb.Append($"  {{\"name\": \"{p.Name}\", \"x\": {p.X}, \"y\": {p.Y}, \"z\": {p.Z}, \"rot\": {p.Rot}, \"foot\": {(p.Foot ? 1 : 0)}}},\n");
            }
            if (sb[sb.Length - 2] == ',') sb.Remove(sb.Length - 2, 1);
            sb.Append("]\n");
            File.WriteAllText(Path.Combine(outDir, "tiles.json"), sb.ToString());
            Console.WriteLine($"tiles: {list.Count} 块，{names.Count} 种");
            foreach (var kv in names) Console.WriteLine($"  {kv.Key} × {kv.Value}");
            return 0;
        }
    }
}
