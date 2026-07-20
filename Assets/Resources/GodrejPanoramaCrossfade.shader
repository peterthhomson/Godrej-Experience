Shader "Godrej/Skybox Panorama Crossfade"
{
    Properties
    {
        [NoScaleOffset] _CubeA ("Current Cubemap", Cube) = "grey" {}
        [HideInInspector] _CubeA_HDR ("Current Decode", Vector) = (1, 1, 0, 1)
        [NoScaleOffset] _PanoA ("Current Panorama", 2D) = "grey" {}
        [HideInInspector] _PanoA_HDR ("Current Panorama Decode", Vector) = (1, 1, 0, 1)
        [HideInInspector] _ProjectionA ("Current Projection", Float) = 0
        [HideInInspector] _LayoutA ("Current Stereo Layout", Float) = 0
        _TintA ("Current Tint", Color) = (.5, .5, .5, .5)
        _ExposureA ("Current Exposure", Range(0, 8)) = 1
        _RotationA ("Current Rotation", Range(0, 360)) = 0

        [NoScaleOffset] _CubeB ("Next Cubemap", Cube) = "grey" {}
        [HideInInspector] _CubeB_HDR ("Next Decode", Vector) = (1, 1, 0, 1)
        [NoScaleOffset] _PanoB ("Next Panorama", 2D) = "grey" {}
        [HideInInspector] _PanoB_HDR ("Next Panorama Decode", Vector) = (1, 1, 0, 1)
        [HideInInspector] _ProjectionB ("Next Projection", Float) = 0
        [HideInInspector] _LayoutB ("Next Stereo Layout", Float) = 0
        _TintB ("Next Tint", Color) = (.5, .5, .5, .5)
        _ExposureB ("Next Exposure", Range(0, 8)) = 1
        _RotationB ("Next Rotation", Range(0, 360)) = 0

        _Blend ("Blend", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            samplerCUBE _CubeA;
            half4 _CubeA_HDR;
            sampler2D _PanoA;
            half4 _PanoA_HDR;
            half _ProjectionA;
            half _LayoutA;
            half4 _TintA;
            half _ExposureA;
            float _RotationA;

            samplerCUBE _CubeB;
            half4 _CubeB_HDR;
            sampler2D _PanoB;
            half4 _PanoB_HDR;
            half _ProjectionB;
            half _LayoutB;
            half4 _TintB;
            half _ExposureB;
            float _RotationB;
            half _Blend;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float3 directionA : TEXCOORD0;
                float3 directionB : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 RotateAroundYInDegrees(float3 vertex, float degrees)
            {
                float radians = degrees * UNITY_PI / 180.0;
                float sine;
                float cosine;
                sincos(radians, sine, cosine);
                float2x2 rotation = float2x2(cosine, -sine, sine, cosine);
                return float3(mul(rotation, vertex.xz), vertex.y).xzy;
            }

            float2 DirectionToEquirectangularUv(float3 direction)
            {
                direction = normalize(direction);
                float latitude = acos(clamp(direction.y, -1.0, 1.0));
                float longitude = atan2(direction.z, direction.x);
                float2 spherical = float2(longitude, latitude) *
                    float2(0.5 / UNITY_PI, 1.0 / UNITY_PI);
                return float2(0.5, 1.0) - spherical;
            }

            float2 ApplyStereoLayout(float2 uv, half layout)
            {
                if (layout > 0.5h && layout < 1.5h)
                {
                    // Side-by-side: left eye in the left half, right eye in the right.
                    uv.x = uv.x * 0.5 + (unity_StereoEyeIndex == 0 ? 0.0 : 0.5);
                }
                else if (layout >= 1.5h)
                {
                    // Over-under: Unity convention is left eye on top, right on bottom.
                    uv.y = uv.y * 0.5 + (unity_StereoEyeIndex == 0 ? 0.5 : 0.0);
                }

                return uv;
            }

            half3 SampleCurrent(float3 direction)
            {
                if (_ProjectionA < 0.5h)
                {
                    return DecodeHDR(texCUBE(_CubeA, direction), _CubeA_HDR);
                }

                float2 uv = ApplyStereoLayout(DirectionToEquirectangularUv(direction), _LayoutA);
                return DecodeHDR(tex2D(_PanoA, uv), _PanoA_HDR);
            }

            half3 SampleNext(float3 direction)
            {
                if (_ProjectionB < 0.5h)
                {
                    return DecodeHDR(texCUBE(_CubeB, direction), _CubeB_HDR);
                }

                float2 uv = ApplyStereoLayout(DirectionToEquirectangularUv(direction), _LayoutB);
                return DecodeHDR(tex2D(_PanoB, uv), _PanoB_HDR);
            }

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);

                // Unity's built-in Cubemap and Panoramic skyboxes rotate their geometry
                // while retaining the original sampling direction. For a fixed camera
                // ray, the equivalent sample direction uses the inverse angle.
                output.directionA = RotateAroundYInDegrees(input.vertex.xyz, -_RotationA);
                output.directionB = RotateAroundYInDegrees(input.vertex.xyz, -_RotationB);
                return output;
            }

            half4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 current = SampleCurrent(input.directionA);
                current *= _TintA.rgb * unity_ColorSpaceDouble.rgb;
                current *= _ExposureA;

                half3 next = SampleNext(input.directionB);
                next *= _TintB.rgb * unity_ColorSpaceDouble.rgb;
                next *= _ExposureB;

                return half4(lerp(current, next, saturate(_Blend)), 1.0h);
            }
            ENDCG
        }
    }

    Fallback Off
}
