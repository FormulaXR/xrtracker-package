using System;
using System.Runtime.InteropServices;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Error codes returned by FT_* functions that return int
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
	}

	public static class FTBridge
	{
#if UNITY_IOS && !UNITY_EDITOR
		private const string DLL_NAME = "__Internal";
#else
		private const string DLL_NAME = "formula_tracker";
#endif

		#region Delegates

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void LogCallback(int logLevel, [MarshalAs(UnmanagedType.LPStr)] string message);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void ImageCallback(IntPtr rgbData, int width, int height, IntPtr userdata);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		public delegate void DepthImageCallback(IntPtr depthData, int width, int height, float depthScale, IntPtr userdata);

		#endregion

		#region Core API

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SetLogCallback(LogCallback callback);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SetImageCallback(ImageCallback imageCallback, IntPtr userdata);

		/// <summary>
		/// Initialize tracker with camera intrinsics.
		/// cameraMode: 0=Native (OpenCV), 1=Injected (Unity feeds), 2=RealSense
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_Init(int cameraMode, ref FTCameraIntrinsics cameraIntrinsics, ref FTTrackerConfig trackerConfig);

		/// <summary>
		/// Initialize tracker without intrinsics (for RealSense mode where SDK provides intrinsics).
		/// cameraMode: 0=Native (OpenCV), 1=Injected (Unity feeds), 2=RealSense
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_Init")]
		public static extern int FT_Init(int cameraMode, IntPtr cameraIntrinsics, ref FTTrackerConfig trackerConfig);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_Init")]
		public static extern int FT_InitWithDefaults(int cameraMode, ref FTCameraIntrinsics cameraIntrinsics, IntPtr trackerConfig);

		/// <summary>
		/// Initialize Injected mode with color + optional depth camera in a single call.
		/// Pass IntPtr.Zero for depthIntrinsics for color-only mode.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_InitInjected(ref FTCameraIntrinsics colorIntrinsics,
			ref FTTrackerConfig trackerConfig, ref FTCameraIntrinsics depthIntrinsics, float depthScale);

		/// <summary>
		/// Initialize Injected mode with color only (no depth).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FT_InitInjected")]
		public static extern int FT_InitInjected(ref FTCameraIntrinsics colorIntrinsics,
			ref FTTrackerConfig trackerConfig, IntPtr depthIntrinsics, float depthScale);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_Shutdown();

		/// <summary>
		/// Set RealSense camera resolution. Must be called BEFORE FT_Init with mode=2.
		/// </summary>
		/// <param name="colorResolution">Color resolution (0-3, see RealSenseColorResolution enum)</param>
		/// <param name="depthResolution">Depth resolution (0-3, see RealSenseDepthResolution enum)</param>
		/// <returns>FT_OK on success, FT_ERROR_ALREADY_INITIALIZED if called after init</returns>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_SetRealSenseResolution(int colorResolution, int depthResolution);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SetEdgeRenderResolution(int resolution);

		#endregion

		#region Capability Queries

		/// <summary>
		/// Check if depth camera is available (RealSense mode provides depth).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_HasDepthCamera();

		#endregion

		#region Silhouette Model Generation

		/// <summary>
		/// Generate silhouette model file from mesh data (blocking call - call from background thread)
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_GenerateRegionModel")]
		public static extern int FT_GenerateSilhouetteModel(
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
		public static extern int FT_GenerateSilhouetteModel(
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
		public static extern bool FT_ValidateSilhouetteModel(
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
		public static extern bool FT_ValidateSilhouetteModel(
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
		public static extern int FT_GenerateDepthModel(
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
		public static extern int FT_GenerateDepthModel(
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
		public static extern int FT_GenerateTrackingModels(
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
		public static extern int FT_RegisterBody(
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
		public static extern int FT_UnregisterBody(string bodyId);

		/// <summary>
		/// Get body pose. Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_GetBodyPose(
			string bodyId,
			out FTTrackingPose pose);

		/// <summary>
		/// Set body pose. Used for validation mode to set expected pose.
		/// Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetBodyPose(
			string bodyId,
			ref FTTrackingPose pose);

		/// <summary>
		/// Set detector's initial pose (used when ExecuteDetection or ResetBody is called).
		/// This updates what pose the body will reset to, not the current pose.
		/// Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetDetectorPose(
			string bodyId,
			ref FTTrackingPose pose);

		/// <summary>
		/// Set stability (Tikhonov regularization) parameters at runtime.
		/// Higher values = smoother but slower response.
		/// Returns true if body found and parameters updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetStabilityParameters(
			string bodyId,
			float tikhonovRotation,
			float tikhonovTranslation);

		/// <summary>
		/// Set edge tracking tuning parameters at runtime.
		/// functionAmplitude: Edge uncertainty tolerance (default: 0.43, range: 0.3-0.6). Higher = more forgiving for blurry edges.
		/// learningRate: Pose update step size (default: 1.3, range: 0.5-2.5). Lower = more stable, higher = faster response.
		/// Returns true if body found and parameters updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetModalityParameters(
			string bodyId,
			float functionAmplitude,
			float learningRate);

		/// <summary>
		/// Check if body is registered. Returns true if registered.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_IsBodyRegistered(string bodyId);

		/// <summary>
		/// Get body tracking status. Returns true if body found.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_GetBodyStatus(string bodyId, out FTBodyStatus status);

		#endregion

		#region Body Hierarchy

		/// <summary>
		/// <summary>
		/// Update relative pose of attached child body.
		/// Returns true if child is attached and pose updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetRelativePose(
			string childId,
			ref FTTrackingPose relativePose);

		/// <summary>
		/// Update which DOF are free for attached child body.
		/// freeDirections: DOF flags bitmask (0 = rigid, bits 0-5 = rot_x,y,z,trans_x,y,z)
		/// Returns true if child is attached and DOF updated.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetChildFreeDOF(
			string childId,
			int freeDirections);

		/// <summary>
		/// Get parent body ID. Returns true if child found.
		/// parentIdOut will be empty string if no parent.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_GetParentBody(
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
		public static extern int FT_UpdateBodyGeometry(
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
		public static extern int FT_SetOcclusionEnabled(string bodyId, [MarshalAs(UnmanagedType.I1)] bool enabled);

		/// <summary>
		/// Get whether occlusion is enabled for a body.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_GetOcclusionEnabled(string bodyId);

		/// <summary>
		/// Set silhouette modality occlusion parameters for a body.
		/// Can be called at runtime - will re-setup modality if occlusion is active.
		/// </summary>
		/// <param name="bodyId">Body to update</param>
		/// <param name="radius">Search radius around silhouette points (default: 0.01m)</param>
		/// <param name="threshold">Depth threshold for occlusion (default: 0.01m, use smaller for tight assemblies)</param>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_SetRegionOcclusionParameters")]
		public static extern int FT_SetSilhouetteOcclusionParameters(string bodyId, float radius, float threshold);

		/// <summary>
		/// Add an occluder body for a tracked body.
		/// The tracked body will ignore regions occluded by the occluder.
		/// Note: For OccluderBody instances, use FT_SetOcclusionEnabled instead - they auto-occlude all bodies.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_AddOccluder(string bodyId, string occluderId);

		/// <summary>
		/// Remove an occluder relationship.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_RemoveOccluder(string bodyId, string occluderId);

		/// <summary>
		/// Enable mutual occlusion between two bodies (both occlude each other).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_EnableMutualOcclusion(string bodyAId, string bodyBId);

		/// <summary>
		/// Disable mutual occlusion between two bodies.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_DisableMutualOcclusion(string bodyAId, string bodyBId);

		#endregion

		#region Silhouette Validation

		/// <summary>
		/// Enable silhouette validation using runtime silhouette renderer.
		/// Validates that tracking points are actually on visible silhouette boundaries.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "FT_EnableRegionChecking")]
		public static extern int FT_EnableSilhouetteChecking(string bodyId);

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
		public static extern int FT_SetModalityContribution(string bodyId, [MarshalAs(UnmanagedType.I1)] bool contributes);

		/// <summary>
		/// Lightweight flag to enable/disable whether a body contributes to optimization.
		/// When disabled, the body still tracks and computes quality metrics, but its
		/// gradient/hessian are not added to the optimization solution.
		/// This is efficient for toggling based on tracking quality (no re-setup needed).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetBodyContributesToOptimization(string bodyId, [MarshalAs(UnmanagedType.I1)] bool contributes);

		#endregion

		#region Frame Processing

		/// <summary>
		/// Feed frame with intrinsics (injected camera mode).
		/// Intrinsics are updated every frame to handle ARKit/ARCore intrinsics drift.
		/// screenRotation: Device orientation relative to camera sensor
		///   0 = LandscapeLeft (sensor native, no rotation)
		///   1 = Portrait (90° CW from sensor)
		///   2 = LandscapeRight (180° from sensor)
		///   3 = PortraitUpsideDown (270° CW from sensor)
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_FeedFrame(IntPtr dataRGB, int width, int height,
		                                       float fu_normalized, float fv_normalized,
		                                       float ppu_normalized, float ppv_normalized,
		                                       int screenRotation);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_TrackStep();

		/// <summary>
		/// Set AR camera world pose. Call each frame before FT_TrackStep().
		/// Enables world-space tracking: body poses become world-space, Tikhonov acts as world-space prior.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SetCameraWorldPose(ref FTTrackingPose pose);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_ExecuteDetection();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_StartTracking();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_StartQualityChecking();

		/// <summary>
		/// Process queued image callback (call from main thread after FT_TrackStep).
		/// Updates the background texture with the frame used for tracking.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_ProcessImageCallback();

		#endregion

		#region Per-Body Tracking Control

		/// <summary>
		/// Start quality checking for a specific body (computes quality but doesn't update pose)
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern void FT_StartQualityCheckingBody(string bodyId);

		/// <summary>
		/// Start tracking a specific body
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern void FT_StartTrackingBody(string bodyId);

		/// <summary>
		/// Stop tracking a specific body
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern void FT_StopTrackingBody(string bodyId);

		/// <summary>
		/// Reset body to initial pose and stop tracking
		/// Returns: FT_OK on success, error code on failure
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_ResetBody(string bodyId);

		#endregion

		#region Utility

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetCameraIntrinsics(
			out float fu, out float fv, out float ppu, out float ppv,
			out int width, out int height);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_EnumerateCameras([Out] FTCameraDevice[] devices, int maxCount);

		#endregion

		#region Depth Frame Injection

		/// <summary>
		/// Feed a depth frame (uint16 data). Only works in injected mode with injected depth camera.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_FeedDepthFrame(IntPtr depthData, int width, int height);

		/// <summary>
		/// Set depth image callback for recording depth frames from RealSense.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SetDepthImageCallback(DepthImageCallback callback, IntPtr userdata);

		/// <summary>
		/// Process queued depth image callback (call from main thread after FT_TrackStep).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_ProcessDepthImageCallback();

		/// <summary>
		/// Get active depth camera intrinsics and depth scale.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetDepthCameraIntrinsics(
			out float fu, out float fv, out float ppu, out float ppv,
			out int width, out int height, out float depthScale);

		/// <summary>
		/// Get depth camera's camera2world_pose (extrinsic transform relative to color camera).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetDepthCameraPose(out FTTrackingPose pose);

		/// <summary>
		/// Set depth camera's camera2world_pose on injected depth camera.
		/// Use when playing back RealSense recordings to restore the depth-to-color transform.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_SetDepthCameraPose(ref FTTrackingPose pose);

		#endregion

		#region Sequence Playback

		/// <summary>
		/// Set sequence directory. Must be called BEFORE FT_Init with mode=Sequence.
		/// C++ reads sequence.json in SetUp and handles all frame I/O internally.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern void FT_SetSequenceDirectory(string directory);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequencePlay();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequencePause();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequenceSeek(int frameIndex);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequenceStep(int delta);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequenceSetLoop([MarshalAs(UnmanagedType.U1)] bool loop);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_SequenceSetPlaybackRange(int startFrame, int endFrame);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetSequenceFrameCount();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetSequenceCurrentFrame();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetSequenceStartIndex();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.U1)]
		public static extern bool FT_SequenceHasDepth();

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
		public static extern int FT_RecordFrame(
			IntPtr rgbData, int width, int height,
			float fu, float fv, float ppu, float ppv,
			IntPtr depthData, int depthWidth, int depthHeight,
			float depthFu, float depthFv, float depthPpu, float depthPpv,
			float depthScale);

		/// <summary>
		/// Set depth camera extrinsics for recording (position + quaternion).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_SetRecordingDepthPose(
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

		#endregion

		#region Occluder Bodies

		/// <summary>
		/// Register a body that only serves as occluder (not tracked).
		/// These bodies contribute to depth/silhouette rendering but are not tracked.
		/// Unity sends world pose each frame via FT_SetOccluderPose.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_RegisterOccluderBody(
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
		public static extern int FT_UnregisterOccluderBody(string bodyId);

		/// <summary>
		/// Set occluder body world pose (call each frame from Unity).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_SetOccluderPose(string bodyId, ref FTTrackingPose pose);

		/// <summary>
		/// Get occluder body current world pose.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_GetOccluderPose(string bodyId, out FTTrackingPose pose);

		/// <summary>
		/// Check if occluder body is registered.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_IsOccluderBodyRegistered(string bodyId);

		#endregion

		#region Correspondence Visualization

		/// <summary>
		/// Get region (silhouette) modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetRegionCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get depth modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetDepthCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get edge modality correspondence lines for visualization.
		/// Returns the number of lines written to the buffer.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetEdgeCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get visible model edge lines for debug visualization.
		/// quality = 1.0 for crease, 0.5 for silhouette edge.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetEdgeModelLines(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		/// <summary>
		/// Get texture modality correspondence lines for visualization.
		/// Each line connects the model keypoint to its matched feature in the current frame.
		/// Returns 0 if texture modality is not enabled or not available.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetTextureCorrespondences(
			string bodyId,
			[Out] FTCorrespondenceLine[] buffer,
			int maxCount);

		#endregion

		#region Calibration

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_Calib_Start(int cameraId, int width, int height,
			int boardW, int boardH, out int actualWidth, out int actualHeight);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_Calib_Stop();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_Calib_SetImageCallback(ImageCallback callback, IntPtr userdata);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_Calib_ProcessFrame(out int cornersFound,
			[Out] float[] cornersXY, int maxCorners);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_Calib_Capture();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_Calib_GetCaptureCount();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern float FT_Calib_Run(
			out float fx, out float fy, out float cx, out float cy,
			out float fxDist, out float fyDist, out float cxDist, out float cyDist,
			out float k1, out float k2, out float k3, out float p1, out float p2,
			out int outWidth, out int outHeight);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_Calib_Reset();

		#endregion

		#region License

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_SetLicenseData(string signedLicenseJson, int length,
			string machineId);

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetLicenseTier();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetLicenseStatus();

		[System.Obsolete("Free tier deprecated.")]
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern float FT_GetFreeSecondsRemaining();

		[System.Obsolete("Free tier deprecated.")]
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		[return: MarshalAs(UnmanagedType.I1)]
		public static extern bool FT_IsLicenseFrozen();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetLicenseDaysUntilExpiry();

		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_WatermarkHeartbeat();

		#endregion

		// Validation Zone APIs are in ValidationBridge.cs (IV.FormulaTracker.Validation namespace)
	}

	#region Configuration Structs (shared with C++ - keep naming as-is)

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
		public float tikhonov_rotation;
		public float tikhonov_translation;
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

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = FTRegionModalityAdvanced.MAX_SCALES)]

		public int edge_use_keyframe;
		public int edge_restore_all_keyframe_sites;
		public float edge_keyframe_rotation_deg;
		public float edge_keyframe_translation;
		public float edge_keyframe_depletion_threshold;
		public float edge_probe_search_radius_scale;
		public float edge_probe_max_projection_error_deg;
		public float edge_probe_max_residual_px;

		public static FTBodyConfig Default()
		{
			return new FTBodyConfig
			{
				tikhonov_rotation = 1000f,
				tikhonov_translation = 30000f,
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
				edge_keyframe_rotation_deg = 3f,
				edge_keyframe_translation = 0.03f,
				edge_keyframe_depletion_threshold = 0.1f,
				edge_probe_search_radius_scale = 0.3f,
				edge_probe_max_projection_error_deg = 20f,
				edge_probe_max_residual_px = 6f
			};
		}
	}

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
	/// Correspondence line for visualization. Start/end in M3T camera space (X-right, Y-down, Z-forward).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct FTCorrespondenceLine
	{
		public float start_x, start_y, start_z;
		public float end_x, end_y, end_z;
		public float quality; // 0=bad, 1=good
	}

	/// <summary>
	/// Raw metrics from silhouette and depth tracking - quality calculation done in C#
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct FTBodyStatus
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
		public int n_tracking_edge_sites; // Alive tracking sites this frame (after Tukey)
		public int n_total_edge_sites;    // Total edge sites from last keyframe render
		public float mean_edge_residual;  // Median absolute residual in pixels
		public float edge_projection_error;  // Gradient alignment angle in degrees (0-90)

		/// <summary>
		/// Check if currently in tracking state (optimization running)
		/// </summary>
		public bool IsTracking => is_tracking == 1;

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