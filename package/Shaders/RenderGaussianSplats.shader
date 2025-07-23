// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
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
    float3 worldPos : TEXCOORD1; // <<< 추가: 프래그먼트의 월드 좌표 (스플랫 중심)
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

Texture2D _ShadowMapFacePX; SamplerState sampler_ShadowMapFacePX; // +X
Texture2D _ShadowMapFaceNX; SamplerState sampler_ShadowMapFaceNX; // -X
Texture2D _ShadowMapFacePY; SamplerState sampler_ShadowMapFacePY; // +Y
Texture2D _ShadowMapFaceNY; SamplerState sampler_ShadowMapFaceNY; // -Y
Texture2D _ShadowMapFacePZ; SamplerState sampler_ShadowMapFacePZ; // +Z
Texture2D _ShadowMapFaceNZ; SamplerState sampler_ShadowMapFaceNZ; // -Z

float3 _PointLightPosition;    // 광원의 월드 좌표
float _ShadowBias;             // 그림자 바이어스
float _LightFarPlaneGS;        // 광원 시점의 Far Plane 거리
float _LightNearPlaneGS; 

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
		o.worldPos = view.centerWorldPos; // <<< 추가된 라인

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

float SampleShadowMapFromFaces(float3 lightToFragNormalized, float3 absLightToFragDir)
{
    float2 uv;
    float shadowMapStoredDepth = 1.0; // 기본값: 빛을 받음
    float recipMajorAxis;

    if (absLightToFragDir.x >= absLightToFragDir.y && absLightToFragDir.x >= absLightToFragDir.z) // X축이 주 축
    {
        recipMajorAxis = 1.0 / absLightToFragDir.x;
        if (lightToFragNormalized.x > 0) { // Positive X
            uv = float2(-lightToFragNormalized.z * recipMajorAxis, -lightToFragNormalized.y * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFacePX.Sample(sampler_ShadowMapFacePX, uv).r;
        } else { // Negative X
            uv = float2(lightToFragNormalized.z * recipMajorAxis, -lightToFragNormalized.y * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFaceNX.Sample(sampler_ShadowMapFaceNX, uv).r;
        }
    }
    else if (absLightToFragDir.y >= absLightToFragDir.x && absLightToFragDir.y >= absLightToFragDir.z) // Y축이 주 축
    {
        recipMajorAxis = 1.0 / absLightToFragDir.y;
        if (lightToFragNormalized.y > 0) { // Positive Y
            uv = float2(lightToFragNormalized.x * recipMajorAxis, lightToFragNormalized.z * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFacePY.Sample(sampler_ShadowMapFacePY, uv).r;
        } else { // Negative Y
            uv = float2(lightToFragNormalized.x * recipMajorAxis, -lightToFragNormalized.z * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFaceNY.Sample(sampler_ShadowMapFaceNY, uv).r;
        }
    }
    else // Z축이 주 축
    {
        recipMajorAxis = 1.0 / absLightToFragDir.z;
        if (lightToFragNormalized.z > 0) { // Positive Z
            uv = float2(lightToFragNormalized.x * recipMajorAxis, -lightToFragNormalized.y * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFacePZ.Sample(sampler_ShadowMapFacePZ, uv).r;
        } else { // Negative Z
            uv = float2(-lightToFragNormalized.x * recipMajorAxis, -lightToFragNormalized.y * recipMajorAxis) * 0.5 + 0.5;
            shadowMapStoredDepth = _ShadowMapFaceNZ.Sample(sampler_ShadowMapFaceNZ, uv).r;
        }
    }
    return shadowMapStoredDepth;
}

float LinearizeDeviceDepthToViewZ(float nonLinearDepth01, float nearPlane, float farPlane)
{
    // Unity/DirectX 스타일 프로젝션 (NDC z = 0 at near, 1 at far) 기준
    // 이 공식은 nonLinearDepth01이 이미 0(near)에서 1(far) 범위라고 가정합니다.
    // viewZ = 1.0 / ( (1.0/near - 1.0/far) * nonLinearDepth01 + 1.0/far )
    // viewZ = near * far / (near + nonLinearDepth01 * (far - near)) <- 이 공식은 nonLinearDepth01을 역으로 사용
    // 올바른 공식 중 하나:
    // Z_ndc = nonLinearDepth01
    // Z_clip_val_for_w_one = Z_ndc * 2.0 - 1.0; (if projection maps to [-1,1] clip z for w=1)
    // Z_view = (2.0 * near * far) / (far + near - Z_clip_val_for_w_one * (far - near))
    // Unity의 경우, 깊이 버퍼 값(0..1)에서 바로 뷰 공간 Z로 가는 공식 (양수):
    // perspective: 1.0 / ( (1.0/near - 1.0/far) * depth_01_from_buffer + 1.0/far )
    // 여기서는 문서에 있던 공식을 사용하되, nonLinearDepth01이 이미 0..1 범위라고 가정합니다.
    // nonLinearDepth01 * 2.0f - 1.0f -> [-1, 1] 범위로 변환
    return abs((2.0f * nearPlane * farPlane) / (farPlane + nearPlane - (nonLinearDepth01 * 2.0f - 1.0f) * (farPlane - nearPlane)));
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

	// --- 그림자 계산 시작 ---
    float3 currentWorldPos = i.worldPos.xyz;
    float3 lightToFragDir = currentWorldPos - _PointLightPosition;
    float currentDepthFromLightLinear = length(lightToFragDir); // 현재 프래그먼트의 실제 선형 깊이
    float3 normalizedLightToFragDir = normalize(lightToFragDir);
    float3 absLightToFragDir = abs(normalizedLightToFragDir);

    float shadowMapHardwareDepth01 = SampleShadowMapFromFaces(normalizedLightToFragDir, absLightToFragDir); // 0~1 범위의 비선형 하드웨어 뎁스

    // 섀도우 맵에서 읽은 비선형 뎁스를 선형 뷰 공간 Z 거리로 변환
    float occluderDepthLinear = LinearizeDeviceDepthToViewZ(shadowMapHardwareDepth01, _LightNearPlaneGS, _LightFarPlaneGS);

    half shadowFactor = 1.0h; 
    // 이제 두 선형 깊이 값을 비교합니다.
    if (currentDepthFromLightLinear > occluderDepthLinear + _ShadowBias)
    {
        shadowFactor = 0.3h; // 그림자 감쇠값 (예: 0.3)
    }
    // --- 그림자 계산 종료 ---
	
    half4 res = half4(i.col.rgb * shadowFactor * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
