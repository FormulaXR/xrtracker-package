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

		/// <summary>
		/// Fusion state for a tracked body in stationary AR mode.
		/// </summary>
		public enum BodyFusionState
		{
			/// <summary>Not tracking yet (detection phase).</summary>
			NotTracking,
			/// <summary>Warmup or quality >= nice — accepting M3T poses for display.</summary>
			Accepting,
			/// <summary>Quality below nice — holding last good pose, feeding AR pose back to native.</summary>
			Holding,
		}

		private class BodyState
		{
			// Quality-based state machine (used for all modes)
			public int BadFrameCount { get; set; }
			public int GoodFrameCount { get; set; }
			public bool WasTracking { get; set; }
			public int FramesSinceTrackingStarted { get; set; }

			// Nice-gate (AR stationary mode)
			public Vector3 LastGoodPosition { get; set; }
			public Quaternion LastGoodRotation { get; set; }
			public bool HasLastGoodPose { get; set; }
			public float TimeSinceLastCorrection { get; set; }
			public float LastAcceptedInstantQuality { get; set; }
			public int RecoveryFrameCount { get; set; }

			// Fusion state for UI/debug
			public BodyFusionState FusionState { get; set; }

#if HAS_AR_FOUNDATION
			// Phase 2 (anchored) state — body pose stored in session-anchor space.
			// During Phase 2 these become the source of truth for display, replacing LastGoodPosition/Rotation.
			public Vector3 AnchorSpacePosition { get; set; }
			public Quaternion AnchorSpaceRotation { get; set; }
			public bool HasAnchorSpacePose { get; set; }
#endif
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

		/// <summary>
		/// Access to the underlying pose-fusion helper. Used by XRTrackerManager to query the session anchor.
		/// </summary>
		internal ARPoseFusion ARPoseFusion => _arPoseFusion;
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
		/// Called immediately after native tracking step completes.
		/// For async tracking (Native, Injected) this fires from Update() on the next frame.
		/// All pose reading happens here so native results are guaranteed to be ready.
		/// </summary>
		private void OnAfterTrackingStep()
		{
			ProcessAllBodies();
		}

		private void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
		}

		/// <summary>
		/// Runs every render frame. Handles detection-phase camera following (must be smooth,
		/// every frame) while tracking-phase poses are applied from OnAfterTrackingStep
		/// (after async FT_TrackStep completes).
		/// </summary>
		[BeforeRenderOrder(100)]
		private void OnBeforeRender()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;

			foreach (var body in manager.TrackedBodies)
			{
				if (body == null || !body.IsRegistered || !body.IsActiveMode)
					continue;

				if (!body.IsTracking)
				{
					// Detection phase: follow camera every render frame
					body.ApplyDetectorPose();
				}
				else if (body.IsStationary && IsARPoseFusionActive
					&& _bodyStates.TryGetValue(body, out var state))
				{
					// Tracking phase: run display lerp every render frame
					// (state machine + target pose updated per tracking step in OnAfterTrackingStep)
#if HAS_AR_FOUNDATION
					bool inAnchorSpace = _arPoseFusion.HasReliableAnchor;
#else
					bool inAnchorSpace = false;
#endif
					ApplyStoredDisplayPose(body, state, inAnchorSpace);
				}
			}
		}

		private void ProcessAllBodies()
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
				ProcessStationary(body, state);
			}
			else
			{
				// Non-stationary, or stationary without AR Foundation (fallback)
				ProcessBodyNoAR(body, state, quality);
				ApplyNativePose(body);

#if HAS_AR_FOUNDATION
				// Non-stationary bodies also need anchor for stable camera pose
				if (IsARPoseFusionActive && XRTrackerManager.Instance.UseAnchor
					&& body.IsTracking && !_arPoseFusion.HasReliableAnchor
					&& body.InstantQuality >= body.NiceQualityThreshold)
				{
					var localBounds = body.ComputeLocalBounds();
					Vector3 boundsCenterWorld = body.transform.TransformPoint(localBounds.center);
					_arPoseFusion.RequestBodyAnchor(boundsCenterWorld, body.transform.rotation, localBounds, body.transform);
				}
#endif
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
				TryStartTracking(body, state);
			}
		}

