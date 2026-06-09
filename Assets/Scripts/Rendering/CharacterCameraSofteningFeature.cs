using BodyTracking.LookDev;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace BodyTracking.Rendering
{
    /// <summary>
    /// Full-screen soften pass for AR: blurs sharp CG edges/texture detail while leaving the soft camera feed
    /// mostly alone. Uses Blitter.BlitCameraTexture in compatibility mode (RenderGraph disabled on device).
    /// </summary>
    public class CharacterCameraSofteningFeature : ScriptableRendererFeature
    {
        static readonly int StrengthId = Shader.PropertyToID("_Strength");
        static readonly int MinBlendId = Shader.PropertyToID("_MinBlend");
        static readonly int BlurRadiusId = Shader.PropertyToID("_BlurRadius");
        static readonly int DepthStrengthId = Shader.PropertyToID("_DepthStrength");
        static readonly int MatchStrengthId = Shader.PropertyToID("_MatchStrength");
        static readonly int MatchContrastId = Shader.PropertyToID("_MatchContrast");
        static readonly int MatchSaturationId = Shader.PropertyToID("_MatchSaturation");
        static readonly int MatchBlackLiftId = Shader.PropertyToID("_MatchBlackLift");

        [SerializeField] Shader shader;
        SofteningPass pass;
        Material material;

        public override void Create()
        {
            if (shader == null)
                shader = Shader.Find("Hidden/TENDOR/CharacterCameraSoftening");

            if (shader != null && material == null)
                material = CoreUtils.CreateEngineMaterial(shader);

            pass ??= new SofteningPass("Character Camera Softening");
            // After transparents + AR character; still before FinalBlit (AfterRendering block).
            pass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var settings = CharacterCameraMatch.Current;
            if (!settings.enabled || !settings.screenSoftening || material == null)
                return;

            // Skip the two full-screen blits when no CG character is on screen (idle/record camera mode).
            if (!CharacterCameraMatch.ScreenSofteningActive)
                return;

            if (renderingData.cameraData.cameraType == CameraType.Preview
                || renderingData.cameraData.cameraType == CameraType.Reflection
                || UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData))
                return;

            pass.Setup(material, settings);
            pass.requiresIntermediateTexture = true;
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            CoreUtils.Destroy(material);
            material = null;
        }

        sealed class SofteningPass : ScriptableRenderPass
        {
            Material mat;
            RTHandle scratch;
            readonly ProfilingSampler executeSampler;

            public SofteningPass(string passName)
            {
                profilingSampler = new ProfilingSampler(passName);
                executeSampler = new ProfilingSampler($"{passName} Execute");
            }

            public void Setup(Material material, CharacterCameraMatch.Settings settings)
            {
                mat = material;
                mat.SetFloat(StrengthId, settings.screenStrength);
                mat.SetFloat(MinBlendId, settings.screenMinBlend);
                mat.SetFloat(BlurRadiusId, settings.screenBlurRadius);
                mat.SetFloat(DepthStrengthId, settings.screenDepthStrength);
                mat.SetFloat(MatchStrengthId, settings.matchStrength);
                mat.SetFloat(MatchContrastId, settings.matchContrast);
                mat.SetFloat(MatchSaturationId, settings.matchSaturation);
                mat.SetFloat(MatchBlackLiftId, settings.matchBlackLift);
            }

            void ReAllocateScratch(RenderTextureDescriptor desc)
            {
                desc.msaaSamples = 1;
                desc.depthStencilFormat = GraphicsFormat.None;
                RenderingUtils.ReAllocateHandleIfNeeded(ref scratch, desc, FilterMode.Bilinear, TextureWrapMode.Clamp,
                    name: "_CharacterSofteningScratch");
            }

            public void Dispose()
            {
                scratch?.Release();
            }

#pragma warning disable 618, 672
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                ResetTarget();
                ReAllocateScratch(renderingData.cameraData.cameraTargetDescriptor);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get();
                ref var cameraData = ref renderingData.cameraData;
                var source = cameraData.renderer.cameraColorTargetHandle;

                using (new ProfilingScope(cmd, executeSampler))
                {
                    // BlitCameraTexture handles AR/XR UV flips correctly on device (manual DrawProcedural did not).
                    Blitter.BlitCameraTexture(cmd, source, scratch, 0f, false);
                    Blitter.BlitCameraTexture(cmd, scratch, source, mat, 0);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore 618, 672

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var desc = renderGraph.GetTextureDesc(resources.cameraColor);
                desc.name = "_CharacterSofteningScratch";
                desc.clearBuffer = false;

                var source = resources.activeColorTexture;
                var copy = renderGraph.CreateTexture(desc);

                using (var builder = renderGraph.AddRasterRenderPass<CopyData>("Copy Character Softening", out var copyData, profilingSampler))
                {
                    copyData.input = source;
                    builder.UseTexture(copyData.input, AccessFlags.Read);
                    builder.SetRenderAttachment(copy, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (CopyData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.input, new Vector4(1, 1, 0, 0), 0f, false);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<MainData>("Character Camera Softening", out var mainData, profilingSampler))
                {
                    mainData.material = mat;
                    mainData.source = copy;
                    builder.UseTexture(mainData.source, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (MainData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                    });
                }
            }

            sealed class CopyData
            {
                internal TextureHandle input;
            }

            sealed class MainData
            {
                internal Material material;
                internal TextureHandle source;
            }
        }
    }
}
