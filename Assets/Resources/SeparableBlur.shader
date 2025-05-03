Shader "Hidden/SeparableBlur"
{
    Properties
    {
        _MainTex    ("Base (RGB)", 2D) = "white" {}
        _Direction  ("Blur Direction", Vector) = (1,0,0,0)
        _Radius     ("Blur Radius", Float)    = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4   _MainTex_TexelSize; // x = 1/width, y = 1/height
            float2   _Direction;
            float    _Radius;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv       : TEXCOORD0;
                float4 vertex   : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // compute offset in UV space
                float2 offs = _Direction * _MainTex_TexelSize.xy * _Radius;

                // three-tap separable blur
                fixed4 c0 = tex2D(_MainTex, i.uv - offs);
                fixed4 c1 = tex2D(_MainTex, i.uv);
                fixed4 c2 = tex2D(_MainTex, i.uv + offs);

                return c0 * 0.25 + c1 * 0.5 + c2 * 0.25;
            }
            ENDCG
        }
    }
    Fallback Off
}
