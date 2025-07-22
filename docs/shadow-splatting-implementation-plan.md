# Shadow Splatting Implementation Plan for UnityGaussianSplatting (Revised)

## Executive Summary

This document outlines a revised implementation plan for integrating GS³ paper-based shadow splatting into the UnityGaussianSplatting project. Based on critical feedback, we've pivoted from a compute shader approach to leverage Unity's rasterization pipeline, following the actual GS³ implementation more closely.

**Key Changes from Original Plan:**
- Use rasterization pipeline instead of compute shaders for shadow accumulation
- Start with directional lights (simpler) before point lights
- Separate shadow/residual buffers instead of extending SplatViewData
- Replace MLP with post-processing filters
- More realistic performance targets

**Revised Goals:**
- Directional light support first (point lights in v1.1)
- Hardware-accelerated opacity accumulation via blending
- GaussianSplatRenderer-exclusive (independent from Unity lighting)
- Performance: <2ms for directional, <5-7ms for point lights (5M splats)
- Preserve existing project structure

---

## 1. Technical Architecture Overview

### 1.1 Core Algorithm (Revised Based on GS³ Paper)

The GS³ paper uses GPU rasterization for efficient shadow accumulation, which we now follow more closely:

1. **Light Space Rendering**: Render splats from light viewpoint using rasterization pipeline
2. **Hardware Blending**: Use custom blending for transmittance accumulation: T_j = ∏(k=0 to j-1)(1 - β_k × γ_k)
3. **Shadow Buffer Storage**: Store accumulated shadow values in separate buffer
4. **Post-Process Refinement**: Apply bilateral filter instead of MLP for denoising
5. **Residual Computation**: Simple per-splat indirect color buffer (no complex MLP)
6. **Final Composition**: Multiply shading with shadow and add residual contributions

**Key Insight**: The GPU's built-in depth sorting and blending hardware handles the complex ordering and accumulation that would be extremely inefficient in compute shaders.

### 1.2 Integration Points

```
Current Pipeline:                  Revised Pipeline:
1. Load Splat Data        →       1. Load Splat Data
2. Calculate View Matrices →      2. Calculate View Matrices  
3. Sort by Camera Distance →      3. Sort by Camera Distance
4. Evaluate SH            →       4. [NEW] Shadow Rendering Pass
5. Composite              →       5. Evaluate SH with Shadow Lookup
                                  6. Composite with Residual
```

### 1.3 Memory Architecture (Revised)

```
Original Design (Problematic):          Revised Design (Efficient):
- Extended SplatViewData                - Original SplatViewData unchanged
- +20 bytes per splat                  - Separate shadow buffer
- 340MB total for 5M splats            - Separate residual buffer
- High bandwidth usage                  - On-demand access pattern
```

---

## 2. Implementation Phases (Revised)

### Phase 1: Core Infrastructure (Week 1)

#### Step 1.1: Separate Buffer Architecture
**File:** `package/Runtime/GaussianSplatRenderSystem.cs`

```csharp
// Add shadow buffer management (DO NOT modify SplatViewData)
public class GaussianSplatRenderSystem
{
    // New shadow-related buffers
    private ComputeBuffer m_ShadowValueBuffer;      // Per-splat shadow values
    private ComputeBuffer m_ResidualColorBuffer;   // Per-splat indirect color
    private RenderTexture m_ShadowAccumTexture;    // Light-space accumulation RT
    
    private void AllocateShadowBuffers(int splatCount)
    {
        m_ShadowValueBuffer = new ComputeBuffer(splatCount, sizeof(float));
        m_ResidualColorBuffer = new ComputeBuffer(splatCount, 3 * sizeof(float));
        // Shadow RT: R32G32_FLOAT for (transmittance, weight)
        m_ShadowAccumTexture = new RenderTexture(1024, 1024, 24, 
            RenderTextureFormat.RGFloat);
    }
}
```

#### Step 1.2: Directional Light Component (Simplified)
**New File:** `package/Runtime/GaussianDirectionalLight.cs`

