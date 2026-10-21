using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// A body that only contributes to occlusion rendering and is not tracked.
	/// Use it for hands, tools, scene geometry, or parts that are being inserted but are not yet validated.
	/// It is much simpler than a TrackedBody, because it has no tracking and no silhouette model, only a mesh and a pose.
	/// Unity sends the pose to the native plugin before each tracking step.
	/// </summary>
	public class OccluderBody : MonoBehaviour
	{
		[SerializeField]
		[Tooltip("Unique identifier for this occluder body. Adding the component fills it with the GameObject name. Registration uses the value as it is, so give every occluder body its own identifier.")]
		private string _bodyId;

		[SerializeField]
		[Tooltip("Mesh filters to use for occlusion. Adding the component collects the mesh filters of its children, and the Add Child Meshes context menu entry collects them again. Registration fails with an error while the list is empty.")]
		private List<MeshFilter> _meshFilters = new();

		[SerializeField]
		[Tooltip("Size of one mesh unit in meters, for example 0.001 for a mesh modeled in millimeters.")]
		private float _geometryUnitInMeter = 1;

		// Registration state
		private bool _isRegistered;
		private bool _nativePoseWritePending;
		private CombinedMeshData _meshData;
		private XRTrackerManager _stepSource;

		/// <summary>
		/// Unique identifier for this occluder body.
		/// </summary>
		public string BodyId => _bodyId;

		/// <summary>
		/// Whether this occluder body is registered with the native tracker.
		/// </summary>
		public bool IsRegistered => _isRegistered;

		/// <summary>True when the last out-of-frame native pose write was refused.</summary>
		internal bool NativePoseWritePending => _nativePoseWritePending;

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

		// The pose is written in the window before a step, where no step runs on the worker thread. A write from LateUpdate would land
		// while the previous frame's step is still reading the occluder.
		private void OnBeforeTrackingStep()
		{
			if (!_isRegistered) return;

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
				Debug.LogError($"[OccluderBody] {_bodyId} could not register ({error}).");
				return;
			}

			// Combine meshes
			_meshData = MeshCombiner.Combine(_meshFilters, transform);

			// Register with native
			XRTrackerManager.WaitForTrackingStepToReturn();
			int result = FTBridge.FT_RegisterOccluderBody(
				_bodyId,
				_meshData.Vertices.ToArray(),
				_meshData.VertexCount,
				_meshData.Triangles.ToArray(),
				_meshData.TriangleCount,
				_geometryUnitInMeter * transform.lossyScale.x);

			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"[OccluderBody] {_bodyId} could not register (error {result}).");
				_meshData.Dispose();
				return;
			}

			_isRegistered = true;
			Debug.Log($"[OccluderBody] {_bodyId} registered.");

			// Set initial pose
			UpdateNativePose();

			_stepSource = XRTrackerManager.Instance;
			if (_stepSource != null)
				_stepSource.OnBeforeTrackingStep += OnBeforeTrackingStep;
		}

		/// <summary>
		/// Unregister this occluder body from the native tracker.
		/// </summary>
		public void UnregisterBody()
		{
			if (!_isRegistered) return;

			if (_stepSource != null)
				_stepSource.OnBeforeTrackingStep -= OnBeforeTrackingStep;
			_stepSource = null;

			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_UnregisterOccluderBody(_bodyId);

			if (_meshData.Vertices.IsCreated)
				_meshData.Dispose();

			_isRegistered = false;
			Debug.Log($"[OccluderBody] {_bodyId} unregistered.");
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

			// The write is not part of the frame decision, so a failure does not drop
			// the step. It stays pending and the next step's write retries it.
			_nativePoseWritePending = !FrameWriteGuard.RecordOutOfFrame(
				FTBridge.FT_SetOccluderPose(_bodyId, ref pose), "FT_SetOccluderPose");
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
