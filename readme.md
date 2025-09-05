# R-2. Gaussian Splat Shadow Rendering TDD

이 브랜치는 예전의 시도에 대한 코드입니다.

## 현재 문제 상황:
저는 Unity URP(Universal Render Pipeline) 환경에서 Gaussian Splatting 모델에 동적 포인트 라이트 그림자를 구현하고 있습니다. 그림자 생성 방식은 포인트 라이트 위치에서 6방향으로 뎁스 맵(Depth Map)을 렌더링하여 6개의 개별 2D 텍스처에 저장한 후, 주 렌더링 패스에서 이 텍스처들을 샘플링하여 그림자를 적용하는 것입니다.
하지만 현재 6개의 뎁스 텍스처에 올바른 6방향 뷰가 렌더링되지 않고, 잘못된 결과가 그려지는 문제가 발생하고 있습니다. 주 렌더링 패스(RenderGaussianSplats.shader)는 정상적으로 동작하지만, 그림자 맵을 생성하는 섀도우 캐스터 패스(ShadowCasterSplat.shader)에 문제가 있는 것으로 보입니다. 특히, 각 큐브맵 면에 대한 뷰 변환을 계산하고 적용하는 컴퓨트 셰이더 단계에서 좌표계 문제가 있는 것으로 강력히 의심됩니다.

### 핵심 파이프라인:
1. CSCalcSharedLightData (Compute Shader): 6번의 렌더링 루프 밖에서 한 번만 실행됩니다. 스플랫의 로컬 좌표, 월드 좌표, 3D 공분산 등 재사용 가능한 데이터를 계산하여 SharedLightData 버퍼에 저장합니다.
2. CSCalcLightViewData (Compute Shader): 큐브맵의 6개 면에 대해 각각 루프를 돌며 실행됩니다.
    * C#에서 현재 면에 맞는 _LightModelViewMatrix와 _LightProjMatrix를 전달받습니다.
    * SharedLightData 버퍼를 입력으로 받아, 스플랫의 최종 클립 공간 위치와 화면상 모양(2D 타원 축)을 계산하여 LightViewData 버퍼에 저장합니다.
3. ShadowCasterSplat.shader (Vertex/Fragment Shader):
    * LightViewData 버퍼를 읽어 각 스플랫을 뎁스 텍스처에 렌더링(DrawProcedural)하여 깊이 값을 기록합니다.

CSCalcLightViewData 커널이 6개의 다른 _LightModelViewMatrix를 받음에도 불구하고, 결과적으로 6개의 뎁스 텍스처가 모두 비슷하게 잘못 렌더링됩니다. 이는 CSCalcLightViewData 내부의 좌표 변환 로직이 _LightModelViewMatrix를 올바르게 활용하지 못하고 있거나, 잘못된 값을 커널에 전달 하는 것으로 시사됩니다.