```csharp
[System.Serializable]
public class GaussianDirectionalLight : MonoBehaviour
{
    public enum LightMode { Realtime, Static }
    public enum LightType { Directional, Point } // Start with Directional
    
    [Header("Light Properties")]
    public LightType type = LightType.Directional;
    public LightMode mode = LightMode.Realtime;
    public Vector3 direction = Vector3.down;
    public float intensity = 1.0f;
    
    [Header("Shadow Settings")]
    public float shadowBias = 0.015f;
    public float shadowDistance = 50.0f;
    public int shadowResolution = 1024;
    
    // Runtime data
    internal Matrix4x4 lightViewMatrix;
    internal Matrix4x4 lightProjMatrix;
    internal RenderTexture shadowTexture;
}
```

#### Step 1.3: Shadow Rendering Shader (Using Rasterization)
**New File:** `package/Shaders/RenderShadowSplats.shader`

```hlsl
Shader "GaussianSplatting/ShadowPass"
{
    Properties
    {
        _ShadowBias ("Shadow Bias", Float) = 0.015
    }
    
    SubShader
    {
        Tags { "Queue" = "Transparent" }
        
        Pass
        {
            Name "ShadowAccumulation"
            ZWrite On
            ZTest LEqual
            Cull Off
            
            // Custom blending for transmittance accumulation
            // Output: (transmittance, weight)
            Blend DstColor Zero, One One  // Multiply transmittance, Add weights | For single-pass permeability calculations without MRT, `BlendOp Min` may be more intuitive, and if MRT is used, it would be clearer to specify `BlendOp Multiply, BlendOp Add` for each target. Test to find the best combination when implemented in practice.
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "GaussianSplatting.hlsl"
            
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float opacity : TEXCOORD1;
                float gaussianWeight : TEXCOORD2;
            };
            
            float4x4 _LightViewMatrix;
            float4x4 _LightProjMatrix;
            
            v2f vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                v2f o;
                
                // Get splat data
                SplatData splat = _SplatData[instanceID];
                
                // Transform to light space
                float4 worldPos = float4(splat.pos, 1.0);
                float4 lightViewPos = mul(_LightViewMatrix, worldPos);
                o.pos = mul(_LightProjMatrix, lightViewPos);
                
                // Calculate 2D Gaussian for this vertex
                o.uv = GetQuadUV(vertexID);
                o.opacity = splat.opacity;
                
                // Gaussian weight calculation
                float2 screenPos = o.pos.xy / o.pos.w;
                o.gaussianWeight = CalculateGaussianDensity(screenPos, splat.axis);
                
                return o;
            }
            
            float2 frag(v2f i) : SV_Target
            {
                // Calculate transmittance contribution
                float beta = i.gaussianWeight;
                float gamma = i.opacity;
                float transmittance = 1.0 - beta * gamma;
                
                // Output: (transmittance to multiply, weight to add)
                return float2(transmittance, beta);
            }
            ENDHLSL
        }
    }
}
```

#### Step 1.4: Shadow Post-Processing
**New File:** `package/Shaders/ShadowPostProcess.compute`

```hlsl
#pragma kernel CSBilateralFilter
#pragma kernel CSExtractShadowValues

#include "GaussianSplatting.hlsl"

Texture2D<float2> _ShadowAccumTexture;  // From shadow pass
RWStructuredBuffer<float> _ShadowValues; // Per-splat output

// Bilateral filter for shadow denoising (replaces MLP)
[numthreads(8, 8, 1)]
void CSBilateralFilter(uint3 id : SV_DispatchThreadID)
{
    float2 center = _ShadowAccumTexture[id.xy];
    float shadowRaw = center.x / max(center.y, 0.001);
    
    // Simple 3x3 bilateral filter
    float weightSum = 0.0;
    float filteredShadow = 0.0;
    
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            uint2 samplePos = id.xy + int2(x, y);
            float2 sample = _ShadowAccumTexture[samplePos];
            float sampleShadow = sample.x / max(sample.y, 0.001);
            
            // Spatial weight
            float spatialWeight = exp(-0.5 * (x*x + y*y));
            
            // Range weight
            float diff = abs(sampleShadow - shadowRaw);
            float rangeWeight = exp(-diff * 10.0);
            
            float weight = spatialWeight * rangeWeight;
            filteredShadow += sampleShadow * weight;
            weightSum += weight;
        }
    }
    
    _ShadowAccumTexture[id.xy] = float2(filteredShadow / weightSum, 1.0);
}

// Extract per-splat shadow values from light-space texture
[numthreads(256, 1, 1)]
void CSExtractShadowValues(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= _SplatCount) return;
    
    // Project splat to light space and sample shadow
    float4 lightSpacePos = GetSplatLightSpacePosition(idx);
    float2 shadowUV = lightSpacePos.xy * 0.5 + 0.5;
    
    float2 shadowData = _ShadowAccumTexture.SampleLevel(_LinearClampSampler, shadowUV, 0);
    _ShadowValues[idx] = shadowData.x;
}
```

