// A premultiplied-alpha canvas (EntityCanvasRenderer render texture) laid over the scene like a stacked HTML
// canvas: Blend One OneMinusSrcAlpha, scaled by _Alpha (the CSS opacity: 1, or 0.2 for #entities-mask).
Shader "PixelHero/CanvasLayer"
{
    Properties
    {
        _MainTex ("Canvas", 2D) = "black" {}
        _Alpha ("Opacity", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Alpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * _Alpha;
            }
            ENDCG
        }
    }
}
