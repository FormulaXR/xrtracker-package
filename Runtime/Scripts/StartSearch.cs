using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What the whole-shape search of a start read on the latest step of a body that has not started. The search scores many
	/// prerendered outline views of the body at every image position and keeps the best hypothesis, with its score and the
	/// margin by which it beats the best hypothesis that stands far from it.
	/// </summary>
	public readonly struct StartSearchReading
	{
		internal StartSearchReading(float[] raw)
		{
			Valid = raw[StartSearchLayout.VALID] >= 0.5f;
			Serial = Whole(raw[StartSearchLayout.SERIAL]);
			SearchedThisStep = raw[StartSearchLayout.SEARCHED_THIS_STEP] >= 0.5f;
			Steps = Whole(raw[StartSearchLayout.STEPS]);
			BankReady = raw[StartSearchLayout.BANK_READY] >= 0.5f;
			BankTemplates = Whole(raw[StartSearchLayout.BANK_TEMPLATES]);
			BankBuildMs = raw[StartSearchLayout.BANK_BUILD_MS];
			SearchMs = raw[StartSearchLayout.SEARCH_MS];
			PrepareHalfMs = raw[StartSearchLayout.PREPARE_HALF_MS];
			PrepareFullMs = raw[StartSearchLayout.PREPARE_FULL_MS];
			CoarseMs = raw[StartSearchLayout.COARSE_MS];
			RefineMs = raw[StartSearchLayout.REFINE_MS];
			Score = raw[StartSearchLayout.SCORE];
			RunnerScore = raw[StartSearchLayout.RUNNER_SCORE];
			Margin = raw[StartSearchLayout.MARGIN];
			OriginX = raw[StartSearchLayout.ORIGIN_X];
			OriginY = raw[StartSearchLayout.ORIGIN_Y];
			DepthMetres = raw[StartSearchLayout.DEPTH_M];
			TemplateIndex = Whole(raw[StartSearchLayout.TEMPLATE_INDEX]);
			ViewTiltDegrees = raw[StartSearchLayout.VIEW_TILT_DEG];
			ViewAzimuthDegrees = raw[StartSearchLayout.VIEW_AZIMUTH_DEG];
			RollDegrees = raw[StartSearchLayout.ROLL_DEG];
			DistanceFactor = raw[StartSearchLayout.DISTANCE_FACTOR];
			WinnerTrackerPose = PoseAt(raw, StartSearchLayout.TRACKER_POSE);
			WinnerCameraPose = PoseAt(raw, StartSearchLayout.CAMERA_POSE);
			SeatWritten = raw[StartSearchLayout.SEAT_WRITTEN] >= 0.5f;
			PositionsSearched = Whole(raw[StartSearchLayout.POSITIONS_SEARCHED]);
			PositionsGrid = Whole(raw[StartSearchLayout.POSITIONS_GRID]);
			Status = Whole(raw[StartSearchLayout.STATUS]);
		}

		/// <summary>Whether at least one search has finished since the search was switched on and a winner exists.</summary>
		public bool Valid { get; }

		/// <summary>How many searches have finished since the search was switched on. A new number means a new search finished on the latest step.</summary>
		public int Serial { get; }

		/// <summary>Whether a search ran on the latest tracking step.</summary>
		public bool SearchedThisStep { get; }

		/// <summary>How many tracking steps have run since the search was switched on.</summary>
		public int Steps { get; }

		/// <summary>Whether the prerendered views exist.</summary>
		public bool BankReady { get; }

		/// <summary>How many outline templates the prerendered views make.</summary>
		public int BankTemplates { get; }

		/// <summary>Milliseconds the prerendered views took to build.</summary>
		public float BankBuildMs { get; }

		/// <summary>Milliseconds of the latest search, all stages together.</summary>
		public float SearchMs { get; }

		/// <summary>Milliseconds the half-resolution image preparation of the latest search took.</summary>
		public float PrepareHalfMs { get; }

		/// <summary>Milliseconds the full-resolution image preparation of the latest search took.</summary>
		public float PrepareFullMs { get; }

		/// <summary>Milliseconds the coarse stage of the latest search took.</summary>
		public float CoarseMs { get; }

		/// <summary>Milliseconds the refine stage and the runner-up search of the latest search took.</summary>
		public float RefineMs { get; }

		/// <summary>The score of the winning hypothesis, from 0 to 1.</summary>
		public float Score { get; }

		/// <summary>The score of the best hypothesis that stands far from the winner.</summary>
		public float RunnerScore { get; }

		/// <summary>The score of the winner divided by the score of the runner-up. It reads 99 when there is no runner-up.</summary>
		public float Margin { get; }

		/// <summary>The horizontal pixel of the camera image that the winner puts the origin of the body on.</summary>
		public float OriginX { get; }

		/// <summary>The vertical pixel of the camera image that the winner puts the origin of the body on.</summary>
		public float OriginY { get; }

		/// <summary>The depth of the winner along the optical axis, in metres.</summary>
		public float DepthMetres { get; }

		/// <summary>The index of the outline template that the winner matched.</summary>
		public int TemplateIndex { get; }

		/// <summary>The tilt of the view that the winner matched, in degrees.</summary>
		public float ViewTiltDegrees { get; }

		/// <summary>The azimuth of the view that the winner matched, in degrees.</summary>
		public float ViewAzimuthDegrees { get; }

		/// <summary>The roll of the winner about the optical axis, in degrees.</summary>
		public float RollDegrees { get; }

		/// <summary>The distance of the winner as a factor of the distance the view was built at.</summary>
		public float DistanceFactor { get; }

		/// <summary>The pose of the winner in the tracker's frame, the frame <c>FT_GetBodyPose</c> returns, in the geometry frame.</summary>
		public FTTrackingPose WinnerTrackerPose { get; }

		/// <summary>The pose of the winner in the camera frame, in the geometry frame.</summary>
		public FTTrackingPose WinnerCameraPose { get; }

		/// <summary>How many positions of the grid the latest search looked at for one template, on average. It is the whole grid when the search had no window.</summary>
		public int PositionsSearched { get; }

		/// <summary>How many positions the whole grid has.</summary>
		public int PositionsGrid { get; }

		/// <summary>Whether the winner has been written as the seat of the latest step.</summary>
		public bool SeatWritten { get; }

		/// <summary>
		/// How the latest search went. It reads 0 for a search that went well, 1 when there was no camera image, 2 when the guide
		/// was behind the camera or not usable, 3 when the views could not be built and 4 when the search failed.
		/// </summary>
		public int Status { get; }

		/// <summary>
		/// Converts the winner into a position and a rotation in the frame of the tracker, and says whether it can be used. The winner is
		/// converted only when the reading holds one. A reading that has no winner yet is all zeros, and a rotation of zero length cannot be
		/// converted. A winner with a rotation of zero length or with a number that is not finite reads as not usable as well.
		/// </summary>
		internal bool TryGetWinner(out Vector3 position, out Quaternion rotation)
		{
			if (Valid)
				return WinnerTrackerPose.TryGetTransformation(out position, out rotation);
			position = Vector3.zero;
			rotation = Quaternion.identity;
			return false;
		}

		private static int Whole(float value) =>
			float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.RoundToInt(value);

		private static FTTrackingPose PoseAt(float[] raw, int first) => new FTTrackingPose
		{
			pos_x = raw[first],
			pos_y = raw[first + 1],
			pos_z = raw[first + 2],
			rot_x = raw[first + 3],
			rot_y = raw[first + 4],
			rot_z = raw[first + 5],
			rot_w = raw[first + 6],
		};
	}

	/// <summary>
	/// What the start gate decided on the latest step of a body that the search reads. A start passes only when the same
	/// winner was found on enough searches in a row, and when the margin reaches the bar if the body has one.
	/// </summary>
	public readonly struct StartSearchVerdict
	{
		internal StartSearchVerdict(int streak, float agreePixels, bool passes)
		{
			Streak = streak;
			AgreePixels = agreePixels;
			Passes = passes;
		}

		/// <summary>How many searches in a row have found a winner within the agreement distance of the winner before it.</summary>
		public int Streak { get; }

		/// <summary>
		/// How far the latest winner stands from the winner before it, as the median on-screen displacement of the model's points in
		/// pixels. It is not a number when there was no winner before it or the distance could not be measured.
		/// </summary>
		public float AgreePixels { get; }

		/// <summary>Whether the gate lets a start through.</summary>
		public bool Passes { get; }
	}

	/// <summary>
	/// Where each value sits in the reading that <c>FT_GetStartSearchReading</c> fills. The layout lives here and nowhere else.
	/// </summary>
	internal static class StartSearchLayout
	{
		internal const int VALID = 0;
		internal const int SERIAL = 1;
		internal const int SEARCHED_THIS_STEP = 2;
		internal const int STEPS = 3;
		internal const int BANK_READY = 4;
		internal const int BANK_TEMPLATES = 5;
		internal const int BANK_BUILD_MS = 6;
		internal const int SEARCH_MS = 7;
		internal const int PREPARE_HALF_MS = 8;
		internal const int PREPARE_FULL_MS = 9;
		internal const int COARSE_MS = 10;
		internal const int REFINE_MS = 11;
		internal const int SCORE = 12;
		internal const int RUNNER_SCORE = 13;
		internal const int MARGIN = 14;
		internal const int ORIGIN_X = 15;
		internal const int ORIGIN_Y = 16;
		internal const int DEPTH_M = 17;
		internal const int TEMPLATE_INDEX = 18;
		internal const int VIEW_TILT_DEG = 19;
		internal const int VIEW_AZIMUTH_DEG = 20;
		internal const int ROLL_DEG = 21;
		internal const int DISTANCE_FACTOR = 22;

		/// <summary>The first of seven floats, three for the position and four for the rotation, in the tracker's frame.</summary>
		internal const int TRACKER_POSE = 23;

		/// <summary>The first of seven floats, three for the position and four for the rotation, in the camera frame.</summary>
		internal const int CAMERA_POSE = 30;

		internal const int SEAT_WRITTEN = 37;

		/// <summary>How many positions of the grid were searched for one template, on average.</summary>
		internal const int POSITIONS_SEARCHED = 45;

		/// <summary>How many positions the whole grid has.</summary>
		internal const int POSITIONS_GRID = 46;
		internal const int STATUS = 38;
	}

	/// <summary>Which start a body takes while the whole-shape search holds it.</summary>
	public enum StartSearchPath
	{
		/// <summary>No winner has been measured against the guide yet, or none has stood within reach or far enough to decide, so the body's own start runs as it does without the search.</summary>
		Unknown = 0,

		/// <summary>The guide stands within reach of the winner, so the body's own start runs as it does without the search.</summary>
		InReach = 1,

		/// <summary>The winner stands farther than the reach from the guide, so the body is seeded from the winner and settles from it.</summary>
		Far = 2,
	}

	/// <summary>
	/// The rules of the start search. Before a body's first start the plugin searches the camera image for the whole shape of the
	/// body and proposes the best hypothesis as the pose the tracker's refinement starts from. A proposal is only a guess, so the
	/// start is withheld until the same winner was found on several searches in a row, and until the winner beats the
	/// runner-up by a margin and reaches a score floor (each can be set to 0, which sets no bar). A guide that already stands within
	/// reach of the winner is left alone, and the body starts as it does without the search, which is also what it does while no winner
	/// has been measured. The verification of the pose stays with the start bars.
	/// </summary>
	internal static class StartSearch
	{
		/// <summary>
		/// The default number of tracking steps between two searches. One search of a bank of the usual size takes tens of milliseconds on a
		/// desktop, so a search on every step would spend more than a step can spare.
		/// </summary>
		internal const int DEFAULT_PERIOD_STEPS = 2;

		internal const int MIN_PERIOD_STEPS = 1;
		internal const int MAX_PERIOD_STEPS = 60;

		/// <summary>
		/// The default margin bar. A winner that beats the best hypothesis standing far from it by less than this does not let a start
		/// through, because a winner that barely beats a rival standing far from it is an ambiguous match. A bar of 0 sets no bar.
		/// </summary>
		internal const float DEFAULT_MARGIN_BAR = 1.4f;

		internal const float MAX_MARGIN_BAR = 3f;

		/// <summary>
		/// The default score floor. A winner whose score is under this does not let a start through, whatever its margin. The score is
		/// the share of the outline's points that find an edge of the same direction, so a low score means the outline matches the picture at
		/// too few of its points. A floor of 0 sets no floor.
		/// </summary>
		internal const float DEFAULT_SCORE_FLOOR = 0.6f;

		internal const float MAX_SCORE_FLOOR = 1f;

		/// <summary>
		/// The default reach of the guide, as the median on-screen displacement in pixels between the guide and the winner of a search. A
		/// guide that stands within it of the winner is in reach, so the body keeps the start it has without the search. A winner that stands
		/// farther is where the body starts from instead. A reach of 0 treats every guide as out of reach.
		/// </summary>
		internal const float DEFAULT_REACH_PIXELS = 16f;

		internal const float MAX_REACH_PIXELS = 200f;

		/// <summary>
		/// The default half angle of the cone of views around the guide, in degrees. The bank holds the guide's own view and rings of
		/// directions out to this angle at about the spacing between neighbours, so a half angle of 5 degrees at a spacing of 5 degrees
		/// is 7 views.
		/// </summary>
		internal const float DEFAULT_CONE_HALF_ANGLE = 5f;

		internal const float MAX_CONE_HALF_ANGLE = 45f;

		/// <summary>The default spacing between neighbouring views of the cone, in degrees. The turns in the image plane are made at this step too.</summary>
		internal const float DEFAULT_CONE_SPACING = 5f;

		internal const float MIN_CONE_SPACING = 2.5f;
		internal const float MAX_CONE_SPACING = 20f;

		/// <summary>
		/// The default range of the turns in the image plane, in degrees. The turns are zero and plus and minus each step of the spacing out to
		/// this value, so the zero turn is always held, and a range that is not a whole number of steps stops at the last whole step inside it.
		/// </summary>
		internal const float DEFAULT_ROLL_RANGE = 10f;

		internal const float MAX_ROLL_RANGE = 45f;

		/// <summary>
		/// The default half angle of the fine stage, in degrees. The fine stage scores the views within this angle of the winner at a step of
		/// 2.5 degrees. A value of 0 switches the fine stage off, which is the default, because the winner of a bank at a spacing of 5
		/// degrees already stands within about that spacing of the true tilt.
		/// </summary>
		internal const float DEFAULT_FINE_HALF_ANGLE = 0f;

		internal const float MAX_FINE_HALF_ANGLE = 15f;

		/// <summary>
		/// The default margin, in pixels of the search image (its longer side is at most 640 pixels), by which the box of the guide's outline
		/// is grown to make the window of the image the search looks at. A template is placed only where its own outline box lies inside the
		/// window, so a guide whose object stands within about this far of it is found. A margin of 0 searches the whole image.
		/// </summary>
		internal const float DEFAULT_WINDOW_MARGIN = 96f;

		internal const float MAX_WINDOW_MARGIN = 640f;

		/// <summary>The default number of distances the bank holds around the guide's own, which are 0.92, 1.0 and 1.09 times it. The other values are 1 and 5.</summary>
		internal const int DEFAULT_DISTANCE_COUNT = 3;

		/// <summary>
		/// Whether the plugin makes the other distances of the bank by scaling one drawing of each view, in place of drawing every distance. It
		/// does not by default. Scaling draws each view once, at the guide's own distance, and scales the outline of that drawing about the point
		/// the body's origin projects to. That is a third of the drawings at three distances, but a scaled outline only approximates the view
		/// drawn at that distance, so drawing every distance is the default.
		/// </summary>
		internal const bool DEFAULT_SCALE_DISTANCES = false;

		/// <summary>What the bank of the plugin's search is made of.</summary>
		internal readonly struct BankSetting : System.IEquatable<BankSetting>
		{
			internal BankSetting(float coneHalfAngle, float coneSpacing, float rollRange, float fineHalfAngle, float windowMargin, int distanceCount)
			{
				ConeHalfAngle = coneHalfAngle;
				ConeSpacing = coneSpacing;
				RollRange = rollRange;
				FineHalfAngle = fineHalfAngle;
				WindowMargin = windowMargin;
				DistanceCount = distanceCount;
			}

			internal float ConeHalfAngle { get; }
			internal float ConeSpacing { get; }
			internal float RollRange { get; }

			/// <summary>The half angle of the fine stage. A value of 0 means no fine stage.</summary>
			internal float FineHalfAngle { get; }

			/// <summary>The margin, in pixels of the search image, that the box of the guide's outline is grown by. A value of 0 means the whole image.</summary>
			internal float WindowMargin { get; }

			/// <summary>How many distances the bank holds, which is 1, 3 or 5.</summary>
			internal int DistanceCount { get; }

			public bool Equals(BankSetting other) =>
				ConeHalfAngle == other.ConeHalfAngle && ConeSpacing == other.ConeSpacing &&
				RollRange == other.RollRange && FineHalfAngle == other.FineHalfAngle &&
				WindowMargin == other.WindowMargin && DistanceCount == other.DistanceCount;

			public override bool Equals(object other) => other is BankSetting bank && Equals(bank);

			public override int GetHashCode() => ConeHalfAngle.GetHashCode() ^ (ConeSpacing.GetHashCode() << 1) ^ (RollRange.GetHashCode() << 2) ^
				(FineHalfAngle.GetHashCode() << 3) ^ (WindowMargin.GetHashCode() << 4) ^ (DistanceCount << 5);
		}


		/// <summary>How many searches in a row must find the same winner before a start is let through.</summary>
		internal const int AGREE_SEARCHES = 3;

		/// <summary>
		/// The most two consecutive winners may stand apart and still be the same winner, as the median on-screen displacement of
		/// the model's points in pixels.
		/// </summary>
		internal const float AGREE_PIXELS = 8f;

		/// <summary>
		/// The most the view of the guide may turn, in degrees, since the plugin built its views before they are built again around the
		/// guide as the camera sees it now. The views cover a cone of directions and a range of turns around the guide, and a larger turn
		/// than this may carry the object out of them.
		/// </summary>
		internal const float REANCHOR_TURN_DEGREES = 12f;

		/// <summary>
		/// The most the distance of the guide may change, as a share of the distance at which the views were built, before the views are
		/// built again. The views cover a band of distances around the distance of the guide, and a larger change than this may carry the
		/// object out of it.
		/// </summary>
		internal const float REANCHOR_DEPTH_SHARE = 0.1f;

		/// <summary>
		/// How far, in pixels of median outline displacement, the guide the search was sent may fall behind a guide that follows the
		/// camera before the views are asked for again. The search looks around the guide it was sent with a margin of 96 pixels, so at
		/// this size a guide on screen stays well inside the window.
		/// </summary>
		internal const float REANCHOR_FOLLOWING_PIXELS = 32f;

		/// <summary>The setting as the body reads it, either its own or the override of the tooling.</summary>
		internal readonly struct Setting
		{
			internal Setting(bool on, int periodSteps, float marginBar, float scoreFloor, float reachPixels)
			{
				On = on;
				PeriodSteps = periodSteps;
				MarginBar = marginBar;
				ScoreFloor = scoreFloor;
				ReachPixels = reachPixels;
			}

			internal bool On { get; }
			internal int PeriodSteps { get; }

			/// <summary>The margin the winner must reach. A bar of 0 or less means no margin bar.</summary>
			internal float MarginBar { get; }

			/// <summary>The score the winner must reach. A floor of 0 or less means no floor.</summary>
			internal float ScoreFloor { get; }

			/// <summary>The most the guide may stand from the winner, in pixels, and still count as in reach.</summary>
			internal float ReachPixels { get; }
		}

		/// <summary>What the body has told the plugin about its search, so that a steady state makes no call.</summary>
		internal struct Request
		{
			/// <summary>Whether a request was made for the current wish, whatever the plugin answered.</summary>
			internal bool Asked;

			/// <summary>Whether the plugin took the request and holds a search for the body.</summary>
			internal bool Held;

			internal int PeriodSteps;
			internal int Generation;
		}

		/// <summary>What the body does about the search on a step.</summary>
		internal enum Step
		{
			/// <summary>The plugin already has what is wanted, so nothing is sent.</summary>
			Nothing,

			/// <summary>The search is wanted and the plugin does not have it as it is wanted, so the guide is sent.</summary>
			Send,

			/// <summary>The plugin holds a search that is no longer wanted, so it is switched off.</summary>
			Clear,
		}

		/// <summary>The previous winner, the last search taken and how many searches in a row have agreed.</summary>
		internal struct State
		{
			internal bool HasPrevious;

			/// <summary>The previous winner in the frame of the tracker, which the comparison turns into the camera of its step.</summary>
			internal Vector3 Position;

			internal Quaternion Rotation;
			internal bool HasSerial;
			internal int LastSerial;
			internal int Streak;
			internal StartSearchVerdict Last;
		}

		/// <summary>
		/// Whether the object, held at a guide that stands still in the world, is seen from a different enough side or distance than
		/// when the plugin built its views, so that the views no longer cover it and must be built again. The views are built around the
		/// guide as the camera saw it at the time, and under pose fusion the camera moves while the guide does not.
		/// </summary>
		/// <param name="worldFromBody">The guide, in the world.</param>
		/// <param name="anchorWorldFromCamera">The camera at the time the views were built.</param>
		/// <param name="worldFromCamera">The camera now.</param>
		/// <returns>
		/// True when the turn of the object's view, taken about the line of sight to it, is more than <see cref="REANCHOR_TURN_DEGREES"/>
		/// or its distance changed by more than <see cref="REANCHOR_DEPTH_SHARE"/>. False when the guide is not in front of either camera,
		/// because views cannot be built around a guide that is behind the camera.
		/// </returns>
		internal static bool GuideViewMoved(Matrix4x4 worldFromBody, Matrix4x4 anchorWorldFromCamera, Matrix4x4 worldFromCamera)
		{
			Matrix4x4 anchored = anchorWorldFromCamera.inverse * worldFromBody;
			Matrix4x4 now = worldFromCamera.inverse * worldFromBody;
			Vector3 anchoredPosition = anchored.GetPosition();
			Vector3 nowPosition = now.GetPosition();
			const float MIN_DEPTH = 0.02f;
			if (anchoredPosition.z <= MIN_DEPTH || nowPosition.z <= MIN_DEPTH)
				return false;
			if (Mathf.Abs(nowPosition.z / anchoredPosition.z - 1f) > REANCHOR_DEPTH_SHARE)
				return true;
			Quaternion anchoredOnAxis = Quaternion.Inverse(Quaternion.LookRotation(anchoredPosition)) * anchored.rotation;
			Quaternion nowOnAxis = Quaternion.Inverse(Quaternion.LookRotation(nowPosition)) * now.rotation;
			return Quaternion.Angle(anchoredOnAxis, nowOnAxis) > REANCHOR_TURN_DEGREES;
		}

		/// <summary>The period of the search in tracking steps, kept from 1 to 60.</summary>
		internal static int ClampPeriodSteps(int value) => Mathf.Clamp(value, MIN_PERIOD_STEPS, MAX_PERIOD_STEPS);

		/// <summary>The margin bar kept from 0 to 3, where a number that is not a number reads as the default.</summary>
		internal static float ClampMarginBar(float value) =>
			float.IsNaN(value) ? DEFAULT_MARGIN_BAR : Mathf.Clamp(value, 0f, MAX_MARGIN_BAR);

		/// <summary>The score floor kept from 0 to 1, where a number that is not a number reads as the default.</summary>
		internal static float ClampScoreFloor(float value) =>
			float.IsNaN(value) ? DEFAULT_SCORE_FLOOR : Mathf.Clamp(value, 0f, MAX_SCORE_FLOOR);

		/// <summary>The half angle of the cone kept from 0 to 45 degrees, where a number that is not a number reads as the default.</summary>
		internal static float ClampConeHalfAngle(float value) =>
			float.IsNaN(value) ? DEFAULT_CONE_HALF_ANGLE : Mathf.Clamp(value, 0f, MAX_CONE_HALF_ANGLE);

		/// <summary>The spacing of the cone kept from 2.5 to 20 degrees, where a number that is not a number reads as the default.</summary>
		internal static float ClampConeSpacing(float value) =>
			float.IsNaN(value) ? DEFAULT_CONE_SPACING : Mathf.Clamp(value, MIN_CONE_SPACING, MAX_CONE_SPACING);

		/// <summary>The range of the turns kept from 0 to 45 degrees, where a number that is not a number reads as the default.</summary>
		internal static float ClampRollRange(float value) =>
			float.IsNaN(value) ? DEFAULT_ROLL_RANGE : Mathf.Clamp(value, 0f, MAX_ROLL_RANGE);

		/// <summary>The half angle of the fine stage kept from 0 to 15 degrees, where a number that is not a number reads as the default.</summary>
		internal static float ClampFineHalfAngle(float value) =>
			float.IsNaN(value) ? DEFAULT_FINE_HALF_ANGLE : Mathf.Clamp(value, 0f, MAX_FINE_HALF_ANGLE);

		/// <summary>The window margin kept from 0 to 640 pixels, where a number that is not a number reads as the default.</summary>
		internal static float ClampWindowMargin(float value) =>
			float.IsNaN(value) ? DEFAULT_WINDOW_MARGIN : Mathf.Clamp(value, 0f, MAX_WINDOW_MARGIN);

		/// <summary>The number of distances read as the nearest of 1, 3 and 5.</summary>
		internal static int ClampDistanceCount(int value) => value <= 1 ? 1 : (value <= 3 ? 3 : 5);

		/// <summary>The values of a bank, each kept in its range.</summary>
		internal static BankSetting ClampBank(float coneHalfAngle, float coneSpacing, float rollRange, float fineHalfAngle, float windowMargin, int distanceCount) =>
			new BankSetting(ClampConeHalfAngle(coneHalfAngle), ClampConeSpacing(coneSpacing), ClampRollRange(rollRange), ClampFineHalfAngle(fineHalfAngle),
				ClampWindowMargin(windowMargin), ClampDistanceCount(distanceCount));

		/// <summary>The reach kept from 0 to 200 pixels, where a number that is not a number reads as the default.</summary>
		internal static float ClampReachPixels(float value) =>
			float.IsNaN(value) ? DEFAULT_REACH_PIXELS : Mathf.Clamp(value, 0f, MAX_REACH_PIXELS);

		/// <summary>
		/// The distance of a winner from the guide, as a multiple of the reach, beyond which the winner counts as out of reach. Between the reach
		/// and this multiple a winner counts for neither path, so a winner that hovers around the reach does not switch the path back and forth.
		/// </summary>
		internal const float FAR_REACH_FACTOR = 1.25f;

		/// <summary>How many winners in a row must agree on the other path before the body switches to it.</summary>
		internal const int LATCH_WINNERS = 2;

		/// <summary>
		/// The path the body takes after a new winner has been measured against the guide, with a latch on the switch. The body enters the far
		/// path only after <see cref="LATCH_WINNERS"/> winners in a row stand farther than <see cref="FAR_REACH_FACTOR"/> times the reach (a
		/// distance that could not be measured counts as farther) and each also reaches the margin bar and the score floor, and it leaves it
		/// for the guide in reach only after <see cref="LATCH_WINNERS"/> winners in a row stand within the reach. A winner between the two
		/// distances breaks both runs, and so does a weak winner, which counts toward the far path never and so leaves the body on its own
		/// start. Until a winner has stood within the reach the path stays unknown, and an unknown path runs the body's own start.
		/// </summary>
		/// <param name="current">The path the body takes now.</param>
		/// <param name="farRun">The count of winners in a row farther than the far distance, updated here.</param>
		/// <param name="nearRun">The count of winners in a row within the reach, updated here.</param>
		/// <param name="pixelsFromGuide">The median on-screen displacement of the model's points between the guide and the winner.</param>
		/// <param name="reachPixels">The reach in force.</param>
		/// <param name="strong">Whether the winner reaches the margin bar and the score floor (see <see cref="WinnerIsStrong"/>).</param>
		internal static StartSearchPath Latch(StartSearchPath current, ref int farRun, ref int nearRun, float pixelsFromGuide, float reachPixels,
			bool strong = true)
		{
			float reach = ClampReachPixels(reachPixels);
			bool far = strong && (float.IsNaN(pixelsFromGuide) || pixelsFromGuide > reach * FAR_REACH_FACTOR);
			bool near = !float.IsNaN(pixelsFromGuide) && pixelsFromGuide <= reach;
			farRun = far ? farRun + 1 : 0;
			nearRun = near ? nearRun + 1 : 0;
			if (current != StartSearchPath.Far && farRun >= LATCH_WINNERS)
				return StartSearchPath.Far;
			if (current == StartSearchPath.Far)
				return nearRun >= LATCH_WINNERS ? StartSearchPath.InReach : StartSearchPath.Far;
			return near ? StartSearchPath.InReach : current;
		}

		/// <summary>
		/// What to do on a step. A search that is wanted is sent once, and again only when the period or the generation of the guide
		/// differs from what was sent, unless the plugin was found without the export, which is not asked for again. A search that
		/// the plugin holds and that is no longer wanted is switched off once.
		/// </summary>
		/// <param name="wanted">Whether the search applies to the body on this step.</param>
		/// <param name="sent">What the body has told the plugin so far.</param>
		/// <param name="periodSteps">The period in force.</param>
		/// <param name="generation">The generation of the guide, which grows every time the person replaces the guide.</param>
		/// <param name="pluginLacksExport">Whether the plugin was found without the export.</param>
		internal static Step Decide(bool wanted, in Request sent, int periodSteps, int generation, bool pluginLacksExport)
		{
			if (wanted && !pluginLacksExport)
			{
				bool upToDate = sent.Asked && sent.PeriodSteps == periodSteps && sent.Generation == generation;
				return upToDate ? Step.Nothing : Step.Send;
			}
			return sent.Held ? Step.Clear : Step.Nothing;
		}

		/// <summary>
		/// Whether the winner of a reading reaches the margin bar and the score floor of the setting. A bar or a floor of 0 or less asks
		/// for nothing. A winner that does not is weak, and a weak winner can start no body and send none onto the far path.
		/// </summary>
		internal static bool WinnerIsStrong(in Setting setting, in StartSearchReading reading)
		{
			float bar = ClampMarginBar(setting.MarginBar);
			float floor = ClampScoreFloor(setting.ScoreFloor);
			return (bar <= 0f || reading.Margin >= bar) && (floor <= 0f || reading.Score >= floor);
		}

		/// <summary>Whether a reading holds a search that has not been taken yet.</summary>
		internal static bool IsNewSearch(in State state, in StartSearchReading reading) =>
			reading.Valid && (!state.HasSerial || reading.Serial != state.LastSerial);

		/// <summary>
		/// This takes up the reading of a step. A reading with no winner changes nothing. A reading with the serial of a search that
		/// was taken already leaves the streak and the winner as they were. A new search counts toward the streak when its winner
		/// stands within <see cref="AGREE_PIXELS"/> of the winner before it, and otherwise it starts the streak again at one. The
		/// first search counts as a streak of one. Whenever the reading holds a winner, the verdict is worked out again against the
		/// streak, the margin bar and the score floor.
		/// </summary>
		/// <param name="pixelsFromPrevious">
		/// The on-screen displacement between the winner before and this winner, or not a number when there is none to measure.
		/// </param>
		/// <param name="position">The winner in the frame of the tracker, kept as the winner to compare the next search with.</param>
		/// <param name="rotation">The winner's rotation in the frame of the tracker.</param>
		internal static void Take(ref State state, in Setting setting, in StartSearchReading reading, float pixelsFromPrevious,
			Vector3 position, Quaternion rotation)
		{
			if (!reading.Valid)
				return;

			float agreePixels = state.Last.AgreePixels;
			if (IsNewSearch(state, reading))
			{
				bool agrees = state.HasPrevious && !float.IsNaN(pixelsFromPrevious) && pixelsFromPrevious <= AGREE_PIXELS;
				agreePixels = state.HasPrevious ? pixelsFromPrevious : float.NaN;
				state.Streak = agrees ? state.Streak + 1 : 1;
				state.HasPrevious = true;
				state.Position = position;
				state.Rotation = rotation;
				state.HasSerial = true;
				state.LastSerial = reading.Serial;
			}

			bool passes = state.Streak >= AGREE_SEARCHES && WinnerIsStrong(setting, reading);
			state.Last = new StartSearchVerdict(state.Streak, agreePixels, passes);
		}

		/// <summary>
		/// This is for a step whose reading could not be read. The step counts for no search, and it leaves no winner to compare the
		/// next search with.
		/// </summary>
		internal static void Lose(ref State state)
		{
			state.HasPrevious = false;
			state.Streak = 0;
			state.Last = new StartSearchVerdict(0, float.NaN, false);
		}

		/// <summary>
		/// This reads a reading from the floats the plugin filled. An array that is missing or too short gives a reading with no winner.
		/// </summary>
		internal static StartSearchReading Parse(float[] raw) =>
			raw == null || raw.Length < FTBridge.START_SEARCH_READING_LENGTH
				? default(StartSearchReading)
				: new StartSearchReading(raw);
	}
}
