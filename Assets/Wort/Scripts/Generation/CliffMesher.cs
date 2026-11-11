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
        public const float FootFlare = 0.18f; // 崖脚横截面往外扩（m，贴地处最大，0.7m 以上为 0）
        public const float Bulge = 0.26f;  // 侧面往外鼓（m，Voronoi 石块中心处）
        const float RimIn = 0.12f, RimOut = 0.5f, RimNoise = 0.1f; // 顶面融合：离崖边 ≤ RimIn 全岩石，≥ RimOut 全泥土

        // 子网格：融合材质的地面都放在 Soil 里（着色器按 Blend 混合两张贴图）
        const int Rock = (int)VoxelMaterial.Soil, Dirt = (int)VoxelMaterial.Soil;

        public static MeshData Build(RegionVoxels rv, int cx, int cz, uint seed)
        {
            var map = rv.Map;
            var mesh = new MeshData();
            int size = map.Settings.ChunkSize, ox = cx * size, oz = cz * size;
            // 预留容量（每 chunk 约 5–6 万三角形），避免 List 反复扩容拷贝
            mesh.Positions.Capacity = 600000; mesh.Normals.Capacity = 600000; mesh.Blend.Capacity = 200000;
            mesh.Indices[Dirt].Capacity = 200000;
            var fRim = new Fbm(Hash.Of(seed, 911), 0.6, 2);
            
            var b = new Builder { M = mesh, Ox = ox, Oz = oz };
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    Column(map, b, fRim, x, z);
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
            // 只看有水 / 斜坡的格（不再扫整个 chunk 的体素体积）
            var map = rv.Map;
            var chunk = rv.GetChunk(cx, cz);
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    int i = map.Index(x, z), h = map.Height[i];
                    if (map.Slope[i] >= 0 && h >= chunk.MinY && h < chunk.MinY + chunk.SizeY)
                    {
                        var s = chunk.GetShape(lx, h, lz);
                        if (s != VoxelShape.Full) ChunkMesher.EmitSlope(rv, mesh, chunk.GetMaterial(lx, h, lz), (int)s - 1, x, h, z, ox, oz);
                    }
                    if (water && map.HasWater(i))
                        for (int y = Math.Max(h, chunk.MinY); y < map.Water[i] && y < chunk.MinY + chunk.SizeY; y++)
                            if (chunk.GetMaterial(lx, y, lz) == VoxelMaterial.Water) ChunkMesher.EmitWater(rv, mesh, x, y, z, ox, oz);
                }
        }

        static int H(RegionMap m, int x, int z)
        {
            if (x < 0) x = 0; if (z < 0) z = 0; if (x >= m.W) x = m.W - 1; if (z >= m.H) z = m.H - 1;
            return m.Height[z * m.W + x];
        }

        /// <summary>一格：顶面（含边缘材质）、每个露出侧面的墙、顶边倒角、角上的倒角面。墙归高的那一格。</summary>
        static void Column(RegionMap map, Builder bd, Fbm fRim, int x, int z)
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

                // 每一层（slab）两端的情况：0 = 直接到格边，1 = 外角（倒 Bevel），2 = 凹角（倒 Fillet）
                int nSlab = h - hn;
                var kindA = new int[nSlab]; var kindB = new int[nSlab];
                for (int k = 0; k < nSlab; k++)
                {
                    int y = hn + k;
                    for (int e = 0; e < 2; e++)
                    {
                        int sg = e == 0 ? -1 : 1;
                        int hp = H(map, x + sg * tx, z + sg * tz), hd = H(map, x + nx + sg * tx, z + nz + sg * tz);
                        bool perpSolid = hp > y, diagSolid = hd > y;
                        int kind = !perpSolid && !diagSolid ? 1 : (perpSolid && diagSolid ? 2 : 0);
                        if (e == 0) kindA[k] = kind; else kindB[k] = kind;
                    }
                }
                float Inset(int kind) => kind == 1 ? Bevel : (kind == 2 ? Fillet : 0);
                // 两端情况相同的连续几层合并成一段墙（不再一层一段，避免横向条纹）
                for (int k0 = 0; k0 < nSlab;)
                {
                    int k1 = k0 + 1;
                    while (k1 < nSlab && kindA[k1] == kindA[k0] && kindB[k1] == kindB[k0]) k1++;
                    bool segTop = k1 == nSlab;
                    float y0 = hn + k0, y1 = segTop ? h - Bevel : hn + k1;
                    int kA = kindA[k0], kB = kindB[k0];
                    float u0 = -0.5f + Inset(kA), u1 = 0.5f - Inset(kB);
                    // 端头和同一平面上相邻格的墙重叠的高度范围（开区间）：这个范围里两面墙在共用的边上有同样的行，端头顶点可以一起鼓
                    var endLo = new float[2]; var endHi = new float[2];
                    for (int e = 0; e < 2; e++)
                    {
                        endLo[e] = 1e9f; endHi[e] = -1e9f;
                        if ((e == 0 ? kA : kB) != 0) continue;
                        int sg = e == 0 ? -1 : 1;
                        int hp = H(map, x + sg * tx, z + sg * tz), hd = H(map, x + nx + sg * tx, z + nz + sg * tz);
                        if (hp <= hd) continue;
                        endLo[e] = Math.Max(hn, hd) + 0.01f;
                        endHi[e] = hp == h ? h - Bevel - 0.01f : Math.Min(h, hp) - Bevel - 0.49f;
                    }
                    Wall(bd, nx, nz, PX(u0, 0), PZ(u0, 0), PX(u1, 0), PZ(u1, 0), y0, y1, 4, endLo[0], endHi[0], endLo[1], endHi[1], k0 == 0, segTop);
                    // 角上的竖向倒角面（外角）/ 内倒角面（凹角），由南北向的墙负责
                    if (ns)
                        for (int e = 0; e < 2; e++)
                        {
                            int sg = e == 0 ? -1 : 1;
                            int kind = e == 0 ? kA : kB;
                            float ue = sg * (0.5f - Inset(kind));
                            if (kind == 1)
                            {
                                float qx = cxw + tx * sg * 0.5f + nx * (0.5f - Bevel), qz = czw + tz * sg * 0.5f + nz * (0.5f - Bevel);
                                if (sg > 0) Wall(bd, (nx + tx) * 0.7071f, (nz + tz) * 0.7071f, PX(ue, 0), PZ(ue, 0), qx, qz, y0, y1, 1, 1, 0, 1, 0, false, true);
                                else Wall(bd, (nx - tx) * 0.7071f, (nz - tz) * 0.7071f, qx, qz, PX(ue, 0), PZ(ue, 0), y0, y1, 1, 1, 0, 1, 0, false, true);
                                // 倒角削掉的那个小三角正下方没有地面（是高格自己的范围），补一个三角形当地面
                                float kx = cxw + nx * 0.5f + tx * sg * 0.5f, kz = czw + nz * 0.5f + tz * sg * 0.5f;
                                bd.TriB(Dirt, 0, 1, 0, kx, y0, kz, 0, PX(ue, 0), y0, PZ(ue, 0), 0, qx, y0, qz, 0);
                            }
                            else if (kind == 2)
                            {
                                int hq = H(map, x + nx + sg * tx, z + nz + sg * tz);
                                float yTop = Math.Min(y1, hq - Bevel);
                                float kx = cxw + nx * 0.5f + tx * sg * 0.5f, kz = czw + nz * 0.5f + tz * sg * 0.5f; // 凹角的竖边
                                float qx = kx + nx * Fillet, qz = kz + nz * Fillet;
                                if (yTop > y0) bd.Quad(Rock, nx - tx * sg, 0, nz - tz * sg, PX(ue, 0), y0, PZ(ue, 0), PX(ue, 0), yTop, PZ(ue, 0), qx, yTop, qz, qx, y0, qz);
                            }
                        }
                    k0 = k1;
                }
                {
                    float y1 = h - Bevel;
                    var endInset = new[] { Inset(kindA[nSlab - 1]), Inset(kindB[nSlab - 1]) };
                    var endKind = new[] { kindA[nSlab - 1], kindB[nSlab - 1] };
                    float u0 = -0.5f + endInset[0], u1 = 0.5f - endInset[1];
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
                            // 旁边是同高 / 更高的格：倒角的端头补一个三角（角点在 h 上）。
                            // 旁边那格同高、同一侧也露出时，两段倒角本来就连成一条，端头不补（否则是一片悬在斜面上的小三角）
                            float kx = PX(sg * 0.5f, 0), kz = PZ(sg * 0.5f, 0);
                            int hp2 = H(map, x + sg * tx, z + sg * tz), hpn = H(map, x + sg * tx + nx, z + sg * tz + nz);
                            bool continues = hp2 == h && hpn < h;
                            if (!continues)
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
        /// 一段竖墙（从 A 到 B，法线 (nx, nz)，高 y0..y1，可跨多层）：cols 列，行按世界高度每 0.5m 一行（相邻墙的行对得上）。
        /// 内部顶点上下左右错开一点，再按 Voronoi 噪声往外鼓（一块块石头，不是横条）；上沿不动（接顶边倒角）；
        /// ground 为 true 时最下一行往外撇一点（崖脚）。端头的列只在 [lo, hi] 高度范围里鼓（和相邻格的墙一致），否则不动。
        /// 位移只取决于世界坐标，所以相邻的墙在共用的边上算出的位置相同、不会裂。
        /// </summary>
        static void Wall(Builder bd, float nx, float nz, float ax, float az, float bx, float bz, float y0, float y1, int cols,
            float loA, float hiA, float loB, float hiB, bool ground, bool realTop)
        {
            if (y1 - y0 < 1e-3f) return;
            var rows = rowBuf ?? (rowBuf = new List<float>(128));
            rows.Clear(); rows.Add(y0);
            // 朝南的面（镜头看得见）每 0.25m 一行、列也加倍，其余每 0.5m 一行
            // 北面镜头看不见：不细分、不鼓；东西面只看得到轮廓：列减半
            bool south = nz < -0.5f, north = nz > 0.5f;
            float step = south ? 0.25f : (north ? 100f : 0.5f);
            if (south) cols *= 2; else if (north) cols = 1; else cols = Math.Max(1, cols / 2);
            for (float y = (float)Math.Floor(y0 / step + 1.001f) * step; y < y1 - step * 0.4f; y += step) rows.Add(y);
            rows.Add(y1);
            int C = cols, R = rows.Count - 1;
            float tx = -nz, tz = nx; // 沿墙方向（世界）
            float plane = ax * nx + az * nz;
            float along0 = ax * tx + az * tz, along1 = bx * tx + bz * tz;
            bool vorReady = false;
            var vb = vorBig ?? (vorBig = new VorCache()); var vs = vorSmall ?? (vorSmall = new VorCache());
            int nv = (C + 1) * (R + 1);
            if (wx == null || wx.Length < nv) { wx = new float[nv * 2]; wy = new float[nv * 2]; wz = new float[nv * 2]; }
            float[] px = wx, py = wy, pz = wz;
            for (int r = 0; r <= R; r++)
                for (int c = 0; c <= C; c++)
                {
                    int k = r * (C + 1) + c;
                    float t = (float)c / C;
                    float X = ax + (bx - ax) * t, Z = az + (bz - az) * t, Y = rows[r];
                    float along = X * tx + Z * tz;
                    bool end = c == 0 || c == C;
                    bool interiorRow = r > 0 && r < R;
                    // 端头列：在和相邻墙重叠的范围内都可动（分段处的上下沿也算，只要不是地面和真正的顶）
                    bool endRowOk = (r > 0 || !ground) && (r < R || !realTop);
                    bool move = end ? endRowOk && (c == 0 ? (Y > loA && Y < hiA) : (Y > loB && Y < hiB)) : interiorRow;
                    if (move)
                    {
                        // 错开：只对非端头的列沿墙错开，所有可动顶点上下错开（都取决于世界坐标）
                        uint hsh = Hash.Of((int)Math.Floor(along * 8 + 0.5f), (int)Math.Floor(Y * 8 + 0.5f), (uint)(plane * 131 + 7));
                        float jy = ((hsh & 0xFF) / 255f - 0.5f) * 0.44f * Math.Min(step, 0.5f); // 北面 step 很大，上下错开不能跟着放大
                        float ju = end ? 0 : (((hsh >> 8) & 0xFF) / 255f - 0.5f) * 0.12f;
                        Y += jy; along += ju; X += tx * ju; Z += tz * ju;
                        // 两层 Voronoi：大块（2.2m 一格）决定整体凸起，小块（0.8m）在上面加细节；大块占主导，小凸起合并成大凸起
                        if (!vorReady) { vb.Init(Math.Min(along0, along1) - 0.3f, Math.Max(along0, along1) + 0.3f, y0 - 0.3f, y1 + 0.3f, 2.2f, (uint)(plane * 977 + 3)); vs.Init(Math.Min(along0, along1) - 0.3f, Math.Max(along0, along1) + 0.3f, y0 - 0.3f, y1 + 0.3f, 0.65f, (uint)(plane * 977 + 11)); vorReady = true; }
                        float qb = 1 - vb.F1(along, Y) * 1.6f; qb = qb < 0 ? 0 : qb;
                        float qs = 1 - vs.F1(along, Y) * 1.7f; qs = qs < 0 ? 0 : qs;
                        float off = Bulge * (0.05f + 0.45f * qb + 0.6f * qs);
                        if (ground) { float ft = 1 - (Y - y0) / 0.7f; if (ft > 0) off += FootFlare * ft * ft; } // 崖脚往外扩
                        X += nx * off; Z += nz * off;
                    }
                    else if (r == 0 && ground && !end)
                    {
                        X += nx * FootFlare; Z += nz * FootFlare; // 崖脚往外扩
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

        [ThreadStatic] static List<float> rowBuf;
        [ThreadStatic] static VorCache vorBig, vorSmall;

        /// <summary>
        /// 一面墙范围内的 Voronoi 特征点缓存（每格一个随机点，先算好），F1 查询不再重复算哈希。结果与逐点算完全相同。
        /// </summary>
        sealed class VorCache
        {
            float g; int gu0, gv0, nu, nv; float[] pu = new float[64], pv = new float[64];
            public void Init(float u0, float u1, float v0, float v1, float grid, uint seed)
            {
                g = grid;
                gu0 = (int)Math.Floor(u0 / g) - 1; gv0 = (int)Math.Floor(v0 / g) - 1;
                nu = (int)Math.Floor(u1 / g) + 2 - gu0; nv = (int)Math.Floor(v1 / g) + 2 - gv0;
                int n = nu * nv;
                if (pu.Length < n) { pu = new float[n * 2]; pv = new float[n * 2]; }
                for (int j = 0; j < nv; j++)
                    for (int i = 0; i < nu; i++)
                    {
                        uint hh = Hash.Of(gu0 + i, gv0 + j, seed);
                        pu[j * nu + i] = (gu0 + i + (hh & 0xFFFF) / 65535f) * g;
                        pv[j * nu + i] = (gv0 + j + (hh >> 16) / 65535f) * g;
                    }
            }
            public float F1(float u, float v)
            {
                int cu = (int)Math.Floor(u / g) - gu0, cv = (int)Math.Floor(v / g) - gv0;
                float best = 9;
                for (int j = cv - 1; j <= cv + 1; j++)
                    for (int i = cu - 1; i <= cu + 1; i++)
                    {
                        int k = j * nu + i;
                        float du = u - pu[k], dv = v - pv[k], d = du * du + dv * dv;
                        if (d < best) best = d;
                    }
                return (float)Math.Sqrt(best) / g;
            }
        }
        [ThreadStatic] static float[] wx, wy, wz;

        /// <summary>Voronoi F1（格 G 米，每格一个随机点），返回到最近点的距离 / G，约 0–0.8。只用开方，结果确定。</summary>
        static float Vor(float u, float v, uint seed, float G)
        {
            int gu = (int)Math.Floor(u / G), gv = (int)Math.Floor(v / G);
            float best = 9;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    uint hh = Hash.Of(gu + i, gv + j, seed);
                    float cu = (gu + i + (hh & 0xFFFF) / 65535f) * G, cv = (gv + j + (hh >> 16) / 65535f) * G;
                    float du = u - cu, dv = v - cv, d = du * du + dv * dv;
                    if (d < best) best = d;
                }
            return (float)Math.Sqrt(best) / G;
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
