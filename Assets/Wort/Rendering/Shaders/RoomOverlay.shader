// 房间提示层（调试用，负责人 2026-11-12）：贴在地面上方的一层薄薄的半透明色块。
// 顶点色 rgb = 房间颜色；a 区分部位：≈0.25 平整区域（淡色），≈0.5 内收 1 格（台地可生成：淡色 + 斜条纹），≈1 描边。
// 不写深度、两面都画、轻微往前偏移，避免和地面闪烁。
Shader "Wort/RoomOverlay"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "RoomOverlay"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 color : COLOR; float3 positionWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float a = i.color.a;
                float alpha;
                if (a > 0.75)
                    alpha = 0.8;                                            // 描边
                else if (a > 0.4)
                {
                    float s = frac((i.positionWS.x + i.positionWS.z) * 1.5); // 斜条纹，约 0.47m 一条
                    alpha = s < 0.3 ? 0.45 : 0.18;                          // 内收 1 格：台地可生成
                }
                else
                    alpha = 0.18;                                           // 平整区域
                return half4(i.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
