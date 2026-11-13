using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Wort.Rendering
{
    /// <summary>
    /// 天空光照：云影 + 天气（晴 / 转阴 / 阴）对太阳、环境光、饱和度的影响。
    /// - 云影 = 主光源 cookie。贴图每帧由 CloudShadow.compute 生成（光照空间里的云透射率，无缝重复），
    ///   URP 把 cookie 乘在主光源颜色上 → 只削弱直射光，和 shadowmap 是同一个乘积里的两个因子，不会和树影叠加变黑。
    /// - overcast：0 大晴天，0.35 普通晴天，1 阴天。以后由天气系统写；现在用 Inspector 滑条。
    ///   普通晴天这一档里，云量再按时段（hour）和“停一阵、再较快变化”的随机过程起伏。
    /// - 没有自动曝光：被云挡掉的直射光按 scatterReturn 的比例加回环境光（阴天整片天空发亮），阴天不会发黑。
    /// 编辑模式也会运行（方便看效果），会改动场景里太阳的 cookie / 颜色 / 强度和环境光，组件关掉时全部还原。
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Wort/Sky Lighting")]
    public sealed class SkyLighting : MonoBehaviour
    {
        [Header("天气输入（以后由天气 / 昼夜系统驱动）")]
        [Tooltip("0 = 大晴天，0.35 = 普通晴天，1 = 阴天")]
        [Range(0f, 1f)] public float overcast = 0.35f;
        [Tooltip("一天中的时段，影响普通晴天的云量：早上少而薄，午后多而厚，傍晚消散")]
        [Range(0f, 24f)] public float hour = 14f;
        [Tooltip("普通晴天里，云量是否按时段和随机过程起伏")]
        public bool fairWeatherVariation = true;
        [Tooltip("随机起伏的种子")]
        public int seed = 1;

        [Header("太阳")]
        public Light sun;
        [Tooltip("由本组件设置太阳的颜色和强度")]
        public bool driveSun = true;
        public Color sunColor = new Color(1f, 0.96f, 0.89f);
        public float sunIntensity = 2f;

        [Header("环境光（三色渐变：天空 / 地平线 / 地面）")]
        [Tooltip("由本组件设置环境光（会把环境光模式改成 Gradient）")]
        public bool driveAmbient = true;
        [Tooltip("晴天的天空环境光")]
        public Color clearSkyAmbient = new Color(0.49f, 0.54f, 0.64f);
        [Tooltip("阴天天空本身的颜色倾向（亮度主要来自下面的“漫射补回”）")]
        public Color overcastAmbient = new Color(0.69f, 0.71f, 0.73f);
        [Tooltip("阴天时，被云挡掉的直射光有多少比例变成天空漫射光（没有自动曝光，靠它让阴天不发黑）")]
        [Range(0f, 1f)] public float scatterReturn = 0.44f;
        [Tooltip("阴天降低的饱和度")]
        [Range(0f, 1f)] public float overcastDesaturate = 0.28f;

        [Header("云")]
        [Tooltip("普通晴天（overcast 0.35）时的云覆盖率")]
        [Range(0f, 1f)] public float fairCoverage = 0.45f;
        [Tooltip("云团大小（米）。实际按“一张 cookie 里重复整数次”取近似值")]
        public float cloudSize = 26f;
        [Tooltip("云边软度：越小边缘越实")]
        [Range(0.01f, 0.3f)] public float edgeSoftness = 0.07f;
        [Tooltip("云层厚度（米，缩小尺度）：越厚云心越深")]
        public float cloudThickness = 5f;
        [Tooltip("风（米 / 秒，世界 x / z）")]
        public Vector2 wind = new Vector2(1.6f, 0.6f);
        [Tooltip("云形状随时间变化的速度")]
        public float evolveSpeed = 1f;
        [Tooltip("半影宽度（米）")]
        public float penumbra = 0.45f;
        [Tooltip("小尺度絮状细节的强度")]
        [Range(0f, 1f)] public float detail = 1f;
        [Tooltip("扭曲强度（米）：让云团形状不规则")]
        public float warp = 14f;
        [Tooltip("沿云层厚度的积分步数")]
        [Range(1, 8)] public int steps = 5;
        [Tooltip("一张 cookie 覆盖的范围（米），无缝重复")]
        public float tileSize = 128f;
        [Tooltip("cookie 分辨率")]
        public int resolution = 256;

        [Header("其他")]
        [Tooltip("编辑模式下也运行（会改动场景里的太阳和环境光，关掉组件即还原）")]
        [UnityEngine.Serialization.FormerlySerializedAs("runInEditMode")]
        public bool previewInEditMode = true;
        [SerializeField] private ComputeShader compute;

        // —— 运行时状态（Inspector 只读显示） ——
        public float EffectiveOvercast { get; private set; }
        public float Coverage { get; private set; }
        public float GustOffset { get; private set; }
        public float DirectVisibility { get; private set; }

        private RenderTexture raw, raw2, cookie;
        private CommandBuffer cmd;
        private int kGenerate, kBlurX, kBlurY;
        private Vector2 motion;          // 风造成的 uv 偏移（取小数）
        private float evolveShape, evolveDetail;
        private double lastTime = double.NaN;
        private int lastFrame = -1;

        private GameObject volumeGo;
        private VolumeProfile volumeProfile;
        private ColorAdjustments colorAdjust;

        // 还原用
        private bool saved;
        private Texture savedCookie;
        private Vector2 savedCookieSize, savedCookieOffset;
        private Color savedSunColor;
        private float savedSunIntensity;
        private AmbientMode savedAmbientMode;
        private Color savedAmbientSky, savedAmbientEquator, savedAmbientGround, savedAmbientLight;
        private Light savedSunRef;

        // —— 噪声（所有实例共用） ——
        private static Texture3D s_noise;
        private static float[] s_quantiles;   // 云形状噪声的分位数表：覆盖率 → 阈值
        private const int NoiseSize = 64;
        private const int ShapeCells = 5;     // 形状噪声每张贴图 5 格
        private const int DetailCells = 8;

        private static readonly int IdNoise = Shader.PropertyToID("_CloudNoise");
        private static readonly int IdRaw = Shader.PropertyToID("_Raw");
        private static readonly int IdRawIn = Shader.PropertyToID("_RawIn");
        private static readonly int IdBlurOut = Shader.PropertyToID("_BlurOut");
        private static readonly int IdCookie = Shader.PropertyToID("_Cookie");
        private static readonly int IdRes = Shader.PropertyToID("_Res");
        private static readonly int IdMotion = Shader.PropertyToID("_Motion");
        private static readonly int IdShape = Shader.PropertyToID("_Shape");
        private static readonly int IdScale = Shader.PropertyToID("_Scale");
        private static readonly int IdBlur = Shader.PropertyToID("_Blur");

        private bool Active => Application.isPlaying || previewInEditMode;

        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContext;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Restore();
            ReleaseResources();
        }

        private void OnValidate()
        {
            resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(32, resolution)), 32, 1024);
            tileSize = Mathf.Max(16f, tileSize);
            cloudSize = Mathf.Max(2f, cloudSize);
            cloudThickness = Mathf.Max(0.1f, cloudThickness);
