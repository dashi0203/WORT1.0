using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>程序化崖壁网格（ProcCliffMesher）：只生成看得见的面、只鼓顶层 / 前排的侧面、只投影的面齐全。</summary>
    public class ProcCliffTests
    {
        /// <summary>平地 1 层上一个 4×4×4 方块（x, z ∈ [10, 14)，顶高 5）。</summary>
        static ProcCliffMesh Block()
        {
            int n = 64; var h = new byte[n * n];
            for (int i = 0; i < h.Length; i++) h[i] = 1;
            for (int z = 10; z < 14; z++)
                for (int x = 10; x < 14; x++) h[z * n + x] = 5;
            return ProcCliffMesher.Build(TestGround.Create(1, 1, h, 1), null, 0, 0);
        }

        /// <summary>某部分里、最高点高于地面（y > 1）的三角形数，也就是方块上的。</summary>
        static int OnBlock(ProcCliffMesh m, ProcCliffMesh.Part part)
        {
            if (!m.Indices.TryGetValue((int)part, out var l)) return 0;
            int c = 0;
            for (int t = 0; t < l.Count; t += 3)
            {
                float y = System.Math.Max(m.Positions[l[t] * 3 + 1], System.Math.Max(m.Positions[l[t + 1] * 3 + 1], m.Positions[l[t + 2] * 3 + 1]));
                if (y > 1.01f) c++;
            }
            return c;
        }

        [Test]
        public void BlockFaceCounts()
        {
            var m = Block();
            // 方块上：顶面、正面、侧面、只投影的面都有；北边那排顶面进裁剪组
            Assert.Greater(OnBlock(m, ProcCliffMesh.Part.Top), 0);
            Assert.AreEqual(0, OnBlock(m, ProcCliffMesh.Part.TopClip));   // 北边不再裁剪，改成往外伸
            Assert.Greater(OnBlock(m, ProcCliffMesh.Part.Wall), 0);
            // 只投影：北、东、西各 4 块平面
            Assert.AreEqual(24, OnBlock(m, ProcCliffMesh.Part.Shadow));
        }

        /// <summary>看得见的三角形之间没有 T 形接缝：任何顶点都不落在另一条边的中间（否则会裂出细缝、闪烁）。只查方块附近。</summary>
        [Test]
        public void NoTJunctions()
        {
            var m = Block();
            var verts = new System.Collections.Generic.List<(float x, float y, float z)>();
            var edges = new System.Collections.Generic.List<(int a, int b)>();
            bool Near(int v) { float x = m.Positions[v * 3], z = m.Positions[v * 3 + 2]; return x > 7 && x < 17 && z > 7 && z < 17; }
            foreach (var kv in m.Indices)
            {
                if (kv.Key % ProcCliffMesh.PartCount == (int)ProcCliffMesh.Part.Shadow) continue;
                var l = kv.Value;
                for (int t = 0; t < l.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = l[t + k], b = l[t + (k + 1) % 3];
                        if (Near(a) || Near(b)) edges.Add((a, b));
                    }
            }
            for (int v = 0; v < m.VertexCount; v++)
                if (Near(v)) verts.Add((m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]));
            int bad = 0;
            foreach (var (a, b) in edges)
            {
                float ax = m.Positions[a * 3], ay = m.Positions[a * 3 + 1], az = m.Positions[a * 3 + 2];
                float bx = m.Positions[b * 3], by = m.Positions[b * 3 + 1], bz = m.Positions[b * 3 + 2];
                float dx = bx - ax, dy = by - ay, dz = bz - az, len2 = dx * dx + dy * dy + dz * dz;
                if (len2 < 1e-8f) continue;
                foreach (var (px, py, pz) in verts)
                {
                    float t = ((px - ax) * dx + (py - ay) * dy + (pz - az) * dz) / len2;
                    if (t < 1e-3f || t > 1 - 1e-3f) continue;
                    float qx = ax + dx * t - px, qy = ay + dy * t - py, qz = az + dz * t - pz;
                    if (qx * qx + qy * qy + qz * qz < 1e-8f) bad++;
                }
            }
            Assert.AreEqual(0, bad);
        }

        [Test]
        public void FlatGroundMerges()
        {
            // 平地 1 层（64×64）除了地图边一圈都合并：整块 chunk 的 Top 三角形应远少于 2 × 4096
            var m = Block();
            int top = m.Indices[(int)ProcCliffMesh.Part.Top].Count / 3;
            Assert.Less(top, 1500f, "平地应合并成大矩形");
        }

        [Test]
        public void NormalsFaceOutward()
        {
            var m = Block();
            for (int v = 0; v < m.VertexCount; v++)
            {
                float k = m.Data1[v * 4], nx = m.Normals[v * 3], nz = m.Normals[v * 3 + 2];
                Assert.IsFalse(float.IsNaN(nx) || float.IsNaN(nz));
                if (k == 2) Assert.Less(nz, 0f, "正面朝南");
                if (k == 4) Assert.Greater(nx * m.Data1[v * 4 + 1], 0f, "侧面朝外");
            }
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void OffsetFrontAndSideMeetOnVoxelEdge(int side)
        {
            const int n = 64, x = 10, z = 10, floor = 1, top = 3;
            var heights = new byte[n * n];
            for (int i = 0; i < heights.Length; i++) heights[i] = floor;
            heights[z * n + x] = top;
            heights[(z + 1) * n + x] = top;
            heights[(z + 1) * n + x + side] = top;

            var mesh = ProcCliffMesher.Build(TestGround.Create(1, 1, heights, floor), null, 0, 0);
            int edge = side > 0 ? x + 1 : x;
            int frontVertices = 0, sideVertices = 0;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                int pos = v * 3, data = v * 4;
                if (mesh.Positions[pos + 2] != z + 1 || mesh.Positions[pos + 1] >= top) continue;
                if (mesh.Data1[data] == 4 && mesh.Data2[data] == edge && mesh.Data1[data + 1] == side)
                {
                    Assert.AreEqual(edge, mesh.Positions[pos], 1e-5f, "侧面回到内角的体素格线");
                    sideVertices++;
                }
                if (mesh.Data1[data] == 2 && mesh.Positions[pos + 1] == floor)
                {
                    int end = data + (side > 0 ? 1 : 2);
                    if (mesh.Data2[end] != edge || System.Math.Abs(mesh.Positions[pos] - edge) >= 0.5f) continue;
                    Assert.AreEqual(edge, mesh.Positions[pos], 1e-5f, "后退一格的正面底边接上同一格角");
                    frontVertices++;
                }
            }

            Assert.Greater(frontVertices, 0);
            Assert.Greater(sideVertices, 0);
        }

        [Test]
        public void TopIsFlatAndBulgeZeroAtTop()
        {
            var m = Block();
            for (int v = 0; v < m.VertexCount; v++)
            {
                float k = m.Data1[v * 4], y = m.Positions[v * 3 + 1], top = m.Data1[v * 4 + 2];
                if (k == 1) Assert.AreEqual(top, y, 1e-5f, "顶面不变形");
            }
            // 鼓起在崖顶处也有一截（顶面窄条接过去），往下更大
            Assert.Greater(ProcCliffMesher.Bulge(1, 5, 11.3, 5), 0.0);
            Assert.Greater(ProcCliffMesher.Bulge(1, 4.5, 11.3, 5), ProcCliffMesher.Bulge(1, 5, 11.3, 5));
        }
    }
}
