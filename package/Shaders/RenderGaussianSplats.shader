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
float4 _GlobalTint;

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
float4 _LightZBufferParams;

float _LightBrightness;        // 빛을 받는 영역의 밝기
float _ShadowBrightness;       // 그림자 영역의 밝기
float _GaussianShadowEnabled;  // 그림자 사용 여부 (0: 미사용)

// --- 점광원 그림자 계산 함수 ---
bool SamplePointShadow(float3 worldPos)
{
    // 현재 픽셀 위치에서 광원까지의 벡터
    float3 lightVec = worldPos - _PointLightPosition;

    // Cube shadow map은 각 면의 카메라 전방 축을 기준으로 깊이를 저장한다.
    // 따라서 벡터의 각 성분 중 절대값이 가장 큰 축이 실제로 사용된 면이며,
    // 해당 축 방향 성분이 뎁스 버퍼에 기록된 선형 깊이값과 대응된다.
    float3 absLightVec = abs(lightVec);
    float currentLinearDepth = max(absLightVec.x, max(absLightVec.y, absLightVec.z));

    float shadowMapNonLinearDepth = SAMPLE_TEXTURECUBE(_ShadowCubemap, sampler_ShadowCubemap, lightVec).r;
    float shadowMapLinear01Depth = LinearEyeDepth(shadowMapNonLinearDepth, _LightZBufferParams);

    // 현재 축 기반 깊이가 저장된 깊이(+바이어스)보다 멀면 그림자 판정
    bool visibility = currentLinearDepth <= shadowMapLinear01Depth + _ShadowBias;
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

	float4 tint = _GlobalTint;
	half3 finalColor = i.col.rgb * (half3)tint.rgb;
	half tintAlpha = saturate((half)tint.a);

	if (_GaussianShadowEnabled > 0.5)
	{
		half visibility = SamplePointShadow(i.worldPos);
		half lightIntensity = lerp(_ShadowBrightness, _LightBrightness, visibility);
		finalColor *= lightIntensity;
	}

	half4 res = half4(finalColor * alpha, alpha * tintAlpha);
	return res;
}
ENDCG
        }
    }
}
