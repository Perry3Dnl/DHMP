Shader "DHMP/ArenaUnlit"
{
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 position : SV_POSITION; float shade : TEXCOORD0; };
            fixed4 _Color;
            v2f vert(appdata v)
            {
                v2f o; o.position = UnityObjectToClipPos(v.vertex);
                o.shade = 0.6 + 0.4 * saturate(dot(UnityObjectToWorldNormal(v.normal), normalize(float3(-0.4, 1, -0.5))));
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return fixed4(_Color.rgb * i.shade, _Color.a); }
            ENDCG
        }
    }
}
