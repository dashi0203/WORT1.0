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
        [Toggle] _ShadowEdge ("Jagged Shadow Edges", Float) = 1
        _ShadowNotch0 ("Shadow Notch Min (m)", Float) = 0.06
        _ShadowNotch1 ("Shadow Notch Max (m)", Float) = 0.2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_CliffMap); SAMPLER(sampler_CliffMap);
        TEXTURE2D(_CliffNormal); SAMPLER(sampler_CliffNormal);
        TEXTURE2D(_EarthMap); SAMPLER(sampler_EarthMap);
        TEXTURE2D(_EarthNormal); SAMPLER(sampler_EarthNormal);
        TEXTURE2D(_TopRockMap); SAMPLER(sampler_TopRockMap);
        TEXTURE2D(_TopRockNormal); SAMPLER(sampler_TopRockNormal);
        CBUFFER_START(UnityPerMaterial)
            float4 _CliffMap_ST;
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
        float lum(half3 c) { return dot(c, half3(0.299, 0.587, 0.114)); }
        float smooth01(float x) { x = saturate(x); return x * x * (3 - 2 * x); }
        float bevelR(float along, float seed) { return _BevelR0 + (_BevelR1 - _BevelR0) * vnoise(along, 100 + seed, 0.7); }

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

            half4 frag(Varyings i) : SV_Target
            {
                float3 P = i.positionWS;
                float3 ng = normalize(i.normalWS);
                const float3 up = float3(0, 1, 0);
                float kind = i.d1.x;
                float2 ue = P.xz / _EarthPeriod;
                half3 earthC = SAMPLE_TEXTURE2D(_EarthMap, sampler_EarthMap, ue).rgb;
                float3 N; half3 albedo; bool isWall = kind > 1.5;

                if (!isWall)
                {
                    // ---------- 顶面 ----------
                    float mask = i.d1.y; float2 cell = i.d2.xy;
                    float fx = P.x - cell.x, fz = P.z - cell.y;
                    bool bN = fmod(mask, 2) >= 1, bE = fmod(floor(mask / 2), 2) >= 1, bS = fmod(floor(mask / 4), 2) >= 1, bW = fmod(floor(mask / 8), 2) >= 1;
                    float dN = bN ? 1 - fz : 9, dE = bE ? 1 - fx : 9, dS = bS ? fz : 9, dW = bW ? fx : 9;
                    NorthClip(P, i.d1, i.d2);
                    float dm = min(min(dN, dE), min(dS, dW));
                    // 内拐角（只有斜对角更低）：按到角点的距离算顶岩边和泥土内收，两边的顶岩边在拐角处接上
                    float cm = floor(mask / 16);
                    float dc = 9;
                    if (fmod(cm, 2) >= 1) dc = min(dc, length(float2(1 - fx, 1 - fz)));
                    if (fmod(floor(cm / 2), 2) >= 1) dc = min(dc, length(float2(fx, 1 - fz)));
                    if (fmod(floor(cm / 4), 2) >= 1) dc = min(dc, length(float2(1 - fx, fz)));
                    if (fmod(floor(cm / 8), 2) >= 1) dc = min(dc, length(float2(fx, fz)));
                    float dr = min(dm, dc);   // 顶岩边 / 泥土内收用；倒角只看真正的露边 dm
                    float3 earthN = topFrame(UnpackNormal(SAMPLE_TEXTURE2D(_EarthNormal, sampler_EarthNormal, ue)));
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
                        N = sg > 0 ? normalize(rotateAxis(earthN, normalize(cross(up, away)), radians(45) * sg)) : earthN;
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
                        float2 ur = P.xz / _TopRockPeriod;
                        half3 rk = _UseTopRock > 0.5 ? SAMPLE_TEXTURE2D(_TopRockMap, sampler_TopRockMap, ur).rgb : earthC;
                        float3 rockN = _UseTopRock > 0.5 ? topFrame(UnpackNormal(SAMPLE_TEXTURE2D(_TopRockNormal, sampler_TopRockNormal, ur))) : earthN;
                        // 泥土内收
                        if (dc < dm) along = P.x + P.z;
                        float wi = _Inset0 + (_Inset1 - _Inset0) * (0.6 * vnoise(along, 81, 0.5) + 0.4 * vnoise(along, 82, 0.12));
                        wi += 0.15 * saturate((vnoise(along, 83, 0.9) - 0.75) / 0.25);
                        float se = dr - wi + (lum(earthC) - lum(rk)) * 0.12 + (vnoise2(P.xz, 85, 0.06) - 0.5) * 0.06;
                        float qe = _UseTopRock > 0.5 ? smoothstep(-0.015, 0.015, se) : 1;
                        float ao = 1 - 0.35 * saturate(1 + se / 0.05) * (1 - qe) * _UseTopRock;
                        albedo = lerp(rk * ao, earthC, qe);
                        float3 base = normalize(lerp(rockN, earthN, qe));
                        // 平滑倒角：细节跟着表面一起绕边缘倒到 45°
                        float sm = dm < 8 ? saturate(1 - dm / bevelR(along, seed)) : 0;
                        float3 k = normalize(cross(up, outd));
                        N = sm > 0 ? normalize(rotateAxis(base, k, radians(45) * sm)) : base;
                    }
                }
                else
                {
                    // ---------- 墙面（正面 kind 2 / 侧面 kind 4）----------
                    bool side = kind > 3;
                    float sgn = side ? i.d1.y : 0;
                    float yt = i.d1.z, yb = i.d1.w, edge = i.d2.x;
                    float3 outd = side ? float3(sgn, 0, 0) : float3(0, 0, -1);
                    // 崖壁贴图：正面 u = x；侧面从这一列正面转角接着铺
                    float cu = side ? edge + sgn * (P.z - i.d2.y) : P.x;
                    float2 uc = float2(cu, P.y) * _CliffMap_ST.xy + _CliffMap_ST.zw;
                    half3 tc = SAMPLE_TEXTURE2D(_CliffMap, sampler_CliffMap, uc).rgb;
                    float3 ct = UnpackNormal(SAMPLE_TEXTURE2D(_CliffNormal, sampler_CliffNormal, uc));
                    float3 T = normalize(cross(ng, up)), B = normalize(cross(T, ng));
                    float3 Wv = normalize(T * ct.x + B * ct.y + ng * ct.z);
                    // 崖脚融合泥土
                    half3 ew = SAMPLE_TEXTURE2D(_EarthMap, sampler_EarthMap, float2(cu, P.y) / _EarthPeriod).rgb;
                    float lc = lum(tc);
                    float qf = smooth01((_FootBlend - (P.y - yb)) / _FootBlend + (lum(ew) - lc) * 1.2);
                    qf = max(qf, smooth01(1 - (P.y - yb) / 0.06));   // 贴地那一点一定是泥土，和地面同色
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
                        float per = _UseTopRock > 0.5 ? _TopRockPeriod : _EarthPeriod;
                        float2 us = float2(su, sv) / per;
                        half3 trw = _UseTopRock > 0.5 ? SAMPLE_TEXTURE2D(_TopRockMap, sampler_TopRockMap, us).rgb : SAMPLE_TEXTURE2D(_EarthMap, sampler_EarthMap, us).rgb;
                        float3 trn = _UseTopRock > 0.5 ? UnpackNormal(SAMPLE_TEXTURE2D(_TopRockNormal, sampler_TopRockNormal, us)) : UnpackNormal(SAMPLE_TEXTURE2D(_EarthNormal, sampler_EarthNormal, us));
                        float qt = smooth01((_TopBlend - (yt - P.y)) / _TopBlend + (lum(trw) - lc) * 1.2 - 0.15 + (vnoise2(float2(cu, P.y), 91, 0.08) - 0.5) * 0.5);
                        albedo = lerp(albedo, trw, qt);
                        float3 rockW = rotateAxis(topFrame(trn), k, radians(90));      // 顶面坐标 → 墙面
                        Wv = normalize(lerp(Wv, rockW, qt));
                    }
                    // 崖脚融合区：法线细节跟着颜色换成泥土的（顶面坐标 → 墙面）
                    if (qf > 0)
                    {
                        float3 en = topFrame(UnpackNormal(SAMPLE_TEXTURE2D(_EarthNormal, sampler_EarthNormal, float2(cu, P.y) / _EarthPeriod)));
                        Wv = normalize(lerp(Wv, rotateAxis(en, k, radians(90)), qf));
                    }
                    N = Wv;
                    if (_EdgeFx > 0.5)
                    {
                        // 平滑倒角：离崖顶 r 内往上翻到 45°（与顶面同一个 r，折线处对上）
                        float sw = saturate(1 - (yt - P.y) / bevelR(along, seed));
                        if (sw > 0) N = normalize(rotateAxis(N, k, -radians(45) * sw));
                        // 崖脚内凹圆角：离崖脚 r 内往上翻到 45°，和墙根前地面的 45° 在交界线对齐（地面一侧见顶面分支，种子 20–23）
                        float fseed = side ? (sgn > 0 ? 23 : 21) : 20;
                        float sf = saturate(1 - (P.y - yb) / bevelR(along, fseed));
                        if (sf > 0) N = normalize(rotateAxis(N, k, -radians(45) * sf));
                        // 竖直转角：正面在段的东西两头（那一侧更低时）转向侧面，侧面在正面转角附近转向正面，各 45°，转角处对齐
                        if (!side)
                        {
                            float packed = i.d2.w, hwS = floor(packed / 1024), heE = packed - hwS * 1024;
                            if (P.y > heE + 0.01)
                            {
                                float sv = saturate(1 - max(i.d2.z - P.x, 0) / bevelR(P.y + i.d2.z, 40));
                                if (sv > 0) N = normalize(rotateAxis(N, up, -radians(45) * sv));
                            }
                            if (P.y > hwS + 0.01)
                            {
                                float sv = saturate(1 - max(P.x - i.d2.y, 0) / bevelR(P.y + i.d2.y, 40));
                                if (sv > 0) N = normalize(rotateAxis(N, up, radians(45) * sv));
                            }
                        }
                        else
                        {
                            float sv = saturate(1 - max(P.z - i.d2.y, 0) / bevelR(P.y + edge, 40));
                            if (sv > 0) N = normalize(rotateAxis(N, up, sgn * radians(45) * sv));
                        }
                    }
                }

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
            // 双面投影：网格是空心的，边缘咬碎 / 北边裁剪开的缝里进来的光要被内侧的面挡住，否则影子会缺大块
            ZWrite On ZTest LEqual ColorMask 0 Cull Off
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
