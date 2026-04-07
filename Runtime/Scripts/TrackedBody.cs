using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

namespace IV.FormulaTracker
{
	public enum TrackingMethod
	{
		Silhouette = 0,
		Edge = 1
	}

	[DefaultExecutionOrder(1)]
	public class TrackedBody : MonoBehaviour
	{
		#region Serialized Fields

		[Tooltip("Unique identifier for this tracked object")] [SerializeField]
		private string _bodyId;

		[Tooltip("MeshFilters containing the 3D model(s) to track. Multiple meshes will be combined using their local transforms.")] [SerializeField]
		private List<MeshFilter> _meshFilters = new List<MeshFilter>();

		// Tracking Model section - drawn by custom editor
		[FormerlySerializedAs("_silhouetteModelAsset")] [SerializeField]
		private TrackingModelAsset _trackingModelAsset;

		[SerializeField] private ModelSettings _modelSettings = new ModelSettings();

		// Parent body - auto-detected from Unity hierarchy (not serialized)
		private TrackedBody _parentBody;

		[Tooltip("Which degrees of freedom can be optimized when attached to parent. None = rigid attachment.")] [SerializeField]
		private TrackedMotion _trackedMotion = TrackedMotion.None;

		[Tooltip("When enabled, this child body will occlude its parent during tracking. " +
		         "Use this when assembling parts that cover portions of the parent. " +
		         "Requires the parent to have 'Enable Occlusion' enabled.")]
		[SerializeField]
		private bool _occludeParent;

		[Tooltip("How to determine the initial pose for detection")] [SerializeField]
		private InitialPoseSource _initialPoseSource = InitialPoseSource.ScenePosition;

		[Tooltip("Viewpoint transform to calculate initial pose from. The object's pose will be computed relative to this viewpoint.")] [SerializeField]
		private Transform _initialViewpoint;

		[Tooltip("Object is fixed in the world. Enables AR pose fusion to maintain position when tracking quality drops.")]
		[SerializeField]
		private bool _isStationary;

		[Tooltip("Time in seconds to smooth pose corrections. 0 = instant.")]
		[SerializeField]
		private float _smoothTime = TrackerDefaults.DEFAULT_SMOOTH_TIME;

		[Tooltip("Rotation stability (Tikhonov regularization). Higher = smoother rotation, slower response to rotation changes.")] [Range(1, 50000)] [SerializeField]
		private float _rotationStability = 5000;

		[Tooltip("Position stability (Tikhonov regularization). Higher = smoother position, slower response to position changes.")] [Range(1, 100000)] [SerializeField]
		private float _positionStability = 30000f;

		[Tooltip("Silhouette tracking parameters.")] [SerializeField]
		[FormerlySerializedAs("_edgeTracking")]
		private SilhouetteTrackingSettings _silhouetteTracking = new();

		[Tooltip("Multi-scale pyramid settings. Adjust for thin objects at distance or when object appears small in image.")] [SerializeField]
		private MultiScaleSettings _multiScale = new();

		[Tooltip("Advanced depth tracking tuning. Adjust when using RealSense depth camera.")] [SerializeField]
		private DepthTrackingSettings _depthTracking = new();

		[Tooltip("Use custom quality threshold to start tracking. Default: 0.5")] [SerializeField]
		private bool _useCustomStartThreshold;

		[Tooltip("Custom quality threshold (0-1) required to start tracking. Default is 0.5.")] [Range(0f, 1f)] [SerializeField]
		private float _customQualityToStart = 0.5f;

		[Tooltip("Use custom quality threshold to stop tracking.")] [SerializeField]
		private bool _useCustomStopThreshold;

		[Tooltip("Custom quality threshold (0-1) below which tracking is lost. Default depends on modality.")] [Range(0f, 1f)] [SerializeField]
		private float _customQualityToStop = 0.3f;

		[Tooltip("Enable for parts that will be physically installed during operation. The child won't affect parent pose until quality confirms the part is present.")]
		[SerializeField]
		private bool _assemblyMode;

		[Tooltip("Quality threshold (0-1) required to confirm the part is installed and start affecting parent pose.")] [Range(0f, 1f)] [SerializeField]
		private float _assemblyQualityToConfirm = 0.4f;

		[Tooltip("Validate tracking points against silhouette boundaries. Uses shared silhouette renderer - significant GPU overhead on mobile.")]
		[FormerlySerializedAs("_enableContourValidation")]
		[SerializeField]
		private bool _enableSilhouetteValidation;

		[Tooltip("Enable feature-based tracking (ORB keypoints) in addition to silhouette tracking. " +
		         "Note: This does NOT use the object's Unity texture/material. Instead, it detects visual features " +
		         "(keypoints) on the object's surface at runtime from the camera image. " +
		         "Improves tracking for objects with distinctive surface patterns. Requires GPU for silhouette rendering.")]
		[FormerlySerializedAs("_enableTextureModality")]
		[SerializeField]
		private bool _enableTextureTracking;

		[Tooltip("Enable depth-based occlusion handling for this body's tracking. " +
		         "When enabled, silhouette points hidden behind other objects (occluders) are ignored. " +
		         "Child bodies with 'Occlude Parent' enabled will automatically occlude this body.")]
		[SerializeField]
		private bool _enableOcclusion;

		[Tooltip("Occlusion detection parameters. Controls how the tracker determines when " +
		         "silhouette points are occluded by other objects. Adjust threshold for tight assemblies.")]
		[SerializeField]
		private OcclusionSettings _occlusionSettings = new();

		[Tooltip("Silhouette: best when a good portion of the outline is visible with good contrast. Handles faster motion.\n" +
		         "Edge: works in low-contrast scenes and when mostly internal edges are visible, like when close to large machinery. Also works with external edges but less resilient to fast motion.")]
		[SerializeField]
		private TrackingMethod _trackingMethod = TrackingMethod.Silhouette;

		[Tooltip("Enable depth tracking using RealSense depth camera. " +
		         "Requires a depth model in the Tracking Model Asset and RealSense camera mode.")]
		[FormerlySerializedAs("_enableDepthModality")]
		[FormerlySerializedAs("_trackingMode")]
		[SerializeField]
		private bool _enableDepthTracking;

		// Derived from _trackingMethod — kept for C++ ABI compatibility
		private bool _enableSilhouetteTracking => _trackingMethod == TrackingMethod.Silhouette;
		private bool _enableEdgeTracking => _trackingMethod == TrackingMethod.Edge;

		[Tooltip("Advanced edge modality tuning parameters.")] [SerializeField, FormerlySerializedAs("_edgeModality")]
		private EdgeTrackingSettings _edgeTracking = new();

		[SerializeField] private UnityEvent _onStartTracking = new();
		[SerializeField] private UnityEvent _onStopTracking = new();

		[SerializeField] private UnityEvent _onBecomeValid = new();
		[SerializeField] private UnityEvent _onBecomeInvalid = new();

		#endregion

		#region Private Fields

		private bool _isRegistered;
		private bool _isAttached;
		private bool _isTracking;
		private FTTrackingPose _initialPose;
		private Vector3 _initialCameraRelativePosition;
		private Quaternion _initialCameraRelativeRotation = Quaternion.identity;

		private bool _silhouetteValidationEnabled;
		private bool _textureTrackingEnabled;
		private bool _occlusionEnabled;
		private bool _depthTrackingEnabled;
		private bool _contributesToOptimization = true;
		private bool? _lastContributionState; // null = not yet sent to native
		private readonly List<TrackedBody> _childBodies = new();

		private FTBodyStatus _lastStatus;

		private TrackingStatus _trackingStatus;

		// Quality averaging buffers for stability (raw values, not remapped)
		private const int QUALITY_BUFFER_SIZE = 30;
		private float[] _histogramBuffer;
		private float[] _varianceStdDevBuffer;
		private float[] _correspondenceDistBuffer;
		private float[] _edgeResidualBuffer;
		private float[] _projectionErrorBuffer;
		private float[] _edgeCoverageBuffer;
		private int _qualityBufferIndex;
		private int _qualityBufferCount;