```mermaid
---
title: flow chart
---
%%{
  init: {
    'theme': 'base',
    'themeVariables': {
      'primaryColor': '#ffffff',
      'primaryTextColor': '#000',
      'primaryBorderColor': '#7C839D',
      'lineColor': '#5C6178',
      'secondaryColor': '#F4F6FB',
      'tertiaryColor': '#E9ECF5'
    }
  }
}%%

graph TD
    accTitle: 가우시안 스플래팅 그림자 파이프라인
    accDescr: 2단계로 구성된 가우시안 스플래팅의 동적 그림자 생성 과정. 1단계는 섀도우 맵 생성, 2단계는 주 렌더링 및 그림자 적용입니다.

    %% ==========================================
    %% 단계 1: 섀도우 맵 생성
    %% ==========================================
    subgraph "단계 1: 섀도우 맵 생성"
        direction TB

        %% 제어 및 루프 외부 노드 정의
        S1_Control["C#: 제어 및 디스패치"]
        S1_CS1(Compute: CSCalcSharedLightData)
        S1_Data1[(SharedLightData Buffer)]
        S1_Result[/6x 2D Depth Textures/]

        %% 흐름 정의: 제어 -> 루프 진입
        S1_Control -- Dispatch --> S1_CS1
        S1_CS1 -. "writes" .-> S1_Data1
        S1_CS1 -- "Start Loop" --> S1_CS2

        %% <<< 루프 구간을 위한 중첩 서브그래프 >>>
        subgraph "Loop x6: 큐브맵 면별 반복 작업"
            direction TB
            S1_CS2(Compute: CSCalcLightViewData)
            S1_Data2[(LightViewData Buffer)]
            S1_HLSL(HLSL: ShadowCasterSplat.shader)

            %% 루프 내부 흐름
            S1_CS2 --> S1_HLSL
            S1_HLSL -- "next face" --> S1_CS2

            %% 루프 내부 데이터 흐름
            S1_CS2 -. "writes" .-> S1_Data2
            S1_Data2 -. "reads" .-> S1_HLSL
        end
        
        %% 루프 외부 데이터 흐름
        S1_Data1 -. "reads" .-> S1_CS2
        S1_HLSL -. "writes to face[i]" .-> S1_Result
    end

    %% ==========================================
    %% 단계 2: 주 렌더링 및 그림자 적용
    %% ==========================================
    subgraph "단계 2: 주 렌더링 및 그림자 적용"
        direction TB
        S2_Control["C#: 제어 및 디스패치"]
        S2_CS(Compute: CSCalcViewData)
        S2_Data1[(SplatViewData Buffer)]
        S2_HLSL(HLSL: Render Splats)
        S2_Blend(Blend to Screen)
        S2_Result[/Final FrameBuffer/]
        S2_Control -- Dispatch --> S2_CS
        S2_CS --> S2_HLSL
        S2_HLSL --> S2_Blend
        S2_CS -. "writes" .-> S2_Data1
        S2_Data1 -. "reads" .-> S2_HLSL
        S2_Blend -. "writes" .-> S2_Result
    end

    %% 단계 간 연결
    S1_Result -. "reads" .-> S2_HLSL
```

## 1. 개요 (Overview)

본 문서는 Unity6 URP 환경에서 가우시안 스플래팅(Gaussian Splatting) 모델에 동적 포인트 라이트 그림자를 구현하기 위한 렌더링 파이프라인을 기술한다. 핵심 목표는 광원 위치에서 6방향으로 뎁스 맵(Depth Map)을 생성하여 큐브맵 형태의 그림자 정보를 구성하고, 이를 주 스플랫 렌더링 시 각 스플랫의 그림자 여부를 판정하는 데 사용하는 것이다.

<aside>
💡

큐브맵의 각 면(face)에 직접 렌더링 시 문제가 발생했기 때문에, **6개의 개별 2D 렌더 텍스처에 각 면의 뎁스 정보를 따로 기록**한 후, 주 렌더링 셰이더에서 이 6개의 텍스처를 참조하여 그림자를 계산하는 방식으로 우회하였다.

</aside>

**주요 구성 요소:**

- **`GaussianSplatRenderer.cs`**: 개별 가우시안 스플랫 에셋의 렌더링을 담당하는 주 컴포넌트.
- **`GaussianSplatShadowRenderer.cs`**: 특정 `GaussianSplatRenderer`에 연결되어 포인트 라이트의 섀도우 맵 생성을 전담하는 컴포넌트. 6개의 2D 뎁스 텍스처를 관리하고 렌더링 명령을 생성한다.
- **`GaussianSplatURPFeature.cs`**: URP의 `ScriptableRendererFeature`로, 렌더 그래프(Render Graph) 내에 섀도우 맵 생성 패스와 주 스플랫 렌더링 패스를 삽입하고 관리한다.
- **`SplatUtilities.compute` (Compute Shader)**: 스플랫 데이터의 GPU 기반 처리를 담당한다.
    - `CSCalcSharedLightData`: 광원과 무관하게 미리 계산될 수 있는 스플랫 데이터(위치, 3D 공분산, 원본 불투명도 등)를 준비한다.
    - `CSCalcLightViewData`: `CSCalcSharedLightData`의 출력을 받아, 특정 광원 시점에서의 스플랫 뷰 데이터(클립 공간 위치, 2D 타원 축 등)를 계산한다.
- **`ShadowCasterSplat.shader` (HLSL Shader)**: 섀도우 맵 생성 패스에서 사용되며, 각 스플랫을 2D 뎁스 텍스처에 렌더링하여 깊이 값을 기록한다.
- **`RenderGaussianSplats.shader` (HLSL Shader)**: 주 스플랫 렌더링 패스에서 사용되며, 6개의 2D 섀도우 맵 텍스처를 샘플링하여 최종 스플랫 색상에 그림자를 적용한다.
- **`GaussianSplatting.hlsl` (HLSL Include)**: 공통 구조체(예: `SplatData`, `SplatViewData`, `SharedLightData`, `LightViewData`) 및 유틸리티 함수를 포함한다.

