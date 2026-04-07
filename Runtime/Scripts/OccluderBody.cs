using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// A body that only contributes to occlusion rendering (not tracked).
	/// Use for hands, tools, scene geometry, or parts being inserted but not yet validated.
	/// Much simpler than TrackedBody - no tracking, no silhouette model, just mesh + pose.
	/// Unity sends world pose to C++ each frame.
	/// </summary>
	public class OccluderBody : MonoBehaviour
	{
		[SerializeField]
		[Tooltip("Unique identifier for this occluder body. Auto-generated from GameObject name if empty.")]
		private string _bodyId;

		[SerializeField]
		[Tooltip("Mesh filters to use for occlusion. If empty, will auto-collect from children.")]
		private List<MeshFilter> _meshFilters = new();

		[SerializeField]
		[Tooltip("Scale factor for geometry (e.g., 0.001 for mm meshes).")]
		private float _geometryUnitInMeter = 1;

		// Registration state
		private bool _isRegistered;
		private CombinedMeshData _meshData;

		/// <summary>
		/// Unique identifier for this occluder body.
		/// </summary>
		public string BodyId => _bodyId;

		/// <summary>
		/// Whether this occluder body is registered with the native tracker.
		/// </summary>
		public bool IsRegistered => _isRegistered;

		/// <summary>
		/// The mesh filters used by this occluder body.
		/// </summary>
		public IReadOnlyList<MeshFilter> MeshFilters => _meshFilters;

		#region Unity Lifecycle

		private void Reset()
		{
			AddChildMeshes();
			_bodyId = gameObject.name;
		}

		private void OnEnable()
		{
			// Auto-register if manager is ready, otherwise wait for initialization
			if (XRTrackerManager.Instance != null && XRTrackerManager.Instance.IsInitialized)
			{
				RegisterBody();
			}
			else
			{
				XRTrackerManager.OnTrackerInitialized += OnTrackerInitialized;
			}
		}

		private void OnDisable()
		{
			XRTrackerManager.OnTrackerInitialized -= OnTrackerInitialized;
			UnregisterBody();
		}

		private void OnTrackerInitialized()
		{
			XRTrackerManager.OnTrackerInitialized -= OnTrackerInitialized;
			RegisterBody();
		}

		private void LateUpdate()
		{
			if (!_isRegistered) return;

			// Update pose each frame
			UpdateNativePose();
		}

		#endregion

		#region Registration

		/// <summary>
		/// Register this occluder body with the native tracker.
		/// Called automatically in OnEnable if manager is ready.
		/// </summary>
		public void RegisterBody()
		{
			if (_isRegistered) return;

			if (!MeshCombiner.Validate(_meshFilters, out string error))
			{
				Debug.LogError($"[OccluderBody] {_bodyId}: {error}");
				return;
			}

			// Combine meshes
			_meshData = MeshCombiner.Combine(_meshFilters, transform);

			// Register with native
			int result = FTBridge.FT_RegisterOccluderBody(
				_bodyId,
				_meshData.Vertices.ToArray(),
				_meshData.VertexCount,
				_meshData.Triangles.ToArray(),
				_meshData.TriangleCount,
				_geometryUnitInMeter * transform.lossyScale.x);

			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"[OccluderBody] {_bodyId}: Registration failed with error {result}");
				_meshData.Dispose();
				return;
			}

			_isRegistered = true;
			Debug.Log($"[OccluderBody] {_bodyId}: Registered");

			// Set initial pose
			UpdateNativePose();
		}

		/// <summary>
		/// Unregister this occluder body from the native tracker.
		/// </summary>
		public void UnregisterBody()
		{
			if (!_isRegistered) return;

			FTBridge.FT_UnregisterOccluderBody(_bodyId);

			if (_meshData.Vertices.IsCreated)
				_meshData.Dispose();

			_isRegistered = false;
			Debug.Log($"[OccluderBody] {_bodyId}: Unregistered");
		}

		#endregion

		#region Pose Management

		/// <summary>
		/// Update world pose in C++.
		/// </summary>
		private void UpdateNativePose()
		{
			var cameraTransform = XRTrackerManager.Instance?.CameraTransform;
			if (cameraTransform == null)
			{
				cameraTransform = Camera.main?.transform;
				if (cameraTransform == null) return;
			}

			// Compute camera-relative pose (same as TrackedBody)
			Vector3 cameraRelativePos = cameraTransform.InverseTransformPoint(transform.position);
			Quaternion cameraRelativeRot = Quaternion.Inverse(cameraTransform.rotation) * transform.rotation;

			// Apply native tracker coordinate conversion (Y-flip)
			var pose = new FTTrackingPose
			{
				pos_x = cameraRelativePos.x,
				pos_y = -cameraRelativePos.y,
				pos_z = cameraRelativePos.z,
				rot_x = cameraRelativeRot.x,
				rot_y = -cameraRelativeRot.y,
				rot_z = cameraRelativeRot.z,
				rot_w = -cameraRelativeRot.w
			};

			FTBridge.FT_SetOccluderPose(_bodyId, ref pose);
		}

		#endregion

		#region Mesh Management

		/// <summary>
		/// Add all MeshFilters from children (excluding those under other OccluderBodies or TrackedBodies).
		/// </summary>
		[ContextMenu("Add Child Meshes")]
		private void AddChildMeshes()
		{
			_meshFilters.Clear();

			var allMeshFilters = GetComponentsInChildren<MeshFilter>(true);
			var childOccluders = GetComponentsInChildren<OccluderBody>(true);
			var childTrackedBodies = GetComponentsInChildren<TrackedBody>(true);

			foreach (var meshFilter in allMeshFilters)
			{
				// Check if under another OccluderBody
				bool isUnderChildOccluder = false;
				foreach (var childOccluder in childOccluders)
				{
					if (childOccluder == this) continue;
					if (meshFilter.transform.IsChildOf(childOccluder.transform))
					{
						isUnderChildOccluder = true;
						break;
					}
				}

				// Check if under a TrackedBody
				bool isUnderTrackedBody = false;
				foreach (var trackedBody in childTrackedBodies)
				{
					if (meshFilter.transform.IsChildOf(trackedBody.transform))
					{
						isUnderTrackedBody = true;
						break;
					}
				}

				if (!isUnderChildOccluder && !isUnderTrackedBody)
				{
					_meshFilters.Add(meshFilter);
				}
			}
		}

		/// <summary>
		/// Compute local bounds of all meshes.
		/// </summary>
		public Bounds ComputeLocalBounds()
		{
			if (_meshFilters == null || _meshFilters.Count == 0)
				return new Bounds(Vector3.zero, Vector3.zero);

			Bounds combinedBounds = new Bounds();
			bool first = true;

			foreach (var mf in _meshFilters)
			{
				if (mf == null || mf.sharedMesh == null) continue;

				var mesh = mf.sharedMesh;
				var localMatrix = GetLocalTransformRelativeTo(mf.transform, transform);

				foreach (var vertex in mesh.vertices)
				{
					Vector3 localPoint = localMatrix.MultiplyPoint3x4(vertex);
					if (first)
					{
						combinedBounds = new Bounds(localPoint, Vector3.zero);
						first = false;
					}
					else
					{
						combinedBounds.Encapsulate(localPoint);
					}
				}
			}

			return combinedBounds;
		}

		private static Matrix4x4 GetLocalTransformRelativeTo(Transform child, Transform parent)
		{
			return parent.worldToLocalMatrix * child.localToWorldMatrix;
		}

		#endregion
	}
}
