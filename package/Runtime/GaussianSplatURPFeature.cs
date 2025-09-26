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
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    public class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            
            const string RenderProfilerTag = "GaussianSplatRenderGraph";
            const string ShadowProfilerTag = "GaussianSplatShadowGraph";
            static readonly ProfilingSampler s_RenderProfilingSampler = new(RenderProfilerTag);
            static readonly ProfilingSampler s_ShadowProfilingSampler = new(ShadowProfilerTag);
            static readonly int s_GaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);
            static readonly int s_ShadowCubemap = Shader.PropertyToID("_ShadowCubemap");
            
            class RenderPassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
            }
            
            class ShadowPassData
            {
                internal GaussianSplatShadowRenderer ShadowRenderer;
                internal bool NeedsRender;
            }
            
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                
                var activeShadowRenderer = FindActiveShadowCaster();
                if (activeShadowRenderer != null)
                {
                    using var shadowBuilder = renderGraph.AddUnsafePass(ShadowProfilerTag, out ShadowPassData shadowPassData);
                    
                    shadowPassData.ShadowRenderer = activeShadowRenderer;
                    shadowPassData.NeedsRender = activeShadowRenderer.IsRenderNeeded();
                    
                    shadowBuilder.AllowPassCulling(false);
                    shadowBuilder.SetRenderFunc(static (ShadowPassData data, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        using var _ = new ProfilingScope(cmd, s_ShadowProfilingSampler);
                        var cubemap = data.ShadowRenderer.GetOrCreateShadowCubemap();
                        if (cubemap == null)
                            return;
                        if (data.NeedsRender)
                        {
                            data.ShadowRenderer.RenderShadowFacesURP(cmd, cubemap);
                        }
                        else if (!data.ShadowRenderer.HasValidShadowCubemap)
                        {
                            return;
                        }
                        cmd.SetGlobalTexture(s_ShadowCubemap, cubemap);
                        data.ShadowRenderer.SetGlobalShadowParameters();
                    });
                }
                
                using var builder = renderGraph.AddUnsafePass(RenderProfilerTag, out RenderPassData passData);
                
                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                var gaussianSplatRt = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);
                
                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = gaussianSplatRt;
                
                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(gaussianSplatRt, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (RenderPassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_RenderProfilingSampler);
                    commandBuffer.SetGlobalTexture(s_GaussianSplatRT, data.GaussianSplatRT);
                    CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                    Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                    if (matComposite == null)
                        return;
                    commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                    commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
            
            static GaussianSplatShadowRenderer FindActiveShadowCaster()
            {
                var splatRenderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
                foreach (var renderer in splatRenderers)
                {
                    if (!renderer.isActiveAndEnabled || !renderer.HasValidAsset || !renderer.HasValidRenderSetup)
                        continue;
                    var shadowRenderer = renderer.GetComponent<GaussianSplatShadowRenderer>();
                    if (shadowRenderer == null || !shadowRenderer.isActiveAndEnabled || shadowRenderer.shadowCasterShader == null)
                        continue;
                    return shadowRenderer;
                }
                return null;
            }
        }
        
        GSRenderPass m_Pass;
        bool m_HasCamera;
        
        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }
        
        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (system == null)
                return;
            if (system.GatherSplatsForCamera(cameraData.camera))
                m_HasCamera = true;
        }
        
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }
        
        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP