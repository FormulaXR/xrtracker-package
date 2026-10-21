using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;
using UnityEngine.Serialization;

namespace IV.FormulaTracker
{
	#region Calibration Data Classes

	[Serializable]
	public class CalibrationData
	{
		public CalibrationIntrinsics intrinsics;
		public CalibrationIntrinsics intrinsicsDist;
	}

	[Serializable]
	public class CalibrationIntrinsics
	{
		public float fx;
		public float fy;
		public float cx;
		public float cy;
		public int width;
		public int height;
		public float k1;
		public float k2;
		public float k3;
		public float k4;
		public float k5;

		/// <summary>
		/// Default intrinsics (works for most webcams).
		/// fu=fv=615.2, ppu=324, ppv=237 at 640x480.
		/// </summary>
		public static CalibrationIntrinsics Default => new CalibrationIntrinsics
		{
			fx = 0.9612f, fy = 1.2817f, cx = 0.5063f, cy = 0.4938f,
			width = 640, height = 480,
			k1 = 0, k2 = 0, k3 = 0, k4 = 0, k5 = 0
		};
	}

	[Serializable]
	public class CameraCalibrationEntry
	{
		public string deviceName;
		public CalibrationIntrinsics intrinsics;
		public CalibrationIntrinsics intrinsicsDist;
	}

	[Serializable]
	public class MultiCameraCalibration
	{
		public List<CameraCalibrationEntry> cameras;
		public CalibrationData @default;
	}

	#endregion

	/// <summary>
	/// Drains a queued injected step before its session can release or replace the
	/// native tracker. The completion worker only writes its result, so shutdown
	/// owns the semaphore release once it has observed completion.
	/// </summary>
	internal static class InjectedTrackingShutdown
	{
		internal static async Task DrainAsync(
			Func<bool> ownsInjectedStep,
			Func<bool> trackingStepComplete,
			Action releaseOwnership)
		{
			while (ownsInjectedStep() && !trackingStepComplete())
				await Task.Yield();
			if (ownsInjectedStep())
				releaseOwnership();
		}
	}

	internal static class DepthFrameFeed
	{
		internal static bool ShouldNotify(int nativeResult)
		{
			return FrameWriteGuard.RecordOutOfFrame(nativeResult, "FT_FeedDepthFrame");
		}
	}

	/// <summary>
	/// Decides whether a separately paused recovery phase may dispatch its next
	/// sequence step synchronously. Ordinary sequence tracking remains on the
	/// asynchronous path; this mode only keeps a post-step recovery operation on
	/// the exact thread that just ran native tracking.
	/// </summary>
	internal static class RecoverySynchronousStepMode
	{
		internal static bool CanEnter(bool initialized, bool trackingReady,
			bool trackingStepComplete, bool sequenceSource, bool alreadyEnabled)
		{
			return initialized && trackingReady && trackingStepComplete &&
				sequenceSource && !alreadyEnabled;
		}

		internal static bool IsActiveForSequence(bool enabled, bool sequenceSource) =>
			enabled && sequenceSource;

		internal static bool MustRestoreAfterCurrentDispatch(bool enabled,
			bool trackingReady) => enabled && !trackingReady;
	}

	internal readonly struct CompletedFrameClockSample
	{
		internal CompletedFrameClockSample(long deltaNs, float deltaSeconds,
			string source, bool deterministic)
		{
			DeltaNs = deltaNs;
			DeltaSeconds = deltaSeconds;
			Source = source;
			Deterministic = deterministic;
		}

		internal long DeltaNs { get; }
		internal float DeltaSeconds { get; }
		internal string Source { get; }
		internal bool Deterministic { get; }
	}

	/// <summary>
	/// Latches one lifecycle delta per completed native step. Recorded playback is
	/// driven only by ordered source timestamps; live capture retains the elapsed
	/// value supplied by the render loop, sampled once at completion.
	/// </summary>
	internal sealed class CompletedFrameClock
	{
		internal const string RecordedTimestampSource = "recorded_timestamp";
		internal const string SeekResetSource = "seek_reset";
		internal const string LiveElapsedSource = "live_elapsed";

		private bool _hasRecordedTimestamp;
		private long _previousRecordedTimestampNs;
		private long _observedSeekEpoch;

		internal void Reset(long seekEpoch)
		{
			_hasRecordedTimestamp = false;
			_previousRecordedTimestampNs = 0;
			_observedSeekEpoch = seekEpoch;
		}

		internal bool TryLatchRecorded(bool hasTimestamp, long timestampNs,
			long seekEpoch, out CompletedFrameClockSample sample, out string failure)
		{
			sample = default;
			failure = string.Empty;
			if (!hasTimestamp)
			{
				failure = "recorded timestamp is unavailable";
				return false;
			}

			bool seekReset = seekEpoch != _observedSeekEpoch;
			if (seekReset || !_hasRecordedTimestamp)
			{
				_observedSeekEpoch = seekEpoch;
				_previousRecordedTimestampNs = timestampNs;
				_hasRecordedTimestamp = true;
				sample = new CompletedFrameClockSample(
					0,
					0f,
					seekReset ? SeekResetSource : RecordedTimestampSource,
					true);
				return true;
			}

			long deltaNs;
			try
			{
				deltaNs = checked(timestampNs - _previousRecordedTimestampNs);
			}
			catch (OverflowException)
			{
				failure = "recorded timestamp delta overflowed";
				return false;
			}
			if (deltaNs <= 0)
			{
				failure = "recorded timestamps are not strictly increasing";
				return false;
			}

			_previousRecordedTimestampNs = timestampNs;
			sample = new CompletedFrameClockSample(
				deltaNs,
				(float)(deltaNs / 1_000_000_000.0),
				RecordedTimestampSource,
				true);
			return true;
		}

		internal bool TryPreviewRecorded(bool hasTimestamp, long timestampNs,
			long seekEpoch, out CompletedFrameClockSample sample, out string failure,
			out CompletedFrameClock pending)
		{
			pending = new CompletedFrameClock
			{
				_hasRecordedTimestamp = _hasRecordedTimestamp,
				_previousRecordedTimestampNs = _previousRecordedTimestampNs,
				_observedSeekEpoch = _observedSeekEpoch,
			};
			return pending.TryLatchRecorded(
				hasTimestamp, timestampNs, seekEpoch, out sample, out failure);
		}

		internal void Commit(CompletedFrameClock pending)
		{
			if (pending == null)
				return;
			_hasRecordedTimestamp = pending._hasRecordedTimestamp;
			_previousRecordedTimestampNs = pending._previousRecordedTimestampNs;
			_observedSeekEpoch = pending._observedSeekEpoch;
		}

		internal static CompletedFrameClockSample LatchLive(float elapsedSeconds)
		{
			float finiteElapsed = float.IsNaN(elapsedSeconds) ||
				float.IsInfinity(elapsedSeconds) || elapsedSeconds < 0f
				? 0f
				: elapsedSeconds;
			long deltaNs = checked((long)System.Math.Round(
				(double)finiteElapsed * 1_000_000_000.0,
				MidpointRounding.AwayFromZero));
			return new CompletedFrameClockSample(
				deltaNs, finiteElapsed, LiveElapsedSource, false);
		}

		internal static bool ShouldUseRecordedClock(
			bool sequenceSource, bool hasRecordedObservationSource)
		{
			return sequenceSource && hasRecordedObservationSource;
		}
	}

	internal static class CompletedFramePublicationContract
	{
		internal static bool CanPublish(bool stepSucceeded, int registeredBodies,
			int refreshedBodies)
		{
			return stepSucceeded && registeredBodies >= 0 &&
				refreshedBodies == registeredBodies;
		}
	}

	/// <summary>
	/// Decides when a dispatched sequence frame opens a new playback pass. With the loop on, the plugin reads the frames of the
	/// range in order and then starts the range again, and a range of one frame reads that frame on every step. In both cases the
	/// frame the step is dispatched for is not after the frame of the previous step, and the recorded timestamps repeat or go
	/// back with it. That is the start of a pass, and it is handled like a seek to the start of the range.
	/// </summary>
	internal static class SequencePassBoundary
	{
		internal static bool OpensNewPass(bool sequenceSource, bool loopOn, int previousFrame, int frame)
		{
			return sequenceSource && loopOn && previousFrame >= 0 && frame >= 0 && frame <= previousFrame;
		}
	}

	internal static class CompletedFrameRegistrationBoundary
	{
		internal static bool UsesRecordedClock(bool sequenceSource,
			bool hasRecordedObservationSource, int registeredBodyCount)
		{
			return registeredBodyCount > 0 &&
				CompletedFrameClock.ShouldUseRecordedClock(sequenceSource,
					hasRecordedObservationSource);
		}

		internal static bool ClearsDormantOrderState(int registeredBefore,
			int registeredAfter)
		{
			return registeredBefore == 1 && registeredAfter == 0;
		}
	}

	/// <summary>
	/// Keeps the recorded AR provider scoped to the feature that consumes its
	/// observations. Sequence images alone must not make a no-fusion replay
	/// depend on an optional recording sidecar.
	/// </summary>
	internal static class SequenceReplayFusionBindingPolicy
	{
		internal static bool ShouldBindRecordedFusion(bool useArPoseFusion)
		{
			return useArPoseFusion;
		}
	}

	internal static class CompletedFrameContextCommitContract
	{
		internal static bool CommitIfValid(bool clockValid, Action commit)
		{
			if (!clockValid)
				return false;
			commit();
			return true;
		}
	}

	internal readonly struct CompletedFrameStatusToken
	{
		internal CompletedFrameStatusToken(bool isValid, string sequenceId,
			int frameIndex, long timestampNs)
		{
			IsValid = isValid;
			SequenceId = sequenceId ?? string.Empty;
			FrameIndex = frameIndex;
			TimestampNs = timestampNs;
		}

		internal bool IsValid { get; }
		internal string SequenceId { get; }
		internal int FrameIndex { get; }
		internal long TimestampNs { get; }
	}

	public class XRTrackerManager : MonoBehaviour
	{
		/// <summary>
		/// Immutable snapshot for behavior-neutral per-frame instrumentation.
		/// It exposes the submitted frame identity without exposing mutable provider state.
		/// </summary>
		internal readonly struct TrackingFrameInstrumentationContext
		{
			public readonly bool IsValid;
			public readonly string SequenceId;
			public readonly int FrameIndex;
			public readonly long TimestampNs;
			public readonly string ObservationSource;
			public readonly string TrackingSpace;
			public readonly string SessionSpaceIdentity;
			public readonly int SessionSpaceEpoch;
			public readonly int CoordinateEpoch;
			public readonly Matrix4x4 WorldFromCameraUnity;
			public readonly Matrix4x4 WorldFromTrackingSpaceUnity;
			public readonly long TrackingStepDeltaNs;
			public readonly string TrackingStepClockSource;
			public readonly int RegisteredBodyCount;
			public readonly int RefreshedBodyCount;
			public readonly bool DeterministicClockEnabled;
			internal readonly float TrackingStepDeltaSeconds;

			internal TrackingFrameInstrumentationContext(
				bool isValid,
				string sequenceId,
				int frameIndex,
				long timestampNs,
				string observationSource,
				string trackingSpace,
				string sessionSpaceIdentity,
				int sessionSpaceEpoch,
				int coordinateEpoch,
				Matrix4x4 worldFromCameraUnity,
				Matrix4x4 worldFromTrackingSpaceUnity,
				CompletedFrameClockSample clockSample,
				int registeredBodyCount,
				int refreshedBodyCount)
			{
				IsValid = isValid;
				SequenceId = sequenceId ?? string.Empty;
				FrameIndex = frameIndex;
				TimestampNs = timestampNs;
				ObservationSource = observationSource ?? string.Empty;
				TrackingSpace = trackingSpace ?? string.Empty;
				SessionSpaceIdentity = sessionSpaceIdentity ?? string.Empty;
				SessionSpaceEpoch = sessionSpaceEpoch;
				CoordinateEpoch = coordinateEpoch;
				WorldFromCameraUnity = worldFromCameraUnity;
				WorldFromTrackingSpaceUnity = worldFromTrackingSpaceUnity;
				TrackingStepDeltaNs = clockSample.DeltaNs;
				TrackingStepClockSource = clockSample.Source ?? string.Empty;
				RegisteredBodyCount = registeredBodyCount;
				RefreshedBodyCount = refreshedBodyCount;
				DeterministicClockEnabled = clockSample.Deterministic;
				TrackingStepDeltaSeconds = clockSample.DeltaSeconds;
			}

			// Constructor for a caller that has no explicit coordinate identity to give.
			// A context built here carries no such identity and therefore cannot
			// authorize recovery.
			internal TrackingFrameInstrumentationContext(
				bool isValid,
				string sequenceId,
				int frameIndex,
				long timestampNs,
				string observationSource,
				string trackingSpace,
				Matrix4x4 worldFromCameraUnity,
				Matrix4x4 worldFromTrackingSpaceUnity,
				CompletedFrameClockSample clockSample,
				int registeredBodyCount,
				int refreshedBodyCount)
				: this(isValid, sequenceId, frameIndex, timestampNs,
					observationSource, trackingSpace, string.Empty, 0, 0,
					worldFromCameraUnity, worldFromTrackingSpaceUnity, clockSample,
					registeredBodyCount, refreshedBodyCount)
			{
			}
		}
		#region Singleton

		private static XRTrackerManager _instance;
		public static XRTrackerManager Instance => _instance;

		public static event Action OnTrackerInitialized;
		public static event Action OnTrackerShutdown;

		#endregion

		#region Serialized Fields

		[Tooltip("Native: Use built-in webcam capture. Injected: Receive frames from external source (AR Foundation, etc.)")] [SerializeField]
		private ImageSource _imageSource = ImageSource.Native;

		[Tooltip("Folder with sequence.json and recorded frames. Falls back to latest subfolder if needed.")] [SerializeField]
		private string _sequenceDirectory;

		[Tooltip("RealSense color camera resolution. Higher = more detail, lower FPS.")] [SerializeField]
		private RealSenseColorResolution _realSenseColorResolution = RealSenseColorResolution.Resolution_1920x1080_30fps;

		[Tooltip("RealSense depth camera resolution. Higher = more detail, lower FPS.")] [SerializeField]
		private RealSenseDepthResolution _realSenseDepthResolution = RealSenseDepthResolution.Resolution_1280x720_30fps;

		[Tooltip("Camera calibration data (JSON). If not assigned, loads default from Resources.")] [SerializeField]
		private TextAsset _calibrationsFile;

		[Tooltip("Automatically select and initialize a camera on start. Leave empty to disable.")] [SerializeField]
		private string _autoSelectCameraName = "";

