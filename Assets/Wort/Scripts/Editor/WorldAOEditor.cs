using UnityEditor;
using UnityEngine;
using Wort.Rendering;

namespace Wort.Editor
{
    /// <summary>WorldAO 的 Inspector：默认字段 + 一键给主角加遮蔽体；菜单 Wort/Add World AO。</summary>
    [CustomEditor(typeof(WorldAO))]
    public sealed class WorldAOEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            int n = 0;
            foreach (var o in Object.FindObjectsByType<AOOccluder>(FindObjectsSortMode.None)) if (o.isActiveAndEnabled) n++;
            EditorGUILayout.LabelField("场景里的遮蔽体", n.ToString());
            if (GUILayout.Button("给主角加遮蔽体（AOOccluder）"))
            {
                var player = Object.FindFirstObjectByType<WortPigPlayer>();
                if (player == null) EditorUtility.DisplayDialog("Wort", "场景里没有找到主角（WortPigPlayer）。", "好");
                else if (player.GetComponent<AOOccluder>() == null)
                {
                    Undo.AddComponent<AOOccluder>(player.gameObject);
                    Selection.activeGameObject = player.gameObject;
                }
            }
            EditorGUILayout.HelpBox("地形 AO 需要 Terrain Lab 生成过一次（生成时上传高度场）。调试显示里白 = 不遮，黑 = 全遮。", MessageType.None);
        }

        /// <summary>菜单 Wort/Add World AO：给当前场景加上世界空间 AO（已有就选中它），主角没有遮蔽体时顺便加上。</summary>
        [MenuItem("Wort/Add World AO")]
        public static void AddToScene()
        {
            var existing = Object.FindFirstObjectByType<WorldAO>();
            Selection.activeGameObject = existing != null ? existing.gameObject : Create().gameObject;
        }

        public static WorldAO Create()
        {
            var go = new GameObject("World AO");
            Undo.RegisterCreatedObjectUndo(go, "Add World AO");
            var ao = go.AddComponent<WorldAO>();
            var player = Object.FindFirstObjectByType<WortPigPlayer>();
            if (player != null && player.GetComponent<AOOccluder>() == null)
                Undo.AddComponent<AOOccluder>(player.gameObject);
            return ao;
        }
    }
}
