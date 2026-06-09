Shader "Hidden/TENDOR/CharacterCameraSoftening"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "CharacterCameraSoftening"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            SAMPLER(sampler_BlitTexture);

            float _Strength;
            float _MinBlend;
            float _BlurRadius;
            float _DepthStrength;
            // Camera tone match (applied to CG geometry only, via depth mask).
            float _MatchStrength;   // 0 = off, 1 = full grade
            float _MatchContrast;   // <1 lowers contrast around mid-grey
            float _MatchSaturation; // <1 desaturates toward luma
            float _MatchBlackLift;  // lifts crushed blacks to match the camera floor

            half Luma(half3 rgb)
            {
                return dot(rgb, half3(0.2126h, 0.7152h, 0.0722h));
            }

            half4 SampleColor(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv);
            }

            float SampleDepth01(float2 uv)
            {
                return Linear01Depth(SampleSceneDepth(uv), _ZBufferParams);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 texel = _BlitTexture_TexelSize.xy * _BlurRadius;

                half4 center = SampleColor(uv);

                // 9-tap box blur
                half3 blur = (
                    SampleColor(uv + float2(texel.x, 0)).rgb +
                    SampleColor(uv + float2(-texel.x, 0)).rgb +
                    SampleColor(uv + float2(0, texel.y)).rgb +
                    SampleColor(uv + float2(0, -texel.y)).rgb +
                    SampleColor(uv + float2(texel.x, texel.y)).rgb +
                    SampleColor(uv + float2(-texel.x, texel.y)).rgb +
                    SampleColor(uv + float2(texel.x, -texel.y)).rgb +
                    SampleColor(uv + float2(-texel.x, -texel.y)).rgb +
                    center.rgb
                ) * 0.111h;

                half colorEdge = abs(Luma(center.rgb) - Luma(blur));

                // Depth discontinuities catch CG silhouettes against the AR feed.
                float depthC = SampleDepth01(uv);
                float depthEdge = abs(depthC - SampleDepth01(uv + float2(texel.x, 0)))
                                + abs(depthC - SampleDepth01(uv + float2(0, texel.y)));

                half blend = saturate(colorEdge * _Strength + depthEdge * _DepthStrength + _MinBlend);
                half3 outRgb = lerp(center.rgb, blur, blend);

                // Tone-match the character to the soft AR feed: knock down CG contrast/saturation and lift blacks.
                // CG geometry writes depth (depthC < 1); the camera feed is a background blit at depthC ~= 1, so the
                // real feed is left untouched. This is a few ALU ops in a pass that already runs — no extra cost.
                half charMask = (1.0h - saturate((depthC - 0.95) / 0.05)) * _MatchStrength;
                if (charMask > 0.0001h)
                {
                    half3 graded = outRgb;
                    half l = Luma(graded);
                    graded = lerp(l.xxx, graded, _MatchSaturation);
                    graded = (graded - 0.5h) * _MatchContrast + 0.5h;
                    graded = graded * (1.0h - _MatchBlackLift) + _MatchBlackLift;
                    outRgb = lerp(outRgb, graded, charMask);
                }

                return half4(outRgb, center.a);
            }
            ENDHLSL
        }
    }
}