		[Tooltip("If auto-select camera name not found, fall back to first available camera.")] [SerializeField]
		private bool _autoSelectFallbackToFirst = true;

		[Tooltip("Target frames per second for tracking loop.")] [SerializeField, Range(10, 60)]
		private int _targetFps = 30;

		[Tooltip("Number of correspondence iterations per frame. Higher = more accurate, slower.")]
		[SerializeField, Range(1, 10), FormerlySerializedAs("_nCorrIterations")]
		private int _correspondenceIterations = 5;

		[Tooltip("Number of pose update iterations per correspondence. Higher = more refined pose, slower.")]
		[SerializeField, Range(1, 5), FormerlySerializedAs("_nUpdateIterations")]
		private int _updateIterations = 2;
		
		// [SerializeField, Range(256, 2048)]
		private int _edgeRenderResolution = 512;

		[Tooltip("Main camera used for tracking. If not set, Camera.main will be used.")] [SerializeField]
		private Camera _mainCamera;

#if HAS_AR_FOUNDATION
		[Tooltip("Use AR Foundation's world tracking to stabilize pose when tracking quality drops")] [SerializeField]
		private bool _useARPoseFusion = true;
#endif

		[Tooltip("When the AR session moves its world, the AR camera jumps farther in one frame than a hand can move a phone (more than 25 centimetres and faster than 5 metres per second, or more than 45 degrees and faster than 900 degrees per second). With this on, every tracked body is carried into the new world, so it keeps its place relative to the camera. With this off, a body can lose the object after such a jump.")]
		[SerializeField]
		private bool _followArWorldJumps = true;

		[Tooltip("Optional: drag a .lic TextAsset here to embed the license in the build.")]
		[SerializeField] private TextAsset _embeddedLicense;

		#endregion

		#region Camera Selection

		private FTCameraDevice[] _availableCameras;
		private int _selectedCameraIndex = -1;
		private MultiCameraCalibration _calibrations;

		public FTCameraDevice[] AvailableCameras => _availableCameras;
		public int SelectedCameraIndex => _selectedCameraIndex;

		public FTCameraDevice? SelectedCamera => _selectedCameraIndex >= 0 && _selectedCameraIndex < _availableCameras?.Length
			? _availableCameras[_selectedCameraIndex]
			: null;

		public event Action<FTCameraDevice[]> OnCamerasEnumerated;
		public event Action<FTCameraDevice> OnCameraSelected;

		#endregion

		#region Events

		public event Action<Texture> OnImage;
		public event Action OnCropFactorsChanged;

		/// <summary>
		/// Called before each tracking step begins.
		/// Use this to prepare data before native tracking runs.
		/// </summary>
		public event Action OnBeforeTrackingStep;

		/// <summary>
		/// Called after each tracking step completes.
		/// Use this to apply poses immediately after native tracking (best timing for non-SLAM).
		/// </summary>
		public event Action OnAfterTrackingStep;

		/// <summary>Runs after native completion and before Unity acceptance/holding.</summary>
		internal event Action<TrackingFrameInstrumentationContext> OnRawTrackingStepCompleted;

		/// <summary>Runs after all current Unity lifecycle subscribers processed the frame.</summary>
		internal event Action<TrackingFrameInstrumentationContext> OnTrackingLifecycleCompleted;
		
		/// <summary>
		/// Called when tracking is paused via PauseTracking().
		/// </summary>
		public event Action OnTrackingPaused;

		/// <summary>
		/// Called when tracking is resumed via ResumeTracking().
		/// </summary>
		public event Action OnTrackingResumed;

		/// <summary>
		/// Called after a frame is fed to the native tracker via FeedFrame/FeedFrameAsync.
		/// Parameters: rgbDataPtr, width, height, fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation.
		/// Use this to intercept frames for recording.
		/// </summary>
		public event Action<IntPtr, int, int, float, float, float, float, int> OnFrameFed;

		/// <summary>
		/// Called when a depth frame is available. Fired by FeedDepthFrame (injected) or
		/// HandleDepthImageCallback (RealSense). Parameters: depthDataPtr, width, height, depthScale.
		/// Use this to intercept depth frames for recording.
		/// </summary>
		public event Action<IntPtr, int, int, float> OnDepthFrameFed;

		/// <summary>
		/// Fired when a TrackedBody is registered with the manager.
		/// </summary>
		public event Action<TrackedBody> OnBodyRegistered;

		/// <summary>
		/// Fired when a TrackedBody is unregistered from the manager.
		/// </summary>
		public event Action<TrackedBody> OnBodyUnregistered;

		#endregion

		#region Private Fields

		private bool _isInitialized;
		private int _managerThreadId;
		private bool _hasExecutedDetection;
		private int _imageWidth;
		private int _imageHeight;
		private float _cachedFu;
		private float _cachedFv;
		private float _cachedPpu;
		private float _cachedPpv;
		private float _cropFactorX = -1f;
		private float _cropFactorY = -1f;

		private FTBridge.ImageCallback _imageCallbackDelegate;
		private FTBridge.DepthImageCallback _depthImageCallbackDelegate;

		private Texture2D _webcamTexture;
		private byte[] _imageBuffer;

		private bool _trackingReady;
		private Coroutine _trackingCoroutine;
		private Transform _cameraTransform;
		// Async tracking state
		private volatile bool _trackingStepComplete = true; // Start true so OnDestroy doesn't wait if never started
		private volatile int _trackingStepResult;
		private readonly object _trackingLock = new object();
		private bool _recoverySynchronousStepMode;
		private bool _deterministicReplayHold;
		private bool _deterministicReplayOwnsPlayback;
		// Serials for the three points a frame passes through. They are separate
		// counters on purpose: a dispatch that never completes, or a completion
		// that never publishes, is only visible as a gap between two of them.
		private long _dispatchSerial;
		private long _completionSerial;
		private long _publicationSerial;
		private int _dispatchedSequenceFrame = -1;
		private readonly CompletedFrameClock _completedFrameClock = new();

#if HAS_AR_FOUNDATION
		private readonly FusionFrameContextResolver _fusionFrameContextResolver = new();
		private readonly IFusionCameraPoseSink _fusionCameraPoseSink = new NativeFusionCameraPoseSink();
		private LiveFusionFrameObservationSource _liveFusionObservationSource;
		private IARSpaceProvider _liveFusionObservationProvider;
		private FusionFrameContext? _submittedFusionFrameContext;
		private FusionFrameContext? _completedFusionFrameContext;
		private readonly ArWorldJumpWatch _worldJumpWatch = new();
		private int _nextLiveFusionFrameIndex;
		private int _lastRejectedFusionFrameIndex = int.MinValue;
		private FusionObservationFailure _lastRejectedFusionFailure;
#endif

		// The plugin turns each colour frame to the screen rotation before tracking and feeds depth frames as they come, so the injected
		// depth camera is turned about its optical axis to match instead. These hold the rotation of the last colour frame fed, the
		// rotation the depth camera was last turned to, and the colour camera pose the plugin holds, which the turn is composed with.
		private bool _injectedDepthCamera;
		private int _fedScreenRotation;
		private int _depthCameraRotation = -1;
		private FTTrackingPose _colorCameraPose = new FTTrackingPose { rot_w = 1f };

		// Async injected mode state
		private volatile bool _injectedTrackingInProgress;
		private byte[] _injectedFrameBuffer; // Double buffer for async frame feeding
		private GCHandle _injectedFrameHandle;
		private bool _injectedFrameHandleAllocated;

		// Registered tracked bodies for quality-based state management
		private readonly List<TrackedBody> _trackedBodies = new List<TrackedBody>();
		private ReadOnlyCollection<TrackedBody> _trackedBodiesView;
		private readonly List<TrackedBody> _completedStepRegisteredBodies =
			new List<TrackedBody>();

		// Semaphore for serializing native access (registration, unregistration, tracking)
		private readonly SemaphoreSlim _nativeAccessLock = new SemaphoreSlim(1, 1);

		private bool UseInjectedCamera() => _imageSource == ImageSource.Injected;

		// The tracker solves in the AR session's world frame, which is the only tracking space there is.
		private const string WORLD_TRACKING_SPACE = "World";

		// License state (cached from native)
		private LicenseTier _licenseTier = LicenseTier.None;
		private LicenseStatus _licenseStatus = LicenseStatus.NotSet;

		#endregion

		#region Public Properties

		public bool IsInitialized => _isInitialized;
		internal bool IsManagerThread => _managerThreadId != 0 &&
			Thread.CurrentThread.ManagedThreadId == _managerThreadId;

		/// <summary>
		/// True when sequence playback has a recorded fusion observation source bound.
		/// It does not affect tracking, fusion, or pose acceptance.
		/// </summary>
		internal bool HasRecordedFusionObservationSource
		{
			get
			{
#if HAS_AR_FOUNDATION
				return _imageSource == ImageSource.Sequence &&
				       Recording.SequenceARPlaybackBinder.IsBound;
#else
				return false;
#endif
			}
		}
		public bool IsTrackingReady => _trackingReady;

		public ImageSource CurrentImageSource
		{
			get => _imageSource;
			set => _imageSource = value;
		}

		/// <summary>
		/// RealSense color camera resolution. Set before calling InitializeRealSenseAsync().
		/// </summary>
		public RealSenseColorResolution CurrentRealSenseColorResolution
		{
			get => _realSenseColorResolution;
			set => _realSenseColorResolution = value;
		}

		/// <summary>
		/// RealSense depth camera resolution. Set before calling InitializeRealSenseAsync().
		/// </summary>
		public RealSenseDepthResolution CurrentRealSenseDepthResolution
		{
			get => _realSenseDepthResolution;
			set => _realSenseDepthResolution = value;
		}

		/// <summary>
		/// True if using native capture (Native or RealSense modes).
		/// These modes use TrackingCoroutine and FT_ProcessImageCallback.
		/// </summary>
		public bool UsesNativeCapture => _imageSource == ImageSource.Native
		                                 || _imageSource == ImageSource.RealSense
		                                 || _imageSource == ImageSource.Sequence;

		/// <summary>
		/// True if calibration file is required (only Native/webcam mode).
		/// RealSense and Injected modes get intrinsics from SDK/AR Foundation.
		/// </summary>
		public bool RequiresCalibrationFile => _imageSource == ImageSource.Native;

		/// <summary>
		/// True if depth camera is available. Query from native after initialization.
		/// </summary>
		public bool HasDepthCamera => _isInitialized && FTBridge.FT_HasDepthCamera();

		public float CropFactorX => _cropFactorX;
		public float CropFactorY => _cropFactorY;

		public int TargetFps
		{
			get => _targetFps;
			set => _targetFps = value;
		}

		/// <summary>
		/// Number of correspondence iterations per frame (1-10). Higher = more accurate, slower.
		/// Changes take effect on the next body registration.
		/// </summary>
		public int CorrespondenceIterations
		{
			get => _correspondenceIterations;
			set => _correspondenceIterations = Mathf.Clamp(value, 1, 10);
		}

		/// <summary>
		/// Number of pose update iterations per correspondence step (1-5). Higher = more refined, slower.
		/// Changes take effect on the next body registration.
		/// </summary>
		public int UpdateIterations
		{
			get => _updateIterations;
			set => _updateIterations = Mathf.Clamp(value, 1, 5);
		}

		/// <summary>
		/// Resolution of the shared normal/depth renderer used by edge modality (256-2048).
		/// Can be changed at runtime — calls FT_SetEdgeRenderResolution immediately.
		/// </summary>
		public int EdgeRenderResolution
		{
			get => _edgeRenderResolution;
			set
			{
				_edgeRenderResolution = Mathf.Clamp(value, 256, 2048);
				if (_isInitialized)
				{
					WaitForTrackingStepToReturn();
					FTBridge.FT_SetEdgeRenderResolution(_edgeRenderResolution);
				}
			}
		}

		public Transform CameraTransform => _cameraTransform;

		/// <summary>
		/// Current tracking image width in pixels. Returns 0 if not initialized.
		/// </summary>
		public int ImageWidth => _imageWidth;

		/// <summary>
		/// Current tracking image height in pixels. Returns 0 if not initialized.
		/// </summary>
		public int ImageHeight => _imageHeight;

		/// <summary>
		/// Returns true if async tracking is currently in progress (injected mode only).
		/// Use this to know when it's safe to call FeedFrameAsync again.
		/// </summary>
		public bool IsTrackingInProgress => _injectedTrackingInProgress;

		/// <summary>
		/// All registered TrackedBodies. Use this to iterate over tracked objects.
		/// The collection is a read-only view that follows the registrations, so it can be kept and read again, and it cannot be cast back to the list behind it.
		/// </summary>
		public IReadOnlyList<TrackedBody> TrackedBodies => _trackedBodiesView ??= new ReadOnlyCollection<TrackedBody>(_trackedBodies);
		internal IReadOnlyList<TrackedBody> CompletedStepRegisteredBodies =>
			_completedStepRegisteredBodies;

#if HAS_AR_FOUNDATION
		internal bool TryGetSubmittedFusionFrameContext(out FusionFrameContext context)
		{
			if (_submittedFusionFrameContext.HasValue)
			{
				context = _submittedFusionFrameContext.Value;
				return true;
			}
			context = default;
			return false;
		}

		internal bool TryGetCompletedFusionFrameContext(out FusionFrameContext context)
		{
			if (_completedFusionFrameContext.HasValue)
			{
				context = _completedFusionFrameContext.Value;
				return true;
			}
			context = default;
			return false;
		}
#endif
		
		public Texture CurrentTexture { get; private set; }

		/// <summary>Cached normalized focal length U from last frame feed.</summary>
		public float CachedFu => _cachedFu;
		/// <summary>Cached normalized focal length V from last frame feed.</summary>
		public float CachedFv => _cachedFv;
		/// <summary>Cached normalized principal point U from last frame feed.</summary>
		public float CachedPpu => _cachedPpu;
		/// <summary>Cached normalized principal point V from last frame feed.</summary>
		public float CachedPpv => _cachedPpv;

		/// <summary>
		/// Whether AR Foundation pose fusion is enabled.
		/// When enabled, uses AR world tracking to stabilize pose when tracking quality drops.
		/// </summary>
#if HAS_AR_FOUNDATION
		/// <summary>
		/// Whether fusion is in force. The serialized choice only counts for a camera source that provides
		/// a camera pose, which is an AR camera source or a sequence with recorded camera poses. For any
		/// other source it reads false whatever is serialized.
		/// </summary>
		public bool UseARPoseFusion
		{
			get => _useARPoseFusion && FusionSupportedBySource();
			set => _useARPoseFusion = value;
		}

