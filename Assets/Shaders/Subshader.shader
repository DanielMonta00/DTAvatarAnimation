Shader "Hidden/UnlitWhite"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual
            Fog { Mode Off }
            ColorMask RGBA
            Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma prefer_hlsl11
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ UNITY_HDR_ON
            #include "UnityCG.cginc"

            struct appdata 
            { 
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            
            struct v2f 
            { 
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Pure white with full alpha for clean masks
                return fixed4(1.0, 1.0, 1.0, 1.0);
            }

            ENDCG
        }
    }
    Fallback Off
}