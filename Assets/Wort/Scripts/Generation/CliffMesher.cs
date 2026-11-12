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
    /// - 斜坡（坡道）也在这里画：坡面是泥土 / 路面，两侧露出的部分是岩石墙（和崖面同样的外鼓、崖脚外扩）；
    ///   坡顶接台面处不画墙、不倒角、不长岩石边，路面连续；坡道挖进崖里时，旁边高格的崖面顺着斜边露出来。
    /// - 路面：每个朝上的顶点按 RoadSurface 的遮罩写 `MeshData.Road`；路面附近的格逐格细分，不参与平地合并。
    /// 画面上的地面永远不超出碰撞格（倒角只往里收）。水面沿用 ChunkMesher。
    /// 不用 System.Random / Math.Sin 等，结果在 .NET 与 Unity 一致。
    /// </summary>
    public static class CliffMesher
    {
        public const float Bevel = 0.1f;   // 外倒角（顶边、外角竖边）
        public const float Fillet = 0.04f; // 内倒角（凹角）
        public const float FootFlare = 0.18f; // 崖脚横截面往外扩（m，贴地处最大，0.7m 以上为 0）
        public const float Bulge = 0.26f;  // 侧面往外鼓（m，Voronoi 石块中心处）
        const float RimIn = 0.12f, RimOut = 0.5f, RimNoise = 0.1f; // 顶面融合：离崖边 ≤ RimIn 全岩石，≥ RimOut 全泥土
        // 路面离崖边 / 崖脚（任何高差处）要留出泥土和岩石边：≤ RoadIn 没有路面，≥ RoadOut 完整路面（负责人 2026-11-12）
        const float RoadIn = 0.35f, RoadOut = 0.6f;
        static float RoadEdge(float d) { float t = (d - RoadIn) / (RoadOut - RoadIn); t = t < 0 ? 0 : (t > 1 ? 1 : t); return t * t * (3 - 2 * t); }

        // 子网格：融合材质的地面都放在 Soil 里（着色器按 Blend 混合两张贴图）
        const int Rock = (int)VoxelMaterial.Soil, Dirt = (int)VoxelMaterial.Soil;

        public static MeshData Build(RegionVoxels rv, int cx, int cz, uint seed)
        {
            var map = rv.Map;
            var mesh = new MeshData();
            int size = map.Settings.ChunkSize, ox = cx * size, oz = cz * size;
            // 预留容量（每 chunk 约 5–6 万三角形），避免 List 反复扩容拷贝
            mesh.Positions.Capacity = 600000; mesh.Normals.Capacity = 600000; mesh.Blend.Capacity = 200000; mesh.Road.Capacity = 200000;
            mesh.Indices[Dirt].Capacity = 200000;
            var fRim = new Fbm(Hash.Of(seed, 911), 0.6, 2);
            var road = rv.RoadMask ?? (rv.RoadMask = RoadSurface.BuildMask(map));
            var mouths = rv.RoadMouths ?? (rv.RoadMouths = RoadSurface.BuildMouths(map));

            var b = new Builder { M = mesh, Ox = ox, Oz = oz, RoadMask = road, MW = map.W, MH = map.H, Mouths = mouths };
            for (int lz = 0; lz < size; lz++)
                for (int lx = 0; lx < size; lx++)
                {
                    int x = ox + lx, z = oz + lz;
                    if (x >= map.W || z >= map.H) continue;
                    if (map.Slope[map.Index(x, z)] >= 0) SlopeColumn(map, b, fRim, x, z);
                    else Column(map, b, fRim, x, z, road);
                }
            MergeFlat(map, b, ox, oz, size, road);
            // 水面：沿用体素网格的做法（融合权重、路面权重补 0）
            AppendWaterAndSlopes(rv, mesh, cx, cz, true, false);
            while (mesh.Blend.Count < mesh.VertexCount) mesh.Blend.Add(0);
            while (mesh.Road.Count < mesh.VertexCount) mesh.Road.Add(0);
            return mesh;
        }

        /// <summary>只含斜坡体素的网格（Unity 里给斜坡做碰撞；其余碰撞用合并的碰撞盒，见 ColliderBoxes）。</summary>
        public static MeshData SlopesOnly(RegionVoxels rv, int cx, int cz)
        {
            var mesh = new MeshData();
            AppendWaterAndSlopes(rv, mesh, cx, cz, false, true);
            return mesh;
        }

        static void AppendWaterAndSlopes(RegionVoxels rv, MeshData mesh, int cx, int cz, bool water, bool slopes)
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
                    if (slopes && map.Slope[i] >= 0 && h >= chunk.MinY && h < chunk.MinY + chunk.SizeY)
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

        /// <summary>
        /// 邻格 (x + dx, z + dz) 靠近本格那条边 / 那个角的高度：斜坡朝本格升起时是坡顶（高度 + 1），否则是格高度（斜坡取低端）。
        /// 用来判断本格那一侧是否露出：坡顶接台面时不算露出（不画墙、不倒角、不长岩石边）。
        /// </summary>
        static int Low(RegionMap m, int x, int z, int dx, int dz)
        {
            int ax = x + dx, az = z + dz;
            if (ax < 0) ax = 0; if (az < 0) az = 0; if (ax >= m.W) ax = m.W - 1; if (az >= m.H) az = m.H - 1;
            int i = az * m.W + ax, up = m.Slope[i], h = m.Height[i];
            if (up < 0) return h;
            int ux = Grid.DX4[up], uz = Grid.DZ4[up];
            return (dx != 0 && ux == -dx) || (dz != 0 && uz == -dz) ? h + 1 : h;
        }

        /// <summary>
        /// 斜坡格，做法与崖顶相同：
        /// - 坡面在露出的侧边往里收 Bevel，再倒一条随坡倾斜的斜面；侧边一圈岩石往里渐变到泥土 / 路面；
        /// - 侧面是一整块鼓包岩石墙（崖脚 → 斜边），顶边顺着坡斜着走（SlopeSideWall）；
        /// - 坡顶接台面、坡底接平地处不画墙、不倒角，路面连续。
        /// </summary>
        static void SlopeColumn(RegionMap map, Builder bd, Fbm fRim, int x, int z)
        {
            int h = H(map, x, z), up = map.Slope[map.Index(x, z)], dn = (up + 2) & 3;
            int ux = Grid.DX4[up], uz = Grid.DZ4[up];
            float cxw = x + 0.5f, czw = z + 0.5f;
            const int K = 4;
            float Y(float px, float pz) => h + ((px - cxw) * ux + (pz - czw) * uz + 0.5f);
            var side = new bool[4]; var edge = new bool[4]; var low = new int[4];
            for (int s = 0; s < 4; s++)
            {
                bool sideways = s != up && s != dn;
                low[s] = Low(map, x, z, Grid.DX4[s], Grid.DZ4[s]);
                int ni = map.Index(Math.Clamp(x + Grid.DX4[s], 0, map.W - 1), Math.Clamp(z + Grid.DZ4[s], 0, map.H - 1));
                bool sameRamp = map.Slope[ni] == up && map.Height[ni] == h; // 并排的同一条坡道
                side[s] = sideways && !sameRamp && low[s] <= h;   // 侧边露出（至少高的一端露出）
                // 路面的边：侧面不是同一条坡道（低处的崖边或高处的崖脚）
                edge[s] = sideways && !sameRamp;
            }
            // ---- 坡面：露出的侧边收进倒角宽度 ----
            // 倒角宽度沿坡长变化：两端相邻格（坡底那格 / 坡顶那格）在同一侧也露出时保持 Bevel，和它们的倒角接上；
            // 不露出时在最后 0.3m 内收窄到 0，坡面边缘平滑并进台面 / 平地（负责人 2026-11-12：不要用小三角硬补）
            var tLow = new float[4]; var tHigh = new float[4];
            for (int s = 0; s < 4; s++)
            {
                if (!side[s]) continue;
                int nx = Grid.DX4[s], nz = Grid.DZ4[s];
                tLow[s] = Low(map, x, z, nx - ux, nz - uz) < h ? 1 : 0;       // 坡底那格这一侧也露出
                // 坡顶一端不收窄：坡顶那格这一侧露出时倒角直接接上；不露出时是凹角（旁边台面的崖边转过来），倒角也要到头、和台面崖边斜接
                tHigh[s] = 1;
            }
            float Taper(int s, float a)
            {
                float lo = tLow[s] > 0 ? 1 : Math.Min(1f, a / 0.3f), hi = tHigh[s] > 0 ? 1 : Math.Min(1f, (1 - a) / 0.3f);
                return Math.Max(0f, Math.Min(lo, hi));
            }

            float Inset(int s, float a) => side[s] ? Bevel * Taper(s, a) : 0f;
            // 坡面网格：沿坡 a（0 = 坡底）、横向 b；两侧各收进各自的倒角宽度
            int pa = (up + 1) & 3, pb = (up + 3) & 3; // 横向两侧
            int pxs = Grid.DX4[pa], pzs = Grid.DZ4[pa];
            float lowX = cxw - ux * 0.5f, lowZ = czw - uz * 0.5f;
            void P(float a, float bb, out float px, out float pz)
            {
                float i0 = Inset(pb, a), i1 = Inset(pa, a);
                float c = -0.5f + i0 + (1 - i0 - i1) * bb; // 沿 pa 方向的横向坐标
                px = lowX + ux * a + pxs * c; pz = lowZ + uz * a + pzs * c;
            }
            var bl = new float[(K + 1) * (K + 1)];
            var rf = new float[(K + 1) * (K + 1)];
            var vxs = new float[(K + 1) * (K + 1)]; var vzs = new float[(K + 1) * (K + 1)];
            for (int j = 0; j <= K; j++)
                for (int i = 0; i <= K; i++)
                {
                    float a = (float)j / K;
                    P(a, (float)i / K, out float px, out float pz);
                    vxs[j * (K + 1) + i] = px; vzs[j * (K + 1) + i] = pz;
                    float d = 9, dr = 9;
                    for (int s = 0; s < 4; s++)
                    {
                        float ds = 0.5f - ((px - cxw) * Grid.DX4[s] + (pz - czw) * Grid.DZ4[s]);
                        // 岩石边随这一侧“露出的高度”变宽，并跟着倒角在不接倒角的一端淡出
                        float expo = Y(px, pz) - low[s]; expo = expo < 0 ? 0 : (expo > 1 ? 1 : expo);
                        if (side[s]) d = Math.Min(d, ds + (1 - expo * Taper(s, a)) * 0.6f);
                        if (edge[s]) dr = Math.Min(dr, ds);
                    }
                    rf[j * (K + 1) + i] = RoadEdge(dr + (float)fRim.Sample(px * 1.7 + 5.1, pz * 1.7) * 0.12f);
                    d += (float)fRim.Sample(px * 2.3, pz * 2.3) * RimNoise;
                    float t = (d - RimIn) / (RimOut - RimIn); t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    bl[j * (K + 1) + i] = 1 - t * t * (3 - 2 * t);
                }
            for (int j = 0; j < K; j++)
                for (int i = 0; i < K; i++)
                {
                    int k00 = j * (K + 1) + i, k10 = k00 + 1, k01 = k00 + K + 1, k11 = k01 + 1;
                    float X(int k) => vxs[k]; float Zc(int k) => vzs[k]; float YY(int k) => Y(vxs[k], vzs[k]);
                    bd.TriBR(Dirt, -ux, 1, -uz, X(k00), YY(k00), Zc(k00), bl[k00], rf[k00], X(k01), YY(k01), Zc(k01), bl[k01], rf[k01], X(k11), YY(k11), Zc(k11), bl[k11], rf[k11]);
                    bd.TriBR(Dirt, -ux, 1, -uz, X(k00), YY(k00), Zc(k00), bl[k00], rf[k00], X(k11), YY(k11), Zc(k11), bl[k11], rf[k11], X(k10), YY(k10), Zc(k10), bl[k10], rf[k10]);
                }
            // ---- 四个侧面 ----
            for (int s = 0; s < 4; s++)
            {
                int nx = Grid.DX4[s], nz = Grid.DZ4[s], tx = -nz, tz = nx;
                int e = low[s];
                float ax = cxw + nx * 0.5f - tx * 0.5f, az = czw + nz * 0.5f - tz * 0.5f;
                float bx = cxw + nx * 0.5f + tx * 0.5f, bz = czw + nz * 0.5f + tz * 0.5f;
                if (s == up)
                {
                    // 背面（坡顶一侧）比邻格高：少见，平的岩石面
                    float y0 = Math.Max(e, h);
                    if (y0 < h + 1) bd.Quad(Rock, nx, 0, nz, ax, y0, az, ax, h + 1, az, bx, h + 1, bz, bx, y0, bz);
                    continue;
                }
                if (s == dn)
                {
                    if (e < h) Wall(bd, nx, nz, ax, az, bx, bz, e, h, 4, 1, 0, 1, 0, true, false);
                    continue;
                }
                if (!side[s])
                {
                    // 侧邻更高（坡道挖进崖里）：高格的墙顶有倒角往里收，补上斜坡自己的侧面三角，免得露出细缝
                    if (!edge[s]) continue; // 并排的同一条坡道
                    float yA = Y(ax, az), yB = Y(bx, bz);
                    if (yA > yB) bd.Tri(Rock, nx, 0, nz, bx, h, bz, ax, h, az, ax, yA, az);
                    else bd.Tri(Rock, nx, 0, nz, ax, h, az, bx, h, bz, bx, yB, bz);
                    continue;
                }
                // 侧面：沿墙方向 t 上，哪一端是坡的高端
                int sgHigh = tx * ux + tz * uz > 0 ? 1 : -1;
                float lx = sgHigh > 0 ? ax : bx, lz = sgHigh > 0 ? az : bz, hx = sgHigh > 0 ? bx : ax, hz = sgHigh > 0 ? bz : az;
                // 两端和同一平面上相邻格的墙在公共竖边上一起鼓（规则与 Column 相同，墙顶按各端的高度）
                var lo = new float[2]; var hi = new float[2]; // [0] = 低端，[1] = 高端
                for (int k = 0; k < 2; k++)
                {
                    int sg = k == 0 ? -sgHigh : sgHigh, top = k == 0 ? h : h + 1;
                    lo[k] = 1e9f; hi[k] = -1e9f;
                    int hp = Low(map, x, z, sg * tx, sg * tz), hd = Low(map, x, z, nx + sg * tx, nz + sg * tz);
                    if (hp <= hd) continue;
                    lo[k] = Math.Max(e, hd) + 0.01f;
                    hi[k] = hp == top ? top - Bevel - 0.01f : Math.Min(top, hp) - Bevel - 0.49f;
                }
                // 坡顶是凹角（旁边台面格和坡顶同高）时，高端和普通凹角一样往里收 Fillet、端头不动，和台面墙的内倒角对上
                bool innerTop = Low(map, x, z, nx + ux, nz + uz) >= h + 1;
                float whx = innerTop ? hx - ux * Fillet : hx, whz = innerTop ? hz - uz * Fillet : hz;
                if (innerTop) { lo[1] = 1e9f; hi[1] = -1e9f; }
                SlopeSideWall(bd, nx, nz, lx, lz, whx, whz, e, h, lo[0], hi[0], lo[1], hi[1]);
                // 坡顶的凹角：坡顶那格旁边的台面格和坡顶同高（它朝外的崖边转过来），两条倒角在这里斜接成一块小斜面
                if (Low(map, x, z, nx + ux, nz + uz) >= h + 1)
                    bd.Tri(Rock, nx - ux, 1, nz - uz, hx, h + 1 - Bevel, hz, hx - nx * Bevel, h + 1, hz - nz * Bevel, hx + ux * Bevel, h + 1, hz + uz * Bevel);
                // 斜着的顶边倒角，分 K 段：墙面上沿（坡高 − 倒角宽）→ 坡面边（收进倒角宽）；
                // 倒角收窄处，墙顶（坡高 − Bevel）到倒角下沿之间补一条墙面（不低于邻格地面）
                var cuts = new List<float>();
                for (int k = 0; k <= K; k++) cuts.Add((float)k / K);
                float kink = e - (h - Bevel); // 墙顶线和邻格地面的交点（补墙条带下沿在这里折一下）
                if (kink > 0 && kink < 1) { cuts.Add(kink); cuts.Sort(); }
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    float a0 = cuts[k], a1 = cuts[k + 1];
                    if (a1 - a0 < 1e-5f) continue;
                    float i0 = Inset(s, a0), i1 = Inset(s, a1);
                    float px0 = lx + (hx - lx) * a0, pz0 = lz + (hz - lz) * a0, px1 = lx + (hx - lx) * a1, pz1 = lz + (hz - lz) * a1;
                    float y0 = h + a0, y1 = h + a1;
                    bd.Quad(Rock, nx, 1, nz, px0, y0 - i0, pz0, px1, y1 - i1, pz1, px1 - nx * i1, y1, pz1 - nz * i1, px0 - nx * i0, y0, pz0 - nz * i0);
                    float b0 = Math.Max(e, y0 - Bevel), b1 = Math.Max(e, y1 - Bevel);
                    if (y0 - i0 > b0 + 1e-4f || y1 - i1 > b1 + 1e-4f)
                        bd.Quad(Rock, nx, 0, nz, px0, b0, pz0, px0, Math.Max(b0, y0 - i0), pz0, px1, Math.Max(b1, y1 - i1), pz1, px1, b1, pz1);
                }
            }
        }

        /// <summary>
        /// 斜坡侧面的岩石墙：从崖脚 e 到斜着的顶边（低端 h − Bevel，高端 h + 1 − Bevel）。
        /// 行按世界高度对齐（和 Wall 相同），每行从斜边到高端均分列；鼓包、错开、崖脚外扩的算法与 Wall 相同，
        /// 斜边上的点不动（接倒角），两端的列只在 [lo, hi] 里动（和相邻格的墙一致）。
        /// </summary>
        static void SlopeSideWall(Builder bd, float nx, float nz, float lx, float lz, float hx, float hz, int e, int h,
            float loL, float hiL, float loH, float hiH)
        {
            // 北面（坡道背面）也鼓，而且鼓得更多，让坡道外形不那么横平竖直（负责人 2026-11-12）；
            // 端头不动，因为相邻崖面的北墙不细分、不鼓
            bool south = nz < -0.5f, north = nz > 0.5f;
            float step = south ? 0.25f : 0.5f;
            int C = south ? 8 : (north ? 4 : 2);
            // 朝南的面（正对镜头）鼓得弱一些（负责人 2026-11-12）
            float bulgeK = north ? 3f : (south ? 0.5f : 1f), flareK = north ? 3.5f : (south ? 0f : 1f);
            if (north) { loL = 1e9f; hiL = -1e9f; loH = 1e9f; hiH = -1e9f; }
            float top0 = h - Bevel, top1 = h + 1 - Bevel;
            var rows = new List<float> { e };
            for (float y = (float)Math.Floor(e / step + 1.001f) * step; y < top1 - step * 0.4f; y += step) rows.Add(y);
            rows.Add(top1);
            // 低端那一列要正好到墙顶（坡底高度 − Bevel），和旁边平台的墙对上；否则行从这里直接转到斜边，留一条三角缝
            if (top0 > e + 0.01f && !rows.Exists(v => Math.Abs(v - top0) < 0.01f)) { rows.Add(top0); rows.Sort(); }
            int R = rows.Count - 1;
            float tx = -nz, tz = nx;
            float plane = lx * nx + lz * nz;
            float alongL = lx * tx + lz * tz, alongH = hx * tx + hz * tz;
            var vb = vorBig ?? (vorBig = new VorCache()); var vs = vorSmall ?? (vorSmall = new VorCache());
            vb.Init(Math.Min(alongL, alongH) - 0.3f, Math.Max(alongL, alongH) + 0.3f, e - 0.3f, top1 + 0.3f, 2.2f, (uint)(plane * 977 + 3));
            vs.Init(Math.Min(alongL, alongH) - 0.3f, Math.Max(alongL, alongH) + 0.3f, e - 0.3f, top1 + 0.3f, 0.65f, (uint)(plane * 977 + 11));
            int nv = (C + 1) * (R + 1);
            var px = new float[nv]; var py = new float[nv]; var pz = new float[nv];
            for (int r = 0; r <= R; r++)
            {
                float y = rows[r];
                float ws = y > top0 ? Math.Min(1f, y - top0) : 0f; // 这一行从斜边开始的位置（0 = 低端）
                for (int c = 0; c <= C; c++)
                {
                    int k = r * (C + 1) + c;
                    float w = ws + (1 - ws) * c / C;
                    float X = lx + (hx - lx) * w, Z = lz + (hz - lz) * w, Y = y;
                    bool diag = c == 0 && ws > 0, lowEnd = c == 0 && ws == 0, highEnd = c == C;
                    bool move;
                    if (r == R || diag) move = false;
                    else if (lowEnd) move = r > 0 && Y > loL && Y < hiL;
                    else if (highEnd) move = r > 0 && Y > loH && Y < hiH;
                    else move = r > 0;
                    if (move)
                    {
                        float along = X * tx + Z * tz;
                        bool end = lowEnd || highEnd;
                        uint hsh = Hash.Of((int)Math.Floor(along * 8 + 0.5f), (int)Math.Floor(Y * 8 + 0.5f), (uint)(plane * 131 + 7));
                        float jy = ((hsh & 0xFF) / 255f - 0.5f) * 0.44f * Math.Min(step, 0.5f);
                        float ju = end ? 0 : (((hsh >> 8) & 0xFF) / 255f - 0.5f) * 0.12f;
                        // 斜边附近：错开和鼓包都逐渐减弱（离斜边 0.4m 以内），列挤在一起时不沿墙错开，免得三角形交叉
                        // 北面的加强鼓包只在下半截：离顶边 1.2m 内减到 0，免得从倒角后面冒出来，看着像倒角 / 岩石边往外扩
                        float below = top0 + w - Y, fade = below / (north ? 1.2f : 0.4f); fade = fade < 0 ? 0 : (fade > 1 ? 1 : fade);
                        if (!end) { jy *= fade; ju *= fade * (1 - ws); }
                        Y += jy; along += ju; X += tx * ju; Z += tz * ju;
                        float qb = 1 - vb.F1(along, Y) * 1.6f; qb = qb < 0 ? 0 : qb;
                        float qs = 1 - vs.F1(along, Y) * 1.7f; qs = qs < 0 ? 0 : qs;
                        // 端头列和相邻格的墙共用竖边，鼓包幅度必须和它们一样（不乘 bulgeK），否则裂缝
                        float off = Bulge * (end ? 1f : bulgeK) * (0.05f + 0.45f * qb + 0.6f * qs) * (end ? 1 : fade);
                        { float ft = 1 - (Y - e) / 0.7f; if (ft > 0) off += Math.Min(FootFlare * flareK, 0.5f * (top0 + w - e)) * ft * ft; }
                        X += nx * off; Z += nz * off;
                    }
                    else if (r == 0 && !lowEnd && !highEnd && !diag)
                    {
                        // 崖脚外扩不超过这里墙高的一半：矮的一段外扩太多会从顶边后面露出来
                        float fl0 = Math.Max(0f, Math.Min(FootFlare * flareK, 0.5f * (top0 + w - e)));
                        X += nx * fl0; Z += nz * fl0;
                    }
                    px[k] = X; py[k] = Y; pz[k] = Z;
                }
            }
            for (int r = 0; r < R; r++)
                for (int c = 0; c < C; c++)
                {
                    int a = r * (C + 1) + c, b2 = a + 1, d = a + C + 1, f = d + 1;
                    bd.Tri(Rock, nx, 0, nz, px[a], py[a], pz[a], px[d], py[d], pz[d], px[f], py[f], pz[f]);
                    bd.Tri(Rock, nx, 0, nz, px[a], py[a], pz[a], px[f], py[f], pz[f], px[b2], py[b2], pz[b2]);
                }
            Backing(bd, nx, nz, lx, lz, hx, hz, e, Math.Max(e, top0), top1);
        }

        /// <summary>一格：顶面（含边缘材质）、每个露出侧面的墙、顶边倒角、角上的倒角面。墙归高的那一格。</summary>
        static void Column(RegionMap map, Builder bd, Fbm fRim, int x, int z, float[] road)
        {
            int h = H(map, x, z);
            // 8 个邻格靠本格那条边 / 那个角的高度，先算好（顶点循环里会反复用）
            var low9 = new int[9];
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) low9[(dz + 1) * 3 + dx + 1] = (dx == 0 && dz == 0) ? h : Low(map, x, z, dx, dz);
            int L(int dx, int dz) => low9[(dz + 1) * 3 + dx + 1];
            float cxw = x + 0.5f, czw = z + 0.5f;
            var exposed = new bool[4];
            for (int s = 0; s < 4; s++) exposed[s] = L(Grid.DX4[s], Grid.DZ4[s]) < h;

            // ---- 挨着斜坡侧面的崖边：露出的高度沿边变化（坡顶处为 0），倒角宽度和岩石边随之收窄，坡面平滑并进台面 ----
            var sideSlope = new bool[4];
            for (int s = 0; s < 4; s++)
            {
                if (!exposed[s]) continue;
                int ax = x + Grid.DX4[s], az = z + Grid.DZ4[s];
                if (ax < 0 || az < 0 || ax >= map.W || az >= map.H) continue;
                int un = map.Slope[map.Index(ax, az)];
                sideSlope[s] = un >= 0 && un != s && un != ((s + 2) & 3);
            }
            float Expo(int s, float px, float pz)
            {
                int ax = x + Grid.DX4[s], az = z + Grid.DZ4[s], ni = map.Index(ax, az), un = map.Slope[ni], hn2 = map.Height[ni];
                float yN = hn2 + ((px - (ax + 0.5f)) * Grid.DX4[un] + (pz - (az + 0.5f)) * Grid.DZ4[un] + 0.5f);
                yN = yN < hn2 ? hn2 : (yN > hn2 + 1 ? hn2 + 1 : yN);
                return h - yN;
            }
            float Ins(int s, float px, float pz)
            {
                if (!exposed[s]) return 0;
                if (!sideSlope[s]) return Bevel;
                float e2 = Expo(s, px, pz) / 0.3f;
                return Bevel * (e2 < 0 ? 0 : (e2 > 1 ? 1 : e2));
            }
            // ---- 顶面：露出的边往里收倒角宽度 ----
            float x0 = x + (exposed[3] ? Bevel : 0), x1 = x + 1 - (exposed[1] ? Bevel : 0);
            float z0 = z + (exposed[2] ? Bevel : 0), z1 = z + 1 - (exposed[0] ? Bevel : 0);
            bool anySide = sideSlope[0] || sideSlope[1] || sideSlope[2] || sideSlope[3];
            void TopV(int i, int j, int KK, out float px, out float pz)
            {
                if (!anySide) { px = x0 + (x1 - x0) * i / KK; pz = z0 + (z1 - z0) * j / KK; return; }
                // 先按格上的位置估一次，再用估出的位置重算（和倒角取宽度的位置一致，边缘对得上）
                float bx = x + (float)i / KK, bz = z + (float)j / KK;
                float iw = Ins(3, bx, bz), ie = Ins(1, bx, bz), isS = Ins(2, bx, bz), iN = Ins(0, bx, bz);
                px = x + iw + (1 - iw - ie) * i / KK; pz = z + isS + (1 - isS - iN) * j / KK;
                iw = Ins(3, x, pz); ie = Ins(1, x + 1, pz); isS = Ins(2, px, z); iN = Ins(0, px, z + 1);
                px = x + iw + (1 - iw - ie) * i / KK; pz = z + isS + (1 - isS - iN) * j / KK;
            }
            bool rim = false;
            for (int s = 0; s < 4; s++) if (exposed[s]) rim = true;
            for (int dz = -1; dz <= 1; dz += 2) for (int dx = -1; dx <= 1; dx += 2) if (L(dx, dz) < h) rim = true;
            bool nearRoad = RoadSurface.Near(road, map.W, map.H, x, z);
            if (rim || nearRoad) // 平地格（不靠崖边、不靠路）在 MergeFlat 里按矩形合并
            {
                const int K = 4;
                var bl = new float[(K + 1) * (K + 1)];
                var rf = new float[(K + 1) * (K + 1)];
                for (int j = 0; j <= K; j++)
                    for (int i = 0; i <= K; i++)
                    {
                        TopV(i, j, K, out float px, out float pz);
                        // 路面离边距离：四邻 / 对角任何高差（更低的崖边、更高的崖脚）都算边。不挨着路的格没有路面，跳过（省掉大量计算）
                        float dr = 9;
                        if (nearRoad)
                        {
                        for (int s = 0; s < 4; s++)
                            if (L(Grid.DX4[s], Grid.DZ4[s]) != h) dr = Math.Min(dr, 0.5f - ((px - cxw) * Grid.DX4[s] + (pz - czw) * Grid.DZ4[s]));
                        for (int dz = -1; dz <= 1; dz += 2)
                            for (int dx = -1; dx <= 1; dx += 2)
                                if (L(dx, dz) != h)
                                {
                                    float kx = cxw + dx * 0.5f - px, kz = czw + dz * 0.5f - pz;
                                    dr = Math.Min(dr, (float)Math.Sqrt(kx * kx + kz * kz));
                                }
                        float drn = dr + (float)fRim.Sample(px * 1.7 + 5.1, pz * 1.7) * 0.12f;
                        rf[j * (K + 1) + i] = RoadEdge(drn);
                        }
                        float d = 9;
                        for (int s = 0; s < 4; s++)
                            if (exposed[s])
                            {
                                float ds = 0.5f - ((px - cxw) * Grid.DX4[s] + (pz - czw) * Grid.DZ4[s]);
                                if (sideSlope[s]) { float ex = Expo(s, px, pz); ex = ex < 0 ? 0 : (ex > 1 ? 1 : ex); ds += (1 - ex) * 0.6f; }
                                d = Math.Min(d, ds);
                            }
                        for (int dz = -1; dz <= 1; dz += 2)
                            for (int dx = -1; dx <= 1; dx += 2)
                                if (L(dx, dz) < h)
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
                        TopV(i, j, K, out float p00x, out float p00z); TopV(i + 1, j, K, out float p10x, out float p10z);
                        TopV(i, j + 1, K, out float p01x, out float p01z); TopV(i + 1, j + 1, K, out float p11x, out float p11z);
                        float b00 = bl[j * (K + 1) + i], b10 = bl[j * (K + 1) + i + 1], b01 = bl[(j + 1) * (K + 1) + i], b11 = bl[(j + 1) * (K + 1) + i + 1];
                        float r00 = rf[j * (K + 1) + i], r10 = rf[j * (K + 1) + i + 1], r01 = rf[(j + 1) * (K + 1) + i], r11 = rf[(j + 1) * (K + 1) + i + 1];
                        bd.TriBR(Dirt, 0, 1, 0, p00x, h, p00z, b00, r00, p01x, h, p01z, b01, r01, p11x, h, p11z, b11, r11);
                        bd.TriBR(Dirt, 0, 1, 0, p00x, h, p00z, b00, r00, p11x, h, p11z, b11, r11, p10x, h, p10z, b10, r10);
                    }
            }

            // ---- 每个露出的侧面 ----
            for (int s = 0; s < 4; s++)
            {
                if (!exposed[s]) continue;
                int nx = Grid.DX4[s], nz = Grid.DZ4[s];
                int tx = -nz, tz = nx;                      // 沿墙方向
                int hn = Low(map, x, z, nx, nz);
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
                        int hp = Low(map, x, z, sg * tx, sg * tz), hd = Low(map, x, z, nx + sg * tx, nz + sg * tz);
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
                        int hp = Low(map, x, z, sg * tx, sg * tz), hd = Low(map, x, z, nx + sg * tx, nz + sg * tz);
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
                                int hq = Low(map, x, z, nx + sg * tx, nz + sg * tz);
                                float yTop = Math.Min(y1, hq - Bevel);
                                float kx = cxw + nx * 0.5f + tx * sg * 0.5f, kz = czw + nz * 0.5f + tz * sg * 0.5f; // 凹角的竖边
                                float qx = kx + nx * Fillet, qz = kz + nz * Fillet;
                                if (yTop > y0) bd.Quad(Rock, nx - tx * sg, 0, nz - tz * sg, PX(ue, 0), y0, PZ(ue, 0), PX(ue, 0), yTop, PZ(ue, 0), qx, yTop, qz, qx, y0, qz);
                                // 对角格比本格低（但高于墙脚）：内倒角只到对角格的倒角底，再补到对角格顶面的一小片，否则凹角处露缝
                                // 对角格不低于本格、这是最上面一段：内倒角只到墙顶（h − Bevel），墙顶到顶面的那一角补一块斜面和一小片竖面
                                if (hq >= h && segTop && yTop > y0)
                                {
                                    bd.Tri(Rock, nx - tx * sg, 1, nz - tz * sg, PX(ue, 0), yTop, PZ(ue, 0), qx, yTop, qz, kx, h, kz);
                                    // 竖面朝向不好判断，两面都画（Unity 默认剔除背面）
                                    bd.Tri(Rock, tx, 0, tz, qx, yTop, qz, qx, h, qz, kx, h, kz);
                                    bd.Tri(Rock, -tx, 0, -tz, qx, yTop, qz, qx, h, qz, kx, h, kz);
                                }
                                if (hq < h && yTop > y0 && yTop >= hq - Bevel - 1e-4f && yTop < y1)
                                {
                                    bd.Tri(Rock, nx - tx * sg, 1, nz - tz * sg, PX(ue, 0), yTop, PZ(ue, 0), qx, yTop, qz, kx, hq, kz);
                                    // 墙端到凹角竖边之间、对角格倒角底 → 对角格顶面的一小条竖面（否则从镜头看是一条细缝）
                                    bd.Tri(Rock, nx, 0, nz, PX(ue, 0), yTop, PZ(ue, 0), PX(ue, 0), Math.Min(y1, hq), PZ(ue, 0), kx, Math.Min(y1, hq), kz);
                                }
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
                        bool perpExposed = Low(map, x, z, sg * tx, sg * tz) < h;
                        tEnd[e] = perpExposed ? Bevel : 0;
                    }
                    float tu0 = -0.5f + tEnd[0], tu1 = 0.5f - tEnd[1];
                    var endIns = new[] { Bevel, Bevel };
                    if (!sideSlope[s])
                        bd.Quad(Rock, nx, 1, nz,
                            PX(u0, 0), y1, PZ(u0, 0), PX(tu0, Bevel), h, PZ(tu0, Bevel),
                            PX(tu1, Bevel), h, PZ(tu1, Bevel), PX(u1, 0), y1, PZ(u1, 0));
                    else
                    {
                        // 倒角宽度沿边变化（挨着斜坡侧面）：分 4 段；倒角收窄处，墙顶到倒角下沿补一条墙面
                        const int KS = 4;
                        float InsAt(float uu) => Ins(s, PX(uu, 0), PZ(uu, 0));
                        endIns[0] = InsAt(tu0); endIns[1] = InsAt(tu1);
                        for (int k = 0; k < KS; k++)
                        {
                            float ta = (float)k / KS, tb = (float)(k + 1) / KS;
                            float ua = u0 + (u1 - u0) * ta, ub2 = u0 + (u1 - u0) * tb, va = tu0 + (tu1 - tu0) * ta, vb = tu0 + (tu1 - tu0) * tb;
                            float ia = InsAt(va), ib = InsAt(vb);
                            bd.Quad(Rock, nx, 1, nz, PX(ua, 0), h - ia, PZ(ua, 0), PX(va, ia), h, PZ(va, ia), PX(vb, ib), h, PZ(vb, ib), PX(ub2, 0), h - ib, PZ(ub2, 0));
                            if (ia < Bevel - 1e-4f || ib < Bevel - 1e-4f)
                                bd.Quad(Rock, nx, 0, nz, PX(ua, 0), y1, PZ(ua, 0), PX(ua, 0), h - ia, PZ(ua, 0), PX(ub2, 0), h - ib, PZ(ub2, 0), PX(ub2, 0), y1, PZ(ub2, 0));
                        }
                    }
                    for (int e = 0; e < 2; e++)
                    {
                        int sg = e == 0 ? -1 : 1;
                        if (endIns[e] < Bevel - 1e-4f) continue; // 倒角在这一端收到 0（坡顶），不补端头
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
                            int hp2 = Low(map, x, z, sg * tx, sg * tz), hpn = Low(map, x, z, sg * tx + nx, sg * tz + nz);
                            bool continues = hp2 == h && hpn < h;
                            if (!continues)
                                bd.Tri(Rock, nx - tx * sg * 0.3f, 1, nz - tz * sg * 0.3f,
                                    PX(ut, Bevel), h, PZ(ut, Bevel), kx, h, kz, PX(ub, 0), y1, PZ(ub, 0));
                            // 凹角且对角格同高：两道倒角之间再补一片（内倒角的顶部），南北向的墙负责
                            int hq = Low(map, x, z, nx + sg * tx, nz + sg * tz);
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
            // 正面（朝南）的崖脚不外扩：外扩会让崖面在画面上显得更长（负责人 2026-11-12）
            float flare = south ? 0f : FootFlare;
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
                        if (ground) { float ft = 1 - (Y - y0) / 0.7f; if (ft > 0) off += flare * ft * ft; } // 崖脚往外扩
                        X += nx * off; Z += nz * off;
                    }
                    else if (r == 0 && ground && !end)
                    {
                        X += nx * flare; Z += nz * flare; // 崖脚往外扩
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
            Backing(bd, nx, nz, ax, az, bx, bz, y0, y1, y1);
        }

        /// <summary>
        /// 墙后的背板：在墙面往格内 3cm 处画一块平的岩石面（梯形：两端高度可以不同）。鼓包都往外，背板永远在后面；
        /// 万一相邻几块网格之间有细缝，从斜一点的角度看过去透出来的是岩石而不是背景。
        /// </summary>
        static void Backing(Builder bd, float nx, float nz, float ax, float az, float bx, float bz, float y0, float yA, float yB)
        {
            const float In = 0.03f;
            float ix = -nx * In, iz = -nz * In;
            if (yA - y0 < 1e-3f && yB - y0 < 1e-3f) return;
            bd.Quad(Rock, nx, 0, nz, ax + ix, y0, az + iz, ax + ix, yA, az + iz, bx + ix, yB, bz + iz, bx + ix, y0, bz + iz);
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
        static void MergeFlat(RegionMap map, Builder bd, int ox, int oz, int size, float[] road)
        {
            var done = new bool[size * size];
            bool Flat(int lx, int lz)
            {
                int x = ox + lx, z = oz + lz;
                if (x >= map.W || z >= map.H) return false;
                int h = H(map, x, z);
                if (map.Slope[map.Index(x, z)] >= 0 || RoadSurface.Near(road, map.W, map.H, x, z)) return false;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) if ((dx != 0 || dz != 0) && Low(map, x, z, dx, dz) < h) return false;
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
            public float[] RoadMask; public int MW, MH;
            public List<RoadSurface.Mouth> Mouths;

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
                => TriBR(mat, hx, hy, hz, ax, ay, az, ba, 1, bx, by, bz, bb, 1, cx, cy, cz, bc, 1);

            /// <summary>带融合权重和路面系数（离边越近越小，乘在路面遮罩上）的三角形。</summary>
            public void TriBR(int mat, float hx, float hy, float hz,
                float ax, float ay, float az, float ba, float ra, float bx, float by, float bz, float bb, float rb, float cx, float cy, float cz, float bc, float rc)
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
                Add(ax, ay, az, nx, ny, nz, ba, ra);
                if (same) { Add(bx, by, bz, nx, ny, nz, bb, rb); Add(cx, cy, cz, nx, ny, nz, bc, rc); }
                else { Add(cx, cy, cz, nx, ny, nz, bc, rc); Add(bx, by, bz, nx, ny, nz, bb, rb); }
                list.Add(v0); list.Add(v0 + 1); list.Add(v0 + 2);
            }

            void Add(float x, float y, float z, float nx, float ny, float nz, float blend, float roadK)
            {
                M.Positions.Add(x - Ox); M.Positions.Add(y); M.Positions.Add(z - Oz);
                M.Normals.Add(nx); M.Normals.Add(ny); M.Normals.Add(nz);
                M.Blend.Add(blend);
                float rw = 0f;
                if (ny > 0.6f && RoadMask != null && roadK > 0f)
                {
                    rw = RoadSurface.Weight(RoadMask, MW, MH, x, z) * roadK;
                    if (rw > 0f) rw = Math.Min(1f, rw * RoadSurface.Funnel(Mouths, x, z)); // 漏斗要查所有坡道口，只在真有路面时算
                }
                M.Road.Add(rw);
            }
        }
    }
}