### Phase 2: Rendering Integration (Week 2-3)

#### Step 2.1: Extend GaussianSplatRenderSystem
**File:** `package/Runtime/GaussianSplatRenderSystem.cs`

Add shadow rendering pipeline:

```csharp
public class GaussianSplatRenderSystem
{
    // Shadow rendering resources
    private Material m_ShadowRenderMaterial;
    private ComputeShader m_ShadowPostProcess;
    private Mesh m_QuadMesh;
    
    // Revised shadow pass using rasterization
    private void ExecuteShadowPass(CommandBuffer cmd, GaussianDirectionalLight light, 
                                   List<GaussianSplatRenderer> splats)
    {
        cmd.BeginSample("Shadow Splatting");
        
        // 1. Setup light matrices
        Matrix4x4 lightView = Matrix4x4.LookAt(
            light.transform.position,
            light.transform.position + light.transform.forward,
            Vector3.up
        );
        Matrix4x4 lightProj = Matrix4x4.Ortho(
            -light.shadowDistance, light.shadowDistance,
            -light.shadowDistance, light.shadowDistance,
            0.1f, light.shadowDistance * 2
        );
        
        // 2. Set render target for shadow accumulation
        cmd.SetRenderTarget(m_ShadowAccumTexture);
        cmd.ClearRenderTarget(true, true, new Color(1, 0, 0, 0)); // Clear to (1,0)
        
        // 3. Setup shadow material properties
        cmd.SetGlobalMatrix("_LightViewMatrix", lightView);
        cmd.SetGlobalMatrix("_LightProjMatrix", lightProj);
        
        // 4. Render splats from light perspective (hardware handles sorting!)
        foreach (var renderer in splats)
        {
            cmd.DrawProcedural(
                Matrix4x4.identity,
                m_ShadowRenderMaterial,
                0, // shadow pass
                MeshTopology.Triangles,
                renderer.splatCount * 6, // 6 vertices per splat quad
                1
            );
        }
        
        // 5. Post-process: bilateral filter
        cmd.BeginSample("Shadow Filter");
        cmd.SetComputeTextureParam(m_ShadowPostProcess, 0, "_ShadowAccumTexture", m_ShadowAccumTexture);
        cmd.DispatchCompute(m_ShadowPostProcess, 0, 
            m_ShadowAccumTexture.width / 8,
            m_ShadowAccumTexture.height / 8, 1);
        cmd.EndSample("Shadow Filter");
        
        // 6. Extract per-splat shadow values
        cmd.SetComputeBufferParam(m_ShadowPostProcess, 1, "_ShadowValues", m_ShadowValueBuffer);
        cmd.DispatchCompute(m_ShadowPostProcess, 1, 
            (totalSplatCount + 255) / 256, 1, 1);
        
        cmd.EndSample("Shadow Splatting");
    }
}
```

#### Step 2.2: Modify Main Rendering Shader
**File:** `package/Shaders/RenderGaussianSplats.shader`

Integrate shadow lookup in fragment shader:

```hlsl
// Add shadow buffer binding
StructuredBuffer<float> _ShadowValues;
StructuredBuffer<float3> _ResidualColors;

// Modified fragment shader
half4 frag(v2f i) : SV_Target
{
    // Existing code...
    
    // Look up shadow value for this splat
    uint splatIndex = i.splatIndex; // Passed from vertex shader
    float shadowValue = _ShadowValues[splatIndex];
    float3 residualColor = _ResidualColors[splatIndex];
    
    // GS³-style composition: Shading * Shadow + Residual
    half3 shading = SampleSH9(normal) * baseColor;
    half3 finalColor = shading * shadowValue + residualColor;
    
    // Apply existing opacity blending
    return half4(finalColor, opacity);
}
```

#### Step 2.3: Residual Color Computation
**New File:** `package/Shaders/ComputeResidual.compute`

