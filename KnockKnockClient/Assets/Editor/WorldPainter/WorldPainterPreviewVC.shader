// Editor-only vertex-color shader for the World Painter preview mesh.
// Shading is baked into the vertex colors at build time, so this does no
// lighting work at all — the cheapest possible way to draw the block preview.
Shader "Hidden/WorldPainterPreviewVC"
{
    Properties
    {
        _SrcBlend ("Src",    Float) = 1   // One
        _DstBlend ("Dst",    Float) = 0   // Zero
        _ZWrite   ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
