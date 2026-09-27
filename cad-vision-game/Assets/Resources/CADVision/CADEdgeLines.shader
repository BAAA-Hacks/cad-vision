// CAD edge overlay lines (Built-in Render Pipeline, Quest-safe).
// Draws a MeshTopology.Lines mesh (feature edges extracted on the CPU) in a flat color. No
// geometry shaders or GL.wireframe. Depth bias is applied in the vertex shader (polygon offset
// does not apply to line primitives on every GPU): vertices are pulled toward the eye by a
// small fraction of their view depth, so edges win against their own surfaces without z-fighting.
// _HiddenOnly = 1 draws only the parts hidden behind surfaces (ZTest Greater), blended, for the
// Wireframe mode's faint hidden lines. Single-pass-instanced stereo safe.
Shader "CADVision/EdgeLines"
{
    Properties
    {
        _Color ("Color", Color) = (0.08, 0.09, 0.11, 1)
        _DepthBias ("Depth Bias (fraction of view depth)", Range(0, 0.02)) = 0.002
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4 // LessEqual
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1 // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0 // Zero
    }

    SubShader
    {
        // After opaque CAD surfaces and the selection outline hull (Geometry+10).
        Tags { "Queue" = "Geometry+20" "RenderType" = "Opaque" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Edges"
            Cull Off
            ZWrite Off
            ZTest [_ZTest]
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _DepthBias;

            struct appdata
            {
                float4 vertex : POSITION;
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

                float3 viewPos = UnityObjectToViewPos(v.vertex.xyz);
                viewPos *= 1.0 - _DepthBias; // Toward the eye (view space origin).
                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }

    Fallback Off
}
