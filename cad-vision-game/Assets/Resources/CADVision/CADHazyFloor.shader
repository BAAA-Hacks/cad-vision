Shader "CADVision/HazyFloor"
{
    Properties
    {
        _Color ("Floor", Color) = (1, 1, 1, 1)
        _HazeColor ("Horizon Haze", Color) = (0.91, 0.95, 0.98, 1)
        _HazeStart ("Haze Start (metres)", Float) = 60
        _HazeDensity ("Haze Density", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows finalcolor:ApplyHaze nofog
        #pragma target 3.0
        #pragma multi_compile_instancing
        fixed4 _Color, _HazeColor;
        float _HazeStart, _HazeDensity;
        struct Input { float3 worldPos; };
        void surf(Input IN, inout SurfaceOutput o)
        {
            o.Albedo = _Color.rgb * 0.85;
            o.Emission = _Color.rgb * 0.15;
            o.Alpha = 1;
        }
        void ApplyHaze(Input IN, SurfaceOutput o, inout fixed4 color)
        {
            float distanceToEye = distance(IN.worldPos, _WorldSpaceCameraPos);
            float visibility = exp2(-max(0, distanceToEye - _HazeStart) * _HazeDensity);
            #ifdef UNITY_PASS_FORWARDADD
                color.rgb *= visibility;
            #else
                color.rgb = lerp(_HazeColor.rgb, color.rgb, visibility);
            #endif
        }
        ENDCG
    }
    Fallback "Diffuse"
}
