// One drawImage of a 2D canvas: the source texel (point sampled), scaled by globalAlpha, written premultiplied.
// _SrcBlend/_DstBlend pick the composite operation:
//   source-over       One, OneMinusSrcAlpha
//   destination-over  OneMinusDstAlpha, One
// Point sampling follows floor() of the exact source position (see frag).
// The target is a premultiplied-alpha canvas (KT EntityCanvasRenderer); Gamma colour space, so no conversions.
Shader "PixelHero/CanvasImage"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Alpha ("Global alpha", Float) = 1
        _Premultiplied ("Source is premultiplied (a canvas)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Alpha;
            float _Premultiplied;

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
                // Nearest neighbour takes floor() of the exact source position. A pixel centre that lands exactly on a
                // texel boundary (non-integer scales, e.g. zoom 3 x customScale 1.5) must pick the texel after it in
                // source order (+x, +y in the canvas image; textures are bottom-up, so +y in the image is -v).
                // GPU uv interpolation is off by up to ~0.005 texel there (measured), so bias by 0.01 texel: harmless
                // below scale 100, where a pixel centre's source position is never within 0.01 texel under a boundary.
                float2 uv = i.uv + float2(_MainTex_TexelSize.x, -_MainTex_TexelSize.y) * 0.01;
                fixed4 texel = tex2D(_MainTex, uv);
                // A canvas drawn onto another canvas is already premultiplied.
                if (_Premultiplied > 0.5) return texel * _Alpha;
                float alpha = texel.a * _Alpha;
                return fixed4(texel.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
}
