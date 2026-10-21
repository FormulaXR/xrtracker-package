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
	public class TrackedBody : MonoBehaviour, ISerializationCallbackReceiver
	{
		#region Serialized Fields

		[Tooltip("Unique identifier for this tracked object.")] [SerializeField]
		private string _bodyId;

		[Tooltip("MeshFilters containing the 3D model(s) to track. Multiple meshes will be combined using their local transforms.")] [SerializeField]
		private List<MeshFilter> _meshFilters = new List<MeshFilter>();

		// Tracking Model section - drawn by custom editor
		[FormerlySerializedAs("_silhouetteModelAsset")] [SerializeField]
		private TrackingModelAsset _trackingModelAsset;

		[SerializeField] private ModelSettings _modelSettings = new ModelSettings();

		// Parent body - auto-detected from Unity hierarchy (not serialized)
		private TrackedBody _parentBody;

		[Tooltip("Which degrees of freedom the tracker may optimize when this body is attached to a parent. None means a rigid attachment.")] [SerializeField]
		private TrackedMotion _trackedMotion = TrackedMotion.None;

		[Tooltip("When enabled, this child body occludes its parent during tracking. " +
		         "Use it when assembling parts that cover portions of the parent. " +
		         "It requires the parent to have Occlusion enabled.")]
		[SerializeField]
		private bool _occludeParent;

		[Tooltip("How the tracker determines the initial pose of the body.")] [SerializeField]
		private InitialPoseSource _initialPoseSource = InitialPoseSource.ScenePosition;

		[Tooltip("The viewpoint transform that the initial pose is calculated from. The tracker computes the pose of the object relative to this viewpoint.")] [SerializeField]
		private Transform _initialViewpoint;

		[Tooltip("The object is fixed in the world. This enables AR pose fusion, which keeps the position of the object when tracking quality drops.")]
		[SerializeField]
		private bool _isStationary;

		[Tooltip("With AR, the body also accepts a fit that keeps the start score for 3 frames in a row and stays close to where AR predicts the object. This helps soft or low-contrast real images keep the lock and show Tracked. With this off, the body accepts only the fits that the strict confidence check passes.")]
		[SerializeField]
		private bool _acceptSteadyFits = true;

		[Tooltip("With AR, the shown pose stays still while the view health is below 0.8 of the usual level for this view, until the view has been healthy again for 5 steps in a row. A dip can only begin after 30 healthy steps in a row. With this off, new fits keep moving the pose while part of the object is hidden. It needs Accept Steady Fits.")]
		[SerializeField]
		private bool _holdDuringViewDip = true;

		[Tooltip("With AR, no new fit is stored while AR moves the object more than 6 pixels per frame on screen, counted for a camera with a focal length of 450 pixels. The shown pose is carried by AR until the camera slows down. The first fit and the first 30 steps after the start are never held back. With this off, fits taken during fast motion keep moving the pose. It needs Accept Steady Fits.")]
		[SerializeField]
		private bool _holdDuringFastMotion = true;

		[Tooltip("With AR, before the first fit is shown, 3 solves in a row must agree within 8 millimeters and 2 degrees. The guard lets go after 30 steps, so a fit that never settles is still taken in the end. With this off, the first fit that passes the other checks is taken at once. It needs Accept Steady Fits.")]
		[SerializeField]
		private bool _firstAcceptGuard = true;

		[Tooltip("The time in seconds over which pose corrections are smoothed. A value of 0 applies them instantly.")]
		[SerializeField]
		private float _smoothTime = TrackerDefaults.DEFAULT_SMOOTH_TIME;

		[Tooltip("How strongly each new pose is pulled toward the previous rotation. A higher value gives smoother rotation and a slower response to rotation changes. The value runs from 100 to 50000.")] [Range(100, 50000)] [SerializeField]
		private float _rotationStability = 5000;

		[Tooltip("How strongly each new pose is pulled toward the previous position. A higher value gives smoother position and a slower response to position changes. The value runs from 1000 to 100000.")] [Range(1000, 100000)] [SerializeField]
		private float _positionStability = 30000f;

		// This field must not carry the former name _edgeTracking. That name belongs to the edge settings below, and Unity moves the property
		// path of an override of a prefab instance through the former names of the fields, so an alias here would send every override of an
		// edge setting to these settings, which hold no such field, and the scene would drop the override on load.
		[Tooltip("Silhouette tracking parameters.")] [SerializeField]
		private SilhouetteTrackingSettings _silhouetteTracking = new();

		[Tooltip("Multi-scale pyramid settings of silhouette tracking. Adjust them for thin objects at a distance or when the object appears small in the image.")] [SerializeField]
		private MultiScaleSettings _multiScale = new();

		[Tooltip("Advanced depth tracking tuning. Adjust it when the body uses depth tracking.")] [SerializeField]
		private DepthTrackingSettings _depthTracking = new();

		[Tooltip("Uses a custom quality threshold to start tracking. Only a body that tracks by the edge method reads it, " +
		         "because the silhouette method has its own threshold.")] [SerializeField]
		private bool _useCustomStartThreshold;

		[Tooltip("The custom quality threshold (0-1) required to start tracking, for a body that tracks by the edge method. The default is 0.65.")] [Range(0f, 1f)] [SerializeField]
		private float _customQualityToStart = TrackerDefaults.EDGE_QUALITY_TO_START;

		[Tooltip("Uses a custom confirm score. Only a body that tracks by the edge method reads it.")] [SerializeField]
		private bool _useCustomConfirmThreshold;

		[Tooltip("The score that the last frame of a start must reach. A start needs the start score on 5 frames in a row and this score on the last one. " +
		         "A value above the start score makes a start surer. When no custom score is set, the last frame needs only the start score.")] [Range(0f, 1f)] [SerializeField]
		private float _customQualityToConfirmStart = TrackerDefaults.EDGE_QUALITY_TO_CONFIRM_START;

		[Tooltip("Before the first start, the tracker searches a window around the guide for the shape of the body, within a cone around the guide's view (5 degrees by default, a setting below), and refines from the best hypothesis and not from the guide. " +
		         "The search only takes over when the guide stands out of reach of the winner. A guide in reach starts as it does without the search. " +
		         "The start waits until the same winner was found on three searches in a row, and the start bars still verify the pose. " +
		         "Under pose fusion on a live camera the guide of a body that starts from its viewpoint follows the camera until the start. " +
		         "Only a body that tracks by the edge method, has no parent and is refined before its start reads this setting. It is on by default, and a scene or prefab that saved this field keeps its saved value.")] [SerializeField]
		private bool _searchBeforeStart = true;

		[Tooltip("How many tracking steps pass between two searches of the whole image. A value of 1 searches on every step.")] [Range(1, 60)] [SerializeField]
		private int _startSearchPeriodSteps = StartSearch.DEFAULT_PERIOD_STEPS;

		[Tooltip("The margin the winner of a search must reach before a start is let through, as the score of the winner divided by the score of the best hypothesis that stands far from it. A value of 0 sets no margin bar.")] [Range(0f, 3f)] [SerializeField]
		private float _startSearchMarginBar = StartSearch.DEFAULT_MARGIN_BAR;

		[Tooltip("The score the winner of a search must reach before a start is let through, as the share of the outline points that find an edge of the same direction. A value of 0 sets no score floor.")] [Range(0f, 1f)] [SerializeField]
		private float _startSearchScoreFloor = StartSearch.DEFAULT_SCORE_FLOOR;

		[Tooltip("How far the guide may stand from the winner of a search, as the median on-screen displacement of the model's points in pixels, and still count as in reach. A guide in reach is left alone and the body starts as it does without the search. A value of 0 treats every guide as out of reach.")] [Range(0f, 200f)] [SerializeField]
		private float _startSearchReachPixels = StartSearch.DEFAULT_REACH_PIXELS;

		[Tooltip("The half angle, in degrees, of the cone of views the plugin draws around the guide's view for the search. The bank holds the guide's own view and rings of directions out to this angle, so 5 degrees at a spacing of 5 degrees is 7 views. The cost of the bank and of every search grows with the number of views.")] [Range(0f, 45f)] [SerializeField]
		private float _startSearchConeHalfAngle = StartSearch.DEFAULT_CONE_HALF_ANGLE;

		[Tooltip("The spacing, in degrees, between neighbouring views of the cone. The turns in the image plane are made at this step too.")] [Range(2.5f, 20f)] [SerializeField]
		private float _startSearchConeSpacing = StartSearch.DEFAULT_CONE_SPACING;

		[Tooltip("The range, in degrees, of the turns in the image plane that the bank holds. The turns are zero and plus and minus each step of the spacing out to this value, so the zero turn is always held.")] [Range(0f, 45f)] [SerializeField]
		private float _startSearchRollRange = StartSearch.DEFAULT_ROLL_RANGE;

		[Tooltip("The half angle, in degrees, of the fine stage, which scores the views within this angle of the winner at a step of 2.5 degrees. A value of 0 switches the fine stage off.")] [Range(0f, 15f)] [SerializeField]
		private float _startSearchFineHalfAngle = StartSearch.DEFAULT_FINE_HALF_ANGLE;

		[Tooltip("How far beyond the box of the guide's outline the search looks, in pixels of the search image (its longer side is at most 640 pixels). A template is placed only where its own outline box lies inside this window, so an object within about this far of the guide is found. A value of 0 searches the whole image.")] [Range(0f, 640f)] [SerializeField]
		private float _startSearchWindowMargin = StartSearch.DEFAULT_WINDOW_MARGIN;

		[Tooltip("How many distances the bank holds around the guide's own. One is the guide's own distance, three are 0.92, 1.0 and 1.09 times it, and five add 0.85 and 1.2.")] [Range(1, 5)] [SerializeField]
		private int _startSearchDistanceCount = StartSearch.DEFAULT_DISTANCE_COUNT;

		[Tooltip("Whether the plugin makes the other distances of the bank by scaling one drawing of each view. Off, which is the default, it draws every distance of every view. On, it draws each view once at the guide's own distance and scales that drawing for the other distances, which takes a third of the drawings at three distances but can start a body a little less well.")] [SerializeField]
		private bool _startSearchScaleDistances = StartSearch.DEFAULT_SCALE_DISTANCES;

		[Tooltip("Uses a custom quality threshold to stop tracking. Only a body that tracks by the edge method reads it.")] [SerializeField]
		private bool _useCustomStopThreshold;

		[Tooltip("The custom quality threshold (0-1) below which tracking is lost, for a body that tracks by the edge method. The default is 0.5.")] [Range(0f, 1f)] [SerializeField]
		private float _customQualityToStop = TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD;

		[Tooltip("Uses a custom quality threshold to start tracking. Only a body that tracks by the silhouette method reads it.")] [SerializeField]
		private bool _useCustomSilhouetteStartThreshold;

		[Tooltip("The custom quality threshold (0-1) required to start tracking, for a body that tracks by the silhouette method. The default is 0.3.")] [Range(0f, 1f)] [SerializeField]
		private float _customSilhouetteQualityToStart = TrackerDefaults.QUALITY_TO_START;

		[Tooltip("Uses a custom quality threshold to stop tracking. Only a body that tracks by the silhouette method reads it.")] [SerializeField]
		private bool _useCustomSilhouetteStopThreshold;

		[Tooltip("The custom quality threshold (0-1) below which tracking is lost, for a body that tracks by the silhouette method. The default is 0.4.")] [Range(0f, 1f)] [SerializeField]
		private float _customSilhouetteQualityToStop = TrackerDefaults.LOSE_TRACKING_THRESHOLD;

		// The shared custom start and stop bars as this body last saw them, which is 0 for a bar that is off and 1 plus the bar in
		// ten-thousandths for a bar that is on. Data saved before these fields existed reads 0 for both, so a custom bar in that data
		// is a bar this body has not seen yet. See CarrySharedBarsToSilhouette.
		[HideInInspector] [SerializeField]
		private int _sharedStartBarSeen;

		[HideInInspector] [SerializeField]
		private int _sharedStopBarSeen;

		[Tooltip("Enable it for parts that are physically installed during operation. The child does not affect the parent pose until the quality confirms that the part is present.")]
		[SerializeField]
		private bool _assemblyMode;

		[Tooltip("The quality threshold (0-1) that confirms the part is installed. From then on the part affects the parent pose.")] [Range(0f, 1f)] [SerializeField]
		private float _assemblyQualityToConfirm = 0.4f;

		[Tooltip("This applies only with Assembly Mode. The part is scored but never joins the parent's solve until the application calls Attach, " +
		         "and it stops joining it when the application calls Detach. With this on, Quality to Confirm is not used.")]
		[SerializeField]
		private bool _attachManually;

		[Tooltip("This applies only with Assembly Mode and Attach Manually, for a part with no tracked motion. While the part is not attached, " +
		         "it settles its own pose a few millimetres and degrees around its seat from its own evidence, without ever moving the parent, " +
		         "and it is scored where it settled. A part that had to move further than the tolerances is not in its seat. " +
		         "Once attached, the part is rigid at its seat again.")]
		[SerializeField]
		private bool _freeCheck;

		[Tooltip("How far, in metres, the centre of the part may settle from its seat and still be in its seat.")]
		[Min(0.0001f)] [SerializeField]
		private float _freeCheckDistanceTolerance = TrackerDefaults.FREE_CHECK_DISTANCE_TOLERANCE;

		[Tooltip("How far, in degrees, the part may turn from its seat and still be in its seat.")]
		[Range(0.1f, 30f)] [SerializeField]
		private float _freeCheckAngleTolerance = TrackerDefaults.FREE_CHECK_ANGLE_TOLERANCE_DEGREES;

		[Tooltip("This is optional. It is the camera pose that the part is checked from, a transform like the start viewpoint that looks along its forward axis. " +
		         "With a check view, the part's presence is judged only while the camera is near this view and still, from the evidence of a whole hold.")]
		[SerializeField]
		private Transform _checkView;

		[Tooltip("How far, in metres, the camera may be from the check view and still be near it.")]
		[Min(0.001f)] [SerializeField]
		private float _checkViewDistance = TrackerDefaults.CHECK_VIEW_DISTANCE;

		[Tooltip("How far, in degrees, the camera's viewing direction may turn from the check view's and still be near it.")]
		[Range(1f, 90f)] [SerializeField]
		private float _checkViewAngle = TrackerDefaults.CHECK_VIEW_ANGLE_DEGREES;

		[Tooltip("How far, in metres, the camera may move over the stillness window and still count as still.")]
		[Min(0.0001f)] [SerializeField]
		private float _stillnessDistance = TrackerDefaults.STILLNESS_DISTANCE;

		[Tooltip("How far, in degrees, the camera's viewing direction may turn over the stillness window and still count as still.")]
		[Range(0.1f, 45f)] [SerializeField]
		private float _stillnessAngle = TrackerDefaults.STILLNESS_ANGLE_DEGREES;

		[Tooltip("How long, in seconds, the camera must stay within the stillness bounds to count as still.")]
		[Min(0.05f)] [SerializeField]
		private float _stillnessWindow = TrackerDefaults.STILLNESS_WINDOW_SECONDS;

		[Tooltip("How long, in seconds, the evidence of one judgement is gathered near the check view with the camera still.")]
		[Min(0.1f)] [SerializeField]
		private float _presenceHold = TrackerDefaults.PRESENCE_HOLD_SECONDS;

		[Tooltip("Validates the tracking points against the silhouette boundaries. It uses the shared silhouette renderer and adds significant GPU load on mobile devices.")]
		[FormerlySerializedAs("_enableContourValidation")]
		[SerializeField]
		private bool _enableSilhouetteValidation;

		[Tooltip("Adds tracking by the surface appearance of the object, as a complement to the Silhouette or the Edge method. " +
		         "This does not use the Unity texture or material of the object. " +
		         "Instead, the tracker detects distinctive points on the visible surface of the object in the camera image and matches them against the rendered model. " +
		         "It improves tracking for objects with distinctive surface patterns such as text, logos or scratches, and it adds little for uniform or solid-colored objects. " +
		         "It works only while the camera is farther from the object than about 1.5 times the radius of the object. " +
		         "It renders the model on the GPU.")]
		[FormerlySerializedAs("_enableTextureModality")]
		[SerializeField]
		private bool _enableTextureTracking;

		[Tooltip("Handles occlusion for this body's tracking, based on depth. " +
		         "When enabled, silhouette points that are hidden behind other objects (occluders) are ignored. " +
		         "Child bodies with Occlude Parent enabled occlude this body automatically.")]
		[SerializeField]
		private bool _enableOcclusion;

		[Tooltip("The occlusion detection parameters. They control how the tracker determines when " +
		         "silhouette points are occluded by other objects. Adjust the threshold for tight assemblies.")]
		[SerializeField]
		private OcclusionSettings _occlusionSettings = new();

		[Tooltip("Silhouette works best when a good part of the outline is visible with good contrast, and it handles faster motion. It needs a tracking model.\n" +
		         "Edge works in low-contrast scenes and when mostly internal edges are visible, such as close to large machinery. It also works with external edges, but it is less resilient to fast motion. It needs no tracking model.")]
		[SerializeField]
		private TrackingMethod _trackingMethod = TrackingMethod.Edge;

		[Tooltip("Uses depth measurements to help the fit. The depth comes from LiDAR on iOS devices that have it, " +
		         "from the depth image of Android devices that provide one (through an AROcclusionManager on the AR camera), " +
		         "or from a RealSense camera on Windows (with the image source set to RealSense). " +
		         "It needs a depth model in the Tracking Model Asset.")]
		[FormerlySerializedAs("_enableDepthModality")]
		[FormerlySerializedAs("_trackingMode")]
		[SerializeField]
		private bool _enableDepthTracking;

		// Derived from _trackingMethod — kept for C++ ABI compatibility
		private bool _enableSilhouetteTracking => _trackingMethod == TrackingMethod.Silhouette;
		private bool _enableEdgeTracking => _trackingMethod == TrackingMethod.Edge;

		[Tooltip("Advanced tuning parameters of edge tracking.")] [SerializeField, FormerlySerializedAs("_edgeModality")]
		private EdgeTrackingSettings _edgeTracking = new();

		// The relative tracking health settings. The tracker measures the sites that agree against what this view normally achieves,
		// and it reads the lifecycle thresholds from that measure. The values come from the code and are not stored in a scene or
		// a prefab, because nothing in the inspector sets them.
		[System.NonSerialized]
		private TrackingHealthSettings _trackingHealth = new();

		[SerializeField] private UnityEvent _onStartTracking = new();
		[SerializeField] private UnityEvent _onStopTracking = new();

		#endregion

		#region Private Fields

		private bool _isRegistered;
		private bool _isAttached;
		private bool _isTracking;
		// The tracker has been asked to refine this body before the lifecycle accepts it.
		private bool _refiningBeforeAcceptance;

		// Set while the refinement was begun for a recovery seat and not for the start path. It is what lets the
		// tracker be stopped again when the seat is withdrawn or accepted, so the search goes on unrefined.
		private bool _recoveryRefining;
		// The body has started tracking at least once since it registered.
		private bool _startedSinceRegistration;
		// Where the limited search walk of this body stands, and the sample of the model it is measured on.
		// All of it is inert unless the tooling asks for a walk.
		private SearchWalkState _searchWalk;
		// The scope the walk last ran in, and what the latest judged step of the walk read, for the diagnostic record.
		private int _searchWalkScope;
		private bool _walkJudgedSinceTaken;
		private SearchWalkVerdict _judgedWalkVerdict;
		private int _judgedWalkSteps;
		private float _judgedWalkPixels;
		private int _judgedWalkRestarts;
		private int _judgedWalkScope;
		private Vector3[] _searchWalkPoints;
		private float[] _searchWalkScratch;
		private Vector3[] _boundsMotionPoints;
		private const int SEARCH_WALK_MAX_POINTS = 256;
		// The settled start. Whether the seat has been published and the refinement can be carried from the step before, and the
		// refined pose of that step. All of it stays at rest unless the setting is on.
		private bool _settleCarryReady;
		private StartSettle.State _settleState;
		// The start search. What the plugin was told, how many searches in a row agreed, what the latest step read, whether that reading
		// is the one of the step that has just run, and whether the plugin was found without the export. The generation grows every time
		// the guide is replaced, which is what makes the plugin build its views again. All of it stays at rest unless the setting is on.
		private StartSearch.Request _startSearchRequest;
		private StartSearch.State _startSearchState;
		private StartSearchReading _startSearchLastReading;
		private bool _startSearchFresh;
		private bool _startSearchMissing;
		private int _startSearchGeneration;
		private float[] _startSearchRaw;
		// The settled start that takes its seed from the search. The plugin is told not to write the seat of the refinement, the winner
		// of the search is kept here as the pose the refinement is seeded with and the pose the settled start measures its drift from,
		// and a start from the search is remembered so the display can follow the tracker once the guide is far from the object.
		private bool _startSearchSeatOff;
		private bool _startSearchSeatMissing;
		private FTTrackingPose _startSearchSeed;
		private bool _startSearchSeedValid;
		private bool _startSearchSeedPublished;
		private bool _startedFromSearch;
		private bool _startSearchAnchorKnown;
		private StartSearchPath _startSearchPath;
		private float _startSearchGuidePixels = float.NaN;
		private int _startSearchReachSerial;
		private bool _startSearchReachHasSerial;
		private int _startSearchFarRun;
		private bool _startSearchBankSent;
		private bool _startSearchBankMissing;
		private StartSearch.BankSetting _startSearchBankSentValue;
		private bool _startSearchRenderSent;
		private bool _startSearchRenderMissing;
		private bool _startSearchRenderSentValue;
		private int _startSearchNearRun;
		private Matrix4x4 _startSearchAnchorCamera;
		private Matrix4x4 _startSearchSentGuide = Matrix4x4.identity;
		private TrackingConfidence _confidence = TrackingConfidence.Lost;
		private TrackingConfidenceReason _confidenceReason = TrackingConfidenceReason.NotStarted;
		private RegistrationQualitySnapshot _registrationQuality =
			RegistrationQualitySnapshot.Unsupported;
		private TemporalTrackingSnapshot _temporalTrackingStatus;
		private TrackingHealthSnapshot _trackingHealthStatus;
		private bool _automaticStartEnabled = true;
		private FTTrackingPose _initialPose;
		private bool _detectorPoseWritePending;

		// The edge settings the tracker was given when the body registered. Only the five detection settings can be changed afterwards
		// (see FTBridge.TrySetEdgeParameters), and the signature of the others tells whether any of them differs.
		private string _registeredOtherEdgeSettings;
		private bool _hasRegisteredEdgeSettings;
		private EdgeDetectionSettings _registeredDetection;
		private bool _detectionApplyPending;
		private bool _liveEdgeSettingsUnavailable;
		private bool _edgeTrackingChangeReported;

		// The depth correspondence settings the tracker holds: what registration gave it and anything sent since. Only these two can be
		// changed afterwards (see FTBridge.TrySetDepthParameters), and a plugin that lacks the call keeps what registration gave it.
		private DepthCorrespondenceSettings _registeredDepth;
		private bool _hasRegisteredDepth;
		private bool _depthApplyPending;
		private bool _liveDepthSettingsUnavailable;

		// The initial pose as it was authored, relative to the camera. A body that tracks by the silhouette method is
		// drawn at it while it searches, in front of the camera as the camera stands.
		private Vector3 _initialCameraRelativePosition;
		private Quaternion _initialCameraRelativeRotation = Quaternion.identity;

		// The pose the body is initialized at and starts tracking from. It is held in the
		// active tracking space and left alone, so the startup decision is taken at the pose
		// the body was placed at rather than at wherever the camera has since moved to.
		private readonly RetainedInitializationPose _retainedInitializationPose = new();
		private Vector3 _lastAppliedDetectorPosition;
		private Quaternion _lastAppliedDetectorRotation = Quaternion.identity;
		private bool _hasAppliedDetectorPose;

		// While a recovery seat made on the retained pose is on its window and the tooling asks for it, the body is drawn at
		// the pose the seat replaced and not at the seat. The retained pose is the seat, so the tracker is published the seat as
		// always, and only the drawn pose is held. Every latch of the retained pose, every start and every reset ends the hold.
		private bool _seatDisplayHeld;
		private Vector3 _seatDisplayPosition;
		private Quaternion _seatDisplayRotation = Quaternion.identity;

		// The pose the body was placed at before it first started, in the form the retained initialization pose is
		// read in. Taken once, when the body first starts, and set again by SetInitialPose. A return to it is what
		// "back to the guide" means, because the retained pose itself follows the last drawn pose after a loss.
		private Vector3 _guidePosition;
		private Quaternion _guideRotation = Quaternion.identity;
		private bool _hasGuide;

		// A write from outside the tracker is an instruction about where the body is, so it has
		// to be told apart from the pose this component wrote itself. These bound "unchanged".
		private const float EXTERNAL_POSE_EDIT_POSITION_EPSILON = 1e-5f;
		private const float EXTERNAL_POSE_EDIT_ROTATION_EPSILON_DEGREES = 1e-3f;

		private bool _silhouetteValidationEnabled;
		private bool _textureTrackingEnabled;
		private bool _occlusionEnabled;
		private bool _depthTrackingEnabled;
		private bool _contributesToOptimization = true;
		private bool? _lastContributionState; // null = not yet sent to native

		// What the application last said about the part with Attach and Detach. It is a statement about the physical part,
		// so a reset of the body and a registration of the body leave it as it is.
		private bool _partAttached;

		// The free check as the plugin last took it. A new link starts out not settling, so false is also the state of a
		// link that was sent nothing.
		private bool _lastSettlingState;
		private Vector2 _lastSettlingTolerances;
		private bool _settling;
		private readonly float[] _seatOffset = new float[FTBridge.SEAT_OFFSET_LENGTH];
		private float _settledDistance;
		private float _settledAngle;
		private readonly PartPresenceJudge _presenceJudge = new();
		private readonly List<TrackedBody> _childBodies = new();

		private FTBodyStatus _lastStatus;
		private int _rejectedPoseCount;
		private PoseProvenance _lastPoseProvenance = PoseProvenance.None;

		// Quality averaging buffers for stability (raw values, not remapped)
		private const int QUALITY_BUFFER_SIZE = 30;
		private float[] _histogramBuffer;
		private float[] _varianceStdDevBuffer;
		private float[] _correspondenceDistBuffer;
		private float[] _edgeResidualBuffer;
		private float[] _projectionErrorBuffer;
		private float[] _edgeCoverageBuffer;
		private float[] _registrationMarginBuffer;
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

		/// <summary>
		/// The name this body is known by to the tracker. It can change only before the body registers. A change after that is ignored
		/// and logs a warning, because every later call to the tracker would then name a body that is not there.
		/// </summary>
		public string BodyId
		{
			get => _bodyId;
			set
			{
				if (_isRegistered && value != _bodyId)
				{
					Debug.LogWarning($"[TrackedBody] {_bodyId}: The id can change only before the body registers, so the change to '{value}' is ignored.");
					return;
				}

				_bodyId = value;
			}
		}

		/// <summary>
		/// MeshFilters for the model geometry. Must be set before OnEnable. A change after the body registers applies the next time it registers.
		/// </summary>
		public List<MeshFilter> MeshFilters
		{
			get => _meshFilters;
			set
			{
				List<MeshFilter> filters = value ?? new List<MeshFilter>();
				if (!ReferenceEquals(filters, _meshFilters))
					WarnWhenChangedAfterRegistration(nameof(MeshFilters));
				_meshFilters = filters;
			}
		}

		/// <summary>
		/// Pre-generated tracking model asset, which holds the silhouette model and the depth model. Must be set before OnEnable.
		/// A change after the body registers applies the next time it registers.
		/// </summary>
		public TrackingModelAsset TrackingModelAsset
		{
			get => _trackingModelAsset;
			set
			{
				if (_trackingModelAsset != value)
					WarnWhenChangedAfterRegistration(nameof(TrackingModelAsset));
				_trackingModelAsset = value;
			}
		}

		/// <summary>
		/// Model generation settings. Must be set before OnEnable. A change after the body registers applies the next time it registers.
		/// </summary>
		public ModelSettings ModelSettings
		{
			get => _modelSettings;
			set
			{
				ModelSettings settings = value ?? new ModelSettings();
				if (!ReferenceEquals(settings, _modelSettings))
					WarnWhenChangedAfterRegistration(nameof(ModelSettings));
				_modelSettings = settings;
			}
		}

		/// <summary>
		/// Logs one warning when a setting that the tracker reads only while the body registers is changed after that.
		/// The new value is still stored and is read the next time the body registers.
		/// </summary>
		private void WarnWhenChangedAfterRegistration(string setting)
		{
			if (_isRegistered)
				Debug.LogWarning($"[TrackedBody] {_bodyId}: {setting} was changed after the body registered, so the change applies the next time the body registers.");
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
		/// Whether a stationary body under AR pose fusion also accepts a fit that the strict registration check does not clear, when the
		/// coverage score passes it on the same start and stop bars as a body without AR and the fit stays close to where AR predicts the
		/// object. It helps soft or low-contrast real images keep the lock, keep correcting and read Tracked. With it off, the body accepts
		/// only the fits that the strict check passes.
		/// With AR, a stationary body also keeps its shown pose still while part of the object is hidden or the view is clearly weaker than usual, and takes new fits again once the view recovers.
		/// With AR, a stationary body also keeps its shown pose while the object moves fast on screen.
		/// With AR, a stationary body also takes its first fit only after 3 solves in a row agree within 8 millimeters and 2 degrees.
		/// <see cref="HoldDuringViewDip"/>, <see cref="HoldDuringFastMotion"/> and <see cref="FirstAcceptGuard"/> switch the last three of these off one by one.
		/// It is on by default, and it has no effect on a body that is not stationary or while pose fusion is not active.
		/// </summary>
		public bool AcceptSteadyFits
		{
			get => _acceptSteadyFits;
			set => _acceptSteadyFits = value;
		}

		/// <summary>
		/// Whether a stationary body under AR pose fusion keeps its shown pose still while its view dips. A view dips while the share of its sites that agree
		/// stands below 0.8 of what the view normally reaches, for example while a hand covers part of the object, and the dip ends after 5 steps in a row at
		/// or above that share. A dip can only begin after 30 steps in a row at or above it. New fits are not stored during a dip.
		/// It is on by default. It acts only while <see cref="AcceptSteadyFits"/> is on, it has no effect on a body that is not stationary or while pose fusion
		/// is not active, and the tracker reads it on every step, so a change applies at once.
		/// </summary>
		public bool HoldDuringViewDip
		{
			get => _holdDuringViewDip;
			set => _holdDuringViewDip = value;
		}

		/// <summary>
		/// Whether a stationary body under AR pose fusion keeps its shown pose while AR moves the object fast on screen. No new fit is stored on a step in which
		/// the object moves more than 6 pixels, counted for a camera with a focal length of 450 pixels. The first fit and the first 30 steps after the start are
		/// never held back.
		/// It is on by default. It acts only while <see cref="AcceptSteadyFits"/> is on, it has no effect on a body that is not stationary or while pose fusion
		/// is not active, and the tracker reads it on every step, so a change applies at once.
		/// </summary>
		public bool HoldDuringFastMotion
		{
			get => _holdDuringFastMotion;
			set => _holdDuringFastMotion = value;
		}

		/// <summary>
		/// Whether a stationary body under AR pose fusion takes its first fit only after 3 solves in a row agree within 8 millimeters and 2 degrees, so a fit that
		/// is still moving is not shown. The guard lets go after 30 steps, so a fit that never settles is still taken in the end.
		/// It is on by default. It acts only while <see cref="AcceptSteadyFits"/> is on, it has no effect on a body that is not stationary or while pose fusion
		/// is not active, and the tracker reads it on every step, so a change applies at once.
		/// </summary>
		public bool FirstAcceptGuard
		{
			get => _firstAcceptGuard;
			set => _firstAcceptGuard = value;
		}

		/// <summary>
		/// The time in seconds over which pose corrections are smoothed. A value of 0 applies them instantly.
		/// </summary>
		public float SmoothTime
		{
			get => _smoothTime;
			set => _smoothTime = Mathf.Max(0f, value);
		}

		/// <summary>
		/// How strongly each new pose is pulled toward the previous rotation, kept from 100 to 50000. Can be set before OnEnable.
		/// </summary>
		public float RotationStability
		{
			get => _rotationStability;
			set => _rotationStability = Mathf.Clamp(value, 100f, 50000f);
		}

		/// <summary>
		/// How strongly each new pose is pulled toward the previous position, kept from 1000 to 100000. Can be set before OnEnable.
		/// </summary>
		public float PositionStability
		{
			get => _positionStability;
			set => _positionStability = Mathf.Clamp(value, 1000f, 100000f);
		}

		/// <summary>
		/// Advanced silhouette tracking tuning settings. Adjust them for difficult objects such as thin tubes.
		/// A change can be applied at runtime with UpdateEdgeTrackingParameters().
		/// </summary>
		public SilhouetteTrackingSettings SilhouetteTracking => _silhouetteTracking;

		/// <summary>
		/// Multi-scale pyramid settings of silhouette tracking. Adjust them for thin objects at a distance or when the object appears small in the image.
		/// They must be set before registration, because runtime changes are not supported.
		/// </summary>
		public MultiScaleSettings MultiScale => _multiScale;

		/// <summary>
		/// Advanced depth tracking tuning settings. Adjust them when the body uses depth tracking.
		/// They must be set before registration, because runtime changes are not supported.
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
		/// Enable texture tracking, which adds tracking by the surface appearance of the object to the Silhouette or the Edge method. Must be set before OnEnable.
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
		/// The tracking method of this body, Silhouette or Edge. A change after the body registers applies the next time it registers.
		/// </summary>
		public TrackingMethod TrackingMethod
		{
			get => _trackingMethod;
			set
			{
				if (_trackingMethod != value)
					WarnWhenChangedAfterRegistration(nameof(TrackingMethod));
				_trackingMethod = value;
			}
		}

		/// <summary>
		/// Whether silhouette tracking is active (derived from TrackingMethod).
		/// </summary>
		public bool EnableSilhouetteTracking => _enableSilhouetteTracking;

		/// <summary>
		/// Enable depth tracking. It needs a source of depth (LiDAR on iOS, the depth image of an Android device that provides one, or a RealSense camera on Windows) and a depth model in the Tracking Model Asset.
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
		/// Edge tracking settings for tuning the crease angle, the search radius and similar values.
		/// </summary>
		/// <remarks>
		/// The tracker reads these when the body registers. While the body plays, a change to the depth edge threshold, the search radius, the
		/// minimum gradient, the sample step or the crease edges reaches the tracker at its next step (see <see cref="UpdateEdgeTrackingParameters"/>)
		/// when the plugin build supports it, and every other edge setting takes effect only when the body registers again, which is when the component is
		/// disabled and enabled again or the scene is played again.
		/// </remarks>
		public EdgeTrackingSettings EdgeTracking => _edgeTracking;

		/// <summary>The edge settings the tracker holds for this body, in one line, or an empty text while the body is not registered.</summary>
		internal string RegisteredEdgeSettingsSummary => _isRegistered && _enableEdgeTracking ? _registeredDetection.ToString() : string.Empty;

		/// <summary>Whether an edge setting now differs from what the tracker holds, which is what it was given when the body registered and any detection settings sent since.</summary>
		internal bool EdgeSettingsDifferFromRegistration =>
			_isRegistered && _enableEdgeTracking && _hasRegisteredEdgeSettings &&
			(!EdgeDetectionSettings.From(_edgeTracking).Equals(_registeredDetection) || _edgeTracking.RegistrationOnlySignature() != _registeredOtherEdgeSettings);

		/// <summary>Whether one of the five detection settings now differs from what the tracker holds and has not reached it yet.</summary>
		internal bool DetectionSettingsWaitForTheNextStep => _detectionApplyPending;

		/// <summary>Whether the loaded plugin build was asked to change edge settings of a registered body and does not support it.</summary>
		internal bool LiveEdgeSettingsUnavailable => _liveEdgeSettingsUnavailable;

		/// <summary>The depth considered distance and standard deviations the tracker holds for this body, in one line, or an empty text while it holds none.</summary>
		internal string RegisteredDepthSettingsSummary => _isRegistered && _depthTrackingEnabled && _hasRegisteredDepth ? _registeredDepth.ToString() : string.Empty;

		/// <summary>Whether the depth considered distance or the standard deviations now differ from what the tracker holds and have not reached it yet.</summary>
		internal bool DepthSettingsWaitForTheNextStep => _depthApplyPending;

		/// <summary>Whether the loaded plugin build was asked to change depth settings of a registered body and does not support it.</summary>
		internal bool LiveDepthSettingsUnavailable => _liveDepthSettingsUnavailable;

		/// <summary>
		/// Initial pose source configuration. Must be set before OnEnable. A change after the body registers applies the next time it registers.
		/// </summary>
		public InitialPoseSource InitialPoseSource
		{
			get => _initialPoseSource;
			set
			{
				if (_initialPoseSource != value)
					WarnWhenChangedAfterRegistration(nameof(InitialPoseSource));
				_initialPoseSource = value;
			}
		}

		/// <summary>
		/// Initial viewpoint transform (used when InitialPoseSource is Viewpoint). Must be set before OnEnable.
		/// A change after the body registers applies the next time it registers.
		/// </summary>
		public Transform InitialViewpoint
		{
			get => _initialViewpoint;
			set
			{
				if (_initialViewpoint != value)
					WarnWhenChangedAfterRegistration(nameof(InitialViewpoint));
				_initialViewpoint = value;
			}
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
		/// Can be set before OnEnable for initial configuration. A change after the body registers applies the next time it registers,
		/// and <see cref="SetTrackedMotion"/> changes it while the body plays.
		/// </summary>
		public TrackedMotion TrackedMotion
		{
			get => _trackedMotion;
			set
			{
				if (_trackedMotion != value)
					WarnWhenChangedAfterRegistration(nameof(TrackedMotion));
				_trackedMotion = value;
			}
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
		/// Whether the tracking pipeline is running on this body. It becomes true when the start decision passes, and
		/// false when the stop decision passes or the body is reset.
		/// </summary>
		/// <remarks>
		/// This is the switch of the pipeline and not a verdict on quality. It stays true while a pose is being held
		/// under pose fusion. Read <see cref="Confidence"/> for how far the displayed pose can be vouched for.
		/// </remarks>
		public bool IsTracking => _isTracking;

		/// <summary>
		/// How much of this body's displayed pose the tracker can currently vouch
		/// for, which is <see cref="TrackingConfidence.Tracked"/>,
		/// <see cref="TrackingConfidence.Degraded"/> or
		/// <see cref="TrackingConfidence.Lost"/>.
		/// </summary>
		/// <remarks>
		/// <see cref="TrackingConfidence.Lost"/> is a report and nothing more. The
		/// tracker does not reset itself when it reaches it, and on the stationary AR
		/// path the body keeps holding the last world pose. What to do about a lost
		/// body is the application's decision, and this is how it learns there is one
		/// to make.
		/// </remarks>
		public TrackingConfidence Confidence => _confidence;

		/// <summary>
		/// Why this body is in the state <see cref="Confidence"/> reports. This is
		/// what separates a body the camera is simply not pointed at from one the
		/// tracker cannot recognise.
		/// </summary>
		public TrackingConfidenceReason ConfidenceReason => _confidenceReason;

		/// <summary>The new instantaneous registration-quality readout.</summary>
		/// <remarks>
		/// Emit-only. Acquisition, acceptance, holding and pose publication do
		/// not read this value.
		/// </remarks>
		internal RegistrationQualitySnapshot RegistrationQuality => _registrationQuality;

		/// <summary>
		/// Emit-only prediction of whether tracking can begin from the currently
		/// evaluated pose. The automatic start and recovery paths never read it.
		/// </summary>
		internal float InitializationConfidence =>
			_registrationQuality.InitializationConfidence;

		/// <summary>Whether <see cref="InitializationConfidence"/> was measured.</summary>
		internal bool HasInitializationConfidence =>
			_registrationQuality.InitializationConfidenceAvailable;

		/// <summary>The temporal interpretation of successive frames' evidence.</summary>
		/// <remarks>
		/// Read by the recovery driver and the hold logic only while <see cref="UseTrackingHealth"/> is on. No reported
		/// state reads it.
		/// </remarks>
		internal TemporalTrackingSnapshot TemporalTrackingStatus => _temporalTrackingStatus;

		/// <summary>
		/// This frame's site-funnel ratio against what this view normally achieves.
		/// </summary>
		internal TrackingHealthSnapshot TrackingHealth => _trackingHealthStatus;

		/// <summary>Settings for the relative tracking-health lifecycle.</summary>
		internal TrackingHealthSettings TrackingHealthSettings => _trackingHealth;

		/// <summary>
		/// Whether this body's lifecycle is decided by relative tracking health
		/// rather than by the quality band.
		/// </summary>
		internal bool UseTrackingHealth
		{
			get => _trackingHealth != null && _trackingHealth.UseTrackingHealth;
			set
			{
				if (_trackingHealth != null)
					_trackingHealth.UseTrackingHealth = value;
			}
		}

		/// <summary>Whether the ordinary lifecycle may start this body automatically.</summary>
		/// <remarks>
		/// This runtime-only switch defaults to <c>true</c>. It exists so an
		/// initialization-quality diagnostic can evaluate a proposed pose without the
		/// lifecycle accepting it and changing the pose under evaluation. Explicit
		/// <see cref="ForceStartTracking"/> calls remain explicit authority and are not
		/// blocked by this switch.
		/// </remarks>
		internal bool AutomaticStartEnabled
		{
			get => _automaticStartEnabled;
			set => _automaticStartEnabled = value;
		}

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
		internal float ProjectionErrorAverage => GetBufferAverage(_projectionErrorBuffer);

		/// <summary>
		/// Averaged edge site coverage (valid/total) over 30 frames [0,1].
		/// Ratio of valid edge points to total keyframe sites.
		/// </summary>
		internal float EdgeCoverageAverage => GetBufferAverage(_edgeCoverageBuffer);

		/// <summary>
		/// The score the stop decision reads, from 0 to 1, averaged over 30 frames for stability. Compare it with
		/// <see cref="QualityToStartTracking"/>, <see cref="QualityToConfirmStart"/> and <see cref="QualityToStopTracking"/>,
		/// which are on the same scale, and never with a fixed number.
		/// </summary>
		/// <remarks>
		/// <para>Which score it is depends on the tracking method. An edge body without pose fusion reads the share of
		/// its sites that find a matching edge, divided by 0.70 and clipped to one. An edge body under pose fusion reads the
		/// registration margin on the same scale, with the margin's start bar reading as the coverage start bar of 0.65. The start of that
		/// body reads the coverage score, as a body without pose fusion does, and the margin is the score of its acceptance, hold and give-up.
		/// An edge body reads its edge score with depth tracking on or off, because depth only helps the fit. A silhouette body reads its
		/// histogram score, and with depth tracking on, the larger of that and the depth score.</para>
		/// <para>Only the readout is scaled. Every decision reads the unscaled score against the unscaled bars.</para>
		/// </remarks>
		public float TrackingQuality => ReadoutOf(DecisionAverageQuality);

		/// <summary>
		/// The score for the current step alone, on the same scale as <see cref="TrackingQuality"/>. The start decision
		/// reads it on five rows in a row, except for an edge body under pose fusion, whose start reads <see cref="StartScore"/>.
		/// </summary>
		public float InstantQuality => ReadoutOf(DecisionInstantQuality);

		/// <summary>
		/// The score the start decision reads for the current step, on the same 0 to 1 scale as <see cref="TrackingQuality"/>. It is
		/// compared with <see cref="QualityToStartTracking"/> on five steps in a row.
		/// </summary>
		/// <remarks>
		/// For a body that tracks by the edge method under pose fusion it is the coverage score, which is the share of sites that find a
		/// matching edge divided by 0.70 and clipped to one, while <see cref="TrackingQuality"/> is the registration margin that keeps or
		/// gives up the lock, so an application that shows both can tell a body that has not started from a body that has lost its lock.
		/// For every other body the start reads the same score as <see cref="InstantQuality"/> and this property returns it.
		/// </remarks>
		public float StartScore => StartReadsCoverage ? StartCoverageSignal : InstantQuality;

		/// <summary>The score the decisions read, averaged over the quality window, before any readout scale.</summary>
		/// <remarks>
		/// A body that tracks by the edge method decides on its edge score alone, with depth tracking on or off, because depth
		/// only helps the fit. A body that tracks by silhouette decides on the larger of its silhouette score and, with depth
		/// tracking on, its depth score, because a body can have a depth model and no usable silhouette.
		/// </remarks>
		internal float DecisionAverageQuality
		{
			get
			{
				if (ReadsRegistrationMargin)
					return RegistrationMarginAverage;
				if (_enableEdgeTracking)
					return ComputeEdgeQuality(EdgeCoverageAverage);
				float quality = SilhouetteTrackingQuality;
				if (_enableDepthTracking)
					quality = Mathf.Max(quality, DepthTrackingQuality);
				return quality;
			}
		}

		/// <summary>The score the decisions read for the current step, before any readout scale.</summary>
		/// <remarks>The same choice of score as <see cref="DecisionAverageQuality"/>, for the current step alone.</remarks>
		internal float DecisionInstantQuality
		{
			get
			{
				if (ReadsRegistrationMargin)
					return InstantRegistrationMargin;
				if (_enableEdgeTracking)
					return InstantEdgeCoverageQuality;
				float quality = InstantSilhouetteQuality;
				if (_enableDepthTracking)
					quality = Mathf.Max(quality, InstantDepthQuality);
				return quality;
			}
		}

		/// <summary>
		/// The factor that puts a registration margin on the scale of the coverage score. A margin at its start bar
		/// reads exactly <see cref="TrackerDefaults.EDGE_QUALITY_TO_START"/>, and the result is clipped to zero to one.
		/// </summary>
		internal static float MarginReadout(float margin) =>
			Mathf.Clamp01(margin / TrackerDefaults.REGISTRATION_MARGIN_ACQUIRE_BAR * TrackerDefaults.EDGE_QUALITY_TO_START);

		/// <summary>A score or a bar of the scalar this body's decisions read, on the scale a reader sees.</summary>
		private float ReadoutOf(float value) => ReadsRegistrationMargin ? MarginReadout(value) : value;

		/// <summary>
		/// The registration margin averaged over the same window as the coverage average. Frames with no reading
		/// enter the average as zero, so a body whose evidence record has gone away is pulled down rather than left at
		/// its last good value.
		/// </summary>
		internal float RegistrationMarginAverage =>
			GetBufferAverage(_registrationMarginBuffer);

		/// <summary>This frame's registration margin, or zero if it has none.</summary>
		/// <remarks>
		/// Every decision that reads the margin reads it here or through the average
		/// this fills, so <see cref="TrackerDefaults.MarginResidualTerm"/> is applied
		/// here and nowhere else on the body. Under the default it is the plugin's
		/// score exactly as published.
		/// </remarks>
		internal float InstantRegistrationMargin
		{
			get
			{
				if (!_registrationQuality.ScoreAvailable)
					return 0f;
				if (MarginResidualTerm == TrackerDefaults.MarginResidualTerm.Percentile90)
					return _registrationQuality.Score;
				return RegistrationQualitySnapshot.TryReadSubpixelMedianMargin(
					_registrationQuality.RawEvidence, out float margin)
					? margin
					: 0f;
			}
		}

		/// <summary>
		/// Debug-only direct ratio of valid Edge correspondences to retained Edge sites,
		/// averaged over the same window as the coverage average.
		/// </summary>
		/// <remarks>
		/// Unlike the coverage score, this value is not rescaled to reach one at the
		/// configured coverage maximum. No lifecycle decision reads it.
		/// </remarks>
		internal float DirectEdgeInlierRatio => EdgeCoverageAverage;

		/// <summary>The depth quality from 0 to 1, averaged over 30 frames. It reads zero while depth tracking is off.</summary>
		public float DepthTrackingQuality => ComputeDepthQuality(CorrespondenceDistanceAverage);

		/// <summary>The silhouette quality from 0 to 1, which is the histogram score averaged over 30 frames.</summary>
		public float SilhouetteTrackingQuality => ComputeSilhouetteQuality(HistogramAverage);

		/// <summary>
		/// An alternative edge quality that uses the median residual (pixels). It does not depend on contrast, because it
		/// reads how well the sites converged and not how strong their image gradients were.
		/// It is 1.0 at a residual of 0 and 0.0 at EDGE_RESIDUAL_MAX. No decision reads it.
		/// </summary>
		internal float EdgeResidualQuality
		{
			get
			{
				if (!_enableEdgeTracking || !_lastStatus.HasEdgeModality) return 0;
				return 1f - Mathf.Clamp01(EdgeResidualAverage / TrackerDefaults.EDGE_RESIDUAL_MAX);
			}
		}

		/// <summary>
		/// An alternative edge quality that uses the projection error (degrees). It does not depend on contrast either.
		/// It is 1.0 at a projection error of 0 and 0.0 at EDGE_PROJECTION_ERROR_BAD. No decision reads it.
		/// </summary>
		internal float EdgeProjectionQuality
		{
			get
			{
				if (!_enableEdgeTracking || !_lastStatus.HasEdgeModality) return 0;
				return 1f - Mathf.Clamp01(ProjectionErrorAverage / TrackerDefaults.EDGE_PROJECTION_ERROR_BAD);
			}
		}

		/// <summary>The coverage score of the current step alone, from the share of sites that found a matching edge.</summary>
		private float InstantEdgeCoverageQuality =>
			_lastStatus.n_total_edge_sites > 0
				? ComputeEdgeQuality((float)_lastStatus.n_valid_edge_points / _lastStatus.n_total_edge_sites)
				: 0f;

		/// <summary>
		/// Debug-only direct ratio of valid Edge correspondences to retained Edge sites
		/// on the current completed frame. No lifecycle decision reads it.
		/// </summary>
		internal float InstantDirectEdgeInlierRatio =>
			_lastStatus.n_total_edge_sites > 0
				? Mathf.Clamp01((float)_lastStatus.n_valid_edge_points /
					_lastStatus.n_total_edge_sites)
				: 0f;

		/// <summary>
		/// Debug-only share of produced probe candidates that pass the residual and
		/// edge-normal agreement gates on the current completed frame.
		/// </summary>
		/// <remarks>
		/// This conditions on a candidate already existing, while
		/// <see cref="InstantDirectEdgeInlierRatio"/> also charges missing candidates
		/// and unavailable model sites. No lifecycle decision reads it.
		/// </remarks>
		internal float InstantCandidateAgreementRatio =>
			_lastStatus.n_edge_candidate_sites > 0
				? Mathf.Clamp01((float)_lastStatus.n_valid_edge_points /
					_lastStatus.n_edge_candidate_sites)
				: 0f;

		/// <summary>The depth quality of the current step alone, from 0 to 1.</summary>
		public float InstantDepthQuality =>
			ComputeDepthQuality(_lastStatus.mean_correspondence_distance);

		/// <summary>The silhouette quality of the current step alone, from 0 to 1.</summary>
		public float InstantSilhouetteQuality =>
			ComputeSilhouetteQuality(_lastStatus.histogram_discriminability);

		private float ComputeEdgeQuality(float coverage)
		{
			if (!_enableEdgeTracking || !_lastStatus.HasEdgeModality)
				return 0;
			return EdgeQualityFromCoverage(coverage, ShippedDefaults.AppliesTo(this));
		}

		/// <summary>The edge quality an edge coverage reads as, on the bar's scale, for an edge body without pose fusion.</summary>
		/// <remarks>
		/// Zero at a coverage of zero and one at <see cref="ShippedDefaults.COVERAGE_FULL"/>, unless the
		/// run states another floor and full-scale coverage.
		/// </remarks>
		internal static float EdgeQualityFromCoverage(float coverage) =>
			EdgeQualityFromCoverage(coverage, !ShippedDefaults.PoseFusionActive());

		/// <summary>The edge quality an edge coverage reads as, on the scale of a body the defaults do or do not serve.</summary>
		/// <remarks>
		/// A scale whose full-scale share is not above its floor reads zero, because a negative span would read a
		/// coverage of zero as a perfect score.
		/// </remarks>
		internal static float EdgeQualityFromCoverage(float coverage, bool shippedDefaults)
		{
			(float floor, float full) = ShippedDefaults.CoverageScale(shippedDefaults);
			return full > floor ? Mathf.InverseLerp(floor, full, coverage) : 0f;
		}

		private float ComputeDepthQuality(float correspondenceDistance)
		{
			if (!_enableDepthTracking || !_lastStatus.HasDepthModality)
				return 0;
			float depthVis = DepthVisibility;
			float minVis = _isTracking ? 0.01f : 0.10f;
			if (depthVis < minVis) return 0;
			return 1f - Mathf.Clamp01(correspondenceDistance / _depthTracking.DistanceTolerance);
		}

		private float ComputeSilhouetteQuality(float histogramDiscriminability)
		{
			if (SilhouetteVisibility <= 0)
				return 0;
			float globalPeak = TrackedBodyManager.Instance?.GlobalHistogramPeak ?? TrackerDefaults.HISTOGRAM_GOOD;
			return Mathf.InverseLerp(TrackerDefaults.HISTOGRAM_BAD, globalPeak, histogramDiscriminability);
		}

		/// <summary>
		/// The shape quality from 0 to 1, where higher is better, averaged over 30 frames for stability.
		/// It uses the standard deviation of the variance to measure how consistently the contour fits everywhere.
		/// A low standard deviation means a consistent fit everywhere, which points to the correct model.
		/// A high standard deviation means an inconsistent fit, which points to a wrong model or a poor viewing angle.
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
		/// The share of silhouette lines that are visible (0-1). It reads zero for a body that tracks by the edge method,
		/// which has no silhouette lines.
		/// </summary>
		public float SilhouetteVisibility => _lastStatus.n_max_lines > 0 ? (float)_lastStatus.n_valid_lines / _lastStatus.n_max_lines : 0f;

		/// <summary>
		/// Depth visibility ratio (0-1). Ratio of valid depth points.
		/// </summary>
		public float DepthVisibility => _lastStatus.n_max_points > 0 ? (float)_lastStatus.n_valid_points / _lastStatus.n_max_points : 0f;

		/// <summary>
		/// Raw status from native tracker.
		/// </summary>
		internal FTBodyStatus LastStatus => _lastStatus;

		/// <summary>
		/// Poses this body refused this session because they could not be used.
		/// </summary>
		/// <remarks>
		/// Non-zero means a gate fired, not that tracking failed. Diagnostics only.
		/// </remarks>
		internal int RejectedPoseCount => _rejectedPoseCount;

		/// <summary>
		/// Where the pose currently on this transform came from.
		/// </summary>
		/// <remarks>
		/// Always <see cref="PoseProvenance.None"/> unless provenance recording is
		/// enabled on the manager, which it is not by default. Read-only, and no
		/// decision in this package consumes it.
		/// </remarks>
		internal PoseProvenance LastPoseProvenance => _lastPoseProvenance;

		/// <summary>
		/// Per-frame acceptance evidence for the current frame. Read-only, and no
		/// decision in this package consumes any of it.
		/// </summary>
		/// <remarks>
		/// The three site counts form a funnel over one site set, so they are only
		/// meaningful read together:
		/// <c>ExpectedVisibleSites &gt;= CandidateSites &gt;= AgreeingSites</c>.
		/// The first step reports what the camera could see, the second whether a
		/// candidate was found at all, the third whether the candidate agreed.
		/// The single coverage ratio the quality scalar uses collapses all three,
		/// which is why a body leaving the frame and a body whose evidence
		/// disagrees are indistinguishable to it.
		/// </remarks>
		internal AcceptanceEvidence Evidence => AcceptanceEvidence.From(_lastStatus, _enableEdgeTracking);

		/// <summary>
		/// Conditioning of this body's pose-solve system on the frame's last
		/// optimisation step. Read-only; no decision consumes it.
		/// </summary>
		internal PoseConditioning Conditioning => PoseConditioning.From(_lastStatus);

		/// <summary>
		/// Populations this body rebuilt with a render, cumulative since set-up.
		/// </summary>
		/// <remarks>
		/// This and <see cref="ViewBankReuses"/> partition every population
		/// replacement, so the two read together give the share of replacements a
		/// stored view answered without a render.
		/// </remarks>
		internal int ViewBankRenders => _lastStatus.edge_view_bank_renders;

		/// <summary>
		/// Populations answered from a stored view at no render cost, cumulative
		/// since set-up. The companion of <see cref="ViewBankRenders"/>.
		/// </summary>
		internal int ViewBankReuses => _lastStatus.edge_view_bank_reuses;

		/// <summary>Stored views held right now, at most the bank's capacity.</summary>
		internal int ViewBankSize => _lastStatus.edge_view_bank_size;

		/// <summary>
		/// Share of the live view's sites that passed this frame's visibility test.
		/// </summary>
		/// <remarks>
		/// <c>-1</c> says there was no render to test against. That is an absence,
		/// not a low fraction, and it is deliberately not clamped to zero: a reader
		/// that averages it in would record the frames carrying no measurement as
		/// the frames carrying the worst one.
		/// </remarks>
		internal float ViewVisibleFraction => _lastStatus.edge_view_visible_fraction;

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

		/// <summary>Which scalar this body's decisions read.</summary>
		/// <remarks>
		/// The manager owns the setting because the bars move with the scalar and
		/// a scene with two bodies reading two different scales would have no one
		/// band. Before a manager exists the shipped default answers.
		/// <para>The registration margin is measured on edge sites only, so a body
		/// with no edge modality has no margin to read. Such a body reads the
		/// coverage signal for its start, stop and hold decisions, whatever the
		/// manager's setting, and an edge body reads the manager's setting.</para>
		/// </remarks>
		internal TrackerDefaults.QualitySignalSource QualitySignalSource =>
			!_enableEdgeTracking
				? TrackerDefaults.QualitySignalSource.Coverage
				: TrackedBodyManager.Instance != null
				? TrackedBodyManager.Instance.QualitySignalSource
				: ShippedDefaults.QualitySignalSourceFor(ShippedDefaults.PoseFusionActive());

		private bool ReadsRegistrationMargin =>
			QualitySignalSource ==
			TrackerDefaults.QualitySignalSource.RegistrationMargin;

		/// <summary>A bar of the scalar the body reads: its own when it sets one, the default otherwise.</summary>
		internal static float CustomOrDefault(bool useCustom, float custom, float fallback) =>
			useCustom ? custom : fallback;

		/// <summary>Which residual term the margin this body's decisions read takes.</summary>
		/// <remarks>Read from the manager, as the signal source is. Before a manager
		/// exists the shipped default answers.</remarks>
		internal TrackerDefaults.MarginResidualTerm MarginResidualTerm =>
			TrackedBodyManager.Instance != null
				? TrackedBodyManager.Instance.MarginResidualTerm
				: TrackerDefaults.MARGIN_RESIDUAL_TERM;

		// The bars the decisions run at, on the scale of the scalar the decisions read. On the margin scale the three
		// bars are the margin's own, and a custom threshold configured on a body is ignored with its reason: it was set on
		// the coverage scale and there is no reading of it on this one.

		/// <summary>The start bar as configured, before the band corrects an inverted pair.</summary>
		internal float ConfiguredStartBar => ReadsRegistrationMargin
			? TrackerDefaults.REGISTRATION_MARGIN_ACQUIRE_BAR
			: _enableEdgeTracking
				? CustomOrDefault(_useCustomStartThreshold, _customQualityToStart,
					TrackerDefaults.EDGE_QUALITY_TO_START)
				: CustomOrDefault(_useCustomSilhouetteStartThreshold, _customSilhouetteQualityToStart,
					TrackerDefaults.QUALITY_TO_START);

		/// <summary>The stop bar as configured.</summary>
		internal float ConfiguredStopBar => ReadsRegistrationMargin
			? TrackerDefaults.REGISTRATION_MARGIN_HOLD_BAR
			: _enableEdgeTracking
				? CustomOrDefault(_useCustomStopThreshold, _customQualityToStop,
					TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD)
				: CustomOrDefault(_useCustomSilhouetteStopThreshold, _customSilhouetteQualityToStop,
					TrackerDefaults.LOSE_TRACKING_THRESHOLD);

		/// <summary>The bar a frame's solve must clear to be accepted.</summary>
		internal float AcceptBar => ReadsRegistrationMargin
			? TrackerDefaults.REGISTRATION_MARGIN_ACQUIRE_BAR
			: _enableEdgeTracking
				? TrackerDefaults.EDGE_NICE_QUALITY_THRESHOLD
				: TrackerDefaults.NICE_QUALITY_THRESHOLD;

		/// <summary>Whether the last frame of a start can have a bar of its own, which is the custom confirm score.</summary>
		private bool ConfirmsOnItsOwnBar =>
			_enableEdgeTracking && QualitySignalSource == TrackerDefaults.QualitySignalSource.Coverage;

		/// <summary>
		/// The score the last frame of a start must reach. For a body that tracks by the edge method and reads the coverage
		/// score it is the custom confirm score when one is set, and the configured start bar otherwise. For every other body
		/// and every other score it is the configured start bar, so it asks for nothing more than the start does.
		/// </summary>
		internal float ConfirmBar => ConfirmsOnItsOwnBar
			? CustomOrDefault(_useCustomConfirmThreshold, _customQualityToConfirmStart, ConfiguredStartBar)
			: ConfiguredStartBar;

		/// <summary>
		/// Whether the start of this body reads the coverage score. An edge body under pose fusion does, as a body without pose
		/// fusion does, unless a run states otherwise, and its acceptance, hold and give-up keep reading the registration margin.
		/// </summary>
		internal bool StartReadsCoverage => _enableEdgeTracking && ReadsRegistrationMargin &&
			ShippedDefaults.StartFromCoverageUnderFusion(
				ShippedDefaults.AppliesUnderFusion(this, TrackedBodyManager.Instance));

		/// <summary>The coverage score of the current step on the scale of a body without pose fusion, which is the scale its start bar is on.</summary>
		private float StartCoverageSignal =>
			_lastStatus.HasEdgeModality && _lastStatus.n_total_edge_sites > 0
				? EdgeQualityFromCoverage((float)_lastStatus.n_valid_edge_points / _lastStatus.n_total_edge_sites, true)
				: 0f;

		/// <summary>The score the start decision reads on this row, before any readout scale.</summary>
		internal float StartSignal => StartReadsCoverage ? StartCoverageSignal : SignalFor(QualityDecision.Acquire);

		/// <summary>The bar the start decision reads on this row, on the scale of <see cref="StartSignal"/>.</summary>
		internal float StartBar => StartReadsCoverage
			? QualityBand.Poise(
				CustomOrDefault(_useCustomStartThreshold, _customQualityToStart, TrackerDefaults.EDGE_QUALITY_TO_START),
				CustomOrDefault(_useCustomStopThreshold, _customQualityToStop, TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD),
				TrackerDefaults.EDGE_NICE_QUALITY_THRESHOLD).Acquire
			: DecisionBand.ThresholdFor(QualityDecision.Acquire);

		/// <summary>The bar the coverage score must keep clearing once a body is going, on the scale of <see cref="StartSignal"/>.</summary>
		internal float StopBar => QualityBand.Poise(
			CustomOrDefault(_useCustomStartThreshold, _customQualityToStart, TrackerDefaults.EDGE_QUALITY_TO_START),
			CustomOrDefault(_useCustomStopThreshold, _customQualityToStop, TrackerDefaults.EDGE_LOSE_TRACKING_THRESHOLD),
			TrackerDefaults.EDGE_NICE_QUALITY_THRESHOLD).Hold;

		/// <summary>
		/// The coverage score averaged over the quality window, on the scale of <see cref="StartSignal"/>. It is the score the stop of a body without
		/// pose fusion reads, against <see cref="StopBar"/>.
		/// </summary>
		internal float StopSignal => EdgeQualityFromCoverage(EdgeCoverageAverage, true);

		/// <summary>Whether this row's score clears the start bar.</summary>
		internal bool StartClears => StartReadsCoverage ? StartSignal >= StartBar : DecisionBand.Clears(QualityDecision.Acquire, SignalFor(QualityDecision.Acquire));

		/// <summary>
		/// The bar the score must clear on five rows in a row to start tracking, on the scale of
		/// <see cref="TrackingQuality"/>. It is the bar that is enforced, so a start bar set below the stop bar reads as
		/// the stop bar.
		/// </summary>
		public float QualityToStartTracking => StartReadsCoverage ? StartBar : ReadoutOf(DecisionBand.Acquire);

		/// <summary>
		/// The bar the score must clear on the last row of a start, on the scale of <see cref="TrackingQuality"/>. For a
		/// body that tracks by the edge method and reads the coverage score it is the start bar unless the body sets its own
		/// confirm score. For every other body it is <see cref="QualityToStartTracking"/>, so it asks for nothing more than
		/// the start does.
		/// </summary>
		/// <remarks>
		/// The start reads the same scaled score for both, so a confirm score at or below the start score is never the
		/// reason a start is withheld.
		/// </remarks>
		public float QualityToConfirmStart => StartReadsCoverage ? StartBar : ReadoutOf(ConfirmsOnItsOwnBar ? ConfirmBar : DecisionBand.Acquire);

		/// <summary>
		/// The bar the averaged score must stay at or above for tracking to continue, on the scale of
		/// <see cref="TrackingQuality"/>. Tracking stops after the average has been below it on five rows in a row.
		/// </summary>
		public float QualityToStopTracking => ReadoutOf(ConfiguredStopBar);

		/// <summary>
		/// Whether to use a custom confirm score instead of the default, for a body that tracks by the edge method. A body
		/// that tracks by the silhouette method never reads it.
		/// </summary>
		public bool UseCustomConfirmThreshold
		{
			get => _useCustomConfirmThreshold;
			set => _useCustomConfirmThreshold = value;
		}

		/// <summary>
		/// Custom confirm score (0-1), the score the last frame of a start must reach, for a body that tracks by the edge
		/// method. Only used when UseCustomConfirmThreshold is true.
		/// </summary>
		public float CustomQualityToConfirmStart
		{
			get => _customQualityToConfirmStart;
			set => _customQualityToConfirmStart = Mathf.Clamp01(value);
		}

		/// <summary>
		/// The settled start that the tooling asks for, and otherwise none. A body has no setting of its own for it. The settled start refines
		/// the body from the pose of the step before and not from the guide, and starts it only once the refined pose has stopped moving. A
		/// run of the tooling switches it on through <see cref="TrackingOverrides.StartSettle"/>, and the start search switches it on by itself
		/// while the guide is out of reach of the winner (see <see cref="StartSettleEffective"/>).
		/// </summary>
		internal StartSettle.Setting StartSettleInForce
		{
			get
			{
				(int Rows, float Pixels, float MaxDriftPixels)? forced = TrackingOverrides.StartSettle;
				if (forced.HasValue)
					return new StartSettle.Setting(forced.Value.Rows > 0, forced.Value.Rows, forced.Value.Pixels, forced.Value.MaxDriftPixels);
				return new StartSettle.Setting(false, StartSettle.DEFAULT_ROWS, StartSettle.DEFAULT_PIXELS, StartSettle.DEFAULT_MAX_DRIFT_PIXELS);
			}
		}

		/// <summary>
		/// The settled start as this step reads it. It is <see cref="StartSettleInForce"/>, except while the search has put the guide out of
		/// reach of the winner (see <see cref="StartSearchFarPath"/>). Then the body settles from the winner before it starts, with the rows,
		/// pixels and drift bound the tooling states or with the defaults.
		/// </summary>
		internal StartSettle.Setting StartSettleEffective
		{
			get
			{
				StartSettle.Setting own = StartSettleInForce;
				if (own.On || !StartSearchFarPath)
					return own;
				return new StartSettle.Setting(true, StartSettle.DEFAULT_ROWS, StartSettle.DEFAULT_PIXELS, StartSettle.DEFAULT_MAX_DRIFT_PIXELS);
			}
		}

		/// <summary>What the settle check read on the latest step before the first start. <see cref="StartSettleReading.Known"/> is false when it has read none.</summary>
		public StartSettleReading LastStartSettle => _settleState.Last;

		/// <summary>
		/// Whether the plugin searches the whole camera image for the shape of the body before the body's first start. With this on,
		/// the plugin scores many prerendered outline views of the body at every image position and keeps the best hypothesis. A guide
		/// that stands within <see cref="StartSearchReachPixels"/> of that winner is left alone, and the body starts as it does without
		/// the search. A winner that stands farther is the pose the tracker's refinement starts from in place of the guide, the body
		/// settles from it before it starts, and the start is withheld until the same winner was
		/// found on three searches in a row and reaches the margin bar and the score floor. The guide the person placed stays where it is
		/// and stays drawn until the start. The search only proposes a pose, so the start bars still verify it. This acts only before the
		/// first start of a body that tracks by the edge method, has no parent and is refined before its start. A plugin without the
		/// exports starts the body without the search. It is on by default. A body that switches it off starts as it does without the search,
		/// and a scene or prefab that saved the field keeps its saved value, so only a body that has never stored it starts with the search on.
		/// </summary>
		public bool SearchBeforeStart
		{
			get => _searchBeforeStart;
			set => _searchBeforeStart = value;
		}

		/// <summary>
		/// The number of tracking steps between two searches of the whole image. The default is 2, which searches on every second step. A value
		/// of 1 searches on every step. It is kept from 1 to 60. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public int StartSearchPeriodSteps
		{
			get => _startSearchPeriodSteps;
			set => _startSearchPeriodSteps = StartSearch.ClampPeriodSteps(value);
		}

		/// <summary>
		/// The margin the winner of a search must reach before a start is let through, as the score of the winner divided by the score
		/// of the best hypothesis that stands far from it. The default is 1.4, and 0 sets no margin bar. It is kept from 0 to 3. It is
		/// read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchMarginBar
		{
			get => _startSearchMarginBar;
			set => _startSearchMarginBar = StartSearch.ClampMarginBar(value);
		}

		/// <summary>
		/// The score the winner of a search must reach before a start is let through, as the share of the outline points that find an
		/// edge of the same direction. The default is 0.6, and 0 sets no score floor. It is kept from 0 to 1. It is read only while
		/// <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchScoreFloor
		{
			get => _startSearchScoreFloor;
			set => _startSearchScoreFloor = StartSearch.ClampScoreFloor(value);
		}

		/// <summary>
		/// How far the guide may stand from the winner of a search, as the median on-screen displacement of the model's points in
		/// pixels, and still count as in reach. A guide in reach is left alone, the winner is not used, and the body starts as it does
		/// without the search. A winner that stands farther is where the body starts from. The default is 16, and 0 treats every guide
		/// as out of reach. It is kept from 0 to 200. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchReachPixels
		{
			get => _startSearchReachPixels;
			set => _startSearchReachPixels = StartSearch.ClampReachPixels(value);
		}

		/// <summary>
		/// The half angle, in degrees, of the cone of views the plugin draws around the guide's view for the start search. A search finds
		/// an object whose true view stands within it of the guide's view. The default is 5. It is kept from 0 to 45. It is read only while
		/// <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchConeHalfAngle
		{
			get => _startSearchConeHalfAngle;
			set => _startSearchConeHalfAngle = StartSearch.ClampConeHalfAngle(value);
		}

		/// <summary>
		/// The spacing, in degrees, between neighbouring views of the cone, and the step of the turns in the image plane. The default is 5.
		/// It is kept from 2.5 to 20. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchConeSpacing
		{
			get => _startSearchConeSpacing;
			set => _startSearchConeSpacing = StartSearch.ClampConeSpacing(value);
		}

		/// <summary>
		/// The range, in degrees, of the turns in the image plane that the bank holds. The turns are zero and plus and minus each step of
		/// the spacing out to this value, so the zero turn is always held. The default is 10. It is kept from 0 to 45. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchRollRange
		{
			get => _startSearchRollRange;
			set => _startSearchRollRange = StartSearch.ClampRollRange(value);
		}

		/// <summary>
		/// The half angle, in degrees, of the fine stage, which scores the views within this angle of the winner at a step of 2.5 degrees.
		/// A value of 0 switches the fine stage off. It is kept from 0 to 15. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchFineHalfAngle
		{
			get => _startSearchFineHalfAngle;
			set => _startSearchFineHalfAngle = StartSearch.ClampFineHalfAngle(value);
		}

		/// <summary>
		/// How far beyond the box of the guide's outline the search looks, in pixels of the search image (its longer side is at most 640
		/// pixels). A template is placed only where its own outline box lies inside the window, so an object within about this far of the
		/// guide is found. The default is 96, and 0 searches the whole image. It is kept from 0 to 640. It is read only while
		/// <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public float StartSearchWindowMargin
		{
			get => _startSearchWindowMargin;
			set => _startSearchWindowMargin = StartSearch.ClampWindowMargin(value);
		}

		/// <summary>
		/// How many distances the bank holds around the guide's own distance. One is the guide's own, three (the default) are 0.92, 1.0 and 1.09
		/// times it, and five add 0.85 and 1.2. Another value is read as the nearest of the three. It is read only while
		/// <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public int StartSearchDistanceCount
		{
			get => _startSearchDistanceCount;
			set => _startSearchDistanceCount = StartSearch.ClampDistanceCount(value);
		}

		/// <summary>
		/// Whether the plugin makes the other distances of the bank of the start search by scaling one drawing of each view. Off, which is the
		/// default, it draws every distance of every view. On, it draws each view once, at the guide's own distance, and scales that drawing for
		/// the other distances, which saves the drawings of the other distances. It is read only while <see cref="SearchBeforeStart"/> is on.
		/// </summary>
		public bool StartSearchScaleDistances
		{
			get => _startSearchScaleDistances;
			set => _startSearchScaleDistances = value;
		}

		/// <summary>Whether the plugin scales the distances of the bank as the run reads it, where an explicit value of the tooling wins over the body's own.</summary>
		internal bool StartSearchScaleDistancesInForce => TrackingOverrides.StartSearchScaleDistances ?? _startSearchScaleDistances;

		/// <summary>What the bank of the search is made of as the run reads it, where an explicit value of the tooling wins over the body's own.</summary>
		internal StartSearch.BankSetting StartSearchBankInForce
		{
			get
			{
				(float ConeHalfAngle, float ConeSpacing, float RollRange, float FineHalfAngle, float WindowMargin, int DistanceCount)? forced = TrackingOverrides.StartSearchBank;
				if (forced.HasValue)
					return StartSearch.ClampBank(forced.Value.ConeHalfAngle, forced.Value.ConeSpacing, forced.Value.RollRange, forced.Value.FineHalfAngle,
						forced.Value.WindowMargin, forced.Value.DistanceCount);
				return StartSearch.ClampBank(_startSearchConeHalfAngle, _startSearchConeSpacing, _startSearchRollRange, _startSearchFineHalfAngle,
					_startSearchWindowMargin, _startSearchDistanceCount);
			}
		}

		/// <summary>The start search as the run reads it, where an explicit value of the tooling wins over the body's own.</summary>
		internal StartSearch.Setting StartSearchInForce
		{
			get
			{
				(int PeriodSteps, float MarginBar, float ScoreFloor, float ReachPixels)? forced = TrackingOverrides.StartSearch;
				if (forced.HasValue)
					return new StartSearch.Setting(forced.Value.PeriodSteps > 0, StartSearch.ClampPeriodSteps(forced.Value.PeriodSteps),
						StartSearch.ClampMarginBar(forced.Value.MarginBar), StartSearch.ClampScoreFloor(forced.Value.ScoreFloor),
						StartSearch.ClampReachPixels(forced.Value.ReachPixels));
				return new StartSearch.Setting(_searchBeforeStart, StartSearch.ClampPeriodSteps(_startSearchPeriodSteps),
					StartSearch.ClampMarginBar(_startSearchMarginBar), StartSearch.ClampScoreFloor(_startSearchScoreFloor),
					StartSearch.ClampReachPixels(_startSearchReachPixels));
			}
		}

		/// <summary>
		/// Whether to use a custom quality threshold to start tracking instead of the default. This is the threshold of a
		/// body that tracks by the edge method. A body that tracks by the silhouette method reads
		/// <see cref="UseCustomSilhouetteStartThreshold"/> and never this one.
		/// </summary>
		public bool UseCustomStartThreshold
		{
			get => _useCustomStartThreshold;
			set => _useCustomStartThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) required to start tracking, for a body that tracks by the edge method. Only
		/// used when UseCustomStartThreshold is true. It starts at the edge start bar, which is 0.65.
		/// </summary>
		public float CustomQualityToStart
		{
			get => _customQualityToStart;
			set => _customQualityToStart = Mathf.Clamp01(value);
		}

		/// <summary>
		/// Whether to use a custom quality threshold to stop tracking instead of the default, for a body that tracks by
		/// the edge method. A body that tracks by the silhouette method reads
		/// <see cref="UseCustomSilhouetteStopThreshold"/> and never this one.
		/// </summary>
		public bool UseCustomStopThreshold
		{
			get => _useCustomStopThreshold;
			set => _useCustomStopThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) below which tracking is lost, for a body that tracks by the edge method. Only
		/// used when UseCustomStopThreshold is true. It starts at the edge stop bar, which is 0.5.
		/// </summary>
		public float CustomQualityToStop
		{
			get => _customQualityToStop;
			set => _customQualityToStop = Mathf.Clamp01(value);
		}

		/// <summary>
		/// Whether to use a custom quality threshold to start tracking instead of the default, for a body that tracks by
		/// the silhouette method.
		/// </summary>
		public bool UseCustomSilhouetteStartThreshold
		{
			get => _useCustomSilhouetteStartThreshold;
			set => _useCustomSilhouetteStartThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) required to start tracking, for a body that tracks by the silhouette method.
		/// Only used when UseCustomSilhouetteStartThreshold is true.
		/// </summary>
		public float CustomSilhouetteQualityToStart
		{
			get => _customSilhouetteQualityToStart;
			set => _customSilhouetteQualityToStart = Mathf.Clamp01(value);
		}

		/// <summary>
		/// Whether to use a custom quality threshold to stop tracking instead of the default, for a body that tracks by
		/// the silhouette method.
		/// </summary>
		public bool UseCustomSilhouetteStopThreshold
		{
			get => _useCustomSilhouetteStopThreshold;
			set => _useCustomSilhouetteStopThreshold = value;
		}

		/// <summary>
		/// Custom quality threshold (0-1) below which tracking is lost, for a body that tracks by the silhouette method.
		/// Only used when UseCustomSilhouetteStopThreshold is true.
		/// </summary>
		public float CustomSilhouetteQualityToStop
		{
			get => _customSilhouetteQualityToStop;
			set => _customSilhouetteQualityToStop = Mathf.Clamp01(value);
		}

		/// <summary>
		/// The acquire / hold / accept thresholds this body's decisions run at.
		/// </summary>
		/// <remarks>
		/// Built from the three configured bars each time it is read, so a bar changed at
		/// runtime reaches the next decision and no caller can hold a stale copy. The band
		/// is where the invariant that the entry bar is not below the holding bar is
		/// applied to a body that tracks by the edge method. A body that tracks by the silhouette
		/// method runs its bars as declared, a start bar of 0.3 and a stop bar of 0.4 by default.
		/// These are the unscaled bars the decisions read. The
		/// public bars are the same values on the scale a reader sees.
		/// </remarks>
		internal QualityBand DecisionBand => _enableEdgeTracking
			? QualityBand.Poise(ConfiguredStartBar, ConfiguredStopBar, AcceptBar)
			: QualityBand.Declared(ConfiguredStartBar, ConfiguredStopBar, AcceptBar);

		/// <summary>
		/// The quality scalar a decision reads.
		/// </summary>
		/// <remarks>
		/// Which scalar that is comes from <see cref="QualityBand.SignalFor"/> and
		/// from nowhere else. Reading a scalar directly at a decision site is the
		/// habit this method exists to replace.
		/// </remarks>
		internal float SignalFor(QualityDecision decision)
		{
			return QualityBand.SignalFor(decision) == QualitySignal.Instant
				? DecisionInstantQuality
				: DecisionAverageQuality;
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

		/// <summary>
		/// Whether the part joins the parent's solve only when the application says so (only used if AssemblyMode is true).
		/// When true, the part is scored but never joins the parent's solve until <see cref="Attach"/> is called, and
		/// <see cref="AssemblyQualityToConfirm"/> is not used.
		/// </summary>
		public bool AttachManually
		{
			get => _attachManually;
			set => _attachManually = value;
		}

		/// <summary>
		/// Whether the application has attached the part with <see cref="Attach"/> and not detached it since. It is false
		/// until the first call of <see cref="Attach"/>. A reset of the body and a new registration of the body do not
		/// change it, because it says what the application knows about the physical part and not what the tracker holds.
		/// </summary>
		public bool IsAttached => _partAttached;

		/// <summary>
		/// Says that the part is mounted. From the next tracked frame on, a part with <see cref="AssemblyMode"/> and
		/// <see cref="AttachManually"/> joins the parent's solve, as if its mesh were a part of the parent. The call only
		/// records the request and the manager applies it, so it is safe to call at any time.
		/// </summary>
		public void Attach() => _partAttached = true;

		/// <summary>
		/// Says that the part is no longer mounted. From the next tracked frame on, a part with <see cref="AssemblyMode"/>
		/// and <see cref="AttachManually"/> is scored again and no longer joins the parent's solve. The call only records
		/// the request and the manager applies it.
		/// </summary>
		public void Detach() => _partAttached = false;

		/// <summary>
		/// Whether this body is a part that joins the parent's solve only on the application's word.
		/// </summary>
		internal bool JoinsSolveByAttachOnly => _parentBody != null && _assemblyMode && _attachManually;

		/// <summary>
		/// Puts a freshly registered part into the state the application asked for. The tracker's link of a body starts out
		/// contributing, so a part that waits for <see cref="Attach"/> has to be taken out of the solve before the first step
		/// that includes it and not after the first frame the manager processes it in. The state sent to the previous link
		/// of this body is gone with that link, so the memory of it is cleared whatever the mode is.
		/// </summary>
		/// <remarks>It runs while the registration is published, which is under the native lock.</remarks>
		private void ApplyContributionAtRegistration()
		{
			_lastContributionState = null;
			if (JoinsSolveByAttachOnly)
				SetContributesToOptimization(_partAttached);
			// A new link starts out not settling, so only a part that asks to settle is written to here.
			_lastSettlingState = false;
			_settling = false;
			_presenceJudge.Reset();
			if (WantsToSettle)
				ApplySettling();
		}

		/// <summary>
		/// Whether the part settles its own pose around its seat while it is not attached. It is read only by a part with
		/// <see cref="AssemblyMode"/> and <see cref="AttachManually"/> and no <see cref="TrackedMotion"/>. While the part is
		/// not attached, it settles a few millimetres and degrees around its seat from its own evidence, the parent's solve
		/// never sees it, and the part is scored where it settled. Once attached, the part is rigid at its seat again and
		/// joins the parent's solve. A plugin that cannot settle a part leaves the part at its seat, which is the check
		/// without this setting.
		/// </summary>
		public bool FreeCheck
		{
			get => _freeCheck;
			set => _freeCheck = value;
		}

		/// <summary>How far, in metres, the centre of the part may settle from its seat and still be in its seat.</summary>
		public float FreeCheckDistanceTolerance
		{
			get => _freeCheckDistanceTolerance;
			set => _freeCheckDistanceTolerance = Mathf.Max(0.0001f, value);
		}

		/// <summary>How far, in degrees, the part may turn from its seat and still be in its seat.</summary>
		public float FreeCheckAngleTolerance
		{
			get => _freeCheckAngleTolerance;
			set => _freeCheckAngleTolerance = Mathf.Clamp(value, 0.1f, 30f);
		}

		/// <summary>
		/// The camera pose the part is checked from, or null. It is a transform like the start viewpoint, which looks along
		/// its forward axis, and it is usually a child of the part or of its parent so that it moves with the object. With a
		/// check view, <see cref="Presence"/> judges the part while the camera is near this view and still.
		/// </summary>
		public Transform CheckView
		{
			get => _checkView;
			set => _checkView = value;
		}

		/// <summary>How far, in metres, the camera may be from the check view and still be near it.</summary>
		public float CheckViewDistance
		{
			get => _checkViewDistance;
			set => _checkViewDistance = Mathf.Max(0.001f, value);
		}

		/// <summary>How far, in degrees, the camera's viewing direction may turn from the check view's and still be near it.</summary>
		public float CheckViewAngle
		{
			get => _checkViewAngle;
			set => _checkViewAngle = Mathf.Clamp(value, 1f, 90f);
		}

		/// <summary>How far, in metres, the camera may move over the stillness window and still count as still.</summary>
		public float StillnessDistance
		{
			get => _stillnessDistance;
			set => _stillnessDistance = Mathf.Max(0.0001f, value);
		}

		/// <summary>How far, in degrees, the camera's viewing direction may turn over the stillness window and still count as still.</summary>
		public float StillnessAngle
		{
			get => _stillnessAngle;
			set => _stillnessAngle = Mathf.Clamp(value, 0.1f, 45f);
		}

		/// <summary>How long, in seconds, the camera must stay within the stillness bounds to count as still.</summary>
		public float StillnessWindow
		{
			get => _stillnessWindow;
			set => _stillnessWindow = Mathf.Max(0.05f, value);
		}

		/// <summary>How long, in seconds, the evidence of one judgement is gathered near the check view with the camera still.</summary>
		public float PresenceHold
		{
			get => _presenceHold;
			set => _presenceHold = Mathf.Max(0.1f, value);
		}

		/// <summary>Whether the part is settling around its seat in the plugin now. It is false while the part is attached.</summary>
		public bool IsSettling => _settling;

		/// <summary>How far, in metres, the centre of the part has settled from its seat. It is zero when the part is not settling.</summary>
		public float SettledDistance => _settling ? _settledDistance : 0f;

		/// <summary>How far, in degrees, the part has turned from its seat while settling. It is zero when the part is not settling.</summary>
		public float SettledAngle => _settling ? _settledAngle : 0f;

		/// <summary>
		/// Whether the part is in its seat. A part that is not settling is held at its seat and is in it. A settling part is in
		/// it while it settled no further than <see cref="FreeCheckDistanceTolerance"/> and <see cref="FreeCheckAngleTolerance"/>.
		/// </summary>
		public bool IsInSeat => !_settling ||
			FreeCheckRules.IsInSeat(_settledDistance, _settledAngle, _freeCheckDistanceTolerance, _freeCheckAngleTolerance);

		/// <summary>
		/// The presence reading of the part: <see cref="PartPresence.Unknown"/>, <see cref="PartPresence.Present"/> or
		/// <see cref="PartPresence.Absent"/>. It is judged only with a <see cref="CheckView"/>, only while the camera is near
		/// it and still, and from the evidence of a whole <see cref="PresenceHold"/>. A step of the hold passes when
		/// <see cref="TrackingQuality"/> is at the method's present bar or above it and the part is in its seat. The
		/// per-frame score stays in <see cref="TrackingQuality"/>.
		/// </summary>
		public PartPresence Presence => _presenceJudge.Presence;

		/// <summary>Whether the camera was near the check view on the last processed step.</summary>
		public bool IsAtCheckView => _presenceJudge.IsAtView;

		/// <summary>Whether the camera was still on the last processed step.</summary>
		public bool IsCameraStill => _presenceJudge.IsStill;

		/// <summary>The score <see cref="TrackingQuality"/> must reach for a step of the hold to pass, by tracking method.</summary>
		internal float PresenceBar => _trackingMethod == TrackingMethod.Edge
			? TrackerDefaults.EDGE_NICE_QUALITY_THRESHOLD
			: TrackerDefaults.NICE_QUALITY_THRESHOLD;

		/// <summary>The writer of the free check into the plugin. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, bool, float, float, float, float, NativeCallOutcome> SettlingWriter = FTBridge.TrySetChildSettling;

		/// <summary>The reader of the part's offset from its seat. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, float[], NativeCallOutcome> SeatOffsetReader = FTBridge.TryGetChildSeatOffset;

		/// <summary>Whether this part asks to settle now.</summary>
		internal bool WantsToSettle => JoinsSolveByAttachOnly && _freeCheck && !_partAttached && _trackedMotion == TrackedMotion.None;

		/// <summary>
		/// Sends the free check to the plugin when what the part asks for has changed since the last write, and reads where
		/// the part settled. A part the plugin cannot settle is checked at its seat.
		/// </summary>
		internal void ApplySettling()
		{
			if (!_isRegistered)
			{
				_settling = false;
				_lastSettlingState = false;
				return;
			}

			bool wanted = WantsToSettle;
			var tolerances = new Vector2(_freeCheckDistanceTolerance, _freeCheckAngleTolerance);
			bool changed = _lastSettlingState != wanted || (wanted && tolerances != _lastSettlingTolerances);
			if (changed)
			{
				float rotationTolerance = _freeCheckAngleTolerance * Mathf.Deg2Rad;
				NativeCallOutcome outcome = SettlingWriter(_bodyId, wanted,
					rotationTolerance * FreeCheckRules.SEAT_PULL_WIDTH_OF_TOLERANCE,
					_freeCheckDistanceTolerance * FreeCheckRules.SEAT_PULL_WIDTH_OF_TOLERANCE,
					rotationTolerance * FreeCheckRules.SETTLE_LIMIT_OF_TOLERANCE,
					_freeCheckDistanceTolerance * FreeCheckRules.SETTLE_LIMIT_OF_TOLERANCE);
				NativeBodyWriteJournal.RecordCall(_bodyId,
					wanted ? "FT_SetChildSettling(true)" : "FT_SetChildSettling(false)",
					outcome == NativeCallOutcome.Done);
				if (outcome == NativeCallOutcome.Refused && wanted)
					Debug.LogWarning($"[TrackedBody] {_bodyId}: the plugin refused the free check, so the part is checked at its seat");
				// A refused or missing write is not repeated for the same request, so it is not retried on every frame.
				_settling = wanted && outcome == NativeCallOutcome.Done;
				_lastSettlingState = wanted;
				_lastSettlingTolerances = tolerances;
			}

			_settledDistance = 0f;
			_settledAngle = 0f;
			if (_settling && SeatOffsetReader(_bodyId, _seatOffset) == NativeCallOutcome.Done)
				FreeCheckRules.Magnitudes(_seatOffset, out _settledDistance, out _settledAngle);
		}

		/// <summary>
		/// Judges the part's presence after one completed tracking step, from the camera's pose and the check view's pose in
		/// the frame of the part. The camera pose is in the world the body transforms live in, and null when the step has none.
		/// </summary>
		internal void UpdatePresence(float deltaSeconds, Pose? cameraInWorld)
		{
			bool judgeable = _checkView != null && cameraInWorld.HasValue && _isRegistered && _isTracking && enabled
				&& gameObject.activeInHierarchy;
			Pose cameraInPart = judgeable ? PoseInThisFrame(cameraInWorld.Value.position, cameraInWorld.Value.rotation) : default;
			Pose viewInPart = judgeable ? PoseInThisFrame(_checkView.position, _checkView.rotation) : default;
			bool passes = TrackingQuality >= PresenceBar && IsInSeat;
			var settings = new PresenceSettings(_checkViewDistance, _checkViewAngle, _stillnessDistance, _stillnessAngle,
				_stillnessWindow, _presenceHold);
			_presenceJudge.Step(settings, deltaSeconds, judgeable, cameraInPart, viewInPart, passes);
		}

		/// <summary>A world pose in this body's frame, without this body's scale.</summary>
		private Pose PoseInThisFrame(Vector3 position, Quaternion rotation)
		{
			Quaternion inverse = Quaternion.Inverse(transform.rotation);
			return new Pose(inverse * (position - transform.position), inverse * rotation);
		}

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
			_registrationMarginBuffer = new float[QUALITY_BUFFER_SIZE];
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
			_initialPose = ConversionUtils.GetConvertedPose(cameraRelativePosition, cameraRelativeRotation);
			_initialCameraRelativePosition = cameraRelativePosition;
			_initialCameraRelativeRotation = cameraRelativeRotation;
		}

		private void ComputeInitialPose()
		{
			Transform viewpoint = _initialPoseSource == InitialPoseSource.Viewpoint && _initialViewpoint != null
				? _initialViewpoint
				: CameraTransform;

			ViewpointFromBody(viewpoint, transform.position, transform.rotation,
				out Vector3 viewpointFromBodyPosition, out Quaternion viewpointFromBodyRotation);
			StoreInitialPose(viewpointFromBodyPosition, viewpointFromBodyRotation);

			var manager = XRTrackerManager.Instance;
			if (manager != null && manager.UseARPoseFusion)
			{
				ResolveFusionSeat(
					_initialPoseSource == InitialPoseSource.Viewpoint,
					manager.CurrentImageSource == ImageSource.Sequence,
					CameraTransform,
					transform.position, transform.rotation,
					viewpointFromBodyPosition, viewpointFromBodyRotation,
					out Vector3 seatPosition, out Quaternion seatRotation);
				LatchRetainedInitializationPose(seatPosition, seatRotation,
					NativeBodyWrite.CADENCE_INITIAL_POSE);
			}
			else
			{
				// Without fusion the native tracking frame is the authored viewpoint/camera frame.
				// Retaining the raw Unity-world transform here overwrites the correct initial pose.
				_retainedInitializationPose.Latch(
					Matrix4x4.TRS(
						viewpointFromBodyPosition,
						viewpointFromBodyRotation,
						Vector3.one),
					RetainedInitializationPose.WORLD_SPACE_KEY,
					Matrix4x4.identity);
				NativeBodyWriteJournal.RecordRetainedPoseLatch(_bodyId,
					NativeBodyWrite.CADENCE_INITIAL_POSE,
					viewpointFromBodyPosition, viewpointFromBodyRotation);
				_hasAppliedDetectorPose = false;
				_seatDisplayHeld = false;
			}
		}

		/// <summary>
		/// The pose of a body seen from a viewpoint, which is how the initial pose is stored. The position is divided by the scale of
		/// the viewpoint, as <see cref="Transform.InverseTransformPoint(Vector3)"/> does, and the rotation does not depend on it.
		/// </summary>
		internal static void ViewpointFromBody(Transform viewpoint, Vector3 bodyPosition, Quaternion bodyRotation,
			out Vector3 position, out Quaternion rotation)
		{
			position = viewpoint.InverseTransformPoint(bodyPosition);
			rotation = Quaternion.Inverse(viewpoint.rotation) * bodyRotation;
		}

		/// <summary>
		/// The world pose a viewpoint has to stand at for <see cref="ViewpointFromBody"/> to return the given pose for a body that
		/// stands at the given world pose. It is the inverse of that formula, so the scale of the viewpoint multiplies the position
		/// back in, and the scale itself is not changed.
		/// </summary>
		/// <param name="bodyPosition">The world position of the body.</param>
		/// <param name="bodyRotation">The world rotation of the body.</param>
		/// <param name="viewpointFromBodyPosition">The position of the body seen from the viewpoint.</param>
		/// <param name="viewpointFromBodyRotation">The rotation of the body seen from the viewpoint.</param>
		/// <param name="viewpointScale">The world scale of the viewpoint, which is its lossy scale.</param>
		/// <param name="viewpointPosition">The world position the viewpoint has to stand at.</param>
		/// <param name="viewpointRotation">The world rotation the viewpoint has to stand at.</param>
		internal static void ViewpointStandingFor(Vector3 bodyPosition, Quaternion bodyRotation,
			Vector3 viewpointFromBodyPosition, Quaternion viewpointFromBodyRotation, Vector3 viewpointScale,
			out Vector3 viewpointPosition, out Quaternion viewpointRotation)
		{
			viewpointRotation = bodyRotation * Quaternion.Inverse(viewpointFromBodyRotation);
			viewpointPosition = bodyPosition -
				viewpointRotation * Vector3.Scale(viewpointScale, viewpointFromBodyPosition);
		}

		/// <summary>
		/// The world pose a body is seated at under pose fusion. A sequence source keeps the body's own
		/// world transform, because the fused camera driver moves the scene so the viewpoint lands on the
		/// camera. A live camera has no such driver, so a body that starts from its viewpoint is seated
		/// where that viewpoint sees it from the camera.
		/// </summary>
		internal static void ResolveFusionSeat(bool viewpointSource, bool sequenceSource, Transform camera,
			Vector3 bodyPosition, Quaternion bodyRotation,
			Vector3 viewpointFromBodyPosition, Quaternion viewpointFromBodyRotation,
			out Vector3 seatPosition, out Quaternion seatRotation)
		{
			if (viewpointSource && !sequenceSource && camera != null)
			{
				seatPosition = camera.TransformPoint(viewpointFromBodyPosition);
				seatRotation = camera.rotation * viewpointFromBodyRotation;
				return;
			}

			seatPosition = bodyPosition;
			seatRotation = bodyRotation;
		}

		/// <summary>Whether a guide pose has been kept for this body.</summary>
		internal bool HasGuide => _hasGuide;

		/// <summary>Keeps a pose as the guide this body returns to.</summary>
		internal void SetGuide(Vector3 worldPosition, Quaternion worldRotation)
		{
			if (!ConversionUtils.IsUsablePose(worldPosition, worldRotation))
				return;
			_guidePosition = worldPosition;
			_guideRotation = worldRotation;
			_hasGuide = true;
		}

		/// <summary>The guide pose, in the form the retained initialization pose is read in.</summary>
		internal bool TryGetGuide(out Vector3 position, out Quaternion rotation)
		{
			position = _guidePosition;
			rotation = _guideRotation;
			return _hasGuide;
		}

		/// <summary>
		/// Keeps the pose the body is held at as its guide, once, before its first start. After that the retained
		/// pose is free to follow the body, and the guide is where it started.
		/// </summary>
		private void KeepGuideBeforeFirstStart()
		{
			if (_startedSinceRegistration)
				return;
			if (TryGetRetainedInitializationWorldPose(out Vector3 position, out Quaternion rotation))
				SetGuide(position, rotation);
		}

		/// <summary>
		/// Latches the guide pose again as the pose this body is held at, so it is displayed there and the next step
		/// is published from there. Does nothing when no guide was kept.
		/// </summary>
		/// <returns>Whether the guide was latched.</returns>
		internal bool ReturnToGuide()
		{
			if (!_hasGuide || !ConversionUtils.IsUsablePose(_guidePosition, _guideRotation))
				return false;
			LatchRetainedInitializationPose(_guidePosition, _guideRotation, NativeBodyWrite.CADENCE_GUIDE);
			return true;
		}

		/// <summary>
		/// Replaces the pose this body is held at while it is not tracking. The next
		/// before-step publishes it to the tracker like any retained pose.
		/// </summary>
		internal void AdoptRetainedInitializationPose(Vector3 worldPosition, Quaternion worldRotation)
		{
			if (!ConversionUtils.IsUsablePose(worldPosition, worldRotation))
			{
				RecordRejectedPose();
				return;
			}
			LatchRetainedInitializationPose(worldPosition, worldRotation,
				NativeBodyWrite.CADENCE_ADOPTED);
		}

		/// <summary>
		/// Carries every pose this body holds in the world frame to the new frame, after the AR session moved its world between two steps. The pose of
		/// the body relative to the camera is the same on both sides of the jump, so each pose goes through the same map as the camera.
		/// </summary>
		/// <remarks>
		/// <para>These are the poses carried. The retained initialization pose (the seat), the guide, the pose a recovery seat replaced, the pose
		/// last drawn from the retained pose, and the pose of the transform of a body without a parent, so that the body is not drawn behind the
		/// camera for the rest of the frame.</para>
		/// <para>The settled start and the start search hold poses of the old frame that cannot be carried, because the plugin keeps the guide it was
		/// sent and the refinement that was carried from the step before. They are reset as a replaced seat resets them, and the search is asked for
		/// again around the carried seat on the next step.</para>
		/// </remarks>
		internal void CarryWorld(Pose carry)
		{
			if (_retainedInitializationPose.HasPose && _retainedInitializationPose.IsInSpace(RetainedInitializationPose.WORLD_SPACE_KEY))
			{
				Matrix4x4 seat = ArWorldJump.Carry(carry, _retainedInitializationPose.SpaceFromBody);
				_retainedInitializationPose.Latch(seat, RetainedInitializationPose.WORLD_SPACE_KEY, Matrix4x4.identity);
				NativeBodyWriteJournal.RecordRetainedPoseLatch(_bodyId, NativeBodyWrite.CADENCE_WORLD_CARRY, seat.GetPosition(), seat.rotation);
			}
			_guidePosition = ArWorldJump.Carry(carry, _guidePosition);
			_guideRotation = ArWorldJump.Carry(carry, _guideRotation);
			_seatDisplayPosition = ArWorldJump.Carry(carry, _seatDisplayPosition);
			_seatDisplayRotation = ArWorldJump.Carry(carry, _seatDisplayRotation);
			_lastAppliedDetectorPosition = ArWorldJump.Carry(carry, _lastAppliedDetectorPosition);
			_lastAppliedDetectorRotation = ArWorldJump.Carry(carry, _lastAppliedDetectorRotation);
			// A child stands under its parent in the hierarchy, so it has moved with the transform of its parent already.
			if (!HasParent)
				SetWorldPose(ArWorldJump.Carry(carry, transform.position), ArWorldJump.Carry(carry, transform.rotation), _lastPoseProvenance);
			NoteRetainedPoseReplaced();
		}

		/// <summary>The pose this body is held at while not tracking, in world space.</summary>
		internal bool TryGetRetainedInitializationWorldPose(out Vector3 position, out Quaternion rotation)
		{
			position = Vector3.zero;
			rotation = Quaternion.identity;
			if (!_retainedInitializationPose.HasPose)
				return false;
			Matrix4x4 worldFromBody = _retainedInitializationPose.WorldFromBody(Matrix4x4.identity);
			position = worldFromBody.GetPosition();
			rotation = worldFromBody.rotation;
			return ConversionUtils.IsUsablePose(position, rotation);
		}

		/// <summary>
		/// Records a world-space pose as the pose this body is initialized at.
		/// </summary>
		private void LatchRetainedInitializationPose(Vector3 worldPosition, Quaternion worldRotation,
			string cadence)
		{
			_retainedInitializationPose.Latch(
				Matrix4x4.TRS(worldPosition, worldRotation, Vector3.one),
				RetainedInitializationPose.WORLD_SPACE_KEY,
				Matrix4x4.identity);
			NativeBodyWriteJournal.RecordRetainedPoseLatch(_bodyId, cadence,
				worldPosition, worldRotation);
			_hasAppliedDetectorPose = false;
			_seatDisplayHeld = false;
			NoteRetainedPoseReplaced();
		}

		/// <summary>Whether the body is drawn at the pose a pending recovery seat replaced, and not at the retained pose.</summary>
		internal bool SeatDisplayHeld => _seatDisplayHeld;

		/// <summary>
		/// Draws the given world pose in place of the retained pose until the window of the seat ends. It is called after the
		/// seat has been written to the retained pose, because writing the retained pose ends a hold. The retained pose and
		/// what is published to the tracker are untouched.
		/// </summary>
		/// <returns>False when the pose is not usable and nothing is held.</returns>
		internal bool HoldDisplayAt(Vector3 worldPosition, Quaternion worldRotation)
		{
			if (!ConversionUtils.IsUsablePose(worldPosition, worldRotation))
				return false;
			_seatDisplayHeld = true;
			_seatDisplayPosition = worldPosition;
			_seatDisplayRotation = worldRotation;
			return true;
		}

		/// <summary>Ends the hold of the drawn pose, so the retained pose is drawn again.</summary>
		internal void ClearSeatDisplayHold()
		{
			_seatDisplayHeld = false;
		}

		/// <summary>
		/// The pose a decision reads as the pose of this body when it has nothing better. While a seat holds the display it
		/// is the retained pose, which is what the transform would read without the hold, and otherwise the transform.
		/// </summary>
		internal void ReadWorldPoseForDecision(out Vector3 position, out Quaternion rotation)
		{
			if (_seatDisplayHeld && TryGetRetainedInitializationWorldPose(out position, out rotation))
				return;
			position = transform.position;
			rotation = transform.rotation;
		}

		/// <summary>
		/// Publishes the retained initialization pose to the native tracker. Called only from
		/// <see cref="BeforeTrackingStep"/>, so every native pose write still happens on the main
		/// thread with the native lock held and before the step that reads it.
		/// </summary>
		private bool PushRetainedInitializationPose()
		{
			if (!_isRegistered || !_retainedInitializationPose.HasPose)
				return false;
#if HAS_AR_FOUNDATION
			var manager = XRTrackerManager.Instance;
			if (manager != null && manager.UseARPoseFusion)
			{
				// Under pose fusion the pose is published with the camera pose of the step it
				// belongs to, so it is withheld on a step that has no valid camera frame.
				if (!manager.TryGetSubmittedFusionFrameContext(out FusionFrameContext context) ||
					!context.IsValid)
				{
					NativeBodyWriteJournal.RecordWithheld(_bodyId, "FT_SetDetectorPose", "no frame");
					return false;
				}
			}
#endif
			Matrix4x4 spaceFromBody = _retainedInitializationPose.SpaceFromBody;
			// The check above establishes the pose is in the right frame; this one
			// establishes it is a pose at all. Published unchecked it would reach the
			// reset pose and the live pose in the same call, so a reset could not
			// recover from it.
			if (!ConversionUtils.IsUsablePose(spaceFromBody.GetPosition(), spaceFromBody.rotation))
			{
				NativeBodyWriteJournal.RecordWithheld(_bodyId, "FT_SetDetectorPose",
					"not a pose");
				RecordRejectedPose();
				return false;
			}

			var pose = ConversionUtils.GetConvertedPose(spaceFromBody);
			const string space = "World";
			Vector3 written = spaceFromBody.GetPosition();
			Quaternion writtenRotation = spaceFromBody.rotation;
			// Both writes are recorded, and neither short-circuits the other: a frame
			// that publishes the detector pose but not the body pose is exactly the
			// partial submission the guard exists to catch. Each result is taken into
			// a local first, so the record carries what the plugin actually returned
			// and not only the guard's verdict on it.
			int detectorStatus = FTBridge.FT_SetDetectorPose(_bodyId, ref pose);
			NativeBodyWriteJournal.RecordPose(_bodyId, "FT_SetDetectorPose", space,
				written, writtenRotation,
				FrameWriteGuard.Record(detectorStatus, "FT_SetDetectorPose"),
				detectorStatus);
			int bodyStatus = FTBridge.FT_SetBodyPose(_bodyId, ref pose);
			NativeBodyWriteJournal.RecordPose(_bodyId, "FT_SetBodyPose", space,
				written, writtenRotation,
				FrameWriteGuard.Record(bodyStatus, "FT_SetBodyPose"),
				bodyStatus);
			return true;
		}

		/// <summary>
		/// Publishes the pose the refinement of this step starts from. It is the retained initialization pose, which is the guide,
		/// unless the search has put the guide out of reach of its winner and has given a seed, and then it is the seed.
		/// </summary>
		private bool PublishRefinementSeat()
		{
			_startSearchSeedPublished = false;
			if (StartSearchFarPath && _startSearchSeedValid)
			{
				_startSearchSeedPublished = PushStartSearchSeed();
				return _startSearchSeedPublished;
			}
			return PushRetainedInitializationPose();
		}

		/// <summary>
		/// Publishes the seed the search gave to the native tracker as the pose of the body. Only the body's pose is written, so the
		/// detector pose, which a reset returns to, stays at the guide. Called only from <see cref="BeforeTrackingStep"/>.
		/// </summary>
		private bool PushStartSearchSeed()
		{
			if (!_isRegistered || !_startSearchSeedValid)
				return false;
#if HAS_AR_FOUNDATION
			var manager = XRTrackerManager.Instance;
			if (manager != null && manager.UseARPoseFusion)
			{
				if (!manager.TryGetSubmittedFusionFrameContext(out FusionFrameContext context) || !context.IsValid)
				{
					NativeBodyWriteJournal.RecordWithheld(_bodyId, "FT_SetBodyPose", "no frame");
					return false;
				}
			}
#endif
			FTTrackingPose pose = _startSearchSeed;
			if (!pose.TryGetTransformation(out Vector3 position, out Quaternion rotation) ||
				!ConversionUtils.IsUsablePose(position, rotation))
			{
				NativeBodyWriteJournal.RecordWithheld(_bodyId, "FT_SetBodyPose", "not a pose");
				return false;
			}
			int bodyStatus = FTBridge.FT_SetBodyPose(_bodyId, ref pose);
			NativeBodyWriteJournal.RecordPose(_bodyId, "FT_SetBodyPose", "World", position, rotation,
				FrameWriteGuard.Record(bodyStatus, "FT_SetBodyPose"), bodyStatus);
			return true;
		}

		/// <summary>Whether an initialization pose has been latched for this body.</summary>
		internal bool HasRetainedInitializationPose => _retainedInitializationPose.HasPose;

		/// <summary>Tracking space the retained initialization pose is expressed in.</summary>
		internal int RetainedInitializationSpaceKey => _retainedInitializationPose.SpaceKey;

		/// <summary>The retained initialization pose, in its own tracking space.</summary>
		internal Matrix4x4 RetainedInitializationSpaceFromBody => _retainedInitializationPose.SpaceFromBody;

		private void OnEnable()
		{
			_lifecycleVersion++;

			// If already registered (for example, re-enabled before a pending unregister completed),
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
			// The next enable re-latches from the authored transform, so nothing written by the
			// previous session should be mistaken for a pose someone set from outside.
			_hasAppliedDetectorPose = false;
			_seatDisplayHeld = false;
			UnregisterBody();
		}

		/// <summary>Records the shared custom bars this body holds, so that data this release writes is never read as a bar the body has not seen.</summary>
		void ISerializationCallbackReceiver.OnBeforeSerialize()
		{
			_sharedStartBarSeen = SharedBarRecord(_useCustomStartThreshold, _customQualityToStart);
			_sharedStopBarSeen = SharedBarRecord(_useCustomStopThreshold, _customQualityToStop);
		}

		/// <summary>Carries a shared custom bar that is new to a body that tracks by the silhouette method over to the bar of that method.</summary>
		void ISerializationCallbackReceiver.OnAfterDeserialize()
		{
			CarrySharedBarsToSilhouette();
		}

		private static int SharedBarRecord(bool useCustom, float bar) =>
			useCustom ? 1 + (int)(Mathf.Clamp01(bar) * 10000f + 0.5f) : 0;

		/// <summary>
		/// Migrates data saved when one pair of custom bars served both methods. That pair now belongs to the edge method. A body
		/// that tracks by the silhouette method keeps a custom bar it saved by carrying the shared bar over to its own pair, which
		/// happens when the shared bar differs from the one the body last saw. Data saved before the pairs were split has seen no
		/// bar, so a custom bar in it is carried over once. An instance of a prefab that switches a shared bar on, off or to another value
		/// differs from the prefab it was made from, so the instance is carried over the same way after the change is applied to it.
		/// </summary>
		/// <remarks>
		/// Reading the data again finds the record equal to the shared bar and changes nothing. A bar that is already on in the
		/// silhouette pair is not overwritten when the shared bar is new, an edge body keeps every value it holds, and the
		/// body never sets itself dirty, so a file is written only when the user saves. Plain field arithmetic only,
		/// because Unity may call this from any thread while it loads.
		/// </remarks>
		private void CarrySharedBarsToSilhouette()
		{
			int start = SharedBarRecord(_useCustomStartThreshold, _customQualityToStart);
			int stop = SharedBarRecord(_useCustomStopThreshold, _customQualityToStop);

			if (_trackingMethod == TrackingMethod.Silhouette)
			{
				if (start != _sharedStartBarSeen)
					CarryBar(start, _sharedStartBarSeen, _customQualityToStart,
						ref _useCustomSilhouetteStartThreshold, ref _customSilhouetteQualityToStart);
				if (stop != _sharedStopBarSeen)
					CarryBar(stop, _sharedStopBarSeen, _customQualityToStop,
						ref _useCustomSilhouetteStopThreshold, ref _customSilhouetteQualityToStop);
			}

			_sharedStartBarSeen = start;
			_sharedStopBarSeen = stop;
		}

		private static void CarryBar(int shared, int seen, float sharedBar, ref bool useOwn, ref float ownBar)
		{
			if (shared == 0)
			{
				useOwn = false;
				return;
			}

			if (seen == 0 && useOwn)
				return;

			useOwn = true;
			ownBar = sharedBar;
		}

		private void OnValidate()
		{
#if UNITY_EDITOR
			if (Application.isPlaying && _isRegistered)
			{
				UpdateStabilityParameters();
				UpdateEdgeTrackingParameters();
				UpdateDepthTrackingParameters();
			}
#endif
		}

		/// <summary>
		/// Called before each native tracking step.
		/// While the body is not yet tracking, republishes the retained initialization pose so the
		/// startup decision is taken at the pose the body was placed at. The pose is held still:
		/// it does not follow the camera. Under the default nothing refines it before tracking
		/// starts. Under <see cref="TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAccept"/>
		/// the tracker refines each step from it, and the start decision reads the refined record.
		/// </summary>
		internal virtual void BeforeTrackingStep()
		{
			if (_detectionApplyPending)
				ApplyDetectionSettingsToNative();

			if (_depthApplyPending)
				ApplyDepthSettingsToNative();

			if (_detectorPoseWritePending)
				ApplyInitialPoseToNative();

			if (_isTracking)
			{
				ClearStartSearch();
				_startSearchFresh = false;
				return;
			}

			// A body that tracks by the silhouette method takes the plain detection phase. It is not
			// refined before its start, its seat is not republished from a retained pose, and no walk, recovery
			// seat or gizmo move is taken up. Without pose fusion the tracker keeps the pose the body was
			// registered at, and with it the guide follows the camera.
			if (!_enableEdgeTracking)
			{
				FollowCameraWhileSearching();
				return;
			}

			EndRecoverySeatRefinementIfDone();
			bool movedFromOutside = AdoptStandingExternalPoseEdit();
			FollowCameraBeforeFirstStart(movedFromOutside);
			NoteSearchWalkScope();
			bool walkEngaged = SearchWalkEngaged(out _, out _);
			bool walkCarries = SearchWalk.CarriesNextStep(walkEngaged, _refiningBeforeAcceptance, _searchWalk);
			bool settleCarries = !walkEngaged && _settleCarryReady && StartSettleApplies;
			if (settleCarries)
			{
				RestartRefinementAtCarriedPose();
			}
			else if (!walkCarries)
			{
				bool published = PublishRefinementSeat();
				if (walkEngaged)
					_searchWalk = SearchWalk.Begin(_searchWalk);
				// A refinement from the guide that is far from the object is not carried while the search has not given a seed.
				_settleCarryReady = published && !walkEngaged && StartSettleEffective.On &&
					(!StartSearchFarPath || _startSearchSeedPublished);
			}
			ReanchorStartSearch();
			ApplyStartSearch();
			BeginRefinementBeforeAcceptanceIfAsked();
		}

		/// <summary>
		/// Whether the tooling's limited search walk is engaged for this body right now, and the limits it runs at.
		/// </summary>
		private bool SearchWalkEngaged(out int maxSteps, out float maxPixels)
		{
			maxSteps = 0;
			maxPixels = 0f;
			if (!_isRegistered || HasParent || !_automaticStartEnabled)
				return false;
			bool shipped = ShippedDefaults.AppliesTo(this);
			return SearchWalk.IsEngaged(ShippedDefaults.SearchWalkLimits(shipped),
					ShippedDefaults.HoldStartPose(shipped),
					ShippedDefaults.PoseFusionActive(),
					RefinesBeforeItsStart, out maxSteps, out maxPixels) &&
				SearchWalk.InScope(ShippedDefaults.RecoveryWalk(shipped),
					_startedSinceRegistration, RecoverySeatRefineRequested);
		}

		/// <summary>
		/// Begins the walk again when the scope it runs in has changed, so a recovery seat is published to the tracker on
		/// the row it starts and the held search starts again from the pose the body holds once a seat has ended. With
		/// the recovery walk off the scope never changes, so nothing happens.
		/// </summary>
		private void NoteSearchWalkScope()
		{
			int scope = SearchWalk.ScopeOf(ShippedDefaults.RecoveryWalk(ShippedDefaults.AppliesTo(this)),
				_startedSinceRegistration, RecoverySeatRefineRequested);
			if (scope == _searchWalkScope)
				return;
			_searchWalkScope = scope;
			_searchWalk = SearchWalk.Restart(_searchWalk);
		}

		/// <summary>
		/// Begins the walk again because the pose it started from, the retained pose, was replaced. Only while a recovery
		/// walk is on, so the walk of the first start is not affected.
		/// </summary>
		private void NoteRetainedPoseReplaced()
		{
			ResetStartSettle();
			++_startSearchGeneration;
			ResetStartSearch();
			if (ShippedDefaults.RecoveryWalk(ShippedDefaults.AppliesTo(this)))
				_searchWalk = SearchWalk.Restart(_searchWalk);
		}

		/// <summary>
		/// Judges the search step that has just run against the limits of the walk, and counts it.
		/// Called by <see cref="TrackedBodyManager"/> after a step of a body that is not tracking, before the
		/// start decision. Off when no walk is engaged, which leaves the step judged by the ordinary rules.
		/// </summary>
		internal SearchWalkVerdict JudgeSearchWalk()
		{
			if (_isTracking || !_refiningBeforeAcceptance)
				return SearchWalkVerdict.Off;
			if (!SearchWalkEngaged(out int maxSteps, out float maxPixels))
				return SearchWalkVerdict.Off;
			_searchWalk.Steps++;
			_searchWalk.Pixels = MeasureSearchWalkPixels();
			SearchWalkVerdict verdict = SearchWalk.Judge(_searchWalk.Steps, _searchWalk.Pixels, maxSteps, maxPixels);
			_walkJudgedSinceTaken = true;
			_judgedWalkVerdict = verdict;
			_judgedWalkSteps = _searchWalk.Steps;
			_judgedWalkPixels = _searchWalk.Pixels;
			_judgedWalkRestarts = _searchWalk.Restarts;
			_judgedWalkScope = _searchWalkScope;
			return verdict;
		}

		/// <summary>
		/// What the walk read on the step it judged since the last call, for the diagnostic record. False when no step of a
		/// walk was judged since then. The step on which the body started is judged before the start is decided, so a
		/// start reads the walk it started from. The scope is the one the walk ran in (<see cref="SearchWalk.SCOPE_HELD"/>
		/// for a lost body with no seat on its window, <see cref="SearchWalk.SCOPE_SEAT"/> inside a seat's window, and
		/// <see cref="SearchWalk.SCOPE_UNSCOPED"/> when the recovery walk is off).
		/// </summary>
		internal bool TryTakeJudgedWalk(out SearchWalkVerdict verdict, out int steps, out float pixels, out int restarts,
			out int scope)
		{
			verdict = _judgedWalkVerdict;
			steps = _judgedWalkSteps;
			pixels = _judgedWalkPixels;
			restarts = _judgedWalkRestarts;
			scope = _judgedWalkScope;
			bool judged = _walkJudgedSinceTaken;
			_walkJudgedSinceTaken = false;
			return judged;
		}

		/// <summary>
		/// Ends the walk without a start, so the next step starts again from the placed start pose.
		/// </summary>
		internal void SnapSearchWalkBack()
		{
			_searchWalk.SnapBackDue = true;
		}

		/// <summary>
		/// Where the limited search walk stands, for a body that is not tracking and has a walk engaged.
		/// </summary>
		internal bool TryGetSearchWalk(out int steps, out int maxSteps, out float pixels, out float maxPixels,
			out int restarts)
		{
			steps = _searchWalk.Steps;
			pixels = _searchWalk.Pixels;
			restarts = _searchWalk.Restarts;
			if (_isTracking || !SearchWalkEngaged(out maxSteps, out maxPixels))
			{
				maxSteps = 0;
				maxPixels = 0f;
				return false;
			}
			return true;
		}

		/// <summary>
		/// The median on-screen displacement, in pixels, of the pose the tracker holds now from the placed start
		/// pose. Not a number when it cannot be measured.
		/// </summary>
		/// <remarks>
		/// Both poses are taken in the camera frame as the tracker has them, the placed one through the same
		/// conversion that publishes it, so the two share a convention. The points are a sample of the model.
		/// </remarks>
		private float MeasureSearchWalkPixels()
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !_retainedInitializationPose.HasPose ||
				!FTBridge.FT_GetBodyPose(_bodyId, out FTTrackingPose carried))
				return float.NaN;
			if (!carried.TryGetTransformation(out Vector3 carriedPosition, out Quaternion carriedRotation))
				return float.PositiveInfinity;
			FTTrackingPose placed = ConversionUtils.GetConvertedPose(_retainedInitializationPose.SpaceFromBody);
			if (!placed.TryGetTransformation(out Vector3 placedPosition, out Quaternion placedRotation))
				return float.NaN;
			EnsureSearchWalkPoints();
			return SearchWalk.MedianDisplacementPx(_searchWalkPoints, _searchWalkPoints.Length,
				placedPosition, placedRotation, carriedPosition, carriedRotation,
				manager.CachedFu, manager.CachedFv, _searchWalkScratch);
		}

		/// <summary>
		/// Whether the points that on-screen distances are measured with have been sampled. They are sampled by the first measurement, from the pose the body
		/// stands at then, so a caller that only reads the readout asks this first and leaves the sampling to the measurements that decide.
		/// </summary>
		internal bool SearchWalkPointsSampled => _searchWalkPoints != null;

		/// <summary>
		/// Samples the model's vertices once, in the body's own frame, so the same points can be placed at any
		/// pose. A body with no readable mesh is measured at its origin.
		/// </summary>
		private void EnsureSearchWalkPoints()
		{
			if (_searchWalkPoints != null)
				return;

			var all = new List<Vector3>();
			Quaternion worldToBody = Quaternion.Inverse(transform.rotation);
			Vector3 origin = transform.position;
			foreach (MeshFilter filter in _meshFilters)
			{
				if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable)
					continue;
				Matrix4x4 worldFromMesh = filter.transform.localToWorldMatrix;
				foreach (Vector3 vertex in filter.sharedMesh.vertices)
					all.Add(worldToBody * (worldFromMesh.MultiplyPoint3x4(vertex) - origin));
			}

			if (all.Count == 0)
				all.Add(Vector3.zero);
			Vector3 low = all[0];
			Vector3 high = all[0];
			for (int i = 1; i < all.Count; ++i)
			{
				low = Vector3.Min(low, all[i]);
				high = Vector3.Max(high, all[i]);
			}
			_boundsMotionPoints = SearchWalk.BoundsMotionPoints(low, high, TrackerDefaults.FAST_MOTION_HOLD_MAX_HALF_EXTENT_METRES);
			int count = Mathf.Min(SEARCH_WALK_MAX_POINTS, all.Count);
			_searchWalkPoints = new Vector3[count];
			for (int i = 0; i < count; ++i)
				_searchWalkPoints[i] = all[(int)((long)i * all.Count / count)];
			_searchWalkScratch = new float[count];
		}

		/// <summary>
		/// Set by the manager while a recovery candidate is seated on this body, the tooling has asked for the seat to
		/// be judged on its refined pose, and the body is not tracking. It makes the tracker refine the seat on the
		/// incoming rows. It is never set for the guide, for the start path, or without the tooling's request.
		/// </summary>
		internal bool RecoverySeatRefineRequested { get; set; }

		/// <summary>
		/// Whether the tracker should be asked to refine a recovery seat now. Only when it is asked for, for a
		/// registered body with no parent that is not tracking and is not being refined already.
		/// </summary>
		internal static bool BeginsRecoverySeatRefinement(bool requested, bool isRegistered, bool isTracking,
			bool hasParent, bool alreadyRefining) =>
			requested && isRegistered && !isTracking && !hasParent && !alreadyRefining;

		/// <summary>
		/// Stops a refinement that was begun for a recovery seat once the seat is no longer pending, so the guide or the
		/// held search goes on unrefined as it did before the seat. A refinement the start path began is not touched.
		/// </summary>
		private void EndRecoverySeatRefinementIfDone()
		{
			if (!_recoveryRefining || RecoverySeatRefineRequested)
				return;
			FTBridge.FT_StopTrackingBody(_bodyId);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_StopTrackingBody", true);
			_refiningBeforeAcceptance = false;
			_recoveryRefining = false;
		}

		private void BeginRefinementBeforeAcceptanceIfAsked()
		{
			bool forSeat = BeginsRecoverySeatRefinement(RecoverySeatRefineRequested, _isRegistered, _isTracking,
				HasParent, _refiningBeforeAcceptance);
			if (!forSeat && !BeginsRefinementBeforeAcceptance(MarginResidualTerm, _enableEdgeTracking,
				_isRegistered, _isTracking, _automaticStartEnabled, HasParent, _refiningBeforeAcceptance))
				return;
			// The optimizer runs from the seat written just above. The seat is written again before
			// every step while the body is not tracking, so no refinement carries into the next
			// frame and the seat itself never moves. Nothing is accepted here: the body's tracking
			// state stays false until the start decision passes on the refined record.
			FTBridge.FT_StartTrackingBody(_bodyId);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_StartTrackingBody", true);
			_refiningBeforeAcceptance = true;
			if (forSeat)
				_recoveryRefining = true;
		}

		/// <summary>
		/// Whether a body should now ask the tracker to refine it before the lifecycle accepts it.
		/// </summary>
		/// <remarks>
		/// Only for a body that tracks by the edge method, only under a mode for which
		/// <see cref="RefinesBeforeAcceptance(TrackerDefaults.MarginResidualTerm, bool)"/> holds,
		/// only for a registered body that is waiting for the lifecycle to start it, and only once
		/// until something returns the body to its unrefined state. A body with a parent has no
		/// optimizer of its own, and a body the lifecycle may not start is one a diagnostic is
		/// evaluating as placed. A body that tracks by the silhouette method is judged at the pose it is
		/// placed at and never refined before its start.
		/// </remarks>
		internal static bool BeginsRefinementBeforeAcceptance(TrackerDefaults.MarginResidualTerm term,
			bool edgeTracking, bool isRegistered, bool isTracking, bool automaticStart, bool hasParent,
			bool alreadyRefining)
		{
			return RefinesBeforeAcceptance(term, edgeTracking)
				&& isRegistered && !isTracking && automaticStart && !hasParent && !alreadyRefining;
		}

		/// <summary>
		/// Whether <paramref name="term"/> is a mode that refines a body from its held seat before
		/// the start decision.
		/// </summary>
		/// <remarks>
		/// The mode that also guards the stored reference refines exactly as the mode it extends.
		/// </remarks>
		internal static bool RefinesBeforeAcceptance(TrackerDefaults.MarginResidualTerm term) =>
			term == TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAccept ||
			term == TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference;

		/// <summary>
		/// Whether a body that tracks by the given method is refined from its held seat before the start decision under
		/// <paramref name="term"/>. Only a body that tracks by the edge method is. The registration margin and the
		/// refinement before a start are measured on edge sites, and a body that tracks by the silhouette method is
		/// judged at the pose it is placed at.
		/// </summary>
		internal static bool RefinesBeforeAcceptance(TrackerDefaults.MarginResidualTerm term, bool edgeTracking) =>
			edgeTracking && RefinesBeforeAcceptance(term);

		/// <summary>Whether this body is refined from its held seat before the start decision, which is so for an edge body under a refining term.</summary>
		internal bool RefinesBeforeItsStart => RefinesBeforeAcceptance(MarginResidualTerm, _enableEdgeTracking);

		/// <summary>Whether the tracker is refining this body before the lifecycle accepts it.</summary>
		internal bool RefiningBeforeAcceptance => _refiningBeforeAcceptance;

		/// <summary>Whether the body has started tracking at least once since it registered.</summary>
		internal bool StartedSinceRegistration => _startedSinceRegistration;

		/// <summary>
		/// Forgets the request to refine before acceptance. Called wherever the tracker is put back
		/// to judging the seat as placed, so the next frame that is not tracking asks again.
		/// </summary>
		internal void ForgetRefinementBeforeAcceptance()
		{
			_refiningBeforeAcceptance = false;
			_recoveryRefining = false;
			RecoverySeatRefineRequested = false;
			_searchWalk = default;
			ResetStartSettle();
			// A reset may have dropped the search in the plugin, so the next step asks for it again if it is still wanted.
			ResetStartSearch();
			_startSearchRequest.Asked = false;
		}

		/// <summary>
		/// Takes up a pose the body was moved to from outside the package, as the pose it is
		/// initialized at. Runs on the step cadence, immediately before the publication that
		/// carries it, so the tracker is initialized at the pose the body is standing at and
		/// at no other.
		/// </summary>
		/// <remarks>
		/// The display drive leaves such a body where it was put rather than replacing the
		/// retained pose itself, so the move is still standing when this reads it, however many
		/// frames were rendered in between. Taking it up here also refreshes what the display
		/// drive compares against, without which that drive would read the same move as
		/// standing for ever and never drive the transform again.
		/// </remarks>
		/// <returns>Whether a move from outside was taken up on this step.</returns>
		private bool AdoptStandingExternalPoseEdit()
		{
			if (!_hasAppliedDetectorPose || !_retainedInitializationPose.HasPose)
				return false;

			Vector3 position = transform.position;
			Quaternion rotation = transform.rotation;
			if (!WasMovedFromOutside(position, rotation))
				return false;

			_retainedInitializationPose.Latch(
				Matrix4x4.TRS(position, rotation, Vector3.one),
				RetainedInitializationPose.WORLD_SPACE_KEY,
				Matrix4x4.identity);
			NativeBodyWriteJournal.RecordRetainedPoseLatch(_bodyId,
				NativeBodyWrite.CADENCE_BEFORE_STEP, position, rotation);
			_lastAppliedDetectorPosition = position;
			_lastAppliedDetectorRotation = rotation;
			_seatDisplayHeld = false;
			NoteRetainedPoseReplaced();
			return true;
		}

		/// <summary>
		/// Whether the guide of a body follows the camera before its start. Under pose fusion on a live camera, a body that
		/// starts from its viewpoint keeps its guide at the authored pose in front of the camera of every step until it
		/// first starts, so the person aligns the object with an outline that moves with the device. A sequence source keeps the
		/// body's own world transform, and a body that starts from its scene position stays where it was placed.
		/// </summary>
		internal static bool GuideFollowsCamera(bool poseFusion, bool viewpointSource, bool sequenceSource,
			bool hasParent, bool startedSinceRegistration)
		{
			return poseFusion && viewpointSource && !sequenceSource && !hasParent && !startedSinceRegistration;
		}

		/// <summary>
		/// Places the guide at the authored pose in front of the camera of this step, when <see cref="GuideFollowsCamera"/>
		/// says it follows the camera. A move from outside taken up on this step becomes the new pose in front of the camera,
		/// so a guide moved by the application keeps following from where it was put.
		/// </summary>
		private void FollowCameraBeforeFirstStart(bool movedFromOutside)
		{
#if HAS_AR_FOUNDATION
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !_isRegistered)
				return;
			if (!GuideFollowsCamera(manager.UseARPoseFusion, _initialPoseSource == InitialPoseSource.Viewpoint,
					manager.CurrentImageSource == ImageSource.Sequence, HasParent, _startedSinceRegistration))
				return;
			if (!manager.TryGetSubmittedFusionFrameContext(out FusionFrameContext context) || !context.IsValid)
				return;

			Matrix4x4 worldFromCamera = context.WorldFromCameraUnity;
			if (movedFromOutside)
			{
				Matrix4x4 cameraFromBody = worldFromCamera.inverse *
					Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
				_initialCameraRelativePosition = cameraFromBody.GetPosition();
				_initialCameraRelativeRotation = cameraFromBody.rotation;
			}
			GuideInFrontOfCamera(worldFromCamera, _initialCameraRelativePosition,
				_initialCameraRelativeRotation, out Vector3 position, out Quaternion rotation);
			_retainedInitializationPose.Latch(
				Matrix4x4.TRS(position, rotation, Vector3.one),
				RetainedInitializationPose.WORLD_SPACE_KEY,
				Matrix4x4.identity);
#endif
		}

		/// <summary>
		/// Apply the initialization pose directly to the Unity Transform. Called every render
		/// frame by TrackedBodyManager while the body is not tracking, so the displayed pose and
		/// the pose the tracker scores are the same pose.
		/// </summary>
		internal void ApplyDetectorPose()
		{
			if (!_enableEdgeTracking)
			{
				DrawGuideInFrontOfCamera();
				return;
			}
			ApplyRetainedInitializationPose(
				Matrix4x4.identity,
				RetainedInitializationPose.WORLD_SPACE_KEY);
		}

