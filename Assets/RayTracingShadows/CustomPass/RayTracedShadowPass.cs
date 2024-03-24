using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using System.Collections.Generic;

public class RayTracedShadowPass : ScriptableRendererFeature
{

    public Light light;
    public Material material;
    public ComputeShader computeShader;

    private Light dirLight;
    [Range(0, 1)]
    public float shadowIntensity = 0.01f;
    [Range(0, 1)]
    public float shadowSpread = 0.01f;

    class CustomRenderPass : ScriptableRenderPass
    {

        public Material material;
        public ComputeShader computeShader;
        public Light dirLight;
        [Range(0, 1)]
        public float shadowSpread = 0.01f;
        [Range(0, 1)]
        public float shadowIntensity = 0.01f;

        private class PassData
        {
            public TextureHandle shadowMap;
            public TextureHandle depthTexture;
            public TextureHandle gbuffer2;

            // parms:
            public Material shadowMapBlitMat = null;
            public ComputeShader shadowMappingCS = null;
            public Light dirLight = null;
            [Range(0, 1)]
            public float shadowSpread = 0.01f;
            [Range(0, 1)]
            public float shadowIntensity = 0.75f;

            public Camera cam;
            public uint cameraWidth = 0;
            public uint cameraHeight = 0;

            public RayTracingAccelerationStructure rtas = null;


            public int frameIndex = 0;
            public int temporalAccumulationStep = 0;
            public Matrix4x4 prevCameraMatrix = Matrix4x4.identity;
            public Matrix4x4 prevProjMatrix = Matrix4x4.identity;
            public Matrix4x4 prevLightMatrix = Matrix4x4.identity;
            public float prevShadowSpread = 0.0f;
            public float prevShadowIntensity = 0.0f;
        }

