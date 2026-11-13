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
            // 顶面 16 格各 2 个三角形，北边一排进裁剪组
            Assert.AreEqual(24, OnBlock(m, ProcCliffMesh.Part.Top));
            Assert.AreEqual(8, OnBlock(m, ProcCliffMesh.Part.TopClip));
            // 正面 4 格 × 16 行 × 2 = 128；东西两侧各：前排整高 16 行 × 4 段 × 2 = 128，后三格只有顶层 4 行 × 4 段 × 2 × 3 = 96 → 两侧 448
            Assert.AreEqual(128 + 448, OnBlock(m, ProcCliffMesh.Part.Wall));
            // 只投影：北、东、西各 4 块平面
            Assert.AreEqual(24, OnBlock(m, ProcCliffMesh.Part.Shadow));
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

        [Test]
        public void TopIsFlatAndBulgeZeroAtTop()
        {
            var m = Block();
            for (int v = 0; v < m.VertexCount; v++)
            {
                float k = m.Data1[v * 4], y = m.Positions[v * 3 + 1], top = m.Data1[v * 4 + 2];
                if (k == 1) Assert.AreEqual(top, y, 1e-5f, "顶面不变形");
            }
            // 鼓起在崖顶处为 0、往下大于 0
            Assert.AreEqual(0.0, ProcCliffMesher.Bulge(1, 5, 11.3, 5), 1e-9);
            Assert.Greater(ProcCliffMesher.Bulge(1, 4.5, 11.3, 5), 0.0);
        }
    }
}