#if HAS_AR_FOUNDATION
		/// <summary>
		/// Stationary mode with world-space (Phase 1) or anchor-space (Phase 2) Tikhonov prior.
		///
		/// Phase 1: no session anchor yet. C++ operates in scene-origin coordinates.
		/// On first nice-quality acceptance, RequestBodyAnchor fires (async). Switchover happens
		/// in BeforeTrackingStep when the anchor is ready.
		///
		/// Phase 2: anchor exists and is tracked. C++ operates in anchor-space coordinates.
		/// Stored body pose follows ARKit feature corrections via AnchorToWorld.
		/// </summary>
		private void ProcessStationary(TrackedBody body, BodyState state)
		{
			// Not yet tracking — body follows camera via BeforeTrackingStep.SetWorldPose
			if (!body.IsTracking)
			{
				state.FusionState = BodyFusionState.NotTracking;
				TryStartTracking(body, state);
				return;
			}

			state.FramesSinceTrackingStarted++;

			// Read raw native pose (no world conversion — ProcessStationary handles
			// anchor-space→world via StoreAcceptedPose + ApplyStoredDisplayPose).
			if (!FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose rawPose))
				return;
			rawPose.GetTransformation(out Vector3 nativePos, out Quaternion nativeRot);

			// In Phase 2, the native pose is in anchor space because
			// BeforeTrackingStep fed camera2anchor before TrackStep ran.
			bool inAnchorSpace = _arPoseFusion.HasReliableAnchor;

			float instantQuality = body.InstantQuality;
			bool warmup = state.FramesSinceTrackingStarted <= TrackerDefaults.STABILIZATION_FRAMES;
			bool qualityIsNice = instantQuality >= body.NiceQualityThreshold;

			// Tracking loss: stationary with AR pose fusion never auto-resets.
			// The anchor (or world-space stored pose) holds position until the user manually resets.
			// AR SLAM will eventually recover the anchor, and auto-reset would destroy it.

			// Nice-gate with recovery delay: after a quality dip, require FRAMES_TO_RECOVER
			// consecutive nice frames before re-accepting native poses. This lets the optimizer
			// settle after intrinsics changes (autofocus) or brief occlusions.
			if (qualityIsNice || warmup)
			{
				bool wasHolding = state.FusionState == BodyFusionState.Holding;
				bool recovered = warmup || !wasHolding
					|| ++state.RecoveryFrameCount >= TrackerDefaults.FRAMES_TO_RECOVER;

				if (recovered)
				{
					state.RecoveryFrameCount = 0;
					StoreAcceptedPose(state, nativePos, nativeRot, inAnchorSpace, instantQuality);
					state.TimeSinceLastCorrection = 0;
					state.FusionState = BodyFusionState.Accepting;

					// Phase 1: at first nice-quality frame, request session anchor creation (async).
					// The async creation completes some frames later; switchover happens atomically
					// in XRTrackerManager.BeforeTrackingStep when HasReliableAnchor flips to true.
					if (XRTrackerManager.Instance.UseAnchor && !inAnchorSpace && qualityIsNice)
					{
						var localBounds = body.ComputeLocalBounds();
						Vector3 boundsCenterWorld = body.transform.TransformPoint(localBounds.center);
						_arPoseFusion.RequestBodyAnchor(boundsCenterWorld, body.transform.rotation, localBounds, body.transform);
					}
				}
				else
				{
					// Recovering — still holding, keep correcting C++
					state.TimeSinceLastCorrection += Time.deltaTime;
					state.FusionState = BodyFusionState.Holding;
				}
			}
			else
			{
				// Quality below nice — don't display the M3T pose. Hold last good pose
				// (anchor-relative in Phase 2, so it follows ARKit corrections).
				// Periodically feed AR pose back to native to help recovery.
				state.RecoveryFrameCount = 0;
				state.TimeSinceLastCorrection += Time.deltaTime;
				state.FusionState = BodyFusionState.Holding;

				if (state.TimeSinceLastCorrection >= body.QualityGateCorrectionInterval
					&& HasStoredPose(state, inAnchorSpace))
				{
					FTTrackingPose pose = inAnchorSpace
						? ConversionUtils.GetConvertedPose(state.AnchorSpacePosition, state.AnchorSpaceRotation)
						: ConversionUtils.GetConvertedPose(state.LastGoodPosition, state.LastGoodRotation);
					FTBridge.FT_SetBodyPose(body.BodyId, ref pose);
					state.TimeSinceLastCorrection = 0;
				}
			}

			// Display: lerp toward stored target pose, expressed in world space.
			// Phase 2 composes via AnchorToWorld so the body follows ARKit feature corrections automatically.
			ApplyStoredDisplayPose(body, state, inAnchorSpace);

			// Never auto-reset stationary bodies with AR pose fusion.
			// AR SLAM recovers on its own; auto-reset would destroy the anchor and stored pose.
		}

		/// <summary>
		/// Apply the smooth display lerp toward the stored pose. In Phase 2 the
		/// stored pose lives in anchor space and is composed via AnchorToWorld
		/// so the body inherits ARKit feature corrections automatically. In
		/// Phase 1 it's a direct world-space lerp toward LastGoodPosition.
		/// No-op when no stored pose exists yet.
		/// </summary>
		private void ApplyStoredDisplayPose(TrackedBody body, BodyState state, bool inAnchorSpace)
		{
			if (!HasStoredPose(state, inAnchorSpace))
				return;

			Vector3 targetPos;
			Quaternion targetRot;
			if (inAnchorSpace)
			{
				Pose worldPose = _arPoseFusion.AnchorToWorld(
					state.AnchorSpacePosition, state.AnchorSpaceRotation);
				targetPos = worldPose.position;
				targetRot = worldPose.rotation;
			}
			else
			{
				targetPos = state.LastGoodPosition;
				targetRot = state.LastGoodRotation;
			}

			float alpha = ComputeLerpAlpha(body.SmoothTime);
			body.SetWorldPose(
				Vector3.Lerp(body.transform.position, targetPos, alpha),
				Quaternion.Slerp(body.transform.rotation, targetRot, alpha));
		}

		private static void StoreAcceptedPose(BodyState state, Vector3 nativePos, Quaternion nativeRot, bool inAnchorSpace, float instantQuality)
		{
			if (inAnchorSpace)
			{
				state.AnchorSpacePosition = nativePos;
				state.AnchorSpaceRotation = nativeRot;
				state.HasAnchorSpacePose = true;
			}
			else
			{
				state.LastGoodPosition = nativePos;
				state.LastGoodRotation = nativeRot;
				state.HasLastGoodPose = true;
			}

			state.LastAcceptedInstantQuality = instantQuality;
		}

		private static bool HasStoredPose(BodyState state, bool inAnchorSpace)
		{
			return inAnchorSpace ? state.HasAnchorSpacePose : state.HasLastGoodPose;
		}

		/// <summary>
		/// Atomic Phase 1→2 switchover. Called from XRTrackerManager.BeforeTrackingStep AFTER setting
		/// camera2anchor and BEFORE FT_TrackStep runs. Converts each tracked body's last accepted
		/// world-space pose to anchor space and pushes it to C++ via FT_SetBodyPose so the optimizer
		/// step runs in a consistent anchor frame.
		///
		/// Idempotent: bodies already in anchor space are skipped. Bodies that joined the session
		/// after the anchor was already created have their first accepted pose stored directly in
		/// AnchorSpacePosition/Rotation by ProcessStationary (no switchover needed for them).
		/// </summary>
		internal void TryPerformAnchorSwitchover()
		{
			if (!_arPoseFusion.HasReliableAnchor)
				return;

			foreach (var (body, state) in _bodyStates)
			{
				if (state.HasAnchorSpacePose)
					continue; // already in anchor space

				// Use stored pose if available, otherwise fall back to current transform
				Vector3 worldPos;
				Quaternion worldRot;
				if (state.HasLastGoodPose)
				{
					worldPos = state.LastGoodPosition;
					worldRot = state.LastGoodRotation;
				}
				else if (body.IsTracking)
				{
					worldPos = body.transform.position;
					worldRot = body.transform.rotation;
				}
				else
				{
					continue; // not tracking and no stored pose
				}

				if (_arPoseFusion.TryWorldToAnchor(worldPos, worldRot,
					out var anchorPos, out var anchorRot))
				{
					state.AnchorSpacePosition = anchorPos;
					state.AnchorSpaceRotation = anchorRot;
					state.HasAnchorSpacePose = true;

					FTTrackingPose pose = ConversionUtils.GetConvertedPose(anchorPos, anchorRot);
					FTBridge.FT_SetBodyPose(body.BodyId, ref pose);

					Debug.Log($"[ARPoseFusion] Phase 1→2 switchover for body {body.BodyId}");
				}
			}
		}