		private string _fusionVerdictDirectory;
		private PoseFusionSourcePolicy.Verdict _fusionVerdict;
		private bool _fusionIgnoredLogged;

		/// <summary>
		/// False when the selected camera source cannot carry fusion. Logs once that the serialized choice
		/// is ignored, and why.
		/// </summary>
		private bool FusionSupportedBySource()
		{
			PoseFusionSourcePolicy.Verdict verdict = CurrentFusionVerdict();
			if (!PoseFusionSourcePolicy.Discards(verdict))
				return true;

			if (!_fusionIgnoredLogged && _useARPoseFusion)
			{
				_fusionIgnoredLogged = true;
				Debug.Log("[XRTracker] Pose fusion is ignored because " +
				          PoseFusionSourcePolicy.Reason(verdict, _imageSource) + ".");
			}
			return false;
		}

		private PoseFusionSourcePolicy.Verdict CurrentFusionVerdict()
		{
			if (_imageSource != ImageSource.Sequence)
				return PoseFusionSourcePolicy.Evaluate(_imageSource, null);

			string directory = string.IsNullOrEmpty(_resolvedSequenceDirectory)
				? _sequenceDirectory
				: _resolvedSequenceDirectory;
			if (_fusionVerdictDirectory != directory ||
			    _fusionVerdict == PoseFusionSourcePolicy.Verdict.SequenceNotYetKnown)
			{
				_fusionVerdict = PoseFusionSourcePolicy.Evaluate(ImageSource.Sequence, directory);
				_fusionVerdictDirectory = directory;
			}
			return _fusionVerdict;
		}
#else
		public bool UseARPoseFusion { get; set; } = false;
#endif

		/// <summary>
		/// Whether every tracked body is carried into the new world when the AR session moves its world. The AR camera then jumps farther in one
		/// step than a hand can move a phone, which is more than 25 centimetres and faster than 5 metres per second, or more than 45 degrees and
		/// faster than 900 degrees per second. With this on, each body keeps its place relative to the camera across the jump. With this off, the
		/// bodies stay at the poses they held in the old world, and a body can lose the object. It is on by default, it acts only under AR pose
		/// fusion, and the tracker reads it on every step, so a change applies at once. The watch for the jump runs on every step whatever this is
		/// set to, so turning it on during a session never reads the camera pose from before it was turned on as a jump. A value that a run has
		/// set for the AR world carry wins over it.
		/// </summary>
		public bool FollowArWorldJumps
		{
			get => _followArWorldJumps;
			set => _followArWorldJumps = value;
		}

		/// <summary>
		/// The camera the tracker follows and whose projection it sets. Setting null clears it together with its transform.
		/// </summary>
		public Camera MainCamera
		{
			get => _mainCamera;
			set
			{
				_mainCamera = value;
				_cameraTransform = _mainCamera != null ? _mainCamera.transform : null;
			}
		}

		/// <summary>Current license tier (None = no license loaded).</summary>
		public LicenseTier LicenseTier => _licenseTier;

		/// <summary>Current license validation status.</summary>
		public LicenseStatus LicenseStatus => _licenseStatus;

		/// <summary>True if the free tier limit of 60 seconds has been reached. The free tier no longer exists, so this is obsolete.</summary>
		[System.Obsolete("The free tier no longer exists, so IsLicenseFrozen has no meaning. Read LicenseStatus and LicenseTier to see whether the license in use is valid.")]
		public bool IsLicenseFrozen => FTBridge.FT_IsLicenseFrozen();

		/// <summary>Seconds remaining of the free tier limit of 60 seconds. The free tier no longer exists, so this is obsolete.</summary>
		[System.Obsolete("The free tier no longer exists, so FreeSecondsRemaining has no meaning. Read LicenseStatus and LicenseTier to see whether the license in use is valid.")]
		public float FreeSecondsRemaining => FTBridge.FT_GetFreeSecondsRemaining();

		/// <summary>Machine ID used for Commercial license node-locking.</summary>
		public string MachineId => SystemInfo.deviceUniqueIdentifier;

		#endregion

		#region License

		private void LoadLicense()
		{
			string licenseJson = null;

			// 1. Embedded TextAsset (highest priority — OEM / shipped builds)
			if (_embeddedLicense != null)
				licenseJson = _embeddedLicense.text;

			// 2. StreamingAssets/*.lic
			if (licenseJson == null)
				licenseJson = FindLicenseInStreamingAssets();

			// 3. persistentDataPath/FormulaTracker.lic
			if (licenseJson == null)
			{
				string path = Path.Combine(Application.persistentDataPath, "FormulaTracker.lic");
				if (File.Exists(path))
				{
					try { licenseJson = File.ReadAllText(path); }
					catch (Exception e) { Debug.LogWarning($"[XRTracker] Failed to read license: {e.Message}"); }
				}
			}

			// 4. No license found → prompt registration
			if (licenseJson == null)
			{
				_licenseTier = LicenseTier.None;
				_licenseStatus = LicenseStatus.NotSet;
				Debug.LogWarning("[XRTracker] No license found. Register for a free Developer license: Tools > XRTracker > License Registration");
				return;
			}

			// Send to native
			string machineId = SystemInfo.deviceUniqueIdentifier;
			int result = FTBridge.FT_SetLicenseData(licenseJson, licenseJson.Length, machineId);
			_licenseStatus = (LicenseStatus)result;
			_licenseTier = (LicenseTier)FTBridge.FT_GetLicenseTier();

			if (_licenseStatus == LicenseStatus.Valid)
			{
				if (_licenseTier == LicenseTier.Developer)
					Debug.Log("[XRTracker] Developer license activated. For development use only \u2014 production usage is prohibited.");
				else
					Debug.Log($"[XRTracker] License: {_licenseTier}");
				CheckLicenseExpiryWarning();
			}
			else
				Debug.LogWarning($"[XRTracker] License failed: {_licenseStatus} (tier: {_licenseTier})");
		}

		private void CheckLicenseExpiryWarning()
		{
			int days = FTBridge.FT_GetLicenseDaysUntilExpiry();
			if (days < 0) return; // Perpetual
			if (days <= 30)
				Debug.LogWarning($"[XRTracker] License expires in {days} day{(days != 1 ? "s" : "")}. Check your email for the renewed license file.");
		}

		/// <summary>
		/// Load a license from a JSON string at runtime.
		/// Returns the resulting LicenseStatus.
		/// </summary>
		public LicenseStatus SetLicense(string licenseJson)
		{
			if (string.IsNullOrEmpty(licenseJson))
				return LicenseStatus.FormatError;

			string machineId = SystemInfo.deviceUniqueIdentifier;
			WaitForTrackingStepToReturn();
			int result = FTBridge.FT_SetLicenseData(licenseJson, licenseJson.Length, machineId);
			_licenseStatus = (LicenseStatus)result;
			_licenseTier = (LicenseTier)FTBridge.FT_GetLicenseTier();
			return _licenseStatus;
		}

		/// <summary>
		/// Refresh the cached license tier and status from the native side.
		/// </summary>
		public void RefreshLicenseState()
		{
			_licenseTier = (LicenseTier)FTBridge.FT_GetLicenseTier();
			_licenseStatus = (LicenseStatus)FTBridge.FT_GetLicenseStatus();
		}

		private string FindLicenseInStreamingAssets()
		{
#if UNITY_ANDROID && !UNITY_EDITOR
			return null; // Android StreamingAssets inside APK — use embedded TextAsset instead
#else
			string saPath = Application.streamingAssetsPath;
			if (!Directory.Exists(saPath)) return null;

			string[] licFiles = Directory.GetFiles(saPath, "*.lic");
			if (licFiles.Length == 0) return null;

			Array.Sort(licFiles);
			try { return File.ReadAllText(licFiles[0]); }
			catch (Exception e)
			{
				Debug.LogWarning($"[XRTracker] Failed to read {licFiles[0]}: {e.Message}");
				return null;
			}
#endif
		}

		#endregion

		#region Unity Lifecycle

		private void Awake()
		{
			if (_instance != null && _instance != this)
			{
				Destroy(gameObject);
				return;
			}

			_instance = this;
			_managerThreadId = Thread.CurrentThread.ManagedThreadId;
			_completedFrameClock.Reset(FTBridge.SequenceSeekEpoch);

			LoadLicense();
			EnsureLicenseWatermark();

			if (_mainCamera == null)
				_mainCamera = Camera.main;

			if (_mainCamera != null)
				_cameraTransform = _mainCamera.transform;

			FTBridge.FT_SetEdgeRenderResolution(_edgeRenderResolution);

			// Auto-create TrackedBodyManager if not present
			EnsureTrackedBodyManager();
			TrackedBodyManager.Instance?.ResetCompletedFrameOrderState();

#if HAS_AR_FOUNDATION
			EnsureLiveFusionObservationSource();
#endif
			// A recording with camera poses that plays under pose fusion needs the camera moved along those poses, and the driver has to
			// exist before the bodies are enabled so it can seat the scene in the recording's world frame.
			EnsureRecordedCameraPoseDriver(_imageSource == ImageSource.Sequence && UseARPoseFusion);
		}

		/// <summary>
		/// Adds the recorded camera pose driver to the tracker's object when a recording with camera poses plays under pose fusion and the
		/// scene has no driver yet. A scene that carries one is left alone, so the camera is not driven twice.
		/// </summary>
		private void EnsureRecordedCameraPoseDriver(bool posesPlayUnderFusion)
		{
			if (!Recording.RecordedCameraPoseDriverPolicy.ShouldAttach(posesPlayUnderFusion, Recording.RecordedCameraPoseDriver.ExistsInScene()))
				return;
			gameObject.AddComponent<Recording.RecordedCameraPoseDriver>();
			Debug.Log("[XRTracker] The camera follows the recorded camera poses of this recording.");
		}

		private void EnsureTrackedBodyManager()
		{
			if (TrackedBodyManager.Instance == null)
			{
				var manager = gameObject.AddComponent<TrackedBodyManager>();
				Debug.Log("[XRTracker] Auto-created TrackedBodyManager");
			}
		}

		private void EnsureLicenseWatermark()
		{
			// No license → show registration overlay (blocking)
			if (_licenseTier == LicenseTier.None && _licenseStatus == LicenseStatus.NotSet)
			{
				if (GetComponent<LicenseRegistrationOverlay>() == null)
					gameObject.AddComponent<LicenseRegistrationOverlay>();
				return;
			}

#pragma warning disable CS0618 // The Free and Trial tiers are retired, but a license file can still name them.
			bool needsWatermark = _licenseTier == LicenseTier.Free ||
			                      _licenseTier == LicenseTier.Trial ||
			                      _licenseTier == LicenseTier.Developer ||
			                      _licenseStatus == LicenseStatus.Expired;
#pragma warning restore CS0618
			if (!needsWatermark) return;
			if (GetComponent<LicenseWatermark>() != null) return;
			gameObject.AddComponent<LicenseWatermark>();
		}

		private async void Start()
		{
			if (_imageSource == ImageSource.Native)
			{
				LoadCalibrations();
				EnumerateCameras();

				// Auto-select camera by name if specified
				if (!string.IsNullOrEmpty(_autoSelectCameraName) && _availableCameras != null && _availableCameras.Length > 0)
				{
					int cameraIndex = FindCameraByName(_autoSelectCameraName);
					if (cameraIndex >= 0)
					{
						await SelectCameraAsync(cameraIndex);
					}
					else if (_autoSelectFallbackToFirst)
					{
						Debug.LogWarning($"[XRTracker] Camera '{_autoSelectCameraName}' not found, using first available camera");
						await SelectCameraAsync(0);
					}
					else
					{
						Debug.LogWarning($"[XRTracker] Camera '{_autoSelectCameraName}' not found, no camera selected");
					}
				}
			}
			else if (_imageSource == ImageSource.RealSense)
			{
				await InitializeRealSenseAsync();
			}
			else if (_imageSource == ImageSource.Sequence)
			{
				await InitializeSequenceWithCoroutineAsync();
			}
		}

		private void Update()
		{
			if (!_isInitialized) return;

			// Check if async injected tracking completed
			if (_injectedTrackingInProgress && _trackingStepComplete)
			{
				try
				{
					CompleteNativeTrackingStepFromUpdate(_trackingStepResult);
				}
				finally
				{
					ReleaseInjectedTrackingOwnership();
				}
			}

			CheckScreenAspectChange();

			if (!_hasExecutedDetection)
			{
				// Replay and camera capture queue the first step before this runs, so it may still be in flight.
				WaitForTrackingStepToReturn();
				FTBridge.FT_ExecuteDetection();
				_hasExecutedDetection = true;
			}
		}

		private IEnumerator TrackingCoroutine()
		{
			float targetFrameTime = 1f / _targetFps;

			while (_trackingReady && _isInitialized)
			{
				// A deterministic replay owns every frame it drives, so this loop
				// must not dispatch alongside it. The loop stays alive, so leaving
				// that mode resumes ordinary playback without restarting anything.
				if (_deterministicReplayHold || _deterministicReplayOwnsPlayback)
				{
					yield return null;
					continue;
				}

				float frameStart = Time.realtimeSinceStartup;

				// Try to acquire native lock - skip frame if body registration in progress
				if (!_nativeAccessLock.Wait(0))
				{
					yield return null;
					continue;
				}

				bool dispatchedStep = false;
				try
				{
					// 1. Run BeforeTrackingStep on main thread (accesses Unity transforms).
					// Sequence and native capture have no managed FT_FeedFrame call, so this
					// is where their frame-write decision begins.
					if (!FrameWriteGuard.BeginFrame())
						continue;
					if (!BeforeTrackingStep())
						continue;

					if (RecoverySynchronousStepMode.IsActiveForSequence(
						_recoverySynchronousStepMode, _imageSource == ImageSource.Sequence))
					{
						// Recovery owns one paused/seeked frame at a time. Keeping this
						// controlled phase on the main thread lets recovery read the tracker
						// right after the step, under the native thread contract, without
						// moving ordinary frames.
						++_dispatchSerial;
						_dispatchedSequenceFrame = OpenSequenceDispatch();
						_trackingStepResult = FTBridge.FT_TrackStep();
						dispatchedStep = true;
						CompleteNativeTrackingStepFromCoroutine(_trackingStepResult);
					}
					else
					{
						// Ordinary sequence and native capture keep the asynchronous path.
						++_dispatchSerial;
						_dispatchedSequenceFrame = OpenSequenceDispatch();
						QueueNativeTrackingStep();
						dispatchedStep = true;

						// Wait for background thread to complete (non-blocking yield).
						while (!_trackingStepComplete)
						{
							yield return null;
						}

						// Publish results only when native consumed the submitted frame.
						CompleteNativeTrackingStepFromCoroutine(_trackingStepResult);
					}
				}
				finally
				{
					if (!dispatchedStep)
						FrameWriteGuard.AbortFrame();
					_nativeAccessLock.Release();
					if (RecoverySynchronousStepMode.MustRestoreAfterCurrentDispatch(
						_recoverySynchronousStepMode, _trackingReady))
					{
						ExitRecoverySynchronousStepMode();
					}
				}

				// 6. Process image callback to update background texture (must be after tracking to sync with pose)
				if (UsesNativeCapture)
				{
					FTBridge.FT_ProcessImageCallback();
					FTBridge.FT_ProcessDepthImageCallback();
				}

				// 7. Wait remaining time to hit target FPS
				float elapsed = Time.realtimeSinceStartup - frameStart;
				float remainingTime = targetFrameTime - elapsed;
				if (remainingTime > 0)
				{
					yield return new WaitForSeconds(remainingTime);
				}
			}

			_trackingCoroutine = null;
		}

