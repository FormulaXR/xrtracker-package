#if HAS_AR_FOUNDATION
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Handles AR Foundation integration for pose recovery.
	/// Encapsulates AR session detection, reliability checking, anchor management, and pose conversion.
	/// </summary>
	internal class ARPoseFusion
	{
		private ARSession _arSession;
		private ARAnchorManager _arAnchorManager;
		private ARRaycastManager _arRaycastManager;
		private Transform _cameraTransform;

		private ARAnchor _bodyAnchor;
		private bool _anchorRequestPending;
		private bool _anchorWarningLogged;
		private bool _raycastManagerInitialized;
		private bool _hasRaycastManager;

		private readonly List<ARRaycastHit> _raycastHits = new();
		private bool _listeningToAnchorEvents;
		private bool _anchorUpdatedThisFrame;

		/// <summary>
		/// Whether AR Foundation is available (ARSession exists and enabled).
		/// </summary>
		public bool IsAvailable { get; private set; }

		/// <summary>
		/// Whether AR tracking is currently reliable (session is tracking properly).
		/// Returns false if session is not in SessionTracking state.
		/// </summary>
		public bool IsReliable => IsAvailable && ARSession.state == ARSessionState.SessionTracking && ARSession.notTrackingReason == NotTrackingReason.None;

		/// <summary>
		/// Current AR session state (for debugging/logging).
		/// </summary>
		public ARSessionState SessionState => ARSession.state;

		/// <summary>
		/// Gets the current reason why AR is not tracking (for debugging/logging).
		/// Note: Only valid when SessionState != SessionTracking.
		/// </summary>
		public NotTrackingReason NotTrackingReason => ARSession.notTrackingReason;

		/// <summary>
		/// Camera transform used for pose conversion. Set via SetCameraTransform.
		/// </summary>
		public Transform CameraTransform => _cameraTransform;

		/// <summary>
		/// The session anchor that defines the reference frame for pose fusion. Null until first nice-quality body lock.
		/// </summary>
		public ARAnchor BodyAnchor => _bodyAnchor;

		/// <summary>
		/// True when an anchor exists and ARKit is currently tracking it.
		/// Phase 2 (anchored) tracking is gated on this. When false, fall back to Phase 1 (world-origin) behavior.
		/// </summary>
		public bool HasReliableAnchor => _bodyAnchor != null && _bodyAnchor.trackingState == TrackingState.Tracking;

		/// <summary>
		/// True when ARKit updated the session anchor this frame (bundle adjustment).
		/// Display path should snap instead of lerping.
		/// </summary>
		public bool AnchorUpdatedThisFrame => _anchorUpdatedThisFrame;

		/// <summary>
		/// Clear the per-frame anchor update flag. Call after display poses have been applied.
		/// </summary>
		public void ClearAnchorUpdatedFlag() => _anchorUpdatedThisFrame = false;

		/// <summary>
		/// Detect AR Foundation availability by finding ARSession in scene. Also caches the ARAnchorManager if present.
		/// </summary>
		public bool Detect()
		{
			_arSession = Object.FindAnyObjectByType<ARSession>();
			IsAvailable = _arSession != null && _arSession.enabled;
			_arAnchorManager = Object.FindAnyObjectByType<ARAnchorManager>();
			_arRaycastManager = Object.FindAnyObjectByType<ARRaycastManager>();
			return IsAvailable;
		}

		/// <summary>
		/// Set the camera transform used for pose conversions.
		/// </summary>
		public void SetCameraTransform(Transform cameraTransform)
		{
			_cameraTransform = cameraTransform;
		}

		/// <summary>
		/// Request creation of the session anchor at the given world pose.
		/// Fire-and-forget — uses ARFoundation's async API. Caller polls HasReliableAnchor to detect completion.
		/// No-op if anchor already exists, request already pending, or no ARAnchorManager in scene.
		/// </summary>
		public void RequestBodyAnchor(Vector3 boundsCenter, Quaternion worldRotation, Bounds localBounds, Transform bodyTransform)
		{
			if (_bodyAnchor != null || _anchorRequestPending)
				return;

			if (_arAnchorManager == null || !_arAnchorManager.enabled)
			{
				if (!_anchorWarningLogged)
				{
					Debug.LogWarning("[ARPoseFusion] No enabled ARAnchorManager found in scene. Anchor-based pose fusion disabled — running in legacy world-origin mode. Add an ARAnchorManager component to XR Origin to enable.");
					_anchorWarningLogged = true;
				}
				return;
			}

			if (!_raycastManagerInitialized)
			{
				_arRaycastManager = Object.FindAnyObjectByType<ARRaycastManager>();
				_hasRaycastManager = _arRaycastManager != null;
				_raycastManagerInitialized = true;
			}

			var anchorPose = FindBestAnchorPose(boundsCenter, localBounds, bodyTransform);
			_anchorRequestPending = true;
			_ = CreateAnchorAsync(anchorPose.position, anchorPose.rotation);
		}

		/// <summary>
		/// Raycast downward from the body's bounds center to find a surface below (table, floor, workbench).
		/// These surfaces tend to have better ARKit features than the object itself.
		/// Falls back to bounds center if no hit.
		/// </summary>
		private Pose FindBestAnchorPose(Vector3 boundsCenter, Bounds localBounds, Transform bodyTransform)
		{
			var fallback = new Pose(boundsCenter, Quaternion.identity);

			if (!_hasRaycastManager)
			{
				Debug.Log("[ARPoseFusion] No ARRaycastManager — anchor at bounds center");
				return fallback;
			}

			if (_arRaycastManager.Raycast(new Ray(boundsCenter, Vector3.down), _raycastHits, TrackableType.AllTypes)
				&& _raycastHits.Count > 0)
			{
				var hit = _raycastHits[0];
				Debug.Log($"[ARPoseFusion] Anchor at surface below body {hit.pose.position} (dist={hit.distance:F3}m)");
				return hit.pose;
			}

			Debug.Log("[ARPoseFusion] No surface below body — anchor at bounds center");
			return fallback;
		}

		private async Awaitable CreateAnchorAsync(Vector3 worldPosition, Quaternion worldRotation)
		{
			try
			{
				var result = await _arAnchorManager.TryAddAnchorAsync(new Pose(worldPosition, worldRotation));
				if (result.status.IsSuccess() && result.value != null)
				{
					_bodyAnchor = result.value;
					Debug.Log($"[ARPoseFusion] Session anchor created at {worldPosition}");
					// AttachDebugMarker(_bodyAnchor.transform);
					SubscribeToAnchorEvents();
				}
				else
				{
					Debug.LogWarning($"[ARPoseFusion] Anchor creation failed: status={result.status}");
				}
			}
			finally
			{
				_anchorRequestPending = false;
			}
		}

		private static void AttachDebugMarker(Transform anchorTransform)
		{
			var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
			sphere.name = "ARPoseFusion_AnchorDebug";
			sphere.transform.SetParent(anchorTransform, worldPositionStays: false);
			sphere.transform.localPosition = Vector3.zero;
			sphere.transform.localRotation = Quaternion.identity;
			sphere.transform.localScale = Vector3.one * 0.01f; // 1 cm

			var collider = sphere.GetComponent<Collider>();
			if (collider != null)
				Object.Destroy(collider);

			var renderer = sphere.GetComponent<MeshRenderer>();
			var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
			if (shader != null)
				renderer.material = new Material(shader) { color = Color.red };
			else
				renderer.material.color = Color.red;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
		}

		private void SubscribeToAnchorEvents()
		{
			if (_listeningToAnchorEvents || _arAnchorManager == null)
				return;
			_arAnchorManager.trackablesChanged.AddListener(OnAnchorsChanged);
			_listeningToAnchorEvents = true;
		}

		private void UnsubscribeFromAnchorEvents()
		{
			if (!_listeningToAnchorEvents || _arAnchorManager == null)
				return;
			_arAnchorManager.trackablesChanged.RemoveListener(OnAnchorsChanged);
			_listeningToAnchorEvents = false;
		}

		private void OnAnchorsChanged(ARTrackablesChangedEventArgs<ARAnchor> args)
		{
			if (_bodyAnchor == null)
				return;

			foreach (var anchor in args.updated)
			{
				if (anchor != _bodyAnchor)
					continue;

				_anchorUpdatedThisFrame = true;
				return;
			}
		}

		/// <summary>
		/// Destroy the session anchor and reset all anchor state.
		/// Called on tracking reset so the system returns to Phase 1 (world-origin).
		/// </summary>
		public void DestroyAnchor()
		{
			UnsubscribeFromAnchorEvents();
			if (_bodyAnchor != null)
			{
				Debug.Log("[ARPoseFusion] Destroying session anchor (tracking reset)");
				Object.Destroy(_bodyAnchor.gameObject);
				_bodyAnchor = null;
			}
			_anchorRequestPending = false;
		}

		/// <summary>
		/// Compute camera-in-anchor-space matrix. Returns false if no reliable anchor (caller should fall back to camera2world).
		/// </summary>
		public bool TryGetCameraInAnchorSpace(Transform cameraTransform, out Matrix4x4 cameraInAnchor)
		{
			if (!HasReliableAnchor || cameraTransform == null)
			{
				cameraInAnchor = Matrix4x4.identity;
				return false;
			}
			cameraInAnchor = _bodyAnchor.transform.worldToLocalMatrix * cameraTransform.localToWorldMatrix;
			return true;
		}

		/// <summary>
		/// Convert a world-space pose to anchor-space. Used at one-shot switchover to compute initial body2anchor.
		/// </summary>
		public bool TryWorldToAnchor(Vector3 worldPos, Quaternion worldRot, out Vector3 anchorPos, out Quaternion anchorRot)
		{
			if (!HasReliableAnchor)
			{
				anchorPos = Vector3.zero;
				anchorRot = Quaternion.identity;
				return false;
			}
			var anchorTf = _bodyAnchor.transform;
			anchorPos = anchorTf.InverseTransformPoint(worldPos);
			anchorRot = Quaternion.Inverse(anchorTf.rotation) * worldRot;
			return true;
		}

		/// <summary>
		/// Convert an anchor-space pose to world-space. Used by display path so the body follows ARKit feature corrections.
		/// </summary>
		public Pose AnchorToWorld(Vector3 anchorPos, Quaternion anchorRot)
		{
			if (_bodyAnchor == null)
				return new Pose(anchorPos, anchorRot);
			var anchorTf = _bodyAnchor.transform;
			return new Pose(
				anchorTf.TransformPoint(anchorPos),
				anchorTf.rotation * anchorRot);
		}

		/// <summary>
		/// Convert world pose to camera-relative pose.
		/// </summary>
		public void WorldToCameraRelative(Vector3 worldPosition, Quaternion worldRotation,
			out Vector3 cameraRelativePosition, out Quaternion cameraRelativeRotation)
		{
			cameraRelativePosition = _cameraTransform.InverseTransformPoint(worldPosition);
			cameraRelativeRotation = Quaternion.Inverse(_cameraTransform.rotation) * worldRotation;
		}

		/// <summary>
		/// Apply stored world pose to body for rendering.
		/// Note: Pose is fed to native tracker in OnBeforeTrackingStep for optimal timing.
		/// </summary>
		/// <param name="body">The tracked body to update</param>
		/// <param name="storedWorldPosition">Stored world position from good tracking</param>
		/// <param name="storedWorldRotation">Stored world rotation from good tracking</param>
		public void ApplyStoredPose(TrackedBody body, Vector3 storedWorldPosition, Quaternion storedWorldRotation)
		{
			// Apply world pose via TrackedBody (for Unity rendering)
			body.SetWorldPose(storedWorldPosition, storedWorldRotation);
		}
	}
}
#endif
