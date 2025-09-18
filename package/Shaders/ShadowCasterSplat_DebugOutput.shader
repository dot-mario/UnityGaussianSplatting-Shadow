// ShadowCasterSplat_DebugOutput.shader (LightViewData 확인용 저부하 버전)

Shader "Hidden/ShadowCasterSplat_DebugOutput_LightViewData"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            Name "ShadowCasterDebugLightViewData"
            ZWrite On
            ColorMask RGBA // 컬러 출력을 위해 RGBA 사용
            Cull Off

            CGPROGRAM
            #pragma vertex vert_debug_lightview
            #pragma fragment frag_debug_lightview
            #pragma multi_compile_instancing
            #pragma use_dxc

            #include "GaussianSplatting.hlsl" // LoadSplatData 등 기본 함수 포함 가정

            // GaussianSplatShadowRenderer.cs에서 설정될 유니폼
            // uniform float _SplatOpacityScale; // LightViewData.opacity에 이미 반영되었다고 가정

            // 컴퓨트 셰이더에서 계산된 LightViewData 구조체 (HLSL에 정의된 것과 일치)
            StructuredBuffer<LightViewData> _LightSplatViewDataOutput; // C#에서 바인딩

            struct Attributes
            {
                uint vertexID   : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct v2f_debug_lightview
            {
                float4 positionCS     : SV_POSITION;
                // LightViewData의 값들을 프래그먼트 셰이더로 전달하기 위한 변수들
                float4 dbg_LightView_PosCS : TEXCOORD0; // LightViewData.posCS 시각화용
                float2 dbg_LightView_Axis1 : TEXCOORD1; // LightViewData.axis1 시각화용
                float2 dbg_LightView_Axis2 : TEXCOORD2; // LightViewData.axis2 시각화용
                half   dbg_LightView_Opacity : TEXCOORD3; // LightViewData.opacity 시각화용
            };

            v2f_debug_lightview vert_debug_lightview(Attributes input)
            {
                v2f_debug_lightview output = (v2f_debug_lightview)0;
                uint splatIdx = input.instanceID;

                LightViewData lightView = _LightSplatViewDataOutput[splatIdx];

                // LightViewData 값들을 디버깅을 위해 프래그먼트 셰이더로 전달
                output.dbg_LightView_PosCS = lightView.centerClipPos;
                output.dbg_LightView_Axis1 = lightView.axis1;
                output.dbg_LightView_Axis2 = lightView.axis2;
                output.dbg_LightView_Opacity = lightView.opacity;

                // 스플랫을 작은 고정 크기 점/사각형으로 표시
                // 발자국 계산 없이 중심점만 사용하고, vertexID로 작은 오프셋만 줌
                if (lightView.centerClipPos.w > 0.0001f) // 기본적인 후방 컬링
                {
                    output.positionCS = lightView.centerClipPos; // 중심점의 클립 공간 좌표

                    // 화면상에 매우 작은 고정 크기 사각형 만들기 (NDC 공간에서)
                    // vertexID를 사용하여 4개의 코너 오프셋 생성 (-1, -1), (1, -1), (-1, 1), (1, 1)
                    uint vidx = input.vertexID;
                    float2 corner_offset_ndc = float2( (vidx & 1) ? 1.0f : -1.0f, (vidx & 2) ? 1.0f : -1.0f );
                    
                    // 화면에서 1~2픽셀 정도의 매우 작은 크기로 고정 (부하 최소화)
                    // _ScreenParams 또는 _ShadowCubemapFaceParams.xy (해상도)를 사용하여 픽셀 크기 계산
                    // float2 pixel_size_in_ndc = 2.0f / _ScreenParams.xy; // 또는 _ShadowCubemapFaceParams.xy
                    // output.positionCS.xy += corner_offset_ndc * pixel_size_in_ndc * 1.0f * output.positionCS.w;
                    
                    // 또는 더 간단하게, NDC 공간에서 매우 작은 고정 오프셋 사용 (예: 0.005)
                    // 이 값은 해상도에 따라 조절 필요
                    float fixed_ndc_offset_size = 0.005f; 
                    output.positionCS.xy += corner_offset_ndc * fixed_ndc_offset_size * output.positionCS.w;
                }
                else // 컬링
                {
                    output.positionCS = float4(2.0f, 2.0f, 2.0f, 1.0f); // NDC 밖으로
                }
                return output;
            }

            half4 frag_debug_lightview(v2f_debug_lightview input) : SV_Target
            {
                // 컬링된 픽셀 (또는 w가 매우 작은 경우)
                if (input.positionCS.w <= 0.0001f || input.dbg_LightView_PosCS.w <= 0.0001f)
                {
                    // 아무것도 출력 안하거나, 특정 색으로 표시 가능
                    // return float4(0,0,0,0); // 투명하게
                    discard; // 아예 그리지 않음
                }

                // --- 여기서 LightViewData의 특정 값을 색상으로 변환하여 시각화 ---
                // 예시 1: LightViewData.posCS.w (클립 공간 W, 깊이와 유사) 값을 빨간색으로 시각화
                // float w_val = input.dbg_LightView_PosCS.w;
                // float normalized_w = saturate(w_val / 100.0f); // 100.0f는 예상되는 W의 최대 범위, 조절 필요
                // return float4(normalized_w, 0.0, 0.0, 1.0);

                // 예시 2: LightViewData.opacity 값을 초록색으로 시각화
                // return float4(0.0, input.dbg_LightView_Opacity, 0.0, 1.0);

                // 예시 3: LightViewData.axis1.x 값을 파란색으로 시각화 (음수일 수 있으므로 0.5 더하고 0.5 곱함)
                // float axis1x_normalized = saturate(input.dbg_LightView_Axis1.x * 0.5f + 0.5f);
                // return float4(0.0, 0.0, axis1x_normalized, 1.0);
                
                // 예시 4: LightViewData.posCS.z / posCS.w (NDC z 깊이) 값을 그레이스케일로
                float ndc_z = input.dbg_LightView_PosCS.z / input.dbg_LightView_PosCS.w;
                float ndc_z_normalized = saturate(ndc_z); // DirectX는 0(near) ~ 1(far) 범위
                return float4(ndc_z_normalized, ndc_z_normalized, ndc_z_normalized, 1.0);

                // 기본: 문제가 있다면 마젠타색으로 표시
                // return float4(1.0, 0.0, 1.0, 1.0); 
            }
            ENDCG
        }
    }
    Fallback Off
}