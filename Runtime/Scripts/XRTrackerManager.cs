using System;
using System.Collections;
using System.Collections.Generic;
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

	public class XRTrackerManager : MonoBehaviour
	{
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

		[Tooltip("Render resolution for the shared edge normal renderer. " +
		         "Higher = more edge sites but slower renders.")]
		[SerializeField, Range(256, 2048)]
		private int _edgeRenderResolution = 512;

		[Tooltip("Main camera used for tracking. If not set, Camera.main will be used.")] [SerializeField]
		private Camera _mainCamera;

#if HAS_AR_FOUNDATION
		[Tooltip("Use AR Foundation's world tracking to stabilize pose when tracking quality drops")] [SerializeField]
		private bool _useARPoseFusion = true;
#endif

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

		#endregion

		#region Private Fields

		private bool _isInitialized;
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
		private readonly object _trackingLock = new object();

		// Async injected mode state
		private volatile bool _injectedTrackingInProgress;
		private byte[] _injectedFrameBuffer; // Double buffer for async frame feeding
		private GCHandle _injectedFrameHandle;
		private bool _injectedFrameHandleAllocated;

		// Registered tracked bodies for quality-based state management
		private readonly List<TrackedBody> _trackedBodies = new List<TrackedBody>();

		// Semaphore for serializing native access (registration, unregistration, tracking)
		private readonly SemaphoreSlim _nativeAccessLock = new SemaphoreSlim(1, 1);

		private bool UseInjectedCamera() => _imageSource == ImageSource.Injected;

		// License state (cached from native)
		private LicenseTier _licenseTier = LicenseTier.None;
		private LicenseStatus _licenseStatus = LicenseStatus.NotSet;
		private bool _licenseFrozen;

		#endregion

		#region Public Properties

		public bool IsInitialized => _isInitialized;
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
					FTBridge.FT_SetEdgeRenderResolution(_edgeRenderResolution);
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
		/// </summary>
		public IReadOnlyList<TrackedBody> TrackedBodies => _trackedBodies;
		
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
		public bool UseARPoseFusion
		{
			get => _useARPoseFusion;
			set => _useARPoseFusion = value;
		}
#else
		public bool UseARPoseFusion { get; set; } = false;
#endif

		public Camera MainCamera
		{
			get => _mainCamera;
			set
			{
				_mainCamera = value;
				_cameraTransform = _mainCamera.transform;
			}
		}

		/// <summary>Current license tier (None = no license loaded).</summary>
		public LicenseTier LicenseTier => _licenseTier;

		/// <summary>Current license validation status.</summary>
		public LicenseStatus LicenseStatus => _licenseStatus;

		/// <summary>True if Free tier 60s limit has been reached. Deprecated — Free tier no longer active.</summary>
		[System.Obsolete("Free tier deprecated. IsLicenseFrozen is no longer relevant.")]
		public bool IsLicenseFrozen => FTBridge.FT_IsLicenseFrozen();

		/// <summary>Seconds remaining for Free tier (60s). Deprecated — Free tier no longer active.</summary>
		[System.Obsolete("Free tier deprecated. FreeSecondsRemaining is no longer relevant.")]
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
			_licenseFrozen = FTBridge.FT_IsLicenseFrozen();

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
			int result = FTBridge.FT_SetLicenseData(licenseJson, licenseJson.Length, machineId);
			_licenseStatus = (LicenseStatus)result;
			_licenseTier = (LicenseTier)FTBridge.FT_GetLicenseTier();
			_licenseFrozen = FTBridge.FT_IsLicenseFrozen();
			return _licenseStatus;
		}

		/// <summary>
		/// Refresh cached license state from native side. Call if you need up-to-date frozen state.
		/// </summary>
		public void RefreshLicenseState()
		{
			_licenseTier = (LicenseTier)FTBridge.FT_GetLicenseTier();
			_licenseStatus = (LicenseStatus)FTBridge.FT_GetLicenseStatus();
			_licenseFrozen = FTBridge.FT_IsLicenseFrozen();
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

		[System.Obsolete("Free tier deprecated. Kept for future reuse.")]
		internal static string GenerateFreeLicense()
		{
			string deviceName = SystemInfo.deviceName
				.Replace("\\", "\\\\").Replace("\"", "\\\"");
			string payload = $"{{\"licenseType\":\"free\",\"licenseeName\":\"{deviceName}\"}}";
			string payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
			string licJson = $"{{\"payload\":\"{payloadB64}\",\"signature\":\"\",\"version\":1}}";

			try
			{
				string path = Path.Combine(Application.persistentDataPath, "FormulaTracker.lic");
				File.WriteAllText(path, licJson);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[XRTracker] Could not save Free license: {e.Message}");
			}

			return licJson;
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

			LoadLicense();
			EnsureLicenseWatermark();

			if (_mainCamera == null)
				_mainCamera = Camera.main;

			if (_mainCamera != null)
				_cameraTransform = _mainCamera.transform;

			FTBridge.FT_SetEdgeRenderResolution(_edgeRenderResolution);

			// Auto-create TrackedBodyManager if not present
			EnsureTrackedBodyManager();
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

			bool needsWatermark = _licenseTier == LicenseTier.Free ||
			                      _licenseTier == LicenseTier.Trial ||
			                      _licenseTier == LicenseTier.Developer ||
			                      _licenseStatus == LicenseStatus.Expired;
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
				AfterTrackingStep();
				_injectedTrackingInProgress = false;
				_nativeAccessLock.Release(); // Release lock acquired in FeedFrameAsync
			}

			CheckScreenAspectChange();
			// Update tracking state for all bodies - handled by TrackedBodyManager
			TrackedBodyManager.Instance?.UpdateAllBodies();

			if (!_hasExecutedDetection)
			{
				FTBridge.FT_ExecuteDetection();
				_hasExecutedDetection = true;
			}
		}

		private IEnumerator TrackingCoroutine()
		{
			float targetFrameTime = 1f / _targetFps;

			while (_trackingReady && _isInitialized)
			{
				float frameStart = Time.realtimeSinceStartup;

				// Try to acquire native lock - skip frame if body registration in progress
				if (!_nativeAccessLock.Wait(0))
				{
					yield return null;
					continue;
				}

				// 1. Run BeforeTrackingStep on main thread (accesses Unity transforms)
				BeforeTrackingStep();

				// 2. Run FT_TrackStep on background thread (heavy computation)
				_trackingStepComplete = false;
				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						FTBridge.FT_TrackStep();
					}
					finally
					{
						lock (_trackingLock)
						{
							_trackingStepComplete = true;
						}
					}
				});

				// 3. Wait for background thread to complete (non-blocking yield)
				while (!_trackingStepComplete)
				{
					yield return null;
				}

				// 4. Run AfterTrackingStep on main thread (accesses Unity transforms)
				AfterTrackingStep();

				// 5. Release native lock after tracking step completes
				_nativeAccessLock.Release();

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

			// Wait for any in-progress async injected tracking to complete
			waitCount = 0;
			while (_injectedTrackingInProgress && waitCount < 100)
			{
				Thread.Sleep(10);
				waitCount++;
			}

			// Free the pinned frame buffer handle
			if (_injectedFrameHandleAllocated)
			{
				_injectedFrameHandle.Free();
				_injectedFrameHandleAllocated = false;
			}

			_injectedFrameBuffer = null;

			_isInitialized = false;
			_cameraTransform = null;

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

			GCHandle handle = GCHandle.Alloc(rgbData, GCHandleType.Pinned);
			try
			{
				FTBridge.FT_FeedFrame(handle.AddrOfPinnedObject(), width, height,
					fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
				OnFrameFed?.Invoke(handle.AddrOfPinnedObject(), width, height,
					fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
				BeforeTrackingStep();
				FTBridge.FT_TrackStep();
				AfterTrackingStep();
			}
			finally
			{
				handle.Free();
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

			FTBridge.FT_FeedFrame(rgbData, width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
			OnFrameFed?.Invoke(rgbData, width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
			BeforeTrackingStep();
			FTBridge.FT_TrackStep();
			AfterTrackingStep();
		}

		/// <summary>Sequence directory for Sequence mode.</summary>
		public string SequenceDirectory
		{
			get => _sequenceDirectory;
			set => _sequenceDirectory = value;
		}

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

			// Ensure buffer is allocated and correct size
			int requiredSize = width * height * 3;
			if (_injectedFrameBuffer == null || _injectedFrameBuffer.Length != requiredSize)
			{
				// Free previous handle if allocated
				if (_injectedFrameHandleAllocated)
				{
					_injectedFrameHandle.Free();
					_injectedFrameHandleAllocated = false;
				}

				_injectedFrameBuffer = new byte[requiredSize];
			}

			// Copy frame data (so caller can reuse their buffer)
			Buffer.BlockCopy(rgbData, 0, _injectedFrameBuffer, 0, requiredSize);

			// Pin buffer for native access
			if (!_injectedFrameHandleAllocated)
			{
				_injectedFrameHandle = GCHandle.Alloc(_injectedFrameBuffer, GCHandleType.Pinned);
				_injectedFrameHandleAllocated = true;
			}

			// Feed frame with intrinsics to native
			FTBridge.FT_FeedFrame(_injectedFrameHandle.AddrOfPinnedObject(), width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
			OnFrameFed?.Invoke(_injectedFrameHandle.AddrOfPinnedObject(), width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);

			// Run BeforeTrackingStep on main thread (needs Unity transforms)
			BeforeTrackingStep();

			// Mark tracking in progress
			_injectedTrackingInProgress = true;

			// Run heavy tracking computation on background thread
			_trackingStepComplete = false;
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					FTBridge.FT_TrackStep();
				}
				finally
				{
					// Signal completion - AfterTrackingStep will be called in Update
					lock (_trackingLock)
					{
						_trackingStepComplete = true;
					}
				}
			});

			return true;
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

			// Feed frame with intrinsics to native
			FTBridge.FT_FeedFrame(rgbData, width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);
			OnFrameFed?.Invoke(rgbData, width, height,
				fuNorm, fvNorm, ppuNorm, ppvNorm, screenRotation);

			// Run BeforeTrackingStep on main thread (needs Unity transforms)
			BeforeTrackingStep();

			// Mark tracking in progress
			_injectedTrackingInProgress = true;

			// Run heavy tracking computation on background thread
			_trackingStepComplete = false;
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					FTBridge.FT_TrackStep();
				}
				finally
				{
					// Signal completion - AfterTrackingStep will be called in Update
					lock (_trackingLock)
					{
						_trackingStepComplete = true;
					}
				}
			});

			return true;
		}

		/// <summary>
		/// Feed a depth frame to the injected depth camera and fire OnDepthFrameFed.
		/// </summary>
		public void FeedDepthFrame(IntPtr depthData, int width, int height)
		{
			if (!_isInitialized) return;

			FTBridge.FT_FeedDepthFrame(depthData, width, height);
			OnDepthFrameFed?.Invoke(depthData, width, height, 0f);
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

			FTBridge.FT_SetDepthImageCallback(_depthImageCallbackDelegate, IntPtr.Zero);
		}

		/// <summary>
		/// Disable depth frame forwarding. Clears the native depth callback
		/// so FT_TrackStep skips the ~800KB/frame depth buffer copy.
		/// </summary>
		public void DisableDepthForwarding()
		{
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
			FTBridge.FT_ExecuteDetection();
			_hasExecutedDetection = true;
		}

		public void StartTracking()
		{
			FTBridge.FT_StartTracking();
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
			}
		}

		/// <summary>
		/// Unregister a TrackedBody.
		/// Called automatically by TrackedBody when unregistered.
		/// </summary>
		public void UnregisterTrackedBody(TrackedBody body)
		{
			_trackedBodies.Remove(body);
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

		/// <summary>
		/// Called before native tracking step on all registered bodies.
		/// </summary>
		public void BeforeTrackingStep()
		{
			// Feed AR camera world pose to native tracker (enables world-space Tikhonov prior)
			if (UseARPoseFusion && _cameraTransform != null)
			{
				var camPos = _cameraTransform.position;
				var camRot = _cameraTransform.rotation;

				var camPose = ConversionUtils.GetConvertedPose(camPos, camRot);
				FTBridge.FT_SetCameraWorldPose(ref camPose);
			}

			foreach (var body in _trackedBodies)
			{
				if (body != null && body.IsRegistered)
				{
					body.BeforeTrackingStep();
				}
			}

			// Notify subscribers
			OnBeforeTrackingStep?.Invoke();
		}

		/// <summary>
		/// Called after native tracking step on all registered bodies.
		/// </summary>
		public void AfterTrackingStep()
		{
			foreach (var body in _trackedBodies)
			{
				if (body != null && body.IsRegistered)
				{
					body.AfterTrackingStep();
				}
			}

			// Notify subscribers (TrackedBodyManager uses this for non-SLAM pose application)
			OnAfterTrackingStep?.Invoke();
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

			await ShutdownPreviousSessionAsync();

			FTBridge.FT_SetLogCallback(OnLogMessage);
			FTBridge.FT_SetSequenceDirectory(resolved);

			int result = await InitTrackerAsync((int)ImageSource.Sequence, IntPtr.Zero);
			if (result != 0) return;

			StartNativeTracking();
			OnTrackerInitialized?.Invoke();
		}

		/// <summary>
		/// If the directory has no sequence.json, find the latest timestamped child folder.
		/// </summary>
		private static string ResolveSequenceDirectory(string dir)
		{
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return dir;

			if (File.Exists(Path.Combine(dir, "sequence.json")))
				return dir;

			var subDirs = Directory.GetDirectories(dir);
			if (subDirs.Length == 0)
				return dir;

			Array.Sort(subDirs, StringComparer.Ordinal);
			string latest = subDirs[subDirs.Length - 1];

			Debug.Log($"[XRTracker] No sequence in '{Path.GetFileName(dir)}', " +
			          $"using latest recording: {Path.GetFileName(latest)}");
			return latest;
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

			_isInitialized = true;
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
					Debug.LogError("[XRTracker] RealSense not supported - library not compiled with USE_REALSENSE=ON");
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
			if (!_isInitialized) return;

			_trackingReady = false;
			if (_trackingCoroutine != null)
			{
				StopCoroutine(_trackingCoroutine);
				_trackingCoroutine = null;
			}

			while (!_trackingStepComplete)
				await Task.Yield();

			_isInitialized = false;
			FTBridge.FT_Shutdown();
			_hasExecutedDetection = false;
		}

		/// <summary>
		/// Shared init: create tracker config, call FT_Init on background thread, set _isInitialized.
		/// </summary>
		private async Task<int> InitTrackerAsync(int cameraMode, IntPtr cameraIntrinsics)
		{
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