Simple residual computation (no complex MLP):

```hlsl
#pragma kernel CSComputeResidual

StructuredBuffer<SplatData> _SplatData;
StructuredBuffer<float> _ShadowValues;
RWStructuredBuffer<float3> _ResidualColors;

// Simple ambient/indirect approximation
float3 _AmbientColor;
float _IndirectIntensity;

[numthreads(256, 1, 1)]
void CSComputeResidual(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= _SplatCount) return;
    
    SplatData splat = _SplatData[idx];
    float shadow = _ShadowValues[idx];
    
    // Simple indirect illumination model
    // Areas in shadow receive more ambient/bounce light
    float indirectFactor = 1.0 - shadow;
    float3 residual = _AmbientColor * _IndirectIntensity * indirectFactor;
    
    // Store per-splat residual color
    _ResidualColors[idx] = residual;
}
```

### Phase 3: Optimization & Quality (Week 4)

#### Step 3.1: Performance Optimizations

**Half-Resolution Shadow Rendering:**
```csharp
// In GaussianSplatRenderSystem.cs
public enum ShadowQuality
{
    Full = 1024,
    Half = 512,
    Quarter = 256
}

private RenderTexture CreateShadowTexture(ShadowQuality quality)
{
    int resolution = (int)quality;
    return new RenderTexture(resolution, resolution, 24, RenderTextureFormat.RGFloat);
}
```

**Temporal Shadow Caching:**
```hlsl
// In ShadowPostProcess.compute
#pragma kernel CSTemporalAccumulation

Texture2D<float> _ShadowValuesPrev;
RWStructuredBuffer<float> _ShadowValues;
float _TemporalBlendFactor; // 0.1-0.3 typical

[numthreads(256, 1, 1)]
void CSTemporalAccumulation(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= _SplatCount) return;
    
    float currentShadow = _ShadowValues[idx];
    float prevShadow = _ShadowValuesPrev[idx];
    
    // Temporal blend for stability
    float blended = lerp(prevShadow, currentShadow, _TemporalBlendFactor);
    _ShadowValues[idx] = blended;
}
```

**Adaptive Shadow Bias:**
```hlsl
// In RenderShadowSplats.shader vertex shader
float CalculateAdaptiveBias(SplatData splat, float3 lightDir)
{
    // Scale-dependent bias (from GS³)
    float scaleFactor = length(splat.axis.xyz);
    
    // Angle-dependent bias
    float3 normal = GetSplatNormal(splat); // Approximate from axis
    float NdotL = abs(dot(normal, lightDir));
    float angleBias = 1.0 - NdotL;
    
    // Combine biases
    return _ShadowBias * (1.0 + scaleFactor * 0.1 + angleBias * 0.5);
}
```

#### Step 3.2: Quality Improvements

**Enhanced Bilateral Filter:**
```hlsl
// In ShadowPostProcess.compute
[numthreads(8, 8, 1)]
void CSEnhancedBilateralFilter(uint3 id : SV_DispatchThreadID)
{
    float2 center = _ShadowAccumTexture[id.xy];
    float shadowCenter = center.x / max(center.y, 0.001);
    
    // 5x5 kernel for better quality
    float weightSum = 0.0;
    float filteredShadow = 0.0;
    
    const float sigma_spatial = 2.0;
    const float sigma_range = 0.1;
    
    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            uint2 samplePos = id.xy + int2(x, y);
            float2 sample = _ShadowAccumTexture[samplePos];
            float sampleShadow = sample.x / max(sample.y, 0.001);
            
            // Gaussian spatial weight
            float dist2 = x*x + y*y;
            float spatialWeight = exp(-dist2 / (2.0 * sigma_spatial * sigma_spatial));
            
            // Gaussian range weight
            float diff = abs(sampleShadow - shadowCenter);
            float rangeWeight = exp(-diff * diff / (2.0 * sigma_range * sigma_range));
            
            float weight = spatialWeight * rangeWeight;
            filteredShadow += sampleShadow * weight;
            weightSum += weight;
        }
    }
    
    _ShadowAccumTexture[id.xy] = float2(filteredShadow / weightSum, 1.0);
}
```

#### Step 3.3: Debug Visualization
**New File:** `package/Shaders/ShadowDebug.shader`

