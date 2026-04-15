using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Fetches correspondence data from M3T's region/depth modalities each frame
	/// and builds a line mesh for visualization. Works with CorrespondenceFeature
	/// (ScriptableRendererFeature) for rendering.
	/// </summary>
	public class CorrespondenceVisualizer : MonoBehaviour
	{
		static readonly HashSet<CorrespondenceVisualizer> s_instances = new();
		public static IReadOnlyCollection<CorrespondenceVisualizer> Instances => s_instances;

		[Header("Data Source")]
		[Tooltip("TrackedBody to visualize correspondences for. Auto-detected from this GameObject if null.")]
		[SerializeField] TrackedBody _trackedBody;

		[Header("Visibility")]
		[Tooltip("Show silhouette (region) modality correspondences.")]
		[SerializeField] bool _showRegion = true;
		[Tooltip("Show depth modality correspondences.")]
		[SerializeField] bool _showDepth = true;
		[Tooltip("Show edge modality correspondences.")]
		[SerializeField] bool _showEdge = true;
		[Tooltip("Show active edge tracking sites as oriented ticks (magenta).")]
		[SerializeField] bool _showModelEdges = true;
		[Tooltip("Show texture modality feature correspondences.")]
		[SerializeField] bool _showTexture = true;

		[Header("Colors")]
		[SerializeField] Color _goodColor = Color.green;
		[SerializeField] Color _badColor = Color.red;
		[SerializeField] Color _creaseEdgeColor = Color.magenta;
		[SerializeField] Color _silhouetteEdgeColor = Color.cyan;
		[SerializeField] Color _normalColor = Color.yellow;
		[Tooltip("Color for dead (failed) edge sites.")]
		[SerializeField] Color _deadSiteColor = new Color(0.15f, 0.15f, 0.15f, 1f);

		const int MAX_CORRESPONDENCES = 512;

		FTCorrespondenceLine[] _regionBuffer;
		FTCorrespondenceLine[] _depthBuffer;
		FTCorrespondenceLine[] _edgeBuffer;
		FTCorrespondenceLine[] _modelEdgeBuffer;
		FTCorrespondenceLine[] _textureBuffer;
		Mesh _mesh;

		// Reusable lists to avoid per-frame allocation
		readonly List<Vector3> _vertices = new();
		readonly List<Color> _colors = new();
		readonly List<int> _indices = new();

		public Mesh CorrespondenceMesh => _mesh;

		void OnEnable()
		{
			s_instances.Add(this);

			if (_trackedBody == null)
				_trackedBody = GetComponent<TrackedBody>();

			_regionBuffer = new FTCorrespondenceLine[MAX_CORRESPONDENCES];
			_depthBuffer = new FTCorrespondenceLine[MAX_CORRESPONDENCES];
			_edgeBuffer = new FTCorrespondenceLine[MAX_CORRESPONDENCES];
			_modelEdgeBuffer = new FTCorrespondenceLine[MAX_CORRESPONDENCES * 4];
			_textureBuffer = new FTCorrespondenceLine[MAX_CORRESPONDENCES];

			_mesh = new Mesh
			{
				name = "CorrespondenceMesh",
				hideFlags = HideFlags.HideAndDontSave
			};

			EnsureBuiltInRendererIfNeeded();
		}

		static void EnsureBuiltInRendererIfNeeded()
		{
			if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
				return;

			var cam = Camera.main;
			if (cam != null && cam.GetComponent<CorrespondenceBuiltIn>() == null)
				cam.gameObject.AddComponent<CorrespondenceBuiltIn>();
		}

		void OnDisable()
		{
			s_instances.Remove(this);

			if (_mesh != null)
				DestroyImmediate(_mesh);
			_mesh = null;
		}

		void LateUpdate()
		{
			if (_trackedBody == null || string.IsNullOrEmpty(_trackedBody.BodyId))
				return;

			var manager = XRTrackerManager.Instance;
			if (manager == null || manager.CameraTransform == null)
				return;

			Transform camTransform = manager.CameraTransform;
			string bodyId = _trackedBody.BodyId;

			_vertices.Clear();
			_colors.Clear();
			_indices.Clear();

			if (_showRegion)
			{
				int regionCount = FTBridge.FT_GetRegionCorrespondences(bodyId, _regionBuffer, MAX_CORRESPONDENCES);
				AppendLines(_regionBuffer, regionCount, camTransform);
			}

			if (_showDepth)
			{
				int depthCount = FTBridge.FT_GetDepthCorrespondences(bodyId, _depthBuffer, MAX_CORRESPONDENCES);
				AppendLines(_depthBuffer, depthCount, camTransform);
			}

			if (_showEdge)
			{
				int edgeCount = FTBridge.FT_GetEdgeCorrespondences(bodyId, _edgeBuffer, MAX_CORRESPONDENCES);
				AppendLines(_edgeBuffer, edgeCount, camTransform);
			}

			if (_showModelEdges)
			{
				int modelEdgeCount = FTBridge.FT_GetEdgeModelLines(bodyId, _modelEdgeBuffer, _modelEdgeBuffer.Length);
				AppendModelEdgeLines(_modelEdgeBuffer, modelEdgeCount, camTransform);
			}

			if (_showTexture)
			{
				int textureCount = FTBridge.FT_GetTextureCorrespondences(bodyId, _textureBuffer, MAX_CORRESPONDENCES);
				AppendLines(_textureBuffer, textureCount, camTransform);
			}

			_mesh.Clear();

			if (_vertices.Count == 0) return;

			_mesh.SetVertices(_vertices);
			_mesh.SetColors(_colors);
			_mesh.SetIndices(_indices, MeshTopology.Lines, 0);
		}

		static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
		static bool IsLineValid(ref FTCorrespondenceLine line) =>
			IsFinite(line.start_x) && IsFinite(line.start_y) && IsFinite(line.start_z) &&
			IsFinite(line.end_x) && IsFinite(line.end_y) && IsFinite(line.end_z);

		void AppendLines(FTCorrespondenceLine[] buffer, int count, Transform camTransform)
		{
			for (int i = 0; i < count; i++)
			{
				ref FTCorrespondenceLine line = ref buffer[i];

				if (!IsLineValid(ref line)) continue;

				// M3T camera space (Y-down) to Unity camera space (Y-up): flip Y
				Vector3 startCam = new Vector3(line.start_x, -line.start_y, line.start_z);
				Vector3 endCam = new Vector3(line.end_x, -line.end_y, line.end_z);

				// Camera-local to world
				Vector3 startWorld = camTransform.TransformPoint(startCam);
				Vector3 endWorld = camTransform.TransformPoint(endCam);

				Color color = Color.Lerp(_badColor, _goodColor, line.quality);

				int baseIdx = _vertices.Count;
				_vertices.Add(startWorld);
				_vertices.Add(endWorld);
				_colors.Add(color);
				_colors.Add(color);
				_indices.Add(baseIdx);
				_indices.Add(baseIdx + 1);
			}
		}

		void AppendModelEdgeLines(FTCorrespondenceLine[] buffer, int count, Transform camTransform)
		{
			for (int i = 0; i < count; i++)
			{
				ref FTCorrespondenceLine line = ref buffer[i];

				if (!IsLineValid(ref line)) continue;

				Vector3 startCam = new Vector3(line.start_x, -line.start_y, line.start_z);
				Vector3 endCam = new Vector3(line.end_x, -line.end_y, line.end_z);

				Vector3 startWorld = camTransform.TransformPoint(startCam);
				Vector3 endWorld = camTransform.TransformPoint(endCam);

				Color color = line.quality < 0f ? _deadSiteColor
					: line.quality < 0.5f ? _normalColor
					: line.quality > 0.75f ? _creaseEdgeColor : _silhouetteEdgeColor;

				int baseIdx = _vertices.Count;
				_vertices.Add(startWorld);
				_vertices.Add(endWorld);
				_colors.Add(color);
				_colors.Add(color);
				_indices.Add(baseIdx);
				_indices.Add(baseIdx + 1);
			}
		}
	}
}
