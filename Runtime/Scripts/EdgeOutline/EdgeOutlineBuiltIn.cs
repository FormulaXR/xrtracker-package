using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Built-in pipeline fallback for edge outline rendering.
	/// Attaches CommandBuffers to the camera to draw occlusion + edge passes.
	/// When URP is the active pipeline, EdgeOutlineFeature handles this instead.
	/// Auto-added by EdgeOutlineRenderer when no SRP is active.
	/// </summary>
	[RequireComponent(typeof(Camera))]
	public class EdgeOutlineBuiltIn : MonoBehaviour
	{
		Camera _camera;
		CommandBuffer _cmd;
		Material _occlusionMat;
		Material _edgeMat;

		static readonly int ColorProp = Shader.PropertyToID("_Color");
		static readonly int WidthProp = Shader.PropertyToID("_Width");
		static readonly int StencilCompProp = Shader.PropertyToID("_StencilComp");
		const int STENCIL_NOT_EQUAL = 6;
		const int STENCIL_ALWAYS = 8;
		MaterialPropertyBlock _propertyBlock;

		void OnEnable()
		{
			_propertyBlock = new MaterialPropertyBlock();
			_camera = GetComponent<Camera>();

			var occlusionShader = Shader.Find("Hidden/EdgeOutlineOcclusion");
			var edgeShader = Shader.Find("Hidden/EdgeOutline");

			if (occlusionShader != null)
				_occlusionMat = new Material(occlusionShader) { hideFlags = HideFlags.HideAndDontSave };
			if (edgeShader != null)
				_edgeMat = new Material(edgeShader) { hideFlags = HideFlags.HideAndDontSave };

			_cmd = new CommandBuffer { name = "EdgeOutline" };
			_camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, _cmd);
		}

		void OnDisable()
		{
			if (_camera != null && _cmd != null)
				_camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, _cmd);

			if (_cmd != null) { _cmd.Dispose(); _cmd = null; }
			if (_occlusionMat != null) { DestroyImmediate(_occlusionMat); _occlusionMat = null; }
			if (_edgeMat != null) { DestroyImmediate(_edgeMat); _edgeMat = null; }
		}

		void OnPreRender()
		{
			if (_cmd == null || _occlusionMat == null || _edgeMat == null)
				return;

			_cmd.Clear();

			IReadOnlyCollection<EdgeOutlineRenderer> instances = EdgeOutlineRenderer.Instances;
			if (instances.Count == 0)
				return;

			// Update all outlines for this camera
			foreach (var outline in instances)
			{
				if (outline == null || !outline.isActiveAndEnabled)
					continue;
				outline.UpdateForCamera(_camera);
			}

			// Pass 1: Stencil mask + depth for all outlines
			foreach (var outline in instances)
			{
				if (outline == null || !outline.isActiveAndEnabled)
					continue;

				IList<MeshFilter> meshFilters = outline.GetMeshFilters();
				foreach (MeshFilter mf in meshFilters)
				{
					if (mf == null || mf.sharedMesh == null) continue;
					Mesh mesh = mf.sharedMesh;
					Matrix4x4 localToWorld = mf.transform.localToWorldMatrix;
					for (int sub = 0; sub < mesh.subMeshCount; sub++)
						_cmd.DrawMesh(mesh, localToWorld, _occlusionMat, sub, 0);
				}
			}

			// Pass 2: Edge lines — stencil test per outline
			foreach (var outline in instances)
			{
				if (outline == null || !outline.isActiveAndEnabled)
					continue;

				Mesh edgeMesh = outline.EdgeMesh;
				if (edgeMesh == null || edgeMesh.vertexCount == 0)
					continue;

				_edgeMat.SetInt(StencilCompProp, outline.ShowInternalEdges ? STENCIL_ALWAYS : STENCIL_NOT_EQUAL);
				_propertyBlock.SetColor(ColorProp, outline.EdgeColor);
				_propertyBlock.SetFloat(WidthProp, outline.EdgeWidth);
				Matrix4x4 matrix = outline.transform.localToWorldMatrix;
				_cmd.DrawMesh(edgeMesh, matrix, _edgeMat, 0, 0, _propertyBlock);
			}
		}
	}
}
