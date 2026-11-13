using UnityEditor;
using UnityEngine;
using Wort.Rendering;

namespace Wort.Editor
{
    /// <summary>SpatialView 的 Inspector：按功能分组折叠、中文标签（负责人 2026-11-13：英文字段名找不到）；底部运行时状态；菜单 Wort/Add Spatial View。</summary>
    [CustomEditor(typeof(SpatialView))]
    public sealed class SpatialViewEditor : UnityEditor.Editor
    {
        private static bool fHole = true, fEagle = true, fFog = true, fBottom = true, fPlayer;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        private void P(string field, string label)
        {
            var p = serializedObject.FindProperty(field);
            if (p == null) return;
            EditorGUILayout.PropertyField(p, new GUIContent(label, p.tooltip));
        }

        private static bool Section(bool open, string title)
        {
            EditorGUILayout.Space(4);
            return EditorGUILayout.BeginFoldoutHeaderGroup(open, title);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var v = (SpatialView)target;
            float lw = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(lw, 190f);   // 中文标签较长，加宽免得被截断

            fHole = Section(fHole, "遮挡虚化（主角被挡住时挖圆）");
            if (fHole)
            {
                P("occlusionCutout", "开启");
                P("holeRadius", "范围：圆的半径（米）");
                P("openThreshold", "被挡多少时开启");
                P("closeThreshold", "降到多少时收回");
                P("holeTime", "展开 / 收回用时（秒）");
                P("holeWave", "圆边波浪幅度");
                P("holeFade", "边缘渐变带宽");
                P("holeEdgeBite", "黑边扰动（米）");
                P("holeCutAbove", "只裁脚下多高以上（米）");
                P("debugForceHole", "调试：一直显示虚化圆");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fEagle = Section(fEagle, "鹰眼视觉");
            if (fEagle)
            {
                P("eagleVision", "开启（运行时按快捷键）");
                P("eagleKey", "快捷键");
                P("eagleRadius", "范围：圆的半径（米）");
                P("cutAbove", "剔除脚下多高以上（格）");
                P("eagleDimExtra", "反选区：圆外再扩（米）");
                P("eagleDim", "反选区压暗程度");
                P("eagleDimSoft", "反选区边缘软化（米）");
                P("desaturate", "饱和度降低");
                P("sweepTime", "进出过渡用时（秒）");
                P("eagleWave", "圆边波浪幅度");
                P("eagleDissolve", "边缘溶解带宽（米）");
                P("eagleCutJitter", "断口高低起伏（米）");
                P("capDilate", "黑色横切面外扩（米）");
                P("rimEnabled", "蓝色外扩边");
                P("rimColor", "外扩边颜色");
                P("rimMinPixels", "外扩边最细（像素）");
                P("rimMaxPixels", "外扩边最粗（像素）");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fFog = Section(fFog, "主角脚下薄雾");
            if (fFog)
            {
                P("fog", "开启");
                P("fogOffset", "起雾高度：脚下往下（米）");
                P("fogWidth", "渐变厚度（米）");
                P("fogDensity", "最大浓度");
                P("fogTint", "雾色");
                P("fogSunLight", "太阳光对雾色的贡献");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fBottom = Section(fBottom, "黑色空处与最低层压暗");
            if (fBottom)
            {
                P("blackVoid", "相机背景纯黑");
                P("darkenBottom", "最低一层压暗");
                P("bottomDarken", "最多暗多少");
                P("bottomFade", "恢复正常的高度（米）");
                P("overrideBottom", "手动指定最低层高度");
                if (v.overrideBottom) P("bottomHeight", "最低层高度（米）");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fPlayer = Section(fPlayer, "主角");
            if (fPlayer)
            {
                P("player", "主角（空 = 自动找）");
                P("bodyHeight", "模型大致高度（米）");
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
            EditorGUIUtility.labelWidth = lw;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("运行时状态", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("主角被挡住的比例", v.OccludedFraction);
                EditorGUILayout.FloatField("虚化圆当前半径", v.HoleRadiusNow);
                EditorGUILayout.FloatField("鹰眼视觉过渡", v.EagleBlend);
            }
            if (!WorldAOField.HasField)
                EditorGUILayout.HelpBox("还没有地形高度场：鹰眼视觉的横切面和最低层高度需要 Terrain Lab 先生成一次。", MessageType.Info);
        }

        /// <summary>菜单 Wort/Add Spatial View：给当前场景加上空间呈现（已有就选中它）。</summary>
        [MenuItem("Wort/Add Spatial View")]
        public static void AddToScene()
        {
            var existing = Object.FindFirstObjectByType<SpatialView>();
            Selection.activeGameObject = existing != null ? existing.gameObject : Create().gameObject;
        }

        public static SpatialView Create()
        {
            var go = new GameObject("Spatial View");
            Undo.RegisterCreatedObjectUndo(go, "Add Spatial View");
            return go.AddComponent<SpatialView>();
        }
    }
}
