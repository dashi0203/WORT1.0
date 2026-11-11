using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>
    /// 地形外观方案 B（《地形外观与崖面美术规范》）：每格仍是立方体，碰撞照旧用 1m 体素。
    /// - 顶面是平的泥土；露出的顶边（落差 ≥ 1 层）倒一条约 0.1m 的斜面；
    /// - 外角的竖边倒约 0.1m；内角（凹角）的竖边和顶边倒一点点（约 0.04m）；
    /// - 顶面边缘一圈是岩石，往里渐变到泥土：每个顶点写融合权重 `MeshData.Blend`（0 = 泥土，1 = 岩石），
    ///   着色器再按岩石贴图的高低起伏决定交界（贴图见 TerrainTextures，128×128）；
    /// - 侧面是岩石，有体积：参考 topdowntemp 的悬崖件，每层侧面往外鼓约 0.17m、层中间一道折痕，格与格交界处收回；
    /// - 不变形的平地顶面按同高矩形合并。
    /// 画面上的地面永远不超出碰撞格（倒角只往里收）。水面、斜坡沿用 ChunkMesher。
    /// 不用 System.Random / Math.Sin 等，结果在 .NET 与 Unity 一致。
    /// </summary>
    public static class CliffMesher
    {
        public const float Bevel = 0.1f;   // 外倒角（顶边、外角竖边）
        public const float Fillet = 0.04f; // 内倒角（凹角）
        public const float Bulge = 0.17f;  // 侧面往外鼓（m）
        const float RimIn = 0.12f, RimOut = 0.5f, RimNoise = 0.1f; // 顶面融合：离崖边 ≤ RimIn 全岩石，≥ RimOut 全泥土
        const float FaceNoise = 0.04f;

        // 子网格：融合材质的地面都放在 Soil 里（着色器按 Blend 混合两张贴图）
        const int Rock = (int)VoxelMaterial.Soil, Dirt = (int)VoxelMaterial.Soil;

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
            MergeFlat(map, b, ox, oz, size);
            // 水面、斜坡：沿用体素网格的做法（融合权重补 0 = 泥土）
            AppendWaterAndSlopes(rv, mesh, cx, cz, true);
            while (mesh.Blend.Count < mesh.VertexCount) mesh.Blend.Add(0);
            return mesh;
        }

        /// <summary>只含斜坡体素的网格（Unity 里给斜坡做碰撞；其余碰撞用合并的碰撞盒，见 ColliderBoxes）。</summary>
        public static MeshData SlopesOnly(RegionVoxels rv, int cx, int cz)
        {
            var mesh = new MeshData();
            AppendWaterAndSlopes(rv, mesh, cx, cz, false);
            return mesh;
        }

        static void AppendWaterAndSlopes(RegionVoxels rv, MeshData mesh, int cx, int cz, bool water)
        {
            int size = rv.Map.Settings.ChunkSize, ox = cx * size, oz = cz * size;
            var chunk = rv.GetChunk(cx, cz);
            for (int y = chunk.MinY; y < chunk.MinY + chunk.SizeY; y++)
                for (int lz = 0; lz < size; lz++)
                    for (int lx = 0; lx < size; lx++)
                    {
                        var m = chunk.GetMaterial(lx, y, lz);
                        if (m == VoxelMaterial.Air) continue;
                        var s = chunk.GetShape(lx, y, lz);
                        if (m == VoxelMaterial.Water) { if (water) ChunkMesher.EmitWater(rv, mesh, ox + lx, y, oz + lz, ox, oz); }
                        else if (s != VoxelShape.Full) ChunkMesher.EmitSlope(rv, mesh, m, (int)s - 1, ox + lx, y, oz + lz, ox, oz);
                    }
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
            if (rim) // 平地格（不靠崖边）在 MergeFlat 里按矩形合并
            {
                const int K = 4;
                var bl = new float[(K + 1) * (K + 1)];
                for (int j = 0; j <= K; j++)
                    for (int i = 0; i <= K; i++)
                    {
                        float px = x0 + (x1 - x0) * i / K, pz = z0 + (z1 - z0) * j / K;
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
                        float t = (d - RimIn) / (RimOut - RimIn); t = t < 0 ? 0 : (t > 1 ? 1 : t);
                        bl[j * (K + 1) + i] = 1 - t * t * (3 - 2 * t);
                    }
                for (int j = 0; j < K; j++)
                    for (int i = 0; i < K; i++)
                    {
                        float ax = x0 + (x1 - x0) * i / K, bx = x0 + (x1 - x0) * (i + 1) / K;
                        float az = z0 + (z1 - z0) * j / K, bz = z0 + (z1 - z0) * (j + 1) / K;
                        float b00 = bl[j * (K + 1) + i], b10 = bl[j * (K + 1) + i + 1], b01 = bl[(j + 1) * (K + 1) + i], b11 = bl[(j + 1) * (K + 1) + i + 1];
                        bd.TriB(Dirt, 0, 1, 0, ax, h, az, b00, ax, h, bz, b01, bx, h, bz, b11);
                        bd.TriB(Dirt, 0, 1, 0, ax, h, az, b00, bx, h, bz, b11, bx, h, az, b10);
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
                    // 端头能否一起鼓：旁边那格在同一平面上也有墙、且这一段的上下沿一样（否则端头不动，避免裂缝）
                    var endBulge = new bool[2];
                    for (int e = 0; e < 2; e++)
                    {
                        if (endKind[e] != 0) continue;
                        int sg = e == 0 ? -1 : 1;
                        int hp = H(map, x + sg * tx, z + sg * tz), hd = H(map, x + nx + sg * tx, z + nz + sg * tz);
                        endBulge[e] = hp > y && hd <= y && (top == (y == hp - 1));
                    }
                    // 墙
                    Wall(bd, fFace, nx, nz, PX(u0, 0), PZ(u0, 0), PX(u1, 0), PZ(u1, 0), y0, y1, 4, 1f, endBulge[0], endBulge[1]);
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
                                if (sg > 0) Wall(bd, fFace, (nx + tx) * 0.7071f, (nz + tz) * 0.7071f, PX(ue, 0), PZ(ue, 0), qx, qz, y0, y1, 2, 1.2f, false, false);
                                else Wall(bd, fFace, (nx - tx) * 0.7071f, (nz - tz) * 0.7071f, qx, qz, PX(ue, 0), PZ(ue, 0), y0, y1, 2, 1.2f, false, false);
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

        /// <summary>
        /// 一段竖墙（从 A 到 B，法线 (nx, nz)）：cols 列 × 4 行；两端的列和上下两行不动（和相邻的面、倒角对齐），
        /// 中间的顶点往外鼓（参考 topdowntemp 的悬崖件），中间一行收回一些当折痕，再加一点低模噪声。
        /// </summary>
        static void Wall(Builder bd, Fbm f, float nx, float nz, float ax, float az, float bx, float bz, float y0, float y1, int cols, float amp, bool bulgeA, bool bulgeB)
        {
            const int R = 4;
            float[] rowProfile = { 0, 0.85f, 0.65f, 0.85f, 0 };
            int C = cols;
            var px = new float[(C + 1) * (R + 1)]; var py = new float[px.Length]; var pz = new float[px.Length];
            for (int r = 0; r <= R; r++)
                for (int c = 0; c <= C; c++)
                {
                    int k = r * (C + 1) + c;
                    float t = (float)c / C;
                    float X = ax + (bx - ax) * t, Z = az + (bz - az) * t, Y = y0 + (y1 - y0) * r / R;
                    bool inner = (c > 0 || bulgeA) && (c < C || bulgeB);
                    if (inner && r > 0 && r < R)
                    {
                        // 中间鼓、两端收一半；端头和旁边那格的墙用同一个值，所以连续
                        float colProfile = 1 - (2 * t - 1) * (2 * t - 1) * 0.5f;
                        float n01 = (float)f.Sample(X * 1.9 + Y * 0.7, Z * 1.9 - Y * 1.1) * 0.5f + 0.5f;
                        float off = Bulge * amp * rowProfile[r] * colProfile * (0.6f + 0.4f * n01)
                                    + (float)f.Sample(X * 4.1 - Y * 2.3, Z * 4.1 + Y * 3.7) * FaceNoise;
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

        /// <summary>不靠崖边的平地格（四邻和四个对角都不比它低），同高的按矩形贪心合并成一块顶面（泥土）。</summary>
        static void MergeFlat(RegionMap map, Builder bd, int ox, int oz, int size)
        {
            var done = new bool[size * size];
            bool Flat(int lx, int lz)
            {
                int x = ox + lx, z = oz + lz;
                if (x >= map.W || z >= map.H) return false;
                int h = H(map, x, z);
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if (H(map, x + dx, z + dz) < h) return false;
                return true;
            }
            var flat = new bool[size * size];
            for (int lz = 0; lz < size; lz++) for (int lx = 0; lx < size; lx++) flat[lz * size + lx] = Flat(lx, lz);
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int k = lz * size + lx;
                    if (done[k] || !flat[k]) continue;
                    int h = H(map, ox + lx, oz + lz);
                    bool Same(int ix, int iz) { int q = iz * size + ix; return flat[q] && !done[q] && H(map, ox + ix, oz + iz) == h; }
                    int w = 1; while (lx + w < size && Same(lx + w, lz)) w++;
                    int d = 1;
                    for (; lz + d < size; d++) { bool ok = true; for (int i = 0; i < w; i++) if (!Same(lx + i, lz + d)) { ok = false; break; } if (!ok) break; }
                    for (int j = 0; j < d; j++) for (int i = 0; i < w; i++) done[(lz + j) * size + lx + i] = true;
                    float x0 = ox + lx, z0 = oz + lz, x1 = x0 + w, z1 = z0 + d;
                    bd.TriB(Dirt, 0, 1, 0, x0, h, z0, 0, x0, h, z1, 0, x1, h, z1, 0);
                    bd.TriB(Dirt, 0, 1, 0, x0, h, z0, 0, x1, h, z1, 0, x1, h, z0, 0);
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
                => TriB(mat, hx, hy, hz, ax, ay, az, 1, bx, by, bz, 1, cx, cy, cz, 1);

            /// <summary>带每个顶点融合权重的三角形。</summary>
            public void TriB(int mat, float hx, float hy, float hz,
                float ax, float ay, float az, float ba, float bx, float by, float bz, float bb, float cx, float cy, float cz, float bc)
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
                Add(ax, ay, az, nx, ny, nz, ba);
                if (same) { Add(bx, by, bz, nx, ny, nz, bb); Add(cx, cy, cz, nx, ny, nz, bc); }
                else { Add(cx, cy, cz, nx, ny, nz, bc); Add(bx, by, bz, nx, ny, nz, bb); }
                list.Add(v0); list.Add(v0 + 1); list.Add(v0 + 2);
            }

            void Add(float x, float y, float z, float nx, float ny, float nz, float blend)
            {
                M.Positions.Add(x - Ox); M.Positions.Add(y); M.Positions.Add(z - Oz);
                M.Normals.Add(nx); M.Normals.Add(ny); M.Normals.Add(nz);
                M.Blend.Add(blend);
            }
        }
    }
}
