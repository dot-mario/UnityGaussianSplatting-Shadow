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
            Blend Off
            ColorMask 0
            Cull Off

CGPROGRAM
#pragma vertex vert_shadow_caster
#pragma fragment frag_shadow_caster
// #pragma multi_compile_instancing
#pragma require compute
#pragma use_dxc

#include "GaussianSplatting.hlsl"

float _ShadowAlphaCutoff;

StructuredBuffer<LightViewData> _LightSplatViewDataOutput;
float4 _LightScreenParams; // .xy = resolution (width, height)

struct v2f_shadow_caster
{
    float4 vertex   : SV_POSITION;
    float2 pos     : TEXCOORD0;
    half   splatOpacity : TEXCOORD1;
};

v2f_shadow_caster vert_shadow_caster(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f_shadow_caster o = (v2f_shadow_caster)0;
    LightViewData lightView = _LightSplatViewDataOutput[instID];
	float4 centerClipPos = lightView.centerClipPos;
	bool behindCam = centerClipPos.w <= 0;
    if (behindCam)
    {
        o.vertex = asfloat(0x7fc00000);
    }
    else
    {
        o.splatOpacity = lightView.opacity;

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;
        
        // "RenderGaussianSplats.shader" 셰이더와 동일한 방식으로 2D 타원 축을 사용해 클립 공간 오프셋 계산
        // lightView.axis1, axis2는 픽셀 공간 기준이라고 가정하고 클립 공간으로 변환
        float2 screenSpaceOffset = quadPos.x * lightView.axis1 + quadPos.y * lightView.axis2;
        float2 deltaScreenPos = screenSpaceOffset * 2 / _LightScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;
    }
    
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

void frag_shadow_caster(v2f_shadow_caster i)
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);
    half final_alpha = saturate(alpha * i.splatOpacity);
    
    // 알파 임계값보다 낮으면 픽셀을 버림 (그리지 않음)
    if (final_alpha < _ShadowAlphaCutoff)
        discard;
    // discard 되지 않은 픽셀은 ZWrite On에 의해 깊이 버퍼에 자동으로 기록됨.
}
ENDCG
        }
    }
}