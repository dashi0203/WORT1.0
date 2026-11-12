using UnityEngine;

namespace Wort.Terrain
{
    /// <summary>
    /// 空白测试地的手改数据（负责人 2026-11-12：测体素地基，Minecraft 式手搭）：chunks × chunks 个 chunk，每格一个高度和一个套件编号。
    /// 默认存在 Assets/Wort/Data/TestGround.asset。TerrainLab 的生成器选 TestGround 时读它，体素笔刷改它（可撤销）。
    /// </summary>
    [CreateAssetMenu(menuName = "Wort/Test Ground", fileName = "TestGround")]
    public sealed class TestGroundAsset : ScriptableObject
    {
        [Tooltip("边长（chunk）。改了会清空已有的修改。")]
        public int chunks = 3;
        [Tooltip("平地高度（层）。往下挖最多挖到 0。")]
        public int baseHeight = 4;
        [SerializeField, HideInInspector] private byte[] heights;
        [SerializeField, HideInInspector] private byte[] sets;

        public int Width => chunks * 64;

        /// <summary>数组大小不对时（第一次用、改了边长）重建为平地。</summary>
        public void EnsureSize()
        {
            int n = Width * Width;
            if (heights != null && heights.Length == n && sets != null && sets.Length == n) return;
            heights = new byte[n];
            sets = new byte[n];
            for (int i = 0; i < n; i++) heights[i] = (byte)Mathf.Clamp(baseHeight, 0, 255);
        }

        public byte[] Heights { get { EnsureSize(); return heights; } }
        public byte[] Sets { get { EnsureSize(); return sets; } }

        public void ResetFlat()
        {
            heights = null; sets = null;
            EnsureSize();
        }
    }
}
