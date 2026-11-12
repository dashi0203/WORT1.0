using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 地基外观第二代原型（负责人 2026-11-12 参考图：人类区粗石、沼泽层板、森林圆石）：崖面不再是贴在墙上的高度图，
    /// 而是一个三维有向距离场（SDF）：
    /// - 底子：体素柱的并集（边倒圆）；
    /// - 人类区 / 森林：墙面往里收，墙上堆一块块石头（人类区 = 有棱角的凸多面体粗石，森林 = 圆角石块），石头能包住外角；
    /// - 沼泽：墙面往里收，叠一层层圆边泥板；每层的外轮廓按世界坐标的二维噪声前后起伏（波浪在水平面上），下层比上层凸出多；
    /// 再用 Surface Nets（0.1m 网格）把距离场变成网格。只在有高差的格附近算，其余平地照旧按格画平面。
    /// 只改画面：碰撞、checksum 不变。只用加减乘除和开方，不用 System.Random / Math.Sin 等。
    /// </summary>
    public static class CliffSdf
    {
        public const float S = 0.1f;      // 网格边长（m）
        const int PerCell = 10;           // 每格 10 个网格
        const float Round = 0.06f;        // 柱体边倒圆
        const float Inset = 0.14f;        // 人类区 / 森林墙面往里收（石头嵌在里面）
        const float SwampInset = 0.1f;

        sealed class Rock
        {
            public float X, Y, Z, R;       // 中心、包围半径
            public float[] P;              // 人类区：平面 (nx, ny, nz, d) 若干组；森林：null
            public float Tx, Tz, Nx, Nz;   // 墙的坐标系（沿墙、朝外）
            public float A, B, E, Rr;      // 森林：圆角盒半尺寸（沿墙、竖直、朝外）与圆角
            public float Cap;              // 石头不超过这个高度（崖顶下）
            public uint Facet;             // 人类区：表面细分刻面的种子（0 = 不细分）
        }

        sealed class Ctx
        {
            public RegionMap M; public LookField Look; public uint Seed;
            public List<Rock> Rocks = new List<Rock>();
            public Dictionary<long, List<int>> Grid = new Dictionary<long, List<int>>();
        }

        static long Key(int a, int b) => ((long)a << 32) ^ (uint)b;
        static float Unit(uint h) => (h & 0xFFFF) / 65535f;
        static float Sat(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        static int H(RegionMap m, int x, int z)
        {
            if (x < 0) x = 0; if (z < 0) z = 0; if (x >= m.W) x = m.W - 1; if (z >= m.H) z = m.H - 1;
            return m.Height[z * m.W + x];
        }

        /// <summary>生成 [x0, x1) × [z0, z1) 格范围的地基网格（世界坐标减去 (ox, oz)）。</summary>
        public static MeshData Build(RegionMap map, LookField look, uint seed, int x0, int z0, int x1, int z1, int ox, int oz)
        {
            var c = new Ctx { M = map, Look = look, Seed = seed };
            var mesh = new MeshData();
            // 1) 有高差的格（八邻任一不同高）：这些格用距离场画
            var band = new HashSet<long>();
            for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    int h = H(map, x, z);
                    for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if (H(map, x + dx, z + dz) != h) { band.Add(Key(x, z)); dz = 2; break; }
                }
            // 2) 石头：每个露出的墙面按样式摆
            for (int z = z0 - 1; z <= z1; z++)
                for (int x = x0 - 1; x <= x1; x++)
                    for (int s = 0; s < 4; s++)
                    {
                        int h = H(map, x, z), hn = H(map, x + Grid.DX4[s], z + Grid.DZ4[s]);
                        if (hn >= h) continue;
                        var st = look.MainStyle(x + 0.5, z + 0.5);
                        if (st == CliffStyle.Human || st == CliffStyle.Forest) PlaceRocks(c, x, z, s, h, hn, st);
                    }
            // 3) 平地格：整格一块顶面（略往距离场区域里伸，靠重叠盖住接缝）
            foreach (var _ in new int[1])
                for (int z = z0; z < z1; z++)
                    for (int x = x0; x < x1; x++)
                    {
                        if (band.Contains(Key(x, z))) continue;
                        float h = H(map, x, z);
                        Emit(mesh, ox, oz, x, h, z, 0, 1, 0, x, h, z + 1, 0, 1, 0, x + 1, h, z + 1, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0);
                        Emit(mesh, ox, oz, x, h, z, 0, 1, 0, x + 1, h, z + 1, 0, 1, 0, x + 1, h, z, 0, 1, 0, 0, 0, 0, 1, 1, 1, 0);
                    }
            // 4) 距离场采样：有高差的格（外扩 1 个网格），每列竖直范围 = 附近格高度范围 ± 0.5m
            var cols = new Dictionary<long, (int j0, float[] v)>();
            foreach (var key in band)
            {
                int cx = (int)(key >> 32), cz = (int)(uint)key;
                int lo = int.MaxValue, hi = int.MinValue;
                for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++) { int hh = H(map, cx + dx, cz + dz); lo = Math.Min(lo, hh); hi = Math.Max(hi, hh); }
                int j0 = (int)Math.Floor((lo - 0.5f) / S), j1 = (int)Math.Ceiling((hi + 0.5f) / S);
                for (int k = cz * PerCell - 1; k <= (cz + 1) * PerCell + 1; k++)
                    for (int i = cx * PerCell - 1; i <= (cx + 1) * PerCell + 1; i++)
                    {
                        long ck = Key(i, k);
                        if (cols.TryGetValue(ck, out var ex) && ex.j0 <= j0 && ex.j0 + ex.v.Length - 1 >= j1) continue;
                        int a = j0, b = j1;
                        if (cols.TryGetValue(ck, out ex)) { a = Math.Min(a, ex.j0); b = Math.Max(b, ex.j0 + ex.v.Length - 1); }
                        cols[ck] = (a, new float[b - a + 1]);
                    }
            }
            var colKeys = new List<long>(cols.Keys);
            System.Threading.Tasks.Parallel.ForEach(colKeys, ck =>
            {
                var (j0, v) = cols[ck];
                int i = (int)(ck >> 32), k = (int)(uint)ck;
                for (int j = 0; j < v.Length; j++) v[j] = Sdf(c, i * S, (j0 + j + 0.5f) * S, k * S, out _);
            });
            float Val(int i, int j, int k, out bool ok)
            {
                ok = false;
                if (!cols.TryGetValue(Key(i, k), out var cl)) return 0;
                int jj = j - cl.j0;
                if (jj < 0 || jj >= cl.v.Length) return 0;
                ok = true; return cl.v[jj];
            }
            // 5) Surface Nets：每个跨过表面的立方体一个顶点（各棱交点的平均），每条跨过表面的棱一个四边形
            var vid = new Dictionary<(int, int, int), (float x, float y, float z)>();
            foreach (var ck in colKeys)
            {
                int i = (int)(ck >> 32), k = (int)(uint)ck;
                if (!cols.ContainsKey(Key(i + 1, k)) || !cols.ContainsKey(Key(i, k + 1)) || !cols.ContainsKey(Key(i + 1, k + 1))) continue;
                var (j0, v) = cols[ck];
                for (int j = j0; j < j0 + v.Length - 1; j++)
                {
                    var d = new float[8]; bool all = true;
                    for (int q = 0; q < 8; q++) { d[q] = Val(i + (q & 1), j + ((q >> 1) & 1), k + (q >> 2), out bool ok); if (!ok) { all = false; break; } }
                    if (!all) continue;
                    int neg = 0; for (int q = 0; q < 8; q++) if (d[q] < 0) neg++;
                    if (neg == 0 || neg == 8) continue;
                    float sx = 0, sy = 0, sz = 0; int n = 0;
                    for (int q = 0; q < 8; q++)
                        for (int ax = 0; ax < 3; ax++)
                        {
                            int bit = 1 << ax; if ((q & bit) != 0) continue;
                            int q2 = q | bit;
                            if ((d[q] < 0) == (d[q2] < 0)) continue;
                            float t = d[q] / (d[q] - d[q2]);
                            float px = (q & 1), py = (q >> 1) & 1, pz = q >> 2;
                            float qx = (q2 & 1), qy = (q2 >> 1) & 1, qz = q2 >> 2;
                            sx += px + (qx - px) * t; sy += py + (qy - py) * t; sz += pz + (qz - pz) * t; n++;
                        }
                    vid[(i, j, k)] = ((i + sx / n) * S, (j + 0.5f + sy / n) * S, (k + sz / n) * S);
                }
            }
            foreach (var ck in colKeys)
            {
                int i = (int)(ck >> 32), k = (int)(uint)ck;
                var (j0, v) = cols[ck];
                for (int j = j0; j < j0 + v.Length; j++)
                {
                    float d0 = v[j - j0];
                    for (int ax = 0; ax < 3; ax++)
                    {
                        int di = ax == 0 ? 1 : 0, dj = ax == 1 ? 1 : 0, dk = ax == 2 ? 1 : 0;
                        float d1 = Val(i + di, j + dj, k + dk, out bool ok);
                        if (!ok || (d0 < 0) == (d1 < 0)) continue;
                        // 共享这条棱的 4 个立方体
                        (int, int, int) c0, c1, c2, c3;
                        if (ax == 0) { c0 = (i, j - 1, k - 1); c1 = (i, j, k - 1); c2 = (i, j, k); c3 = (i, j - 1, k); }
                        else if (ax == 1) { c0 = (i - 1, j, k - 1); c1 = (i, j, k - 1); c2 = (i, j, k); c3 = (i - 1, j, k); }
                        else { c0 = (i - 1, j - 1, k); c1 = (i, j - 1, k); c2 = (i, j, k); c3 = (i - 1, j, k); }
                        if (!vid.TryGetValue(c0, out var p0) || !vid.TryGetValue(c1, out var p1) || !vid.TryGetValue(c2, out var p2) || !vid.TryGetValue(c3, out var p3)) continue;
                        // 法线朝外（距离场为正的一侧）
                        float hx = di, hy = dj, hz = dk; if (d0 > 0) { hx = -hx; hy = -hy; hz = -hz; }
                        Quad(c, mesh, ox, oz, hx, hy, hz, p0, p1, p2, p3);
                    }
                }
            }
            while (mesh.Road.Count < mesh.VertexCount) mesh.Road.Add(0);
            return mesh;
        }

        static void Quad(Ctx c, MeshData mesh, int ox, int oz, float hx, float hy, float hz,
            (float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) d, (float x, float y, float z) e)
        {
            Tri(c, mesh, ox, oz, hx, hy, hz, a, b, d);
            Tri(c, mesh, ox, oz, hx, hy, hz, a, d, e);
        }

        static void Tri(Ctx c, MeshData mesh, int ox, int oz, float hx, float hy, float hz,
            (float x, float y, float z) a, (float x, float y, float z) b, (float x, float y, float z) d)
        {
            float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, wx = d.x - a.x, wy = d.y - a.y, wz = d.z - a.z;
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-9f) return;
            nx /= l; ny /= l; nz /= l;
            if (nx * hx + ny * hy + nz * hz < 0) { nx = -nx; ny = -ny; nz = -nz; var t = b; b = d; d = t; }
            float Ao((float x, float y, float z) p) => Occlusion(c, p.x, p.y, p.z, nx, ny, nz);
            float Bl((float x, float y, float z) p) { Sdf(c, p.x, p.y, p.z, out bool rock); return rock || ny < 0.6f ? 1 : 0; }
            Emit(mesh, ox, oz, a.x, a.y, a.z, nx, ny, nz, b.x, b.y, b.z, nx, ny, nz, d.x, d.y, d.z, nx, ny, nz,
                Bl(a), Bl(b), Bl(d), Ao(a), Ao(b), Ao(d), 0);
        }

        static void Emit(MeshData m, int ox, int oz,
            float ax, float ay, float az, float anx, float any, float anz,
            float bx, float by, float bz, float bnx, float bny, float bnz,
            float cx, float cy, float cz, float cnx, float cny, float cnz,
            float ba, float bb, float bc, float oa, float ob, float oc, float acc)
        {
            int v0 = m.VertexCount;
            void V(float x, float y, float z, float nx, float ny, float nz, float bl, float ao)
            {
                m.Positions.Add(x - ox); m.Positions.Add(y); m.Positions.Add(z - oz);
                m.Normals.Add(nx); m.Normals.Add(ny); m.Normals.Add(nz);
                m.Blend.Add(bl); m.Ao.Add(ao); m.Accent.Add(acc);
            }
            V(ax, ay, az, anx, any, anz, ba, oa); V(bx, by, bz, bnx, bny, bnz, bb, ob); V(cx, cy, cz, cnx, cny, cnz, bc, oc);
            var list = m.Indices[(int)VoxelMaterial.Soil];
            list.Add(v0); list.Add(v0 + 1); list.Add(v0 + 2);
        }

        /// <summary>距离场暗部：沿法线往外 0.12m / 0.3m 处离表面越近，说明被周围挡得越多。</summary>
        static float Occlusion(Ctx c, float x, float y, float z, float nx, float ny, float nz)
        {
            float d1 = Sdf(c, x + nx * 0.12f, y + ny * 0.12f, z + nz * 0.12f, out _);
            float d2 = Sdf(c, x + nx * 0.3f, y + ny * 0.3f, z + nz * 0.3f, out _);
            return (0.35f + 0.65f * Sat(d1 / 0.12f)) * (0.6f + 0.4f * Sat(d2 / 0.3f));
        }

        // ---------------- 距离场 ----------------

        /// <summary>
        /// 体素柱并集的距离：体内 = max(到本格顶面的竖直距离, 本层二维距离)（都为负）；体外 = 附近 5×5 个柱体（不倒角）的最小距离。
        /// </summary>
        static float Columns(RegionMap m, float x, float y, float z)
        {
            int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
            int hc = H(m, cx, cz);
            if (y < hc) return Math.Max(y - hc, Foot(m, x, z, (int)Math.Floor(y)));
            float best = y - hc;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int gx = cx + dx, gz = cz + dz;
                    float h = H(m, gx, gz);
                    if (h <= y && (dx != 0 || dz != 0) && h <= hc) continue;
                    float qx = Math.Max(Math.Abs(x - (gx + 0.5f)) - 0.5f, 0), qz = Math.Max(Math.Abs(z - (gz + 0.5f)) - 0.5f, 0), qy = Math.Max(y - h, 0);
                    float d = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz);
                    if (d < best) best = d;
                }
            return best;
        }

        /// <summary>
        /// 某一高度层的二维有向距离：点 (x, z) 到“高度 &gt; level 的格”的边界的距离（里面为负）。附近 5×5 格。
        /// </summary>
        static float Foot(RegionMap m, float x, float z, int level)
        {
            int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
            bool inside = H(m, cx, cz) > level;
            float best = 9;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int gx = cx + dx, gz = cz + dz;
                    if ((H(m, gx, gz) > level) == inside) continue;
                    float qx = Math.Max(Math.Abs(x - (gx + 0.5f)) - 0.5f, 0), qz = Math.Max(Math.Abs(z - (gz + 0.5f)) - 0.5f, 0);
                    float d = (float)Math.Sqrt(qx * qx + qz * qz);
                    if (d < best) best = d;
                }
            return inside ? -best : best;
        }

        /// <summary>点 (x, y, z) 上方最近的“崖顶”：附近格中高于 y 的最低格高度（没有时为很大的数）。</summary>
        static float NextTop(RegionMap m, float x, float y, float z)
        {
            int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
            float best = 1e6f;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    float h = H(m, cx + dx, cz + dz);
                    if (h > y && h < best) best = h;
                }
            return best;
        }

        static float Sdf(Ctx c, float x, float y, float z, out bool rock)
        {
            rock = false;
            var m = c.M;
            float col = Columns(m, x, y, z);
            int level = (int)Math.Floor(y);
            var st = c.Look.MainStyle(x, z);
            // 点离墙较远（在平地上方 / 里面深处）时直接用柱体
            float foot = Foot(m, x, z, level);
            if (st == CliffStyle.Swamp) return Swamp(c, x, y, z, col, foot, ref rock);
            if (st != CliffStyle.Human && st != CliffStyle.Forest) return col;
            // 墙面往里收 Inset（崖顶下 0.14m 的唇边不收，石头从唇下长出来）
            float top = NextTop(m, x, y, z);
            float wall = top - y < 0.14f ? col : Math.Max(col, foot + (st == CliffStyle.Forest ? 0.07f : Inset));
            float best = wall;
            // 石头
            long gk = Key((int)Math.Floor(x), (int)Math.Floor(z));
            if (c.Grid.TryGetValue(gk, out var list))
                foreach (int ri in list)
                {
                    var r = c.Rocks[ri];
                    float dx = x - r.X, dy = y - r.Y, dz = z - r.Z;
                    if (dx * dx + dy * dy + dz * dz > (r.R + 0.2f) * (r.R + 0.2f)) continue;
                    float d;
                    if (r.P != null)
                    {
                        d = -9;
                        for (int q = 0; q < r.P.Length; q += 4) { float v = dx * r.P[q] + dy * r.P[q + 1] + dz * r.P[q + 2] - r.P[q + 3]; if (v > d) d = v; }
                        // 第二层：石头表面按细 Voronoi（约 0.15m）切成小刻面，每个刻面往里收 0–5cm（负责人：大石块里再细分、随机挤出）
                        if (d > -0.08f) d += Facets(dx * r.Tx + dz * r.Tz, dy, dx * r.Nx + dz * r.Nz, r.Facet);
                    }
                    else
                    {
                        float lu = dx * r.Tx + dz * r.Tz, ln = dx * r.Nx + dz * r.Nz;
                        float qx = Math.Abs(lu) - (r.A - r.Rr), qy = Math.Abs(dy) - (r.B - r.Rr), qz = Math.Abs(ln) - (r.E - r.Rr);
                        float mx = Math.Max(qx, 0), my = Math.Max(qy, 0), mz = Math.Max(qz, 0);
                        d = (float)Math.Sqrt(mx * mx + my * my + mz * mz) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0) - r.Rr;
                    }
                    d = Math.Max(d, y - r.Cap);
                    if (d < best) { best = d; rock = true; }
                }
            return best;
        }

        /// <summary>
        /// 沼泽：墙面往里收，叠一层层圆边泥板。层的上下界按世界高度（每层 0.3–0.45m，跨墙连续、水平）；
        /// 每层外轮廓 = 该层高度的二维距离 − 外凸量，外凸量按世界坐标二维噪声起伏（前后方向的波浪），下层凸得多、往上越来越平缓。
        /// </summary>
        static float Swamp(Ctx c, float x, float y, float z, float col, float foot, ref bool rock)
        {
            var m = c.M;
            float top = NextTop(m, x, y, z);
            float wall = Math.Max(col, foot + SwampInset);
            int band = Band(c.Seed, y, out float yb0, out float yb1);
            float best = wall;
            for (int k = band - 1; k <= band + 1; k++)
            {
                float lo, hi;
                if (k == band) { lo = yb0; hi = yb1; }
                else Band(c.Seed, k == band - 1 ? yb0 - 0.01f : yb1 + 0.01f, out lo, out hi);
                if (lo >= top) continue;
                if (hi > top) hi = top;
                float half = (hi - lo) * 0.5f, yc = (lo + hi) * 0.5f;
                if (half < 0.04f) continue;
                int lv = (int)Math.Floor(yc);
                float f2 = Foot(m, x, z, lv);
                // 这一层在墙上的相对高度：越往上外凸越小、起伏越平缓
                float lowN = 1e6f; int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { float hh = H(m, cx + dx, cz + dz); if (hh < lowN) lowN = hh; }
                float rel = top - lowN > 0.5f ? Sat((yc - lowN) / (top - lowN)) : 0.5f;
                uint hs = Hash.Of(k, 0, c.Seed + 4041);
                float wave = Noise2(x / 1.3f + k * 5.17f, z / 1.3f - k * 3.11f, c.Seed + 4042) * 0.75f + Noise2(x / 0.5f, z / 0.5f + k * 2.3f, c.Seed + 4043) * 0.25f;
                // 扁板：每层再按水平面上的 Voronoi（约 1.8m）分成一块块，每块凸出量不同，块与块之间是竖缝（负责人：沼泽全程是扁平的细胞元）
                float slab = SlabOffset(x, z, k, c.Seed + 4044, out float crack);
                float outw = (0.12f + 0.08f * Unit(hs)) * (1 - 0.55f * rel) + (0.16f * (1 - 0.6f * rel)) * (wave - 0.5f) * 2 + slab * (1 - 0.5f * rel) - SwampInset - crack;
                float r = Math.Min(half, 0.13f);
                float qx = f2 - outw - SwampInset + r, qy = Math.Abs(y - yc) - half + r;
                float mx = Math.Max(qx, 0), my = Math.Max(qy, 0);
                float d = (float)Math.Sqrt(mx * mx + my * my) + Math.Min(Math.Max(qx, qy), 0) - r;
                d = Math.Max(d, y - top);
                if (d < best) { best = d; rock = true; }
            }
            // 顶面（离崖边远的地方）照旧用柱体
            if (col < best && foot < -0.6f) { best = col; rock = false; }
            return best;
        }

        static float SlabOffset(float x, float z, int k, uint seed, out float crack)
        {
            const float G = 1.8f;
            float fu = x / G + k * 0.37f, fv = z / G - k * 0.61f;
            int gu = (int)Math.Floor(fu), gv = (int)Math.Floor(fv);
            float d1 = 9, d2 = 9; uint id = 0;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    uint hh = Hash.Of(gu + i, gv + j, seed + (uint)k * 7);
                    float pu = gu + i + Unit(hh), pv = gv + j + Unit(hh >> 16);
                    float du = fu - pu, dv = fv - pv, d = du * du + dv * dv;
                    if (d < d1) { d2 = d1; d1 = d; id = hh; } else if (d < d2) d2 = d;
                }
            float e = ((float)Math.Sqrt(d2) - (float)Math.Sqrt(d1)) * 0.5f * G; // 到相邻块的距离（m）
            crack = e < 0.04f ? (0.04f - e) * 1.5f : 0;
            return (Unit(id >> 5) - 0.5f) * 0.12f;
        }

        /// <summary>沼泽层：按世界高度划分，每层 0.3–0.45m（跨所有墙连续）。</summary>
        static int Band(uint seed, float y, out float lo, out float hi)
        {
            int k = (int)Math.Floor(y / 0.38f);
            float B(int q) => q * 0.38f + (Unit(Hash.Of(q, 1, seed + 4040)) - 0.5f) * 0.1f;
            if (y < B(k)) k--; else if (y >= B(k + 1)) k++;
            lo = B(k); hi = B(k + 1);
            return k;
        }

        /// <summary>石头表面的小刻面：在石头自己的坐标（沿墙 u、竖直 v、朝外 n）里取细 Voronoi，每块往里收 0–5cm；朝外的面上起作用。</summary>
        static float Facets(float u, float v, float n, uint seed)
        {
            const float G = 0.15f;
            float fu = u / G, fv = v / G;
            int gu = (int)Math.Floor(fu), gv = (int)Math.Floor(fv);
            float best = 9; uint id = 0;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    uint hh = Hash.Of(gu + i, gv + j, seed);
                    float pu = gu + i + Unit(hh), pv = gv + j + Unit(hh >> 16);
                    float du = fu - pu, dv = fv - pv, d = du * du + dv * dv;
                    if (d < best) { best = d; id = hh; }
                }
            // 每个刻面一个倾斜的小平面（往里收的量随位置线性变化），刻面之间形成折线
            float tilt = (Unit(id >> 3) - 0.5f) * 0.25f * u + (Unit(id >> 7) - 0.5f) * 0.25f * v;
            return 0.05f * Unit(id >> 11) + tilt * 0.4f;
        }

        static float Noise2(float u, float v, uint seed)
        {
            int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v);
            float tu = u - iu, tv = v - iv; tu = tu * tu * (3 - 2 * tu); tv = tv * tv * (3 - 2 * tv);
            float a = Unit(Hash.Of(iu, iv, seed)), b = Unit(Hash.Of(iu + 1, iv, seed)), c2 = Unit(Hash.Of(iu, iv + 1, seed)), d = Unit(Hash.Of(iu + 1, iv + 1, seed));
            return (a + (b - a) * tu) * (1 - tv) + (c2 + (d - c2) * tu) * tv;
        }

        // ---------------- 石头 ----------------

        /// <summary>
        /// 在格 (x, z) 朝 s 的墙面（从 hn 到 h）上摆石头。石头按世界坐标排成错开的行（行高、列宽带随机），
        /// 中心在本格墙段上的由本格摆，所以相邻格不重复、跨格连续；石头可以伸出墙端包住外角。
        /// </summary>
        static void PlaceRocks(Ctx c, int x, int z, int s, int h, int hn, CliffStyle st)
        {
            int nx = Grid.DX4[s], nz = Grid.DZ4[s], tx = -nz, tz = nx;
            float cxw = x + 0.5f, czw = z + 0.5f;
            float plane = (cxw + nx * 0.5f) * nx + (czw + nz * 0.5f) * nz;
            float cAlong = cxw * tx + czw * tz;
            uint seed = (uint)((int)Math.Floor(plane * 2 + 0.5f) * 7919 + s * 104729 + (st == CliffStyle.Human ? 11 : 23)) ^ c.Seed;
            bool human = st == CliffStyle.Human;
            float rowH = human ? 0.6f : 0.34f, colW = human ? 0.85f : 0.4f;
            int j0 = (int)Math.Floor(hn / rowH) - 1, j1 = (int)Math.Floor(h / rowH) + 1;
            for (int j = j0; j <= j1; j++)
            {
                float stagger = human ? Unit(Hash.Of(j, 7, seed)) * colW : (j & 1) * colW * 0.5f;
                int i0 = (int)Math.Floor((cAlong - 0.5f - stagger) / colW) - 1;
                for (int i = i0; i <= i0 + 4; i++)
                {
                    uint hh = Hash.Of(i, j, seed);
                    float along = (i + 0.5f + (Unit(hh) - 0.5f) * (human ? 0.45f : 0.25f)) * colW + stagger;
                    if (along < cAlong - 0.5f || along >= cAlong + 0.5f) continue;
                    float y = (j + 0.5f + (Unit(hh >> 5) - 0.5f) * (human ? 0.4f : 0.2f)) * rowH;
                    if (y < hn - 0.05f || y > h - 0.12f) continue;
                    // 大小：人类区 0.36–0.75m 宽、0.3–0.55m 高（偶尔特大）；森林 0.32–0.46m
                    float big = human && Unit(hh >> 9) > 0.8f ? 1.3f : 1f;
                    // 在这面墙上的相对高度：人类区越往上石头越扁、凸得越少（负责人）
                    float rel = h - hn > 0 ? Sat((y - hn) / (h - hn)) : 0.5f;
                    float A = human ? (0.26f + 0.2f * Unit(hh >> 11)) * big * (1 + 0.25f * rel) : 0.18f + 0.08f * Unit(hh >> 11);
                    float B = human ? (0.2f + 0.12f * Unit(hh >> 13)) * big * (1 - 0.35f * rel) : 0.15f + 0.05f * Unit(hh >> 13);
                    float E = human ? 0.3f : 0.16f;
                    float outN = human ? (0.05f + 0.12f * Unit(hh >> 17)) * (1 - 0.6f * rel) : 0.0f + 0.04f * Unit(hh >> 17); // 石头面凸出墙面多少
                    float wx = cxw + nx * 0.5f + tx * (along - cAlong), wz = czw + nz * 0.5f + tz * (along - cAlong);
                    var r = new Rock { Tx = tx, Tz = tz, Nx = nx, Nz = nz, Cap = h - 0.05f, Facet = human ? hh | 1 : 0 };
                    r.X = wx + nx * (outN - E); r.Z = wz + nz * (outN - E); r.Y = y;
                    if (human)
                    {
                        // 凸多面体：前、后、左右、上下 6 个面各带随机倾斜，再切 4 个斜角（前上、前下、前左、前右）和 2 个随机面
                        var P = new List<float>();
                        void Pl(float lu, float ly, float ln, float d)
                        {
                            float l = (float)Math.Sqrt(lu * lu + ly * ly + ln * ln);
                            P.Add((lu * tx + ln * nx) / l); P.Add(ly / l); P.Add((lu * tz + ln * nz) / l); P.Add(d);
                        }
                        float J(int q) => (Unit(Hash.Of(i * 31 + q, j, seed + 99)) - 0.5f);
                        Pl(J(0) * 0.5f, J(1) * 0.5f, 1, E);            // 前
                        Pl(0, 0, -1, E);                                // 后
                        Pl(1, J(2) * 0.5f, J(3) * 0.3f, A); Pl(-1, J(4) * 0.5f, J(5) * 0.3f, A);
                        Pl(J(6) * 0.4f, 1, J(7) * 0.3f, B); Pl(J(8) * 0.4f, -1, J(9) * 0.3f, B);
                        float ch = 0.72f + 0.12f * (J(10) + 0.5f);
                        Pl(0, 1, 1, (B + E) * ch * 0.7071f); Pl(0, -1, 1, (B + E) * ch * 0.7071f);
                        Pl(1, 0, 1, (A + E) * ch * 0.7071f); Pl(-1, 0, 1, (A + E) * ch * 0.7071f);
                        Pl(J(11), 0.6f + J(12), 0.8f, (B + E) * 0.62f); Pl(J(13) - 0.3f, -0.3f + J(14), 0.9f, (A + E) * 0.6f);
                        r.P = P.ToArray();
                        r.R = (float)Math.Sqrt(A * A + B * B + E * E);
                    }
                    else
                    {
                        r.A = A; r.B = B; r.E = E; r.Rr = Math.Min(Math.Min(A, B), E) * 0.95f;
                        r.R = (float)Math.Sqrt(A * A + B * B + E * E);
                    }
                    int id = c.Rocks.Count; c.Rocks.Add(r);
                    int gx0 = (int)Math.Floor(r.X - r.R - 0.2f), gx1 = (int)Math.Floor(r.X + r.R + 0.2f);
                    int gz0 = (int)Math.Floor(r.Z - r.R - 0.2f), gz1 = (int)Math.Floor(r.Z + r.R + 0.2f);
                    for (int gz = gz0; gz <= gz1; gz++)
                        for (int gx = gx0; gx <= gx1; gx++)
                        {
                            long k = Key(gx, gz);
                            if (!c.Grid.TryGetValue(k, out var l2)) c.Grid[k] = l2 = new List<int>();
                            l2.Add(id);
                        }
                }
            }
        }
    }
}
