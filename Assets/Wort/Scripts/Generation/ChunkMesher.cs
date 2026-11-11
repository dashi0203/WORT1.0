using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>与引擎无关的网格数据。顶点坐标为 chunk 局部坐标（米），三角形按材质分组。</summary>
    public sealed class MeshData
    {
        public readonly List<float> Positions = new List<float>();
        public readonly List<float> Normals = new List<float>();
        /// <summary>每个顶点的贴图融合权重（0 = 泥土，1 = 岩石）。只有 CliffMesher 写；为空表示不用融合。</summary>
        public readonly List<float> Blend = new List<float>();
        public readonly List<int>[] Indices;

        public MeshData()
        {
            Indices = new List<int>[(int)VoxelMaterial.Count];
            for (int i = 0; i < Indices.Length; i++) Indices[i] = new List<int>();
        }

        public int VertexCount => Positions.Count / 3;

        public int TriangleCount
        {
            get { int n = 0; foreach (var l in Indices) n += l.Count / 3; return n; }
        }
    }

    /// <summary>
    /// 体素 → 网格。整格 6 面；斜坡 = 底面 + 背面（高的一侧）+ 两侧三角 + 斜面。
    /// 被相邻体素完整遮住的面不生成。三角形绕序符合 Unity（从正面看顺时针）。
    /// </summary>
    public static class ChunkMesher
    {
        // 6 个方向：0..3 为水平（与 Grid.DX4/DZ4 一致：北 东 南 西），4 = 上，5 = 下
        static readonly int[] FX = { 0, 1, 0, -1, 0, 0 };
        static readonly int[] FY = { 0, 0, 0, 0, 1, -1 };
        static readonly int[] FZ = { 1, 0, -1, 0, 0, 0 };
        const int Up = 4, Down = 5;

        static int Opposite(int f) => f < 4 ? (f + 2) & 3 : (f == Up ? Down : Up);

        public static MeshData Build(RegionVoxels rv, int cx, int cz)
        {
            var mesh = new MeshData();
            var chunk = rv.GetChunk(cx, cz);
            int size = chunk.Size;
            int ox = cx * size, oz = cz * size;
            for (int y = chunk.MinY; y < chunk.MinY + chunk.SizeY; y++)
                for (int lz = 0; lz < size; lz++)
                    for (int lx = 0; lx < size; lx++)
                    {
                        var m = chunk.GetMaterial(lx, y, lz);
                        if (m == VoxelMaterial.Air) continue;
                        var s = chunk.GetShape(lx, y, lz);
                        int x = ox + lx, z = oz + lz;
                        if (m == VoxelMaterial.Water) EmitWater(rv, mesh, x, y, z, ox, oz);
                        else if (s == VoxelShape.Full) EmitFull(rv, mesh, m, x, y, z, ox, oz);
                        else EmitSlope(rv, mesh, m, (int)s - 1, x, y, z, ox, oz);
                    }
            return mesh;
        }

        /// <summary>邻居在方向 towardUs（从邻居指向本体素）上的面是否是完整的方形。</summary>
        static bool Covers(VoxelMaterial m, VoxelShape s, int towardUs)
        {
            if (m == VoxelMaterial.Water) return towardUs == Down; // 水底顶面不画；岸边侧面保留（水面略低于格顶）
            if (!VoxelPalette.IsSolid(m)) return false;
            if (s == VoxelShape.Full) return true;
            int up = (int)s - 1;
            return towardUs == Down || towardUs == up;
        }

        static void Neighbor(RegionVoxels rv, int x, int y, int z, int f, out VoxelMaterial m, out VoxelShape s)
        {
            rv.Get(x + FX[f], y + FY[f], z + FZ[f], out m, out s);
        }

        static void EmitFull(RegionVoxels rv, MeshData mesh, VoxelMaterial m, int x, int y, int z, int ox, int oz)
        {
            for (int f = 0; f < 6; f++)
            {
                Neighbor(rv, x, y, z, f, out var nm, out var ns);
                if (Covers(nm, ns, Opposite(f))) continue;
                var mat = f == Up ? m : VoxelPalette.Side(m);
                QuadFace(mesh, mat, x - ox, y, z - oz, f, 0, 1);
            }
        }

        internal static void EmitWater(RegionVoxels rv, MeshData mesh, int x, int y, int z, int ox, int oz)
        {
            for (int f = 0; f < 6; f++)
            {
                if (f == Down) continue;
                Neighbor(rv, x, y, z, f, out var nm, out var ns);
                if (nm == VoxelMaterial.Water || VoxelPalette.IsSolid(nm) && (f == Up || Covers(nm, ns, Opposite(f)))) continue;
                // 水面略低于格顶，避免与岸边顶面共面闪烁
                rv.Get(x, y + 1, z, out var above, out _);
                float top = above == VoxelMaterial.Water ? 1f : 0.9f;
                QuadFace(mesh, VoxelMaterial.Water, x - ox, y, z - oz, f, 0, top);
            }
        }

        internal static void EmitSlope(RegionVoxels rv, MeshData mesh, VoxelMaterial m, int up, int x, int y, int z, int ox, int oz)
        {
            var side = VoxelPalette.Side(m);
            int lx = x - ox, lz = z - oz;

            // 底面
            Neighbor(rv, x, y, z, Down, out var nm, out var ns);
            if (!Covers(nm, ns, Up)) QuadFace(mesh, side, lx, y, lz, Down, 0, 1);

            // 背面（高的一侧，完整方形）
            Neighbor(rv, x, y, z, up, out nm, out ns);
            if (!Covers(nm, ns, Opposite(up))) QuadFace(mesh, side, lx, y, lz, up, 0, 1);

            // 两侧三角
            for (int k = 1; k <= 3; k += 2)
            {
                int p = (up + k) & 3;
                Neighbor(rv, x, y, z, p, out nm, out ns);
                if (Covers(nm, ns, Opposite(p))) continue;
                if (VoxelPalette.IsSolid(nm) && (int)ns - 1 == up) continue; // 同向斜坡并排
                // 三角：低边底 → 高边底 → 高边顶，位于 p 一侧的竖直面上
                GetHorizontalEdge(p, out float ax, out float az, out float bx, out float bz);
                // 该侧面的两个端点里，哪个靠近“高”的一侧
                float hx = 0.5f + 0.5f * FX[up], hz = 0.5f + 0.5f * FZ[up];
                float d0 = Math.Abs(ax - hx) + Math.Abs(az - hz);
                float d1 = Math.Abs(bx - hx) + Math.Abs(bz - hz);
                float lowX = d0 > d1 ? ax : bx, lowZ = d0 > d1 ? az : bz;
                float highX = d0 > d1 ? bx : ax, highZ = d0 > d1 ? bz : az;
                Triangle(mesh, side, FX[p], 0, FZ[p],
                    lx + lowX, y, lz + lowZ,
                    lx + highX, y, lz + highZ,
                    lx + highX, y + 1, lz + highZ);
            }

            // 斜面
            Neighbor(rv, x, y, z, Up, out nm, out ns);
            if (VoxelPalette.IsSolid(nm) && ns == VoxelShape.Full) return;
            {
                // 低边在 -up 一侧（高度 y），高边在 up 一侧（高度 y+1）
                GetHorizontalEdge((up + 2) & 3, out float ax, out float az, out float bx, out float bz);
                GetHorizontalEdge(up, out float cx, out float cz, out float dx, out float dz);
                float nx = -FX[up], ny = 1, nz = -FZ[up];
                float inv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                Quad(mesh, m, nx * inv, ny * inv, nz * inv,
                    lx + ax, y, lz + az,
                    lx + bx, y, lz + bz,
                    lx + dx, y + 1, lz + dz,
                    lx + cx, y + 1, lz + cz);
            }
        }

        /// <summary>单位格在水平方向 f 一侧的那条边的两个端点（格内坐标 0..1）。</summary>
        static void GetHorizontalEdge(int f, out float ax, out float az, out float bx, out float bz)
        {
            switch (f)
            {
                case 0: ax = 0; az = 1; bx = 1; bz = 1; break; // 北
                case 1: ax = 1; az = 0; bx = 1; bz = 1; break; // 东
                case 2: ax = 0; az = 0; bx = 1; bz = 0; break; // 南
                default: ax = 0; az = 0; bx = 0; bz = 1; break; // 西
            }
        }

        /// <summary>整格在方向 f 上的方形面，竖直范围 [y + y0, y + y1]（顶面在 y + y1）。</summary>
        static void QuadFace(MeshData mesh, VoxelMaterial mat, float x, float y, float z, int f, float y0, float y1)
        {
            float nx = FX[f], ny = FY[f], nz = FZ[f];
            if (f == Up)
            {
                float t = y + y1;
                Quad(mesh, mat, 0, 1, 0, x, t, z, x + 1, t, z, x + 1, t, z + 1, x, t, z + 1);
            }
            else if (f == Down)
            {
                Quad(mesh, mat, 0, -1, 0, x, y, z, x + 1, y, z, x + 1, y, z + 1, x, y, z + 1);
            }
            else
            {
                GetHorizontalEdge(f, out float ax, out float az, out float bx, out float bz);
                Quad(mesh, mat, nx, ny, nz,
                    x + ax, y + y0, z + az,
                    x + bx, y + y0, z + bz,
                    x + bx, y + y1, z + bz,
                    x + ax, y + y1, z + az);
            }
        }

        /// <summary>四边形（顶点按环绕顺序给出，方向自动按法线修正）。</summary>
        static void Quad(MeshData mesh, VoxelMaterial mat, float nx, float ny, float nz,
            float x0, float y0, float z0, float x1, float y1, float z1,
            float x2, float y2, float z2, float x3, float y3, float z3)
        {
            int b = mesh.VertexCount;
            Add(mesh, x0, y0, z0, nx, ny, nz);
            Add(mesh, x1, y1, z1, nx, ny, nz);
            Add(mesh, x2, y2, z2, nx, ny, nz);
            Add(mesh, x3, y3, z3, nx, ny, nz);
            bool flip = Facing(x0, y0, z0, x1, y1, z1, x2, y2, z2, nx, ny, nz) < 0;
            var list = mesh.Indices[(int)mat];
            if (!flip) { list.Add(b); list.Add(b + 1); list.Add(b + 2); list.Add(b); list.Add(b + 2); list.Add(b + 3); }
            else { list.Add(b); list.Add(b + 2); list.Add(b + 1); list.Add(b); list.Add(b + 3); list.Add(b + 2); }
        }

        static void Triangle(MeshData mesh, VoxelMaterial mat, float nx, float ny, float nz,
            float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2)
        {
            int b = mesh.VertexCount;
            Add(mesh, x0, y0, z0, nx, ny, nz);
            Add(mesh, x1, y1, z1, nx, ny, nz);
            Add(mesh, x2, y2, z2, nx, ny, nz);
            bool flip = Facing(x0, y0, z0, x1, y1, z1, x2, y2, z2, nx, ny, nz) < 0;
            var list = mesh.Indices[(int)mat];
            if (!flip) { list.Add(b); list.Add(b + 1); list.Add(b + 2); }
            else { list.Add(b); list.Add(b + 2); list.Add(b + 1); }
        }

        /// <summary>
        /// Unity 的正面是“从正面看顺时针”，等价于 Cross(b - a, c - a) 与法线同向。
        /// </summary>
        static float Facing(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz,
            float nx, float ny, float nz)
        {
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float crx = uy * vz - uz * vy;
            float cry = uz * vx - ux * vz;
            float crz = ux * vy - uy * vx;
            return crx * nx + cry * ny + crz * nz;
        }

        static void Add(MeshData mesh, float x, float y, float z, float nx, float ny, float nz)
        {
            mesh.Positions.Add(x); mesh.Positions.Add(y); mesh.Positions.Add(z);
            mesh.Normals.Add(nx); mesh.Normals.Add(ny); mesh.Normals.Add(nz);
        }
    }
}