#if UNITY_EDITOR
            if (compute == null) LoadCompute();
#endif
            if (!Active && saved) Restore();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            sun = RenderSettings.sun;
            LoadCompute();
        }

        private void LoadCompute()
        {
            compute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Wort/Rendering/Shaders/CloudShadow.compute");
        }
#endif

        // ———————————————————————————— 每帧 ————————————————————————————

        private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!Active || Time.frameCount == lastFrame && Application.isPlaying) return;
            lastFrame = Time.frameCount;
            if (sun == null) sun = RenderSettings.sun;
            if (sun == null || sun.type != LightType.Directional) return;
#if UNITY_EDITOR
            if (compute == null) LoadCompute();
#endif
            if (compute == null || !SystemInfo.supportsComputeShaders) return;

            double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
            float dt = double.IsNaN(lastTime) ? 0f : Mathf.Clamp((float)(now - lastTime), 0f, 0.25f);
            lastTime = now;

            SaveOriginals();
            EnsureResources();
            UpdateWeather(now, dt, out float sigma, out float haze);
            Generate(context, sigma, haze);
            ApplyLighting(haze);
        }

        /// <summary>运行时把太阳挪到相机附近（只按 cookie 的整张重复距离挪，图案不跳），减少以后太阳转动时云影的滑动。</summary>
        private void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (!Application.isPlaying || sun == null || cookie == null) return;
            if (cam.cameraType != CameraType.Game) return;
            Transform t = sun.transform;
            Vector3 c = cam.transform.position;
            float x = Mathf.Round(Vector3.Dot(c, t.right) / tileSize) * tileSize;
            float y = Mathf.Round(Vector3.Dot(c, t.up) / tileSize) * tileSize;
            Vector3 p = t.right * x + t.up * y;
            if ((t.position - p).sqrMagnitude > 1e-4f) t.position = p;
        }

        // ———————————————————————————— 天气 → 参数 ————————————————————————————

        private void UpdateWeather(double now, float dt, out float sigma, out float haze)
        {
            float c = Mathf.Clamp01(overcast);
            float w = FairWeight(c);
            Diurnal(hour, out float dOff, out float dThick);
            GustOffset = fairWeatherVariation ? Gust(now) : 0f;
            float ce = fairWeatherVariation ? Mathf.Clamp01(c + w * (dOff + GustOffset)) : c;
            EffectiveOvercast = ce;

            Coverage = CoverageFor(ce);
            sigma = (1.2f + 0.8f * SmoothStep(0.35f, 0.95f, ce)) * (fairWeatherVariation ? 1f + (dThick - 1f) * w : 1f);
            haze = 1f - 0.97f * SmoothStep(0.7f, 1f, ce);

            // 风、形状变化（累加，太阳方向变了也连续）
            Transform t = sun.transform;
            Vector3 wind3 = new Vector3(wind.x, 0f, wind.y);
            Vector2 wl = new Vector2(Vector3.Dot(wind3, t.right), Vector3.Dot(wind3, t.up)) / tileSize;
            motion -= wl * dt;
            motion.x -= Mathf.Floor(motion.x);
            motion.y -= Mathf.Floor(motion.y);
            evolveShape = Mathf.Repeat(evolveShape + 0.007f * evolveSpeed * dt, 1f);
            evolveDetail = Mathf.Repeat(evolveDetail + 0.015f * evolveSpeed * dt, 1f);
        }

        /// <summary>普通晴天这一档的权重：大晴天、阴天两头为 0，时段和随机起伏只在这一档起作用。</summary>
        private static float FairWeight(float c) => SmoothStep(0.08f, 0.2f, c) * (1f - SmoothStep(0.5f, 0.75f, c));

        private static readonly float[] DiurnalHours = { 5f, 8f, 11f, 14f, 16f, 18f, 20f };
        private static readonly float[] DiurnalOffset = { -0.2f, -0.13f, -0.04f, 0.05f, 0.05f, -0.06f, -0.18f };
        private static readonly float[] DiurnalThick = { 0.6f, 0.7f, 0.9f, 1.15f, 1.1f, 0.85f, 0.65f };

        /// <summary>晴天积云的日变化：返回 overcast 偏移和云厚倍率。</summary>
        private static void Diurnal(float h, out float offset, out float thick)
        {
            offset = Interp(h, DiurnalHours, DiurnalOffset);
            thick = Interp(h, DiurnalHours, DiurnalThick);
        }

        private float CoverageFor(float c)
        {
            float cov;
            if (c <= 0.08f) cov = 0f;
            else if (c <= 0.35f) cov = Mathf.Lerp(0f, fairCoverage, (c - 0.08f) / 0.27f);
            else if (c <= 0.9f) cov = Mathf.Lerp(fairCoverage, 1f, (c - 0.35f) / 0.55f);
            else cov = 1f;
            return cov * cov * (3f - 2f * cov) * 0.5f + cov * 0.5f;   // 端点稍微平滑
        }

        // —— “停一阵、再较快变化”：停留 20–90 秒，然后 6–25 秒内缓入缓出地变到新的偏移（±0.1） ——
        private System.Random gustRng;
        private int gustSeed;
        private double segStart, segHoldEnd, segEnd;
        private float segFrom, segTo;

        private float Gust(double t)
        {
            if (gustRng == null || gustSeed != seed || t < segStart)
            {
                gustRng = new System.Random(seed);
                gustSeed = seed;
                segFrom = segTo = 0f;
                segStart = segHoldEnd = segEnd = t;
            }
            while (t >= segEnd)
            {
                segFrom = segTo;
                segStart = segEnd;
                segHoldEnd = segStart + RandRange(20, 90);
                segEnd = segHoldEnd + RandRange(6, 25);
                float nv = (float)RandRange(-0.1, 0.1);
                if (Mathf.Abs(nv - segFrom) < 0.06f) nv = Mathf.Clamp(segFrom + Mathf.Sign(nv - segFrom + 1e-6f) * 0.08f, -0.1f, 0.1f);
                segTo = nv;
            }
            if (t < segHoldEnd) return segFrom;
            float f = (float)((t - segHoldEnd) / (segEnd - segHoldEnd));
            f = f * f * (3f - 2f * f);
            return Mathf.Lerp(segFrom, segTo, f);
        }

        private double RandRange(double a, double b) => a + (b - a) * gustRng.NextDouble();

        // ———————————————————————————— 生成 cookie ————————————————————————————

        private void Generate(ScriptableRenderContext context, float sigma, float haze)
        {
            float ly = Mathf.Max(0.2f, -sun.transform.forward.y);   // 太阳越低，穿过云层的路程越长
            float thr = Coverage <= 1e-4f ? 2f : Quantile(1f - Coverage);
            float extinction = sigma * 0.55f * cloudThickness / ly;
            float shapeRepeat = Mathf.Max(1f, Mathf.Round(tileSize / (cloudSize * ShapeCells)));
            float detailRepeat = Mathf.Max(1f, Mathf.Round(tileSize / (3.2f * DetailCells)));
            float texel = tileSize / resolution;

            cmd.Clear();
            cmd.SetComputeVectorParam(compute, IdRes, new Vector4(resolution, 1f / resolution, 0, 0));
            cmd.SetComputeVectorParam(compute, IdMotion, new Vector4(motion.x, motion.y, evolveShape, evolveDetail));
            cmd.SetComputeVectorParam(compute, IdShape, new Vector4(thr, edgeSoftness, extinction, steps));
            cmd.SetComputeVectorParam(compute, IdScale, new Vector4(shapeRepeat, detailRepeat, warp / tileSize, detail));
            cmd.SetComputeVectorParam(compute, IdBlur, new Vector4(penumbra / texel, haze, 0, 0));

            int groups = Mathf.CeilToInt(resolution / 8f);
            cmd.SetComputeTextureParam(compute, kGenerate, IdNoise, s_noise);
            cmd.SetComputeTextureParam(compute, kGenerate, IdRaw, raw);
            cmd.DispatchCompute(compute, kGenerate, groups, groups, 1);
            cmd.SetComputeTextureParam(compute, kBlurX, IdRawIn, raw);
            cmd.SetComputeTextureParam(compute, kBlurX, IdBlurOut, raw2);
            cmd.DispatchCompute(compute, kBlurX, groups, groups, 1);
            cmd.SetComputeTextureParam(compute, kBlurY, IdRawIn, raw2);
            cmd.SetComputeTextureParam(compute, kBlurY, IdCookie, cookie);
            cmd.DispatchCompute(compute, kBlurY, groups, groups, 1);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            // 大范围平均能照到多少直射光（用来补环境光），按覆盖率和云厚估算，不回读 GPU
            float opacity = 1f - Mathf.Exp(-1.3f * sigma);
            DirectVisibility = haze * (1f - Coverage * opacity);
        }

        // ———————————————————————————— 光照 ————————————————————————————

        private void ApplyLighting(float haze)
        {
            float ce = EffectiveOvercast;

            if (sun.cookie != cookie) sun.cookie = cookie;
            var data = GetLightData(sun);
            if (data != null)
            {
                var size = new Vector2(tileSize, tileSize);
                if (data.lightCookieSize != size) data.lightCookieSize = size;
                if (data.lightCookieOffset != Vector2.zero) data.lightCookieOffset = Vector2.zero;
            }

            if (driveSun)
            {
                if (sun.color != sunColor) sun.color = sunColor;
                if (!Mathf.Approximately(sun.intensity, sunIntensity)) sun.intensity = sunIntensity;
            }

            float k = SmoothStep(0.3f, 1f, ce);
            if (driveAmbient)
            {
                // 线性空间里算：晴天 / 阴天的天空色插值，再把被云挡掉的直射光按比例加回来
                Color amb = Color.Lerp(clearSkyAmbient.linear, overcastAmbient.linear, k);
                float lost = 1f - DirectVisibility;
                float comp = scatterReturn * (0.18f + 0.82f * SmoothStep(0.35f, 0.95f, ce));
                float ly = Mathf.Max(0f, -sun.transform.forward.y);
                Color sunLin = sun.color.linear * sun.intensity * ly;
                amb += new Color(sunLin.r * 0.92f, sunLin.g * 0.96f, sunLin.b, 0f) * (lost * comp);
                amb.a = 1f;

                if (RenderSettings.ambientMode != AmbientMode.Trilight) RenderSettings.ambientMode = AmbientMode.Trilight;
                // 只在变化时写（编辑模式下每次写都会让场景和环境光重新计算）
                Color sky = amb.gamma, equator = (amb * 0.8f).gamma, ground = (amb * 0.45f).gamma;
                if (Differs(RenderSettings.ambientSkyColor, sky)) RenderSettings.ambientSkyColor = sky;
                if (Differs(RenderSettings.ambientEquatorColor, equator)) RenderSettings.ambientEquatorColor = equator;
                if (Differs(RenderSettings.ambientGroundColor, ground)) RenderSettings.ambientGroundColor = ground;
            }

            EnsureVolume();
            if (colorAdjust != null)
                colorAdjust.saturation.value = -100f * overcastDesaturate * k;
        }

        private static bool Differs(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.002f;
        }

        private static UniversalAdditionalLightData GetLightData(Light light)
        {
            if (light.TryGetComponent(out UniversalAdditionalLightData data)) return data;
            return light.gameObject.AddComponent<UniversalAdditionalLightData>();
        }

        // ———————————————————————————— 资源 ————————————————————————————

        private void EnsureResources()
        {
            if (s_noise == null) BakeNoise();
            if (cmd == null) cmd = new CommandBuffer { name = "Wort Cloud Shadows" };
            kGenerate = compute.FindKernel("Generate");
            kBlurX = compute.FindKernel("BlurX");
            kBlurY = compute.FindKernel("BlurY");
            if (raw == null || raw.width != resolution)
            {
                ReleaseTextures();
                raw = NewRT(RenderTextureFormat.RHalf, "Wort Cloud Raw");
                raw2 = NewRT(RenderTextureFormat.RHalf, "Wort Cloud Blur");
                cookie = NewRT(RenderTextureFormat.ARGB32, "Wort Cloud Cookie");
            }
        }

        private RenderTexture NewRT(RenderTextureFormat format, string rtName)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format, RenderTextureReadWrite.Linear)
            {
                name = rtName,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            return rt;
        }

        private void EnsureVolume()
        {
            if (volumeGo != null && colorAdjust != null) return;
            if (volumeGo != null) DestroySafe(volumeGo);
            volumeGo = new GameObject("Wort Sky Lighting Volume") { hideFlags = HideFlags.HideAndDontSave };
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100f;
            volumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            volumeProfile.hideFlags = HideFlags.HideAndDontSave;
            colorAdjust = volumeProfile.Add<ColorAdjustments>(false);
            colorAdjust.saturation.overrideState = true;   // 只接管饱和度，其他参数照用场景里的 Volume
            volume.sharedProfile = volumeProfile;
        }

        private void ReleaseTextures()
        {
            if (raw != null) { raw.Release(); DestroySafe(raw); }
            if (raw2 != null) { raw2.Release(); DestroySafe(raw2); }
            if (cookie != null) { cookie.Release(); DestroySafe(cookie); }
            raw = raw2 = cookie = null;
        }

        private void ReleaseResources()
        {
            ReleaseTextures();
            cmd?.Release();
            cmd = null;
            if (volumeGo != null) DestroySafe(volumeGo);
            if (volumeProfile != null) DestroySafe(volumeProfile);
            volumeGo = null;
            volumeProfile = null;
            colorAdjust = null;
            lastTime = double.NaN;
        }

        private static void DestroySafe(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void SaveOriginals()
        {
            if (saved && savedSunRef == sun) return;
            if (saved) Restore();
            savedSunRef = sun;
            savedCookie = sun.cookie;
            var data = GetLightData(sun);
            savedCookieSize = data != null ? data.lightCookieSize : Vector2.one;
            savedCookieOffset = data != null ? data.lightCookieOffset : Vector2.zero;
            savedSunColor = sun.color;
            savedSunIntensity = sun.intensity;
            savedAmbientMode = RenderSettings.ambientMode;
            savedAmbientSky = RenderSettings.ambientSkyColor;
            savedAmbientEquator = RenderSettings.ambientEquatorColor;
            savedAmbientGround = RenderSettings.ambientGroundColor;
            savedAmbientLight = RenderSettings.ambientLight;
            saved = true;
        }

        private void Restore()
        {
            if (!saved) return;
            saved = false;
            if (savedSunRef != null)
            {
                savedSunRef.cookie = savedCookie;
                if (savedSunRef.TryGetComponent(out UniversalAdditionalLightData data))
                {
                    data.lightCookieSize = savedCookieSize;
                    data.lightCookieOffset = savedCookieOffset;
                }
                if (driveSun)
                {
                    savedSunRef.color = savedSunColor;
                    savedSunRef.intensity = savedSunIntensity;
                }
            }
            if (driveAmbient)
            {
                RenderSettings.ambientMode = savedAmbientMode;
                RenderSettings.ambientSkyColor = savedAmbientSky;
                RenderSettings.ambientEquatorColor = savedAmbientEquator;
                RenderSettings.ambientGroundColor = savedAmbientGround;
                RenderSettings.ambientLight = savedAmbientLight;
            }
            if (colorAdjust != null) colorAdjust.saturation.value = 0f;
            savedSunRef = null;
        }

        // ———————————————————————————— 噪声烘焙 ————————————————————————————

        /// <summary>
        /// 64³ 无缝 3D 噪声：r = 云形状（5/10/20/40 格四层 value noise），gb = 扭曲（4/8 格），a = 细节（8/16/32 格）。
        /// 同时统计 r 的分位数表，覆盖率 → 阈值 精确对应。
        /// </summary>
        private static void BakeNoise()
        {
            const int n = NoiseSize;
            var shape = new PeriodicNoise(101);
            var warpA = new PeriodicNoise(202);
            var warpB = new PeriodicNoise(303);
            var det = new PeriodicNoise(404);
            var pixels = new Color32[n * n * n];
            var rs = new float[n * n * n];
            for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (float)x / n, v = (float)y / n, w = (float)z / n;
                float r = shape.Fbm(u, v, w, ShapeCells, 4, 0.5f);
                float g = warpA.Fbm(u, v, w, 4, 2, 0.5f);
                float b = warpB.Fbm(u, v, w, 4, 2, 0.5f);
                float a = det.Fbm(u, v, w, DetailCells, 3, 0.5f);
                int i = x + n * (y + n * z);
                rs[i] = r;
                pixels[i] = new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
            }
            s_noise = new Texture3D(n, n, n, TextureFormat.RGBA32, false)
            {
                name = "Wort Cloud Noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            s_noise.SetPixels32(pixels);
            s_noise.Apply(false, true);

            System.Array.Sort(rs);
            s_quantiles = new float[257];
            for (int i = 0; i <= 256; i++)
            {
                float q = rs[Mathf.Min(rs.Length - 1, i * (rs.Length - 1) / 256)];
                s_quantiles[i] = Mathf.Round(q * 255f) / 255f;   // 与贴图里的 8 位精度一致
            }
        }

        private static float Quantile(float p)
        {
            float f = Mathf.Clamp01(p) * 256f;
            int i = Mathf.Min(255, (int)f);
            return Mathf.Lerp(s_quantiles[i], s_quantiles[i + 1], f - i);
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        /// <summary>周期为 1 的 value noise（格点值按格数取模），用于烘焙无缝贴图。</summary>
        private sealed class PeriodicNoise
        {
            private readonly float[] table = new float[256];
            private readonly int[] perm = new int[256];

            public PeriodicNoise(int seedValue)
            {
                var rng = new System.Random(seedValue);
                for (int i = 0; i < 256; i++) { table[i] = (float)rng.NextDouble(); perm[i] = i; }
                for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
            }

            public float Fbm(float u, float v, float w, int cells, int octaves, float gain)
            {
                float sum = 0f, amp = 1f, total = 0f;
                for (int o = 0; o < octaves; o++)
                {
                    sum += amp * Value(u * cells, v * cells, w * cells, cells, o);
                    total += amp;
                    amp *= gain;
                    cells *= 2;
                }
                return sum / total;
            }

            private float Value(float x, float y, float z, int period, int octave)
            {
                int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
                float fx = Fade(x - xi), fy = Fade(y - yi), fz = Fade(z - zi);
                float c000 = Lattice(xi, yi, zi, period, octave), c100 = Lattice(xi + 1, yi, zi, period, octave);
                float c010 = Lattice(xi, yi + 1, zi, period, octave), c110 = Lattice(xi + 1, yi + 1, zi, period, octave);
                float c001 = Lattice(xi, yi, zi + 1, period, octave), c101 = Lattice(xi + 1, yi, zi + 1, period, octave);
                float c011 = Lattice(xi, yi + 1, zi + 1, period, octave), c111 = Lattice(xi + 1, yi + 1, zi + 1, period, octave);
                float a = Mathf.Lerp(c000, c100, fx), b = Mathf.Lerp(c010, c110, fx);
                float c = Mathf.Lerp(c001, c101, fx), d = Mathf.Lerp(c011, c111, fx);
                return Mathf.Lerp(Mathf.Lerp(a, b, fy), Mathf.Lerp(c, d, fy), fz);
            }

            private float Lattice(int x, int y, int z, int period, int octave)
            {
                x = ((x % period) + period) % period;
                y = ((y % period) + period) % period;
                z = ((z % period) + period) % period;
                return table[perm[(perm[(perm[(x + octave * 31) & 255] + y) & 255] + z) & 255]];
            }

            private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        // ———————————————————————————— 工具 ————————————————————————————

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static float Interp(float x, float[] xs, float[] ys)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return Mathf.Lerp(ys[i - 1], ys[i], (x - xs[i - 1]) / (xs[i] - xs[i - 1]));
            return ys[ys.Length - 1];
        }
    }
}
