using System.Collections.Generic;
using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>空白测试地、按高度选瓦片（TilePlacer）、Minecraft 式取面 / 放块 / 挖块（VoxelPick）。</summary>
    public sealed class VoxelEditTests
    {
        private static RegionMap Ground()
        {
            var m = TestGround.Create(1, 4, null);
            // 十字台：2 层高（4 → 6）
            for (int z = 6; z <= 7; z++) for (int x = 2; x <= 9; x++) m.Height[m.Index(x, z)] = 6;
            for (int z = 3; z <= 10; z++) for (int x = 5; x <= 6; x++) m.Height[m.Index(x, z)] = 6;
            return m;
        }

        [Test]
        public void TopMaskEdgesAndInnerCorners()
        {
            var m = Ground();
            TilePlacer.TopMask(m, 5, 3, out int e, out int c);
            Assert.AreEqual(4 | 8, e, "十字南臂（宽 2 格）西南那一格：西、南露边");
            TilePlacer.TopMask(m, 5, 7, out e, out c);
            Assert.AreEqual(0, e, "十字中间（臂宽 2 格）：四邻都同高");
            Assert.AreEqual(2, c, "它的西北斜邻低、两侧不露：西北凹角");
            TilePlacer.TopMask(m, 5, 5, out e, out c);
            Assert.AreEqual(4, e, "南臂靠中心的格：只露西边");
            TilePlacer.TopMask(m, 5, 6, out e, out c);
            Assert.AreEqual(4, c, "南臂与西臂之间的凹角：西南斜邻低、两侧不露");
        }

        [Test]
        public void WallsStackCrownOnTopAndRotateBySide()
        {
            var m = Ground();
            var list = new List<TilePlacement>();
            TilePlacer.Cell(m, null, 5, 3, list);
            // 1 块顶面 + 南、西两面各 2 层（臂宽 2 格，东边那格同高）
            Assert.AreEqual(1 + 2 * 2, list.Count);
            var south = list.FindAll(p => p.Rot == 0 && p.Name.StartsWith("Front_"));
            Assert.AreEqual(2, south.Count);
            Assert.IsTrue(south.Exists(p => p.Y == 5 && p.Name.Contains("Crown") && !p.Foot));
            Assert.IsTrue(south.Exists(p => p.Y == 4 && p.Name.Contains("Repeat") && p.Foot));
            Assert.IsTrue(list.Exists(p => p.Rot == 1 && p.X == 5 && p.Z == 4 && p.Name.StartsWith("Side_")), "西面：原点在西北角，转 90°");
            list.Clear();
            TilePlacer.Cell(m, null, 6, 3, list);
            Assert.IsTrue(list.Exists(p => p.Rot == 3 && p.X == 7 && p.Z == 3 && p.Name.StartsWith("Side_")), "东面：原点在东南角，转 270°");
        }

        [Test]
        public void RaycastHitsTopAndSideFaces()
        {
            var m = Ground();
            Assert.IsTrue(VoxelPick.Raycast(m, 5.5f, 20, 6.5f, 0, -1, 0, 500, out var h));
            Assert.AreEqual((5, 6, 5, 4), (h.X, h.Z, h.Y, h.Face));
            Assert.IsTrue(VoxelPick.Raycast(m, 5.5f, 5.5f, 0.5f, 0, 0, 1, 500, out h));
            Assert.AreEqual((5, 3, 5, 2), (h.X, h.Z, h.Y, h.Face), "从南边水平打南臂：打到朝南的面，第 5 层");
            Assert.IsTrue(VoxelPick.Raycast(m, 12.5f, 6.6f, 6.5f, -1, -0.4f, 0, 500, out h));
            Assert.AreEqual((9, 6, 5, 1), (h.X, h.Z, h.Y, h.Face), "斜着往下打东臂的东面");
        }

        [Test]
        public void PlaceAndRemoveFollowMinecraftRules()
        {
            var m = Ground();
            VoxelPick.Raycast(m, 5.5f, 20, 6.5f, 0, -1, 0, 500, out var top);
            Assert.IsTrue(VoxelPick.PlaceTarget(m, top, out int x, out int z, out int h));
            Assert.AreEqual((5, 6, 7), (x, z, h), "点顶面放：这一格 +1");
            Assert.IsTrue(VoxelPick.RemoveTarget(m, top, 0, out x, out z, out h));
            Assert.AreEqual((5, 6, 5), (x, z, h), "点顶面挖：这一格 −1");
            VoxelPick.Raycast(m, 5.5f, 5.5f, 0.5f, 0, 0, 1, 500, out var side);
            Assert.IsTrue(VoxelPick.PlaceTarget(m, side, out x, out z, out h));
            Assert.AreEqual((5, 2, 6), (x, z, h), "点侧面放：面朝向那格填到这一层");
            Assert.IsTrue(VoxelPick.RemoveTarget(m, side, 0, out x, out z, out h));
            Assert.AreEqual((5, 3, 5), (x, z, h), "点侧面挖：挖到被打到的那层");
        }
    }
}