## 2. 섀도우 맵 생성 단계 (Shadow Map Generation Phase)

이 단계의 목표는 포인트 라이트 위치에서 6방향으로 씬을 렌더링하여 각 방향에 대한 뎁스 맵을 생성하는 것이다.

### 2.1. `GaussianSplatShadowRenderer.cs`의 역할

- **6개의 2D 뎁스 렌더 텍스처 관리**:
    - `RenderTexture[] m_ShadowFaceRTs`: 비(Non)-URP 경로에서 사용할 6개의 2D `RenderTexture` 배열. `EnsureResourcesAreCreated()`에서 각 면의 해상도(`shadowCubemapResolution`)와 뎁스 포맷(`GraphicsFormat.D32_SFloat` 등)에 맞춰 생성 및 관리된다.
    - `GetShadowFaceDescriptor()`: URP Feature가 Render Graph에서 6개의 2D 뎁스 텍스처 핸들을 생성하는 데 필요한 `RenderTextureDescriptor`를 제공한다. 이 디스크립터는 `dimension = TextureDimension.Tex2D`로 설정된다.
- **컴퓨트 셰이더 및 렌더링 준비**:
    - `EnsureGpuResourcesForCompute()`: `m_LightViewDataBuffer`와 `m_SharedLightDataBuffer` 등 컴퓨트 셰이더에 필요한 GPU 버퍼를 준비한다.
    - `EnsureShadowCasterMaterial()`: `shadowCasterShader`를 사용하는 머티리얼(`m_ShadowCasterMaterial`)을 준비한다.
- **URP 연동 메서드**:
    - `RenderShadowFacesURP(CommandBuffer cmd, TextureHandle[] faceTextureHandles)`: URP Feature로부터 `CommandBuffer`와 6개의 2D `TextureHandle` 배열을 전달받는다.
        1. `DispatchSharedDataKernel(cmd)`: `CSCalcSharedLightData` 커널을 디스패치하여 모든 스플랫에 대한 광원 공통 데이터를 미리 계산하고 `m_SharedLightDataBuffer`에 저장한다.
        2. 루프 (6회 반복, 각 큐브맵 면에 대해):
            - `GetLightViewMatrixForFace((CubemapFace)i)`: 현재 면에 대한 광원의 뷰 행렬을 계산한다.
            - 프로젝션 행렬 생성: `Matrix4x4.Perspective(90f, 1.0f, lightNearPlane, lightFarPlane)`.
            - 컴퓨트 셰이더 파라미터 설정: `_LightViewMatrix`, `_LightProjMatrix`, `_LightScreenParams` 등을 `CSCalcLightViewData` 커널에 설정한다.
            - `CSCalcLightViewData` 커널 디스패치: `m_SharedLightDataBuffer`를 입력으로 받아, 현재 면에 대한 `LightViewData`를 계산하고 `m_LightViewDataBuffer`에 저장한다.
            - `cmd.SetRenderTarget(faceTextureHandles[i])`: URP Feature가 제공한 i번째 2D `TextureHandle`을 렌더 타겟으로 설정한다.
            - `cmd.ClearRenderTarget(true, false, Color.black, 1.0f)`: 뎁스 버퍼만 1.0 (먼 값)으로 클리어한다.
            - `m_ShadowCasterMaterial`에 필요한 버퍼(`_LightSplatViewDataOutput`) 및 유니폼 설정.
            - `cmd.DrawProcedural(...)`: `ShadowCasterSplat.shader`를 사용하여 현재 렌더 타겟(i번째 2D 뎁스 텍스처)에 스플랫을 렌더링한다.
- **기타**: `IsRenderNeeded()`, `MarkShadowsDirty()`, `HasSettingsChanged()`, `UpdatePreviousSettings()` 등 상태 관리 메서드.

### 2.2. `SplatUtilities.compute` (컴퓨트 셰이더)

- **`CSCalcSharedLightData` 커널**:
    - 입력: 원본 스플랫 데이터 (`_SplatPos`, `_SplatOther`, `_SplatColor` 등).
    - 출력: `_SharedLightDataOutput` 버퍼 (`SharedLightData` 구조체 배열).
    - 작업: 각 스플랫의 월드 위치(`centerWorldPos`), 3D 공분산 행렬 요소(`cov3d0`, `cov3d1`), 그리고 필터링 기준을 적용한 불투명도(`opacity`)를 계산하여 저장한다.
