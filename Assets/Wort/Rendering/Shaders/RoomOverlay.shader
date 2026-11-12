// 房间提示层（调试用，负责人 2026-11-12）：贴在地面上方的一层薄薄的半透明色块。
// 顶点色 rgb = 房间颜色；a 区分部位：≈0.25 色块（平整区域；内收 1 格、台地可生成的区域在 1cm 高处再叠一层，叠起来颜色更深），≈1 描边。
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
                float alpha = i.color.a > 0.75 ? 0.8 : 0.2; // 描边 / 色块（两层色块叠起来就是更深的颜色）
                return half4(i.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
