using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// An axis direction of the object, in the object's own space. It is used for both the up axis and the forward axis of the viewpoint settings.
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

	/// <summary>
	/// Which viewing angles the tracking model includes.
	/// </summary>
	public enum ViewpointPreset
	{
		/// <summary>Full sphere. All viewing angles (default).</summary>
		FullSphere,
		/// <summary>Upper hemisphere. Top and side views, without the views from below (-10° to 90°).</summary>
		UpperHemisphere,
		/// <summary>Mostly horizontal views for objects at camera height (-20° to 30°).</summary>
		SideRing,
		/// <summary>Custom elevation range.</summary>
		Custom
	}

	/// <summary>
	/// Settings that control how the tracking model is generated from the viewpoints around the object.
	/// The silhouette model and the depth model both use them. A body that tracks by the edge method without depth uses none of them.
	/// </summary>
	[Serializable]
	public class ModelSettings
	{
		[Tooltip("The distance in meters from the center of the object to the virtual camera viewpoints. A value of -1 uses 0.8 times the diameter of the object.")]
		public float sphereRadius = -1f;

		[Tooltip("How many times the sphere of viewpoints is subdivided. A value of 2 gives 162 views, 3 gives 642 views and 4 gives 2562 views, and a value of -1 uses 3. " +
		         "More views take longer to generate and make the model larger.")]
		public int nDivides = 2;

		[Tooltip("How many outline points the generator samples for each view. A value of -1 uses 200. More points improve tracking and use more memory.")]
		public int nPoints = 200;

		[Tooltip("The largest radius in meters that the generator uses to calculate the depth offset of an outline point. A value of -1 uses 0.05 meters.")]
		public float maxRadiusDepthOffset = -1f;

		[Tooltip("The stride in meters between the samples of the depth offset. A value of -1 uses 0.002 meters.")]
		public float strideDepthOffset = -1f;

		[Tooltip("The resolution in pixels of the internal render that the generator draws for each view. A value of -1 uses 2000. A higher resolution gives better quality and takes longer to generate.")]
		public int imageSize = -1;

		[Tooltip("Which axis of the object points up. The elevation angles are measured from this direction. Change it for models that were authored with Z pointing up.")]
		public ForwardAxis upAxis = ForwardAxis.PositiveY;

		[Tooltip("Which viewing angles the model includes. A smaller range reduces the size of the model and the time it takes to generate, for objects that are only seen from certain directions.")]
		public ViewpointPreset viewpointPreset = ViewpointPreset.FullSphere;

		[Tooltip("The lowest elevation angle in degrees. A value of -90 looks from directly below, 0 from the side and 90 from directly above.")]
		[Range(-90f, 90f)]
		public float minElevation = -90f;

		[Tooltip("The highest elevation angle in degrees. A value of -90 looks from directly below, 0 from the side and 90 from directly above.")]
		[Range(-90f, 90f)]
		public float maxElevation = 90f;

		[Tooltip("Limits the viewpoints to a horizontal (azimuth) range around a forward direction.")]
		public bool enableHorizontalFilter = false;

		[Tooltip("The smallest horizontal angle in degrees, from -180 to 180. A negative angle is to the left of the forward direction.")]
		[Range(-180f, 180f)]
		public float minHorizontal = -180f;

		[Tooltip("The largest horizontal angle in degrees, from -180 to 180. A positive angle is to the right of the forward direction.")]
		[Range(-180f, 180f)]
		public float maxHorizontal = 180f;

		[Tooltip("The reference direction of the horizontal filter. The viewpoints are measured from this axis.")]
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
		/// Convert a direction in Unity's axis convention to the native tracker's.
		/// The native frame is Y-down and Unity's is Y-up; X and Z agree.
		/// +Y → (0,-1,0), -Y → (0,1,0), others unchanged.
		/// </summary>
		private static void UnityDirectionToNativeAxes(Vector3 unityDir, out float nx, out float ny, out float nz)
		{
			nx = unityDir.x;
			ny = -unityDir.y;  // flip Y for the native Y-down convention
			nz = unityDir.z;
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

			// Convert up and forward axes to native tracker coordinates
			UnityDirectionToNativeAxes(ForwardAxisToVector(upAxis), out float upX, out float upY, out float upZ);
			UnityDirectionToNativeAxes(fwdDir, out float fwdX, out float fwdY, out float fwdZ);

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
	/// Depth tracking tuning parameters. Adjust them to change how the measurements of the depth sensor are matched to the model.
	/// </summary>
	[Serializable]
	public class DepthTrackingSettings
	{
		[Tooltip("The largest distance in meters between the measured depth and the expected depth for which a point still counts as valid. A higher value tolerates more noise and a lower value matches more strictly.")]
		[Range(0.005f, 0.1f)]
		[SerializeField]
		private float _distanceTolerance = 0.02f;

		[Tooltip("The distance in meters between the sampled correspondence points. A lower value gives more points and runs slower. A higher value gives fewer points and runs faster.")] [Range(0.001f, 0.02f)] [SerializeField]
		private float _strideLength = 0.005f;

		[Tooltip("The radius in meters of the self-occlusion check. The tracker checks the points within this radius for occlusion by other parts of the model.")]
		[Range(0.005f, 0.05f)]
		[SerializeField]
		private float _selfOcclusionRadius = 0.01f;

		[Tooltip("The depth threshold in meters of the self-occlusion check. A point counts as occluded when another part of the model is closer by this amount.")]
		[Range(0.01f, 0.1f)]
		[SerializeField]
		private float _selfOcclusionThreshold = 0.03f;

		[Tooltip("The radius in meters of the check for occlusion by other objects, which the tracker takes from the measured depth.")] [Range(0.005f, 0.05f)] [SerializeField]
		private float _measuredOcclusionRadius = 0.01f;

		[Tooltip("The depth threshold in meters of the check for occlusion by other objects. A point counts as occluded when the measured depth is closer by this amount.")]
		[Range(0.01f, 0.1f)]
		[SerializeField]
		private float _measuredOcclusionThreshold = 0.03f;

		[Tooltip("The measurement noise in meters of the depth correspondences, one value for each correspondence iteration from coarse to fine. " +
		         "Larger early values make the first iterations cautious and smaller late values make the last ones precise. " +
		         "The tracker uses the last value for any later iteration. The defaults are 0.05, 0.03 and 0.02.")]
		[SerializeField]
		private float[] _standardDeviations = { 0.05f, 0.03f, 0.02f };

		/// <summary>The standard deviations the plugin uses when nothing sets them, one for each correspondence iteration, in meters.</summary>
		internal static readonly float[] DefaultStandardDeviations = { 0.05f, 0.03f, 0.02f };

		/// <summary>The most standard deviations the plugin takes, which is also the most correspondence iterations it keeps a value for.</summary>
		public const int MAX_STANDARD_DEVIATIONS = 8;

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

		/// <summary>
		/// Measurement noise of the depth correspondences in meters, one value for each correspondence iteration from coarse to fine.
		/// </summary>
		/// <remarks>
		/// The defaults are 0.05, 0.03 and 0.02. At registration the tracker is given these values, and a change while the body plays
		/// reaches the tracker at its next step when the plugin build supports it.
		/// </remarks>
		public float[] StandardDeviations => _standardDeviations;
	}

	/// <summary>
	/// The two depth settings that decide how a depth correspondence is searched and weighed, which are the ones the plugin can change while a
	/// body tracks. Every other depth setting is read once, when the body registers.
	/// </summary>
	internal readonly struct DepthCorrespondenceSettings : IEquatable<DepthCorrespondenceSettings>
	{
		private readonly float[] _standardDeviations;

		internal DepthCorrespondenceSettings(float consideredDistance, float[] standardDeviations)
		{
			ConsideredDistance = consideredDistance;
			_standardDeviations = Normalised(standardDeviations);
		}

		/// <summary>The distance in meters within which a measured depth is taken as the correspondence of a model point.</summary>
		internal float ConsideredDistance { get; }

		/// <summary>The measurement noise of each correspondence iteration in meters, at most <see cref="DepthTrackingSettings.MAX_STANDARD_DEVIATIONS"/> values.</summary>
		internal IReadOnlyList<float> StandardDeviations => _standardDeviations;

		/// <summary>Reads the two settings the way registration reads them, and the plugin's own standard deviations when a body states none.</summary>
		internal static DepthCorrespondenceSettings From(DepthTrackingSettings settings) =>
			new DepthCorrespondenceSettings(settings.DistanceTolerance, settings.StandardDeviations);

		/// <summary>What a plugin holds for a body that registered with this considered distance and has not been given anything since.</summary>
		internal static DepthCorrespondenceSettings AtRegistration(float consideredDistance) =>
			new DepthCorrespondenceSettings(consideredDistance, DepthTrackingSettings.DefaultStandardDeviations);

		private static float[] Normalised(float[] values)
		{
			if (values == null || values.Length == 0)
				return (float[])DepthTrackingSettings.DefaultStandardDeviations.Clone();
			int count = Math.Min(values.Length, DepthTrackingSettings.MAX_STANDARD_DEVIATIONS);
			var copy = new float[count];
			Array.Copy(values, copy, count);
			return copy;
		}

		public bool Equals(DepthCorrespondenceSettings other)
		{
			if (ConsideredDistance != other.ConsideredDistance) return false;
			float[] mine = _standardDeviations ?? Array.Empty<float>();
			float[] theirs = other._standardDeviations ?? Array.Empty<float>();
			if (mine.Length != theirs.Length) return false;
			for (int index = 0; index < mine.Length; index++)
				if (mine[index] != theirs[index]) return false;
			return true;
		}

		public override bool Equals(object obj) => obj is DepthCorrespondenceSettings other && Equals(other);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(ConsideredDistance);
			foreach (float value in _standardDeviations ?? Array.Empty<float>())
				hash.Add(value);
			return hash.ToHashCode();
		}

		/// <summary>The values in one line, with a point for the decimals whatever the culture.</summary>
		public override string ToString() => FormattableString.Invariant($"depth considered distance {ConsideredDistance:0.###} m, standard deviations ") +
			string.Join(", ", (_standardDeviations ?? Array.Empty<float>()).Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
	}

	/// <summary>
	/// Tuning parameters of edge tracking. The tracker renders the model, finds edges in the render from depth steps and from sharp creases, and searches the camera image for matching edges.
	/// </summary>
	[Serializable]
	public class EdgeTrackingSettings
	{
		[Tooltip("The depth step in meters between neighbouring pixels of the rendered model that makes an edge. " +
		         "A lower value finds more edges, including subtle depth changes. " +
		         "A higher value finds only sharp depth edges. A large object seen from far away may need several centimetres, " +
		         "so that curved surfaces such as cylinders do not produce edges where the image shows none.")]
		[Range(0.001f, 0.5f)]
		[SerializeField]
		private float _depthEdgeThreshold = 0.01f;

		[Tooltip("How far from the model the tracker searches the camera image for a matching edge, as a fraction of the image width. " +
		         "A larger radius tolerates faster motion but risks wrong matches. " +
		         "A smaller radius is more stable but may lose tracking during fast motion.")]
		[Range(0.005f, 0.2f)]
		[SerializeField]
		private float _searchRadius = 0.03125f;

		[Tooltip("The smallest contrast that an edge in the camera image needs to count as a match. " +
		         "An adaptive threshold of 50 percent of the peak response also applies on top of it. " +
		         "A higher value accepts only strong edges and a lower value gives more matches.")]
		[Range(1f, 765f)]
		[SerializeField]
		private float _minGradient = 15f;

		[Tooltip("The spacing in pixels between the sample points along the visible edges. " +
		         "A lower value samples more densely, which is more accurate and slower. " +
		         "A higher value samples more sparsely, which is faster.")]
		[Range(2f, 20f)]
		[SerializeField]
		private float _sampleStep = 6f;

		// Not serialized and not shown in the inspector. Only code inside the package sets it.
		private bool _useComputePipeline = true;

		[Tooltip("The multiplier of the search radius for each correspondence iteration, from coarse to fine. " +
		         "Higher early values search wider for robustness and lower late values search closer for precision. " +
		         "The defaults are 3, 2, 1.5 and 1.")]
		[SerializeField]
		private float[] _searchRadiusScales = { 3f, 2f, 1.5f, 1f };

		[Tooltip("The measurement noise for each correspondence iteration, from coarse to fine. " +
		         "Higher early values weight the matches less, so the stability settings damp the steps and the tracker moves cautiously. " +
		         "Lower late values give a precise alignment. " +
		         "The defaults are 15, 5, 3.5 and 1.5, the same values that silhouette tracking uses.")]
		[SerializeField]
		private float[] _standardDeviations = { 15f, 5f, 3.5f, 1.5f };

		// Not serialized and not shown in the inspector, all off. Each is written into the
		// body configuration at registration and only code inside the package sets it.
		// The configuration keeps their fields for its layout.
		private bool _restoreAllKeyframeSites;
		private bool _enableCandidateDiagnostics;
		private bool _enableSiteReliabilityDiagnostics;
		private bool _enableInformationSiteDiagnostics;
		private bool _enableProbeSiteDiagnostics;
		private bool _useSelectedCandidateState;
		private bool _useDistinctResponseCandidates;

		/// <summary>The smallest rotation threshold of the edge keyframe, in degrees. A smaller value that a scene or prefab saved is read as this one.</summary>
		public const float MIN_KEYFRAME_ROTATION_DEG = 10f;

		/// <summary>The largest rotation threshold of the edge keyframe, in degrees.</summary>
		public const float MAX_KEYFRAME_ROTATION_DEG = 20f;

		// The edge sites are reused across frames and refreshed when the camera moves past the two spacings below
		// or when too few of them still find an edge. The reuse itself is always on.
		[Tooltip("How far the camera may rotate about its own centre, in degrees, before the tracker draws the edges of the model again. A rotation about the centre changes no visibility, so this limit is wide. The value runs from 10 to 20 degrees, and a smaller value saved in a scene is read as 10. The default is 20.")]
		[Range(MIN_KEYFRAME_ROTATION_DEG, MAX_KEYFRAME_ROTATION_DEG)]
		[SerializeField]
		private float _keyframeRotationDeg = 20f;

		// A smaller spacing draws the edges of the model again more often, which keeps its sites
		// closer to the current view at the cost of more renders.
		[Tooltip("How far the camera must move, in meters, from the pose of a stored view before the tracker draws the edges of the model again. It is also the radius within which the tracker may reuse a stored view instead of rendering again. Some objects hold better with a narrower spacing, for example 0.01. The default is 0.03.")]
		[Range(0.001f, 1f)]
		[SerializeField]
		private float _keyframeTranslation = 0.03f;

		[Tooltip("The tracker draws the edges of the model again when the sites that still find an edge fall below this fraction of the sites it started with. The default is 0.1.")]
		[Range(0f, 1f)]
		[SerializeField]
		private float _keyframeDepletionThreshold = 0.1f;

		// The quality probe's search radius scale, projection error limit and residual
		// limit, and the per-site contrast threshold. Not serialized and not in the
		// inspector, so no scene can carry a value that differs from these.
		private float _probeSearchRadiusScale = 0.3f;
		private float _probeMaxProjectionErrorDeg = 20f;
		private float _probeMaxResidualPx = 6f;
		private bool _usePerSiteThreshold = true;

		[Tooltip("Detects edges at sharp surface creases such as chamfers, holes and fillets. " +
		         "It needs the mesh normals from Unity.")]
		[SerializeField]
		private bool _enableCreaseEdges = false;

		[Tooltip("The angle in degrees above which two neighbouring surface normals create an edge.")]
		[Range(5f, 90f)]
		[SerializeField]
		private float _creaseEdgeAngle = 60f;

		/// <summary>The largest depth step, in meters, that the depth edge threshold may be set to.</summary>
		public const float MAX_DEPTH_EDGE_THRESHOLD = 0.5f;

		/// <summary>
		/// The depth step, in meters, between neighbouring pixels of the rendered depth that makes a depth edge, kept from
		/// 0.001 to <see cref="MAX_DEPTH_EDGE_THRESHOLD"/>.
		/// </summary>
		public float DepthEdgeThreshold
		{
			get => _depthEdgeThreshold;
			set => _depthEdgeThreshold = Mathf.Clamp(value, 0.001f, MAX_DEPTH_EDGE_THRESHOLD);
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

		internal bool UseComputePipeline
		{
			get => _useComputePipeline;
			set => _useComputePipeline = value;
		}

		public float[] SearchRadiusScales => _searchRadiusScales;
		public float[] StandardDeviations => _standardDeviations;

		/// <summary>
		/// Kept for source compatibility. The edge keyframe is always on, so the getter
		/// returns true and the setter does nothing.
		/// </summary>
		[System.Obsolete("The edge keyframe is always on. Setting this has no effect.")]
		public bool UseKeyframe
		{
			get => true;
			set { }
		}

		internal bool RestoreAllKeyframeSites
		{
			get => _restoreAllKeyframeSites;
			set => _restoreAllKeyframeSites = value;
		}

		internal bool EnableCandidateDiagnostics
		{
			get => _enableCandidateDiagnostics;
			set => _enableCandidateDiagnostics = value;
		}

		internal bool EnableSiteReliabilityDiagnostics
		{
			get => _enableSiteReliabilityDiagnostics;
			set => _enableSiteReliabilityDiagnostics = value;
		}

		internal bool EnableInformationSiteDiagnostics
		{
			get => _enableInformationSiteDiagnostics;
			set => _enableInformationSiteDiagnostics = value;
		}

		internal bool EnableProbeSiteDiagnostics
		{
			get => _enableProbeSiteDiagnostics;
			set => _enableProbeSiteDiagnostics = value;
		}

		internal bool UseSelectedCandidateState
		{
			get => _useSelectedCandidateState;
			set => _useSelectedCandidateState = value;
		}

		internal bool UseDistinctResponseCandidates
		{
			get => _useDistinctResponseCandidates;
			set => _useDistinctResponseCandidates = value;
		}

		// Each clamp is the declared inspector range of its own field, and not a
		// second, tighter bound, so a value authored in a scene and a value
		// assigned in code mean the same thing.
		/// <summary>
		/// The rotation of the body relative to the camera, in degrees, past which the edge sites are drawn again, from
		/// <see cref="MIN_KEYFRAME_ROTATION_DEG"/> to <see cref="MAX_KEYFRAME_ROTATION_DEG"/>.
		/// </summary>
		/// <remarks>
		/// A scene or prefab that saved a smaller value reads the smallest one, and the saved value is left as it is until it is edited. A smaller
		/// limit draws the edge sites again on every small turn, at whatever pose the solve has reached, so the sites can follow a pose that has
		/// drifted from the object. The getter holds the range too, because this is the value the body registers with.
		/// </remarks>
		public float KeyframeRotationDeg
		{
			get => Mathf.Clamp(_keyframeRotationDeg, ShippedDefaults.KeyframeRotationFloor(), MAX_KEYFRAME_ROTATION_DEG);
			set => _keyframeRotationDeg = Mathf.Clamp(value, ShippedDefaults.KeyframeRotationFloor(), MAX_KEYFRAME_ROTATION_DEG);
		}

		public float KeyframeTranslation
		{
			get => _keyframeTranslation;
			set => _keyframeTranslation = Mathf.Clamp(value, 0.001f, 1f);
		}

		public float KeyframeDepletionThreshold
		{
			get => _keyframeDepletionThreshold;
			set => _keyframeDepletionThreshold = Mathf.Clamp(value, 0f, 1f);
		}

		internal float ProbeSearchRadiusScale
		{
			get => _probeSearchRadiusScale;
			set => _probeSearchRadiusScale = Mathf.Clamp(value, 0.05f, 1f);
		}

		internal float ProbeMaxProjectionErrorDeg
		{
			get => _probeMaxProjectionErrorDeg;
			set => _probeMaxProjectionErrorDeg = Mathf.Clamp(value, 1f, 90f);
		}

		internal float ProbeMaxResidualPx
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

		internal bool UsePerSiteThreshold
		{
			get => _usePerSiteThreshold;
			set => _usePerSiteThreshold = value;
		}

		/// <summary>
		/// The edge settings the plugin reads only when a body registers, in one text, so that a later change to any of them can be told from a change
		/// to the five detection settings (see <see cref="EdgeDetectionSettings"/>), which the plugin can take while a body tracks.
		/// </summary>
		internal string RegistrationOnlySignature()
		{
			var text = new System.Text.StringBuilder();
			AppendNumbers(text, _searchRadiusScales);
			AppendNumbers(text, _standardDeviations);
			AppendNumbers(text, new[]
			{
				KeyframeRotationDeg, _keyframeTranslation, _keyframeDepletionThreshold,
			});
			return text.ToString();
		}

		private static void AppendNumbers(System.Text.StringBuilder text, float[] numbers)
		{
			if (numbers != null)
			{
				foreach (float number in numbers)
					text.Append(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
			}
			text.Append('|');
		}
	}

	/// <summary>
	/// The five edge settings that decide which sites a render yields and how they are searched, which are the ones the plugin can change
	/// while a body tracks. Every other edge setting is read once, when the body registers.
	/// </summary>
	internal readonly struct EdgeDetectionSettings : IEquatable<EdgeDetectionSettings>
	{
		/// <summary>The crease angle the plugin reads as "no crease edges".</summary>
		internal const float NO_CREASE_EDGES_DEG = 1000f;

		internal EdgeDetectionSettings(float depthEdgeThreshold, float searchRadius, float minGradient, float sampleStep, float creaseThresholdDeg)
		{
			DepthEdgeThreshold = depthEdgeThreshold;
			SearchRadius = searchRadius;
			MinGradient = minGradient;
			SampleStep = sampleStep;
			CreaseThresholdDeg = creaseThresholdDeg;
		}

		/// <summary>The depth step, in meters, that makes a depth edge.</summary>
		internal float DepthEdgeThreshold { get; }

		/// <summary>The search radius of the correspondence search.</summary>
		internal float SearchRadius { get; }

		/// <summary>The minimum contrast of an edge correspondence.</summary>
		internal float MinGradient { get; }

		/// <summary>The pixel spacing between sample points along a visible edge.</summary>
		internal float SampleStep { get; }

		/// <summary>The crease angle in degrees, or <see cref="NO_CREASE_EDGES_DEG"/> when crease edges are off.</summary>
		internal float CreaseThresholdDeg { get; }

		/// <summary>Reads the five settings the way registration reads them.</summary>
		internal static EdgeDetectionSettings From(EdgeTrackingSettings settings) => new EdgeDetectionSettings(
			settings.DepthEdgeThreshold,
			settings.SearchRadius,
			settings.MinGradient,
			settings.SampleStep,
			settings.EnableCreaseEdges ? settings.CreaseEdgeAngle : NO_CREASE_EDGES_DEG);

		public bool Equals(EdgeDetectionSettings other) =>
			DepthEdgeThreshold == other.DepthEdgeThreshold && SearchRadius == other.SearchRadius && MinGradient == other.MinGradient &&
			SampleStep == other.SampleStep && CreaseThresholdDeg == other.CreaseThresholdDeg;

		public override bool Equals(object obj) => obj is EdgeDetectionSettings other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(DepthEdgeThreshold, SearchRadius, MinGradient, SampleStep, CreaseThresholdDeg);

		/// <summary>The values in one line, in the names the inspector uses and with a point for the decimals whatever the culture.</summary>
		public override string ToString() => FormattableString.Invariant(
			$"depth edge threshold {DepthEdgeThreshold:0.###} m, search radius {SearchRadius:0.#####}, min gradient {MinGradient:0.##}, sample step {SampleStep:0.##}, ") +
			(CreaseThresholdDeg >= NO_CREASE_EDGES_DEG ? "crease edges off" : FormattableString.Invariant($"crease edge angle {CreaseThresholdDeg:0.##}"));
	}

	/// <summary>
	/// Multi-scale pyramid settings for silhouette tracking. Adjust them for thin objects at a distance.
	/// </summary>
	[Serializable]
	public class MultiScaleSettings
	{
		[Tooltip("The image downsampling factors for the coarse-to-fine optimization. " +
		         "Higher values downsample more, which is coarser and faster and handles large motion. " +
		         "Lower values downsample less, which is finer and more precise. " +
		         "The defaults are 6, 4, 2 and 1, which process the image at one sixth, one quarter, one half and full resolution. " +
		         "For thin objects at a distance, use smaller values such as 3, 2 and 1. " +
		         "For fast motion, use larger values such as 8, 6, 4, 2 and 1.")]
		[SerializeField]
		private int[] _scales = { 6, 4, 2, 1 };

		[Tooltip("The uncertainty in pixels of the correspondence search at each scale. It needs as many values as the scales. " +
		         "Higher values tolerate correspondence errors, which is robust but less precise. " +
		         "Lower values match more strictly, which is precise but may fail with noise. " +
		         "The defaults are 15, 5, 3.5 and 1.5, decreasing with finer scales. " +
		         "For thin objects, consider lower values such as 8, 4 and 2.")]
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
	/// Occlusion detection parameters for silhouette tracking.
	/// They control how the tracker determines when silhouette points are occluded by other objects.
	/// </summary>
	[Serializable]
	public class OcclusionSettings
	{
		[Tooltip("The radius in meters around each silhouette point in which the tracker checks for occlusion.")]
		[Range(0.005f, 0.05f)]
		[SerializeField]
		private float _radius = 0.01f;

		[Tooltip("The depth threshold in meters. A point counts as occluded when an occluder is closer than this. " +
		         "Use smaller values between 0.005 and 0.01 for tight assemblies.")]
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
	/// Advanced tuning parameters of silhouette tracking. Adjust them for difficult objects such as thin tubes.
	/// </summary>
	[Serializable]
	public class SilhouetteTrackingSettings
	{
		[Tooltip(
			"How cleanly the tracker expects the colors of the object and the colors of the background to separate across the outline. A higher value expects a clean separation. A lower value allows each side of the outline to hold some colors of the other side.")]
		[Range(0.3f, 0.6f)]
		[SerializeField]
		private float _edgeTolerance = 0.43f;

		[Tooltip(
			"How strongly the tracker adjusts the pose on each frame. A lower value makes smaller, more cautious adjustments, which helps when few outline points are available, as with thin objects. A higher value converges faster but may overshoot or oscillate.")]
		[Range(0.5f, 2.5f)]
		[SerializeField]
		private float _updateResponsiveness = 1.3f;

		[Tooltip("The smallest uninterrupted foreground or background region in pixels. " +
		         "The tracker treats smaller regions as noise and ignores them. " +
		         "The default is 3. For thin objects, lower it to between 1.5 and 2. For noisy images, raise it to between 4 and 5.")]
		[Range(1f, 6f)]
		[SerializeField]
		private float _minContinuousDistance = 3f;

		[Tooltip("How many pixels the tracker samples across the outline, perpendicular to it. " +
		         "The default is 8. For thin objects, reduce it to between 4 and 6.")]
		[Range(4, 16)]
		[SerializeField]
		private int _functionLength = 8;

		[Tooltip("The number of bins per color channel in the color histograms. Use a power of 2 from 2 to 64. " +
		         "A higher value separates finer color differences. The default is 16, which gives 4096 bins in total.")]
		[SerializeField]
		private int _histogramBins = 16;

		/// <summary>
		/// How cleanly the tracker expects the colors of the object and the colors of the background to separate across the outline,
		/// kept from 0.3 to 0.6. A higher value expects a clean separation and a lower value allows each side of the outline to hold some
		/// colors of the other side.
		/// </summary>
		public float EdgeTolerance
		{
			get => _edgeTolerance;
			set => _edgeTolerance = Mathf.Clamp(value, 0.3f, 0.6f);
		}

		/// <summary>
		/// How strongly the tracker adjusts the pose on each frame, kept from 0.5 to 2.5. A lower value makes smaller, more cautious
		/// adjustments and a higher value converges faster but may overshoot.
		/// </summary>
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
	/// The relative tracking-health lifecycle: what the site funnel is measured
	/// against, and the thresholds the temporal machine reads it with.
	/// </summary>
	/// <remarks>
	/// <para>Every number a decision reads lives here rather than at the decision,
	/// so that a value can be changed in one place.</para>
	/// <para>With <see cref="UseTrackingHealth"/> off nothing in this block is
	/// read.</para>
	/// </remarks>
	[Serializable]
	internal class TrackingHealthSettings
	{
		[Tooltip("Let the temporal tracking state drive acquisition, holding, " +
			"acceptance and the reported status, measured against what this view " +
			"normally achieves. On by default.")]
		[SerializeField] private bool _useTrackingHealth = true;

		[Tooltip("Fewest expected-visible sites a frame must carry before its " +
			"agreeing-over-expected ratio means anything.")]
		[SerializeField, Min(1)] private int _minimumExpectedSites = 30;

		[Tooltip("Highest health value reported. A view that beats its own " +
			"expectation is healthy, not proportionally better.")]
		[SerializeField, Min(1f)] private float _healthCeiling = 1.25f;

		[Tooltip("Widest bearing, in degrees, at which a stored expectation still " +
			"describes the current view.")]
		[SerializeField, Min(0f)] private float _viewBearingDegrees = 15f;

		[Tooltip("Widest range ratio, in either direction, at which a stored " +
			"expectation still describes the current view.")]
		[SerializeField, Min(1f)] private float _viewRangeRatio = 1.25f;

		[Tooltip("Weight one healthy frame carries in the running expectation.")]
		[SerializeField, Range(0f, 1f)] private float _expectationAlpha = 0.02f;

		[Tooltip("Which per-frame measurement the ratio is formed from. Both are " +
			"divided by the same per-view expectation.")]
		[SerializeField] private HealthNumerator _healthNumerator =
			HealthNumerator.AgreeingShare;

		[Tooltip("Health at or above which a tracked frame updates the expectation.")]
		[SerializeField, Range(0f, 2f)] private float _expectationUpdateHealth = 0.8f;

		[Tooltip("Health a frame must reach to count toward acquisition.")]
		[SerializeField, Range(0f, 2f)] private float _acquireFraction = 0.75f;

		[Tooltip("Consecutive counting frames that complete the verification window.")]
		[SerializeField, Min(1)] private int _acquireFrames = 5;

		[Tooltip("Health at or above which tracking is held rather than doubted.")]
		[SerializeField, Range(0f, 2f)] private float _trackedFraction = 0.55f;

		[Tooltip("Health below which the evidence is called weak.")]
		[SerializeField, Range(0f, 2f)] private float _criticalFraction = 0.35f;

		[Tooltip("Health at or below which the evidence is called catastrophic.")]
		[SerializeField, Range(0f, 2f)] private float _catastrophicFraction = 0.15f;

		[Tooltip("Consecutive frames below the holding bar that declare a loss.")]
		[SerializeField, Min(1)] private int _lostFrames = 5;

		[Tooltip("Consecutive catastrophic frames that declare a loss.")]
		[SerializeField, Min(1)] private int _catastrophicFrames = 2;

		[Tooltip("Consecutive good frames a confirmed recovery must hold.")]
		[SerializeField, Min(1)] private int _recoveryFrames = 5;

		[Tooltip("Pose step per frame, as a fraction of body diameter, at or " +
			"below which the pose counts as settled.")]
		[SerializeField, Min(0f)] private float _settleTranslationFraction = 0.005f;

		[Tooltip("Pose rotation step per frame, in degrees, at or below which the " +
			"pose counts as settled.")]
		[SerializeField, Min(0f)] private float _settleRotationDegrees = 0.5f;

		[Tooltip("Raw agreeing-over-expected ratio a frame must reach to count " +
			"toward acquisition when this view has no expectation yet.")]
		[SerializeField, Range(0f, 1f)] private float _coldStartRawFloor = 0.15f;

		[Tooltip("Widest coefficient of variation the counted cold-start frames " +
			"may show. A window that is still moving is not a window.")]
		[SerializeField, Min(0f)] private float _coldStartVariation = 0.25f;

		[Tooltip("Frames an acquisition may spend before it is given up and " +
			"detection starts over. The same budget bounds a recovery's window.")]
		[SerializeField, Min(1)] private int _acquireBudgetFrames = 90;

		[Tooltip("Appearance agreement at or above which the guard leaves health " +
			"alone. Below it the guard falls linearly to a half, which is what " +
			"refuses a pose that is edge-coherent on the wrong object. The guard " +
			"is off on a frame that carries no appearance evidence.")]
		[SerializeField, Range(0f, 1f)] private float _appearanceFloor = 0.3f;

		[Tooltip("Health a frame must reach for the automatic saved-view " +
			"admission to consider it.")]
		[SerializeField, Range(0f, 2f)] private float _admissionHealth = 0.9f;

		[Tooltip("Consecutive qualifying frames before a view is offered to the " +
			"retained bank.")]
		[SerializeField, Min(1)] private int _admissionFrames = 15;

		[Tooltip("Frames after one offer before another is made. The bank applies " +
			"its own spacing on top of this.")]
		[SerializeField, Min(0)] private int _admissionCooldownFrames = 60;

		/// <summary>Whether the relative-health lifecycle is the authority on this body.</summary>
		public bool UseTrackingHealth { get => _useTrackingHealth; set => _useTrackingHealth = value; }

		/// <summary>Fewest expected-visible sites that make a ratio meaningful.</summary>
		public int MinimumExpectedSites => Mathf.Max(1, _minimumExpectedSites);

		/// <summary>Which per-frame measurement the ratio is formed from.</summary>
		public HealthNumerator HealthNumerator
		{
			get => _healthNumerator;
			set => _healthNumerator = value;
		}

		/// <summary>Highest reported health.</summary>
		public float HealthCeiling => Mathf.Max(1f, _healthCeiling);

		/// <summary>Widest bearing at which a stored expectation still applies.</summary>
		public float ViewBearingDegrees => Mathf.Max(0f, _viewBearingDegrees);

		/// <summary>Widest range ratio at which a stored expectation still applies.</summary>
		public float ViewRangeRatio => Mathf.Max(1f, _viewRangeRatio);

		/// <summary>Weight one healthy frame carries in the running expectation.</summary>
		public float ExpectationAlpha => Mathf.Clamp01(_expectationAlpha);

		/// <summary>Health at or above which a tracked frame teaches the expectation.</summary>
		public float ExpectationUpdateHealth => _expectationUpdateHealth;

		/// <summary>Pose step fraction of body diameter that still counts as settled.</summary>
		public float SettleTranslationFraction
		{
			get => Mathf.Max(0f, _settleTranslationFraction);
			set => _settleTranslationFraction = Mathf.Max(0f, value);
		}

		/// <summary>Pose rotation step in degrees that still counts as settled.</summary>
		public float SettleRotationDegrees
		{
			get => Mathf.Max(0f, _settleRotationDegrees);
			set => _settleRotationDegrees = Mathf.Max(0f, value);
		}

		/// <summary>Raw ratio floor a cold-start window frame must clear.</summary>
		public float ColdStartRawFloor => Mathf.Clamp01(_coldStartRawFloor);

		/// <summary>Widest coefficient of variation a cold-start window may show.</summary>
		public float ColdStartVariation => Mathf.Max(0f, _coldStartVariation);

		/// <summary>Frames one acquisition or recovery window may spend.</summary>
		public int AcquireBudgetFrames => Mathf.Max(1, _acquireBudgetFrames);

		/// <summary>Appearance agreement below which the guard reduces health.</summary>
		public float AppearanceFloor
		{
			get => Mathf.Clamp01(_appearanceFloor);
			set => _appearanceFloor = Mathf.Clamp01(value);
		}

		/// <summary>Health the automatic saved-view admission asks for.</summary>
		public float AdmissionHealth => _admissionHealth;

		/// <summary>Consecutive qualifying frames before a view is offered.</summary>
		public int AdmissionFrames => Mathf.Max(1, _admissionFrames);

		/// <summary>Frames between two offers.</summary>
		public int AdmissionCooldownFrames => Mathf.Max(0, _admissionCooldownFrames);

		/// <summary>
		/// The temporal policy these settings describe, rebuilt on every read so a
		/// value changed at runtime reaches the next decision.
		/// </summary>
		public TemporalTrackingPolicy Policy => new TemporalTrackingPolicy(
			_acquireFraction, Mathf.Max(1, _acquireFrames), _trackedFraction,
			_criticalFraction, _catastrophicFraction, Mathf.Max(1, _lostFrames),
			Mathf.Max(1, _catastrophicFrames), Mathf.Max(1, _recoveryFrames));
	}

	/// <summary>
	/// Default values and bars of the tracking lifecycle.
	/// TrackingQuality uses the histogram discriminability and ShapeQuality uses the standard deviation of the variance.
	/// </summary>
	public static class TrackerDefaults
	{
		/// <summary>A minimum quality score (0-1) required to start tracking.</summary>
		public const float QUALITY_TO_START = 0.3f;

		/// <summary>Consecutive frames with good quality required before starting.</summary>
		public const int FRAMES_TO_START = 5;

		/// <summary>
		/// How many of the newest judgements of a hold the take-back looks at.
		/// </summary>
		/// <remarks>
		/// Together with <see cref="TAKE_BACK_REQUIRED_FRAMES"/> this is the take-back:
		/// both equal <see cref="FRAMES_TO_START"/>, so a held body takes its lock back
		/// only after that many consecutive frames clear the hold bar.
		/// </remarks>
		internal const int TAKE_BACK_WINDOW_FRAMES = FRAMES_TO_START;

		/// <summary>
		/// How many frames inside the take-back window must clear the hold bar, in the
		/// picture, for a held body to take its lock back.
		/// </summary>
		internal const int TAKE_BACK_REQUIRED_FRAMES = FRAMES_TO_START;

		/// <summary>The widest take-back window a body can remember.</summary>
		internal const int MAX_TAKE_BACK_WINDOW_FRAMES = 32;

		/// <summary>Consecutive frames with bad quality required before stopping.</summary>
		public const int FRAMES_TO_LOSE = 5;

		/// <summary>Consecutive nice-quality frames required before re-accepting native poses after a quality dip.</summary>
		public const int FRAMES_TO_RECOVER = 5;

		/// <summary>Frames after tracking starts before quality-based decisions (loss/SLAM entry) are allowed.</summary>
		public const int STABILIZATION_FRAMES = 30;

		/// <summary>Quality threshold for "nice" tracking status, as against "poor".</summary>
		public const float NICE_QUALITY_THRESHOLD = 0.875f;

		/// <summary>Quality threshold below which tracking is considered lost.</summary>
		public const float LOSE_TRACKING_THRESHOLD = 0.4f;

		// Shape quality constants (validation of the model fit), based on the standard deviation of the variance.
		/// <summary>The standard deviation at or below which the shape fit reads as perfect.</summary>
		public const float SD_MIN = 0.10f;

		/// <summary>The standard deviation at or above which the shape fit reads as poor.</summary>
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

		/// <summary>Peak decay rate per frame. At 60 frames per second, 0.9995 is a decay of about 3 percent per second.</summary>
		public const float PEAK_DECAY_RATE = 0.9995f;
		
		/// <summary>How far, in metres, the centre of an assembly part may settle from its seat under the free check and still be in its seat.</summary>
		public const float FREE_CHECK_DISTANCE_TOLERANCE = 0.005f;

		/// <summary>How far, in degrees, an assembly part may turn from its seat under the free check and still be in its seat.</summary>
		public const float FREE_CHECK_ANGLE_TOLERANCE_DEGREES = 3f;

		/// <summary>How far, in metres, the camera may be from a part's check view and still be near it.</summary>
		public const float CHECK_VIEW_DISTANCE = 0.1f;

		/// <summary>How far, in degrees, the camera's viewing direction may turn from a part's check view and still be near it.</summary>
		public const float CHECK_VIEW_ANGLE_DEGREES = 15f;

		/// <summary>How far, in metres, the camera may move over the stillness window and still count as still.</summary>
		public const float STILLNESS_DISTANCE = 0.015f;

		/// <summary>How far, in degrees, the camera's viewing direction may turn over the stillness window and still count as still.</summary>
		public const float STILLNESS_ANGLE_DEGREES = 3f;

		/// <summary>How long, in seconds, the camera must stay within the stillness bounds to count as still.</summary>
		public const float STILLNESS_WINDOW_SECONDS = 0.5f;

		/// <summary>How long, in seconds, the evidence of one presence judgement is gathered at a part's check view.</summary>
		public const float PRESENCE_HOLD_SECONDS = 1f;

		// Edge thresholds (a different operating range than silhouette)
		/// <summary>The score a body that tracks by the edge method must reach on consecutive frames to start tracking, when the body sets no bar of its own.</summary>
		public const float EDGE_QUALITY_TO_START = 0.65f;

		/// <summary>The averaged score below which a body that tracks by the edge method stops tracking, when the body sets no bar of its own.</summary>
		public const float EDGE_LOSE_TRACKING_THRESHOLD = 0.5f;

		/// <summary>The score at or above which a body that tracks by the edge method counts as tracking well.</summary>
		public const float EDGE_NICE_QUALITY_THRESHOLD = 0.65f;

		/// <summary>
		/// The score the last frame of a start must reach for a body that tracks by the edge method and reads the coverage
		/// score, when the body sets none of its own. It is the start score, so a start needs
		/// <see cref="EDGE_QUALITY_TO_START"/> on <see cref="FRAMES_TO_START"/> frames in a row and nothing more. A body that
		/// sets a confirm score of its own is held to that one, and a confirm score at or below the start score withholds
		/// nothing.
		/// </summary>
		public const float EDGE_QUALITY_TO_CONFIRM_START = EDGE_QUALITY_TO_START;

		/// <summary>
		/// Instantaneous edge quality below which a stationary body under pose
		/// fusion gives up its lock and is put back on the pose it is held at. It applies
		/// only when such a body reads the coverage score, because a body under pose
		/// fusion reads the registration margin and gives up its lock at the margin's
		/// stop bar.
		/// </summary>
		/// <remarks>
		/// <para>One bar for every object. It sits below the score of frames where the
		/// solve is on the object and above the score of frames where the solve has
		/// left it, so it separates the two without a per-object value.</para>
		/// <para>Deliberately above <see cref="EDGE_LOSE_TRACKING_THRESHOLD"/>,
		/// which is not a mistake in either constant: that one ends a body's
		/// tracking session and has to be sure, while this one only puts the solve
		/// back where the body is held and costs a few frames of solving if it is
		/// wrong. The cheaper decision gets the more sensitive bar. It is kept
		/// separate from the decision band for the same reason it has its own name:
		/// the band's entry bar is never below its holding bar, so raising the
		/// holding bar to this value would raise the entry bar with it and a body
		/// that starts at 0.78 would never start at all.</para>
		/// </remarks>
		internal const float EDGE_HOLD_RELEASE_THRESHOLD = 0.8f;
		
		// Edge quality constants
		/// <summary>
		/// Edge coverage value that maps to quality 1.0 via InverseLerp(0, this, coverage) for an edge body under pose
		/// fusion that reads the coverage score. An edge body without pose fusion reads 0.70 as one, and a body under pose
		/// fusion reads the registration margin by default.
		/// </summary>
		[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
		public const float EDGE_QUALITY_COVERAGE_MAX = 0.55f;
		/// <summary>Edge median residual (pixels) fallback when no valid edge points.</summary>
		public const float EDGE_RESIDUAL_MAX = 15;
		/// <summary>Projection error (degrees) fallback when no valid edge points.</summary>
		public const float EDGE_PROJECTION_ERROR_BAD = 30f;


		/// <summary>Default smooth time in seconds for stationary pose corrections. 0 = instant.</summary>
		public const float DEFAULT_SMOOTH_TIME = 0.1f;

		/// <summary>
		/// Fraction of the way the settled display pose of a stationary body moves
		/// toward each stored solve. The solve's frame-to-frame scatter is present
		/// at every quality level, so it is averaged rather than gated: at this
		/// fraction the settled pose is an exponential average over about twenty
		/// stored solves, and a lasting move is followed within about a second at
		/// the tracker's rate.
		/// </summary>
		public const float STATIONARY_SETTLE_PULL = 0.05f;

		/// <summary>
		/// On-screen distance, as the median displacement of the model's points in pixels, from the settled pose to a stored solve
		/// up to which the difference is read as the solve's scatter and averaged at <see cref="STATIONARY_SETTLE_PULL"/>.
		/// </summary>
		public const float STATIONARY_SETTLE_STILL_PIXELS = 1.5f;

		/// <summary>
		/// On-screen distance, as the median displacement of the model's points in pixels, from the settled pose to a stored solve
		/// from which the difference is read as the object having moved, and the settled pose is seated on the solve. Between
		/// this and <see cref="STATIONARY_SETTLE_STILL_PIXELS"/> the pull grows in proportion.
		/// </summary>
		public const float STATIONARY_SETTLE_MOVE_PIXELS = 4f;

		/// <summary>Which scalar every quality decision reads.</summary>
		/// <remarks>
		/// <see cref="QualitySignalSource.Coverage"/> is the site-coverage ratio
		/// rescaled so that a share of 0.70 reads one.
		/// <see cref="QualitySignalSource.RegistrationMargin"/> is the native
		/// registration margin, which is the weaker of the pose's two bar readings, one
		/// at the bar and unclipped above it. The score a reader sees under pose fusion is the
		/// margin on the coverage scale (see <see cref="TrackedBody.TrackingQuality"/>).
		/// </remarks>
		internal enum QualitySignalSource
		{
			Coverage = 0,
			RegistrationMargin = 1,
		}

		/// <summary>
		/// The scalar the acquisition, acceptance and holding decisions of a body that tracks by the edge method
		/// read when no pose fusion is in play.
		/// </summary>
		/// <remarks>
		/// Coverage, on a scale that reads one at a coverage share of 0.70, with the start bar of
		/// <see cref="EDGE_QUALITY_TO_START"/> and the stop bar of <see cref="EDGE_LOSE_TRACKING_THRESHOLD"/>. A body
		/// under pose fusion reads the registration margin, and a body that tracks by the silhouette method reads its
		/// own silhouette score with its own bars.
		/// </remarks>
		internal const QualitySignalSource QUALITY_SIGNAL_SOURCE =
			QualitySignalSource.Coverage;

		/// <summary>
		/// The margin a pose must reach to be treated as registered.
		/// </summary>
		/// <remarks>
		/// This is an operating point, not a construction bar of the margin. A
		/// pose that sits off the object almost never reads at or above it.
		/// <para>The start, nice-quality and seat-admission decisions all read this
		/// one constant.</para>
		/// </remarks>
		internal const float REGISTRATION_MARGIN_ACQUIRE_BAR = 0.988128f;

		/// <summary>
		/// The margin a body already being tracked must keep.
		/// </summary>
		/// <remarks>
		/// A body that is still on the object almost always reads above it, so a
		/// single bad reading does not release the body.
		/// <para>It sits <em>below</em>
		/// <see cref="REGISTRATION_MARGIN_ACQUIRE_BAR"/>, which is the point of
		/// having two: a body is asked for more to be picked up than to be kept.
		/// Holding a body to the acquire bar would release it on frames where it
		/// has not left the object, and a released body then has to clear the
		/// higher bar again before it is taken back.</para>
		/// </remarks>
		internal const float REGISTRATION_MARGIN_HOLD_BAR = 0.848745f;

		/// <summary>
		/// How much better than the pose it would replace a proposal must read.
		/// It is larger than the usual frame-to-frame step of the margin on a pose
		/// that is on the object, so a proposal has to beat the live pose
		/// by more than the live pose moves on its own from one frame to the
		/// next.
		/// </summary>
		internal const float REGISTRATION_MARGIN_SEAT_MARGIN = 0.0581f;

		/// <summary>Which reading of the probe's search offsets the margin's residual term takes.</summary>
		/// <remarks>
		/// <see cref="MarginResidualTerm.Percentile90"/> is the native margin as the
		/// plugin publishes it, which is the 90th percentile of the whole-pixel offsets. It
		/// steps a whole pixel at a time, so it reads coarsely on a pose that is
		/// exactly right.
		/// <see cref="MarginResidualTerm.SubpixelMedian"/> reads the median of the
		/// sub-pixel offsets from the same record instead, computed on this side.
		/// The alignment term, the minimum and every bar stay as they are.
		/// <see cref="MarginResidualTerm.SubpixelMedianRefineBeforeAccept"/> reads the
		/// same median, and also lets the tracker refine a body that has not been
		/// accepted yet. Every step starts again from the held seat, so the seat does
		/// not move, and the start decision reads the record of the refined pose
		/// instead of the record of the seat as it was placed.
		/// <see cref="MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference"/> is that
		/// mode with one change on the stationary path under pose fusion. A solve becomes the stored
		/// reference, and the saved-view verifier ranks and seats its candidates, only on the weaker of
		/// the median margin and the published margin, so both residual terms have to clear. Acceptance,
		/// holding and taking the lock back read the median margin, as in the mode it extends.
		/// </remarks>
		internal enum MarginResidualTerm
		{
			Percentile90 = 0,
			SubpixelMedian = 1,
			SubpixelMedianRefineBeforeAccept = 2,
			SubpixelMedianRefineBeforeAcceptGuardedReference = 3,
		}

		/// <summary>The residual term the margin's decisions read by default.</summary>
		/// <remarks>
		/// The median term, with the seat refined before the start decision and the stored
		/// reference guarded on both residual terms. The refinement is for a body that tracks by
		/// the edge method. A body that tracks by the silhouette method is judged at the pose it is
		/// placed at.
		/// </remarks>
		internal const MarginResidualTerm MARGIN_RESIDUAL_TERM =
			MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference;

		/// <summary>
		/// How far, in millimetres, the tracker's solve may stand from the pose a stationary body is held at, with the camera still, before
		/// the solve counts as having left the object when it is not accepted.
		/// </summary>
		internal const float PREDICTION_GATE_TRANSLATION_MM = 30f;

		/// <summary>The same bound for the rotation, in degrees.</summary>
		internal const float PREDICTION_GATE_ROTATION_DEG = 8f;

		/// <summary>
		/// How many times the distance the AR camera moved between two steps is added to each bound. The camera pose of a step may be a step
		/// old, so the object can appear displaced by about as much as the camera moved, and this leaves room for twice that.
		/// </summary>
		internal const float PREDICTION_GATE_CAMERA_STEP_FACTOR = 2f;

		/// <summary>
		/// How many steps in a row a solve of a stationary body has to be verified on the coverage score and inside the prediction gate before it
		/// is accepted when the registration margin does not clear.
		/// </summary>
		internal const int SOFT_ACCEPT_ROWS = 3;

		/// <summary>
		/// How many times wider the prediction gate stands before any solve has been accepted. The pose a body is held at is then only the guide
		/// that was placed, which can stand tens of millimetres from the object, and a gate of the width that protects an accepted pose would put
		/// every solve that found the object back at the guide.
		/// </summary>
		internal const float SOFT_ACCEPT_FIRST_GATE_SCALE = 3f;

		/// <summary>
		/// How many steps the registration margin must have gone without accepting a solve before a verified solve is stored as the pose the body
		/// is held at. A picture on which the margin accepts from time to time is a picture the margin can judge, so the margin keeps the stored
		/// pose there, and the verified solve only keeps the lock and the readout.
		/// </summary>
		internal const int SOFT_ACCEPT_QUIET_ROWS = 30;

		/// <summary>How many steps in a row the solve has to stand still before the first fit of a stationary body is accepted.</summary>
		internal const int FIRST_ACCEPT_SETTLE_ROWS = 3;

		/// <summary>The most, in millimetres, that two solves of the newest <see cref="FIRST_ACCEPT_SETTLE_ROWS"/> steps may stand apart for the fit to count as settled.</summary>
		internal const float FIRST_ACCEPT_SPREAD_MM = 8f;

		/// <summary>The same limit for the rotation, in degrees.</summary>
		internal const float FIRST_ACCEPT_SPREAD_DEG = 2f;

		/// <summary>
		/// How many steps after the tracking started the first accept may be held back for a fit that is still moving. After that the guard
		/// lets go, because a fit that never stands still would otherwise never be accepted.
		/// </summary>
		internal const int FIRST_ACCEPT_MAX_WAIT_ROWS = STABILIZATION_FRAMES;

		/// <summary>
		/// How many steps in a row the view of a stationary body has to stand at or above the floor before a dip is over and fits are stored again. A single
		/// step above the floor in the middle of a dip is not a view that has recovered, and the fit of it is as free to slide as the fits around it.
		/// </summary>
		internal const int DIP_HOLD_RECOVERY_ROWS = FRAMES_TO_RECOVER;

		/// <summary>
		/// How many steps in a row the view of a stationary body has to stand at or above the floor before a dip may begin. The pose that a dip holds is the one a
		/// healthy view has just stored, so a view that never stood healthy, such as one that an object is still coming into, holds nothing and the body goes on
		/// storing what it solves. It is the length of the stabilization.
		/// </summary>
		internal const int DIP_HOLD_ENTRY_ROWS = STABILIZATION_FRAMES;

		/// <summary>
		/// How far, in pixels between two steps, the AR camera may move the picture of a stationary body that is held fixed in the world before the fit of the
		/// step is not stored. It is the largest displacement of the centre and of the eight corners of the bounds of the model, with the corners drawn in to
		/// <see cref="FAST_MOTION_HOLD_MAX_HALF_EXTENT_METRES"/> of the centre, in pixels of a camera whose focal length is
		/// <see cref="FAST_MOTION_HOLD_REFERENCE_FOCAL_PIXELS"/>. A step this fast is blurred, so its fit is the least reliable one of the sequence, and the pose the
		/// body is held at is carried by the AR camera in the meantime.
		/// </summary>
		internal const float FAST_MOTION_HOLD_PIXELS = 6f;

		/// <summary>
		/// The most, in metres, that a corner of the bounds of the model is read from its centre along an axis when the motion of the picture is measured. The
		/// corners of the bounds of a body that is metres across stand close to the camera and move far further on screen than the body does, so they would hold
		/// the fits of a large body for any motion of the camera at all.
		/// </summary>
		internal const float FAST_MOTION_HOLD_MAX_HALF_EXTENT_METRES = 0.15f;

		/// <summary>The focal length, in pixels, of the camera that the limit of the fast motion hold is stated for. A camera with another focal length scales the motion to it.</summary>
		internal const float FAST_MOTION_HOLD_REFERENCE_FOCAL_PIXELS = 450f;

		/// <summary>The median angle, in degrees, at which the alignment term reads zero.</summary>
		/// <remarks>The plugin's own constant, repeated so this side can compute the term.</remarks>
		internal const float REGISTRATION_MARGIN_ALIGNMENT_LIMIT_DEG = 90f;

		/// <summary>The median angle, in degrees, at which the alignment term reads one.</summary>
		/// <remarks>The plugin's own constant, repeated so this side can compute the term.</remarks>
		internal const float REGISTRATION_MARGIN_ALIGNMENT_BAR_DEG = 24.854287f;

		/// <summary>
		/// The sub-pixel median, in pixels, at which the median residual term reads
		/// exactly <see cref="REGISTRATION_MARGIN_ACQUIRE_BAR"/>.
		/// </summary>
		/// <remarks>
		/// On a pose that is exactly right the median often sits on the whole-pixel
		/// value 1.0, so the bar is set just off that value rather than on it. A
		/// reading on that value then does not decide by float noise which side of
		/// the bar it falls on.
		/// </remarks>
		internal const float REGISTRATION_MARGIN_MEDIAN_AT_ACQUIRE_PX = 1.02f;

		/// <summary>
		/// The sub-pixel median, in pixels, at which the median residual term reads
		/// exactly <see cref="REGISTRATION_MARGIN_HOLD_BAR"/>.
		/// </summary>
		/// <remarks>It sits a little above the median of the acquire bar, so that a body is asked for more to be picked up than to be kept.</remarks>
		internal const float REGISTRATION_MARGIN_MEDIAN_AT_HOLD_PX = 1.07f;

		/// <summary>
		/// How far the median moves while the median residual term falls by one,
		/// which is its limit less its bar.
		/// </summary>
		/// <remarks>
		/// Derived from the two medians and the two bars and from nothing else, so the
		/// bars keep their meaning on this term: the acquire bar clears exactly at or
		/// under <see cref="REGISTRATION_MARGIN_MEDIAN_AT_ACQUIRE_PX"/> and the hold bar
		/// exactly at or under <see cref="REGISTRATION_MARGIN_MEDIAN_AT_HOLD_PX"/>.
		/// </remarks>
		internal const float REGISTRATION_MARGIN_MEDIAN_SPAN_PX =
			(REGISTRATION_MARGIN_MEDIAN_AT_HOLD_PX - REGISTRATION_MARGIN_MEDIAN_AT_ACQUIRE_PX) /
			(REGISTRATION_MARGIN_ACQUIRE_BAR - REGISTRATION_MARGIN_HOLD_BAR);

		/// <summary>The sub-pixel median, in pixels, at which the median residual term reads zero.</summary>
		internal const float REGISTRATION_MARGIN_MEDIAN_LIMIT_PX =
			REGISTRATION_MARGIN_MEDIAN_AT_ACQUIRE_PX +
			REGISTRATION_MARGIN_ACQUIRE_BAR * REGISTRATION_MARGIN_MEDIAN_SPAN_PX;

		/// <summary>The sub-pixel median, in pixels, at which the median residual term reads one.</summary>
		internal const float REGISTRATION_MARGIN_MEDIAN_BAR_PX =
			REGISTRATION_MARGIN_MEDIAN_LIMIT_PX - REGISTRATION_MARGIN_MEDIAN_SPAN_PX;

		/// <summary>
		/// How many views of a body are kept as places a recovery can start from.
		/// Zero keeps none and makes no recovery attempt.
		/// </summary>
		internal const int RECOVERY_SEED_BANK_CAPACITY = 8;

		/// <summary>
		/// How far apart two kept views must be. A view nearer than BOTH of these to
		/// one already kept is the same view again: a camera that swung around the
		/// body without approaching it has given a new one, and so has a camera that
		/// walked toward the body along the same line.
		/// </summary>
		internal const float RECOVERY_SEED_ADMISSION_ROTATION_DEG = 15f;
		internal const float RECOVERY_SEED_ADMISSION_TRANSLATION_M = 0.08f;

		/// <summary>
		/// The largest probe residual a view may carry and still be kept. Read from
		/// the probe rather than the score: a solve that has slid onto the wrong part
		/// of the model can hold a score above the tracking bar while its residual
		/// reads several times its usual value, and a view kept from such a frame
		/// would hand a recovery a wrong place to start.
		/// </summary>
		internal const float RECOVERY_SEED_ADMISSION_MAX_PROBE_RESIDUAL = 0.5f;

		/// <summary>The edge coverage a view must reach before the bank will keep it.</summary>
		/// <remarks>
		/// The bank compares against the share of the rendered site set a frame found,
		/// so this floor has to be on that scale. It is the coverage-class acceptance
		/// threshold carried back through the same map the coverage-class quality is
		/// built with, which is why the two factors appear here in that order. Reading
		/// it from a body's decision band instead would be correct only while that band
		/// is itself derived from coverage, because a band carrying a different scalar
		/// makes the second factor meaningless.
		/// </remarks>
		internal const float RECOVERY_SEED_ADMISSION_MIN_EDGE_COVERAGE =
			EDGE_NICE_QUALITY_THRESHOLD * EDGE_QUALITY_COVERAGE_MAX;

		/// <summary>
		/// How long a body must have been out of tracking before it is seated on a
		/// kept view, and how long between one seating and the next.
		/// </summary>
		/// <remarks>
		/// Re-acquisition usually succeeds on its own within a few frames, so a body
		/// that has spent this long failing at it is not about to succeed. The
		/// interval is the same length because a seat needs the same few frames to
		/// prove itself before another one replaces it.
		/// </remarks>
		internal const int RECOVERY_ATTEMPT_DELAY_FRAMES = 30;
		internal const int RECOVERY_ATTEMPT_INTERVAL_FRAMES = 30;

		/// <summary>
		/// How many poses the plugin's site cache keeps for each body while the option is on. A sweep visits the last
		/// accepted pose, the start pose and the whole ring of <see cref="SEEN_POSE_RING_CAPACITY"/>, which is up to 34
		/// poses, and the pose the body holds is evaluated beside each, and a saved-view attempt adds up to 8 more. A
		/// least-recently-used cache holding fewer poses than a cyclic
		/// sweep visits hits none of them, so the capacity is the 32 of the ring, the last, the start, the held pose,
		/// the 8 proposals and 4 spare, which is 48.
		/// </summary>
		internal const int EVIDENCE_SITE_CACHE_CAPACITY = 48;

		/// <summary>
		/// Whether the option that reuses the work of a pose evaluation also leaves out the search the start of an
		/// evaluation makes at the first rung. It changes the state the later search starts from, so it is on only if the
		/// verify mode proved the outputs identical.
		/// </summary>
		internal const bool EVIDENCE_CACHE_SKIP_FIRST_SEARCH = false;

		/// <summary>
		/// Whether a body that lost the object without pose fusion also tries the
		/// camera-relative poses it was accepted at before. On by default.
		/// </summary>
		/// <remarks>
		/// Without pose fusion the camera is the only frame, so a pose the body held
		/// is right again only when the object is back where it was relative to the
		/// camera: behind a hand that has moved away, or on a return to a view already
		/// tracked. Such a pose is only ever proposed. It is scored at the current
		/// frame, has to pass the same acquire bar and seat margin as a saved view,
		/// and becomes tracking only through the ordinary start.
		/// </remarks>
		internal const bool SEEN_POSE_RECOVERY = true;

		/// <summary>How many accepted poses a body keeps as places a recovery can start from.</summary>
		internal const int SEEN_POSE_RING_CAPACITY = 32;

		/// <summary>
		/// How far, in metres, an accepted pose must be from every kept pose to be kept too.
		/// </summary>
		/// <remarks>
		/// A pose is kept when it is farther than this or turned more than
		/// <see cref="SEEN_POSE_ADMISSION_ROTATION_DEG"/> from every pose already kept, so
		/// a still object fills one entry and a moving one leaves a trail.
		/// </remarks>
		internal const float SEEN_POSE_ADMISSION_TRANSLATION_M = 0.025f;

		/// <summary>How far, in degrees, an accepted pose must be turned from every kept pose to be kept too.</summary>
		internal const float SEEN_POSE_ADMISSION_ROTATION_DEG = 5f;

		/// <summary>
		/// How long a body must have been out of tracking before its kept poses are tried.
		/// </summary>
		/// <remarks>
		/// Shorter than the saved-view delay because a kept pose costs two pose
		/// evaluations and no retrieval, and the case it serves, an object uncovered
		/// where it was, is one where waiting only loses frames.
		/// </remarks>
		internal const int SEEN_POSE_RECOVERY_DELAY_FRAMES = 10;

		/// <summary>
		/// The first entry id a kept pose is proposed under. Saved-view ids stay below it,
		/// so an id at or above it is never handed back to the plugin's bank.
		/// </summary>
		internal const int SEEN_POSE_ENTRY_ID_BASE = 1 << 20;

		/// <summary>
		/// Interval in seconds between AR pose corrections fed back to native
		/// when tracking quality drops below the nice threshold. Prevents the
		/// optimizer from drifting too far while the display holds last-good pose.
		/// The default is 0.15 seconds, which is about 9 frames at 60 frames per second.
		/// </summary>
		public const float QUALITY_GATE_CORRECTION_INTERVAL = 0.15f;

		/// <summary>
		/// Grace period in seconds after quality drops below nice before periodic
		/// corrections start being fed back to native. Lets the optimizer self-recover
		/// from brief dips (autofocus, occlusion flicker) without being overridden.
		/// </summary>
		public const float QUALITY_GATE_RECOVERY_DELAY = 0.5f;

	}

}