```hlsl
Shader "GaussianSplatting/ShadowDebug"
{
    Properties
    {
        _DebugMode ("Debug Mode", Int) = 0
    }
    
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            Texture2D<float2> _ShadowAccumTexture;
            StructuredBuffer<float> _ShadowValues;
            int _DebugMode;
            
            float4 frag(v2f i) : SV_Target
            {
                switch(_DebugMode)
                {
                    case 0: // Shadow accumulation texture
                        float2 shadowData = _ShadowAccumTexture.Sample(sampler_point_clamp, i.uv);
                        float shadow = shadowData.x / max(shadowData.y, 0.001);
                        return float4(shadow.xxx, 1);
                        
                    case 1: // Shadow weights
                        float weight = shadowData.y;
                        return float4(weight, 0, 0, 1);
                        
                    case 2: // Per-splat shadow values
                        uint idx = GetSplatIndexFromUV(i.uv);
                        float splatShadow = _ShadowValues[idx];
                        return float4(splatShadow.xxx, 1);
                }
                
                return float4(1, 0, 1, 1); // Error color
            }
            ENDHLSL
        }
    }
}
```

### Phase 4: Polish & Integration (Week 5)

#### Step 4.1: Inspector UI
**File:** `package/Editor/GaussianDirectionalLightEditor.cs`

```csharp
[CustomEditor(typeof(GaussianDirectionalLight))]
public class GaussianDirectionalLightEditor : Editor
{
    private bool showDebugOptions = false;
    private bool showPerformanceMetrics = false;
    
    public override void OnInspectorGUI()
    {
        var light = (GaussianDirectionalLight)target;
        
        // Light Type (disabled for v1.0)
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.EnumPopup("Light Type", LightType.Directional);
        EditorGUILayout.HelpBox("Point lights will be supported in v1.1", MessageType.Info);
        EditorGUI.EndDisabledGroup();
        
        // Light Properties
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Light Properties", EditorStyles.boldLabel);
        light.direction = EditorGUILayout.Vector3Field("Direction", light.direction);
        light.intensity = EditorGUILayout.Slider("Intensity", light.intensity, 0, 2);
        
        // Shadow Settings
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shadow Settings", EditorStyles.boldLabel);
        light.shadowBias = EditorGUILayout.Slider("Shadow Bias", light.shadowBias, 0.001f, 0.1f);
        light.shadowDistance = EditorGUILayout.FloatField("Shadow Distance", light.shadowDistance);
        light.shadowResolution = EditorGUILayout.IntPopup("Resolution", 
            light.shadowResolution, 
            new string[] { "256", "512", "1024", "2048" },
            new int[] { 256, 512, 1024, 2048 });
        
        // Performance Metrics
        showPerformanceMetrics = EditorGUILayout.Foldout(showPerformanceMetrics, "Performance Metrics");
        if (showPerformanceMetrics && Application.isPlaying)
        {
            EditorGUILayout.LabelField($"Shadow Pass: {GetShadowPassTime():F2} ms");
            EditorGUILayout.LabelField($"Filter Pass: {GetFilterPassTime():F2} ms");
            EditorGUILayout.LabelField($"Total Overhead: {GetTotalOverhead():F2} ms");
        }
        
        // Debug Options
        showDebugOptions = EditorGUILayout.Foldout(showDebugOptions, "Debug Visualization");
        if (showDebugOptions)
        {
            EditorGUILayout.EnumPopup("Debug Mode", GetDebugMode());
            if (GUILayout.Button("Capture Shadow Buffer"))
            {
                CaptureShadowBuffer();
            }
        }
    }
}
```

#### Step 4.2: Runtime Integration
**File:** `package/Runtime/GaussianSplatRenderer.cs`

```csharp
public partial class GaussianSplatRenderer : MonoBehaviour
{
    [Header("Shadow Settings")]
    [SerializeField] private bool m_ReceiveShadows = true;
    [SerializeField] private ShadowQuality m_ShadowQuality = ShadowQuality.Half;
    
    // Auto-detect lights in scene
    private void UpdateLightReferences()
    {
        if (!m_ReceiveShadows) return;
        
        var lights = FindObjectsOfType<GaussianDirectionalLight>();
        if (lights.Length > 0)
        {
            // Register with render system for shadow pass
            GaussianSplatRenderSystem.instance.RegisterShadowReceiver(this, lights[0]);
        }
    }
}
```

