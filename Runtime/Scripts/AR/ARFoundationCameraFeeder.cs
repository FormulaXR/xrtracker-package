#if HAS_AR_FOUNDATION
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace IV.FormulaTracker
{
	[RequireComponent(typeof(ARCameraManager))]
	public class ARFoundationCameraFeeder : MonoBehaviour
	{
		#region Serialized Fields

		[SerializeField] private ARCameraManager _cameraManager;
		[SerializeField] private bool _limitFps = true;

		[Tooltip("Optional. Auto-discovered if null. Required for LiDAR depth on iOS Pro devices.")]
		[SerializeField] private AROcclusionManager _occlusionManager;

		private const int MAX_RESOLUTION = 640;

		#endregion
		
		#region Getters / Setters

		public bool LimitFps
		{
			get => _limitFps;
			set => _limitFps = value;
		}

		public ARCameraManager CameraManager
		{
			get => _cameraManager;
			set => _cameraManager = value;
		}

		public AROcclusionManager OcclusionManager
		{
			get => _occlusionManager;
			set => _occlusionManager = value;
		}

		#endregion

		#region Private Fields

		private bool _isInitializing;
		private int _targetWidth;
		private int _targetHeight;
		private NativeArray<byte> _rgbBuffer;

		// Frame timing - tracks when next frame should be processed
		// Advances by fixed interval to maintain rhythm regardless of jitter
		private DateTime _nextFrameTime = DateTime.Now;

#if UNITY_IOS
		private bool _hasLiDAR;
		private NativeArray<ushort> _depthBuffer;
		private int _depthWidth;
		private int _depthHeight;
#endif

		#endregion

		#region Unity Lifecycle

		private void Awake()
		{
			if (_cameraManager == null)
				_cameraManager = GetComponent<ARCameraManager>();
#if UNITY_IOS
			if (_occlusionManager == null)
				_occlusionManager = GetComponentInParent<AROcclusionManager>();
#endif
		}

		private void OnEnable()
		{
			Application.onBeforeRender += OnBeforeRender;
		}

		private void OnDisable()
		{
			Application.onBeforeRender -= OnBeforeRender;
		}

		private void OnDestroy()
		{
			if (_rgbBuffer.IsCreated)
				_rgbBuffer.Dispose();
#if UNITY_IOS
			if (_depthBuffer.IsCreated)
				_depthBuffer.Dispose();
#endif
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Called before rendering. Runs before TrackedBody.OnBeforeRender (which is at order 100), but after camera pose update (which runs at 0).
		/// </summary>
		[BeforeRenderOrder(99)]
		private void OnBeforeRender()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null)
				return;

			// If currently initializing or tracking in progress, skip
			if (_isInitializing || manager.IsTrackingInProgress)
				return;

			// Acquire the image first (needed for both init and tracking)
			if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
				return;

			using (cpuImage)
			{
				// Calculate target resolution maintaining aspect ratio
				if (_targetWidth == 0 || _targetHeight == 0)
					CalculateTargetResolution(cpuImage.width, cpuImage.height);

				if (!ExtractIntrinsics(cpuImage, out FTCameraIntrinsics intrinsics))
					return;

				// Initialize if needed
				if (!manager.IsInitialized)
				{
					InitializeTrackerAsync(intrinsics);
					return;
				}

				// FPS limiting - only apply if render rate is higher than tracking target
				// Skip limiting if app already runs at target fps (no point limiting 30fps to 30fps)
				int appTargetFps = Application.targetFrameRate;
				bool shouldLimit = _limitFps &&
				                   (appTargetFps <= 0 || appTargetFps > manager.TargetFps);

				if (shouldLimit)
				{
					DateTime now = DateTime.Now;
					if (now < _nextFrameTime)
						return;

					// Advance by fixed interval (maintains rhythm regardless of jitter)
					double targetIntervalMs = 1000.0 / manager.TargetFps;
					_nextFrameTime = _nextFrameTime.AddMilliseconds(targetIntervalMs);

					// If we fell too far behind (e.g., app was paused), reset to now
					if (now > _nextFrameTime.AddMilliseconds(targetIntervalMs * 2))
						_nextFrameTime = now;
				}

				ProcessAndFeedFrame(cpuImage, intrinsics);
			}
		}

		private async void InitializeTrackerAsync(FTCameraIntrinsics intrinsics)
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || manager.IsInitialized)
				return;

			_isInitializing = true;

			bool success;
#if UNITY_IOS
			if (ProbeLiDAR(intrinsics, out var depthIntrinsics))
			{
				success = await manager.InitializeInjectedAsync(intrinsics, depthIntrinsics, 0.001f);
				if (success)
					Debug.Log($"[ARFoundationCameraFeeder] LiDAR depth enabled ({_depthWidth}x{_depthHeight})");
			}
			else
			{
				success = await manager.InitializeInjectedAsync(intrinsics);
			}
#else
			success = await manager.InitializeInjectedAsync(intrinsics);
