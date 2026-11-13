using UnityEngine;

namespace Wort.Terrain
{
    /// <summary>
    /// 程序化崖壁的一套材质（一个 Biome 一套；编号 = TerrainLab 里数组下标，和瓦片套件编号一致）。
    /// main 用于顶面、墙面、只投影的面；clip 用于北面露边的顶面（同一个着色器 Wort/ProcCliff，打开 _ClipNorth）。
    /// </summary>
    [CreateAssetMenu(menuName = "Wort/Proc Cliff Set", fileName = "ProcCliffSet")]
    public sealed class ProcCliffSet : ScriptableObject
    {
        public Material main;
        public Material clip;
    }
}
