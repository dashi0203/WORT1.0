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
            // 顶面：靠露边的 12 格一格一块（北边 4 格进裁剪组），中间 2×2 合并成一块 → Top = 8 格 × 2 + 2
            //       另加东西两侧顶边往外的窄条：每侧 4 格 × 2 段 × 2（北边一格进裁剪组）→ Top 再 + 24、TopClip 再 + 8
            Assert.AreEqual(18 + 24, OnBlock(m, ProcCliffMesh.Part.Top));
            Assert.AreEqual(8 + 8, OnBlock(m, ProcCliffMesh.Part.TopClip));
            // 正面 4 格连成一段：16 行 × 2 = 32；东西两侧各：前排整高 16 行 × 2 段 × 2 = 64，后三格只有顶层 4 行 × 2 段 × 2 × 3 = 48 → 两侧 224
            Assert.AreEqual(32 + 224, OnBlock(m, ProcCliffMesh.Part.Wall));
            // 只投影：北、东、西各 4 块平面
            Assert.AreEqual(24, OnBlock(m, ProcCliffMesh.Part.Shadow));
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
