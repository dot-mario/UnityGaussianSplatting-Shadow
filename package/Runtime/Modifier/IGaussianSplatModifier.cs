// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime.Modifier
{
    public interface IGaussianSplatModifier
    {
        // GaussianSplatRenderer 활성화 또는 모디파이어 추가 시 호출
        // asset: 사용 중인 GaussianSplatAsset
        // renderer: GaussianSplatRenderer 인스턴스
        void OnInitialize(GaussianSplatAsset asset, GaussianSplatRenderer renderer);

        // GaussianSplatRenderer 비활성화 또는 모디파이어 제거 시 호출
        void OnShutdown(GaussianSplatRenderer renderer);

        // 모디파이어별 GPU 리소스 생성 (필요시 호출)
        // renderer: GaussianSplatRenderer 인스턴스
        // 반환 값: 리소스 설정 성공 여부
        bool SetupResources(GaussianSplatRenderer renderer);

        // 모디파이어별 GPU 리소스 해제 (필요시 호출)
        void ReleaseResources();

        // 매 프레임 뷰 종속 계산 (정렬, 뷰 데이터 계산 등) 전에 호출
        // cmd: CommandBuffer (Compute Shader 디스패치 등에 사용)
        // renderer: GaussianSplatRenderer 인스턴스 (GPU 버퍼 접근 등)
        // camera: 현재 렌더링 중인 카메라
        void ExecuteGPUModifications(CommandBuffer cmd, GaussianSplatRenderer renderer, Camera camera);

        // Splat 드로우 직전에 호출 (머티리얼 프로퍼티 블록에 사용자 정의 프로퍼티 설정)
        // mpb: 현재 드로우 콜에 사용될 MaterialPropertyBlock
        // renderer: GaussianSplatRenderer 인스턴스
        // camera: 현재 렌더링 중인 카메라
        void SetMaterialProperties(MaterialPropertyBlock mpb, GaussianSplatRenderer renderer, Camera camera);
    }
}