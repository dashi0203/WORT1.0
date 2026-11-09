namespace Wort.Generation
{
    /// <summary>GDD 6.1 的 Biome 清单。</summary>
    public enum BiomeId : byte
    {
        Human = 0,       // 人类区
        MixedForest = 1, // 混交林
        Conifer = 2,     // 寒带针叶林
        Swamp = 3,       // 沼泽
        Mountain = 4,    // 山地
        Moor = 5,        // 荒原
        Coast = 6,       // 海岸
    }

    /// <summary>
    /// 各 Biome 的起伏参数（GDD 6.3【暂定，第一版起点】的数值化）。
    /// 生成时按 Biome 权重混合这些参数，而不是混合高度。
    /// </summary>
    public sealed class BiomeParams
    {
        public BiomeId Id;
        /// <summary>宏观高度（层）。</summary>
        public double MacroBase;
        /// <summary>微起伏幅度（层）：0–1 层 ≈ 0.6，0–2 层 ≈ 1.2。</summary>
        public double MicroAmp;
        /// <summary>微起伏是否“大而缓 / 长缓坡”（用更长波长）。</summary>
        public bool MicroBroad;
        /// <summary>台地覆盖率。</summary>
        public double TerraceCoverage;
        /// <summary>台地叠加后的高度上限（层）。</summary>
        public double MaxTerraceHeight;
        /// <summary>叠层级数：1 = 不叠，1.35 = 偶尔，2 = 最多 2 级，4 = 2–4 级。</summary>
        public double StackLevels;
        /// <summary>每块台地的坡道数。</summary>
        public double RampsPerTerrace;
        /// <summary>是否生成高台（4–5 层，小，地标）。</summary>
        public bool AllowMesa;
        /// <summary>是否生成孤峰（6 层以上，只有山地）。</summary>
        public bool AllowSpire;
        /// <summary>池塘 / 浅洼覆盖率。</summary>
        public double PondCoverage;
    }

    public static class BiomeTable
    {
        public const int Count = 7;

        public static readonly BiomeParams[] All =
        {
            new BiomeParams { Id = BiomeId.Human,       MacroBase = 2, MicroAmp = 0.6, MicroBroad = true,  TerraceCoverage = 0.125, MaxTerraceHeight = 2,  StackLevels = 1,    RampsPerTerrace = 2.5, AllowMesa = false, AllowSpire = false, PondCoverage = 0.0 },
            new BiomeParams { Id = BiomeId.MixedForest, MacroBase = 4, MicroAmp = 0.6, MicroBroad = false, TerraceCoverage = 0.175, MaxTerraceHeight = 4,  StackLevels = 1.35, RampsPerTerrace = 1.5, AllowMesa = true,  AllowSpire = false, PondCoverage = 0.025 },
            new BiomeParams { Id = BiomeId.Conifer,     MacroBase = 7, MicroAmp = 1.2, MicroBroad = false, TerraceCoverage = 0.225, MaxTerraceHeight = 5,  StackLevels = 2,    RampsPerTerrace = 1,   AllowMesa = true,  AllowSpire = false, PondCoverage = 0.015 },
            new BiomeParams { Id = BiomeId.Swamp,       MacroBase = 0, MicroAmp = 0.5, MicroBroad = false, TerraceCoverage = 0.04,  MaxTerraceHeight = 3,  StackLevels = 1,    RampsPerTerrace = 1,   AllowMesa = false, AllowSpire = false, PondCoverage = 0.30 },
            new BiomeParams { Id = BiomeId.Mountain,    MacroBase = 9, MicroAmp = 1.2, MicroBroad = false, TerraceCoverage = 0.42,  MaxTerraceHeight = 12, StackLevels = 4,    RampsPerTerrace = 0.5, AllowMesa = true,  AllowSpire = true,  PondCoverage = 0.0 },
            new BiomeParams { Id = BiomeId.Moor,        MacroBase = 6, MicroAmp = 1.2, MicroBroad = true,  TerraceCoverage = 0.125, MaxTerraceHeight = 5,  StackLevels = 1,    RampsPerTerrace = 2,   AllowMesa = true,  AllowSpire = false, PondCoverage = 0.01 },
            new BiomeParams { Id = BiomeId.Coast,       MacroBase = 1, MicroAmp = 0.6, MicroBroad = false, TerraceCoverage = 0.05,  MaxTerraceHeight = 5,  StackLevels = 1,    RampsPerTerrace = 1,   AllowMesa = false, AllowSpire = false, PondCoverage = 0.0 },
        };

        public static BiomeParams Get(BiomeId id) => All[(int)id];
    }
}
