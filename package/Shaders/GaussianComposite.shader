// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
float _CompositeOpacity;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    float opacity = saturate(col.a);
    float invAlpha = opacity > 1e-5 ? rcp(opacity) : 0.0;
    float3 baseColor = opacity > 1e-5 ? GammaToLinearSpace(float3(col.rgb) * invAlpha) : float3(0.0, 0.0, 0.0);

    float compositeOpacity = max(_CompositeOpacity, 0.0);
    float remappedOpacity = compositeOpacity > 0.0 ? 1.0 - pow(saturate(1.0 - opacity), compositeOpacity) : 0.0;

    return float4(baseColor, remappedOpacity);
}
ENDCG
        }
    }
}
