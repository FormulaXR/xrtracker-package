using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	internal delegate bool TrackedBodyStatusReader(
		string bodyId, out FTBodyStatus status);

	/// <summary>The on-screen distance in pixels between two world poses of a body, or not a number when it cannot be measured.</summary>
	internal delegate float SettleDistance(Vector3 firstPosition, Quaternion firstRotation, Vector3 secondPosition, Quaternion secondRotation);

	internal readonly struct StationaryCorrectionTimerStep
	{
		internal StationaryCorrectionTimerStep(float timeBelowNice,
			float timeSinceLastCorrection, bool shouldAttempt)
		{
			TimeBelowNice = timeBelowNice;
			TimeSinceLastCorrection = timeSinceLastCorrection;
			ShouldAttempt = shouldAttempt;
		}

		internal float TimeBelowNice { get; }
		internal float TimeSinceLastCorrection { get; }
		internal bool ShouldAttempt { get; }
	}

	internal static class StationaryCorrectionTimer
	{
		internal static StationaryCorrectionTimerStep Advance(
			float timeBelowNice,
			float timeSinceLastCorrection,
			float deltaSeconds,
			bool hasStoredPose,
			float recoveryDelay,
			float correctionInterval)
		{
			float nextBelowNice = timeBelowNice + deltaSeconds;
			float nextSinceCorrection = timeSinceLastCorrection + deltaSeconds;
			return new StationaryCorrectionTimerStep(
				nextBelowNice,
				nextSinceCorrection,
				hasStoredPose && nextBelowNice >= recoveryDelay &&
					nextSinceCorrection >= correctionInterval);
		}
	}

	/// <summary>
	/// Manages tracking state and pose application for all TrackedBodies.
	/// Handles stationary mode: the body is held in the AR session's world frame with tracker corrections.
	/// This is the single point of control for start/stop/reset decisions.
	/// </summary>
	[DefaultExecutionOrder(-1)] // Before TrackedBody
	public class TrackedBodyManager : MonoBehaviour
	{
		/// <summary>
		/// Immutable observability for one stationary lifecycle decision. Consumers
		/// read it only after the matching completed-lifecycle event.
		/// </summary>
		internal readonly struct StationaryCorrectionTelemetry
		{
			public const int NativeStatusNotAttempted = int.MinValue;

			internal StationaryCorrectionTelemetry(
				XRTrackerManager.TrackingFrameInstrumentationContext frame,
				float timeBelowNiceBefore,
				float timeBelowNiceAfter,
				float timeSinceLastCorrectionBefore,
				float timeSinceLastCorrectionAfter,
				bool attempted,
				int nativeStatus,
				bool poseAvailable,
				FTTrackingPose pose,
				int bodyStatusRefreshCount)
			{
				SequenceId = frame.SequenceId;
				FrameIndex = frame.FrameIndex;
				RecordedTimestampNs = frame.TimestampNs;
				AppliedTimerDeltaNs = frame.TrackingStepDeltaNs;
				ClockSource = frame.TrackingStepClockSource;
				StatusRefreshCount = bodyStatusRefreshCount;
				TimeBelowNiceBefore = timeBelowNiceBefore;
				TimeBelowNiceAfter = timeBelowNiceAfter;
				TimeSinceLastCorrectionBefore = timeSinceLastCorrectionBefore;
				TimeSinceLastCorrectionAfter = timeSinceLastCorrectionAfter;
				Attempted = attempted;
				NativeStatus = nativeStatus;
				PoseAvailable = poseAvailable;
				Pose = pose;
			}

			public readonly string SequenceId;
			public readonly int FrameIndex;
			public readonly long RecordedTimestampNs;
			public readonly long AppliedTimerDeltaNs;
			public readonly string ClockSource;
			public readonly int StatusRefreshCount;
			public readonly float TimeBelowNiceBefore;
			public readonly float TimeBelowNiceAfter;
			public readonly float TimeSinceLastCorrectionBefore;
			public readonly float TimeSinceLastCorrectionAfter;
			public readonly bool Attempted;
			public readonly int NativeStatus;
			public readonly bool PoseAvailable;
			public readonly FTTrackingPose Pose;
		}

		/// <summary>
		/// Read-only state of one body's hold seed schedule after the current
		/// lifecycle decision.
		/// </summary>
		internal readonly struct HoldSeedScheduleTelemetry
		{
			internal HoldSeedScheduleTelemetry(bool holdActive, int attemptsThisHold)
			{
				HoldActive = holdActive;
				AttemptsThisHold = attemptsThisHold;
			}

			public readonly bool HoldActive;
			public readonly int AttemptsThisHold;
		}

		#region Singleton

		private static TrackedBodyManager _instance;
		public static TrackedBodyManager Instance => _instance;

		#endregion

		#region Private Types

		/// <summary>
		/// Fusion state for a tracked body in stationary AR mode.
		/// </summary>
		internal enum BodyFusionState
		{
			/// <summary>Not tracking yet (detection phase).</summary>
			NotTracking,
			/// <summary>Warmup or quality >= nice — accepting native tracker poses for display.</summary>
			Accepting,
			/// <summary>Quality below nice — holding last good pose, feeding AR pose back to native.</summary>
			Holding,
		}

		/// <summary>What a stationary body's fusion step did with the solve of the step. It is a diagnostic readout and takes no part in tracking.</summary>
		internal enum FusionStepOutcome
		{
			/// <summary>The step did not reach the acceptance decision, because the body was not tracking, was held out of view or had no pose.</summary>
			NotRun,
			/// <summary>The lock was given up, so nothing was accepted or stored.</summary>
			HeldByRelease,
			/// <summary>The solve was accepted and stored as the pose the body is held at.</summary>
			Stored,
			/// <summary>The solve was accepted and the reference guard refused to store it.</summary>
			AcceptedNotStored,
			/// <summary>The solve was accepted and the recovery rows after a hold had not yet run out, so it was not stored.</summary>
			AcceptedRecovering,
			/// <summary>The solve was accepted and the return from out of view discarded it.</summary>
			AcceptedSuppressed,
			/// <summary>The solve was not accepted.</summary>
			Refused,
		}

		/// <summary>The readings a stationary body's fusion step took, as they stood when it decided.</summary>
		internal struct FusionStepRecord
		{
			/// <summary>What the step did with the solve.</summary>
			public FusionStepOutcome Outcome;
			/// <summary>Whether the registration margin cleared the accept bar on the step.</summary>
			public bool MarginClears;
			/// <summary>Whether the first accept was held back because the solve had not stood still.</summary>
			public bool FirstAcceptWaits;
			/// <summary>Whether the coverage score cleared the bar the soft accept or the coverage decision reads.</summary>
			public bool Covered;
			/// <summary>Whether the solve lay inside the prediction gate.</summary>
			public bool InsideGate;
			/// <summary>The steps in a row on which the soft accept had verified the solve.</summary>
			public int SoftAcceptRows;
			/// <summary>Whether the soft accept verified the solve on enough steps in a row.</summary>
			public bool SoftAccepted;
			/// <summary>The steps since the registration margin accepted a solve.</summary>
			public int RowsSinceMarginAccept;
			/// <summary>Whether the solve was accepted when the step took its decision.</summary>
			public bool Accepted;
			/// <summary>Whether an accepted solve was allowed to become the stored pose.</summary>
			public bool MayBecomeReference;
			/// <summary>The reading the stored pose guard compared with the hold bar.</summary>
			public float ReferenceReading;
			/// <summary>Whether the solve was accepted on the coverage score alone.</summary>
			public bool CoverageAccepted;
			/// <summary>Whether the view stood below the share of what it normally reaches that stores a fit, so that the solve was not stored.</summary>
			public bool DipHeld;
			/// <summary>The share of what the view normally reaches that the agreeing sites stood at, or zero when it could not be read.</summary>
			public float ViewHealth;
			/// <summary>Whether the AR camera moved the picture of the body faster than the limit, so that the solve was not stored.</summary>
			public bool FastMotionHeld;
			/// <summary>How far the AR camera moved the picture of the body on this step, as the largest displacement of the centre and the corners of its bounds in pixels of a camera with a focal length of 450 pixels, or minus one when it was not measured.</summary>
			public float CameraMotionPixels;
			/// <summary>How far the fit stored on this step stood from the settled pose on screen, in pixels, or minus one when it was not measured or nothing was stored.</summary>
			public float SettleMovePixels;
			/// <summary>Whether the start search started the body, which is a start while the guide was out of reach of the pose the tracker found.</summary>
			public bool StartedFromSearch;
			/// <summary>Whether the first accept was held back because the coverage score of the start had not stood at or above the bar of the first accept on enough steps in a row.</summary>
			public bool FirstAcceptBarWaits;
			/// <summary>The coverage score of the start on this step, on the scale of the start bar, or minus one when the start of the body does not read the coverage score.</summary>
			public float StartSignal;
			/// <summary>The steps in a row, up to the number the first accept needs, on which the coverage score of the start stood at or above the bar of the first accept.</summary>
			public int FirstAcceptBarRows;
			/// <summary>Whether the readout was held at degraded on this step, because the pose on screen had not yet reached the stored fit.</summary>
			public bool ReadoutHeldForDisplay;
			/// <summary>How far the pose on screen stood from the stored fit while the readout waited for it, in pixels measured as the settle pull measures them, or minus one when it was not measured.</summary>
			public float DisplayToFitPixels;
			/// <summary>Whether the AR session moved its world just before this step, so that the poses of the body were carried to the new frame.</summary>
			public bool WorldCarried;
		}

		/// <remarks>
		/// Internal rather than private. Nothing outside this assembly and the
		/// assemblies it shares its internals with sees it.
		/// </remarks>
		internal class BodyState
		{
			// What the newest stationary fusion step did with the solve, for diagnostics.
			public FusionStepRecord Step;

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
			public float TimeBelowNice { get; set; }
			public float LastAcceptedInstantQuality { get; set; }
			public int RecoveryFrameCount { get; set; }

			// The prediction gate. The camera of the step before gives the speed of the AR pose.
			public int PredictionReseeds { get; set; }

			// Whether the AR session moved its world just before the newest step, so that this body was carried over to the new frame.
			public bool WorldCarriedThisStep { get; set; }

			// The soft accept. The steps in a row on which the solve was verified on the coverage score inside the prediction gate, whether this
			// step's solve was accepted that way, and the steps since the registration margin accepted a solve.
			public int SoftAcceptRows { get; set; }
			public bool SoftAcceptedThisStep { get; set; }
			public int RowsSinceMarginAccept { get; set; } = TrackerDefaults.SOFT_ACCEPT_QUIET_ROWS;
			public Vector3 PreviousStepCameraPosition { get; set; }
			public Quaternion PreviousStepCameraRotation { get; set; }
			public bool HasPreviousStepCamera { get; set; }

			// The newest solves in the world frame, one per step the coverage score supports, and what was done with them. The first-accept guard
			// holds the first accept back until the solve stands still.
			public readonly SolveWindow SolveWindow = new SolveWindow();
			public int FirstAcceptRowsWaited { get; set; }

			// The first-accept coverage bar. The steps in a row, up to the number the first accept needs, on which the coverage score of the start stood at
			// or above the bar, which count only while the body holds no accepted fit.
			public int FirstAcceptBarRows { get; set; }

			// The dip of the view. Whether the view stood below the floor and has not yet stood at or above it for the rows that end a dip.
			public bool InViewDip { get; set; }
			public int ViewDipHealthyRows { get; set; }
			public int ViewHealthyStreak { get; set; }

			// Fusion state for UI/debug
			public BodyFusionState FusionState { get; set; }

			// The three-state machine's memory, and the reason it gave on the frame
			// it last ran. A field rather than a property because the machine takes
			// it by reference and advances it in place.
			public TrackingConfidenceMemory Confidence;
			public TrackingConfidenceReason ConfidenceReason;
			public TemporalTrackingMemory TemporalTracking;

			// What the relative-health pass measured about this frame, held here
			// because every consumer of it runs later in the same frame. The
			// viewpoint and the previous frame's pose are what the settle bound and
			// the expectation lookup are taken from; the body diameter is measured
			// once because measuring it walks every vertex of every mesh.
			public RecoveryViewDescriptor CurrentView;
			public bool HasCurrentView;
			public Vector3 PreviousWorldPosition;
			public Quaternion PreviousWorldRotation;
			public bool HasPreviousWorldPose;
			public bool PoseSettled;
			public float BodyDiameterMeters;
			public bool ColdStartWindow;
			public bool ColdStartSatisfied;

			// The raw ratios of the frames the cold-start window has counted so far.
			// It is what the window's expectation is defined from, and it is cleared
			// by any frame that fails, because a window is a run of frames rather
			// than a collection of them.
			public readonly List<float> ColdStartRatios = new List<float>();

			// What the lifecycle decided about this body on the frame it last ran,
			// written by the decisions themselves as they take them. A field rather
			// than a property because the sites fill it in piece by piece, and a
			// record assembled afterwards would be a second reading rather than the
			// one that decided.
			public BodyLifecycleDecision Decision;

			// The release loop's memory. A body that has given up its lock is held
			// at its seat, solving from there, until the score says the solve is on
			// the body again for as long as a start takes.
			public bool HoldActive { get; set; }
			public int ReleaseFrameCount { get; set; }
			public int ReentryFrameCount { get; set; }
			// The current hold's take-back judgements, newest in bit 0: set when the
			// frame cleared the hold bar in the picture. Zeroed wherever a hold begins
			// or ends, so the take-back window never reaches into another hold.
			public uint TakeBackHistory { get; set; }
			// Owned by this body's state and valid only for its current hold. Entry
			// consumes one attempt whether or not the plugin accepts the call; a
			// re-entry, new hold, manual reset or state removal starts from zero.
			public int HoldSeedAttemptsThisHold { get; set; }

			// The re-check of the predicted pose while held.
			public FusionRecheckState Recheck;

			// Set on the frame a recovery's verification window completes, and read
			// once by the display so the pose that frame publishes carries where it
			// came from. Every later frame is an ordinary solve again.
			public bool RecoveryWindowPassedThisFrame;
			public bool PublishRecoveryProvenanceOnce;

			// Whether this tracking step stored the solve as the reference. A step
			// that did not leaves the display on the reference it already had.
			public bool ReferenceStoredThisStep;

			// The pose the display converges on. Every stored solve pulls it a fixed
			// fraction of the way, so the solve's scatter averages out while a lasting
			// move is followed. It is seated again on the first store after the body
			// was held at its seat, and it is held in the same space as the stored
			// pose and dropped with it.
			public Vector3 SettledPosition;
			public Quaternion SettledRotation;
			public bool HasSettledPose;
			public bool SeatSettledOnNextStore;

			// The readout only. Set when a stored fit seats the settled pose, which is the first fit and the first fit after a hold, and cleared once the
			// pose on screen has come within the still distance of the settle pull of the stored fit. Nothing but the readout reads it.
			public bool DisplayCatchUpPending;

			// How long this body has been out of tracking, and what the driver has done
			// about it. The count is the trigger: a body re-acquires on its own within a
			// few frames or it is not going to, and one that is not is seated on a view
			// it was solved well in.
			public int FramesOutOfTracked;
			public int RecoveryAttempts;
			public int FramesSinceRecoveryAttempt;
			public bool RecoverySeedBankConfigured;
			public SavedViewRecoveryState SavedView;

			// What the loss guard said about the last completed step, read back once
			// per frame and read by exactly one decision. A body with no loss guard
			// configured leaves this at its default, which allows publishing, so an
			// unconfigured body runs the ordinary path.
			public FTLossQuarantineState LossQuarantine;
			public bool HasLossQuarantineState;

#if HAS_AR_FOUNDATION
			// The display filter's own state, held in the same frame the stored pose
			// is held in, which is the world frame. It is not read back from the
			// transform, because the transform carries the pose the filter has
			// already written and filtering there would feed it back into itself.
			public Vector3 DisplayPosition { get; set; }
			public Quaternion DisplayRotation { get; set; }
			public bool HasDisplayPose { get; set; }

			// What the camera pose says about whether this body should be in frame,
			// recorded every stationary frame whether or not anything consumes it.
			public BodyViewVisibility Visibility { get; set; }

			// Where this body stands with the world-frame coast, and how many returns
			// to view have actually landed a seed in the tracker. The count moves
			// only on a write the tracker accepted.
			// How many times the display filter has advanced for this body. The one
			// number that says whether the display path is running at all, which is
			// not otherwise observable from the pose alone.
			public int DisplayFilterAdvances { get; set; }

			public WorldFrameCoastPhase CoastPhase { get; set; }
			public int CoastReentrySeeds { get; set; }
			public int CoastSeedWriteFailures { get; set; }
#endif

			/// <summary>
			/// Carries every pose this state holds in the world frame to the new frame, after the AR session moved its world. Each pose goes through the
			/// same map as the camera, so the pose of the body relative to the camera is the same before and after.
			/// </summary>
			/// <remarks>
			/// These are the poses carried. The stored fit, the settled pose, the pose of the display filter, the camera of the step before (so that the
			/// jump is no camera motion), the pose of the step before that the settling of the pose reads, every solve in the window of the first accept,
			/// and the pose a saved-view seat replaced. The other values here are counts, flags and readings of the picture, which a new frame does not
			/// change.
			/// </remarks>
			public void CarryWorld(Pose carry)
			{
				LastGoodPosition = ArWorldJump.Carry(carry, LastGoodPosition);
				LastGoodRotation = ArWorldJump.Carry(carry, LastGoodRotation);
				SettledPosition = ArWorldJump.Carry(carry, SettledPosition);
				SettledRotation = ArWorldJump.Carry(carry, SettledRotation);
				PreviousStepCameraPosition = ArWorldJump.Carry(carry, PreviousStepCameraPosition);
				PreviousStepCameraRotation = ArWorldJump.Carry(carry, PreviousStepCameraRotation);
				PreviousWorldPosition = ArWorldJump.Carry(carry, PreviousWorldPosition);
				PreviousWorldRotation = ArWorldJump.Carry(carry, PreviousWorldRotation);
				SolveWindow.Carry(carry);
				if (SavedView != null)
				{
					SavedView.PreviousPosition = ArWorldJump.Carry(carry, SavedView.PreviousPosition);
					SavedView.PreviousRotation = ArWorldJump.Carry(carry, SavedView.PreviousRotation);
				}
#if HAS_AR_FOUNDATION
				DisplayPosition = ArWorldJump.Carry(carry, DisplayPosition);
				DisplayRotation = ArWorldJump.Carry(carry, DisplayRotation);
#endif
				WorldCarriedThisStep = true;
			}
		}

		#endregion

		#region Serialized Fields

		/// <remarks>
		/// On by default. With it off, the visibility predicate is still measured
		/// and reported every frame but nothing reads it.
		/// </remarks>
		[Tooltip("Hold a stationary body in the world frame while it is out of " +
			"the camera's view, instead of treating that as a tracking failure. " +
			"On by default.")]
		[SerializeField] private bool _enableWorldFrameCoast = true;

		[Tooltip("How many views of a body are kept as places a recovery can start " +
			"from. Zero keeps none and makes no recovery attempt.")]
		[SerializeField] private int _savedViewRecoveryCapacity =
			TrackerDefaults.RECOVERY_SEED_BANK_CAPACITY;

		#endregion

		#region Internal Options

		// Not serialized and not in the inspector. Each is read through the property
		// below it, where a run-wide value set through TrackingOverrides wins over
		// the value on this manager.

		private bool _enablePoseProvenance;
		private TrackerDefaults.QualitySignalSource? _qualitySignalSource;
		private TrackerDefaults.MarginResidualTerm _marginResidualTerm = TrackerDefaults.MARGIN_RESIDUAL_TERM;
		private bool _seenPoseRecovery = TrackerDefaults.SEEN_POSE_RECOVERY;
		private bool _enableHoldRelease = true;

		private SavedViewRecoveryDriver _savedViewDriver;
		private EvidenceReuseSwitch _evidenceReuse;

		private readonly Dictionary<TrackedBody, SeenPoseCandidates> _seenPoses =
			new Dictionary<TrackedBody, SeenPoseCandidates>();

		// The camera-from-body pose of the recovery seat each body was last seated at, kept so the record judged on the
		// refined pose can be compared with it.
		private readonly Dictionary<TrackedBody, float[]> _seatMatrices = new Dictionary<TrackedBody, float[]>();

		/// <summary>Whether each written pose records where it came from. Off by default.</summary>
		/// <remarks>The tag is recorded and never read by a decision.</remarks>
		internal bool EnablePoseProvenance
		{
			get => TrackingOverrides.PoseProvenance ?? _enablePoseProvenance;
			set => _enablePoseProvenance = value;
		}

		/// <summary>Which scalar this manager's quality decisions read.</summary>
		/// <remarks>
		/// The two sources are on different scales and their bars move with them,
		/// so this is read once per decision rather than compared against a bar
		/// chosen elsewhere.
		/// </remarks>
		internal TrackerDefaults.QualitySignalSource QualitySignalSource
		{
			get => TrackingOverrides.QualitySignalSource ?? _qualitySignalSource ??
				ShippedDefaults.QualitySignalSourceFor(ShippedDefaults.PoseFusionActive(this));
			set => _qualitySignalSource = value;
		}

		/// <summary>Which residual term the registration margin this manager's decisions read takes.</summary>
		/// <remarks>
		/// It changes the margin and nothing else. The bars, the frame counts and the
		/// take-back window read the margin as they always do. The body's instant and
		/// averaged readings and the saved-view verifier all follow it.
		/// </remarks>
		internal TrackerDefaults.MarginResidualTerm MarginResidualTerm
		{
			get => TrackingOverrides.MarginResidualTerm ?? _marginResidualTerm;
			set => _marginResidualTerm = value;
		}

		/// <summary>
		/// Whether a body without pose fusion tries the camera-relative poses it was
		/// accepted at before, after a loss. On by default.
		/// </summary>
		/// <remarks>
		/// It acts only on the path without pose fusion, and only where the saved-view
		/// driver runs, so it needs a saved-view capacity above zero. Off, no pose is
		/// kept and the saved-view driver is given nothing to try.
		/// </remarks>
		internal bool SeenPoseRecovery
		{
			get => TrackingOverrides.SeenPoseRecovery ?? _seenPoseRecovery;
			set => _seenPoseRecovery = value;
		}

		/// <summary>
		/// Whether a stationary body under pose fusion gives up its lock when a frame's
		/// score says the solve has left the body, and takes it back only on verified
		/// re-entry. On by default.
		/// </summary>
		/// <remarks>Off, no release decision is made and no counter moves.</remarks>
		internal bool EnableHoldRelease
		{
			get => TrackingOverrides.HoldRelease ?? _enableHoldRelease;
			set => _enableHoldRelease = value;
		}

		#endregion

		#region Coast Settings

		/// <summary>
		/// Whether a stationary body that leaves the camera's view is held in the
		/// world frame instead of being treated as a tracking failure.
		/// </summary>
		public bool EnableWorldFrameCoast
		{
			get => _enableWorldFrameCoast;
			set => _enableWorldFrameCoast = value;
		}

		/// <summary>How many saved views a body keeps; zero switches the bank off.</summary>
		public int SavedViewRecoveryCapacity
		{
			get => _savedViewRecoveryCapacity;
			set => _savedViewRecoveryCapacity = Mathf.Max(0, value);
		}

		/// <summary>
		/// Whether a hold's judgement history takes the lock back under a window of
		/// <paramref name="window"/> frames of which <paramref name="required"/> must clear.
		/// </summary>
		/// <remarks>
		/// Bit 0 of <paramref name="history"/> is the newest judgement. The window is
		/// clamped to what the history can hold and the requirement to the window, so
		/// no setting can ask for more frames than the window holds.
		/// </remarks>
		internal static bool TakeBackWindowClears(uint history, int required, int window)
		{
			window = Mathf.Clamp(window, 1, TrackerDefaults.MAX_TAKE_BACK_WINDOW_FRAMES);
			required = Mathf.Clamp(required, 1, window);
			uint inWindow = window >= 32 ? history : history & ((1u << window) - 1u);
			int cleared = 0;
			while (inWindow != 0)
			{
				inWindow &= inWindow - 1u;
				++cleared;
			}
			return cleared >= required;
		}

		/// <summary>Which scalar a body's hold decisions read.</summary>
		/// <remarks>
		/// This manager's setting, except for a body with no edge modality. The
		/// registration margin is measured on edge sites only, so such a body reads
		/// the coverage signal, as its own start and stop decisions do.
		/// </remarks>
		private TrackerDefaults.QualitySignalSource SourceFor(TrackedBody body) =>
			!ReferenceEquals(body, null) && !body.EnableEdgeTracking
				? TrackerDefaults.QualitySignalSource.Coverage
				: QualitySignalSource;

		/// <summary>
		/// The bar a body's lock is given up under and taken back over, on the
		/// scale of the scalar that reads it.
		/// </summary>
		internal static float HoldReleaseBarFor(
			TrackerDefaults.QualitySignalSource source) =>
			source == TrackerDefaults.QualitySignalSource.RegistrationMargin
				? TrackerDefaults.REGISTRATION_MARGIN_HOLD_BAR
				: TrackerDefaults.EDGE_HOLD_RELEASE_THRESHOLD;

		/// <summary>
		/// Whether a layer that was added for the edge method after the first release is in force for a body that tracks by
		/// the given method. A layer is in force only when its own switch is on and the body tracks by the edge method. A
		/// body that tracks by the silhouette method has none of them, because its score is measured on no edge sites and
		/// its start, stop and reset are the ones it has always had.
		/// </summary>
		internal static bool EdgeLayerApplies(bool switchedOn, bool edgeTracking) => switchedOn && edgeTracking;

		/// <summary>The same for a body. No body is no restriction, because a layer can be asked about a pose that has no body.</summary>
		private static bool EdgeLayerApplies(bool switchedOn, TrackedBody body) =>
			EdgeLayerApplies(switchedOn, ReferenceEquals(body, null) || body.EnableEdgeTracking);

		/// <summary>The hold bar for <paramref name="body"/>, which may be null.</summary>
		private float HoldReleaseBarOf(TrackedBody body) => HoldReleaseBarFor(SourceFor(body));

		/// <summary>Raised on every saved-view attempt and every window outcome, for a host that records them.</summary>
		internal event System.Action<TrackedBody, SavedViewDecision> OnSavedViewAttempt;

		/// <summary>
		/// What the current camera pose says about whether a body should be in
		/// frame. Reported whether or not the coast is enabled.
		/// </summary>
		internal bool TryGetBodyViewVisibility(TrackedBody body, out BodyViewVisibility visibility)
		{
#if HAS_AR_FOUNDATION
			if (_bodyStates.TryGetValue(body, out BodyState state))
			{
				visibility = state.Visibility;
				return visibility.Measured;
			}
#endif
			visibility = default;
			return false;
		}

		/// <summary>
		/// Whether this body is currently held in the world frame because it is out
		/// of view, and how many times a return to view has seeded the tracker.
		/// </summary>
		/// <summary>
		/// How many render-frame callbacks this manager has received, and how many
		/// times a body's display filter has advanced.
		/// </summary>
		/// <remarks>
		/// The display filter advances from the render callback and nowhere else.
		/// On a host that renders no frames the callback never arrives, and the
		/// displayed pose is then whatever it last was — which is correct behaviour
		/// and invisible from the pose alone. These counters make it visible.
		/// </remarks>
		internal bool TryGetDisplayAdvanceCounts(TrackedBody body,
			out int renderFrameCallbacks, out int displayFilterAdvances)
		{
			renderFrameCallbacks = _renderFrameCallbacks;
#if HAS_AR_FOUNDATION
			if (_bodyStates.TryGetValue(body, out BodyState state))
			{
				displayFilterAdvances = state.DisplayFilterAdvances;
				return true;
			}
#endif
			displayFilterAdvances = 0;
			return false;
		}

		/// <summary>
		/// What the lifecycle decided about this body on the frame it last
		/// ran, as the decisions themselves recorded it.
		/// </summary>
		/// <remarks>
		/// Kept after a frame that gave up on the body, whose state this manager has
		/// already dropped. Nothing in this package reads it.
		/// </remarks>
		internal bool TryGetBodyLifecycleDecision(TrackedBody body,
			out BodyLifecycleDecision decision)
		{
			if (body != null && _lastLifecycleDecision.TryGetValue(body, out decision))
				return true;
			decision = default;
			return false;
		}

		/// <summary>
		/// The pose this body is being held at while it is not tracking, in world
		/// space, and whether there is one.
		/// </summary>
		/// <remarks>
		/// The stored solve when there is one, and the pose the body was placed at
		/// when there is not. It is what the tracker is put back on, so it is
		/// reported as one value rather than leaving a reader to work out which of
		/// the two applies.
		/// </remarks>
		internal bool TryGetHeldWorldPose(TrackedBody body, out Vector3 position,
			out Quaternion rotation, out bool fromStoredSolve)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			fromStoredSolve = false;
			if (body == null)
				return false;
			if (TryGetStoredTrackerPose(body, out position, out rotation))
			{
				fromStoredSolve = true;
				return true;
			}
			if (!body.HasRetainedInitializationPose)
				return false;
			body.ReadWorldPoseForDecision(out position, out rotation);
			return true;
		}

		/// <summary>
		/// The two inputs the verification window counts frames on, and whether a
		/// view was described for this frame at all.
		/// </summary>
		/// <remarks>
		/// All three are computed only while the relative-health authority is on.
		/// With it off they are false because they were never measured, which is a
		/// different statement from a frame that measured them and failed, and the
		/// only way to tell the two apart from outside is to read them.
		/// </remarks>
		internal bool TryGetVerificationWindowInputs(TrackedBody body,
			out bool poseSettled, out bool coldStartWindow, out bool coldStartSatisfied,
			out bool hasCurrentView)
		{
			poseSettled = false;
			coldStartWindow = false;
			coldStartSatisfied = false;
			hasCurrentView = false;
			if (body == null || !_bodyStates.TryGetValue(body, out BodyState state))
				return false;
			poseSettled = state.PoseSettled;
			coldStartWindow = state.ColdStartWindow;
			coldStartSatisfied = state.ColdStartSatisfied;
			hasCurrentView = state.HasCurrentView;
			return true;
		}

		internal bool TryGetBodyCoastState(TrackedBody body,
			out WorldFrameCoastPhase phase, out int reentrySeeds, out int seedWriteFailures)
		{
#if HAS_AR_FOUNDATION
			if (_bodyStates.TryGetValue(body, out BodyState state))
			{
				phase = state.CoastPhase;
				reentrySeeds = state.CoastReentrySeeds;
				seedWriteFailures = state.CoastSeedWriteFailures;
				return true;
			}
#endif
			phase = WorldFrameCoastPhase.Inactive;
			reentrySeeds = 0;
			seedWriteFailures = 0;
			return false;
		}

		/// <summary>
		/// What the fusion step did for a body that is held in the world frame, for diagnostics. It reports how many steps the first accept waited for
		/// the solve to stand still, how many times the tracker was put back at the held pose, and whether the newest step was accepted on the
		/// coverage score.
		/// </summary>
		internal bool TryGetFusionCounters(TrackedBody body, out int firstAcceptRowsWaited, out int predictionReseeds, out bool softAcceptedThisStep)
		{
			firstAcceptRowsWaited = 0;
			predictionReseeds = 0;
			softAcceptedThisStep = false;
			if (body == null || !_bodyStates.TryGetValue(body, out BodyState state))
				return false;
			firstAcceptRowsWaited = state.FirstAcceptRowsWaited;
			predictionReseeds = state.PredictionReseeds;
			softAcceptedThisStep = state.SoftAcceptedThisStep;
			return true;
		}

		/// <summary>What the newest fusion step of a stationary body did with its solve, for diagnostics.</summary>
		internal bool TryGetFusionStep(TrackedBody body, out FusionStepRecord record)
		{
			record = default;
			if (body == null || !_bodyStates.TryGetValue(body, out BodyState state))
				return false;
			record = state.Step;
			return true;
		}

		/// <summary>The pose the display of a stationary body converges on, in world space, and whether there is one.</summary>
		internal bool TryGetSettledWorldPose(TrackedBody body, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (body == null || !_bodyStates.TryGetValue(body, out BodyState state) || !state.HasSettledPose)
				return false;
			position = state.SettledPosition;
			rotation = state.SettledRotation;
			return true;
		}

		/// <summary>
		/// Returns the per-body hold seed schedule counters.
		/// </summary>
		internal bool TryGetHoldSeedScheduleTelemetry(
			TrackedBody body, out HoldSeedScheduleTelemetry telemetry)
		{
			if (body != null && _bodyStates.TryGetValue(body, out BodyState state))
			{
				telemetry = new HoldSeedScheduleTelemetry(
					state.HoldActive,
					state.HoldSeedAttemptsThisHold);
				return true;
			}
			telemetry = default;
			return false;
		}

		#endregion

		#region Private Fields

		private readonly Dictionary<TrackedBody, BodyState> _bodyStates = new();
		private readonly Dictionary<TrackedBody, StationaryCorrectionTelemetry>
			_lastCorrectionTelemetry = new();
		// Kept beside the body state rather than inside it, because a frame that
		// gives up on a body drops that body's state and the decision to give up is
		// the one a reader most needs to see.
		private readonly Dictionary<TrackedBody, BodyLifecycleDecision>
			_lastLifecycleDecision = new();
		private readonly List<TrackedBody> _bodiesToRemove = new();

		// The pose of each child with free motion relative to its parent in the tracker's own solve, read before the frame is processed,
		// while the tracker still holds the two poses it solved together.
		private readonly Dictionary<TrackedBody, Pose> _childRelativePoses = new();
		private XRTrackerManager.TrackingFrameInstrumentationContext?
			_completedFrameInstrumentationContext;
		private bool _hasLastStatusRefreshToken;
		private string _lastStatusRefreshSequenceId;
		private int _lastStatusRefreshFrameIndex;
		private long _lastStatusRefreshTimestampNs;
		private TrackedBodyStatusReader _completedStatusReader =
			FTBridge.FT_GetBodyStatus;
		private int _renderFrameCallbacks;
		private int _completedTemporalFrameIndex = -1;
		private long _completedTemporalStepSerial;
		private long _temporalFallbackStepSerial;
		private TemporalTrackingPolicy _temporalTrackingPolicy =
			TemporalTrackingPolicy.LiveCalibrationCandidate;
		/// <remarks>
		/// Held beside the body rather than inside its per-frame state, because a
		/// body that loses tracking and starts over has not stopped being the same
		/// body seen from the same directions. What a view normally achieves is
		/// knowledge about the object, and a restart is not evidence against it.
		/// </remarks>
		private readonly Dictionary<TrackedBody, ViewExpectationStore> _viewExpectations = new();

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
		/// Whether each displayed pose is tagged with the source it came from.
		/// </summary>
		/// <summary>
		/// Whether AR pose fusion is available and enabled.
		/// </summary>
