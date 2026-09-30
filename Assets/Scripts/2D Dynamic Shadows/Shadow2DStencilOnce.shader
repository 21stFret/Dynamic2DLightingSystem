Shader "Custom/Shadow2DStencilOnce"
{
    Properties
    {
        _MainTex    ("Sprite Texture", 2D)    = "white" {}
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent"
               "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            // Shadow tint. For the ~3000 sun-shadow grass instances this is the SAME value
            // every frame (ShadowMaster derives it once from sun elevation), so it's set once
            // via Shader.SetGlobalColor from DynamicShadow2D.cs rather than per-instance —
            // zero per-object cost and doesn't block GPU instancing/batching at all.
            // The handful of auto (fire/torch) shadows genuinely vary per-instance (distance
            // to their own light) and override this per-renderer via MaterialPropertyBlock,
            // which always wins over the global value. Named _ShadowTint rather than _Color to
            // avoid colliding with the global _Color namespace other shaders may read.
            fixed4 _ShadowTint;

            struct appdata_t
            {
                float4 vertex:POSITION;
                float2 uv:TEXCOORD0;
                fixed4 color:COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos:SV_POSITION;
                float2 uv:TEXCOORD0;
                fixed4 color:COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = v.uv;
                o.color = v.color * _ShadowTint;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - 0.01);
                return c;
            }
            ENDCG
        }
    }
}
