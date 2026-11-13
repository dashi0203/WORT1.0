// 地形融合着色器（《地形外观与崖面美术规范》方案 B）：泥土 / 岩石两张贴图按顶点色 r（CliffMesher 写的融合权重）混合；
// 路面贴图按顶点色 g（RoadSurface 的路面权重）先盖在泥土上，岩石再盖在最上面。路面交界公式与 TerrainTextures.MixRoad 相同。
// - 投影：顶面用世界 xz，侧面用沿墙坐标和 y（按法线选），1m 一张；再取一组 2.3m 一张的坐标平均，减轻平铺重复；
// - 交界：权重 + 两张贴图 alpha（高度）之差，高的先露出来，公式与 Wort.Generation.TerrainTextures.Mix 相同；
// - 光照：主光漫反射 + 阴影 + 云影（主光源 cookie）+ 环境光（SH）× SSAO。阴影投射 / 深度沿用 URP Lit 的 pass。
Shader "Wort/TerrainBlend"
{
    Properties
    {
        _DirtTex ("Dirt (RGB, A = height)", 2D) = "white" {}
        _RockTex ("Rock (RGB, A = height)", 2D) = "gray" {}
        _RoadTex ("Road (RGB, A = height)", 2D) = "gray" {}
        _Contrast ("Height Contrast", Float) = 0.6
        _EdgeWidth ("Edge Width", Float) = 0.12
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
            #pragma multi_compile_fragment _ _LIGHT_COOKIES            // 主光源 cookie = 云影
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION   // URP SSAO（PC_Renderer 里的 Renderer Feature）
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WortAO.hlsl"   // 世界空间 AO，参数见 Wort.Rendering.WorldAO

            TEXTURE2D(_DirtTex); SAMPLER(sampler_DirtTex);
            TEXTURE2D(_RockTex); SAMPLER(sampler_RockTex);
            TEXTURE2D(_RoadTex); SAMPLER(sampler_RoadTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _DirtTex_ST;
                float4 _RockTex_ST;
                float4 _RoadTex_ST;
                float _Contrast;
                float _EdgeWidth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 color : COLOR; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float blend : TEXCOORD2;
                float road : TEXCOORD4;
                float fog : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.blend = v.color.r;
                o.road = v.color.g;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float2 uv = abs(n.y) > 0.6 ? i.positionWS.xz : (abs(n.z) > abs(n.x) ? i.positionWS.xy : i.positionWS.zy);
                float2 uv2 = uv * 0.43 + float2(0.31, 0.17);
                half4 d = (SAMPLE_TEXTURE2D(_DirtTex, sampler_DirtTex, uv) + SAMPLE_TEXTURE2D(_DirtTex, sampler_DirtTex, uv2)) * 0.5;
                half4 r = (SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, uv) + SAMPLE_TEXTURE2D(_RockTex, sampler_RockTex, uv2)) * 0.5;
                // 路面盖在泥土上（只在朝上的面）
                float q = 0;
                if (i.road > 0.001 && n.y > 0.6)
                {
                    half4 rd = (SAMPLE_TEXTURE2D(_RoadTex, sampler_RoadTex, uv) + SAMPLE_TEXTURE2D(_RoadTex, sampler_RoadTex, uv2)) * 0.5;
                    float wr = i.road;
                    float tr = wr + (rd.a - d.a) * 0.8 * (1 - abs(2 * wr - 1));
                    q = saturate((tr - 0.5) / 0.15 + 0.5);
                    d.rgb = lerp(d.rgb, rd.rgb, q);
                }
                float w = i.blend;
                float t = w + (r.a - d.a) * _Contrast * (1 - abs(2 * w - 1));
                float m = saturate((t - 0.5) / _EdgeWidth + 0.5);
                half3 albedo = lerp(d.rgb, r.rgb, m);

                // 带 positionWS 的重载才会乘主光源 cookie（云影，Wort.Rendering.SkyLighting 生成）
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, half4(1, 1, 1, 1));
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                float aoDbg; float wao = WortAO(i.positionWS, n, aoDbg);
                if (aoDbg >= 0) return half4(aoDbg.xxx, 1);
                half3 diffuse = light.color * saturate(dot(n, light.direction)) * light.shadowAttenuation * ao.directAmbientOcclusion * WortAODirect(wao); // 主光源不乘 distanceAttenuation（方向光本来就是 1；见上面 Forward+ 的说明）
                half3 color = albedo * (diffuse + SampleSH(n) * ao.indirectAmbientOcclusion * wao);
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
