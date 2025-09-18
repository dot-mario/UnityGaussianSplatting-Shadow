// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // ReSharper disable once InconsistentNaming
    public class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        private GSRenderPass m_Pass;
        private bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }
        
        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            // GaussianSplatRenderSystem을 통해 렌더링할 스플랫이 있는지 확인하고 m_HasCamera 플래그 설정
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (system != null && system.GatherSplatsForCamera(cameraData.camera))
            {
                m_HasCamera = true;
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera) // OnCameraPreCull에서 설정된 플래그에 따라 패스 추가 여부 결정
                return;
            
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            base.Dispose(disposing);
        }

        // 모든 렌더링 로직을 담고 있는 내부 클래스
        private class GSRenderPass : ScriptableRenderPass
        {
            // 프로파일러 태그 및 셰이더 프로퍼티 ID
            private const string k_GaussianSplatRTName = "_GaussianSplatRT";
            // k_RGShadowCubemapName은 이제 사용되지 않음 (개별 면 텍스처 사용)
            private const string k_ProfilerTagRenderSplats = "GaussianSplatRender_URP";
            private const string k_ProfilerTagShadow = "GaussianSplatShadow_URP";

            private static readonly ProfilingSampler s_ProfilingSamplerRenderSplats = new(k_ProfilerTagRenderSplats);
            private static readonly ProfilingSampler s_ProfilingSamplerShadow = new(k_ProfilerTagShadow);

            private static readonly int s_GaussianSplatRT_ID = Shader.PropertyToID(k_GaussianSplatRTName);

            // GaussianSplatShadowRenderer에 정의된 6개 면 텍스처 ID 배열을 참조 (정적 멤버로 접근)
             private static readonly int[] s_ShadowMapFaceTextureGlobalIDs_Feature = new int[6] {
                 Shader.PropertyToID("_ShadowMapFacePX"), Shader.PropertyToID("_ShadowMapFaceNX"),
                 Shader.PropertyToID("_ShadowMapFacePY"), Shader.PropertyToID("_ShadowMapFaceNY"),
                 Shader.PropertyToID("_ShadowMapFacePZ"), Shader.PropertyToID("_ShadowMapFaceNZ")
             };


            // 렌더 패스에 데이터를 전달하기 위한 구조체
            private class ShadowPassData
            {
                internal GaussianSplatShadowRenderer shadowRenderer;
                internal TextureHandle[] shadowFaceHandles = new TextureHandle[6]; // 6개의 2D 텍스처 핸들
            }

            private class MainPassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var universalCameraData = frameData.Get<UniversalCameraData>();
                var universalResourceData = frameData.Get<UniversalResourceData>();
                
                var activeShadowCaster = FindActiveShadowCaster();

                // --- 1. 그림자 맵 렌더링 패스 (6개의 2D 텍스처 사용) ---
                if (activeShadowCaster != null && activeShadowCaster.IsRenderNeeded())
                {
                    using (var builder = renderGraph.AddUnsafePass<ShadowPassData>(k_ProfilerTagShadow, out var passData))
                    {
                        passData.shadowRenderer = activeShadowCaster;
                        RenderTextureDescriptor faceDesc = activeShadowCaster.GetShadowFaceDescriptor(); // 2D 뎁스용 디스크립터

                        for(int i=0; i<6; ++i)
                        {
                            // 각 면에 대한 TextureHandle 생성
                            passData.shadowFaceHandles[i] = UniversalRenderer.CreateRenderGraphTexture(renderGraph, faceDesc, $"_ShadowFaceRT_{i}", true);
                            builder.UseTexture(passData.shadowFaceHandles[i], AccessFlags.Write);
                        }
                        builder.AllowPassCulling(false); // 섀도우 캐스터가 있으면 항상 실행 (IsRenderNeeded로 이미 판단)

                        // GaussianSplatURPFeature.cs - GSRenderPass - RecordRenderGraph - Shadow Pass - SetRenderFunc 내부

                        builder.SetRenderFunc((ShadowPassData data, UnsafeGraphContext context) =>
                        {
                            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                            using (new ProfilingScope(cmd, s_ProfilingSamplerShadow))
                            {
                                // ShadowRenderer에 6개의 2D TextureHandle을 전달하여 렌더링 명령 기록
                                data.shadowRenderer.RenderShadowFacesURP(cmd, data.shadowFaceHandles);
                                
                                for(int i=0; i<6; ++i)
                                {
                                    cmd.SetGlobalTexture(s_ShadowMapFaceTextureGlobalIDs_Feature[i], data.shadowFaceHandles[i]);
                                }
                                
                                data.shadowRenderer.SetGlobalShadowParameters();
                            }
                        });
                    }
                }

                // --- 2. 주 스플랫 렌더링 패스 ---
                using (var builder = renderGraph.AddUnsafePass<MainPassData>(k_ProfilerTagRenderSplats, out var passData))
                {
                    RenderTextureDescriptor rtDesc = universalCameraData.cameraTargetDescriptor;
                    rtDesc.depthBufferBits = 0; 
                    rtDesc.msaaSamples = 1;
                    rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat; 
                    var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, k_GaussianSplatRTName, true);

                    passData.CameraData = universalCameraData;
                    passData.SourceTexture = universalResourceData.activeColorTexture;
                    passData.SourceDepth = universalResourceData.activeDepthTexture;
                    passData.GaussianSplatRT = textureHandle;

                    builder.UseTexture(universalResourceData.activeColorTexture, AccessFlags.ReadWrite);
                    builder.UseTexture(universalResourceData.activeDepthTexture); 
                    builder.UseTexture(textureHandle, AccessFlags.Write);
                    builder.AllowPassCulling(false); 

                    builder.SetRenderFunc((MainPassData data, UnsafeGraphContext context) =>
                    {
                        var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        using (new ProfilingScope(commandBuffer, s_ProfilingSamplerRenderSplats)) 
                        {
                            commandBuffer.SetGlobalTexture(s_GaussianSplatRT_ID, data.GaussianSplatRT); 
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                            
                            Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                            
                            if (matComposite != null)
                            {
                                commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose); 
                                Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                                commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                            }
                        }
                    });
                }
            }
            
            // 참고: 이 메서드는 매 프레임 모든 렌더러를 순회하므로 비용이 높을 수 있습니다.
            // 최적화를 위해 GaussianSplatRenderSystem에서 활성 섀도우 캐스터 목록을 관리하고,
            // Feature는 그 목록을 참조하는 것이 더 효율적일 수 있습니다.
            private GaussianSplatShadowRenderer FindActiveShadowCaster()
            {
                // 이 함수는 GaussianSplatRenderer가 활성화되어 있고, 유효한 에셋 및 렌더 설정을 가지고 있으며,
                // GaussianSplatShadowRenderer 컴포넌트가 추가되어 있고 활성화되어 있으며,
                // shadowCasterShader가 할당된 경우에 해당 ShadowRenderer를 반환합니다.
                // 여기서는 단순화를 위해 FindObjectsByType을 사용하지만, 실제 프로젝트에서는 최적화가 필요할 수 있습니다.
                var splatRenderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
                foreach (var renderer in splatRenderers)
                {
                    if (renderer.isActiveAndEnabled && renderer.HasValidAsset && renderer.HasValidRenderSetup)
                    {
                        var shadowCaster = renderer.GetComponent<GaussianSplatShadowRenderer>();
                        if (shadowCaster != null && shadowCaster.isActiveAndEnabled && shadowCaster.shadowCasterShader != null)
                        {
                            return shadowCaster; // 첫 번째로 찾은 활성 섀도우 캐스터 반환
                        }
                    }
                }
                return null;
            }
        }
    }
}
#endif