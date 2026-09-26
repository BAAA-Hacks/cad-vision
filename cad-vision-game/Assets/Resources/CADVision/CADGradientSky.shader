Shader "CADVision/GradientSky"
{
    Properties
    {
        _ZenithColor ("Upper Sky", Color) = (0.58, 0.78, 0.96, 1)
        _HorizonColor ("Horizon Haze", Color) = (0.91, 0.95, 0.98, 1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            half4 _ZenithColor, _HorizonColor;
            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 position : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.position = UnityObjectToClipPos(v.vertex);
                o.direction = v.vertex.xyz;
                return o;
            }
            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float height = saturate(normalize(i.direction).y);
                float blend = smoothstep(0.0, 0.22, height);
                return half4(lerp(_HorizonColor.rgb, _ZenithColor.rgb, blend), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