		private void OnDestroy()
		{
			FrameWriteGuard.Clear();
			ExitRecoverySynchronousStepMode();
			_trackingReady = false;
			if (_trackingCoroutine != null)
			{
				StopCoroutine(_trackingCoroutine);
				_trackingCoroutine = null;
			}

			// Wait for any in-progress background tracking step to complete
			// This prevents calling FT_Shutdown while FT_TrackStep is running
			int waitCount = 0;
			while (!_trackingStepComplete && waitCount < 100)
			{
				Thread.Sleep(10);
				waitCount++;
			}


			// Update no longer runs during teardown, so it cannot release an
			// injected feed that has just completed.
			if (_trackingStepComplete)
				ReleaseInjectedTrackingOwnership();

			// Free the pinned frame buffer handle
			if (_injectedFrameHandleAllocated)
			{
				_injectedFrameHandle.Free();
				_injectedFrameHandleAllocated = false;
			}

			_injectedFrameBuffer = null;

			_isInitialized = false;
			_cameraTransform = null;

#if HAS_AR_FOUNDATION
			Recording.SequenceARPlaybackBinder.Unbind();
			ResetFusionFrameState();
#endif

			FTBridge.FT_Shutdown();

			if (_webcamTexture != null)
			{
				Destroy(_webcamTexture);
				_webcamTexture = null;
			}

			_hasExecutedDetection = false;

			if (_instance == this)
				_instance = null;
			
			OnTrackerShutdown?.Invoke();
		}

		#endregion

		#region Public API