#if HAS_AR_FOUNDATION
		public bool IsARPoseFusionActive
		{
			get
			{
				var manager = XRTrackerManager.Instance;
				if (manager?.UseARPoseFusion != true)
					return false;
				return _arPoseFusion.IsAvailable ||
					manager.TryGetSubmittedFusionFrameContext(out _) ||
					manager.TryGetCompletedFusionFrameContext(out _);
			}
		}

		/// <summary>
		/// Access to the underlying pose-fusion helper. Used by XRTrackerManager to reach the AR space provider.
		/// </summary>
		internal ARPoseFusion ARPoseFusion => _arPoseFusion;
#else
		public bool IsARPoseFusionActive => false;
#endif

		/// <summary>
		/// Number of bodies currently being managed.
		/// </summary>
		internal int ManagedBodyCount => _bodyStates.Count;

		/// <summary>
		/// Global histogram peak observed across all root bodies. Used as reference for quality calculations.
		/// Adapts to lighting conditions. Clamped between PEAK_MIN and PEAK_MAX.
		/// Resets to HISTOGRAM_GOOD when no root body is tracking.
		/// </summary>
		internal float GlobalHistogramPeak => _globalHistogramPeak;

		/// <summary>
		/// Number of bodies currently in AR mode.
		/// </summary>
		internal int BodiesInARMode
		{
			get
			{
				int count = 0;
				foreach (var (body, _) in _bodyStates)
					if (RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive))
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
				manager.OnBodyUnregistered -= OnBodyUnregistered;
				manager.OnBodyUnregistered += OnBodyUnregistered;
			}
		}

		private void UnsubscribeFromTrackingManager()
		{
			var manager = XRTrackerManager.Instance;
			if (manager != null)
			{
				manager.OnAfterTrackingStep -= OnAfterTrackingStep;
				manager.OnBodyUnregistered -= OnBodyUnregistered;
			}
		}

		private void OnBodyUnregistered(TrackedBody body)
		{
			if (body == null)
				return;
			if (!_bodyStates.TryGetValue(body, out BodyState state))
				state = new BodyState();
			body.SetRegistrationQuality(RegistrationQualitySnapshot.Unsupported);
			var input = new TemporalTrackingInput(_completedTemporalFrameIndex,
				_completedTemporalStepSerial, false, body.RegistrationQuality,
				TemporalRecoveryPhase.None, 0);
			body.SetTemporalTrackingStatus(TemporalTrackingMachine.Decide(
				ref state.TemporalTracking, input, _temporalTrackingPolicy));
			_bodyStates.Remove(body);
			_lastCorrectionTelemetry.Remove(body);
			_lastLifecycleDecision.Remove(body);
			_viewExpectations.Remove(body);
			_declaredRecoveryPhases.Remove(body);
			_seenPoses.Remove(body);
			_seatMatrices.Remove(body);
		}

		/// <summary>
		/// Called immediately after native tracking step completes.
		/// For async tracking (Native, Injected) this fires from Update() on the next frame.
		/// All pose reading happens here so native results are guaranteed to be ready.
		/// </summary>
		private void OnAfterTrackingStep()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null)
				return;
			ProcessAllBodies(manager.CompletedStepRegisteredBodies);
		}

		private void OnDestroy()
		{
			_lastCorrectionTelemetry.Clear();
			_completedFrameInstrumentationContext = null;
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
			// A host that drives the tracking frames advances the display itself, once per
			// frame it drives and after that frame's step has been processed. Rendered frames
			// do not arrive one per driven frame and are not ordered against the step at all,
			// so advancing here as well would break the single-call contract below and make
			// the retention of the display filter depend on how many frames happened to be
			// rendered. The host's call is the one that is ordered, so it is the one kept.
			var tracker = XRTrackerManager.Instance;
			if (tracker != null && tracker.DeterministicReplayOwnsPlayback)
				return;

			AdvanceDisplayForRenderedFrame();
		}

		/// <summary>
		/// Advances every tracked body's display by one frame: the detection-phase
		/// camera follow, and the display filter for stationary bodies under AR pose
		/// fusion.
		/// </summary>
		/// <remarks>
		/// <para>This runs automatically once per rendered frame, from the engine's
		/// before-render callback, and that is the only time a normal application
		/// needs it. It is public for one case: a host that drives tracking frames
		/// itself and renders none — a deterministic replay — where the callback
		/// never arrives and the displayed pose would otherwise never advance at
		/// all. Such a host calls this exactly once per frame it drives, after the
		/// tracking step for that frame has been processed.</para>
		/// <para>Calling it more than once per frame is the defect this method's
		/// single-call contract exists to prevent: the filter's retention becomes
		/// <c>(1-alpha)^n</c> and its time constant stops meaning what it says.</para>
		/// </remarks>
		internal void AdvanceDisplayForRenderedFrame()
		{
			++_renderFrameCallbacks;
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;

			foreach (var body in manager.TrackedBodies)
			{
				if (body == null || !body.IsRegistered || !body.IsActiveMode || !RunsOwnLifecycle(body.HasParent))
					continue;

				if (!body.IsTracking)
				{
					// Detection phase: follow camera every render frame
#if HAS_AR_FOUNDATION
					if (manager.UseARPoseFusion)
					{
						if (manager.TryGetCompletedFusionFrameContext(
							out FusionFrameContext completedContext))
						{
							body.ApplyDetectorPose(completedContext);
						}
						else if (manager.TryGetSubmittedFusionFrameContext(
							out FusionFrameContext submittedContext))
						{
							body.ApplyDetectorPose(submittedContext);
						}
					}
					else
#endif
					{
						body.ApplyDetectorPose();
					}
				}
#if HAS_AR_FOUNDATION
				else if (RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive)
					&& _bodyStates.TryGetValue(body, out var state))
				{
					// Tracking phase: the display filter advances here and nowhere
					// else, once per rendered frame. The stored target it advances
					// towards is updated per tracking step in OnAfterTrackingStep,
					// which runs earlier in this same frame.
					FusionFrameContext? frameContext = manager.TryGetCompletedFusionFrameContext(
						out FusionFrameContext completedContext)
						? completedContext
						: null;
					AdvanceDisplayFilter(body, state, frameContext);
				}
#endif
			}
		}

		private void ProcessAllBodies(IReadOnlyList<TrackedBody> trackedBodies)
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized || trackedBodies == null)
				return;

			// While the plugin reports the license frozen it returns poses that are not usable, so the state is
			// reported once each time it changes and not on every frame.
#pragma warning disable CS0618 // The frozen state belongs to the retired tiers, which the plugin can still report.
			bool licenseFrozen = manager.IsLicenseFrozen;
#pragma warning restore CS0618
			if (licenseFrozen != _wasLicenseFrozen)
			{
				_wasLicenseFrozen = licenseFrozen;
				if (licenseFrozen)
					Debug.LogWarning("[XRTracker] The license does not allow tracking at this time, so tracking is degraded.");
				else
					Debug.Log("[XRTracker] The license allows tracking again.");
			}

#if HAS_AR_FOUNDATION
			FusionFrameContext? frameContext = manager.TryGetCompletedFusionFrameContext(
				out FusionFrameContext completedContext)
				? completedContext
				: null;
#endif

			CaptureChildRelativePoses(trackedBodies);

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

				// A child never starts on its own, because it is a part of its parent. Whatever directions it has free, it is tracked while its
				// parent is tracked and stopped when its parent stops, and only a body without a parent runs the lifecycle of a body of its own.
				if (RunsOwnLifecycle(body.HasParent))
				{
					ProcessMainBody(body, state
#if HAS_AR_FOUNDATION
						, frameContext
#endif
					);
				}
				else
					ProcessChildBody(body, state);
			}

			// Clean up
			foreach (TrackedBody body in _bodiesToRemove)
			{
				_bodyStates.Remove(body);
				_lastCorrectionTelemetry.Remove(body);
				_lastLifecycleDecision.Remove(body);
				_declaredRecoveryPhases.Remove(body);
				_seenPoses.Remove(body);
				_seatMatrices.Remove(body);
			}
			_bodiesToRemove.Clear();
		}

		#endregion

		#region Active Mode Processing

		private void ProcessMainBody(
			TrackedBody body,
			BodyState state
#if HAS_AR_FOUNDATION
			, FusionFrameContext? frameContext
#endif
			)
		{
			UpdateTrackingHealth(body, state);

			// Opened here rather than added to, so a comparison reported as not made
			// is one this frame did not make rather than one an earlier frame did.
			state.Decision = default;
			state.Decision.IsTracking = body.IsTracking;

			if (RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive))
			{
#if HAS_AR_FOUNDATION
				state.Decision.Path = LifecyclePath.StationaryFusion;
				ProcessStationary(body, state, frameContext);
#else
				state.Decision.Path = LifecyclePath.Standard;
				ProcessStationary(body, state);
#endif
			}
			else
			{
				// Non-stationary, or stationary without AR Foundation (fallback)
				ProcessBodyNoAR(body, state);

				// This path applies the solved pose on the frame it is produced and never
				// holds an earlier one, so the fusion state follows tracking directly. It is
				// read only for reporting, so writing it here changes no behavior.
				state.FusionState = body.IsTracking
					? BodyFusionState.Accepting
					: BodyFusionState.NotTracking;

				// A body that is still searching stays drawn at its seat. The tracker's refined pose is
				// read for the start decision above and is not shown, because a drawn pose is taken up
				// as the next seat and would carry the refinement into the next step. Under pose fusion
				// that holds for a body that is not stationary until its first start, as it does for a
				// stationary one, so a guide that stands far from the object is not walked onto whatever
				// fits and started there.
				bool holdsSeat = ShippedDefaults.HoldStartPose(ShippedDefaults.AppliesTo(body, this)) ||
					ShippedDefaults.HoldStartPoseUnderFusion(ShippedDefaults.AppliesUnderFusion(body, this),
						body.StartedSinceRegistration);
				if (!HoldsStartPose(holdsSeat, state.Decision.IsTracking, body.IsTracking))
					ApplyNativePose(body, state);

				// The poses a body was accepted at are kept for the recovery of an edge body. A body that tracks by the
				// silhouette method has no recovery, so it keeps none.
				if (EdgeLayerApplies(SeenPoseRecovery, body) && state.Decision.Path == LifecyclePath.Standard)
					RecordSeenPose(body);
			}

			UpdateTrackingConfidence(body, state);
			UpdateTemporalTrackingStatus(body, state);
			LearnViewExpectation(body, state);
			FireEventsIfChanged(body, state);

			// A comparison records the counts the threshold check produced, which is
			// what decided. Only a frame that made no comparison reports the counts
			// as they stand at the end of it.
			if (!state.Decision.Acquire.Ran && !state.Decision.Hold.Ran)
			{
				state.Decision.GoodFrameCount = state.GoodFrameCount;
				state.Decision.BadFrameCount = state.BadFrameCount;
			}
			state.Decision.FramesSinceTrackingStarted = state.FramesSinceTrackingStarted;
			state.Decision.InWarmup =
				state.FramesSinceTrackingStarted <= TrackerDefaults.STABILIZATION_FRAMES;
			_lastLifecycleDecision[body] = state.Decision;
		}

		/// <summary>
		/// Advances a body's three-state machine and records what it produced.
		/// </summary>
		/// <remarks>
		/// <para>This runs after the frame's own processing, so every input it reads
		/// is the value that frame's decisions actually produced rather than the one
		/// they started from.</para>
		/// <para>It reads, records and publishes. No decision in this manager
		/// consumes what it produces: the state reaches the application and nothing
		/// else, and the lifecycle it sits beside is still recorded so the two remain
		/// comparable on every frame. It reads the same score and the same bars that
		/// decide the lifecycle, and never the tracking health.</para>
		/// </remarks>
		private void UpdateTrackingConfidence(TrackedBody body, BodyState state)
		{
			ReadLossQuarantineState(body, state);
			bool heldOutOfView = false;
#if HAS_AR_FOUNDATION
			// What the system is doing, not what the geometry says. With the coast
			// disabled a body outside the view is held by the ordinary quality gate
			// and is on its way to being lost like any other unverified body, and the
			// state reports that rather than the patience a coast would have granted.
			// The geometry has its own recorded channel and is not read here.
			heldOutOfView = state.CoastPhase == WorldFrameCoastPhase.Coasting;
#endif
			// The report reads the same score against the same start, confirm and stop bars that decide
			// IsTracking, and nothing else. The last frame of a start has the confirm bar when it is above the start bar.
			float acquireSignal = body.SignalFor(QualityDecision.Acquire);
			float holdSignal = body.SignalFor(QualityDecision.Hold);
			QualityBand band = body.DecisionBand;
			float confirmBar = body.ConfirmBar;
			if (body.StartReadsCoverage)
			{
				// The start reads the coverage score at its own bar, so the report reads that pair and keeps the margin for the hold.
				acquireSignal = body.StartSignal;
				band = QualityBand.Declared(body.StartBar, band.Hold, band.Accept);
				confirmBar = body.StartBar;
				// With the coverage score deciding, the hold is the stop of a body without pose fusion: the averaged coverage score against the stop bar.
				// A body whose coverage score does not hold the lock keeps the margin for it.
				if (CoverageHoldsFor(body))
				{
					holdSignal = body.StopSignal;
					band = QualityBand.Declared(body.StartBar, body.StopBar, band.Accept);
				}
			}
			// A solve that the soft accept verified is a solve that became the body's pose, and the report follows that acceptance. Its holding
			// reading stands at the holding bar for the frame, so a body that is accepted this way reads Tracked and is not given up on a margin that
			// its picture cannot reach.
			if (state.SoftAcceptedThisStep)
				holdSignal = System.Math.Max(holdSignal, band.Hold);
			TrackingConfidenceTransition transition = TrackingConfidenceMachine.Decide(
				ref state.Confidence,
				body.IsTracking,
				state.FramesSinceTrackingStarted <= TrackerDefaults.STABILIZATION_FRAMES,
				(state.FusionState == BodyFusionState.Accepting || state.SoftAcceptedThisStep)
					&& state.LossQuarantine.AllowsPublishing,
				heldOutOfView,
				acquireSignal,
				holdSignal,
				band,
				TrackerDefaults.FRAMES_TO_LOSE,
				TrackerDefaults.FRAMES_TO_START,
				System.Math.Max(band.Acquire, confirmBar));
#if HAS_AR_FOUNDATION
			transition = HoldReadoutForDisplay(body, state, transition);
#endif
			state.ConfidenceReason = transition.Reason;
			body.SetConfidence(transition);
		}

#if HAS_AR_FOUNDATION
		/// <summary>
		/// Whether the readout of this body waits for the pose on screen to reach the stored fit. It does for a top-level stationary edge body under pose
		/// fusion that the switch serves.
		/// </summary>
		private bool ReadoutWaitsForDisplayFor(TrackedBody body) =>
			RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive)
			&& ShippedDefaults.ReadoutWaitsForDisplay(ShippedDefaults.AppliesUnderFusion(body, this), body.AcceptSteadyFits);

		/// <summary>
		/// Keeps a body that has an accepted fit at <see cref="TrackingConfidence.Degraded"/> until the pose on screen has first come within the still distance
		/// of the settle pull of the stored fit. It reads the pose on screen and writes the readout, and nothing else.
		/// </summary>
		/// <remarks>
		/// The distance is the median on-screen displacement of the model's points between the pose on screen and the stored fit, measured from the scene
		/// camera as the settle pull measures the distance between the settled pose and a new fit. A pose on screen that the filter has not drawn yet is
		/// the pose the transform holds, which is where the filter starts from. It is measured only while the wait is pending, and a distance that could not
		/// be measured ends the wait, so that a body without a camera is never held back.
		/// </remarks>
		private TrackingConfidenceTransition HoldReadoutForDisplay(TrackedBody body, BodyState state, TrackingConfidenceTransition transition)
		{
			if (!ReadoutWaitsForDisplayFor(body))
				return transition;
			state.Step.ReadoutHeldForDisplay = false;
			state.Step.DisplayToFitPixels = -1f;
			float pixels = float.NaN;
			if (state.HasLastGoodPose && !state.SeatSettledOnNextStore && state.DisplayCatchUpPending)
			{
				Vector3 shownPosition;
				Quaternion shownRotation;
				if (state.HasDisplayPose)
				{
					shownPosition = state.DisplayPosition;
					shownRotation = state.DisplayRotation;
				}
				else
				{
					Transform shown = body.transform;
					shownPosition = shown.position;
					shownRotation = shown.rotation;
				}
				// The first measurement samples the points of the model from the pose the body stands at, and the sampling rounds differently at another
				// pose. The readout leaves it to the measurements that decide, which sample on the steps after the first store, and until they have it
				// waits, unless nothing could be measured at all because there is no camera.
				if (!body.SearchWalkPointsSampled)
				{
					XRTrackerManager scene = XRTrackerManager.Instance;
					bool canMeasure = scene != null && scene.CameraTransform != null && scene.CachedFu > 0f;
					pixels = canMeasure ? float.PositiveInfinity : float.NaN;
				}
				else
				{
					BindSettleDistance(body);
					pixels = MeasureSettleDistance(shownPosition, shownRotation, state.LastGoodPosition, state.LastGoodRotation);
					state.Step.DisplayToFitPixels = float.IsNaN(pixels) ? -1f : pixels;
				}
			}
			bool behind = ShownPoseBehindFit(state.HasLastGoodPose, state.SeatSettledOnNextStore, ref state.DisplayCatchUpPending, pixels);
			if (!behind || transition.State != TrackingConfidence.Tracked)
				return transition;
			state.Step.ReadoutHeldForDisplay = true;
			return TrackingConfidenceMachine.HoldForDisplay(ref state.Confidence, transition);
		}
