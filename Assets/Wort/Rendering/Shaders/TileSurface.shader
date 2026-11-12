// 模块化地形瓦片（Tileset，如 Art/Tilesets/Limestone）的表面着色器，替代 URP Lit。属性名与 URP Lit 相同（_BaseMap / _BumpMap / _BumpScale / _BaseColor），
// 换着色器后材质上原有的数值照用。
// - 光照（负责人 2026-11-12：颜色贴图来自参考图，明暗是画进去的，不能再按法线完整打一遍光）：
//   漫反射 = lerp(几何法线的 N·L, 法线贴图的 N·L, _DetailNormal)。几何法线只给整块面的明暗（随时间 / 方向变化），
//   法线贴图只加一层弱细节，石块的体积主要靠颜色贴图本身。环境光用几何法线。
// - 投影：_Projection = 0 用网格 UV（崖壁、顶沿，跟着瓦片旋转）；1 = 世界 xz（顶面砂土，跨格连续，没有格间接缝），_WorldTile 米一张。
// - 路面：全局遮罩 _WortGroundMask（R = 路面权重，由 TerrainLab 按 RoadSurface 烘焙，覆盖 _WortGroundMaskRect = (x0, z0, 宽, 深) 的世界范围），
//   只在 _RoadOn = 1 的材质（顶面砂土）朝上的面生效；交界公式与 Wort.Generation.TerrainTextures.MixRoad 相同（路面贴图 A = 高度，砂土 A = 高度，没有时取 0.5）。
//   全局 _WortGroundMaskOn = 0 时不画路面。
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
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            TEXTURE2D(_RoadTex); SAMPLER(sampler_RoadTex);
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
                half3 diffuse = light.color * ndl * light.shadowAttenuation * light.distanceAttenuation;
                half3 color = albedo * (diffuse + SampleSH(ng));
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
