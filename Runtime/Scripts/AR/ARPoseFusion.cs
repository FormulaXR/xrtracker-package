#if HAS_AR_FOUNDATION
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Handles AR Foundation integration for pose recovery.
	/// Encapsulates AR session detection, reliability checking, and pose conversion.
	/// </summary>
	internal class ARPoseFusion
	{
		private ARSession _arSession;
		private Transform _cameraTransform;

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
		/// Detect AR Foundation availability by finding ARSession in scene.
		/// </summary>
		/// <returns>True if AR Foundation is available</returns>
		public bool Detect()
		{
			_arSession = Object.FindAnyObjectByType<ARSession>();
			IsAvailable = _arSession != null && _arSession.enabled;
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