        // This static method is passed as the RenderFunc delegate to the RenderGraph render pass.
        // It is used to execute draw commands.
        static void ExecuteComputePass(PassData data, ComputeGraphContext context)
        {

            if (data.rtas == null)
            {
                RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings();
                settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
                settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
                settings.layerMask = 255;

                data.rtas = new RayTracingAccelerationStructure(settings);
            }

            if (data.cameraWidth != data.cam.pixelWidth || data.cameraHeight != data.cam.pixelHeight)
            {
  
                data.cameraWidth = (uint)data.cam.pixelWidth;
                data.cameraHeight = (uint)data.cam.pixelHeight;

                data.temporalAccumulationStep = 0;
            }

            if (!SystemInfo.supportsInlineRayTracing)
            {
                Debug.Log("Ray Queries (DXR 1.1) are not supported by this GPU or by the current graphics API.");
                return;
            }

            if (data.dirLight == null || data.dirLight.type != UnityEngine.LightType.Directional)
            {
                Debug.Log("Please assign a Directional Light.");
                return;
            }

            if (data.rtas == null)
                return;

            if (!data.cam)
                return;

            if (data.frameIndex == 0)
            {
                data.temporalAccumulationStep = 0;
                data.prevLightMatrix = data.dirLight.transform.localToWorldMatrix;
                data.prevCameraMatrix = data.cam.cameraToWorldMatrix;
                data.prevProjMatrix = data.cam.projectionMatrix;
                data.prevShadowSpread = data.shadowSpread;
            }

            RayTracingInstanceCullingConfig cullingConfig = new RayTracingInstanceCullingConfig();

            cullingConfig.flags = RayTracingInstanceCullingFlags.EnableLODCulling;

            cullingConfig.lodParameters.fieldOfView = data.cam.fieldOfView;
            cullingConfig.lodParameters.cameraPixelHeight = data.cam.pixelHeight;
            cullingConfig.lodParameters.isOrthographic = false;
            cullingConfig.lodParameters.cameraPosition = data.cam.transform.position;

            cullingConfig.subMeshFlagsConfig.opaqueMaterials = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly;
            cullingConfig.subMeshFlagsConfig.transparentMaterials = RayTracingSubMeshFlags.Disabled;
            cullingConfig.subMeshFlagsConfig.alphaTestedMaterials = RayTracingSubMeshFlags.Disabled;

            List<RayTracingInstanceCullingTest> instanceTests = new List<RayTracingInstanceCullingTest>();

            RayTracingInstanceCullingTest instanceTest = new RayTracingInstanceCullingTest();
            instanceTest.allowOpaqueMaterials = true;
            instanceTest.allowAlphaTestedMaterials = true;
            instanceTest.allowTransparentMaterials = false;
            instanceTest.layerMask = -1;
            instanceTest.shadowCastingModeMask = (1 << (int)ShadowCastingMode.On) | (1 << (int)ShadowCastingMode.TwoSided);
            instanceTest.instanceMask = 1 << 0;

            instanceTests.Add(instanceTest);

            cullingConfig.instanceTests = instanceTests.ToArray();

            data.rtas.ClearInstances();
            RayTracingInstanceCullingResults cullingResults = data.rtas.CullInstances(ref cullingConfig);

            int kernelIndex = data.shadowMappingCS.FindKernel("CSMain");
            if (kernelIndex == -1)
                return;

            if (!data.shadowMappingCS.IsSupported(kernelIndex))
            {
                Debug.Log("Compute shader " + data.shadowMappingCS.name + " failed to compile or is not supported.");
                return;
            }

            uint threadGroupSizeX;
            uint threadGroupSizeY;
            uint threadGroupSizeZ;
            data.shadowMappingCS.GetKernelThreadGroupSizes(kernelIndex, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);

            context.cmd.BuildRayTracingAccelerationStructure(data.rtas);

            float invHalfTanFOV = 1.0f / Mathf.Tan(Mathf.Deg2Rad * data.cam.fieldOfView * 0.5f);
            float aspectRatio = data.cam.pixelHeight / (float)data.cam.pixelWidth;

            Vector4 DepthToViewParams = new Vector4(
                    2.0f / (invHalfTanFOV * aspectRatio * data.cam.pixelWidth),
                    2.0f / (invHalfTanFOV * data.cam.pixelHeight),
                    1.0f / (invHalfTanFOV * aspectRatio),
                    1.0f / invHalfTanFOV
                    );

            if (data.prevCameraMatrix != data.cam.cameraToWorldMatrix ||
                data.prevProjMatrix != data.cam.projectionMatrix ||
                data.prevLightMatrix != data.dirLight.transform.localToWorldMatrix ||
                data.prevShadowSpread != data.shadowSpread ||
                data.prevShadowIntensity != data.shadowIntensity ||
                cullingResults.transformsChanged)
            {
                data.temporalAccumulationStep = 0;
            }

            context.cmd.SetComputeVectorParam(data.shadowMappingCS, "g_DepthToViewParams", DepthToViewParams);
            context.cmd.SetComputeIntParam(data.shadowMappingCS, "g_FrameIndex", data.frameIndex);
            context.cmd.SetComputeVectorParam(data.shadowMappingCS, "g_LightDir", data.dirLight.transform.forward);
            context.cmd.SetComputeFloatParam(data.shadowMappingCS, "g_ShadowSpread", data.shadowSpread * 0.1f);
            context.cmd.SetComputeFloatParam(data.shadowMappingCS, "g_ShadowIntensity", data.shadowIntensity);

            context.cmd.SetRayTracingAccelerationStructure(data.shadowMappingCS, kernelIndex, "g_AccelStruct", data.rtas);
            context.cmd.SetComputeIntParam(data.shadowMappingCS, "g_TemporalAccumulationStep", data.temporalAccumulationStep);

            context.cmd.SetComputeMatrixParam(data.shadowMappingCS, "g_CameraToWorld", data.cam.cameraToWorldMatrix);

            context.cmd.SetComputeTextureParam(data.shadowMappingCS, kernelIndex, "g_Output", data.shadowMap);
            context.cmd.SetComputeTextureParam(data.shadowMappingCS, kernelIndex, "_DepthBuffer", data.depthTexture);
            context.cmd.SetComputeTextureParam(data.shadowMappingCS, kernelIndex, "_Gbuffer2", data.gbuffer2);

            Matrix4x4 lightMatrix = data.dirLight.transform.localToWorldMatrix;
            lightMatrix.SetColumn(2, -lightMatrix.GetColumn(2));
            context.cmd.SetComputeMatrixParam(data.shadowMappingCS, "g_LightMatrix", lightMatrix);

            context.cmd.DispatchCompute(data.shadowMappingCS, kernelIndex, (int)((data.cam.pixelWidth + threadGroupSizeX + 1) / threadGroupSizeX), (int)((data.cam.pixelHeight + threadGroupSizeY + 1) / threadGroupSizeY), 1);

            data.prevCameraMatrix = data.cam.cameraToWorldMatrix;
            data.prevProjMatrix = data.cam.projectionMatrix;
            data.prevLightMatrix = data.dirLight.transform.localToWorldMatrix;
            data.prevShadowSpread = data.shadowSpread;
            data.prevShadowIntensity = data.shadowIntensity;

            data.temporalAccumulationStep++;
            data.frameIndex++;
        }