		/// <summary>
		/// Feed a frame with intrinsics and run tracking synchronously (blocks main thread).
		/// For better performance, use FeedFrameAsync instead.
		/// </summary>
		/// <param name="screenRotation">Screen orientation (use FTScreenRotation enum or GetScreenRotation())</param>
		public void FeedFrame(byte[] rgbData, int width, int height,
			float fuNorm, float fvNorm, float ppuNorm, float ppvNorm,
			int screenRotation = 0)
		{
			if (_imageSource != ImageSource.Injected || !_isInitialized) return;
			if (!_nativeAccessLock.Wait(0)) return;

			try
			{
				if (!FrameWriteGuard.BeginFrame())
					return;
				GCHandle handle = GCHandle.Alloc(rgbData, GCHandleType.Pinned);
				try
				{
					_fedScreenRotation = Mathf.Clamp(screenRotation, 0, 3);
					if (FrameWriteGuard.Record(
					FTBridge.FT_FeedFrame(handle.AddrOfPinnedObject(), width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation),
					"FT_FeedFrame"))
					{
						OnFrameFed?.Invoke(handle.AddrOfPinnedObject(), width, height,
							fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
					}
					if (BeforeTrackingStep())
					{
						CompleteNativeTrackingStep(FTBridge.FT_TrackStep());
					}
				}
				finally
				{
					handle.Free();
				}
			}
			finally
			{
				FrameWriteGuard.AbortFrame();
				_nativeAccessLock.Release();
			}
		}

		/// <summary>
		/// Feed a frame with intrinsics and run tracking synchronously (blocks main thread).
		/// For better performance, use FeedFrameAsync instead.
		/// </summary>
		/// <param name="screenRotation">Screen orientation (use FTScreenRotation enum or GetScreenRotation())</param>
		public void FeedFrame(IntPtr rgbData, int width, int height,
			float fuNorm, float fvNorm, float ppuNorm, float ppvNorm,
			int screenRotation = 0)
		{
			if (_imageSource != ImageSource.Injected || !_isInitialized) return;
			if (!_nativeAccessLock.Wait(0)) return;

			try
			{
				if (!FrameWriteGuard.BeginFrame())
					return;
				_fedScreenRotation = Mathf.Clamp(screenRotation, 0, 3);
				if (FrameWriteGuard.Record(
					FTBridge.FT_FeedFrame(rgbData, width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation),
					"FT_FeedFrame"))
				{
					OnFrameFed?.Invoke(rgbData, width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
				}
				if (BeforeTrackingStep())
				{
					CompleteNativeTrackingStep(FTBridge.FT_TrackStep());
				}
			}
			finally
			{
				FrameWriteGuard.AbortFrame();
				_nativeAccessLock.Release();
			}
		}

		/// <summary>Sequence directory for Sequence mode (as set by the caller — may be a parent).</summary>
		public string SequenceDirectory
		{
			get => _sequenceDirectory;
			set => _sequenceDirectory = value;
		}

		/// <summary>
		/// Resolved sequence directory used by the current session (after parent-folder
		/// disambiguation). Set during InitializeSequenceAsync; null in non-sequence modes.
		/// </summary>
		public string ResolvedSequenceDirectory => _resolvedSequenceDirectory;
		private string _resolvedSequenceDirectory;

		/// <summary>
		/// Feed a frame with intrinsics and run tracking asynchronously (frees main thread).
		/// Call this from AR Foundation's OnCameraFrameReceived.
		/// Returns immediately - tracking runs on background thread.
		/// Check IsTrackingInProgress before feeding next frame.
		/// Poses are applied automatically when tracking completes.
		/// </summary>
		/// <param name="rgbData">RGB byte array (will be copied internally)</param>
		/// <param name="width">Frame width</param>
		/// <param name="height">Frame height</param>
		/// <param name="fuNorm">Normalized focal length U (fx / width)</param>
		/// <param name="fvNorm">Normalized focal length V (fy / height)</param>
		/// <param name="ppuNorm">Normalized principal point U (cx / width)</param>
		/// <param name="ppvNorm">Normalized principal point V (cy / height)</param>
		/// <param name="screenRotation">Screen orientation (use FTScreenRotation enum or GetScreenRotation())</param>
		/// <returns>True if frame was accepted, false if previous tracking still in progress or native locked</returns>
		public bool FeedFrameAsync(byte[] rgbData, int width, int height,
			float fuNorm, float fvNorm, float ppuNorm, float ppvNorm,
			int screenRotation = 0)
		{
			if (_imageSource != ImageSource.Injected || !_isInitialized || !_trackingReady) return false;

			// If previous tracking still in progress, skip this frame
			if (_injectedTrackingInProgress) return false;

			// Try to acquire native lock without waiting - skip frame if body registration in progress
			if (!_nativeAccessLock.Wait(0))
				return false;

			bool queuedStep = false;
			try
			{
				int requiredSize = width * height * 3;
				if (_injectedFrameBuffer == null || _injectedFrameBuffer.Length != requiredSize)
				{
					if (_injectedFrameHandleAllocated)
					{
						_injectedFrameHandle.Free();
						_injectedFrameHandleAllocated = false;
					}
					_injectedFrameBuffer = new byte[requiredSize];
				}
				Buffer.BlockCopy(rgbData, 0, _injectedFrameBuffer, 0, requiredSize);
				if (!_injectedFrameHandleAllocated)
				{
					_injectedFrameHandle = GCHandle.Alloc(_injectedFrameBuffer, GCHandleType.Pinned);
					_injectedFrameHandleAllocated = true;
				}

				if (!FrameWriteGuard.BeginFrame())
					return false;
				_fedScreenRotation = Mathf.Clamp(screenRotation, 0, 3);
				if (FrameWriteGuard.Record(
					FTBridge.FT_FeedFrame(_injectedFrameHandle.AddrOfPinnedObject(), width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation),
					"FT_FeedFrame"))
				{
					OnFrameFed?.Invoke(_injectedFrameHandle.AddrOfPinnedObject(), width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
				}
				if (!BeforeTrackingStep())
					return false;

				QueueNativeTrackingStep();
				queuedStep = true;
				_injectedTrackingInProgress = true;
				return true;
			}
			finally
			{
				if (!queuedStep)
				{
					_injectedTrackingInProgress = false;
					FrameWriteGuard.AbortFrame();
					_nativeAccessLock.Release();
				}
			}
		}

		/// <summary>
		/// Feed a frame with intrinsics and run tracking asynchronously using native pointer.
		/// The pointer must remain valid until IsTrackingInProgress becomes false.
		/// </summary>
		/// <param name="rgbData">Pointer to RGB data (must remain valid during tracking)</param>
		/// <param name="width">Frame width</param>
		/// <param name="height">Frame height</param>
		/// <param name="fuNorm">Normalized focal length U (fx / width)</param>
		/// <param name="fvNorm">Normalized focal length V (fy / height)</param>
		/// <param name="ppuNorm">Normalized principal point U (cx / width)</param>
		/// <param name="ppvNorm">Normalized principal point V (cy / height)</param>
		/// <param name="screenRotation">Screen orientation (use FTScreenRotation enum or GetScreenRotation())</param>
		/// <returns>True if frame was accepted, false if previous tracking still in progress or native locked</returns>
		public bool FeedFrameAsync(IntPtr rgbData, int width, int height,
			float fuNorm, float fvNorm, float ppuNorm, float ppvNorm,
			int screenRotation = 0)
		{
			if (_imageSource != ImageSource.Injected || !_isInitialized || !_trackingReady) return false;

			// If previous tracking still in progress, skip this frame
			if (_injectedTrackingInProgress) return false;

			// Try to acquire native lock without waiting - skip frame if body registration in progress
			if (!_nativeAccessLock.Wait(0))
				return false;

			bool queuedStep = false;
			try
			{
				if (!FrameWriteGuard.BeginFrame())
					return false;
				_fedScreenRotation = Mathf.Clamp(screenRotation, 0, 3);
				if (FrameWriteGuard.Record(
					FTBridge.FT_FeedFrame(rgbData, width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation),
					"FT_FeedFrame"))
				{
					OnFrameFed?.Invoke(rgbData, width, height,
						fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
				}
				if (!BeforeTrackingStep())
					return false;

				QueueNativeTrackingStep();
				queuedStep = true;
				_injectedTrackingInProgress = true;
				return true;
			}
			finally
			{
				if (!queuedStep)
				{
					_injectedTrackingInProgress = false;
					FrameWriteGuard.AbortFrame();
					_nativeAccessLock.Release();
				}
			}
		}

		/// <summary>
		/// Feed a depth frame to the injected depth camera and fire OnDepthFrameFed.
		/// </summary>
		public void FeedDepthFrame(IntPtr depthData, int width, int height)
		{
			if (!_isInitialized) return;

			WaitForTrackingStepToReturn();
			if (DepthFrameFeed.ShouldNotify(
				FTBridge.FT_FeedDepthFrame(depthData, width, height)))
			{
				OnDepthFrameFed?.Invoke(depthData, width, height, 0f);
			}
		}

		/// <summary>
		/// Enable depth frame forwarding from C++ to Unity (for recording).
		/// Registers the native depth callback so FT_TrackStep queues depth frames.
		/// Call DisableDepthForwarding() when done to avoid unnecessary overhead.
		/// </summary>
		public void EnableDepthForwarding()
		{
			if (_depthImageCallbackDelegate == null)
				_depthImageCallbackDelegate = OnDepthImageReceived;

			WaitForTrackingStepToReturn();
			FTBridge.FT_SetDepthImageCallback(_depthImageCallbackDelegate, IntPtr.Zero);
		}

		/// <summary>
		/// Disable depth frame forwarding. Clears the native depth callback
		/// so FT_TrackStep skips the ~800KB/frame depth buffer copy.
		/// </summary>
		public void DisableDepthForwarding()
		{
			WaitForTrackingStepToReturn();
			FTBridge.FT_SetDepthImageCallback(null, IntPtr.Zero);
		}

		/// <summary>
		/// Get the current screen rotation mode for use with FeedFrame methods.
		/// </summary>
		/// <returns>Screen rotation value (0-3) for FT_FeedFrame</returns>
		public static int GetScreenRotation()
		{
			return Screen.orientation switch
			{
				ScreenOrientation.LandscapeLeft => (int)FTScreenRotation.LandscapeLeft,
				ScreenOrientation.Portrait => (int)FTScreenRotation.Portrait,
				ScreenOrientation.LandscapeRight => (int)FTScreenRotation.LandscapeRight,
				ScreenOrientation.PortraitUpsideDown => (int)FTScreenRotation.PortraitUpsideDown,
				_ => (int)FTScreenRotation.LandscapeLeft // Default to landscape left
			};
		}

		public void EnumerateCameras()
		{
			// Always create fresh array to avoid stale data
			var tempCameras = new FTCameraDevice[10];
			int count = FTBridge.FT_EnumerateCameras(tempCameras, 10);

			// Resize to actual count
			_availableCameras = new FTCameraDevice[count];
			Array.Copy(tempCameras, _availableCameras, count);

			OnCamerasEnumerated?.Invoke(_availableCameras);
		}

		/// <summary>
		/// Find camera index by name (case-insensitive exact match).
		/// </summary>
		/// <param name="cameraName">Camera name to search for</param>
		/// <returns>Camera index if found, -1 otherwise</returns>
		public int FindCameraByName(string cameraName)
		{
			if (_availableCameras == null || string.IsNullOrEmpty(cameraName))
				return -1;

			// Exact match (case-insensitive)
			for (int i = 0; i < _availableCameras.Length; i++)
			{
				if (string.Equals(_availableCameras[i].name, cameraName, StringComparison.OrdinalIgnoreCase))
					return i;
			}

			return -1;
		}

		public async Task SelectCameraAsync(int cameraIndex)
		{
			if (_availableCameras == null || cameraIndex < 0 || cameraIndex >= _availableCameras.Length)
			{
				Debug.LogError($"[XRTracker] Invalid camera index: {cameraIndex}");
				return;
			}

			await ShutdownPreviousSessionAsync();

			_selectedCameraIndex = cameraIndex;
			FTCameraDevice cam = _availableCameras[cameraIndex];
			OnCameraSelected?.Invoke(cam);
			await InitializeWithCameraAsync(cam);
		}

		public void PauseTracking()
		{
			if (!_trackingReady) return;

			ExitRecoverySynchronousStepMode();
			_trackingReady = false;
			// Coroutine will exit on next iteration when it checks _trackingReady
			OnTrackingPaused?.Invoke();
		}

		public void ResumeTracking()
		{
			if (_trackingReady) return;

			_trackingReady = true;
			OnTrackingResumed?.Invoke();
			if (_imageSource == ImageSource.Native && _trackingCoroutine == null)
			{
				_trackingCoroutine = StartCoroutine(TrackingCoroutine());
			}
		}

		public void StartDetection()
		{
			WaitForTrackingStepToReturn();
			FTBridge.FT_ExecuteDetection();
			_hasExecutedDetection = true;
			// Detection returns every body to judging its seat as placed.
			foreach (TrackedBody body in _trackedBodies)
			{
				if (body != null)
					body.ForgetRefinementBeforeAcceptance();
			}
		}

		public void StartTracking()
		{
			WaitForTrackingStepToReturn();
			FTBridge.FT_StartTracking();
		}

		/// <summary>
		/// Enables synchronous dispatch only for a paused recovery sequence after an
		/// ordinary replay has completed. It refuses any source other than a settled
		/// sequence step, so it cannot alter an ordinary in-flight dispatch.
		/// </summary>
		internal bool TryEnterRecoverySynchronousStepMode()
		{
			if (!RecoverySynchronousStepMode.CanEnter(
					_isInitialized, _trackingReady, _trackingStepComplete,
					_imageSource == ImageSource.Sequence, _recoverySynchronousStepMode))
			{
				return false;
			}
			_recoverySynchronousStepMode = true;
			return true;
		}

		/// <summary>
		/// Restores normal asynchronous sequence dispatch. It is idempotent so all
		/// terminal paths can call it without depending on their predecessor.
		/// </summary>
		internal void ExitRecoverySynchronousStepMode()
		{
			_recoverySynchronousStepMode = false;
		}

		/// <summary>
		/// True while a host owns sequence playback frame by frame and this
		/// manager's own loop dispatches nothing.
		/// </summary>
		internal bool DeterministicReplayOwnsPlayback => _deterministicReplayOwnsPlayback;

		/// <summary>Tracking steps this manager has dispatched.</summary>
		internal long DispatchSerial => _dispatchSerial;

		/// <summary>Dispatch completions this manager has processed.</summary>
		internal long CompletionSerial => _completionSerial;

		/// <summary>Completed steps this manager has published to subscribers.</summary>
		internal long PublicationSerial => _publicationSerial;

		/// <summary>
		/// The sequence frame the most recent dispatch was opened for, or -1 for a
		/// source that has no sequence frame.
		/// </summary>
		internal int DispatchedSequenceFrame => _dispatchedSequenceFrame;

		private int SequenceFrameForDispatch() =>
			_imageSource == ImageSource.Sequence
				? FTBridge.FT_GetSequenceCurrentFrame()
				: -1;

		/// <summary>
		/// Returns the sequence frame the step being dispatched is opened for, and starts a new playback pass first when that frame
		/// is the start of a wrap of the loop or a repeat of a single frame. Only the manager's own loop calls it, so a host that
		/// drives the frames itself keeps the frame order it chose.
		/// </summary>
		private int OpenSequenceDispatch()
		{
			int frame = SequenceFrameForDispatch();
			if (SequencePassBoundary.OpensNewPass(
				_imageSource == ImageSource.Sequence, FTBridge.SequenceLoop, _dispatchedSequenceFrame, frame))
			{
				FTBridge.BeginSequencePass();
			}
			return frame;
		}

		/// <summary>
		/// Hands sequence playback to a host that drives one named frame at a time.
		/// </summary>
		/// <remarks>
		/// <para>Ordinary playback lets the camera decide the next frame and lets
		/// this manager's loop decide when to ask for it. Both decisions depend on
		/// elapsed time, so the number of tracking steps a recorded frame receives
		/// is not a property of the recording. A host that needs the same recording
		/// to produce the same trajectory takes both decisions itself through this
		/// seam.</para>
		/// <para>This is deliberately not the recovery synchronous mode. That mode
		/// exists to keep one read right after the step on the tracking thread for
		/// a phase that is already paused, and it hands dispatch straight back;
		/// this one suppresses dispatch entirely for as long as the host holds it.</para>
		/// <para>Detection is executed here if it has not run yet, so no driven
		/// frame is the one that first executes it.</para>
		/// </remarks>
		/// <returns>False when the source is not a sequence, the manager is not
		/// initialized, a step is in flight, or a mode already owns dispatch.</returns>
		/// <summary>
		/// Stops this manager's own loop from dispatching, before it has one.
		/// </summary>
		/// <remarks>
		/// The loop is started as part of initialization and runs its first
		/// iteration immediately, which for a recorded sequence consumes a frame
		/// before anything outside the manager has had a chance to say otherwise.
		/// A host that intends to drive every frame declares that here, while the
		/// manager is still uninitialized, so frame zero of the recording is the
		/// first frame the tracker ever sees.
		/// </remarks>
		internal void HoldPlaybackForDeterministicReplay()
		{
			_deterministicReplayHold = true;
		}

		internal bool TryEnterDeterministicReplayMode()
		{
			if (!_isInitialized || _imageSource != ImageSource.Sequence)
				return false;
			if (_deterministicReplayOwnsPlayback || _recoverySynchronousStepMode)
				return false;
			if (!_trackingStepComplete)
				return false;

			_deterministicReplayOwnsPlayback = true;
			if (!_hasExecutedDetection)
			{
				WaitForTrackingStepToReturn();
				FTBridge.FT_ExecuteDetection();
				_hasExecutedDetection = true;
			}
			// Nothing may advance the recording between driven frames.
			FTBridge.FT_SequencePause();
			return true;
		}

		/// <summary>
		/// Returns sequence playback to this manager's own loop. Idempotent.
		/// </summary>
		internal void ExitDeterministicReplayMode()
		{
			_deterministicReplayHold = false;
			if (!_deterministicReplayOwnsPlayback)
				return;
			_deterministicReplayOwnsPlayback = false;
			FTBridge.FT_SequencePlay();
		}

		/// <summary>
		/// Drives exactly one tracking step on one named sequence frame.
		/// </summary>
		/// <remarks>
		/// The camera is moved onto the frame before the step, so the step cannot read a
		/// different one, and the step's account is inferred afterwards from the camera's
		/// cursor, so the caller can check what it read. Everything between those two
		/// points is the ordinary path: the same frame-write guard, the same
		/// before-step, the same completion and the same publication that live capture
		/// uses.
		/// </remarks>
		internal DeterministicReplayStepOutcome ExecuteDeterministicReplayStep(int frameIndex)
		{
			if (!_deterministicReplayOwnsPlayback)
				return DeterministicReplayStepOutcome.Refused("no host owns playback");
			if (!_isInitialized || _imageSource != ImageSource.Sequence)
				return DeterministicReplayStepOutcome.Refused("the source is not a sequence");
			if (!_nativeAccessLock.Wait(0))
				return DeterministicReplayStepOutcome.Refused("native access is held elsewhere");

			bool dispatchedStep = false;
			bool cameraMoved = false;
			try
			{
				if (!FrameWriteGuard.BeginFrame())
					return DeterministicReplayStepOutcome.Refused("the frame write guard refused the frame");
				cameraMoved = true;
				if (!FTBridge.SeekSequenceForDrivenStep(frameIndex))
					return DeterministicReplayStepOutcome.Refused($"frame {frameIndex} is outside the recording");
				int cursorBefore = FTBridge.FT_GetSequenceCurrentFrame();
				if (!BeforeTrackingStep())
					return DeterministicReplayStepOutcome.Refused("the before-step refused the frame");

				++_dispatchSerial;
				_dispatchedSequenceFrame = frameIndex;
				long dispatchSerial = _dispatchSerial;

				int result = FTBridge.FT_TrackStep();
				dispatchedStep = true;
				_trackingStepResult = result;
				// The plugin states no account of which frame a driven step read, so the account is
				// inferred from the camera's cursor, which the ledger can still contradict.
				FTTrackStepToken token = SequenceStepAccount.Infer(
					dispatchSerial, frameIndex, cursorBefore, FTBridge.FT_GetSequenceCurrentFrame(), result);

				bool published = CompleteNativeTrackingStep(result);
				FTBridge.FT_ProcessImageCallback();
				FTBridge.FT_ProcessDepthImageCallback();
				return DeterministicReplayStepOutcome.Executed(
					dispatchSerial, _completionSerial, _publicationSerial, result, published, token);
			}
			finally
			{
				if (!dispatchedStep)
					FrameWriteGuard.AbortFrame();
				// The moved camera is unpaused to read its one frame. Nothing may advance the
				// recording between driven frames, so it is held again whichever way this ended.
				if (cameraMoved)
					FTBridge.FT_SequencePause();
				_nativeAccessLock.Release();
			}
		}

		/// <summary>
		/// Prevents another coroutine dispatch after the current tracking step
		/// returns. Unlike <see cref="PauseTracking"/>, it deliberately leaves the
		/// recovery dispatcher enabled until that step has released native access.
		/// </summary>
		internal void RequestStopAfterCurrentTrackingStep()
		{
			_trackingReady = false;
		}

		/// <summary>
		/// Register a TrackedBody for quality-based state management.
		/// Called automatically by TrackedBody when registered.
		/// </summary>
		public void RegisterTrackedBody(TrackedBody body)
		{
			if (!_trackedBodies.Contains(body))
			{
				_trackedBodies.Add(body);
				OnBodyRegistered?.Invoke(body);
			}
		}

		/// <summary>
		/// Unregister a TrackedBody.
		/// Called automatically by TrackedBody when unregistered.
		/// </summary>
		public void UnregisterTrackedBody(TrackedBody body)
		{
			int registeredBefore = CountRegisteredTrackedBodies();
			if (_trackedBodies.Remove(body))
			{
				int registeredAfter = CountRegisteredTrackedBodies();
				if (CompletedFrameRegistrationBoundary.ClearsDormantOrderState(
					registeredBefore, registeredAfter))
				{
					_completedFrameClock.Reset(FTBridge.SequenceSeekEpoch);
					TrackedBodyManager.Instance?.ResetCompletedFrameOrderState();
				}
				OnBodyUnregistered?.Invoke(body);
			}
		}

		private int CountRegisteredTrackedBodies()
		{
			int count = 0;
			foreach (TrackedBody body in _trackedBodies)
			{
				if (body != null && body.IsRegistered)
					count++;
			}
			return count;
		}

		private IReadOnlyList<TrackedBody> CaptureCompletedStepRegisteredBodies()
		{
			_completedStepRegisteredBodies.Clear();
			foreach (TrackedBody body in _trackedBodies)
			{
				if (body != null && body.IsRegistered)
					_completedStepRegisteredBodies.Add(body);
			}
			return _completedStepRegisteredBodies;
		}

		/// <summary>
		/// Acquires the native access lock asynchronously. Call ReleaseNativeLock when done.
		/// Use this before calling native registration/unregistration functions to ensure
		/// no tracking is in progress.
		/// </summary>
		public Task AcquireNativeLockAsync()
		{
			return _nativeAccessLock.WaitAsync();
		}

		/// <summary>
		/// Releases the native access lock. Must be called after AcquireNativeLockAsync.
		/// </summary>
		public void ReleaseNativeLock()
		{
			_nativeAccessLock.Release();
		}

		internal bool TryPublishTrackedBodyRegistration(
			TrackedBody body,
			Func<bool> lifecycleIsCurrent,
			Action markRegistered,
			Action cancelNativeRegistration)
		{
			if (body == null || lifecycleIsCurrent == null || markRegistered == null ||
				cancelNativeRegistration == null)
				return false;
			if (!IsManagerThread || _nativeAccessLock.CurrentCount != 0)
				return false;
			if (!lifecycleIsCurrent())
			{
				cancelNativeRegistration();
				return false;
			}

			markRegistered();
			RegisterTrackedBody(body);
			return true;
		}

		/// <summary>
		/// Called before native tracking step on all registered bodies.
		/// </summary>
		public bool BeforeTrackingStep()
		{
			// Injected feeders open the decision before FT_FeedFrame. Native and
			// sequence capture reach here first, so open theirs here instead.
			bool readyForStep = FrameWriteGuard.BeginFrameIfNeeded();
			try
			{
			if (!readyForStep)
				return false;

			// Feed the AR camera pose to the native tracker, in the AR session's world frame.
#if HAS_AR_FOUNDATION
			if (UseARPoseFusion)
			{
				int expectedFrameIndex = _imageSource == ImageSource.Sequence
					? FTBridge.FT_GetSequenceCurrentFrame()
					: _nextLiveFusionFrameIndex++;
				long timestampNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000.0);
				bool hasObservation;
				FusionFrameObservation observation = default;
				if (_imageSource == ImageSource.Sequence)
				{
					hasObservation = Recording.SequenceARPlaybackBinder.TryGetObservation(
						expectedFrameIndex,
						timestampNs,
						out observation);
				}
				else
				{
					EnsureLiveFusionObservationSource();
					hasObservation = _liveFusionObservationSource != null &&
						_liveFusionObservationSource.TryGetObservation(
							expectedFrameIndex,
							timestampNs,
							out observation);
				}

				if (!hasObservation)
				{
					observation = new FusionFrameObservation(
						_imageSource == ImageSource.Sequence
							? FusionObservationSource.Recorded
							: FusionObservationSource.Live,
						string.Empty,
						expectedFrameIndex,
						timestampNs,
						false,
						Matrix4x4.identity,
						UnityEngine.XR.ARFoundation.ARSessionState.None,
						UnityEngine.XR.ARSubsystems.NotTrackingReason.None,
						string.Empty,
						string.Empty,
						FusionObservationFailure.SourceUnavailable);
				}

				FusionFrameContext context = _fusionFrameContextResolver.Resolve(observation);
				_submittedFusionFrameContext = context;
				if (!context.IsValid)
				{
					ReportFusionObservationRejection(context);
					readyForStep = false;
				}
				else
				{
					_lastRejectedFusionFrameIndex = int.MinValue;
					_lastRejectedFusionFailure = FusionObservationFailure.None;

					if (!FusionCameraPoseSubmission.TrySubmit(context, _fusionCameraPoseSink))
					{
						readyForStep = false;
					}
					else
					{
						_colorCameraPose = context.NativeCameraPose;
						CarryBodiesWhenTheSessionMovesItsWorld(context);
					}
				}
			}
#endif

			if (readyForStep)
			{
				TurnDepthCameraToScreen();

				foreach (var body in _trackedBodies)
				{
					if (body != null && body.IsRegistered)
					{
						body.BeforeTrackingStep();
					}
				}

#if HAS_AR_FOUNDATION
				// Snapshot the AR session state per frame on the main thread. The writer
				// thread picks one of these up when each frame is committed to disk.
				if (Recording.ARSessionStateWriter.IsActive)
					Recording.ARSessionStateWriter.CaptureSnapshot();
#endif

				// Notify subscribers
				OnBeforeTrackingStep?.Invoke();
			}
		}
		finally
		{
			// Always close the decision. This includes rejected fusion observations
			// and exceptions from subscribers, so a failure cannot leak into the
			// next frame.
			if (FrameWriteGuard.ConsumeFailure())
				readyForStep = false;
		}

		return readyForStep;
		}

		/// <summary>
		/// Turns the injected depth camera about its optical axis to the screen rotation of the colour frame just fed.
		/// </summary>
		/// <remarks>
		/// The plugin turns the colour image clockwise by one quarter turn for portrait, two for landscape right and three for portrait
		/// upside down, and the camera coordinates turn with it by the same angle about the optical axis. The depth image keeps the
		/// orientation of the sensor, so the depth camera sits that turn away from the colour camera. The plugin keeps the pose of the depth
		/// camera in the colour camera from the pose written here, so the turn is composed with the colour camera pose it holds.
		/// </remarks>
		private void TurnDepthCameraToScreen()
		{
			if (!_injectedDepthCamera || !UseInjectedCamera() || _depthCameraRotation == _fedScreenRotation)
				return;

			float halfAngle = _fedScreenRotation switch
			{
				1 => 45f,
				2 => 90f,
				3 => -45f,
				_ => 0f
			} * Mathf.Deg2Rad;
			var turn = new Quaternion(0f, 0f, Mathf.Sin(halfAngle), Mathf.Cos(halfAngle));
			var color = new Quaternion(_colorCameraPose.rot_x, _colorCameraPose.rot_y, _colorCameraPose.rot_z, _colorCameraPose.rot_w);
			Quaternion depth = color * turn;

			FTTrackingPose depthPose = _colorCameraPose;
			depthPose.rot_x = depth.x;
			depthPose.rot_y = depth.y;
			depthPose.rot_z = depth.z;
			depthPose.rot_w = depth.w;

			// An unusable colour pose is retried with the next frame's pose. Any other result settles this rotation.
			if (FTBridge.FT_SetDepthCameraPose(ref depthPose) != FTErrorCode.INVALID_PARAMETER)
				_depthCameraRotation = _fedScreenRotation;
		}

#if HAS_AR_FOUNDATION
		/// <summary>
		/// Watches the camera pose of the step that is about to run, and when the AR session has moved its world since the step before, carries
		/// every body to the new frame, so the tracker steps on a pose that is still in front of the camera. The jump counts as no camera motion,
		/// because every pose that the lifecycle compares the camera with is carried by the same map.
		/// </summary>
		/// <remarks>
		/// The watch takes the camera pose of every step, also while Follow AR World Jumps is off. The bodies are carried or not by
		/// <see cref="TrackedBodyManager.CarryBodiesToNewWorld(Pose)"/> after the watch has looked, so turning the setting on during a session
		/// compares the next step with the step before it and not with the last step from before it was turned off.
		/// </remarks>
		private void CarryBodiesWhenTheSessionMovesItsWorld(FusionFrameContext context)
		{
			Matrix4x4 worldFromCamera = context.WorldFromCameraUnity;
			if (!_worldJumpWatch.Observe(worldFromCamera.GetPosition(), worldFromCamera.rotation, context.TimestampNs,
					FTBridge.SequenceSeekEpoch, out Pose carry))
				return;
			TrackedBodyManager bodies = TrackedBodyManager.Instance;
			if (bodies != null && bodies.CarryBodiesToNewWorld(carry))
				Debug.Log("[XRTracker] The AR session moved its world, so the tracked bodies were carried over to it.");
		}

		private void EnsureLiveFusionObservationSource()
		{
			IARSpaceProvider provider = TrackedBodyManager.Instance?.ARPoseFusion?.CurrentProvider;
			if (provider == null || ReferenceEquals(provider, _liveFusionObservationProvider))
				return;
			_liveFusionObservationProvider = provider;
			_liveFusionObservationSource = new LiveFusionFrameObservationSource(
				() => _cameraTransform,
				provider);
		}

		private void ReportFusionObservationRejection(FusionFrameContext context)
		{
			if (_lastRejectedFusionFrameIndex != context.FrameIndex ||
				_lastRejectedFusionFailure != context.Failure)
			{
				Debug.LogError($"[XRTracker] Fusion observation rejected at frame " +
					$"{context.FrameIndex}: {context.Failure}");
				_lastRejectedFusionFrameIndex = context.FrameIndex;
				_lastRejectedFusionFailure = context.Failure;
			}
			if (_imageSource == ImageSource.Sequence)
			{
				if (!FTBridge.FT_SequenceSkipRejectedFrame())
					_trackingReady = false;
			}
		}
#endif

		private bool TryPublishCompletedTrackingStep()
		{
			IReadOnlyList<TrackedBody> registeredBodies =
				CaptureCompletedStepRegisteredBodies();
			int registeredBodyCount = registeredBodies.Count;
#if HAS_AR_FOUNDATION
			FusionFrameContext? candidateFusionFrameContext =
				_submittedFusionFrameContext;
			CompletedFrameStatusToken candidateStatusToken = registeredBodyCount > 0
				? BuildCompletedFrameStatusToken(candidateFusionFrameContext)
				: default;
#else
			CompletedFrameStatusToken candidateStatusToken = default;
#endif
			bool clockValid = TryLatchCompletedFrameClock(
#if HAS_AR_FOUNDATION
				candidateFusionFrameContext,
#endif
				registeredBodyCount,
				out CompletedFrameClockSample clockSample,
				out CompletedFrameClock pendingRecordedClock,
				out string clockFailure);
			if (!clockValid)
			{
				Debug.LogError($"[XRTracker] Completed-frame clock rejected the step: " +
					clockFailure + ". " + DescribeLastStep());
				if (_imageSource == ImageSource.Sequence)
					_trackingReady = false;
				return false;
			}

			TrackedBodyManager bodyManager = TrackedBodyManager.Instance;
			int refreshedBodyCount = 0;
			bool refreshSucceeded;
			if (registeredBodyCount == 0)
			{
				refreshSucceeded = true;
			}
			else if (bodyManager == null)
			{
				refreshSucceeded = false;
			}
			else
			{
				refreshSucceeded = bodyManager.TryRefreshCompletedBodyStatuses(
					registeredBodies,
					candidateStatusToken,
					clockSample,
					out refreshedBodyCount);
			}
			if (!refreshSucceeded || !CompletedFramePublicationContract.CanPublish(
				true, registeredBodyCount, refreshedBodyCount))
			{
				Debug.LogError($"[XRTracker] Completed frame refreshed {refreshedBodyCount} " +
					$"of {registeredBodyCount} registered body statuses; publication stopped.");
				if (_imageSource == ImageSource.Sequence)
					_trackingReady = false;
				return false;
			}
			if (registeredBodyCount > 0)
				_completedFrameClock.Commit(pendingRecordedClock);

#if HAS_AR_FOUNDATION
			CompletedFrameContextCommitContract.CommitIfValid(clockValid, () =>
			{
				_completedFusionFrameContext = candidateFusionFrameContext;
				_submittedFusionFrameContext = null;
			});
#endif

			TrackingFrameInstrumentationContext instrumentationContext =
				BuildTrackingFrameInstrumentationContext(clockSample, registeredBodyCount,
					refreshedBodyCount);
			bodyManager?.SetCompletedFrameInstrumentationContext(instrumentationContext);

			foreach (TrackedBody body in registeredBodies)
			{
				body.AfterTrackingStep();
			}

			++_publicationSerial;
			OnRawTrackingStepCompleted?.Invoke(instrumentationContext);

			// Notify subscribers (TrackedBodyManager uses this for non-SLAM pose application)
			OnAfterTrackingStep?.Invoke();
			OnTrackingLifecycleCompleted?.Invoke(instrumentationContext);
			return true;
		}

		/// <summary>
		/// What the last native tracking step did, for a message about a refusal.
		/// </summary>
		/// <remarks>
		/// A refusal that says only which rule was broken leaves the reader to guess
		/// which of several very different things happened -- the recording ended,
		/// the camera stalled, a step ran on a frame it had already read. The camera
		/// publishes a token saying exactly what the step did, so the message says it
		/// rather than leaving it to be guessed.
		/// </remarks>
		private static string DescribeLastStep()
		{
			if (!FTBridge.FT_GetLastTrackStepToken(out FTTrackStepToken token))
				return "The camera published no token for it.";
			return $"The step read {token.imagesLoaded} frame(s): " +
				$"requested {token.requestedFrame}, loaded {token.loadedFrame}, " +
				$"next-read {token.frameIndexBefore} -> {token.frameIndexAfter}, " +
				$"loads {token.imageLoadSerial}, step {token.stepSerial}, " +
				$"result {token.result}, paused {token.cameraPaused}, " +
				$"pending-step {token.cameraPendingStep}, " +
				$"has-loaded-frame {token.cameraHasLoadedFrame}.";
		}

		private bool TryLatchCompletedFrameClock(
#if HAS_AR_FOUNDATION
			FusionFrameContext? candidateFusionFrameContext,
#endif
			int registeredBodyCount,
			out CompletedFrameClockSample sample,
			out CompletedFrameClock pendingRecordedClock,
			out string failure)
		{
			pendingRecordedClock = null;
			if (!CompletedFrameRegistrationBoundary.UsesRecordedClock(
				_imageSource == ImageSource.Sequence,
				HasRecordedFusionObservationSource,
				registeredBodyCount))
			{
				sample = CompletedFrameClock.LatchLive(Time.deltaTime);
				failure = string.Empty;
				return true;
			}

#if HAS_AR_FOUNDATION
			bool hasTimestamp = candidateFusionFrameContext.HasValue &&
				candidateFusionFrameContext.Value.IsValid;
			long timestampNs = hasTimestamp
				? candidateFusionFrameContext.Value.TimestampNs
				: 0;
#else
			bool hasTimestamp = false;
			long timestampNs = 0;
#endif
			return _completedFrameClock.TryPreviewRecorded(
				hasTimestamp,
				timestampNs,
				FTBridge.SequenceSeekEpoch,
				out sample,
				out failure,
				out pendingRecordedClock);
		}

#if HAS_AR_FOUNDATION
		private static CompletedFrameStatusToken BuildCompletedFrameStatusToken(
			FusionFrameContext? candidate)
		{
			if (!candidate.HasValue || !candidate.Value.IsValid)
				return default;
			FusionFrameContext context = candidate.Value;
			return new CompletedFrameStatusToken(
				true,
				context.Observation.SequenceId,
				context.FrameIndex,
				context.TimestampNs);
		}
#endif

		/// <summary>
		/// The frame context for the step that is about to run, for an instrument
		/// that has to annotate what the step is about to do.
		/// </summary>
		/// <remarks>
		/// The two completed events carry the frame that has just finished, which
		/// is the right frame for anything describing a result. An instrument that
		/// stamps a reference the step itself will consume needs the frame the step
		/// is about to submit, and reading it from a completed event stamps it one
		/// frame late — an error that is a full frame of camera motion, is frozen
		/// into whatever it annotates, and is largest exactly where the camera
		/// moves fastest.
		/// <para>
		/// This exposes the submitted context and nothing else: the fusion context
		/// type stays internal, and the record returned is the same one the
		/// completed events already publish. It carries no clock sample and no body
		/// counts, because neither is known before the step has run, and reporting a
		/// stale one would be the same mistake one level down.
		/// </para>
		/// <para>
		/// Returns false when no submitted context is available, so a caller
		/// refuses rather than stamping an identity it did not earn.
		/// </para>
		/// </remarks>
		internal bool TryGetSubmittedFrameInstrumentationContext(
			out TrackingFrameInstrumentationContext context)
		{
#if HAS_AR_FOUNDATION
			if (_submittedFusionFrameContext.HasValue)
			{
				FusionFrameContext submitted = _submittedFusionFrameContext.Value;
				context = new TrackingFrameInstrumentationContext(
					submitted.IsValid,
					submitted.Observation.SequenceId,
					submitted.FrameIndex,
					submitted.Observation.TimestampNs,
					submitted.Observation.Source.ToString(),
					WORLD_TRACKING_SPACE,
					submitted.Observation.SessionSpaceIdentity,
					submitted.Observation.SessionSpaceEpoch,
					ExplicitRecoveryCoordinateEpoch(submitted),
					submitted.Observation.WorldFromCameraUnity,
					Matrix4x4.identity,
					default,
					0,
					0);
				return true;
			}
#endif
			return TryGetSequenceFrameIdentity(out context);
		}

		/// <summary>
		/// The identity a replayed frame carries when no fusion observation was
		/// submitted for it.
		/// </summary>
		/// <remarks>
		/// <para>A replay driven from a recorded sequence has a frame, a tracking
		/// space and a camera, and it has them whether or not the pose-fusion path
		/// is running. The space the tracker solves in is the world the scene already
		/// places the camera in, and the transform between the two is the identity
		/// rather than a value that has to be looked up. The frame index comes from the sequence itself, which is the
		/// one identity a replayed frame genuinely has, and the sequence directory
		/// names the space so two sequences never share one.</para>
		/// <para>No wall clock is claimed. The recording carries none the manager can
		/// read here, and a stamp taken from this machine's clock would make the same
		/// replay produce a different record every run, so the timestamp is reported
		/// as zero: present, ordered by the frame index beside it, and not pretending
		/// to be a capture time.</para>
		/// </remarks>
		private bool TryGetSequenceFrameIdentity(
			out TrackingFrameInstrumentationContext context)
		{
			context = default;
			if (_imageSource != ImageSource.Sequence)
				return false;
			int frameIndex = FTBridge.FT_GetSequenceCurrentFrame();
			if (frameIndex < 0)
				return false;
			Camera camera = MainCamera;
			Matrix4x4 worldFromCamera = camera != null
				? Matrix4x4.TRS(camera.transform.position, camera.transform.rotation,
					Vector3.one)
				: Matrix4x4.identity;
			string identity = string.IsNullOrEmpty(_sequenceDirectory)
				? "sequence" : _sequenceDirectory;
			context = new TrackingFrameInstrumentationContext(
				true,
				identity,
				frameIndex,
				0,
				"Sequence",
				"World",
				identity,
				0,
				unchecked((int)FTBridge.SequenceSeekEpoch),
				worldFromCamera,
				Matrix4x4.identity,
				default,
				0,
				0);
			return true;
		}

		/// <summary>
		/// The completed frame's identity, for a caller that has to check the space a
		/// pose was requested in against the space it is about to be applied in.
		/// </summary>
		/// <remarks>
		/// The submitted and the completed identity come from the same two sources in
		/// the same order, so a saved view and a confirmation are stamped with one
		/// space and checked against it. Reading them from different sources would
		/// refuse every confirmation on a session where only one of the two ever had
		/// an observation.
		/// </remarks>
		internal bool TryGetCompletedFrameIdentity(
			out TrackingFrameInstrumentationContext context)
		{
#if HAS_AR_FOUNDATION
			if (_completedFusionFrameContext.HasValue)
			{
				FusionFrameContext completed = _completedFusionFrameContext.Value;
				context = new TrackingFrameInstrumentationContext(
					completed.IsValid,
					completed.Observation.SequenceId,
					completed.FrameIndex,
					completed.Observation.TimestampNs,
					completed.Observation.Source.ToString(),
					WORLD_TRACKING_SPACE,
					completed.Observation.SessionSpaceIdentity,
					completed.Observation.SessionSpaceEpoch,
					ExplicitRecoveryCoordinateEpoch(completed),
					completed.Observation.WorldFromCameraUnity,
					Matrix4x4.identity,
					default,
					0,
					0);
				return completed.IsValid;
			}
#endif
			return TryGetSequenceFrameIdentity(out context);
		}

		private TrackingFrameInstrumentationContext BuildTrackingFrameInstrumentationContext(
			CompletedFrameClockSample clockSample, int registeredBodyCount,
			int refreshedBodyCount)
		{
#if HAS_AR_FOUNDATION
			if (_completedFusionFrameContext.HasValue)
			{
				FusionFrameContext context = _completedFusionFrameContext.Value;
				return new TrackingFrameInstrumentationContext(
					context.IsValid,
					context.Observation.SequenceId,
					context.FrameIndex,
					context.Observation.TimestampNs,
					context.Observation.Source.ToString(),
					WORLD_TRACKING_SPACE,
					context.Observation.SessionSpaceIdentity,
					context.Observation.SessionSpaceEpoch,
					ExplicitRecoveryCoordinateEpoch(context),
					context.Observation.WorldFromCameraUnity,
					Matrix4x4.identity,
					clockSample,
					registeredBodyCount,
					refreshedBodyCount);
			}
#endif
			int frameIndex = _imageSource == ImageSource.Sequence
				? FTBridge.FT_GetSequenceCurrentFrame()
				: -1;
			return new TrackingFrameInstrumentationContext(
				false,
				string.Empty,
				frameIndex,
				0,
				string.Empty,
				string.Empty,
				string.Empty,
				0,
				unchecked((int)FTBridge.SequenceSeekEpoch),
				Matrix4x4.identity,
				Matrix4x4.identity,
				clockSample,
				registeredBodyCount,
				refreshedBodyCount);
		}

#if HAS_AR_FOUNDATION
		private static int ExplicitRecoveryCoordinateEpoch(FusionFrameContext context)
		{
			return unchecked((context.Observation.SessionSpaceEpoch * 397) ^
				((int)FTBridge.SequenceSeekEpoch * 31));
		}
#endif

		private bool CompleteNativeTrackingStep(int result)
		{
			++_completionSerial;
			if (result == 0)
			{
				return TryPublishCompletedTrackingStep();
			}

#if HAS_AR_FOUNDATION
			if (_submittedFusionFrameContext.HasValue)
			{
				Debug.LogError($"[XRTracker] Native tracking rejected submitted frame " +
					$"{_submittedFusionFrameContext.Value.FrameIndex} with result {result}");
			}
			else
			{
				Debug.LogError($"[XRTracker] Native tracking step failed with result {result}");
			}
			int submittedFrame = _submittedFusionFrameContext.HasValue
				? _submittedFusionFrameContext.Value.FrameIndex
				: int.MinValue;
			_submittedFusionFrameContext = null;
			if (_imageSource == ImageSource.Sequence &&
				FTBridge.FT_GetSequenceCurrentFrame() == submittedFrame &&
				!FTBridge.FT_SequenceSkipRejectedFrame())
			{
				_trackingReady = false;
			}
#else
			Debug.LogError($"[XRTracker] Native tracking step failed with result {result}");
#endif
			return false;
		}

		internal bool CompleteNativeTrackingStepFromUpdate(int result)
		{
			return CompleteNativeTrackingStep(result);
		}

		internal bool CompleteNativeTrackingStepFromCoroutine(int result)
		{
			return CompleteNativeTrackingStep(result);
		}

		private void QueueNativeTrackingStep()
		{
			_trackingStepResult = int.MinValue;
			_trackingStepComplete = false;
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					_trackingStepResult = FTBridge.FT_TrackStep();
				}
				finally
				{
					lock (_trackingLock)
					{
						_trackingStepComplete = true;
						Monitor.PulseAll(_trackingLock);
					}
				}
			});
		}

