using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 地形外观方案 B（《地形外观与崖面美术规范》）：每格仍是立方体，碰撞照旧用 1m 体素。
    /// - 顶面是平的泥土；露出的顶边（落差 ≥ 1 层）倒一条约 0.1m 的斜面；
    /// - 外角的竖边倒约 0.1m；内角（凹角）的竖边和顶边倒一点点（约 0.04m）；
    /// - 顶面边缘一圈是石头，往里经过“碎石”过渡到泥土，分界加噪声（预览里没有贴图，用三种颜色代替渐变）；
    /// - 侧面是石头；只有朝南的面（镜头看得见）做低模起伏和一道岩层折线，其余面保持平面。
    /// 画面上的地面永远不超出碰撞格（倒角只往里收）。水面、斜坡沿用 ChunkMesher。
    /// 不用 System.Random / Math.Sin 等，结果在 .NET 与 Unity 一致。
    /// </summary>
    public static class CliffMesher
    {
        public const float Bevel = 0.1f;   // 外倒角（顶边、外角竖边）
        public const float Fillet = 0.04f; // 内倒角（凹角）
        const float RimRock = 0.22f, RimMix = 0.42f, RimNoise = 0.08f; // 顶面边缘：石头 / 碎石过渡 / 泥土
        const float FaceNoise = 0.05f, Crease = 0.03f;                   // 朝南面的起伏和岩层折线

        const int Rock = (int)VoxelMaterial.Rock, Mix = (int)VoxelMaterial.Heath, Dirt = (int)VoxelMaterial.Soil;

        public static MeshData Build(RegionVoxels rv, int cx, int cz, uint seed)
        {
            var map = rv.Map;
            var mesh = new MeshData();
            int size = map.Settings.ChunkSize, ox = cx * size, oz = cz * size;
            var fRim = new Fbm(Hash.Of(seed, 911), 0.6, 2);
            var fFace = new Fbm(Hash.Of(seed, 912), 0.5, 2);
            var b = new Builder { M = mesh, Ox = ox, Oz = oz };
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    Column(map, b, fRim, fFace, x, z);
                }
            // 水面、斜坡：沿用体素网格的做法
            var chunk = rv.GetChunk(cx, cz);
            for (int y = chunk.MinY; y < chunk.MinY + chunk.SizeY; y++)
                for (int lz = 0; lz < size; lz++)
                    for (int lx = 0; lx < size; lx++)
                    {
                        var m = chunk.GetMaterial(lx, y, lz);
                        if (m == VoxelMaterial.Air) continue;
                        var s = chunk.GetShape(lx, y, lz);
                        if (m == VoxelMaterial.Water) ChunkMesher.EmitWater(rv, mesh, ox + lx, y, oz + lz, ox, oz);
                        else if (s != VoxelShape.Full) ChunkMesher.EmitSlope(rv, mesh, m, (int)s - 1, ox + lx, y, oz + lz, ox, oz);
                    }
            return mesh;
        }

        static int H(RegionMap m, int x, int z)
        {
            if (x < 0) x = 0; if (z < 0) z = 0; if (x >= m.W) x = m.W - 1; if (z >= m.H) z = m.H - 1;
            return m.Height[z * m.W + x];
        }

        /// <summary>一格：顶面（含边缘材质）、每个露出侧面的墙、顶边倒角、角上的倒角面。墙归高的那一格。</summary>
        static void Column(RegionMap map, Builder bd, Fbm fRim, Fbm fFace, int x, int z)
        {
            int h = H(map, x, z);
            float cxw = x + 0.5f, czw = z + 0.5f;
            var exposed = new bool[4];
            for (int s = 0; s < 4; s++) exposed[s] = H(map, x + Grid.DX4[s], z + Grid.DZ4[s]) < h;

            // ---- 顶面：露出的边往里收 Bevel ----
            float x0 = x + (exposed[3] ? Bevel : 0), x1 = x + 1 - (exposed[1] ? Bevel : 0);
            float z0 = z + (exposed[2] ? Bevel : 0), z1 = z + 1 - (exposed[0] ? Bevel : 0);
            bool rim = false;
            for (int s = 0; s < 4; s++) if (exposed[s]) rim = true;
            for (int dz = -1; dz <= 1; dz += 2) for (int dx = -1; dx <= 1; dx += 2) if (H(map, x + dx, z + dz) < h) rim = true;
            if (!rim)
                bd.Quad(Dirt, 0, 1, 0, x0, h, z0, x0, h, z1, x1, h, z1, x1, h, z0);
            else
            {
                const int K = 4;
                for (int j = 0; j < K; j++)
                    for (int i = 0; i < K; i++)
                    {
                        float ax = x0 + (x1 - x0) * i / K, bx = x0 + (x1 - x0) * (i + 1) / K;
                        float az = z0 + (z1 - z0) * j / K, bz = z0 + (z1 - z0) * (j + 1) / K;
                        float px = (ax + bx) * 0.5f, pz = (az + bz) * 0.5f;
                        float d = 9;
                        for (int s = 0; s < 4; s++)
                            if (exposed[s]) d = Math.Min(d, 0.5f - ((px - cxw) * Grid.DX4[s] + (pz - czw) * Grid.DZ4[s]));
                        for (int dz = -1; dz <= 1; dz += 2)
                            for (int dx = -1; dx <= 1; dx += 2)
                                if (H(map, x + dx, z + dz) < h)
                                {
                                    float kx = cxw + dx * 0.5f - px, kz = czw + dz * 0.5f - pz;
                                    d = Math.Min(d, (float)Math.Sqrt(kx * kx + kz * kz));
                                }
                        d += (float)fRim.Sample(px * 2.3, pz * 2.3) * RimNoise;
                        int mat = d < RimRock ? Rock : (d < RimMix ? Mix : Dirt);
                        bd.Quad(mat, 0, 1, 0, ax, h, az, ax, h, bz, bx, h, bz, bx, h, az);
                    }
            }

            // ---- 每个露出的侧面 ----
            for (int s = 0; s < 4; s++)
            {
                if (!exposed[s]) continue;
                int nx = Grid.DX4[s], nz = Grid.DZ4[s];
                int tx = -nz, tz = nx;                      // 沿墙方向
                int hn = H(map, x + nx, z + nz);
                bool ns = nz != 0;                          // 南北向的墙负责角上的共享面
                // 墙上一点：u ∈ [-0.5, 0.5] 沿墙，k = 往格内收的距离
                float PX(float u, float k) => cxw + nx * (0.5f - k) + tx * u;
                float PZ(float u, float k) => czw + nz * (0.5f - k) + tz * u;

                for (int y = hn; y < h; y++)
                {
                    bool top = y == h - 1;
                    float y0 = y, y1 = top ? h - Bevel : y + 1;
                    var endInset = new float[2];
                    var endKind = new int[2]; // 0 = 直接到格边，1 = 外角，2 = 内角
                    for (int e = 0; e < 2; e++)
                    {
                        int sg = e == 0 ? -1 : 1;
                        int hp = H(map, x + sg * tx, z + sg * tz), hd = H(map, x + nx + sg * tx, z + nz + sg * tz);
                        bool perpSolid = hp > y, diagSolid = hd > y;
                        if (!perpSolid && !diagSolid) { endKind[e] = 1; endInset[e] = Bevel; }
                        else if (perpSolid && diagSolid) { endKind[e] = 2; endInset[e] = Fillet; }
                    }
                    float u0 = -0.5f + endInset[0], u1 = 0.5f - endInset[1];
                    // 墙
                    if (s == 2) SouthWall(bd, fFace, nx, nz, PX(u0, 0), PZ(u0, 0), PX(u1, 0), PZ(u1, 0), y0, y1);
                    else bd.Quad(Rock, nx, 0, nz, PX(u0, 0), y0, PZ(u0, 0), PX(u0, 0), y1, PZ(u0, 0), PX(u1, 0), y1, PZ(u1, 0), PX(u1, 0), y0, PZ(u1, 0));
                    // 角上的竖向倒角面（外角）/ 内倒角面（凹角），由南北向的墙负责
                    if (ns)
                        for (int e = 0; e < 2; e++)
                        {
                            int sg = e == 0 ? -1 : 1;
                            float ue = sg * (0.5f - endInset[e]);
                            if (endKind[e] == 1)
                            {
                                // 另一面墙在角上的端点：沿墙方向在格边上，往格内收 Bevel
                                float qx = cxw + tx * sg * 0.5f + nx * (0.5f - Bevel), qz = czw + tz * sg * 0.5f + nz * (0.5f - Bevel);
                                bd.Quad(Rock, nx + tx * sg, 0, nz + tz * sg, PX(ue, 0), y0, PZ(ue, 0), PX(ue, 0), y1, PZ(ue, 0), qx, y1, qz, qx, y0, qz);
                            }
                            else if (endKind[e] == 2)
                            {
                                int hq = H(map, x + nx + sg * tx, z + nz + sg * tz);
                                float yTop = Math.Min(y1, y == hq - 1 ? hq - Bevel : y + 1);
                                float kx = cxw + nx * 0.5f + tx * sg * 0.5f, kz = czw + nz * 0.5f + tz * sg * 0.5f; // 凹角的竖边
                                float qx = kx + nx * Fillet, qz = kz + nz * Fillet;
                                bd.Quad(Rock, nx - tx * sg, 0, nz - tz * sg, PX(ue, 0), y0, PZ(ue, 0), PX(ue, 0), yTop, PZ(ue, 0), qx, yTop, qz, qx, y0, qz);
                            }
                        }

                    if (!top) continue;
                    // ---- 顶边倒角：墙顶（格边，h - Bevel）→ 顶面边（收进 Bevel，h） ----
                    var tEnd = new float[2];
                    for (int e = 0; e < 2; e++)
                    {
                        int sg = e == 0 ? -1 : 1;
                        bool perpExposed = H(map, x + sg * tx, z + sg * tz) < h;
                        tEnd[e] = perpExposed ? Bevel : 0;
                    }
                    float tu0 = -0.5f + tEnd[0], tu1 = 0.5f - tEnd[1];
                    bd.Quad(Rock, nx, 1, nz,
                        PX(u0, 0), y1, PZ(u0, 0), PX(tu0, Bevel), h, PZ(tu0, Bevel),
                        PX(tu1, Bevel), h, PZ(tu1, Bevel), PX(u1, 0), y1, PZ(u1, 0));
                    for (int e = 0; e < 2; e++)
                    {
                        int sg = e == 0 ? -1 : 1;
                        float ub = sg * (0.5f - endInset[e]), ut = sg * (0.5f - tEnd[e]);
                        if (tEnd[e] > 0)
                        {
                            // 两条顶边倒角在角上相接：三角面（顶面角、本墙倒角底端、另一面墙倒角底端），南北向的墙负责
                            if (!ns) continue;
                            float qx = cxw + tx * sg * 0.5f + nx * (0.5f - endInset[e]), qz = czw + tz * sg * 0.5f + nz * (0.5f - endInset[e]);
                            bd.Tri(Rock, nx + tx * sg, 1.4f, nz + tz * sg,
                                PX(ut, Bevel), h, PZ(ut, Bevel), PX(ub, 0), y1, PZ(ub, 0), qx, y1, qz);
                        }
                        else
                        {
                            // 旁边是同高 / 更高的格：倒角的端头补一个三角（角点在 h 上）
                            float kx = PX(sg * 0.5f, 0), kz = PZ(sg * 0.5f, 0);
                            bd.Tri(Rock, nx - tx * sg * 0.3f, 1, nz - tz * sg * 0.3f,
                                PX(ut, Bevel), h, PZ(ut, Bevel), kx, h, kz, PX(ub, 0), y1, PZ(ub, 0));
                            // 凹角且对角格同高：两道倒角之间再补一片（内倒角的顶部），南北向的墙负责
                            int hq = H(map, x + nx + sg * tx, z + nz + sg * tz);
                            if (ns && endKind[e] == 2 && hq == h)
                            {
                                float qx = kx + nx * Fillet, qz = kz + nz * Fillet;
                                bd.Tri(Rock, nx - tx * sg, 1, nz - tz * sg, kx, h, kz, PX(ub, 0), y1, PZ(ub, 0), qx, y1, qz);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>朝南的墙：3 列 × 2 行，中间的顶点沿法线起伏，中间一行往里收一点当岩层折线；边上的顶点不动（和相邻的面对齐）。</summary>
        static void SouthWall(Builder bd, Fbm f, int nx, int nz, float ax, float az, float bx, float bz, float y0, float y1)
        {
            const int C = 3, R = 2;
            var px = new float[(C + 1) * (R + 1)]; var py = new float[px.Length]; var pz = new float[px.Length];
            for (int r = 0; r <= R; r++)
                for (int c = 0; c <= C; c++)
                {
                    int k = r * (C + 1) + c;
                    float t = (float)c / C;
                    float X = ax + (bx - ax) * t, Z = az + (bz - az) * t, Y = y0 + (y1 - y0) * r / R;
                    if (c > 0 && c < C && r > 0 && r < R)
                    {
                        float off = (float)f.Sample(X * 1.7 + Y * 0.9, Z * 1.7 - Y * 1.3) * FaceNoise - Crease;
                        X += nx * off; Z += nz * off;
                    }
                    px[k] = X; py[k] = Y; pz[k] = Z;
                }
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                {
                    int a = r * (C + 1) + c, b2 = a + 1, d = a + C + 1, e = d + 1;
                    bd.Tri(Rock, nx, 0, nz, px[a], py[a], pz[a], px[d], py[d], pz[d], px[e], py[e], pz[e]);
                    bd.Tri(Rock, nx, 0, nz, px[a], py[a], pz[a], px[e], py[e], pz[e], px[b2], py[b2], pz[b2]);
                }
        }

        /// <summary>写平面着色三角形（每个三角形自带法线）；法线朝向跟提示方向一致，必要时翻转绕序（Unity：从正面看顺时针）。</summary>
        sealed class Builder
        {
            public MeshData M; public int Ox, Oz;

            public void Quad(int mat, float hx, float hy, float hz,
                float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, float dx, float dy, float dz)
            {
                Tri(mat, hx, hy, hz, ax, ay, az, bx, by, bz, cx, cy, cz);
                Tri(mat, hx, hy, hz, ax, ay, az, cx, cy, cz, dx, dy, dz);
            }

            public void Tri(int mat, float hx, float hy, float hz,
                float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
            {
                float ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
                float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l < 1e-7f) return;
                nx /= l; ny /= l; nz /= l;
                bool same = nx * hx + ny * hy + nz * hz >= 0;
                if (!same) { nx = -nx; ny = -ny; nz = -nz; }
                var list = M.Indices[mat];
                int v0 = M.VertexCount;
                Add(ax, ay, az, nx, ny, nz);
                if (same) { Add(bx, by, bz, nx, ny, nz); Add(cx, cy, cz, nx, ny, nz); }
                else { Add(cx, cy, cz, nx, ny, nz); Add(bx, by, bz, nx, ny, nz); }
                list.Add(v0); list.Add(v0 + 1); list.Add(v0 + 2);
            }

            void Add(float x, float y, float z, float nx, float ny, float nz)
            {
                M.Positions.Add(x - Ox); M.Positions.Add(y); M.Positions.Add(z - Oz);
                M.Normals.Add(nx); M.Normals.Add(ny); M.Normals.Add(nz);
            }
        }
    }
}
