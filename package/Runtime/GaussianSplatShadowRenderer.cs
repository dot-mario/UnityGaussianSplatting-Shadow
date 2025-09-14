// SPDX-License-Identifier: MIT

using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering; // Required for TextureHandle in some Unity versions
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization; // Required for UniversalRenderPipelineAsset

namespace GaussianSplatting.Runtime
{
    [ExecuteAlways]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianSplatShadowRenderer : MonoBehaviour
    {
        [FormerlySerializedAs("pointLightPosition")] [Header("광원 설정 (Light Settings)")]
        public Transform pointLightTransform;

        [Header("그림자 맵 설정 (Shadow Map Settings)")]
        public int shadowCubemapResolution = 1024;
        public float lightNearPlane = 0.5f;
        public float lightFarPlane = 100.0f;
        public float shadowBias = 0.005f;

        [Header("셰이더 참조 (Shader Reference)")]
        public Shader shadowCasterShader; // 필수

        [Header("디버깅 (Debugging)")]
        [Tooltip("디버깅을 위해 매 프레임 큐브맵 렌더링을 강제합니다 (성능 저하 유발).")]
        public bool forceCubemapRenderForDebug = false;

        private RenderTexture[] m_ShadowFaceRTs = new RenderTexture[6]; // 6개의 2D RT (비URP 경로에서 주로 사용)
        private Material m_ShadowCasterMaterial;
        private GaussianSplatRenderer m_GaussianSplatRenderer;
        private bool m_shadowsDirty = true;

        // 이전 상태 저장 변수들
        private Vector3 m_PreviousPointLightPosition;
        private int m_PreviousShadowCubemapResolution;
        private float m_PreviousLightNearPlane;
        private float m_PreviousLightFarPlane;
        private Hash128 m_PreviousAssetDataHash;
        private float m_PreviousSplatScale;

        // Shader Property IDs
        // 컴퓨트 셰이더 및 섀도우 캐스터 셰이더용 ID
        private static readonly int s_LightViewMatrixID = Shader.PropertyToID("_LightViewMatrix");
        private static readonly int s_LightModelViewMatrixID = Shader.PropertyToID("_LightModelViewMatrix");
        private static readonly int s_LightProjMatrixID = Shader.PropertyToID("_LightProjMatrix");
        private static readonly int s_LightScreenParamsID_Shadow = Shader.PropertyToID("_LightScreenParams"); // 섀도우캐스터용
        private static readonly int s_LightSplatViewDataOutputID = Shader.PropertyToID("_LightSplatViewDataOutput");
        private static readonly int s_SharedLightDataInputID = Shader.PropertyToID("_SharedLightDataInput");
        private static readonly int s_SharedLightDataOutputID = Shader.PropertyToID("_SharedLightDataOutput");

        // 주 스플랫 셰이더에 전달할 6개의 2D 섀도우 맵 텍스처 이름 ID
        private static readonly int[] s_ShadowMapFaceTextureIDs = new int[6] {
            Shader.PropertyToID("_ShadowMapFacePX"), Shader.PropertyToID("_ShadowMapFaceNX"),
            Shader.PropertyToID("_ShadowMapFacePY"), Shader.PropertyToID("_ShadowMapFaceNY"),
            Shader.PropertyToID("_ShadowMapFacePZ"), Shader.PropertyToID("_ShadowMapFaceNZ")
        };
        // 주 스플랫 셰이더에 전달할 광원 파라미터 ID
        private static readonly int s_PointLightPositionID_Main = Shader.PropertyToID("_PointLightPosition");
        private static readonly int s_ShadowBiasID_Main = Shader.PropertyToID("_ShadowBias");
        private static readonly int s_LightFarPlaneID_Main = Shader.PropertyToID("_LightFarPlaneGS");
        private static readonly int s_LightNearPlaneID_Main = Shader.PropertyToID("_LightNearPlaneGS");

        private ComputeShader m_SplatUtilitiesCS;
        private GraphicsBuffer m_LightViewDataBuffer;
        private GraphicsBuffer m_SharedLightDataBuffer;
        private int m_CSCalcLightViewDataKernel = -1;
        private int m_CSCalcSharedLightDataKernel = -1;

        // 프로파일러 마커
        internal static readonly ProfilerMarker s_ProfCalcSharedData = new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.CalcSharedData", MarkerFlags.SampleGPU);
        private static readonly ProfilerMarker[] s_ProfDrawFaceMarkers = new ProfilerMarker[6]
        {
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_PX", MarkerFlags.SampleGPU),
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_NX", MarkerFlags.SampleGPU),
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_PY", MarkerFlags.SampleGPU),
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_NY", MarkerFlags.SampleGPU),
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_PZ", MarkerFlags.SampleGPU),
            new ProfilerMarker(ProfilerCategory.Render, "GaussianShadow.DrawFace_NZ", MarkerFlags.SampleGPU)
        };
        
        void Awake()
        {
            m_GaussianSplatRenderer = GetComponent<GaussianSplatRenderer>();
        }

        void OnEnable()
        {
            if (m_GaussianSplatRenderer == null)
                m_GaussianSplatRenderer = GetComponent<GaussianSplatRenderer>();

            if (m_GaussianSplatRenderer == null || m_GaussianSplatRenderer.CSSplatUtilities == null) // GaussianSplatRenderer의 CSSplatUtilities 접근자 사용
            {
                Debug.LogError($"[{nameof(GaussianSplatShadowRenderer)}] GaussianSplatRenderer 또는 그것의 CSSplatUtilities가 할당되지 않았습니다. 이 컴포넌트를 비활성화합니다.", this);
                enabled = false;
                return;
            }
            m_SplatUtilitiesCS = m_GaussianSplatRenderer.CSSplatUtilities;
            m_CSCalcLightViewDataKernel = m_SplatUtilitiesCS.FindKernel("CSCalcLightViewData");
            m_CSCalcSharedLightDataKernel = m_SplatUtilitiesCS.FindKernel("CSCalcSharedLightData");

            if (m_CSCalcLightViewDataKernel == -1 || m_CSCalcSharedLightDataKernel == -1)
            {
                Debug.LogError($"[{nameof(GaussianSplatShadowRenderer)}] '{m_SplatUtilitiesCS.name}'에서 필요한 컴퓨트 셰이더 커널을 찾을 수 없습니다. 이 컴포넌트를 비활성화합니다.", this);
                enabled = false;
                return;
            }
            
            CleanupResources(); // 이전 리소스 정리 확실히
            m_shadowsDirty = true;
            UpdatePreviousSettings();
        }

        void OnDisable()
        {
            CleanupResources();
        }
        
        public void MarkShadowsDirty()
        {
            m_shadowsDirty = true;
        }

        void OnValidate()
        {
            if (shadowCubemapResolution < 16) shadowCubemapResolution = 16;
            if (lightFarPlane <= lightNearPlane) lightFarPlane = lightNearPlane + 0.1f;
            m_shadowsDirty = true;
        }

        void Update()
        {
            if (IsURPActive()) return; // URP 환경에서는 URP Feature가 렌더링을 제어

            // 비 URP 환경을 위한 로직
            if (!m_GaussianSplatRenderer || !m_GaussianSplatRenderer.isActiveAndEnabled || !shadowCasterShader) return;
            if (!m_GaussianSplatRenderer.HasValidAsset || !m_GaussianSplatRenderer.HasValidRenderSetup) return;

            if (IsRenderNeeded())
            {
                RenderShadowFacesNonURP(); 
            }
            // 비URP 시에도 주 렌더러의 머티리얼에 텍스처와 파라미터 설정
            SetShadowParametersOnMainMaterial();
        }
        
        public bool IsRenderNeeded()
        {
            if (HasSettingsChanged())
            {
                m_shadowsDirty = true;
            }
            return m_shadowsDirty || forceCubemapRenderForDebug;
        }

        public RenderTextureDescriptor GetShadowFaceDescriptor() // URP Feature가 2D 뎁스 텍스처 디스크립터를 요청할 때 사용
        {
            var desc = new RenderTextureDescriptor(shadowCubemapResolution, shadowCubemapResolution)
            {
                graphicsFormat = GraphicsFormat.None, // 컬러 버퍼 없음
                depthStencilFormat = GraphicsFormat.D32_SFloat, // 32비트 부동소수점 뎁스
                dimension = TextureDimension.Tex2D,
                autoGenerateMips = false,
                useMipMap = false,
                msaaSamples = 1,
                memoryless = RenderTextureMemoryless.None,
                shadowSamplingMode = ShadowSamplingMode.None // 개별 2D 뎁스 RT는 보통 이 설정
            };
            return desc;
        }
        
        

        void DispatchSharedDataKernel(CommandBuffer cmd)

        {
            cmd.BeginSample(s_ProfCalcSharedData);
            
            m_GaussianSplatRenderer.SetAssetDataOnCS(cmd, GaussianSplatRenderer.KernelIndices.CalcSharedLightData);
            
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, GaussianSplatRenderer.Props.SplatPos, m_GaussianSplatRenderer.GpuPosData);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, GaussianSplatRenderer.Props.SplatChunks, m_GaussianSplatRenderer.GpuChunksBuffer);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, GaussianSplatRenderer.Props.SplatOther, m_GaussianSplatRenderer.GpuOtherData);
            cmd.SetComputeTextureParam(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, GaussianSplatRenderer.Props.SplatColor, m_GaussianSplatRenderer.GpuColorTexture);
            
            cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.MatrixObjectToWorld, m_GaussianSplatRenderer.transform.localToWorldMatrix);
            cmd.SetComputeFloatParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatScale, m_GaussianSplatRenderer.m_SplatScale);
            cmd.SetComputeFloatParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatOpacityScale, m_GaussianSplatRenderer.m_OpacityScale);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatCount, m_GaussianSplatRenderer.splatCount);
            
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, s_SharedLightDataOutputID, m_SharedLightDataBuffer);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatChunkCount, m_GaussianSplatRenderer.m_GpuChunksValid ? m_GaussianSplatRenderer.GpuChunksBuffer.count : 0);

            m_SplatUtilitiesCS.GetKernelThreadGroupSizes(m_CSCalcSharedLightDataKernel, out uint gsX, out _, out _);
            int threadGroups = (m_GaussianSplatRenderer.splatCount + (int)gsX - 1) / (int)gsX;

            cmd.DispatchCompute(m_SplatUtilitiesCS, m_CSCalcSharedLightDataKernel, threadGroups, 1, 1);
            
            cmd.EndSample(s_ProfCalcSharedData);
        }

        // URP Feature가 호출: 6개의 2D TextureHandle에 렌더링 명령 기록
        public void RenderShadowFacesURP(CommandBuffer cmd, TextureHandle[] faceTextureHandles)
        {
            if (faceTextureHandles == null || faceTextureHandles.Length != 6)
            {
                Debug.LogError("RenderShadowFacesURP: 6개의 TextureHandle이 필요합니다.", this);
                return;
            }

            EnsureGpuResourcesForCompute(); 
            if (m_LightViewDataBuffer == null || !m_LightViewDataBuffer.IsValid() || m_SharedLightDataBuffer == null || !m_SharedLightDataBuffer.IsValid())
            {
                Debug.LogError("RenderShadowFacesURP: GPU 버퍼가 준비되지 않았습니다.", this);
                return;
            }
            EnsureShadowCasterMaterial(); // 머티리얼 확인

            

            DispatchSharedDataKernel(cmd); // 공유 데이터 계산 (1회)
            
            
            m_GaussianSplatRenderer.SetAssetDataOnCS(cmd, GaussianSplatRenderer.KernelIndices.CalcLightViewData);

            Matrix4x4 lightProjectionMatrix = GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(90f, 1.0f, lightNearPlane, lightFarPlane), true);

            // 루프 외부에서 설정 가능한 컴퓨트 셰이더 파라미터 (CSCalcLightViewDataKernel용)
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatPos, m_GaussianSplatRenderer.GpuPosData);
            cmd.SetComputeTextureParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatColor, m_GaussianSplatRenderer.GpuColorTexture); // GpuColorTexture 접근자 필요
            cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.MatrixObjectToWorld, m_GaussianSplatRenderer.transform.localToWorldMatrix);
            
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, s_SharedLightDataInputID, m_SharedLightDataBuffer);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, s_LightSplatViewDataOutputID, m_LightViewDataBuffer);
            
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatBitsValid, (m_GaussianSplatRenderer.m_GpuEditSelected != null && m_GaussianSplatRenderer.m_GpuEditSelected.IsValid()) && (m_GaussianSplatRenderer.GpuEditDeleted != null && m_GaussianSplatRenderer.GpuEditDeleted.IsValid()) ? 1 : 0);
            uint format = (uint)m_GaussianSplatRenderer.asset.posFormat | ((uint)m_GaussianSplatRenderer.asset.scaleFormat << 8) | ((uint)m_GaussianSplatRenderer.asset.shFormat << 16);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatFormat, (int)format);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatCount, m_GaussianSplatRenderer.ActiveSplatCount);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatChunks, m_GaussianSplatRenderer.GpuChunksBuffer);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatChunkCount, m_GaussianSplatRenderer.m_GpuChunksValid ? m_GaussianSplatRenderer.GpuChunksBuffer.count : 0);


            for (int i = 0; i < 6; ++i)
            {
                cmd.BeginSample(s_ProfDrawFaceMarkers[i]);
                CubemapFace face = (CubemapFace)i;
                Matrix4x4 currentLightViewMatrix = GetLightViewMatrixForFace(face);
                // Matrix4x4 currentLightViewMatrix = pointLightTransform.worldToLocalMatrix;

                cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, s_LightViewMatrixID, currentLightViewMatrix);
                cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, s_LightModelViewMatrixID, currentLightViewMatrix * m_GaussianSplatRenderer.transform.localToWorldMatrix);
                cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, s_LightProjMatrixID, lightProjectionMatrix);
                cmd.SetComputeVectorParam(m_SplatUtilitiesCS, s_LightScreenParamsID_Shadow, new Vector4(shadowCubemapResolution, shadowCubemapResolution, 0, 0));
                
                m_SplatUtilitiesCS.GetKernelThreadGroupSizes(m_CSCalcLightViewDataKernel, out uint gsX, out _, out _);
                int threadGroups = (m_GaussianSplatRenderer.ActiveSplatCount + (int)gsX - 1) / (int)gsX;
                cmd.DispatchCompute(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, threadGroups, 1, 1);

                cmd.SetRenderTarget(faceTextureHandles[i]); // URP가 제공한 i번째 2D TextureHandle
                cmd.ClearRenderTarget(true, false, Color.clear, 1.0f); // 뎁스만 1.0으로 클리어

                MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                mpb.Clear();
                m_GaussianSplatRenderer.SetAssetDataOnMaterial(mpb);
                mpb.SetVector(s_LightScreenParamsID_Shadow, new Vector4(shadowCubemapResolution, shadowCubemapResolution, 0, 0));
                mpb.SetBuffer(s_LightSplatViewDataOutputID, m_LightViewDataBuffer);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer,   m_GaussianSplatRenderer.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale,      m_GaussianSplatRenderer.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale,m_GaussianSplatRenderer.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize,       m_GaussianSplatRenderer.m_PointDisplaySize);
                
                cmd.DrawProcedural(
                    m_GaussianSplatRenderer.m_GpuIndexBuffer, 
                    m_GaussianSplatRenderer.transform.localToWorldMatrix,
                    m_ShadowCasterMaterial, 
                    0, 
                    MeshTopology.Triangles, 
                    6,
                    m_GaussianSplatRenderer.ActiveSplatCount,
                    properties: mpb);
                cmd.EndSample(s_ProfDrawFaceMarkers[i]);
            }

            if (!forceCubemapRenderForDebug)
            {
                m_shadowsDirty = false;
            }
            UpdatePreviousSettings();
        }
        
        private void RenderShadowFacesNonURP() // NonURP는 아직 개발 완료 안됨
        {
            EnsureResourcesAreCreated(); 
            EnsureGpuResourcesForCompute();
            if (m_LightViewDataBuffer == null || !m_LightViewDataBuffer.IsValid() || m_SharedLightDataBuffer == null || !m_SharedLightDataBuffer.IsValid()) return;

            var cmd = CommandBufferPool.Get($"RenderShadowFacesNonURP_{gameObject.name}");
            
            DispatchSharedDataKernel(cmd);

            Matrix4x4 lightProjectionMatrix = Matrix4x4.Perspective(90f, 1.0f, lightNearPlane, lightFarPlane);
            // (CSCalcLightViewDataKernel용 전역 파라미터 설정 - RenderShadowFacesURP와 동일하게)
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatPos, m_GaussianSplatRenderer.GpuPosData);
            cmd.SetComputeTextureParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatColor, m_GaussianSplatRenderer.GpuColorTexture);
            // cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.MatrixObjectToWorld, m_GaussianSplatRenderer.transform.localToWorldMatrix);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, s_SharedLightDataInputID, m_SharedLightDataBuffer);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, s_LightSplatViewDataOutputID, m_LightViewDataBuffer);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatBitsValid, (m_GaussianSplatRenderer.m_GpuEditSelected != null && m_GaussianSplatRenderer.m_GpuEditSelected.IsValid()) && (m_GaussianSplatRenderer.GpuEditDeleted != null && m_GaussianSplatRenderer.GpuEditDeleted.IsValid()) ? 1 : 0);
            uint format = (uint)m_GaussianSplatRenderer.asset.posFormat | ((uint)m_GaussianSplatRenderer.asset.scaleFormat << 8) | ((uint)m_GaussianSplatRenderer.asset.shFormat << 16);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatFormat, (int)format);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatCount, m_GaussianSplatRenderer.ActiveSplatCount);
            cmd.SetComputeBufferParam(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, GaussianSplatRenderer.Props.SplatChunks, m_GaussianSplatRenderer.GpuChunksBuffer);
            cmd.SetComputeIntParam(m_SplatUtilitiesCS, GaussianSplatRenderer.Props.SplatChunkCount, m_GaussianSplatRenderer.m_GpuChunksValid ? m_GaussianSplatRenderer.GpuChunksBuffer.count : 0);

            for (int i = 0; i < 6; ++i)
            {
                cmd.BeginSample(s_ProfDrawFaceMarkers[i]);
                CubemapFace face = (CubemapFace)i;
                Matrix4x4 currentLightViewMatrix = GetLightViewMatrixForFace(face);

                cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, s_LightModelViewMatrixID, currentLightViewMatrix);
                cmd.SetComputeMatrixParam(m_SplatUtilitiesCS, s_LightProjMatrixID, lightProjectionMatrix);
                cmd.SetComputeVectorParam(m_SplatUtilitiesCS, s_LightScreenParamsID_Shadow, new Vector4(shadowCubemapResolution, shadowCubemapResolution, 0, 0));
                
                m_SplatUtilitiesCS.GetKernelThreadGroupSizes(m_CSCalcLightViewDataKernel, out uint gsX, out _, out _);
                int threadGroups = (m_GaussianSplatRenderer.ActiveSplatCount + (int)gsX - 1) / (int)gsX;
                cmd.DispatchCompute(m_SplatUtilitiesCS, m_CSCalcLightViewDataKernel, threadGroups, 1, 1);
            
                cmd.SetRenderTarget(m_ShadowFaceRTs[i]);
                cmd.ClearRenderTarget(true, false, Color.clear, 1.0f);

                m_ShadowCasterMaterial.SetVector(s_LightScreenParamsID_Shadow, new Vector4(shadowCubemapResolution, shadowCubemapResolution, 0, 0));
                m_ShadowCasterMaterial.SetBuffer(s_LightSplatViewDataOutputID, m_LightViewDataBuffer);
                cmd.DrawProcedural(
                    m_GaussianSplatRenderer.m_GpuIndexBuffer, Matrix4x4.identity,
                    m_ShadowCasterMaterial, 0, MeshTopology.Triangles, 6, m_GaussianSplatRenderer.ActiveSplatCount);
                cmd.EndSample(s_ProfDrawFaceMarkers[i]);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            if (!forceCubemapRenderForDebug)
            {
                m_shadowsDirty = false;
            }
            UpdatePreviousSettings();
        }
        
        private bool IsURPActive()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        }

        public void SetShadowParametersOnMainMaterial()
        {
            if (!m_GaussianSplatRenderer || !m_GaussianSplatRenderer.m_MatSplats) return;
            Material mainSplatMaterial = m_GaussianSplatRenderer.m_MatSplats;

            mainSplatMaterial.SetVector(s_PointLightPositionID_Main, pointLightTransform.position);
            mainSplatMaterial.SetFloat(s_ShadowBiasID_Main, shadowBias);
            mainSplatMaterial.SetFloat(s_LightFarPlaneID_Main, lightFarPlane);
            mainSplatMaterial.SetFloat(s_LightNearPlaneID_Main, lightNearPlane);
        }
        
        private bool HasSettingsChanged()
        {
            if (m_GaussianSplatRenderer == null || m_GaussianSplatRenderer.asset == null) // asset null 체크 추가
            {
                // 에셋이 없는 경우, 이전 해시와 비교하는 것은 의미가 없으므로, 다른 파라미터 변경만 고려
                return m_PreviousPointLightPosition != pointLightTransform.position ||
                       m_PreviousShadowCubemapResolution != shadowCubemapResolution ||
                       !Mathf.Approximately(m_PreviousLightNearPlane, lightNearPlane) ||
                       !Mathf.Approximately(m_PreviousLightFarPlane, lightFarPlane) ||
                       !Mathf.Approximately(m_PreviousSplatScale, m_GaussianSplatRenderer.m_SplatScale) ||
                       m_PreviousAssetDataHash != default; // 이전 에셋 해시가 있었다면 변경된 것으로 간주
            }
            
            return m_PreviousPointLightPosition != pointLightTransform.position ||
                   m_PreviousShadowCubemapResolution != shadowCubemapResolution ||
                   !Mathf.Approximately(m_PreviousLightNearPlane, lightNearPlane) ||
                   !Mathf.Approximately(m_PreviousLightFarPlane, lightFarPlane) ||
                   !Mathf.Approximately(m_PreviousSplatScale, m_GaussianSplatRenderer.m_SplatScale) ||
                   m_PreviousAssetDataHash != m_GaussianSplatRenderer.asset.dataHash;
        }

        private void UpdatePreviousSettings()
        {
            m_PreviousPointLightPosition = pointLightTransform.position;
            m_PreviousShadowCubemapResolution = shadowCubemapResolution;
            m_PreviousLightNearPlane = lightNearPlane;
            m_PreviousLightFarPlane = lightFarPlane;

            if (m_GaussianSplatRenderer != null) 
            {
                m_PreviousSplatScale = m_GaussianSplatRenderer.m_SplatScale;
                if (m_GaussianSplatRenderer.asset != null)
                    m_PreviousAssetDataHash = m_GaussianSplatRenderer.asset.dataHash;
                else
                    m_PreviousAssetDataHash = default; // 에셋이 없으면 해시도 기본값
            }
            else
            {
                 m_PreviousSplatScale = default; // 또는 적절한 초기값
                 m_PreviousAssetDataHash = default;
            }
        }
        
        private void EnsureShadowCasterMaterial()
        {
            if (!shadowCasterShader) {
                Debug.LogError($"[{nameof(GaussianSplatShadowRenderer)}] ShadowCasterShader가 할당되지 않았습니다.", this);
                enabled = false; // 또는 적절한 예외 처리
                return;
            }
            if (!m_ShadowCasterMaterial || m_ShadowCasterMaterial.shader != shadowCasterShader)
            {
                // 이전 머티리얼이 있다면 파괴
                if (m_ShadowCasterMaterial) 
                {
                    if (Application.isPlaying) Destroy(m_ShadowCasterMaterial);
                    else DestroyImmediate(m_ShadowCasterMaterial);
                }
                m_ShadowCasterMaterial = new Material(shadowCasterShader) { 
                    name = $"ShadowCasterSplat_Mat_{gameObject.GetInstanceID()}",
                    hideFlags = HideFlags.HideAndDontSave // 씬에 저장되지 않도록
                };
            }
        }
        
        private void EnsureGpuResourcesForCompute()
        {
            if (!m_GaussianSplatRenderer || !m_GaussianSplatRenderer.HasValidAsset) return;
            int splatCount = m_GaussianSplatRenderer.ActiveSplatCount; // ActiveSplatCount 사용
            if (splatCount == 0) return;

            if (m_LightViewDataBuffer == null || !m_LightViewDataBuffer.IsValid() || m_LightViewDataBuffer.count != splatCount)
            {
                CleanupLightViewBuffer();
                // sizeof(float)*4 (posCS) + sizeof(float)*2 (axis1) + sizeof(float)*2 (axis2) + sizeof(half) (opacity) + padding
                // 정확한 HLSL struct LightViewData 크기 = 16 + 8 + 8 + 4(half+padding) = 36 bytes
                m_LightViewDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 36) { name = "LightViewDataBuffer_GSSR" };
            }

            if (m_SharedLightDataBuffer == null || !m_SharedLightDataBuffer.IsValid() || m_SharedLightDataBuffer.count != splatCount)
            {
                CleanupSharedLightBuffer();
                // struct SharedLightData { float3 pos; float3 cov3d0; float3 cov3d1; half opacity; };
                // float3 (12) + float3 (12) + float3 (12) + half (2) + padding (2) = 40 바이트
                m_SharedLightDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 52) { name = "SharedLightDataBuffer_GSSR" };
            }
        }
        
        private void EnsureResourcesAreCreated() // 주로 비URP 경로에서 m_ShadowFaceRTs 생성/관리
        {
            EnsureShadowCasterMaterial();
            
            var faceDesc = GetShadowFaceDescriptor(); // 2D 뎁스 텍스처용 디스크립터
            bool recreateAllRTs = false;

            if (m_ShadowFaceRTs == null || m_ShadowFaceRTs.Length != 6) {
                m_ShadowFaceRTs = new RenderTexture[6];
                recreateAllRTs = true;
            }

            for (int i = 0; i < 6; i++)
            {
                if (recreateAllRTs || m_ShadowFaceRTs[i] == null || 
                    m_ShadowFaceRTs[i].width != shadowCubemapResolution || 
                    m_ShadowFaceRTs[i].height != shadowCubemapResolution || 
                    !m_ShadowFaceRTs[i].IsCreated() ||
                    m_ShadowFaceRTs[i].graphicsFormat != faceDesc.graphicsFormat || 
                    m_ShadowFaceRTs[i].depthStencilFormat != faceDesc.depthStencilFormat)
                {
                    if (m_ShadowFaceRTs[i])
                    {
                        m_ShadowFaceRTs[i].Release();
                        if (Application.isPlaying) Destroy(m_ShadowFaceRTs[i]);
                        else DestroyImmediate(m_ShadowFaceRTs[i]);
                    }
                    m_ShadowFaceRTs[i] = new RenderTexture(faceDesc) { 
                        name = $"ShadowFaceRT_{i}_{gameObject.GetInstanceID()}",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if(!m_ShadowFaceRTs[i].Create()) {
                        Debug.LogError($"Failed to create ShadowFaceRT_{i}");
                    }
                }
            }
        }

        private Matrix4x4 GetLightViewMatrixForFace(CubemapFace face)
        {
            // 1. LookAt 파라미터 설정
            // Target: 뷰의 방향
            // Up: 뷰의 상단 방향
            Vector3 targetDirection;
            Vector3 upDirection;

            switch (face)
            {
                // Target: +X (Right), Up: -Y
                case CubemapFace.PositiveX: 
                    targetDirection = Vector3.right;
                    upDirection = Vector3.down;
                    break;
                // Target: -X (Left), Up: -Y
                case CubemapFace.NegativeX:
                    targetDirection = Vector3.left;
                    upDirection = Vector3.down;
                    break;
                // Target: +Y (Up), Up: +Z
                case CubemapFace.PositiveY:
                    targetDirection = Vector3.up;
                    upDirection = Vector3.forward;
                    break;
                // Target: -Y (Down), Up: -Z
                case CubemapFace.NegativeY:
                    targetDirection = Vector3.down;
                    upDirection = Vector3.back;
                    break;
                // Target: +Z (Forward), Up: -Y
                case CubemapFace.PositiveZ:
                    targetDirection = Vector3.forward;
                    upDirection = Vector3.down;
                    break;
                // Target: -Z (Back), Up: -Y
                case CubemapFace.NegativeZ:
                    targetDirection = Vector3.back;
                    upDirection = Vector3.down;
                    break;
                default:
                    return Matrix4x4.identity;
            }

            // 2. Unity의 LookAt 함수를 사용하여 View 행렬 생성 (+Z가 전방인 뷰 공간)
            Matrix4x4 unityViewMatrix = Matrix4x4.LookAt(
                pointLightTransform.position, 
                pointLightTransform.position + targetDirection, 
                upDirection
            );
            
            return unityViewMatrix;
        }

        private void CleanupResources()
        {
            CleanupFaceRTs();
            CleanupLightViewBuffer();
            CleanupSharedLightBuffer();

            if (m_ShadowCasterMaterial != null)
            {
                if (Application.isPlaying) Destroy(m_ShadowCasterMaterial);
                else DestroyImmediate(m_ShadowCasterMaterial);
                m_ShadowCasterMaterial = null;
            }
        }

        private void CleanupFaceRTs()
        {
            if (m_ShadowFaceRTs != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (m_ShadowFaceRTs[i] != null)
                    {
                        if (m_ShadowFaceRTs[i].IsCreated()) m_ShadowFaceRTs[i].Release();
                        if (Application.isPlaying) Destroy(m_ShadowFaceRTs[i]);
                        else DestroyImmediate(m_ShadowFaceRTs[i]);
                        m_ShadowFaceRTs[i] = null;
                    }
                }
            }
        }
        
        private void CleanupLightViewBuffer()
        {
            m_LightViewDataBuffer?.Dispose();
            m_LightViewDataBuffer = null;
        }
        
        private void CleanupSharedLightBuffer()
        {
            m_SharedLightDataBuffer?.Dispose();
            m_SharedLightDataBuffer = null;
        }
    }
}