#endif

		/// <summary>
		/// Whether the pose on screen is still behind the stored fit. It is behind while no fit is stored, which is when an accepted solve of the body has
		/// not been stored yet and the guide is what is on screen, and while a seat is due, which is when the lock was given up and the next stored fit seats
		/// the display. After a fit has seated the display it stays behind until the distance on screen between the two has first come to the still distance
		/// of the settle pull or less, and then it is not behind again until another fit seats the display.
		/// </summary>
		/// <param name="hasStoredFit">Whether the body holds a stored fit.</param>
		/// <param name="seatDue">Whether the next stored fit seats the settled pose.</param>
		/// <param name="catchUpPending">Whether a fit has seated the display and the pose on screen has not yet reached it. It is cleared here.</param>
		/// <param name="shownToFitPixels">The distance on screen between the pose on screen and the stored fit, or not a number when it was not measured.</param>
		internal static bool ShownPoseBehindFit(bool hasStoredFit, bool seatDue, ref bool catchUpPending, float shownToFitPixels)
		{
			if (!hasStoredFit || seatDue)
				return true;
			if (!catchUpPending)
				return false;
			if (float.IsNaN(shownToFitPixels) || shownToFitPixels <= TrackerDefaults.STATIONARY_SETTLE_STILL_PIXELS)
			{
				catchUpPending = false;
				return false;
			}
			return true;
		}

		#region Relative Tracking Health

		/// <summary>
		/// Whether relative tracking health describes this body's lifecycle.
		/// </summary>
		/// <remarks>
		/// Health is formed from the edge evidence record: agreeing sites over
		/// expected-visible sites, read against what the view normally achieves. A
		/// body tracked by the silhouette method carries no such record, so the ratio
		/// is never formed, no view ever gains an expectation, and a lifecycle driven
		/// from them would sit in its opening state for a whole session while the body
		/// tracked perfectly well. Asking the method here keeps the description on the
		/// bodies whose evidence can support one, and leaves every other body reading
		/// what it read before this measurement existed.
		/// </remarks>
		internal static bool HealthAuthority(TrackedBody body) =>
			body != null && body.UseTrackingHealth &&
			body.TrackingMethod == TrackingMethod.Edge;

		/// <summary>Whether this frame's solve may become the body's pose.</summary>
		/// <remarks>
		/// The reading is the decision band against this frame's own quality scalar,
		/// and nothing else. Health describes the lifecycle and is not asked here, so
		/// a body whose health is being measured accepts exactly the solves it would
		/// accept if nothing were measuring it.
		/// </remarks>
		private static bool ClearsAccept(TrackedBody body)
		{
			return body.DecisionBand.Clears(QualityDecision.Accept,
				body.SignalFor(QualityDecision.Accept));
		}

		/// <summary>
		/// Whether a solve that cleared the accept bar may become the reference the
		/// display converges on and the hold preserves.
		/// </summary>
		/// <remarks>
		/// It is refused exactly when the release loop counts the frame toward
		/// giving the lock up. Those frames are taken while the body is leaving, so
		/// a reference stored from one is the pose the hold then parks on.
		/// </remarks>
		internal static bool MayBecomeReference(bool holdReleaseEnabled, bool coldStartWindow,
			bool warmup, float instantQuality, float holdReleaseBar)
		{
			return !holdReleaseEnabled || coldStartWindow || warmup
				|| instantQuality >= holdReleaseBar;
		}

		/// <summary>
		/// The reading that decides whether an accepted solve may become the stored reference.
		/// </summary>
		/// <remarks>
		/// Under the mode that guards the stored reference, and only when decisions read the
		/// registration margin, it is the weaker of the accept reading and the margin the plugin
		/// published. The accept reading is the median margin, the weaker of the alignment and
		/// median terms, and the published margin is the weaker of the same alignment term and the
		/// whole-pixel 90th-percentile term, so the result is the weakest of the three. The median
		/// can clear the bar on a grossly rotated solve that the percentile refuses, and a solve
		/// stored as the reference is the pose every later give-up parks the display on, so the
		/// reference has to clear on both. A record with no published score reads zero. Under every
		/// other term, and under any other signal source, the accept reading is returned unchanged.
		/// </remarks>
		internal static float ReferenceReadingFor(TrackerDefaults.MarginResidualTerm term,
			TrackerDefaults.QualitySignalSource source, float acceptReading,
			in RegistrationQualitySnapshot record)
		{
			if (term != TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference ||
				source != TrackerDefaults.QualitySignalSource.RegistrationMargin)
				return acceptReading;
			float published = record.ScoreAvailable ? record.Score : 0f;
			return Mathf.Min(acceptReading, published);
		}

		/// <summary>
		/// Records the holding reading a path just took, exactly as it took it.
		/// </summary>
		private void RecordHoldReading(TrackedBody body, BodyState state, bool belowHold)
		{
			state.Decision.Hold = CoverageHoldsFor(body)
				? LifecycleComparison.Taken(body.StopSignal, body.StopBar, QualitySignal.Averaged, !belowHold)
				: LifecycleComparison.Taken(
					body.SignalFor(QualityDecision.Hold),
					body.DecisionBand.ThresholdFor(QualityDecision.Hold),
					QualityBand.SignalFor(QualityDecision.Hold),
					!belowHold);
		}

		/// <summary>
		/// Whether this body also accepts and stores a fit on the coverage score and the start and stop bars, as a body without pose fusion does,
		/// whatever the registration margin reads, and lets the coverage score hold its lock. A stationary body follows its Accept Steady Fits setting,
		/// and a body that is not stationary has no such setting to follow.
		/// </summary>
		private bool CoverageDecidesFor(TrackedBody body)
		{
			if (ReferenceEquals(body, null) || !body.StartReadsCoverage)
				return false;
			bool bodySetting = !RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive) || body.AcceptSteadyFits;
			return ShippedDefaults.CoverageDecides(ShippedDefaults.AppliesUnderFusion(body, this), bodySetting);
		}

		/// <summary>
		/// Whether the coverage score holds the lock of this body, which is when the coverage score decides and the averaged score stands at the stop
		/// bar. The registration margin keeps holding the lock as well, so an object whose coverage reads low (one that is partly hidden) is not
		/// given up while the margin still reads a good fit, and the lock is given up only when neither reads it.
		/// </summary>
		private bool CoverageHoldsFor(TrackedBody body) => CoverageDecidesFor(body) && body.StopSignal >= body.StopBar;

		/// <summary>
		/// Records the acceptance reading the stationary path just took.
		/// </summary>
		private static void RecordAcceptReading(TrackedBody body, BodyState state, bool clears)
		{
			state.Decision.Accept = LifecycleComparison.Taken(
				body.SignalFor(QualityDecision.Accept),
				body.DecisionBand.ThresholdFor(QualityDecision.Accept),
				QualityBand.SignalFor(QualityDecision.Accept),
				clears);
		}

		/// <summary>Whether tracking is still held rather than declared lost.</summary>
		/// <remarks>
		/// The reading is the decision band against the averaged quality scalar. A
		/// loss is a claim about a stretch of frames rather than about one of them,
		/// and the frame counter at the call site is what turns a run of poor
		/// readings into a stop.
		/// </remarks>
		private static bool ClearsHold(TrackedBody body)
		{
			return body.DecisionBand.Clears(QualityDecision.Hold,
				body.SignalFor(QualityDecision.Hold));
		}

		/// <summary>The per-body expectation store, created on first use.</summary>
		private ViewExpectationStore GetViewExpectations(TrackedBody body)
		{
			if (!_viewExpectations.TryGetValue(body, out ViewExpectationStore store))
			{
				store = new ViewExpectationStore();
				_viewExpectations[body] = store;
			}
			return store;
		}

		/// <summary>Reads what one body has learned about its own viewpoints.</summary>
		internal bool TryGetViewExpectations(TrackedBody body,
			out ViewExpectationStore store)
		{
			store = null;
			return body != null && _viewExpectations.TryGetValue(body, out store);
		}

		/// <summary>
		/// Measures this frame's site-funnel ratio and what the current view is
		/// expected to reach, and records the two inputs the verification window
		/// takes beyond that.
		/// </summary>
		/// <remarks>
		/// This runs before any decision in the frame, so every decision that reads
		/// health reads the same value, and it runs on every body so the readout is
		/// there whether or not the setting is on. With the setting off it stops at
		/// the ratio: the viewpoint, the expectation and the settle bound all cost
		/// an extra pose read that a body with the setting off does not need.
		/// </remarks>
		private void UpdateTrackingHealth(TrackedBody body, BodyState state)
		{
			RegistrationQualitySnapshot quality = body.RegistrationQuality;
			TrackingHealthSettings settings = body.TrackingHealthSettings;
			// The site-count precondition is asked of both numerators, so changing
			// the numerator changes what a frame measures and never which frames
			// carry a measurement at all. The score adds only its own availability
			// on top: a score that was not computed is absent, not a low one.
			bool rawAvailable = quality.FeatureSupported &&
				quality.ExpectedSupport >= settings.MinimumExpectedSites;
			float raw = 0f;
			if (rawAvailable)
			{
				if (settings.HealthNumerator == HealthNumerator.RegistrationScore)
				{
					rawAvailable = quality.ScoreAvailable;
					raw = rawAvailable ? quality.Score : 0f;
				}
				else
				{
					raw = (float)quality.AgreeingSupport / quality.ExpectedSupport;
				}
			}

			if (!HealthAuthority(body))
			{
				state.HasCurrentView = false;
				state.HasPreviousWorldPose = false;
				state.PoseSettled = false;
				state.ColdStartWindow = false;
				state.ColdStartSatisfied = false;
				body.SetTrackingHealth(rawAvailable
					? TrackingHealthSnapshot.FromRaw(raw)
					: TrackingHealthSnapshot.Unavailable);
				return;
			}

			state.HasCurrentView = TryDescribeCurrentView(body,
				out RecoveryViewDescriptor view);
			state.CurrentView = view;
			state.PoseSettled = UpdatePoseSettling(body, state);

			float expected = 0f;
			var source = ViewExpectationSource.None;
			bool hasExpectation = state.HasCurrentView &&
				GetViewExpectations(body).TryLookup(view, settings.ViewBearingDegrees,
					settings.ViewRangeRatio, out expected, out source);
			float appearanceGuard = TrackingHealthSnapshot.AppearanceGuardFor(
				quality.AppearanceEvidenceAvailable,
				quality.RawEvidence.appearance_agreement, settings.AppearanceFloor);
			body.SetTrackingHealth(!rawAvailable
				? TrackingHealthSnapshot.Unavailable
				: hasExpectation
					? TrackingHealthSnapshot.FromRatio(raw, expected, source,
						settings.HealthCeiling, appearanceGuard)
					: TrackingHealthSnapshot.FromRaw(raw));

			state.ColdStartWindow = !hasExpectation;
			state.ColdStartSatisfied = state.ColdStartWindow &&
				AdvanceColdStartWindow(state, settings, rawAvailable, raw);
		}

		/// <summary>
		/// Describes the viewpoint the tracker currently holds this body from.
		/// </summary>
		/// <remarks>
		/// The pose is read in the body frame rather than the geometry frame. A
		/// mesh that was re-centred at set-up has the two frames metres apart, and
		/// keying viewpoints by the geometry frame would put two genuinely different
		/// views under one key on one body and split one view across two on another.
		/// </remarks>
		private static bool TryDescribeCurrentView(TrackedBody body,
			out RecoveryViewDescriptor view)
		{
			var cameraFromBody = Matrix4x4.identity;
			view = default;
			if (!PoseVerification.TryGetCameraFromBodyPose(body.BodyId,
				out float[] rowMajor))
			{
				return false;
			}
			for (int row = 0; row < 4; ++row)
			{
				for (int column = 0; column < 4; ++column)
					cameraFromBody[row, column] = rowMajor[row * 4 + column];
			}
			return RecoveryViewDescriptor.TryDescribe(cameraFromBody, out view);
		}

		/// <summary>
		/// Whether the pose moved little enough between the last two frames for this
		/// frame to count toward acquisition.
		/// </summary>
		/// <remarks>
		/// <para>The step is measured on the pose itself rather than on the evidence,
		/// because the two failures being separated look the same in the evidence. A
		/// start from a wrong pose can hold better static evidence than a start from
		/// the right one; what it cannot do is stay still, because nothing in the
		/// image holds it.</para>
		/// <para>It is a step from the previous frame rather than a drift from where
		/// the run began. A drift from a fixed anchor would be the better statistic
		/// in principle, but a wrong start can settle against an anchor, whereas
		/// measured from one frame to the next it never settles. The step is the
		/// statistic that refuses the wrong start, so it is the one used.</para>
		/// <para>It is measured in the world frame and not in the camera's. A camera
		/// carried by hand turns about two degrees between frames, so the same
		/// perfectly tracked body moves that far in the camera's view every frame,
		/// and a bound tight enough to mean anything is one a handheld camera never
		/// satisfies. What the bound is asking about is the pose, so it asks in the
		/// frame the pose is standing still in. That frame exists because the body
		/// itself is not moving; for a body that is, this measures the body's motion
		/// and the bound has to be read as one.</para>
		/// <para>The translation bound is a fraction of the body's own size, so one
		/// number covers a part in the hand and a machine across a room. A body whose
		/// size cannot be measured is judged on rotation alone rather than being
		/// refused for ever by a bound of zero.</para>
		/// </remarks>
		private bool UpdatePoseSettling(TrackedBody body, BodyState state)
		{
			if (!TryGetNativeWorldPose(body, out Vector3 position,
				out Quaternion rotation))
			{
				state.HasPreviousWorldPose = false;
				return false;
			}

			Vector3 previousPosition = state.PreviousWorldPosition;
			Quaternion previousRotation = state.PreviousWorldRotation;
			bool hadPrevious = state.HasPreviousWorldPose;
			state.PreviousWorldPosition = position;
			state.PreviousWorldRotation = rotation;
			state.HasPreviousWorldPose = true;
			if (!hadPrevious)
				return false;

			if (state.BodyDiameterMeters <= 0f)
				state.BodyDiameterMeters = body.ComputeLocalBounds().size.magnitude;

			TrackingHealthSettings settings = body.TrackingHealthSettings;
			if (!(Quaternion.Angle(previousRotation, rotation) <=
				settings.SettleRotationDegrees))
			{
				return false;
			}
			if (!(state.BodyDiameterMeters > 0f))
				return true;

			return Vector3.Distance(previousPosition, position) <=
				settings.SettleTranslationFraction * state.BodyDiameterMeters;
		}

		/// <summary>
		/// Advances the cold-start window and reports whether this frame counts.
		/// </summary>
		/// <remarks>
		/// With no expectation there is no health, so the window falls back to the
		/// ratio itself: it must clear a floor, and the run of counted frames must
		/// be steady. Steadiness is what a fixed floor cannot say on its own, since
		/// a ratio drifting upward through the floor and one sitting on it are the
		/// same reading taken twice.
		/// </remarks>
		private static bool AdvanceColdStartWindow(BodyState state,
			TrackingHealthSettings settings, bool rawAvailable, float raw)
		{
			if (!rawAvailable || !state.PoseSettled || raw < settings.ColdStartRawFloor ||
				!VariationWithin(state.ColdStartRatios, raw, settings.ColdStartVariation))
			{
				state.ColdStartRatios.Clear();
				return false;
			}
			state.ColdStartRatios.Add(raw);
			return true;
		}

		/// <summary>
		/// Whether adding <paramref name="candidate"/> keeps the run's coefficient of
		/// variation within <paramref name="limit"/>.
		/// </summary>
		private static bool VariationWithin(List<float> ratios, float candidate,
			float limit)
		{
			float sum = candidate;
			for (int index = 0; index < ratios.Count; ++index)
				sum += ratios[index];
			int count = ratios.Count + 1;
			float mean = sum / count;
			if (!(mean > 0f))
				return false;

			float squares = (candidate - mean) * (candidate - mean);
			for (int index = 0; index < ratios.Count; ++index)
				squares += (ratios[index] - mean) * (ratios[index] - mean);
			return Mathf.Sqrt(squares / count) / mean <= limit;
		}

		/// <summary>
		/// Records what this view achieves, once the frame has been interpreted.
		/// </summary>
		/// <remarks>
		/// Two things happen here and they are exclusive. A window that completed
		/// with no expectation defines one from the frames it counted, taking the
		/// median rather than the mean so the one frame the pose was still arriving
		/// on cannot set the bar. Every later tracked frame that is comfortably
		/// above its own expectation moves it slowly. Nothing else writes: a body
		/// that is acquiring, doubted, lost, recovering or showing a held pose is
		/// not in a position to say what a healthy frame looks like, and letting it
		/// would lower the bar by exactly the amount that is going wrong.
		/// </remarks>
		private void LearnViewExpectation(TrackedBody body, BodyState state)
		{
			if (!HealthAuthority(body) || !state.HasCurrentView)
				return;

			TrackingHealthSettings settings = body.TrackingHealthSettings;
			TemporalTrackingSnapshot temporal = body.TemporalTrackingStatus;
			bool tracked = temporal.State == TemporalTrackingState.Tracked;

			if (state.ColdStartWindow)
			{
				if (tracked && state.ColdStartRatios.Count > 0)
				{
					GetViewExpectations(body).Seed(state.CurrentView,
						Median(state.ColdStartRatios), temporal.FrameIndex,
						ViewExpectationSource.ColdStartWindow,
						settings.ViewBearingDegrees, settings.ViewRangeRatio);
					state.ColdStartRatios.Clear();
				}
				return;
			}

			TrackingHealthSnapshot health = body.TrackingHealth;
			// A pose the appearance evidence disagrees with may be coherent on the
			// wrong thing, and a bar it lowered would be a bar the wrong thing set. A
			// pose a recovery put there has not been believed yet either: the window
			// is still deciding, and an expectation learned inside it would be the
			// candidate vouching for itself.
			if (!tracked || !health.Available ||
				health.Health < settings.ExpectationUpdateHealth ||
				state.FusionState == BodyFusionState.Holding ||
				health.AppearanceVetoed || IsRecovering(temporal.RecoveryPhase))
			{
				return;
			}
			GetViewExpectations(body).Update(state.CurrentView, health.Raw,
				temporal.FrameIndex, settings.ExpectationAlpha,
				settings.ViewBearingDegrees, settings.ViewRangeRatio);
		}

		/// <summary>
		/// Whether a recovery is still deciding this body's pose.
		/// </summary>
		/// <remarks>
		/// Cancelled is deliberately not one of them. It says a recovery finished
		/// without adopting anything, and it stays on the surface until the surface is
		/// used again, so reading it as "still recovering" would stop a body that
		/// re-acquired on its own from ever learning again.
		/// </remarks>
		private static bool IsRecovering(TemporalRecoveryPhase phase) =>
			phase == TemporalRecoveryPhase.Requested ||
			phase == TemporalRecoveryPhase.Preview ||
			phase == TemporalRecoveryPhase.Confirmed;

		private static float Median(List<float> values)
		{
			if (values.Count == 0)
				return 0f;
			var ordered = new List<float>(values);
			ordered.Sort();
			int middle = ordered.Count / 2;
			return (ordered.Count & 1) == 1
				? ordered[middle]
				: 0.5f * (ordered[middle - 1] + ordered[middle]);
		}

		#endregion

		private void UpdateTemporalTrackingStatus(TrackedBody body, BodyState state)
		{
			ObserveRecovery(body, out TemporalRecoveryPhase recoveryPhase,
				out ulong recoveryToken);
			if (!HealthAuthority(body))
			{
				var legacyInput = new TemporalTrackingInput(_completedTemporalFrameIndex,
					_completedTemporalStepSerial, body.IsRegistered,
					body.RegistrationQuality, recoveryPhase, recoveryToken);
				body.SetTemporalTrackingStatus(TemporalTrackingMachine.Decide(
					ref state.TemporalTracking, legacyInput, _temporalTrackingPolicy));
				if (RecoveryFollowsLifecycle(body))
					DriveRecovery(body, state, true);
				return;
			}

			TrackingHealthSnapshot health = body.TrackingHealth;
			var input = TemporalTrackingInput.WithHealth(_completedTemporalFrameIndex,
				_completedTemporalStepSerial, body.IsRegistered,
				body.RegistrationQuality, recoveryPhase, recoveryToken,
				health.Available, health.Health, state.PoseSettled,
				state.ColdStartWindow, state.ColdStartSatisfied);
			body.SetTemporalTrackingStatus(TemporalTrackingMachine.Decide(
				ref state.TemporalTracking, input, body.TrackingHealthSettings.Policy));
			DriveRecovery(body, state, RecoveryFollowsLifecycle(body));
		}

		/// <summary>
		/// Whether the recovery driver is stepped from the managed lifecycle's own loss for a body, instead of from
		/// the temporal state, when the tooling asks for it. A child body follows its parent and never runs its own.
		/// </summary>
		internal static bool RecoveryFollowsLifecycle(bool requested, bool isActiveMode, bool hasParent) =>
			requested && isActiveMode && !hasParent;

		/// <summary>
		/// The same, for a body that tracks by the given method. Only a body that tracks by the edge method has a recovery,
		/// whatever the request says.
		/// </summary>
		internal static bool RecoveryFollowsLifecycle(bool requested, bool isActiveMode, bool hasParent, bool edgeTracking) =>
			EdgeLayerApplies(RecoveryFollowsLifecycle(requested, isActiveMode, hasParent), edgeTracking);

		private bool RecoveryFollowsLifecycle(TrackedBody body) =>
			RecoveryFollowsLifecycle(ShippedDefaults.RecoveryFromLifecycleLoss(ShippedDefaults.AppliesTo(body, this)),
				body.IsActiveMode, body.HasParent, body.EnableEdgeTracking);

		/// <summary>
		/// What the recovery driver is told about whether the body is tracked. From the lifecycle, a body is tracked
		/// while it tracks and before it has ever started, so a body that started and then stopped counts its rows
		/// toward its attempts whatever the temporal state says. A body whose lifecycle reads its tracking health is
		/// also out whenever the temporal state says so, so the lifecycle adds attempts and
		/// removes none. Without the request it is the temporal state's answer.
		/// </summary>
		internal static bool RecoveryReadsTracked(bool fromLifecycle, bool temporalTracked,
			bool startedSinceRegistration, bool isTracking, bool healthAuthority)
		{
			if (!fromLifecycle)
				return temporalTracked;
			bool lifecycleTracked = isTracking || !startedSinceRegistration;
			return healthAuthority ? lifecycleTracked && temporalTracked : lifecycleTracked;
		}

		/// <summary>
		/// Whether the tracker should refine a recovery seat on the incoming rows. Only while the tooling asks for a
		/// seat to be judged on its refined coverage score, the body is not tracking, and a seat made as the retained
		/// pose is waiting on its window. The guide, the start path and every other seat are left unrefined.
		/// </summary>
		internal static bool RecoverySeatRefineWanted(bool judgedOnRefinedCoverage, bool isTracking,
			bool seatPending, bool seatIsRetainedPose) =>
			judgedOnRefinedCoverage && !isTracking && seatPending && seatIsRetainedPose;

		/// <summary>The refine count the coverage branch of the recovery reads from the tooling, zero when none is asked for.</summary>
		internal static int CoverageRefineStepsFor(int? requested) =>
			requested.HasValue && requested.Value >= 1 ? requested.Value : 0;

		/// <summary>
		/// How many lost rows apart two kept-pose attempts are at least, or zero when the tooling asks for no cadence or
		/// names a count under one.
		/// </summary>
		internal static int KeptPoseCadenceFor(int? requested) =>
			requested.HasValue && requested.Value >= 1 ? requested.Value : 0;

		/// <summary>
		/// The capacity and the admission spacing the saved-view bank is configured with, which are the manager's
		/// capacity and the default spacing.
		/// </summary>
		internal static (int Capacity, float RotationDeg, float TranslationM) SavedViewBankFor(int managerCapacity) =>
			(Mathf.Max(0, managerCapacity),
				TrackerDefaults.RECOVERY_SEED_ADMISSION_ROTATION_DEG,
				TrackerDefaults.RECOVERY_SEED_ADMISSION_TRANSLATION_M);

		/// <summary>
		/// Whether this frame is one to seat the body on a kept view.
		/// </summary>
		/// <remarks>
		/// The bank has to hold something, the body has to have been failing to
		/// re-acquire for longer than re-acquisition takes when it works, and the last
		/// seating has to have had its own chance to prove itself. Stated as one
		/// predicate so it can be asserted without a scene.
		/// </remarks>
		internal static bool ShouldSeatOnKeptView(int bankSize, int framesOutOfTracked,
			int framesSinceAttempt)
		{
			if (bankSize <= 0) return false;
			if (framesOutOfTracked < TrackerDefaults.RECOVERY_ATTEMPT_DELAY_FRAMES)
				return false;
			return framesSinceAttempt >= TrackerDefaults.RECOVERY_ATTEMPT_INTERVAL_FRAMES;
		}

		/// <summary>
		/// The saved-view capacity a body's bank is configured with this frame.
		/// </summary>
		/// <remarks>
		/// Refining before acceptance puts the body in the tracker's tracking state, and the
		/// bank admits views from any body in that state. Until the body first starts, the
		/// bank is therefore kept shut, so no view of a pose nobody accepted enters it. It is
		/// empty until then in every mode, so shutting it loses nothing, and it opens empty
		/// when the body starts, as it does under the default.
		/// When the start pose is held while searching, the bank is shut for a body that has never
		/// started in every mode, so no recovery route (saved view, kept pose or refined seat) can
		/// seat or move a body that has not started.
		/// </remarks>
		internal static int RecoveryCapacityFor(TrackerDefaults.MarginResidualTerm term, bool edgeTracking, int capacity,
			bool isTracking, bool startedSinceRegistration, bool holdStartPose = false)
		{
			if ((holdStartPose || TrackedBody.RefinesBeforeAcceptance(term, edgeTracking)) &&
				!isTracking && !startedSinceRegistration)
				return 0;
			return capacity;
		}

		/// <summary>
		/// The residual term the saved-view verifier reads for a body whose decision this frame
		/// took <paramref name="path"/>.
		/// </summary>
		/// <remarks>
		/// The mode that guards the stored reference ranks and seats candidates on both residual
		/// terms only on the stationary path under pose fusion, the path that stores a reference and
		/// holds it. Every other path has no stored reference to guard, so its verifier reads the
		/// median term exactly as the mode it extends does. Every other term is returned unchanged.
		/// </remarks>
		internal static TrackerDefaults.MarginResidualTerm VerifierTermFor(
			TrackerDefaults.MarginResidualTerm term, LifecyclePath path)
		{
			if (term == TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference &&
				path != LifecyclePath.StationaryFusion)
				return TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAccept;
			return term;
		}

		/// <summary>
		/// Counts how long the body has been out of tracking and, when it has been
		/// too long, asks its saved views for a pose, seats the one the verifier
		/// prefers, and takes it back if the window does not confirm it.
		/// </summary>
		private void DriveRecovery(TrackedBody body, BodyState state, bool fromLifecycle = false)
		{
			state.SavedView ??= new SavedViewRecoveryState();
			_savedViewDriver ??= new SavedViewRecoveryDriver(new SavedViewRecoveryBridgePort(),
				() => Time.realtimeSinceStartupAsDouble * 1000.0, new SeenPoseBridgeScorer());
			bool poseFusion = ShippedDefaults.PoseFusionActive(this);
			bool shipped = ShippedDefaults.AppliesTo(body.EnableEdgeTracking, poseFusion);
			_savedViewDriver.KeptPoseCadenceRows = KeptPoseCadenceFor(ShippedDefaults.RecoveryKeptPoseCadence(shipped));
			// The plugin is told what the run-wide options ask for when that changes, and never while they are null.
			_evidenceReuse ??= new EvidenceReuseSwitch();
			SyncEvidenceReuse(_evidenceReuse, poseFusion);
			SeenPoseCandidates seenPoses = SeenPoseRecovery && state.Decision.Path == LifecyclePath.Standard &&
				_seenPoses.TryGetValue(body, out SeenPoseCandidates kept)
					? kept
					: null;
			if (!body.TryGetRetainedInitializationWorldPose(out Vector3 held, out Quaternion heldRotation))
			{
				held = body.transform.position;
				heldRotation = body.transform.rotation;
			}
			// A candidate is read as the registration margin against the acquire bar unless its coverage is read below.
			System.Func<float, bool> clearsAcquire = ClearsMarginAcquireBar;
			// A candidate can be judged after the tracker has refined it. It applies to a seat made while the body is not
			// tracking, where the body is seated on a candidate as its retained pose and the start path refines it on the
			// incoming rows, with or without pose fusion.
			int refineSteps = 0;
			float refinedSignal = 0f;
			float judgeBar = 0f;
			// On the coverage scale a candidate is read as the share of agreeing sites its own record carries, mapped with the
			// scale the body's coverage score uses, and it is accepted at the body's start threshold, the pose the body
			// holds being read the same way.
			CoverageReadScale? coverageScale = null;
			if (ShippedDefaults.CoverageRecovery(shipped) && body.EnableEdgeTracking &&
				SourceFor(body) == TrackerDefaults.QualitySignalSource.Coverage)
			{
				(float floor, float full) = ShippedDefaults.CoverageScale(shipped);
				coverageScale = new CoverageReadScale(floor, full);
				_coverageAcceptBar = body.ConfiguredStartBar;
				clearsAcquire = _clearsCoverageAcceptBar ??= ClearsCoverageAcceptBar;
				// A candidate can be judged on its refined coverage score, at the start threshold.
				refineSteps = CoverageRefineStepsFor(ShippedDefaults.CoverageRecoveryRefineSteps(shipped));
				if (refineSteps > 0)
				{
					refinedSignal = body.DecisionInstantQuality;
					judgeBar = body.ConfiguredStartBar;
				}
			}
			(int bankCapacity, float admissionRotationDeg, float admissionTranslationM) =
				SavedViewBankFor(_savedViewRecoveryCapacity);
			SavedViewDecision decision = _savedViewDriver.Step(state.SavedView, body.BodyId,
				_completedTemporalFrameIndex,
				RecoveryCapacityFor(MarginResidualTerm, body.EnableEdgeTracking, bankCapacity,
					body.IsTracking, body.StartedSinceRegistration,
					ShippedDefaults.HoldStartPose(shipped)),
				admissionRotationDeg,
				admissionTranslationM,
				TrackerDefaults.RECOVERY_SEED_ADMISSION_MAX_PROBE_RESIDUAL,
				AdmissionCoverageFor(body),
				RecoveryReadsTracked(fromLifecycle, state.TemporalTracking.State == TemporalTrackingState.Tracked,
					body.StartedSinceRegistration, body.IsTracking, HealthAuthority(body)),
				body.IsTracking, state.HoldActive,
				clearsAcquire,
				held, heldRotation, VerifierTermFor(MarginResidualTerm, state.Decision.Path),
				seenPoses, refineSteps, refinedSignal, judgeBar, coverageScale);
			state.FramesOutOfTracked = state.SavedView.FramesOutOfTracked;
			state.RecoveryAttempts = state.SavedView.Attempts;
			state.RecoverySeedBankConfigured = state.SavedView.Configured;
			// While a seat is to be judged on its refined coverage score, the tracker refines the seat
			// on the incoming rows. The seat is published again before every step, so nothing carries from row to row.
			body.RecoverySeatRefineRequested = RecoverySeatRefineWanted(refineSteps > 0 && coverageScale.HasValue,
				body.IsTracking, state.SavedView.PendingEntryId > 0,
				state.SavedView.PendingPath == SavedViewSeatPath.RetainedPose);
			if (decision.Seat && decision.SeatPath == SavedViewSeatPath.RetainedPose &&
				decision.Candidate.camera_from_body != null)
				_seatMatrices[body] = (float[])decision.Candidate.camera_from_body.Clone();
			if (decision.RefineJudged)
			{
				decision.Refining = body.RefiningBeforeAcceptance;
				if (_seatMatrices.TryGetValue(body, out float[] seatMatrix) &&
					PoseVerification.TryGetCameraFromBodyPose(body.BodyId, out float[] liveMatrix))
				{
					SeenPoseCandidates.Difference(seatMatrix, liveMatrix, out float metres, out float degrees);
					decision.RefineMeasured = true;
					decision.RefineMillimetres = metres * 1000f;
					decision.RefineDegrees = degrees;
				}
			}
			if (decision.Withdraw)
				WithdrawSavedViewSeat(body, decision);
			if (decision.Seat)
				SeatSavedViewCandidate(body, decision);
			if (decision.Window == SavedViewWindowOutcome.Passed)
				state.PublishRecoveryProvenanceOnce = true;
			if (decision.Attempted || decision.Window != SavedViewWindowOutcome.None || decision.RefineJudged ||
				decision.CadenceSkipped)
				OnSavedViewAttempt?.Invoke(body, decision);
		}

		/// <summary>
		/// Tells the plugin what the options ask of its site cache. The cache is a setting of the plugin, so it follows pose
		/// fusion and not one body's modality, and it only ever serves the evaluations of edge evidence, which a body
		/// without edge tracking never asks for. It is on unless pose fusion is in play or the run switches it off.
		/// </summary>
		internal static void SyncEvidenceReuse(EvidenceReuseSwitch reuse, bool poseFusion) =>
			reuse.Sync(ShippedDefaults.RecoveryEvidenceSiteCache(!poseFusion),
				TrackingOverrides.RecoveryEvidenceSiteCacheVerify, TrackingOverrides.RecordEvaluationStartTiming);

		private static readonly System.Func<float, bool> ClearsMarginAcquireBar =
			signal => signal >= TrackerDefaults.REGISTRATION_MARGIN_ACQUIRE_BAR;

		private float _coverageAcceptBar;
		private System.Func<float, bool> _clearsCoverageAcceptBar;

		private bool ClearsCoverageAcceptBar(float signal) => signal >= _coverageAcceptBar;

		/// <summary>The edge coverage at which this body's accept bar is cleared.</summary>
		/// <remarks>
		/// The floor is a constant on the coverage scale, not a reading taken off the
		/// body's decision band. The band carries whichever scalar the body's decisions
		/// are taken on, and only one of those scalars is a coverage-derived quality;
		/// scaling any of the others by the coverage map's own maximum produces a
		/// number on no scale at all. The parameter stays so the call sites keep saying
		/// which body the floor is being applied to.
		/// </remarks>
		private static float AdmissionCoverageFor(TrackedBody body) =>
			TrackerDefaults.RECOVERY_SEED_ADMISSION_MIN_EDGE_COVERAGE;

		/// <summary>
		/// Whether a recovery seat made on the retained pose leaves the body drawn at the pose it replaced while the seat is on
		/// its window. It needs the hold to be asked for (<see cref="ShippedDefaults.RecoveryHoldDisplayDuringSeat"/>, which is
		/// on by default) and a body that is not tracking, and it needs the retained pose to
		/// be what is drawn, which is so for a body under pose fusion and for a body that holds its start pose while searching.
		/// For any other body the tracker's own pose is drawn on every row and there is nothing to hold.
		/// </summary>
		internal static bool DisplayHoldApplies(bool requested, bool isTracking, bool poseFusionActive, bool holdsStartPose) =>
			requested && !isTracking && (poseFusionActive || holdsStartPose);

		/// <summary>
		/// Takes a seat that did not start back: the retained pose goes back to the pose it replaced while the body is
		/// not tracking, or the held seat is written again.
		/// </summary>
		internal void WithdrawSavedViewSeat(TrackedBody body, in SavedViewDecision decision)
		{
			if (decision.WithdrawPath == SavedViewSeatPath.RetainedPose)
				body.AdoptRetainedInitializationPose(decision.RestorePosition, decision.RestoreRotation);
			else
				TrySeedTrackerFromStoredPose(body, out _);
		}

		/// <summary>
		/// Puts a verified candidate where the tracker will solve from it: as the
		/// retained pose while the body is not tracking, or through the gated pose
		/// write while it is held.
		/// </summary>
		internal void SeatSavedViewCandidate(TrackedBody body, in SavedViewDecision decision)
		{
			FTTrackingPose pose = decision.Candidate.tracker_pose;
			if (decision.SeatPath == SavedViewSeatPath.RetainedPose)
			{
				if (TrackerPoseToWorld(body, pose, true, out Vector3 worldPos, out Quaternion worldRot))
				{
					// The pose the seat replaces is read before it is replaced. Writing the seat ends a hold, so the hold is begun after it.
					Vector3 replacedPosition = default;
					Quaternion replacedRotation = Quaternion.identity;
					bool holdDisplay = DisplayHoldApplies(ShippedDefaults.RecoveryHoldDisplayDuringSeat(), body.IsTracking,
						IsARPoseFusionActive, ShippedDefaults.HoldStartPose(ShippedDefaults.AppliesTo(body, this))) &&
						body.TryGetRetainedInitializationWorldPose(out replacedPosition, out replacedRotation);
					body.AdoptRetainedInitializationPose(worldPos, worldRot);
					if (holdDisplay)
						body.HoldDisplayAt(replacedPosition, replacedRotation);
				}
				return;
			}
			int status = WriteBodyPose(body, ref pose);
			pose.TryGetTransformation(out Vector3 position, out Quaternion rotation);
			NativeBodyWriteJournal.RecordPose(body.BodyId, "FT_SetBodyPose",
				"World", position, rotation,
				FrameWriteGuard.RecordOutOfFrame(status, "FT_SetBodyPose"), status);
		}

		/// <summary>
		/// Keeps this row's pose as a place a later recovery can start from, when the row
		/// was accepted on the path without pose fusion.
		/// </summary>
		/// <remarks>
		/// <para>The start pose is taken once, on the first row, from the pose the body is
		/// held at before anything refines it. It is written into the tracker's space by
		/// the inverse of the read the tracker's own solve takes, and kept only if reading
		/// it back lands where it was.</para>
		/// <para>A row is accepted when the body is tracking and its own acquire reading
		/// clears the acquire bar, the bar a start has to clear. Its pose is kept as the
		/// tracker reports it, beside the camera-from-body matrix a pose evaluation takes,
		/// both read on the same row. On the first such row the matrix built from the
		/// tracker's pose is compared with the one the tracker reports. The start pose is
		/// built that way, so it is only tried when the two agree.</para>
		/// </remarks>
		private void RecordSeenPose(TrackedBody body)
		{
			if (body == null || !body.IsRegistered || body.HasParent || IsARPoseFusionActive)
				return;
			if (!_seenPoses.TryGetValue(body, out SeenPoseCandidates seen))
			{
				seen = new SeenPoseCandidates();
				_seenPoses[body] = seen;
			}
			if (!seen.HasStart && !body.StartedSinceRegistration &&
				body.TryGetRetainedInitializationWorldPose(out Vector3 heldAt, out Quaternion heldRotation) &&
				TryWorldToTrackerPose(body, heldAt, heldRotation, out FTTrackingPose startPose) &&
				PoseVerification.TryToBodyFrame(body.BodyId, SeenPoseCandidates.ToMatrix(startPose),
					out float[] startCameraFromBody))
			{
				seen.SetStart(startPose, startCameraFromBody);
			}
			if (!body.IsTracking ||
				!body.DecisionBand.Clears(QualityDecision.Acquire, body.SignalFor(QualityDecision.Acquire)))
			{
				return;
			}
			if (!FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose pose) ||
				!PoseVerification.TryGetCameraFromBodyPose(body.BodyId, out float[] cameraFromBody))
			{
				return;
			}
			if (!seen.FrameChecked)
			{
				bool built = PoseVerification.TryToBodyFrame(body.BodyId, SeenPoseCandidates.ToMatrix(pose),
					out float[] rebuilt);
				SeenPoseCandidates.Difference(rebuilt, cameraFromBody, out float metres, out float degrees);
				bool agrees = built && metres <= 0.001f && degrees <= 0.1f;
				seen.SetFrameCheck(agrees);
			}
			seen.Record(pose, cameraFromBody);
		}

		/// <summary>
		/// Writes a world pose into the tracker's space on the path without pose fusion,
		/// the inverse of how <see cref="TrackerPoseToWorld"/> reads the tracker's solve.
		/// </summary>
		/// <returns>False when the path has pose fusion, there is no camera, or reading the
		/// pose back does not land on the world pose.</returns>
		private bool TryWorldToTrackerPose(TrackedBody body, Vector3 worldPosition,
			Quaternion worldRotation, out FTTrackingPose pose)
		{
			pose = default;
			if (IsARPoseFusionActive)
				return false;
			XRTrackerManager manager = XRTrackerManager.Instance;
			Transform cam = manager != null ? manager.CameraTransform : null;
			if (cam == null)
				return false;
			pose = ConversionUtils.GetConvertedPose(cam.InverseTransformPoint(worldPosition),
				Quaternion.Inverse(cam.rotation) * worldRotation);
			return TrackerPoseToWorld(body, pose, false, out Vector3 back, out Quaternion backRotation) &&
				Vector3.Distance(back, worldPosition) <= 1e-4f &&
				Quaternion.Angle(backRotation, worldRotation) <= 0.01f;
		}

		/// <summary>
		/// What the recovery workflow is doing with this body on this frame.
		/// </summary>
		/// <remarks>
		/// A recovery that seeds a pose declares its phase, and that declaration is
		/// the phase. With none declared the body is not recovering.
		/// </remarks>
		private void ObserveRecovery(TrackedBody body,
			out TemporalRecoveryPhase phase, out ulong token)
		{
			phase = TemporalRecoveryPhase.None;
			token = 0;
			if (body != null && _declaredRecoveryPhases.TryGetValue(body,
				out DeclaredRecoveryPhase declared))
			{
				phase = declared.Phase;
				token = declared.Token;
			}
		}

		/// <summary>
		/// Reads back what the loss guard did on the step that just completed.
		/// </summary>
		/// <remarks>
		/// <para>The guard can only ever remove a frame from the published state. It
		/// has no way to put one there: the acceptance it is combined with still has
		/// to hold on its own. So a body with no guard configured, a disabled guard, or
		/// a plugin that does not export the call behaves as if there were none.</para>
		/// <para>The three surfaces stay separate. The solver running is not an
		/// acceptance; a stored pose existing is not an acceptance; only a frame the
		/// lifecycle accepted and the guard did not refuse reaches
		/// <see cref="TrackingConfidence.Tracked"/>. A quarantined body still has a
		/// pose to draw, and it is drawn as held rather than as tracked.</para>
		/// </remarks>
		private void ReadLossQuarantineState(TrackedBody body, BodyState state)
		{
			if (body == null || string.IsNullOrEmpty(body.BodyId))
				return;

			FTLossQuarantineState read = FTLossQuarantineState.Create();
			try
			{
				if (FTBridge.FT_GetLossQuarantineState(body.BodyId, ref read) != FTErrorCode.OK)
					return;
			}
			catch (System.EntryPointNotFoundException)
			{
				// A plugin without the guard cannot be quarantining anything.
				state.HasLossQuarantineState = false;
				return;
			}
			catch (System.DllNotFoundException)
			{
				state.HasLossQuarantineState = false;
				return;
			}

			state.LossQuarantine = read;
			state.HasLossQuarantineState = read.configured != 0;
		}

		/// <summary>
		/// Standard state machine with frame hysteresis (for Off and Tracker modes).
		/// Pose is applied via OnAfterTrackingStep (non-AR) or in ProcessActiveBody (when AR active).
		/// </summary>
		private void ProcessBodyNoAR(TrackedBody body, BodyState state)
		{
			if (state.Decision.Path == LifecyclePath.None)
				state.Decision.Path = LifecyclePath.Standard;

			if (body.IsTracking)
			{
				state.FramesSinceTrackingStarted++;

				if (state.FramesSinceTrackingStarted > TrackerDefaults.STABILIZATION_FRAMES)
				{
					bool belowHold = !(CoverageHoldsFor(body) || ClearsHold(body));
					RecordHoldReading(body, state, belowHold);
					bool lost = CheckFrameThreshold(
						state, belowHold, TrackerDefaults.FRAMES_TO_LOSE, trackGoodFrames: false);
					state.Decision.GoodFrameCount = state.GoodFrameCount;
					state.Decision.BadFrameCount = state.BadFrameCount;
					if (lost)
					{
						state.Decision.StopFired = true;
						ResetTracking(body, state);
					}
				}
			}
			else
			{
				// A body on a limited search walk is judged against its limits before the start decision. A
				// step past the pixel limit cannot count toward a start. A walk that ends without a start
				// begins again from the placed start pose, and the rows in a row that a start needs all come
				// from one walk.
				SearchWalkVerdict walk = body.JudgeSearchWalk();
				if (walk == SearchWalkVerdict.OutOfRange)
					ResetCounters(state);
				else
					TryStartTracking(body, state);

				if (!body.IsTracking &&
					(walk == SearchWalkVerdict.OutOfRange || walk == SearchWalkVerdict.Spent))
				{
					body.SnapSearchWalkBack();
					ResetCounters(state);
				}
			}
		}

