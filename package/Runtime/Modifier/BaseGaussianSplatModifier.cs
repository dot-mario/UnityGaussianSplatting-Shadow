// SPDX-License-Identifier: MIT
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime.Modifier
{
    public abstract class BaseGaussianSplatModifier : MonoBehaviour, IGaussianSplatModifier
    {
        [Header("Modifier Settings")]
        [Tooltip("Controls whether this modifier's logic is applied.")]
        public bool IsModifierActive = true; // 커스텀 활성화 변수

        protected GaussianSplatRenderer TargetRenderer { get; private set; }
        protected GaussianSplatAsset TargetAsset { get; private set; }

        // IGaussianSplatModifier 인터페이스 구현 (필요시 virtual 또는 abstract로 선언)
        public virtual void OnInitialize(GaussianSplatAsset asset, GaussianSplatRenderer renderer)
        {
            TargetRenderer = renderer;
            TargetAsset = asset;
            // Debug.Log($"{this.GetType().Name} on {gameObject.name} Initialized by {renderer.gameObject.name}");
        }

        public virtual void OnShutdown(GaussianSplatRenderer renderer)
        {
            // Debug.Log($"{this.GetType().Name} on {gameObject.name} Shutdown by {renderer.gameObject.name}");
            TargetRenderer = null;
            TargetAsset = null;
        }

        public virtual bool SetupResources(GaussianSplatRenderer renderer)
        {
            // 기본적으로 할 일 없음, 파생 클래스에서 필요시 재정의
            return true;
        }

        public virtual void ReleaseResources()
        {
            // 기본적으로 할 일 없음, 파생 클래스에서 필요시 재정의
        }

        // 파생 클래스에서 반드시 구현해야 할 핵심 로직
        public abstract void ExecuteGPUModifications(CommandBuffer cmd, GaussianSplatRenderer renderer, Camera camera);
        public abstract void SetMaterialProperties(MaterialPropertyBlock mpb, GaussianSplatRenderer renderer, Camera camera);
    }
}