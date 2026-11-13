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

        // 体素笔刷（Minecraft 式，只在 TestGround 生成器下可用）
        private static bool voxelOn;
        private static int voxelMode;          // 0 方块（左键放 / Shift 或右键挖），1 拉平（Ctrl 取样高度），2 刷套件
        private static int voxelSize = 1;      // 笔刷边长（格）
        private static int voxelSet;           // 当前套件编号
        private static int flattenHeight = 4;  // 拉平到的高度
        private static readonly string[] VoxelModeNames = { "方块（放 / 挖）", "拉平", "刷套件" };
        private bool voxelHasHit;
        private VoxelHit voxelHit;
        private readonly HashSet<int> voxelStroke = new HashSet<int>(); // 这一笔已经改过的格（拖动时每格只改一次）
        private bool voxelRemoving;

        private void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;
        private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        private void OnUndoRedo()
        {
            // 覆盖层 / 测试地被撤销 / 重做：整张重新生成（覆盖层会重新叠加，测试地按资源里的高度重建）
            var lab = target as TerrainLab;
            if (lab != null && lab.HasGenerated && (lab.RoadOverlay != null || lab.IsTestGround))
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

            if (lab.IsTestGround)
                DrawVoxelBrushInspector(lab);
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
            if (voxelOn && lab.IsTestGround && lab.LastMap != null)
            {
                VoxelSceneGUI(lab);
                return;
            }
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

        // ================= 体素笔刷（Minecraft 式） =================

        private void DrawVoxelBrushInspector(TerrainLab lab)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("体素笔刷（Minecraft 式）", EditorStyles.boldLabel);
            bool on = GUILayout.Toggle(voxelOn, voxelOn ? "体素笔刷：开（Scene 视图里操作）" : "体素笔刷：关", "Button", GUILayout.Height(26));
            if (on != voxelOn)
            {
                voxelOn = on;
                if (on)
                {
                    brushOn = false; // 和道路笔刷互斥
                    EnsureTestGround(lab);
                }
                SceneView.RepaintAll();
            }
            voxelMode = GUILayout.Toolbar(voxelMode, VoxelModeNames);
            voxelSize = EditorGUILayout.IntSlider("笔刷大小（格）", voxelSize, 1, 7);
            if (voxelMode == 1)
                flattenHeight = EditorGUILayout.IntSlider("拉平到高度（层）", flattenHeight, 0, 40);

            var sets = lab.ProcCliffSets;
            if (sets != null && sets.Length > 0)
            {
                var names = new string[sets.Length];
                for (int k = 0; k < sets.Length; k++)
                    names[k] = $"{k + 1}. " + (sets[k] == null ? "（空）" : sets[k].name.Replace("_ProcCliffSet", ""));
                voxelSet = Mathf.Clamp(voxelSet, 0, sets.Length - 1);
                voxelSet = EditorGUILayout.Popup("当前套件（数字键切换）", voxelSet, names);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重置为平地（可撤销）") && lab.TestGroundData != null &&
                    EditorUtility.DisplayDialog("Wort", "把测试地恢复成平地？（高度和套件都清掉，可以撤销）", "重置", "取消"))
                {
                    Undo.RecordObject(lab.TestGroundData, "Reset Test Ground");
                    lab.TestGroundData.ResetFlat();
                    EditorUtility.SetDirty(lab.TestGroundData);
                    Generate(lab);
                }
            }
            // 程序化崖壁（模块化瓦片路线已删除，负责人 2026-11-13）
            using (new EditorGUILayout.HorizontalScope())
            {
                bool proc = EditorGUILayout.ToggleLeft("程序化崖壁（关掉 = 旧崖面外观）", lab.ProcCliff);
                if (proc != lab.ProcCliff)
                {
                    Undo.RecordObject(lab, "Toggle Proc Cliff");
                    lab.ProcCliff = proc;
                    if (proc && (lab.ProcCliffSets == null || lab.ProcCliffSets.Length == 0)) FillProcCliffSets(lab);
                    EditorUtility.SetDirty(lab);
                    Generate(lab);
                }
                if (GUILayout.Button("填入程序化崖壁材质"))
                {
                    Undo.RecordObject(lab, "Assign Proc Cliff Sets");
                    FillProcCliffSets(lab);
                    EditorUtility.SetDirty(lab);
                    Generate(lab);
                }
            }
            if (!lab.UseProcCliff)
                EditorGUILayout.HelpBox("没有打开程序化崖壁（或没有材质套），地形用旧的崖面外观画。勾上“程序化崖壁”或点“填入程序化崖壁材质”。", MessageType.Warning);
            EditorGUILayout.HelpBox(
                "方块：左键 = 放一块（点顶面往上叠，点侧面在旁边那格填到这一层）；Shift + 左键或右键 = 挖一块（点侧面会连上面的一起挖掉，柱子不能悬空）。按住拖动连续放 / 挖。\n" +
                "拉平：左键拖动把笔刷范围拉到指定高度；Ctrl + 左键取样（把指着的那格高度设为目标）。\n" +
                "刷套件：左键拖动把范围内的格换成当前套件（Biome 材质套，交界自动融合）。\n" +
                "数字键 1–9 切换套件；Alt + 拖动照常转视角。所有修改都可以 Ctrl+Z 撤销。",
                MessageType.None);
        }

        /// <summary>按名字排序填入项目里全部 ProcCliffSet（和瓦片套件同样按名字排序，编号对得上）。</summary>
        private static void FillProcCliffSets(TerrainLab lab)
        {
            var found = new List<ProcCliffSet>();
            foreach (var guid in AssetDatabase.FindAssets("t:ProcCliffSet"))
            {
                var t = AssetDatabase.LoadAssetAtPath<ProcCliffSet>(AssetDatabase.GUIDToAssetPath(guid));
                if (t != null) found.Add(t);
            }
            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            lab.ProcCliffSets = found.ToArray();
        }

        /// <summary>找或建测试地资源（Assets/Wort/Data/TestGround.asset）并指定给 TerrainLab。</summary>
        private static void EnsureTestGround(TerrainLab lab)
        {
            if (lab.TestGroundData != null && EditorUtility.IsPersistent(lab.TestGroundData))
                return;
            const string path = "Assets/Wort/Data/TestGround.asset";
            var tg = AssetDatabase.LoadAssetAtPath<TestGroundAsset>(path);
            if (tg == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Wort/Data"))
                    AssetDatabase.CreateFolder("Assets/Wort", "Data");
                tg = CreateInstance<TestGroundAsset>();
                tg.EnsureSize();
                AssetDatabase.CreateAsset(tg, path);
                AssetDatabase.SaveAssets();
            }
            Undo.RecordObject(lab, "Assign Test Ground");
            lab.TestGroundData = tg;
            EditorUtility.SetDirty(lab);
            Generate(lab);
        }

        /// <summary>笔刷范围：以 (cx, cz) 为中心、边长 voxelSize 的方块。</summary>
        private static IEnumerable<(int x, int z)> Square(int cx, int cz)
        {
            int r0 = (voxelSize - 1) / 2, r1 = voxelSize / 2;
            for (int z = cz - r0; z <= cz + r1; z++)
                for (int x = cx - r0; x <= cx + r1; x++)
                    yield return (x, z);
        }

        /// <summary>
        /// 这一下要改哪些格、改成多高（不改地图）。方块模式按 Minecraft 规则：放 = 顶面 +1 / 侧面旁边那格填到这一层；挖 = 顶面 −1 / 侧面挖到被打到的那层。
        /// 笔刷大于 1 时，对以目标格为中心的方块范围做同样的事（顶面：各自 ±1；侧面：都填到 / 挖到同一层）。
        /// </summary>
        private List<(int x, int z, int h)> VoxelEdits(TerrainLab lab, VoxelHit hit, bool remove)
        {
            var map = lab.LastMap;
            var edits = new List<(int, int, int)>();
            if (voxelMode == 1)
            {
                foreach (var (x, z) in Square(hit.X, hit.Z))
                    if (map.InBounds(x, z)) edits.Add((x, z, flattenHeight));
                return edits;
            }
            if (remove)
            {
                if (!VoxelPick.RemoveTarget(map, hit, 0, out int tx, out int tz, out int th)) return edits;
                foreach (var (x, z) in Square(tx, tz))
                {
                    if (!map.InBounds(x, z)) continue;
                    int h = map.Height[map.Index(x, z)];
                    int nh = hit.Face == 4 ? h - 1 : Mathf.Min(h, th);
                    if (nh >= 0 && nh != h) edits.Add((x, z, nh));
                }
            }
            else
            {
                if (!VoxelPick.PlaceTarget(map, hit, out int tx, out int tz, out int th) && hit.Face != 4) return edits;
                foreach (var (x, z) in Square(tx, tz))
                {
                    if (!map.InBounds(x, z)) continue;
                    int h = map.Height[map.Index(x, z)];
                    int nh = hit.Face == 4 ? h + 1 : Mathf.Max(h, th);
                    if (nh != h) edits.Add((x, z, nh));
                }
            }
            return edits;
        }

        private void VoxelSceneGUI(TerrainLab lab)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(id); // 笔刷开着时左键不选物体
                return;
            }
            // 数字键切换套件
            if (e.type == EventType.KeyDown && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9 && lab.ProcCliffSets != null)
            {
                int k = e.keyCode - KeyCode.Alpha1;
                if (k < lab.ProcCliffSets.Length) { voxelSet = k; Repaint(); e.Use(); }
                return;
            }
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                voxelHasHit = lab.RaycastVoxel(HandleUtility.GUIPointToWorldRay(e.mousePosition), out voxelHit);
                SceneView.RepaintAll();
            }
            if (e.alt)
                return; // Alt + 拖动 = 转视角

            bool down = e.type == EventType.MouseDown && (e.button == 0 || (e.button == 1 && voxelMode == 0));
            bool drag = e.type == EventType.MouseDrag && GUIUtility.hotControl == id;
            if ((down || drag) && voxelHasHit)
            {
                if (down)
                {
                    // Ctrl + 左键（拉平模式）：取样高度
                    if (voxelMode == 1 && (e.control || e.command))
                    {
                        flattenHeight = lab.LastMap.Height[lab.LastMap.Index(voxelHit.X, voxelHit.Z)];
                        Repaint();
                        e.Use();
                        return;
                    }
                    EnsureTestGround(lab);
                    voxelStroke.Clear();
                    voxelRemoving = e.shift || e.button == 1;
                    Undo.RecordObject(lab.TestGroundData, VoxelModeNames[voxelMode]);
                    GUIUtility.hotControl = id;
                }
                List<int> changed;
                bool heights = voxelMode != 2;
                if (voxelMode == 2)
                {
                    var cells = new List<(int, int)>();
                    foreach (var c in Square(voxelHit.X, voxelHit.Z)) cells.Add(c);
                    changed = lab.PaintTileset(cells, (byte)voxelSet);
                }
                else
                {
                    var edits = VoxelEdits(lab, voxelHit, voxelRemoving);
                    // 方块模式拖动时，每格这一笔只改一次（不然指着同一处会一直往上叠）
                    if (voxelMode == 0)
                        edits.RemoveAll(t => voxelStroke.Contains(lab.LastMap.Index(t.x, t.z)));
                    changed = lab.SetColumnHeights(edits);
                    foreach (int i in changed) voxelStroke.Add(i);
                }
                if (changed.Count > 0)
                {
                    EditorUtility.SetDirty(lab.TestGroundData);
                    lab.RebuildCells(changed, heights);
                    voxelHasHit = lab.RaycastVoxel(HandleUtility.GUIPointToWorldRay(e.mousePosition), out voxelHit);
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                voxelStroke.Clear();
                e.Use();
            }
            else if (e.type == EventType.Repaint && voxelHasHit)
                DrawVoxelGhost(lab, e.shift);
        }

        /// <summary>预览：指着的面描一圈，要放的方块画绿色线框、要挖的画红色线框。</summary>
        private void DrawVoxelGhost(TerrainLab lab, bool shift)
        {
            var map = lab.LastMap;
            var h = voxelHit;
            // 指着的面
            Handles.color = new Color(1f, 1f, 1f, 0.9f);
            Vector3 c, a, b;
            if (h.Face == 4) { c = new Vector3(h.X + 0.5f, h.Y + 1.002f, h.Z + 0.5f); a = Vector3.right; b = Vector3.forward; }
            else
            {
                float nx = Wort.Generation.Grid.DX4[h.Face], nz = Wort.Generation.Grid.DZ4[h.Face];
                c = new Vector3(h.X + 0.5f + nx * 0.502f, h.Y + 0.5f, h.Z + 0.5f + nz * 0.502f);
                a = new Vector3(-nz, 0, nx); b = Vector3.up;
            }
            var pts = new[] { c - a * 0.5f - b * 0.5f, c + a * 0.5f - b * 0.5f, c + a * 0.5f + b * 0.5f, c - a * 0.5f + b * 0.5f, c - a * 0.5f - b * 0.5f };
            for (int k = 0; k < pts.Length; k++) pts[k] = lab.RegionToWorld(pts[k]);
            Handles.DrawAAPolyLine(3f, pts);

            if (voxelMode == 2)
            {
                Handles.color = new Color(0.4f, 0.8f, 1f, 0.9f);
                foreach (var (x, z) in Square(h.X, h.Z))
                    if (map.InBounds(x, z))
                    {
                        float y = map.Height[map.Index(x, z)] + 0.02f;
                        Handles.DrawWireCube(lab.RegionToWorld(new Vector3(x + 0.5f, y, z + 0.5f)), new Vector3(1f, 0.02f, 1f));
                    }
                return;
            }
            bool remove = voxelMode == 0 && (shift || voxelRemoving && GUIUtility.hotControl != 0);
            var edits = VoxelEdits(lab, h, remove);
            Handles.color = remove ? new Color(1f, 0.35f, 0.3f, 0.95f) : new Color(0.4f, 1f, 0.45f, 0.95f);
            foreach (var (x, z, nh) in edits)
            {
                int oh = map.Height[map.Index(x, z)];
                float y0 = Mathf.Min(oh, nh), y1 = Mathf.Max(oh, nh);
                Handles.DrawWireCube(lab.RegionToWorld(new Vector3(x + 0.5f, (y0 + y1) * 0.5f, z + 0.5f)), new Vector3(1f, y1 - y0, 1f));
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
