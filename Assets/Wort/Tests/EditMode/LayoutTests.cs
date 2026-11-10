using System.IO;
using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>
    /// layout 管线（Docs/WorldGen.md）的测试：8×8 测试 layout 与云端预览逐格一致，且所有检查通过。
    /// 期望的 checksum 来自 Tools/TerrainPreview 的 --layout 模式。
    /// </summary>
    public sealed class LayoutTests
    {
        private const uint PreviewChecksumTest8x8 = 0x4D5BF7DCu;
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
        }

        [Test]
        public void BasinFloorIsReachableBothWays()
        {
            // 盆地的出口坡道由连通性修复自动放：盆地底面要能进能出
            var res = LayoutGenerator.Generate(Load());
            var g = new SurfaceGraph(res);
            var home = res.Layout.Site(res.Layout.Home);
            int start = home.Z * res.Map.W + home.X;
            var fwd = g.Forward(start);
            var back = g.Backward(start);
            int basinCells = 0;
            for (int i = 0; i < g.N; i++)
            {
                if (res.RegionAt(i).Id != "basin" || !g.Valid(i)) continue;
                basinCells++;
                Assert.IsTrue(fwd[i] && back[i], $"盆地格 ({i % res.Map.W},{i / res.Map.W}) 不能往返");
            }
            Assert.Greater(basinCells, 100);
        }

        [Test]
        public void PitWithoutExitIsATrap()
        {
            // 反例：在住所东边的开阔地挖一个 10×10、深 3 层的坑，检查必须报“进得去、回不来”
            var res = LayoutGenerator.Generate(Load());
            var map = res.Map;
            var home = res.Layout.Site(res.Layout.Home);
            int x0 = home.X + 50, z0 = home.Z - 5;
            int floor = map.Height[map.Index(x0, z0)] - 3;
            for (int z = z0; z < z0 + 10; z++)
                for (int x = x0; x < x0 + 10; x++)
                {
                    int i = map.Index(x, z);
                    map.Height[i] = floor; map.Slope[i] = -1; map.Water[i] = RegionMap.NoWater;
                }
            var rep = LayoutChecks.Run(res);
            Assert.Greater(rep.Traps, 0);
            Assert.IsFalse(rep.Ok);
        }

        [Test]
        public void NoSlopeVoxels()
        {
            // 斜坡由策划手动放：layout 管线里不生成任何斜坡体素，坡道是台阶
            var map = LayoutGenerator.Generate(Load()).Map;
            for (int i = 0; i < map.Slope.Length; i++) Assert.AreEqual(-1, map.Slope[i]);
        }
    }
}