- **`CSCalcLightViewData` 커널**:
    - 입력: `_SharedLightDataInput` (위 `CSCalcSharedLightData`의 출력), 광원의 뷰/프로젝션 행렬 (`_LightViewMatrix`, `_LightProjMatrix`), 스크린 파라미터 (`_LightScreenParams`).
    - 출력: `_LightSplatViewDataOutput` 버퍼 (`LightViewData` 구조체 배열).
    - 작업: 각 스플랫에 대해 다음을 계산한다:
        1. `centerClipPos`: `sharedLightData.centerWorldPos`를 `_LightViewMatrix`와 `_LightProjMatrix`로 변환하여 광원 시점의 클립 공간 좌표 계산 (`LightViewData.centerClipPos`).
        2. 후방 컬링: `centerLightClipPos.w <= 0.0001f`이면 컬링.
        3. `CalcCovariance2D`: `centerWorldPos`, `sharedData.cov3d0`, `sharedData.cov3d1` 및 광원의 뷰/프로젝션 행렬을 사용하여 광원 시점에서 투영된 2D 공분산 행렬 계산.
        4. `DecomposeCovariance`: 2D 공분산으로부터 화면 공간 타원 축 `LightViewData.axis1`, `LightViewData.axis2` 계산.
        5. `LightViewData.opacity`: `sharedData.opacity` 재사용.

### 2.3. `ShadowCasterSplat.shader` (HLSL)

- **역할**: 각 스플랫을 광원 시점에서 2D 뎁스 텍스처에 렌더링하여 깊이 값을 기록한다.
- **버텍스 셰이더 (`vert_shadow_caster`)**:
    - 입력: `_LightSplatViewDataOutput` 버퍼 (스플랫별 `LightViewData`).
    - 작업:
        1. `LightViewData`에서 `centerClipPos` (중심 클립 공간 좌표), `axis1`, `axis2` (화면 공간 타원 축), `opacity`를 가져온다.
        2. `centerClipPos.w`를 확인하여 카메라 뒤 컬링.
        3. 로컬 쿼드 정점 좌표(`corner_offset_local`, 보통 `[-2, +2]` 범위)를 생성하여 `output.localPos`로 전달 (프래그먼트 셰이더의 가우시안 모양 계산용).
        4. `output.localPos`, `axis1`, `axis2`, `_LightScreenParams`를 사용하여 화면 공간 오프셋을 계산하고, 이를 클립 공간 오프셋으로 변환.
        5. `posCS.xy`에 클립 공간 오프셋을 더하여 최종 정점 위치 `output.positionCS` 계산. (`z`, `w`는 `posCS`의 값 사용).
        6. (필요시) `FlipProjectionIfBackbuffer` 호출.
- **프래그먼트 셰이더 (`frag_shadow_caster`)**:
    - 입력: `v2f_shadow_caster` (보간된 `localPos`, `splatOpacity`).
    - 작업:
        1. `power = -dot(input.localPos, input.localPos)`로 가우시안 감쇠 계산.
        2. `alpha_shape = exp(power)`로 모양에 따른 알파 계산.
        3. *(선택적)* `input.splatOpacity`에 대한 임계값 또는 `alpha_shape`에 대한 임계값을 사용하여 노이즈 스플랫 `discard`.
        4. `final_alpha = saturate(alpha_shape * input.splatOpacity)`.
        5. `if (final_alpha < THRESHOLD)`이면 `discard`. (THRESHOLD는 `1.0/255.0` 또는 조정된 값)
    - `ZWrite On`과 `ColorMask 0` 설정으로 인해, `discard`되지 않은 픽셀의 깊이 값만 렌더 타겟(2D 뎁스 텍스처)에 기록된다.

### 2.4. `GaussianSplatURPFeature.cs` (섀도우 패스 부분)

