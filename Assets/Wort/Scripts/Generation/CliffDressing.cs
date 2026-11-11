using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>崖边风格：泥土崖（森林、平地）、山体（山地、针叶林外圈）。以后改成可配置的数据（截面、trim sheet、装饰件和密度）。</summary>
    public sealed class CliffStyle
    {
        public float Chamfer = 0.3f;      // 外角削进 / 内角填出的距离（m）
        public float Strata = 0.12f;      // 每层崖面前后错开的幅度（m）
        public float Lip = 0.08f;         // 顶沿外翻（m），给崖面一条暗影
        public float RimDensity = 0.05f;  // 顶沿装饰石的密度（每米崖边）
        public float RimSize = 0.4f;      // 装饰石的平均边长（m）
        public float FaceRough = 0.08f;   // 崖面内部起伏（m，沿法线）
        public float FaceStones = 0.08f;  // 朝南崖面嵌石密度（每平方米）
        public float FootScree = 0.15f;   // 崖脚碎石密度（每米崖脚）

        public static readonly CliffStyle Earth = new CliffStyle();
        public static readonly CliffStyle Mountain = new CliffStyle { Chamfer = 0.35f, Strata = 0.2f, Lip = 0.04f, RimDensity = 0.7f, RimSize = 0.5f, FaceRough = 0.14f, FaceStones = 0.35f, FootScree = 0.45f };
    }

    /// <summary>
    /// 崖面外观原型（只改画面，碰撞和玩法仍用 1m 体素格）：
    /// 1. 网格顶点按所在格角的四格高度变形：外角削斜、内角填斜、直边按层前后错开（岩层感）、顶沿略外翻；
    ///    变形只取决于顶点的世界坐标，所以相邻面、相邻 chunk 之间不会裂缝。偏离格子最多约 0.35m。
    /// 2. 沿崖顶边按风格密度放装饰石（占位方块，以后换美术件）。
    /// 输出为平面着色的三角形（每个三角形自带法线）。
    /// </summary>
    public static class CliffDressing
    {
        public static MeshData Apply(MeshData src, RegionMap map, int ox, int oz, uint seed)
        {
            var fStrata = new Fbm(Hash.Of(seed, 901), 3, 2);
            var fRough = new Fbm(Hash.Of(seed, 902), 0.7, 2);
            var outM = new MeshData();
            var p = src.Positions;
            for (int mat = 0; mat < src.Indices.Length; mat++)
            {
                var idx = src.Indices[mat];
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    var v = new float[9];
                    int a0 = idx[t] * 3;
                    float hx = src.Normals[a0], hy = src.Normals[a0 + 1], hz = src.Normals[a0 + 2];
                    var o = new float[9]; // 原始（世界）坐标
                    for (int k = 0; k < 3; k++)
                    {
                        int a = idx[t + k] * 3;
                        float x = p[a] + ox, y = p[a + 1], z = p[a + 2] + oz;
                        o[k * 3] = x; o[k * 3 + 1] = y; o[k * 3 + 2] = z;
                        Displace(map, fStrata, ref x, y, ref z);
                        v[k * 3] = x; v[k * 3 + 1] = y; v[k * 3 + 2] = z;
                    }
                    if (Math.Abs(hy) < 0.1f && mat != (int)VoxelMaterial.Water)
                        Subdivide(outM, mat, map, fRough, o, v, hx, hz, 2, ox, oz);
                    else
                    {
                        for (int k = 0; k < 3; k++) { v[k * 3] -= ox; v[k * 3 + 2] -= oz; }
                        AddTri(outM, mat, v, hx, hy, hz);
                    }
                }
            }
            AddRimStones(outM, map, ox, oz, seed);
            AddFaceStones(outM, map, ox, oz, seed);
            return outM;
        }

        /// <summary>
        /// 竖面三角形细分 depth 次（每次一分四）。新顶点 = 两端（已变形）顶点的中点；
        /// 不在整数高度、也不在格角竖线上的新顶点再沿面法线加起伏（只取决于原始世界坐标，相邻三角形共用的边不会裂）。
        /// 每层下沿 1/4 处略往里收，形成一层一层的岩层缝。
        /// </summary>
        static void Subdivide(MeshData m, int mat, RegionMap map, Fbm f, float[] o, float[] v, float hx, float hz, int depth, int ox, int oz)
        {
            if (depth == 0)
            {
                var w = (float[])v.Clone();
                for (int k = 0; k < 3; k++) { w[k * 3] -= ox; w[k * 3 + 2] -= oz; }
                AddTri(m, mat, w, hx, 0, hz);
                return;
            }
            var mo = new float[9]; var mv = new float[9];
            for (int e = 0; e < 3; e++)
            {
                int a = e, b = (e + 1) % 3;
                for (int c = 0; c < 3; c++)
                {
                    mo[e * 3 + c] = (o[a * 3 + c] + o[b * 3 + c]) * 0.5f;
                    mv[e * 3 + c] = (v[a * 3 + c] + v[b * 3 + c]) * 0.5f;
                }
                float x = mo[e * 3], y = mo[e * 3 + 1], z = mo[e * 3 + 2];
                bool yInt = Math.Abs(y - Math.Round(y)) < 1e-3;
                bool column = Math.Abs(x - Math.Round(x)) < 1e-3 && Math.Abs(z - Math.Round(z)) < 1e-3;
                if (!yInt && !column)
                {
                    var st = StyleAt(map, (int)Math.Floor(x - hx * 0.5f), (int)Math.Floor(z - hz * 0.5f));
                    float frac = y - (float)Math.Floor(y);
                    float off = (float)f.Sample(x * 1.3 + z * 0.7, y * 2.1 + (x - z) * 0.31) * st.FaceRough;
                    if (Math.Abs(frac - 0.25f) < 1e-3) off -= st.FaceRough * 0.8f; // 岩层缝
                    mv[e * 3] += hx * off; mv[e * 3 + 2] += hz * off;
                }
            }
            // 四个子三角形：(0, m01, m20) (m01, 1, m12) (m20, m12, 2) (m01, m12, m20)
            int[][] tris = { new[] { 0, 3, 5 }, new[] { 3, 1, 4 }, new[] { 5, 4, 2 }, new[] { 3, 4, 5 } };
            foreach (var tr in tris)
            {
                var so = new float[9]; var sv = new float[9];
                for (int k = 0; k < 3; k++)
                {
                    int id = tr[k];
                    for (int c = 0; c < 3; c++)
                    {
                        so[k * 3 + c] = id < 3 ? o[id * 3 + c] : mo[(id - 3) * 3 + c];
                        sv[k * 3 + c] = id < 3 ? v[id * 3 + c] : mv[(id - 3) * 3 + c];
                    }
                }
                Subdivide(m, mat, map, f, so, sv, hx, hz, depth - 1, ox, oz);
            }
        }

        /// <summary>朝南崖面（落差 ≥ 2 层）嵌石；崖脚（落差 ≥ 1 层）堆碎石。占位：随机旋转、上小下大的石块。</summary>
        static void AddFaceStones(MeshData m, RegionMap map, int ox, int oz, uint seed)
        {
            int size = map.Settings.ChunkSize;
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    int i = z * map.W + x;
                    int h = map.Height[i], hs = H(map, x, z - 1);
                    int drop = h - hs;
                    if (drop < 1 || map.HasWater(z > 0 ? i - map.W : i)) continue;
                    var st = StyleAt(map, x, z);
                    // 崖面：每层每米抽签
                    if (drop >= 2)
                        for (int y = hs; y < h - 1; y++)
                            for (int k = 0; k < 2; k++)
                            {
                                uint r = Hash.Of(Hash.Of(seed, (uint)i), (uint)(y * 7 + k + 1000));
                                if ((r & 0xFFFF) / 65536f >= st.FaceStones * 0.5f) continue;
                                float sz = 0.25f + 0.45f * ((r >> 16) & 0xFF) / 255f;
                                float px = lx + 0.15f + 0.7f * ((r >> 24) & 0xFF) / 255f;
                                float py = y + 0.2f + 0.6f * ((r >> 8) & 0xFF) / 255f;
                                Stone(m, px, py - sz * 0.3f, lz - 0.02f, sz * 0.5f, sz * 0.6f, sz * 0.35f, r);
                            }
                    // 崖脚碎石：在南邻格上、贴着崖
                    for (int k = 0; k < 3; k++)
                    {
                        uint r = Hash.Of(Hash.Of(seed, (uint)i), (uint)(k + 2000));
                        if ((r & 0xFFFF) / 65536f >= st.FootScree * Math.Min(drop, 3) / 3f) continue;
                        float sz = 0.2f + 0.4f * ((r >> 16) & 0xFF) / 255f;
                        float px = lx + 0.1f + 0.8f * ((r >> 24) & 0xFF) / 255f;
                        float pz = lz - 0.1f - 0.4f * ((r >> 8) & 0xFF) / 255f;
                        Stone(m, px, hs, pz, sz * 0.55f, sz * 0.5f, sz * 0.45f, r);
                    }
                }
        }

        /// <summary>占位石块：绕 Y 随机旋转、顶面缩小（截锥），5 个可见面。</summary>
        static void Stone(MeshData m, float cx, float y0, float cz, float hx, float hy, float hz, uint r)
        {
            int mat = (int)VoxelMaterial.Rock;
            // 旋转：哈希出的单位向量（不用 Math.Sin / Cos，见 CLAUDE.md）
            float ra = 0.2f + ((r >> 4) & 0xFF) / 255f, rb = ((r >> 12) & 0xFF) / 255f;
            float rl = (float)Math.Sqrt(ra * ra + rb * rb);
            float ca = ra / rl, sa = rb / rl;
            float y1 = y0 + hy * 2, t = 0.7f;
            float[] bx = new float[4], bz = new float[4], tx = new float[4], tz = new float[4];
            float[] sxs = { -1, 1, 1, -1 }, szs = { -1, -1, 1, 1 };
            for (int k = 0; k < 4; k++)
            {
                float lx = sxs[k] * hx, lz = szs[k] * hz;
                bx[k] = cx + lx * ca - lz * sa; bz[k] = cz + lx * sa + lz * ca;
                tx[k] = cx + (lx * ca - lz * sa) * t; tz[k] = cz + (lx * sa + lz * ca) * t;
            }
            Quad(m, mat, 0, 1, 0, tx[0], y1, tz[0], tx[3], y1, tz[3], tx[2], y1, tz[2], tx[1], y1, tz[1]);
            for (int k = 0; k < 4; k++)
            {
                int n = (k + 1) % 4;
                float nx = (bz[n] - bz[k]), nz = -(bx[n] - bx[k]); // 朝外（逆时针排列时）
                float mx = (bx[k] + bx[n]) * 0.5f - cx, mz = (bz[k] + bz[n]) * 0.5f - cz;
                if (nx * mx + nz * mz < 0) { nx = -nx; nz = -nz; }
                Quad(m, mat, nx, 0.3f, nz, bx[k], y0, bz[k], tx[k], y1, tz[k], tx[n], y1, tz[n], bx[n], y0, bz[n]);
            }
        }

        static int H(RegionMap m, int x, int z)
        {
            if (x < 0) x = 0; if (z < 0) z = 0; if (x >= m.W) x = m.W - 1; if (z >= m.H) z = m.H - 1;
            return m.Height[z * m.W + x];
        }

        static CliffStyle StyleAt(RegionMap m, int x, int z)
        {
            if (x < 0) x = 0; if (z < 0) z = 0; if (x >= m.W) x = m.W - 1; if (z >= m.H) z = m.H - 1;
            var b = (BiomeId)m.Biome[z * m.W + x];
            return b == BiomeId.Mountain || b == BiomeId.Conifer ? CliffStyle.Mountain : CliffStyle.Earth;
        }

        /// <summary>顶点 (x, y, z) 在格角上：看周围四格在高度 y 处是不是实心（H ≥ y）。</summary>
        static void Displace(RegionMap m, Fbm f, ref float x, float y, ref float z)
        {
            int cx = (int)Math.Floor(x + 0.5), cz = (int)Math.Floor(z + 0.5);
            if (Math.Abs(x - cx) > 1e-3 || Math.Abs(z - cz) > 1e-3) return; // 不在格角上（斜坡中点等）
            int yi = (int)Math.Floor(y + 0.5);
            if (Math.Abs(y - yi) > 1e-3) return;
            // 四格：(cx-1,cz-1) (cx,cz-1) (cx-1,cz) (cx,cz)，格中心相对格角的方向
            int n = 0, maxTop = int.MinValue; float sx = 0, sz = 0, ex = 0, ez = 0;
            for (int k = 0; k < 4; k++)
            {
                int dx = k & 1, dz = k >> 1;
                int h = H(m, cx - 1 + dx, cz - 1 + dz);
                float dirX = dx == 0 ? -0.5f : 0.5f, dirZ = dz == 0 ? -0.5f : 0.5f;
                if (h >= yi) { n++; sx += dirX; sz += dirZ; if (h > maxTop) maxTop = h; }
                else { ex += dirX; ez += dirZ; }
            }
            if (n == 0 || n == 4) return;
            var st = StyleAt(m, cx, cz);
            if (n == 1) { x += sx * 2 * st.Chamfer; z += sz * 2 * st.Chamfer; return; }      // 外角：往实心格那边削
            if (n == 3) { x += ex * 2 * st.Chamfer * 0.8f; z += ez * 2 * st.Chamfer * 0.8f; return; } // 内角：往空格那边填
            // 直边（n == 2 且相邻）：沿法线（实心 → 空）按层前后错开；顶沿外翻
            float nx = ex, nz = ez;
            float len = (float)Math.Sqrt(nx * nx + nz * nz);
            if (len < 1e-3) return; // 对角两格：不动
            nx /= len; nz /= len;
            float off = (float)f.Sample(cx + yi * 13.7, cz - yi * 7.3) * st.Strata;
            bool topEdge = maxTop == yi;
            if (topEdge) off += st.Lip;
            x += nx * off; z += nz * off;
        }

        /// <summary>加一个三角形；法线朝向与提示法线 (hx, hy, hz) 一致（必要时翻转绕序）。</summary>
        static void AddTri(MeshData m, int mat, float[] v, float hx, float hy, float hz)
        {
            float ux = v[3] - v[0], uy = v[4] - v[1], uz = v[5] - v[2];
            float wx = v[6] - v[0], wy = v[7] - v[1], wz = v[8] - v[2];
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-6f) return;
            nx /= l; ny /= l; nz /= l;
            // Unity 正面 = Cross(b - a, c - a) 与法线同向（见 ChunkMesher.Facing）；与提示法线反向时换绕序
            bool same = nx * hx + ny * hy + nz * hz >= 0;
            if (!same) { nx = -nx; ny = -ny; nz = -nz; }
            int[] order = same ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };
            var list = m.Indices[mat];
            foreach (int k in order)
            {
                list.Add(m.VertexCount);
                m.Positions.Add(v[k * 3]); m.Positions.Add(v[k * 3 + 1]); m.Positions.Add(v[k * 3 + 2]);
                m.Normals.Add(nx); m.Normals.Add(ny); m.Normals.Add(nz);
            }
        }

        /// <summary>沿崖顶边放装饰石（占位方块，露出顶面 ≤ 0.2m）。每条崖顶边（本格高、邻格低 ≥ 1 层）按风格密度抽签。</summary>
        static void AddRimStones(MeshData m, RegionMap map, int ox, int oz, uint seed)
        {
            int size = map.Settings.ChunkSize;
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    int i = z * map.W + x;
                    if (map.HasWater(i)) continue;
                    int h = map.Height[i];
                    var st = StyleAt(map, x, z);
                    for (int d = 0; d < 4; d++)
                    {
                        int nh = H(map, x + Grid.DX4[d], z + Grid.DZ4[d]);
                        if (nh > h - 1) continue;
                        uint r = Hash.Of(Hash.Of(seed, (uint)i), (uint)(d * 131 + 77));
                        if ((r & 0xFFFF) / 65536f >= st.RimDensity) continue;
                        float s = st.RimSize * (0.6f + 0.8f * ((r >> 16) & 0xFF) / 255f);
                        float hgt = s * (0.35f + 0.4f * ((r >> 24) & 0xFF) / 255f);
                        float along = (((r >> 8) & 0xFF) / 255f - 0.5f) * (1 - s);
                        // 石头中心：靠这条边、略探出去
                        float cxs = lx + 0.5f + Grid.DX4[d] * (0.5f - s * 0.35f) + (Grid.DX4[d] == 0 ? along : 0);
                        float czs = lz + 0.5f + Grid.DZ4[d] * (0.5f - s * 0.35f) + (Grid.DZ4[d] == 0 ? along : 0);
                        // 大半埋进崖边：顶面只露出 0.05–0.2m，往下包住崖沿
                        float above = 0.05f + 0.15f * (hgt / (s * 0.75f));
                        float below = s * 0.6f;
                        Stone(m, cxs, h - below, czs, s * 0.5f, (above + below) * 0.5f, s * 0.45f, r);
                    }
                }
        }

        static void Box(MeshData m, int mat, float cx, float y0, float cz, float hx, float hy, float hz)
        {
            float x0 = cx - hx, x1 = cx + hx, z0 = cz - hz, z1 = cz + hz, y1 = y0 + hy;
            // 上、南、北、东、西（底面看不见，不做）
            Quad(m, mat, 0, 1, 0, x0, y1, z0, x0, y1, z1, x1, y1, z1, x1, y1, z0);
            Quad(m, mat, 0, 0, -1, x0, y0, z0, x0, y1, z0, x1, y1, z0, x1, y0, z0);
            Quad(m, mat, 0, 0, 1, x1, y0, z1, x1, y1, z1, x0, y1, z1, x0, y0, z1);
            Quad(m, mat, 1, 0, 0, x1, y0, z0, x1, y1, z0, x1, y1, z1, x1, y0, z1);
            Quad(m, mat, -1, 0, 0, x0, y0, z1, x0, y1, z1, x0, y1, z0, x0, y0, z0);
        }

        static void Quad(MeshData m, int mat, float hx, float hy, float hz, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, float dx, float dy, float dz)
        {
            AddTri(m, mat, new[] { ax, ay, az, bx, by, bz, cx, cy, cz }, hx, hy, hz);
            AddTri(m, mat, new[] { ax, ay, az, cx, cy, cz, dx, dy, dz }, hx, hy, hz);
        }
    }
}
