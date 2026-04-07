using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Manages tracking state and pose application for all TrackedBodies.
	/// Handles stationary mode: SLAM-anchored with tracker corrections.
	/// This is the single point of control for start/stop/reset decisions.
	/// </summary>
	[DefaultExecutionOrder(-1)] // Before TrackedBody
	public class TrackedBodyManager : MonoBehaviour
	{
		#region Singleton

		private static TrackedBodyManager _instance;
		public static TrackedBodyManager Instance => _instance;

		#endregion

		#region Private Types

		private class BodyState
		{
			// Quality-based state machine (used for all modes)
			public int BadFrameCount { get; set; }
			public int GoodFrameCount { get; set; }
			public bool WasTracking { get; set; }
			public int FramesSinceTrackingStarted { get; set; }
		}

		#endregion

		#region Private Fields

		private readonly Dictionary<TrackedBody, BodyState> _bodyStates = new();
		private readonly List<TrackedBody> _bodiesToRemove = new();
#if HAS_AR_FOUNDATION
		private readonly ARPoseFusion _arPoseFusion = new();
#endif

		// Global adaptive histogram peak (shared across all bodies)
		private float _globalHistogramPeak = TrackerDefaults.HISTOGRAM_GOOD;

		// License freeze handling
		private bool _wasLicenseFrozen;

		#endregion

		#region Public Properties

		/// <summary>
		/// Whether AR pose fusion is available and enabled.
		/// </summary>
#if HAS_AR_FOUNDATION
		public bool IsARPoseFusionActive => XRTrackerManager.Instance?.UseARPoseFusion == true && _arPoseFusion.IsAvailable;
#else
		public bool IsARPoseFusionActive => false;
#endif

		/// <summary>
		/// Number of bodies currently being managed.
		/// </summary>
		public int ManagedBodyCount => _bodyStates.Count;

		/// <summary>
		/// Global histogram peak observed across all root bodies. Used as reference for quality calculations.
		/// Adapts to lighting conditions. Clamped between PEAK_MIN and PEAK_MAX.
		/// Resets to HISTOGRAM_GOOD when no root body is tracking.
		/// </summary>
		public float GlobalHistogramPeak => _globalHistogramPeak;

		/// <summary>
		/// Number of bodies currently in AR mode (Anchored or Recovery phase).
		/// </summary>
		public int BodiesInARMode
		{
			get
			{
				int count = 0;
				foreach (var (body, _) in _bodyStates)
					if (body.IsStationary && IsARPoseFusionActive)
						count++;
				return count;
			}
		}

		#endregion

		#region Unity Lifecycle

		private void Awake()
		{
			if (_instance != null && _instance != this)
			{
				Destroy(this);
				return;
			}

			_instance = this;
		}

		private void Start()
		{
#if HAS_AR_FOUNDATION
			_arPoseFusion.Detect();
#endif
			SubscribeToTrackingManager();
		}

		private void OnEnable()
		{
			Application.onBeforeRender += OnBeforeRender;
			SubscribeToTrackingManager();
		}

		private void OnDisable()
		{
			Application.onBeforeRender -= OnBeforeRender;
			UnsubscribeFromTrackingManager();
		}

		private void SubscribeToTrackingManager()
		{
			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				manager.OnAfterTrackingStep -= OnAfterTrackingStep;
				manager.OnAfterTrackingStep += OnAfterTrackingStep;
			}
		}

		private void UnsubscribeFromTrackingManager()
		{
			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				manager.OnAfterTrackingStep -= OnAfterTrackingStep;
			}
		}

		/// <summary>
		/// Called immediately after native tracking step.
		/// Applies poses for non-AR bodies (Off and Tracker modes when AR Foundation not active).
		/// </summary>
		private void OnAfterTrackingStep()
		{
			if (IsARPoseFusionActive)
				return;

			// Non-AR: apply poses immediately after tracking
			ApplyPosesNonAR();
		}

		/// <summary>
		/// Apply native poses for all active mode bodies when AR Foundation is not active.
		/// Stationary modes fall back to standard pose application without AR Foundation.
		/// </summary>
		private void ApplyPosesNonAR()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;

			var trackedBodies = manager.TrackedBodies;
			if (trackedBodies == null)
				return;

			foreach (var body in trackedBodies)
			{
				if (!body.IsRegistered || !body.IsActiveMode)
					continue;

				ApplyNativePose(body);
			}
		}

		private void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
		}

		/// <summary>
		/// Handles state machine and pose application for all tracked bodies.
		/// Runs at order 100 (same as TrackedBody, but registered later so runs after).
		/// </summary>
		[BeforeRenderOrder(100)]
		private void OnBeforeRender()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;

			// License freeze: log once (junk poses from native will cause visible degradation)
			if (manager.IsLicenseFrozen && !_wasLicenseFrozen)
			{
				_wasLicenseFrozen = true;
				Debug.LogWarning("[XRTracker] Free license time limit reached. Tracking degraded. Upgrade to continue tracking.");
			}

