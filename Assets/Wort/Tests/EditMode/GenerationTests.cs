using NUnit.Framework;
using Wort.Generation;

namespace Wort.Tests
{
    /// <summary>
    /// 生成核心的确定性测试。期望的 checksum 来自云端预览工具（Tools/TerrainPreview，.NET 8）：
    /// Unity（Mono）里算出相同的值，说明云端预览图与 Unity 里的地形逐格一致。
    /// </summary>
    public sealed class GenerationTests
    {
        private const uint PreviewChecksumSeed20261109 = 0xEE3CECBBu;
        private const uint PreviewChecksumSeed7 = 0x3DD96B8Au;

        private static RegionMap Generate(uint seed) => RegionGenerator.Generate(new RegionSettings { Seed = seed });

        [Test]
        public void SameSeedGivesSameResult()
        {
            Assert.AreEqual(Generate(7).Checksum(), Generate(7).Checksum());
        }

        [Test]
        public void MatchesCloudPreview()
        {
            Assert.AreEqual(PreviewChecksumSeed20261109, Generate(20261109).Checksum(),
                "种子 20261109 与云端预览不一致：请把 Console 里的 checksum 发回云端会话。");
            Assert.AreEqual(PreviewChecksumSeed7, Generate(7).Checksum(),
                "种子 7 与云端预览不一致：请把 Console 里的 checksum 发回云端会话。");
        }

        [Test]
        public void NoConsecutiveNaturalSlopes()
        {
            Assert.AreEqual(0, Generate(20261109).Stats["naturalSlopeChains"]);
        }
    }
}
