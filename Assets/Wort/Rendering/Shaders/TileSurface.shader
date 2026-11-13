// 模块化地形瓦片（Tileset，如 Art/Tilesets/Limestone）的表面着色器，替代 URP Lit。属性名与 URP Lit 相同（_BaseMap / _BumpMap / _BumpScale / _BaseColor），
// 换着色器后材质上原有的数值照用。
// - 光照（负责人 2026-11-12：颜色贴图来自参考图，明暗是画进去的，不能再按法线完整打一遍光）：
//   漫反射 = lerp(几何法线的 N·L, 法线贴图的 N·L, _DetailNormal)。几何法线只给整块面的明暗（随时间 / 方向变化），
//   法线贴图只加一层弱细节，石块的体积主要靠颜色贴图本身。环境光用几何法线。
// - 投影：_Projection = 0 用网格 UV（崖壁、顶沿，跟着瓦片旋转）；1 = 世界 xz（顶面砂土，跨格连续，没有格间接缝），_WorldTile 米一张。
// - 路面：全局遮罩 _WortGroundMask（R = 路面权重，由 TerrainLab 按 RoadSurface 烘焙，覆盖 _WortGroundMaskRect = (x0, z0, 宽, 深) 的世界范围），
//   只在 _RoadOn = 1 的材质（顶面砂土）朝上的面生效；交界公式与 Wort.Generation.TerrainTextures.MixRoad 相同（路面贴图 A = 高度，砂土 A = 高度，没有时取 0.5）。
//   全局 _WortGroundMaskOn = 0 时不画路面。
// - 唇边接缝（_SeamOn = 1，用在崖顶墙件 Crown 的崖壁和唇边两个材质上；负责人 2026-11-12：接缝太明显）：
//   墙件 UV 纵向 = 件内高度，唇边下沿在 _LipY。交界两侧 _SeamWidth 内，两个材质用同一个公式逐像素决定显示“浅色唇边”还是“深色崖壁”：
//   P = 0.5 + 到交界的距离 / 宽度 + (崖壁贴图明暗 − 0.3) × _SeamNoise —— 深色石缝往上咬进唇边、亮的石面往下变浅，交界跟着石块轮廓走；
//   两侧算出的交界线连续。浅色一侧取唇边贴图在交界上方的镜像（不用唇边贴图下部的竖条纹）。交界下方 _ShadowDepth 内加淡暗影（_LipShadow）。
//   _SeamOn = 2（单网格墙件，OliveStrata / RustCrag 的 Crown）：整块用崖壁贴图（_BaseMap / _BumpMap），唇边以上直接取 _SeamLip（原 Crown 贴图），交界照上面的公式融合。
Shader "Wort/TileSurface"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _DetailNormal ("Normal Map Lighting Weight", Range(0, 1)) = 0.35
        [Enum(MeshUV, 0, WorldXZ, 1)] _Projection ("Projection", Float) = 0
        _WorldTile ("World Tile Size (m)", Float) = 1
        [Toggle] _RoadOn ("Road Overlay", Float) = 0
        _RoadTex ("Road (RGB, A = height)", 2D) = "gray" {}
        [Enum(Off, 0, TwoSubmesh, 1, SingleMesh, 2)] _SeamOn ("Lip Seam Blend (Crown walls)", Float) = 0
        _SeamCliff ("Seam: Cliff Map", 2D) = "gray" {}
        _SeamLip ("Seam: Lip Map", 2D) = "white" {}
        _LipY ("Lip Bottom (UV v)", Float) = 0.88
        _SeamWidth ("Seam Width (m)", Float) = 0.07
        _SeamNoise ("Seam Irregularity", Float) = 3
        _LipShadow ("Lip Shadow", Range(0, 1)) = 0.12
        _ShadowDepth ("Lip Shadow Depth (m)", Float) = 0.14
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            // Forward+（PC 渲染器用的就是它）：不声明这两个关键字时，URP 的 GetMainLight 会从逐物体灯光数据取衰减，
            // 而 Forward+ 不填这份数据 → 太阳光被乘成 0，只剩环境光（负责人 2026-11-12：瓦片不受光、没影子）。Unity 6 叫 _CLUSTER_LIGHT_LOOP，旧版叫 _FORWARD_PLUS。
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            TEXTURE2D(_RoadTex); SAMPLER(sampler_RoadTex);
            TEXTURE2D(_SeamCliff); SAMPLER(sampler_SeamCliff);
            TEXTURE2D(_SeamLip); SAMPLER(sampler_SeamLip);
            TEXTURE2D(_WortGroundMask); SAMPLER(sampler_WortGroundMask);
            float4 _WortGroundMaskRect;
            float _WortGroundMaskOn;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float4 _RoadTex_ST;
                half4 _BaseColor;
                float _BumpScale;
                float _DetailNormal;
                float _Projection;
                float _WorldTile;
                float _RoadOn;
                float _SeamOn;
                float _LipY;
                float _SeamWidth;
                float _SeamNoise;
                float _LipShadow;
                float _ShadowDepth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                float fog : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs nIn = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = nIn.normalWS;
                o.tangentWS = float4(nIn.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = v.uv;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 ng = normalize(i.normalWS);
                float3 t = normalize(i.tangentWS.xyz);
                float3 b = cross(ng, t) * i.tangentWS.w;
                float2 uv;
                if (_Projection > 0.5)
                {
                    // 世界 xz：顶面砂土跨格连续；切线基换成世界 x / z，法线贴图方向也一致
                    uv = i.positionWS.xz / max(_WorldTile, 1e-3);
                    t = float3(1, 0, 0); b = float3(0, 0, 1);
                }
                else uv = i.uv;
                half4 baseC = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(uv, _BaseMap)) * _BaseColor;
                half3 nt = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, TRANSFORM_TEX(uv, _BumpMap)), _BumpScale);
                float3 nm = normalize(t * nt.x + b * nt.y + ng * nt.z);

                half3 albedo = baseC.rgb;
                half lipShade = 1;
                if (_SeamOn > 0.5)
                {
                    float d = i.uv.y - _LipY; // > 0 在唇边上，< 0 在崖壁上（m，墙件 1m 高）
                    if (abs(d) < _SeamWidth * 2.5 || d < 0 && -d < _ShadowDepth)
                    {
                        half3 cc = SAMPLE_TEXTURE2D(_SeamCliff, sampler_SeamCliff, i.uv).rgb * _BaseColor.rgb;
                        half3 lc = SAMPLE_TEXTURE2D(_SeamLip, sampler_SeamLip, float2(i.uv.x, _LipY + abs(d) + 0.005)).rgb * _BaseColor.rgb;
                        float hc = dot(cc, half3(0.299, 0.587, 0.114));
                        float P = saturate(0.5 + d / _SeamWidth + (hc - 0.3) * _SeamNoise);
                        P = P * P * (3 - 2 * P);
                        albedo = lerp(cc, lc, P);
                        lipShade = 1 - _LipShadow * (1 - P) * (1 - smoothstep(0, _ShadowDepth, max(-d, 0)));
                    }
                    else if (_SeamOn > 1.5 && d > 0)
                        albedo = SAMPLE_TEXTURE2D(_SeamLip, sampler_SeamLip, i.uv).rgb * _BaseColor.rgb; // 单网格墙件：唇边以上直接用唇边贴图
                }
                if (_RoadOn > 0.5 && _WortGroundMaskOn > 0.5 && ng.y > 0.6)
                {
                    float2 muv = (i.positionWS.xz - _WortGroundMaskRect.xy) / _WortGroundMaskRect.zw;
                    float wr = SAMPLE_TEXTURE2D(_WortGroundMask, sampler_WortGroundMask, muv).r;
                    if (wr > 0.001)
                    {
                        half4 rd = SAMPLE_TEXTURE2D(_RoadTex, sampler_RoadTex, i.positionWS.xz);
                        float dh = baseC.a < 0.999 ? baseC.a : 0.5;
                        float tr = wr + (rd.a - dh) * 0.8 * (1 - abs(2 * wr - 1));
                        float q = saturate((tr - 0.5) / 0.15 + 0.5);
                        albedo = lerp(albedo, rd.rgb, q);
                    }
                }

                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float ndl = lerp(saturate(dot(ng, light.direction)), saturate(dot(nm, light.direction)), _DetailNormal);
                half3 diffuse = light.color * ndl * light.shadowAttenuation; // 主光源不乘 distanceAttenuation（方向光本来就是 1；见上面 Forward+ 的说明）
                half3 color = albedo * (diffuse + SampleSH(ng)) * lipShade;
                color = MixFog(color, i.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }
}
