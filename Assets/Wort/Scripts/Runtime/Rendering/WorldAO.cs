using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Wort.Generation;

namespace Wort.Rendering
{
    /// <summary>
    /// 世界空间环境光遮蔽的调试 / 调参入口（负责人 2026-11-13：要 AO，但不要抢眼，要能调）。
    /// 计算在着色器 WortAO.hlsl 里：地形按体素高度场，物体按 AOOccluder 登记的胶囊；只乘环境光。
    /// 场景里没有这个组件时 AO 关闭。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Wort/World AO")]
    public sealed class WorldAO : MonoBehaviour
    {
        public enum DebugView
        {
            [InspectorName("关")] Off = 0,
            [InspectorName("只看 AO（合成）")] Combined = 1,
            [InspectorName("只看地形 AO")] TerrainOnly = 2,
            [InspectorName("只看物体 AO")] ObjectsOnly = 3
        }

        [Tooltip("总开关（对比用）")]
        public bool aoEnabled = true;
        [Tooltip("调试显示：画面只输出 AO 的灰度（白 = 不遮，黑 = 全遮）")]
        public DebugView debugView = DebugView.Off;

        [Header("地形（崖脚、内拐角、墙根）")]
        [Tooltip("最多暗多少（0 = 关，1 = 最暗处环境光全黑）。只作用于环境光")]
        [Range(0f, 1f)] public float terrainStrength = 0.35f;
        [Tooltip("影响范围（米）：离墙 / 高处多远以内开始变暗")]
        [Range(0.2f, 2f)] public float terrainRadius = 0.8f;

        [Header("物体（主角等，挂 AOOccluder 的）")]
        [Tooltip("最多暗多少（0 = 关）。只作用于环境光")]
        [Range(0f, 1f)] public float objectStrength = 0.4f;
        [Tooltip("只取离相机最近的这么多个物体（着色器上限 16）")]
        [Range(0, 16)] public int maxOccluders = 16;

        [Header("其他")]
        [Tooltip("直射光也乘 AO 的比例。0 = 只压环境光（推荐：强光下几乎看不出，阴影里 / 阴天才明显）")]
        [Range(0f, 1f)] public float directLighting = 0f;

        private static readonly int IdParams = Shader.PropertyToID("_WortAOParams");
        private static readonly int IdParams2 = Shader.PropertyToID("_WortAOParams2");
        private static readonly int IdOccA = Shader.PropertyToID("_WortOccA");
        private static readonly int IdOccB = Shader.PropertyToID("_WortOccB");
        private static readonly int IdOccCount = Shader.PropertyToID("_WortOccCount");
        private readonly Vector4[] occA = new Vector4[16], occB = new Vector4[16];
        private readonly List<(float d, AOOccluder o)> sorted = new List<(float, AOOccluder)>();

        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContext;
            Apply();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            Shader.SetGlobalVector(IdParams, Vector4.zero);
            Shader.SetGlobalVector(IdParams2, Vector4.zero);
            Shader.SetGlobalFloat(IdOccCount, 0);
        }

        private void OnValidate() => Apply();

        private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras) => Apply();

        private void Apply()
        {
            if (!isActiveAndEnabled) return;
            Shader.SetGlobalVector(IdParams, new Vector4(aoEnabled ? 1 : 0, terrainStrength, terrainRadius, objectStrength));
            Shader.SetGlobalVector(IdParams2, new Vector4((float)debugView, directLighting, 0, 0));

            // 离主相机最近的几个遮蔽体
            Vector3 focus = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            sorted.Clear();
            foreach (var o in AOOccluder.All)
            {
                if (o == null || !o.isActiveAndEnabled) continue;
                Vector3 p = o.transform.position;
                float dx = p.x - focus.x, dz = p.z - focus.z;
                sorted.Add((dx * dx + dz * dz, o));
            }
            sorted.Sort((a, b) => a.d.CompareTo(b.d));
            int n = Mathf.Min(Mathf.Min(maxOccluders, 16), sorted.Count);
            for (int i = 0; i < 16; i++)
            {
                if (i < n)
                {
                    sorted[i].o.GetCapsule(out Vector3 a, out Vector3 b, out float r);
                    occA[i] = new Vector4(a.x, a.y, a.z, r);
                    occB[i] = new Vector4(b.x, b.y, b.z, 0);
                }
                else occA[i] = occB[i] = Vector4.zero;
            }
            Shader.SetGlobalVectorArray(IdOccA, occA);
            Shader.SetGlobalVectorArray(IdOccB, occB);
            Shader.SetGlobalFloat(IdOccCount, n);
        }
    }

    /// <summary>地形高度场贴图（1 格 1 像素，R = 高度，G = 坡向 + 1，B = 水面高度），TerrainLab 生成 / 改地形后上传，WortAO.hlsl 读。</summary>
    public static class WorldAOField
    {
        private static Texture2D tex;
        /// <summary>地图里最低的地面高度（最低一层压暗用，SpatialView 读）。</summary>
        public static float MinHeight { get; private set; }
        public static bool HasField => tex != null;
        private static readonly int IdTex = Shader.PropertyToID("_WortHeightTex");
        private static readonly int IdRect = Shader.PropertyToID("_WortHeightRect");

        /// <summary>地图在世界里的原点（格 0, 0 的西南角），和 TerrainLab 的 chunk 摆放一致（_WortBiomeRect 同样从 0, 0 起）。</summary>
        public static void Upload(RegionMap map, Vector2 origin = default)
        {
            if (map == null) return;
            int w = map.W, h = map.H;
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null)
                {
                    if (Application.isPlaying) Object.Destroy(tex); else Object.DestroyImmediate(tex);
                }
                tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true)
                {
                    name = "Wort Height Field",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave
                };
            }
            var px = new Color[w * h];
            int min = int.MaxValue;
            for (int i = 0; i < px.Length; i++)
            {
                // B = 水面高度（没有水 = −10000）：遮挡虚化判断“挡住主角的柱子”时柱顶取地面和水面较高者
                px[i] = new Color(map.Height[i], map.Slope != null ? map.Slope[i] + 1 : 0, map.HasWater(i) ? map.Water[i] : -10000f, 0);
                if (map.Height[i] < min) min = map.Height[i];
            }
            MinHeight = min;
            tex.SetPixels(px);   // 行优先 z * W + x，正好是贴图的 (x, y)
            tex.Apply(false);
            Shader.SetGlobalTexture(IdTex, tex);
            Shader.SetGlobalVector(IdRect, new Vector4(origin.x, origin.y, w, h));
        }
    }
}