		/// <summary>
		/// Blocks until the tracking step that runs on the worker thread, if one is in flight, has returned.
		/// </summary>
		/// <remarks>
		/// Only the main thread queues a step, so a native call that changes tracker state, made on the main thread right after this, cannot
		/// run while a step reads that state. The plugin holds a lock of its own around that state as well, so a call that skips this wait
		/// still waits inside the plugin. The wait lasts at most one step and
		/// happens only when a step is in flight, so it costs nothing in the window before or after a step. It does nothing off the main
		/// thread, where it could not keep the next step from starting and could be the worker itself.
		/// </remarks>
		internal static void WaitForTrackingStepToReturn()
		{
			XRTrackerManager manager = _instance;
			if (ReferenceEquals(manager, null) || manager._trackingStepComplete || !manager.IsManagerThread)
				return;

			lock (manager._trackingLock)
			{
				while (!manager._trackingStepComplete)
					Monitor.Wait(manager._trackingLock);
			}
		}

		/// <summary>
		/// Releases exactly the semaphore acquisition retained by a successfully
		/// queued injected step. Completion, shutdown, and teardown use this same
		/// ownership transfer so an inactive Update loop cannot strand the lock.
		/// </summary>
		private void ReleaseInjectedTrackingOwnership()
		{
			if (!_injectedTrackingInProgress)
				return;
			_injectedTrackingInProgress = false;
			_nativeAccessLock.Release();
		}

