using System.Collections.Generic;
using UnityEngine;
using Wort.Generation;

namespace Wort.Terrain
{
    /// <summary>
    /// 手刷道路的覆盖层（存成资源，默认 Assets/Wort/Data/RoadOverlay_seed{种子}.asset）。
    /// 每条记录 = 一格 + 类型（0 擦掉，1 小路，2 次干道，3 主干道），同一格只留最后一次。生成地形后由 TerrainLab 叠加（RoadOverlay.Apply）。
    /// 只对记录的种子和生成器有效；以后正式的 chunk 编辑工具沿用这一层。
    /// </summary>
    [CreateAssetMenu(menuName = "Wort/Road Overlay", fileName = "RoadOverlay")]
    public sealed class RoadOverlayAsset : ScriptableObject
    {
        [Tooltip("这份覆盖层对应的地形种子。种子不同时不叠加。")]
        public int seed;
        [Tooltip("对应的生成器（TerrainLab.GeneratorKind 的名字）。")]
        public string generator;

        [SerializeField, HideInInspector] private List<int> xs = new List<int>();
        [SerializeField, HideInInspector] private List<int> zs = new List<int>();
        [SerializeField, HideInInspector] private List<byte> kinds = new List<byte>();

        private Dictionary<long, int> index;

        public int Count => xs.Count;

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private void BuildIndex()
        {
            index = new Dictionary<long, int>(xs.Count);
            for (int k = 0; k < xs.Count; k++)
                index[Key(xs[k], zs[k])] = k;
        }

        /// <summary>记一格的修改（同一格覆盖旧记录）。返回是否有变化。</summary>
        public bool Set(int x, int z, byte kind)
        {
            if (index == null || index.Count != xs.Count)
                BuildIndex();
            long key = Key(x, z);
            if (index.TryGetValue(key, out int k))
            {
                if (kinds[k] == kind)
                    return false;
                kinds[k] = kind;
                return true;
            }
            index[key] = xs.Count;
            xs.Add(x); zs.Add(z); kinds.Add(kind);
            return true;
        }

        public void ClearAll()
        {
            xs.Clear(); zs.Clear(); kinds.Clear();
            index = null;
        }

        /// <summary>叠加到地图上，返回改动的格。</summary>
        public List<int> ApplyTo(RegionMap map) => RoadOverlay.Apply(map, xs, zs, kinds);

        private void OnValidate() => index = null; // 撤销 / 重做后重建索引
    }
}
