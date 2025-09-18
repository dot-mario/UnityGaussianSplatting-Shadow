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

float4x4 _ShadowMapFaceMatrixPX, _ShadowMapFaceMatrixNX, _ShadowMapFaceMatrixPY, _ShadowMapFaceMatrixNY, _ShadowMapFaceMatrixPZ, _ShadowMapFaceMatrixNZ;

Texture2D _ShadowMapFacePX; SamplerState sampler_ShadowMapFacePX; // +X
Texture2D _ShadowMapFaceNX; SamplerState sampler_ShadowMapFaceNX; // -X
Texture2D _ShadowMapFacePY; SamplerState sampler_ShadowMapFacePY; // +Y
Texture2D _ShadowMapFaceNY; SamplerState sampler_ShadowMapFaceNY; // -Y
Texture2D _ShadowMapFacePZ; SamplerState sampler_ShadowMapFacePZ; // +Z
Texture2D _ShadowMapFaceNZ; SamplerState sampler_ShadowMapFaceNZ; // -Z

float3 _PointLightPosition;    // 광원의 월드 좌표
float _ShadowBias;             // 그림자 바이어스
float _LightFarPlaneGS;        // 광원 시점의 Far Plane 거리
float _LightNearPlaneGS;       // 광원 시점의 Near Plane 거리

// --- 점광원 그림자 계산 함수 ---
half SamplePointShadow(float3 worldPos)
{
    // --- 1. 광원 벡터 계산 및 변수 초기화 ---
    float3 lightVec = worldPos - _PointLightPosition;
    float3 absVec = abs(lightVec);
    float4 shadowCoord; // 결과를 담을 변수

    // --- 2. 픽셀 위치에 따라 올바른 VP 행렬을 선택하여 광원 시점의 클립 좌표 계산 ---
    if (absVec.x > absVec.y && absVec.x > absVec.z) // X face
    {
        if (lightVec.x > 0)
            shadowCoord = mul(_ShadowMapFaceMatrixPX, float4(worldPos, 1.0));
        else
            shadowCoord = mul(_ShadowMapFaceMatrixNX, float4(worldPos, 1.0));
    }
    else if (absVec.y > absVec.z) // Y face
    {
        if (lightVec.y > 0)
            shadowCoord = mul(_ShadowMapFaceMatrixPY, float4(worldPos, 1.0));
        else
            shadowCoord = mul(_ShadowMapFaceMatrixNY, float4(worldPos, 1.0));
    }
    else // Z face
    {
        if (lightVec.z > 0)
            shadowCoord = mul(_ShadowMapFaceMatrixPZ, float4(worldPos, 1.0));
        else
            shadowCoord = mul(_ShadowMapFaceMatrixNZ, float4(worldPos, 1.0));
    }

    // --- 3. NDC 좌표 및 UV 계산 (모든 페이스에 공통) ---
    shadowCoord.xyz /= shadowCoord.w;                 // 동차 나누기 -> NDC (-1 ~ 1 범위)
    float currentDepth = shadowCoord.z;               // 현재 픽셀의 깊이 (D3D에서 0 ~ 1 범위)
    float2 shadowUV = shadowCoord.xy * 0.5 + 0.5;     // UV 좌표 (0 ~ 1 범위)
    shadowUV.y = 1.0 - shadowUV.y;                    // D3D 환경을 위한 Y 좌표 반전

    // --- 4. 뎁스맵에서 깊이 값 샘플링 ---
    float shadowMapDepth = 1.0;
    if (absVec.x > absVec.y && absVec.x > absVec.z) // X face
    {
        if (lightVec.x > 0)
            shadowMapDepth = _ShadowMapFacePX.Sample(sampler_ShadowMapFacePX, shadowUV).r;
        else
            shadowMapDepth = _ShadowMapFaceNX.Sample(sampler_ShadowMapFaceNX, shadowUV).r;
    }
    else if (absVec.y > absVec.z) // Y face
    {
        if (lightVec.y > 0)
            shadowMapDepth = _ShadowMapFacePY.Sample(sampler_ShadowMapFacePY, shadowUV).r;
        else
            shadowMapDepth = _ShadowMapFaceNY.Sample(sampler_ShadowMapFaceNY, shadowUV).r;
    }
    else // Z face
    {
        if (lightVec.z > 0)
            shadowMapDepth = _ShadowMapFacePZ.Sample(sampler_ShadowMapFacePZ, shadowUV).r;
        else
            shadowMapDepth = _ShadowMapFaceNZ.Sample(sampler_ShadowMapFaceNZ, shadowUV).r;
    }

    // --- 5. 깊이 비교 및 최종 그림자 값 반환 ---
	half shadow = (currentDepth < shadowMapDepth - _ShadowBias) ? 0.2 : 1.0;
    return shadow;
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

    half shadow = SamplePointShadow(i.worldPos);
    half3 finalColor = i.col.rgb * shadow;
    
    half4 res = half4(finalColor * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