#if HAS_AR_FOUNDATION
		internal void ApplyDetectorPose(FusionFrameContext context)
		{
			if (!context.IsValid)
				return;
			if (!_enableEdgeTracking)
			{
				DrawPublishedGuide();
				return;
			}
			if (DrawGuideFollowingCamera())
				return;
			ApplyRetainedInitializationPose(
				Matrix4x4.identity,
				RetainedInitializationPose.WORLD_SPACE_KEY);
		}

		/// <summary>
		/// Draws a guide that follows the camera at its authored pose in front of the camera as it stands on this rendered
		/// frame, so the outline stays still on screen between tracking steps. The step publishes the same pose in front
		/// of the camera of its own frame.
		/// </summary>
		/// <returns>Whether the guide of this body follows the camera, and so was handled here.</returns>
		private bool DrawGuideFollowingCamera()
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !_isRegistered ||
				!GuideFollowsCamera(manager.UseARPoseFusion, _initialPoseSource == InitialPoseSource.Viewpoint,
					manager.CurrentImageSource == ImageSource.Sequence, HasParent, _startedSinceRegistration))
				return false;
			Transform cam = manager.CameraTransform;
			if (cam == null)
				return false;
			// A move from outside is left standing for the step to take up, as the retained display does.
			if (_hasAppliedDetectorPose && WasMovedFromOutside(transform.position, transform.rotation))
				return true;

			GuideInFrontOfCamera(cam.localToWorldMatrix, _initialCameraRelativePosition,
				_initialCameraRelativeRotation, out Vector3 position, out Quaternion rotation);
			_lastAppliedDetectorPosition = position;
			_lastAppliedDetectorRotation = rotation;
			_hasAppliedDetectorPose = true;
			SetWorldPose(position, rotation);
			return true;
		}
