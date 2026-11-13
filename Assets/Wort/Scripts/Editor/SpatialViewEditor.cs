using UnityEditor;
using UnityEngine;
using Wort.Rendering;

namespace Wort.Editor
{
    /// <summary>SpatialView 的 Inspector：默认字段 + 运行时状态；菜单 Wort/Add Spatial View。</summary>
    [CustomEditor(typeof(SpatialView))]
    public sealed class SpatialViewEditor : UnityEditor.Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var v = (SpatialView)target;
            EditorGUILayout.Space();
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
