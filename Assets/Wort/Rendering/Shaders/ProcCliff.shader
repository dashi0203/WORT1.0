// 程序化崖壁（ProcCliffMesher 生成的网格）的着色器。预览定形见 Tools/TilesetPreview/proc_cliff.py（负责人 2026-11-13）。
// 网格每顶点带两组数据：TEXCOORD1 = (种类, 露边掩码 / 侧面朝向, 崖顶高, 崖脚高)，TEXCOORD2 = 顶面格的世界 x z / 墙面折线坐标、正面 z。
// 种类：1 顶面，2 正面（朝南），4 东西侧面。贴图坐标全部按世界坐标算，不用网格 UV。
// - 顶面两层：顶岩铺满（_UseTopRock = 0 时顶岩就是泥土，沼泽 / 森林），泥土离露边内收 _Inset0–_Inset1（不规则，按两边明暗差推进），
//   泥土边在顶岩上压一点暗边；离露边远的像素直接走“只有泥土”的便宜分支。
// - 平滑倒角：离露边 r（18–32cm，沿边缓慢变化）内，顶面法线连续往外倒到 45°，墙面法线往上翻到 45°，两边同一个 r，折线处角度和贴图都连续；
//   石头的法线细节放在顶面坐标里，跟着表面一起绕边缘旋转（不淡出），所以折线看不出来。
// - 墙面：崖壁颜色 + 法线；顶部 _TopBlend 内融合顶岩（按“顶面越过折线的延续”展开取样，石块从顶面绕到墙上），崖脚 _FootBlend 内融合泥土。
// - _ClipNorth = 1（TopClip 材质，只用在北面露边的格）：北边是真正的轮廓线，按噪声透明裁剪 0–_NorthClip，轮廓不再笔直。
// - 暗部提亮 _BlackLift（同 TileSurface）；崖壁提亮 / 加饱和 _WallGain、_WallSat。
// - 平滑法线（负责人 2026-11-13）：顶沿、竖直转角（正面 ↔ 侧面）、崖脚内凹圆角（墙底 ↔ 墙根前地面）都在折线两侧各转 45°，两边同一个宽度函数，折线处对齐。
// - Biome 融合：贴图在数组 _ColArr / _NrmArr 里（TerrainLab 运行时打包），每像素按权重图取候选套件，各自算 Surface 再按分数合成（斑块式过渡）。
//   上面 _CliffMap 等属性只是每套材质存贴图用的，渲染时不直接读。
// - 性能开关：_EdgeFx = 0 关掉倒角和法线弯折，_WallTopFx = 0 关掉墙顶融合（对比开销用）。
Shader "Wort/ProcCliff"
{
    Properties
    {
        _CliffMap ("Cliff", 2D) = "gray" {}
        [Normal] _CliffNormal ("Cliff Normal", 2D) = "bump" {}
        _EarthMap ("Earth", 2D) = "white" {}
        [Normal] _EarthNormal ("Earth Normal", 2D) = "bump" {}
        _EarthPeriod ("Earth Period (m)", Float) = 2
        [Toggle] _UseTopRock ("Use Top Rock (off = earth to the edge)", Float) = 0
        _TopRockMap ("Top Rock", 2D) = "white" {}
        [Normal] _TopRockNormal ("Top Rock Normal", 2D) = "bump" {}
        _TopRockPeriod ("Top Rock Period (m)", Float) = 2.7
        _Inset0 ("Earth Inset Min (m)", Float) = 0.15
        _Inset1 ("Earth Inset Max (m)", Float) = 0.35
        _BevelR0 ("Smooth Bevel Min (m)", Float) = 0.18
        _BevelR1 ("Smooth Bevel Max (m)", Float) = 0.32
        _TopBlend ("Wall Top Blend (m)", Float) = 0.35
        _FootBlend ("Wall Foot Blend (m)", Float) = 0.35
        [Toggle] _ClipNorth ("Clip North Edge", Float) = 0
        _NorthClip ("North Clip Depth (m)", Float) = 0.12
        _BlackLift ("Black Lift (sRGB)", Range(0, 0.3)) = 0.08
        _WallGain ("Wall Gain", Range(0.5, 2)) = 1.12
        _WallSat ("Wall Saturation", Range(0, 2)) = 1.12
        [Toggle] _EdgeFx ("Edge Effects (bevel / bend)", Float) = 1
        [Toggle] _WallTopFx ("Wall Top Blend", Float) = 1
        _FootAO ("Cliff Foot Darkening", Range(0.5, 1)) = 0.8
        [Toggle] _VarOn ("Cliff Variants", Float) = 1
        _P22 ("Variant 2x2 Chance (per 2x2 block)", Range(0, 1)) = 0.15
        _PRow ("Variant Row Pattern Chance", Range(0, 1)) = 0.2
        _PCol ("Variant Column Pattern Chance", Range(0, 1)) = 0.2
        [HideInInspector] _ColArr ("", 2DArray) = "" {}
        [HideInInspector] _NrmArr ("", 2DArray) = "" {}
        [HideInInspector] _SetP0 ("", Vector) = (0, 2.7, 2, 0)
        [HideInInspector] _SetP1 ("", Vector) = (0, 2.7, 2, 0)
        [HideInInspector] _SetP2 ("", Vector) = (0, 2.7, 2, 0)
        [HideInInspector] _SetP3 ("", Vector) = (0, 2.7, 2, 0)
        [HideInInspector] _SetCount ("", Float) = 1
        [HideInInspector] _ArrSize ("", Float) = 512
        _BiomePatch ("Biome Patch Strength", Float) = 0.8
        _BiomeSharp ("Biome Edge Softness", Float) = 0.025
        [Toggle] _ShadowEdge ("Jagged Shadow Edges", Float) = 1
        _ShadowNotch0 ("Shadow Notch Min (m)", Float) = 0.06
        _ShadowNotch1 ("Shadow Notch Max (m)", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // 运行时由 TerrainLab 把各套材质的贴图打包成两个数组（颜色 / 法线），每套 3 层：崖壁、泥土、顶岩（没有顶岩的套放泥土）
        TEXTURE2D_ARRAY(_ColArr); SAMPLER(sampler_ColArr);
        TEXTURE2D_ARRAY(_NrmArr); SAMPLER(sampler_NrmArr);
        // Biome 权重图（全局）：一格一个像素，RGBA = 套件 0–3 的权重（已模糊），_WortBiomeRect = (x0, z0, 宽, 深)
        TEXTURE2D(_WortBiomeMap); SAMPLER(sampler_WortBiomeMap);
        float4 _WortBiomeRect;
        float _WortBiomeOn;
        static float _FW;
        CBUFFER_START(UnityPerMaterial)
            float4 _SetP0, _SetP1, _SetP2, _SetP3;   // 每套：(有顶岩, 顶岩周期, 泥土周期, 0)
            float _SetCount, _ArrSize, _BiomePatch, _BiomeSharp;
            float _VarOn, _P22, _PRow, _PCol, _FootAO;
            float _EarthPeriod, _UseTopRock, _TopRockPeriod;
            float _Inset0, _Inset1, _BevelR0, _BevelR1, _TopBlend, _FootBlend;
            float _ClipNorth, _NorthClip, _BlackLift, _WallGain, _WallSat, _EdgeFx, _WallTopFx;
            float _ShadowEdge, _ShadowNotch0, _ShadowNotch1;
        CBUFFER_END

        // ---- 噪声（只在 GPU 上用，不需要和 C# 一致）----
        float hash1(float n) { return frac(sin(n * 12.9898) * 43758.5453); }
        float vnoise(float t, float seed, float per)
        {
            t = t / per + seed * 17.3;
            float i = floor(t), f = t - i; f = f * f * (3 - 2 * f);
            return lerp(hash1(i + seed * 78.233), hash1(i + 1 + seed * 78.233), f);
        }
        float hash2(float2 p, float seed) { return frac(sin(dot(p, float2(12.9898, 78.233)) + seed * 4.1) * 43758.5453); }
        float vnoise2(float2 p, float seed, float per)
        {
            p = p / per + float2(seed * 7.1, -seed * 3.7);
            float2 i = floor(p), f = p - i; f = f * f * (3 - 2 * f);
            return lerp(lerp(hash2(i, seed), hash2(i + float2(1, 0), seed), f.x),
                    lerp(hash2(i + float2(0, 1), seed), hash2(i + float2(1, 1), seed), f.x), f.y);
        }
        float4 setP(float s) { return s < 0.5 ? _SetP0 : s < 1.5 ? _SetP1 : s < 2.5 ? _SetP2 : _SetP3; }
        // 崖壁变体（负责人 2026-11-13）：每 2×2 格一块（对齐偶数格），按哈希定这一块的样式——整块 2×2 变体 / 两行各自 2×1 或基础 / 两列各自 1×2 或基础 / 全是基础。
        // 块和块不重叠，同一位置每次结果相同。数组里每套 19 层：0 崖壁、1 泥土、2 顶岩、3–10 2x2（v*4 + 行*2 + 列）、11–14 2x1（v*2 + 列）、15–18 1x2（v*2 + 行）。
        float cliffSlice(float s, float2 uc)
        {
            float base = s * 19;
            if (_VarOn < 0.5 || setP(s).w < 0.5) return base;
            float2 c = floor(uc), b = floor(c / 2), f = c - b * 2;
            float hb = hash2(b, 501 + s * 7);
            if (hb < _P22) return base + 3 + (hash2(b, 502 + s) < 0.5 ? 0 : 4) + f.y * 2 + f.x;
            if (hb < _P22 + _PRow)
            {
                float hr = hash2(float2(b.x, c.y), 503 + s);
                return hr < 0.6 ? base + 11 + (hr < 0.3 ? 0 : 2) + f.x : base;
            }
            if (hb < _P22 + _PRow + _PCol)
            {
                float hc = hash2(float2(c.x, b.y), 504 + s);
                return hc < 0.6 ? base + 15 + (hc < 0.3 ? 0 : 2) + f.y : base;
            }
            return base;
        }
        float lum(half3 c) { return dot(c, half3(0.299, 0.587, 0.114)); }
        float smooth01(float x) { x = saturate(x); return x * x * (3 - 2 * x); }
        float bevelR(float along, float seed) { return _BevelR0 + (_BevelR1 - _BevelR0) * vnoise(along, 100 + seed, 0.7); }

        float lodFor(float per) { return max(0, log2(max(_FW * _ArrSize / per, 1e-6))); }
        half4 ColA(float slice, float2 uv, float per) { return SAMPLE_TEXTURE2D_ARRAY_LOD(_ColArr, sampler_ColArr, uv, slice, lodFor(per)); }
        half4 NrmA(float slice, float2 uv, float per) { return SAMPLE_TEXTURE2D_ARRAY_LOD(_NrmArr, sampler_NrmArr, uv, slice, lodFor(per)); }

        // 绕轴 k（单位向量）旋转 ang 弧度
        float3 rotateAxis(float3 v, float3 k, float ang)
        {
            float c = cos(ang), s = sin(ang);
            return v * c + cross(k, v) * s + k * dot(k, v) * (1 - c);
        }
        // 顶面坐标系（切线 +x、副切线 +z、法线 +y）里的切线空间法线 → 世界
        float3 topFrame(float3 tn) { return normalize(float3(tn.x, tn.z, tn.y)); }

        // 北边透明裁剪（TopClip 材质）：前向、深度、深度法线三个 Pass 用同一个函数，避免深度预渲染在裁掉的地方留空洞
        void NorthClip(float3 P, float4 d1, float4 d2)
        {
            if (_ClipNorth < 0.5 || d1.x > 1.5) return;
            if (fmod(d1.y, 2) < 1) return;
            float dN = 1 - (P.z - d2.y);
            float cw = _NorthClip * (0.55 * vnoise(P.x, 31, 0.35) + 0.45 * vnoise(P.x, 32, 0.09));
            cw += 0.10 * saturate((vnoise(P.x, 33, 0.8) - 0.7) / 0.3);
            // 北边在内拐角接回同高或更高的邻格，裁剪在格角收为零。
            float fx = P.x - d2.x;
            if (fmod(floor(d1.y / 4096), 2) >= 1) cw *= smooth01((1 - fx) / 0.35);
            if (fmod(floor(d1.y / 8192), 2) >= 1) cw *= smooth01(fx / 0.35);
            clip(dN - cw);
        }
        // 阴影专用的边缘裁剪（负责人 2026-11-13：影子横平竖直）：只在 ShadowCaster 里用，画面不变。
        // 顶面在露边内侧、墙面（正面 / 侧面 / 只投影的面）在墙顶以下，按同一个沿边噪声咬掉 _ShadowNotch0–_ShadowNotch1，投到地上的影子轮廓变得参差。
        float shadowNotch(float along) { return _ShadowNotch0 + (_ShadowNotch1 - _ShadowNotch0) * (0.6 * vnoise(along, 51, 0.4) + 0.4 * vnoise(along, 52, 0.1)); }
        void ShadowEdgeClip(float3 P, float4 d1, float4 d2, float3 nWS)
        {
            if (_ShadowEdge < 0.5) return;
            if (d1.x < 1.5 && d1.x > 0.5)
            {
                // 顶面：到各露边的距离
                float mask = d1.y; float fx = P.x - d2.x, fz = P.z - d2.y;
                float c = 1;
                if (fmod(mask, 2) >= 1) c = min(c, (1 - fz) - shadowNotch(P.x));
                if (fmod(floor(mask / 2), 2) >= 1) c = min(c, (1 - fx) - shadowNotch(P.z));
                if (fmod(floor(mask / 4), 2) >= 1) c = min(c, fz - shadowNotch(P.x));
                if (fmod(floor(mask / 8), 2) >= 1) c = min(c, fx - shadowNotch(P.z));
                clip(c);
            }
            else
            {
                // 墙面 / 只投影的面：离墙顶的深度；沿边坐标按朝向取 x 或 z（与顶面同一条边对上）
                float along = abs(nWS.x) > abs(nWS.z) ? P.z : P.x;
                clip((d1.z - P.y) - shadowNotch(along));
            }
        }
        ENDHLSL


        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            // Forward+：见 TileSurface.shader 的说明（不声明时主光源被乘成 0）
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT;
                float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 d1 : TEXCOORD2;
                float4 d2 : TEXCOORD3;
                float fog : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.d1 = v.d1; o.d2 = v.d2;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // 一套 Biome 的颜色和法线（s = 套件编号；贴图都在两个数组里：第 s*3 层崖壁、s*3+1 层泥土、s*3+2 层顶岩）
            void Surface(float s, float3 P, float3 ng, float4 d1, float4 d2, out half3 albedo, out float3 N)
            {
                float4 sp = setP(s);
                float useTR = sp.x, trPer = sp.y, ePer = sp.z;
                albedo = 0; N = float3(0, 1, 0);
                const float3 up = float3(0, 1, 0);
                float kind = d1.x;
                float2 ue = P.xz / ePer;
                half3 earthC = ColA(s * 19 + 1, ue, ePer).rgb;
                bool isWall = kind > 1.5;

                if (!isWall)
                {
                    // ---------- 顶面 ----------
                    float mask = d1.y; float2 cell = d2.xy;
                    float fx = P.x - cell.x, fz = P.z - cell.y;
                    bool bN = fmod(mask, 2) >= 1, bE = fmod(floor(mask / 2), 2) >= 1, bS = fmod(floor(mask / 4), 2) >= 1, bW = fmod(floor(mask / 8), 2) >= 1;
                    float dN = bN ? 1 - fz : 9, dE = bE ? 1 - fx : 9, dS = bS ? fz : 9, dW = bW ? fx : 9;
                    float dm = min(min(dN, dE), min(dS, dW));
                    // 内拐角（只有斜对角更低）：按到角点的距离算顶岩边和泥土内收，两边的顶岩边在拐角处接上
                    float cm = floor(mask / 16);
                    float dc = 9;
                    if (fmod(cm, 2) >= 1) dc = min(dc, length(float2(1 - fx, 1 - fz)));
                    if (fmod(floor(cm / 2), 2) >= 1) dc = min(dc, length(float2(fx, 1 - fz)));
                    if (fmod(floor(cm / 4), 2) >= 1) dc = min(dc, length(float2(1 - fx, fz)));
                    if (fmod(floor(cm / 8), 2) >= 1) dc = min(dc, length(float2(fx, fz)));
                    float dr = min(dm, dc);   // 顶岩边 / 泥土内收用；倒角只看真正的露边 dm
                    float3 earthN = topFrame(UnpackNormal(NrmA(s * 19 + 1, ue, ePer)));
                    // 崖脚（内凹圆角）的地面一侧：哪边邻居更高，墙根前 r 内法线往外倾到 45°（墙底同样往上翻 45°，交界线对齐）
                    float hm = floor(mask / 256);
                    float dHN = fmod(hm, 2) >= 1 ? 1 - fz : 9, dHE = fmod(floor(hm / 2), 2) >= 1 ? 1 - fx : 9;
                    float dHS = fmod(floor(hm / 4), 2) >= 1 ? fz : 9, dHW = fmod(floor(hm / 8), 2) >= 1 ? fx : 9;
                    float dH = min(min(dHN, dHE), min(dHS, dHW));
                    if ((dr > 0.6 && dH > 0.4) || _EdgeFx < 0.5)
                    {
                        // 离露边、墙根都远：只有泥土（大部分像素走这里，和普通材质一样便宜）
                        albedo = earthC; N = earthN;
                    }
                    else if (dr > 0.6)
                    {
                        // 墙根前的地面：沿墙方向取宽度（与墙底同一个函数：北墙 x / 20，东墙 z / 21，南墙 x / 22，西墙 z / 23）
                        float along = P.x, seed = 20; float3 away = float3(0, 0, -1);
                        if (dHE <= dH + 1e-5) { along = P.z; seed = 21; away = float3(-1, 0, 0); }
                        else if (dHS <= dH + 1e-5) { along = P.x; seed = 22; away = float3(0, 0, 1); }
                        else if (dHW <= dH + 1e-5) { along = P.z; seed = 23; away = float3(1, 0, 0); }
                        float sg = saturate(1 - dH / bevelR(along, seed));
                        albedo = earthC;
                        N = earthN;   // 崖脚地面不再往外倾（45° 朝南迎光，反而比平地亮）；改成下面的崖脚暗影
                    }
                    else
                    {
                        // 最近的露边：沿边坐标和种子（S 1, N 2, E 3, W 4）
                        float along = P.x, seed = 1;
                        if (dN <= dm + 1e-5) { along = P.x; seed = 2; }
                        else if (dE <= dm + 1e-5) { along = P.z; seed = 3; }
                        else if (dW <= dm + 1e-5) { along = P.z; seed = 4; }
                        // 外倒方向：各露边按远近加权（凸角自然变圆）
                        float3 outd = 0;
                        outd += float3(0, 0, 1) * saturate(1 - dN / 0.5);
                        outd += float3(1, 0, 0) * saturate(1 - dE / 0.5);
                        outd += float3(0, 0, -1) * saturate(1 - dS / 0.5);
                        outd += float3(-1, 0, 0) * saturate(1 - dW / 0.5);
                        outd = normalize(outd + float3(0, 0, -1e-4));
                        // 顶岩（沼泽 / 森林即泥土）
                        float2 ur = P.xz / trPer;
                        half3 rk = useTR > 0.5 ? ColA(s * 19 + 2, ur, trPer).rgb : earthC;
                        float3 rockN = useTR > 0.5 ? topFrame(UnpackNormal(NrmA(s * 19 + 2, ur, trPer))) : earthN;
                        // 泥土内收
                        if (dc < dm) along = P.x + P.z;
                        float wi = _Inset0 + (_Inset1 - _Inset0) * (0.6 * vnoise(along, 81, 0.5) + 0.4 * vnoise(along, 82, 0.12));
                        wi += 0.15 * saturate((vnoise(along, 83, 0.9) - 0.75) / 0.25);
                        float se = dr - wi + (lum(earthC) - lum(rk)) * 0.12 + (vnoise2(P.xz, 85, 0.06) - 0.5) * 0.06;
                        float qe = useTR > 0.5 ? smoothstep(-0.015, 0.015, se) : 1;
                        float ao = 1 - 0.35 * saturate(1 + se / 0.05) * (1 - qe) * useTR;
                        albedo = lerp(rk * ao, earthC, qe);
                        float3 base = normalize(lerp(rockN, earthN, qe));
                        // 平滑倒角：细节跟着表面一起绕边缘倒到 45°
                        float sm = dm < 8 ? saturate(1 - dm / bevelR(along, seed)) : 0;
                        float3 k = normalize(cross(up, outd));
                        N = sm > 0 ? normalize(rotateAxis(base, k, radians(45) * sm)) : base;
                    }
                    // 崖脚暗影（负责人 2026-11-13：崖底接缝发亮）：内凹的墙根应该更暗，离更高的邻格 0.35m 内逐渐压暗，墙面底部用同一条曲线，接缝两侧亮度一致
                    albedo *= lerp(_FootAO, 1, smooth01(dH / 0.35));
                    // 坡道顶面：把“朝上”的结果整体转到几何法线
                    if (ng.y < 0.999)
                    {
                        float3 ax = normalize(cross(up, ng));
                        N = normalize(rotateAxis(N, ax, acos(saturate(ng.y))));
                    }
                }
                else
                {
                    // ---------- 墙面（正面 kind 2 / 侧面 kind 4）----------
                    bool side = kind > 3;
                    float sgn = side ? d1.y : 0;
                    float yt = d1.z, yb = d1.w, edge = d2.x;
                    float3 outd = side ? float3(sgn, 0, 0) : float3(0, 0, -1);
                    // 崖壁贴图：正面 u = x；侧面从这一列正面转角接着铺
                    float cu = side ? edge + sgn * (P.z - d2.y) : P.x;
                    float2 uc = float2(cu, P.y);
                    float cs = cliffSlice(s, uc);   // 崖壁这一格用基础块还是哪一块变体
                    half3 tc = ColA(cs, uc, 1).rgb;
                    float3 ct = UnpackNormal(NrmA(cs, uc, 1));
                    float3 T = normalize(cross(ng, up)), B = normalize(cross(T, ng));
                    float3 Wv = normalize(T * ct.x + B * ct.y + ng * ct.z);
                    // 崖脚融合泥土
                    float footContact = smooth01(1 - (P.y - yb) / 0.06);
                    float2 footUv = lerp(float2(cu, P.y), P.xz, footContact) / ePer;
                    half3 ew = ColA(s * 19 + 1, footUv, ePer).rgb;
                    // 崖脚那格地面在墙根处实际是什么：离它自己的露边近（窄脊、台地边一圈）就是顶岩，否则是泥土——崖脚融合到它，不是一律泥土
                    float gm = side ? d2.z : (sgn == 0 ? d1.y : 0);
                    float gfx = frac(P.x), gfz = frac(P.z);
                    float dg = side ? min(fmod(gm, 2) >= 1 ? 1 - gfz : 9, fmod(floor(gm / 4), 2) >= 1 ? gfz : 9)
                                    : min(fmod(floor(gm / 2), 2) >= 1 ? 1 - gfx : 9, fmod(floor(gm / 8), 2) >= 1 ? gfx : 9);
                    float footRock = useTR > 0.5 ? 1 - smoothstep(-0.03, 0.03, dg - 0.5 * (_Inset0 + _Inset1)) : 0;
                    float2 footUvR = lerp(float2(cu, P.y), P.xz, footContact) / trPer;
                    if (footRock > 0) ew = lerp(ew, ColA(s * 19 + 2, footUvR, trPer).rgb, footRock);
                    float lc = lum(tc);
                    float qf = smooth01((_FootBlend - (P.y - yb)) / _FootBlend + (lum(ew) - lc) * 1.2);
                    qf = max(qf, footContact);   // 贴地那一点一定是泥土，和地面同色
                    // 崖壁提亮 / 加饱和只作用在崖壁本身（融合之前），崖脚泥土、墙顶顶岩保持和地面 / 顶面同色
                    float lt0 = lum(tc);
                    half3 tcw = saturate((lt0 + (tc - lt0) * _WallSat) * _WallGain);
                    albedo = lerp(tcw, ew, qf);
                    float along = side ? P.z : P.x, seed = side ? (sgn > 0 ? 3 : 4) : 1;
                    float3 k = normalize(cross(up, outd));
                    if (_WallTopFx > 0.5 && yt - P.y < _TopBlend + 0.1)
                    {
                        // 墙顶融合顶岩：按“顶面越过折线的延续”展开取样
                        float su = side ? edge + sgn * (yt - P.y) : P.x;
                        float sv = side ? P.z : edge - (yt - P.y);
                        float per = useTR > 0.5 ? trPer : ePer;
                        float2 us = float2(su, sv) / per;
                        half3 trw = useTR > 0.5 ? ColA(s * 19 + 2, us, trPer).rgb : ColA(s * 19 + 1, us, ePer).rgb;
                        float3 trn = useTR > 0.5 ? UnpackNormal(NrmA(s * 19 + 2, us, trPer)) : UnpackNormal(NrmA(s * 19 + 1, us, ePer));
                        float qt = smooth01((_TopBlend - (yt - P.y)) / _TopBlend + (lum(trw) - lc) * 1.2 - 0.15 + (vnoise2(float2(cu, P.y), 91, 0.08) - 0.5) * 0.5);
                        albedo = lerp(albedo, trw, qt);
                        float3 rockW = rotateAxis(topFrame(trn), k, radians(90));      // 顶面坐标 → 墙面
                        Wv = normalize(lerp(Wv, rockW, qt));
                    }
                    // 崖脚融合区：法线细节跟着颜色换成泥土的（顶面坐标 → 墙面）
                    if (qf > 0)
                    {
                        float3 en = topFrame(UnpackNormal(NrmA(s * 19 + 1, footUv, ePer)));
                        if (footRock > 0) en = normalize(lerp(en, topFrame(UnpackNormal(NrmA(s * 19 + 2, footUvR, trPer))), footRock));
                        Wv = normalize(lerp(Wv, rotateAxis(en, k, radians(90)), qf));
                    }
                    N = Wv;
                    albedo *= lerp(_FootAO, 1, smooth01((P.y - yb) / 0.35));
                    if (_EdgeFx > 0.5)
                    {
                        // 平滑倒角：离崖顶 r 内往上翻到 45°（与顶面同一个 r，折线处对上）
                        float sw = saturate(1 - (yt - P.y) / bevelR(along, seed));
                        if (sw > 0) N = normalize(rotateAxis(N, k, -radians(45) * sw));
                        // 崖脚内凹圆角：离崖脚 r 内往上翻到 45°，和墙根前地面的 45° 在交界线对齐（地面一侧见顶面分支，种子 20–23）
                        // 崖脚：不再往上翻 45°（会比地面亮）；墙根的法线已随融合转成地面法线，再乘同一条崖脚暗影
                        // 竖直转角：正面在段的东西两头（那一侧更低时）转向侧面，侧面在正面转角附近转向正面，各 45°，转角处对齐
                        if (!side)
                        {
                            float packed = d2.w, hwS = floor(packed / 1024), heE = packed - hwS * 1024;
                            if (P.y > heE + 0.01)
                            {
                                float sv = saturate(1 - max(d2.z - P.x, 0) / bevelR(P.y + d2.z, 40));
                                if (sv > 0) N = normalize(rotateAxis(N, up, -radians(45) * sv));
                            }
                            if (P.y > hwS + 0.01)
                            {
                                float sv = saturate(1 - max(P.x - d2.y, 0) / bevelR(P.y + d2.y, 40));
                                if (sv > 0) N = normalize(rotateAxis(N, up, radians(45) * sv));
                            }
                        }
                        else
                        {
                            float sv = saturate(1 - max(P.z - d2.y, 0) / bevelR(P.y + edge, 40));
                            if (sv > 0) N = normalize(rotateAxis(N, up, sgn * radians(45) * sv));
                        }
                    }
                }

            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 P = i.positionWS;
                float3 ng = normalize(i.normalWS);
                NorthClip(P, i.d1, i.d2);
                _FW = max(length(ddx(P)), length(ddy(P)));   // 一个像素在世界里多大：所有贴图的 mip 都按它算（分支 / 循环里不用硬件求导）
                // Biome 融合（负责人 2026-11-13，斑块式过渡；预览 proc_cliff.py BIOME=1）：
                // 每套算一个分数 = 权重 + 各自的斑块噪声 + 各自颜色的明暗，分数最高的胜出，接近处很窄的软过渡；权重几乎为 0 的套不算（大部分像素只算一套）
                float4 w = _WortBiomeOn > 0.5 ? SAMPLE_TEXTURE2D_LOD(_WortBiomeMap, sampler_WortBiomeMap, (P.xz - _WortBiomeRect.xy) / _WortBiomeRect.zw, 0) : float4(1, 0, 0, 0);
                half3 albs[4]; float3 ns[4]; float sc[4]; float best = -9;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    albs[k] = 0; ns[k] = float3(0, 1, 0); sc[k] = -9;
                    if (w[k] > 0.02 && k < _SetCount)
                    {
                        Surface(k, P, ng, i.d1, i.d2, albs[k], ns[k]);
                        float nz = 0.45 * vnoise2(P.xz, 79 + 13 * k, 8) + 0.35 * vnoise2(P.xz, 77 + 13 * k, 2.5) + 0.2 * vnoise2(P.xz, 78 + 13 * k, 0.7);
                        sc[k] = w[k] + (nz - 0.5) * _BiomePatch + (lum(albs[k]) - 0.5) * 0.45;
                        best = max(best, sc[k]);
                    }
                }
                half3 albedo = 0; float3 N = 0; float tot = 0;
                [unroll] for (int j = 0; j < 4; j++)
                {
                    float e = sc[j] > -8 ? exp((sc[j] - best) / max(_BiomeSharp, 1e-3)) : 0;
                    albedo += albs[j] * e; N += ns[j] * e; tot += e;
                }
                albedo /= max(tot, 1e-5); N = normalize(N + float3(0, 1e-5, 0));

                // 暗部提亮（sRGB 里把曲线最低点往上拉）
                half3 srgb = FastLinearToSRGB(albedo);
                albedo = FastSRGBToLinear(_BlackLift + srgb * (1 - _BlackLift));

                Light light = GetMainLight(TransformWorldToShadowCoord(P));
                float ndl = saturate(dot(N, light.direction));
                half3 diffuse = light.color * ndl * light.shadowAttenuation;
                half3 color = albedo * (diffuse + SampleSH(N));
                color = MixFog(color, i.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }
        Pass
        {
            // 自带投影 Pass：北边透明裁剪也进阴影（影子边缘跟着轮廓变碎）
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            // 只画背光面（Cull Front）：外壳闭合（顶面 + 正面 / 侧面 + 只投影的北东西面），光从咬碎的缝进来也会被出口面挡住；双面会在地面上出自阴影条纹
            ZWrite On ZTest LEqual ColorMask 0 Cull Front
            HLSLPROGRAM
            #pragma vertex svert
            #pragma fragment sfrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct SA { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2; };
            struct SV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2; float3 normalWS : TEXCOORD3; };
            SV svert(SA v)
            {
                SV o;
                float3 w = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 ld = normalize(_LightPosition - w);
            #else
                float3 ld = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(w, n, ld));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = cs; o.positionWS = w; o.d1 = v.d1; o.d2 = v.d2; o.normalWS = n;
                return o;
            }
            half4 sfrag(SV i) : SV_Target { NorthClip(i.positionWS, i.d1, i.d2); ShadowEdgeClip(i.positionWS, i.d1, i.d2, i.normalWS); return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex dvert
            #pragma fragment dfrag
            struct DA { float4 positionOS : POSITION; float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2; };
            struct DV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2; };
            DV dvert(DA v) { DV o; float3 w = TransformObjectToWorld(v.positionOS.xyz); o.positionWS = w; o.positionCS = TransformWorldToHClip(w); o.d1 = v.d1; o.d2 = v.d2; return o; }
            half dfrag(DV i) : SV_Target { NorthClip(i.positionWS, i.d1, i.d2); return i.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex nvert
            #pragma fragment nfrag
            struct NA { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 d1 : TEXCOORD1; float4 d2 : TEXCOORD2; };
            struct NV { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float4 d1 : TEXCOORD2; float4 d2 : TEXCOORD3; };
            NV nvert(NA v) { NV o; float3 w = TransformObjectToWorld(v.positionOS.xyz); o.positionWS = w; o.positionCS = TransformWorldToHClip(w); o.normalWS = TransformObjectToWorldNormal(v.normalOS); o.d1 = v.d1; o.d2 = v.d2; return o; }
            half4 nfrag(NV i) : SV_Target { NorthClip(i.positionWS, i.d1, i.d2); return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
