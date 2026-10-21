using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Status codes returned by the native calls that return an int status.
	/// </summary>
	public static class FTErrorCode
	{
		public const int OK = 0;
		public const int NOT_INITIALIZED = -1;
		public const int ALREADY_INITIALIZED = -2;
		public const int BODY_NOT_FOUND = -3;
		public const int BODY_ALREADY_EXISTS = -4;
		public const int INVALID_PARAMETER = -5;
		public const int MODEL_GENERATION_FAILED = -6;
		public const int MODEL_LOAD_FAILED = -7;
		public const int SETUP_FAILED = -8;
		public const int NOT_SUPPORTED = -9;
		/// <summary>
		/// A tracking step or a probe held the shared tracking objects on another
		/// thread, so the write did not happen. The only transient code here: the
		/// same call succeeds later, and the caller should drop the frame rather
		/// than report a fault. A write that fails unnoticed is how a new image
		/// reaches tracking carrying the previous frame's camera pose.
		/// </summary>
		public const int BUSY = -10;
	}

	/// <summary>
	/// The calls into the native tracking plugin. The package drives almost all of them itself, so they are internal.
	/// The public calls are the pose writes, the stability parameters and the recording calls.
	/// </summary>
	public static class FTBridge
	{
		private static long s_sequenceSeekEpoch;
		private static int s_sequenceLoop;

		internal static long SequenceSeekEpoch => Interlocked.Read(ref s_sequenceSeekEpoch);

		/// <summary>
		/// Whether sequence playback wraps from the last frame of its range back to the first. It holds the last value that went
		/// through <see cref="FT_SequenceSetLoop"/>, because the plugin has no call that reports it.
		/// </summary>
		internal static bool SequenceLoop => Volatile.Read(ref s_sequenceLoop) != 0;

		/// <summary>
		/// Declares that sequence playback has begun a new pass over its range, after a wrap of the loop or a repeat of a single
		/// frame. It advances the same epoch a seek advances, so the completed-frame clock starts the pass fresh, and it moves
		/// no frame because the plugin has already done that.
		/// </summary>
		internal static void BeginSequencePass() => Interlocked.Increment(ref s_sequenceSeekEpoch);

		/// <summary>
		/// Forgets the playback state held on this side. A new sequence session starts without a loop until the host sets one.
		/// </summary>
		internal static void ForgetSequencePlaybackState() => Volatile.Write(ref s_sequenceLoop, 0);
#if UNITY_IOS && !UNITY_EDITOR
		private const string DLL_NAME = "__Internal";
#else
		private const string DLL_NAME = "formula_tracker";
#endif

		#region Delegates

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void LogCallback(int logLevel, [MarshalAs(UnmanagedType.LPStr)] string message);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void ImageCallback(IntPtr rgbData, int width, int height, IntPtr userdata);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void DepthImageCallback(IntPtr depthData, int width, int height, float depthScale, IntPtr userdata);

		#endregion

		#region Core API

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SetLogCallback(LogCallback callback);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SetImageCallback(ImageCallback imageCallback, IntPtr userdata);

		/// <summary>
		/// Initialize tracker with camera intrinsics.
		/// The cameraMode argument picks the source of the frames. Mode 0 reads a camera through the plugin,
		/// mode 1 takes the frames that the host feeds in, and mode 2 reads a RealSense camera.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_Init(int cameraMode, ref FTCameraIntrinsics cameraIntrinsics, ref FTTrackerConfig trackerConfig);

		/// <summary>
		/// Initialize tracker without intrinsics (for RealSense mode where the camera driver provides them).
		/// The cameraMode argument picks the source of the frames. Mode 0 reads a camera through the plugin,
		/// mode 1 takes the frames that the host feeds in, and mode 2 reads a RealSense camera.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_Init")]
		internal static extern int FT_Init(int cameraMode, IntPtr cameraIntrinsics, ref FTTrackerConfig trackerConfig);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_Init")]
		internal static extern int FT_InitWithDefaults(int cameraMode, ref FTCameraIntrinsics cameraIntrinsics, IntPtr trackerConfig);

		/// <summary>
		/// Initialize Injected mode with color + optional depth camera in a single call.
		/// Pass IntPtr.Zero for depthIntrinsics for color-only mode.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_InitInjected(ref FTCameraIntrinsics colorIntrinsics,
			ref FTTrackerConfig trackerConfig, ref FTCameraIntrinsics depthIntrinsics, float depthScale);

		/// <summary>
		/// Initialize Injected mode with color only (no depth).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_InitInjected")]
		internal static extern int FT_InitInjected(ref FTCameraIntrinsics colorIntrinsics,
			ref FTTrackerConfig trackerConfig, IntPtr depthIntrinsics, float depthScale);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_Shutdown();

		/// <summary>
		/// Set RealSense camera resolution. Must be called BEFORE FT_Init with mode=2.
		/// </summary>
		/// <param name="colorResolution">Color resolution (0-3, see RealSenseColorResolution enum)</param>
		/// <param name="depthResolution">Depth resolution (0-3, see RealSenseDepthResolution enum)</param>
		/// <returns>FT_OK on success, FT_ERROR_ALREADY_INITIALIZED if called after init</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_SetRealSenseResolution(int colorResolution, int depthResolution);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SetEdgeRenderResolution(int resolution);

		#endregion

		#region Capability Queries

		/// <summary>
		/// Check if depth camera is available (RealSense mode provides depth).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_HasDepthCamera();

		#endregion

		#region Silhouette Model Generation

		/// <summary>
		/// Generate silhouette model file from mesh data (blocking call - call from background thread)
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_GenerateRegionModel")]
		internal static extern int FT_GenerateSilhouetteModel(
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			string outputPath,
			ref FTModelConfig modelConfig);

		/// <summary>
		/// Generate silhouette model - pointer version for async/pinned usage.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_GenerateRegionModel")]
		internal static extern int FT_GenerateSilhouetteModel(
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			string outputPath,
			IntPtr modelConfig);

		/// <summary>
		/// Check if existing silhouette model is valid for the given mesh
		/// Returns: true if valid, false if needs regeneration
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_ValidateRegionModel")]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_ValidateSilhouetteModel(
			string modelPath,
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			ref FTModelConfig modelConfig);

		/// <summary>
		/// Validate silhouette model - pointer version for async/pinned usage.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_ValidateRegionModel")]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_ValidateSilhouetteModel(
			string modelPath,
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			IntPtr modelConfig);

		/// <summary>
		/// Generate depth model file from mesh data (blocking call - call from background thread)
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GenerateDepthModel(
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			string outputPath,
			ref FTModelConfig modelConfig);

		/// <summary>
		/// Generate depth model - pointer version for async/pinned usage.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_GenerateDepthModel")]
		internal static extern int FT_GenerateDepthModel(
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			string outputPath,
			IntPtr modelConfig);

		/// <summary>
		/// Generate both silhouette + depth model files in one call.
		/// Shares a single Vulkan device, avoiding context churn that causes hangs on large meshes.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GenerateTrackingModels(
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr trianglesPtr,
			int triangleCount,
			string silhouetteModelPath,
			string depthModelPath,
			ref FTModelConfig modelConfig);

		#endregion

		#region Body Management

		/// <summary>
		/// Register a tracked body.
		/// Silhouette data: provide silhouetteModelPath OR (silhouetteModelData + silhouetteModelSize)
		/// Depth data: provide depthModelData + depthModelSize if depth modality is enabled
		/// Parent: provide parentId + relativePose for child, or null/IntPtr.Zero for independent
		/// freeDirections: DOF flags bitmask (0 = rigid, bits 0-5 = rot_x,y,z,trans_x,y,z)
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_RegisterBody(
			string bodyId,
			IntPtr verticesPtr,
			int vertexCount,
			IntPtr normalsPtr,
			int hasNormals,
			IntPtr trianglesPtr,
			int triangleCount,
			string silhouetteModelPath,
			IntPtr silhouetteModelData,
			int silhouetteModelSize,
			IntPtr depthModelData,
			int depthModelSize,
			ref FTModelConfig modelConfig,
			ref FTBodyConfig bodyConfig,
			ref FTTrackingPose initialPose,
			string parentId,
			IntPtr relativePose,
			int freeDirections);

		/// <summary>
		/// Unregister a body
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_UnregisterBody(string bodyId);

		/// <summary>
		/// Get body pose. Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetBodyPose(
			string bodyId,
			out FTTrackingPose pose);

		/// <summary>
		/// Writes the pose of a body before the tracking step of the frame.
		/// </summary>
		/// <returns>
		/// <see cref="FTErrorCode.OK"/> when the plugin took the pose, and the status code of the refusal otherwise.
		/// <see cref="FTErrorCode.BUSY"/> is transient, so the caller drops the frame and writes again on the next one.
		/// </returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_SetBodyPose(string bodyId, ref FTTrackingPose pose);

		/// <summary>
		/// Writes the pose a body returns to when detection runs or the body resets. This changes the pose the body
		/// restarts from and leaves its current pose as it is.
		/// </summary>
		/// <returns>The same status codes as <see cref="FT_SetBodyPose"/>.</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_SetDetectorPose(string bodyId, ref FTTrackingPose pose);

		/// <summary>
		/// Set stability (regularization) parameters at runtime.
		/// Higher values = smoother but slower response.
		/// Returns true if body found and parameters updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetStabilityParameters(
			string bodyId,
			float rotationRegularization,
			float translationRegularization);

		/// <summary>
		/// Set edge tracking tuning parameters at runtime.
		/// functionAmplitude: Edge uncertainty tolerance (default: 0.43, range: 0.3-0.6). Higher = more forgiving for blurry edges.
		/// learningRate: Pose update step size (default: 1.3, range: 0.5-2.5). Lower = more stable, higher = faster response.
		/// Returns true if body found and parameters updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_SetModalityParameters(
			string bodyId,
			float functionAmplitude,
			float learningRate);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_SetEdgeParameters(
			string bodyId,
			float depthEdgeThreshold,
			float trackingRadius,
			float minGradient,
			float sampleStep,
			float creaseThresholdDeg);

		/// <summary>What asking the plugin to change the edge settings of a registered body came to.</summary>
		internal enum EdgeParametersResult
		{
			/// <summary>The plugin changed the settings, and the next tracking step draws its sites with them.</summary>
			Applied,
			/// <summary>The plugin knows the call and did not apply it, because the body is not registered, does not track by the edge method, or a value is not a positive number.</summary>
			Refused,
			/// <summary>This plugin build does not export the call, so the settings of a registered body stay as registration read them.</summary>
			NotInPlugin,
		}

		/// <summary>
		/// Changes, while the body tracks, the edge settings that decide which sites are drawn and how they are searched. The caller must hold
		/// the native access lock, because the plugin must not be in a tracking step at the same time.
		/// </summary>
		/// <remarks>
		/// A plugin build made before this call existed does not export it. That reads as <see cref="EdgeParametersResult.NotInPlugin"/> and
		/// nothing is thrown, so the caller can say that the settings need the body to register again.
		/// </remarks>
		internal static EdgeParametersResult TrySetEdgeParameters(string bodyId, in EdgeDetectionSettings settings)
		{
			if (string.IsNullOrEmpty(bodyId)) return EdgeParametersResult.Refused;
			try
			{
				return FT_SetEdgeParameters(bodyId, settings.DepthEdgeThreshold, settings.SearchRadius, settings.MinGradient, settings.SampleStep,
					settings.CreaseThresholdDeg)
					? EdgeParametersResult.Applied
					: EdgeParametersResult.Refused;
			}
			catch (EntryPointNotFoundException) { return EdgeParametersResult.NotInPlugin; }
			catch (DllNotFoundException) { return EdgeParametersResult.NotInPlugin; }
		}

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_SetDepthParameters(
			string bodyId,
			float[] consideredDistances,
			int consideredDistanceCount,
			float[] standardDeviations,
			int standardDeviationCount);

		/// <summary>What asking the plugin to change the depth settings of a registered body came to.</summary>
		internal enum DepthParametersResult
		{
			/// <summary>The plugin changed the settings, and the next tracking step searches its depth correspondences with them.</summary>
			Applied,
			/// <summary>The plugin knows the call and did not apply it, because the body is not registered, has no depth modality, or a value is not a positive number.</summary>
			Refused,
			/// <summary>This plugin build does not export the call, so the depth settings of a registered body stay as registration left them.</summary>
			NotInPlugin,
		}

		/// <summary>
		/// Changes, while the body tracks, the considered distance and the standard deviations of the depth correspondences. The caller must hold
		/// the native access lock, because the plugin must not be in a tracking step at the same time.
		/// </summary>
		/// <remarks>
		/// A plugin build made before this call existed does not export it. That reads as <see cref="DepthParametersResult.NotInPlugin"/> and
		/// nothing is thrown, so the caller can say that the settings need the body to register again.
		/// </remarks>
		internal static DepthParametersResult TrySetDepthParameters(string bodyId, in DepthCorrespondenceSettings settings)
		{
			if (string.IsNullOrEmpty(bodyId)) return DepthParametersResult.Refused;
			float[] deviations = new float[settings.StandardDeviations.Count];
			for (int index = 0; index < deviations.Length; index++)
				deviations[index] = settings.StandardDeviations[index];
			try
			{
				return FT_SetDepthParameters(bodyId, new[] { settings.ConsideredDistance }, 1, deviations, deviations.Length)
					? DepthParametersResult.Applied
					: DepthParametersResult.Refused;
			}
			catch (EntryPointNotFoundException) { return DepthParametersResult.NotInPlugin; }
			catch (DllNotFoundException) { return DepthParametersResult.NotInPlugin; }
		}

		/// <summary>
		/// Check if body is registered. Returns true if registered.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_IsBodyRegistered(string bodyId);

		/// <summary>
		/// Get body tracking status. Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetBodyStatus(string bodyId, out FTBodyStatus status);

		#region Loss guard state

		/// <summary>
		/// Byte size the loss-guard state must have to match the native ABI.
		/// </summary>
		internal const int LOSS_QUARANTINE_STATE_STRUCT_SIZE = 220;

		/// <summary>
		/// Supplies the AR camera pose for one frame to the loss guard, and to nothing
		/// else.
		/// </summary>
		/// <remarks>
		/// This is not <c>FT_SetCameraWorldPose</c>. That call moves the tracking
		/// camera and changes what the solver does; this one is read by the guard
		/// alone. One observation belongs to one frame: the next tracking step
		/// consumes it and clears it, so a frame the application forgot to supply one
		/// for is read as missing rather than silently given the previous camera.
		/// </remarks>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_SubmitLossQuarantineObservation(int frameIndex,
			ref FTTrackingPose worldFromCamera);

		/// <summary>
		/// Moves one body's trusted state into another tracking space.
		/// </summary>
		/// <remarks>
		/// Call this inside the same commit that converts the camera, the body poses,
		/// the held poses and the priors. A trusted pose left in the old space would be
		/// compared against observations in the new one, and the difference between the
		/// two spaces would read as the object having moved.
		/// </remarks>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_ConvertLossQuarantineFrame(string bodyId,
			ref FTTrackingPose newFromOld);

		/// <summary>
		/// Reads what the guard did on the last step. Emit-only.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetLossQuarantineState(string bodyId,
			ref FTLossQuarantineState state);

		#endregion

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		private static extern int FT_GetBodyStatusStructSize();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		private static extern int FT_GetBodyConfigStructSize();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		private static extern int FT_GetEdgeKeyframeReasonCount();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_GetEdgePredictionCorrespondences(
			string bodyId, [MarshalAs(UnmanagedType.I1)] out bool enable);

		/// <summary>
		/// Reads whether correspondence iteration 0 keeps the correspondences that the correlation stage built, for one body.
		/// It returns false when the plugin does not carry that switch, which is how the current plugin answers.
		/// </summary>
		internal static bool TryGetEdgePredictionCorrespondences(string bodyId,
			out bool enable)
		{
			enable = false;
			if (string.IsNullOrEmpty(bodyId)) return false;
			try
			{
				return FT_GetEdgePredictionCorrespondences(bodyId, out enable);
			}
			catch (EntryPointNotFoundException) { return false; }
			catch (DllNotFoundException) { return false; }
		}

		/// <summary>
		/// Byte size this struct must have to match the native one. Kept in sync by
		/// hand with the compile-time assertion in the native header.
		/// </summary>
		internal const int BODY_STATUS_STRUCT_SIZE = 540;

		/// <summary>
		/// Byte size the configuration struct must have to match the native ABI.
		/// </summary>
		internal const int BODY_CONFIG_STRUCT_SIZE = 352;

		/// <summary>Number of keyframe event reasons this build understands.</summary>
		internal const int EDGE_KEYFRAME_REASON_COUNT = 14;

		/// <summary>
		/// Verifies that the loaded plugin lays <see cref="FTBodyStatus"/> out the way
		/// this assembly does.
		/// </summary>
		/// <remarks>
		/// <see cref="FT_GetBodyStatus"/> takes no size parameter, so a plugin whose
		/// struct is larger than this one writes past the end of the managed buffer.
		/// That corruption has no symptom at the call site, which is why this is
		/// checked explicitly rather than left to be noticed. A plugin too old to
		/// export the size symbol cannot be checked and is reported as a mismatch.
		/// </remarks>
		/// <param name="detail">Human-readable outcome, for logs and manifests.</param>
		/// <returns>True when the plugin and this assembly agree.</returns>
		internal static bool VerifyBodyStatusAbi(out string detail)
		{
			int managed = Marshal.SizeOf(typeof(FTBodyStatus));
			if (managed != BODY_STATUS_STRUCT_SIZE)
			{
				detail = $"managed FTBodyStatus is {managed} bytes, expected {BODY_STATUS_STRUCT_SIZE}";
				return false;
			}

			int native;
			int reasons;
			try
			{
				native = FT_GetBodyStatusStructSize();
				reasons = FT_GetEdgeKeyframeReasonCount();
			}
			catch (EntryPointNotFoundException)
			{
				detail = "plugin does not export the body status ABI handshake";
				return false;
			}

			if (native != managed)
			{
				detail = $"native FTBodyStatus is {native} bytes, managed is {managed}";
				return false;
			}
			if (reasons != EDGE_KEYFRAME_REASON_COUNT)
			{
				detail = $"plugin reports {reasons} keyframe reasons, this build knows {EDGE_KEYFRAME_REASON_COUNT}";
				return false;
			}

			detail = $"FTBodyStatus {managed} bytes, {reasons} keyframe reasons";
			return true;
		}

		/// <summary>
		/// Verifies that the configuration passed by reference to registration has
		/// the exact size exported by the loaded native plugin.
		/// </summary>
		internal static bool VerifyBodyConfigAbi(out string detail)
		{
			int managed = Marshal.SizeOf(typeof(FTBodyConfig));
			if (managed != BODY_CONFIG_STRUCT_SIZE)
			{
				detail = $"managed FTBodyConfig is {managed} bytes, expected {BODY_CONFIG_STRUCT_SIZE}";
				return false;
			}

			int native;
			try
			{
				native = FT_GetBodyConfigStructSize();
			}
			catch (EntryPointNotFoundException)
			{
				detail = "plugin does not export the body configuration ABI handshake";
				return false;
			}

			if (native != managed)
			{
				detail = $"native FTBodyConfig is {native} bytes, managed is {managed}";
				return false;
			}

			detail = $"FTBodyConfig {managed} bytes";
			return true;
		}

		/// <summary>
		/// Whether the managed layer may start the native tracker, given the result
		/// of <see cref="VerifyBodyStatusAbi"/>.
		/// </summary>
		/// <remarks>
		/// Fail-closed, and deliberately so: a boundary that cannot be confirmed is
		/// refused rather than used. The alternative is one silent out-of-bounds
		/// read per body per frame, which has no symptom at the call site. The
		/// check is exact — two sizes are equal or they are not — so no tolerance
		/// is involved.
		/// </remarks>
		/// <param name="layoutAgrees">What the handshake returned.</param>
		/// <param name="detail">What the handshake reported, naming both sizes.</param>
		/// <param name="report">The line to log, carrying <paramref name="detail"/>.</param>
		/// <returns>True when initialization may proceed.</returns>
		internal static bool MayStartNativeTracker(
			bool layoutAgrees, string detail, out string report)
		{
			if (layoutAgrees)
			{
				report = detail;
				return true;
			}

			report = "the loaded plugin does not lay out the tracking status block " +
				$"the way this build reads it, so tracking was not started: {detail}";
			return false;
		}

		#endregion

		#region Body Hierarchy

		/// <summary>
		/// Update relative pose of attached child body.
		/// Returns true if child is attached and pose updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_SetRelativePose(
			string childId,
			ref FTTrackingPose relativePose);

		/// <summary>
		/// Update which DOF are free for attached child body.
		/// freeDirections: DOF flags bitmask (0 = rigid, bits 0-5 = rot_x,y,z,trans_x,y,z)
		/// Returns true if child is attached and DOF updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_SetChildFreeDOF(
			string childId,
			int freeDirections);

		/// <summary>
		/// Get parent body ID. Returns true if child found.
		/// parentIdOut will be empty string if no parent.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetParentBody(
			string childId,
			System.Text.StringBuilder parentIdOut,
			int maxLen);

		/// <summary>
		/// Update body geometry (mesh and tracking models) at runtime while preserving tracking state.
		/// </summary>
		/// <param name="bodyId">Body to update</param>
		/// <param name="vertices">New vertex positions (x,y,z triplets)</param>
		/// <param name="vertexCount">Number of vertices</param>
		/// <param name="triangles">New triangle indices</param>
		/// <param name="triangleCount">Number of triangle indices (must be multiple of 3)</param>
		/// <param name="silhouetteModelData">Pre-baked silhouette model binary data</param>
		/// <param name="silhouetteModelSize">Size of silhouette model data in bytes</param>
		/// <param name="depthModelData">Pre-baked depth model binary data (or IntPtr.Zero if not used)</param>
		/// <param name="depthModelSize">Size of depth model data in bytes (or 0)</param>
		/// <returns>FT_OK on success, error code on failure</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_UpdateBodyGeometry(
			string bodyId,
			IntPtr vertices,
			int vertexCount,
			IntPtr triangles,
			int triangleCount,
			IntPtr silhouetteModelData,
			int silhouetteModelSize,
			IntPtr depthModelData,
			int depthModelSize);

		#endregion

		#region Occlusion

		/// <summary>
		/// Enable/disable occlusion for a body. When enabled, the body's tracking will
		/// ignore regions occluded by OccluderBody instances. Occlusion is automatically
		/// activated when OccluderBodies exist, and deactivated when they are removed.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_SetOcclusionEnabled(string bodyId, [MarshalAs(UnmanagedType.I1)] bool enabled);

		/// <summary>
		/// Get whether occlusion is enabled for a body.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetOcclusionEnabled(string bodyId);

		/// <summary>
		/// Set silhouette modality occlusion parameters for a body.
		/// Can be called at runtime - will re-setup modality if occlusion is active.
		/// </summary>
		/// <param name="bodyId">Body to update</param>
		/// <param name="radius">Search radius around silhouette points (default: 0.01m)</param>
		/// <param name="threshold">Depth threshold for occlusion (default: 0.01m, use smaller for tight assemblies)</param>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_SetRegionOcclusionParameters")]
		internal static extern int FT_SetSilhouetteOcclusionParameters(string bodyId, float radius, float threshold);

		/// <summary>
		/// Add an occluder body for a tracked body.
		/// The tracked body will ignore regions occluded by the occluder.
		/// Note: For OccluderBody instances, use FT_SetOcclusionEnabled instead - they auto-occlude all bodies.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_AddOccluder(string bodyId, string occluderId);

		/// <summary>
		/// Remove an occluder relationship.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_RemoveOccluder(string bodyId, string occluderId);

		/// <summary>
		/// Enable mutual occlusion between two bodies (both occlude each other).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_EnableMutualOcclusion(string bodyAId, string bodyBId);

		/// <summary>
		/// Disable mutual occlusion between two bodies.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_DisableMutualOcclusion(string bodyAId, string bodyBId);

		#endregion

		#region Silhouette Validation

		/// <summary>
		/// Enable silhouette validation using runtime silhouette renderer.
		/// Validates that tracking points are actually on visible silhouette boundaries.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_EnableRegionChecking")]
		internal static extern int FT_EnableSilhouetteChecking(string bodyId);

		/// <summary>
		/// Enable/disable whether a body contributes to gradient optimization.
		/// When disabled, the body still computes correspondences and quality metrics,
		/// but does NOT contribute gradient/hessian to the optimization.
		/// This is useful for validation mode where we want kinematic attachment (pose follows
		/// parent) but don't want the child's silhouette affecting the parent's tracking.
		/// Note: This is a heavy operation requiring re-setup. For frequent toggling based on
		/// quality, use FT_SetBodyContributesToOptimization instead.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_SetModalityContribution(string bodyId, [MarshalAs(UnmanagedType.I1)] bool contributes);

		/// <summary>
		/// Lightweight flag to enable/disable whether a body contributes to optimization.
		/// When disabled, the body still tracks and computes quality metrics, but its
		/// gradient/hessian are not added to the optimization solution.
		/// This is efficient for toggling based on tracking quality (no re-setup needed).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_SetBodyContributesToOptimization(string bodyId, [MarshalAs(UnmanagedType.I1)] bool contributes);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_SetChildSettling(string childId, [MarshalAs(UnmanagedType.I1)] bool settling,
			float rotationSigma, float translationSigma, float rotationLimit, float translationLimit);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_GetChildSeatOffset(string childId, [Out] float[] offset);

		/// <summary>The length of the seat offset <see cref="TryGetChildSeatOffset"/> fills.</summary>
		internal const int SEAT_OFFSET_LENGTH = 6;

		private static int s_childSettlingExport = -1;

		/// <summary>
		/// Whether the loaded plugin can settle a child around its seat. It is unknown (null) until the first call, and
		/// false from the first call that found the plugin without the export.
		/// </summary>
		internal static bool? ChildSettlingAvailable => s_childSettlingExport < 0 ? (bool?)null : s_childSettlingExport == 1;

		/// <summary>
		/// Lets a rigid child that stays out of its parent's solve settle its own pose around its seat, or puts it back on
		/// its seat. Every angle is in radians and every distance in metres.
		/// </summary>
		/// <returns>
		/// <see cref="NativeCallOutcome.Done"/> when the plugin took the setting, <see cref="NativeCallOutcome.Refused"/>
		/// when it refused it (the body is not an attached child, or a value is not usable), and
		/// <see cref="NativeCallOutcome.Missing"/> when the plugin has no such export. The first missing export is logged
		/// once, and the part is then checked at its seat.
		/// </returns>
		internal static NativeCallOutcome TrySetChildSettling(string childId, bool settling, float rotationSigma,
			float translationSigma, float rotationLimit, float translationLimit)
		{
			if (s_childSettlingExport == 0)
				return NativeCallOutcome.Missing;
			try
			{
				bool taken = FT_SetChildSettling(childId, settling, rotationSigma, translationSigma, rotationLimit, translationLimit);
				s_childSettlingExport = 1;
				return taken ? NativeCallOutcome.Done : NativeCallOutcome.Refused;
			}
			catch (EntryPointNotFoundException)
			{
				NoteChildSettlingMissing();
				return NativeCallOutcome.Missing;
			}
			catch (DllNotFoundException)
			{
				return NativeCallOutcome.Missing;
			}
		}

		/// <summary>
		/// Reads a child's offset from its seat, the rotation vector in radians and then the translation in metres, in the
		/// frame of the child at its seat with the origin at the centre of the child's geometry.
		/// </summary>
		internal static NativeCallOutcome TryGetChildSeatOffset(string childId, float[] offset)
		{
			if (offset == null || offset.Length < SEAT_OFFSET_LENGTH)
				return NativeCallOutcome.Refused;
			if (s_childSettlingExport == 0)
				return NativeCallOutcome.Missing;
			try
			{
				return FT_GetChildSeatOffset(childId, offset) ? NativeCallOutcome.Done : NativeCallOutcome.Refused;
			}
			catch (EntryPointNotFoundException)
			{
				NoteChildSettlingMissing();
				return NativeCallOutcome.Missing;
			}
			catch (DllNotFoundException)
			{
				return NativeCallOutcome.Missing;
			}
		}

		private static void NoteChildSettlingMissing()
		{
			if (s_childSettlingExport == 0)
				return;
			s_childSettlingExport = 0;
			UnityEngine.Debug.LogWarning("[XRTracker] The plugin cannot settle an assembly part around its seat, so every free check falls back to the check at the seat.");
		}

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_SetStartSearch")]
		private static extern int FT_SetStartSearch(string bodyId, ref FTTrackingPose guide, int periodSteps, int guideGeneration);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_SetStartSearch")]
		private static extern int FT_ClearStartSearch(string bodyId, IntPtr noGuide, int periodSteps, int guideGeneration);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		private static extern bool FT_GetStartSearchReading(string bodyId, [Out] float[] reading, int count);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private static extern int FT_SetStartSearchSeat(string bodyId, int writeSeat);

		/// <summary>
		/// Tells the plugin whether its start search writes the best hypothesis as the pose the refinement of the body starts from on
		/// every step (the default), or only searches and reports and leaves the seat to the body. A plugin without the export is logged
		/// once, as for the search.
		/// </summary>
		internal static NativeCallOutcome TrySetStartSearchSeat(string bodyId, bool writeSeat) =>
			GuardStartSearchExport(() => FT_SetStartSearchSeat(bodyId, writeSeat ? 1 : 0) == FTErrorCode.OK);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private static extern int FT_SetStartSearchBank(string bodyId, float coneHalfAngle, float coneSpacing, float rollRange, float fineHalfAngle,
			float windowMargin, int distanceCount);

		/// <summary>
		/// Tells the plugin what the bank of its start search is made of, which is a cone of views around the guide (its half angle and
		/// the spacing between neighbours, in degrees), the turns in the image plane (from minus to plus the roll range, in degrees) and
		/// the half angle of the fine stage in degrees, where 0 switches the fine stage off. It also tells the margin, in pixels of the search
		/// image, by which the box of the guide's outline is grown to make the window the search looks at (0 searches the whole image), and how
		/// many distances the bank holds around the guide's own (1, 3 or 5). A plugin without the export is logged once, as for the search, and
		/// then keeps the bank it was built with.
		/// </summary>
		internal static NativeCallOutcome TrySetStartSearchBank(string bodyId, float coneHalfAngle, float coneSpacing, float rollRange, float fineHalfAngle,
			float windowMargin, int distanceCount) =>
			GuardStartSearchExport(() => FT_SetStartSearchBank(bodyId, coneHalfAngle, coneSpacing, rollRange, fineHalfAngle, windowMargin, distanceCount) == FTErrorCode.OK);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private static extern int FT_SetStartSearchRenderEachDistance(string bodyId, int renderEachDistance);

		/// <summary>
		/// Tells the plugin how the other distances of the bank of its start search are made. On, which is the default of the body, it draws
		/// every distance of every view. Off, it draws each view once at the guide's own distance and makes the other distances by scaling that
		/// drawing. A plugin without the export is logged once, as for the search, and then keeps the way it was built with.
		/// </summary>
		internal static NativeCallOutcome TrySetStartSearchRenderEachDistance(string bodyId, bool renderEachDistance) =>
			GuardStartSearchExport(() => FT_SetStartSearchRenderEachDistance(bodyId, renderEachDistance ? 1 : 0) == FTErrorCode.OK);

		/// <summary>The length of the reading <see cref="TryGetStartSearchReading"/> fills.</summary>
		internal const int START_SEARCH_READING_LENGTH = 48;

		private static int s_startSearchExport = -1;

		/// <summary>
		/// Whether the loaded plugin can search the whole image for a body before its start. It is unknown (null) until the first
		/// call, and false from the first call that found the plugin without the export.
		/// </summary>
		internal static bool? StartSearchAvailable => s_startSearchExport < 0 ? (bool?)null : s_startSearchExport == 1;

		/// <summary>Forgets what the first call found, so the next call asks the plugin again.</summary>
		internal static void ForgetStartSearchProbe() => s_startSearchExport = -1;

		/// <summary>
		/// Switches the whole-shape start search of a body on and sets its guide in the geometry frame, or with no guide switches it
		/// off. The plugin runs a search every <paramref name="periodSteps"/> tracking steps. A guide generation that differs from
		/// the one the plugin holds makes it build its prerendered views again for the new guide.
		/// </summary>
		/// <returns>
		/// <see cref="NativeCallOutcome.Done"/> when the plugin took the call, <see cref="NativeCallOutcome.Refused"/> when it
		/// refused it (the body is not an edge body without a parent, or a value is not usable), and
		/// <see cref="NativeCallOutcome.Missing"/> when the plugin has no such export. The first missing export is logged once,
		/// and the start then keeps using the guide.
		/// </returns>
		internal static NativeCallOutcome TrySetStartSearch(string bodyId, FTTrackingPose? guide, int periodSteps, int guideGeneration)
		{
			return GuardStartSearchExport(() =>
			{
				if (guide.HasValue)
				{
					FTTrackingPose pose = guide.Value;
					return FT_SetStartSearch(bodyId, ref pose, periodSteps, guideGeneration) == FTErrorCode.OK;
				}
				return FT_ClearStartSearch(bodyId, IntPtr.Zero, periodSteps, guideGeneration) == FTErrorCode.OK;
			});
		}

		/// <summary>
		/// Reads what the search found on the latest step of the body, in the layout of <c>FT_GetStartSearchReading</c>. The reading
		/// must hold <see cref="START_SEARCH_READING_LENGTH"/> floats, and a shorter one is refused without a call.
		/// </summary>
		internal static NativeCallOutcome TryGetStartSearchReading(string bodyId, float[] reading)
		{
			if (reading == null || reading.Length < START_SEARCH_READING_LENGTH)
				return NativeCallOutcome.Refused;
			return GuardStartSearchExport(() => FT_GetStartSearchReading(bodyId, reading, reading.Length));
		}

		/// <summary>
		/// Runs a call into the export and answers how it went. A plugin without the export is found once, logged once, and
		/// never asked again.
		/// </summary>
		internal static NativeCallOutcome GuardStartSearchExport(Func<bool> call)
		{
			if (s_startSearchExport == 0)
				return NativeCallOutcome.Missing;
			try
			{
				bool taken = call();
				s_startSearchExport = 1;
				return taken ? NativeCallOutcome.Done : NativeCallOutcome.Refused;
			}
			catch (EntryPointNotFoundException)
			{
				NoteStartSearchMissing();
				return NativeCallOutcome.Missing;
			}
			catch (DllNotFoundException)
			{
				return NativeCallOutcome.Missing;
			}
		}

		private static void NoteStartSearchMissing()
		{
			if (s_startSearchExport == 0)
				return;
			s_startSearchExport = 0;
			UnityEngine.Debug.LogWarning("[XRTracker] The plugin cannot search the whole image for a body before its start, so the start keeps using the guide.");
		}

		#endregion

		#region Frame Processing

		/// <summary>
		/// Feed frame with intrinsics (injected camera mode).
		/// Intrinsics are updated every frame because the AR session's reported intrinsics drift between frames.
		/// screenRotation: Device orientation relative to camera sensor
		///   0 = LandscapeLeft (sensor native, no rotation)
		///   1 = Portrait (90° CW from sensor)
		///   2 = LandscapeRight (180° from sensor)
		///   3 = PortraitUpsideDown (270° CW from sensor)
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_FeedFrame(IntPtr dataRGB, int width, int height,
		                                       float fu_normalized, float fv_normalized,
		                                       float ppu_normalized, float ppv_normalized,
		                                       int screenRotation);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_TrackStep();

		/// <summary>
		/// Writes the world pose of the AR camera. The caller writes it every frame before the tracking step. It switches
		/// tracking to world space, where body poses are world poses and the regularization holds a body at its world pose.
		/// </summary>
		/// <returns>
		/// <see cref="FTErrorCode.OK"/> when the plugin took the pose, and the status code of the refusal otherwise.
		/// <see cref="FTErrorCode.BUSY"/> is transient, so the caller drops the frame and writes again on the next one.
		/// </returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_SetCameraWorldPose(ref FTTrackingPose pose);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_ExecuteDetection();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_StartTracking();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_StartQualityChecking();

		/// <summary>
		/// Process queued image callback (call from main thread after FT_TrackStep).
		/// Updates the background texture with the frame used for tracking.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_ProcessImageCallback();

		#endregion

		#region Per-Body Tracking Control

		/// <summary>
		/// Start quality checking for a specific body (computes quality but doesn't update pose)
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern void FT_StartQualityCheckingBody(string bodyId);

		/// <summary>
		/// Start tracking a specific body
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern void FT_StartTrackingBody(string bodyId);

		/// <summary>
		/// Stop tracking a specific body
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern void FT_StopTrackingBody(string bodyId);

		/// <summary>
		/// Reset body to initial pose and stop tracking
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_ResetBody(string bodyId);

		#endregion

		#region Utility

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetCameraIntrinsics(
			out float fu, out float fv, out float ppu, out float ppv,
			out int width, out int height);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_EnumerateCameras([Out] FTCameraDevice[] devices, int maxCount);

		#endregion

		#region Depth Frame Injection

		/// <summary>
		/// Feed a depth frame (uint16 data). Only works in injected mode with injected depth camera.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_FeedDepthFrame(IntPtr depthData, int width, int height);

		/// <summary>
		/// Set depth image callback for recording depth frames from RealSense.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SetDepthImageCallback(DepthImageCallback callback, IntPtr userdata);

		/// <summary>
		/// Process queued depth image callback (call from main thread after FT_TrackStep).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_ProcessDepthImageCallback();

		/// <summary>
		/// Get active depth camera intrinsics and depth scale.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetDepthCameraIntrinsics(
			out float fu, out float fv, out float ppu, out float ppv,
			out int width, out int height, out float depthScale);

		/// <summary>
		/// Get depth camera's camera2world_pose (extrinsic transform relative to color camera).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetDepthCameraPose(out FTTrackingPose pose);

		/// <summary>
		/// Set depth camera's camera2world_pose on injected depth camera.
		/// Use when playing back RealSense recordings to restore the depth-to-color transform.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_SetDepthCameraPose(ref FTTrackingPose pose);

		#endregion

		#region Sequence Playback

		/// <summary>
		/// Set sequence directory. Must be called BEFORE FT_Init with mode=Sequence.
		/// C++ reads sequence.json in SetUp and handles all frame I/O internally.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern void FT_SetSequenceDirectory(string directory);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SequencePlay();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SequencePause();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			EntryPoint = "FT_SequenceSeek")]
		private static extern void FT_SequenceSeekNative(int frameIndex);

		/// <summary>
		/// Seeks sequence playback and declares a deterministic lifecycle-clock reset.
		/// Every managed seek goes through this boundary so a caller cannot move the
		/// recorded source without resetting the next completed-frame delta.
		/// </summary>
		internal static void FT_SequenceSeek(int frameIndex)
		{
			Interlocked.Increment(ref s_sequenceSeekEpoch);
			FT_SequenceSeekNative(frameIndex);
		}

		/// <summary>
		/// Puts the sequence camera on the frame that the next tracking step must read.
		/// </summary>
		/// <remarks>
		/// This is a seek that declares no lifecycle-clock reset, because a driven frame is the next frame of the
		/// sequence and not a jump. The camera is left unpaused so that it reads the frame, and the caller pauses it
		/// again once the step has returned.
		/// </remarks>
		/// <param name="frameIndex">The frame the next step must read.</param>
		/// <returns>False for a frame outside the sequence.</returns>
		internal static bool SeekSequenceForDrivenStep(int frameIndex)
		{
			int first = FT_GetSequenceStartIndex();
			int count = FT_GetSequenceFrameCount();
			if (count <= 0 || frameIndex < first || frameIndex >= first + count)
				return false;
			FT_SequenceSeekNative(frameIndex);
			return true;
		}

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SequenceStep(int delta);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			EntryPoint = "FT_SequenceSetLoop")]
		private static extern void FT_SequenceSetLoopNative([MarshalAs(UnmanagedType.U1)] bool loop);

		/// <summary>
		/// Switches the loop of sequence playback and keeps the setting on this side, so the tracker can tell a wrap of the loop
		/// from a jump of the recorded time.
		/// </summary>
		internal static void FT_SequenceSetLoop(bool loop)
		{
			Volatile.Write(ref s_sequenceLoop, loop ? 1 : 0);
			FT_SequenceSetLoopNative(loop);
		}

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SequenceSetPlaybackRange(int startFrame, int endFrame);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetSequenceFrameCount();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetSequenceCurrentFrame();

		/// <summary>
		/// Reads what the last completed native tracking step did. Returns false
		/// before the first step of this session.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetLastTrackStepToken(out FTTrackStepToken token);

		[DllImport(DLL_NAME)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_SequenceSkipRejectedFrame();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetSequenceStartIndex();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.U1)]
		internal static extern bool FT_SequenceHasDepth();

		#endregion

		#region Sequence Recording

		/// <summary>
		/// Start recording frames to the specified directory.
		/// Standalone: works without FT_Init (for SceneView recording in editor).
		/// Live recording auto-captures from cameras in FT_TrackStep.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_StartRecording(string directory);

		/// <summary>
		/// Stop recording, drain write queue, write metadata JSON.
		/// Returns the number of frames recorded.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_StopRecording();

		/// <summary>
		/// Record a frame from external source (SceneView only).
		/// NOT used for live recording — FT_TrackStep auto-records from cameras.
		/// depthData can be IntPtr.Zero if no depth. All intrinsics are normalized.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_RecordFrame(
			IntPtr rgbData, int width, int height,
			float fu, float fv, float ppu, float ppv,
			IntPtr depthData, int depthWidth, int depthHeight,
			float depthFu, float depthFv, float depthPpu, float depthPpv,
			float depthScale);

		/// <summary>
		/// Atomically records a standalone frame and its matching native
		/// camera-to-world pose. Works without FT_Init. Returns only after the full
		/// color/depth batch and matching extrinsics row are committed to disk.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_RecordFrameWithCameraPose(
			IntPtr rgbData, int width, int height,
			float fu, float fv, float ppu, float ppv,
			IntPtr depthData, int depthWidth, int depthHeight,
			float depthFu, float depthFv, float depthPpu, float depthPpv,
			float depthScale, long timestampNs,
			ref FTTrackingPose worldFromCameraNative);

		/// <summary>
		/// Set depth camera extrinsics for recording (position + quaternion).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_SetRecordingDepthPose(
			float px, float py, float pz,
			float qx, float qy, float qz, float qw);

		/// <summary>
		/// Returns 1 if recording, 0 if not.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_IsRecording();

		/// <summary>
		/// Returns the number of color frames recorded so far.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetRecordedFrameCount();

		/// <summary>
		/// Fired after a color frame is committed to disk (PNG + extrinsics line).
		/// NOTE: invoked on the recorder's writer thread, NOT Unity main thread.
		/// Implementations must marshal to main thread if they touch Unity API.
		/// </summary>
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate void FT_FrameRecordedCallback(int frameIndex, long timestampNs);

		/// <summary>
		/// Subscribe to frame-recorded notifications. Pass null to unsubscribe.
		/// Keep a reference to the delegate alive (GC pinning) for the lifetime
		/// of the subscription — the native side holds a raw function pointer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_SetFrameRecordedCallback(FT_FrameRecordedCallback callback);

		#endregion

		#region Occluder Bodies

		/// <summary>
		/// Register a body that only serves as occluder (not tracked).
		/// These bodies contribute to depth/silhouette rendering but are not tracked.
		/// Unity sends world pose each frame via FT_SetOccluderPose.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_RegisterOccluderBody(
			string bodyId,
			[In] float[] vertices,
			int vertexCount,
			[In] int[] indices,
			int triangleCount,
			float geometryUnitInMeter);

		/// <summary>
		/// Unregister an occluder body.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_UnregisterOccluderBody(string bodyId);

		/// <summary>
		/// Writes the world pose of an occluder body. The caller writes it every frame.
		/// </summary>
		/// <returns>The same status codes as <see cref="FT_SetBodyPose"/>.</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_SetOccluderPose(string bodyId, ref FTTrackingPose pose);

		/// <summary>
		/// Get occluder body current world pose.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetOccluderPose(string bodyId, out FTTrackingPose pose);

		/// <summary>
		/// Check if occluder body is registered.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_IsOccluderBodyRegistered(string bodyId);

		#endregion

		#region Correspondence Visualization

		/// <summary>
		/// Get region (silhouette) modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetRegionCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get depth modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetDepthCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get edge modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetEdgeCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get visible model edge lines for debug visualization.
		/// quality = 1.0 for crease, 0.5 for silhouette edge.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetEdgeModelLines(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get texture modality correspondence lines for visualization.
		/// Each line connects the model keypoint to its matched feature in the current frame.
		/// Returns 0 if texture modality is not enabled or not available.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_GetTextureCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		#endregion

		#region Calibration

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_Calib_Start(int cameraId, int width, int height,
			int boardW, int boardH, out int actualWidth, out int actualHeight);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_Calib_Stop();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_Calib_SetImageCallback(ImageCallback callback, IntPtr userdata);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_Calib_ProcessFrame(out int cornersFound,
			[Out] float[] cornersXY, int maxCorners);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_Calib_Capture();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_Calib_GetCaptureCount();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern float FT_Calib_Run(
			out float fx, out float fy, out float cx, out float cy,
			out float fxDist, out float fyDist, out float cxDist, out float cyDist,
			out float k1, out float k2, out float k3, out float p1, out float p2,
			out int outWidth, out int outHeight);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_Calib_Reset();

		#endregion

		#region Pose verification

		/// <summary>
		/// Evaluates the current frame at <paramref name="cameraFromBody16"/> and
		/// fills one <see cref="FTPoseEvidence"/> record.
		/// </summary>
		/// <remarks>
		/// The pose is a row-major 4x4 in metres, in the native convention. The
		/// call takes no optimization step and leaves no tracker state changed;
		/// the record's <c>live_state_unchanged</c> reports the native side's own
		/// field-by-field check of that, and a mismatch fails the call rather than
		/// returning a number. <paramref name="evidenceSize"/> is the size of the
		/// caller's record, which is also the ABI check. Returns
		/// <see cref="FTErrorCode.OK"/> on success and a negative code otherwise.
		/// </remarks>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_EvaluatePoseEvidence(string bodyId,
			float[] cameraFromBody16, int cameraFromBody16Floats,
			ref FTPoseEvidence evidence, int evidenceSize,
			StringBuilder report, int reportSize);

		/// <summary>
		/// The pose the tracker currently holds for one body, row-major 4x4,
		/// metres, in the body frame relative to the camera.
		/// </summary>
		/// <remarks>
		/// This is the frame the pose-evaluation entry points above take, so a
		/// caller scoring the tracked pose passes what this returns and converts
		/// nothing. It is read from the modality rather than composed from the
		/// world poses a caller holds, so it is the pose the tracker actually
		/// worked with rather than a second derivation of it.
		/// </remarks>
		/// <param name="bodyId">The tracked body to read.</param>
		/// <param name="pose16">A 16-element array the pose is written into.</param>
		/// <returns>True when a pose was written.</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetCameraFromBodyPose(string bodyId,
			float[] pose16);

		/// <summary>
		/// One body's geometry-to-body transform, row-major 4x4, metres.
		/// </summary>
		/// <remarks>
		/// A body whose mesh pivot is away from its bounding-box centre is
		/// re-centred when it is set up, so the frame a host authors and reads
		/// poses in and the frame the tracker works in differ by this transform
		/// for the life of the body; it is the identity for a body that needed no
		/// re-centring. A caller holding a geometry-frame pose converts it with
		/// <see cref="PoseVerification.TryToBodyFrame"/> rather than composing
		/// this by hand.
		/// </remarks>
		/// <param name="bodyId">The tracked body to read.</param>
		/// <param name="pose16">A 16-element array the transform is written into.</param>
		/// <param name="count">The caller's element count, which is the ABI check.</param>
		/// <returns>True when a transform was written.</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_GetBodyGeometryToBodyPose(string bodyId,
			float[] pose16, int count);

		/// <summary>
		/// Writes one fixed <see cref="FTPoseEvidence"/> record without touching
		/// tracker state, so the managed and native layouts can be compared field
		/// by field rather than only in total size.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_WritePoseEvidenceAbiDiagnostic(
			ref FTPoseEvidence evidence, int evidenceSize);

		#endregion

		#region Saved-view recovery

		[StructLayout(LayoutKind.Sequential)]
		private struct FTRecoverySeedBankConfig
		{
			public int struct_size;
			public int schema_version;
			public int capacity;
			public float admission_rotation_deg;
			public float admission_translation_m;
			public float admission_max_probe_residual;
			public float admission_min_edge_coverage;
			public int reserved_1;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct FTRecoverySeedBankState
		{
			public int struct_size;
			public int schema_version;
			public int enabled;
			public int capacity;
			public int size;
			public int configuration_epoch;
			public int n_offered;
			public int n_admitted;
			public int n_refused_disabled;
			public int n_refused_not_tracking;
			public int n_refused_no_edge_model;
			public int n_refused_residual;
			public int n_refused_too_close;
			public int n_refused_capacity_full;
			public int last_admitted_entry_id;
			public int last_admitted_frame;
			public int n_refused_below_accept;
			public int n_evicted;
			public int n_appearance_failed;
			public int reserved_0;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct FTSavedViewProposalRequest
		{
			public int struct_size;
			public int schema_version;
			public int mark_seated_entry_id;
			public int mark_window_failed_entry_id;
			public int reserved_0;
			public int reserved_1;
		}

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_ConfigureRecoverySeedBank(string bodyId,
			ref FTRecoverySeedBankConfig config);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetRecoverySeedBankState(string bodyId,
			ref FTRecoverySeedBankState state);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_SeatBodyOnRecoveryView(string bodyId, int attempt);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_ProposeFromSavedViews(string bodyId,
			ref FTSavedViewProposalRequest request, [Out] FTVerifiedCandidate[] candidates,
			int candidatesCapacity, int candidateSize, out int count);

		/// <summary>
		/// Switches a body's seed bank on, or off with a capacity of zero.
		/// </summary>
		internal static bool TryConfigureRecoverySeedBank(string bodyId, int capacity,
			float rotationDeg, float translationM, float maxProbeResidual,
			float minEdgeCoverage)
		{
			if (string.IsNullOrEmpty(bodyId) || capacity < 0) return false;
			var config = new FTRecoverySeedBankConfig
			{
				struct_size = Marshal.SizeOf(typeof(FTRecoverySeedBankConfig)),
				schema_version = 2,
				capacity = capacity,
				admission_rotation_deg = rotationDeg,
				admission_translation_m = translationM,
				admission_max_probe_residual = maxProbeResidual,
				admission_min_edge_coverage = minEdgeCoverage,
			};
			try { return FT_ConfigureRecoverySeedBank(bodyId, ref config) == 0; }
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>
		/// How many views a body's bank holds. False when the bank cannot be read at
		/// all, which is what a plugin without the entry point reports.
		/// </summary>
		internal static bool TryGetRecoverySeedBankSize(string bodyId, out int size)
		{
			size = 0;
			if (string.IsNullOrEmpty(bodyId)) return false;
			var state = new FTRecoverySeedBankState
			{
				struct_size = Marshal.SizeOf(typeof(FTRecoverySeedBankState)),
			};
			try
			{
				if (FT_GetRecoverySeedBankState(bodyId, ref state) != 0) return false;
			}
			catch (EntryPointNotFoundException) { return false; }
			size = state.size;
			return true;
		}

		/// <summary>
		/// Seats a body on one of its banked views. Returns the entry id used, or a
		/// negative value when nothing was seated.
		/// </summary>
		internal static int TrySeatBodyOnRecoveryView(string bodyId, int attempt)
		{
			if (string.IsNullOrEmpty(bodyId)) return -1;
			try { return FT_SeatBodyOnRecoveryView(bodyId, attempt); }
			catch (EntryPointNotFoundException) { return -1; }
		}

		/// <summary>
		/// Asks the plugin what a body's saved views propose for this frame, every
		/// row scored by the verifier, ranked, one of them the pose the body holds.
		/// The two marks carry the caller's outcomes for the last proposal's
		/// candidates back to the views that made them. Seats nothing.
		/// </summary>
		internal static bool TryProposeFromSavedViews(string bodyId, int markSeatedEntryId,
			int markWindowFailedEntryId, FTVerifiedCandidate[] candidates, out int count)
		{
			count = 0;
			if (string.IsNullOrEmpty(bodyId) || candidates == null || candidates.Length == 0)
				return false;
			var request = new FTSavedViewProposalRequest
			{
				struct_size = Marshal.SizeOf(typeof(FTSavedViewProposalRequest)),
				schema_version = 1,
				mark_seated_entry_id = markSeatedEntryId,
				mark_window_failed_entry_id = markWindowFailedEntryId,
			};
			try
			{
				return FT_ProposeFromSavedViews(bodyId, ref request, candidates, candidates.Length,
					Marshal.SizeOf(typeof(FTVerifiedCandidate)), out count) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		#region Evaluation timing

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetPoseEvidenceTiming(string bodyId,
			ref FTPoseEvidenceTiming timing, int timingSize);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetProposalEvaluationTimings(string bodyId,
			[Out] FTPoseEvidenceTiming[] timings, int timingSize, int capacity, out int count);

		/// <summary>Reads the stage times of the last pose evaluation of a body.</summary>
		internal static bool TryGetPoseEvidenceTiming(string bodyId, out FTPoseEvidenceTiming timing)
		{
			timing = default;
			if (string.IsNullOrEmpty(bodyId)) return false;
			try
			{
				return FT_GetPoseEvidenceTiming(bodyId, ref timing,
					Marshal.SizeOf(typeof(FTPoseEvidenceTiming))) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>
		/// Reads the stage times of the rows the last saved-view proposal of a body returned, in the order it
		/// returned them. <paramref name="count"/> is the number of rows the proposal returned.
		/// </summary>
		internal static bool TryGetProposalEvaluationTimings(string bodyId, FTPoseEvidenceTiming[] timings,
			out int count)
		{
			count = 0;
			if (string.IsNullOrEmpty(bodyId) || timings == null || timings.Length == 0) return false;
			try
			{
				return FT_GetProposalEvaluationTimings(bodyId, timings,
					Marshal.SizeOf(typeof(FTPoseEvidenceTiming)), timings.Length, out count) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		#endregion

		#region Reuse of work between pose evaluations

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		private static extern int FT_ConfigureEvidenceSiteCache(int flags, int capacity);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		private static extern int FT_SetEvaluationStartTiming(int enabled);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetPoseEvidenceStartTiming(string bodyId,
			ref FTPoseEvidenceStartTiming timing, int timingSize);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetProposalEvaluationStartTimings(string bodyId,
			[Out] FTPoseEvidenceStartTiming[] timings, int timingSize, int capacity, out int count);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl,
			CharSet = CharSet.Ansi)]
		private static extern int FT_GetEvidenceSiteCacheStats(string bodyId,
			ref FTEvidenceSiteCacheStats stats, int statsSize);

		/// <summary>
		/// Switches the plugin's reuse of work between pose evaluations. <paramref name="flags"/> is
		/// <see cref="EvidenceCacheFlags"/> added together, zero is everything off. Returns false when the plugin
		/// refuses the call or has no such entry point.
		/// </summary>
		internal static bool TryConfigureEvidenceSiteCache(int flags, int capacity)
		{
			try
			{
				return FT_ConfigureEvidenceSiteCache(flags, capacity) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>Switches the clock reads of the start-stage split on or off.</summary>
		internal static bool TrySetEvaluationStartTiming(bool enabled)
		{
			try
			{
				return FT_SetEvaluationStartTiming(enabled ? 1 : 0) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>Reads the split of the start stage of the last pose evaluation of a body.</summary>
		internal static bool TryGetPoseEvidenceStartTiming(string bodyId, out FTPoseEvidenceStartTiming timing)
		{
			timing = default;
			if (string.IsNullOrEmpty(bodyId)) return false;
			try
			{
				return FT_GetPoseEvidenceStartTiming(bodyId, ref timing,
					Marshal.SizeOf(typeof(FTPoseEvidenceStartTiming))) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>
		/// Reads the split of the start stage of the rows the last saved-view proposal of a body returned, in the
		/// order it returned them. <paramref name="count"/> is the number of rows the proposal returned.
		/// </summary>
		internal static bool TryGetProposalEvaluationStartTimings(string bodyId,
			FTPoseEvidenceStartTiming[] timings, out int count)
		{
			count = 0;
			if (string.IsNullOrEmpty(bodyId) || timings == null || timings.Length == 0) return false;
			try
			{
				return FT_GetProposalEvaluationStartTimings(bodyId, timings,
					Marshal.SizeOf(typeof(FTPoseEvidenceStartTiming)), timings.Length, out count) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		/// <summary>Reads what the site cache of a body holds and has done.</summary>
		internal static bool TryGetEvidenceSiteCacheStats(string bodyId, out FTEvidenceSiteCacheStats stats)
		{
			stats = default;
			if (string.IsNullOrEmpty(bodyId)) return false;
			try
			{
				return FT_GetEvidenceSiteCacheStats(bodyId, ref stats,
					Marshal.SizeOf(typeof(FTEvidenceSiteCacheStats))) == 0;
			}
			catch (EntryPointNotFoundException) { return false; }
		}

		#endregion

		#endregion

		#region License

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		internal static extern int FT_SetLicenseData(string signedLicenseJson, int length,
			string machineId);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetLicenseTier();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetLicenseStatus();

		/// <summary>
		/// Seconds left of the free tier, which no license issues any more. The managed property that reads it is obsolete,
		/// and the plugin keeps the call so that property still answers.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern float FT_GetFreeSecondsRemaining();

		/// <summary>
		/// Whether the free tier has run out, which no license issues any more. The managed property that reads it is obsolete,
		/// and the plugin keeps the call so that property still answers.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		internal static extern bool FT_IsLicenseFrozen();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FT_GetLicenseDaysUntilExpiry();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void FT_WatermarkHeartbeat();

		#endregion

		// Validation Zone APIs are in ValidationBridge.cs (IV.FormulaTracker.Validation namespace)
	}

	#region Configuration Structs (shared with C++ - keep naming as-is)

	/// <summary>
	/// The registration quality figures of one pose, which are the counts, residuals, coverage, conditioning
	/// and agreement of the edge sites the search found.
	/// </summary>
	/// <remarks>
	/// Field order and size must match the native <c>FTRegistrationQualityEvidence</c> exactly. Counts are site
	/// counts, every share and ratio is a dimensionless fraction, the residual channels are in pixels along the
	/// search line, and the projection errors are in degrees. A figure carries a measurement only when its bit is
	/// set in <c>availability_mask</c>, so a reader consults the mask and does not take a zero for a measured value.
	/// </remarks>
	// Serializable so Unity can serialize the record whole. The attribute governs engine serialization only, and
	// it changes neither the layout nor the packing that the marshaller and the native header agree on.
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct FTRegistrationQualityEvidence
	{
		public const int StructSize = 212;
		public const int CountsAvailableBit = 1;
		public const int ResidualAvailableBit = 2;
		public const int ProjectionAvailableBit = 4;
		public const int SpatialAvailableBit = 8;
		public const int OrientationAvailableBit = 16;
		public const int AmbiguityAvailableBit = 32;
		public const int ConditioningAvailableBit = 64;
		public const int AppearanceAvailableBit = 128;
		public const int ScoreAvailableBit = 256;
		public const int RegionAvailableBit = 512;
		public const int ContourAvailableBit = 1024;
		public const int ComponentAvailableBit = 2048;
		public const int InitializationConfidenceAvailableBit = 4096;

		public int schema_version;
		public int model_version;
		public int struct_size;
		public int availability_mask;
		public int unavailable_reason;
		public int observation_frame;
		public int n_retained_sites;
		public int n_expected_visible_sites;
		public int n_searched_sites;
		public int n_matched_sites;
		public int n_agreeing_sites;
		public float residual_p50;
		public float residual_p90;
		public float residual_p95;
		public float projection_error_p50;
		public float projection_error_p90;
		public float projection_error_p95;
		public float image_coverage;
		public float silhouette_coverage;
		public float orientation_coverage;
		public int n_ambiguous_sites;
		public float response_margin_p50;
		public int conditioning_available;
		public float conditioning_singular_min;
		public float conditioning_singular_max;
		public float conditioning_inverse;
		public int appearance_available;
		public float appearance_agreement;
		public float legacy_raw_coverage;
		public float instantaneous_score;
		public int n_populated_regions;
		public int n_agreeing_regions;
		public float residual_limit_px;
		public float region_agreement_lower_tail;
		public float region_agreement_harmonic;
		public int n_expected_silhouette_sites;
		public int n_agreeing_silhouette_sites;
		public int n_expected_crease_sites;
		public int n_agreeing_crease_sites;
		public int component_identity_available;
		public float component_capped_agreement;
		public float initialization_agreement_strength;
		public float initialization_distributed_support;
		public float initialization_pose_constraint;
		public float initialization_confidence;

		/// <summary>
		/// The matched-site residual read between the integer search steps
		/// rather than on them. The integer channel above is unchanged and
		/// still drives every decision; this is the same measurement at a
		/// finer resolution.
		/// </summary>
		public float residual_subpixel_p50;
		public float residual_subpixel_p90;
		public float residual_subpixel_p95;

		/// <summary>
		/// How the matched sites' figures were arrived at. The four sum to
		/// <see cref="n_matched_sites"/>, so a reader can tell a resolved
		/// spread from a spread that is still the integer lattice wearing a
		/// finer name.
		/// </summary>
		public int n_residual_subpixel_refined;
		public int n_residual_subpixel_neighbour_missing;
		public int n_residual_subpixel_not_concave;
		public int n_residual_subpixel_vertex_outside;

		/// <summary>
		/// Of the matched sites, how many had the fit centred on a sample other
		/// than the one the search chose. A cross-cut of the same sites, not a
		/// fifth outcome, so it is not part of the four-count sum above.
		/// </summary>
		public int n_residual_subpixel_peak_moved;
	}

	/// <summary>
	/// Evidence returned by one side-effect-free evaluation of a supplied pose.
	/// </summary>
	/// <remarks>
	/// Field order and size must match the native <c>FTPoseEvidence</c> exactly.
	/// The plugin can write a fixed pattern in field order, so the managed and native layouts can be compared
	/// field by field and not only in total size. Counts are site
	/// counts, every share and ratio is a dimensionless fraction in [0, 1], the residual channels are in pixels
	/// along the search line, and <c>projection_error_p50</c> is in degrees.
	///
	/// The conditioning block describes the modality's own 6x6 normal matrix assembled at the supplied pose,
	/// before link projection and before any regularization. It is deliberately not named after the per-frame
	/// pose-solve conditioning, because the two describe the same idea one level apart and must not be compared
	/// with one another. No field in the block may be read while <c>conditioning_available</c> is zero.
	/// </remarks>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct FTPoseEvidence
	{
		public int schema_version;
		public int struct_size;

		public int n_sites;
		public int n_expected_visible;
		public int n_candidate_sites;
		public int n_ambiguous_sites;
		public int n_valid_points;
		public float validity_share;
		public float matched_share;
		public float image_coverage_ratio;
		public float silhouette_coverage_ratio;
		public float orientation_coverage_ratio;
		public float residual_dispersion_major;
		public float residual_dispersion_minor;
		public float residual_p50;
		public float projection_error_p50;

		public int conditioning_available;
		public float normal_matrix_eigenvalue_min;
		public float normal_matrix_eigenvalue_max;
		public float normal_matrix_condition_inverse;
		public int normal_matrix_effective_rank;

		public int live_state_unchanged;
		public float evaluate_ms;
		public float restore_ms;
		public FTRegistrationQualityEvidence registration_quality;
	}

	/// <summary>
	/// One pose the saved-view proposer put to the verifier, with what the
	/// verifier said. The row with is_held_pose set is the pose the body holds.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct FTVerifiedCandidate
	{
		public int struct_size;
		public int entry_id;
		public int is_held_pose;
		public int n_matches;
		public int n_inliers;
		public int reserved_0;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
		public float[] camera_from_body;
		public FTTrackingPose tracker_pose;
		public float acquire_coverage;
		public FTPoseEvidence evidence;

		public static FTVerifiedCandidate[] CreateBuffer(int length)
		{
			var buffer = new FTVerifiedCandidate[Math.Max(1, length)];
			for (int index = 0; index < buffer.Length; ++index)
			{
				buffer[index].struct_size = Marshal.SizeOf(typeof(FTVerifiedCandidate));
				buffer[index].camera_from_body = new float[16];
			}
			return buffer;
		}
	}

	/// <summary>
	/// How long each stage of a pose evaluation took, in milliseconds. A measurement beside the evidence record,
	/// never part of it. The stages are the pose set up and the restore of the probe, the render and the site
	/// creation from the render, the edge search, the assembly of the normal system and the results.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct FTPoseEvidenceTiming
	{
		public const int StructSize = 32;
		public int struct_size;
		public int schema_version;
		public float prepare_ms;
		public float render_ms;
		public float search_ms;
		public float assemble_ms;
		public float results_ms;
		public float total_ms;

		/// <summary>Whether this record was read from the plugin.</summary>
		public bool IsRead => struct_size == StructSize;
	}

	/// <summary>
	/// The bits of the flags <c>FT_ConfigureEvidenceSiteCache</c> takes. Zero is everything off.
	/// </summary>
	internal static class EvidenceCacheFlags
	{
		/// <summary>Keep and reuse the sites of a pose that was evaluated before.</summary>
		public const int Sites = 1;

		/// <summary>Also run the fresh path on a hit and compare the two.</summary>
		public const int Verify = 2;

		/// <summary>Reuse the downscaled search image of a frame across the evaluations made on it.</summary>
		public const int SearchImage = 4;

		/// <summary>Leave out the search at the first rung that the start of an evaluation makes.</summary>
		public const int SkipFirstSearch = 8;
	}

	/// <summary>
	/// The start stage of a pose evaluation split into its parts, a measurement beside
	/// <see cref="FTPoseEvidenceTiming"/> and never part of the evidence. The times are zero unless the host asked
	/// for them with <c>FT_SetEvaluationStartTiming</c>.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	internal struct FTPoseEvidenceStartTiming
	{
		public const int StructSize = 64;
		public const int CacheOff = 0;
		public const int CacheMiss = 1;
		public const int CacheHit = 2;
		public int struct_size;
		public int schema_version;
		public float search_image_ms;
		public float precalculate_ms;
		public float render_ms;
		public float site_creation_ms;
		public float first_search_ms;
		public float snapshot_ms;
		public float start_total_ms;
		public int site_cache;
		public int search_image_reused;
		public int first_search_skipped;
		public int n_sites;
		public int verify_comparisons;
		public int verify_mismatches;
		public int reserved_0;

		/// <summary>Whether this record was read from the plugin.</summary>
		public bool IsRead => struct_size == StructSize;
	}

	/// <summary>What the site cache of one body holds and has done, from the first evaluation of the body.</summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
	internal struct FTEvidenceSiteCacheStats
	{
		public const int StructSize = 168;
		public int struct_size;
		public int schema_version;
		public int flags;
		public int capacity;
		public int entries;
		public int hits;
		public int misses;
		public int evictions;
		public int stores;
		public int clears;
		public int comparisons;
		public int mismatches;
		public int bytes;
		public int bytes_peak;
		public int site_size;
		public int sites_last;
		public int search_image_reuses;
		public int search_image_builds;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 96)]
		public string last_mismatch;

		/// <summary>Whether this record was read from the plugin.</summary>
		public bool IsRead => struct_size == StructSize;
	}


	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTCameraDevice
	{
		public int camera_id;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string name;

		public int width;
		public int height;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct FTCameraIntrinsics
	{
		public int camera_id;
		public int width;
		public int height;

		public float fu_normalized;
		public float fv_normalized;
		public float ppu_normalized;
		public float ppv_normalized;

		public float fu_dist_normalized;
		public float fv_dist_normalized;
		public float ppu_dist_normalized;
		public float ppv_dist_normalized;

		public float k1;
		public float k2;
		public float p1;
		public float p2;
		public float k3;

		public static FTCameraIntrinsics FromARFoundation(
			float focalLengthX, float focalLengthY,
			float principalPointX, float principalPointY,
			int imageWidth, int imageHeight)
		{
			// Normalize by image dimensions
			float fuNorm = focalLengthX / imageWidth;
			float fvNorm = focalLengthY / imageHeight;
			float ppuNorm = principalPointX / imageWidth;
			float ppvNorm = principalPointY / imageHeight;

			return new FTCameraIntrinsics
			{
				camera_id = 0,
				width = imageWidth,
				height = imageHeight,
				// AR Foundation frames are already undistorted
				fu_normalized = fuNorm,
				fv_normalized = fvNorm,
				ppu_normalized = ppuNorm,
				ppv_normalized = ppvNorm,
				// Same as undistorted (no distortion to apply)
				fu_dist_normalized = fuNorm,
				fv_dist_normalized = fvNorm,
				ppu_dist_normalized = ppuNorm,
				ppv_dist_normalized = ppvNorm,
				// No distortion coefficients (frames already rectified)
				k1 = 0f,
				k2 = 0f,
				p1 = 0f,
				p2 = 0f,
				k3 = 0f
			};
		}
	}

	/// <summary>
	/// What one native tracking step did, written by that step.
	/// </summary>
	/// <remarks>
	/// A host that plays back a sequence cannot ask the camera afterwards which
	/// frame a step consumed, because the camera reports the frame it will read next,
	/// and a step that read nothing answers that question the same way as a step that
	/// read one. Every field here is captured inside the step that produced it.
	/// It mirrors the native <c>FTTrackStepToken</c>, and the two sizes must stay equal.
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	internal struct FTTrackStepToken
	{
		/// <summary>Steps entered since initialization, this one included.</summary>
		public long stepSerial;
		/// <summary>Successful sequence image reads since initialization.</summary>
		public long imageLoadSerial;
		/// <summary>The frame the host pinned, or -1 when it did not pin one.</summary>
		public int requestedFrame;
		/// <summary>The sequence frame this step read, or -1 when it read none.</summary>
		public int loadedFrame;
		/// <summary>The camera's next-read index on entry to the step.</summary>
		public int frameIndexBefore;
		/// <summary>The camera's next-read index on exit from the step.</summary>
		public int frameIndexAfter;
		/// <summary>Sequence reads this step performed: 0 or 1.</summary>
		public int imagesLoaded;
		/// <summary>The value the step returned.</summary>
		public int result;
		/// <summary>The tracker iteration this step ran as.</summary>
		public int trackingIteration;
		/// <summary>Camera paused flag on exit.</summary>
		public int cameraPaused;
		/// <summary>Camera pending-step flag on exit.</summary>
		public int cameraPendingStep;
		/// <summary>Camera has-loaded-frame flag on exit.</summary>
		public int cameraHasLoadedFrame;
		/// <summary>The step executed a detecting step for at least one body.</summary>
		public int detectionRan;
		/// <summary>The step executed a starting step for at least one body.</summary>
		public int startingRan;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct FTTrackerConfig
	{
		public int n_corr_iterations;
		public int n_update_iterations;

		public static FTTrackerConfig Default()
		{
			return new FTTrackerConfig
			{
				n_corr_iterations = 5,
				n_update_iterations = 2
			};
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct FTModelConfig
	{
		public float geometry_unit_in_meter;
		public float sphere_radius;
		public int n_divides;
		public int n_points;
		public float max_radius_depth_offset;
		public float stride_depth_offset;
		public int image_size;
		public float viewpoint_min_elevation;
		public float viewpoint_max_elevation;

		public int enable_horizontal_filter;
		public float horizontal_min;
		public float horizontal_max;
		public float forward_axis_x;
		public float forward_axis_y;
		public float forward_axis_z;

		public float up_axis_x;
		public float up_axis_y;
		public float up_axis_z;

		public static FTModelConfig Default()
		{
			return new FTModelConfig
			{
				geometry_unit_in_meter = 0.001f,
				sphere_radius = -1f,
				n_divides = -1,
				n_points = -1,
				max_radius_depth_offset = -1f,
				stride_depth_offset = -1f,
				image_size = -1,
				viewpoint_min_elevation = -90f,
				viewpoint_max_elevation = 90f,
				enable_horizontal_filter = 0,
				horizontal_min = -180f,
				horizontal_max = 180f,
				forward_axis_x = 0f,
				forward_axis_y = 0f,
				forward_axis_z = 1f,
				up_axis_x = 0f,
				up_axis_y = -1f,
				up_axis_z = 0f
			};
		}
	}

	/// <summary>
	/// Advanced silhouette modality settings for multi-scale pyramid configuration.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct FTRegionModalityAdvanced
	{
		public const int MAX_SCALES = 8;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_SCALES)]
		public int[] scales;
		public int scales_count;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_SCALES)]
		public float[] standard_deviations;
		public int standard_deviations_count;

		public float min_continuous_distance;
		public int use_min_continuous_distance;

		public int function_length;
		public int use_function_length;

		public int n_histogram_bins;
		public int use_n_histogram_bins;

		public static FTRegionModalityAdvanced Default()
		{
			return new FTRegionModalityAdvanced
			{
				scales = new[] { 6, 4, 2, 1, 0, 0, 0, 0 },
				scales_count = 4,
				standard_deviations = new[] { 15f, 5f, 3.5f, 1.5f, 0f, 0f, 0f, 0f },
				standard_deviations_count = 4,
				min_continuous_distance = 3f,
				use_min_continuous_distance = 1,
				function_length = 8,
				use_function_length = 1,
				n_histogram_bins = 16,
				use_n_histogram_bins = 0
			};
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct FTBodyConfig
	{
		public float rotation_regularization;
		public float translation_regularization;
		public int enable_texture_modality;
		public int enable_depth_modality;
		public int enable_region_modality;
		public int use_depth_for_occlusion;

		// Silhouette modality tuning
		public float function_amplitude;
		public float learning_rate;

		// Advanced silhouette modality settings
		public FTRegionModalityAdvanced region_advanced;

		// Depth modality tuning
		public float depth_distance_tolerance;
		public float depth_stride_length;
		public float depth_self_occlusion_radius;
		public float depth_self_occlusion_threshold;
		public float depth_measured_occlusion_radius;
		public float depth_measured_occlusion_threshold;

		// Silhouette modality occlusion tuning
		public float region_occlusion_radius;
		public float region_occlusion_threshold;

		// Edge modality (render-based depth-discontinuity edge detection)
		public int enable_edge_modality;
		public float edge_depth_threshold;
		public float edge_tracking_radius;
		public float edge_min_gradient;
		public float edge_sample_step;
		public float edge_crease_threshold_deg;
		public int edge_filter_prediction_sites;
		public float edge_prediction_weight_threshold;

		public float edge_ncc_min_correlation;
		public int edge_use_normal_direction;
		public int edge_use_laplacian_edge_detection;
		public int edge_use_per_site_threshold;
		public float edge_inward_search_ratio;
		public int edge_use_compute_pipeline;
		public int edge_use_illumination_compensation;
		public int edge_search_resolution;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = FTRegionModalityAdvanced.MAX_SCALES)]
		public float[] edge_search_radius_scales;
		public int edge_search_radius_scales_count;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = FTRegionModalityAdvanced.MAX_SCALES)]
		public float[] edge_standard_deviations;
		public int edge_standard_deviations_count;

		public int edge_use_keyframe;
		public int edge_restore_all_keyframe_sites;
		public float edge_keyframe_rotation_deg;
		public float edge_keyframe_translation;
		public float edge_keyframe_depletion_threshold;
		public float edge_probe_search_radius_scale;
		public float edge_probe_max_projection_error_deg;
		public float edge_probe_max_residual_px;
		public int edge_enable_candidate_diagnostics;
		public int edge_enable_site_reliability_diagnostics;
		public int edge_use_selected_candidate_state;
		public int edge_use_distinct_response_candidates;
		public int edge_enable_information_site_diagnostics;
		public int edge_enable_probe_site_diagnostics;

		public static FTBodyConfig Default()
		{
			return new FTBodyConfig
			{
				rotation_regularization = 1000f,
				translation_regularization = 30000f,
				enable_texture_modality = 0,
				enable_depth_modality = 0,
				enable_region_modality = 1,
				use_depth_for_occlusion = 0,
				function_amplitude = 0.43f,
				learning_rate = 1.3f,
				region_advanced = FTRegionModalityAdvanced.Default(),
				depth_distance_tolerance = 0.02f,
				depth_stride_length = 0.005f,
				depth_self_occlusion_radius = 0.01f,
				depth_self_occlusion_threshold = 0.03f,
				depth_measured_occlusion_radius = 0.01f,
				depth_measured_occlusion_threshold = 0.03f,
				region_occlusion_radius = 0.01f,
				region_occlusion_threshold = 0.01f,
				enable_edge_modality = 0,
				edge_depth_threshold = 0.01f,
				edge_tracking_radius = 0.03125f,
				edge_min_gradient = 15f,
				edge_sample_step = 6f,
				edge_crease_threshold_deg = 1000f,
				edge_filter_prediction_sites = 1,
				edge_prediction_weight_threshold = 0.3f,
				edge_ncc_min_correlation = 0.7f,
				edge_use_normal_direction = 1,
				edge_use_laplacian_edge_detection = 1,
				edge_use_per_site_threshold = 1,
				edge_inward_search_ratio = 1f,
				edge_use_compute_pipeline = 1,
				edge_use_illumination_compensation = 1,
				edge_search_resolution = 640,
				edge_search_radius_scales = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_search_radius_scales_count = 0,
				edge_standard_deviations = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_standard_deviations_count = 0,
				edge_use_keyframe = 1,
				edge_restore_all_keyframe_sites = 0,
				edge_keyframe_rotation_deg = 20f,
				edge_keyframe_translation = 0.03f,
				edge_keyframe_depletion_threshold = 0.1f,
				edge_probe_search_radius_scale = 0.3f,
				edge_probe_max_projection_error_deg = 20f,
				edge_probe_max_residual_px = 6f,
				edge_enable_candidate_diagnostics = 0,
				edge_enable_site_reliability_diagnostics = 0,
				edge_use_selected_candidate_state = 0,
				edge_use_distinct_response_candidates = 0,
				edge_enable_information_site_diagnostics = 0,
				edge_enable_probe_site_diagnostics = 0
			};
		}
	}

	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct FTTrackingPose
	{
		public float pos_x;
		public float pos_y;
		public float pos_z;
		public float rot_x;
		public float rot_y;
		public float rot_z;
		public float rot_w;
	}

	/// <summary>
	/// Correspondence line for visualization. Start/end in the native camera frame (X-right, Y-down, Z-forward).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	internal struct FTCorrespondenceLine
	{
		public float start_x, start_y, start_z;
		public float end_x, end_y, end_z;
		public float quality; // 0=bad, 1=good
	}

	/// <summary>
	/// Why the edge tracker replaced its keyframe site set or keyframe pose.
	/// </summary>
	/// <remarks>
	/// The values are append-only, so a stored value keeps its meaning. The reason
	/// separates rebuilds the viewpoint forced
	/// (<see cref="RefreshTranslation"/>, <see cref="RefreshRotation"/>) from rebuilds
	/// the tracker's own state ordered (<see cref="RefreshPending"/>), which is the
	/// distinction between a rebuild that could have caused pose error and one that
	/// could only have followed it.
	/// </remarks>
	internal enum FTEdgeKeyframeReason
	{
		None = 0,
		ModalityStart = 1,
		PredictionUnavailable = 2,
		PredictionRejected = 3,
		NotLocked = 4,
		RefreshPending = 5,
		RefreshTranslation = 6,
		RefreshRotation = 7,
		PersistenceDisabled = 8,
		DepletionUnlock = 9,
		RecoveryConfirmed = 10,

		/// <summary>The live population was answered from a stored view, at no render cost.</summary>
		SwitchStoredView = 11,

		/// <summary>Too few of the live view's sites passed the per-frame visibility test.</summary>
		ViewVisibilityLost = 12,

		/// <summary>Carried as a mask bit beside the frame's reason: the rendered view was stored.</summary>
		StoredView = 13,
	}

	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	internal struct FTBodyStatus
	{
		// Silhouette tracking metrics
		public int n_valid_lines;    // Number of valid correspondence lines found
		public int n_max_lines;      // Maximum expected lines (from config)
		public float mean_variance;  // Average variance of line distributions
		public int is_tracking;      // 1 if body is in tracking state, 0 if detecting

		// Extended silhouette modality metrics
		public float mean_position_error;      // Average |mean| - how far edges are from expected position
		public float variance_stddev;          // Std deviation of variances across lines
		public float min_variance;             // Best (lowest) line variance
		public float max_variance;             // Worst (highest) line variance
		public float distribution_entropy;     // Average entropy of distributions (uncertainty measure)
		public float histogram_discriminability; // Foreground/background separation (0-1, higher = better)

		// Depth tracking metrics
		public int n_valid_points;   // Number of valid depth points found
		public int n_max_points;     // Maximum expected points (from config)
		public int has_depth_modality; // 1 if depth tracking is active, 0 if not (field name kept for native compatibility)
		public float mean_correspondence_distance; // Average 3D distance between model points and depth correspondences (meters)
		public float correspondence_distance_stddev; // Std deviation of correspondence distances (consistency measure)
		public int has_region_modality; // 1 if silhouette modality is active, 0 if not

		// Edge modality metrics
		public int has_edge_modality;     // 1 if edge modality is active, 0 if not
		public int n_valid_edge_points;   // Number of valid edge correspondences (probe coverage)
		public int n_tracking_edge_sites; // Alive tracking sites this frame (after robust biweight weighting)
		public int n_total_edge_sites;    // Total edge sites from last keyframe render
		public float mean_edge_residual;  // Median absolute residual in pixels
		public float edge_projection_error;  // Gradient alignment angle in degrees (0-90)

		// Edge site lifecycle metrics (per completed tracking frame)
		public int n_ncc_prediction_input_sites;
		public int n_ncc_prediction_matched_sites;
		public int n_edge_sites_entering_iteration_1;
		public int edge_keyframe_restoration_executed;
		public int n_edge_sites_after_keyframe_restoration;

		// Keyframe lifecycle observation (per completed tracking frame)
		public int edge_keyframe_events;
		public int edge_keyframe_pose_writes;
		public int edge_keyframe_reason_mask;
		public int edge_keyframe_last_reason;
		public int edge_keyframe_last_corr_iteration;
		public int edge_keyframe_generation;
		public int edge_keyframe_source_frame;
		public int n_edge_keyframe_sites_before;
		public int n_edge_keyframe_sites_after;
		public float edge_keyframe_delta_translation;
		public float edge_keyframe_delta_rotation_deg;

		// Prediction-store census: why sites did not reach the store this frame.
		// The next frame's first-stage prediction reads that store, so a stored
		// count of zero here is what makes the next frame re-render.
		public int edge_prediction_store_input_sites;
		public int edge_prediction_store_stored_sites;
		public int edge_prediction_store_rejected_threshold;
		public int edge_prediction_store_rejected_m_estimator;
		public int edge_prediction_store_rejected_too_near;
		public int edge_prediction_store_rejected_outside;
		public int edge_prediction_store_rejected_occlusion;
		public int edge_prediction_store_rejected_weight;
		public int edge_prediction_store_rejected_strip_bounds;
		public int edge_prediction_store_weight_only_recoverable;
		public int edge_prediction_store_call_index;
		public int edge_prediction_store_iteration;

		// Alive sites at the end of each correspondence iteration this frame.
		// Spelled out rather than declared as an array so the struct stays
		// blittable: FT_GetBodyStatus runs once per body per frame, and a managed
		// array field would push every one of those calls through the marshaller.
		public int edge_sites_alive_corr_0;
		public int edge_sites_alive_corr_1;
		public int edge_sites_alive_corr_2;
		public int edge_sites_alive_corr_3;
		public int edge_sites_alive_corr_4;
		public int edge_sites_alive_corr_5;
		public int edge_sites_alive_corr_6;
		public int edge_sites_alive_corr_7;
		public int edge_corr_iterations_observed;

		// Per-frame acceptance evidence. Emit-only: no decision reads any of it.
		// The three counts are a funnel over one site set and only mean anything
		// read together with n_valid_edge_points, which is what survives it:
		// expected visible >= produced a candidate >= agreed.
		public int n_edge_expected_visible_sites;
		public int n_edge_candidate_sites;
		public int n_edge_ambiguous_sites;
		public float edge_image_coverage_ratio;
		public float edge_silhouette_coverage_ratio;
		public float edge_orientation_coverage_ratio;
		public float edge_residual_dispersion_major;
		public float edge_residual_dispersion_minor;

		// Conditioning of this body's pose-solve system on the frame's last
		// optimisation step. Emit-only. The data spectrum precedes the
		// regularisation diagonal and carries what the measurements constrain;
		// the solved spectrum is the matrix that was decomposed, whose smallest
		// eigenvalue the regulariser bounds from below.
		public int pose_conditioning_valid;
		public float pose_data_singular_min;
		public float pose_data_singular_max;
		public float pose_data_condition_inverse;
		public float pose_solve_singular_min;
		public float pose_solve_singular_max;
		public float pose_solve_condition_inverse;

		// Mean absolute residual, in pixels, over the sites counted by
		// n_edge_candidate_sites. Emit-only: no decision reads it. It is
		// meaningful only when that count is above zero; with no matched site
		// there is no residual to average and this holds 0.0f, which is also the
		// smallest residual a frame can report, so availability comes from the
		// count and never from the value. Appended last, so every earlier field
		// keeps its offset.
		public float edge_probe_mean_abs_residual;

		// Versioned instantaneous registration-quality evidence. Appended so the
		// established 300-byte prefix remains byte-identical.
		public FTRegistrationQualityEvidence registration_quality;

		// View-bank counters, appended so every earlier field keeps its offset.
		// The two counts are cumulative since set-up and partition the population
		// replacements: a render rebuilt it, a reuse answered it from a stored view.
		public int edge_view_bank_renders;
		public int edge_view_bank_reuses;
		public int edge_view_bank_size;

		// Share of the live view's sites that passed this frame's visibility test.
		// -1 says there was no render to test against. That is an absence, not a
		// low fraction: a reader that averages it in records the frames with no
		// measurement as the frames with the worst one.
		public float edge_view_visible_fraction;

		// Where the agreeing evidence sits and which directions it constrains,
		// appended at the end so every earlier field keeps its offset. All three
		// are emit-only: no gate, score or threshold in this package reads any of
		// them.
		//
		// Each reads 0 when the population it describes is empty, and 0 is also a
		// real value for each of them, so a reader gates on the site counts beside
		// them rather than on the value itself.
		//
		// The share of expected-visible sites that produced any candidate at all
		// is not a field: CandidateSiteShare below computes it from the two counts
		// that already cross the boundary, so there is one place it is defined
		// rather than two that can disagree.

		/// <summary>
		/// Share of the 16 angular bins about the projection's centroid that hold
		/// an agreeing site, out of the bins that hold any site the frame could
		/// have measured.
		/// </summary>
		public float edge_agreeing_bin_coverage;

		/// <summary>
		/// Smaller over larger eigenvalue of the orientation tensor of the
		/// agreeing sites' image-plane unit normals, 0 to 1. Near 0 every agreeing
		/// normal lies along one line, which leaves the pose unconstrained across
		/// that line however many sites agree.
		/// </summary>
		public float edge_agreeing_normal_anisotropy;

		/// <summary>
		/// The same ratio with each direction weighted by that site's absolute
		/// residual, which separates a fit that is tight across the edge and
		/// sliding along it from one that is tight in both directions.
		/// </summary>
		public float edge_agreeing_residual_anisotropy;

		/// <summary>
		/// Share of the sites the frame expected to see that produced any
		/// candidate at all. It is a visibility statement, and a distinct one from
		/// a candidate that was found and rejected, which is a statement about
		/// contrast or alignment.
		/// </summary>
		/// <remarks>
		/// Computed here rather than emitted, because the plugin already sends
		/// both counts and a third field carrying their ratio would be a second
		/// thing to keep correct. It is -1 when the frame expected no site at all,
		/// which is an absence and not a share of zero.
		/// </remarks>
		public float CandidateSiteShare => n_edge_expected_visible_sites > 0
			? (float)n_edge_candidate_sites / n_edge_expected_visible_sites
			: -1f;

		/// <summary>
		/// Check if currently in tracking state (optimization running)
		/// </summary>
		public bool IsTracking => is_tracking == 1;

		/// <summary>
		/// Whether this frame's prediction store came out empty, which forces the
		/// next frame to re-render instead of predicting.
		/// </summary>
		public bool PredictionStoreEmpty => edge_prediction_store_stored_sites == 0;

		/// <summary>
		/// Whether the keyframe site set or keyframe pose was replaced this frame.
		/// </summary>
		public bool HasKeyframeEvent => edge_keyframe_events > 0;

		/// <summary>
		/// Whether a reason occurred this frame, from the reason bit mask.
		/// </summary>
		/// <remarks>
		/// A frame can carry more than one event, so the mask is what to read when
		/// asking whether something happened at all. <see cref="edge_keyframe_last_reason"/>
		/// reports only the last one.
		/// </remarks>
		public bool HasKeyframeReason(FTEdgeKeyframeReason reason) =>
			(edge_keyframe_reason_mask & (1 << (int)reason)) != 0;

		/// <summary>
		/// Check if depth tracking is active
		/// </summary>
		public bool HasDepthModality => has_depth_modality == 1;

		/// <summary>
		/// Check if silhouette tracking is active
		/// </summary>
		public bool HasRegionModality => has_region_modality == 1;

		/// <summary>
		/// Check if edge tracking is active
		/// </summary>
		public bool HasEdgeModality => has_edge_modality == 1;

		/// <summary>
		/// Visibility ratio (0-1) - higher means more of the object is visible
		/// </summary>
		public float Visibility => n_max_lines > 0 ? (float)n_valid_lines / n_max_lines : 0f;

		/// <summary>
		/// Depth visibility ratio (0-1) - higher means more depth points are valid
		/// </summary>
		public float DepthVisibility => n_max_points > 0 ? (float)n_valid_points / n_max_points : 0f;

		public override string ToString()
		{
			string result = $"lines: {n_valid_lines}/{n_max_lines}, var: {mean_variance:F2}";
			if (HasDepthModality)
				result += $", depth: {n_valid_points}/{n_max_points}, dist: {mean_correspondence_distance:F4}m";
			if (HasEdgeModality)
				result += $", edge: {n_valid_edge_points}/{n_total_edge_sites}, res: {mean_edge_residual:F1}px";
			return result;
		}
	}

	// Validation zone structs are in ValidationBridge.cs (IV.FormulaTracker.Validation namespace)

	#endregion

	/// <summary>
	/// What the loss guard did on the last step. Emit-only.
	/// </summary>
	/// <remarks>
	/// Nothing in the tracker reads any of this back. It reports every transition of the guard
	/// so that the transitions can be checked. Three of its fields answer three different questions and must not be
	/// conflated: <c>solver_ran</c> says a solve happened, <c>held_pose_valid</c> says
	/// a stored pose exists, and <c>publishable</c> says this frame's pose is a fresh
	/// tracked measurement. Only the third is an acceptance.
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	internal struct FTLossQuarantineState
	{
		/// <summary>Size of this struct, checked by the plugin before it is written.</summary>
		public int struct_size;
		/// <summary>The body carries a loss guard.</summary>
		public int configured;
		/// <summary>The guard is switched on.</summary>
		public int enabled;
		/// <summary>0 ordinary, 1 hold, 2 recovering.</summary>
		public int phase;
		/// <summary>The phase the frame started in.</summary>
		public int previous_phase;
		/// <summary>Which terms fired on this frame.</summary>
		public int trigger_mask;
		/// <summary>Any term fired.</summary>
		public int suspicious;
		/// <summary>This pose is a fresh tracked measurement.</summary>
		public int publishable;
		/// <summary>A stored pose exists. This is not an acceptance.</summary>
		public int held_pose_valid;
		/// <summary>The solver was executed. This is not an acceptance either.</summary>
		public int solver_ran;
		/// <summary>A trusted pose has been established.</summary>
		public int trusted_valid;
		/// <summary>The frame the trusted state last advanced on.</summary>
		public int trusted_frame;
		/// <summary>The frame quarantine froze at, or -1.</summary>
		public int frozen_frame;
		/// <summary>The atomic snapshot is in hand.</summary>
		public int snapshot_held;
		/// <summary>The committed model generation inside the snapshot.</summary>
		public int snapshot_model_generation;
		/// <summary>The keyframe generation inside the snapshot.</summary>
		public int snapshot_keyframe_generation;
		/// <summary>How many sites the snapshot holds.</summary>
		public int snapshot_site_count;
		/// <summary>How many prediction sites the snapshot holds.</summary>
		public int snapshot_prediction_site_count;
		/// <summary>Re-entry attempts made this episode.</summary>
		public int attempts;
		/// <summary>Consecutive agreeing frames in the current attempt.</summary>
		public int confirm_streak;
		/// <summary>0 none, 1 confirmed, 2 evidence, 3 agreement, 4 timeout.</summary>
		public int attempt_outcome;
		/// <summary>Phase transitions this episode.</summary>
		public int transitions;
		/// <summary>Frames spent quarantined.</summary>
		public int quarantined_frames;
		/// <summary>Frames published as fresh measurements.</summary>
		public int published_frames;
		/// <summary>The last frame any term fired on.</summary>
		public int last_trigger_frame;
		/// <summary>The mask that fired then.</summary>
		public int last_trigger_mask;
		/// <summary>The frame of the observation the guard used.</summary>
		public int observation_frame;
		/// <summary>An observation for this frame arrived.</summary>
		public int observation_valid;
		/// <summary>The trusted pose, row-major.</summary>
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
		public float[] trusted_world_from_body;
		/// <summary>Departure from the trusted pose this frame, in metres.</summary>
		public float innovation_translation_m;
		/// <summary>The same, in degrees.</summary>
		public float innovation_rotation_deg;
		/// <summary>How far a recovering frame sits from the frozen pose, in metres.</summary>
		public float agreement_translation_m;
		/// <summary>The same, in degrees.</summary>
		public float agreement_rotation_deg;
		/// <summary>Model fraction the trusted pose puts inside the image.</summary>
		public float trusted_visible_fraction;
		/// <summary>Valid over total edge sites this frame.</summary>
		public float evidence_ratio;

		/// <summary>A declared reference was in hand for this body.</summary>
		public int stationary_reference_valid;
		/// <summary>
		/// Rotation departure of this frame's pose from a declared reference.
		/// Emit-only: no decision reads it back. Negative when nothing was
		/// measured, so a frame that produced no answer cannot be read as zero.
		/// </summary>
		public float reference_departure_rotation_deg;
		/// <summary>The translation counterpart, on the same terms.</summary>
		public float reference_departure_translation_m;

		/// <summary>
		/// How many times the attempt under way has had its confirmation streak
		/// reset. An attempt runs through a failing frame, so the frames that
		/// break a streak are not reported as refusals; this is what counts them.
		/// Emit-only: no decision reads it back.
		/// </summary>
		public int attempt_streak_resets;
		/// <summary>
		/// What broke the streak last: 0 none, 1 no solve, 2 evidence,
		/// 3 agreement. Emit-only, on the same terms.
		/// </summary>
		public int streak_reset_reason;

		/// <summary>A state buffer the plugin will accept.</summary>
		public static FTLossQuarantineState Create()
		{
			return new FTLossQuarantineState
			{
				struct_size = Marshal.SizeOf(typeof(FTLossQuarantineState)),
				trusted_world_from_body = new float[16]
			};
		}

		/// <summary>Where the body stands in the loss lifecycle.</summary>
		public LossQuarantinePhase Phase
		{
			get { return (LossQuarantinePhase)phase; }
		}

		/// <summary>
		/// Whether this frame's pose may reach the published state as a fresh tracked
		/// measurement.
		/// </summary>
		/// <remarks>
		/// A body carrying no guard is not gated by one, so an unconfigured or disabled
		/// guard answers yes and the ordinary path is unchanged.
		/// </remarks>
		public bool AllowsPublishing
		{
			get { return configured == 0 || enabled == 0 || publishable != 0; }
		}
	}

	/// <summary>Where a body stands in the loss lifecycle.</summary>
	internal enum LossQuarantinePhase
	{
		/// <summary>The shipped path.</summary>
		Ordinary = 0,
		/// <summary>Quarantined. The frozen state is held and nothing is published.</summary>
		Hold = 1,
		/// <summary>An attempt is under way from the frozen state.</summary>
		Recovering = 2
	}

	/// <summary>
	/// Screen rotation modes for FT_FeedFrame.
	/// Maps Unity ScreenOrientation to native rotation codes.
	/// </summary>
	public enum FTScreenRotation
	{
		/// <summary>LandscapeLeft - sensor native orientation, no rotation needed</summary>
		LandscapeLeft = 0,
		/// <summary>Portrait - 90° clockwise from sensor</summary>
		Portrait = 1,
		/// <summary>LandscapeRight - 180° from sensor</summary>
		LandscapeRight = 2,
		/// <summary>PortraitUpsideDown - 270° clockwise from sensor</summary>
		PortraitUpsideDown = 3
	}
}