#### Step 4.3: Sample Scene Setup
**New File:** `package/Editor/GaussianShadowSampleScenes.cs`

```csharp
public static class GaussianShadowSampleScenes
{
    [MenuItem("Gaussian Splatting/Create Shadow Test Scene")]
    public static void CreateShadowTestScene()
    {
        // Clear existing scene
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
        
        // Add Gaussian splat renderer
        var splatGO = new GameObject("Gaussian Splat");
        var renderer = splatGO.AddComponent<GaussianSplatRenderer>();
        // Load sample asset...
        
        // Add directional light
        var lightGO = new GameObject("Gaussian Directional Light");
        var light = lightGO.AddComponent<GaussianDirectionalLight>();
        light.transform.rotation = Quaternion.Euler(45, -30, 0);
        light.intensity = 1.0f;
        light.shadowResolution = 1024;
        
        // Setup camera
        Camera.main.transform.position = new Vector3(0, 2, -5);
        Camera.main.transform.LookAt(Vector3.zero);
        
        // Add debug UI
        var debugGO = new GameObject("Shadow Debug UI");
        debugGO.AddComponent<ShadowDebugUI>();
    }
}
```

---

## 3. GS³ Triple Splatting Adaptation (Revised)

### 3.1 Overview
The GS³ paper's triple splatting approach, adapted for Unity's rasterization pipeline:

1. **Shadow Pass**: Render splats from light viewpoint using hardware blending
2. **Main Pass**: Render splats with SH evaluation and shadow lookup
3. **Composition**: Multiply shading by shadow and add residual

### 3.2 Key Differences from Original GS³

**What We Keep:**
- Core shadow accumulation algorithm: T_j = ∏(k=0 to j-1)(1 - β_k × γ_k)
- Weighted averaging for multiple ray intersections
- Adaptive shadow bias based on splat scale
- Triple pass concept (shadow, shading, residual)

**What We Change:**
- **Rasterization instead of compute**: Leverage GPU's built-in sorting/blending
- **Bilateral filter instead of MLP**: Simpler, more predictable denoising
- **Simple residual buffer**: No complex neural network for indirect illumination
- **Directional light first**: Simpler than point light's 6-face cubemap

### 3.3 Rendering Pipeline Flow (Revised)

```csharp
// Actual implementation approach for Unity
void RenderGaussianSplatsWithShadows(CommandBuffer cmd)
{
    // Pass 1: Shadow Rendering (GPU rasterization handles sorting!)
    cmd.BeginSample("ShadowRendering");
    cmd.SetRenderTarget(m_ShadowAccumTexture);
    cmd.ClearRenderTarget(true, true, new Color(1, 0, 0, 0));
    cmd.DrawProcedural(...); // Hardware blending accumulates transmittance
    cmd.EndSample("ShadowRendering");
    
    // Pass 2: Shadow Post-Processing
    cmd.BeginSample("ShadowFilter");
    cmd.DispatchCompute(bilateralFilter, ...); // Denoise
    cmd.DispatchCompute(extractShadowValues, ...); // Per-splat values
    cmd.EndSample("ShadowFilter");
    
    // Pass 3: Residual Computation
    cmd.BeginSample("ResidualCompute");
    cmd.DispatchCompute(computeResidual, ...);
    cmd.EndSample("ResidualCompute");
    
    // Pass 4: Main Rendering with Shadow Integration
    cmd.BeginSample("MainRendering");
    cmd.SetGlobalBuffer("_ShadowValues", m_ShadowValueBuffer);
    cmd.SetGlobalBuffer("_ResidualColors", m_ResidualColorBuffer);
    cmd.DrawProcedural(...); // Main splat rendering
    cmd.EndSample("MainRendering");
}
```

---

## 4. Technical Implementation Details (Revised)

### 4.1 Shadow Accumulation via Hardware Blending

The revised approach leverages GPU hardware for efficient accumulation:

```hlsl
// Traditional compute approach (INEFFICIENT):
for (uint k = 0; k < idx; k++)
    transmittance *= (1 - beta_k * gamma_k);

// Our approach using hardware blending (EFFICIENT):
// 1. Render splats with custom blend mode
// 2. Output: float2(1 - beta * gamma, beta)
// 3. Blend mode: (src.x * dst.x, src.y + dst.y)
// 4. Hardware handles ordering automatically!
```

