using System.Collections.Generic;
using UnityEngine;

namespace Wort.Terrain
{
    /// <summary>
    /// 一套模块化地形瓦片（Docs/TilesetSpec.md，如 Art/Tilesets/Limestone）：Prefab 清单，按名字查网格和材质。
    /// 名字：Top_E{E}_C{C}（没有凹角时也可以叫 Top_E{E}）、{Front|Side}_{Crown|Repeat}_{A|B}。
    /// 用菜单 Assets/Wort/从所选文件夹创建 Tileset 生成，或手动把 Prefab 拖进 pieces。
    /// </summary>
    [CreateAssetMenu(menuName = "Wort/Tileset", fileName = "Tileset")]
    public sealed class TilesetAsset : ScriptableObject
    {
        [Tooltip("显示用的名字（如“石灰岩（人类区）”）。")]
        public string displayName;
        [Tooltip("这一套的全部瓦片 Prefab（名字即瓦片名）。")]
        public List<GameObject> pieces = new List<GameObject>();

        private Dictionary<string, (Mesh mesh, Material[] materials)> cache;

        private void OnValidate() => cache = null;

        private void BuildCache()
        {
            cache = new Dictionary<string, (Mesh, Material[])>();
            foreach (var go in pieces)
            {
                if (go == null) continue;
                var mf = go.GetComponentInChildren<MeshFilter>();
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mf == null || mf.sharedMesh == null || mr == null) continue;
                string name = go.name;
                cache[name] = (mf.sharedMesh, mr.sharedMaterials);
                // Top_E8 与 Top_E8_C0 是同一块
                if (name.StartsWith("Top_E") && name.Length == 6) cache[name + "_C0"] = (mf.sharedMesh, mr.sharedMaterials);
            }
        }

        /// <summary>按瓦片名取网格和材质；墙件缺 B 变体时用 A。</summary>
        public bool TryGet(string name, out Mesh mesh, out Material[] materials)
        {
            if (cache == null) BuildCache();
            if (cache.TryGetValue(name, out var v) || (name.EndsWith("_B") && cache.TryGetValue(name.Substring(0, name.Length - 1) + "A", out v)))
            {
                mesh = v.mesh; materials = v.materials;
                return true;
            }
            mesh = null; materials = null;
            return false;
        }
    }
}
