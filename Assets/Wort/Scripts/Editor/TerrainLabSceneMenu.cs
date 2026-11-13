using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Wort.Terrain;

namespace Wort.Editor
{
    /// <summary>菜单 Wort/Create Terrain Lab Scene：用代码搭出地形实验场景，避免手写场景 YAML。</summary>
    public static class TerrainLabSceneMenu
    {
        private const string ScenePath = "Assets/Wort/Scenes/TerrainLab.unity";
        private const string ChibiModel = "Assets/Wort/Art/Characters/Chibi/ChibiCharacter.fbx";
        private const string ChibiController = "Assets/Wort/Art/Characters/Chibi/ChibiLocomotion.controller";
        private const string InputActions = "Assets/Wort/Input/InputSystem_Actions.inputactions";
        private const string VolumeProfilePath = "Assets/Wort/Rendering/SampleSceneProfile.asset";

        [MenuItem("Wort/Create Terrain Lab Scene")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("Wort", ScenePath + " 已存在，要覆盖吗？", "覆盖", "取消"))
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 环境光：空场景没有天空盒，用平光
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.62f);

            // 主光：从南偏东上方照来，让朝南（朝相机）的崖面有光
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
            RenderSettings.sun = light;

            // 云影 + 天气光照（Wort.Rendering.SkyLighting）
            SkyLightingEditor.Create();

            var volumeGo = new GameObject("Global Volume");
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);

            var player = CreatePlayer();

            // 错切相机：与 topdowntemp 相同（正交、垂直朝下、shear 1、orthoSize 9.5）
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 9.5f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.14f, 0.19f);
            camGo.transform.SetPositionAndRotation(new Vector3(256f, 80f, 180f), Quaternion.Euler(90f, 0f, 0f));
            camGo.AddComponent<AudioListener>();
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            camGo.AddComponent<WortShearCamera>();
            var follow = camGo.AddComponent<PlanarCameraFollow>();
            SetObject(follow, "target", player.transform);

            var labGo = new GameObject("Terrain Lab");
            var lab = labGo.AddComponent<TerrainLab>();
            SetObject(lab, "player", player.transform);

            if (!AssetDatabase.IsValidFolder("Assets/Wort/Scenes"))
                AssetDatabase.CreateFolder("Assets/Wort", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            // 编辑模式下先生成一次，方便直接在 Scene / Game 视图里看
            try
            {
                EditorUtility.DisplayProgressBar("Wort", "生成 8×8 chunk 地形…", 0.5f);
                lab.Generate();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Selection.activeGameObject = labGo;
        }

        /// <summary>复刻 topdowntemp 的 Chibi Player（缩放、碰撞体、压扁、动画）。</summary>
        private static GameObject CreatePlayer()
        {
            var root = new GameObject("Chibi Player");
            root.transform.localScale = Vector3.one * 1.74f;

            var controller = root.AddComponent<CharacterController>();
            controller.height = 0.76f;
            controller.radius = 0.27f;
            controller.center = new Vector3(0f, 0.38f, 0f);
            controller.slopeLimit = 50f;   // 斜坡体素是 45°
            controller.stepOffset = 0.2f;
            controller.skinWidth = 0.03f;

            var pivot = new GameObject("Visual Scale Pivot").transform;
            pivot.SetParent(root.transform, false);

            Animator animator = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ChibiModel);
            if (prefab != null)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, pivot);
                model.transform.localPosition = new Vector3(-0.010145247f, 0.041202813f, -0.33174288f);
                model.transform.localRotation = new Quaternion(0f, -0.7185263f, 0f, 0.6954998f);
                model.transform.localScale = Vector3.one * 1.0498348f;
                animator = model.GetComponent<Animator>();
                if (animator == null)
                    animator = model.AddComponent<Animator>();
                animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ChibiController);
                animator.applyRootMotion = false;
            }
            else
            {
                Debug.LogWarning("[TerrainLab] 找不到 " + ChibiModel + "（Git LFS 是否已拉取？）");
            }

            var mover = root.AddComponent<WortPigPlayer>();
            SetObject(mover, "inputActions", AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActions));
            SetObject(mover, "animator", animator);
            SetFloat(mover, "jumpHeight", 1.25f); // GDD：1m 可以跳上去，2m 不行

            var squash = root.AddComponent<VisualYScale>();
            SetObject(squash, "visualPivot", pivot);
            SetFloat(squash, "heightMultiplier", 0.5f);
            return root;
        }

        private static void SetObject(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(Object target, string field, float value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