#else
		private void ProcessStationary(TrackedBody body, BodyState state)
		{
			// AR Foundation not available — fall back to standard processing
			ProcessBodyNoAR(body, state, body.TrackingQuality);
		}
#endif

		/// <summary>
		/// Attempts to start tracking based on quality threshold.
		/// </summary>
		private void TryStartTracking(TrackedBody body, BodyState state)
		{
			if (CheckFrameThreshold(state, body.InstantQuality >= body.QualityToStartTracking, TrackerDefaults.FRAMES_TO_START, trackGoodFrames: true))
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
#if HAS_AR_FOUNDATION
					// When anchor is active, C++ operates in anchor space.
					// Convert back to world via anchor transform.
					if (_arPoseFusion.HasReliableAnchor)
					{
						var worldPose = _arPoseFusion.AnchorToWorld(pos, rot);
						worldPos = worldPose.position;
						worldRot = worldPose.rotation;
					}
					else
					{
						// Phase 1: C++ operates in world space
						worldPos = pos;
						worldRot = rot;
					}
#else
					worldPos = pos;
					worldRot = rot;
#endif
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
			state.HasLastGoodPose = false;
			state.TimeSinceLastCorrection = 0;
			state.LastAcceptedInstantQuality = 0;
			state.FusionState = BodyFusionState.NotTracking;
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
		/// Snapshot of per-body fusion state for debug UI.
		/// </summary>
		public struct BodyFusionInfo
		{
			public BodyFusionState State;
			public bool InAnchorSpace;
			public bool IsWarmup;
			public float InstantQuality;
			public float NiceThreshold;
			public float LastAcceptedQuality;
			public int FramesSinceStart;
		}

		/// <summary>
		/// Get fusion debug info for a tracked body. Returns false if body is not in AR stationary mode.
		/// </summary>
		public bool TryGetBodyFusionInfo(TrackedBody body, out BodyFusionInfo info)
		{
			info = default;
			if (!IsBodyInARMode(body) || !_bodyStates.TryGetValue(body, out var state))
				return false;

			info.State = state.FusionState;
#if HAS_AR_FOUNDATION
			info.InAnchorSpace = _arPoseFusion.HasReliableAnchor;
#endif
			info.IsWarmup = state.FramesSinceTrackingStarted <= TrackerDefaults.STABILIZATION_FRAMES;
			info.InstantQuality = body.InstantQuality;
			info.NiceThreshold = body.NiceQualityThreshold;
			info.LastAcceptedQuality = state.LastAcceptedInstantQuality;
			info.FramesSinceStart = state.FramesSinceTrackingStarted;
			return true;
		}

		/// <summary>
		/// Reset state for all bodies. Stops tracking, clears AR states,
		/// and triggers detection (which resets poses to initial).
		/// </summary>
		public void ResetAll()
		{
			foreach (TrackedBody body in XRTrackerManager.Instance.TrackedBodies)
				ResetBodyInternal(body);

#if HAS_AR_FOUNDATION
			// Destroy anchor so next tracking cycle starts fresh in Phase 1 (world-origin).
			// A new anchor will be created when a body reaches nice quality again.
			_arPoseFusion.DestroyAnchor();
#endif
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