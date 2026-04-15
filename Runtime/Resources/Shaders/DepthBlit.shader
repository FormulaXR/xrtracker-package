Shader "Hidden/FormulaTracker/DepthBlit"
{
    // Reads URP's _CameraDepthTexture and converts to linear eye-space depth (meters).
    // Used by SceneViewRecorder to capture depth from the camera's actual depth buffer
    // instead of RenderWithShader (which bypasses URP and produces wrong values).
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D_float _MainTex;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float rawDepth = tex2D(_MainTex, i.uv).r;
                float linearDepth = LinearEyeDepth(rawDepth);
                return float4(linearDepth, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