		/// <summary>
		/// Initialize tracker for Injected mode (AR Foundation).
		/// Call FeedFrame() to provide camera frames, then TrackStep() to run tracking.
		/// </summary>
		public async Task<bool> InitializeInjectedAsync(FTCameraIntrinsics intrinsics)
		{
			return await InitializeInjectedCoreAsync(intrinsics, null, 0f);
		}

		/// <summary>
		/// Initialize tracker for Injected mode with color + depth cameras.
		/// Depth camera is created before bodies register, ensuring DepthModality is available.
		/// </summary>
		public async Task<bool> InitializeInjectedAsync(FTCameraIntrinsics intrinsics,
			FTCameraIntrinsics depthIntrinsics, float depthScale)
		{
			return await InitializeInjectedCoreAsync(intrinsics, depthIntrinsics, depthScale);
		}

		/// <summary>
		/// Public API for initializing sequence mode. Sets the image source, sequence directory,
		/// and calls the internal initialization.
		/// </summary>
		public async Task InitializeSequenceAsync(string sequenceDirectory)
		{
			_imageSource = ImageSource.Sequence;
			_sequenceDirectory = sequenceDirectory;
			await InitializeSequenceWithCoroutineAsync();
		}

		/// <summary>
		/// Initialize tracker for Sequence mode using TrackingCoroutine (same as Native mode).
		/// C++ SequenceColorCamera reads PNGs and auto-advances — no separate feed function.
		/// </summary>
		private async Task InitializeSequenceWithCoroutineAsync()
		{
			string resolved = ResolveSequenceDirectory(_sequenceDirectory);
			if (string.IsNullOrEmpty(resolved))
			{
				Debug.LogError("[XRTracker] Sequence directory not set or invalid");
				return;
			}

			_resolvedSequenceDirectory = resolved;

			await ShutdownPreviousSessionAsync();

			FTBridge.FT_SetLogCallback(OnLogMessage);
			FTBridge.FT_SetSequenceDirectory(resolved);

			int result = await InitTrackerAsync((int)ImageSource.Sequence, IntPtr.Zero);
			if (result != 0) return;

#if HAS_AR_FOUNDATION
			// Recorded AR observations are only a replay input while AR fusion is
			// enabled. In image-only replay, binding a sidecar without a usable
			// timestamp would otherwise arm the completed-frame recorded clock and
			// prevent an already completed native step from publishing.
			if (SequenceReplayFusionBindingPolicy.ShouldBindRecordedFusion(
				UseARPoseFusion))
			{
				Recording.SequenceARPlaybackBinder.TryBind(_resolvedSequenceDirectory);
				EnsureRecordedCameraPoseDriver(Recording.SequenceARPlaybackBinder.IsBound);
			}
			else
			{
				Recording.SequenceARPlaybackBinder.Unbind();
			}
#endif
			StartNativeTracking();
			OnTrackerInitialized?.Invoke();
		}

		/// <summary>
		/// If the directory has no sequence.json, find the latest timestamped child folder.
		/// </summary>
		private static string ResolveSequenceDirectory(string dir)
		{
			string resolved = PoseFusionSourcePolicy.ResolveSequenceDirectory(dir);
			if (!string.IsNullOrEmpty(resolved) && resolved != dir)
			{
				Debug.Log($"[XRTracker] No sequence in '{Path.GetFileName(dir)}', " +
				          $"using latest recording: {Path.GetFileName(resolved)}");
			}
			return resolved;
		}

		private async Task<bool> InitializeInjectedCoreAsync(FTCameraIntrinsics intrinsics,
			FTCameraIntrinsics? depthIntrinsics, float depthScale)
		{
			if (_imageSource != ImageSource.Injected)
			{
				Debug.LogError("[XRTracker] InitializeInjectedAsync requires ImageSource.Injected");
				return false;
			}

			await ShutdownPreviousSessionAsync();
			FTBridge.FT_SetLogCallback(OnLogMessage);

			var trackerConfig = new FTTrackerConfig
			{
				n_corr_iterations = _correspondenceIterations,
				n_update_iterations = _updateIterations
			};

			int result;
			if (depthIntrinsics.HasValue)
			{
				var di = depthIntrinsics.Value;
				result = await Task.Run(() =>
					FTBridge.FT_InitInjected(ref intrinsics, ref trackerConfig, ref di, depthScale));
			}
			else
			{
				result = await Task.Run(() =>
					FTBridge.FT_InitInjected(ref intrinsics, ref trackerConfig, IntPtr.Zero, 0f));
			}

			if (result != 0)
			{
				Debug.LogError($"[XRTracker] Failed to initialize injected mode (error: {result})");
				return false;
			}

			FrameWriteGuard.Clear();
			_isInitialized = true;
			_trackingReady = true;
			_injectedDepthCamera = depthIntrinsics.HasValue;
			_depthCameraRotation = -1;
			_colorCameraPose = new FTTrackingPose { rot_w = 1f };
			_imageWidth = intrinsics.width;
			_imageHeight = intrinsics.height;
			_cachedFu = intrinsics.fu_normalized * intrinsics.width;
			_cachedFv = intrinsics.fv_normalized * intrinsics.height;
			_cachedPpu = intrinsics.ppu_normalized * intrinsics.width;
			_cachedPpv = intrinsics.ppv_normalized * intrinsics.height;

			CheckScreenAspectChange();
			OnTrackerInitialized?.Invoke();
			return true;
		}

