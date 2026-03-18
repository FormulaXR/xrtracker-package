Shader "Hidden/EdgeOutline"
{
    Properties
    {
        _Color ("Color", Color) = (0, 1, 1, 1)
        _Width ("Width", Float) = 3
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Overlay"
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "EdgeOutlineLines"

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Width;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;   // this endpoint
                float3 otherOS    : NORMAL;     // other endpoint (repurposed)
                float2 params     : TEXCOORD0;  // x = side (-1 or +1)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;

                float4 clipPos   = TransformObjectToHClip(input.positionOS);
                float4 clipOther = TransformObjectToHClip(input.otherOS);

                // Screen-space positions in pixels
                // _ProjectionParams.x is -1 when projection is flipped (DirectX)
                float flipY = _ProjectionParams.x;
                float2 screenPos   = float2(clipPos.x / clipPos.w, clipPos.y / clipPos.w * flipY) * 0.5 + 0.5;
                float2 screenOther = float2(clipOther.x / clipOther.w, clipOther.y / clipOther.w * flipY) * 0.5 + 0.5;
                screenPos   *= _ScreenParams.xy;
                screenOther *= _ScreenParams.xy;

                // Edge direction + perpendicular in pixel space
                float2 dir  = screenOther - screenPos;
                float  len  = length(dir);
                dir = len > 0.001 ? dir / len : float2(1, 0);
                float2 perp = float2(-dir.y, dir.x);

                // Offset in pixels -> NDC -> clip space
                float2 offsetPx  = perp * input.params.x * _Width * 0.5;
                float2 offsetNDC = offsetPx * 2.0 / _ScreenParams.xy;
                clipPos.x += offsetNDC.x * clipPos.w;
                clipPos.y += offsetNDC.y * clipPos.w * flipY;

                // Slight depth bias to draw in front of occluder
                #if defined(UNITY_REVERSED_Z)
                    clipPos.z += 0.0001 * clipPos.w;
                #else
                    clipPos.z -= 0.0001 * clipPos.w;
                #endif

                output.positionCS = clipPos;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
