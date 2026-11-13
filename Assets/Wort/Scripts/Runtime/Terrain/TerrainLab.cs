using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Wort.Generation;
using Debug = UnityEngine.Debug;

namespace Wort.Terrain
{
    /// <summary>
    /// 8×8 chunk 地形实验（GDD 第 16 节）。生成核心在 Wort.Generation（纯 C#），
    /// 这里只负责把 MeshData 转成 Unity 网格、纯色材质和碰撞体，并把角色放到出生点。
    /// 生成的物体都标记为 DontSave：不写进场景文件，进入 Play 时重新生成。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TerrainLab : MonoBehaviour
    {
        const string RootName = "Generated Terrain";

        [SerializeField, Tooltip("地形种子。同一种子在云端预览和 Unity 里结果相同（见 Console 里的 checksum）。")]
        private int seed = 20261109;

        [SerializeField, Tooltip("进入 Play 时自动生成。")]
        private bool generateOnPlay = true;

        [SerializeField, Tooltip("生成后移动到出生点的角色。")]
        private Transform player;

        [SerializeField, Tooltip("给地形加 MeshCollider（水面不加）。")]
        private bool addColliders = true;

        public enum GeneratorKind { Legacy8x8, Terrace5x5, TestGround }

        [SerializeField, Tooltip("Legacy8x8 = 旧 8×8 生成器；Terrace5x5 = 体素器第一步（TerraceField，5×5，种子 3 是负责人选定的测试种子）；" +
            "TestGround = 空白测试地（手搭地形测地基，Inspector 里有体素笔刷）。")]
        private GeneratorKind generator = GeneratorKind.Legacy8x8;

        [SerializeField, Tooltip("Terrace5x5 时启用右上角的随机 chunk（chunk 3,3），用 RandomRegion 生成；Inspector 里有“重新生成随机 chunk”按钮。")]
        private bool randomChunk = true;

        [SerializeField, Tooltip("随机 chunk 的刷新序号（0 起）。每点一次“重新生成随机 chunk”加 1；同一种子、同一序号结果相同。")]
        private int refreshIndex;

        [SerializeField, Tooltip("地形外观方案 B（CliffMesher）：立方体 + 外倒角 / 内倒角、泥土顶面、边缘一圈石头。只改画面，碰撞仍用原来的 1m 体素网格。")]
        private bool cliffDressing = true;

        [SerializeField, Tooltip("随机区域的房间提示：每个房间一层薄薄的半透明色块（平整区域淡色；往里收 1 格、以后台地可生成的区域再叠一层、颜色更深；边缘描边）。只显示，不影响碰撞和 checksum。")]
        private bool showRoomHints = true;

        [SerializeField, Tooltip("手刷道路的覆盖层（Inspector 里的“道路笔刷”会自动创建）。种子 / 生成器和当前不一致时不叠加。")]
        private RoadOverlayAsset roadOverlay;

        [SerializeField, Tooltip("空白测试地的数据（每格高度、套件编号）。体素笔刷打开时自动创建 Assets/Wort/Data/TestGround.asset。")]
        private TestGroundAsset testGround;

        [SerializeField, Tooltip("TestGround / Terrace5x5 用程序化崖壁（ProcCliffMesher + Wort/ProcCliff）。关掉就用旧的崖面外观（CliffMesher）。模块化瓦片路线已删除（负责人 2026-11-13）。")]
        private bool procCliff = true;   // 默认开：设置被丢掉（例如切到旧分支）时不会悄悄退回旧外观

        [SerializeField, Tooltip("程序化崖壁的材质套（编号 = 数组下标，和瓦片套件编号一致）。Inspector 里可以一键填入。")]
        private ProcCliffSet[] procCliffSets;

        [SerializeField, Tooltip("程序化崖壁的 Biome 融合：权重按格模糊的半径（格）。测试地小，默认 4；正式世界按 GDD 是 32–64m 的过渡带。0 = 不融合（硬交界）。")]
        private int biomeBlendRadius = 4;

        [SerializeField, Tooltip("正式生成器（5×5 等）的 Biome 融合半径（格）：地图自带的 Biome 是逐格硬分区，这里模糊成渐变带（GDD：过渡带 32–64m）。")]
        private int biomeBlendRadiusWorld = 10;

        // 程序化崖壁运行时资源：各套贴图打包成的数组、合并材质、Biome 权重图（都不存盘）
        private Texture2DArray procColArr, procNrmArr;
        private Material procMat;
        private Texture2D biomeMap;
        private ProcCliffSet[] procBuiltFor;

        private Material[] materials;
        private RegionVoxels lastVoxels;
        private RandomRegionResult lastRandom;
        private Material roomHintMaterial;
        private Material vineMaterial, stakeMaterial;
        private Material blendMaterial;   // Wort/TerrainBlend（方案 B 的泥土 / 岩石融合）
        private Texture2D dirtTexture, rockTexture, roadTexture;

        public int Seed
        {
            get => seed;
            set => seed = value;
        }

        public RegionMap LastMap { get; private set; }
        public long LastMilliseconds { get; private set; }

        public bool HasGenerated => transform.Find(RootName) != null;

        private void Start()
        {
            if (generateOnPlay && !HasGenerated)
                Generate();
        }

        private void OnDestroy()
        {
            Clear();
            if (materials == null)
                return;
            foreach (var m in materials)
                DestroySafe(m);
            materials = null;
            DestroySafe(blendMaterial);
            DestroySafe(roomHintMaterial);
            DestroySafe(vineMaterial);
            DestroySafe(stakeMaterial);
            DestroySafe(dirtTexture);
            DestroySafe(rockTexture);
            DestroySafe(roadTexture);
            blendMaterial = null;
        }

        public void Generate()
        {
            Clear();
            var sw = Stopwatch.StartNew();
            uint useed = unchecked((uint)seed);
            RegionMap map;
            lastRandom = null;
            if (generator == GeneratorKind.TestGround)
            {
                if (testGround == null)
                {
                    Debug.LogWarning("[TerrainLab] 没有指定 TestGround 数据，先用一块临时平地（打开体素笔刷会自动创建资源）。", this);
                    testGround = ScriptableObject.CreateInstance<TestGroundAsset>();
                    testGround.hideFlags = HideFlags.DontSave;
                }
                map = Wort.Generation.TestGround.Create(testGround.chunks, testGround.baseHeight, testGround.Heights, useed);
            }
            else if (generator == GeneratorKind.Terrace5x5 && randomChunk)
                map = RefreshRandom(useed, out lastRandom);
            else
                map = generator == GeneratorKind.Terrace5x5
                    ? TerraceField.Generate(new TerraceFieldSettings { Seed = useed }, out _)
                    : RegionGenerator.Generate(new RegionSettings { Seed = useed });
            ApplyRoadOverlay(map);
            var settings = map.Settings;
            var voxels = new RegionVoxels(map);
            lastVoxels = voxels;
            EnsureMaterials();

            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(transform, false);
            var all = new List<(int cx, int cz)>();
            for (int cz = 0; cz < settings.ChunksZ; cz++)
                for (int cx = 0; cx < settings.ChunksX; cx++)
                    all.Add((cx, cz));
            int triangles = BuildChunks(root.transform, map, voxels, all, useed, out long tCompute, out long tApply);
            BuildRoomHints(root.transform);
            BuildClimbs(root.transform);
            sw.Stop();
            LastMap = map;
            LastMilliseconds = sw.ElapsedMilliseconds;
            PlacePlayer(map);

            map.Stats.TryGetValue("slopePermille", out int slope);
            map.Stats.TryGetValue("ramps", out int ramps);
            if (CanRefreshRandom)
                Debug.Log($"[TerrainLab] {LastRefreshReport}", this);
            string what = CanRefreshRandom ? "含随机区域的地图" : "固定世界";
            Debug.Log($"[TerrainLab] seed {seed}  {what} checksum 0x{map.Checksum():X8}  {sw.ElapsedMilliseconds} ms（网格计算 {tCompute} ms，建物体 {tApply} ms）  " +
                      $"triangles {triangles}  ramps {ramps}  slopes {slope / 10f:0.0}%  spawn ({map.SpawnX}, {map.SpawnZ})", this);
        }

        /// <summary>一个 chunk 的计算结果（纯 C#，可以在后台线程算）：外观网格、碰撞盒、斜坡碰撞网格。</summary>
        private sealed class ChunkBuild
        {
            public int Cx, Cz;
            public MeshData Visual;
            public List<ColliderBoxes.Box> Boxes;
            public MeshData Slopes;
            public ProcCliffMesh Proc;        // 用程序化崖壁时：这个 chunk 的网格（Visual 为空）
        }

        /// <summary>TestGround 且打开了程序化崖壁、指定了材质套时，用程序化崖壁画地形。</summary>
        public bool UseProcCliff => (generator == GeneratorKind.TestGround || generator == GeneratorKind.Terrace5x5) &&
                                    procCliff && procCliffSets != null && procCliffSets.Length > 0 && procCliffSets[0] != null;

        /// <summary>只做计算，不碰任何 Unity 对象（所以能并行）。</summary>
        private ChunkBuild ComputeChunk(RegionMap map, RegionVoxels voxels, int cx, int cz, uint useed)
        {
            var b = new ChunkBuild { Cx = cx, Cz = cz };
            if (UseProcCliff)
            {
                b.Proc = ProcCliffMesher.Build(map, generator == GeneratorKind.TestGround && testGround != null ? testGround.Sets : null, cx, cz);
                b.Visual = generator == GeneratorKind.TestGround ? new MeshData() : ChunkMesher.Build(voxels, cx, cz);   // 只用它的水面
            }
            else
                b.Visual = cliffDressing ? CliffMesher.Build(voxels, cx, cz, useed) : ChunkMesher.Build(voxels, cx, cz);
            if (addColliders && (cliffDressing || b.Proc != null))
            {
                b.Boxes = ColliderBoxes.Build(map, cx, cz);
                b.Slopes = CliffMesher.SlopesOnly(voxels, cx, cz);
            }
            return b;
        }

        /// <summary>主线程：把计算结果建成 Unity 的网格和物体。</summary>
        private int ApplyChunk(Transform root, RegionMap map, ChunkBuild b)
        {
            var settings = map.Settings;
            var chunk = new GameObject($"Chunk {b.Cx},{b.Cz}") { hideFlags = HideFlags.DontSave };
            chunk.transform.SetParent(root, false);
            chunk.transform.localPosition = new Vector3(b.Cx * settings.ChunkSize, 0f, b.Cz * settings.ChunkSize);
            chunk.isStatic = true;
            int triangles;
            if (b.Proc != null)
            {
                triangles = AddProcCliff(chunk, b.Proc);
                AddPart(chunk, "Water", b.Visual, true, false);
            }
            else
            {
                AddPart(chunk, "Ground", b.Visual, false, addColliders && !cliffDressing);
                AddPart(chunk, "Water", b.Visual, true, false);
                triangles = b.Visual.TriangleCount;
            }
            if (addColliders && (cliffDressing || b.Proc != null))
                AddBoxColliders(chunk, map, b); // 碰撞仍按 1m 格：同高矩形合并成碰撞盒 + 斜坡小网格
            return triangles;
        }

        /// <summary>
        /// 程序化崖壁：整个 chunk 一个网格（子网格 = 套件 × 部分），只投影的面放在子物体 Shadow 上（ShadowsOnly，屏幕上不画）。返回三角形数（不含只投影的面）。
        /// </summary>
        private int AddProcCliff(GameObject chunk, ProcCliffMesh pm)
        {
            int n = pm.VertexCount;
            if (n == 0) return 0;
            var pos = new Vector3[n]; var nor = new Vector3[n]; var tan = new Vector4[n];
            var d1 = new List<Vector4>(n); var d2 = new List<Vector4>(n);
            for (int i = 0; i < n; i++)
            {
                pos[i] = new Vector3(pm.Positions[i * 3], pm.Positions[i * 3 + 1], pm.Positions[i * 3 + 2]);
                nor[i] = new Vector3(pm.Normals[i * 3], pm.Normals[i * 3 + 1], pm.Normals[i * 3 + 2]);
                tan[i] = new Vector4(pm.Tangents[i * 4], pm.Tangents[i * 4 + 1], pm.Tangents[i * 4 + 2], pm.Tangents[i * 4 + 3]);
                d1.Add(new Vector4(pm.Data1[i * 4], pm.Data1[i * 4 + 1], pm.Data1[i * 4 + 2], pm.Data1[i * 4 + 3]));
                d2.Add(new Vector4(pm.Data2[i * 4], pm.Data2[i * 4 + 1], pm.Data2[i * 4 + 2], pm.Data2[i * 4 + 3]));
            }
            Mesh NewMesh(string name)
            {
                var m = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave, name = name };
                m.vertices = pos; m.normals = nor; m.tangents = tan;
                m.SetUVs(1, d1); m.SetUVs(2, d2);
                return m;
            }
            var visGroups = new List<List<int>>(); var visMats = new List<Material>();
            var shadowIdx = new List<int>(); Material shadowMat = null;
            foreach (var kv in pm.Indices)
            {
                int set = kv.Key / ProcCliffMesh.PartCount;
                var part = (ProcCliffMesh.Part)(kv.Key % ProcCliffMesh.PartCount);
                // 所有套件共用一个合并材质（贴图在数组里，按 Biome 权重图逐像素选套件）；同一部分的组合并成一个子网格
                if (part == ProcCliffMesh.Part.Shadow) { shadowIdx.AddRange(kv.Value); shadowMat = procMat; continue; }
                var mat = procMat;
                int at = visMats.IndexOf(mat);
                if (at < 0) { visGroups.Add(new List<int>(kv.Value)); visMats.Add(mat); }
                else visGroups[at].AddRange(kv.Value);
            }
            long tris = 0;
            var mesh = NewMesh($"{chunk.name} ProcCliff");
            mesh.subMeshCount = visGroups.Count;
            for (int k = 0; k < visGroups.Count; k++) { mesh.SetTriangles(visGroups[k], k, false); tris += visGroups[k].Count / 3; }
            mesh.RecalculateBounds();
            var go = new GameObject("ProcCliff") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(chunk.transform, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = visMats.ToArray();
            renderer.shadowCastingMode = ShadowCastingMode.On;
            if (shadowIdx.Count > 0)
            {
                // 看不见的北 / 东 / 西竖面：只进阴影贴图，保证台地挡光完整（负责人 2026-11-13）
                var sm = NewMesh($"{chunk.name} ProcCliff Shadow");
                sm.SetTriangles(shadowIdx, 0, true);
                var sgo = new GameObject("Shadow") { hideFlags = HideFlags.DontSave };
                sgo.transform.SetParent(go.transform, false);
                sgo.isStatic = true;
                sgo.AddComponent<MeshFilter>().sharedMesh = sm;
                var sr = sgo.AddComponent<MeshRenderer>();
                sr.sharedMaterial = shadowMat;
                sr.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
            return (int)tris;
        }

        /// <summary>
        /// 程序化崖壁的运行时资源：
        /// 1) 把各套贴图在显卡里复制进两个 Texture2DArray（颜色 / 法线）。每套 SetStride 层：0 崖壁、1 泥土、2 顶岩（没有顶岩放泥土）、
        ///    3–18 崖壁变体按格切开（2x2 v1/v2 各 4 格、2x1 v1/v2 各 2 格、1x2 v1/v2 各 2 格；没有变体用崖壁填）。同类贴图尺寸、格式必须一致（导入设置已统一）。
        /// 2) 合并材质：第 0 套 main 的参数 + 数组 + 每套参数（有无顶岩、周期、有无变体）。
        /// 3) Biome 权重图：每格一个像素，RGBA = 套件 0–3 的权重。测试地 = 每格套件编号 one-hot 再按 biomeBlendRadius 模糊；
        ///    5×5 等正式生成器 = 地图自带的 Biome 渐变权重（GDD：32–64m 过渡带）按各套认领的 Biome 加起来。设为全局贴图。
        /// </summary>
        public const int SetStride = 19;
        private void PrepareProcCliff(RegionMap map)
        {
            int n = Mathf.Min(procCliffSets.Length, 4);
            if (procColArr == null || procMat == null || procBuiltFor != procCliffSets)
            {
                DestroySafe(procColArr); DestroySafe(procNrmArr); DestroySafe(procMat);
                var main0 = procCliffSets[0].main;
                var cols = new List<(Texture tex, int cx, int cy)>(); var nrms = new List<(Texture tex, int cx, int cy)>();
                var setP = new Vector4[4];
                for (int i = 0; i < n; i++)
                {
                    var ps = procCliffSets[i] != null ? procCliffSets[i] : procCliffSets[0];
                    var m = ps.main != null ? ps.main : main0;
                    bool rock = m.GetFloat("_UseTopRock") > 0.5f && m.GetTexture("_TopRockMap") != null;
                    Texture cliffC = m.GetTexture("_CliffMap"), cliffN = m.GetTexture("_CliffNormal");
                    cols.Add((cliffC, -1, 0)); cols.Add((m.GetTexture("_EarthMap"), -1, 0)); cols.Add((rock ? m.GetTexture("_TopRockMap") : m.GetTexture("_EarthMap"), -1, 0));
                    nrms.Add((cliffN, -1, 0)); nrms.Add((m.GetTexture("_EarthNormal"), -1, 0)); nrms.Add((rock ? m.GetTexture("_TopRockNormal") : m.GetTexture("_EarthNormal"), -1, 0));
                    bool vars = ps.variantColors != null && ps.variantColors.Length >= 6 && ps.variantNormals != null && ps.variantNormals.Length >= 6;
                    // 变体按格切：2x2（4 格，下排 x0 x1、上排 x0 x1）、2x1（2 格，左右）、1x2（2 格，下上）
                    int[] vi = { 0, 1, 2, 3, 4, 5 };
                    foreach (int v in vi)
                    {
                        int w = v < 2 ? 2 : v < 4 ? 2 : 1, h = v < 2 ? 2 : v < 4 ? 1 : 2;
                        for (int cy = 0; cy < h; cy++)
                            for (int cx = 0; cx < w; cx++)
                            {
                                cols.Add(vars ? (ps.variantColors[v], cx, cy) : (cliffC, -1, 0));
                                nrms.Add(vars ? (ps.variantNormals[v], cx, cy) : (cliffN, -1, 0));
                            }
                    }
                    setP[i] = new Vector4(rock ? 1 : 0, m.GetFloat("_TopRockPeriod"), m.GetFloat("_EarthPeriod"), vars ? 1 : 0);
                }
                procColArr = BuildArray(cols, false, "ProcCliff Color");
                procNrmArr = BuildArray(nrms, true, "ProcCliff Normal");
                procMat = new Material(main0) { name = "ProcCliff (runtime)", hideFlags = HideFlags.DontSave };
                procMat.SetTexture("_ColArr", procColArr); procMat.SetTexture("_NrmArr", procNrmArr);
                for (int i = 0; i < 4; i++) procMat.SetVector("_SetP" + i, setP[Mathf.Min(i, n - 1)]);
                procMat.SetFloat("_SetCount", n);
                procMat.SetFloat("_ArrSize", procColArr != null ? procColArr.width : 512);
                procBuiltFor = procCliffSets;
            }

            // Biome 权重图（每次重建：体素笔刷会改套件）
            int W = map.W, Hh = map.H;
            var wts = new float[W * Hh * 4];
            if (generator == GeneratorKind.TestGround)
            {
                var sets = testGround != null ? testGround.Sets : null;
                bool ok = sets != null && sets.Length == W * Hh;
                for (int i = 0; i < W * Hh; i++) wts[i * 4 + Mathf.Clamp(ok ? sets[i] : 0, 0, n - 1)] = 1;
                if (biomeBlendRadius > 0)
                    for (int pass = 0; pass < 2; pass++) { BoxBlur(wts, W, Hh, biomeBlendRadius, true); BoxBlur(wts, W, Hh, biomeBlendRadius, false); }
            }
            else
            {
                // 每个 Biome 归哪一套（没有套认领的归第 0 套）
                int nb = map.Weights.Length;
                var owner = new int[nb];
                for (int i = 0; i < n; i++)
                    if (procCliffSets[i] != null && procCliffSets[i].biomeIds != null)
                        foreach (int b in procCliffSets[i].biomeIds)
                            if (b >= 0 && b < nb && owner[b] == 0) owner[b] = i;
                for (int b = 0; b < nb; b++)
                {
                    var wb = map.Weights[b];
                    if (wb == null) continue;
                    for (int i = 0; i < W * Hh; i++) wts[i * 4 + owner[b]] += wb[i];
                }
                for (int i = 0; i < W * Hh; i++)
                {
                    float t = wts[i * 4] + wts[i * 4 + 1] + wts[i * 4 + 2] + wts[i * 4 + 3];
                    if (t > 1e-5f) for (int c = 0; c < 4; c++) wts[i * 4 + c] /= t; else wts[i * 4] = 1;
                }
                if (biomeBlendRadiusWorld > 0)
                    for (int pass = 0; pass < 2; pass++) { BoxBlur(wts, W, Hh, biomeBlendRadiusWorld, true); BoxBlur(wts, W, Hh, biomeBlendRadiusWorld, false); }
            }
            if (biomeMap == null || biomeMap.width != W || biomeMap.height != Hh)
            {
                DestroySafe(biomeMap);
                biomeMap = new Texture2D(W, Hh, TextureFormat.RGBA32, false, true)
                    { name = "Biome Weights", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            }
            var px = new Color32[W * Hh];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)Mathf.RoundToInt(wts[i * 4] * 255), (byte)Mathf.RoundToInt(wts[i * 4 + 1] * 255),
                                    (byte)Mathf.RoundToInt(wts[i * 4 + 2] * 255), (byte)Mathf.RoundToInt(wts[i * 4 + 3] * 255));
            biomeMap.SetPixels32(px);   // 行优先 z * W + x，正好是贴图的 (x, y)
            biomeMap.Apply(false);
            Shader.SetGlobalTexture("_WortBiomeMap", biomeMap);
            Shader.SetGlobalVector("_WortBiomeRect", new Vector4(0, 0, W, Hh));
            Shader.SetGlobalFloat("_WortBiomeOn", 1);
        }

        /// <summary>
        /// 在显卡里把贴图逐层逐 mip 复制进数组。cx = -1 表示整张；否则从大贴图里取第 (cx, cy) 格（左下起，每格 = 第一张的尺寸）。
        /// 压缩格式按 4×4 块复制，比一块还小的 mip 从第一张整张复制（只在极远处用到）。不一致的贴图报错并用第一张代替。
        /// </summary>
        private Texture2DArray BuildArray(List<(Texture tex, int cx, int cy)> list, bool linear, string name)
        {
            var first = list[0].tex as Texture2D;
            if (first == null) { Debug.LogError($"[TerrainLab] {name}：第 0 套缺贴图", this); return null; }
            int S = first.width, mips = first.mipmapCount;
            var arr = new Texture2DArray(S, first.height, list.Count, first.format, mips > 1, linear)
                { name = name, hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            for (int i = 0; i < list.Count; i++)
            {
                var (tex, cx, cy) = list[i];
                var t = tex as Texture2D;
                bool cell = cx >= 0;
                bool ok = t != null && t.format == first.format &&
                          (cell ? t.width >= (cx + 1) * S && t.height >= (cy + 1) * first.height : t.width == S && t.height == first.height && t.mipmapCount == mips);
                if (!ok)
                {
                    Debug.LogError($"[TerrainLab] {name} 第 {i} 层（{(t != null ? t.name : "空")}）尺寸 / 格式和第一张（{first.name} {S}×{first.height} {first.format}）不一致，先用第一张代替。请统一导入设置。", this);
                    t = first; cell = false;
                }
                for (int mip = 0; mip < mips; mip++)
                {
                    int ms = Mathf.Max(1, S >> mip);
                    if (!cell) Graphics.CopyTexture(t, 0, mip, arr, i, mip);
                    else if (ms >= 4 && mip < t.mipmapCount) Graphics.CopyTexture(t, 0, mip, cx * ms, cy * ms, ms, ms, arr, i, mip, 0, 0);
                    else Graphics.CopyTexture(first, 0, mip, arr, i, mip);
                }
            }
            return arr;
        }

        /// <summary>RGBA 权重图的一维方框模糊（边缘按边上的值延伸）。</summary>
        private static void BoxBlur(float[] w, int W, int H, int r, bool alongX)
        {
            int len = alongX ? W : H, lines = alongX ? H : W;
            var line = new float[len * 4]; var acc = new float[4];
            for (int l = 0; l < lines; l++)
            {
                for (int i = 0; i < len; i++) { int idx = (alongX ? l * W + i : i * W + l) * 4; for (int c = 0; c < 4; c++) line[i * 4 + c] = w[idx + c]; }
                for (int c = 0; c < 4; c++) acc[c] = 0;
                for (int k = -r; k <= r; k++) { int j = Mathf.Clamp(k, 0, len - 1); for (int c = 0; c < 4; c++) acc[c] += line[j * 4 + c]; }
                for (int i = 0; i < len; i++)
                {
                    int idx = (alongX ? l * W + i : i * W + l) * 4;
                    for (int c = 0; c < 4; c++) w[idx + c] = acc[c] / (2 * r + 1);
                    int add = Mathf.Clamp(i + r + 1, 0, len - 1), sub = Mathf.Clamp(i - r, 0, len - 1);
                    for (int c = 0; c < 4; c++) acc[c] += line[add * 4 + c] - line[sub * 4 + c];
                }
            }
        }

        /// <summary>
        /// 重建一批 chunk：先删旧的，网格 / 碰撞的计算在后台线程并行（生成核心是纯 C#），再在主线程依次建 Unity 物体。
        /// 返回三角形总数；tCompute / tApply 是两段各自的耗时（ms）。
        /// </summary>
        private int BuildChunks(Transform root, RegionMap map, RegionVoxels voxels, IList<(int cx, int cz)> list, uint useed,
            out long tCompute, out long tApply)
        {
            foreach (var (cx, cz) in list)
            {
                var old = root.Find($"Chunk {cx},{cz}");
                if (old == null) continue;
                foreach (var filter in old.GetComponentsInChildren<MeshFilter>(true))
                    DestroySafe(filter.sharedMesh);
                DestroySafe(old.gameObject);
            }
            var sw = Stopwatch.StartNew();
            // 共用的缓存先在主线程算好，避免多个线程同时去算
            if (UseProcCliff) PrepareProcCliff(map);
            if (voxels.RoadMask == null) voxels.RoadMask = RoadSurface.BuildMask(map);
            if (voxels.RoadMouths == null) voxels.RoadMouths = RoadSurface.BuildMouths(map);
            var builds = new ChunkBuild[list.Count];
            System.Threading.Tasks.Parallel.For(0, list.Count, k => builds[k] = ComputeChunk(map, voxels, list[k].cx, list[k].cz, useed));
            tCompute = sw.ElapsedMilliseconds;
            int triangles = 0;
            foreach (var b in builds)
                triangles += ApplyChunk(root, map, b);
            tApply = sw.ElapsedMilliseconds - tCompute;
            return triangles;
        }

        // ---- 随机 chunk（5×5 右上角，chunk 3,3）----
        static readonly List<(int cx, int cz)> RandomChunks = new List<(int, int)> { (3, 3) };
        private uint bakedSeed;
        private RegionMap baked;
        private byte[] bakedCls;
        private List<RegionEntrance> candidates;

        // 按刷新 ID 重放时用的刷新种子（只对 replayWorldSeed 这个世界种子有效）；“重新生成随机 chunk”会清掉它
        private uint? replaySeed;
        private uint replayWorldSeed;

        public bool CanRefreshRandom => generator == GeneratorKind.Terrace5x5 && randomChunk;
        public int RefreshIndex => refreshIndex;
        public string LastRefreshReport { get; private set; }
        /// <summary>最近一次随机刷新的 ID（世界种子:刷新种子），可复制后用“按 ID 重放”或预览工具 --rid 复现。</summary>
        public string LastRefreshId { get; private set; }
        public bool IsReplaying => replaySeed.HasValue;

        /// <summary>固定世界只烘焙一次（同一种子缓存），随机区域按 refreshIndex 生成。</summary>
        private RegionMap RefreshRandom(uint useed, out RandomRegionResult result)
        {
            if (baked == null || bakedSeed != useed)
            {
                baked = TerraceField.Generate(new TerraceFieldSettings { Seed = useed, RandomChunks = RandomChunks }, out bakedCls);
                candidates = RandomRegion.AutoCandidates(baked, bakedCls, RandomChunks, useed);
                // 连通测试：出生点到每个候选出入口铺路（路面显示），铺不通的打印警告
                foreach (var line in RandomRegion.ConnectFixed(baked, bakedCls, RandomChunks, candidates))
                    if (line.Contains("铺不通")) Debug.LogWarning("[TerrainLab] " + line, this); else Debug.Log("[TerrainLab] " + line, this);
                bakedSeed = useed;
            }
            if (replaySeed.HasValue && replayWorldSeed != useed)
                replaySeed = null;
            uint rs = replaySeed ?? RandomRegion.RefreshSeed(useed, 1, refreshIndex);
            var sw = Stopwatch.StartNew();
            result = RandomRegion.RefreshWithSeed(baked, bakedCls, RandomChunks, candidates, rs, new RandomRegionSettings());
            LastRefreshId = RandomRegion.FormatId(useed, rs);
            string which = replaySeed.HasValue ? "重放" : $"刷新 {refreshIndex + 1}";
            LastRefreshReport = $"{which}  ID {LastRefreshId}：{(result.Ok ? "通过" : "失败")}，尝试 {result.Attempts} 次，区域生成 {sw.ElapsedMilliseconds} ms，checksum 0x{result.Map.Checksum():X8}";
            if (result.AttemptLog.Count > 0)
                LastRefreshReport += "\n" + string.Join("\n", result.AttemptLog);
            if (!result.Ok)
                Debug.LogWarning($"[TerrainLab] 随机 chunk 校验失败（ID {LastRefreshId}）：\n" + string.Join("\n", result.Report), this);
            return result.Map;
        }

        /// <summary>
        /// 重新生成随机 chunk：刷新序号 +1，只重建随机 chunk 和它周围一圈（3×3 个 chunk），角色不动。
        /// Console 打印区域生成、体素、外观网格 + 碰撞各自的耗时。
        /// </summary>
        public void RegenerateRandomChunk()
        {
            if (!CanRefreshRandom)
                return;
            replaySeed = null;
            if (transform.Find(RootName) == null || LastMap == null)
            {
                Generate();
                return;
            }
            refreshIndex++;
            RebuildRandomChunks();
        }

        /// <summary>
        /// 按刷新 ID（如 <c>3:9F2A61C0</c>）重放某次随机刷新。ID 里的世界种子和当前不同时，先换种子并整张重新生成。
        /// </summary>
        public bool ReplayRefresh(string id, out string error)
        {
            error = null;
            if (!CanRefreshRandom) { error = "需要 Terrace5x5 并勾选随机 chunk"; return false; }
            if (!RandomRegion.TryParseId(id, out uint ws, out uint rs)) { error = "格式应为 世界种子:8 位十六进制，如 3:9F2A61C0"; return false; }
            replaySeed = rs;
            replayWorldSeed = ws;
            if (unchecked((uint)seed) != ws || transform.Find(RootName) == null || LastMap == null)
            {
                seed = unchecked((int)ws);
                Generate();
                return true;
            }
            RebuildRandomChunks();
            return true;
        }

        private void RebuildRandomChunks()
        {
            var root = transform.Find(RootName);
            uint useed = unchecked((uint)seed);
            var swAll = Stopwatch.StartNew();
            var oldMap = LastMap;
            var map = RefreshRandom(useed, out lastRandom);
            ApplyRoadOverlay(map);
            long tRegion = swAll.ElapsedMilliseconds;
            var voxels = new RegionVoxels(map);
            lastVoxels = voxels;
            long tVoxels = swAll.ElapsedMilliseconds - tRegion;
            EnsureMaterials();
            // 要重建的 chunk：随机 chunk 本身和上下左右 4 个（接缝带就在它们旁边）；
            // 斜角的 4 个只被随机 chunk 角上那一格影响（网格只看相邻 1 格），那一格这次没变就不重建
            var (rcx, rcz) = RandomChunks[0];
            int size = map.Settings.ChunkSize;
            var list = new List<(int cx, int cz)>();
            for (int cz = rcz - 1; cz <= rcz + 1; cz++)
                for (int cx = rcx - 1; cx <= rcx + 1; cx++)
                {
                    if (cx < 0 || cz < 0 || cx >= map.Settings.ChunksX || cz >= map.Settings.ChunksZ)
                        continue;
                    bool diagonal = cx != rcx && cz != rcz;
                    if (diagonal && oldMap != null && oldMap.W == map.W && oldMap.H == map.H)
                    {
                        int kx = cx < rcx ? rcx * size : rcx * size + size - 1, kz = cz < rcz ? rcz * size : rcz * size + size - 1;
                        int i = map.Index(kx, kz);
                        bool same = oldMap.Height[i] == map.Height[i] && oldMap.Slope[i] == map.Slope[i]
                            && oldMap.Water[i] == map.Water[i] && oldMap.Flags[i] == map.Flags[i];
                        if (same) continue;
                    }
                    list.Add((cx, cz));
                }
            BuildChunks(root, map, voxels, list, useed, out long tCompute, out long tApply);
            BuildRoomHints(root);
            BuildClimbs(root);
            long tMesh = swAll.ElapsedMilliseconds - tRegion - tVoxels;
            LastMap = map;
            LastMilliseconds = swAll.ElapsedMilliseconds;
            Debug.Log($"[TerrainLab] {LastRefreshReport}；体素 {tVoxels} ms；外观网格 + 碰撞 {tMesh} ms（网格计算 {tCompute} ms，并行；建物体 {tApply} ms；重建 {list.Count} 个 chunk）；合计 {swAll.ElapsedMilliseconds} ms", this);
        }

        // ---- 房间提示（随机区域） ----
        public bool ShowRoomHints
        {
            get => showRoomHints;
            set { showRoomHints = value; RefreshRoomHints(); }
        }

        /// <summary>按当前开关重建房间提示层（Inspector 改了开关时调用）。</summary>
        public void RefreshRoomHints()
        {
            var root = transform.Find(RootName);
            if (root != null) BuildRoomHints(root);
        }

        private static readonly Color32[] RoomHues =
        {
            new Color32(0x4F, 0xC3, 0xF7, 0), new Color32(0xFF, 0xB7, 0x4D, 0), new Color32(0x81, 0xC7, 0x84, 0), new Color32(0xF0, 0x62, 0x92, 0),
            new Color32(0xBA, 0x68, 0xC8, 0), new Color32(0xFF, 0xF1, 0x76, 0), new Color32(0x4D, 0xB6, 0xAC, 0), new Color32(0xFF, 0x8A, 0x65, 0),
        };

        /// <summary>重建房间提示层（显示关闭或没有随机区域时只删掉旧的）。</summary>
        private void BuildRoomHints(Transform root)
        {
            var old = root.Find("Room Hints");
            if (old != null)
            {
                var f = old.GetComponent<MeshFilter>();
                if (f != null) DestroySafe(f.sharedMesh);
                DestroySafe(old.gameObject);
            }
            if (!showRoomHints || lastRandom == null || lastRandom.RoomOf == null)
                return;
            if (roomHintMaterial == null)
            {
                var shader = Shader.Find("Wort/RoomOverlay");
                if (shader == null) { Debug.LogWarning("[TerrainLab] 找不到 Wort/RoomOverlay 着色器，房间提示不显示。", this); return; }
                roomHintMaterial = new Material(shader) { name = "Room Hints", hideFlags = HideFlags.DontSave };
            }
            var map = lastRandom.Map;
            var zones = RandomRegion.RoomZones(lastRandom);
            var verts = new List<Vector3>(); var cols = new List<Color32>(); var tris = new List<int>();
            void Quad(float x0, float z0, float x1, float z1, float y, Color32 c)
            {
                int v = verts.Count;
                verts.Add(new Vector3(x0, y, z0)); verts.Add(new Vector3(x0, y, z1)); verts.Add(new Vector3(x1, y, z1)); verts.Add(new Vector3(x1, y, z0));
                for (int k = 0; k < 4; k++) cols.Add(c);
                tris.Add(v); tris.Add(v + 1); tris.Add(v + 2); tris.Add(v); tris.Add(v + 2); tris.Add(v + 3);
            }
            const float Lift = 0.03f, Edge = 0.07f;
            int W = map.W;
            for (int i = 0; i < zones.Length; i++)
            {
                if (zones[i] == 0) continue;
                int x = i % W, z = i / W, room = lastRandom.RoomOf[i];
                var hue = RoomHues[((room % RoomHues.Length) + RoomHues.Length) % RoomHues.Length];
                float y = map.Height[i] + Lift;
                var fill = new Color32(hue.r, hue.g, hue.b, 64);
                Quad(x, z, x + 1, z + 1, y, fill);
                if (zones[i] == 2) Quad(x, z, x + 1, z + 1, y + 0.01f, fill); // 内收 1 格（台地可生成）：高 1cm 再叠一层，颜色更深
                // 描边：和不是同一房间平地的邻格之间的边
                var edge = new Color32(hue.r, hue.g, hue.b, 255);
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + Wort.Generation.Grid.DX4[d], nz = z + Wort.Generation.Grid.DZ4[d];
                    bool same = map.InBounds(nx, nz) && zones[nz * W + nx] > 0 && lastRandom.RoomOf[nz * W + nx] == room;
                    if (same) continue;
                    if (d == 0) Quad(x, z + 1 - Edge, x + 1, z + 1, y + 0.005f, edge);
                    else if (d == 1) Quad(x + 1 - Edge, z, x + 1, z + 1, y + 0.005f, edge);
                    else if (d == 2) Quad(x, z, x + 1, z + Edge, y + 0.005f, edge);
                    else Quad(x, z, x + Edge, z + 1, y + 0.005f, edge);
                }
            }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave, name = "Room Hints" };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            var go = new GameObject("Room Hints") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = roomHintMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // ---- 藤蔓绳（占位模型：只显示，不加碰撞；攀爬操作以后做） ----
        private void BuildClimbs(Transform root)
        {
            var old = root.Find("Vine Ropes");
            if (old != null) DestroySafe(old.gameObject);
            if (lastRandom == null || lastRandom.Climbs == null || lastRandom.Climbs.Count == 0)
                return;
            if (vineMaterial == null || stakeMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                vineMaterial = new Material(shader) { name = "Vine Rope", hideFlags = HideFlags.DontSave };
                vineMaterial.SetColor("_BaseColor", new Color(0.24f, 0.45f, 0.18f));
                stakeMaterial = new Material(shader) { name = "Vine Stake", hideFlags = HideFlags.DontSave };
                stakeMaterial.SetColor("_BaseColor", new Color(0.42f, 0.3f, 0.18f));
            }
            var group = new GameObject("Vine Ropes") { hideFlags = HideFlags.DontSave };
            group.transform.SetParent(root, false);
            foreach (var c in lastRandom.Climbs)
            {
                float dx = Wort.Generation.Grid.DX4[c.Dir], dz = Wort.Generation.Grid.DZ4[c.Dir];
                // 绳子贴着高格朝 Dir 那面的崖边垂下（往外 0.06m，避开崖面外鼓），从低处地面到崖顶
                float ex = c.X + 0.5f + dx * 0.56f, ez = c.Z + 0.5f + dz * 0.56f;
                float len = c.Top - c.Bottom;
                var rope = Primitive(PrimitiveType.Cylinder, group.transform, vineMaterial, "Vine Rope");
                rope.transform.localPosition = new Vector3(ex, c.Bottom + len * 0.5f, ez);
                rope.transform.localScale = new Vector3(0.08f, len * 0.5f, 0.08f); // 圆柱默认高 2m
                // 顶上的固定点：崖顶边上一个小木桩
                var stake = Primitive(PrimitiveType.Cube, group.transform, stakeMaterial, "Vine Stake");
                stake.transform.localPosition = new Vector3(c.X + 0.5f + dx * 0.32f, c.Top + 0.12f, c.Z + 0.5f + dz * 0.32f);
                stake.transform.localScale = new Vector3(0.14f, 0.24f, 0.14f);
            }
        }

        private GameObject Primitive(PrimitiveType type, Transform parent, Material mat, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            var col = go.GetComponent<Collider>();
            if (col != null) DestroySafe(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.SetParent(parent, false);
            return go;
        }

        // ---- 道路覆盖层与道路笔刷 ----
        public RoadOverlayAsset RoadOverlay { get => roadOverlay; set => roadOverlay = value; }
        public GeneratorKind Generator => generator;

        /// <summary>覆盖层是否适用于当前种子和生成器。</summary>
        public bool RoadOverlayMatches => roadOverlay != null && roadOverlay.seed == seed && roadOverlay.generator == generator.ToString();

        private void ApplyRoadOverlay(RegionMap map)
        {
            if (roadOverlay == null || roadOverlay.Count == 0)
                return;
            if (!RoadOverlayMatches)
            {
                Debug.LogWarning($"[TerrainLab] 道路覆盖层 {roadOverlay.name} 对应种子 {roadOverlay.seed} / {roadOverlay.generator}，和当前不一致，没有叠加。", this);
                return;
            }
            int n = roadOverlay.ApplyTo(map).Count;
            Debug.Log($"[TerrainLab] 叠加道路覆盖层 {roadOverlay.name}：{roadOverlay.Count} 条记录，改动 {n} 格（checksum 因此和不叠加时不同）", this);
        }

        /// <summary>世界空间射线找地面上的点（区域坐标 x / z）。用地图高度逐步检测，不依赖碰撞体。</summary>
        public bool PickGround(Ray worldRay, out Vector2 cell)
        {
            cell = default;
            if (LastMap == null)
                return false;
            var o = transform.InverseTransformPoint(worldRay.origin);
            var d = transform.InverseTransformDirection(worldRay.direction);
            if (!Wort.Generation.RoadOverlay.Pick(LastMap, o.x, o.y, o.z, d.x, d.y, d.z, 2000f, out float hx, out float hz))
                return false;
            cell = new Vector2(hx, hz);
            return true;
        }

        /// <summary>区域坐标 → 世界坐标（地面顶上），给 Scene 视图画笔刷圈用。</summary>
        public Vector3 GroundToWorld(Vector2 p)
        {
            float y = 0f;
            if (LastMap != null)
            {
                int cx = Mathf.Clamp(Mathf.FloorToInt(p.x), 0, LastMap.W - 1), cz = Mathf.Clamp(Mathf.FloorToInt(p.y), 0, LastMap.H - 1);
                y = LastMap.Height[LastMap.Index(cx, cz)] + (LastMap.Slope[LastMap.Index(cx, cz)] >= 0 ? 0.5f : 0f);
            }
            return transform.TransformPoint(new Vector3(p.x, y + 0.02f, p.y));
        }

        /// <summary>
        /// 道路笔刷：在 p（区域坐标）处按宽度刷一笔，写进覆盖层并立即改地图；返回改动的格（未重建网格，见 RebuildRoadChunks）。
        /// kind：0 擦除，1 小路，2 次干道，3 主干道。
        /// </summary>
        public List<int> PaintRoad(Vector2 p, int width, byte kind)
        {
            var changed = new List<int>();
            if (LastMap == null || roadOverlay == null)
                return changed;
            var xs = new List<int>(); var zs = new List<int>(); var ks = new List<byte>();
            foreach (var (x, z) in Wort.Generation.RoadOverlay.Footprint(p.x, p.y, width))
            {
                if (!LastMap.InBounds(x, z))
                    continue;
                roadOverlay.Set(x, z, kind);
                xs.Add(x); zs.Add(z); ks.Add(kind);
            }
            changed.AddRange(Wort.Generation.RoadOverlay.Apply(LastMap, xs, zs, ks));
            return changed;
        }

        // ---- 空白测试地与体素笔刷（Minecraft 式） ----
        public TestGroundAsset TestGroundData { get => testGround; set => testGround = value; }
        public ProcCliffSet[] ProcCliffSets { get => procCliffSets; set => procCliffSets = value; }
        public bool ProcCliff { get => procCliff; set => procCliff = value; }
        public bool IsTestGround => generator == GeneratorKind.TestGround;

        /// <summary>世界空间射线打体素面（高度场 DDA，见 VoxelPick）。</summary>
        public bool RaycastVoxel(Ray worldRay, out VoxelHit hit)
        {
            hit = default;
            if (LastMap == null)
                return false;
            var o = transform.InverseTransformPoint(worldRay.origin);
            var d = transform.InverseTransformDirection(worldRay.direction);
            return VoxelPick.Raycast(LastMap, o.x, o.y, o.z, d.x, d.y, d.z, 2000f, out hit);
        }

        /// <summary>区域坐标 → 世界坐标。</summary>
        public Vector3 RegionToWorld(Vector3 p) => transform.TransformPoint(p);

        /// <summary>改一批格的高度（写进 TestGround 数据和当前地图，不重建网格）；返回真的变了的格。</summary>
        public List<int> SetColumnHeights(IList<(int x, int z, int h)> edits)
        {
            var changed = new List<int>();
            if (LastMap == null || testGround == null || !IsTestGround)
                return changed;
            var hs = testGround.Heights;
            foreach (var (x, z, h0) in edits)
            {
                if (!LastMap.InBounds(x, z)) continue;
                int i = LastMap.Index(x, z), h = Mathf.Clamp(h0, 0, 60);
                if (LastMap.Height[i] == h) continue;
                LastMap.Height[i] = h;
                hs[i] = (byte)h;
                changed.Add(i);
            }
            return changed;
        }

        /// <summary>给一批格刷套件编号；返回变了的格。</summary>
        public List<int> PaintTileset(IEnumerable<(int x, int z)> cells, byte set)
        {
            var changed = new List<int>();
            if (LastMap == null || testGround == null || !IsTestGround)
                return changed;
            var ss = testGround.Sets;
            foreach (var (x, z) in cells)
            {
                if (!LastMap.InBounds(x, z)) continue;
                int i = LastMap.Index(x, z);
                if (ss[i] == set) continue;
                ss[i] = set;
                changed.Add(i);
            }
            return changed;
        }

        /// <summary>重建这些格（及其周围 1 格）所在的 chunk。heightsChanged 时体素也重算（碰撞盒、斜坡按新高度）。</summary>
        public int RebuildCells(ICollection<int> cells, bool heightsChanged)
        {
            if (heightsChanged && LastMap != null)
            {
                lastVoxels = new RegionVoxels(LastMap);
                LastMap.HeightRange(out _, out int max);
                LastMap.Stats["maxHeight"] = max;
            }
            return RebuildRoadChunks(cells);
        }

        /// <summary>重建这些格（及其周围 1 格）所在的 chunk：路面权重取周围格，跨 chunk 边时两边都要重建。</summary>
        public int RebuildRoadChunks(ICollection<int> cells)
        {
            var root = transform.Find(RootName);
            if (root == null || LastMap == null || cells.Count == 0)
                return 0;
            var map = LastMap;
            int size = map.Settings.ChunkSize;
            var dirty = new HashSet<(int, int)>();
            foreach (int i in cells)
            {
                int x = i % map.W, z = i / map.W;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ax = x + dx, az = z + dz;
                        if (map.InBounds(ax, az))
                            dirty.Add((ax / size, az / size));
                    }
            }
            if (lastVoxels == null || lastVoxels.Map != map)
                lastVoxels = new RegionVoxels(map);
            lastVoxels.RoadMask = null; // 路变了，遮罩重算（坡道口不变）
            EnsureMaterials();
            uint useed = unchecked((uint)seed);
            BuildChunks(root, map, lastVoxels, new List<(int cx, int cz)>(dirty), useed, out _, out _);
            return dirty.Count;
        }

        public void Clear()
        {
            var root = transform.Find(RootName);
            if (root == null)
                return;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                DestroySafe(filter.sharedMesh);
            DestroySafe(root.gameObject);
            LastMap = null;
        }

        private void AddPart(GameObject chunk, string name, MeshData data, bool water, bool collider)
        {
            var mesh = BuildMesh(data, water, out var used);
            if (mesh == null)
                return;
            mesh.name = $"{chunk.name} {name}";
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(chunk.transform, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = used;
            renderer.shadowCastingMode = water ? ShadowCastingMode.Off : ShadowCastingMode.On;
            if (collider)
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void AddBoxColliders(GameObject chunk, RegionMap map, ChunkBuild b)
        {
            int cx = b.Cx, cz = b.Cz;
            var go = new GameObject("Collider") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(chunk.transform, false);
            go.isStatic = true;
            map.HeightRange(out int minH, out _);
            float bottom = minH - 2;
            int ox = cx * map.Settings.ChunkSize, oz = cz * map.Settings.ChunkSize;
            foreach (var bx in b.Boxes)
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = new Vector3((bx.X0 + bx.X1) * 0.5f - ox, (bottom + bx.Top) * 0.5f, (bx.Z0 + bx.Z1) * 0.5f - oz);
                box.size = new Vector3(bx.X1 - bx.X0, bx.Top - bottom, bx.Z1 - bx.Z0);
            }
            var slopes = BuildMesh(b.Slopes, false, out _);
            if (slopes != null)
            {
                slopes.name = $"{chunk.name} Slope Collider";
                go.AddComponent<MeshFilter>().sharedMesh = slopes; // 只为 Clear() 能找到并销毁网格；没有 MeshRenderer，不渲染
                go.AddComponent<MeshCollider>().sharedMesh = slopes;
            }
        }

        /// <summary>MeshData → Mesh。water 为 true 时只取水面子网格，否则取除水以外的全部。</summary>
        private Mesh BuildMesh(MeshData data, bool water, out Material[] used)
        {
            var subs = new List<int>();
            for (int m = 0; m < data.Indices.Length; m++)
            {
                bool isWater = m == (int)VoxelMaterial.Water;
                if (isWater == water && data.Indices[m].Count > 0)
                    subs.Add(m);
            }
            used = null;
            if (subs.Count == 0)
                return null;

            int count = data.VertexCount;
            var vertices = new List<Vector3>(count);
            var normals = new List<Vector3>(count);
            var p = data.Positions;
            var n = data.Normals;
            for (int i = 0; i < count; i++)
            {
                vertices.Add(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
                normals.Add(new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]));
            }

            var mesh = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            bool blended = !water && data.Blend.Count == count && blendMaterial != null;
            if (blended)
            {
                // r = 岩石融合权重，g = 路面权重（RoadSurface）
                bool road = data.Road.Count == count;
                var colors = new List<Color32>(count);
                for (int i = 0; i < count; i++)
                    colors.Add(new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(data.Blend[i]) * 255f),
                        road ? (byte)Mathf.RoundToInt(Mathf.Clamp01(data.Road[i]) * 255f) : (byte)0, 0, 255));
                mesh.SetColors(colors);
            }
            mesh.subMeshCount = subs.Count;
            used = new Material[subs.Count];
            for (int k = 0; k < subs.Count; k++)
            {
                mesh.SetTriangles(data.Indices[subs[k]], k, false);
                used[k] = blended ? blendMaterial : materials[subs[k]];
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>按 VoxelPalette 建纯色 URP 材质（GDD 16：只用纯色材质）。</summary>
        private void EnsureMaterials()
        {
            EnsureBlendMaterial();
            int count = (int)VoxelMaterial.Count;
            if (materials != null && materials.Length == count && materials[1] != null)
                return;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            materials = new Material[count];
            for (int m = 1; m < count; m++)
            {
                var vm = (VoxelMaterial)m;
                uint c = VoxelPalette.Color(vm);
                var color = (Color)new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
                var mat = new Material(shader) { name = "Voxel " + vm, hideFlags = HideFlags.DontSave };
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Smoothness", vm == VoxelMaterial.Water ? 0.6f : 0f);
                materials[m] = mat;
            }
        }

        /// <summary>方案 B 的融合材质：贴图由 TerrainTextures 生成（128×128，A 通道 = 高度），与云端预览一致。</summary>
        private void EnsureBlendMaterial()
        {
            if (blendMaterial != null)
                return;
            var shader = Shader.Find("Wort/TerrainBlend");
            if (shader == null)
            {
                Debug.LogWarning("[TerrainLab] 找不到 Wort/TerrainBlend 着色器，崖面外观改用纯色材质。", this);
                return;
            }
            dirtTexture = MakeTexture("Dirt", TerrainTextures.Dirt(out var dh), dh);
            rockTexture = MakeTexture("Rock", TerrainTextures.Rock(out var rh), rh);
            roadTexture = MakeTexture("Road", TerrainTextures.Road(out var oh), oh);
            blendMaterial = new Material(shader) { name = "Terrain Blend", hideFlags = HideFlags.DontSave };
            blendMaterial.SetTexture("_DirtTex", dirtTexture);
            blendMaterial.SetTexture("_RockTex", rockTexture);
            blendMaterial.SetTexture("_RoadTex", roadTexture);
        }

        private static Texture2D MakeTexture(string name, uint[] rgb, float[] height)
        {
            int n = TerrainTextures.Size;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true)
            {
                name = "Terrain " + name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                uint c = rgb[i];
                px[i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)Mathf.RoundToInt(Mathf.Clamp01(height[i]) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        private void PlacePlayer(RegionMap map)
        {
            if (player == null)
                return;
            int h = map.Height[map.Index(map.SpawnX, map.SpawnZ)];
            var position = transform.TransformPoint(new Vector3(map.SpawnX + 0.5f, h + 0.05f, map.SpawnZ + 0.5f));
            var controller = player.GetComponent<CharacterController>();
            bool wasEnabled = controller != null && controller.enabled;
            if (controller != null)
                controller.enabled = false;
            player.position = position;
            if (controller != null)
                controller.enabled = wasEnabled;

            var cam = Camera.main;
            if (cam != null)
            {
                var c = cam.transform.position;
                cam.transform.position = new Vector3(position.x, c.y, position.z);
            }
        }

        private static void DestroySafe(Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                Destroy(o);
            else
                DestroyImmediate(o);
        }
    }
}