- **역할**: URP Render Graph 내에 섀도우 맵 생성 패스를 정의하고 실행한다.
- **`RecordRenderGraph` 메서드 내 섀도우 패스 로직**:
    1. `FindActiveShadowCaster()`를 통해 현재 활성화된 `GaussianSplatShadowRenderer` 인스턴스를 찾는다.
    2. `activeShadowCaster.IsRenderNeeded()`를 확인하여 섀도우 맵 업데이트가 필요한지 판단한다.
    3. 필요하다면, `ShadowPassData`를 사용하여 Render Graph 패스를 추가한다.
    4. `activeShadowCaster.GetShadowFaceDescriptor()`를 호출하여 2D 뎁스 텍스처에 대한 디스크립터를 얻는다.
    5. 루프를 돌며 6개의 2D `TextureHandle` (`passData.shadowFaceHandles[i]`)을 `renderGraph.CreateTexture` (또는 `UniversalRenderer.CreateRenderGraphTexture`)를 사용하여 생성하고, `builder.UseTexture`로 쓰기 접근을 설정한다.
    6. `builder.SetRenderFunc`를 정의:
        - `CommandBuffer`를 가져온다.
        - `activeShadowCaster.RenderShadowFacesURP(cmd, passData.shadowFaceHandles)`를 호출하여, 6개의 2D 텍스처 핸들에 그림자를 렌더링하는 명령을 기록한다.
        - 루프를 돌며 렌더링된 6개의 2D 텍스처 핸들(`passData.shadowFaceHandles[i]`)을 각각 고유한 이름(`s_ShadowMapFaceTextureGlobalIDs_Feature[i]`)으로 `cmd.SetGlobalTexture`를 사용하여 전역 셰이더 변수로 설정한다.
        - `activeShadowCaster.SetShadowParametersOnMainMaterial(resolvedFaceTextures)`를 호출하여 (여기서 `resolvedFaceTextures`는 `context.renderGraph.GetTexture`로 얻은 `RTHandle`에서 `.rt`를 통해 실제 `Texture` 배열로 변환하거나, 텍스처 설정은 전역 변수에 의존하고 이 함수는 비텍스처 유니폼만 설정하도록 할 수 있음) 주 스플랫 머티리얼에 그림자 관련 파라미터를 설정한다.

## 3. 주 스플랫 렌더링 및 그림자 적용 단계 (Main Splat Rendering & Shadow Application Phase)

이 단계에서는 이전 단계에서 생성된 6개의 2D 뎁스 텍스처를 사용하여 주 스플랫 렌더링 시 각 스플랫 픽셀에 그림자를 적용한다.

### 3.1. **`RenderGaussianSplats.shader`** 셰이더 (HLSL)

- **역할**: 가우시안 스플랫을 메인 카메라 시점에서 렌더링하고, 계산된 그림자 정보를 최종 색상에 반영한다.
- **유니폼 선언**:
    - 6개의 `Texture2D` (예: `_ShadowMapFacePX`)와 해당 `SamplerState` (예: `sampler_ShadowMapFacePX`)를 선언하여 6개의 2D 섀도우 맵 면을 받는다.
    - 광원 정보 (`_PointLightPosition`), 그림자 바이어스 (`_ShadowBias`), 광원의 Near/Far Plane (`_LightNearPlaneGS`, `_LightFarPlaneGS`) 등을 유니폼으로 받는다.
- **버텍스 셰이더 (`vert`)**:
    - `SplatViewData`에서 스플랫 중심의 월드 좌표(`view.worldPos_center`)를 읽어 프래그먼트 셰이더로 전달한다 (`o.worldPos`).
    - 기존 로직대로 스플랫의 화면상 위치(`o.clipPos`)와 가우시안 모양 계산용 로컬 좌표(`o.localGaussianPos`) 등을 계산한다.