		// Custom attachment pose override
		private bool _useCustomAttachmentPose;
		private Vector3 _customAttachmentPosition;
		private Quaternion _customAttachmentRotation = Quaternion.identity;

		// Lifecycle version counter for cancelling stale async register/unregister operations.
		// Incremented on every OnEnable/OnDisable. If a captured version doesn't match after an await,
		// the operation is stale (a newer enable/disable occurred) and should bail out.
		private int _lifecycleVersion;

		#endregion

		#region Public Properties

		public string BodyId
		{
			get => _bodyId;
			set => _bodyId = value;
		}

		/// <summary>
		/// MeshFilters for the model geometry. Must be set before OnEnable.
		/// </summary>
		public List<MeshFilter> MeshFilters
		{
			get => _meshFilters;
			set => _meshFilters = value ?? new List<MeshFilter>();
		}

		/// <summary>
		/// Pre-generated silhouette model asset. Must be set before OnEnable.
		/// </summary>
		public TrackingModelAsset TrackingModelAsset
		{
			get => _trackingModelAsset;
			set => _trackingModelAsset = value;
		}

		/// <summary>
		/// Model generation settings. Must be set before OnEnable.
		/// </summary>
		public ModelSettings ModelSettings
		{
			get => _modelSettings;
			set => _modelSettings = value ?? new ModelSettings();
		}

		/// <summary>
		/// Whether this object is stationary in the world. Enables AR pose fusion.
		/// </summary>
		public bool IsStationary
		{
			get => _isStationary;
			set => _isStationary = value;
		}

		/// <summary>
		/// Time in seconds to smooth pose corrections. 0 = instant.
		/// </summary>
		public float SmoothTime
		{
			get => _smoothTime;
			set => _smoothTime = Mathf.Max(0f, value);
		}

		/// <summary>
		/// Rotation stability (Tikhonov regularization). Can be set before OnEnable.
		/// </summary>
		public float RotationStability
		{
			get => _rotationStability;
			set => _rotationStability = Mathf.Clamp(value, 100f, 50000f);
		}

		/// <summary>
		/// Position stability (Tikhonov regularization). Can be set before OnEnable.
		/// </summary>
		public float PositionStability
		{
			get => _positionStability;
			set => _positionStability = Mathf.Clamp(value, 1000f, 100000f);
		}

		/// <summary>
		/// Advanced edge tracking tuning settings. Adjust for difficult objects like thin tubes.
		/// Changes can be applied at runtime via UpdateEdgeTrackingParameters().
		/// </summary>
		public SilhouetteTrackingSettings SilhouetteTracking => _silhouetteTracking;

		/// <summary>
		/// Multi-scale pyramid settings. Adjust for thin objects at distance or when object appears small in image.
		/// Must be set before registration - runtime changes not currently supported.
		/// </summary>
		public MultiScaleSettings MultiScale => _multiScale;

		/// <summary>
		/// Advanced depth tracking tuning settings. Adjust when using RealSense depth camera.
		/// Must be set before registration - runtime changes not currently supported.
		/// </summary>
		public DepthTrackingSettings DepthTracking => _depthTracking;

		/// <summary>
		/// Enable silhouette validation (GPU overhead). Can be set before OnEnable.
		/// </summary>
		public bool EnableSilhouetteValidation
		{
			get => _enableSilhouetteValidation;
			set => _enableSilhouetteValidation = value;
		}

		/// <summary>
		/// Enable texture tracking (ORB keypoints). Must be set before OnEnable.
		/// </summary>
		public bool EnableTextureTracking
		{
			get => _enableTextureTracking;
			set => _enableTextureTracking = value;
		}

		/// <summary>
		/// Enable occlusion handling. Can be set before OnEnable.
		/// </summary>
		public bool EnableOcclusion
		{
			get => _enableOcclusion;
			set => _enableOcclusion = value;
		}

		/// <summary>
		/// Occlusion detection parameters for this body.
		/// Adjust threshold for tight assemblies where parts are close together.
		/// </summary>
		public OcclusionSettings OcclusionSettings => _occlusionSettings;

		/// <summary>
		/// When true, this child body will occlude its parent during tracking.
		/// Requires the parent to have EnableOcclusion set to true.
		/// </summary>
		public bool OccludeParent
		{
			get => _occludeParent;
			set => _occludeParent = value;
		}

		/// <summary>
		/// Tracking algorithm selection (Silhouette, Edge, or both).
		/// </summary>
		public TrackingMethod TrackingMethod
		{
			get => _trackingMethod;
			set => _trackingMethod = value;
		}

		/// <summary>
		/// Whether silhouette tracking is active (derived from TrackingMethod).
		/// </summary>
		public bool EnableSilhouetteTracking => _enableSilhouetteTracking;

		/// <summary>
		/// Enable depth tracking. Requires RealSense camera mode and depth model.
		/// </summary>
		public bool EnableDepthTracking
		{
			get => _enableDepthTracking;
			set => _enableDepthTracking = value;
		}

		/// <summary>
		/// Whether edge tracking is active (derived from TrackingMethod).
		/// </summary>
		public bool EnableEdgeTracking => _enableEdgeTracking;

		/// <summary>
		/// Edge tracking settings for tuning crease angle, search length, etc.
		/// </summary>
		public EdgeTrackingSettings EdgeTracking => _edgeTracking;

		/// <summary>
		/// Initial pose source configuration. Must be set before OnEnable.
		/// </summary>
		public InitialPoseSource InitialPoseSource
		{
			get => _initialPoseSource;
			set => _initialPoseSource = value;
		}

		/// <summary>
		/// Initial viewpoint transform (used when InitialPoseSource is Viewpoint). Must be set before OnEnable.
		/// </summary>
		public Transform InitialViewpoint
		{
			get => _initialViewpoint;
			set => _initialViewpoint = value;
		}

		public bool IsRegistered => _isRegistered;

		/// <summary>
		/// Parent body this object is attached to (null if independent).
		/// </summary>
		public TrackedBody ParentBody => _parentBody;

		/// <summary>
		/// Whether this body has a parent assigned (for inspector visibility).
		/// </summary>
		public bool HasParent => _parentBody != null;

		/// <summary>
		/// Whether this body has active tracking (at least one DOF enabled or no parent).
		/// True if no parent (always active) or if any TrackedMotion flags are set.
		/// </summary>
		public bool IsActiveMode => _parentBody == null || _trackedMotion != TrackedMotion.None;

		/// <summary>
		/// Current tracked motion configuration for attached bodies.
		/// Can be set before OnEnable for initial configuration.
		/// </summary>
		public TrackedMotion TrackedMotion
		{
			get => _trackedMotion;
			set => _trackedMotion = value;
		}

		/// <summary>
		/// Event fired when tracking starts.
		/// </summary>
		public UnityEvent OnStartTracking => _onStartTracking;

		/// <summary>
		/// Event fired when tracking stops.
		/// </summary>
		public UnityEvent OnStopTracking => _onStopTracking;

		/// <summary>
		/// Whether this body is currently in the tracking state.
		/// All modes use quality-based state management with frame hysteresis.
		/// </summary>
		public bool IsTracking => _isTracking;

		/// <summary>
		/// Averaged histogram discriminability over 30 frames.
		/// Used internally for global peak tracking and quality calculation.
		/// </summary>
		internal float HistogramAverage => GetBufferAverage(_histogramBuffer);

		/// <summary>
		/// Averaged variance StdDev over 30 frames.
		/// Used internally for shape quality calculation.
		/// </summary>
		internal float VarianceStdDevAverage => GetBufferAverage(_varianceStdDevBuffer);

		/// <summary>
		/// Averaged correspondence distance over 30 frames.
		/// Used for depth-based quality calculation.
		/// </summary>
		internal float CorrespondenceDistanceAverage => GetBufferAverage(_correspondenceDistBuffer);

		/// <summary>
		/// Averaged edge residual over 30 frames (pixels).
		/// Used for edge-based quality calculation.
		/// </summary>
		internal float EdgeResidualAverage => GetBufferAverage(_edgeResidualBuffer);

		/// <summary>
		/// Averaged projection error over 30 frames (degrees).
		/// </summary>
		public float ProjectionErrorAverage => GetBufferAverage(_projectionErrorBuffer);

		/// <summary>
		/// Averaged edge site coverage (valid/total) over 30 frames [0,1].
		/// Ratio of valid edge points to total keyframe sites.
		/// </summary>
		public float EdgeCoverageAverage => GetBufferAverage(_edgeCoverageBuffer);

		/// <summary>
		/// <summary>
		/// Current tracking quality (0-1). Higher is better. Averaged over 30 frames for stability.
		/// Uses the best quality across all active modalities.
		/// </summary>
		public float TrackingQuality
		{
			get
			{
				float quality = SilhouetteTrackingQuality;
				if (_enableDepthTracking)
					quality = Mathf.Max(quality, DepthTrackingQuality);
				if (_enableEdgeTracking)
					quality = Mathf.Max(quality, EdgeTrackingQuality);
				return quality;
			}
		}

		public float EdgeTrackingQuality
		{
			get
			{
				if (_enableEdgeTracking && _lastStatus.HasEdgeModality)
				{
					return Mathf.InverseLerp(0f, TrackerDefaults.EDGE_QUALITY_COVERAGE_MAX, EdgeCoverageAverage);
				}

				return 0;
			}
		}

		public float DepthTrackingQuality
		{
			get
			{
				if (_enableDepthTracking && _lastStatus.HasDepthModality)
				{
					float depthVis = DepthVisibility;
					// Before tracking starts, require 10% visibility — a handful of random
					// depth coincidences shouldn't produce meaningful quality.
					// Once tracking, even low visibility is fine (partial occlusion, etc.).
					float minVis = _isTracking ? 0.01f : 0.10f;
					if (depthVis < minVis) return 0;
					return 1f - Mathf.Clamp01(CorrespondenceDistanceAverage / _depthTracking.DistanceTolerance);
				}

				return 0;
			}
		}

		public float SilhouetteTrackingQuality
		{
			get
			{
				// Standard silhouette-based quality
				if (Visibility <= 0)
					return 0;

				float globalPeak = TrackedBodyManager.Instance?.GlobalHistogramPeak ?? TrackerDefaults.HISTOGRAM_GOOD;
				return Mathf.InverseLerp(TrackerDefaults.HISTOGRAM_BAD, globalPeak, HistogramAverage);
			}
		}

		/// <summary>
		/// Shape quality (0-1). Higher is better. Averaged over 30 frames for stability.
		/// Based on variance StdDev - measures how consistently the model edges align across the contour.
		/// Low StdDev = consistent fit everywhere = correct model.
		/// High StdDev = inconsistent fit = wrong model or poor viewing angle.
		/// </summary>
		public float ShapeQuality
		{
			get
			{
				if (!_isTracking || _lastStatus.Visibility <= 0)
					return 0;

				return Mathf.InverseLerp(TrackerDefaults.SD_MAX, TrackerDefaults.SD_MIN, VarianceStdDevAverage);
			}
		}

		/// <summary>
		/// Visibility ratio (0-1). Ratio of visible silhouette lines.
		/// </summary>
		public float Visibility => _lastStatus.n_max_lines > 0 ? (float)_lastStatus.n_valid_lines / _lastStatus.n_max_lines : 0f;

		/// <summary>
		/// Depth visibility ratio (0-1). Ratio of valid depth points.
		/// </summary>
		public float DepthVisibility => _lastStatus.n_max_points > 0 ? (float)_lastStatus.n_valid_points / _lastStatus.n_max_points : 0f;

		/// <summary>
		/// Raw status from native tracker.
		/// </summary>
		public FTBodyStatus LastStatus => _lastStatus;

		/// <summary>
		/// Whether silhouette validation is currently enabled.
		/// </summary>
		public bool IsSilhouetteValidationEnabled => _silhouetteValidationEnabled;

		/// <summary>
		/// Whether texture tracking (feature-based tracking) is currently enabled.
		/// </summary>
		public bool IsTextureTrackingEnabled => _textureTrackingEnabled;

		/// <summary>
		/// Whether occlusion handling is currently enabled.
		/// </summary>
		public bool IsOcclusionEnabled => _occlusionEnabled;

		/// <summary>
		/// Whether depth tracking is currently enabled.
		/// </summary>
		public bool IsDepthTrackingEnabled => _depthTrackingEnabled;

		private Transform CameraTransform => XRTrackerManager.Instance.CameraTransform;

		/// <summary>
		/// Whether a silhouette model asset is assigned (used for conditional UI).
		/// </summary>
		private bool HasTrackingModelAsset => _trackingModelAsset != null && _trackingModelAsset.HasValidData;

		/// <summary>
		/// Path for runtime-generated silhouette model (persistent data path).
		/// </summary>
		private string RuntimeSilhouetteModelPath => Path.Combine(Application.persistentDataPath, _bodyId + "_silhouette_model.bin");

		// Effective thresholds (read by TrackedBodyManager)
		public float QualityToStartTracking => _useCustomStartThreshold
			? _customQualityToStart
			: _enableEdgeTracking
				? TrackerDefaults.EDGE_QUALITY_TO_START
				: TrackerDefaults.QUALITY_TO_START;

		public float QualityToStopTracking => _useCustomStopThreshold
			? _customQualityToStop
			: _enableEdgeTracking
				? TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD
				: TrackerDefaults.LOSE_TRACKING_THRESHOLD;

		/// <summary>
		/// Whether to use a custom quality threshold to start tracking instead of the default.
		/// </summary>
		public bool UseCustomStartThreshold
		{
			get => _useCustomStartThreshold;
			set => _useCustomStartThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) required to start tracking. Only used when UseCustomStartThreshold is true.
		/// </summary>
		public float CustomQualityToStart
		{
			get => _customQualityToStart;
			set => _customQualityToStart = Mathf.Clamp01(value);
		}

		/// <summary>
		/// Whether to use a custom quality threshold to stop tracking instead of the default.
		/// </summary>
		public bool UseCustomStopThreshold
		{
			get => _useCustomStopThreshold;
			set => _useCustomStopThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) below which tracking is lost. Only used when UseCustomStopThreshold is true.
		/// </summary>
		public float CustomQualityToStop
		{
			get => _customQualityToStop;
			set => _customQualityToStop = Mathf.Clamp01(value);
		}

		/// <summary>
		/// Whether assembly mode is enabled for this child body.
		/// When true, the child won't affect parent pose until quality confirms the part is present.
		/// </summary>
		public bool AssemblyMode
		{
			get => _assemblyMode;
			set => _assemblyMode = value;
		}

		/// <summary>
		/// Quality threshold to confirm part is installed (only used if AssemblyMode is true).
		/// </summary>
		public float AssemblyQualityToConfirm
		{
			get => _assemblyQualityToConfirm;
			set => _assemblyQualityToConfirm = Mathf.Clamp01(value);
		}

		public TrackingStatus TrackingStatus => _trackingStatus;

		#endregion

		#region Unity Lifecycle

		private void Awake()
		{
			if (string.IsNullOrEmpty(_bodyId))
				_bodyId = gameObject.name;

			DetectParentFromHierarchy();

			// Initialize quality averaging buffers (raw values)
			_histogramBuffer = new float[QUALITY_BUFFER_SIZE];
			_varianceStdDevBuffer = new float[QUALITY_BUFFER_SIZE];
			_correspondenceDistBuffer = new float[QUALITY_BUFFER_SIZE];
			_edgeResidualBuffer = new float[QUALITY_BUFFER_SIZE];
			_projectionErrorBuffer = new float[QUALITY_BUFFER_SIZE];
			_edgeCoverageBuffer = new float[QUALITY_BUFFER_SIZE];
		}

		/// <summary>
		/// Auto-detect parent TrackedBody from Unity hierarchy.
		/// </summary>
		private void DetectParentFromHierarchy()
		{
			_parentBody = FindParentInHierarchy();
		}

