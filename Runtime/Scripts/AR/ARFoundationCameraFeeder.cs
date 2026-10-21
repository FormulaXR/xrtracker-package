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
		[Tooltip("When on, the feeder passes camera frames to the tracker at no more than the Target FPS of the XRTrackerManager. " +
		         "It has no effect when the application frame rate (Application.targetFrameRate) is already at or below that value.")]
		[SerializeField] private bool _limitFps = false;

		[Tooltip("Optional. Auto-discovered if null. Needed for depth, from LiDAR on iOS Pro devices and from the depth image on Android devices that support it.")]
		[SerializeField] private AROcclusionManager _occlusionManager;

		[Tooltip("Android only. How many seconds the tracker waits at start for the first depth image when a body in the scene uses depth. " +
		         "Android gives depth only once the camera has moved a little, and tracking starts without depth when none arrives in time.")]
		[SerializeField] private float _depthWaitSeconds = 3f;

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

		/// <summary>
		/// Android only. How many seconds the tracker waits at start for the first depth image when a body in the scene uses depth.
		/// </summary>
		public float DepthWaitSeconds
		{
			get => _depthWaitSeconds;
			set => _depthWaitSeconds = Mathf.Max(0f, value);
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

#if UNITY_IOS || UNITY_ANDROID
		private bool _hasDepth;
		private NativeArray<ushort> _depthBuffer;
		private int _depthWidth;
		private int _depthHeight;
		private bool _depthFormatReported;
#endif

#if UNITY_ANDROID
		// Below zero before the first start attempt, the time the wait began while the tracker waits for the first depth image,
		// and positive infinity once the wait is over.
		private float _depthWaitStart = -1f;
		private int _cameraTextureWidth;
		private int _cameraTextureHeight;
#endif

		#endregion

		#region Unity Lifecycle

		private void Awake()
		{
			if (_cameraManager == null)
				_cameraManager = GetComponent<ARCameraManager>();
#if UNITY_IOS || UNITY_ANDROID
			if (_occlusionManager == null)
				_occlusionManager = GetComponentInParent<AROcclusionManager>();
#endif
		}

		private void OnEnable()
		{
			Application.onBeforeRender += OnBeforeRender;
#if UNITY_ANDROID
			if (_cameraManager != null)
				_cameraManager.frameReceived += OnCameraFrameReceived;
#endif
		}

		private void OnDisable()
		{
			Application.onBeforeRender -= OnBeforeRender;
#if UNITY_ANDROID
			if (_cameraManager != null)
				_cameraManager.frameReceived -= OnCameraFrameReceived;
#endif
		}

		private void OnDestroy()
		{
			if (_rgbBuffer.IsCreated)
				_rgbBuffer.Dispose();
#if UNITY_IOS || UNITY_ANDROID
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
#if UNITY_ANDROID
					if (KeepWaitingForDepth())
						return;
#endif
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
#if UNITY_IOS || UNITY_ANDROID
			if (ProbeDepth(intrinsics, out var depthIntrinsics))
			{
				success = await manager.InitializeInjectedAsync(intrinsics, depthIntrinsics, 0.001f);
				if (success)
					Debug.Log($"[ARFoundationCameraFeeder] Depth enabled ({_depthWidth}x{_depthHeight})");
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

#if UNITY_ANDROID
		private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
		{
			if (args.textures == null || args.textures.Count == 0 || args.textures[0] == null)
				return;

			_cameraTextureWidth = args.textures[0].width;
			_cameraTextureHeight = args.textures[0].height;
		}

		/// <summary>
		/// Holds the tracker start back while a body in the scene uses depth and the first depth image has not arrived, for at most
		/// <see cref="DepthWaitSeconds"/>. The native depth camera exists only if it is created at start, and a body with depth cannot track
		/// until that camera has received a frame, so starting before depth flows would either drop depth for the session or stall the body.
		/// </summary>
		private bool KeepWaitingForDepth()
		{
			if (float.IsPositiveInfinity(_depthWaitStart))
				return false;

			if (_depthWaitStart < 0f)
			{
				if (!AnySceneBodyWantsDepth() || !OcclusionCanGiveDepth())
				{
					_depthWaitStart = float.PositiveInfinity;
					return false;
				}

				_depthWaitStart = Time.realtimeSinceStartup;
			}

			if (_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
			{
				depthImage.Dispose();
				_depthWaitStart = float.PositiveInfinity;
				return false;
			}

			if (Time.realtimeSinceStartup - _depthWaitStart < _depthWaitSeconds)
				return true;

			Debug.Log($"[ARFoundationCameraFeeder] No depth image arrived within {_depthWaitSeconds:0.#} s, so tracking starts without depth");
			_depthWaitStart = float.PositiveInfinity;
			return false;
		}

		private static bool AnySceneBodyWantsDepth()
		{
			foreach (TrackedBody body in FindObjectsByType<TrackedBody>(FindObjectsSortMode.None))
			{
				if (body.isActiveAndEnabled && body.EnableDepthTracking &&
				    body.TrackingModelAsset != null && body.TrackingModelAsset.HasValidDepthModel)
					return true;
			}
			return false;
		}

		private bool OcclusionCanGiveDepth()
		{
			return _occlusionManager != null && _occlusionManager.isActiveAndEnabled &&
			       _occlusionManager.requestedEnvironmentDepthMode != EnvironmentDepthMode.Disabled &&
			       _occlusionManager.descriptor != null &&
			       _occlusionManager.descriptor.environmentDepthImageSupported != Supported.Unsupported;
		}
#endif

#if UNITY_IOS || UNITY_ANDROID
		private bool ProbeDepth(FTCameraIntrinsics colorIntrinsics, out FTCameraIntrinsics depthIntrinsics)
		{
			depthIntrinsics = default;

			if (_occlusionManager == null)
			{
				Debug.Log("[ARFoundationCameraFeeder] No AROcclusionManager — depth disabled");
				return false;
			}

			if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
			{
				Debug.Log("[ARFoundationCameraFeeder] No depth image available — color-only mode");
				return false;
			}

			using (depthImage)
			{
				if (depthImage.format != XRCpuImage.Format.DepthFloat32 && depthImage.format != XRCpuImage.Format.DepthUint16)
				{
					Debug.LogWarning($"[ARFoundationCameraFeeder] Depth image format {depthImage.format} is not supported — color-only mode");
					return false;
				}

				_hasDepth = true;
				_depthWidth = depthImage.width;
				_depthHeight = depthImage.height;
				depthIntrinsics = DepthIntrinsics(colorIntrinsics, _depthWidth, _depthHeight);
			}

			return true;
		}

		/// <summary>
		/// Expresses the colour camera's intrinsics in the pixels of the depth image.
		/// </summary>
		/// <remarks>
		/// On iOS the depth map covers the same view as the colour image at a lower resolution. On Android the depth image covers the view of
		/// the GPU camera texture, which is a centred crop of the CPU image whenever the two have different aspect ratios, so the normalized
		/// focal length grows by the inverse of the share of the image that is kept and the principal point moves with the crop.
		/// </remarks>
		private FTCameraIntrinsics DepthIntrinsics(FTCameraIntrinsics colorIntrinsics, int depthWidth, int depthHeight)
		{
			float keptU = 1f;
			float keptV = 1f;
#if UNITY_ANDROID
			float colorAspect = (float)colorIntrinsics.width / colorIntrinsics.height;
			bool hasTexture = _cameraTextureWidth > 0 && _cameraTextureHeight > 0;
			float cropAspect = hasTexture
				? (float)_cameraTextureWidth / _cameraTextureHeight
				: (float)depthWidth / depthHeight;
			if (cropAspect > colorAspect)
				keptV = colorAspect / cropAspect;
			else
				keptU = cropAspect / colorAspect;

			string cropSource = hasTexture
				? $"camera texture {_cameraTextureWidth}x{_cameraTextureHeight}"
				: "the depth image itself, because the camera texture size is unknown";
			Debug.Log($"[ARFoundationCameraFeeder] Depth image {depthWidth}x{depthHeight} covers the centre {keptU:P0} x {keptV:P0} " +
			          $"of the colour image {colorIntrinsics.width}x{colorIntrinsics.height}, from the aspect ratio of {cropSource}");
#endif
			float fu = colorIntrinsics.fu_normalized / keptU;
			float fv = colorIntrinsics.fv_normalized / keptV;
			float ppu = (colorIntrinsics.ppu_normalized - (1f - keptU) * 0.5f) / keptU;
			float ppv = (colorIntrinsics.ppv_normalized - (1f - keptV) * 0.5f) / keptV;

			return FTCameraIntrinsics.FromARFoundation(
				fu * depthWidth, fv * depthHeight, ppu * depthWidth, ppv * depthHeight, depthWidth, depthHeight);
		}

		private void AcquireAndFeedDepth()
		{
			if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
			{
				FeedEmptyDepth();
				return;
			}

			using (depthImage)
			{
				int width = depthImage.width;
				int height = depthImage.height;
				int pixelCount = width * height;

				if (!_depthBuffer.IsCreated || _depthBuffer.Length < pixelCount)
				{
					if (_depthBuffer.IsCreated)
						_depthBuffer.Dispose();
					_depthBuffer = new NativeArray<ushort>(pixelCount, Allocator.Persistent);
				}

				unsafe
				{
					var plane = depthImage.GetPlane(0);
					ushort* dstPtr = (ushort*)_depthBuffer.GetUnsafePtr();

					switch (depthImage.format)
					{
						case XRCpuImage.Format.DepthFloat32:
						{
							float* srcPtr = (float*)plane.data.GetUnsafeReadOnlyPtr();
							int srcRowStride = plane.rowStride / sizeof(float);

							for (int y = 0; y < height; y++)
							{
								for (int x = 0; x < width; x++)
								{
									float meters = srcPtr[y * srcRowStride + x];
									int mm = (int)(meters * 1000f + 0.5f);
									dstPtr[y * width + x] =
										(ushort)(mm < 0 ? 0 : (mm > 65535 ? 65535 : mm));
								}
							}
							break;
						}
						case XRCpuImage.Format.DepthUint16:
						{
							// Already millimetres, which is what the tracker reads at a depth scale of 0.001.
							byte* srcPtr = (byte*)plane.data.GetUnsafeReadOnlyPtr();
							long rowBytes = width * sizeof(ushort);

							for (int y = 0; y < height; y++)
								UnsafeUtility.MemCpy(dstPtr + y * width, srcPtr + (long)y * plane.rowStride, rowBytes);
							break;
						}
						default:
						{
							if (!_depthFormatReported)
							{
								Debug.LogWarning($"[ARFoundationCameraFeeder] Depth image format {depthImage.format} is not supported — depth frames are skipped");
								_depthFormatReported = true;
							}
							FeedEmptyDepth();
							return;
						}
					}

					_depthWidth = width;
					_depthHeight = height;
					XRTrackerManager.Instance.FeedDepthFrame((IntPtr)dstPtr, width, height);
				}
			}
		}

		/// <summary>
		/// Feeds a depth frame with no measurements. The native depth camera keeps the last frame it was given, so a frame with no depth
		/// would otherwise be tracked against depth from an earlier camera pose. Zero reads as no measurement.
		/// </summary>
		private void FeedEmptyDepth()
		{
			int pixelCount = _depthWidth * _depthHeight;
			if (pixelCount <= 0)
				return;

			if (!_depthBuffer.IsCreated || _depthBuffer.Length < pixelCount)
			{
				if (_depthBuffer.IsCreated)
					_depthBuffer.Dispose();
				_depthBuffer = new NativeArray<ushort>(pixelCount, Allocator.Persistent);
			}

			unsafe
			{
				ushort* dstPtr = (ushort*)_depthBuffer.GetUnsafePtr();
				UnsafeUtility.MemClear(dstPtr, (long)pixelCount * sizeof(ushort));
				XRTrackerManager.Instance.FeedDepthFrame((IntPtr)dstPtr, _depthWidth, _depthHeight);
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
#if UNITY_IOS || UNITY_ANDROID
			// Feed depth BEFORE color — FeedFrame triggers TrackStep which reads the depth image
			if (_hasDepth && AnyBodyNeedsDepth())
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
				XRTrackerManager.Instance.FeedFrameAsync(
					(IntPtr)_rgbBuffer.GetUnsafeReadOnlyPtr(), _targetWidth, _targetHeight,
					intrinsics.fu_normalized, intrinsics.fv_normalized, intrinsics.ppu_normalized, intrinsics.ppv_normalized,
					screenRotation);
			}
		}

		#endregion
	}
}
#endif
