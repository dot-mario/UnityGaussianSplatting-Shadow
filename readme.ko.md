_Read this in other languages: [English](./readme.md)
***
# Unity Gaussian Splatting Shadow Rendering

Unity URP(Universal Render Pipeline) 환경에서 Gaussian Splatting 모델에 동적 포인트 라이트 그림자를 구현한 렌더링 시스템이다.

## TL;DR
Unity URP 환경에서 Gaussian Splatting 모델의 동적 포인트 라이트 그림자 구현이 완료되었다. 포인트 라이트 위치에서 6방향으로 뎁스를 렌더링하여 하나의 큐브 `RenderTexture`(6개 면)에 저장하고, 주 렌더링 패스에서 이 큐브맵을 샘플링해 그림자를 적용한다.

### 핵심 파이프라인:
1. **CSCalcSharedLightData (Compute Shader)**: 6번의 렌더링 루프 밖에서 한 번만 실행된다. 스플랫의 로컬 좌표, 월드 좌표, 3D 공분산 등 재사용 가능한 데이터를 계산하여 SharedLightData 버퍼에 저장한다.
2. **CSCalcLightViewData (Compute Shader)**: 큐브맵의 6개 면에 대해 각각 루프를 돌며 실행된다.
    * C#에서 현재 면에 맞는 올바른 GPU 규칙을 따른 _LightViewMatrix와 _LightProjMatrix를 전달받는다.
    * SharedLightData 버퍼를 입력으로 받아, 스플랫의 최종 클립 공간 위치와 화면상 모양(2D 타원 축)을 계산하여 LightViewData 버퍼에 저장한다.
3. **ShadowCasterSplat.shader (Vertex/Fragment Shader)**:
    * LightViewData 버퍼를 읽어 현재 바인딩된 큐브맵 면(깊이 + 컬러 마스크 0)에 `DrawProcedural`로 렌더링하여 해당 면의 깊이를 기록한다.

### 해결된 핵심 문제:
문제는 `GaussianSplatShadowRenderer.cs`에서 view matrix와 projection matrix를 생성할 때 GPU의 오른손 좌표계 규칙이 아닌 C# Unity의 왼손 좌표계 규칙을 따라 계산했기 때문에 발생했다. 이는 다음과 같이 해결되었다:
- **View Matrix**: UNITY_MATRIX_V와 동일한 구조로 직접 계산하여 GPU 좌표계 규칙을 준수
- **Projection Matrix**: `GL.GetGPUProjectionMatrix()`를 올바른 순서로 적용하여 GPU 규칙에 맞게 변환
- **전역 변수 사용**: 셰이더 전역 변수를 통한 더 효율적인 파라미터 전달

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
    accDescr: 2단계로 구성된 가우시안 스플래팅의 동적 그림자 생성 과정. 1단계는 섀도우 맵 생성, 2단계는 주 렌더링 및 그림자 적용이다.

    %% ==========================================
    %% 단계 1: 섀도우 맵 생성
    %% ==========================================
    subgraph "단계 1: 섀도우 맵 생성"
        direction TB

        %% 제어 및 루프 외부 노드 정의
        S1_Control["C#: 제어 및 디스패치"]
        S1_CS1(Compute: CSCalcSharedLightData)
        S1_Data1[(SharedLightData Buffer)]
        S1_Result[/Shadow Cubemap (6 faces)/]

        %% 흐름 정의: 제어 -> 루프 진입
        S1_Control -- Dispatch --> S1_CS1
        S1_CS1 -- "writes" --> S1_Data1

        %% <<< 루프 구간을 위한 중첩 서브그래프 >>>
        subgraph "Loop x6: 큐브맵 면별 반복 작업"
            direction TB
            S1_CS2(Compute: CSCalcLightViewData)
            S1_Data2[(LightViewData Buffer)]
            S1_HLSL(HLSL: ShadowCasterSplat.shader)

            %% 루프 내부 흐름
            S1_HLSL -- "next face" --> S1_CS2

            %% 루프 내부 데이터 흐름
            S1_CS2 -- "writes" --> S1_Data2
            S1_Data2 -- "reads" --> S1_HLSL
        end
        
        %% 루프 외부 데이터 흐름
        S1_Data1 -- "reads" --> S1_CS2
        S1_HLSL -- "writes to cubemap face[i]" --> S1_Result
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
        S2_HLSL --> S2_Blend
        S2_CS -- "writes" --> S2_Data1
        S2_Data1 -- "reads" --> S2_HLSL
        S2_Blend -- "writes" --> S2_Result
    end

    %% 단계 간 연결
    S1_Result -- "reads" --> S2_HLSL