		/// <summary>
		/// Find the nearest TrackedBody ancestor in Unity hierarchy (excluding self).
		/// Used internally and by editor for display.
		/// </summary>
		public TrackedBody FindParentInHierarchy()
		{
			Transform current = transform.parent;
			while (current != null)
			{
				var parentBody = current.GetComponent<TrackedBody>();
				if (parentBody != null)
					return parentBody;
				current = current.parent;
			}

			return null;
		}

		private void StoreInitialPose(Vector3 cameraRelativePosition, Quaternion cameraRelativeRotation)
		{
			_initialCameraRelativePosition = cameraRelativePosition;
			_initialCameraRelativeRotation = cameraRelativeRotation;
			_initialPose = ConversionUtils.GetConvertedPose(cameraRelativePosition, cameraRelativeRotation);
		}

		private void ComputeInitialPose()
		{
			Transform viewpoint = _initialPoseSource == InitialPoseSource.Viewpoint && _initialViewpoint != null
				? _initialViewpoint
				: CameraTransform;

			StoreInitialPose(
				viewpoint.InverseTransformPoint(transform.position),
				Quaternion.Inverse(viewpoint.rotation) * transform.rotation);
		}

		private void OnEnable()
		{
			_lifecycleVersion++;

			// If already registered (e.g. re-enabled before a pending unregister completed),
			// just ensure we're in the manager's tracked list and skip re-registration.
			if (_isRegistered)
			{
				XRTrackerManager.Instance?.RegisterTrackedBody(this);
				return;
			}

			if (_parentBody == null)
				ComputeInitialPose();

			if (XRTrackerManager.Instance != null && XRTrackerManager.Instance.IsInitialized)
				RegisterBody();
			else
				XRTrackerManager.OnTrackerInitialized += OnTrackerInitialized;
		}

		private void OnDisable()
		{
			_lifecycleVersion++;
			XRTrackerManager.OnTrackerInitialized -= OnTrackerInitialized;
			UnregisterBody();
		}

		private void OnValidate()
		{
#if UNITY_EDITOR
			if (Application.isPlaying && _isRegistered)
			{
				UpdateStabilityParameters();
				UpdateEdgeTrackingParameters();
			}
#endif
		}

		/// <summary>
		/// Called before each native tracking step.
		/// During AR detection, updates the detector pose so the body follows the camera
		/// (world-space camera2world requires world-space detection poses).
		/// </summary>
		public virtual void BeforeTrackingStep()
		{
			var manager = XRTrackerManager.Instance;
			if (_isTracking || manager == null || !manager.UseARPoseFusion)
				return;

			Transform cam = CameraTransform;
			Vector3 worldPos = cam.TransformPoint(_initialCameraRelativePosition);
			Quaternion worldRot = cam.rotation * _initialCameraRelativeRotation;
			var worldPose = ConversionUtils.GetConvertedPose(worldPos, worldRot);
			FTBridge.FT_SetDetectorPose(_bodyId, ref worldPose);
			FTBridge.FT_SetBodyPose(_bodyId, ref worldPose);
		}

		/// <summary>
		/// Called after each native tracking step.
		/// Sets pending pose flag - TrackedBodyManager will apply pose at the right time:
		/// - Non-SLAM: via OnAfterTrackingStep callback (immediately after native tracking)
		/// - SLAM: via OnBeforeRender (needs latest AR camera pose)
		/// </summary>
		public virtual void AfterTrackingStep()
		{
		}

		private void Reset()
		{
			AddChildMeshes();
			_bodyId = gameObject.name;
		}

		#endregion

		#region Public Methods

		public void ResetTracking()
		{
			if (!_isRegistered)
				return;

			TrackedBodyManager.Instance.ResetBody(this);
		}

		public void ExecuteDetection()
		{
			if (!_isRegistered)
				return;

			_isTracking = false;
			FTBridge.FT_ExecuteDetection();
		}

		/// <summary>
		/// Force-start tracking, bypassing quality-based auto-start.
		/// Useful for testing modalities that can't self-assess object presence (e.g. edge-only).
		/// </summary>
		public void ForceStartTracking()
		{
			if (!_isRegistered || _isTracking)
				return;

			_isTracking = true;
			FTBridge.FT_StartTrackingBody(_bodyId);
		}

		/// <summary>
		/// Update stability (Tikhonov regularization) parameters at runtime.
		/// Call this after changing RotationStability or PositionStability while playing.
		/// </summary>
		public void UpdateStabilityParameters()
		{
			if (!_isRegistered)
				return;

			FTBridge.FT_SetStabilityParameters(_bodyId, _rotationStability, _positionStability);
		}

		/// <summary>
		/// Update edge tracking tuning parameters at runtime.
		/// Call this after changing EdgeTracking settings while playing.
		/// </summary>
		public void UpdateEdgeTrackingParameters()
		{
			if (!_isRegistered)
				return;

			FTBridge.FT_SetModalityParameters(_bodyId, _silhouetteTracking.FunctionAmplitude, _silhouetteTracking.LearningRate);
		}

		/// <summary>
		/// Update occlusion detection parameters at runtime.
		/// Call this after changing OcclusionSettings while playing.
		/// </summary>
		public void UpdateOcclusionParameters()
		{
			if (!_isRegistered)
				return;

			FTBridge.FT_SetSilhouetteOcclusionParameters(_bodyId, _occlusionSettings.Radius, _occlusionSettings.Threshold);
		}

		/// <summary>
		/// Set the initial pose from a viewpoint transform.
		/// The object's current world position is computed relative to the viewpoint.
		/// </summary>
		/// <param name="viewpoint">The viewpoint transform (e.g., camera position). If null, uses main camera.</param>
		public void SetInitialPose(Transform viewpoint)
		{
			Transform vp = viewpoint ? viewpoint : CameraTransform;
			StoreInitialPose(
				vp.InverseTransformPoint(transform.position),
				Quaternion.Inverse(vp.rotation) * transform.rotation);
			ApplyInitialPoseToNative();
		}

		/// <summary>
		/// Set the initial pose from raw position and rotation values (camera-relative).
		/// </summary>
		/// <param name="position">Position relative to camera</param>
		/// <param name="rotation">Rotation relative to camera</param>
		public void SetInitialPose(Vector3 position, Quaternion rotation)
		{
			StoreInitialPose(position, rotation);
			ApplyInitialPoseToNative();
		}

		private void ApplyInitialPoseToNative()
		{
			if (!_isRegistered)
				return;

			// Update the detector's stored pose (used for future resets/detections)
			// Does NOT immediately reset - call ResetTracking() to apply immediately
			FTBridge.FT_SetDetectorPose(_bodyId, ref _initialPose);
		}

		/// <summary>
		/// Fetches current status from native tracker.
		/// Called by TrackedBodyManager before processing.
		/// </summary>
		internal void RefreshStatus()
		{
			if (!_isRegistered)
				return;

			FTBridge.FT_GetBodyStatus(_bodyId, out _lastStatus);

			// Update quality averaging buffers
			UpdateQualityBuffers();
		}

