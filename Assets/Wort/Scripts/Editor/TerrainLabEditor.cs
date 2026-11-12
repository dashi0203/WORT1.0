using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Wort.Generation;
using Wort.Terrain;

namespace Wort.Editor
{
    [CustomEditor(typeof(TerrainLab))]
    public sealed class TerrainLabEditor : UnityEditor.Editor
    {
        private string replayId = "";

        // 道路笔刷（静态：换选中物体后保留设置）
        private static bool brushOn;
        private static int brushKind = 1;   // 0 擦除，1 小路（1 格），2 次干道（2 格），3 主干道（3 格）
        private static int eraseWidth = 1;  // 擦除的宽度（1–3 格）
        private static readonly string[] KindNames = { "擦除", "小路 1 格", "次干道 2 格", "主干道 3 格" };
        private static readonly string[] WidthNames = { "1 格", "2 格", "3 格" };
        private static int BrushWidth => brushKind == 0 ? eraseWidth : RegionMap.RoadWidthOf((byte)brushKind);
        private readonly HashSet<int> strokeCells = new HashSet<int>();
        private bool hasHover;
        private Vector2 hover;

        private void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;
        private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        private void OnUndoRedo()
        {
            // 覆盖层被撤销 / 重做：整张重新生成（覆盖层会重新叠加）
            var lab = target as TerrainLab;
            if (lab != null && lab.HasGenerated && lab.RoadOverlay != null)
                Generate(lab);
        }

        public override void OnInspectorGUI()
        {
            var lab = (TerrainLab)target;
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck() && lab.HasGenerated)
            {
                lab.RefreshRoomHints(); // 例如勾 / 取消“显示房间提示”
                SceneView.RepaintAll();
            }

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
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel($"刷新 ID：{lab.LastRefreshId ?? "（未生成）"}", GUILayout.Height(18));
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lab.LastRefreshId)))
                        if (GUILayout.Button("复制", GUILayout.Width(60)))
                            EditorGUIUtility.systemCopyBuffer = lab.LastRefreshId;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    replayId = EditorGUILayout.TextField("重放 ID", replayId);
                    if (GUILayout.Button("按 ID 重放", GUILayout.Width(90)))
                    {
                        Undo.RecordObject(lab, "Replay Random Chunk");
                        if (!lab.ReplayRefresh(replayId, out string error))
                            Debug.LogWarning($"[TerrainLab] 重放失败：{error}", lab);
                        EditorUtility.SetDirty(lab);
                        SceneView.RepaintAll();
                    }
                }
            }

            DrawRoadBrushInspector(lab);

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

        private void DrawRoadBrushInspector(TerrainLab lab)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("道路笔刷", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool on = GUILayout.Toggle(brushOn, brushOn ? "笔刷：开（Scene 视图左键拖动）" : "笔刷：关", "Button", GUILayout.Height(24));
                if (on != brushOn)
                {
                    brushOn = on;
                    if (on) EnsureOverlay(lab);
                    SceneView.RepaintAll();
                }
            }
            brushKind = GUILayout.Toolbar(brushKind, KindNames);
            if (brushKind == 0)
                eraseWidth = GUILayout.Toolbar(eraseWidth - 1, WidthNames) + 1;
            EditorGUILayout.HelpBox("大多数路用小路（1 格，看起来约 0.85m）；次干道 2 格、主干道 3 格。", MessageType.None);
            var ov = lab.RoadOverlay;
            if (ov == null)
                EditorGUILayout.HelpBox("还没有道路覆盖层；打开笔刷时会在 Assets/Wort/Data/ 下按种子自动创建。", MessageType.None);
            else
            {
                EditorGUILayout.HelpBox($"覆盖层：{AssetDatabase.GetAssetPath(ov)}（种子 {ov.seed} / {ov.generator}，{ov.Count} 条记录）" +
                    (lab.RoadOverlayMatches ? "" : "\n和当前种子 / 生成器不一致，不会叠加；打开笔刷会改用当前种子的覆盖层。"),
                    lab.RoadOverlayMatches ? MessageType.None : MessageType.Warning);
                if (GUILayout.Button("清空覆盖层（可撤销）") && ov.Count > 0 && EditorUtility.DisplayDialog("Wort", $"清空 {ov.Count} 条手刷道路记录？", "清空", "取消"))
                {
                    Undo.RecordObject(ov, "Clear Road Overlay");
                    ov.ClearAll();
                    EditorUtility.SetDirty(ov);
                    Generate(lab);
                }
            }
        }

        /// <summary>找或建当前种子 / 生成器的覆盖层资源（Assets/Wort/Data/RoadOverlay_seed{种子}_{生成器}.asset）。</summary>
        private static void EnsureOverlay(TerrainLab lab)
        {
            if (lab.RoadOverlayMatches)
                return;
            string gen = lab.Generator.ToString();
            string path = $"Assets/Wort/Data/RoadOverlay_seed{lab.Seed}_{gen}.asset";
            var ov = AssetDatabase.LoadAssetAtPath<RoadOverlayAsset>(path);
            if (ov == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Wort/Data"))
                    AssetDatabase.CreateFolder("Assets/Wort", "Data");
                ov = CreateInstance<RoadOverlayAsset>();
                ov.seed = lab.Seed;
                ov.generator = gen;
                AssetDatabase.CreateAsset(ov, path);
                AssetDatabase.SaveAssets();
            }
            Undo.RecordObject(lab, "Assign Road Overlay");
            lab.RoadOverlay = ov;
            EditorUtility.SetDirty(lab);
        }

        private void OnSceneGUI()
        {
            var lab = (TerrainLab)target;
            if (!brushOn || lab.LastMap == null)
                return;
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(id); // 笔刷开着时左键不选物体
                return;
            }
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                hasHover = lab.PickGround(HandleUtility.GUIPointToWorldRay(e.mousePosition), out hover);
                SceneView.RepaintAll();
            }
            if (e.alt)
                return; // Alt + 拖动 = 转视角
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && hasHover)
            {
                EnsureOverlay(lab);
                if (e.type == EventType.MouseDown)
                {
                    strokeCells.Clear();
                    Undo.RecordObject(lab.RoadOverlay, "Paint Road");
                    GUIUtility.hotControl = id;
                }
                foreach (int i in lab.PaintRoad(hover, BrushWidth, (byte)brushKind))
                    strokeCells.Add(i);
                EditorUtility.SetDirty(lab.RoadOverlay);
                // 拖动中每攒够一些格就重建一次，松开时把剩下的重建
                if (strokeCells.Count >= 24)
                {
                    lab.RebuildRoadChunks(strokeCells);
                    strokeCells.Clear();
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && GUIUtility.hotControl == id)
            {
                lab.RebuildRoadChunks(strokeCells);
                strokeCells.Clear();
                GUIUtility.hotControl = 0;
                e.Use();
            }
            else if (e.type == EventType.Repaint && hasHover)
            {
                Handles.color = brushKind == 0 ? new Color(1f, 0.35f, 0.3f, 0.9f) : (brushKind == 1 ? new Color(1f, 0.85f, 0.4f, 0.9f) : new Color(1f, 0.6f, 0.2f, 0.9f));
                Handles.DrawWireDisc(lab.GroundToWorld(hover), lab.transform.up, BrushWidth * 0.5f);
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
