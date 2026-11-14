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
        [Tooltip("边缘不透明度渐变带宽（半径的比例）：圆边不透明，往里渐变到全透")]
        [Range(0.02f, 1f)] public float holeFade = 0.45f;
        [Tooltip("裁切边界的扰动（米）：只裁真正挡住主角的部分，边界沿地形走，这个值让它参差而不是直角。0 = 不扰动")]
        [Range(0f, 1f)] public float holeEdgeBite = 0.35f;
        [Tooltip("只裁高于主角脚下多少米的东西（和鹰眼视觉的“剔除脚下多高以上”同一个意思）")]
        [Range(0f, 3f)] public float holeCutAbove = 0.5f;
        [Tooltip("调试：不管有没有被挡住，一直显示虚化圆")]
        public bool debugForceHole;

        // —— 鹰眼视觉（GDD【暂定】） ——
        public bool eagleVision;
        [Tooltip("切换键（默认 T）")]
        public Key eagleKey = Key.T;
        [Tooltip("剔除主角脚下多高以上的东西（格）")]
        [Range(0f, 4f)] public float cutAbove = 0.5f;
        [Tooltip("进出过渡用时（秒）：裁切面从上往下扫")]
        [Range(0.05f, 1.5f)] public float sweepTime = 0.35f;
        [Tooltip("饱和度降多少")]
        [Range(0f, 1f)] public float desaturate = 0.8f;
        [Tooltip("范围：以主角脚下为圆心的水平圆的半径（米）。0 = 不限范围（整个画面都裁）")]
        [Range(0f, 20f)] public float eagleRadius = 3f;
        [Tooltip("反选区压暗：裁切圆再往外多少米以外，整片画面压暗")]
        [Range(0f, 10f)] public float eagleDimExtra = 1f;
        [Tooltip("反选区压暗程度（0 = 不压暗）")]
        [Range(0f, 1f)] public float eagleDim = 0.6f;
        [Tooltip("反选区边缘往里渐变的宽度（米）")]
        [Range(0.05f, 4f)] public float eagleDimFade = 1f;
        [Tooltip("屏幕暗角强度")]
        [Range(0f, 1f)] public float eagleVignette = 0.35f;
        [Tooltip("圆边波浪幅度（半径的比例，和遮挡虚化同一套）")]
        [Range(0f, 0.4f)] public float eagleWaveAmp = 0.08f;
        [Tooltip("圆边不透明度渐变带宽（半径的比例，和遮挡虚化同一套）")]
        [Range(0.02f, 1f)] public float eagleFade = 0.45f;
        [Tooltip("黑色横截面边缘扰动（米）：往外参差地扩多少（和遮挡虚化洞里的黑边同款）")]
        [Range(0f, 1f)] public float capRagged = 0.3f;
        [Tooltip("裁切高度的起伏幅度（米）：墙上的断口高低起伏")]
        [Range(0f, 0.6f)] public float eagleCutJitter = 0.18f;
        [Tooltip("黑色横截面比格线往外扩多少（米）：只盖住接缝，太大会在墙线外多出一条黑边")]
        [Range(0f, 0.4f)] public float capOutset = 0.06f;

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
        private static readonly int IdHoleEdge = Shader.PropertyToID("_WortHoleEdge");
        private static readonly int IdViewDir = Shader.PropertyToID("_WortViewDir");
        private static readonly int IdCutParams = Shader.PropertyToID("_WortCutParams");
        private static readonly int IdCutOn = Shader.PropertyToID("_WortCutOn");
        private static readonly int IdCutCircle = Shader.PropertyToID("_WortCutCircle");
        private static readonly int IdCutStyle = Shader.PropertyToID("_WortCutStyle");
        private static readonly int IdEagleDim = Shader.PropertyToID("_WortEagleDim");
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
            Shader.SetGlobalVector(IdCutParams, new Vector4(cutY, eagleCutJitter, time, capOutset));
            Shader.SetGlobalVector(IdCutCircle, new Vector4(fp.x, fp.z, eagleRadius > 0f ? Mathf.Max(0.05f, eagleRadius * e) : 0f, eagleWaveAmp));
            Shader.SetGlobalVector(IdCutStyle, new Vector4(eagleFade, capRagged, 0, 0));
            Shader.SetGlobalVector(IdEagleDim, new Vector4(eagleDimExtra, eagleDim, eagleDimFade, cutOn ? e : 0f));
            UpdateCap(cutOn, cam, cutY);
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
            Shader.SetGlobalVector(IdHoleParams, new Vector4(time, holeWave, holeFade, holeCutAbove));
            Shader.SetGlobalVector(IdHoleEdge, new Vector4(holeEdgeBite, 1, player != null ? player.position.y : 0f, bodyHeight));
            if (cam != null)
            {
                // 错切投影下的真实视线方向（朝相机）：屏幕中心在近 / 远裁剪面各反算一个世界点
                Matrix4x4 inv = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
                Vector3 vdir = (inv.MultiplyPoint(new Vector3(0, 0, -1)) - inv.MultiplyPoint(new Vector3(0, 0, 1))).normalized;
                Shader.SetGlobalVector(IdViewDir, vdir);
            }

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

        /// <summary>
        /// 主角身上 9 个点（3 个高度 × 相机左右 3 个位置）沿视线往相机方向打射线，被挡住的比例。
        /// 错切相机（WortShearCamera）的视线不是相机的朝向（竖直向下），而是“往上、往南”斜着的：
        /// 投影矩阵里 y 加了 shear × 深度，屏幕上同一个点对应的是一条斜线。这里直接用 投影 × 视图 矩阵的逆，
        /// 把这个点所在的屏幕位置在近、远裁剪面上各反算一个世界点，两点连线就是真实视线，不依赖 shear 的具体写法。
        /// </summary>
        private float MeasureOcclusion(Camera cam)
        {
            Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            Matrix4x4 inv = vp.inverse;
            Vector3 right = cam.transform.right;
            int blocked = 0, total = 0;
            for (int iy = 0; iy < 3; iy++)
            for (int ix = -1; ix <= 1; ix++)
            {
                Vector3 p = player.position + Vector3.up * (bodyHeight * (0.15f + 0.35f * iy)) + right * (0.22f * ix);
                Vector3 ndc = vp.MultiplyPoint(p);
                Vector3 near = inv.MultiplyPoint(new Vector3(ndc.x, ndc.y, -1f));
                Vector3 far = inv.MultiplyPoint(new Vector3(ndc.x, ndc.y, 1f));
                Vector3 dir = (near - far).normalized;   // 朝相机
                float maxDist = Vector3.Distance(p, near);
                int n = Physics.RaycastNonAlloc(p + dir * 0.05f, dir, hits, maxDist, ~0, QueryTriggerInteraction.Ignore);
                bool hit = false;
                for (int k = 0; k < n && !hit; k++)
                    if (!hits[k].collider.transform.IsChildOf(player)) hit = true;
                total++;
                if (hit) blocked++;
            }
            return total > 0 ? (float)blocked / total : 0f;
        }

        private void UpdateCap(bool on, Camera cam, float cutY)
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
                // 单位正方形（±1），按裁切圆的大小缩放
                capMesh.vertices = new[] { new Vector3(-1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, -1) };
                capMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                capMesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 4000f, 2f));   // 顶点着色器把高度改到裁切面，包围盒竖向放大避免被剔除
                capMat = new Material(capShader) { name = "Wort Cut Cap", hideFlags = HideFlags.HideAndDontSave };
                capGo = new GameObject("Wort Cut Cap") { hideFlags = HideFlags.HideAndDontSave };
                capGo.AddComponent<MeshFilter>().sharedMesh = capMesh;
                var mr = capGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = capMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            if (!capGo.activeSelf) capGo.SetActive(true);
            // 平面只铺到裁切圆（+ 波浪和外扩的余量），不铺满屏幕：横切面着色器每像素要查 9 格高度场，铺满屏幕很贵
            Vector3 f = player != null ? player.position : (cam != null ? cam.transform.position : Vector3.zero);
            float half = eagleRadius > 0f ? eagleRadius * (1f + eagleWaveAmp) + 0.5f : 60f;
            // 圆按“投到主角脚下高度”判断，横截面在裁切高度上，所以世界里要顺着视线往相机那边挪（错切下是往南挪 cutY − 脚下）
            if (cam != null && player != null)
            {
                Matrix4x4 inv = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
                Vector3 v = (inv.MultiplyPoint(new Vector3(0, 0, -1)) - inv.MultiplyPoint(new Vector3(0, 0, 1))).normalized;
                if (v.y > 0.05f) f += new Vector3(v.x, 0f, v.z) * ((cutY - f.y) / v.y);
            }
            capGo.transform.position = new Vector3(f.x, 0f, f.z);
            capGo.transform.localScale = new Vector3(half, 1f, half);
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
                var vg = profile.Add<Vignette>(false);   // 鹰眼视觉的屏幕暗角
                vg.intensity.overrideState = true;
                vg.smoothness.overrideState = true;
                vg.smoothness.value = 0.45f;
                volume.sharedProfile = profile;
            }
            if (profile.TryGet(out ColorAdjustments adj)) adj.saturation.value = -100f * desaturate;
            if (profile.TryGet(out Vignette vig)) vig.intensity.value = eagleVignette;
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