		private void UpdateQualityBuffers()
		{
			if (_histogramBuffer == null || _varianceStdDevBuffer == null)
				return;

			// Store raw values in buffers
			_histogramBuffer[_qualityBufferIndex] = _lastStatus.histogram_discriminability;
			_varianceStdDevBuffer[_qualityBufferIndex] = _lastStatus.variance_stddev;
			if (_correspondenceDistBuffer != null)
			{
				// Only store real distances when depth has valid correspondences.
				// When n_valid_points == 0, C++ returns 0.0 which would falsely indicate
				// perfect alignment. Store DistanceTolerance instead (= quality 0).
				bool hasValidDepth = _lastStatus.HasDepthModality && _lastStatus.n_valid_points > 0;
				_correspondenceDistBuffer[_qualityBufferIndex] = hasValidDepth
					? _lastStatus.mean_correspondence_distance
					: _depthTracking.DistanceTolerance;
			}
			if (_edgeResidualBuffer != null)
			{
				// When no valid edge points, C++ returns 0.0 which would falsely indicate
				// perfect alignment. Store EDGE_RESIDUAL_MAX instead (= quality 0).
				bool hasValidEdge = _lastStatus.HasEdgeModality && _lastStatus.n_valid_edge_points > 0;
				_edgeResidualBuffer[_qualityBufferIndex] = hasValidEdge
					? _lastStatus.mean_edge_residual
					: TrackerDefaults.EDGE_RESIDUAL_MAX;
			}
			if (_projectionErrorBuffer != null)
			{
				// When no valid edge points, C++ returns 90.0 (worst case).
				// Store EDGE_PROJECTION_ERROR_BAD instead to map to quality 0.
				bool hasValidEdge = _lastStatus.HasEdgeModality && _lastStatus.n_valid_edge_points > 0;
				_projectionErrorBuffer[_qualityBufferIndex] = hasValidEdge
					? _lastStatus.edge_projection_error
					: TrackerDefaults.EDGE_PROJECTION_ERROR_BAD;
			}
			if (_edgeCoverageBuffer != null)
			{
				_edgeCoverageBuffer[_qualityBufferIndex] = _lastStatus.HasEdgeModality && _lastStatus.n_total_edge_sites > 0
					? (float)_lastStatus.n_valid_edge_points / _lastStatus.n_total_edge_sites
					: 0f;
			}
			_qualityBufferIndex = (_qualityBufferIndex + 1) % QUALITY_BUFFER_SIZE;
			if (_qualityBufferCount < QUALITY_BUFFER_SIZE)
				_qualityBufferCount++;
		}

		private float GetBufferAverage(float[] buffer)
		{
			if (buffer == null || _qualityBufferCount == 0)
				return 0f;

			float sum = 0f;
			for (int i = 0; i < _qualityBufferCount; i++)
				sum += buffer[i];

			return sum / _qualityBufferCount;
		}

		#endregion

		#region Internal Methods (called by TrackedBodyManager)

		/// <summary>
		/// Sets the tracking state. Called by TrackedBodyManager.
		/// </summary>
		internal void SetTrackingState(bool isTracking)
		{
			_isTracking = isTracking;
		}

		/// <summary>
		/// Sets the tracking status for display. Called by TrackedBodyManager.
		/// </summary>
		internal void SetTrackingStatus(TrackingStatus status)
		{
			_trackingStatus = status;
		}

		internal void ResetQualityMetrics()
		{
			_qualityBufferCount = 0;
			_qualityBufferIndex = 0;
			_lastStatus = default;
		}

		/// <summary>
		/// Fires tracking event if state changed. Called by TrackedBodyManager.
		/// </summary>
		internal void FireTrackingEvent(bool isTracking)
		{
			if (isTracking)
				_onStartTracking?.Invoke();
			else
				_onStopTracking?.Invoke();
		}

		/// <summary>
		/// Event fired when pose is updated. Subscribe to receive pose updates without modifying TrackedBody.
		/// </summary>
		public event Action<Vector3, Quaternion> OnPoseUpdated;

		/// <summary>
		/// Sets the world pose of this body. Called by TrackedBodyManager.
		/// Sets transform and fires OnPoseUpdated event.
		/// </summary>
		internal void SetWorldPose(Vector3 position, Quaternion rotation)
		{
			transform.position = position;
			transform.rotation = rotation;
			OnPoseUpdated?.Invoke(position, rotation);
		}

		/// <summary>
		/// Set custom attachment pose using world-space position and rotation.
		/// Internally computes the relative pose to parent.
		/// If already attached, updates the pose immediately on native side.
		/// If not attached yet, the pose will be used when registering/attaching.
		/// </summary>
		/// <param name="childWorldPosition">Child's world-space position</param>
		/// <param name="childWorldRotation">Child's world-space rotation</param>
		/// <returns>True if successful (or stored for later use)</returns>
		public bool SetAttachmentPose(Vector3 childWorldPosition, Quaternion childWorldRotation)
		{
			if (_parentBody == null)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Cannot set attachment pose without a parent body assigned");
				return false;
			}

			// Compute scale-independent relative pose (same logic as GetRelativePoseToParent)
			Vector3 worldOffset = childWorldPosition - _parentBody.transform.position;
			Vector3 relativePosition = Quaternion.Inverse(_parentBody.transform.rotation) * worldOffset;
			Quaternion relativeRotation = Quaternion.Inverse(_parentBody.transform.rotation) * childWorldRotation;

			_useCustomAttachmentPose = true;
			_customAttachmentPosition = relativePosition;
			_customAttachmentRotation = relativeRotation;