- **프래그먼트 셰이더 (`frag`)**:
    1. 기존 로직대로 스플랫의 기본 색상(`calculatedColor`)과 모양 알파(`shapeAlpha`), 최종 알파(`finalAlpha`)를 계산하고, 선택된 스플랫 처리 및 `discard` 로직을 수행한다.
    2. **그림자 계산**:
        - `currentWorldPos = i.worldPos.xyz`: 현재 프래그먼트(스플랫 중심)의 월드 좌표.
        - `lightToFragDir = currentWorldPos - _PointLightPosition`: 광원에서 프래그먼트로 향하는 벡터.
        - `currentDepthFromLightLinear = length(lightToFragDir)`: 광원으로부터의 실제 선형 거리.
        - `normalizedLightToFragDir = normalize(lightToFragDir)`: 정규화된 방향 벡터.
        - `absLightToFragDir = abs(normalizedLightToFragDir)`.
        - `shadowMapHardwareDepth01 = SampleShadowMapFromFaces(normalizedLightToFragDir, absLightToFragDir)`: 헬퍼 함수를 호출하여, 3D 방향에 맞는 2D 섀도우 맵 면을 선택하고 해당 면의 UV를 계산하여 0~1 범위의 비선형 하드웨어 뎁스 값을 샘플링한다.
        - `occluderDepthLinear = LinearizeDeviceDepthToViewZ(shadowMapHardwareDepth01, _LightNearPlaneGS, _LightFarPlaneGS)`: 샘플링된 비선형 뎁스 값을 선형 뷰 공간 Z 거리로 변환한다.
        - `shadowFactor = 1.0h; if (currentDepthFromLightLinear > occluderDepthLinear + _ShadowBias) { shadowFactor = 0.3h; }`: 현재 프래그먼트의 선형 깊이와 그림자 맵의 선형화된 깊이를 비교하여 그림자 계수를 결정한다. (0.3h는 그림자 영역의 밝기 예시)
    3. **최종 색상 적용**:
        - `calculatedColor *= shadowFactor;`: 계산된 그림자 계수를 스플랫 색상에 곱한다.
        - `return half4(calculatedColor, finalAlpha);`: Non-Premultiplied Alpha 형식으로 최종 색상과 알파를 출력한다. (블렌딩 모드 `Blend OneMinusDstAlpha One`과 일치)

### 3.2. `SampleShadowMapFromFaces` 함수 (HLSL, "**RenderGaussianSplats.shader**" 내)

- 입력: `lightToFragNormalized`, `absLightToFragDir`.
- 작업:
    1. `absLightToFragDir`의 x, y, z 중 가장 큰 성분을 기준으로 주 축(major axis)을 결정하여 6개의 큐브맵 면 중 어떤 면에 해당하는지 판단한다.
    2. 선택된 면의 좌표계 규칙에 따라, `lightToFragNormalized`의 나머지 두 성분과 `recipMajorAxis`를 사용하여 해당 면 위의 2D 좌표(-1~1 범위)를 계산한다.
    3. 계산된 2D 좌표를 `0.5 + 0.5` 하여 0~1 범위의 UV 좌표로 변환한다.
    4. 해당 면에 대응하는 `Texture2D` 유니폼(예: `_ShadowMapFacePX`)과 샘플러를 사용하여 UV 위치에서 뎁스 값(R 채널)을 샘플링하여 반환한다.

### 3.3. `LinearizeDeviceDepthToViewZ` 함수 (HLSL, "**RenderGaussianSplats.shader**" 내)

- 입력: `nonLinearDepth01` (섀도우 맵에서 읽은 0~1 범위의 비선형 하드웨어 뎁스), `nearPlane` (`_LightNearPlaneGS`), `farPlane` (`_LightFarPlaneGS`).
- 작업: Unity/DirectX 스타일 프로젝션 기준의 표준 공식을 사용하여 비선형 뎁스 값을 선형적인 뷰 공간 Z 거리(양수)로 변환하여 반환한다.

### 3.4. `GaussianSplatURPFeature.cs` (메인 패스 부분)

- 섀도우 패스에서 `SetGlobalTexture`로 설정된 6개의 2D 섀도우 맵 텍스처와, `SetShadowParametersOnMainMaterial`을 통해 주 스플랫 머티리얼에 설정된 기타 그림자 유니폼(`_PointLightPosition`, `_ShadowBias` 등)을 사용하여 `GaussianSplatRenderSystem.instance.SortAndRenderSplats()`를 호출한다.
- `SortAndRenderSplats` 함수는 "Render Splats" 셰이더를 사용하여 스플랫을 렌더링하며, 이때 프래그먼트 셰이더는 위에서 설명한 그림자 계산 로직을 수행한다.

## 4. 주요 데이터 흐름 및 상호작용

1. **`GaussianSplatRenderer`**: 원본 스플랫 에셋 데이터(위치, 회전, 스케일, 색상, SH 계수 등)를 GPU 버퍼로 로드한다.
2. **`GaussianSplatShadowRenderer`**:
    - 광원 정보(위치, Near/Far Plane, 해상도)를 관리한다.
    - URP Feature에 2D 뎁스 텍스처 생성을 위한 디스크립터를 제공한다.
    - URP Feature로부터 6개의 2D `TextureHandle`을 받아, `CommandBuffer`에 섀도우 맵 생성 명령을 기록한다.
        - `CSCalcSharedLightData` 실행 -> `CSCalcLightViewData` 실행 (각 면에 대해) -> `ShadowCasterSplat.shader`로 `DrawProcedural` (각 면 텍스처에).
    - 주 스플랫 머티리얼에 그림자 관련 유니폼(광원 위치, 바이어스, 6개의 2D 섀도우 맵 텍스처 등)을 설정한다.
