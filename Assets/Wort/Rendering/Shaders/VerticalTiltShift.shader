Shader "Wort/Vertical Tilt Shift"
{
    Properties
    {
        _FocusHeight ("Focus Height (World Y)", Float) = 0.85
        _ClearHalfHeight ("Clear Half Height (World Units)", Range(0, 6)) = 0.9
        _FadeHeight ("Blur Transition (World Units)", Range(0.05, 8)) = 1.5
        _BlurRadius ("Maximum Radius (Pixels at 1080p)", Range(0, 24)) = 6
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Vertical Tilt Shift"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _FocusHeight;
                float _ClearHalfHeight;
                float _FadeHeight;
                float _BlurRadius;
            CBUFFER_END

            half4 ReadColor(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 screenUV = (uv - _BlitScaleBias.zw) / _BlitScaleBias.xy;
                half4 center = ReadColor(uv);
                float depth = SampleSceneDepth(screenUV);
                #if UNITY_REVERSED_Z
                    if (depth <= 0.0)
                        return center;
                #else
                    if (depth >= 1.0)
                        return center;
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
                #endif

                float3 worldPosition = ComputeWorldSpacePosition(screenUV, depth, UNITY_MATRIX_I_VP);
                float heightDistance = abs(worldPosition.y - _FocusHeight);
                float blur = smoothstep(_ClearHalfHeight, _ClearHalfHeight + _FadeHeight, heightDistance);
                if (blur <= 0.0 || _BlurRadius <= 0.0)
                    return center;

                float2 screenSize = GetScaledScreenParams().xy;
                float2 stepUV = _BlitScaleBias.xy * float2(screenSize.y / screenSize.x, 1.0) *
                    (blur * _BlurRadius / (1080.0 * 1.414214));
                float2 dx = float2(stepUV.x, 0.0);
                float2 dy = float2(0.0, stepUV.y);
                half3 color = center.rgb * 4.0;
                color += (ReadColor(uv - dx).rgb + ReadColor(uv + dx).rgb +
                          ReadColor(uv - dy).rgb + ReadColor(uv + dy).rgb) * 2.0;
                color += ReadColor(uv - dx - dy).rgb + ReadColor(uv - dx + dy).rgb +
                         ReadColor(uv + dx - dy).rgb + ReadColor(uv + dx + dy).rgb;
                return half4(color * 0.0625, center.a);
            }
            ENDHLSL
        }
    }
}
