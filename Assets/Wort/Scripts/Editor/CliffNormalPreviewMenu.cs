using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Wort.Editor
{
    /// <summary>
    /// 菜单 Wort/Debug/Cliff Normal Preview：新建一个不保存的临时场景，对比 TilesetExperiment/ 里的崖壁法线。
    /// 贴图直接从仓库根目录的 TilesetExperiment/ 读（不进 Assets、不生成 .meta），材质也只存在于内存里。
    /// 每套岩壁一排三面墙（每面 3×3 块 1m 瓦片，看平铺接缝），从左到右：
    ///   A = 旧版 Normal_67_Flat（不无缝），法线权重 1；B = 新版 Normal_Periodic512（无缝），权重 1；C = 新版，权重 0.35（现在材质的默认值）。
    /// 三排从上到下：层状 9471、红褐 9473、灰褐 9485。旋转 Directional Light 看换光方向的效果。
    /// </summary>
    public static class CliffNormalPreviewMenu
    {
        private static readonly (string stem, string oldNormal)[] Sets =
        {
            ("IMG_9471", "IMG_9471_Normal_67_Flat512.png"),
            ("IMG_9473", "IMG_9473_Normal_67_Flat511.png"),
            ("IMG_9485", "IMG_9485_Normal_67_Flat511.png"),
        };

        [MenuItem("Wort/Debug/Cliff Normal Preview")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TilesetExperiment"));
            if (!Directory.Exists(dir))
            {
                EditorUtility.DisplayDialog("Wort", "找不到 " + dir, "OK");
                return;
            }
            var shader = Shader.Find("Wort/TileSurface");
            if (shader == null)
            {
                EditorUtility.DisplayDialog("Wort", "找不到着色器 Wort/TileSurface", "OK");
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);

            var lightGo = new GameObject("Directional Light（旋转它看换光）");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(35f, -35f, 0f);
            RenderSettings.sun = light;

            var root = new GameObject("CliffNormalPreview");
            for (int row = 0; row < Sets.Length; row++)
            {
                var (stem, oldName) = Sets[row];
                var baseTex = Load(Path.Combine(dir, stem + "_BaseColor.png"), false);
                var oldN = Load(Path.Combine(dir, oldName), true);
                var newN = Load(Path.Combine(dir, stem + "_Normal_Periodic512.png"), true);
                if (baseTex == null || oldN == null || newN == null)
                    continue;
                float yy = (Sets.Length - 1 - row) * 4f;
                Wall(root.transform, $"{stem} A 旧版 权重1", new Vector3(0f, yy, 0f), Mat(shader, baseTex, oldN, 1f));
                Wall(root.transform, $"{stem} B 新版无缝 权重1", new Vector3(4f, yy, 0f), Mat(shader, baseTex, newN, 1f));
                Wall(root.transform, $"{stem} C 新版无缝 权重0.35", new Vector3(8f, yy, 0f), Mat(shader, baseTex, newN, 0.35f));
            }

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.25f, 0.27f, 0.3f);
            camGo.transform.position = new Vector3(5.5f, 5.5f, -20f);
            camGo.transform.LookAt(new Vector3(5.5f, 5.5f, 0f));

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        private static Texture2D Load(string path, bool linear)
        {
            if (!File.Exists(path))
            {
                Debug.LogError("[CliffNormalPreview] 缺文件 " + path);
                return null;
            }
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = Path.GetFileNameWithoutExtension(path) };
            t.LoadImage(File.ReadAllBytes(path));
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.Apply(true);
            return t;
        }

        private static Material Mat(Shader shader, Texture2D baseTex, Texture2D normal, float weight)
        {
            var m = new Material(shader);
            m.SetTexture("_BaseMap", baseTex);
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            m.SetFloat("_DetailNormal", weight);
            m.SetFloat("_Projection", 0f);
            return m;
        }

        // 3×3 块 1m 竖直瓦片，正面朝 -Z（朝相机）
        private static void Wall(Transform parent, string name, Vector3 origin, Material mat)
        {
            var wall = new GameObject(name).transform;
            wall.SetParent(parent, false);
            wall.localPosition = origin;
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.DestroyImmediate(q.GetComponent<Collider>());
                q.name = $"Tile {x},{y}";
                q.transform.SetParent(wall, false);
                q.transform.localPosition = new Vector3(x + 0.5f, y + 0.5f, 0f);
                q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }
    }
}
