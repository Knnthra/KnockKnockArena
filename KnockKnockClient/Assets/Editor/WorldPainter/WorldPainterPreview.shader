// Editor-only preview shader for the World Painter scene brush.
// Draws GPU-instanced cubes with a flat color and a simple directional shade so
// block shapes read in 3D. Blend/ZWrite are material-configurable so the same
// shader serves both the opaque block cubes and the translucent zone overlays.
Shader "Hidden/WorldPainterPreview"
{
    Properties
    {
        _Color    ("Color",  Color) = (1, 1, 1, 1)
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
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed  shade : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 wn = UnityObjectToWorldNormal(v.normal);
                o.shade = 0.72 + 0.28 * saturate(dot(wn, normalize(float3(0.5, 0.85, 0.35))));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(_Color.rgb * i.shade, _Color.a);
            }
            ENDCG
        }
    }
}