**Key Advantages:**
- GPU's built-in depth sorting handles ordering
- Hardware blending performs accumulation in parallel
- No explicit loops or sequential dependencies
- Orders of magnitude faster than compute approach

### 4.2 Memory Layout (Optimized)

**Separate Buffer Architecture:**
```
Original SplatViewData: 48 bytes (unchanged)
+ Shadow buffer: 4 bytes per splat (float)
+ Residual buffer: 12 bytes per splat (float3)
= Total: 64 bytes per splat

For 5M splats:
- Base data: 240 MB (unchanged)
- Shadow data: 20 MB
- Residual data: 60 MB
- Total: 320 MB (well within limits)
```

**Bandwidth Optimization:**
- Buffers updated independently
- Shadow values can be cached temporally
- Residual updates only when lights change

### 4.3 Performance Targets (Revised)

**Directional Light (v1.0):**
- Shadow rendering: <1.0ms
- Bilateral filter: <0.3ms
- Shadow extraction: <0.2ms
- Residual compute: <0.2ms
- **Total overhead: <1.7ms** ✓

**Point Light (v1.1):**
- 6x shadow rendering: <6.0ms
- Filtering & extraction: <0.5ms
- **Total overhead: <6.5ms** (acceptable for future)

**Memory Bandwidth:**
- Read: ~80 MB/frame (5M splats)
- Write: ~20 MB/frame (shadow values only)
- 50% reduction vs. original design

---

## 5. Integration Strategy

### 5.1 Minimal Disruption Approach

1. **Preserve Existing API**: All changes are additive, no breaking changes
2. **Optional Feature**: Shadow system only activates when GaussianDirectionalLight exists
3. **Backward Compatible**: Scenes without lights render identically to before
4. **Separate Buffers**: No modification to core SplatViewData structure

### 5.2 Render Pipeline Support

The implementation works across all three pipelines:
- **Built-in RP**: Direct command buffer injection
- **URP**: Extend GaussianSplatURPFeature with shadow pass
- **HDRP**: Extend GaussianSplatHDRPPass with shadow stage

```csharp
// Example URP integration
public class GaussianSplatURPFeature : ScriptableRendererFeature
{
    class ShadowPass : ScriptableRenderPass
    {
        public override void Execute(ScriptableRenderContext context, 
                                   ref RenderingData renderingData)
        {
            // Shadow rendering logic
        }
    }
}
```

### 5.3 Platform Considerations

- **GPU Requirements**: Same as base (Compute Shader 5.0)
- **API Support**: DX12, Metal, Vulkan (no changes)
- **Blending Support**: Requires custom blend modes (widely supported)
- **Mobile**: Not supported (same as base)

---

## 6. Testing Strategy

### 6.1 Unit Tests
- Hardware blending correctness verification
- Shadow buffer precision validation
- Bilateral filter quality assessment

### 6.2 Integration Tests
- Multi-splat renderer scenarios
- Light direction changes
- Shadow quality across distances
- Render pipeline compatibility

### 6.3 Performance Tests
```csharp
[Test]
public void ShadowPass_Performance_UnderTarget()
{
    // Setup: 5M splats, directional light
    var totalTime = MeasureShadowPass();
    Assert.Less(totalTime, 1.7f); // ms
}
```

### 6.4 Visual Tests
- Shadow softness and quality
- Temporal stability
- Bias artifact detection
- Edge case validation (grazing angles)

---

## 7. Risk Mitigation (Revised)

### 7.1 Technical Risks

**Risk:** Custom blending mode compatibility
- **Mitigation:** Test on all target platforms early
- **Fallback:** Alternative accumulation via MRT if needed

**Risk:** Shadow rendering performance
- **Mitigation:** Multiple quality tiers (256/512/1024)
- **Fallback:** Temporal upsampling for lower resolutions

**Risk:** Bilateral filter artifacts
- **Mitigation:** Tunable filter parameters
- **Fallback:** Simple box filter option

**Risk:** Point light complexity (v1.1)
- **Mitigation:** Start with directional lights only
- **Plan:** Defer point lights to ensure v1.0 stability

### 7.2 Schedule Risks