#endif

		/// <summary>
		/// Draws a body that tracks by the silhouette method at its authored pose in front of the camera as the camera stands.
		/// Nothing is taken up from the transform, because the guide of such a body is the authored pose.
		/// </summary>
		private void DrawGuideInFrontOfCamera()
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			Transform cam = manager != null ? manager.CameraTransform : null;
			if (cam == null)
				return;
			GuideInFrontOfCamera(cam.localToWorldMatrix, _initialCameraRelativePosition,
				_initialCameraRelativeRotation, out Vector3 position, out Quaternion rotation);
			SetWorldPose(position, rotation);
		}

		/// <summary>The world pose of the authored camera-relative pose, in front of a camera that stands at <paramref name="worldFromCamera"/>.</summary>
		internal static void GuideInFrontOfCamera(Matrix4x4 worldFromCamera, Vector3 cameraRelativePosition,
			Quaternion cameraRelativeRotation, out Vector3 position, out Quaternion rotation)
		{
			position = worldFromCamera.MultiplyPoint3x4(cameraRelativePosition);
			rotation = worldFromCamera.rotation * cameraRelativeRotation;
		}

		/// <summary>Draws a body that tracks by the silhouette method at the guide it last published to the tracker.</summary>
		private void DrawPublishedGuide()
		{
			if (!_retainedInitializationPose.HasPose)
				return;
			Matrix4x4 worldFromBody = _retainedInitializationPose.WorldFromBody(Matrix4x4.identity);
			SetWorldPose(worldFromBody.GetPosition(), worldFromBody.rotation);
		}

		/// <summary>
		/// The detection phase of a body that tracks by the silhouette method, written before every step while the body is not
		/// tracking. Without pose fusion nothing is written, the tracker keeps the pose the body was registered at and a reset
		/// returns it there. Under pose fusion the guide is the authored pose in front of the camera of this step, so it is
		/// published at that pose on every step. The guide is never refined, walked or replaced by a recovery.
		/// </summary>
		private void FollowCameraWhileSearching()
		{
#if HAS_AR_FOUNDATION
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !manager.UseARPoseFusion || !_isRegistered || HasParent)
				return;
			if (!manager.TryGetSubmittedFusionFrameContext(out FusionFrameContext context) || !context.IsValid)
				return;
			GuideInFrontOfCamera(context.WorldFromCameraUnity, _initialCameraRelativePosition,
				_initialCameraRelativeRotation, out Vector3 position, out Quaternion rotation);
			_retainedInitializationPose.Latch(
				Matrix4x4.TRS(position, rotation, Vector3.one),
				RetainedInitializationPose.WORLD_SPACE_KEY,
				Matrix4x4.identity);
			PushRetainedInitializationPose();
