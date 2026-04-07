using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Built-in pipeline fallback for correspondence line rendering.
	/// When URP is the active pipeline, CorrespondenceFeature handles this instead.
	/// Auto-added by CorrespondenceVisualizer when no SRP is active.
	/// </summary>
	[RequireComponent(typeof(Camera))]
	public class CorrespondenceBuiltIn : MonoBehaviour
	{
		Camera _camera;
		CommandBuffer _cmd;
		Material _lineMat;

		void OnEnable()
		{
			_camera = GetComponent<Camera>();

			var shader = Shader.Find("Hidden/CorrespondenceLines");
			if (shader != null)
				_lineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

			_cmd = new CommandBuffer { name = "CorrespondenceLines" };
			_camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _cmd);
		}

		void OnDisable()
		{
			if (_camera != null && _cmd != null)
				_camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _cmd);

			if (_cmd != null) { _cmd.Dispose(); _cmd = null; }
			if (_lineMat != null) { DestroyImmediate(_lineMat); _lineMat = null; }
		}

		void OnPreRender()
		{
			if (_cmd == null || _lineMat == null)
				return;

			_cmd.Clear();

			IReadOnlyCollection<CorrespondenceVisualizer> instances = CorrespondenceVisualizer.Instances;
			if (instances.Count == 0)
				return;

			foreach (var viz in instances)
			{
				if (viz == null || !viz.isActiveAndEnabled)
					continue;

				Mesh mesh = viz.CorrespondenceMesh;
				if (mesh == null || mesh.vertexCount == 0)
					continue;

				_cmd.DrawMesh(mesh, Matrix4x4.identity, _lineMat, 0, 0);
			}
		}
	}
}