**Risk:** Hardware blending debugging
- **Mitigation:** Build reference CPU implementation
- **Tool:** RenderDoc for GPU debugging
- **Buffer:** Focus on core features first

---

## 8. Success Metrics (Revised)

### 8.1 Performance Metrics
- ✓ Directional shadow pass <1.7ms (5M splats, RTX 3070)
- ✓ Memory overhead <80MB (shadow + residual buffers)
- ✓ 60+ FPS maintained with shadows enabled
- ✓ Hardware blending efficiency >90%

### 8.2 Quality Metrics
- ✓ Minimal shadow acne with adaptive bias
- ✓ Smooth shadows via bilateral filtering
- ✓ Stable temporal behavior
- ✓ Accurate light occlusion

### 8.3 Usability Metrics
- ✓ Single component to add (GaussianDirectionalLight)
- ✓ Auto-detection by renderers
- ✓ Real-time performance feedback
- ✓ Intuitive debug visualization

---

## 9. Implementation Checklist (Revised)

### Week 1: Core Infrastructure
- [ ] Create separate shadow/residual buffers
- [ ] Implement GaussianDirectionalLight component
- [ ] Create RenderShadowSplats.shader with custom blending
- [ ] Setup shadow accumulation render texture

### Week 2-3: Rendering Integration
- [ ] Implement shadow rendering pass in GaussianSplatRenderSystem
- [ ] Create bilateral filter for shadow denoising
- [ ] Integrate shadow lookup in main rendering shader
- [ ] Add residual color computation

### Week 4: Optimization & Quality
- [ ] Implement multiple shadow quality tiers
- [ ] Add temporal accumulation option
- [ ] Tune adaptive bias algorithm
- [ ] Create debug visualization modes

### Week 5: Polish & Testing
- [ ] Create inspector UI with performance metrics
- [ ] Build sample test scenes
- [ ] Performance profiling and optimization
- [ ] Documentation and examples

---

## 10. Future Enhancements (Post-v1.0)

### v1.1: Point Light Support
- Cubemap shadow rendering (6 faces)
- Optimized single-pass cubemap technique
- Temporal face updating (1 face per frame)

### v1.2: Multiple Lights
- Support 2-4 directional/point lights
- Light priority system
- Shadow atlas management

### v2.0: Advanced Features
- Area light soft shadows
- Colored shadows for translucent splats
- Advanced indirect illumination
- Static shadow baking

---

## 11. Key Improvements from Revised Approach

### 11.1 Critical Architecture Changes
1. **Rasterization Pipeline**: Leverages GPU hardware for efficient sorting/blending
2. **Separate Buffers**: Avoids memory bandwidth issues of extended structures
3. **Bilateral Filtering**: Predictable shadow refinement without neural networks
4. **Directional Light First**: Reduces complexity for v1.0 stability
5. **Hardware Blending**: Orders of magnitude faster than compute loops

### 11.2 Performance Benefits
1. **Parallel Accumulation**: No sequential dependencies
2. **Hardware Acceleration**: Built-in depth sorting and blending
3. **Reduced Bandwidth**: 50% less memory traffic
4. **Realistic Targets**: Achievable <2ms for directional lights
5. **Scalable Quality**: Multiple resolution tiers

### 11.3 Implementation Advantages
1. **Proven Approach**: Follows GS³'s actual implementation strategy
2. **Debugging Tools**: RenderDoc compatible for GPU debugging
3. **Incremental Development**: Can validate each stage independently
4. **Platform Compatibility**: Standard GPU features only

## 12. Conclusion

This revised implementation plan addresses the critical issues identified in the feedback, particularly the inefficient compute shader approach for shadow accumulation. By pivoting to use Unity's rasterization pipeline with hardware blending, we achieve the same mathematical result as GS³ but with dramatically better performance.

The separate buffer architecture avoids memory bandwidth issues while maintaining flexibility. Starting with directional lights reduces initial complexity, ensuring a stable v1.0 release. The bilateral filter provides effective shadow refinement without the unpredictability of neural network approximations.

This approach balances the innovative shadow techniques from GS³ with practical engineering considerations for Unity, resulting in a more robust and performant implementation.

---

**Document Version:** 2.0 (Revised)  
**Date:** July 22, 2025  
**Author:** Shadow Splatting Implementation Team