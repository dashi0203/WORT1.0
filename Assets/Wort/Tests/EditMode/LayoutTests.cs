using System.IO;
using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>
    /// layout 管线（Docs/WorldGen.md）的测试：8×8 技术验证 layout 与云端预览逐格一致，且所有检查通过。
    /// 期望的 checksum 来自 Tools/TerrainPreview 的 --layout 模式。
    /// </summary>
    public sealed class LayoutTests
    {
        private const uint PreviewChecksumTest8x8 = 0x311C95C3u;
        private const string Dir = "Assets/Wort/Data/Layout";

        private static WorldLayout Load() =>
            WorldLayout.Parse(File.ReadAllText(Path.Combine(Dir, "Test8x8.json")),
                File.ReadAllText(Path.Combine(Dir, "SiteTemplates.json")));

        [Test]
        public void MatchesCloudPreview()
        {
            var res = LayoutGenerator.Generate(Load());
            Assert.AreEqual(PreviewChecksumTest8x8, res.Map.Checksum(),
                "Test8x8 与云端预览不一致：请把实际 checksum 发回云端会话。");
        }

        [Test]
        public void AllChecksPass()
        {
            var rep = LayoutGenerator.Generate(Load()).Report;
            Assert.IsTrue(rep.Ok, string.Join("\n", rep.Errors));
            Assert.AreEqual(0, rep.Traps);
            Assert.AreEqual(0, rep.Leaks);
        }

        [Test]
        public void OnlyGatelessRidgeIsUnreachable()
        {
            var res = LayoutGenerator.Generate(Load());
            var g = new SurfaceGraph(res);
            var home = res.Layout.Site(res.Layout.Home);
            var fwd = g.Forward(home.Z * res.Map.W + home.X);
            for (int node = 0; node < 2 * g.N; node++)
                if (g.Valid(node) && !fwd[node])
                    Assert.AreEqual("ridge", res.RegionAt(g.Cell(node)).Id, "走不到的节点不在 ridge 上");
        }

        [Test]
        public void BasinWithoutExitIsATrap()
        {
            // 反例：去掉盆地的台阶出口，检查必须报“进得去、回不来”
            var layout = Load();
            layout.Gates.RemoveAll(gate => gate.Id == "basin.exit");
            var rep = LayoutGenerator.Generate(layout).Report;
            Assert.Greater(rep.Traps, 0);
            Assert.IsFalse(rep.Ok);
        }
    }
}
