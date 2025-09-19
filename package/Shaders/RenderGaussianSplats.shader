// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats With Point Shadow"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl" 

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float3 worldPos : TEXCOORD1; // for shadow
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

TEXTURECUBE(_ShadowCubemap);
SAMPLER(sampler_ShadowCubemap);

float3 _PointLightPosition;    // 광원의 월드 좌표
float _ShadowBias;             // 그림자 바이어스
float _LightFarPlaneGS;        // 광원 시점의 Far Plane 거리
float _LightNearPlaneGS;       // 광원 시점의 Near Plane 거리

float _LightBrightness;        // 빛을 받는 영역의 밝기
float _ShadowBrightness;       // 그림자 영역의 밝기

float LinearToNonLinearDepth(float linearDepth, float near, float far)
{
    return (far * (linearDepth - near)) / (linearDepth * (far - near));
}

// --- 점광원 그림자 계산 함수 ---
bool SamplePointShadow(float3 worldPos)
{
    // 1. 현재 픽셀의 '선형' 깊이(실제 거리)를 계산
    float3 lightVec = worldPos - _PointLightPosition;
    float currentLinearDepth = length(lightVec);

    float shadowMapNonLinearDepth = SAMPLE_TEXTURECUBE(_ShadowCubemap, sampler_ShadowCubemap, lightVec).r;

    // 3. 큐브맵의 '비선형' 뎁스 값을 '선형' 깊이(실제 거리)로 변환
    //    LinearEyeDepth는 뷰 공간 기준이므로, 여기서는 0-1 선형 값으로 변환 후 Far Plane을 곱함
    float shadowMapLinearDepth = LinearEyeDepth(shadowMapNonLinearDepth, _ZBufferParams) * _LightFarPlaneGS;

    // 4. '선형' 깊이끼리 비교하여 최종 그림자 판단
    //    현재 픽셀의 거리가 그림자 맵에 저장된 거리보다 멀리 있으면 그림자.
    bool visibility = currentLinearDepth <= shadowMapLinearDepth + _ShadowBias;
    return visibility;
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.centerClipPos;
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;
        o.worldPos = view.centerWorldPos; // for shadow

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

    half visibility = SamplePointShadow(i.worldPos);
	half lightIntensity = lerp(_ShadowBrightness, _LightBrightness, visibility);
    half3 finalColor = i.col.rgb * lightIntensity;
    
    half4 res = half4(finalColor * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