#if HAS_AR_FOUNDATION
			if (_arPoseFusion.CameraTransform == null && _arPoseFusion.IsAvailable)
			{
				_arPoseFusion.SetCameraTransform(manager.CameraTransform);
			}
#endif

			IReadOnlyList<TrackedBody> trackedBodies = manager.TrackedBodies;
			if (trackedBodies == null)
				return;

			foreach (TrackedBody body in trackedBodies)
			{
				if (body == null || !body.IsRegistered)
				{
					_bodiesToRemove.Add(body);
					continue;
				}

				// Get or create state
				if (!_bodyStates.TryGetValue(body, out var state))
				{
					state = new BodyState();
					_bodyStates[body] = state;
				}

				// Process based on mode
				if (body.IsActiveMode)
					ProcessMainBody(body, state);
				else
					ProcessChildBody(body, state);
			}

			// Clean up
			foreach (TrackedBody body in _bodiesToRemove)
				_bodyStates.Remove(body);
			_bodiesToRemove.Clear();
		}

		#endregion

		#region Active Mode Processing

		private void ProcessMainBody(TrackedBody body, BodyState state)
		{
			float quality = body.TrackingQuality;
			UpdateTrackingStatus(body, quality);

			if (body.IsStationary && IsARPoseFusionActive)
			{
				ProcessStationary(body, state, quality);
			}
			else
			{
				// Non-stationary, or stationary without AR Foundation (fallback)
				ProcessBodyNoAR(body, state, quality);
				if (IsARPoseFusionActive)
					ApplyNativePose(body);
			}

			FireEventsIfChanged(body, state);
		}

		/// <summary>
		/// Standard state machine with frame hysteresis (for Off and Tracker modes).
		/// Pose is applied via OnAfterTrackingStep (non-AR) or in ProcessActiveBody (when AR active).
		/// </summary>
		private void ProcessBodyNoAR(TrackedBody body, BodyState state, float quality)
		{
			if (body.IsTracking)
			{
				state.FramesSinceTrackingStarted++;

				if (state.FramesSinceTrackingStarted > TrackerDefaults.STABILIZATION_FRAMES)
				{
					float loseThreshold = body.QualityToStopTracking;
					if (CheckFrameThreshold(state, quality < loseThreshold, TrackerDefaults.FRAMES_TO_LOSE, trackGoodFrames: false))
						ResetTracking(body, state);
				}
			}
			else
			{
				TryStartTracking(body, state, quality);
			}
		}

#if HAS_AR_FOUNDATION
		/// <summary>
		/// Stationary mode with world-space Tikhonov prior.
		/// The C++ optimizer handles world-space stability via camera2world_pose.
		/// C# side applies the pose with smooth lerp and handles start/stop tracking.
		/// </summary>
		private void ProcessStationary(TrackedBody body, BodyState state, float quality)
		{
			// Not yet tracking — show detector pose while waiting
			if (!body.IsTracking)
			{
				ApplyNativePose(body);
				TryStartTracking(body, state, quality);
				return;
			}

			state.FramesSinceTrackingStarted++;

			if (!GetNativeWorldPose(body, out Vector3 nativePos, out Quaternion nativeRot))
				return;

			// Smooth lerp toward native world-space pose (Tikhonov keeps it stable on C++ side)
			float alpha = ComputeLerpAlpha(body.SmoothTime);
			body.SetWorldPose(
				Vector3.Lerp(body.transform.position, nativePos, alpha),
				Quaternion.Slerp(body.transform.rotation, nativeRot, alpha));

			// Only reset when AR SLAM itself is unreliable (e.g. camera covered, excessive motion).
			// Object leaving camera view is fine — Tikhonov holds the world pose until it re-enters.
			if (!_arPoseFusion.IsReliable)
				ResetTracking(body, state);
		}
#else
		private void ProcessStationary(TrackedBody body, BodyState state, float quality)
		{
			// AR Foundation not available — fall back to standard processing
			ProcessBodyNoAR(body, state, quality);
		}
