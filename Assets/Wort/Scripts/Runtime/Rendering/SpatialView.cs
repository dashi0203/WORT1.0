using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Wort.Rendering
{
    /// <summary>
    /// 空间呈现（GDD 第 7 节；负责人 2026-11-13）。计算在着色器 WortSpace.hlsl / CutCap.shader 里，这里只算参数、设全局变量：
    /// - 黑色空处：相机背景改纯黑（透过虚化 / 裁切看到的空处是黑的）；地形最低一层压暗。
    /// - 遮挡虚化：每帧从主角身上取 9 个点往相机方向打射线，被挡住的比例 ≥ 开启阈值时，挡在前面的东西挖一个圆（波浪边缘、溶解），
    ///   降到关闭阈值以下再收回。攀爬物（URP Lit）不参与。
    /// - 鹰眼视觉（GDD【暂定】）：画面大幅降饱和；剔除主角脚下 + 1.5 格以上的所有地形，横切面补黑（高度场判断实心），边缘微微起伏、溶解。
    ///   和以后的室内 / 地下高度裁切共用同一套裁切。开启时遮挡虚化自动关闭。
    /// - 主角脚下薄雾：比主角脚下低 2.5 格开始、往下 2–4 格渐浓，颜色跟着当前天光 / 太阳。
    /// 被裁掉的东西照常投影（投影 pass 不裁）。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Wort/Spatial View")]
    public sealed class SpatialView : MonoBehaviour
    {
        // —— 主角 ——
        [Tooltip("不填就自动找 WortPigPlayer")]
        public Transform player;
        [Tooltip("主角模型大致高度（米，检测遮挡、定虚化圆的中心用）")]
        public float bodyHeight = 1.2f;

        // —— 黑色空处与最低层压暗 ——
        [Tooltip("相机背景改纯黑")]
        public bool blackVoid = true;
        public bool darkenBottom = true;
        [Tooltip("最低一层最多暗多少")]
        [Range(0f, 1f)] public float bottomDarken = 0.45f;
        [Tooltip("从最低一层往上多高恢复正常（米）")]
        [Range(0.1f, 4f)] public float bottomFade = 1f;
        [Tooltip("手动指定最低层高度；关掉时用地图里最低的地面")]
        public bool overrideBottom = false;
        public float bottomHeight = 0f;

        // —— 遮挡虚化 ——
        public bool occlusionCutout = true;
        [Tooltip("圆的半径（米，屏幕平面里）")]
        [Range(0.5f, 6f)] public float holeRadius = 2.2f;
        [Tooltip("被挡住的比例达到这个值时开启")]
        [Range(0f, 1f)] public float openThreshold = 0.8f;
        [Tooltip("降到这个值以下时收回")]
        [Range(0f, 1f)] public float closeThreshold = 0.4f;
        [Tooltip("展开 / 收回用时（秒）")]
        [Range(0.05f, 1f)] public float holeTime = 0.25f;
        [Tooltip("边缘波浪幅度（半径的比例）")]
        [Range(0f, 0.3f)] public float holeWave = 0.08f;
        [Tooltip("边缘溶解带宽（半径的比例）")]
        [Range(0.02f, 0.6f)] public float holeDissolve = 0.25f;
        [Tooltip("比主角身体中心高出多少才算挡在前面（米）")]
        [Range(0f, 1f)] public float frontMargin = 0.25f;
        [Tooltip("调试：不管有没有被挡住，一直显示虚化圆")]
        public bool debugForceHole;

        // —— 鹰眼视觉（GDD【暂定】） ——
        public bool eagleVision;
        [Tooltip("切换键（默认 T）")]
        public Key eagleKey = Key.T;
        [Tooltip("剔除主角脚下多高以上的东西（格）")]
        [Range(0.5f, 4f)] public float cutAbove = 1.5f;
        [Tooltip("进出过渡用时（秒）：裁切面从上往下扫")]
        [Range(0.05f, 1.5f)] public float sweepTime = 0.35f;
        [Tooltip("饱和度降多少")]
        [Range(0f, 1f)] public float desaturate = 0.8f;
        [Tooltip("范围：以主角脚下为圆心的水平圆的半径（米）。0 = 不限范围（整个画面都裁）")]
        [Range(0f, 20f)] public float eagleRadius = 3f;
        [Tooltip("圆边波浪幅度（半径的比例，和遮挡虚化同一套）")]
        [Range(0f, 0.4f)] public float eagleWave = 0.12f;
        [Tooltip("裁切边缘的溶解带宽（米）：边界外一圈碎碎地留下")]
        [Range(0f, 1.5f)] public float eagleDissolve = 0.35f;
        [Tooltip("裁切高度的起伏幅度（米）：墙上的断口高低起伏")]
        [Range(0f, 0.6f)] public float eagleCutJitter = 0.18f;
        [Tooltip("蓝色外扩边")]
        public bool rimEnabled = true;
        [Tooltip("外扩边颜色（HDR，可以调亮发光）")]
        [ColorUsage(false, true)] public Color rimColor = new Color(0.25f, 0.65f, 1.6f);
        [Tooltip("外扩边宽度范围（像素），沿边随噪声在两者之间浮动")]
        [Range(0f, 12f)] public float rimMinPixels = 3f;
        [Range(0f, 12f)] public float rimMaxPixels = 5f;
        [Tooltip("横切面比格线往外扩多少（米，盖住侧墙鼓包，同时是边缘溶解带）")]
        [Range(0.02f, 0.6f)] public float capDilate = 0.32f;

        // —— 主角脚下薄雾 ——
        public bool fog = true;
        [Tooltip("比主角脚下低多少开始起雾（米）")]
        [Range(0f, 6f)] public float fogOffset = 2.5f;
        [Tooltip("渐变厚度（米）")]
        [Range(0.5f, 6f)] public float fogWidth = 3f;
        [Tooltip("最大浓度")]
        [Range(0f, 1f)] public float fogDensity = 0.35f;
        [Tooltip("雾色（再乘当前天光 + 一部分太阳光）")]
        public Color fogTint = new Color(0.86f, 0.9f, 0.96f);
        [Tooltip("太阳光对雾色的贡献")]
        [Range(0f, 1f)] public float fogSunLight = 0.3f;

        [SerializeField, HideInInspector] private Shader capShader;

        // 运行时状态（Inspector 只读显示）
        public float OccludedFraction { get; private set; }
        public float HoleRadiusNow { get; private set; }
        public float EagleBlend { get; private set; }

        private static readonly int IdHoleCenter = Shader.PropertyToID("_WortHoleCenter");
        private static readonly int IdHoleParams = Shader.PropertyToID("_WortHoleParams");
        private static readonly int IdCutParams = Shader.PropertyToID("_WortCutParams");
        private static readonly int IdCutOn = Shader.PropertyToID("_WortCutOn");
        private static readonly int IdCutCircle = Shader.PropertyToID("_WortCutCircle");
        private static readonly int IdCutStyle = Shader.PropertyToID("_WortCutStyle");
        private static readonly int IdCutRim = Shader.PropertyToID("_WortCutRimColor");
        private static readonly int IdBottom = Shader.PropertyToID("_WortBottomParams");
        private static readonly int IdFog = Shader.PropertyToID("_WortFogParams");
        private static readonly int IdFogColor = Shader.PropertyToID("_WortFogColor");

        private bool holeOpen;
        private SkyLighting skyRef;
        private float holeVel;
        private float feetY = float.NaN, feetVel;
        private readonly RaycastHit[] hits = new RaycastHit[8];
        private Camera savedCam;
        private Color savedBg;
        private CameraClearFlags savedFlags;
        private GameObject capGo, volumeGo;
        private Material capMat;
        private Mesh capMesh;
        private VolumeProfile profile;
        private Volume volume;

        private void OnEnable()
        {
#if UNITY_EDITOR
            if (capShader == null) capShader = Shader.Find("Wort/CutCap");
#endif
            Apply(0f);
        }

        private void OnDisable()
        {
            RestoreCamera();
            Shader.SetGlobalVector(IdHoleCenter, Vector4.zero);
            Shader.SetGlobalFloat(IdCutOn, 0);
            Shader.SetGlobalVector(IdBottom, Vector4.zero);
            Shader.SetGlobalVector(IdFog, Vector4.zero);
            DestroySafe(capGo); DestroySafe(capMat); DestroySafe(capMesh);
            DestroySafe(volumeGo); DestroySafe(profile);
            capGo = volumeGo = null; capMat = null; capMesh = null; profile = null; volume = null;
        }

        private void OnValidate()
        {
            if (closeThreshold > openThreshold) closeThreshold = openThreshold;
        }

        private void LateUpdate() => Apply(Application.isPlaying ? Time.deltaTime : 0f);

        private void Apply(float dt)
        {
            if (!isActiveAndEnabled) return;
            if (player == null)
            {
                var p = FindFirstObjectByType<WortPigPlayer>();
                if (p != null) player = p.transform;
            }
            Camera cam = Camera.main;
            float time = Application.isPlaying ? Time.time : (float)Time.realtimeSinceStartupAsDouble;

            // —— 黑色空处 ——
            if (blackVoid && cam != null)
            {
                if (savedCam != cam) { RestoreCamera(); savedCam = cam; savedBg = cam.backgroundColor; savedFlags = cam.clearFlags; }
                if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
                if (cam.backgroundColor != Color.black) cam.backgroundColor = Color.black;
            }
            else RestoreCamera();

            // —— 主角脚下高度（平滑；跳起来时不跟着动） ——
            float footNow = player != null ? player.position.y : 0f;
            var cc = player != null ? player.GetComponent<CharacterController>() : null;
            bool grounded = cc == null || cc.isGrounded || !Application.isPlaying;
            if (float.IsNaN(feetY) || dt <= 0f) feetY = footNow;
            else if (grounded) feetY = Mathf.SmoothDamp(feetY, footNow, ref feetVel, 0.25f, Mathf.Infinity, dt);

            // —— 鹰眼视觉 ——
            if (Application.isPlaying && Keyboard.current != null && Keyboard.current[eagleKey].wasPressedThisFrame)
                eagleVision = !eagleVision;
            float target = eagleVision ? 1f : 0f;
            EagleBlend = dt > 0f ? Mathf.MoveTowards(EagleBlend, target, dt / Mathf.Max(0.01f, sweepTime)) : target;
            float e = EagleBlend * EagleBlend * (3f - 2f * EagleBlend);
            bool cutOn = EagleBlend > 1e-3f && WorldAOField.HasField;
            float cutY = feetY + cutAbove + (1f - e) * 6f;   // 裁切面从上往下落，同时圆从脚下展开
            Vector3 fp = player != null ? player.position : Vector3.zero;
            Shader.SetGlobalFloat(IdCutOn, cutOn ? 1f : 0f);
            Shader.SetGlobalVector(IdCutParams, new Vector4(cutY, eagleCutJitter, time, capDilate));
            Shader.SetGlobalVector(IdCutCircle, new Vector4(fp.x, fp.z, eagleRadius > 0f ? Mathf.Max(0.05f, eagleRadius * e) : 0f, eagleWave));
            Shader.SetGlobalVector(IdCutStyle, new Vector4(eagleDissolve, rimMinPixels, Mathf.Max(rimMinPixels, rimMaxPixels), rimEnabled ? 1 : 0));
            Color rl = rimColor.linear;
            Shader.SetGlobalVector(IdCutRim, new Vector4(rl.r, rl.g, rl.b, 1));
            UpdateCap(cutOn, cam);
            UpdateDesaturation(e);

            // —— 遮挡虚化（鹰眼视觉开着时不需要） ——
            float targetR = 0f;
            if (occlusionCutout && player != null && cam != null && EagleBlend < 1e-3f)
            {
                if (Application.isPlaying)
                {
                    OccludedFraction = MeasureOcclusion(cam);
                    if (!holeOpen && OccludedFraction >= openThreshold) holeOpen = true;
                    else if (holeOpen && OccludedFraction <= closeThreshold) holeOpen = false;
                }
                if (holeOpen || debugForceHole) targetR = holeRadius;
            }
            else holeOpen = false;
            HoleRadiusNow = dt > 0f ? Mathf.SmoothDamp(HoleRadiusNow, targetR, ref holeVel, holeTime * 0.5f, Mathf.Infinity, dt) : targetR;
            Vector3 c = player != null ? player.position + Vector3.up * (bodyHeight * 0.5f) : Vector3.zero;
            Shader.SetGlobalVector(IdHoleCenter, new Vector4(c.x, c.y, c.z, HoleRadiusNow > 0.02f ? HoleRadiusNow : 0f));
            Shader.SetGlobalVector(IdHoleParams, new Vector4(time, holeWave, holeDissolve, frontMargin));

            // —— 最低一层压暗 ——
            float bottom = overrideBottom || !WorldAOField.HasField ? bottomHeight : WorldAOField.MinHeight;
            Shader.SetGlobalVector(IdBottom, new Vector4(bottom, bottomFade, bottomDarken, darkenBottom ? 1 : 0));

            // —— 脚下薄雾：颜色 = 雾色 ×（天光 + 一部分太阳光 × 云遮后的可见度） ——
            Color sky = RenderSettings.ambientMode == AmbientMode.Trilight ? RenderSettings.ambientSkyColor : RenderSettings.ambientLight;
            Color lit = sky.linear;
            Light sun = RenderSettings.sun;
            if (sun != null)
            {
                if (skyRef == null && Time.frameCount % 60 == 0) skyRef = FindFirstObjectByType<SkyLighting>();
                float vis = skyRef != null && skyRef.isActiveAndEnabled ? skyRef.DirectVisibility : 1f;
                lit += sun.color.linear * sun.intensity * fogSunLight * vis;
            }
            Color fc = fogTint.linear * lit;
            Shader.SetGlobalVector(IdFog, new Vector4(feetY - fogOffset, fogWidth, fogDensity, fog ? 1 : 0));
            Shader.SetGlobalVector(IdFogColor, new Vector4(fc.r, fc.g, fc.b, time));
        }

        /// <summary>主角身上 9 个点（3 个高度 × 相机左右 3 个位置）往相机方向打射线，被挡住的比例。</summary>
        private float MeasureOcclusion(Camera cam)
        {
            Vector3 right = cam.transform.right;
            int blocked = 0, total = 0;
            for (int iy = 0; iy < 3; iy++)
            for (int ix = -1; ix <= 1; ix++)
            {
                Vector3 p = player.position + Vector3.up * (bodyHeight * (0.15f + 0.35f * iy)) + right * (0.22f * ix);
                Ray r = cam.ScreenPointToRay(cam.WorldToScreenPoint(p));   // 错切投影下的真实视线方向
                Vector3 dir = -r.direction;
                int n = Physics.RaycastNonAlloc(p + dir * 0.05f, dir, hits, 80f, ~0, QueryTriggerInteraction.Ignore);
                bool hit = false;
                for (int k = 0; k < n && !hit; k++)
                    if (!hits[k].collider.transform.IsChildOf(player)) hit = true;
                total++;
                if (hit) blocked++;
            }
            return total > 0 ? (float)blocked / total : 0f;
        }

        private void UpdateCap(bool on, Camera cam)
        {
            if (!on)
            {
                if (capGo != null && capGo.activeSelf) capGo.SetActive(false);
                return;
            }
            if (capGo == null)
            {
                if (capShader == null) capShader = Shader.Find("Wort/CutCap");
                if (capShader == null) return;
                capMesh = new Mesh { name = "Wort Cut Cap", hideFlags = HideFlags.HideAndDontSave };
                const float S = 60f;
                capMesh.vertices = new[] { new Vector3(-S, 0, -S), new Vector3(-S, 0, S), new Vector3(S, 0, S), new Vector3(S, 0, -S) };
                capMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                capMesh.bounds = new Bounds(Vector3.zero, new Vector3(2 * S, 2000f, 2 * S));   // 顶点着色器把高度改到裁切面，包围盒放大避免被剔除
                capMat = new Material(capShader) { name = "Wort Cut Cap", hideFlags = HideFlags.HideAndDontSave };
                capGo = new GameObject("Wort Cut Cap") { hideFlags = HideFlags.HideAndDontSave };
                capGo.AddComponent<MeshFilter>().sharedMesh = capMesh;
                var mr = capGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = capMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            if (!capGo.activeSelf) capGo.SetActive(true);
            Vector3 f = cam != null ? cam.transform.position : (player != null ? player.position : Vector3.zero);
            capGo.transform.position = new Vector3(Mathf.Round(f.x), 0f, Mathf.Round(f.z));
        }

        private void UpdateDesaturation(float w)
        {
            if (volumeGo == null)
            {
                if (w <= 0f) return;
                volumeGo = new GameObject("Wort Eagle Vision Volume") { hideFlags = HideFlags.HideAndDontSave };
                volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 120f;   // 比 SkyLighting 的阴天降饱和（100）高
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.hideFlags = HideFlags.HideAndDontSave;
                var ca = profile.Add<ColorAdjustments>(false);
                ca.saturation.overrideState = true;
                volume.sharedProfile = profile;
            }
            if (profile.TryGet(out ColorAdjustments adj)) adj.saturation.value = -100f * desaturate;
            volume.weight = w;
        }

        private void RestoreCamera()
        {
            if (savedCam != null)
            {
                savedCam.clearFlags = savedFlags;
                savedCam.backgroundColor = savedBg;
            }
            savedCam = null;
        }

        private static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }
    }
}