#if HAS_AR_FOUNDATION
		private void ProcessStationary(
			TrackedBody body,
			BodyState state,
			FusionFrameContext? frameContext)
		{
			if (!frameContext.HasValue || !frameContext.Value.IsValid)
				return;
			FusionFrameContext context = frameContext.Value;
			state.Step = default;
			state.Step.CameraMotionPixels = -1f;
			state.Step.SettleMovePixels = -1f;
			state.Step.DisplayToFitPixels = -1f;
			state.Step.StartSignal = -1f;
			state.Step.StartedFromSearch = body.StartedFromSearch;
			state.Step.WorldCarried = state.WorldCarriedThisStep;
			state.WorldCarriedThisStep = false;
			float timeBelowNiceBefore = state.TimeBelowNice;
			float timeSinceLastCorrectionBefore = state.TimeSinceLastCorrection;
			bool correctionAttempted = false;
			int correctionNativeStatus = StationaryCorrectionTelemetry.NativeStatusNotAttempted;
			bool correctionPoseAvailable = false;
			FTTrackingPose correctionPose = default;
			float timerDeltaSeconds = _completedFrameInstrumentationContext.HasValue
				? _completedFrameInstrumentationContext.Value.TrackingStepDeltaSeconds
				: 0f;

			float cameraStepMetres = 0f, cameraStepDegrees = 0f;
			bool hadPreviousCamera = state.HasPreviousStepCamera;
			Vector3 previousCameraPosition = state.PreviousStepCameraPosition;
			Quaternion previousCameraRotation = state.PreviousStepCameraRotation;
			NoteStepCamera(state, ref cameraStepMetres, ref cameraStepDegrees);

			// Not yet tracking — body is held at its retained initialization pose, which
			// BeforeTrackingStep republishes and ApplyDetectorPose displays.
			if (!body.IsTracking)
			{
				state.FusionState = BodyFusionState.NotTracking;
				TryStartTracking(body, state);
				RecordStationaryCorrectionTelemetry(
					body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
					correctionAttempted, correctionNativeStatus,
					correctionPoseAvailable, correctionPose);
				return;
			}

			state.FramesSinceTrackingStarted++;

			// Measured every tracking frame, whether or not the coast is enabled, because
			// the result is also reported to other readers through TryGetBodyViewVisibility.
			state.Visibility = MeasureViewVisibility(body, state, context);

			// The visibility split, decided from existing online state only.
			WorldFrameCoastPhase phase = state.CoastPhase;
			// A body that tracks by the silhouette method is never held in the world frame by the visibility split.
			WorldFrameCoastDecision coast = WorldFrameCoast.Decide(
				ref phase,
				EdgeLayerApplies(_enableWorldFrameCoast, body),
				WorldPoseIsHealthy(context),
				state.Visibility.Measured,
				state.Visibility.ShouldBeVisible);
			state.CoastPhase = phase;

			if (coast.HoldInWorldFrame)
			{
				// Out of view and the world frame is healthy. Nothing is spent and
				// nothing is written; the composition already holds the body where the
				// world says it is.
				state.RecoveryFrameCount = 0;
				state.SolveWindow.Clear();
				state.FirstAcceptBarRows = 0;
				state.FusionState = BodyFusionState.Holding;
				RecordStationaryCorrectionTelemetry(
					body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
					correctionAttempted, correctionNativeStatus,
					correctionPoseAvailable, correctionPose);
				return;
			}

			if (coast.SeedTracker)
			{
				// The body is back in frame. The tracker is still solving from
				// wherever it froze, which is a camera-relative pose the camera has
				// since left behind; the held pose is the better estimate, so it is
				// handed over through the same explicit gated write the quality gate
				// already uses. It seeds the solve and updates no stored reference.
				if (TrySeedTrackerFromHeldPose(body, state, out correctionPose, out correctionNativeStatus))
				{
					correctionAttempted = true;
					correctionPoseAvailable = true;
					phase = state.CoastPhase;
					if (WorldFrameCoast.RecordSeedResult(ref phase, correctionNativeStatus))
						++state.CoastReentrySeeds;
					else
						++state.CoastSeedWriteFailures;
					state.CoastPhase = phase;
				}
				state.TimeBelowNice = 0f;
				state.TimeSinceLastCorrection = 0f;
				state.RecoveryFrameCount = 0;
				state.SolveWindow.Clear();
				state.FirstAcceptBarRows = 0;
			}

			// Read raw native pose (no world conversion, the tracker solves in the world
			// frame under pose fusion).
			if (!FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose rawPose))
			{
				RecordStationaryCorrectionTelemetry(
					body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
					correctionAttempted, correctionNativeStatus,
					correctionPoseAvailable, correctionPose);
				return;
			}
			// A pose that is not a pose must not reach the acceptance decision: the
			// quality scalar is computed from site counts and cannot see it, so a
			// diverged solve would be accepted and stored as the last good pose.
			if (!rawPose.TryGetTransformation(out Vector3 nativePos, out Quaternion nativeRot))
			{
				body.RecordRejectedPose();
				RecordStationaryCorrectionTelemetry(
					body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
					correctionAttempted, correctionNativeStatus,
					correctionPoseAvailable, correctionPose);
				return;
			}

			// What the decision reads is also what is recorded with the pose it
			// accepts, so the stored quality is the number that admitted it rather
			// than a second one nobody consulted.
			float instantQuality = body.SignalFor(QualityDecision.Accept);
			bool warmup = state.FramesSinceTrackingStarted <= TrackerDefaults.STABILIZATION_FRAMES;
			bool qualityIsNice = ClearsAccept(body);
			state.Step.MarginClears = qualityIsNice;
			RecordAcceptReading(body, state, qualityIsNice);
			// The first accept waits until the solve stands still. It reads the newest solves in the world frame, and it is off unless the body is
			// served by the fusion defaults. A first accept that waits is no accept at all for the row, margin or not, which is how the row read
			// before the fit existed.
			bool fusionDefaults = ShippedDefaults.AppliesUnderFusion(body, this);
			bool coverageDecides = CoverageDecidesFor(body);
			bool firstAcceptGuardOn = ShippedDefaults.FirstAcceptGuard(fusionDefaults, ShippedDefaults.FirstAcceptGuardSetting(body)) && body.StartReadsCoverage;
			if (firstAcceptGuardOn)
				NoteSolve(body, state, nativePos, nativeRot);
			bool firstAcceptGuardWaits = firstAcceptGuardOn
				&& FirstAcceptWaits(state.HasLastGoodPose, state.FramesSinceTrackingStarted, state.SolveWindow);
			// The bar of the first accept does not let go after the stabilization rows as the guard does, because a fit that never clears it is a fit
			// that the start bar alone could not tell from a wrong one.
			float startSignal = body.StartReadsCoverage ? body.StartSignal : -1f;
			bool firstAcceptBarOn = ShippedDefaults.FirstAcceptCoverageBar(fusionDefaults, body.AcceptSteadyFits) && body.StartReadsCoverage;
			bool firstAcceptBarWaits = false;
			if (firstAcceptBarOn)
			{
				state.FirstAcceptBarRows = AdvanceFirstAcceptBarRows(state.FirstAcceptBarRows, startSignal, ShippedDefaults.FirstAcceptCoverageBarValue);
				firstAcceptBarWaits = FirstAcceptBarWaits(state.HasLastGoodPose, state.FirstAcceptBarRows);
			}
			bool firstAcceptWaits = firstAcceptGuardWaits || firstAcceptBarWaits;
			if (firstAcceptWaits)
			{
				qualityIsNice = false;
				++state.FirstAcceptRowsWaited;
			}
			// A solve the margin does not clear is also accepted when the coverage score and the prediction gate verify it on several steps in a
			// row. It keeps the lock and the readout. It is stored as any accepted solve is only on a picture on which the margin has not accepted
			// for a while, because where the margin does accept it keeps the stored pose, and a stored solve is not a reading of the margin, so it
			// may become the reference without the margin standing at the hold bar.
			bool softAccepted = false;
			state.SoftAcceptedThisStep = false;
			if (coverageDecides)
			{
				// The coverage score accepts, on the bars a body without pose fusion reads. A solve that clears the bar and lies inside the prediction
				// gate is accepted on the step, stored as the pose the body is held at whatever the margin reads, and read as the solve that became
				// the pose. The strict check of the registration margin keeps accepting what it accepted before, which includes a solve that stands
				// outside the gate, so a guide that was placed far from the object is left by the first solve the margin clears.
				bool acceptedByCoverage = AcceptByCoverage(body, state, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees) && !firstAcceptWaits;
				qualityIsNice = qualityIsNice || acceptedByCoverage;
				softAccepted = acceptedByCoverage;
				state.SoftAcceptedThisStep = acceptedByCoverage;
				state.Step.CoverageAccepted = acceptedByCoverage;
				if (acceptedByCoverage)
				{
					state.Decision.Accept = LifecycleComparison.Taken(body.StartSignal, body.StartBar, QualitySignal.Instant, true);
				}
			}
			else if (ShippedDefaults.SoftAccept(ShippedDefaults.AppliesUnderFusion(body, this), body.AcceptSteadyFits) && body.StartReadsCoverage)
			{
				if (qualityIsNice)
				{
					state.RowsSinceMarginAccept = 0;
					state.SoftAcceptRows = 0;
				}
				else
				{
					if (state.RowsSinceMarginAccept < int.MaxValue)
						++state.RowsSinceMarginAccept;
					softAccepted = AdvanceSoftAccept(body, state, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees) && !firstAcceptWaits;
				}
				if (softAccepted)
				{
					state.SoftAcceptedThisStep = true;
					if (state.RowsSinceMarginAccept >= TrackerDefaults.SOFT_ACCEPT_QUIET_ROWS)
						qualityIsNice = true;
				}
			}
			state.ReferenceStoredThisStep = false;
			float referenceReading = ReferenceReadingFor(MarginResidualTerm, SourceFor(body), instantQuality, body.RegistrationQuality);
			if (softAccepted)
				referenceReading = Mathf.Max(referenceReading, HoldReleaseBarOf(body));
			bool mayBecomeReference = MayBecomeReference(
				EdgeLayerApplies(EnableHoldRelease, body), state.ColdStartWindow, warmup,
				referenceReading,
				HoldReleaseBarOf(body));
			// While something covers part of the body, the sites that remain leave some directions free, and a fit that slides along them is accepted
			// as readily as one that does not. A body that is held in the world stays where it is until its view recovers.
			bool dipHeld = false;
			if (ShippedDefaults.DipHold(fusionDefaults, ShippedDefaults.DipHoldSetting(body)))
			{
				(state.InViewDip, state.ViewDipHealthyRows, state.ViewHealthyStreak) = AdvanceViewDip(state.InViewDip, state.ViewDipHealthyRows,
					state.ViewHealthyStreak, body.TrackingHealth, body.TrackingHealthSettings.ExpectationUpdateHealth);
				dipHeld = DipWithholdsFit(state.InViewDip, state.HasLastGoodPose, warmup) && WorldPoseIsHealthy(context);
			}
			else
			{
				state.InViewDip = false;
				state.ViewDipHealthyRows = 0;
				state.ViewHealthyStreak = 0;
			}
			// A step on which the AR camera moves the picture of the body fast is blurred, and its fit is the least reliable one of the run. The pose the
			// body is held at is carried by the AR camera, so it is kept as it is until the camera slows down.
			bool fastMotionHeld = false;
			if (ShippedDefaults.FastMotionHold(fusionDefaults, ShippedDefaults.FastMotionHoldSetting(body)))
			{
				float cameraMotionPixels = float.NaN;
				if (state.HasLastGoodPose && hadPreviousCamera)
				{
					cameraMotionPixels = body.CameraMotionPixels(state.LastGoodPosition, state.LastGoodRotation, previousCameraPosition,
						previousCameraRotation, state.PreviousStepCameraPosition, state.PreviousStepCameraRotation);
				}
				if (!float.IsNaN(cameraMotionPixels))
					state.Step.CameraMotionPixels = cameraMotionPixels;
				fastMotionHeld = FastMotionWithholdsFit(cameraMotionPixels, ShippedDefaults.FastMotionHoldPixels, state.HasLastGoodPose, warmup)
					&& WorldPoseIsHealthy(context);
			}
			bool fitWithheld = dipHeld || fastMotionHeld;
			if (fitWithheld)
				mayBecomeReference = false;
			state.Step.DipHeld = dipHeld;
			state.Step.FastMotionHeld = fastMotionHeld;
			state.Step.ViewHealth = body.TrackingHealth.Available ? body.TrackingHealth.AcceptanceHealth : 0f;
			state.Step.FirstAcceptWaits = firstAcceptWaits;
			state.Step.FirstAcceptBarWaits = firstAcceptBarWaits;
			state.Step.StartSignal = startSignal;
			state.Step.FirstAcceptBarRows = state.FirstAcceptBarRows;
			state.Step.SoftAcceptRows = state.SoftAcceptRows;
			state.Step.SoftAccepted = softAccepted;
			state.Step.RowsSinceMarginAccept = state.RowsSinceMarginAccept;
			state.Step.Accepted = qualityIsNice;
			state.Step.MayBecomeReference = mayBecomeReference;
			state.Step.ReferenceReading = referenceReading;

			// The re-check of the predicted pose runs on a held body when the tooling asks for it. A cycle that
			// takes the lock back on its refined pose ends the hold on this row, and the release loop is not
			// asked about it again.
			bool tookBackByRecheck = state.HoldActive && DriveFusionRecheck(body, state, nativePos, nativeRot, ref correctionAttempted, ref correctionPoseAvailable,
				ref correctionPose, ref correctionNativeStatus);
			if (!tookBackByRecheck && AdvanceHoldRelease(body, state, instantQuality, warmup, ref correctionAttempted, ref correctionPoseAvailable,
				ref correctionPose, ref correctionNativeStatus))
			{
				// Held at the seat. Nothing is stored while the lock is given up,
				// and the world pose the display is showing is the one it was
				// already showing, so a release is not something a person can see.
				state.RecoveryFrameCount = 0;
				state.FusionState = BodyFusionState.Holding;
				state.SeatSettledOnNextStore = true;
				state.Step.Outcome = FusionStepOutcome.HeldByRelease;
				RecordStationaryCorrectionTelemetry(
					body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
					correctionAttempted, correctionNativeStatus,
					correctionPoseAvailable, correctionPose);
				return;
			}

			// Tracking loss: stationary with AR pose fusion never auto-resets.
			// The world-space stored pose holds position until the user manually resets.
			// The tracker will eventually recover it, and auto-reset would destroy it.

			// Nice-gate with recovery delay: after a quality dip, require FRAMES_TO_RECOVER
			// consecutive nice frames before re-accepting native poses. This lets the optimizer
			// settle after intrinsics changes (autofocus) or brief occlusions.

			// The settling window suppresses the recovery delay, not the band. What it
			// exists for is to let the solve converge without the re-acceptance count
			// standing in the way; what it must not do is store a reference from a
			// frame the acceptance decision refuses, because a reference committed
			// with no gate at all is the reference every later comparison is made
			// against. So the band is asked on every frame, settling or not, and the
			// exemption below is only from the count.
			if (qualityIsNice)
			{
				bool wasHolding = state.FusionState == BodyFusionState.Holding;
				bool recovered = warmup || !wasHolding
					|| ++state.RecoveryFrameCount >= TrackerDefaults.FRAMES_TO_RECOVER;

				if (recovered && coast.SuppressStore)
				{
					// This frame's solve ran before the seed reached the tracker, so
					// it is still a solve from the pose the tracker froze at rather
					// than from where the body actually is. The first solve eligible
					// to become the stored reference is the next completed one.
					state.RecoveryFrameCount = 0;
					state.FusionState = BodyFusionState.Holding;
					state.Step.Outcome = FusionStepOutcome.AcceptedSuppressed;
				}
				else if (recovered)
				{
					state.RecoveryFrameCount = 0;
					state.RecoveryWindowPassedThisFrame =
						RecoveryWindowCompletesThisFrame(body, state);
					if (!mayBecomeReference)
					{
						// Nice enough to keep tracking on, not good enough to become the
						// reference: the release loop is counting this frame toward giving
						// the lock up. The display stays on the last solve that held it.
						state.Step.Outcome = FusionStepOutcome.AcceptedNotStored;
						// A solve that is withheld because the view dips or the picture moves fast is put back at the held pose when it stands outside
						// the prediction gate, as a solve that was not accepted is.
						if (fitWithheld && !state.HoldActive && ShippedDefaults.PredictionGate(fusionDefaults))
							ApplyPredictionGate(body, state, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees,
								ref correctionAttempted, ref correctionPoseAvailable, ref correctionPose, ref correctionNativeStatus);
					}
					else if (!StoreAcceptedPose(state, nativePos, nativeRot, instantQuality, state.RecoveryWindowPassedThisFrame
							? PoseProvenance.RecoveryHypothesis
							: PoseProvenance.Optimizer))
					{
						body.RecordRejectedPose();
						state.Step.Outcome = FusionStepOutcome.AcceptedNotStored;
					}
					else
					{
						state.ReferenceStoredThisStep = true;
						state.Step.Outcome = FusionStepOutcome.Stored;
						// The display of a body that tracks by the silhouette method follows its stored solve, as it always has.
						if (EdgeLayerApplies(true, body))
						{
							BindSettleDistance(body);
							SettleStoredFit(state, nativePos, nativeRot, warmup, _settleDistance, out float settleMovePixels);
							state.Step.SettleMovePixels = float.IsNaN(settleMovePixels) ? -1f : settleMovePixels;
						}
						if (state.RecoveryWindowPassedThisFrame)
							state.PublishRecoveryProvenanceOnce = true;
					}
					state.TimeSinceLastCorrection = 0;
					state.TimeBelowNice = 0;
					state.FusionState = BodyFusionState.Accepting;
				}
				else
				{
					// Recovering — still holding, keep correcting C++
					state.TimeSinceLastCorrection += timerDeltaSeconds;
					state.FusionState = BodyFusionState.Holding;
					state.Step.Outcome = FusionStepOutcome.AcceptedRecovering;
				}
			}
			else
			{
				// Quality below nice — don't display native pose. Hold last good pose.
				// After a grace period, periodically feed pose back to native to help recovery.
				state.RecoveryFrameCount = 0;
				state.Step.Outcome = FusionStepOutcome.Refused;
				if (!state.HoldActive && ShippedDefaults.PredictionGate(ShippedDefaults.AppliesUnderFusion(body, this)))
					ApplyPredictionGate(body, state, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees,
						ref correctionAttempted, ref correctionPoseAvailable, ref correctionPose, ref correctionNativeStatus);
				StationaryCorrectionTimerStep timerStep =
					StationaryCorrectionTimer.Advance(
						state.TimeBelowNice,
						state.TimeSinceLastCorrection,
						timerDeltaSeconds,
						state.HasLastGoodPose,
						TrackerDefaults.QUALITY_GATE_RECOVERY_DELAY,
						TrackerDefaults.QUALITY_GATE_CORRECTION_INTERVAL);
				state.TimeBelowNice = timerStep.TimeBelowNice;
				state.TimeSinceLastCorrection = timerStep.TimeSinceLastCorrection;
				state.FusionState = BodyFusionState.Holding;

				if (timerStep.ShouldAttempt)
				{
					Vector3 correctionPosition = state.LastGoodPosition;
					Quaternion correctionRotation = state.LastGoodRotation;
					if (!ConversionUtils.IsUsablePose(correctionPosition, correctionRotation))
					{
						// Feeding an unusable pose back would replace the tracker's own
						// recoverable estimate with it, so the correction is skipped and
						// the timer is left to retry on a later frame.
						body.RecordRejectedPose();
						RecordStationaryCorrectionTelemetry(
							body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
							correctionAttempted, correctionNativeStatus,
							correctionPoseAvailable, correctionPose);
						return;
					}
					correctionPose = ConversionUtils.GetConvertedPose(
						correctionPosition, correctionRotation);
					correctionAttempted = true;
					correctionPoseAvailable = true;
					correctionNativeStatus = WriteBodyPose(body, ref correctionPose);
					if (FrameWriteGuard.RecordOutOfFrame(
						correctionNativeStatus, "FT_SetBodyPose"))
					{
						state.TimeSinceLastCorrection = 0;
					}
				}
			}

			RecordStationaryCorrectionTelemetry(
				body, state, timeBelowNiceBefore, timeSinceLastCorrectionBefore,
				correctionAttempted, correctionNativeStatus,
				correctionPoseAvailable, correctionPose);
		}

		/// <summary>Takes the AR camera of this step and gives how far it moved since the step before, in metres and degrees.</summary>
		private void NoteStepCamera(BodyState state, ref float stepMetres, ref float stepDegrees)
		{
			Pose? camera = CompletedStepCameraPose();
			if (!camera.HasValue)
				return;
			if (state.HasPreviousStepCamera)
			{
				stepMetres = Vector3.Distance(camera.Value.position, state.PreviousStepCameraPosition);
				stepDegrees = Quaternion.Angle(camera.Value.rotation, state.PreviousStepCameraRotation);
			}
			state.PreviousStepCameraPosition = camera.Value.position;
			state.PreviousStepCameraRotation = camera.Value.rotation;
			state.HasPreviousStepCamera = true;
		}

		/// <summary>
		/// Whether a solve stands further from the pose a stationary body is held at than the AR pose can explain. The bound is the fixed one
		/// of <see cref="TrackerDefaults.PREDICTION_GATE_TRANSLATION_MM"/> and <see cref="TrackerDefaults.PREDICTION_GATE_ROTATION_DEG"/>
		/// plus the distance and the angle the AR camera moved between two steps, times <see cref="TrackerDefaults.PREDICTION_GATE_CAMERA_STEP_FACTOR"/>.
		/// </summary>
		internal static bool OutsidePredictionGate(Vector3 heldPosition, Quaternion heldRotation, Vector3 solvePosition, Quaternion solveRotation,
			float cameraStepMetres, float cameraStepDegrees, float scale = 1f)
		{
			float translationBound = scale * (0.001f * TrackerDefaults.PREDICTION_GATE_TRANSLATION_MM
				+ TrackerDefaults.PREDICTION_GATE_CAMERA_STEP_FACTOR * cameraStepMetres);
			float rotationBound = scale * (TrackerDefaults.PREDICTION_GATE_ROTATION_DEG
				+ TrackerDefaults.PREDICTION_GATE_CAMERA_STEP_FACTOR * cameraStepDegrees);
			return Vector3.Distance(heldPosition, solvePosition) > translationBound
				|| Quaternion.Angle(heldRotation, solveRotation) > rotationBound;
		}

		/// <summary>The factor the prediction gate is widened by before any solve has been accepted, when the soft accept is on.</summary>
		private static float FirstGateScale(BodyState state, bool softAccept) =>
			softAccept && !state.HasLastGoodPose ? TrackerDefaults.SOFT_ACCEPT_FIRST_GATE_SCALE : 1f;

		/// <summary>The pose a stationary body is held at, which is the last accepted solve and, when none was accepted, the seat.</summary>
		private bool TryGetHeldPrediction(TrackedBody body, BodyState state, out Vector3 position, out Quaternion rotation)
		{
			if (state.HasLastGoodPose)
			{
				position = state.LastGoodPosition;
				rotation = state.LastGoodRotation;
				return true;
			}
			return TryPrepareSeatSeed(body, out position, out rotation, out _);
		}

		/// <summary>
		/// Advances the dip of the view of a stationary body by one step. A view is in a dip from the step on which the share of its agreeing sites against
		/// what it normally reaches stands below the floor, which is the share at which a frame teaches the body what its view normally reaches, until that
		/// share has stood at or above the floor for <see cref="TrackerDefaults.DIP_HOLD_RECOVERY_ROWS"/> steps in a row. A dip begins only after the view has
		/// stood at or above the floor for <see cref="TrackerDefaults.DIP_HOLD_ENTRY_ROWS"/> steps in a row, because the pose it holds is then one that a
		/// healthy view has just stored. A step that cannot be read, because it carries too few sites or because the view is new and has no expectation, is no
		/// evidence of a dip, so it never begins one, it counts toward the end of one, and it ends the run of healthy steps that a dip needs to begin.
		/// </summary>
		internal static (bool InDip, int HealthyRows, int Streak) AdvanceViewDip(bool inDip, int healthyRows, int streak, TrackingHealthSnapshot health, float floor)
		{
			bool read = health.Available && health.ExpectationAvailable;
			if (read && health.AcceptanceHealth < floor)
				return (inDip || streak >= TrackerDefaults.DIP_HOLD_ENTRY_ROWS, 0, 0);
			streak = read ? System.Math.Min(streak + 1, TrackerDefaults.DIP_HOLD_ENTRY_ROWS) : 0;
			if (!inDip)
				return (false, 0, streak);
			healthyRows++;
			return healthyRows >= TrackerDefaults.DIP_HOLD_RECOVERY_ROWS ? (false, 0, streak) : (true, healthyRows, streak);
		}

		/// <summary>
		/// Whether a dip of the view withholds the fit of this step. The first fits are never held back: a body that holds no stored pose has nothing to keep
		/// still, and the warmup rows store.
		/// </summary>
		internal static bool DipWithholdsFit(bool inDip, bool hasStoredPose, bool warmup) => inDip && hasStoredPose && !warmup;

		/// <summary>
		/// Whether this step's solve is accepted on the coverage score alone. The score has to clear the start bar, which is the bar for a fit to become
		/// the pose the body is held at, and the solve has to lie inside the prediction gate.
		/// </summary>
		private bool AcceptByCoverage(TrackedBody body, BodyState state, Vector3 nativePos, Quaternion nativeRot,
			float cameraStepMetres, float cameraStepDegrees)
		{
			if (!TryGetHeldPrediction(body, state, out Vector3 heldPosition, out Quaternion heldRotation))
				return false;
			bool covered = body.StartSignal >= body.StartBar;
			bool inside = !OutsidePredictionGate(heldPosition, heldRotation, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees,
				FirstGateScale(state, true));
			state.Step.Covered = covered;
			state.Step.InsideGate = inside;
			return covered && inside;
		}

		/// <summary>
		/// Counts the steps in a row on which the coverage score of the solve clears the start bar and the solve lies inside the prediction
		/// gate, and says whether there are enough of them to accept the solve.
		/// </summary>
		internal static bool AdvanceSoftAcceptRows(ref int rows, bool covered, bool insideGate, int required)
		{
			rows = covered && insideGate ? rows + 1 : 0;
			return rows >= required;
		}

		/// <summary>
		/// Whether this step's solve is accepted by the soft accept. The coverage score has to clear the start bar to begin a run of verified steps
		/// and only the stop bar to go on with it, as the start and the stop of a body do, and the solve has to lie inside the prediction gate.
		/// </summary>
		private bool AdvanceSoftAccept(TrackedBody body, BodyState state, Vector3 nativePos, Quaternion nativeRot,
			float cameraStepMetres, float cameraStepDegrees)
		{
			if (!TryGetHeldPrediction(body, state, out Vector3 heldPosition, out Quaternion heldRotation))
			{
				state.SoftAcceptRows = 0;
				return false;
			}
			bool going = state.SoftAcceptRows >= TrackerDefaults.SOFT_ACCEPT_ROWS;
			bool covered = body.StartSignal >= (going ? body.StopBar : body.StartBar);
			bool inside = !OutsidePredictionGate(heldPosition, heldRotation, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees,
				FirstGateScale(state, true));
			state.Step.Covered = covered;
			state.Step.InsideGate = inside;
			int rows = state.SoftAcceptRows;
			bool accepted = AdvanceSoftAcceptRows(ref rows, covered, inside, TrackerDefaults.SOFT_ACCEPT_ROWS);
			state.SoftAcceptRows = rows;
			return accepted;
		}

		/// <summary>
		/// Adds the solve of this step to the window of the newest solves, when the coverage score supports it and the lock is held. Any other step
		/// clears the window, so the window is always a run of good steps in a row.
		/// </summary>
		private static void NoteSolve(TrackedBody body, BodyState state, Vector3 nativePos, Quaternion nativeRot)
		{
			if (state.HoldActive || body.StartSignal < body.StartBar)
			{
				state.SolveWindow.Clear();
				return;
			}
			state.SolveWindow.Add(nativePos, nativeRot);
		}

		/// <summary>
		/// Whether the first accept of a stationary body has to wait, because the solve has not yet stood still for
		/// <see cref="TrackerDefaults.FIRST_ACCEPT_SETTLE_ROWS"/> steps in a row. Only the first accept waits, and only for as many steps as the
		/// stabilization takes, so a fit that never stands still is accepted in the end.
		/// </summary>
		internal static bool FirstAcceptWaits(bool hasLastGoodPose, int framesSinceTrackingStarted, SolveWindow window) =>
			!hasLastGoodPose
			&& framesSinceTrackingStarted <= TrackerDefaults.FIRST_ACCEPT_MAX_WAIT_ROWS
			&& !window.Within(TrackerDefaults.FIRST_ACCEPT_SETTLE_ROWS,
				0.001f * TrackerDefaults.FIRST_ACCEPT_SPREAD_MM, TrackerDefaults.FIRST_ACCEPT_SPREAD_DEG);

		/// <summary>
		/// Advances the run of steps in a row on which the coverage score of the start stood at or above the bar of the first accept. A step below the bar
		/// ends the run, and a run is counted up to the number of steps the first accept needs.
		/// </summary>
		internal static int AdvanceFirstAcceptBarRows(int rows, float startSignal, float bar) =>
			startSignal >= bar ? System.Math.Min(rows + 1, TrackerDefaults.FIRST_ACCEPT_SETTLE_ROWS) : 0;

		/// <summary>
		/// Whether the first accept of a stationary body has to wait for the bar of the first accept, because the coverage score of the start has not stood
		/// at or above it on <see cref="TrackerDefaults.FIRST_ACCEPT_SETTLE_ROWS"/> steps in a row. Only a body that holds no accepted fit waits, and it
		/// waits for as long as the score stays below the bar.
		/// </summary>
		internal static bool FirstAcceptBarWaits(bool hasLastGoodPose, int barRows) =>
			!hasLastGoodPose && barRows < TrackerDefaults.FIRST_ACCEPT_SETTLE_ROWS;

		/// <summary>
		/// Whether the prediction gate leaves a body alone. A body that the start search started stands away from its guide, so until it has accepted a
		/// fit the only pose it could be put back at is that guide. Once it has accepted one, the pose it is held at is a fit, and the gate serves it as
		/// it serves any other body.
		/// </summary>
		internal static bool PredictionGateSkipsBody(bool startedFromSearch, bool hasAcceptedPose) =>
			startedFromSearch && (!hasAcceptedPose || (TrackingOverrides.PredictionGateSparesSearchStarts ?? false));

		/// <summary>
		/// Puts the tracker back at the pose the body is held at when a solve that was not accepted has left it. A body that the start search started is
		/// left alone until it has accepted a fit, because the search places it away from its guide.
		/// </summary>
		/// <remarks>
		/// The pose it is put back at is the last accepted solve and, when none was accepted, the seat. Both are poses that no unaccepted
		/// solve can move, and it goes out through the same write that the quality gate and the coast's return use. An accepted solve is
		/// never looked at here, so the settle pull that follows a moved object is untouched.
		/// </remarks>
		private void ApplyPredictionGate(TrackedBody body, BodyState state, Vector3 nativePos, Quaternion nativeRot,
			float cameraStepMetres, float cameraStepDegrees, ref bool correctionAttempted, ref bool correctionPoseAvailable,
			ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			if (PredictionGateSkipsBody(body.StartedFromSearch, state.HasLastGoodPose))
				return;
			if (ShippedDefaults.CorrectionAfterFirstFit && !state.HasLastGoodPose)
				return;
			if (!TryGetHeldPrediction(body, state, out Vector3 heldPosition, out Quaternion heldRotation))
				return;
			bool softAccept = ShippedDefaults.SoftAccept(ShippedDefaults.AppliesUnderFusion(body, this), body.AcceptSteadyFits) || CoverageDecidesFor(body);
			if (!OutsidePredictionGate(heldPosition, heldRotation, nativePos, nativeRot, cameraStepMetres, cameraStepDegrees, FirstGateScale(state, softAccept)))
				return;
			if (!TrySeedTracker(body, heldPosition, heldRotation, "World", out FTTrackingPose seed, out int status))
				return;
			correctionAttempted = true;
			correctionPoseAvailable = true;
			correctionPose = seed;
			correctionNativeStatus = status;
			state.TimeSinceLastCorrection = 0;
			++state.PredictionReseeds;
		}

		private void RecordStationaryCorrectionTelemetry(
			TrackedBody body,
			BodyState state,
			float timeBelowNiceBefore,
			float timeSinceLastCorrectionBefore,
			bool attempted,
			int nativeStatus,
			bool poseAvailable,
			FTTrackingPose pose)
		{
			if (!_completedFrameInstrumentationContext.HasValue)
				return;
			_lastCorrectionTelemetry[body] = new StationaryCorrectionTelemetry(
				_completedFrameInstrumentationContext.Value,
				timeBelowNiceBefore,
				state.TimeBelowNice,
				timeSinceLastCorrectionBefore,
				state.TimeSinceLastCorrection,
				attempted,
				nativeStatus,
				poseAvailable,
				pose,
				1);
		}

		/// <summary>
		/// Whether the AR session's own reported state says its world pose can be
		/// trusted this frame.
		/// </summary>
		/// <remarks>
		/// An unlimited out-of-view coast rests entirely on this. It is read from
		/// the session states the platform already publishes — the frame composed
		/// without fault, a world pose present, the session tracking, no reason
		/// given for not tracking. Every term is an existing online state; none is a number
		/// this layer chose.
		/// </remarks>
		private static bool WorldPoseIsHealthy(FusionFrameContext context)
		{
			if (!context.IsValid)
				return false;

			FusionFrameObservation observation = context.Observation;
			if (!observation.HasWorldFromCamera)
				return false;
			if (observation.SessionState != UnityEngine.XR.ARFoundation.ARSessionState.SessionTracking)
				return false;
			if (observation.NotTrackingReason != UnityEngine.XR.ARSubsystems.NotTrackingReason.None)
				return false;
			return true;
		}

		/// <summary>
		/// What this frame's camera pose says about whether the body should be in
		/// frame.
		/// </summary>
		/// <remarks>
		/// The pose the predicate projects is the one the system believes, which is
		/// the stored pose — not the transform, which carries the
		/// smoothed display pose and lags it. When nothing is stored yet the
		/// transform is the only belief there is, so it is used.
		/// </remarks>
		private BodyViewVisibility MeasureViewVisibility(
			TrackedBody body, BodyState state, FusionFrameContext context)
		{
			if (!CameraProjection.TryRead(out CameraProjection projection))
				return default;

			Matrix4x4 worldFromCamera = context.Observation.WorldFromCameraUnity;

			Vector3 believedPosition;
			Quaternion believedRotation;
			if (state.HasLastGoodPose)
			{
				believedPosition = state.LastGoodPosition;
				believedRotation = state.LastGoodRotation;
			}
			else if (body.StartedFromSearch && TryGetNativeWorldPose(body, out believedPosition, out believedRotation))
			{
				// A body the search started may stand any distance from the guide it was placed at, so until a pose has been accepted the
				// pose the tracker solved is what is believed about where it is, and not the pose it was placed at.
			}
			else
			{
				body.ReadWorldPoseForDecision(out believedPosition, out believedRotation);
			}
			if (!ConversionUtils.IsUsablePose(believedPosition, believedRotation))
				return default;

			Bounds localBounds = body.ComputeLocalBounds();
			if (localBounds.size.sqrMagnitude <= 0f)
				return default;

			Matrix4x4 cameraFromWorld = worldFromCamera.inverse;
			Matrix4x4 worldFromBody = Matrix4x4.TRS(
				believedPosition, believedRotation, body.transform.lossyScale);
			bool couldBeInView = ViewVisibility.CouldBeInView(
				projection, cameraFromWorld, worldFromBody, localBounds);
			float fraction = ViewVisibility.InViewFraction(
				projection, cameraFromWorld, worldFromBody, localBounds);
			float centreDepth = (cameraFromWorld * worldFromBody)
				.MultiplyPoint3x4(localBounds.center).z;
			// The conservative answer decides: a body that could be in view is in view, and a body that is
			// not is in view only when some of it projects into the picture.
			bool shouldBeVisible = couldBeInView || fraction > 0f;
			return new BodyViewVisibility(true, fraction, couldBeInView, shouldBeVisible,
				centreDepth,
				Vector3.Scale(localBounds.size, body.transform.lossyScale).magnitude);
		}

		/// <summary>
		/// Hands the held pose to the tracker once, on the frame a coasted body
		/// comes back into view.
		/// </summary>
		/// <remarks>
		/// This is the one explicit, gated correction write on this path —
		/// the same call the quality gate already makes, at a moment chosen by the
		/// visibility predicate rather than by a timer. It seeds the solve and
		/// updates no stored reference: <see cref="StoreAcceptedPose"/> refuses
		/// anything that is not the tracker's own solve, so a held pose cannot
		/// become the "last good" pose by this or any other route.
		/// </remarks>
		private bool TrySeedTrackerFromHeldPose(
			TrackedBody body,
			BodyState state,
			out FTTrackingPose seedPose,
			out int nativeStatus)
		{
			seedPose = default;
			nativeStatus = StationaryCorrectionTelemetry.NativeStatusNotAttempted;
			if (!state.HasLastGoodPose)
				return false;

			Vector3 heldPosition = state.LastGoodPosition;
			Quaternion heldRotation = state.LastGoodRotation;
			if (!ConversionUtils.IsUsablePose(heldPosition, heldRotation))
			{
				body.RecordRejectedPose();
				return false;
			}

			return TrySeedTracker(body, heldPosition, heldRotation,
				"World", out seedPose, out nativeStatus);
		}

		/// <summary>
		/// Decides whether this body keeps its lock, and returns whether it is held
		/// at its seat after the decision.
		/// </summary>
		/// <remarks>
		/// <para>Three things, in the order they can happen. A body holding its lock
		/// gives it up when the score says this frame's solve has stopped looking
		/// like the body, for as many consecutive frames as a loss takes. A body
		/// that has given it up is seeded back onto its seat, and seeded again every
		/// time another loss window passes without the score recovering, so a solve
		/// that wanders while it is held is put back rather than verified from
		/// wherever it drifted to. And a body that has given it up takes it back
		/// only when the score clears the same bar on enough of the newest frames of
		/// its hold, which by default is as many consecutive frames as a start
		/// takes.</para>
		/// <para>The decision is taken on this frame's score and never on the
		/// running average. The average is a thirty-frame mean, so after any stretch
		/// with no sites it climbs from zero over thirty frames whatever the solve
		/// is doing, and a decision read off that number cannot tell a pose that is
		/// right from one that has left the body.</para>
		/// <para>Re-entry is attempted only while the body is somewhere the camera
		/// can see it. A score taken on a frame the body is not in says nothing
		/// about the pose, and counting it would let a body take its lock back on
		/// evidence about the background. The predicate is the one the manager
		/// already computes for this frame rather than a second in-picture rule,
		/// because two rules would give the same body two answers to one
		/// question.</para>
		/// </remarks>
		private bool AdvanceHoldRelease(TrackedBody body, BodyState state,
			float instantQuality, bool warmup, ref bool correctionAttempted, ref bool correctionPoseAvailable,
			ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			// The switch is read before anything is measured or counted, so with
			// it off the release counters, the seat write and the decision record
			// are left untouched. A body that tracks by the silhouette method never gives its lock up
			// this way, because the bar of this loop is a coverage bar and the body has no sites.
			if (!EdgeLayerApplies(EnableHoldRelease, body)) return false;

			// Unavailable health is missing evidence, not evidence of zero. The
			// loop judges relative to what this view normally achieves; before
			// that expectation is known, there is nothing to judge against.
			if (state.ColdStartWindow)
			{
				state.Decision.Release = default;
				state.Decision.HoldActive = state.HoldActive;
				return state.HoldActive;
			}

			float holdReleaseBar = HoldReleaseBarOf(body);
			QualitySignal releaseSignal = QualitySignal.Instant;
			// With the coverage score deciding, the lock is held while the averaged coverage score stands at the stop bar, as the lock of a body
			// without pose fusion is. When it does not, the registration margin still holds the lock where it reads a good fit.
			if (CoverageHoldsFor(body))
			{
				instantQuality = body.StopSignal;
				holdReleaseBar = body.StopBar;
				releaseSignal = QualitySignal.Averaged;
			}
			// A solve that the coverage score and the prediction gate verified on several steps in a row keeps the lock as a solve the margin
			// clears does, so a body on a soft picture is not given up on a margin that picture cannot reach.
			bool holdsLock = instantQuality >= holdReleaseBar || state.SoftAcceptedThisStep;
			state.Decision.Release = LifecycleComparison.Taken(
				instantQuality, holdReleaseBar,
				releaseSignal, holdsLock);

			// One reading, asked from whichever side the body is on. Not held, it is the
			// give-up and falls under GiveUpBar to enter a hold; held, it is the
			// take-back and clears TakeBackBar to leave one. Both bars are the same
			// constant today and both read the instant scalar; the record names them
			// apart to say which one judged a frame, not to imply they differ.
			state.Decision.Release.Direction = state.HoldActive
				? HoldDirection.TakeBack
				: HoldDirection.GiveUp;
			state.Decision.Release.GiveUpBar = holdReleaseBar;
			state.Decision.Release.TakeBackBar = holdReleaseBar;

			if (state.HoldActive)
			{
				bool inPicture = state.Visibility.Measured && state.Visibility.CouldBeInView;
				bool clears = holdsLock && inPicture;
				state.TakeBackHistory = (state.TakeBackHistory << 1) | (clears ? 1u : 0u);
				if (clears)
				{
					state.ReleaseFrameCount = 0;
					++state.ReentryFrameCount;
					// Only a clearing frame can complete a window: a window ending on a
					// frame that does not clear holds no more clears than the one ending
					// on the last clearing frame before it, which would already have fired.
					if (TakeBackWindowClears(state.TakeBackHistory,
						TrackerDefaults.TAKE_BACK_REQUIRED_FRAMES, TrackerDefaults.TAKE_BACK_WINDOW_FRAMES))
					{
						state.HoldActive = false;
						state.ReentryFrameCount = 0;
						state.TakeBackHistory = 0;
						state.HoldSeedAttemptsThisHold = 0;
						state.Recheck = default;
						state.Decision.ReEntered = true;
					}
				}
				else
				{
					state.ReentryFrameCount = 0;
					state.Decision.ReEntryRefused = true;
					if (!holdsLock &&
						++state.ReleaseFrameCount >= TrackerDefaults.FRAMES_TO_LOSE)
					{
						state.ReleaseFrameCount = 0;
						// With the re-check on, the cycle writes the seat itself every few rows and starts the
						// modality clean, so the repeated seed of the hold is left to it.
						if (!FusionRecheckOn(body))
							ApplyHoldSeedSchedule(body, state, ref correctionAttempted,
								ref correctionPoseAvailable, ref correctionPose,
								ref correctionNativeStatus);
					}
				}
			}
			else if (holdsLock)
			{
				state.ReleaseFrameCount = 0;
			}
			else if (!warmup &&
				++state.ReleaseFrameCount >= TrackerDefaults.FRAMES_TO_LOSE)
			{
				state.HoldActive = true;
				state.ReleaseFrameCount = 0;
				state.ReentryFrameCount = 0;
				state.TakeBackHistory = 0;
				state.HoldSeedAttemptsThisHold = 0;
				state.Decision.StopFired = true;
				ApplyHoldSeedSchedule(body, state, ref correctionAttempted,
					ref correctionPoseAvailable, ref correctionPose,
					ref correctionNativeStatus);
				if (FusionRecheckOn(body))
					BeginFusionRecheckAtEntry(body, state);
			}

			state.Decision.HoldActive = state.HoldActive;
			return state.HoldActive;
		}

		/// <summary>
		/// Writes the seat to the tracker when asked, and stops and starts the body's tracking, so the next step renders and
		/// predicts at the seat and from nothing older. False when the seat cannot be written or read.
		/// </summary>
		private bool WriteSeatAndRestartModality(TrackedBody body, bool writeSeat,
			ref bool correctionAttempted, ref bool correctionPoseAvailable, ref FTTrackingPose correctionPose,
			ref int correctionNativeStatus)
		{
			if (body == null)
				return false;
			if (writeSeat)
			{
				if (!TrySeedTrackerFromSeat(body, out FTTrackingPose seedPose, out int status))
					return false;
				correctionAttempted = true;
				correctionPoseAvailable = true;
				correctionPose = seedPose;
				correctionNativeStatus = status;
			}
			else if (!TryPrepareSeatSeed(body, out _, out _, out _))
			{
				return false;
			}
			FTBridge.FT_StopTrackingBody(body.BodyId);
			NativeBodyWriteJournal.RecordCall(body.BodyId, "FT_StopTrackingBody", true);
			FTBridge.FT_StartTrackingBody(body.BodyId);
			NativeBodyWriteJournal.RecordCall(body.BodyId, "FT_StartTrackingBody", true);
			return true;
		}

		/// <summary>Whether this held body re-checks its predicted pose, by the shipped default for an edge body under pose fusion or an explicit request.</summary>
		private bool FusionRecheckOn(TrackedBody body) =>
			EdgeLayerApplies(true, body) &&
			FusionRecovery.RecheckOn(ShippedDefaults.FusionRecoveryRecheck(ShippedDefaults.AppliesUnderFusion(body, this)));

		/// <summary>The rows in a row this body's re-check needs before it takes the lock back, by the shipped default or an explicit request.</summary>
		private int FusionRecheckRunRows(TrackedBody body) =>
			FusionRecovery.RunRowsFor(ShippedDefaults.FusionRecheckRun(ShippedDefaults.AppliesUnderFusion(body, this)));

		/// <summary>The rows between the starts of two cycles and the rows a cycle refines, which a re-check judged on a run lengthens to the run.</summary>
		private void RecheckRows(TrackedBody body, out int interval, out int refine)
		{
			int required = FusionRecheckRunRows(body);
			refine = required > 1 ? required : FusionRecovery.RECHECK_REFINE_ROWS;
			interval = required > 1 ? System.Math.Max(FusionRecovery.RECHECK_INTERVAL_ROWS, required + 1) : FusionRecovery.RECHECK_INTERVAL_ROWS;
		}

		/// <summary>Begins a new cycle from the seat on this row, in place of waiting for the interval.</summary>
		private void RestartFusionRecheck(TrackedBody body, BodyState state, ref bool correctionAttempted,
			ref bool correctionPoseAvailable, ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			RecheckRows(body, out int interval, out int refine);
			FusionRecovery.Next(ref state.Recheck, true, true, interval, refine);
			if (WriteSeatAndRestartModality(body, true, ref correctionAttempted, ref correctionPoseAvailable,
				ref correctionPose, ref correctionNativeStatus))
				state.Decision.Recheck.Began = true;
			else
				state.Recheck = default;
		}

		/// <summary>
		/// Begins the first cycle of a hold on the row the lock is given up. The seat has just been written by the hold's
		/// own seed, so only the modality is started clean.
		/// </summary>
		private void BeginFusionRecheckAtEntry(TrackedBody body, BodyState state)
		{
			bool inPicture = state.Visibility.Measured && state.Visibility.CouldBeInView;
			bool attempted = false, available = false;
			FTTrackingPose pose = default;
			int status = 0;
			RecheckRows(body, out int interval, out int refine);
			FusionRecheckAction action = FusionRecovery.Next(ref state.Recheck, inPicture, true, interval, refine);
			if (action == FusionRecheckAction.Begin &&
				WriteSeatAndRestartModality(body, false, ref attempted, ref available, ref pose, ref status))
			{
				state.Decision.Recheck.Began = true;
			}
			else
			{
				state.Recheck = default;
			}
		}

		/// <summary>
		/// Advances the re-check of a held body by one row. Returns true when a cycle judged the refined pose and took the
		/// lock back on it.
		/// </summary>
		private bool DriveFusionRecheck(TrackedBody body, BodyState state, Vector3 nativePos, Quaternion nativeRot, ref bool correctionAttempted,
			ref bool correctionPoseAvailable, ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			if (!FusionRecheckOn(body))
				return false;
			bool inPicture = state.Visibility.Measured && state.Visibility.CouldBeInView;
			RecheckRows(body, out int interval, out int refine);
			FusionRecheckAction action = FusionRecovery.Next(ref state.Recheck, inPicture, false, interval, refine);
			if (action == FusionRecheckAction.Begin)
			{
				if (WriteSeatAndRestartModality(body, true, ref correctionAttempted,
					ref correctionPoseAvailable, ref correctionPose, ref correctionNativeStatus))
					state.Decision.Recheck.Began = true;
				else
					state.Recheck = default;
				return false;
			}
			if (action != FusionRecheckAction.Judge)
				return false;

			float value = body.SignalFor(QualityDecision.Acquire);
			float bar = body.DecisionBand.ThresholdFor(QualityDecision.Acquire);
			bool clearsStart = body.DecisionBand.Clears(QualityDecision.Acquire, value);
			float gateBar = StartMarginGate.BarFor(TrackingOverrides.StartMarginGate) ?? FusionRecovery.RECHECK_GATE_BAR;
			StartMarginGateReading gate = StartMarginGate.Read(gateBar, body.RegistrationQuality);
			var reading = new FusionRecheckReading
			{
				Judged = true, Value = value, Bar = bar, ClearsStart = clearsStart, GateBar = gateBar,
				GateRead = gate.Read, GateValue = gate.Value, GatePassed = gate.Passed,
			};
			if (TryPrepareSeatSeed(body, out Vector3 seat, out Quaternion seatRotation, out _))
				FusionRecovery.Distance(nativePos, nativeRot, seat, seatRotation, out reading.Millimetres, out reading.Degrees);
			bool passes = FusionRecovery.Passes(clearsStart, gate.Passed);
			int required = FusionRecheckRunRows(body);
			if (required > 1)
			{
				// Judged like a start. The pose takes the lock back only after the rows a start needs in a row, each clearing
				// the start value and the margin gate. A row that does not clear starts a new cycle from the seat at once.
				bool complete = FusionRecovery.RunAdvance(ref state.Recheck.RunPasses, passes, required);
				reading.RunPasses = state.Recheck.RunPasses;
				reading.RunRequired = required;
				state.Decision.Recheck = reading;
				if (!complete)
				{
					if (!passes)
						RestartFusionRecheck(body, state, ref correctionAttempted, ref correctionPoseAvailable,
							ref correctionPose, ref correctionNativeStatus);
					return false;
				}
			}
			else
			{
				state.Decision.Recheck = reading;
				if (!passes)
					return false;
			}

			// The lock is taken back on the refined pose. It becomes the stored reference and the settled pose
			// at once, so the display stands on it from this row.
			if (!StoreAcceptedPose(state, nativePos, nativeRot, value, PoseProvenance.RecoveryHypothesis))
				return false;
			state.SeatSettledOnNextStore = true;
			SettleReference(state, nativePos, nativeRot, true);
			state.ReferenceStoredThisStep = true;
			state.HoldActive = false;
			state.ReleaseFrameCount = 0;
			state.ReentryFrameCount = 0;
			state.TakeBackHistory = 0;
			state.HoldSeedAttemptsThisHold = 0;
			state.Recheck = default;
			state.RecoveryFrameCount = 0;
			state.FusionState = BodyFusionState.Accepting;
			state.PublishRecoveryProvenanceOnce = true;
			state.Decision.ReEntered = true;
			state.Decision.Recheck.TookBack = true;
			return true;
		}

		/// <summary>
		/// Writes the seat to the tracker as one seed of the hold and counts it. Entry to a hold and every repeated seed
		/// of the same hold go through here.
		/// </summary>
		private void ApplyHoldSeedSchedule(TrackedBody body, BodyState state,
			ref bool correctionAttempted, ref bool correctionPoseAvailable,
			ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			++state.HoldSeedAttemptsThisHold;
			SeedFromSeat(body, ref correctionAttempted,
				ref correctionPoseAvailable, ref correctionPose,
				ref correctionNativeStatus);
		}

		/// <summary>Seeds the seat and records what the write did.</summary>
		private void SeedFromSeat(TrackedBody body, ref bool correctionAttempted, ref bool correctionPoseAvailable,
			ref FTTrackingPose correctionPose, ref int correctionNativeStatus)
		{
			if (!TrySeedTrackerFromSeat(body, out FTTrackingPose seedPose, out int nativeStatus))
			{
				return;
			}
			correctionAttempted = true;
			correctionPoseAvailable = true;
			correctionPose = seedPose;
			correctionNativeStatus = nativeStatus;
		}

		/// <summary>
		/// Hands the pose the body was placed at to the tracker, on the frame the
		/// lifecycle gives up its lock.
		/// </summary>
		/// <remarks>
		/// <para>The seat is handed over rather than the stored solve. The stored
		/// solve is written from frames the acceptance band admitted, and a lock
		/// that drifted was admitted on its way out, so at the moment a release
		/// fires the store is the pose that has just been judged wrong. The seat is
		/// also the only pose in the system that no bad frame can move.</para>
		/// <para>It goes out through the same single call the coast's return uses.
		/// A second way of putting a pose into the tracker would be a second thing
		/// to keep correct.</para>
		/// </remarks>
		private bool TrySeedTrackerFromSeat(TrackedBody body, out FTTrackingPose seedPose, out int nativeStatus)
		{
			seedPose = default;
			nativeStatus = StationaryCorrectionTelemetry.NativeStatusNotAttempted;
			if (ShippedDefaults.CorrectionAfterFirstFit && _bodyStates.TryGetValue(body, out BodyState seatState) && !seatState.HasLastGoodPose)
				return false;
			if (!TryPrepareSeatSeed(body, out Vector3 position, out Quaternion rotation, out string space))
				return false;
			return TrySeedTracker(body, position, rotation, space,
				out seedPose, out nativeStatus);
		}

		/// <summary>
		/// Resolves the seat exactly as the existing write path does, without
		/// making the native call. This lets the candidate count only writes that
		/// were actually ready to be made.
		/// </summary>
		private bool TryPrepareSeatSeed(TrackedBody body, out Vector3 position, out Quaternion rotation, out string spaceName)
		{
			position = default;
			rotation = default;
			spaceName = "World";
			if (!body.HasRetainedInitializationPose)
				return false;

			Matrix4x4 spaceFromBody = body.RetainedInitializationSpaceFromBody;
			position = spaceFromBody.GetPosition();
			rotation = spaceFromBody.rotation;
			if (!ConversionUtils.IsUsablePose(position, rotation))
			{
				body.RecordRejectedPose();
				return false;
			}
			return true;
		}

		/// <summary>
		/// Writes one pose to the tracker as the pose to solve from next, and
		/// updates no stored reference.
		/// </summary>
		private bool TrySeedTracker(TrackedBody body, Vector3 position,
			Quaternion rotation, string space, out FTTrackingPose seedPose,
			out int nativeStatus)
		{
			seedPose = default;
			nativeStatus = StationaryCorrectionTelemetry.NativeStatusNotAttempted;
			if (!ConversionUtils.IsUsablePose(position, rotation))
			{
				body.RecordRejectedPose();
				return false;
			}

			seedPose = ConversionUtils.GetConvertedPose(position, rotation);
			nativeStatus = WriteBodyPose(body, ref seedPose);
			NativeBodyWriteJournal.RecordPose(body.BodyId, "FT_SetBodyPose", space,
				position, rotation,
				FrameWriteGuard.RecordOutOfFrame(nativeStatus, "FT_SetBodyPose"),
				nativeStatus);
			return true;
		}

		/// <summary>
		/// Advances this body's display filter by one rendered frame and writes the
		/// result to the transform.
		/// </summary>
		/// <remarks>
		/// <para>Called from exactly one place, <see cref="OnBeforeRender"/>, so the
		/// filter advances once per rendered frame. Advancing it again whenever a
		/// tracking step landed in the same frame made the retained fraction
		/// <c>(1-alpha)^2</c> on some frames and <c>(1-alpha)</c> on others, which
		/// divided the effective time constant by the tracker's throughput and left
		/// <see cref="TrackedBody.SmoothTime"/> meaning something the frame rate
		/// decided.</para>
		/// <para>The filter runs on the stored pose, which is held in the world
		/// frame, and writes its result to the transform.</para>
		/// </remarks>
		private void AdvanceDisplayFilter(
			TrackedBody body,
			BodyState state,
			FusionFrameContext? frameContext)
		{
			if (!frameContext.HasValue || !frameContext.Value.IsValid)
				return;
			if (!state.HasLastGoodPose)
				return;

			// The target is the settled pose once there is one; until the first
			// store settles it, the stored pose itself.
			Vector3 targetPos = state.HasSettledPose ? state.SettledPosition
				: state.LastGoodPosition;
			Quaternion targetRot = state.HasSettledPose ? state.SettledRotation
				: state.LastGoodRotation;

			// The filter starts from where the body already stands, so the first
			// displayed pose after tracking starts eases out of the initialization
			// pose instead of jumping to the first solve.
			if (!state.HasDisplayPose)
			{
				Vector3 seedPos = body.transform.position;
				Quaternion seedRot = body.transform.rotation;
				if (!ConversionUtils.IsUsablePose(seedPos, seedRot))
				{
					body.RecordRejectedPose();
					return;
				}
				state.DisplayPosition = seedPos;
				state.DisplayRotation = seedRot;
				state.HasDisplayPose = true;
			}

			float alpha = ComputeLerpAlpha(body.SmoothTime);
			Vector3 filteredPos = Vector3.Lerp(state.DisplayPosition, targetPos, alpha);
			Quaternion filteredRot = Quaternion.Slerp(state.DisplayRotation, targetRot, alpha);

			// A filtered pose that was usable can still blend to one that is not. The
			// filter state is committed only after this check, so a refused frame
			// does not advance it.
			if (!ConversionUtils.IsUsablePose(filteredPos, filteredRot))
			{
				body.RecordRejectedPose();
				return;
			}

			state.DisplayPosition = filteredPos;
			state.DisplayRotation = filteredRot;
			++state.DisplayFilterAdvances;

			// The stored pose is always a solve; what varies is why it is on screen.
			// A coasting body is holding it because the body is out of frame and the
			// world frame is carrying it, which is a different statement from
			// holding it because this frame's solve was not accepted.
			PoseProvenance source;
			if (state.PublishRecoveryProvenanceOnce)
			{
				// One frame only. The window completed on the solve this pose came
				// from, and the next frame is an ordinary tracked one.
				state.PublishRecoveryProvenanceOnce = false;
				source = PoseProvenance.RecoveryHypothesis;
			}
			else if (state.CoastPhase == WorldFrameCoastPhase.Coasting)
				source = PoseProvenance.WorldCoast;
			else if (state.FusionState == BodyFusionState.Accepting && state.ReferenceStoredThisStep)
				source = PoseProvenance.Optimizer;
			else
				source = PoseProvenance.LastValid;
			body.SetWorldPose(filteredPos, filteredRot, Tag(source));
		}


		/// <summary>
		/// Stores an accepted pose as this body's last good pose. Returns false and
		/// stores nothing when the pose cannot be used, or when it did not come from
		/// the tracker's own solve.
		/// </summary>
		/// <remarks>
		/// Every caller today validates first. The check is repeated here because
		/// this store is the one place a bad value becomes durable: it survives
		/// every later frame, is displayed while holding, and is written back to the
		/// tracker.
		/// <para>
		/// The provenance argument is the enforcement of the rule that a coasted or
		/// predicted pose may never update a stored reference. It is a parameter
		/// rather than a comment so that a future caller has to name where its pose
		/// came from, and so that naming anything but the solve is refused here
		/// rather than trusted not to happen. Held poses re-enter the tracker only
		/// through the explicit gated write, which seeds the solve and stores
		/// nothing.
		/// </para>
		/// </remarks>
		internal static bool StoreAcceptedPose(BodyState state, Vector3 nativePos, Quaternion nativeRot, float instantQuality, PoseProvenance provenance)
		{
			// A recovered pose is the tracker's own solve too. What makes it worth a
			// name of its own is that the solve was started somewhere the tracker did
			// not choose, so the frame it is finally believed on is tagged with that.
			// The single caller tags it only on the frame a recovery's verification
			// window completes; every frame after it is an ordinary solve again.
			if (provenance != PoseProvenance.Optimizer &&
				provenance != PoseProvenance.RecoveryHypothesis)
			{
				Debug.LogError("[XRTracker] A pose that is not the tracker's own solve " +
					$"({provenance}) was offered as this body's last good pose; it was refused.");
				return false;
			}
			if (!ConversionUtils.IsUsablePose(nativePos, nativeRot))
				return false;

			state.LastGoodPosition = nativePos;
			state.LastGoodRotation = nativeRot;
			state.HasLastGoodPose = true;

			state.LastAcceptedInstantQuality = instantQuality;
			return true;
		}

		/// <summary>
		/// Moves the settled pose a fixed fraction of the way toward a solve that
		/// was just stored, or seats it on that solve.
		/// </summary>
		/// <remarks>
		/// Seated when there is no settled pose yet, while tracking is warming up,
		/// and on the first store after the body was held at its seat: in each
		/// case the previous settled pose says nothing about where the body is
		/// now. Every other store pulls by the settle fraction, which makes the
		/// settled pose an exponential average of the stored solves.
		/// </remarks>
		internal static void SettleReference(BodyState state, Vector3 storedPos,
			Quaternion storedRot, bool warmup) =>
			SettleReference(state, storedPos, storedRot, warmup, float.NaN);

		// The on-screen distance between two world poses of the body that is settling, seen from the scene camera. One delegate serves every step,
		// so a stored fit costs no allocation.
		private SettleDistance _settleDistance;
		private TrackedBody _settleBody;
		private bool _settleHasCamera;
		private Vector3 _settleCameraPosition;
		private Quaternion _settleCameraRotation;

		/// <summary>
		/// Binds the on-screen distance of the settling to the body and to the scene camera as it stands. The distance is not a number when there is no camera.
		/// </summary>
		private void BindSettleDistance(TrackedBody body)
		{
			_settleDistance ??= MeasureSettleDistance;
			_settleBody = body;
			XRTrackerManager manager = XRTrackerManager.Instance;
			Transform cam = manager != null ? manager.CameraTransform : null;
			_settleHasCamera = cam != null;
			if (_settleHasCamera)
			{
				_settleCameraPosition = cam.position;
				_settleCameraRotation = cam.rotation;
			}
		}

		private float MeasureSettleDistance(Vector3 firstPosition, Quaternion firstRotation, Vector3 secondPosition, Quaternion secondRotation) =>
			_settleHasCamera ? _settleBody.OnScreenPixelsBetween(firstPosition, firstRotation, secondPosition, secondRotation,
				_settleCameraPosition, _settleCameraRotation) : float.NaN;

		/// <summary>
		/// Whether the AR camera moves the picture of a body held fixed in the world too fast for the fit of the step to be stored. The first fit is never
		/// held back, because a body that holds no stored pose has nothing to keep still. Nor are the fits of the warmup, when the pose the body is held at is
		/// still settling toward the object and the fits that follow are what corrects it, and a motion that could not be measured holds nothing.
		/// </summary>
		internal static bool FastMotionWithholdsFit(float cameraMotionPixels, float limitPixels, bool hasStoredPose, bool warmup) =>
			hasStoredPose && !warmup && cameraMotionPixels > limitPixels;

		/// <summary>
		/// Moves the settled pose toward the stored fit of this step, or seats it on that fit.
		/// </summary>
		/// <param name="state">The state of the body.</param>
		/// <param name="storedPosition">World position of the fit stored on this step.</param>
		/// <param name="storedRotation">World rotation of the fit stored on this step.</param>
		/// <param name="seatNow">Whether the settled pose is seated on this fit whatever it was.</param>
		/// <param name="distance">The on-screen distance between two world poses, which is how far the fit stands from the settled pose.</param>
		/// <param name="movePixels">How far the fit stood from the settled pose on screen, or not a number when it was not measured.</param>
		internal static void SettleStoredFit(BodyState state, Vector3 storedPosition, Quaternion storedRotation, bool seatNow,
			SettleDistance distance, out float movePixels)
		{
			movePixels = state.HasSettledPose && distance != null
				? distance(state.SettledPosition, state.SettledRotation, storedPosition, storedRotation)
				: float.NaN;
			SettleReference(state, storedPosition, storedRotation, seatNow, movePixels);
		}

		/// <summary>
		/// The fraction of the way the settled pose moves toward a stored solve that stands <paramref name="movePixels"/>
		/// from it on screen. A difference within the solve's scatter is averaged at the settle fraction, a difference
		/// that shows the object moved is followed at once, and the pull grows in proportion between the two. A distance
		/// that could not be measured is averaged.
		/// </summary>
		internal static float SettlePull(float movePixels)
		{
			if (float.IsNaN(movePixels) || movePixels <= TrackerDefaults.STATIONARY_SETTLE_STILL_PIXELS)
				return TrackerDefaults.STATIONARY_SETTLE_PULL;
			float share = Mathf.InverseLerp(TrackerDefaults.STATIONARY_SETTLE_STILL_PIXELS,
				TrackerDefaults.STATIONARY_SETTLE_MOVE_PIXELS, movePixels);
			return Mathf.Lerp(TrackerDefaults.STATIONARY_SETTLE_PULL, 1f, share);
		}

		/// <summary>
		/// Moves the settled pose toward a stored solve by <see cref="SettlePull"/> of the on-screen distance between the
		/// two, or seats it on that solve.
		/// </summary>
		internal static void SettleReference(BodyState state, Vector3 storedPos,
			Quaternion storedRot, bool warmup, float movePixels)
		{
			if (!state.HasSettledPose || state.SeatSettledOnNextStore || warmup)
			{
				state.SettledPosition = storedPos;
				state.SettledRotation = storedRot;
				state.HasSettledPose = true;
				state.SeatSettledOnNextStore = false;
				state.DisplayCatchUpPending = true;
				return;
			}

			float pull = SettlePull(movePixels);
			state.SettledPosition = Vector3.Lerp(state.SettledPosition, storedPos, pull);
			state.SettledRotation = Quaternion.Slerp(state.SettledRotation, storedRot, pull);
		}

		/// <summary>
		/// Whether this frame is the one a recovery's verification window completes
		/// on.
		/// </summary>
		/// <remarks>
		/// It asks the same three questions the temporal machine's own recovery
		/// branch asks, one step ahead of it: the machine runs after this frame's
		/// pose has already been stored, so a caller that needs to know where the
		/// pose it is storing came from cannot wait for the answer. Reading the
		/// machine's memory rather than re-deriving it is what keeps the two from
		/// drifting apart.
		/// </remarks>
		private static bool RecoveryWindowCompletesThisFrame(TrackedBody body,
			BodyState state)
		{
			if (!HealthAuthority(body) ||
				state.TemporalTracking.State != TemporalTrackingState.Recovering ||
				!state.TemporalTracking.RecoveryConfirmed ||
				!state.PoseSettled)
			{
				return false;
			}
			TrackingHealthSnapshot health = body.TrackingHealth;
			TemporalTrackingPolicy policy = body.TrackingHealthSettings.Policy;
			return health.Available && health.Health >= policy.AcquireGood &&
				state.TemporalTracking.RecoveryGoodFrames + 1 >= policy.RecoveryFrames;
		}

