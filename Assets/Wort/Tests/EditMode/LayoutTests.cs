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
        private const uint PreviewChecksumTest8x8 = 0xFC883C97u;
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
        public void BasinWithoutRampIsATrap()
        {
            // 反例：把通进盆地的坡道抹平成盆地底面，检查必须报“进得去、回不来”
            var res = LayoutGenerator.Generate(Load());
            var map = res.Map;
            int floor = int.MaxValue;
            for (int i = 0; i < map.Height.Length; i++)
                if (res.RegionAt(i).Id == "basin" && !map.Has(i, CellFlags.Ramp)) floor = System.Math.Min(floor, map.Height[i]);
            int removed = 0;
            for (int i = 0; i < map.Height.Length; i++)
                if (res.RegionAt(i).Id == "basin" && map.Has(i, CellFlags.Ramp)) { map.Height[i] = floor; map.Slope[i] = -1; removed++; }
            Assert.Greater(removed, 0, "盆地里没有自动坡道");
            var rep = LayoutChecks.Run(res);
            Assert.Greater(rep.Traps, 0);
            Assert.IsFalse(rep.Ok);
        }
    }
}