			// If already attached, update native side immediately
			if (_isAttached)
			{
				var pose = ConversionUtils.GetConvertedPose(relativePosition, relativeRotation);
				bool success = FTBridge.FT_SetRelativePose(_bodyId, ref pose);
				if (!success)
				{
					Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to update attachment pose on native side");
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Clear custom attachment pose override.
		/// If already attached, recomputes pose from current Unity hierarchy.
		/// If not attached yet, pose will be computed from hierarchy at registration time.
		/// </summary>
		public void ClearCustomAttachmentPose()
		{
			_useCustomAttachmentPose = false;

			// If already attached, recompute from hierarchy and update native
			if (_isAttached && _parentBody != null)
			{
				GetRelativePoseToParent(out Vector3 relPos, out Quaternion relRot);
				var pose = ConversionUtils.GetConvertedPose(relPos, relRot);
				FTBridge.FT_SetRelativePose(_bodyId, ref pose);
			}
		}

		/// <summary>
		/// Update body geometry (mesh and silhouette model) at runtime while preserving tracking state.
		/// Use this to swap the visual representation without losing the current pose estimate.
		/// </summary>
		/// <param name="newMeshFilters">New mesh filters defining the geometry</param>
		/// <param name="newTrackingModel">Pre-baked tracking model asset (silhouette + optional depth). Null for edge-only mode.</param>
		/// <returns>True if geometry was successfully updated</returns>
		public bool UpdateGeometry(IList<MeshFilter> newMeshFilters, TrackingModelAsset newTrackingModel = null)
		{
			if (!_isRegistered)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Cannot update geometry - body not registered");
				return false;
			}

			if (newMeshFilters == null || newMeshFilters.Count == 0)
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: No mesh filters provided");
				return false;
			}

			CombinedMeshData meshData = default;
			GCHandle silhouetteHandle = default;
			GCHandle depthHandle = default;

			try
			{
				// Combine new meshes
				meshData = MeshCombiner.Combine(newMeshFilters, transform);

				// Get silhouette model bytes (if provided)
				IntPtr silhouettePtr = IntPtr.Zero;
				int silhouetteSize = 0;
				if (newTrackingModel != null && newTrackingModel.HasValidData)
				{
					byte[] silhouetteData = newTrackingModel.ModelData;
					silhouetteHandle = GCHandle.Alloc(silhouetteData, GCHandleType.Pinned);
					silhouettePtr = silhouetteHandle.AddrOfPinnedObject();
					silhouetteSize = silhouetteData.Length;
				}

				// Get depth model bytes (if available)
				IntPtr depthPtr = IntPtr.Zero;
				int depthSize = 0;
				if (newTrackingModel != null && newTrackingModel.HasValidDepthModel)
				{
					byte[] depthData = newTrackingModel.DepthModelData;
					depthHandle = GCHandle.Alloc(depthData, GCHandleType.Pinned);
					depthPtr = depthHandle.AddrOfPinnedObject();
					depthSize = depthData.Length;
				}

				// Call native update
				int result = FTBridge.FT_UpdateBodyGeometry(
					_bodyId,
					meshData.VerticesPtr,
					meshData.VertexCount,
					meshData.TrianglesPtr,
					meshData.TriangleCount,
					silhouettePtr,
					silhouetteSize,
					depthPtr,
					depthSize);

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[TrackedBody] {_bodyId}: Failed to update geometry, error {result}");
					return false;
				}

				// Update local references
				if (newTrackingModel != null)
					_trackingModelAsset = newTrackingModel;

				Debug.Log($"[TrackedBody] {_bodyId}: Geometry updated successfully");
				return true;
			}
			catch (Exception e)
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: Exception updating geometry: {e.Message}");
				return false;
			}
			finally
			{
				meshData.Dispose();
				if (silhouetteHandle.IsAllocated)
					silhouetteHandle.Free();
				if (depthHandle.IsAllocated)
					depthHandle.Free();
			}
		}

		/// <summary>
		/// Registers any children that were waiting for this parent.
		/// Called after parent registration completes.
		/// </summary>
		private void RegisterPendingChildren()
		{
			foreach (var child in _childBodies.ToList())
			{
				if (!child._isRegistered)
				{
					Debug.Log($"[TrackedBody] {_bodyId}: Registering pending child {child._bodyId}");
					child.RegisterBody();
				}
			}
		}

		/// <summary>
		/// Updates which degrees of freedom are tracked for this attached body.
		/// </summary>
		/// <param name="trackedMotion">New motion configuration (None = rigid)</param>
		/// <returns>True if successfully updated</returns>
		public bool SetTrackedMotion(TrackedMotion trackedMotion)
		{
			if (_parentBody == null)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Not attached to a parent");
				return false;
			}

			if (_isRegistered && !FTBridge.FT_SetChildFreeDOF(_bodyId, (int)trackedMotion))
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: Failed to set tracked motion");
				return false;
			}

			_trackedMotion = trackedMotion;
			Debug.Log($"[TrackedBody] {_bodyId}: Updated motion to {(trackedMotion == TrackedMotion.None ? "rigid" : "articulated")}");
			return true;
		}

		/// <summary>
		/// Add another body as an occluder for this body.
		/// This body's tracking will ignore regions occluded by the occluder.
		/// Requires occlusion to be enabled on this body.
		/// </summary>
		/// <param name="occluder">The body that occludes this one</param>
		/// <returns>True if successfully added</returns>
		public bool AddOccluder(TrackedBody occluder)
		{
			if (occluder == null)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Cannot add null occluder");
				return false;
			}

			if (occluder == this)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Cannot add self as occluder");
				return false;
			}

			if (!_enableOcclusion)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Cannot add occluder - occlusion is not enabled on this body");
				return false;
			}

			if (!_isRegistered || !occluder._isRegistered)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Both bodies must be registered to add occlusion");
				return false;
			}

			int result = FTBridge.FT_AddOccluder(_bodyId, occluder._bodyId);
			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: Failed to add occluder {occluder._bodyId}: error {result}");
				return false;
			}

			Debug.Log($"[TrackedBody] {_bodyId}: Added occluder {occluder._bodyId}");
			return true;
		}

		/// <summary>
		/// Remove an occluder relationship.
		/// </summary>
		/// <param name="occluder">The body to remove as occluder</param>
		/// <returns>True if successfully removed</returns>
		public bool RemoveOccluder(TrackedBody occluder)
		{
			if (occluder == null || !_isRegistered)
				return false;

			int result = FTBridge.FT_RemoveOccluder(_bodyId, occluder._bodyId);
			if (result != FTErrorCode.OK)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to remove occluder {occluder._bodyId}: error {result}");
				return false;
			}

			Debug.Log($"[TrackedBody] {_bodyId}: Removed occluder {occluder._bodyId}");
			return true;
		}

		/// <summary>
		/// Enable mutual occlusion between this body and another.
		/// Both bodies will occlude each other.
		/// </summary>
		/// <param name="other">The other body</param>
		/// <returns>True if successfully enabled</returns>
		public bool EnableMutualOcclusion(TrackedBody other)
		{
			if (other == null || other == this)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Invalid body for mutual occlusion");
				return false;
			}

			if (!_isRegistered || !other._isRegistered)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Both bodies must be registered");
				return false;
			}

			int result = FTBridge.FT_EnableMutualOcclusion(_bodyId, other._bodyId);
			if (result != FTErrorCode.OK)
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: Failed to enable mutual occlusion with {other._bodyId}: error {result}");
				return false;
			}

			Debug.Log($"[TrackedBody] Enabled mutual occlusion: {_bodyId} <-> {other._bodyId}");
			return true;
		}

		/// <summary>
		/// Disable mutual occlusion between this body and another.
		/// </summary>
		/// <param name="other">The other body</param>
		/// <returns>True if successfully disabled</returns>
		public bool DisableMutualOcclusion(TrackedBody other)
		{
			if (other == null || !_isRegistered)
				return false;

			int result = FTBridge.FT_DisableMutualOcclusion(_bodyId, other._bodyId);
			if (result != FTErrorCode.OK)
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to disable mutual occlusion: error {result}");
				return false;
			}

			Debug.Log($"[TrackedBody] Disabled mutual occlusion: {_bodyId} <-> {other._bodyId}");
			return true;
		}

		/// <summary>
		/// Whether this body currently contributes to optimization.
		/// When false, the body still tracks and computes quality metrics, but its
		/// observations don't affect the parent body's pose optimization.
		/// </summary>
		public bool ContributesToOptimization => _contributesToOptimization;

		/// <summary>
		/// Set whether this body contributes to the optimization solution.
		/// When disabled, the body still gets its pose updated (follows parent if attached),
		/// but its gradient/hessian don't influence the parent's tracking.
		/// Use this for child bodies with low tracking quality.
		/// Only sends to native when state actually changes (efficient for per-frame calls).
		/// </summary>
		/// <param name="contributes">True to contribute, false to exclude from optimization</param>
		/// <returns>True if successfully set</returns>
		public bool SetContributesToOptimization(bool contributes)
		{
			// Only send to native if state changed (or never sent)
			if (_lastContributionState.HasValue && _lastContributionState.Value == contributes)
			{
				return true;
			}

			if (!_isRegistered)
			{
				_contributesToOptimization = contributes;
				return true;
			}

			if (!FTBridge.FT_SetBodyContributesToOptimization(_bodyId, contributes))
			{
				Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to set contribution state");
				return false;
			}

			_contributesToOptimization = contributes;
			_lastContributionState = contributes;
			return true;
		}

		#endregion

		#region Private Methods

		/// <summary>
		/// Compute relative pose from this body to parent in world-space meters (scale-independent).
		/// Uses world-space offset to avoid scale affecting the relative position.
		/// </summary>
		private void GetRelativePoseToParent(out Vector3 relativePosition, out Quaternion relativeRotation)
		{
			// Compute world-space offset (in meters, not affected by parent's scale)
			Vector3 worldOffset = transform.position - _parentBody.transform.position;
			// Rotate offset to parent's local frame (rotation only, no scale)
			relativePosition = Quaternion.Inverse(_parentBody.transform.rotation) * worldOffset;
			// Relative rotation
			relativeRotation = Quaternion.Inverse(_parentBody.transform.rotation) * transform.rotation;
		}

		private void OnTrackerInitialized()
		{
			XRTrackerManager.OnTrackerInitialized -= OnTrackerInitialized;
			RegisterBody();
		}

		private async void RegisterBody()
		{
			if (_isRegistered)
				return;

			int versionAtStart = _lifecycleVersion;

			// If we have a parent that isn't registered yet, wait for parent to register us
			if (_parentBody != null && !_parentBody._isRegistered)
			{
				if (!_parentBody._childBodies.Contains(this))
					_parentBody._childBodies.Add(this);
				Debug.Log($"[TrackedBody] {_bodyId}: Waiting for parent {_parentBody._bodyId} to register first");
				return;
			}

			// Validate meshes
			if (!MeshCombiner.Validate(_meshFilters, out string error))
			{
				Debug.LogError($"[TrackedBody] {_bodyId}: {error}");
				return;
			}

			// Runtime validation is skipped - hash computation is too slow for runtime
			// Validation warnings are shown in Editor only (via SilhouetteModelGenerator)

			// Mesh data from MeshCombiner is in local space (root scale cancelled out)
			// Always use 1.0 for geometry_unit - mesh should be imported at correct scale
			float scale = transform.lossyScale.x;
			FTModelConfig modelConfig = _modelSettings.ToNativeConfig(scale);
			var bodyConfig = new FTBodyConfig
			{
				tikhonov_rotation = _rotationStability,
				tikhonov_translation = _positionStability,
				enable_texture_modality = _enableTextureTracking ? 1 : 0,
				enable_depth_modality = _enableDepthTracking ? 1 : 0,
				enable_region_modality = _enableSilhouetteTracking ? 1 : 0,
				use_depth_for_occlusion = (_enableDepthTracking && _enableOcclusion) ? 1 : 0,
				function_amplitude = _silhouetteTracking.FunctionAmplitude,
				learning_rate = _silhouetteTracking.LearningRate,
				// Multi-scale pyramid settings
				region_advanced = _multiScale.ToNative(_silhouetteTracking),
				// Depth modality settings
				depth_distance_tolerance = _depthTracking.DistanceTolerance,
				depth_stride_length = _depthTracking.StrideLength,
				depth_self_occlusion_radius = _depthTracking.SelfOcclusionRadius,
				depth_self_occlusion_threshold = _depthTracking.SelfOcclusionThreshold,
				depth_measured_occlusion_radius = _depthTracking.MeasuredOcclusionRadius,
				depth_measured_occlusion_threshold = _depthTracking.MeasuredOcclusionThreshold,
				// Silhouette modality occlusion settings
				region_occlusion_radius = _occlusionSettings.Radius,
				region_occlusion_threshold = _occlusionSettings.Threshold,
				// Edge modality settings (render-based edge detection)
				enable_edge_modality = _enableEdgeTracking ? 1 : 0,
				edge_depth_threshold = _edgeTracking.DepthEdgeThreshold,
				edge_tracking_radius = _edgeTracking.SearchRadius,
				edge_min_gradient = _edgeTracking.MinGradient,
				edge_sample_step = _edgeTracking.SampleStep,
				edge_crease_threshold_deg = _edgeTracking.EnableCreaseEdges ? _edgeTracking.CreaseEdgeAngle : 1000f,
				// Hardcoded defaults (change here to test alternatives)
				edge_use_normal_direction = 1,
				edge_use_laplacian_edge_detection = 1,
				edge_inward_search_ratio = 1f,
				edge_use_compute_pipeline = 1,
				edge_use_illumination_compensation = 1,
				edge_search_resolution = 640,
				edge_filter_prediction_sites = 1,
				edge_prediction_weight_threshold = 0.3f,
				edge_ncc_min_correlation = 0.7f,
				edge_search_radius_scales = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_search_radius_scales_count = 0,
				edge_standard_deviations = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_standard_deviations_count = 0,
				edge_use_keyframe = _edgeTracking.UseKeyframe ? 1 : 0,
				edge_keyframe_rotation_deg = _edgeTracking.KeyframeRotationDeg,
				edge_keyframe_translation = _edgeTracking.KeyframeTranslation,
				edge_probe_search_radius_scale = _edgeTracking.ProbeSearchRadiusScale,
				edge_probe_max_projection_error_deg = _edgeTracking.ProbeMaxProjectionErrorDeg,
				edge_probe_max_residual_px = _edgeTracking.ProbeMaxResidualPx
			};

			// Copy edge coarse-to-fine arrays
			if (_edgeTracking.SearchRadiusScales != null && _edgeTracking.SearchRadiusScales.Length > 0)
			{
				int count = Math.Min(_edgeTracking.SearchRadiusScales.Length, FTRegionModalityAdvanced.MAX_SCALES);
				for (int i = 0; i < count; i++)
					bodyConfig.edge_search_radius_scales[i] = _edgeTracking.SearchRadiusScales[i];
				bodyConfig.edge_search_radius_scales_count = count;
			}
			if (_edgeTracking.StandardDeviations != null && _edgeTracking.StandardDeviations.Length > 0)
			{
				int count = Math.Min(_edgeTracking.StandardDeviations.Length, FTRegionModalityAdvanced.MAX_SCALES);
				for (int i = 0; i < count; i++)
					bodyConfig.edge_standard_deviations[i] = _edgeTracking.StandardDeviations[i];
				bodyConfig.edge_standard_deviations_count = count;
			}
			CombinedMeshData meshData = default;
			byte[] depthModelData = null;
			if (EnableDepthTracking && _trackingModelAsset != null && _trackingModelAsset.HasValidDepthModel)
			{
				depthModelData = _trackingModelAsset.DepthModelData;
			}

			try
			{
				meshData = MeshCombiner.Combine(_meshFilters, transform);
				int result = await RegisterBodyAsync(versionAtStart, RuntimeSilhouetteModelPath, modelConfig, bodyConfig, _initialPose, meshData, depthModelData);

				// If a new OnEnable/OnDisable occurred while awaiting, this registration is stale.
				if (_lifecycleVersion != versionAtStart)
				{
					Debug.Log($"[TrackedBody] {_bodyId}: Registration cancelled (lifecycle changed during async registration)");
					return;
				}

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[TrackedBody] Failed to register {_bodyId}: error code {result}");
					return;
				}

				_isRegistered = true;
				XRTrackerManager.Instance?.RegisterTrackedBody(this);

				// If we have a parent, we were attached during registration (parent was already registered)
				if (_parentBody != null)
				{
					_isAttached = true;
					if (!_parentBody._childBodies.Contains(this))
						_parentBody._childBodies.Add(this);

					// Add this child as occluder for parent if configured
					if (_occludeParent)
						_parentBody.AddOccluder(this);

					Debug.Log($"[TrackedBody] {_bodyId}: Registered and attached to {_parentBody._bodyId}");
				}

				// Enable silhouette validation if configured
				if (_enableSilhouetteValidation)
				{
					int rcResult = FTBridge.FT_EnableSilhouetteChecking(_bodyId);
					_silhouetteValidationEnabled = rcResult == FTErrorCode.OK;
					if (_silhouetteValidationEnabled)
						Debug.Log($"[TrackedBody] {_bodyId}: Silhouette validation enabled");
					else
						Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to enable silhouette validation: {rcResult}");
				}

				// Texture tracking is enabled at registration time via FTBodyConfig
				if (_enableTextureTracking)
				{
					_textureTrackingEnabled = true;
					Debug.Log($"[TrackedBody] {_bodyId}: Texture tracking enabled (at registration)");
				}

				// Enable occlusion if requested
				if (_enableOcclusion)
				{
					int occResult = FTBridge.FT_SetOcclusionEnabled(_bodyId, true);
					_occlusionEnabled = occResult == FTErrorCode.OK;
					if (_occlusionEnabled)
						Debug.Log($"[TrackedBody] {_bodyId}: Occlusion enabled");
					else
						Debug.LogWarning($"[TrackedBody] {_bodyId}: Failed to enable occlusion: {occResult}");
				}

				// Depth tracking is enabled at registration via FTBodyConfig + depth model data
				if (depthModelData != null && depthModelData.Length > 0)
				{
					_depthTrackingEnabled = true;
					Debug.Log($"[TrackedBody] {_bodyId}: Depth tracking enabled (at registration)");
				}

				// Register any children that were waiting for us
				RegisterPendingChildren();

				// Only execute detection for root bodies or if parent isn't tracking
				if (_parentBody == null || !_parentBody.IsTracking)
					FTBridge.FT_ExecuteDetection();
			}
			finally
			{
				meshData.Dispose();
			}
		}

		private async Task<int> RegisterBodyAsync(int versionAtStart, string silhouetteModelPath, FTModelConfig modelConfig, FTBodyConfig bodyConfig, FTTrackingPose trackingPose,
			CombinedMeshData meshData, byte[] depthModelData)
		{
			// Silhouette data source: either from asset (memory) or file path
			byte[] modelData = null;
			GCHandle dataHandle = default;
			IntPtr dataPtr = IntPtr.Zero;

			// Depth model data (if provided)
			GCHandle depthDataHandle = default;
			IntPtr depthDataPtr = IntPtr.Zero;
			int depthDataSize = 0;

			if (depthModelData != null && depthModelData.Length > 0)
			{
				depthDataHandle = GCHandle.Alloc(depthModelData, GCHandleType.Pinned);
				depthDataPtr = depthDataHandle.AddrOfPinnedObject();
				depthDataSize = depthModelData.Length;
			}

			bool needsSilhouetteModel = _enableSilhouetteTracking;
			bool hasAssetWithSilhouette = _trackingModelAsset != null && _trackingModelAsset.HasValidSilhouetteModel;

			if (hasAssetWithSilhouette && needsSilhouetteModel)
			{
				Debug.Log($"[TrackedBody] Using pre-generated silhouette model asset for {_bodyId}");
				modelData = _trackingModelAsset.ModelData;
				dataHandle = GCHandle.Alloc(modelData, GCHandleType.Pinned);
				dataPtr = dataHandle.AddrOfPinnedObject();
			}
			else if (needsSilhouetteModel)
			{
				// Silhouette data validation/generation on background thread (slow)
				(int meshCount, int vertexCount, int triangleCount) stats = MeshCombiner.GetStats(_meshFilters);
				FTModelConfig modelConfigCopy = modelConfig;

				await Task.Run(() =>
				{
					FTModelConfig cfg = modelConfigCopy;
					bool modelValid = FTBridge.FT_ValidateSilhouetteModel(
						silhouetteModelPath, meshData.VerticesPtr, meshData.VertexCount, meshData.TrianglesPtr, meshData.TriangleCount, ref cfg);

					if (!modelValid)
					{
						Debug.Log($"[TrackedBody] Generating silhouette model for {_bodyId} ({stats.meshCount} mesh(es), {stats.vertexCount} vertices)...");

						int genResult = FTBridge.FT_GenerateSilhouetteModel(
							meshData.VerticesPtr, meshData.VertexCount, meshData.TrianglesPtr, meshData.TriangleCount,
							silhouetteModelPath, ref cfg);

						if (genResult != FTErrorCode.OK)
						{
							throw new Exception($"Failed to generate silhouette model: error code {genResult}");
						}
					}
				});
			}
			else
			{
				Debug.Log($"[TrackedBody] {_bodyId}: Edge-only mode, skipping silhouette model generation");
			}

			try
			{
				// Bail if lifecycle changed during model generation
				if (_lifecycleVersion != versionAtStart)
					return FTErrorCode.OK; // Not an error - just cancelled

				// Prepare parent attachment info (if any)
				string parentId = null;
				IntPtr relativePosePtr = IntPtr.Zero;
				int freeDof = 0;
				GCHandle relativePoseHandle = default;

				if (_parentBody != null)
				{
					parentId = _parentBody._bodyId;
					freeDof = GetEffectiveDOFFlags();

					Vector3 relPos;
					Quaternion relRot;

					if (_useCustomAttachmentPose)
					{
						relPos = _customAttachmentPosition;
						relRot = _customAttachmentRotation;
					}
					else
					{
						GetRelativePoseToParent(out relPos, out relRot);
					}

					FTTrackingPose relativePose = ConversionUtils.GetConvertedPose(relPos, relRot);

					relativePoseHandle = GCHandle.Alloc(relativePose, GCHandleType.Pinned);
					relativePosePtr = relativePoseHandle.AddrOfPinnedObject();
				}

				// Acquire native lock to ensure no tracking in progress during registration
				await XRTrackerManager.Instance.AcquireNativeLockAsync();

				// Bail if lifecycle changed while waiting for native lock
				if (_lifecycleVersion != versionAtStart)
				{
					XRTrackerManager.Instance.ReleaseNativeLock();
					if (relativePoseHandle.IsAllocated)
						relativePoseHandle.Free();
					return FTErrorCode.OK; // Not an error - just cancelled
				}

				int result;
				try
				{
					result = FTBridge.FT_RegisterBody(
						_bodyId,
						meshData.VerticesPtr,
						meshData.VertexCount,
						meshData.NormalsPtr,
						meshData.HasNormals ? 1 : 0,
						meshData.TrianglesPtr,
						meshData.TriangleCount,
						needsSilhouetteModel && !hasAssetWithSilhouette ? silhouetteModelPath : null,
						dataPtr,
						modelData?.Length ?? 0,
						depthDataPtr,
						depthDataSize,
						ref modelConfig,
						ref bodyConfig,
						ref trackingPose,
						parentId,
						relativePosePtr,
						freeDof);
				}
				finally
				{
					XRTrackerManager.Instance.ReleaseNativeLock();
					if (relativePoseHandle.IsAllocated)
						relativePoseHandle.Free();
				}

				return result;
			}
			finally
			{
				if (dataHandle.IsAllocated)
					dataHandle.Free();
				if (depthDataHandle.IsAllocated)
					depthDataHandle.Free();
			}
		}

		private async void UnregisterBody()
		{
			if (!_isRegistered)
				return;

			int versionAtStart = _lifecycleVersion;

			// Yield one frame before unregistering. This allows a same-frame OnEnable
			// (e.g. from visibility track thrashing during procedure step transitions)
			// to increment _lifecycleVersion and cancel this stale unregister.
			await Task.Yield();

			if (_lifecycleVersion != versionAtStart)
				return; // Re-enabled before unregister executed - body stays registered

			// Acquire native lock to ensure no tracking in progress during unregistration
			var manager = XRTrackerManager.Instance;
			if (manager != null)
				await manager.AcquireNativeLockAsync();

			// Check again after potentially waiting for the lock
			if (_lifecycleVersion != versionAtStart)
			{
				manager?.ReleaseNativeLock();
				return;
			}

			try
			{
				XRTrackerManager.Instance?.UnregisterTrackedBody(this);

				int result = FTBridge.FT_UnregisterBody(_bodyId);
				if (result != FTErrorCode.OK)
				{
					Debug.LogWarning($"[TrackedBody] Unregister failed for {_bodyId}: error {result}");
				}

				_isRegistered = false;
				_isTracking = false;
				_isAttached = false;
				_lastStatus = default;
			}
			finally
			{
				manager?.ReleaseNativeLock();
			}
		}

		/// <summary>
		/// Returns the effective DOF flags for registration/attachment.
		/// </summary>
		private int GetEffectiveDOFFlags()
		{
			return (int)_trackedMotion;
		}

		void AddChildMeshes()
		{
			_meshFilters.Clear();
			var allMeshFilters = GetComponentsInChildren<MeshFilter>(true);
			var childTrackedBodies = GetComponentsInChildren<TrackedBody>(true);

			foreach (var meshFilter in allMeshFilters)
			{
				bool isUnderChildTrackedBody = false;
				foreach (var childBody in childTrackedBodies)
				{
					if (childBody != this && meshFilter.transform.IsChildOf(childBody.transform))
					{
						isUnderChildTrackedBody = true;
						break;
					}
				}

				if (!isUnderChildTrackedBody)
					_meshFilters.Add(meshFilter);
			}
		}

		#endregion

		#region Bounds & Gizmos

		/// <summary>
		/// Computes the combined local-space bounds of all mesh filters.
		/// Bounds are relative to this TrackedBody's transform.
		/// </summary>
		public Bounds ComputeLocalBounds()
		{
			return TrackedBodyUtils.ComputeLocalBounds(_meshFilters, transform);
		}

		/// <summary>
		/// Gets the effective sphere radius for silhouette model generation.
		/// Returns the configured value if positive, otherwise computes auto value (0.8 x diameter).
		/// </summary>
		public float GetEffectiveSphereRadius()
		{
			return TrackedBodyUtils.GetEffectiveSphereRadius(_meshFilters, transform, _modelSettings.sphereRadius);
		}

		/// <summary>
		/// Gets the number of viewpoints based on nDivides setting.
		/// Geodesic sphere vertex count: 10 * 4^n + 2
		/// </summary>
		public int GetViewpointCount()
		{
			int divides = _modelSettings.nDivides >= 0 ? _modelSettings.nDivides : 3;
			// Geodesic icosahedron: V = 10 * 4^n + 2
			return 10 * (int)Mathf.Pow(4, divides) + 2;
		}

		/// <summary>
		/// Gets the effective points per view.
		/// </summary>
		public int GetPointsPerView()
		{
			return _modelSettings.nPoints > 0 ? _modelSettings.nPoints : 200;
		}

		#endregion
	}
}
