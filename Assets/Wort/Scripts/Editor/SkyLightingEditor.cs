using UnityEditor;
using UnityEngine;
using Wort.Rendering;

namespace Wort.Editor
{
    /// <summary>SkyLighting 的 Inspector：默认字段 + 运行时状态（实际 overcast、云覆盖率、随机起伏、直射光平均可见度）。</summary>
    [CustomEditor(typeof(SkyLighting))]
    public sealed class SkyLightingEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var sky = (SkyLighting)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("运行时状态", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("实际 overcast", sky.EffectiveOvercast);
                EditorGUILayout.FloatField("随机起伏", sky.GustOffset);
                EditorGUILayout.FloatField("云覆盖率", sky.Coverage);
                EditorGUILayout.FloatField("直射光平均可见度", sky.DirectVisibility);
            }
            if (sky.sun == null)
                EditorGUILayout.HelpBox("没有指定太阳（Directional Light），也没有 RenderSettings.sun。", MessageType.Warning);
            if (!SystemInfo.supportsComputeShaders)
                EditorGUILayout.HelpBox("当前平台不支持 compute shader，云影不会生成。", MessageType.Warning);
        }

        /// <summary>菜单 Wort/Add Sky Lighting：给当前场景加上云影 / 天气光照（已有就选中它）。</summary>
        [MenuItem("Wort/Add Sky Lighting (Cloud Shadows)")]
        public static void AddToScene()
        {
            var existing = Object.FindFirstObjectByType<SkyLighting>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                return;
            }
            Selection.activeGameObject = Create().gameObject;
        }

        /// <summary>建一个带 SkyLighting 的物体，太阳取 RenderSettings.sun，没有就取场景里第一盏方向光。</summary>
        public static SkyLighting Create()
        {
            var go = new GameObject("Sky Lighting");
            Undo.RegisterCreatedObjectUndo(go, "Add Sky Lighting");
            var sky = go.AddComponent<SkyLighting>();   // Reset() 里自动找太阳、加载 CloudShadow.compute
            if (sky.sun == null)
            {
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sky.sun = l; break; }
            }
            if (sky.sun != null)
            {
                sky.sunIntensity = sky.sun.intensity;   // 沿用场景里原来的太阳强度
            }
            EditorUtility.SetDirty(sky);
            return sky;
        }
    }
}