#endif

			_isInitializing = false;

			if (!success)
				Debug.LogError("[ARFoundationCameraFeeder] Failed to initialize tracker");
		}

		private void CalculateTargetResolution(int srcWidth, int srcHeight)
		{
			float aspect = (float)srcWidth / srcHeight;

			if (srcWidth >= srcHeight)
			{
				_targetWidth = Mathf.Min(srcWidth, MAX_RESOLUTION);
				_targetHeight = Mathf.RoundToInt(_targetWidth / aspect);
			}
			else
			{
				_targetHeight = Mathf.Min(srcHeight, MAX_RESOLUTION);
				_targetWidth = Mathf.RoundToInt(_targetHeight * aspect);
			}

			// Ensure even dimensions for video codecs
			_targetWidth = (_targetWidth / 2) * 2;
			_targetHeight = (_targetHeight / 2) * 2;
		}

		private bool ExtractIntrinsics(XRCpuImage cpuImage, out FTCameraIntrinsics intrinsics)
		{
			intrinsics = default;
			if (!_cameraManager.TryGetIntrinsics(out XRCameraIntrinsics arIntrinsics))
				return false;

			float scaleX = (float)_targetWidth / cpuImage.width;
			float scaleY = (float)_targetHeight / cpuImage.height;

			float fx = arIntrinsics.focalLength.x * scaleX;
			float fy = arIntrinsics.focalLength.y * scaleY;
			float cx = arIntrinsics.principalPoint.x * scaleX;
			float cy = arIntrinsics.principalPoint.y * scaleY;

			intrinsics = FTCameraIntrinsics.FromARFoundation(fx, fy, cx, cy, _targetWidth, _targetHeight);
			return true;
		}

#if UNITY_IOS
		private bool ProbeLiDAR(FTCameraIntrinsics colorIntrinsics, out FTCameraIntrinsics depthIntrinsics)
		{
			depthIntrinsics = default;

			if (_occlusionManager == null)
			{
				Debug.Log("[ARFoundationCameraFeeder] No AROcclusionManager — depth disabled");
				return false;
			}

			if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
			{
				Debug.Log("[ARFoundationCameraFeeder] No LiDAR depth available — color-only mode");
				return false;
			}

			using (depthImage)
			{
				_hasLiDAR = true;
				_depthWidth = depthImage.width;
				_depthHeight = depthImage.height;

				// ARKit LiDAR depth is aligned to RGB camera — same normalized intrinsics, different resolution
				depthIntrinsics = new FTCameraIntrinsics
				{
					camera_id = 0,
					width = _depthWidth,
					height = _depthHeight,
					fu_normalized = colorIntrinsics.fu_normalized,
					fv_normalized = colorIntrinsics.fv_normalized,
					ppu_normalized = colorIntrinsics.ppu_normalized,
					ppv_normalized = colorIntrinsics.ppv_normalized,
					fu_dist_normalized = colorIntrinsics.fu_dist_normalized,
					fv_dist_normalized = colorIntrinsics.fv_dist_normalized,
					ppu_dist_normalized = colorIntrinsics.ppu_dist_normalized,
					ppv_dist_normalized = colorIntrinsics.ppv_dist_normalized,
				};
			}

			return true;
		}

		private void AcquireAndFeedDepth()
		{
			if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
				return;

			using (depthImage)
			{
				int pixelCount = depthImage.width * depthImage.height;

				if (!_depthBuffer.IsCreated || _depthBuffer.Length < pixelCount)
				{
					if (_depthBuffer.IsCreated)
						_depthBuffer.Dispose();
					_depthBuffer = new NativeArray<ushort>(pixelCount, Allocator.Persistent);
				}

				unsafe
				{
					var plane = depthImage.GetPlane(0);
					float* srcPtr = (float*)plane.data.GetUnsafeReadOnlyPtr();
					ushort* dstPtr = (ushort*)_depthBuffer.GetUnsafePtr();
					int srcRowStride = plane.rowStride / sizeof(float);

					for (int y = 0; y < depthImage.height; y++)
					{
						for (int x = 0; x < depthImage.width; x++)
						{
							float meters = srcPtr[y * srcRowStride + x];
							int mm = (int)(meters * 1000f + 0.5f);
							dstPtr[y * depthImage.width + x] =
								(ushort)(mm < 0 ? 0 : (mm > 65535 ? 65535 : mm));
						}
					}

					XRTrackerManager.Instance.FeedDepthFrame(
						(IntPtr)dstPtr, depthImage.width, depthImage.height);
				}
			}
		}
#endif

		private static bool AnyBodyNeedsDepth()
		{
			foreach (var body in XRTrackerManager.Instance.TrackedBodies)
			{
				if (body.EnableDepthTracking)
					return true;
			}
			return false;
		}

		private void ProcessAndFeedFrame(XRCpuImage cpuImage, FTCameraIntrinsics intrinsics)
		{
#if UNITY_IOS
			// Feed depth BEFORE color — FeedFrame triggers TrackStep which reads the depth image
			if (_hasLiDAR && AnyBodyNeedsDepth())
				AcquireAndFeedDepth();
#endif

			var conversionParams = new XRCpuImage.ConversionParams
			{
				inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
				outputDimensions = new Vector2Int(_targetWidth, _targetHeight),
				outputFormat = TextureFormat.RGB24,
				transformation = XRCpuImage.Transformation.None
			};

			int size = cpuImage.GetConvertedDataSize(conversionParams);

			if (!_rgbBuffer.IsCreated || _rgbBuffer.Length < size)
			{
				if (_rgbBuffer.IsCreated)
					_rgbBuffer.Dispose();
				_rgbBuffer = new NativeArray<byte>(size, Allocator.Persistent);
			}

			// Get current screen orientation for image/intrinsics rotation in native code
			int screenRotation = XRTrackerManager.GetScreenRotation();

			unsafe
			{
				cpuImage.Convert(conversionParams, (IntPtr)_rgbBuffer.GetUnsafePtr(), size);
				XRTrackerManager.Instance.FeedFrame(
					(IntPtr)_rgbBuffer.GetUnsafeReadOnlyPtr(), _targetWidth, _targetHeight,
					intrinsics.fu_normalized, intrinsics.fv_normalized, intrinsics.ppu_normalized, intrinsics.ppv_normalized,
					screenRotation);
			}
		}

		#endregion
	}
}
#endif