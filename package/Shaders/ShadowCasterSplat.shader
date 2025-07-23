// SPDX-License-Identifier: MIT
Shader "Hidden/ShadowCasterSplat"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Name "ShadowCasterPass"
//            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Off

CGPROGRAM
#pragma vertex vert_shadow_caster
#pragma fragment frag_shadow_caster
#pragma multi_compile_instancing
#pragma use_dxc

#include "GaussianSplatting.hlsl"

StructuredBuffer<LightViewData> _LightSplatViewDataOutput;
StructuredBuffer<uint> _OrderBuffer;
float4 _LightScreenParams; // .xy = resolution (width, height)

struct Attributes
{
    uint vertexID   : SV_VertexID;
    uint instanceID : SV_InstanceID;
};

struct v2f_shadow_caster
{
    float4 positionCS   : SV_POSITION;
    float2 localPos     : TEXCOORD0;
    half   splatOpacity : TEXCOORD1;
};

v2f_shadow_caster vert_shadow_caster(Attributes input)
{
    v2f_shadow_caster output = (v2f_shadow_caster)0;
    uint splatIdx = input.instanceID;
    splatIdx = _OrderBuffer[splatIdx];

    LightViewData lightView = _LightSplatViewDataOutput[splatIdx];
	float4 centerClipPos = lightView.centerClipPos;
    output.splatOpacity = lightView.opacity;

    if (centerClipPos.w <= 0.0001f) // behindCam
    {
        output.positionCS = asfloat(0x7fc00000);
        output.localPos = float2(0,0);
        return output;
    }

    uint idx = input.vertexID;
    // 로컬 쿼드 정점 생성 (-1,-1) to (1,1)
    float2 corner_offset_local = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
    corner_offset_local *= 2;
    // "RenderGaussianSplats.shader" 셰이더와의 일관성을 위해 쿼드 로컬 좌표를 -2 ~ +2 범위로 확장
    output.localPos = corner_offset_local;
    
    // "RenderGaussianSplats.shader" 셰이더와 동일한 방식으로 2D 타원 축을 사용해 클립 공간 오프셋 계산
    // lightView.axis1, axis2는 픽셀 공간 기준이라고 가정하고 클립 공간으로 변환
    float2 screenSpaceOffset = output.localPos.x * lightView.axis1 + output.localPos.y * lightView.axis2;
    float2 clipSpaceOffset = screenSpaceOffset * 2.0 / _LightScreenParams.xy;
    output.positionCS = centerClipPos;
    output.positionCS.xy += clipSpaceOffset * output.positionCS.w;
    
	FlipProjectionIfBackbuffer(output.positionCS);
    return output;
}

void frag_shadow_caster(v2f_shadow_caster input)
{
    // 기준 1: 스플랫 자체의 불투명도(input.splatOpacity)가 너무 낮으면 그림자 생성 안 함
    // input.splatOpacity는 GaussianSplatRenderer의 _SplatOpacityScale이 적용된 값입니다.
    // 이 임계값은 실험을 통해 조절해야 합니다. (예: 0.1h)
    // 만약 _SplatOpacityScale이 매우 크다면 (예: 10), 이 임계값은 0.1h 보다 훨씬 커야 할 수 있습니다.
    // 또는, _SplatOpacityScale이 적용되기 전의 원본 알파값을 기준으로 하고 싶다면,
    // 해당 값을 컴퓨트 셰이더에서 따로 계산하여 넘겨받아야 합니다.
    // 여기서는 현재 input.splatOpacity를 사용합니다.
    // const half MIN_SHADOW_CASTING_OPACITY = 0.5h; // 이 값을 조절하세요
    // if (input.splatOpacity < MIN_SHADOW_CASTING_OPACITY)
    // {
    //     discard;
    // }
    
    // 로컬 좌표의 제곱 거리를 사용하여 가우시안 모양의 알파를 계산
    // localPos가 -2~2 범위이므로, dot 결과는 0~8 범위
    float power = -dot(input.localPos, input.localPos);
    
    half alpha_shape = exp(power);
    half final_alpha = saturate(alpha_shape * input.splatOpacity);

    // 기준 2: 스플랫의 모양(alpha_shape) 및 결합된 최종 알파
    // input.splatOpacity가 이미 MIN_SHADOW_CASTING_OPACITY 검사를 통과했으므로,
    // 여기서는 alpha_shape를 더 중요하게 볼 수 있습니다.
    // final_alpha 계산에서 input.splatOpacity를 곱하는 대신, alpha_shape만 사용하거나,
    // 곱하더라도 임계값을 높여 더 많은 부분을 discard 할 수 있습니다.
    
    // 여기서는 alpha_shape 자체로 모양을 판단합니다.
    // const half SHAPE_DISCARD_THRESHOLD = 0.9f; // 예: exp(-4.6) 정도. 모서리를 더 잘라냄. 1.0/255.0 보다 큰 값.
    // if (alpha_shape < SHAPE_DISCARD_THRESHOLD) 
    // {
    //     discard;
    // }
    
    // 알파 임계값보다 낮으면 픽셀을 버림 (그리지 않음)
    if (final_alpha < 1.0/255.0) // 1.0/255.0
    {
        discard;
    }
    // discard 되지 않은 픽셀은 ZWrite On에 의해 깊이 버퍼에 자동으로 기록됨.
}
ENDCG
        }
    }
    Fallback Off
}