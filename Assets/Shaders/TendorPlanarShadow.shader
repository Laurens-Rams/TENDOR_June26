Shader "Tendor/PlanarShadow"
{
    Properties
    {
        _ShadowColor ("Shadow Color", Color) = (0.3, 0.3, 0.3, 0.55)
        _SurfacePadding ("Surface Padding", Float) = 0.015
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "PlanarShadow"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ShadowColor;
                float4 _PlanePoint;
                float4 _PlaneNormal;
                float4 _LightDirection;
                float _SurfacePadding;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 ProjectOntoPlane(float3 worldPos)
            {
                float3 planePoint = _PlanePoint.xyz;
                float3 planeNormal = normalize(_PlaneNormal.xyz);
                float3 lightDir = normalize(_LightDirection.xyz);
                float denom = dot(planeNormal, lightDir);
                if (abs(denom) < 1e-4)
                    return worldPos + planeNormal * _SurfacePadding;

                float t = dot(planePoint - worldPos, planeNormal) / denom;
                if (t < 0.0)
                    return worldPos + planeNormal * _SurfacePadding;

                return worldPos + lightDir * t + planeNormal * _SurfacePadding;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                worldPos = ProjectOntoPlane(worldPos);
                output.positionCS = TransformWorldToHClip(worldPos);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return _ShadowColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
