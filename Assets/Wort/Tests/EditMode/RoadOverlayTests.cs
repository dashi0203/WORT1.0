using System.Collections.Generic;
using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>道路覆盖层（手刷道路）：笔刷覆盖的格、叠加 / 擦除、射线找地面。</summary>
    public sealed class RoadOverlayTests
    {
        private static RegionMap FlatMap()
        {
            var m = new RegionMap(new RegionSettings { ChunksX = 1, ChunksZ = 1 });
            for (int i = 0; i < m.Height.Length; i++) m.Height[i] = 2;
            return m;
        }

        [Test]
        public void FootprintWidth2Is5CellsAndWidth3Is9()
        {
            Assert.AreEqual(5, RoadOverlay.Footprint(10.5f, 10.5f, 2).Count);
            Assert.AreEqual(9, RoadOverlay.Footprint(10.5f, 10.5f, 3).Count);
        }

        [Test]
        public void ApplySetsAndErasesRoad()
        {
            var m = FlatMap();
            int i = m.Index(10, 10);
            var changed = RoadOverlay.Apply(m, new List<int> { 10 }, new List<int> { 10 }, new List<byte> { RegionMap.RoadMain });
            Assert.AreEqual(1, changed.Count);
            Assert.IsTrue(m.Has(i, CellFlags.Road));
            Assert.AreEqual(RegionMap.RoadMain, m.RoadKind[i]);
            // 同样的修改再来一次：没有变化
            Assert.AreEqual(0, RoadOverlay.Apply(m, new List<int> { 10 }, new List<int> { 10 }, new List<byte> { RegionMap.RoadMain }).Count);
            RoadOverlay.Apply(m, new List<int> { 10 }, new List<int> { 10 }, new List<byte> { RegionMap.RoadNone });
            Assert.IsFalse(m.Has(i, CellFlags.Road));
            Assert.AreEqual(0, m.RoadKind[i]);
        }

        [Test]
        public void PickHitsGroundUnderRay()
        {
            var m = FlatMap();
            // 从 (20.3, 30, 15.7) 竖直往下：落在 (20, 15) 这一格，地面高 2
            Assert.IsTrue(RoadOverlay.Pick(m, 20.3f, 30f, 15.7f, 0f, -1f, 0f, 100f, out float x, out float z));
            Assert.AreEqual(20, (int)System.Math.Floor(x));
            Assert.AreEqual(15, (int)System.Math.Floor(z));
        }
    }
}
