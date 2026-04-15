using System;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Silhouette data generation settings. Maps to FTModelConfig for C++ interop.
	/// </summary>
	/// <summary>
	/// Viewpoint coverage preset. Controls which angles are included in model generation.
	/// </summary>
	/// <summary>
	/// Reference direction for horizontal viewpoint filtering.
	/// Y directions are not valid for azimuth filtering (elevation axis).
	/// </summary>
	/// <summary>
	/// Object-local axis direction. Used for both up axis and forward axis configuration.
	/// </summary>
	public enum ForwardAxis
	{
		/// <summary>Positive X direction.</summary>
		[InspectorName("Positive X")]
		PositiveX = 0,
		/// <summary>Negative X direction.</summary>
		[InspectorName("Negative X")]
		NegativeX = 1,
		/// <summary>Positive Y direction.</summary>
		[InspectorName("Positive Y")]
		PositiveY = 2,
		/// <summary>Negative Y direction.</summary>
		[InspectorName("Negative Y")]
		NegativeY = 3,
		/// <summary>Positive Z direction.</summary>
		[InspectorName("Positive Z")]
		PositiveZ = 4,
		/// <summary>Negative Z direction.</summary>
		[InspectorName("Negative Z")]
		NegativeZ = 5
	}

	public enum ViewpointPreset
	{
		/// <summary>Full sphere — all viewing angles (default).</summary>
		FullSphere,
		/// <summary>Upper hemisphere — top + side views, cuts bottom-up views. (-10° to 90°)</summary>
		UpperHemisphere,
		/// <summary>Mostly horizontal views for objects at camera height. (-20° to 30°)</summary>
		SideRing,
		/// <summary>Custom elevation range.</summary>
		Custom
	}

	[Serializable]
	public class ModelSettings
	{
		// [Header("Silhouette Model Parameters (-1 = auto)")]
		[Tooltip("Distance from object center to virtual camera viewpoints. -1 = auto (0.8 x object diameter)")]
		public float sphereRadius = -1f;

		[Tooltip("Number of geodesic subdivisions for viewpoint generation. -1 = 3 (1280 views). Higher = more views, slower generation.")]
		public int nDivides = 2;

		[Tooltip("Number of silhouette points sampled per view. -1 = 200. Higher = better tracking, more memory.")]
		public int nPoints = 200;

		[Tooltip("Maximum radius for depth offset calculation in meters. -1 = 0.05m")]
		public float maxRadiusDepthOffset = -1f;

		[Tooltip("Stride between depth offset samples in meters. -1 = 0.002m")]
		public float strideDepthOffset = -1f;

		[Tooltip("Internal render resolution for model generation. -1 = 2000. Higher = better quality, slower generation.")]
		public int imageSize = -1;

		[Tooltip("Which object-local axis points 'up'. Elevation angles are measured relative to this direction. Change for CAD models authored with Z-up.")]
		public ForwardAxis upAxis = ForwardAxis.PositiveY;

		[Tooltip("Which viewing angles to include in the model. Reduces model size and generation time for objects only viewed from certain directions.")]
		public ViewpointPreset viewpointPreset = ViewpointPreset.FullSphere;

		[Tooltip("Minimum elevation angle in degrees (-90 = directly below, 0 = side view, 90 = directly above)")]
		[Range(-90f, 90f)]
		public float minElevation = -90f;

		[Tooltip("Maximum elevation angle in degrees (-90 = directly below, 0 = side view, 90 = directly above)")]
		[Range(-90f, 90f)]
		public float maxElevation = 90f;

		[Tooltip("Enable horizontal (azimuth) filtering to limit viewpoints around a forward direction.")]
		public bool enableHorizontalFilter = false;

		[Tooltip("Minimum horizontal angle in degrees (-180 to 180). Negative = left of forward.")]
		[Range(-180f, 180f)]
		public float minHorizontal = -180f;

		[Tooltip("Maximum horizontal angle in degrees (-180 to 180). Positive = right of forward.")]
		[Range(-180f, 180f)]
		public float maxHorizontal = 180f;

		[Tooltip("Reference direction for horizontal filtering. Viewpoints are measured relative to this axis.")]
		public ForwardAxis forwardAxis = ForwardAxis.PositiveZ;

		/// <summary>
		/// Get effective min/max elevation based on preset.
		/// </summary>
		public void GetEffectiveElevation(out float min, out float max)
		{
			switch (viewpointPreset)
			{
				case ViewpointPreset.UpperHemisphere:
					min = -10f;
					max = 90f;
					break;
				case ViewpointPreset.SideRing:
					min = -20f;
					max = 30f;
					break;
				case ViewpointPreset.Custom:
					min = minElevation;
					max = maxElevation;
					break;
				default: // FullSphere
					min = -90f;
					max = 90f;
					break;
			}
		}

		/// <summary>
		/// Convert ForwardAxis enum to Unity Vector3 direction.
		/// </summary>
		public static Vector3 ForwardAxisToVector(ForwardAxis axis)
		{
			switch (axis)
			{
				case ForwardAxis.PositiveZ:  return Vector3.forward;
				case ForwardAxis.NegativeZ:  return Vector3.back;
				case ForwardAxis.PositiveX:  return Vector3.right;
				case ForwardAxis.NegativeX:  return Vector3.left;
				case ForwardAxis.PositiveY:  return Vector3.up;
				case ForwardAxis.NegativeY:  return Vector3.down;
				default:                     return Vector3.forward;
			}
		}

		/// <summary>
		/// Convert Unity axis direction to M3T 3D vector.
		/// M3T is Y-down, Unity is Y-up. X and Z are the same.
		/// +Y → (0,-1,0), -Y → (0,1,0), others unchanged.
		/// </summary>
		private static void UnityAxisToM3T(Vector3 unityDir, out float mx, out float my, out float mz)
		{
			mx = unityDir.x;
			my = -unityDir.y;  // flip Y for M3T Y-down convention
			mz = unityDir.z;
		}

		/// <summary>
		/// Get horizontal filter parameters.
		/// Returns enabled=false when filter is off or forward axis is parallel to up axis.
		/// </summary>
		public void GetEffectiveHorizontal(out bool enabled, out float min, out float max,
			out Vector3 fwdDir)
		{
			if (!enableHorizontalFilter)
			{
				enabled = false;
				min = -180f;
				max = 180f;
				fwdDir = Vector3.forward;
				return;
			}

			fwdDir = ForwardAxisToVector(forwardAxis);
			Vector3 upDir = ForwardAxisToVector(upAxis);

			// Forward parallel to up — can't define azimuth plane
			if (Mathf.Abs(Vector3.Dot(fwdDir, upDir)) > 0.99f)
			{
				enabled = false;
				min = -180f;
				max = 180f;
				return;
			}

			enabled = true;
			min = minHorizontal;
			max = maxHorizontal;
		}

		public FTModelConfig ToNativeConfig(float geometryScale = 1f)
		{
			GetEffectiveElevation(out float minElev, out float maxElev);
			GetEffectiveHorizontal(out bool horizEnabled, out float horizMin, out float horizMax,
				out Vector3 fwdDir);

			// Convert up and forward axes to M3T coordinates
			UnityAxisToM3T(ForwardAxisToVector(upAxis), out float upX, out float upY, out float upZ);
			UnityAxisToM3T(fwdDir, out float fwdX, out float fwdY, out float fwdZ);

			return new FTModelConfig
			{
				geometry_unit_in_meter = geometryScale,
				sphere_radius = sphereRadius,
				n_divides = nDivides,
				n_points = nPoints,
				max_radius_depth_offset = maxRadiusDepthOffset,
				stride_depth_offset = strideDepthOffset,
				image_size = imageSize,
				viewpoint_min_elevation = minElev,
				viewpoint_max_elevation = maxElev,
				enable_horizontal_filter = horizEnabled ? 1 : 0,
				horizontal_min = horizMin,
				horizontal_max = horizMax,
				forward_axis_x = fwdX,
				forward_axis_y = fwdY,
				forward_axis_z = fwdZ,
				up_axis_x = upX,
				up_axis_y = upY,
				up_axis_z = upZ
			};
		}
	}

	/// <summary>
	/// Pose filtering settings (Unity-side post-processing).
	/// </summary>
	[Serializable]
	public class SmoothingSettings
	{
		[Tooltip("Clamp pose changes that exceed thresholds. Works independently of smoothing mode - can be used alone to catch sudden jumps without adding interpolation lag.")]
		[SerializeField]
		private bool _enableOutlierRejection = true;

		[Tooltip("Maximum allowed position change per second in meters.")] [Range(0.25f, 5f)] [SerializeField]
		private float _maxPositionDelta = 1.5f;

		[Tooltip("Maximum allowed rotation change per second in degrees.")] [Range(10f, 720f)] [SerializeField]
		private float _maxRotationDelta = 300f;

		[Tooltip("Smoothing algorithm: None = raw pose, Lerp = simple interpolation, Kalman = adaptive filtering")] [SerializeField]
		private SmoothingMode _mode = SmoothingMode.Kalman;

		[Tooltip("Time constant for position smoothing. Lower = faster response, more jitter.")] [Range(0.01f, 0.5f)] [SerializeField]
		private float _positionSmoothTime = 0.08f;

		[Tooltip("Time constant for rotation smoothing. Lower = faster response, more jitter.")] [Range(0.01f, 0.5f)] [SerializeField]
		private float _rotationSmoothTime = 0.08f;

		[Tooltip("Position process noise. Higher = faster adaptation to movement, more jitter.")] [Range(0.01f, 0.75f)] [SerializeField]
		private float _posProcessNoise = 0.15f;

		[Tooltip("Velocity process noise. Higher = faster velocity changes, less smooth motion.")] [Range(0.1f, 2f)] [SerializeField]
		private float _velProcessNoise = 1.5f;

		[Tooltip("Measurement noise. Higher = trust predictions more, smoother but more lag.")] [Range(0.001f, 0.5f)] [SerializeField]
		private float _measurementNoise = 0.01f;

		[Tooltip("Rotation smoothness factor. Higher = smoother rotation, more lag.")] [Range(0.001f, 0.3f)] [SerializeField]
		private float _rotationSmoothness = 0.01f;

		public SmoothingMode Mode => _mode;
		public float PositionSmoothTime => _positionSmoothTime;
		public float RotationSmoothTime => _rotationSmoothTime;
		public float PosProcessNoise => _posProcessNoise;
		public float VelProcessNoise => _velProcessNoise;
		public float MeasurementNoise => _measurementNoise;
		public float RotationSmoothness => _rotationSmoothness;
		public bool EnableOutlierRejection => _enableOutlierRejection;
		public float MaxPositionDelta => _maxPositionDelta;
		public float MaxRotationDelta => _maxRotationDelta;
	}

	/// <summary>
	/// Depth tracking tuning parameters. Adjust these when using RealSense depth camera.
	/// </summary>
	[Serializable]
	public class DepthTrackingSettings
	{
		[Tooltip("Maximum distance (meters) between measured and expected depth for a point to be considered valid. Higher = more tolerant of noise, lower = stricter matching.")]
		[Range(0.005f, 0.1f)]
		[SerializeField]
		private float _distanceTolerance = 0.02f;

		[Tooltip("Distance (meters) between sampled correspondence points. Lower = more points, slower. Higher = fewer points, faster.")] [Range(0.001f, 0.02f)] [SerializeField]
		private float _strideLength = 0.005f;

		[Tooltip("Radius (meters) for self-occlusion detection. Points within this radius are checked for occlusion by other parts of the model.")]
		[Range(0.005f, 0.05f)]
		[SerializeField]
		private float _selfOcclusionRadius = 0.01f;

		[Tooltip("Depth threshold (meters) for self-occlusion. If another part of the model is closer by this amount, the point is considered occluded.")]
		[Range(0.01f, 0.1f)]
		[SerializeField]
		private float _selfOcclusionThreshold = 0.03f;

		[Tooltip("Radius (meters) for external occlusion detection from measured depth.")] [Range(0.005f, 0.05f)] [SerializeField]
		private float _measuredOcclusionRadius = 0.01f;

		[Tooltip("Depth threshold (meters) for external occlusion. If measured depth is closer by this amount, the point is considered occluded.")]
		[Range(0.01f, 0.1f)]
		[SerializeField]
		private float _measuredOcclusionThreshold = 0.03f;

		/// <summary>
		/// Maximum distance between measured and expected depth for valid correspondence.
		/// </summary>
		public float DistanceTolerance
		{
			get => _distanceTolerance;
			set => _distanceTolerance = Mathf.Clamp(value, 0.005f, 0.1f);
		}

		/// <summary>
		/// Distance between sampled correspondence points.
		/// </summary>
		public float StrideLength
		{
			get => _strideLength;
			set => _strideLength = Mathf.Clamp(value, 0.001f, 0.02f);
		}

		/// <summary>
		/// Radius for self-occlusion detection (modeled occlusion).
		/// </summary>
		public float SelfOcclusionRadius
		{
			get => _selfOcclusionRadius;
			set => _selfOcclusionRadius = Mathf.Clamp(value, 0.005f, 0.05f);
		}

		/// <summary>
		/// Depth threshold for self-occlusion detection.
		/// </summary>
		public float SelfOcclusionThreshold
		{
			get => _selfOcclusionThreshold;
			set => _selfOcclusionThreshold = Mathf.Clamp(value, 0.01f, 0.1f);
		}

		/// <summary>
		/// Radius for external/measured occlusion detection.
		/// </summary>
		public float MeasuredOcclusionRadius
		{
			get => _measuredOcclusionRadius;
			set => _measuredOcclusionRadius = Mathf.Clamp(value, 0.005f, 0.05f);
		}

		/// <summary>
		/// Depth threshold for external/measured occlusion detection.
		/// </summary>
		public float MeasuredOcclusionThreshold
		{
			get => _measuredOcclusionThreshold;
			set => _measuredOcclusionThreshold = Mathf.Clamp(value, 0.01f, 0.1f);
		}
	}

	/// <summary>
	/// Edge modality tuning parameters. Controls render-based edge detection from depth discontinuities.
	/// </summary>
	[Serializable]
	public class EdgeTrackingSettings
	{
		[Tooltip("Depth discontinuity threshold in meters for edge detection. " +
		         "Lower = more edges detected (catches subtle depth changes). " +
		         "Higher = only sharp depth edges.")]
		[Range(0.001f, 0.05f)]
		[SerializeField]
		private float _depthEdgeThreshold = 0.01f;

		[Tooltip("Search radius for edge correspondence search (relative to image width). " +
		         "Larger = tolerates faster motion but risks wrong matches. " +
		         "Smaller = more stable but may lose tracking during fast motion.")]
		[Range(0.005f, 0.2f)]
		[SerializeField]
		private float _searchRadius = 0.03125f;

		[Tooltip("Minimum contrast threshold for edge correspondences. " +
		         "The adaptive threshold (50% of peak response) also applies on top. " +
		         "Higher = only strong edges. Lower = more correspondences.")]
		[Range(1f, 765f)]
		[SerializeField]
		private float _minGradient = 15f;

		[Tooltip("Pixel spacing between sample points along visible edges. " +
		         "Lower = denser (more accurate, slower). " +
		         "Higher = sparser (faster).")]
		[Range(2f, 20f)]
		[SerializeField]
		private float _sampleStep = 6f;

		[Tooltip("Search radius multiplier per correspondence iteration (coarse-to-fine). " +
		         "Higher early values search wider for robustness. Lower late values for precision. " +
		         "Default: 3, 2, 1.5, 1 (same progression as region modality scales).")]
		[SerializeField]
		private float[] _searchRadiusScales = { 3f, 2f, 1.5f, 1f };

		[Tooltip("Measurement noise per correspondence iteration (coarse-to-fine). " +
		         "Higher early values = weaker Hessian = Tikhonov damping effective = conservative steps. " +
		         "Lower late values = precise alignment. " +
		         "Default: 15, 5, 3.5, 1.5 (same as region modality).")]
		[SerializeField]
		private float[] _standardDeviations = { 15f, 5f, 3.5f, 1.5f };

		[Tooltip("Reuse edge sites across frames instead of re-rendering every frame. " +
		         "Reduces jitter by keeping a stable set of 3D reference points. " +
		         "Sites are refreshed when pose drifts beyond rotation/translation thresholds.")]
		[SerializeField]
		private bool _useKeyframe = true;

		[Tooltip("Rotation threshold (degrees) to trigger keyframe refresh. Default: 3.0")]
		[Range(0.1f, 20f)]
		[SerializeField]
		private float _keyframeRotationDeg = 3f;

		[Tooltip("Translation threshold (meters) to trigger keyframe refresh. Default: 0.03")]
		[Range(0.001f, 1f)]
		[SerializeField]
		private float _keyframeTranslation = 0.03f;

		[HideInInspector]
		[SerializeField]
		private float _probeSearchRadiusScale = 0.3f;

		[HideInInspector]
		[SerializeField]
		private float _probeMaxProjectionErrorDeg = 20f;

		[HideInInspector]
		[SerializeField]
		private float _probeMaxResidualPx = 6f;

		[Tooltip("Detect edges at sharp surface creases (chamfers, holes, fillets). " +
		         "Requires mesh normals from Unity.")]
		[SerializeField]
		private bool _enableCreaseEdges = false;

		[Tooltip("Angle threshold in degrees. Surface normals differing by more than this angle create edge features.")]
		[Range(5f, 90f)]
		[SerializeField]
		private float _creaseEdgeAngle = 60f;

		public float DepthEdgeThreshold
		{
			get => _depthEdgeThreshold;
			set => _depthEdgeThreshold = Mathf.Clamp(value, 0.001f, 0.05f);
		}

		public float SearchRadius
		{
			get => _searchRadius;
			set => _searchRadius = Mathf.Clamp(value, 0.005f, 0.2f);
		}

		public float MinGradient
		{
			get => _minGradient;
			set => _minGradient = Mathf.Clamp(value, 1f, 765f);
		}

		public float SampleStep
		{
			get => _sampleStep;
			set => _sampleStep = Mathf.Clamp(value, 2f, 20f);
		}

		public float[] SearchRadiusScales => _searchRadiusScales;
		public float[] StandardDeviations => _standardDeviations;
		public bool UseKeyframe
		{
			get => _useKeyframe;
			set => _useKeyframe = value;
		}

		public float KeyframeRotationDeg
		{
			get => _keyframeRotationDeg;
			set => _keyframeRotationDeg = Mathf.Clamp(value, 0.1f, 10f);
		}

		public float KeyframeTranslation
		{
			get => _keyframeTranslation;
			set => _keyframeTranslation = Mathf.Clamp(value, 0.001f, 0.1f);
		}

		public float ProbeSearchRadiusScale
		{
			get => _probeSearchRadiusScale;
			set => _probeSearchRadiusScale = Mathf.Clamp(value, 0.05f, 1f);
		}

		public float ProbeMaxProjectionErrorDeg
		{
			get => _probeMaxProjectionErrorDeg;
			set => _probeMaxProjectionErrorDeg = Mathf.Clamp(value, 1f, 90f);
		}

		public float ProbeMaxResidualPx
		{
			get => _probeMaxResidualPx;
			set => _probeMaxResidualPx = Mathf.Clamp(value, 0.5f, 30f);
		}

		public bool EnableCreaseEdges
		{
			get => _enableCreaseEdges;
			set => _enableCreaseEdges = value;
		}

		public float CreaseEdgeAngle
		{
			get => _creaseEdgeAngle;
			set => _creaseEdgeAngle = Mathf.Clamp(value, 5f, 90f);
		}
	}

	/// <summary>
	/// Multi-scale pyramid settings for edge tracking. Adjust for thin objects at distance.
	/// </summary>
	[Serializable]
	public class MultiScaleSettings
	{
		[Tooltip("Image downsampling factors for coarse-to-fine optimization. " +
		         "Higher values = more downsampling (coarser, faster, handles large motion). " +
		         "Lower values = less downsampling (finer, more precise). " +
		         "Default: 6, 4, 2, 1 (processes at 1/6, 1/4, 1/2, and full resolution). " +
		         "For thin objects at distance: use smaller values like 3, 2, 1. " +
		         "For fast motion: use larger values like 8, 6, 4, 2, 1.")]
		[SerializeField]
		private int[] _scales = { 6, 4, 2, 1 };

		[Tooltip("Per-scale uncertainty in pixels for correspondence search. Must have same count as scales. " +
		         "Higher values = more tolerant to correspondence errors (robust but less precise). " +
		         "Lower values = stricter matching (precise but may fail with noise). " +
		         "Default: 15, 5, 3.5, 1.5 (decreasing with finer scales). " +
		         "For thin objects: consider lower values like 8, 4, 2.")]
		[SerializeField]
		private float[] _standardDeviations = { 15f, 5f, 3.5f, 1.5f };

		public int[] Scales => _scales;
		public float[] StandardDeviations => _standardDeviations;

		/// <summary>
		/// Convert to native struct for C++ interop.
		/// </summary>
		internal FTRegionModalityAdvanced ToNative(SilhouetteTrackingSettings silhouette)
		{
			var result = new FTRegionModalityAdvanced
			{
				scales = new int[8],
				standard_deviations = new float[8],
				min_continuous_distance = silhouette.MinContinuousDistance,
				use_min_continuous_distance = 1,
				function_length = silhouette.FunctionLength,
				use_function_length = 1,
				n_histogram_bins = silhouette.HistogramBins,
				use_n_histogram_bins = silhouette.HistogramBins != 16 ? 1 : 0
			};

			// Copy scales
			if (_scales != null && _scales.Length > 0)
			{
				int count = Math.Min(_scales.Length, 8);
				for (int i = 0; i < count; i++)
					result.scales[i] = _scales[i];
				result.scales_count = count;
			}

			// Copy standard deviations
			if (_standardDeviations != null && _standardDeviations.Length > 0)
			{
				int count = Math.Min(_standardDeviations.Length, 8);
				for (int i = 0; i < count; i++)
					result.standard_deviations[i] = _standardDeviations[i];
				result.standard_deviations_count = count;
			}

			return result;
		}
	}

	/// <summary>
	/// Occlusion detection parameters for silhouette modality.
	/// Controls how the tracker determines when silhouette points are occluded by other objects.
	/// </summary>
	[Serializable]
	public class OcclusionSettings
	{
		[Tooltip("Search radius (meters) around each silhouette point to check for occlusion.")]
		[Range(0.005f, 0.05f)]
		[SerializeField]
		private float _radius = 0.01f;

		[Tooltip("Depth threshold (meters). A point is occluded if an occluder is closer than this. " +
		         "Use smaller values (0.005-0.01m) for tight assemblies.")]
		[Range(0.001f, 0.05f)]
		[SerializeField]
		private float _threshold = 0.01f;

		/// <summary>
		/// Search radius for occlusion detection around silhouette points.
		/// </summary>
		public float Radius
		{
			get => _radius;
			set => _radius = Mathf.Clamp(value, 0.005f, 0.05f);
		}

		/// <summary>
		/// Depth threshold for occlusion detection.
		/// Points are occluded if occluding geometry is closer than this distance.
		/// </summary>
		public float Threshold
		{
			get => _threshold;
			set => _threshold = Mathf.Clamp(value, 0.001f, 0.05f);
		}
	}

	/// <summary>
	/// Advanced edge tracking tuning parameters. Adjust these for difficult objects like thin tubes.
	/// </summary>
	[Serializable]
	public class SilhouetteTrackingSettings
	{
		[Tooltip(
			"How tolerant the tracker is to blurry or indistinct edges. Higher values are more forgiving, useful for thin objects like tubes where edge signal is weaker. Lower values expect sharp, high-contrast edges.")]
		[Range(0.3f, 0.6f)]
		[SerializeField]
		private float _edgeTolerance = 0.43f;

		[Tooltip(
			"How aggressively the tracker adjusts pose each frame. Lower values make smaller, more cautious adjustments - useful when few edge points are available (thin objects). Higher values converge faster but may overshoot or oscillate.")]
		[Range(0.5f, 2.5f)]
		[SerializeField]
		private float _updateResponsiveness = 1.3f;

		[Tooltip("Minimum uninterrupted foreground/background region in pixels. " +
		         "Regions smaller than this are considered invalid (noise rejection). " +
		         "Default: 3.0. For thin objects: lower to 1.5-2.0. For noisy images: increase to 4.0-5.0.")]
		[Range(1f, 6f)]
		[SerializeField]
		private float _minContinuousDistance = 3f;

		[Tooltip("Number of pixels sampled perpendicular to contour for edge detection. " +
		         "Default: 8. For thin objects: reduce to 4-6.")]
		[Range(4, 16)]
		[SerializeField]
		private int _functionLength = 8;

		[Tooltip("Bins per RGB channel for color histograms. Power of 2 (2-64). " +
		         "Higher = finer color differences. Default: 16 (4096 total bins).")]
		[SerializeField]
		private int _histogramBins = 16;

		public float EdgeTolerance
		{
			get => _edgeTolerance;
			set => _edgeTolerance = Mathf.Clamp(value, 0.3f, 0.6f);
		}

		public float UpdateResponsiveness
		{
			get => _updateResponsiveness;
			set => _updateResponsiveness = Mathf.Clamp(value, 0.5f, 2.5f);
		}

		public float MinContinuousDistance => _minContinuousDistance;
		public int FunctionLength => _functionLength;
		public int HistogramBins => _histogramBins;

		// Internal mapping to native parameter names
		internal float FunctionAmplitude => _edgeTolerance;
		internal float LearningRate => _updateResponsiveness;
	}

	/// <summary>
	/// Global auto-tracking settings. Static defaults - adjust these values as needed.
	/// TrackingQuality uses histogram discriminability, ShapeQuality uses variance StdDev.
	/// </summary>
	public static class TrackerDefaults
	{
		/// <summary>A minimum quality score (0-1) required to start tracking.</summary>
		public const float QUALITY_TO_START = 0.3f;

		/// <summary>Consecutive frames with good quality required before starting.</summary>
		public const int FRAMES_TO_START = 5;

		/// <summary>Consecutive frames with bad quality required before stopping.</summary>
		public const int FRAMES_TO_LOSE = 5;

		/// <summary>Consecutive nice-quality frames required before re-accepting native poses after a quality dip.</summary>
		public const int FRAMES_TO_RECOVER = 5;

		/// <summary>Frames after tracking starts before quality-based decisions (loss/SLAM entry) are allowed.</summary>
		public const int STABILIZATION_FRAMES = 30;

		/// <summary>Quality threshold for "nice" tracking status (vs "poor").</summary>
		public const float NICE_QUALITY_THRESHOLD = 0.8f;

		/// <summary>Quality threshold below which tracking is considered lost.</summary>
		public const float LOSE_TRACKING_THRESHOLD = 0.4f;

		// Shape Quality constants (model fit validation)
		// Based on variance StdDev: correct models show sd ~0.10-0.16, wrong models show sd > 0.25
		/// <summary>StdDev floor for the perfect shape fit. Correct models typically achieve ~0.10-0.16.</summary>
		public const float SD_MIN = 0.10f;

		/// <summary>StdDev ceiling for poor shape fit. Wrong models can show sd > 1.0.</summary>
		public const float SD_MAX = 3f;

		// Tracking Quality constants (histogram-based operational confidence)
		/// <summary>Histogram discriminability value for good tracking (maps to quality 1.0). Also used as default/reset value for global peak.</summary>
		public const float HISTOGRAM_GOOD = 0.7f;

		/// <summary>Histogram discriminability value for poor tracking (maps to quality 0.0).</summary>
		public const float HISTOGRAM_BAD = 0.0f;

		// Adaptive Quality constants (global peak tracking)
		/// <summary>Minimum value for global histogram peak. Floor for quality reference.</summary>
		public const float PEAK_MIN = 0.6f;

		/// <summary>Maximum value for global histogram peak. Ceiling for quality reference.</summary>
		public const float PEAK_MAX = 0.85f;

		/// <summary>Peak decay rate per frame. At 60fps, 0.9995 = ~3% decay per second.</summary>
		public const float PEAK_DECAY_RATE = 0.9995f;

		/// <summary>Consecutive good frames before exiting SLAM recovery.</summary>
		public const int STATIONARY_FRAMES_TO_RECOVER = 5;
		
		// Edge modality thresholds (different operating range than silhouette)
		public const float EDGE_QUALITY_TO_START = 0.65f;
		public const float EDGE_LOSE_TRACKING_THRESHOLD = 0.5f;
		public const float EDGE_NICE_QUALITY_THRESHOLD = 0.65f;
		
		// Edge Modality Quality constants
		/// <summary>Edge coverage value that maps to quality 1.0 via InverseLerp(0, this, coverage).</summary>
		public const float EDGE_QUALITY_COVERAGE_MAX = 0.55f;
		/// <summary>Edge median residual (pixels) fallback when no valid edge points.</summary>
		public const float EDGE_RESIDUAL_MAX = 15;
		/// <summary>Projection error (degrees) fallback when no valid edge points.</summary>
		public const float EDGE_PROJECTION_ERROR_BAD = 30f;


		/// <summary>Default smooth time in seconds for stationary pose corrections. 0 = instant.</summary>
		public const float DEFAULT_SMOOTH_TIME = 0.1f;

		/// <summary>
		/// Interval in seconds between AR pose corrections fed back to native
		/// when tracking quality drops below the nice threshold. Prevents the
		/// optimizer from drifting too far while the display holds last-good pose.
		/// Default 0.15s (~9 frames at 60 fps).
		/// </summary>
		public const float DEFAULT_CORRECTION_INTERVAL = 0.15f;
	}

	public enum TrackingStatus
	{
		NotTracking,
		Tracking,
		Poor
	}
}