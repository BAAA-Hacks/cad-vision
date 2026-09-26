// Selection outline (inverted hull) for the Built-in Render Pipeline.
// Draws the back faces of a mesh pushed outward along its (smoothed) normals by a constant
// screen-space width, so only a rim around the silhouette is visible. Unlit, one pass,
// single-pass-instanced stereo safe (Quest renders both eyes in one instanced pass).
Shader "CADVision/OutlineHull"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0, 0.9, 1, 1)
        _OutlineWidth ("Outline Width (fraction of view height)", Range(0, 0.02)) = 0.004
    }

    SubShader
    {
        // After opaque CAD geometry so the object's own depth hides the hull's interior.
        Tags { "Queue" = "Geometry+10" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Outline"
            Cull Front
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _OutlineColor;
            float _OutlineWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 clipPos = UnityObjectToClipPos(v.vertex);
                // Normal direction in clip space (xy), independent of object scale.
                float3 viewNormal = mul((float3x3)UNITY_MATRIX_IT_MV, v.normal);
                float2 clipNormal = mul((float2x2)UNITY_MATRIX_P, viewNormal.xy);
                float len = length(clipNormal);
                if (len > 1e-5)
                    clipNormal /= len;
                // Multiply by w for a constant on-screen width at any distance; x2 = NDC range.
                clipPos.xy += clipNormal * _OutlineWidth * 2.0 * clipPos.w;
                o.pos = clipPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }
    }

    Fallback Off
}
