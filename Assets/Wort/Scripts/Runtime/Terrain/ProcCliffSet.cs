using UnityEngine;

namespace Wort.Terrain
{
    /// <summary>
    /// 程序化崖壁的一套材质（一个 Biome 一套；编号 = TerrainLab 里数组下标，和瓦片套件编号一致）。
    /// main 用于顶面、墙面、只投影的面；clip 用于北面露边的顶面（同一个着色器 Wort/ProcCliff，打开 _ClipNorth）。
    /// TerrainLab 运行时把各套的贴图打包成数组、用一个合并材质画，main / clip 主要是存贴图和参数。
    /// </summary>
    [CreateAssetMenu(menuName = "Wort/Proc Cliff Set", fileName = "ProcCliffSet")]
    public sealed class ProcCliffSet : ScriptableObject
    {
        public Material main;
        public Material clip;

        [Tooltip("这一套对应的 Biome 编号（Wort.Generation.BiomeId：0 人类区 1 混交林 2 针叶林 3 沼泽 4 山地 5 荒原 6 海岸）。正式世界按这些 Biome 的权重之和融合；没有套认领的 Biome 归第 0 套。")]
        public int[] biomeIds;

        [Tooltip("崖壁变体（颜色），顺序固定：2x2 v1、2x2 v2、2x1 v1、2x1 v2、1x2 v1、1x2 v2（每格 512，和基础崖壁同格式）。空 = 不用变体。")]
        public Texture2D[] variantColors;

        [Tooltip("崖壁变体（法线），顺序同上。")]
        public Texture2D[] variantNormals;
    }
}