```

## 1. 개요 (Overview)

본 문서는 Unity6 URP 환경에서 가우시안 스플래팅(Gaussian Splatting) 모델에 동적 포인트 라이트 그림자를 구현하기 위한 렌더링 파이프라인을 기술한다. 핵심 목표는 광원 위치에서 6방향으로 뎁스를 생성해 하나의 섀도우 큐브맵에 저장하고, 이를 주 스플랫 렌더링 시 각 스플랫의 그림자 여부를 판정하는 데 사용하는 것이다.

<aside>
💡

이전에는 큐브맵 각 면(face)에 직접 렌더링할 때 발생하는 문제를 우회하기 위해, **각 면의 뎁스를 6개의 개별 2D 렌더 텍스처에 기록**한 뒤 주 렌더링 셰이더에서 이를 샘플링했다. 현재는 GPU 큐브맵(`RenderTextureDimension.Cube`)에 직접 렌더링하도록 개선하여 해당 워크어라운드를 제거했다.

</aside>

**주요 구성 요소:**

- **`GaussianSplatRenderer.cs`**: 개별 가우시안 스플랫 에셋의 렌더링을 담당하는 주 컴포넌트다.
- **`GaussianSplatShadowRenderer.cs`**: 특정 `GaussianSplatRenderer`에 연결되어 포인트 라이트의 섀도우 맵 생성을 전담하는 컴포넌트다. 공유 섀도우 큐브맵을 관리하고 렌더링 명령을 기록한다.
- **`GaussianSplatURPFeature.cs`**: URP의 `ScriptableRendererFeature`로, 렌더 그래프(Render Graph) 내에 섀도우 맵 생성 패스와 주 스플랫 렌더링 패스를 삽입하고 관리한다.
- **`SplatUtilities.compute` (Compute Shader)**: 스플랫 데이터의 GPU 기반 처리를 담당한다.
    - `CSCalcSharedLightData`: 광원과 무관하게 미리 계산될 수 있는 스플랫 데이터(위치, 3D 공분산, 원본 불투명도 등)를 준비한다.
    - `CSCalcLightViewData`: `CSCalcSharedLightData`의 출력을 받아, 특정 광원 시점에서의 스플랫 뷰 데이터(클립 공간 위치, 2D 타원 축 등)를 계산한다.
- **`ShadowCasterSplat.shader` (HLSL Shader)**: 섀도우 맵 생성 패스에서 사용되며, 각 스플랫을 큐브맵 면의 깊이 버퍼에 렌더링한다.
- **`RenderGaussianSplats.shader` (HLSL Shader)**: 주 스플랫 렌더링 패스에서 사용되며, 섀도우 큐브맵을 샘플링해 최종 스플랫 색상에 그림자를 적용한다.
- **`GaussianSplatting.hlsl` (HLSL Include)**: 공통 구조체(예: `SplatData`, `SplatViewData`, `SharedLightData`, `LightViewData`) 및 유틸리티 함수를 포함한다.

## 2. 섀도우 맵 생성 단계 (Shadow Map Generation Phase)

이 단계의 목표는 포인트 라이트 위치에서 6방향으로 씬을 렌더링하여 각 방향에 대한 뎁스 맵을 생성하는 것이다.

### 2.1. `GaussianSplatShadowRenderer.cs`의 역할

- **섀도우 큐브맵 관리**:
    - `RenderTexture m_ShadowCubemapRT`: 여섯 면을 모두 포함하는 재사용 가능한 큐브 `RenderTexture`. `GetOrCreateShadowCubemap()`에서 요청한 해상도와 포맷으로 생성하거나 재활용한다.
    - `HasValidShadowCubemap`: 현재 큐브맵 데이터가 최신인지 추적하여 불필요한 리렌더링을 피한다.
- **컴퓨트 셰이더 및 렌더링 준비**:
    - `EnsureGpuResourcesForCompute()`: `m_LightViewDataBuffer`, `m_SharedLightDataBuffer` 등 컴퓨트 셰이더에 필요한 GPU 버퍼를 준비한다.
    - `EnsureShadowCasterMaterial()`: `shadowCasterShader`를 사용하는 머티리얼(`m_ShadowCasterMaterial`)을 확보한다.
- **정확한 View/Projection Matrix 계산**:
    - **GPU 호환 Projection Matrix**: `GL.GetGPUProjectionMatrix()`를 올바른 순서로 적용하여 GPU 규칙에 맞게 변환한다.
    - **면별 View Matrix**: `GetLightViewMatrixForFace()`에서 UNITY_MATRIX_V와 동일한 구조로 각 큐브맵 면의 뷰 행렬을 계산한다.
    - **전역 셰이더 변수**: `SetGlobalShadowParameters()`를 통해 광원 위치, 바이어스, 근/원거리, 밝기 제어, `ZBufferParams` 등을 전역으로 설정한다.
- **URP 연동 메서드**:
    - `RenderShadowFacesURP(CommandBuffer cmd, RenderTexture shadowCubemap)`: URP Feature가 전달한 `CommandBuffer`와 공유 큐브맵을 사용한다.
        1. `DispatchSharedDataKernel(cmd)`: `CSCalcSharedLightData` 커널을 한 번 디스패치해 모든 스플랫의 광원 공통 데이터를 계산하고 `m_SharedLightDataBuffer`에 저장한다.
        2. 루프 (6회 반복, 큐브맵 각 면 처리):
            - `GetLightViewMatrixForFace((CubemapFace)i)`로 현재 면에 맞는 뷰 행렬을 구한다.
            - `GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(...), true)`로 GPU 호환 프로젝션 행렬을 적용한다.
            - 컴퓨트 셰이더 파라미터(`_LightViewMatrix`, `_LightModelViewMatrix`, `_LightProjMatrix`, `_LightScreenParams` 등)를 업데이트하고 `CSCalcLightViewData`를 디스패치하여 해당 면의 `LightViewData`를 채운다.
            - `cmd.SetRenderTarget(shadowCubemap, 0, face)`로 큐브맵의 특정 면을 깊이 렌더 타겟으로 바인딩한다.
            - `cmd.ClearRenderTarget(true, false, Color.clear, 1.0f)`로 해당 면의 깊이를 초기화한다.
            - `MaterialPropertyBlock`에 `shadowAlphaCutoff` 등을 설정하고 `cmd.DrawProcedural(...)`로 `ShadowCasterSplat.shader`를 실행해 깊이를 기록한다.
        3. 디버그 강제 렌더링이 아니면 큐브맵을 유효한 상태로 표시한다.
- **기타**: `IsRenderNeeded()`, `MarkShadowsDirty()`, `HasSettingsChanged()`, `UpdatePreviousSettings()` 등 상태 관리 메서드를 통해 갱신 여부를 판단한다.

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

- **역할**: 각 스플랫을 광원 시점에서 활성화된 큐브맵 면에 렌더링하여 깊이 값을 기록한다.
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
    - `ZWrite On`과 `ColorMask 0` 설정으로 인해, `discard`되지 않은 픽셀의 깊이 값만 렌더 타겟(큐브맵 면)에 기록된다.

### 2.4. `GaussianSplatURPFeature.cs` (섀도우 패스 부분)

- **역할**: URP Render Graph 내에 섀도우 맵 생성 패스를 정의하고 실행한다.
- **`RecordRenderGraph` 메서드 내 섀도우 패스 로직**:
    1. `FindActiveShadowCaster()`를 통해 현재 활성화된 `GaussianSplatShadowRenderer` 인스턴스를 찾는다.
    2. `activeShadowCaster.IsRenderNeeded()`를 확인하여 섀도우 맵 업데이트가 필요한지 판단한다.
    3. Render Graph 패스를 추가하여 다음을 수행한다:
        - `activeShadowCaster.GetOrCreateShadowCubemap()`으로 공유 큐브맵을 확보한다.
        - 렌더링이 필요하면 `activeShadowCaster.RenderShadowFacesURP(cmd, cubemap)`을 호출하여 큐브맵을 갱신한다.
        - 큐브맵 생성에 실패했거나 아직 유효하지 않다면 조기 종료한다.
        - `_ShadowCubemap` 전역 텍스처와 그림자 관련 유니폼을 `activeShadowCaster.SetGlobalShadowParameters()`로 설정한다.

## 3. 주 스플랫 렌더링 및 그림자 적용 단계 (Main Splat Rendering & Shadow Application Phase)

이 단계에서는 이전 단계에서 생성된 섀도우 큐브맵을 사용하여 주 스플랫 렌더링 시 각 스플랫 픽셀에 그림자를 적용한다.

### 3.1. **`RenderGaussianSplats.shader`** 셰이더 (HLSL)

- **역할**: 가우시안 스플랫을 메인 카메라 시점에서 렌더링하고, 계산된 그림자 정보를 최종 색상에 반영한다.
- **유니폼 선언**:
    - 하나의 `TEXTURECUBE(_ShadowCubemap)`과 전용 샘플러를 선언해 6개의 면을 통합으로 접근한다.
    - 광원 정보(`_PointLightPosition`), 그림자 바이어스(`_ShadowBias`), Near/Far Plane(`_LightNearPlaneGS`, `_LightFarPlaneGS`), `_LightZBufferParams`, 밝기 제어(`_LightBrightness`, `_ShadowBrightness`) 등을 유니폼으로 받는다.
- **버텍스 셰이더 (`vert`)**:
    - `SplatViewData`에서 스플랫 중심의 월드 좌표(`view.worldPos_center`)를 읽어 프래그먼트 셰이더로 전달한다 (`o.worldPos`).
    - 기존 로직대로 스플랫의 화면상 위치(`o.clipPos`)와 가우시안 모양 계산용 로컬 좌표(`o.localGaussianPos`) 등을 계산한다.
- **프래그먼트 셰이더 (`frag`)**:
    1. 기존 로직대로 스플랫의 기본 색상(`calculatedColor`)과 모양 알파(`shapeAlpha`), 최종 알파(`finalAlpha`)를 계산하고, 선택된 스플랫 처리 및 `discard` 로직을 수행한다.
    2. **그림자 계산**:
        - `half visibility = SamplePointShadow(i.worldPos)`: 섀도우 큐브맵을 샘플링해 현재 프래그먼트의 가시성을 계산한다.
        - 보조 함수는 `lightVec`의 지배적인 축을 사용해 샘플 방향을 결정하고, `_LightZBufferParams`를 이용해 저장된 깊이를 선형화한 뒤 `_ShadowBias`를 적용하여 0~1 값을 반환한다.
    3. **최종 색상 적용**:
        - `half lightIntensity = lerp(_ShadowBrightness, _LightBrightness, visibility)`로 명암 비율을 보간한다.
        - `return half4(i.col.rgb * lightIntensity * alpha, alpha)`로 기존 블렌딩 설정에 맞춰 결과를 출력한다.

### 3.2. `SamplePointShadow` 함수 (HLSL, "**RenderGaussianSplats.shader**" 내)

**개선된 점광원 그림자 계산 함수**

- **입력**: `float3 worldPos` (현재 프래그먼트의 월드 좌표)
- **작업**:
    1. **광원 벡터 계산**: `lightVec = worldPos - _PointLightPosition`으로 광원에서 픽셀로의 벡터 계산
    2. **선형 깊이 산출**: `abs(lightVec)`의 최대 성분을 현재 프래그먼트의 선형 깊이로 사용한다.
    3. **큐브맵 샘플링**: `SAMPLE_TEXTURECUBE(_ShadowCubemap, sampler_ShadowCubemap, lightVec)`으로 저장된 깊이를 읽는다.
    4. **깊이 선형화**: `_LightZBufferParams`를 이용한 `LinearEyeDepth` 호출로 저장된 값을 선형 공간으로 변환한다.
    5. **깊이 비교**: 현재 깊이와 샘플 깊이를 `_ShadowBias`와 함께 비교하여 가시성을 결정한다.
- **반환값**: `half visibility` (1.0 = 빛을 받음, 0.0 = 그림자 영역)

**주요 개선 사항**:
- 프래그먼트 단계에서 VP 행렬 및 UV를 직접 재구성할 필요가 없다.
- 하드웨어 큐브맵 샘플러를 활용하여 텍스처 바인딩 수와 상수 데이터를 줄였다.
- `_LightZBufferParams` 기반 선형화로 Unity의 역Z/정Z 설정과 일관성을 맞춘다.

### 3.3. `GaussianSplatURPFeature.cs` (메인 패스 부분)

- 섀도우 패스에서 전역으로 설정된 `_ShadowCubemap`과 그림자 관련 유니폼(`_PointLightPosition`, `_ShadowBias`, `_LightBrightness`, `_ShadowBrightness`, `_LightZBufferParams` 등)을 사용하여 `GaussianSplatRenderSystem.instance.SortAndRenderSplats()`를 호출한다.
- `SortAndRenderSplats` 함수는 "Render Splats" 셰이더를 실행하며, 프래그먼트 셰이더는 `_ShadowCubemap`을 샘플링하는 `SamplePointShadow`를 통해 그림자를 적용한다.

## 4. 주요 데이터 흐름 및 상호작용

1. **`GaussianSplatRenderer`**: 원본 스플랫 에셋 데이터(위치, 회전, 스케일, 색상, SH 계수 등)를 GPU 버퍼로 로드한다.
2. **`GaussianSplatShadowRenderer`**:
    - 광원 정보(위치, Near/Far Plane, 해상도)를 관리하고 재사용 가능한 섀도우 큐브맵을 소유한다.
    - `CSCalcSharedLightData` → `CSCalcLightViewData`(×6) → `ShadowCasterSplat.shader` 순서로 큐브맵 각 면을 채우는 렌더링 명령을 기록한다.
    - `SetGlobalShadowParameters()`를 통해 `_ShadowCubemap`과 관련 유니폼을 전역으로 설정한다.
3. **`GaussianSplatURPFeature`**:
    - **섀도우 패스**: 활성 섀도우 렌더러를 찾아 큐브맵을 확보/갱신하고, `_ShadowCubemap`과 그림자 유니폼을 전역으로 노출한다.
    - **메인 패스**: `GaussianSplatRenderSystem`을 통해 주 스플랫 렌더링을 수행하며, 전역 큐브맵과 유니폼을 사용한다.
4. **셰이더**:
    - `SplatUtilities.compute`: 스플랫 데이터를 GPU에서 효율적으로 처리하여 섀도우 패스와 메인 패스에 필요한 형태로 가공한다.
    - `ShadowCasterSplat.shader`: 광원 시점에서 스플랫을 큐브맵 면에 렌더링해 깊이를 기록한다.
    - `RenderGaussianSplats.shader`: 메인 카메라 시점에서 스플랫을 렌더링하고 `_ShadowCubemap`을 샘플링하여 그림자를 적용한다.

## 5. 구현된 주요 기능 및 개선 사항

### 해결된 핵심 문제들:
- **View/Projection Matrix 좌표계 문제**: GPU 규칙을 따르는 올바른 행렬 계산으로 해결
- **섀도우 큐브맵 통합**: 6개의 개별 렌더 텍스처 대신 단일 큐브 `RenderTexture`로 전환하면서 면별 렌더링 품질 유지
- **전역 셰이더 변수 최적화**: `_ShadowCubemap`, `_LightZBufferParams`, 밝기 제어 등 파라미터를 효율적으로 전달
- **그림자 계산 단순화**: 하드웨어 큐브맵 샘플링을 사용하는 `SamplePointShadow`로 로직을 통합

### 구현된 기능들:
- **노이즈 스플랫 필터링**:
    - `shadowAlphaCutoff` 파라미터를 통한 조절 가능한 알파 컷오프 (기본값: 0.2)
    - `ShadowCasterSplat.shader`에서 불필요한 노이즈 스플랫 자동 제거
- **정확한 깊이 처리**:
    - 광원의 `lightNearPlane`과 `lightFarPlane` 설정으로 섀도우 맵의 깊이 정밀도 조절
    - `_LightZBufferParams`와 `LinearEyeDepth`를 이용해 역Z/정Z 모두 동일하게 처리
- **섀도우 큐브맵 연동**:
    - `RenderShadowFacesURP`가 큐브맵 면에 직접 깊이를 기록하고, 필요 시에만 재렌더링
    - `SetGlobalShadowParameters()`에서 `_ShadowCubemap`, `_LightBrightness`, `_ShadowBrightness` 등 전역 상태를 노출
- **올바른 좌표계 및 샘플링**:
    - `GetLightViewMatrixForFace`가 UNITY_MATRIX_V 구조를 그대로 따르며, 컴퓨트 단계와 큐브맵 규칙을 일치시킨다.
    - `SamplePointShadow`가 큐브맵 샘플러를 사용해 UV 재구성 로직을 제거
- **성능 최적화**:
    - 메인 패스에서 바인딩해야 할 텍스처 수를 6개에서 1개로 감소
    - 큐브맵 리소스를 프레임 간 재활용하여 할당/커맨드 버퍼 오버헤드 축소

### 추가 개선 가능 사항:
- **성능 최적화**:
    - `FindActiveShadowCaster` 메서드의 매 프레임 호출을 중앙 시스템 관리로 최적화 가능
    - 큐브맵 면별 컬링을 통한 불필요한 렌더링 제거 가능
- **품질 향상**:
    - 그림자 맵 해상도를 동적으로 조절하는 LOD 시스템 추가 가능
    - 소프트 섀도우 구현을 위한 PCF(Percentage-Closer Filtering) 적용 가능
