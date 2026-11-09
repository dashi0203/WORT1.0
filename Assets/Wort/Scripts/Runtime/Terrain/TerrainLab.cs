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

        private Material[] materials;

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
        }

        public void Generate()
        {
            Clear();
            var sw = Stopwatch.StartNew();
            var settings = new RegionSettings { Seed = unchecked((uint)seed) };
            var map = RegionGenerator.Generate(settings);
            var voxels = new RegionVoxels(map);
            EnsureMaterials();

            var root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(transform, false);
            int triangles = 0;
            for (int cz = 0; cz < settings.ChunksZ; cz++)
                for (int cx = 0; cx < settings.ChunksX; cx++)
                {
                    var data = ChunkMesher.Build(voxels, cx, cz);
                    triangles += data.TriangleCount;
                    var chunk = new GameObject($"Chunk {cx},{cz}") { hideFlags = HideFlags.DontSave };
                    chunk.transform.SetParent(root.transform, false);
                    chunk.transform.localPosition = new Vector3(cx * settings.ChunkSize, 0f, cz * settings.ChunkSize);
                    chunk.isStatic = true;
                    AddPart(chunk, "Ground", data, false, addColliders);
                    AddPart(chunk, "Water", data, true, false);
                }
            sw.Stop();
            LastMap = map;
            LastMilliseconds = sw.ElapsedMilliseconds;
            PlacePlayer(map);

            map.Stats.TryGetValue("slopePermille", out int slope);
            map.Stats.TryGetValue("ramps", out int ramps);
            Debug.Log($"[TerrainLab] seed {seed}  checksum 0x{map.Checksum():X8}  {sw.ElapsedMilliseconds} ms  " +
                      $"triangles {triangles}  ramps {ramps}  slopes {slope / 10f:0.0}%  spawn ({map.SpawnX}, {map.SpawnZ})", this);
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
            mesh.subMeshCount = subs.Count;
            used = new Material[subs.Count];
            for (int k = 0; k < subs.Count; k++)
            {
                mesh.SetTriangles(data.Indices[subs[k]], k, false);
                used[k] = materials[subs[k]];
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>按 VoxelPalette 建纯色 URP 材质（GDD 16：只用纯色材质）。</summary>
        private void EnsureMaterials()
        {
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