3. **`GaussianSplatURPFeature`**:
    - **섀도우 패스**: 6개의 2D 뎁스 `TextureHandle`을 생성하고, `GaussianSplatShadowRenderer`에 전달하여 렌더링을 지시한다. 렌더링된 6개 텍스처를 전역 셰이더 변수로 설정한다.
    - **메인 패스**: `GaussianSplatRenderSystem`을 통해 주 스플랫 렌더링을 수행한다. 이때 사용되는 "Render Splats" 셰이더는 전역으로 설정된 6개의 섀도우 맵 텍스처와 기타 유니폼을 사용하여 그림자를 계산한다.
4. **셰이더**:
    - `SplatUtilities.compute`: 스플랫 데이터를 GPU에서 효율적으로 처리하여 섀도우 패스와 메인 패스에 필요한 형태로 가공한다.
    - `ShadowCasterSplat.shader`: 광원 시점에서 스플랫을 2D 뎁스 텍스처에 그려 깊이 정보를 기록한다.
    - `RenderGaussianSplats.shader` 셰이더: 메인 카메라 시점에서 스플랫을 그리고, 6개의 2D 섀도우 맵을 샘플링하여 그림자를 적용한다.

## 5. 핵심 고려 사항 및 잠재적 개선점

- **노이즈 스플랫 필터링**:
    - `ShadowCasterSplat.shader`의 프래그먼트 셰이더에서 `input.splatOpacity`나 `alpha_shape`에 대한 임계값을 강화하여, 불필요한 노이즈 스플랫이 그림자를 생성하지 않도록 조절할 수 있다.
    - 또는, `CSCalcSharedLightData` 커널에서 원본 에셋의 불투명도나 스케일이 매우 작은 스플랫은 `opacity`를 0으로 만들어 섀도우 패스에서 자동으로 `discard` 되도록 할 수 있다.
    - 하지만 이것은 궁극적인 해결책이 아니다. 이상적으로는 노이즈가 없는 가우시안 스플랫 모델이 있어야 한다.
- **깊이 값 정밀도 및 선형화**:
    - 광원의 `lightNearPlane`과 `lightFarPlane` 설정은 섀도우 맵의 깊이 정밀도에 큰 영향을 미친다. 씬의 스케일에 맞게 적절히 조절해야 한다.
    - "RenderGaussianSplats.shader" 셰이더에서 `LinearizeDeviceDepthToViewZ` 함수를 사용하여 섀도우 맵의 비선형 하드웨어 뎁스 값을 선형 뷰 공간 깊이로 변환하여, 현재 프래그먼트의 선형 깊이와 올바르게 비교하는 것이 중요하다.
- **`SampleShadowMapFromFaces` 함수의 UV 계산 정확성**:
    - 이 함수 내의 UV 계산 로직은 `GaussianSplatShadowRenderer.cs`의 `GetLightViewMatrixForFace`에서 각 큐브맵 면을 렌더링할 때 사용한 뷰 행렬의 방향 및 업 벡터 설정과 정확히 일치해야 한다. 불일치 시 잘못된 면이나 UV에서 샘플링하여 그림자가 깨질 수 있다.
- **성능**:
    - 6개의 개별 2D 텍스처를 샘플링하는 것은 단일 `TextureCube`를 샘플링하는 것보다 셰이더 내 로직이 복잡해지고, 텍스처 페치(fetch)에 약간의 오버헤드가 있을 수 있다. 하지만 `SetRenderTarget`의 큐브맵 면 지정 기능에 문제가 있었으므로 이는 안정적인 대안이다.
    - `FindActiveShadowCaster` 메서드는 현재 매 프레임 `FindObjectsByType`을 호출하므로, 씬에 많은 `GaussianSplatRenderer`가 있다면 성능에 영향을 줄 수 있다. 중앙 시스템에서 활성 캐스터 목록을 관리하는 방식으로 최적화할 수 있다.