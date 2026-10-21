namespace IV.FormulaTracker
{
	/// <summary>
	/// The value each run-wide option takes for the body that reads it, when nothing states one.
	/// </summary>
	/// <remarks>
	/// <para>Every override in <see cref="TrackingOverrides"/> is null in a shipped application, and null means the
	/// value below for a body that <see cref="AppliesTo(bool, bool)"/>. An explicit value (false, a number, a pair)
	/// wins over it, so a test or replay run can still switch any part off.</para>
	/// <para>A body is served by these defaults when it tracks by the edge method and no pose fusion is in play. Every
	/// other body reads each option as it always has. A body that tracks by the silhouette method keeps its own score
	/// and its own start and stop bars, because the registration margin and the coverage scale are measured on edge
	/// sites, which it has none of. It also keeps the start and the stop it has always had. It is judged at the pose it is
	/// placed at and never refined before its start, its guide is never walked or replaced, its bars are not corrected, it
	/// is reset to its guide when its tracking stops, and it has no recovery, start gate, confirm score, hold release or
	/// world-frame coast. A body under pose fusion keeps the registration margin as its score and the options
	/// above off.</para>
	/// <para>A body that tracks by the edge method under pose fusion is served by its own, smaller default (see
	/// <see cref="AppliesUnderFusion(bool, bool)"/>). Its held pose is re-checked against the image, and the lock is taken
	/// back only after a run of rows that each clear the start value and the margin gate.</para>
	/// </remarks>
	internal static class ShippedDefaults
	{
		/// <summary>The coverage share that reads as a score of one. The start bar of 0.65 then needs 45.5 percent of the sites.</summary>
		internal const float COVERAGE_FULL = 0.70f;

		/// <summary>The rows after a recovery candidate is seated on which its refined score is judged.</summary>
		internal const int COVERAGE_REFINE_ROWS = 3;

		/// <summary>The lost rows apart two kept-pose attempts are at least, counted in rows.</summary>
		internal const int KEPT_POSE_CADENCE_ROWS = 5;

		/// <summary>The most steps one walk of a lost body may run before it begins again from the pose it holds.</summary>
		internal const int WALK_STEPS = 60;

		/// <summary>The most pixels a walked pose may stand from the pose a lost body holds.</summary>
		internal const float WALK_PIXELS = 24f;

		/// <summary>The rows in a row the re-check of a held fusion body needs, each clearing the start value and the margin gate. It is the run of rows the lifecycle uses for a start.</summary>
		internal const int FUSION_RECHECK_RUN_ROWS = 5;

		/// <summary>
		/// Answers whether pose fusion is in play, in place of the manager, so a test can state either answer. Null
		/// asks the manager.
		/// </summary>
		internal static bool? PoseFusionForTests;

		/// <summary>
		/// Whether pose fusion is in play, which is the test the lifecycle uses to send a stationary body down the
		/// fusion path. It is false without an AR session, whatever the fusion setting reads.
		/// </summary>
		internal static bool PoseFusionActive() => PoseFusionActive(TrackedBodyManager.Instance);

		/// <summary>Whether pose fusion is in play for the given manager, which is the one that asks.</summary>
		internal static bool PoseFusionActive(TrackedBodyManager manager)
		{
			if (PoseFusionForTests.HasValue)
				return PoseFusionForTests.Value;
			return !ReferenceEquals(manager, null) && manager.IsARPoseFusionActive;
		}

		/// <summary>Whether the defaults serve a body that tracks by the given method under the given fusion state.</summary>
		internal static bool AppliesTo(bool edgeTracking, bool poseFusionActive) => edgeTracking && !poseFusionActive;

		/// <summary>Whether the defaults serve this body now.</summary>
		internal static bool AppliesTo(TrackedBody body) => AppliesTo(body, TrackedBodyManager.Instance);

		/// <summary>Whether the defaults serve this body now, with pose fusion read from the given manager.</summary>
		internal static bool AppliesTo(TrackedBody body, TrackedBodyManager manager) =>
			!ReferenceEquals(body, null) && AppliesTo(body.EnableEdgeTracking, PoseFusionActive(manager));

		/// <summary>Whether the fusion defaults serve a body that tracks by the given method under the given fusion state, which is an edge body with pose fusion in play.</summary>
		internal static bool AppliesUnderFusion(bool edgeTracking, bool poseFusionActive) => edgeTracking && poseFusionActive;

		/// <summary>Whether the fusion defaults serve this body now, with pose fusion read from the given manager.</summary>
		internal static bool AppliesUnderFusion(TrackedBody body, TrackedBodyManager manager) =>
			!ReferenceEquals(body, null) && AppliesUnderFusion(body.EnableEdgeTracking, PoseFusionActive(manager));

		/// <summary>Whether a held body under pose fusion re-checks its predicted pose against the image.</summary>
		internal static bool FusionRecoveryRecheck(bool appliesUnderFusion) => TrackingOverrides.FusionRecoveryRecheck ?? appliesUnderFusion;

		/// <summary>The rows in a row the re-check needs before it takes the lock back, or null for the earliest single passing row.</summary>
		internal static int? FusionRecheckRun(bool appliesUnderFusion) =>
			TrackingOverrides.FusionRecheckRun ?? (appliesUnderFusion ? FUSION_RECHECK_RUN_ROWS : (int?)null);

		/// <summary>The scalar an edge body's decisions read when nothing states one.</summary>
		internal static TrackerDefaults.QualitySignalSource QualitySignalSourceFor(bool poseFusionActive) =>
			poseFusionActive
				? TrackerDefaults.QualitySignalSource.RegistrationMargin
				: TrackerDefaults.QUALITY_SIGNAL_SOURCE;

		/// <summary>
		/// Whether an edge body under pose fusion starts on the coverage score, on the rows in a row and at the start value of a body
		/// without pose fusion, and keeps the registration margin for its acceptance, hold and give-up. It is on by default, because
		/// the margin's residual bar is set for a crisp picture and reads a right pose on a soft one as a poor start. A value that is set
		/// wins over it.
		/// </summary>
		internal static bool StartFromCoverageUnderFusion(bool appliesUnderFusion) =>
			appliesUnderFusion && (TrackingOverrides.StartFromCoverageUnderFusion ?? true);

		/// <summary>
		/// Whether a stationary edge body under pose fusion puts the tracker back at the pose it is held at when a solve that is not
		/// accepted stands further from that pose than the AR pose can explain. It is on by default. A value that is set wins over it.
		/// </summary>
		internal static bool PredictionGate(bool appliesUnderFusion) =>
			appliesUnderFusion && (TrackingOverrides.PredictionGate ?? true);

		/// <summary>
		/// Whether the poses a body holds in the AR session's world frame follow the world when the session moves it between two steps. The Follow
		/// AR World Jumps setting of the tracker manager decides, and it is on by default. A value that a run has set wins over it.
		/// </summary>
		/// <param name="followSetting">The Follow AR World Jumps setting of the tracker manager.</param>
		internal static bool ArWorldCarry(bool followSetting) => TrackingOverrides.ArWorldCarry ?? followSetting;

		/// <summary>
		/// Whether a stationary edge body under pose fusion also accepts a solve that the registration margin does not clear, when its coverage
		/// score and the prediction gate verify the solve on several steps in a row. The setting of the body (Accept Steady Fits, on by default)
		/// decides, and a value that a run has set wins over it.
		/// </summary>
		internal static bool SoftAccept(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.SoftAccept ?? bodySetting);

		/// <summary>
		/// Whether a stationary edge body under pose fusion takes its first accepted fit only once the solve has stood still for a few steps in a
		/// row, so a fit that is still moving is not locked in. It is on by default, with the Accept Steady Fits setting of the body and the First Accept
		/// Guard toggle of the body (see <see cref="FirstAcceptGuardSetting(TrackedBody)"/>). A value that is set wins over it.
		/// </summary>
		internal static bool FirstAcceptGuard(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.FirstAcceptGuard ?? (FIRST_ACCEPT_GUARD_SHIPPED && bodySetting));

		/// <summary>
		/// Whether an edge body under pose fusion also accepts and stores a fit on the coverage score and the start and stop bars that a body without
		/// pose fusion reads, whatever the registration margin reads, so that the drawn pose follows the fit whenever a body without pose fusion would
		/// read Tracked. The registration margin keeps accepting what it accepted before, and the lock is held while either the coverage score or the
		/// registration margin reads a good fit, so an object whose coverage reads low is not given up while the margin still reads a good fit. For a
		/// stationary body it follows the Accept Steady Fits setting of the body, because both accept a fit on the coverage score. A value that is set
		/// wins over it.
		/// </summary>
		internal static bool CoverageDecides(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.CoverageDecides ?? (COVERAGE_DECIDES_SHIPPED && bodySetting));

		/// <summary>Whether the coverage score decides accept, store, hold and give-up under pose fusion, by default. It is on, and a stationary body follows its Accept Steady Fits setting.</summary>
		internal const bool COVERAGE_DECIDES_SHIPPED = true;

		/// <summary>
		/// Whether a stationary edge body under pose fusion stores no fit while its view dips, which is while the share of its sites that agree stands below
		/// <see cref="TrackingHealthSettings.ExpectationUpdateHealth"/> of what the view normally reaches. The held pose then stays where it is, in the world
		/// frame, until the view recovers, so a part of the body that something covers cannot walk the pose along the directions that the rest of the body
		/// leaves free. It follows the Accept Steady Fits setting of the body, because both decide which fits become the pose, and the Hold During View Dips
		/// toggle of the body (see <see cref="DipHoldSetting(TrackedBody)"/>). A value that is set wins over it.
		/// </summary>
		internal static bool DipHold(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.DipHold ?? (DIP_HOLD_SHIPPED && bodySetting));

		/// <summary>Whether a stationary body stores no fit while its view dips, by default. It is on, and it follows the Accept Steady Fits setting of the body.</summary>
		internal const bool DIP_HOLD_SHIPPED = true;

		/// <summary>
		/// Whether a stationary edge body under pose fusion stores no fit on a step in which the AR camera moves the picture of the body further than a limit
		/// in pixels. The fit of a step that fast is blurred, and the pose the body is held at is carried by the AR camera instead. The first fit and the fits of
		/// the warmup are never held back. It follows the Accept Steady Fits setting of the body, because both decide which fits become the pose, and the Hold During
		/// Fast Motion toggle of the body (see <see cref="FastMotionHoldSetting(TrackedBody)"/>). A value that is set wins over it.
		/// </summary>
		internal static bool FastMotionHold(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.FastMotionHold ?? (FAST_MOTION_HOLD_SHIPPED && bodySetting));

		/// <summary>Whether a stationary body stores no fit while the AR camera moves its picture fast, by default. It is on, and it follows the Accept Steady Fits setting of the body.</summary>
		internal const bool FAST_MOTION_HOLD_SHIPPED = true;

		/// <summary>
		/// Whether the readout of a stationary edge body under pose fusion stays <see cref="TrackingConfidence.Degraded"/> after a first accept, and after a
		/// re-entry that seats the display from a held or a placed pose, until the pose on screen has first come within the still distance of the settle pull
		/// of the stored fit. It changes the readout and nothing else. It follows the Accept Steady Fits setting of the body. A value that is set wins over it.
		/// </summary>
		internal static bool ReadoutWaitsForDisplay(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.ReadoutWaitsForDisplay ?? (READOUT_WAITS_FOR_DISPLAY_SHIPPED && bodySetting));

		/// <summary>Whether the readout waits for the pose on screen to reach the stored fit, by default. It is off, and it follows the Accept Steady Fits setting of the body.</summary>
		internal const bool READOUT_WAITS_FOR_DISPLAY_SHIPPED = false;

		/// <summary>The limit, in pixels between two steps, above which the AR camera moves the picture too fast for the fit of the step to be stored.</summary>
		internal static float FastMotionHoldPixels => TrackingOverrides.FastMotionHoldPixels ?? TrackerDefaults.FAST_MOTION_HOLD_PIXELS;

		/// <summary>Whether the first-accept guard ships on. It follows the Accept Steady Fits setting of the body and its own toggle.</summary>
		internal const bool FIRST_ACCEPT_GUARD_SHIPPED = true;

		/// <summary>
		/// The setting of the body that the view dip hold follows, which is Accept Steady Fits and Hold During View Dips, both on. It is the second argument of
		/// <see cref="DipHold(bool, bool)"/>, and a value that a run has set still wins over it.
		/// </summary>
		internal static bool DipHoldSetting(TrackedBody body) => body.AcceptSteadyFits && body.HoldDuringViewDip;

		/// <summary>
		/// The setting of the body that the fast motion hold follows, which is Accept Steady Fits and Hold During Fast Motion, both on. It is the second argument of
		/// <see cref="FastMotionHold(bool, bool)"/>, and a value that a run has set still wins over it.
		/// </summary>
		internal static bool FastMotionHoldSetting(TrackedBody body) => body.AcceptSteadyFits && body.HoldDuringFastMotion;

		/// <summary>
		/// The setting of the body that the first-accept guard follows, which is Accept Steady Fits and First Accept Guard, both on. It is the second argument of
		/// <see cref="FirstAcceptGuard(bool, bool)"/>, and a value that a run has set still wins over it.
		/// </summary>
		internal static bool FirstAcceptGuardSetting(TrackedBody body) => body.AcceptSteadyFits && body.FirstAcceptGuard;

		/// <summary>
		/// Whether a stationary edge body under pose fusion takes no fit as its first accepted fit until the coverage score of the start, on the scale of
		/// its start bar, has stood at or above a bar of its own on as many steps in a row as the first-accept guard needs. Every way a fit can be accepted
		/// waits for it (the strict registration margin, the coverage decision and Accept Steady Fits), and until it clears the fit is withheld and the
		/// outline stays at the placed guide. The start bar lets a guide start on a score that a fit far from the object can reach as well, and the
		/// first-accept guard lets go after the stabilization rows, so without this bar a wrong fit that stands still is shown as tracked. Once the body
		/// holds an accepted fit the bar has no effect. It follows the Accept Steady Fits setting of the body, because both decide which fits become the
		/// pose. A value that is set wins over it.
		/// </summary>
		internal static bool FirstAcceptCoverageBar(bool appliesUnderFusion, bool bodySetting) =>
			appliesUnderFusion && (TrackingOverrides.FirstAcceptCoverageBar ?? (FIRST_ACCEPT_COVERAGE_BAR_SHIPPED && bodySetting));

		/// <summary>Whether the first-accept coverage bar ships on. It is off, and it follows the Accept Steady Fits setting of the body.</summary>
		internal const bool FIRST_ACCEPT_COVERAGE_BAR_SHIPPED = false;

		/// <summary>
		/// The bar the coverage score of the start has to clear before the first accept, on the scale of the start bar of 0.65, when the first-accept coverage bar
		/// is on. It lies where the score of a fit that is far from the object ends and the score of the fit of a sharp picture begins, and a body whose picture is
		/// soft enough to read lower than this on a right fit never takes its first accept.
		/// </summary>
		internal const float FIRST_ACCEPT_COVERAGE_BAR = 0.78f;

		/// <summary>The bar the first-accept coverage bar reads now, which is the one a run set or the shipped one.</summary>
		internal static float FirstAcceptCoverageBarValue => TrackingOverrides.FirstAcceptCoverageBarValue ?? FIRST_ACCEPT_COVERAGE_BAR;

		/// <summary>Whether the pose a stationary body is held at is written back into the tracker only after its first accepted fit. Off by default.</summary>
		internal static bool CorrectionAfterFirstFit => TrackingOverrides.CorrectionAfterFirstFit ?? false;

		/// <summary>The floor and the full-scale share the coverage score maps with.</summary>
		internal static (float Floor, float Full) CoverageScale(bool applies) =>
			TrackingOverrides.CoverageScale ?? (0f, applies ? COVERAGE_FULL : TrackerDefaults.EDGE_QUALITY_COVERAGE_MAX);

		/// <summary>Whether a recovery candidate is read as its own coverage score.</summary>
		internal static bool CoverageRecovery(bool applies) => TrackingOverrides.CoverageRecovery ?? applies;

		/// <summary>Whether a body that is not tracking stays drawn at the pose it was placed at.</summary>
		internal static bool HoldStartPose(bool applies) => TrackingOverrides.HoldStartPoseWhileSearching ?? applies;

		/// <summary>
		/// Whether an edge body under pose fusion that has not started yet stays drawn at the pose it was placed at. A body that is
		/// stationary does by its own path of the lifecycle, and a body that is not does here, so both judge a start from the
		/// guide the person placed and not from a pose that the refinement carried away from it. Once the body has started the
		/// answer is no, and a body that was lost draws what it did before. A value that is set wins over it.
		/// </summary>
		internal static bool HoldStartPoseUnderFusion(bool appliesUnderFusion, bool startedSinceRegistration) =>
			TrackingOverrides.HoldStartPoseWhileSearching ?? (appliesUnderFusion && !startedSinceRegistration);

		/// <summary>
		/// Whether a body that is not tracking is drawn where it stood while a recovery seat made on its retained pose is on
		/// its window, so that it moves only when tracking starts again. It is on for every body. It changes what is drawn and
		/// no decision, and <see cref="TrackedBodyManager.DisplayHoldApplies"/> says where it has an effect. A value that is
		/// set switches it off, because a value that is set wins over the default.
		/// </summary>
		internal static bool RecoveryHoldDisplayDuringSeat() => TrackingOverrides.RecoveryHoldDisplayDuringSeat ?? true;

		/// <summary>Whether the recovery driver is stepped from the managed lifecycle's own loss.</summary>
		internal static bool RecoveryFromLifecycleLoss(bool applies) => TrackingOverrides.RecoveryFromLifecycleLoss ?? applies;

		/// <summary>The rows a recovery seat is refined before it is judged, or null for none.</summary>
		internal static int? CoverageRecoveryRefineSteps(bool applies) =>
			TrackingOverrides.CoverageRecoveryRefineSteps ?? (applies ? COVERAGE_REFINE_ROWS : (int?)null);

		/// <summary>The lost rows apart two kept-pose attempts are at least, or null for every row that is due.</summary>
		internal static int? RecoveryKeptPoseCadence(bool applies) =>
			TrackingOverrides.RecoveryKeptPoseCadence ?? (applies ? KEPT_POSE_CADENCE_ROWS : (int?)null);

		/// <summary>Whether the plugin reuses the edge sites across frames, which is always the case unless a run asks for fresh sites.</summary>
		internal static bool EdgeKeyframe() => !(TrackingOverrides.EdgeKeyframeOff ?? false);

		/// <summary>The lowest rotation threshold of the edge keyframe, in degrees, that a run can ask for once it switches the lowest value of the property off.</summary>
		internal const float LOWEST_KEYFRAME_ROTATION_DEG = 0.1f;

		/// <summary>
		/// The lowest rotation threshold of the edge keyframe in degrees, which is <see cref="EdgeTrackingSettings.MIN_KEYFRAME_ROTATION_DEG"/> unless a run
		/// switches it off, and then <see cref="LOWEST_KEYFRAME_ROTATION_DEG"/>.
		/// </summary>
		internal static float KeyframeRotationFloor() =>
			(TrackingOverrides.KeyframeRotationFloorOff ?? false) ? LOWEST_KEYFRAME_ROTATION_DEG : EdgeTrackingSettings.MIN_KEYFRAME_ROTATION_DEG;

		/// <summary>Whether the plugin keeps the sites of the poses recovery evaluates.</summary>
		internal static bool RecoveryEvidenceSiteCache(bool applies) => TrackingOverrides.RecoveryEvidenceSiteCache ?? applies;

		/// <summary>Whether a lost body that has started before carries its refinement from row to row.</summary>
		internal static bool RecoveryWalk(bool applies) => TrackingOverrides.RecoveryWalk ?? applies;

		/// <summary>
		/// The limits of the walk. An explicit pair always applies as given. Without one the shipped limits apply only
		/// while the recovery walk is in force, because limits with no recovery walk would walk the first start of every
		/// body.
		/// </summary>
		internal static (int Steps, float Pixels)? SearchWalkLimits(bool applies) =>
			TrackingOverrides.SearchWalkLimits ??
			(applies && RecoveryWalk(applies)
				? (WALK_STEPS, WALK_PIXELS)
				: ((int, float)?)null);

		/// <summary>
		/// The bar of the start margin gate, which is the bar a run states and null for none. No body reads a gate unless a
		/// run states one, because a start already needs the coverage score on five good rows.
		/// </summary>
		internal static float? StartMarginGateBar() =>
			StartMarginGate.BarFor(TrackingOverrides.StartMarginGate);
	}
}
