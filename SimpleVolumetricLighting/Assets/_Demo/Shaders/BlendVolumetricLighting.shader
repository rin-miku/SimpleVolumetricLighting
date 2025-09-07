Shader "Custom/BlendVolumetricLighting"
{
    SubShader
    {
        Pass
        {
            Name "BlendVolumetricLighting"

            ZTest Off
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_CameraDepthTexture);
            TEXTURE3D(_LightScatteringTexture);
            SAMPLER(sampler_LightScatteringTexture);

            float _NearOverFarClip;
            float4 _Resolution;

            int ihash(int n)
            {
            	n = (n << 13) ^ n;
            	return (n * (n * n * 15731 + 789221) + 1376312589) & 2147483647;
            }
            
            float random(int n)
            {
            	return ihash(n) / 2147483647.0;
            }
            
            float2 cellNoise(int2 p)
            {
            	int i = p.y * 256 + p.x;
            	return float2(random(i), random(i + 57)) - 0.5;
            }

            half4 SampleScatter(float depth, float2 uv)
            {
                float z = (depth - _NearOverFarClip) / (1 - _NearOverFarClip);
                if(z < 0.0) return half4(0, 0, 0, 1);
                half3 voxel = half3(uv, z);
                half2 reslution = half2(1.0 / _Resolution.x, 1.0 / _Resolution.y);
                voxel.xy+= cellNoise(voxel.xy * _ScreenSize.xy) * reslution;
                return _LightScatteringTexture.SampleLevel(sampler_LightScatteringTexture, voxel, 0);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                float depth = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_PointClamp, uv);
                depth = Linear01Depth(depth, _ZBufferParams);

                half4 lightColor = SampleScatter(depth, uv);
                half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);    
                
                return color * lightColor.a + lightColor;
            }
            ENDHLSL
        }
    }
}
