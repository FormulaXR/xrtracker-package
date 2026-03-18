#if HAS_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace IV.FormulaTracker
{
	/// <summary>
	/// URP ScriptableRendererFeature that draws correspondence visualization lines.
	/// Renders line meshes from CorrespondenceVisualizer components as a debug overlay.
	/// Add this feature to the URP Renderer Asset.
	/// </summary>
	public class CorrespondenceFeature : ScriptableRendererFeature
	{
		[SerializeField] private RenderPassEvent _renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

		private CorrespondencePass _pass;
		private Material _lineMat;

		private const string MAIN_CAMERA = "MainCamera";

		public override void Create()
		{
			_lineMat = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/CorrespondenceLines"));
			_pass = new CorrespondencePass(_lineMat)
			{
				renderPassEvent = _renderPassEvent
			};
		}

		public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
		{
#pragma warning disable CS0618
			CameraData cameraData = renderingData.cameraData;
#pragma warning restore CS0618

			if (!cameraData.isSceneViewCamera && !cameraData.camera.CompareTag(MAIN_CAMERA))
				return;

			if (CorrespondenceVisualizer.Instances.Count == 0)
				return;

			if (_lineMat == null)
				return;

			renderer.EnqueuePass(_pass);
		}

		protected override void Dispose(bool disposing)
		{
			CoreUtils.Destroy(_lineMat);
		}

		class CorrespondencePass : ScriptableRenderPass
		{
			private readonly Material _lineMat;

			class PassData
			{
				public Material LineMat;
				public TextureHandle ColorTarget;
				public TextureHandle DepthTarget;
			}

			public CorrespondencePass(Material lineMat)
			{
				_lineMat = lineMat;
			}

			public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
			{
				if (_lineMat == null)
					return;

				if (CorrespondenceVisualizer.Instances.Count == 0)
					return;

				UniversalResourceData resourceData = frameContext.Get<UniversalResourceData>();

				using (var builder = renderGraph.AddUnsafePass<PassData>("CorrespondenceLines", out var passData))
				{
					passData.LineMat = _lineMat;
					passData.ColorTarget = resourceData.activeColorTexture;
					passData.DepthTarget = resourceData.activeDepthTexture;

					builder.UseTexture(passData.ColorTarget, AccessFlags.Write);
					builder.UseTexture(passData.DepthTarget, AccessFlags.Read);
					builder.AllowPassCulling(false);

					builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
					{
						CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

						RTHandle colorRT = data.ColorTarget;
						RTHandle depthRT = data.DepthTarget;

						if (colorRT != null && depthRT != null)
							cmd.SetRenderTarget(colorRT, depthRT);

						foreach (var viz in CorrespondenceVisualizer.Instances)
						{
							if (viz == null || !viz.isActiveAndEnabled)
								continue;

							Mesh mesh = viz.CorrespondenceMesh;
							if (mesh == null || mesh.vertexCount == 0)
								continue;

							cmd.DrawMesh(mesh, Matrix4x4.identity, data.LineMat, 0, 0);
						}
					});
				}
			}
		}
	}
}
#endif
