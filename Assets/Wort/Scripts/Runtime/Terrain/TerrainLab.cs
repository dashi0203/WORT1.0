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

        public enum GeneratorKind { Legacy8x8, Terrace5x5 }

        [SerializeField, Tooltip("Legacy8x8 = 旧 8×8 生成器；Terrace5x5 = 体素器第一步（TerraceField，5×5，种子 3 是负责人选定的测试种子）。")]
        private GeneratorKind generator = GeneratorKind.Legacy8x8;

        [SerializeField, Tooltip("Terrace5x5 时启用右上角的随机 chunk（chunk 3,3），用 RandomRegion 生成；Inspector 里有“重新生成随机 chunk”按钮。")]
        private bool randomChunk = true;

        [SerializeField, Tooltip("随机 chunk 的刷新序号（0 起）。每点一次“重新生成随机 chunk”加 1；同一种子、同一序号结果相同。")]
        private int refreshIndex;

        [SerializeField, Tooltip("地形外观方案 B（CliffMesher）：立方体 + 外倒角 / 内倒角、泥土顶面、边缘一圈石头。只改画面，碰撞仍用原来的 1m 体素网格。")]
        private bool cliffDressing = true;

        private Material[] materials;
        private Material blendMaterial;   // Wort/TerrainBlend（方案 B 的泥土 / 岩石融合）
        private Texture2D dirtTexture, rockTexture;

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
            DestroySafe(dirtTexture);
            DestroySafe(rockTexture);
            blendMaterial = null;
        }

        public void Generate()
        {
            Clear();
            var sw = Stopwatch.StartNew();
            uint useed = unchecked((uint)seed);
            RegionMap map;
            if (generator == GeneratorKind.Terrace5x5 && randomChunk)
                map = RefreshRandom(useed, out _);
            else
                map = generator == GeneratorKind.Terrace5x5
                    ? TerraceField.Generate(new TerraceFieldSettings { Seed = useed }, out _)
                    : RegionGenerator.Generate(new RegionSettings { Seed = useed });
            var settings = map.Settings;
            var voxels = new RegionVoxels(map);
            EnsureMaterials();

            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(transform, false);
            int triangles = 0;
            for (int cz = 0; cz < settings.ChunksZ; cz++)
                for (int cx = 0; cx < settings.ChunksX; cx++)
                    triangles += BuildChunk(root.transform, map, voxels, cx, cz, useed);
            sw.Stop();
            LastMap = map;
            LastMilliseconds = sw.ElapsedMilliseconds;
            PlacePlayer(map);

            map.Stats.TryGetValue("slopePermille", out int slope);
            map.Stats.TryGetValue("ramps", out int ramps);
            if (CanRefreshRandom)
                Debug.Log($"[TerrainLab] {LastRefreshReport}", this);
            string what = CanRefreshRandom ? "含随机区域的地图" : "固定世界";
            Debug.Log($"[TerrainLab] seed {seed}  {what} checksum 0x{map.Checksum():X8}  {sw.ElapsedMilliseconds} ms  " +
                      $"triangles {triangles}  ramps {ramps}  slopes {slope / 10f:0.0}%  spawn ({map.SpawnX}, {map.SpawnZ})", this);
        }

        private int BuildChunk(Transform root, RegionMap map, RegionVoxels voxels, int cx, int cz, uint useed)
        {
            var settings = map.Settings;
            var data = ChunkMesher.Build(voxels, cx, cz);
            var visual = cliffDressing ? CliffMesher.Build(voxels, cx, cz, useed) : data;
            var chunk = new GameObject($"Chunk {cx},{cz}") { hideFlags = HideFlags.DontSave };
            chunk.transform.SetParent(root, false);
            chunk.transform.localPosition = new Vector3(cx * settings.ChunkSize, 0f, cz * settings.ChunkSize);
            chunk.isStatic = true;
            AddPart(chunk, "Ground", visual, false, addColliders && !cliffDressing);
            AddPart(chunk, "Water", visual, true, false);
            if (addColliders && cliffDressing)
                AddBoxColliders(chunk, map, voxels, cx, cz); // 碰撞仍按 1m 格：同高矩形合并成碰撞盒 + 斜坡小网格
            return visual.TriangleCount;
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
            var map = RefreshRandom(useed, out _);
            long tRegion = swAll.ElapsedMilliseconds;
            var voxels = new RegionVoxels(map);
            long tVoxels = swAll.ElapsedMilliseconds - tRegion;
            EnsureMaterials();
            var (rcx, rcz) = RandomChunks[0];
            int rebuilt = 0;
            for (int cz = rcz - 1; cz <= rcz + 1; cz++)
                for (int cx = rcx - 1; cx <= rcx + 1; cx++)
                {
                    if (cx < 0 || cz < 0 || cx >= map.Settings.ChunksX || cz >= map.Settings.ChunksZ)
                        continue;
                    var old = root.Find($"Chunk {cx},{cz}");
                    if (old != null)
                    {
                        foreach (var filter in old.GetComponentsInChildren<MeshFilter>(true))
                            DestroySafe(filter.sharedMesh);
                        DestroySafe(old.gameObject);
                    }
                    BuildChunk(root, map, voxels, cx, cz, useed);
                    rebuilt++;
                }
            long tMesh = swAll.ElapsedMilliseconds - tRegion - tVoxels;
            LastMap = map;
            LastMilliseconds = swAll.ElapsedMilliseconds;
            Debug.Log($"[TerrainLab] {LastRefreshReport}；体素 {tVoxels} ms；外观网格 + 碰撞 {tMesh} ms（重建 {rebuilt} 个 chunk）；合计 {swAll.ElapsedMilliseconds} ms", this);
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

        private void AddBoxColliders(GameObject chunk, RegionMap map, RegionVoxels voxels, int cx, int cz)
        {
            var go = new GameObject("Collider") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(chunk.transform, false);
            go.isStatic = true;
            map.HeightRange(out int minH, out _);
            float bottom = minH - 2;
            int ox = cx * map.Settings.ChunkSize, oz = cz * map.Settings.ChunkSize;
            foreach (var b in ColliderBoxes.Build(map, cx, cz))
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = new Vector3((b.X0 + b.X1) * 0.5f - ox, (bottom + b.Top) * 0.5f, (b.Z0 + b.Z1) * 0.5f - oz);
                box.size = new Vector3(b.X1 - b.X0, b.Top - bottom, b.Z1 - b.Z0);
            }
            var slopes = BuildMesh(CliffMesher.SlopesOnly(voxels, cx, cz), false, out _);
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
                var colors = new List<Color32>(count);
                for (int i = 0; i < count; i++)
                    colors.Add(new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(data.Blend[i]) * 255f), 0, 0, 255));
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
            blendMaterial = new Material(shader) { name = "Terrain Blend", hideFlags = HideFlags.DontSave };
            blendMaterial.SetTexture("_DirtTex", dirtTexture);
            blendMaterial.SetTexture("_RockTex", rockTexture);
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