        static void ExecuteBlitPass(PassData data, RasterGraphContext context)
        {
            data.shadowMapBlitMat.SetFloat("_ShadowIntensity", data.shadowIntensity);
            Blitter.BlitTexture(context.cmd, data.shadowMap, new Vector4(1, 1, 0, 0), data.shadowMapBlitMat,0);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            TextureHandle depthTextureHandle = resourceData.cameraDepthTexture;
            TextureHandle normalsHandle = resourceData.gBuffer[2]; 

            TextureDesc desc = new TextureDesc(cameraData.camera.pixelWidth, cameraData.camera.pixelHeight);
            desc.name = "Shadowmap";
            desc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat;
            desc.enableRandomWrite = true;
            TextureHandle shadowTextureHandle = renderGraph.CreateTexture(desc);

            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            Light light = lightData.visibleLights[0].light; 

            const string passName0 = "Compute Shadows Pass";
            using (var builder = renderGraph.AddComputePass<PassData>(passName0, out var passData))
            {
                passData.dirLight = light;

                passData.shadowMap = shadowTextureHandle;
                passData.depthTexture = depthTextureHandle;
                passData.gbuffer2 = normalsHandle;

                passData.shadowMappingCS = computeShader;
                passData.shadowSpread = shadowSpread;
                passData.shadowIntensity = shadowIntensity;

                passData.cam = cameraData.camera;

                // Read: Gbuffer (normals) and Depth Texture
                builder.UseTexture(passData.gbuffer2, AccessFlags.Read);
                builder.UseTexture(passData.depthTexture, AccessFlags.Read);

                // Write: Shadow map
                builder.UseTexture(passData.shadowMap, AccessFlags.ReadWrite);

                builder.SetRenderFunc((PassData data, ComputeGraphContext context) => ExecuteComputePass(data, context));
            }

            const string passName1 = "Blit (Apply Shadows) Pass";
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName1, out var passData)) {

                passData.shadowMapBlitMat = material;
                passData.shadowMap = shadowTextureHandle;
                passData.shadowIntensity = shadowIntensity;
                // Read: Shadow map
                builder.UseTexture(passData.shadowMap, AccessFlags.Read);
                // Write: Color texture
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                // Assigns the ExecutePass function to the render pass delegate. This will be called by the render graph when executing the pass.
                builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecuteBlitPass(data, context));
            }
        }

    }

    CustomRenderPass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new CustomRenderPass();
        m_ScriptablePass.material = material;
        m_ScriptablePass.computeShader = computeShader;

        m_ScriptablePass.dirLight = dirLight;
        m_ScriptablePass.shadowSpread = shadowSpread;
        m_ScriptablePass.shadowIntensity = shadowIntensity;

        // Configures where the render pass should be injected.
        m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingDeferredLights;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
    }
}