#endif
		}

		/// <summary>
		/// Drives the Unity transform from the retained initialization pose, and adopts a pose
		/// written from outside instead of overwriting it.
		/// </summary>
		private void ApplyRetainedInitializationPose(Matrix4x4 worldFromSpace, int spaceKey)
		{
			if (!_retainedInitializationPose.HasPose ||
				!_retainedInitializationPose.IsInSpace(spaceKey))
			{
				return;
			}

			if (_hasAppliedDetectorPose &&
				WasMovedFromOutside(transform.position, transform.rotation))
			{
				// A scene gizmo, game code or a seat moved the body while it was being quality
				// checked. That is an instruction about where the body is, so the body is left
				// where it was put and the transform is not driven over it.
				//
				// The instruction is taken up in BeforeTrackingStep and not here. This runs
				// while a frame is being rendered, and the pose the tracker is initialized at
				// is published from the step. A replacement made here lands before or after
				// that publication depending on how the two cadences happen to interleave, so
				// the same input could initialize the tracker at two different poses on the
				// step after a reset. Nothing the step reads is written from here.
				return;
			}

			Matrix4x4 worldFromBody = _retainedInitializationPose.WorldFromBody(worldFromSpace);
			// A seat that is on its window leaves the body where it stood. What is written into the last-applied pose is what is
			// drawn, so the drawn pose is never read as a move from outside and taken up as the next retained pose.
			if (_seatDisplayHeld)
			{
				_lastAppliedDetectorPosition = _seatDisplayPosition;
				_lastAppliedDetectorRotation = _seatDisplayRotation;
			}
			else
			{
				_lastAppliedDetectorPosition = worldFromBody.GetPosition();
				_lastAppliedDetectorRotation = worldFromBody.rotation;
			}
			_hasAppliedDetectorPose = true;
			SetWorldPose(_lastAppliedDetectorPosition, _lastAppliedDetectorRotation);
		}

		private bool WasMovedFromOutside(Vector3 position, Quaternion rotation)
		{
			// Both comparisons below are ordered, and an ordered comparison against a
			// value that is not a number is false — so an unusable transform would
			// read as "nobody moved it" and be silently overwritten, while an
			// infinite one would read as a deliberate edit and be latched as the
			// authoritative initialization pose. Neither is an instruction about
			// where the body is.
			if (!ConversionUtils.IsUsablePose(position, rotation))
			{
				RecordRejectedPose();
				return false;
			}

			return Vector3.Distance(position, _lastAppliedDetectorPosition) >
					EXTERNAL_POSE_EDIT_POSITION_EPSILON ||
				Quaternion.Angle(rotation, _lastAppliedDetectorRotation) >
					EXTERNAL_POSE_EDIT_ROTATION_EPSILON_DEGREES;
		}

		/// <summary>
		/// Called after each native tracking step. The base class does nothing, because TrackedBodyManager applies the pose at the right time.
		/// Without SLAM it applies the pose right after the native tracking step. With SLAM it applies the pose before rendering, because it needs the latest AR camera pose.
		/// </summary>
		internal virtual void AfterTrackingStep()
		{
		}

		private void Reset()
		{
			AddChildMeshes();
			_bodyId = gameObject.name;
		}

		#endregion

		#region Public Methods

		/// <summary>
		/// Gives up on the pose the body holds. The body returns to the pose it was placed at, its tracking stops
		/// and its lifecycle starts over. A body that has no placed pose kept only stops tracking and starts its
		/// lifecycle over, and it stays where it is.
		/// </summary>
		/// <param name="forgetLearned">
		/// False keeps what the body learned while it tracked, which is its saved-view bank, the poses it was
		/// accepted at and what its views normally achieve, so a recovery after the reset can still use them. True
		/// forgets all three, so the body starts again with nothing but the pose it was placed at.
		/// </param>
		public void ResetTracking(bool forgetLearned)
		{
			if (!_isRegistered || TrackedBodyManager.Instance == null)
				return;

			TrackedBodyManager.Instance.ResetBody(this, forgetLearned);
		}

		/// <summary>
		/// Gives up on the pose the body holds and keeps what the body learned, as <see cref="ResetTracking(bool)"/> does with
		/// <c>false</c>. This overload takes no parameter, so a <see cref="UnityEngine.Events.UnityEvent"/> wired to it in the
		/// inspector binds.
		/// </summary>
		public void ResetTracking() => ResetTracking(false);

		public void ExecuteDetection()
		{
			if (!_isRegistered)
				return;

			_isTracking = false;
			_refiningBeforeAcceptance = false;
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_ExecuteDetection();
		}

		/// <summary>
		/// Force-start tracking, bypassing the quality-based automatic start.
		/// Useful for trying a tracking method that cannot judge by itself whether the object is present, such as edge tracking.
		/// </summary>
		public void ForceStartTracking()
		{
			// A child is tracked while its parent is, and the tracker has no start of its own for it.
			if (!_isRegistered || _isTracking || HasParent)
				return;

			XRTrackerManager.WaitForTrackingStepToReturn();
			KeepGuideBeforeFirstStart();
			_startedFromSearch = _startSearchRequest.Held && StartSearchFarPath;
			ClearStartSearch();
			_isTracking = true;
			_refiningBeforeAcceptance = false;
			_startedSinceRegistration = true;
			FTBridge.FT_StartTrackingBody(_bodyId);
		}

		/// <summary>
		/// Update stability (regularization) parameters at runtime.
		/// Call this after changing RotationStability or PositionStability while playing.
		/// </summary>
		public void UpdateStabilityParameters()
		{
			if (!_isRegistered)
				return;

			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SetStabilityParameters(_bodyId, _rotationStability, _positionStability);
		}

		/// <summary>
		/// Whether the settled start reads this body now. It does for a body that tracks by the edge method, is registered,
		/// has no parent, has not started and is being refined from its guide, with the setting on or with the search having put the
		/// guide out of reach of its winner.
		/// </summary>
		internal bool StartSettleApplies =>
			StartSettleEffective.On && _enableEdgeTracking && _isRegistered && !HasParent && !_isTracking &&
			!_startedSinceRegistration && _automaticStartEnabled && _refiningBeforeAcceptance;

		/// <summary>
		/// Restarts the tracker's refinement at the pose the step before ended on, without writing the seat. The tracker then
		/// draws the model again at that pose and searches from there, as it does on the step that follows a start.
		/// </summary>
		private void RestartRefinementAtCarriedPose()
		{
			FTBridge.FT_StartTrackingBody(_bodyId);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_StartTrackingBody", true);
		}

		/// <summary>
		/// Takes up the refined pose of the step that has just run and says whether the settle check lets a start through. Called
		/// by <see cref="TrackedBodyManager"/> before the start decision, on every step of a body the settled start reads.
		/// </summary>
		/// <param name="poseIsWorld">Whether the tracker's pose is a world pose, as it is under pose fusion, and not a pose in the camera frame.</param>
		/// <param name="cameraKnown">Whether <paramref name="cameraPosition"/> and <paramref name="cameraRotation"/> are the camera of this step.</param>
		/// <param name="cameraPosition">Position of the camera of this step in the world, read only for a world pose.</param>
		/// <param name="cameraRotation">Rotation of the camera of this step in the world, read only for a world pose.</param>
		internal bool NoteSettleStep(bool poseIsWorld, bool cameraKnown, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			StartSettle.Setting setting = StartSettleEffective;
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || (poseIsWorld && !cameraKnown) || (StartSearchFarPath && !_startSearchSeedPublished) ||
				!FTBridge.FT_GetBodyPose(_bodyId, out FTTrackingPose pose) ||
				!pose.TryGetTransformation(out Vector3 position, out Quaternion rotation))
			{
				StartSettle.Lose(ref _settleState, setting);
				return false;
			}

			float pixels = float.NaN;
			if (_settleState.HasPrevious)
			{
				// Both poses are seen from the camera of this step, so a camera that moved between the two steps does not read
				// as a body that moved.
				Vector3 fromPosition = _settleState.Position;
				Quaternion fromRotation = _settleState.Rotation;
				Vector3 toPosition = position;
				Quaternion toRotation = rotation;
				if (poseIsWorld)
				{
					Quaternion worldToCamera = Quaternion.Inverse(cameraRotation);
					fromPosition = worldToCamera * (fromPosition - cameraPosition);
					fromRotation = worldToCamera * fromRotation;
					toPosition = worldToCamera * (toPosition - cameraPosition);
					toRotation = worldToCamera * toRotation;
				}
				EnsureSearchWalkPoints();
				pixels = SearchWalk.MedianDisplacementPx(_searchWalkPoints, _searchWalkPoints.Length,
					fromPosition, fromRotation, toPosition, toRotation, manager.CachedFu, manager.CachedFv, _searchWalkScratch);
			}

			float drift = MeasureSettleDriftPixels(manager, position, rotation, poseIsWorld, cameraPosition, cameraRotation);
			if (StartSettle.Take(ref _settleState, setting, pixels, drift, position, rotation))
			{
				ReturnSettleToGuide();
				return false;
			}
			return _settleState.Last.Passes;
		}

		/// <summary>
		/// How far the refined pose stands from the guide, as the median on-screen displacement of the model's points in pixels, with
		/// the meter the movement between two steps uses. Not a number when the guide or the pose could not be read.
		/// </summary>
		private float MeasureSettleDriftPixels(XRTrackerManager manager, Vector3 carriedPosition, Quaternion carriedRotation,
			bool poseIsWorld, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			// With the settled start seeded by the search, the refinement is measured from the winner it was seeded with, because the guide
			// is out of reach of the winner and would send every seed back.
			bool fromSeed = StartSearchFarPath && _startSearchSeedValid;
			if (!fromSeed && !_retainedInitializationPose.HasPose)
				return float.NaN;
			FTTrackingPose placed = fromSeed ? _startSearchSeed : ConversionUtils.GetConvertedPose(_retainedInitializationPose.SpaceFromBody);
			return MeasurePixelsFrom(manager, placed, carriedPosition, carriedRotation, poseIsWorld, cameraPosition, cameraRotation);
		}

		/// <summary>
		/// How far the picture of this body, held fixed in the world at the given pose, moves on screen between two poses of the camera, as the
		/// largest displacement of the centre and of the eight corners of the bounds of its model, with the corners drawn in to
		/// <see cref="TrackerDefaults.FAST_MOTION_HOLD_MAX_HALF_EXTENT_METRES"/> of the centre, in pixels of a camera whose focal length is
		/// <see cref="TrackerDefaults.FAST_MOTION_HOLD_REFERENCE_FOCAL_PIXELS"/>. Not a number when the tracker is not running or no point stands in front of the
		/// camera at both poses.
		/// </summary>
		internal float CameraMotionPixels(Vector3 heldPosition, Quaternion heldRotation, Vector3 previousCameraPosition,
			Quaternion previousCameraRotation, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !(manager.CachedFu > 0f))
				return float.NaN;
			EnsureSearchWalkPoints();
			float pixels = SearchWalk.MaxPointMotionPx(_boundsMotionPoints, heldPosition, heldRotation, previousCameraPosition, previousCameraRotation,
				cameraPosition, cameraRotation, manager.CachedFu, manager.CachedFv);
			return SearchWalk.ScaleToReferenceFocal(pixels, manager.CachedFu, TrackerDefaults.FAST_MOTION_HOLD_REFERENCE_FOCAL_PIXELS);
		}

		/// <summary>
		/// How far two world poses of this body stand apart on screen, as the median displacement of the model's points in pixels,
		/// seen from a camera at the given world pose. Not a number when the tracker is not running or a pose could not be read.
		/// </summary>
		internal float OnScreenPixelsBetween(Vector3 firstPosition, Quaternion firstRotation, Vector3 secondPosition,
			Quaternion secondRotation, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			XRTrackerManager manager = XRTrackerManager.Instance;
			if (manager == null || !(manager.CachedFu > 0f))
				return float.NaN;
			FTTrackingPose first = ConversionUtils.GetConvertedPose(Matrix4x4.TRS(firstPosition, firstRotation, Vector3.one));
			return MeasurePixelsFrom(manager, first, secondPosition, secondRotation, true, cameraPosition, cameraRotation);
		}

		/// <summary>
		/// How far a pose stands from another, as the median on-screen displacement of the model's points in pixels, with the meter the
		/// movement between two steps uses. Under pose fusion both poses are seen from the camera of the step. Not a number when the
		/// placed pose could not be read.
		/// </summary>
		private float MeasurePixelsFrom(XRTrackerManager manager, FTTrackingPose placed, Vector3 carriedPosition, Quaternion carriedRotation,
			bool poseIsWorld, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			if (!placed.TryGetTransformation(out Vector3 placedPosition, out Quaternion placedRotation))
				return float.NaN;
			if (poseIsWorld)
			{
				Quaternion worldToCamera = Quaternion.Inverse(cameraRotation);
				placedPosition = worldToCamera * (placedPosition - cameraPosition);
				placedRotation = worldToCamera * placedRotation;
				carriedPosition = worldToCamera * (carriedPosition - cameraPosition);
				carriedRotation = worldToCamera * carriedRotation;
			}
			EnsureSearchWalkPoints();
			return SearchWalk.MedianDisplacementPx(_searchWalkPoints, _searchWalkPoints.Length,
				placedPosition, placedRotation, carriedPosition, carriedRotation, manager.CachedFu, manager.CachedFv, _searchWalkScratch);
		}

		/// <summary>
		/// Sends the refinement back to the guide after a step stood past the drift bound. The next step publishes the seat and
		/// asks the tracker to refine from it, as the first step of the body did, and the guide, the retained pose and the display
		/// are untouched.
		/// </summary>
		private void ReturnSettleToGuide()
		{
			_settleCarryReady = false;
			_refiningBeforeAcceptance = false;
		}

		/// <summary>Puts the settled start back to the guide, so the next step publishes the seat and compares with nothing.</summary>
		private void ResetStartSettle()
		{
			_settleCarryReady = false;
			_settleState = default;
		}

		/// <summary>The writer of the start search into the plugin. A null guide switches the search off. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, FTTrackingPose?, int, int, NativeCallOutcome> StartSearchWriter = FTBridge.TrySetStartSearch;

		/// <summary>The reader of what the search found on the latest step. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, float[], NativeCallOutcome> StartSearchReader = FTBridge.TryGetStartSearchReading;

		/// <summary>
		/// Whether the start search should be on for this body now, whether or not the refinement before the start has begun. It
		/// is so for a body that tracks by the edge method, is registered, has no parent, has not started, takes the automatic
		/// start and is refined before its start, with the setting on. The plugin hears about it before the first refining step.
		/// </summary>
		internal bool StartSearchWanted => StartSearchInForce.On && StartSearchAllowed && !_startSearchSeatMissing;

		private bool StartSearchAllowed =>
			_enableEdgeTracking && _isRegistered && !HasParent && !_isTracking && !_startedSinceRegistration &&
			_automaticStartEnabled && RefinesBeforeItsStart;

		/// <summary>
		/// Whether the start gate reads the search of this body on the step that has just run. It does when the search is wanted and
		/// the tracker is refining the body, which holds after the first step that ran with the search on.
		/// </summary>
		internal bool StartSearchApplies => StartSearchWanted && _refiningBeforeAcceptance;

		/// <summary>The writer of what the bank of the search is made of into the plugin. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, float, float, float, float, float, int, NativeCallOutcome> StartSearchBankWriter = FTBridge.TrySetStartSearchBank;

		/// <summary>The writer of how the distances of the bank are made into the plugin. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, bool, NativeCallOutcome> StartSearchDistanceWriter = FTBridge.TrySetStartSearchRenderEachDistance;

		/// <summary>The writer of the search's seat mode into the plugin. It can be replaced, and it is the plugin's call by default.</summary>
		internal static Func<string, bool, NativeCallOutcome> StartSearchSeatWriter = FTBridge.TrySetStartSearchSeat;

		/// <summary>
		/// Whether the search has put the guide out of reach of its winner, so the body is seeded from the winner and settles from it. The
		/// plugin only searches and never writes the seat of the refinement, so the seat is the guide while the guide is in reach and while
		/// no winner has been measured, and it is the seed once the guide is out of reach. The drift bound of the settle is measured from the seed.
		/// </summary>
		internal bool StartSearchFarPath =>
			StartSearchWanted && _startSearchPath == StartSearchPath.Far && !_startSearchMissing;

		/// <summary>Which start the body takes while the search holds it. Unknown until a winner has been measured against the guide.</summary>
		public StartSearchPath StartSearchRoute => _startSearchPath;

		/// <summary>The median on-screen displacement in pixels between the guide and the latest winner measured, or not a number when none was measured.</summary>
		public float StartSearchGuidePixels => _startSearchGuidePixels;

		/// <summary>
		/// Whether the body was started from the winner of the search, which is a start while the guide was out of reach of the winner. It
		/// stays true until the body registers again, and it lets the lifecycle
		/// believe the tracker's own pose, and not the pose the body was placed at, about where the body is until a pose has been accepted.
		/// </summary>
		internal bool StartedFromSearch => _startedFromSearch;

		/// <summary>
		/// This tells the plugin to search the whole image for this body and gives it the guide, or it switches the search off when the
		/// search no longer applies. The guide is the retained pose, published in the frame of the seat. The plugin hears about it only when
		/// something changed, which is the first time, a new period, a new guide or the end of the need, so a steady state makes no
		/// native call. A plugin without the export is asked once, and the body then starts without the search.
		/// </summary>
		internal void ApplyStartSearch()
		{
			_startSearchFresh = false;
			StartSearch.Setting setting = StartSearchInForce;
			bool wanted = setting.On && StartSearchAllowed && !_startSearchSeatMissing && _retainedInitializationPose.HasPose;
			switch (StartSearch.Decide(wanted, _startSearchRequest, setting.PeriodSteps, _startSearchGeneration, _startSearchMissing))
			{
				case StartSearch.Step.Send:
					SendStartSearch(setting);
					break;
				case StartSearch.Step.Clear:
					ClearStartSearch();
					break;
			}
			if (!wanted)
				_startSearchRequest.Asked = false;
			ApplyStartSearchSeatMode(wanted);
			ApplyStartSearchBank(wanted);
			ApplyStartSearchDistanceMode(wanted);
		}

		/// <summary>
		/// Tells the plugin what the bank of the search is made of. It is told when the search has just been sent and again only when a value
		/// changed, so a steady state makes no native call. The plugin makes the bank again for a change, so what was read before the change
		/// is forgotten. A plugin without the export is found once, and it then keeps the bank it was built with.
		/// </summary>
		private void ApplyStartSearchBank(bool wanted)
		{
			if (!_startSearchRequest.Held)
			{
				_startSearchBankSent = false;
				return;
			}
			if (!wanted || _startSearchBankMissing)
				return;
			StartSearch.BankSetting bank = StartSearchBankInForce;
			if (_startSearchBankSent && _startSearchBankSentValue.Equals(bank))
				return;
			NativeCallOutcome outcome = StartSearchBankWriter(_bodyId, bank.ConeHalfAngle, bank.ConeSpacing, bank.RollRange, bank.FineHalfAngle,
				bank.WindowMargin, bank.DistanceCount);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_SetStartSearchBank", outcome == NativeCallOutcome.Done);
			if (outcome == NativeCallOutcome.Missing)
			{
				_startSearchBankMissing = true;
				return;
			}
			bool changed = _startSearchBankSent;
			_startSearchBankSent = true;
			_startSearchBankSentValue = bank;
			if (changed)
				ResetStartSearch();
		}

		/// <summary>
		/// Tells the plugin whether to draw every distance of the bank or to draw each view once and scale the other distances from it. It is
		/// told when the search has just been sent and again only when the value changed, so a steady state makes no native call. The plugin
		/// makes the bank again for a change, so what was read before the change is forgotten. A plugin without the export is found once, and it
		/// then keeps the way it was built with.
		/// </summary>
		private void ApplyStartSearchDistanceMode(bool wanted)
		{
			if (!_startSearchRequest.Held)
			{
				_startSearchRenderSent = false;
				return;
			}
			if (!wanted || _startSearchRenderMissing)
				return;
			bool render = !StartSearchScaleDistancesInForce;
			if (_startSearchRenderSent && _startSearchRenderSentValue == render)
				return;
			NativeCallOutcome outcome = StartSearchDistanceWriter(_bodyId, render);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_SetStartSearchRenderEachDistance", outcome == NativeCallOutcome.Done);
			if (outcome == NativeCallOutcome.Missing)
			{
				_startSearchRenderMissing = true;
				return;
			}
			bool changed = _startSearchRenderSent;
			_startSearchRenderSent = true;
			_startSearchRenderSentValue = render;
			if (changed)
				ResetStartSearch();
		}

		/// <summary>
		/// Tells the plugin to leave the seat of the refinement to this body, which it does for as long as the search holds the body, because the
		/// seat is the guide while the guide is in reach and the seed once it is out of reach. The plugin is asked once, and a plugin without
		/// the export is found once, and the body then starts without the search.
		/// </summary>
		private void ApplyStartSearchSeatMode(bool wanted)
		{
			bool wantOff = wanted;
			if (!_startSearchRequest.Held)
			{
				_startSearchSeatOff = false;
				return;
			}
			if (wantOff == _startSearchSeatOff)
				return;
			NativeCallOutcome outcome = StartSearchSeatWriter(_bodyId, !wantOff);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_SetStartSearchSeat", outcome == NativeCallOutcome.Done);
			if (outcome == NativeCallOutcome.Done)
				_startSearchSeatOff = wantOff;
			else if (outcome == NativeCallOutcome.Missing)
				_startSearchSeatMissing = true;
		}

		/// <summary>
		/// Under pose fusion the guide stands still in the world while the camera moves, and the views the plugin builds are built around
		/// the guide as the camera saw it then. This remembers the camera of the step the views were asked for, and asks for them again
		/// around the guide as the camera sees it now once the object is seen from a side or a distance the views no longer cover. A
		/// guide that is held in the camera needs none of this, because it does not move in the camera. Called only from
		/// <see cref="BeforeTrackingStep"/>.
		/// </summary>
		private void ReanchorStartSearch()
		{
#if HAS_AR_FOUNDATION
			if (!_startSearchRequest.Held || !_retainedInitializationPose.HasPose)
				return;
			var manager = XRTrackerManager.Instance;
			if (manager == null || !manager.UseARPoseFusion ||
				!manager.TryGetSubmittedFusionFrameContext(out FusionFrameContext context) || !context.IsValid)
				return;
			Matrix4x4 worldFromCamera = context.WorldFromCameraUnity;
			if (!_startSearchAnchorKnown)
			{
				_startSearchAnchorCamera = worldFromCamera;
				_startSearchAnchorKnown = true;
				return;
			}
			bool follows = GuideFollowsCamera(manager.UseARPoseFusion, _initialPoseSource == InitialPoseSource.Viewpoint,
				manager.CurrentImageSource == ImageSource.Sequence, HasParent, _startedSinceRegistration);
			if (follows)
			{
				// A guide that follows the camera is seen from the same side and distance on every step, so the views stay right. The
				// plugin looks for the object around the guide it was sent, which stays where it was in the world, so the views are asked
				// for again once that guide has fallen behind the guide on screen.
				Matrix4x4 guideNow = _retainedInitializationPose.SpaceFromBody;
				float behind = MeasurePixelsFrom(manager, ConversionUtils.GetConvertedPose(_startSearchSentGuide),
					guideNow.GetPosition(), guideNow.rotation, true, worldFromCamera.GetPosition(), worldFromCamera.rotation);
				if (!(behind > StartSearch.REANCHOR_FOLLOWING_PIXELS))
					return;
			}
			else if (!StartSearch.GuideViewMoved(_retainedInitializationPose.SpaceFromBody, _startSearchAnchorCamera, worldFromCamera))
				return;
			ResetStartSettle();
			++_startSearchGeneration;
			ResetStartSearch();
#endif
		}

		private void SendStartSearch(in StartSearch.Setting setting)
		{
			Matrix4x4 spaceFromBody = _retainedInitializationPose.SpaceFromBody;
			if (!ConversionUtils.IsUsablePose(spaceFromBody.GetPosition(), spaceFromBody.rotation))
				return;
			FTTrackingPose guide = ConversionUtils.GetConvertedPose(spaceFromBody);
			_startSearchSentGuide = spaceFromBody;
			NativeCallOutcome outcome = StartSearchWriter(_bodyId, guide, setting.PeriodSteps, _startSearchGeneration);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_SetStartSearch(set)", outcome == NativeCallOutcome.Done);
			// A refusal is remembered as asked, so a refused search is not repeated on every step. It is asked again when the period or the guide changes.
			_startSearchRequest = new StartSearch.Request
			{
				Asked = true,
				Held = outcome == NativeCallOutcome.Done || (outcome == NativeCallOutcome.Refused && _startSearchRequest.Held),
				PeriodSteps = setting.PeriodSteps,
				Generation = _startSearchGeneration,
			};
			if (outcome == NativeCallOutcome.Missing)
				_startSearchMissing = true;
			// A request starts the plugin's search over, with the seat of the refinement written by it as it is by default.
			_startSearchSeatOff = false;
			_startSearchBankSent = false;
			_startSearchRenderSent = false;
			// The views are built on the step of the request, so the camera of the next step is the camera they are built for.
			_startSearchAnchorKnown = false;
			// The plugin may count its searches from the start again when it takes a request, so nothing read before this one is kept.
			ResetStartSearch();
		}

		/// <summary>This switches off the search the plugin holds for this body, if it holds one.</summary>
		private void ClearStartSearch()
		{
			_startSearchRequest.Asked = false;
			if (!_startSearchRequest.Held)
				return;
			// The period is a usable one because the plugin may check it before it looks at the missing guide.
			NativeCallOutcome outcome = StartSearchWriter(_bodyId, null, StartSearch.DEFAULT_PERIOD_STEPS, _startSearchGeneration);
			NativeBodyWriteJournal.RecordCall(_bodyId, "FT_SetStartSearch(clear)", outcome == NativeCallOutcome.Done);
			_startSearchRequest.Held = false;
		}

		/// <summary>This forgets what was read and how many searches agreed, so the next search counts as the first.</summary>
		private void ResetStartSearch()
		{
			_startSearchState = default;
			_startSearchLastReading = default;
			_startSearchFresh = false;
			_startSearchSeedValid = false;
			_startSearchSeedPublished = false;
			_startSearchPath = StartSearchPath.Unknown;
			_startSearchGuidePixels = float.NaN;
			_startSearchReachHasSerial = false;
			_startSearchFarRun = 0;
			_startSearchNearRun = 0;
		}

		/// <summary>This forgets what was sent for a body that registers or unregisters, because a new native body holds no search.</summary>
		private void ForgetStartSearch()
		{
			_startSearchRequest = default;
			_startSearchMissing = false;
			_startSearchSeatOff = false;
			_startSearchSeatMissing = false;
			_startSearchBankMissing = false;
			_startSearchBankSent = false;
			_startSearchRenderMissing = false;
			_startSearchRenderSent = false;
			_startedFromSearch = false;
			_startSearchAnchorKnown = false;
			ResetStartSearch();
		}

		/// <summary>
		/// This takes up what the search found on the step that has just run and says whether the start gate lets a start through. The
		/// <see cref="TrackedBodyManager"/> calls it before the start decision, on every step of a body the search reads. Each new winner
		/// is measured against the guide, and the path the body takes switches only on a latch (see <see cref="StartSearch.Latch"/>). While
		/// the guide is in reach, and while no winner has decided, the gate lets every start through, so the body starts as it does without
		/// the search and on the same row. When the guide is out of reach, the winner becomes the seed of the refinement, and the gate lets a
		/// start through only when the same winner was found on three searches in a row and the margin and the score reach their bars. It
		/// lets every start through when the body holds no search, so a plugin without the exports starts the body without the search.
		/// </summary>
		/// <param name="poseIsWorld">Whether the tracker's pose is a world pose, as it is under pose fusion, and not a pose in the camera frame.</param>
		/// <param name="cameraKnown">Whether <paramref name="cameraPosition"/> and <paramref name="cameraRotation"/> are the camera of this step.</param>
		/// <param name="cameraPosition">Position of the camera of this step in the world, read only for a world pose.</param>
		/// <param name="cameraRotation">Rotation of the camera of this step in the world, read only for a world pose.</param>
		internal bool NoteStartSearchStep(bool poseIsWorld, bool cameraKnown, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			if (!_startSearchRequest.Held || _startSearchMissing)
				return true;

			StartSearch.Setting setting = StartSearchInForce;
			_startSearchRaw ??= new float[FTBridge.START_SEARCH_READING_LENGTH];
			NativeCallOutcome outcome = StartSearchReader(_bodyId, _startSearchRaw);
			if (outcome == NativeCallOutcome.Missing)
			{
				_startSearchMissing = true;
				return true;
			}
			if (outcome != NativeCallOutcome.Done)
			{
				StartSearch.Lose(ref _startSearchState);
				return _startSearchPath != StartSearchPath.Far;
			}

			StartSearchReading reading = StartSearch.Parse(_startSearchRaw);
			_startSearchLastReading = reading;
			_startSearchFresh = true;
			Vector3 position = Vector3.zero;
			Quaternion rotation = Quaternion.identity;
			XRTrackerManager manager = XRTrackerManager.Instance;
			bool measurable = manager != null && !(poseIsWorld && !cameraKnown) && reading.TryGetWinner(out position, out rotation);

			if (reading.Valid && (!_startSearchReachHasSerial || reading.Serial != _startSearchReachSerial))
			{
				if (!measurable)
				{
					StartSearch.Lose(ref _startSearchState);
					return _startSearchPath != StartSearchPath.Far;
				}
				_startSearchReachSerial = reading.Serial;
				_startSearchReachHasSerial = true;
				_startSearchGuidePixels = MeasureGuideToWinnerPixels(manager, position, rotation, poseIsWorld, cameraPosition, cameraRotation);
				StartSearchPath next = StartSearch.Latch(_startSearchPath, ref _startSearchFarRun, ref _startSearchNearRun,
					_startSearchGuidePixels, setting.ReachPixels, StartSearch.WinnerIsStrong(setting, reading));
				if (next != _startSearchPath)
					EnterStartSearchPath(next);
			}

			// The searches that agree are counted on every path, so a body that turns to the far path finds the count of the winners
			// before the turn already made.
			float pixels = float.NaN;
			bool newSearch = StartSearch.IsNewSearch(_startSearchState, reading);
			if (newSearch)
			{
				if (!measurable)
				{
					StartSearch.Lose(ref _startSearchState);
					return _startSearchPath != StartSearchPath.Far;
				}

				if (_startSearchState.HasPrevious)
				{
					// Both winners are seen from the camera of this step, so a camera that moved between the two searches does not read
					// as a winner that moved.
					Vector3 fromPosition = _startSearchState.Position;
					Quaternion fromRotation = _startSearchState.Rotation;
					Vector3 toPosition = position;
					Quaternion toRotation = rotation;
					if (poseIsWorld)
					{
						Quaternion worldToCamera = Quaternion.Inverse(cameraRotation);
						fromPosition = worldToCamera * (fromPosition - cameraPosition);
						fromRotation = worldToCamera * fromRotation;
						toPosition = worldToCamera * (toPosition - cameraPosition);
						toRotation = worldToCamera * toRotation;
					}
					EnsureSearchWalkPoints();
					pixels = SearchWalk.MedianDisplacementPx(_searchWalkPoints, _searchWalkPoints.Length,
						fromPosition, fromRotation, toPosition, toRotation, manager.CachedFu, manager.CachedFv, _searchWalkScratch);
				}
			}

			StartSearch.Take(ref _startSearchState, setting, reading, pixels, position, rotation);
			if (_startSearchPath != StartSearchPath.Far)
				return true;

			// A first winner of the far path, or a winner that stands apart from the one before, is the new seed of the refinement. The
			// settle begins again from it, and the next step publishes it and asks the tracker to refine from it. A start waits for that
			// settle, so the step that sets the seed lets none through.
			if (!_startSearchSeedValid || (newSearch && _startSearchState.Streak == 1))
			{
				_startSearchSeed = reading.WinnerTrackerPose;
				_startSearchSeedValid = true;
				ResetStartSettle();
				_refiningBeforeAcceptance = false;
				return false;
			}
			return _startSearchState.Last.Passes;
		}

		/// <summary>
		/// This switches the body to the path the latch has chosen. A first winner that finds the guide in reach changes nothing, because the
		/// body's own start is what the body already runs. Any other change puts the settle and the seed back to the start and asks the tracker
		/// to refine again from the seat the new path publishes, which is the guide in reach and the seed out of it.
		/// </summary>
		private void EnterStartSearchPath(StartSearchPath path)
		{
			StartSearchPath before = _startSearchPath;
			if (path == before)
				return;
			_startSearchPath = path;
			if (before == StartSearchPath.Unknown && path == StartSearchPath.InReach)
				return;
			ResetStartSettle();
			_startSearchSeedValid = false;
			_startSearchSeedPublished = false;
			_refiningBeforeAcceptance = false;
		}

		/// <summary>
		/// How far the guide stands from a winner, as the median on-screen displacement of the model's points in pixels, with the meter the
		/// movement between two steps uses. Not a number when the guide has not been latched or could not be read.
		/// </summary>
		private float MeasureGuideToWinnerPixels(XRTrackerManager manager, Vector3 winnerPosition, Quaternion winnerRotation,
			bool poseIsWorld, Vector3 cameraPosition, Quaternion cameraRotation)
		{
			if (!_retainedInitializationPose.HasPose)
				return float.NaN;
			FTTrackingPose guide = ConversionUtils.GetConvertedPose(_retainedInitializationPose.SpaceFromBody);
			return MeasurePixelsFrom(manager, guide, winnerPosition, winnerRotation, poseIsWorld, cameraPosition, cameraRotation);
		}

		/// <summary>
		/// This reads what the search found on the step that has just run and what the start gate decided on it. It returns false when the
		/// body holds no search, when the plugin could not be read, and on any step on which the gate was not asked, so a body that
		/// has started gives a reading for the step it started on and none after it.
		/// </summary>
		public bool TryGetStartSearchReading(out StartSearchReading reading, out StartSearchVerdict verdict)
		{
			reading = _startSearchLastReading;
			verdict = _startSearchState.Last;
			return _startSearchFresh;
		}

		/// <summary>
		/// Update tracking tuning parameters at runtime.
		/// Call this after changing EdgeTracking or SilhouetteTracking settings while playing.
		/// </summary>
		/// <remarks>
		/// The silhouette settings reach the tracker at once. Of the edge settings, the depth edge threshold, the search radius, the minimum
		/// gradient, the sample step and the crease edges reach it at the next tracking step, and the sites are drawn again with them. That needs a
		/// plugin build that supports it. Every other edge setting is read when the body registers, so it takes effect after the component is
		/// disabled and enabled again, and a warning in the console says so.
		/// </remarks>
		public void UpdateEdgeTrackingParameters()
		{
			if (!_isRegistered)
				return;

			NoteEdgeSettingsChanged();
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SetModalityParameters(_bodyId, _silhouetteTracking.FunctionAmplitude, _silhouetteTracking.LearningRate);
		}

		/// <summary>
		/// Compares the edge settings with what the tracker holds. A difference in the five detection settings is sent to the tracker at the next
		/// step, and a difference anywhere else is reported once in the console, because it takes effect only when the body registers again.
		/// </summary>
		internal void NoteEdgeSettingsChanged()
		{
			if (!_isRegistered || !_enableEdgeTracking || !_hasRegisteredEdgeSettings)
				return;

			_detectionApplyPending = !_liveEdgeSettingsUnavailable && !EdgeDetectionSettings.From(_edgeTracking).Equals(_registeredDetection);

			if (_edgeTracking.RegistrationOnlySignature() == _registeredOtherEdgeSettings)
			{
				_edgeTrackingChangeReported = false;
				return;
			}
			if (_edgeTrackingChangeReported)
				return;
			_edgeTrackingChangeReported = true;
			Debug.LogWarning($"[TrackedBody] {_bodyId}: a keyframe, search scale or standard deviation setting changed while the body plays. The tracker reads those when the body " +
				"registers, so they take effect after the component is disabled and enabled again, or after the scene is played again.");
		}

		/// <summary>
		/// Sends the five detection settings to the tracker. Called from <see cref="BeforeTrackingStep"/>, which the manager runs while it holds the
		/// native access lock and before the step starts, so the plugin is not in a tracking step.
		/// </summary>
		private void ApplyDetectionSettingsToNative()
		{
			_detectionApplyPending = false;
			if (!_isRegistered || !_enableEdgeTracking)
				return;

			EdgeDetectionSettings wanted = EdgeDetectionSettings.From(_edgeTracking);
			if (wanted.Equals(_registeredDetection))
				return;

			switch (FTBridge.TrySetEdgeParameters(_bodyId, wanted))
			{
				case FTBridge.EdgeParametersResult.Applied:
					_registeredDetection = wanted;
					Debug.Log($"[TrackedBody] {_bodyId}: the tracker now holds {wanted}.");
					break;
				case FTBridge.EdgeParametersResult.NotInPlugin:
					_liveEdgeSettingsUnavailable = true;
					Debug.LogWarning($"[TrackedBody] {_bodyId}: this plugin build cannot change edge settings while a body is registered, so the tracker still holds " +
						$"{_registeredDetection}. Disable and enable the component, or play the scene again, to apply the new settings.");
					break;
				default:
					Debug.LogWarning($"[TrackedBody] {_bodyId}: the plugin refused the new edge settings ({wanted}), so the tracker still holds {_registeredDetection}.");
					break;
			}
		}

		/// <summary>
		/// Update the depth correspondence parameters at runtime.
		/// Call this after changing the distance tolerance or the standard deviations of <see cref="DepthTracking"/> while playing.
		/// </summary>
		/// <remarks>
		/// Both reach the tracker at the next tracking step, when the plugin build supports it. Every other depth setting is read when the
		/// body registers.
		/// </remarks>
		public void UpdateDepthTrackingParameters()
		{
			if (!_isRegistered)
				return;

			NoteDepthSettingsChanged();
		}

		/// <summary>
		/// Compares the depth considered distance and standard deviations with what the tracker holds, and sends a difference at the next step.
		/// </summary>
		internal void NoteDepthSettingsChanged()
		{
			if (!_isRegistered || !_depthTrackingEnabled || !_hasRegisteredDepth)
				return;

			_depthApplyPending = !_liveDepthSettingsUnavailable && !DepthCorrespondenceSettings.From(_depthTracking).Equals(_registeredDepth);
		}

		/// <summary>
		/// Sends the depth considered distance and standard deviations to the tracker. Called from <see cref="BeforeTrackingStep"/>, which the
		/// manager runs while it holds the native access lock and before the step starts, so the plugin is not in a tracking step.
		/// </summary>
		private void ApplyDepthSettingsToNative()
		{
			_depthApplyPending = false;
			if (!_isRegistered || !_depthTrackingEnabled)
				return;

			DepthCorrespondenceSettings wanted = DepthCorrespondenceSettings.From(_depthTracking);
			if (wanted.Equals(_registeredDepth))
				return;

			switch (FTBridge.TrySetDepthParameters(_bodyId, wanted))
			{
				case FTBridge.DepthParametersResult.Applied:
					_registeredDepth = wanted;
					Debug.Log($"[TrackedBody] {_bodyId}: the tracker now holds {wanted}.");
					break;
				case FTBridge.DepthParametersResult.NotInPlugin:
					_liveDepthSettingsUnavailable = true;
					Debug.LogWarning($"[TrackedBody] {_bodyId}: this plugin build cannot change depth settings while a body is registered, so the tracker still holds " +
						$"{_registeredDepth}. Disable and enable the component, or play the scene again, after a plugin build that supports it is in place.");
					break;
				default:
					Debug.LogWarning($"[TrackedBody] {_bodyId}: the plugin refused the new depth settings ({wanted}), so the tracker still holds {_registeredDepth}.");
					break;
			}
		}

		/// <summary>
		/// Update occlusion detection parameters at runtime.
		/// Call this after changing OcclusionSettings while playing.
		/// </summary>
		public void UpdateOcclusionParameters()
		{
			if (!_isRegistered)
				return;

			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SetSilhouetteOcclusionParameters(_bodyId, _occlusionSettings.Radius, _occlusionSettings.Threshold);
		}

		/// <summary>
		/// Set the initial pose from a viewpoint transform.
		/// The object's current world position is computed relative to the viewpoint.
		/// </summary>
		/// <param name="viewpoint">The viewpoint transform, such as the camera position. If null, uses main camera.</param>
		public void SetInitialPose(Transform viewpoint)
		{
			Transform vp = viewpoint ? viewpoint : CameraTransform;
			StoreInitialPose(
				vp.InverseTransformPoint(transform.position),
				Quaternion.Inverse(vp.rotation) * transform.rotation);
			// The pose is read from this transform, so the body does not move; the retained pose
			// is re-latched from where it now stands.
			LatchRetainedInitializationPose(transform.position, transform.rotation,
				NativeBodyWrite.CADENCE_ADOPTED);
			SetGuide(transform.position, transform.rotation);
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
			Transform cam = CameraTransform;
			if (cam != null)
			{
				LatchRetainedInitializationPose(
					cam.TransformPoint(position),
					cam.rotation * rotation,
					NativeBodyWrite.CADENCE_ADOPTED);
				SetGuide(cam.TransformPoint(position), cam.rotation * rotation);
			}
			ApplyInitialPoseToNative();
		}

		private void ApplyInitialPoseToNative()
		{
			if (!_isRegistered)
				return;

			// Update the detector's stored pose (used for future resets/detections)
			// Does NOT immediately reset - call ResetTracking() to apply immediately
			XRTrackerManager.WaitForTrackingStepToReturn();
			int status = FTBridge.FT_SetDetectorPose(_bodyId, ref _initialPose);
			bool landed = FrameWriteGuard.RecordOutOfFrame(status, "FT_SetDetectorPose");
			// The pose recorded here is `_initialPose`, which was converted for the
			// call and never existed in engine axes on this path, so it is recorded
			// as what it is. Every other pose in the journal is an engine-axis pose.
			NativeBodyWriteJournal.RecordPluginAxisPose(_bodyId, "FT_SetDetectorPose",
				"viewpoint",
				new Vector3(_initialPose.pos_x, _initialPose.pos_y, _initialPose.pos_z),
				new Quaternion(_initialPose.rot_x, _initialPose.rot_y,
					_initialPose.rot_z, _initialPose.rot_w),
				landed, status);
			_detectorPoseWritePending = !landed;
		}

		/// <summary>
		/// Fetches current status from native tracker.
		/// Called by TrackedBodyManager before processing.
		/// </summary>
		internal bool RefreshStatus(TrackedBodyStatusReader reader)
		{
			if (!_isRegistered)
				return false;

			if (reader == null || !reader(_bodyId, out _lastStatus))
				return false;

			SetRegistrationQuality(RegistrationQualitySnapshot.FromNative(
				_lastStatus.registration_quality));

			// Update quality averaging buffers
			UpdateQualityBuffers();
			return true;
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
			if (_registrationMarginBuffer != null)
			{
				// A frame whose evidence record carries no reading is not a
				// registered frame, and zero is the reading it gets. Availability
				// is a separate statement and RegistrationQuality still carries it.
				// The instant reading is stored so the average follows the same term.
				_registrationMarginBuffer[_qualityBufferIndex] = InstantRegistrationMargin;
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
			if (isTracking)
			{
				KeepGuideBeforeFirstStart();
				_startedFromSearch = _startSearchRequest.Held && StartSearchFarPath;
				ClearStartSearch();
			}
			_isTracking = isTracking;
			_seatDisplayHeld = false;
			_refiningBeforeAcceptance = false;
			_recoveryRefining = false;
			RecoverySeatRefineRequested = false;
			_searchWalk = default;
			if (isTracking)
				_startedSinceRegistration = true;
		}

		/// <summary>
		/// Publishes one frame's three-state result and fires
		/// <see cref="OnConfidenceChanged"/> on a crossing. Called by
		/// TrackedBodyManager.
		/// </summary>
		/// <remarks>
		/// The reason is published on every frame, because a body can stay
		/// <see cref="TrackingConfidence.Degraded"/> while the reason for it changes
		/// — an out-of-view body that comes back into frame and is still not
		/// recognised is the case that matters. The event follows the state, which is
		/// the quantity a handler is written against.
		/// </remarks>
		internal void SetConfidence(TrackingConfidenceTransition transition)
		{
			_confidenceReason = transition.Reason;

			// The published value is what a handler has already been told, so it is
			// what the edge is measured against. Reading the transition's own edge
			// here instead would fire twice whenever a body's state object is
			// replaced under a body whose published value never moved.
			if (_confidence == transition.State)
				return;

			_confidence = transition.State;
			OnConfidenceChanged?.Invoke(transition.State, transition.Reason);
		}

		internal void SetRegistrationQuality(
			in RegistrationQualitySnapshot registrationQuality)
		{
			_registrationQuality = registrationQuality;
		}

		internal void SetTrackingHealth(in TrackingHealthSnapshot trackingHealth)
		{
			_trackingHealthStatus = trackingHealth;
		}

		internal void SetTemporalTrackingStatus(
			in TemporalTrackingSnapshot temporalTrackingStatus)
		{
			bool changed = _temporalTrackingStatus.State != temporalTrackingStatus.State;
			_temporalTrackingStatus = temporalTrackingStatus;
			if (changed)
				OnTemporalTrackingStatusChanged?.Invoke(temporalTrackingStatus);
		}

		internal void ResetQualityMetrics()
		{
			_qualityBufferCount = 0;
			_qualityBufferIndex = 0;
			_lastStatus = default;
			_registrationQuality = RegistrationQualitySnapshot.Unsupported;
			_trackingHealthStatus = TrackingHealthSnapshot.Unavailable;
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
		/// Event fired whenever <see cref="Confidence"/> changes, with the new state
		/// and the reason for it.
		/// </summary>
		/// <remarks>
		/// Edge-triggered: it fires once per crossing and not once per frame, so a
		/// handler runs as often as the state changes and no more.
		/// </remarks>
		public event Action<TrackingConfidence, TrackingConfidenceReason> OnConfidenceChanged;

		/// <summary>Fires once whenever the new temporal state crosses an edge.</summary>
		internal event Action<TemporalTrackingSnapshot> OnTemporalTrackingStatusChanged;

		/// <summary>
		/// Sets the world pose of this body. Called by TrackedBodyManager.
		/// Sets transform and fires OnPoseUpdated event.
		/// </summary>
		internal void SetWorldPose(Vector3 position, Quaternion rotation)
		{
			SetWorldPose(position, rotation, PoseProvenance.None);
		}

		/// <summary>
		/// Sets the world pose of this body and records where it came from.
		/// </summary>
		/// <remarks>
		/// The tag is recorded, not acted on. Nothing in this package branches on
		/// it, and callers pass <see cref="PoseProvenance.None"/> unless provenance
		/// recording is enabled.
		/// </remarks>
		internal void SetWorldPose(Vector3 position, Quaternion rotation, PoseProvenance provenance)
		{
			// The last gate before a value becomes the object's world matrix. A
			// non-finite transform poisons this body's matrix, its children, its
			// bounds and every subscriber below, and the engine does not recover
			// from it, so the write is refused rather than made and regretted.
			if (!ConversionUtils.IsUsablePose(position, rotation))
			{
				RecordRejectedPose();
				return;
			}

			transform.position = position;
			transform.rotation = rotation;
			_lastPoseProvenance = provenance;
			OnPoseUpdated?.Invoke(position, rotation);
		}

		/// <summary>
		/// Counts a pose this body refused because it could not be used.
		/// </summary>
		/// <remarks>
		/// A refusal that leaves no trace is indistinguishable from a frame in which
		/// nothing happened, so every gate reports here. The count feeds diagnostics
		/// only; nothing branches on it.
		/// </remarks>
		internal void RecordRejectedPose()
		{
			++_rejectedPoseCount;
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
				XRTrackerManager.WaitForTrackingStepToReturn();
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
				XRTrackerManager.WaitForTrackingStepToReturn();
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
			_localBoundsCache?.Invalidate();

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
				XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
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

			XRTrackerManager.WaitForTrackingStepToReturn();
			bool written = FTBridge.FT_SetBodyContributesToOptimization(_bodyId, contributes);
			NativeBodyWriteJournal.RecordCall(_bodyId,
				contributes ? "FT_SetBodyContributesToOptimization(true)" : "FT_SetBodyContributesToOptimization(false)",
				written);
			if (!written)
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
			// What the tracker is about to be given, kept to compare later changes against and to say what the tracker holds.
			string otherEdgeSettings = _edgeTracking.RegistrationOnlySignature();
			EdgeDetectionSettings detection = EdgeDetectionSettings.From(_edgeTracking);
			float depthDistanceTolerance = _depthTracking.DistanceTolerance;
			var bodyConfig = new FTBodyConfig
			{
				rotation_regularization = _rotationStability,
				translation_regularization = _positionStability,
				enable_texture_modality = _enableTextureTracking ? 1 : 0,
				enable_depth_modality = _enableDepthTracking ? 1 : 0,
				enable_region_modality = _enableSilhouetteTracking ? 1 : 0,
				use_depth_for_occlusion = (_enableDepthTracking && _enableOcclusion) ? 1 : 0,
				function_amplitude = _silhouetteTracking.FunctionAmplitude,
				learning_rate = _silhouetteTracking.LearningRate,
				// Multi-scale pyramid settings
				region_advanced = _multiScale.ToNative(_silhouetteTracking),
				// Depth modality settings
				depth_distance_tolerance = depthDistanceTolerance,
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
				// Fixed values that are not exposed as settings
				edge_use_normal_direction = 1,
				edge_use_laplacian_edge_detection = 1,
				edge_use_per_site_threshold = _edgeTracking.UsePerSiteThreshold ? 1 : 0,
				edge_inward_search_ratio = 1f,
				edge_use_compute_pipeline = _edgeTracking.UseComputePipeline ? 1 : 0,
				edge_use_illumination_compensation = 1,
				edge_search_resolution = 640,
				edge_filter_prediction_sites = 1,
				edge_prediction_weight_threshold = 0.3f,
				edge_ncc_min_correlation = 0.7f,
				edge_search_radius_scales = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_search_radius_scales_count = 0,
				edge_standard_deviations = new float[FTRegionModalityAdvanced.MAX_SCALES],
				edge_standard_deviations_count = 0,
				edge_use_keyframe = ShippedDefaults.EdgeKeyframe() ? 1 : 0,
				edge_restore_all_keyframe_sites = _edgeTracking.RestoreAllKeyframeSites ? 1 : 0,
				edge_keyframe_rotation_deg = _edgeTracking.KeyframeRotationDeg,
				edge_keyframe_translation = _edgeTracking.KeyframeTranslation,
				edge_keyframe_depletion_threshold = _edgeTracking.KeyframeDepletionThreshold,
				edge_probe_search_radius_scale = _edgeTracking.ProbeSearchRadiusScale,
				edge_probe_max_projection_error_deg = _edgeTracking.ProbeMaxProjectionErrorDeg,
				edge_probe_max_residual_px = _edgeTracking.ProbeMaxResidualPx,
				edge_enable_candidate_diagnostics = _edgeTracking.EnableCandidateDiagnostics ? 1 : 0,
				edge_enable_site_reliability_diagnostics =
					_edgeTracking.EnableSiteReliabilityDiagnostics ? 1 : 0,
				edge_enable_information_site_diagnostics =
					_edgeTracking.EnableInformationSiteDiagnostics ? 1 : 0,
				edge_use_selected_candidate_state = _edgeTracking.UseSelectedCandidateState ? 1 : 0,
				edge_use_distinct_response_candidates =
					_edgeTracking.UseDistinctResponseCandidates ? 1 : 0,
				edge_enable_probe_site_diagnostics =
					_edgeTracking.EnableProbeSiteDiagnostics ? 1 : 0
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

				if (result != FTErrorCode.OK)
				{
					Debug.LogError($"[TrackedBody] Failed to register {_bodyId}: error code {result}");
					return;
				}

				if (!_isRegistered)
				{
					Debug.Log($"[TrackedBody] {_bodyId}: Registration cancelled before publication");
					return;
				}

				// The native lock is released by now, and the calls below set up occlusion and detection on bodies that may be tracking.
				XRTrackerManager.WaitForTrackingStepToReturn();

				_registeredOtherEdgeSettings = otherEdgeSettings;
				_hasRegisteredEdgeSettings = true;
				_registeredDetection = detection;
				_detectionApplyPending = false;
				_liveEdgeSettingsUnavailable = false;
				_edgeTrackingChangeReported = false;
				if (_enableEdgeTracking)
					Debug.Log($"[TrackedBody] {_bodyId}: registered with {detection}.");

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

					// Registration gives the plugin the considered distance and its own standard deviations. When the body states other
					// standard deviations they are sent before the first step that reads them.
					_registeredDepth = DepthCorrespondenceSettings.AtRegistration(depthDistanceTolerance);
					_hasRegisteredDepth = true;
					_liveDepthSettingsUnavailable = false;
					_depthApplyPending = !DepthCorrespondenceSettings.From(_depthTracking).Equals(_registeredDepth);
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

				XRTrackerManager manager = XRTrackerManager.Instance;
				if (manager == null)
					return FTErrorCode.NOT_INITIALIZED;

				// Acquire native lock to ensure no tracking in progress during registration
				await manager.AcquireNativeLockAsync();

				// Bail if lifecycle changed while waiting for native lock
				if (_lifecycleVersion != versionAtStart)
				{
					manager.ReleaseNativeLock();
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
					if (result == FTErrorCode.OK &&
						!manager.TryPublishTrackedBodyRegistration(
							this,
							() => _lifecycleVersion == versionAtStart &&
								XRTrackerManager.Instance == manager,
							() =>
							{
								_isRegistered = true;
								_refiningBeforeAcceptance = false;
								_startedSinceRegistration = false;
								ApplyContributionAtRegistration();
								ResetStartSettle();
								ForgetStartSearch();
							},
							CancelNativeRegistration))
					{
						if (_lifecycleVersion == versionAtStart &&
							XRTrackerManager.Instance == manager)
						{
							CancelNativeRegistration();
							return FTErrorCode.NOT_INITIALIZED;
						}
					}
				}
				finally
				{
					manager.ReleaseNativeLock();
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

		private void CancelNativeRegistration()
		{
			int result = FTBridge.FT_UnregisterBody(_bodyId);
			if (result != FTErrorCode.OK)
			{
				Debug.LogWarning($"[TrackedBody] Failed to cancel registration for " +
					$"{_bodyId}: error {result}");
			}
		}

		private async void UnregisterBody()
		{
			if (!_isRegistered)
				return;

			int versionAtStart = _lifecycleVersion;

			// Yield one frame before unregistering. This allows a same-frame OnEnable
			// (for example, from visibility track thrashing during procedure step transitions)
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
				_hasRegisteredEdgeSettings = false;
				_detectionApplyPending = false;
				_hasRegisteredDepth = false;
				_depthApplyPending = false;
				_refiningBeforeAcceptance = false;
				_startedSinceRegistration = false;
				ResetStartSettle();
				ForgetStartSearch();
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
		/// <remarks>
		/// While the body is registered its geometry is fixed, so the bounds are computed once and reused
		/// until <see cref="UpdateGeometry"/> is called or the mesh filters or their meshes are replaced.
		/// A body that is not registered computes them again on every call.
		/// </remarks>
		public Bounds ComputeLocalBounds()
		{
			if (!_isRegistered)
			{
				_localBoundsCache?.Invalidate();
				return TrackedBodyUtils.ComputeLocalBounds(_meshFilters, transform);
			}

			_localBoundsCache ??= new TrackedBodyUtils.LocalBoundsCache();
			return _localBoundsCache.Get(_meshFilters, transform);
		}

		private TrackedBodyUtils.LocalBoundsCache _localBoundsCache;

		/// <summary>
		/// Gets the effective sphere radius for silhouette model generation.
		/// Returns the configured value if positive, otherwise computes auto value (0.8 x diameter).
		/// </summary>
		public float GetEffectiveSphereRadius()
		{
			return TrackedBodyUtils.GetEffectiveSphereRadius(_meshFilters, transform, _modelSettings.sphereRadius);
		}

		/// <summary>
		/// Gets the number of viewpoints selected by the nDivides setting.
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
