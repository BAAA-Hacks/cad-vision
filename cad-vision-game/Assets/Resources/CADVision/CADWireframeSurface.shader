// Wireframe-mode surface (Built-in Render Pipeline, Quest-safe): see-through faces.
// Pass 1 writes depth only, so the nearest face still hides the faces behind it, hidden edges
// can be drawn faintly behind it (selection edges too). Pass 2 blends a
// faint pale color over whatever is behind (sky, floor, passthrough). The alpha channel
// accumulates as "over", so passthrough stays visible through the faces.
// Queue 2980: after the skybox and opaque scene, before UI canvases (3000), so menus are never
// painted over. Single-pass-instanced stereo safe.
Shader "CADVision/WireframeSurface"
{
    Properties
    {
        _Color ("Color (alpha = opacity)", Color) = (0.86, 0.89, 0.93, 0.15)
        _Shade ("Shade (darkening of grazing faces)", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        CGINCLUDE
        #include "UnityCG.cginc"

        fixed4 _Color;
        float _Shade;

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float facing : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vert(appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_OUTPUT(v2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            o.pos = UnityObjectToClipPos(v.vertex);
            float3 worldNormal = UnityObjectToWorldNormal(v.normal);
            float3 toEye = normalize(_WorldSpaceCameraPos - mul(unity_ObjectToWorld, v.vertex).xyz);
            o.facing = saturate(abs(dot(worldNormal, toEye)));
            return o;
        }
        ENDCG

        Pass
        {
            Name "Depth"
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            fixed4 fragDepth(v2f i) : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Name "Surface"
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(_Color.rgb * (1.0 - _Shade * (1.0 - i.facing)), _Color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