#endif

		/// <summary>
		/// Attempts to start tracking based on quality threshold.
		/// </summary>
		private void TryStartTracking(TrackedBody body, BodyState state, float quality)
		{
			if (CheckFrameThreshold(state, quality >= body.QualityToStartTracking, TrackerDefaults.FRAMES_TO_START, trackGoodFrames: true))
				StartTracking(body, state);
		}

		#endregion

		#region Rigid Child Processing

		private void ProcessChildBody(TrackedBody body, BodyState state)
		{
			float quality = body.TrackingQuality;
			UpdateTrackingStatus(body, quality);

			// Update tracking state based on parent
			bool parentTracking = body.ParentBody != null && body.ParentBody.IsTracking;
			if (body.IsTracking != parentTracking)
			{
				body.SetTrackingState(parentTracking);
			}

			// Assembly mode: child won't affect parent pose until quality confirms part is present
			if (body.AssemblyMode)
			{
				bool shouldContribute = quality >= body.AssemblyQualityToConfirm;
				body.SetContributesToOptimization(shouldContribute);
			}

			FireEventsIfChanged(body, state);
		}

		#endregion

		#region State Control Methods

		private void StartTracking(TrackedBody body, BodyState state)
		{
			body.SetTrackingState(true);
			FTBridge.FT_StartTrackingBody(body.BodyId);
			ResetCounters(state);
			state.FramesSinceTrackingStarted = 0;
		}

		private void ResetTracking(TrackedBody body, BodyState state)
		{
			ResetCounters(state);
			ResetBodyInternal(body);
		}

		#endregion

		#region Pose Methods

		/// <summary>
		/// Apply native tracker pose. When AR pose fusion is active, pose is already world-space.
		/// Otherwise, converts from camera-relative to world.
		/// </summary>
		private void ApplyNativePose(TrackedBody body)
		{
			if (GetNativeWorldPose(body, out Vector3 worldPos, out Quaternion worldRot))
				body.SetWorldPose(worldPos, worldRot);
		}

		/// <summary>
		/// Get native tracker pose in world space.
		/// When AR pose fusion is active, the native tracker outputs world-space poses directly.
		/// Otherwise, converts from camera-relative to world.
		/// </summary>
		private bool GetNativeWorldPose(TrackedBody body, out Vector3 worldPos, out Quaternion worldRot)
		{
			if (FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose pose))
			{
				pose.GetTransformation(out Vector3 pos, out Quaternion rot);

				if (IsARPoseFusionActive)
				{
					// Native tracker already outputs world-space poses
					worldPos = pos;
					worldRot = rot;
				}
				else
				{
					var cam = XRTrackerManager.Instance.CameraTransform;
					worldPos = cam.TransformPoint(pos);
					worldRot = cam.rotation * rot;
				}
				return true;
			}

			worldPos = Vector3.zero;
			worldRot = Quaternion.identity;
			return false;
		}

		/// <summary>
		/// Compute exponential lerp alpha from transition time in seconds.
		/// 0 = instant (alpha=1). At t = transitionTime, reaches ~63%. At 3x, reaches ~95%.
		/// </summary>
		private static float ComputeLerpAlpha(float transitionTime)
		{
			if (transitionTime <= 0f)
				return 1f;

			return 1f - Mathf.Exp(-Time.deltaTime / transitionTime);
		}

		#endregion

		#region Helper Methods

		/// <summary>
		/// Updates frame counters and returns true if threshold was reached.
		/// </summary>
		private static bool CheckFrameThreshold(BodyState state, bool conditionMet, int threshold, bool trackGoodFrames)
		{
			if (conditionMet)
			{
				if (trackGoodFrames)
				{
					state.GoodFrameCount++;
					state.BadFrameCount = 0;
					return state.GoodFrameCount >= threshold;
				}

				state.BadFrameCount++;
				state.GoodFrameCount = 0;
				return state.BadFrameCount >= threshold;
			}

			if (trackGoodFrames)
				state.GoodFrameCount = 0;
			else
				state.BadFrameCount = 0;

			return false;
		}

		/// <summary>
		/// Resets both frame counters.
		/// </summary>
		private static void ResetCounters(BodyState state)
		{
			state.GoodFrameCount = 0;
			state.BadFrameCount = 0;
		}

		private void UpdateTrackingStatus(TrackedBody body, float quality)
		{
			float niceThreshold = body.EnableEdgeTracking ? TrackerDefaults.EDGE_NICE_QUALITY_THRESHOLD : TrackerDefaults.NICE_QUALITY_THRESHOLD;
			var status = body.IsTracking
				? (quality >= niceThreshold ? TrackingStatus.Tracking : TrackingStatus.Poor)
				: TrackingStatus.NotTracking;
			body.SetTrackingStatus(status);
		}

		private void FireEventsIfChanged(TrackedBody body, BodyState state)
		{
			bool isTracking = body.IsTracking;
			if (state.WasTracking != isTracking)
			{
				body.FireTrackingEvent(isTracking);
				state.WasTracking = isTracking;
			}
		}

		#endregion

		#region Public API

		/// <summary>
		/// Check if a specific body is currently in AR pose fusion mode (Anchored or Recovery).
		/// </summary>
		public bool IsBodyInARMode(TrackedBody body)
		{
			return body.IsStationary && IsARPoseFusionActive && _bodyStates.ContainsKey(body);
		}

		/// <summary>
		/// Reset state for all bodies. Stops tracking, clears AR states,
		/// and triggers detection (which resets poses to initial).
		/// </summary>
		public void ResetAll()
		{
			foreach (TrackedBody body in XRTrackerManager.Instance.TrackedBodies)
				ResetBodyInternal(body);
		}

		/// <summary>
		/// Reset state for a specific body. Stops tracking, clears state,
		/// and resets pose to initial.
		/// </summary>
		public void ResetBody(TrackedBody body)
		{
			ResetBodyInternal(body);
		}

		private void ResetBodyInternal(TrackedBody body)
		{
			if (!body.IsRegistered)
				return;

			bool wasTracking = body.IsTracking;

			// Stop tracking if active mode
			if (body.IsActiveMode && wasTracking)
				body.SetTrackingState(false);

			body.ResetQualityMetrics();

			// Update status
			body.SetTrackingStatus(TrackingStatus.NotTracking);

			// Fire event if was tracking
			if (wasTracking)
				body.FireTrackingEvent(false);

			// Clear manager state for this body
			_bodyStates.Remove(body);

			// Reset this specific body in native tracker (also stops tracking)
			if (!body.HasParent)
				FTBridge.FT_ResetBody(body.BodyId);
		}

		/// <summary>
		/// Manually set the world pose for a body (useful for external initialization).
		/// </summary>
		public void SetBodyWorldPose(TrackedBody body, Vector3 position, Quaternion rotation)
		{
			body.SetWorldPose(position, rotation);
		}

		/// <summary>
		/// Re-detect AR Foundation availability. Call if AR session state changes.
		/// </summary>
		public void RefreshARAvailability()
		{
#if HAS_AR_FOUNDATION
			_arPoseFusion.Detect();
#endif
		}

		/// <summary>
		/// Updates all tracked bodies: fetches status from native and prepares for state processing.
		/// Called each frame by XRTrackerManager.
		/// State machine logic runs in OnBeforeRender via ProcessActiveBody.
		/// </summary>
		public void UpdateAllBodies()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;

			foreach (var body in manager.TrackedBodies)
			{
				if (body != null && body.IsRegistered)
					body.RefreshStatus();
			}

			// Update global histogram peak after all bodies have refreshed
			UpdateGlobalPeak(manager.TrackedBodies);
		}

		/// <summary>
		/// Updates the global histogram peak based on all tracking root bodies.
		/// Resets to HISTOGRAM_GOOD when no root body is tracking.
		/// </summary>
		private void UpdateGlobalPeak(IReadOnlyList<TrackedBody> trackedBodies)
		{
			// Check if any root body (no parent) is tracking
			bool anyRootTracking = false;
			foreach (var body in trackedBodies)
			{
				if (body != null && body.IsRegistered && !body.HasParent && body.IsTracking)
				{
					anyRootTracking = true;
					break;
				}
			}

			// Reset peak when no root body is tracking
			if (!anyRootTracking)
			{
				_globalHistogramPeak = TrackerDefaults.HISTOGRAM_GOOD;
				return;
			}

			// Apply slow decay
			_globalHistogramPeak *= TrackerDefaults.PEAK_DECAY_RATE;
			_globalHistogramPeak = Mathf.Max(_globalHistogramPeak, TrackerDefaults.PEAK_MIN);

			// Update from any tracking root body's histogram
			foreach (var body in trackedBodies)
			{
				if (body == null || !body.IsRegistered || body.HasParent || !body.IsTracking)
					continue;

				float histogram = body.HistogramAverage;
				if (histogram > _globalHistogramPeak)
				{
					_globalHistogramPeak = Mathf.Min(histogram, TrackerDefaults.PEAK_MAX);
				}
			}
		}

		#endregion
	}
}