#else
		private void ProcessStationary(TrackedBody body, BodyState state)
		{
			// AR Foundation not available — fall back to standard processing
			ProcessBodyNoAR(body, state);
		}
#endif

		/// <summary>
		/// Attempts to start tracking when the quality threshold clears.
		/// </summary>
		private void TryStartTracking(TrackedBody body, BodyState state)
		{
			if (!PrepareAutomaticStartAttempt(body, state))
				return;

			bool clearsEntry = body.StartClears;
			state.Decision.Acquire = LifecycleComparison.Taken(
				body.StartSignal,
				body.StartBar,
				QualityBand.SignalFor(QualityDecision.Acquire),
				clearsEntry);
			float? gateBar = state.Decision.Path == LifecyclePath.Standard
				? ShippedDefaults.StartMarginGateBar()
				: null;
			// A run-wide override wins over the body's own confirm score.
			float? finalRowBar = StartFinalRow.Resolve(TrackingOverrides.StartFinalRowBar ?? body.ConfirmBar,
				body.EnableEdgeTracking, SourceFor(body), state.Decision.Path);
			// The start search is read before the settled start. A refined pose past the drift bound of the settled start puts the body
			// back to refining from its guide, and the body then no longer counts as refined, so the search of this step would go
			// unread. The gate reads every step of a body it applies to, so that the searches in a row are counted from consecutive ones.
			bool searchAgrees = true;
			if (body.StartSearchApplies)
			{
				XRTrackerManager searchManager = XRTrackerManager.Instance;
				bool searchPoseIsWorld = IsARPoseFusionActive;
				Vector3 searchCameraPosition = Vector3.zero;
				Quaternion searchCameraRotation = Quaternion.identity;
				bool searchCameraKnown = searchManager != null &&
					TryGetStepCameraPose(searchManager, out searchCameraPosition, out searchCameraRotation);
				searchAgrees = body.NoteStartSearchStep(searchPoseIsWorld, searchCameraKnown, searchCameraPosition, searchCameraRotation);
			}
			// The settled start reads the refined pose of every step of a body it applies to, whether or not a start is due, so
			// that the rows in a row are counted from consecutive steps. A body it does not apply to reads nothing.
			bool settled = true;
			if (body.StartSettleApplies)
			{
				XRTrackerManager manager = XRTrackerManager.Instance;
				bool poseIsWorld = IsARPoseFusionActive;
				Vector3 cameraPosition = Vector3.zero;
				Quaternion cameraRotation = Quaternion.identity;
				bool cameraKnown = manager != null && TryGetStepCameraPose(manager, out cameraPosition, out cameraRotation);
				settled = body.NoteSettleStep(poseIsWorld, cameraKnown, cameraPosition, cameraRotation);
			}
			bool started = DecideStart(state, clearsEntry, gateBar, body.EnableEdgeTracking,
				body.RegistrationQuality, out StartMarginGateReading gate,
				finalRowBar, body.SignalFor(QualityDecision.Acquire), out StartFinalRowReading finalRow);
			state.Decision.StartGate = gate;
			state.Decision.FinalRow = finalRow;
			state.Decision.GoodFrameCount = state.GoodFrameCount;
			state.Decision.BadFrameCount = state.BadFrameCount;
			// A start that the settle check or the search withholds leaves the count of good rows where the check left it, as the
			// margin gate does, so the next row that clears the bars and the checks starts the body.
			if (started && settled && searchAgrees)
			{
				state.Decision.StartFired = true;
				StartTracking(body, state);
			}
		}

		/// <summary>
		/// The camera the step saw from, in the frame the tracker's pose is read into the world with. Under pose fusion that
		/// is the camera pose the step was observed with, and otherwise it is the scene camera.
		/// </summary>
		private bool TryGetStepCameraPose(XRTrackerManager manager, out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (IsARPoseFusionActive)
			{
				Pose? stepCamera = CompletedStepCameraPose();
				if (!stepCamera.HasValue)
					return false;
				position = stepCamera.Value.position;
				rotation = stepCamera.Value.rotation;
				return true;
			}
			Transform camera = manager.CameraTransform;
			if (camera == null)
				return false;
			position = camera.position;
			rotation = camera.rotation;
			return true;
		}

		/// <summary>
		/// Counts one row toward a start and decides whether the body starts on it. Coverage decides, through
		/// <paramref name="clearsEntry"/> and the count of good rows. With a gate bar, a start that coverage would take is
		/// also withheld unless the registration margin with the sub-pixel median term reads at or above the bar on this
		/// row, and a row it cannot be read on withholds it. A withheld start leaves the count exactly where the check
		/// left it, so the next row that clears both starts the body and a row that fails coverage resets it as always.
		/// </summary>
		/// <param name="gateBar">The bar, or null for coverage alone.</param>
		/// <param name="gateApplies">False for a body that does not track by the edge method and so has no such evidence.</param>
		internal static bool DecideStart(BodyState state, bool clearsEntry, float? gateBar, bool gateApplies,
			in RegistrationQualitySnapshot evidence, out StartMarginGateReading gate) =>
			DecideStart(state, clearsEntry, gateBar, gateApplies, evidence, out gate,
				null, 0f, out _);

		/// <summary>
		/// The start decision with the final-row clause as a third condition. With a final-row bar, a start that coverage
		/// would take is also withheld unless <paramref name="finalRowValue"/>, the scaled coverage score the start bar
		/// compares on this row, reads at or above the bar. The two second conditions are read independently, so each
		/// reports its own verdict on the row the start would fire, and the body starts only when both pass. A withheld
		/// start leaves the count exactly where the check left it, as the margin gate does.
		/// </summary>
		/// <param name="finalRowBar">The bar of the clause, or null for no clause.</param>
		/// <param name="finalRowValue">The scaled coverage score of this row, read for the clause and for nothing else.</param>
		internal static bool DecideStart(BodyState state, bool clearsEntry, float? gateBar, bool gateApplies,
			in RegistrationQualitySnapshot evidence, out StartMarginGateReading gate,
			float? finalRowBar, float finalRowValue, out StartFinalRowReading finalRow)
		{
			bool started = CheckFrameThreshold(
				state, clearsEntry, TrackerDefaults.FRAMES_TO_START, trackGoodFrames: true);
			gate = default;
			finalRow = default;
			bool withheld = false;
			if (gateBar.HasValue && gateApplies)
			{
				gate = StartMarginGate.Read(gateBar.Value, evidence);
				if (started && !gate.Passed)
				{
					gate.Withheld = true;
					withheld = true;
				}
			}
			if (finalRowBar.HasValue)
			{
				finalRow = StartFinalRow.Read(finalRowBar.Value, finalRowValue);
				if (started && !finalRow.Passed)
				{
					finalRow.Withheld = true;
					withheld = true;
				}
			}
			return started && !withheld;
		}

		internal static bool PrepareAutomaticStartAttempt(TrackedBody body, BodyState state)
		{
			if (body != null && body.AutomaticStartEnabled)
				return true;

			// A disabled interval is not evidence toward a later start. Re-enabling
			// therefore begins a fresh consecutive-frame window rather than consuming
			// frames observed while publication was deliberately suppressed.
			if (state != null)
				state.GoodFrameCount = 0;
			return false;
		}

		#endregion

		#region Rigid Child Processing

		/// <summary>
		/// Assembly mode: a child does not affect its parent's pose until the part is confirmed present. It applies to
		/// every child in assembly mode, a rigid one and one with free motion alike, because both join the parent's solve.
		/// </summary>
		internal static void ApplyAssemblyGate(TrackedBody body)
		{
			if (body == null || !body.HasParent || !body.AssemblyMode)
				return;

			// The application says when the part is mounted, or the part is confirmed on its own score. The score of a part that waits for the
			// application is still read for it and is never compared with anything here. The one quality reading outside the band is a child's
			// own confirm threshold, configured per body, against the averaged scalar. It is not a band decision and does not take a band
			// threshold, which is why it names its own here rather than going through the table.
			body.SetContributesToOptimization(PartJoinsSolve(body.AttachManually, body.IsAttached,
				body.DecisionAverageQuality, body.AssemblyQualityToConfirm));
		}

		/// <summary>The longest step a presence hold takes in one piece, so a stalled step cannot fill a hold on its own.</summary>
		private const float MAX_PRESENCE_STEP_SECONDS = 0.25f;

		/// <summary>The duration of the completed tracking step, read from the step's own clock when it has one.</summary>
		private float CompletedStepSeconds()
		{
			float seconds = _completedFrameInstrumentationContext.HasValue
				? _completedFrameInstrumentationContext.Value.TrackingStepDeltaSeconds
				: 0f;
			if (!(seconds > 0f))
				seconds = Time.unscaledDeltaTime;
			return Mathf.Min(seconds, MAX_PRESENCE_STEP_SECONDS);
		}

		/// <summary>
		/// The camera's world pose for the completed tracking step. Under pose fusion the step carries the camera pose it was
		/// observed with, and the scene camera need not follow it. Otherwise the scene camera is where the step saw from.
		/// </summary>
		private Pose? CompletedStepCameraPose()
		{
			if (_completedFrameInstrumentationContext.HasValue && _completedFrameInstrumentationContext.Value.IsValid)
			{
				Matrix4x4 worldFromCamera = _completedFrameInstrumentationContext.Value.WorldFromCameraUnity;
				return new Pose(worldFromCamera.GetColumn(3), worldFromCamera.rotation);
			}
			var manager = XRTrackerManager.Instance;
			Transform camera = manager != null ? manager.CameraTransform : null;
			return camera != null ? new Pose(camera.position, camera.rotation) : (Pose?)null;
		}

		private void ProcessChildBody(TrackedBody body, BodyState state)
		{
			UpdateTrackingHealth(body, state);

			// Update tracking state from the parent
			bool parentTracking = body.ParentBody != null && body.ParentBody.IsTracking;
			if (body.IsTracking != parentTracking)
			{
				body.SetTrackingState(parentTracking);
			}

			ApplyAssemblyGate(body);
			DrawChildOnParent(body);
			body.ApplySettling();
			body.UpdatePresence(CompletedStepSeconds(), CompletedStepCameraPose());

			// A child is rigidly attached to its parent, so its pose is exactly as accepted,
			// or as held, as the parent's. Without this a held child would report an accepted
			// pose, which is the very distinction the confidence state exists to make.
			if (body.ParentBody != null &&
				_bodyStates.TryGetValue(body.ParentBody, out BodyState parentState))
			{
				state.FusionState = parentState.FusionState;

				// The same argument applies to the three-state machine: a rigidly
				// attached child's pose is exactly as vouched for as its parent's, so
				// it takes the parent's answer rather than running the machine on
				// quality scalars that describe the parent's evidence anyway.
				TrackingConfidenceTransition mirrored = TrackingConfidenceMachine.Mirror(
					ref state.Confidence, parentState.Confidence.State,
					parentState.ConfidenceReason);
				state.ConfidenceReason = mirrored.Reason;
				body.SetConfidence(mirrored);

				TemporalTrackingSnapshot parentTemporal =
					body.ParentBody.TemporalTrackingStatus;
				body.SetTemporalTrackingStatus(TemporalTrackingMachine.Mirror(
					ref state.TemporalTracking, parentTemporal,
					body.ParentBody.BodyId, _completedTemporalFrameIndex,
					_completedTemporalStepSerial));
			}
			else
			{
				UpdateTemporalTrackingStatus(body, state);
			}

			FireEventsIfChanged(body, state);
		}

		#endregion

		#region State Control Methods

		private void StartTracking(TrackedBody body, BodyState state)
		{
			body.SetTrackingState(true);
			FTBridge.FT_StartTrackingBody(body.BodyId);
			NativeBodyWriteJournal.RecordCall(body.BodyId, "FT_StartTrackingBody", true);
			ResetCounters(state);
			state.FramesSinceTrackingStarted = 0;
			state.SoftAcceptRows = 0;
		}

		/// <summary>
		/// The reset a lost body goes through when its lifecycle stops it. It stops the tracking and starts the
		/// lifecycle over where the body is, and it neither returns the body to its placed pose nor forgets what it
		/// learned, so a recovery after it can go on. The pose goes back only when the application resets the body.
		/// </summary>
		internal void ResetTracking(TrackedBody body, BodyState state)
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
		/// <summary>
		/// The provenance to record for a pose, or None when recording is off.
		/// </summary>
		/// <remarks>
		/// Every tagging site goes through here, so the setting is read in one
		/// place and no site can be left tagging while the setting says otherwise.
		/// </remarks>
		private PoseProvenance Tag(PoseProvenance provenance)
		{
			return EnablePoseProvenance ? provenance : PoseProvenance.None;
		}

		/// <summary>
		/// Carries every registered body to the new world frame after the AR session moved its world between two steps. The tracker's own pose of a
		/// tracking body, the state of the lifecycle and the body itself all go through the map from the old frame to the new one, so that the pose of
		/// each body relative to the camera is the same on both sides of the jump. It is called after the camera pose of the new step reached the
		/// tracker and before the step runs, so the step solves from the carried pose and not from a pose behind the camera.
		/// </summary>
		/// <param name="carry">The map from the old frame to the new one.</param>
		/// <returns>Whether at least one body was carried.</returns>
		internal bool CarryBodiesToNewWorld(Pose carry)
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !ShippedDefaults.ArWorldCarry(manager.FollowArWorldJumps))
				return false;
			bool carried = false;
			foreach (TrackedBody body in manager.TrackedBodies)
			{
				if (body == null || !body.IsRegistered)
					continue;
				// A body that is not tracking has its seat written to the tracker before every step, and the seat is carried below.
				if (body.IsTracking)
					CarryTrackerPose(body, carry);
				if (_bodyStates.TryGetValue(body, out BodyState state))
					state.CarryWorld(carry);
				body.CarryWorld(carry);
				carried = true;
			}
			return carried;
		}

		/// <summary>
		/// Writes the pose the tracker holds for a tracking body to the tracker again, carried to the new frame. A write that does not land leaves the
		/// pose in the old frame, and the prediction gate puts the tracker back at the held pose, which is carried, when its next solve stands away from it.
		/// </summary>
		private static void CarryTrackerPose(TrackedBody body, Pose carry)
		{
			if (!FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose pose) ||
				!pose.TryGetTransformation(out Vector3 position, out Quaternion rotation) ||
				!ConversionUtils.IsUsablePose(position, rotation))
				return;
			Vector3 carriedPosition = ArWorldJump.Carry(carry, position);
			Quaternion carriedRotation = ArWorldJump.Carry(carry, rotation);
			FTTrackingPose carried = ConversionUtils.GetConvertedPose(carriedPosition, carriedRotation);
			int status = FTBridge.FT_SetBodyPose(body.BodyId, ref carried);
			NativeBodyWriteJournal.RecordPose(body.BodyId, "FT_SetBodyPose", "World", carriedPosition, carriedRotation,
				FrameWriteGuard.Record(status, "FT_SetBodyPose"), status);
		}

		private void ApplyNativePose(TrackedBody body, BodyState state)
		{
			// This path writes the solved pose on the frame it is produced and never
			// holds an earlier one, so what it displays is always the solve itself.
			// The one frame after a saved-view seat's window has passed names where
			// that solve was started from, and every frame after is an ordinary one.
			if (!GetNativeWorldPose(body, out Vector3 worldPos, out Quaternion worldRot))
				return;
			PoseProvenance source = PoseProvenance.Optimizer;
			if (state.PublishRecoveryProvenanceOnce && body.IsTracking)
			{
				state.PublishRecoveryProvenanceOnce = false;
				source = PoseProvenance.RecoveryHypothesis;
			}
			body.SetWorldPose(worldPos, worldRot, Tag(source));
		}

		/// <summary>
		/// Whether this step leaves the body drawn at its seat instead of at the pose the tracker refined from it.
		/// </summary>
		/// <remarks>
		/// Only when the tooling asks for it, and only for a step that began and ended without the body
		/// tracking. The step that accepts the start and the step that ends tracking still draw the tracker's
		/// pose, so a start is taken from the refined pose and a loss is drawn where the tracker reset it.
		/// </remarks>
		internal static bool HoldsStartPose(bool hold, bool wasTracking, bool isTracking) =>
			hold && !wasTracking && !isTracking;

		/// <summary>
		/// Get native tracker pose in world space.
		/// When AR pose fusion is active, the native tracker outputs world-space poses directly.
		/// Otherwise, converts from camera-relative to world.
		/// </summary>
		private bool GetNativeWorldPose(TrackedBody body, out Vector3 worldPos, out Quaternion worldRot)
		{
			return GetNativeWorldPose(body, true, out worldPos, out worldRot);
		}

		/// <summary>
		/// The same read without recording a refusal, for a caller that is
		/// measuring the pose rather than deciding on it.
		/// </summary>
		private bool TryGetNativeWorldPose(TrackedBody body, out Vector3 worldPos,
			out Quaternion worldRot)
		{
			return GetNativeWorldPose(body, false, out worldPos, out worldRot);
		}

		#region Recovery Driver Surface

		/// <summary>A phase a component has declared for a body it is recovering.</summary>
		private readonly struct DeclaredRecoveryPhase
		{
			public DeclaredRecoveryPhase(TemporalRecoveryPhase phase, ulong token)
			{
				Phase = phase;
				Token = token;
			}

			public TemporalRecoveryPhase Phase { get; }
			public ulong Token { get; }
		}

		private readonly Dictionary<TrackedBody, DeclaredRecoveryPhase>
			_declaredRecoveryPhases = new Dictionary<TrackedBody, DeclaredRecoveryPhase>();

		private static ulong _driverRecoveryToken;

		/// <summary>
		/// A token no other recovery on this body has used, so the lifecycle reads
		/// one adoption as one and not as a continuation of the last.
		/// </summary>
		internal static ulong NextDriverRecoveryToken() => ++_driverRecoveryToken;

		/// <summary>
		/// States what a driver is doing with a body whose pose it seeded directly.
		/// </summary>
		/// <remarks>
		/// It reaches the temporal state machine and nothing else: no pose is
		/// written, no threshold moves, and the window the declaration opens is the
		/// same window a cold start runs.
		/// </remarks>
		internal void DeclareRecoveryPhase(TrackedBody body,
			TemporalRecoveryPhase phase, ulong token)
		{
			if (body == null)
				return;
			_declaredRecoveryPhases[body] = new DeclaredRecoveryPhase(phase, token);
		}

		/// <summary>Withdraws a declaration, leaving the surface's own phase to speak.</summary>
		internal void ClearRecoveryPhase(TrackedBody body)
		{
			if (body != null)
				_declaredRecoveryPhases.Remove(body);
		}

		/// <summary>
		/// The pose this body's last accepted solve left, in the tracker's own
		/// reference space, which is the world frame.
		/// </summary>
		internal bool TryGetStoredTrackerPose(TrackedBody body, out Vector3 position,
			out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (body == null || !_bodyStates.TryGetValue(body, out BodyState state))
				return false;
			if (!state.HasLastGoodPose)
				return false;
			position = state.LastGoodPosition;
			rotation = state.LastGoodRotation;
			return ConversionUtils.IsUsablePose(position, rotation);
		}

		/// <summary>
		/// The pose the tracker is solving from right now, in the same space.
		/// </summary>
		internal bool TryGetLiveTrackerPose(TrackedBody body, out Vector3 position,
			out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			return body != null &&
				TryGetNativeWorldPose(body, out position, out rotation);
		}

		/// <summary>
		/// Hands the stored pose back to the solve through the explicit gated write.
		/// </summary>
		/// <remarks>
		/// The same call the coast's return already makes, at a moment the driver
		/// chooses rather than the visibility predicate. It seeds the solve and
		/// updates no stored reference; a pose handed over this way can only become
		/// the stored one by being solved from and accepted like any other.
		/// </remarks>
		internal bool TrySeedTrackerFromStoredPose(TrackedBody body, out int nativeStatus)
		{
			nativeStatus = StationaryCorrectionTelemetry.NativeStatusNotAttempted;
			if (!TryGetStoredTrackerPose(body, out Vector3 position,
				out Quaternion rotation))
			{
				return false;
			}
			FTTrackingPose seed = ConversionUtils.GetConvertedPose(position, rotation);
			nativeStatus = WriteBodyPose(body, ref seed);
			FrameWriteGuard.RecordOutOfFrame(nativeStatus, "FT_SetBodyPose");
			return true;
		}

		#endregion

		#region Children

		/// <summary>
		/// Whether a body takes the stationary path under pose fusion. A child never does, because it follows its parent, and a pose that the
		/// stationary path holds, settles or writes back into the tracker for a child would be a pose of its own that its parent does not move.
		/// </summary>
		internal static bool RunsStationaryFusion(bool isStationary, bool hasParent, bool poseFusionActive) =>
			isStationary && !hasParent && poseFusionActive;

		/// <summary>
		/// Whether a body runs the lifecycle of a body of its own, which is the start, the stop, the re-entry and the recovery. Only a body
		/// without a parent does. A child is a part of its parent, and the tracker refines it inside the solve of its parent only, so a start
		/// of its own would mean nothing to the tracker and would only set a flag that the parent does not share.
		/// </summary>
		internal static bool RunsOwnLifecycle(bool hasParent) => !hasParent;

		/// <summary>
		/// Whether a part joins the solve of its parent. A part that waits for the application joins when the application has attached it,
		/// and any other part joins once its averaged score has reached its confirm score. Until then the part is only scored, at the pose its
		/// parent gives it, and it never pulls the parent.
		/// </summary>
		internal static bool PartJoinsSolve(bool attachManually, bool attached, float averagedScore, float confirmScore) =>
			attachManually ? attached : averagedScore >= confirmScore;

		/// <summary>Whether a child is drawn from the pose the tracker solved for it, which it is when it has free directions and its parent is tracking.</summary>
		internal static bool DrawsFromTracker(bool hasFreeDirections, bool parentTracking) => hasFreeDirections && parentTracking;

		/// <summary>
		/// Draws a child with free directions at the pose its parent is drawn at, turned and moved by the pose the tracker solved for it
		/// relative to the parent. The parent is not drawn at its solve when it is held or settled, so drawing the child at its own solve would
		/// let it turn and move in every direction against a parent that it may only slide along. A child that is not tracked, or has no free
		/// direction, is not drawn here, and it stays where its parent carries it, which is its last pose relative to the parent.
		/// </summary>
		private void DrawChildOnParent(TrackedBody body)
		{
			if (!DrawsFromTracker(body.TrackedMotion != TrackedMotion.None, body.IsTracking) ||
				!_childRelativePoses.TryGetValue(body, out Pose relative))
			{
				return;
			}
			Transform parentTransform = body.ParentBody.transform;
			Pose drawn = ComposePose(new Pose(parentTransform.position, parentTransform.rotation), relative);
			body.SetWorldPose(drawn.position, drawn.rotation, Tag(PoseProvenance.Optimizer));
		}

		/// <summary>The pose of a child relative to its parent, in the parent's frame.</summary>
		internal static Pose RelativePose(Pose parent, Pose child)
		{
			Quaternion inverse = Quaternion.Inverse(parent.rotation);
			return new Pose(inverse * (child.position - parent.position), inverse * child.rotation);
		}

		/// <summary>The pose of a child that stands at <paramref name="relative"/> to a parent that stands at <paramref name="parent"/>.</summary>
		internal static Pose ComposePose(Pose parent, Pose relative) =>
			new Pose(parent.position + parent.rotation * relative.position, parent.rotation * relative.rotation);

		/// <summary>
		/// Reads, for every child with free motion, its pose relative to its parent as the tracker solved the two together. The tracker moves a
		/// child only through the directions that are free, so this pose changes only along them. It is read before the frame is processed,
		/// because processing can write the parent's pose back into the tracker, and the child's pose is not moved with it until the next step.
		/// </summary>
		private void CaptureChildRelativePoses(IReadOnlyList<TrackedBody> trackedBodies)
		{
			_childRelativePoses.Clear();
			foreach (TrackedBody body in trackedBodies)
			{
				if (body == null || !body.IsRegistered || !body.HasParent || !body.IsActiveMode || !body.ParentBody.IsRegistered)
					continue;
				if (TryGetNativeWorldPose(body.ParentBody, out Vector3 parentPosition, out Quaternion parentRotation) &&
					TryGetNativeWorldPose(body, out Vector3 childPosition, out Quaternion childRotation))
				{
					_childRelativePoses[body] = RelativePose(new Pose(parentPosition, parentRotation), new Pose(childPosition, childRotation));
				}
			}
		}

		/// <summary>
		/// Writes a body's pose into the tracker and, under pose fusion, writes the poses of its children with it, each at the pose it held
		/// relative to the body. The tracker moves the poses of the children only in a step, so a child that was not written with its parent
		/// would stand at the pose it had relative to the pose the parent was moved away from, and the next step would score it there.
		/// </summary>
		private int WriteBodyPose(TrackedBody body, ref FTTrackingPose pose)
		{
			List<(TrackedBody Child, Pose Relative)> children = null;
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager != null && IsARPoseFusionActive &&
				TryGetNativeWorldPose(body, out Vector3 parentPosition, out Quaternion parentRotation))
			{
				foreach (TrackedBody other in manager.TrackedBodies)
				{
					if (other == null || other == body || other.ParentBody != body || !other.IsRegistered ||
						!TryGetNativeWorldPose(other, out Vector3 childPosition, out Quaternion childRotation))
					{
						continue;
					}
					children ??= new List<(TrackedBody, Pose)>();
					children.Add((other, RelativePose(new Pose(parentPosition, parentRotation), new Pose(childPosition, childRotation))));
				}
			}

			int status = FTBridge.FT_SetBodyPose(body.BodyId, ref pose);
			if (children == null || status != FTErrorCode.OK ||
				!TrackerPoseToWorld(body, pose, false, out Vector3 writtenPosition, out Quaternion writtenRotation))
			{
				return status;
			}
			foreach ((TrackedBody child, Pose relative) in children)
			{
				Pose seat = ComposePose(new Pose(writtenPosition, writtenRotation), relative);
				if (!ConversionUtils.IsUsablePose(seat.position, seat.rotation))
					continue;
				FTTrackingPose childPose = ConversionUtils.GetConvertedPose(seat.position, seat.rotation);
				FrameWriteGuard.RecordOutOfFrame(FTBridge.FT_SetBodyPose(child.BodyId, ref childPose), "FT_SetBodyPose");
			}
			return status;
		}

		#endregion

		private bool GetNativeWorldPose(TrackedBody body, bool recordRejection,
			out Vector3 worldPos, out Quaternion worldRot)
		{
			if (FTBridge.FT_GetBodyPose(body.BodyId, out FTTrackingPose pose))
				return TrackerPoseToWorld(body, pose, recordRejection, out worldPos, out worldRot);
			worldPos = Vector3.zero;
			worldRot = Quaternion.identity;
			return false;
		}

		/// <summary>
		/// Rewrites a pose in the tracker's own space into a world pose, exactly as
		/// the tracker's own solve is read.
		/// </summary>
		private bool TrackerPoseToWorld(TrackedBody body, in FTTrackingPose pose,
			bool recordRejection, out Vector3 worldPos, out Quaternion worldRot)
		{
			{
				if (!pose.TryGetTransformation(out Vector3 pos, out Quaternion rot))
				{
					if (recordRejection)
						body.RecordRejectedPose();
					worldPos = Vector3.zero;
					worldRot = Quaternion.identity;
					return false;
				}

				if (IsARPoseFusionActive)
				{
#if HAS_AR_FOUNDATION
					var manager = XRTrackerManager.Instance;
					if (manager == null ||
						!manager.TryGetCompletedFusionFrameContext(out FusionFrameContext context) ||
						!context.IsValid)
					{
						worldPos = Vector3.zero;
						worldRot = Quaternion.identity;
						return false;
					}
					// The tracker solves in the world frame.
					worldPos = pos;
					worldRot = rot;
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

				// The camera-relative branch above composes through the camera transform,
				// so a usable native pose does not guarantee a usable world pose. Checked
				// after every branch rather than inside one of them.
				if (!ConversionUtils.IsUsablePose(worldPos, worldRot))
				{
					body.RecordRejectedPose();
					worldPos = Vector3.zero;
					worldRot = Quaternion.identity;
					return false;
				}
				return true;
			}
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
		/// <remarks>
		/// <c>WasTracking</c> is the edge <see cref="FireEventsIfChanged"/> triggers
		/// on. A reset stops tracking and fires the transition itself, then drops
		/// this state object from the manager; leaving the flag set makes the next
		/// comparison on the orphaned object fire the public event a second time.
		/// </remarks>
		internal static void ResetCounters(BodyState state)
		{
			state.GoodFrameCount = 0;
			state.BadFrameCount = 0;
			state.HoldActive = false;
			state.ReleaseFrameCount = 0;
			state.ReentryFrameCount = 0;
			state.TakeBackHistory = 0;
			state.HoldSeedAttemptsThisHold = 0;
			state.Recheck = default;
			state.HasLastGoodPose = false;
			state.SolveWindow.Clear();
			state.FirstAcceptRowsWaited = 0;
			state.FirstAcceptBarRows = 0;
			state.InViewDip = false;
			state.ViewDipHealthyRows = 0;
			state.ViewHealthyStreak = 0;
			state.HasSettledPose = false;
			state.SeatSettledOnNextStore = false;
			state.DisplayCatchUpPending = false;
			state.TimeSinceLastCorrection = 0;
			state.LastAcceptedInstantQuality = 0;
			state.FusionState = BodyFusionState.NotTracking;
			state.WasTracking = false;
			TrackingConfidenceMachine.Clear(ref state.Confidence);
			state.ConfidenceReason = TrackingConfidenceReason.NotStarted;
#if HAS_AR_FOUNDATION
			state.HasDisplayPose = false;
			state.CoastPhase = WorldFrameCoastPhase.Inactive;
			state.CoastReentrySeeds = 0;
			state.CoastSeedWriteFailures = 0;
			state.Visibility = default;
#endif
		}

		private void FireEventsIfChanged(TrackedBody body, BodyState state)
		{
			bool isTracking = body.IsTracking;
			if (ConsumeTrackingEdge(state, isTracking))
				body.FireTrackingEvent(isTracking);
		}

		/// <summary>
		/// Whether the tracking flag crossed the edge this state last recorded, and
		/// re-arms the state for the next crossing.
		/// </summary>
		/// <remarks>
		/// The one place the public tracking event's firing count is decided. A
		/// reset clears the recorded edge (see <see cref="ResetCounters"/>) because
		/// the reset fires the transition itself, before this state object is
		/// dropped from the manager.
		/// </remarks>
		internal static bool ConsumeTrackingEdge(BodyState state, bool isTracking)
		{
			if (state.WasTracking == isTracking)
				return false;

			state.WasTracking = isTracking;
			return true;
		}

		#endregion

		#region Public API

		/// <summary>
		/// Check if a specific body is currently in AR pose fusion mode.
		/// </summary>
		internal bool IsBodyInARMode(TrackedBody body)
		{
			return RunsStationaryFusion(body.IsStationary, body.HasParent, IsARPoseFusionActive) && _bodyStates.ContainsKey(body);
		}

		/// <summary>
		/// Returns the correction decision for this body and the most recently
		/// completed lifecycle token. The map is cleared before each completed frame,
		/// so false means explicitly unavailable for that token rather than stale data.
		/// </summary>
		internal bool TryGetLastStationaryCorrectionTelemetry(
			TrackedBody body, out StationaryCorrectionTelemetry telemetry)
		{
			telemetry = default;
			return body != null && _lastCorrectionTelemetry.TryGetValue(body, out telemetry);
		}

		/// <summary>
		/// Resets every body the way <see cref="ResetBody"/> resets one.
		/// </summary>
		/// <param name="forgetLearned">
		/// False keeps what each body learned while it tracked. True forgets it, as in <see cref="ResetBody"/>.
		/// </param>
		public void ResetAll(bool forgetLearned)
		{
			foreach (TrackedBody body in XRTrackerManager.Instance.TrackedBodies)
				ResetBody(body, forgetLearned);
		}

		/// <summary>
		/// Resets every body and keeps what each learned, as <see cref="ResetAll(bool)"/> does with <c>false</c>. This overload
		/// takes no parameter, so a <see cref="UnityEngine.Events.UnityEvent"/> wired to it in the inspector binds.
		/// </summary>
		public void ResetAll() => ResetAll(false);

		/// <summary>
		/// Returns a body to the pose it was placed at, stops its tracking and starts its lifecycle over. A body that
		/// has no placed pose kept only stops tracking and starts its lifecycle over, and it stays where it is.
		/// Nothing happens to a body that is not registered.
		/// </summary>
		/// <param name="body">The body to reset.</param>
		/// <param name="forgetLearned">
		/// False keeps what the body learned while it tracked, which is the saved-view bank, the poses it was
		/// accepted at and what its views normally achieve. True forgets all three, so the body starts again with
		/// nothing but the pose it was placed at.
		/// </param>
		public void ResetBody(TrackedBody body, bool forgetLearned)
		{
			if (body == null || !body.IsRegistered)
				return;
			XRTrackerManager.WaitForTrackingStepToReturn();
			if (forgetLearned)
				ForgetLearnedData(body);
			body.ReturnToGuide();
			ResetBodyInternal(body);
		}

		/// <summary>
		/// Resets a body and keeps what it learned, as <see cref="ResetBody(TrackedBody, bool)"/> does with <c>false</c>. This
		/// overload takes one parameter, so a <see cref="UnityEngine.Events.UnityEvent"/> wired to it in the inspector binds.
		/// </summary>
		public void ResetBody(TrackedBody body) => ResetBody(body, false);

		/// <summary>
		/// Forgets what a body learned while it tracked. The bank is emptied through the driver's reset to empty,
		/// which switches it off so the plugin drops every view and reconfigures it empty on the next step.
		/// </summary>
		internal void ForgetLearnedData(TrackedBody body)
		{
			if (body == null)
				return;
			if (_savedViewDriver != null)
			{
				SavedViewRecoveryState driverState =
					_bodyStates.TryGetValue(body, out BodyState state) && state.SavedView != null
						? state.SavedView
						: new SavedViewRecoveryState();
				(_, float rotationDeg, float translationM) = SavedViewBankFor(_savedViewRecoveryCapacity);
				_savedViewDriver.EmptyBank(driverState, body.BodyId, rotationDeg, translationM,
					TrackerDefaults.RECOVERY_SEED_ADMISSION_MAX_PROBE_RESIDUAL, AdmissionCoverageFor(body));
			}
			_seenPoses.Remove(body);
			_viewExpectations.Remove(body);
		}

		/// <summary>The driver the manager steps, which a caller inside the assembly can replace with one over another port.</summary>
		internal SavedViewRecoveryDriver RecoveryDriver
		{
			get => _savedViewDriver;
			set => _savedViewDriver = value;
		}

		/// <summary>The poses each body was accepted at.</summary>
		internal Dictionary<TrackedBody, SeenPoseCandidates> SeenPoseStore => _seenPoses;

		/// <summary>What each body's views normally achieve.</summary>
		internal Dictionary<TrackedBody, ViewExpectationStore> ViewExpectationStores => _viewExpectations;

		private void ResetBodyInternal(TrackedBody body)
		{
			if (body == null || !body.IsRegistered)
				return;
			body.ClearSeatDisplayHold();

			bool wasTracking = body.IsTracking;

			// Stop tracking if the body runs a lifecycle of its own. A child is tracked while its parent is, and its parent's reset stops it.
			if (RunsOwnLifecycle(body.HasParent) && body.IsActiveMode && wasTracking)
				body.SetTrackingState(false);

			body.ResetQualityMetrics();

			if (!_bodyStates.TryGetValue(body, out BodyState temporalState))
				temporalState = new BodyState();
			body.SetTemporalTrackingStatus(TemporalTrackingMachine.Reset(
				ref temporalState.TemporalTracking, _completedTemporalFrameIndex,
				_completedTemporalStepSerial));

			// Fire event if was tracking
			if (wasTracking && RunsOwnLifecycle(body.HasParent))
				body.FireTrackingEvent(false);

			// Clear manager state for this body.
			_bodyStates.Remove(body);
			_lastCorrectionTelemetry.Remove(body);

			// Reset this specific body in native tracker (also stops tracking)
			if (!body.HasParent)
			{
				NativeBodyWriteJournal.RecordCall(body.BodyId, "FT_ResetBody",
					FTBridge.FT_ResetBody(body.BodyId) == FTErrorCode.OK);
			}
			// A reset returns the body to judging its seat as placed.
			body.ForgetRefinementBeforeAcceptance();
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
		/// Updates the derived global summary of the tracked bodies. Native status is
		/// refreshed only at the completed-step boundary, so this method does not read it.
		/// </summary>
		public void UpdateAllBodies()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.IsInitialized)
				return;
			UpdateGlobalPeak(manager.TrackedBodies);
		}

		internal bool TryRefreshCompletedBodyStatuses(
			IReadOnlyList<TrackedBody> registeredBodies,
			CompletedFrameStatusToken candidateToken,
			CompletedFrameClockSample clockSample,
			out int refreshedBodyCount)
		{
			refreshedBodyCount = 0;
			var manager = XRTrackerManager.Instance;
			if (manager == null || registeredBodies == null)
				return false;

			if (candidateToken.IsValid)
			{
				if (clockSample.Source != CompletedFrameClock.SeekResetSource &&
					_hasLastStatusRefreshToken &&
					_lastStatusRefreshSequenceId == candidateToken.SequenceId &&
					_lastStatusRefreshFrameIndex == candidateToken.FrameIndex &&
					_lastStatusRefreshTimestampNs == candidateToken.TimestampNs)
				{
					return false;
				}
			}

			bool allSucceeded = true;
			foreach (TrackedBody body in registeredBodies)
			{
				if (body.RefreshStatus(_completedStatusReader))
					refreshedBodyCount++;
				else
					allSucceeded = false;
			}
			if (allSucceeded)
			{
				if (candidateToken.IsValid)
				{
					_hasLastStatusRefreshToken = true;
					_lastStatusRefreshSequenceId = candidateToken.SequenceId;
					_lastStatusRefreshFrameIndex = candidateToken.FrameIndex;
					_lastStatusRefreshTimestampNs = candidateToken.TimestampNs;
				}
				UpdateGlobalPeak(registeredBodies);
			}
			return allSucceeded;
		}

		internal TrackedBodyStatusReader CompletedStatusReader
		{
			get => _completedStatusReader;
			set => _completedStatusReader = value ?? FTBridge.FT_GetBodyStatus;
		}

		internal void SetCompletedFrameInstrumentationContext(
			XRTrackerManager.TrackingFrameInstrumentationContext context)
		{
			_completedFrameInstrumentationContext = context;
			_completedTemporalFrameIndex = context.FrameIndex;
			_completedTemporalStepSerial = ++_temporalFallbackStepSerial;
			try
			{
				if (FTBridge.FT_GetLastTrackStepToken(out FTTrackStepToken token))
				{
					_completedTemporalStepSerial = token.stepSerial;
					if (token.loadedFrame >= 0)
						_completedTemporalFrameIndex = token.loadedFrame;
				}
			}
			catch (System.EntryPointNotFoundException) { }
			catch (System.DllNotFoundException) { }
			_lastCorrectionTelemetry.Clear();
		}

		internal void ResetCompletedFrameOrderState()
		{
			_completedFrameInstrumentationContext = null;
			_completedTemporalFrameIndex = -1;
			_completedTemporalStepSerial = 0;
			_lastCorrectionTelemetry.Clear();
			_hasLastStatusRefreshToken = false;
			_lastStatusRefreshSequenceId = string.Empty;
			_lastStatusRefreshFrameIndex = 0;
			_lastStatusRefreshTimestampNs = 0;
		}

		/// <summary>Injects a policy without changing any legacy lifecycle setting.</summary>
		internal TemporalTrackingPolicy TemporalStatusPolicy
		{
			get => _temporalTrackingPolicy;
			set => _temporalTrackingPolicy = value;
		}

		/// <summary>
		/// Updates the global histogram peak from all tracking root bodies.
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
