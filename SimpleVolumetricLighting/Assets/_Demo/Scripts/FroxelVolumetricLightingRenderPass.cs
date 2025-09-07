using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

public class FroxelVolumetricLightingRenderPass : ScriptableRenderPass
{
    private FroxelVolumetricLightingSettings settings;
    private RenderTexture lightInjectionTexture;
    private RenderTexture lightScatteringTexture;
    private Vector3Int lightInjectionNumThreads = new Vector3Int(16, 2, 16);
    private Vector3Int lightScatteringNumThreads = new Vector3Int(32, 2, 1);
    private Vector3Int resolution = new Vector3Int(160, 90, 128);
    private Vector4[] frustumRays = new Vector4[4];
    private int spotLightCount;
    private SpotLightParameters[] spotLightParameters;
    private ComputeBuffer spotComputeBuffer;
    private Material blitMaterial;

    public struct SpotLightParameters
    {
        public Vector3 position;
        public Vector3 direction;
        public float range;
        public Vector3 color;
        public float innerCos;
        public float outerCos;
    }

    public class PassData
    {
        public ComputeShader computeShader;
        public TextureHandle additionalShadowsTexture;
    }

    public FroxelVolumetricLightingRenderPass(FroxelVolumetricLightingSettings froxelVolumetricLightingSettings)
    {
        settings = froxelVolumetricLightingSettings;

        lightInjectionTexture = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.ARGBHalf);
        lightInjectionTexture.volumeDepth = resolution.z;
        lightInjectionTexture.dimension = TextureDimension.Tex3D;
        lightInjectionTexture.enableRandomWrite = true;
        lightInjectionTexture.Create();

        lightScatteringTexture = new RenderTexture(resolution.x, resolution.y, 0, RenderTextureFormat.ARGBHalf);
        lightScatteringTexture.volumeDepth = resolution.z;
        lightScatteringTexture.dimension = TextureDimension.Tex3D;
        lightScatteringTexture.enableRandomWrite = true;
        lightScatteringTexture.Create();
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        Camera camera = cameraData.camera;

        using (var builder = renderGraph.AddComputePass("Lightss", out PassData passData))
        {
            passData.computeShader = settings.computeShader;
            passData.additionalShadowsTexture = resourceData.additionalShadowsTexture;

            builder.UseTexture(passData.additionalShadowsTexture);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, ComputeGraphContext context) => 
            {
                ComputeShader computeShader = data.computeShader;
                ComputeCommandBuffer ccb = context.cmd;

                UpdateFrustumRays(camera);
                UpdateSpotLights();

                int kernelIndex = computeShader.FindKernel("LightInjection");
                computeShader.SetTexture(kernelIndex, "_LightInjectionTexture", lightInjectionTexture);
                ccb.SetComputeIntParam(computeShader, "_SpotLightCount", spotLightCount);
                ccb.SetComputeFloatParam(computeShader, "_NearClipPlane", camera.nearClipPlane);
                ccb.SetComputeFloatParam(computeShader, "_FarClipPlane", camera.farClipPlane);
                ccb.SetComputeVectorParam(computeShader, "_Resolution", new Vector4(resolution.x, resolution.y, resolution.z, 0));
                ccb.SetComputeVectorArrayParam(computeShader, "_FrustumRays", frustumRays);
                ccb.SetComputeMatrixArrayParam(computeShader, "_AdditionalLightsWorldToShadow", Shader.GetGlobalMatrixArray("_AdditionalLightsWorldToShadow"));
                ccb.SetComputeBufferParam(computeShader, kernelIndex, "_SpotLightParameters", spotComputeBuffer);
                ccb.SetComputeTextureParam(computeShader, kernelIndex, "_AdditionalShadowsTexture", data.additionalShadowsTexture);
                ccb.DispatchCompute(computeShader, kernelIndex, resolution.x / lightInjectionNumThreads.x, resolution.y / lightInjectionNumThreads.y, resolution.z / lightInjectionNumThreads.z);

                kernelIndex = computeShader.FindKernel("LightScattering");
                computeShader.SetTexture(kernelIndex, "_LightInjectionTexture", lightInjectionTexture);
                computeShader.SetTexture(kernelIndex, "_LightScatteringTexture", lightScatteringTexture);
                ccb.DispatchCompute(computeShader, kernelIndex, resolution.x / lightScatteringNumThreads.x, resolution.y / lightScatteringNumThreads.y, resolution.z / lightScatteringNumThreads.z);
            });
        }

        if (blitMaterial == null) blitMaterial = new Material(Shader.Find("Custom/BlendVolumetricLighting"));
        RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
        descriptor = cameraData.cameraTargetDescriptor;
        descriptor.depthStencilFormat = GraphicsFormat.None;
        descriptor.colorFormat = RenderTextureFormat.ARGBHalf;
        blitMaterial.SetFloat("_NearOverFarClip", camera.nearClipPlane / camera.farClipPlane);
        blitMaterial.SetVector("_Resolution", new Vector4(resolution.x, resolution.y, resolution.z, 0f));
        blitMaterial.SetTexture("_LightScatteringTexture", lightScatteringTexture);
        TextureHandle blendColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, descriptor, "_BlendVolumetricLighting", true);
        BlitMaterialParameters blitMaterialParameters = new BlitMaterialParameters(resourceData.activeColorTexture, blendColor, blitMaterial, 0);
        renderGraph.AddBlitPass(blitMaterialParameters, "Blend Volumetric Lighting");
        resourceData.cameraColor = blendColor;

    }

    private void UpdateFrustumRays(Camera camera)
    {
        Vector3[] frustumCorners = new Vector3[4];
        camera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), camera.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, frustumCorners);
        for (int i = 0; i < frustumCorners.Length; i++)
        {
            Vector3 worldCorner = camera.transform.TransformPoint(frustumCorners[i]);
            Vector3 frustumRay = worldCorner - camera.transform.position;
            frustumRays[i] = frustumRay;
        }
    }

    private void UpdateSpotLights()
    {
        Light[] spotLights = GameObject.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(light => light.type == LightType.Spot).ToArray();
        spotLightCount = spotLights.Count();
        if (spotComputeBuffer == null || spotComputeBuffer.count != spotLightCount)
        {
            spotComputeBuffer?.Dispose();
            spotComputeBuffer = new ComputeBuffer(spotLightCount, Marshal.SizeOf(typeof(SpotLightParameters)));
        }

        spotLightParameters = new SpotLightParameters[spotLightCount];
        for (int i = 0; i < spotLightCount; i++)
        {
            Light light = spotLights[i];
            spotLightParameters[i].position = light.transform.position;
            spotLightParameters[i].direction = light.transform.forward;
            spotLightParameters[i].range = light.range;
            spotLightParameters[i].color = new Vector3(light.color.r, light.color.g, light.color.b) * light.intensity;
            spotLightParameters[i].innerCos = Mathf.Cos(light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
            spotLightParameters[i].outerCos = Mathf.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5f);
        }

        spotComputeBuffer.SetData(spotLightParameters);
    }

    public void Dispose()
    {
        lightInjectionTexture?.Release();
        lightScatteringTexture?.Release();
        spotComputeBuffer?.Dispose();
    }
}
