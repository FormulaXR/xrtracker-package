#if HAS_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace IV.FormulaTracker
{
	/// <summary>
	/// URP ScriptableRendererFeature that draws edge outlines in both Scene and Game view.
	/// Two-pass approach:
	///   Pass 1 (Occlusion): Draws source mesh renderers to depth buffer only (ColorMask 0)
	///   Pass 2 (Edges): Draws edge line mesh with ZTest LEqual so lines behind geometry are hidden
	///
	/// Works with EdgeOutlineRenderer components which register themselves via a static collection.
	/// Add this feature to the URP Renderer Asset.
	/// </summary>
	public class EdgeOutlineFeature : ScriptableRendererFeature
	{
		[SerializeField] private RenderPassEvent _renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

		private EdgeOutlinePass _pass;
		private Material _occlusionMat;
		private Material _edgeMat;

		private const string MAIN_CAMERA = "MainCamera";

		public override void Create()
		{
			_occlusionMat = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/EdgeOutlineOcclusion"));
			_edgeMat = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/EdgeOutline"));

			_pass = new EdgeOutlinePass(_occlusionMat, _edgeMat)
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

			if (EdgeOutlineRenderer.Instances.Count == 0)
				return;

			if (_occlusionMat == null || _edgeMat == null)
				return;

			renderer.EnqueuePass(_pass);
		}

		protected override void Dispose(bool disposing)
		{
			CoreUtils.Destroy(_occlusionMat);
			CoreUtils.Destroy(_edgeMat);
		}

		class EdgeOutlinePass : ScriptableRenderPass
		{
			private readonly Material _occlusionMat;
			private readonly Material _edgeMat;
			private static readonly int ColorProp = Shader.PropertyToID("_Color");
			private static readonly int WidthProp = Shader.PropertyToID("_Width");
			private static readonly MaterialPropertyBlock PropertyBlock = new();

			class PassData
			{
				public Material OcclusionMat;
				public Material EdgeMat;
				public Camera Camera;
				public TextureHandle ColorTarget;
				public TextureHandle DepthTarget;
			}

			public EdgeOutlinePass(Material occlusionMat, Material edgeMat)
			{
				_occlusionMat = occlusionMat;
				_edgeMat = edgeMat;
			}

			public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
			{
				if (_occlusionMat == null || _edgeMat == null)
					return;

				if (EdgeOutlineRenderer.Instances.Count == 0)
					return;

				UniversalResourceData resourceData = frameContext.Get<UniversalResourceData>();
				UniversalCameraData cameraData = frameContext.Get<UniversalCameraData>();

				using (var builder = renderGraph.AddUnsafePass<PassData>("EdgeOutline", out var passData))
				{
					passData.OcclusionMat = _occlusionMat;
					passData.EdgeMat = _edgeMat;
					passData.Camera = cameraData.camera;
					passData.ColorTarget = resourceData.activeColorTexture;
					passData.DepthTarget = resourceData.activeDepthTexture;

					builder.UseTexture(passData.ColorTarget, AccessFlags.Write);
					builder.UseTexture(passData.DepthTarget, AccessFlags.Write);
					builder.AllowPassCulling(false);

					builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
					{
						CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

						RTHandle colorRT = data.ColorTarget;
						RTHandle depthRT = data.DepthTarget;

						// Update all outlines for this camera first
						foreach (var outline in EdgeOutlineRenderer.Instances)
						{
							if (outline == null || !outline.isActiveAndEnabled)
								continue;
							outline.UpdateForCamera(data.Camera);
						}

						// Pass 1: All occlusions — depth-only
						if (depthRT != null)
						{
							cmd.SetRenderTarget(depthRT);

							foreach (var outline in EdgeOutlineRenderer.Instances)
							{
								if (outline == null || !outline.isActiveAndEnabled || !outline.EnableOcclusion)
									continue;

								IList<MeshFilter> meshFilters = outline.GetMeshFilters();
								foreach (MeshFilter mf in meshFilters)
								{
									if (mf == null || mf.sharedMesh == null) continue;
									Mesh mesh = mf.sharedMesh;
									Matrix4x4 localToWorld = mf.transform.localToWorldMatrix;
									for (int sub = 0; sub < mesh.subMeshCount; sub++)
										cmd.DrawMesh(mesh, localToWorld, data.OcclusionMat, sub, 0);
								}
							}
						}

						// Pass 2: All edge lines — color + depth
						if (colorRT != null && depthRT != null)
							cmd.SetRenderTarget(colorRT, depthRT);

						foreach (var outline in EdgeOutlineRenderer.Instances)
						{
							if (outline == null || !outline.isActiveAndEnabled)
								continue;

							Mesh edgeMesh = outline.EdgeMesh;
							if (edgeMesh == null || edgeMesh.vertexCount == 0)
								continue;

							PropertyBlock.SetColor(ColorProp, outline.EdgeColor);
							PropertyBlock.SetFloat(WidthProp, outline.EdgeWidth);
							Matrix4x4 matrix = outline.transform.localToWorldMatrix;
							cmd.DrawMesh(edgeMesh, matrix, data.EdgeMat, 0, 0, PropertyBlock);
						}
					});
				}
			}
		}
	}
}
#endif
