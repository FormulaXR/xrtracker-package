namespace IV.FormulaTracker
{
	/// <summary>
	/// Advanced override points for the tracker's own test and replay tooling. Each value is
	/// null in a shipped application, and null keeps the shipped behaviour. A value that is
	/// set applies to every manager and body that reads it afterwards and wins over the
	/// value on the instance.
	/// </summary>
	/// <remarks>
	/// The options that change a decision are the ones that ship as defaults, and each of them has an
	/// explicit value that switches it off. The others only record.
	/// </remarks>
	internal static class TrackingOverrides
	{
		/// <summary>Whether each written pose records where it came from.</summary>
		internal static bool? PoseProvenance;

		/// <summary>
		/// Whether the plugin renders fresh edge sites on every frame instead of reusing them. Null keeps the shipped behaviour, which is
		/// the reuse of the sites (the edge keyframe), always on. A value of true switches the reuse off for every edge body that registers
		/// afterwards. It is for the replay tooling that measures what the reuse is worth.
		/// </summary>
		internal static bool? EdgeKeyframeOff;

		/// <summary>
		/// Whether the rotation threshold of the edge keyframe reaches the tracker as the body saved it, with no lowest value of 10 degrees standing
		/// in the way. Null keeps the shipped behaviour, which lifts a smaller value to the lowest one. A value of true lets a value from 0.1 to 20
		/// degrees through, on the getter and on the setter of the property. It is for the replay tooling that measures what the lowest value costs.
		/// </summary>
		internal static bool? KeyframeRotationFloorOff;

		/// <summary>Which scalar the quality decisions of an edge body read.</summary>
		internal static TrackerDefaults.QualitySignalSource? QualitySignalSource;

		/// <summary>Which residual term the registration margin takes.</summary>
		internal static TrackerDefaults.MarginResidualTerm? MarginResidualTerm;

		/// <summary>
		/// Whether an edge body under pose fusion starts on the coverage score, on the rows in a row and at the start value that a
		/// body without pose fusion uses, while the registration margin stays the score of its acceptance, its hold and its
		/// give-up. Null keeps the shipped behaviour, which is on. A value of false starts the body on the registration margin.
		/// </summary>
		internal static bool? StartFromCoverageUnderFusion;

		/// <summary>
		/// Whether a stationary edge body under pose fusion puts the tracker back at the pose it is held at when the solve of a step is not
		/// accepted and stands further from that pose than the AR pose can explain. Null keeps the shipped behaviour, which is on.
		/// </summary>
		internal static bool? PredictionGate;

		/// <summary>
		/// Whether a stationary edge body under pose fusion writes the pose it is held at back into the tracker only after its first accepted
		/// fit. Before that, the pose it is held at is only the guide, and writing the guide back keeps the solve from leaving it. Null keeps
		/// today's behaviour, which writes the guide back.
		/// </summary>
		internal static bool? CorrectionAfterFirstFit;

		/// <summary>
		/// Whether a stationary edge body under pose fusion also accepts a solve that the registration margin does not clear, when its coverage
		/// score clears the start bar and the solve lies inside the prediction gate on several steps in a row. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? SoftAccept;

		/// <summary>
		/// Whether a stationary edge body under pose fusion takes its first accepted fit only once the solve has stood still for a few steps in a
		/// row. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? FirstAcceptGuard;

		/// <summary>
		/// Whether a stationary edge body under pose fusion takes its first accepted fit only once the coverage score of the start has stood at or above a
		/// bar of its own on as many steps in a row as the first-accept guard needs. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? FirstAcceptCoverageBar;

		/// <summary>The bar of the first-accept coverage bar. Null keeps the shipped bar.</summary>
		internal static float? FirstAcceptCoverageBarValue;

		/// <summary>
		/// Whether an edge body under pose fusion also accepts and stores a fit on the coverage score and the start and stop bars that a body without
		/// pose fusion reads, whatever the registration margin reads, and holds its lock while either the coverage score or the registration margin
		/// reads a good fit. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? CoverageDecides;

		/// <summary>
		/// Whether a stationary edge body under pose fusion stores no fit while the share of its sites that agree stands below a share of what its view
		/// normally reaches, so that a part of the body that something covers does not move the pose it is held at. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? DipHold;

		/// <summary>
		/// Whether a stationary edge body under pose fusion stores no fit on a step in which the AR camera moves the picture of the body further than a
		/// limit in pixels. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? FastMotionHold;

		/// <summary>The limit, in pixels between two steps, of the fast motion hold. Null keeps the shipped limit.</summary>
		internal static float? FastMotionHoldPixels;

		/// <summary>
		/// Whether the readout of a stationary edge body under pose fusion stays degraded until the pose on screen has reached the stored fit after a
		/// first accept or a re-entry. Null keeps the shipped behaviour.
		/// </summary>
		internal static bool? ReadoutWaitsForDisplay;

		/// <summary>
		/// Whether the prediction gate leaves a body that the start search started alone for the whole session, as it did before the gate served such a body
		/// once it had accepted a fit. Null keeps the shipped behaviour, which serves the body from its first accepted fit. It is for measuring the
		/// difference.
		/// </summary>
		internal static bool? PredictionGateSparesSearchStarts;

		/// <summary>
		/// Whether the poses a body holds in the AR session's world frame are carried to the new frame when the session moves its world between two
		/// steps. Null leaves it to the Follow AR World Jumps setting of the tracker manager, which is on by default. A value of true or false wins
		/// over that setting. A value of false leaves the poses where they were, as the package did before.
		/// </summary>
		internal static bool? ArWorldCarry;

		/// <summary>Whether a body without pose fusion tries the poses it was accepted at, after a loss.</summary>
		internal static bool? SeenPoseRecovery;

		/// <summary>Whether a stationary body under pose fusion gives up and takes back its lock.</summary>
		internal static bool? HoldRelease;

		/// <summary>
		/// Whether a body that is not tracking stays drawn at the seat it was placed at, and starts every step
		/// from that seat. Each step still refines from the seat and is judged on the refined record, but the
		/// refined pose is neither drawn nor taken up as the next seat, and the body takes it only on the step
		/// that accepts its start. No recovery route seats or moves a body that has never started. It applies
		/// with or without pose fusion. Null keeps the shipped behaviour, which is on for a body that tracks by the
		/// edge method without pose fusion.
		/// </summary>
		internal static bool? HoldStartPoseWhileSearching;

		/// <summary>
		/// How far the refinement of a body that holds its start pose may walk while the body searches, as the
		/// most steps one walk may run and the most pixels the carried pose may stand from the placed start pose,
		/// measured as the median on-screen displacement of the model's points. Inside both limits the tracker's
		/// last solve is carried into the next step instead of every step starting again from the placed start
		/// pose. When either limit is hit without a start, the next step starts again from the placed start pose
		/// and the walk begins again. The body stays drawn at the placed start pose throughout, and starts from
		/// the carried pose on the step that clears the start value for the usual number of steps in a row. It
		/// takes effect only while <see cref="HoldStartPoseWhileSearching"/> is on, for a body without pose fusion
		/// that is refined before its start, and only when both limits are above zero. Null keeps the shipped
		/// limits while the recovery walk is on.
		/// </summary>
		internal static (int Steps, float Pixels)? SearchWalkLimits;

		/// <summary>
		/// The floor and the full-scale share the coverage score maps a body's share of agreeing sites with, for
		/// every body that reads it afterwards. The coverage score is zero at the floor and one at the full-scale
		/// share. Null keeps the shipped map, which has a floor of zero and reads one at a share of 0.70 for an edge
		/// body without pose fusion.
		/// </summary>
		internal static (float Floor, float Full)? CoverageScale;

		/// <summary>
		/// Whether a recovery candidate is read as its own coverage score and accepted at the body's start
		/// threshold, for every body whose decisions read the coverage score. The candidate's coverage is the
		/// share of agreeing sites its own record carries, mapped with <see cref="CoverageScale"/>, and the pose
		/// the body holds is read the same way, so a candidate and the pose it would displace share one scale.
		/// Null is the shipped default, on for a body that tracks by the edge method without pose fusion.
		/// </summary>
		internal static bool? CoverageRecovery;

		/// <summary>
		/// Whether the recovery driver is stepped from the managed lifecycle's own loss. While a body that has
		/// started is not tracking, the driver counts the rows since the stop and tries its candidates. While the
		/// body is tracking, or has never started, the driver is stepped as tracked, so its clock stays at zero, its
		/// pending window is judged and the saved-view bank learns. Null is the shipped default, on for a body that
		/// tracks by the edge method without pose fusion.
		/// </summary>
		internal static bool? RecoveryFromLifecycleLoss;

		/// <summary>
		/// How many rows after a recovery candidate is seated on the coverage score its refined pose is judged. The
		/// candidate is then seated without first clearing the start score, the tracker refines it on the incoming
		/// rows, and on that row the body's coverage score has to read the body's start threshold or the seat is taken
		/// back. It acts only where <see cref="CoverageRecovery"/> reads the candidates. Null is the shipped default,
		/// three rows for a body that tracks by the edge method without pose fusion.
		/// </summary>
		internal static int? CoverageRecoveryRefineSteps;

		/// <summary>
		/// How many lost rows apart two kept-pose attempts are at least, counted in rows and never in time. The
		/// saved-view attempts keep their own cadence. Null is the shipped default, 5 rows for a body that tracks by the
		/// edge method without pose fusion, and a kept-pose attempt on every lost row that is due for any other body.
		/// </summary>
		internal static int? RecoveryKeptPoseCadence;

		/// <summary>
		/// Whether the limited search walk (<see cref="SearchWalkLimits"/>) applies to the refinement of a body that has
		/// started before and is lost, from the pose it holds, while no recovery seat is on its window. The refinement is
		/// carried from row to row up to the step limit and the pixel limit, the body stays drawn at the pose it holds,
		/// and a walk that does not end in a start begins again from exactly that pose. While this is on, the walk is
		/// engaged only for a body that has started before. Null is the shipped default, on for a body that tracks by the
		/// edge method without pose fusion.
		/// </summary>
		internal static bool? RecoveryWalk;

		/// <summary>
		/// The bar of the start margin gate. When set, the lifecycle starts a body without pose fusion (its first start,
		/// a restart after a loss, and the start of a recovery seat's window) only if the registration margin, read with
		/// its residual term taken from the median of the sub-pixel search offsets, stands at or above the bar on the row
		/// the start would be taken. Coverage still decides the start, the gate is a second condition. A row the margin
		/// cannot be read on withholds the start. A withheld start leaves the count of good rows where the coverage check
		/// left it, so the next row that passes both starts the body. A value that is not a number from 0 to 2 reads as
		/// null, which is no gate. Null is the shipped default, so no body reads a gate unless a run states one.
		/// </summary>
		internal static float? StartMarginGate;

		/// <summary>
		/// The bar of the final-row clause of a start, for the replay tooling. The clause starts an edge body without pose
		/// fusion that reads the coverage score (its first start, a restart after a loss, and the start of a recovery seat's
		/// window) only if the row on which the run of start rows completes also reads at or above the bar on the scaled
		/// coverage score the start bar compares. A withheld start leaves the count of good rows where the coverage check
		/// left it, so the next row that passes this clause and the start margin gate starts the body, and a row that fails
		/// coverage resets the count as always. A body under pose fusion and a body that tracks by the silhouette method are
		/// never touched. Null reads the confirm score of the body, which is 0.75 unless the body sets its own. A value that
		/// is set wins over the body's confirm score, and a value that is not a number from 0 to 1 reads as no clause.
		/// </summary>
		internal static float? StartFinalRowBar;

		/// <summary>
		/// Whether a body that is not tracking keeps its outline still while a recovery seat made on the retained pose
		/// is on its window. The seat is written to the retained pose and reaches the tracker as before, and the body is
		/// drawn at the pose the seat replaced until the window ends. A withdrawal changes nothing that is drawn, and a
		/// pass or a start draws the tracked pose as it always did. It changes what is drawn and nothing that is decided,
		/// and it has an effect only where the retained pose is what is drawn (a body that holds its start pose while
		/// searching, or a body under pose fusion). Null holds, which is the shipped default, and false draws the seat.
		/// </summary>
		internal static bool? RecoveryHoldDisplayDuringSeat;

		/// <summary>
		/// Whether the plugin keeps the sites of a pose it evaluated and puts them back when recovery evaluates the same
		/// pose again, instead of drawing the body again, and builds the downscaled search image of a frame once for the
		/// evaluations made on it. A hit is exact, so no decision changes. Null is the shipped default, on for a body that
		/// tracks by the edge method without pose fusion.
		/// </summary>
		internal static bool? RecoveryEvidenceSiteCache;

		/// <summary>
		/// Whether every cache hit also runs the evaluation that draws the body, and compares the two field by field.
		/// The evaluation that reused returns. It is for the replay tooling and takes effect only while
		/// <see cref="RecoveryEvidenceSiteCache"/> is on. Null compares nothing.
		/// </summary>
		internal static bool? RecoveryEvidenceSiteCacheVerify;

		/// <summary>
		/// Whether the plugin reads a clock for the stages of the start of a pose evaluation and publishes them. It
		/// changes no decision and exists for the replay records. Null reads no clock.
		/// </summary>
		internal static bool? RecordEvaluationStartTiming;

		/// <summary>
		/// Whether a body under pose fusion that is held (its lock given up) and in the picture re-checks the pose it is
		/// predicted at, the seat held in the world frame and composed with the camera, every few rows. Each cycle writes the
		/// seat, stops and starts the body's tracking so the step renders and predicts at the seat, refines for a few rows,
		/// and judges the refined pose of each of those rows on the start value and the margin gate. A pass takes the lock back at once on the
		/// refined pose, and the repeated seed of the hold is left to the cycle. Null is the shipped default, on for a body that
		/// tracks by the edge method under pose fusion and off for any other body. False turns it off for every body.
		/// </summary>
		internal static bool? FusionRecoveryRecheck;

		/// <summary>
		/// The number of rows in a row the re-check needs before it takes the lock back, each clearing the start value and the margin gate,
		/// in place of the earliest single passing row. It is the run of rows the lifecycle uses for a start when it reads 5. A cycle then
		/// starts the modality clean again on every row that fails. Null is the shipped default, a run of 5 for a body that tracks by the
		/// edge method under pose fusion and the earliest single row for any other body. A value below 2 leaves the earliest single row
		/// for every body. It acts only while the re-check is on.
		/// </summary>
		internal static int? FusionRecheckRun;

		/// <summary>
		/// The settled start, for every edge body without a parent that reads it afterwards. Before its first start the body is
		/// refined from the pose of the step before and not from the guide, and it starts only once the model's outline has moved
		/// by no more than the given pixels on the given number of steps in a row. A refined pose that stands further from the guide
		/// than the last member, in the same pixels, goes back to the guide. A value that is set wins over the body's own setting,
		/// and a number of rows of 0 switches it off. Null reads the body's own setting, which is off.
		/// </summary>
		internal static (int Rows, float Pixels, float MaxDriftPixels)? StartSettle;

		/// <summary>
		/// The whole-shape start search, for every edge body without a parent that reads it afterwards. Before its first start the
		/// plugin searches the camera image for the shape of the body every given number of tracking steps and proposes the best
		/// hypothesis as the pose the refinement starts from, and the start waits until the same winner was found on several
		/// searches in a row. The second member is the margin the winner must reach over the runner-up, and 0 means no margin bar.
		/// The third member is the score the winner must reach, and 0 means no score floor. The fourth member is the reach of the guide in
		/// pixels, and 0 treats every guide as out of reach. A value that is set wins over the body's own setting, and a period of 0
		/// switches the search off. Null reads the body's own setting, which is off.
		/// </summary>
		internal static (int PeriodSteps, float MarginBar, float ScoreFloor, float ReachPixels)? StartSearch;

		/// <summary>
		/// What the bank of the whole-shape start search is made of, for every edge body that reads it afterwards. The members are the half
		/// angle of the cone of views around the guide, the spacing between neighbouring views and the range of the turns in the image
		/// plane (all in degrees), the half angle of the fine stage in degrees, where 0 switches the fine stage off, the margin of the window
		/// in pixels of the search image, where 0 means the whole image, and the number of distances (1, 3 or 5). A value that is set wins
		/// over the body's own setting. Null reads the body's own setting.
		/// </summary>
		internal static (float ConeHalfAngle, float ConeSpacing, float RollRange, float FineHalfAngle, float WindowMargin, int DistanceCount)? StartSearchBank;

		/// <summary>
		/// Whether the plugin makes the other distances of the bank of the whole-shape start search by scaling the outline of one drawing of each
		/// view, or draws every distance. A value that is set wins over the body's own setting. Null reads the body's own setting.
		/// </summary>
		internal static bool? StartSearchScaleDistances;

		/// <summary>Returns every override to null, which is the shipped behaviour.</summary>
		internal static void Clear()
		{
			StartSearch = null;
			StartSearchBank = null;
			StartSearchScaleDistances = null;
			StartSettle = null;
			PoseProvenance = null;
			EdgeKeyframeOff = null;
			KeyframeRotationFloorOff = null;
			QualitySignalSource = null;
			MarginResidualTerm = null;
			StartFromCoverageUnderFusion = null;
			PredictionGate = null;
			CorrectionAfterFirstFit = null;
			SoftAccept = null;
			FirstAcceptGuard = null;
			FirstAcceptCoverageBar = null;
			FirstAcceptCoverageBarValue = null;
			CoverageDecides = null;
			DipHold = null;
			FastMotionHold = null;
			FastMotionHoldPixels = null;
			ReadoutWaitsForDisplay = null;
			PredictionGateSparesSearchStarts = null;
			ArWorldCarry = null;
			SeenPoseRecovery = null;
			HoldRelease = null;
			HoldStartPoseWhileSearching = null;
			SearchWalkLimits = null;
			CoverageScale = null;
			CoverageRecovery = null;
			RecoveryFromLifecycleLoss = null;
			CoverageRecoveryRefineSteps = null;
			RecoveryKeptPoseCadence = null;
			RecoveryWalk = null;
			StartMarginGate = null;
			StartFinalRowBar = null;
			RecoveryHoldDisplayDuringSeat = null;
			RecoveryEvidenceSiteCache = null;
			RecoveryEvidenceSiteCacheVerify = null;
			RecordEvaluationStartTiming = null;
			FusionRecoveryRecheck = null;
			FusionRecheckRun = null;
		}
	}
}