		/// <summary>
		/// Initialize tracker for RealSense mode (native color + depth capture).
		/// RealSense SDK provides intrinsics automatically.
		/// </summary>
		public async Task<bool> InitializeRealSenseAsync()
		{
			if (_imageSource != ImageSource.RealSense)
			{
				Debug.LogError("[XRTracker] InitializeRealSenseAsync requires ImageSource.RealSense");
				return false;
			}

			await ShutdownPreviousSessionAsync();

			FTBridge.FT_SetLogCallback(OnLogMessage);

			// Set resolution BEFORE initializing
			int resResult = FTBridge.FT_SetRealSenseResolution(
				(int)_realSenseColorResolution,
				(int)_realSenseDepthResolution);

			if (resResult != 0 && resResult != FTErrorCode.NOT_SUPPORTED)
				Debug.LogWarning($"[XRTracker] Failed to set RealSense resolution (error: {resResult})");

			int result = await InitTrackerAsync((int)ImageSource.RealSense, IntPtr.Zero);
			if (result != 0)
			{
				if (result == FTErrorCode.NOT_SUPPORTED)
					Debug.LogError("[XRTracker] This plugin build does not support RealSense, so the RealSense image source cannot start.");
				return false;
			}

			// Note: depth callback is NOT registered here — it's enabled on-demand
			// via EnableDepthForwarding() to avoid ~800KB/frame copy overhead when not recording.

			StartNativeTracking();
			OnTrackerInitialized?.Invoke();
			return true;
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Shared shutdown: stop coroutine, wait for tracking, call FT_Shutdown.
		/// Safe to call when not initialized (no-op).
		/// </summary>
		private async Task ShutdownPreviousSessionAsync()
		{
			FrameWriteGuard.Clear();
			FTBridge.ForgetSequencePlaybackState();
			_completedFrameClock.Reset(FTBridge.SequenceSeekEpoch);
			TrackedBodyManager.Instance?.ResetCompletedFrameOrderState();
			ExitRecoverySynchronousStepMode();
#if HAS_AR_FOUNDATION
			Recording.SequenceARPlaybackBinder.Unbind();
			ResetFusionFrameState();
#endif
			if (!_isInitialized)
			{
				await InjectedTrackingShutdown.DrainAsync(
					() => _injectedTrackingInProgress,
					() => _trackingStepComplete,
					ReleaseInjectedTrackingOwnership);
				return;
			}

			_trackingReady = false;
			if (_trackingCoroutine != null)
			{
				StopCoroutine(_trackingCoroutine);
				_trackingCoroutine = null;
			}

			while (!_trackingStepComplete)
				await Task.Yield();

			// Injected feeds retain the semaphore until Update publishes their
			// completion. Shutdown runs before a replacement session can enter
			// Update, so it must drain that retained ownership itself.
			await InjectedTrackingShutdown.DrainAsync(
				() => _injectedTrackingInProgress,
				() => _trackingStepComplete,
				ReleaseInjectedTrackingOwnership);

			_isInitialized = false;
			FTBridge.FT_Shutdown();
			_hasExecutedDetection = false;
		}

#if HAS_AR_FOUNDATION
		private void ResetFusionFrameState()
		{
			_submittedFusionFrameContext = null;
			_completedFusionFrameContext = null;
			_nextLiveFusionFrameIndex = 0;
			_worldJumpWatch.Reset();
			_lastRejectedFusionFrameIndex = int.MinValue;
			_lastRejectedFusionFailure = FusionObservationFailure.None;
			_liveFusionObservationSource = null;
			_liveFusionObservationProvider = null;
		}
#endif

		/// <summary>
		/// Refuses to start the native tracker when the loaded plugin lays the
		/// tracking status block out differently from this build.
		/// </summary>
		/// <remarks>
		/// <c>FT_GetBodyStatus</c> takes no size parameter, so a plugin whose struct
		/// is larger writes past the end of the managed buffer on every body on
		/// every frame, with nothing failing at the call site. The handshake that
		/// detects that already exists; this is the shipping path calling it.
		/// </remarks>
		private static bool NativeBoundaryAgrees()
		{
			bool statusAgrees = FTBridge.VerifyBodyStatusAbi(out string statusDetail);
			bool configAgrees = FTBridge.VerifyBodyConfigAbi(out string configDetail);
			bool layoutAgrees = statusAgrees && configAgrees;
			string detail = statusAgrees && configAgrees
				? $"{statusDetail}; {configDetail}"
				: !statusAgrees ? statusDetail : configDetail;
			if (FTBridge.MayStartNativeTracker(layoutAgrees, detail, out string report))
				return true;

			Debug.LogError($"[XRTracker] {report}");
			return false;
		}

		/// <summary>
		/// Shared init: create tracker config, call FT_Init on background thread, set _isInitialized.
		/// </summary>
		private async Task<int> InitTrackerAsync(int cameraMode, IntPtr cameraIntrinsics)
		{
			if (!NativeBoundaryAgrees())
				return FTErrorCode.SETUP_FAILED;

			var trackerConfig = new FTTrackerConfig
			{
				n_corr_iterations = _correspondenceIterations,
				n_update_iterations = _updateIterations
			};

			int result = await Task.Run(() =>
				FTBridge.FT_Init(cameraMode, cameraIntrinsics, ref trackerConfig));

			if (result != 0)
			{
				Debug.LogError($"[XRTracker] Failed to initialize (error: {result})");
				return result;
			}

			_isInitialized = true;
			return 0;
		}

		private async Task<int> InitTrackerAsync(int cameraMode, FTCameraIntrinsics cameraIntrinsics)
		{
			if (!NativeBoundaryAgrees())
				return FTErrorCode.SETUP_FAILED;

			var trackerConfig = new FTTrackerConfig
			{
				n_corr_iterations = _correspondenceIterations,
				n_update_iterations = _updateIterations
			};

			int result = await Task.Run(() =>
				FTBridge.FT_Init(cameraMode, ref cameraIntrinsics, ref trackerConfig));

			if (result != 0)
			{
				Debug.LogError($"[XRTracker] Failed to initialize (error: {result})");
				return result;
			}

			_isInitialized = true;
			return 0;
		}

		/// <summary>
		/// Shared post-init: set image callback, start TrackingCoroutine, match intrinsics.
		/// Used by Native, RealSense, and Sequence modes.
		/// </summary>
		private void StartNativeTracking()
		{
			FrameWriteGuard.Clear();
			_imageCallbackDelegate = OnImageReceived;
			FTBridge.FT_SetImageCallback(_imageCallbackDelegate, IntPtr.Zero);

			_trackingReady = true;
			_trackingCoroutine = StartCoroutine(TrackingCoroutine());

			MatchCameraIntrinsics();
		}

		private void LoadCalibrations()
		{
			TextAsset asset = _calibrationsFile;

			// Fallback to default in Resources
			if (asset == null)
				asset = Resources.Load<TextAsset>("camera-calibrations");

			if (asset == null)
			{
				Debug.LogWarning("[XRTracker] No calibration file assigned and no default found in Resources. Using default intrinsics.");
				_calibrations = null;
				return;
			}

			try
			{
				_calibrations = JsonUtility.FromJson<MultiCameraCalibration>(asset.text);
			}
			catch (Exception e)
			{
				Debug.LogError($"[XRTracker] Failed to parse calibrations: {e.Message}");
				_calibrations = null;
			}
		}

		private CalibrationData FindCalibrationForDevice(string deviceName)
		{
			if (_calibrations?.cameras != null)
			{
				foreach (var entry in _calibrations.cameras)
				{
					if (entry.deviceName == deviceName)
					{
						return new CalibrationData
						{
							intrinsics = entry.intrinsics,
							intrinsicsDist = entry.intrinsicsDist
						};
					}
				}
			}

			// Device-specific calibration not found — warn the user
			Debug.LogWarning(
				$"[XRTracker] No calibration found for camera '{deviceName}'. " +
				"Tracking will work but accuracy may be reduced. " +
				"For best results, calibrate your camera and add an entry to the calibrations file.");

			if (_calibrations?.@default != null)
				return _calibrations.@default;

			return new CalibrationData
			{
				intrinsics = CalibrationIntrinsics.Default,
				intrinsicsDist = CalibrationIntrinsics.Default
			};
		}

		/// <summary>
		/// Initialize with a camera by name, providing intrinsics directly.
		/// Use this for manual setup without relying on calibration files.
		/// </summary>
		/// <param name="cameraName">Camera device name to find and use</param>
		/// <param name="intrinsics">Undistorted camera intrinsics</param>
		/// <param name="intrinsicsDist">Distorted camera intrinsics (optional, defaults to intrinsics)</param>
		public async Task InitializeWithCameraAsync(string cameraName, CalibrationIntrinsics intrinsics, CalibrationIntrinsics intrinsicsDist = null)
		{
			if (_availableCameras == null)
				EnumerateCameras();

			int index = FindCameraByName(cameraName);
			if (index < 0)
			{
				Debug.LogError($"[XRTracker] Camera '{cameraName}' not found");
				return;
			}

			_selectedCameraIndex = index;
			FTCameraDevice device = _availableCameras[index];
			OnCameraSelected?.Invoke(device);

			await InitializeWithCameraAsync(device, intrinsics, intrinsicsDist ?? intrinsics);
		}

		private async Task InitializeWithCameraAsync(FTCameraDevice device)
		{
			CalibrationData calibration = FindCalibrationForDevice(device.name);
			CalibrationIntrinsics undistorted = calibration.intrinsics;
			CalibrationIntrinsics distorted = calibration.intrinsicsDist ?? calibration.intrinsics;

			await InitializeWithCameraAsync(device, undistorted, distorted);
		}

		private async Task InitializeWithCameraAsync(FTCameraDevice device, CalibrationIntrinsics undistorted, CalibrationIntrinsics distorted)
		{
			FTBridge.FT_SetLogCallback(OnLogMessage);

			var cameraIntrinsics = new FTCameraIntrinsics
			{
				camera_id = device.camera_id,
				width = undistorted.width,
				height = undistorted.height,

				fu_normalized = undistorted.fx,
				fv_normalized = undistorted.fy,
				ppu_normalized = undistorted.cx,
				ppv_normalized = undistorted.cy,

				fu_dist_normalized = distorted.fx,
				fv_dist_normalized = distorted.fy,
				ppu_dist_normalized = distorted.cx,
				ppv_dist_normalized = distorted.cy,

				k1 = distorted.k1,
				k2 = distorted.k2,
				p1 = distorted.k3,
				p2 = distorted.k4,
				k3 = distorted.k5
			};

			int result = await InitTrackerAsync((int)_imageSource, cameraIntrinsics);
			if (result != 0) return;

			if (_imageSource == ImageSource.Native)
				StartNativeTracking();

			OnTrackerInitialized?.Invoke();
		}

		private void MatchCameraIntrinsics()
		{
			if (_mainCamera == null) return;

			int ok = FTBridge.FT_GetCameraIntrinsics(out float fu, out float fv, out float ppu, out float ppv, out int width, out int height);
			if (ok == 0 || width <= 0 || height <= 0 || fu <= 0 || fv <= 0)
			{
				Debug.LogWarning("[XRTracker] FT_GetCameraIntrinsics returned invalid data, skipping projection update");
				return;
			}

			_imageWidth = width;
			_imageHeight = height;
			_cachedFu = fu;
			_cachedFv = fv;
			_cachedPpu = ppu;
			_cachedPpv = ppv;
			CheckScreenAspectChange();
		}

		private void CheckScreenAspectChange()
		{
			if (!_isInitialized || _imageWidth == 0 || _mainCamera == null) return;

			float currentAspect = GetCurrentAspect();
			float textureAspect = (float)_imageWidth / _imageHeight;

			float newCropX = 0f;
			float newCropY = 0f;

			if (textureAspect > currentAspect)
				newCropX = 1f - (currentAspect / textureAspect);
			else if (textureAspect < currentAspect)
				newCropY = 1f - (textureAspect / currentAspect);

			bool changed = Mathf.Abs(newCropX - _cropFactorX) > 0.0001f ||
			               Mathf.Abs(newCropY - _cropFactorY) > 0.0001f;

			if (changed)
			{
				_cropFactorX = newCropX;
				_cropFactorY = newCropY;
				// Update projection matrix with crop factors for correct 3D overlay alignment.
				// AR Foundation overwrites the projection matrix in its own render callback,
				// so this is harmless in AR mode and necessary for non-AR injected feeders
				// (e.g. SequencePlayerFeeder with FormulaBackgroundRenderer).
				UpdateProjectionMatrix(_cachedFu, _cachedFv, _cachedPpu, _cachedPpv, _imageWidth, _imageHeight);

				OnCropFactorsChanged?.Invoke();
			}
		}

		private float GetCurrentAspect()
		{
			if (_mainCamera != null && _mainCamera.pixelRect.height > 0)
			{
				return _mainCamera.pixelRect.width / _mainCamera.pixelRect.height;
			}

			return (float)Screen.width / Screen.height;
		}

		private void UpdateProjectionMatrix(float fu, float fv, float ppu, float ppv, int width, int height)
		{
			float near = _mainCamera.nearClipPlane;
			float far = _mainCamera.farClipPlane;

			float fullLeft = -ppu * near / fu;
			float fullRight = (width - ppu) * near / fu;
			float fullBottom = -(height - ppv) * near / fv;
			float fullTop = ppv * near / fv;

			float fullWidth = fullRight - fullLeft;
			float fullHeight = fullTop - fullBottom;

			float cropLeftRight = fullWidth * _cropFactorX * 0.5f;
			float cropTopBottom = fullHeight * _cropFactorY * 0.5f;

			float left = fullLeft + cropLeftRight;
			float right = fullRight - cropLeftRight;
			float bottom = fullBottom + cropTopBottom;
			float top = fullTop - cropTopBottom;

			Matrix4x4 projMatrix = Matrix4x4.identity;
			projMatrix.m00 = 2.0f * near / (right - left);
			projMatrix.m02 = (right + left) / (right - left);
			projMatrix.m11 = 2.0f * near / (top - bottom);
			projMatrix.m12 = (top + bottom) / (top - bottom);
			projMatrix.m22 = -(far + near) / (far - near);
			projMatrix.m23 = -2.0f * far * near / (far - near);
			projMatrix.m32 = -1.0f;
			projMatrix.m33 = 0.0f;

			_mainCamera.projectionMatrix = projMatrix;
		}

		[MonoPInvokeCallback(typeof(FTBridge.ImageCallback))]
		private static void OnImageReceived(IntPtr rgbData, int width, int height, IntPtr userdata)
		{
			_instance?.HandleImageCallback(rgbData, width, height);
		}

		private void HandleImageCallback(IntPtr rgbData, int width, int height)
		{
			if (_webcamTexture == null || _webcamTexture.width != width || _webcamTexture.height != height)
			{
				if (_webcamTexture != null)
					Destroy(_webcamTexture);

				_webcamTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
				_imageBuffer = new byte[width * height * 3];
			}

			int rowSize = width * 3;
			for (int y = 0; y < height; y++)
			{
				IntPtr sourceRow = rgbData + ((height - 1 - y) * rowSize);
				Marshal.Copy(sourceRow, _imageBuffer, y * rowSize, rowSize);
			}

			_webcamTexture.LoadRawTextureData(_imageBuffer);
			_webcamTexture.Apply();

			PostImage(_webcamTexture);

			// Fire OnFrameFed for native/realsense modes so recorders can capture
			if (UsesNativeCapture && _imageWidth > 0)
			{
				float fuNorm = _cachedFu;
				float fvNorm = _cachedFv;
				float ppuNorm = _cachedPpu;
				float ppvNorm = _cachedPpv;

				// If intrinsics are pixel-space (from native camera), normalize them
				if (fuNorm > 1f && _imageWidth > 0)
				{
					fuNorm = _cachedFu / _imageWidth;
					fvNorm = _cachedFv / _imageHeight;
					ppuNorm = _cachedPpu / _imageWidth;
					ppvNorm = _cachedPpv / _imageHeight;
				}

				OnFrameFed?.Invoke(rgbData, width, height,
					fuNorm, fvNorm, ppuNorm, ppvNorm, 0);
			}
		}

		[MonoPInvokeCallback(typeof(FTBridge.DepthImageCallback))]
		private static void OnDepthImageReceived(IntPtr depthData, int width, int height, float depthScale, IntPtr userdata)
		{
			_instance?.HandleDepthImageCallback(depthData, width, height, depthScale);
		}

		private void HandleDepthImageCallback(IntPtr depthData, int width, int height, float depthScale)
		{
			OnDepthFrameFed?.Invoke(depthData, width, height, depthScale);
		}

		public void PostImage(Texture texture)
		{
			CurrentTexture = texture;
			OnImage?.Invoke(texture);
		}

		[MonoPInvokeCallback(typeof(FTBridge.LogCallback))]
		private static void OnLogMessage(int logLevel, string message)
		{
			switch (logLevel)
			{
				case 0: Debug.Log($"[XRTracker] {message}"); break;
				case 1: Debug.LogWarning($"[XRTracker] {message}"); break;
				case 2: Debug.LogError($"[XRTracker] {message}"); break;
			}
		}

		#endregion
	}
}
