using UnityEditor;
using UnityEngine;
using Wort.Terrain;

namespace Wort.Editor
{
    [CustomEditor(typeof(TerrainLab))]
    public sealed class TerrainLabEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var lab = (TerrainLab)target;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("生成", GUILayout.Height(28)))
                    Generate(lab);
                if (GUILayout.Button("换种子并生成", GUILayout.Height(28)))
                {
                    Undo.RecordObject(lab, "Change Terrain Seed");
                    lab.Seed = Random.Range(1, int.MaxValue);
                    EditorUtility.SetDirty(lab);
                    Generate(lab);
                }
                if (GUILayout.Button("清除", GUILayout.Height(28)))
                    lab.Clear();
            }

            if (lab.CanRefreshRandom)
            {
                if (GUILayout.Button($"重新生成随机 chunk（当前第 {lab.RefreshIndex + 1} 次）", GUILayout.Height(28)))
                {
                    Undo.RecordObject(lab, "Regenerate Random Chunk");
                    lab.RegenerateRandomChunk();
                    EditorUtility.SetDirty(lab);
                    SceneView.RepaintAll();
                }
                if (!string.IsNullOrEmpty(lab.LastRefreshReport))
                    EditorGUILayout.HelpBox(lab.LastRefreshReport, MessageType.None);
            }

            var map = lab.LastMap;
            if (map != null)
            {
                map.Stats.TryGetValue("ramps", out int ramps);
                map.Stats.TryGetValue("slopePermille", out int slope);
                map.Stats.TryGetValue("unreachableCells", out int unreachable);
                map.Stats.TryGetValue("maxHeight", out int maxH);
                EditorGUILayout.HelpBox(
                    $"checksum 0x{map.Checksum():X8}（与云端预览相同即结果一致）\n" +
                    $"耗时 {lab.LastMilliseconds} ms   坡道 {ramps}   斜坡 {slope / 10f:0.0}%   最高 {maxH} 层   走不到 {unreachable} 格\n" +
                    $"出生点 ({map.SpawnX}, {map.SpawnZ})",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("生成的地形不保存进场景，进入 Play 时会自动重新生成。", MessageType.None);
            }
        }

        private static void Generate(TerrainLab lab)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Wort", "生成地形…", 0.5f);
                lab.Generate();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            SceneView.RepaintAll();
        }
    }
}
