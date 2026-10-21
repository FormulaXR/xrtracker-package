using System;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What the saved-view driver asks of the plugin, behind an interface so its
	/// decisions can be driven and observed without a native library present.
	/// </summary>
	internal interface ISavedViewRecoveryPort
	{
		bool Configure(string bodyId, int capacity, float rotationDeg, float translationM,
			float maxProbeResidual, float minEdgeCoverage);
		bool TryGetBankSize(string bodyId, out int size);
		bool TryPropose(string bodyId, int markSeatedEntryId, int markWindowFailedEntryId,
			FTVerifiedCandidate[] candidates, out int count);
	}

	/// <summary>
	/// Hands over the stage times of each row the last call to a port or a scorer returned, in the order the rows
	/// were written. A fake that does not implement it reads as a source with no timing.
	/// </summary>
	internal interface ICandidateTimingSource
	{
		bool TryReadTimings(string bodyId, int count, FTPoseEvidenceTiming[] timings);
	}

	/// <summary>
	/// Hands over the split of the start stage of each row the last call to a port or a scorer returned, in the order
	/// the rows were written. A fake that does not implement it reads as a source with no split.
	/// </summary>
	internal interface ICandidateStartTimingSource
	{
		bool TryReadStartTimings(string bodyId, int count, FTPoseEvidenceStartTiming[] timings);
	}

	/// <summary>The port the shipped driver runs through: the plugin's own entry points.</summary>
	internal sealed class SavedViewRecoveryBridgePort : ISavedViewRecoveryPort, ICandidateTimingSource,
		ICandidateStartTimingSource
	{
		public bool Configure(string bodyId, int capacity, float rotationDeg, float translationM,
			float maxProbeResidual, float minEdgeCoverage) =>
			FTBridge.TryConfigureRecoverySeedBank(bodyId, capacity, rotationDeg, translationM,
				maxProbeResidual, minEdgeCoverage);

		public bool TryGetBankSize(string bodyId, out int size) =>
			FTBridge.TryGetRecoverySeedBankSize(bodyId, out size);

		public bool TryPropose(string bodyId, int markSeatedEntryId, int markWindowFailedEntryId,
			FTVerifiedCandidate[] candidates, out int count) =>
			FTBridge.TryProposeFromSavedViews(bodyId, markSeatedEntryId, markWindowFailedEntryId,
				candidates, out count);

		public bool TryReadTimings(string bodyId, int count, FTPoseEvidenceTiming[] timings) =>
			timings != null && timings.Length >= count &&
			FTBridge.TryGetProposalEvaluationTimings(bodyId, timings, out int read) && read == count;

		public bool TryReadStartTimings(string bodyId, int count, FTPoseEvidenceStartTiming[] timings) =>
			timings != null && timings.Length >= count &&
			FTBridge.TryGetProposalEvaluationStartTimings(bodyId, timings, out int read) && read == count;
	}

	/// <summary>The floor and the full-scale share a body maps its coverage share with.</summary>
	internal readonly struct CoverageReadScale
	{
		public CoverageReadScale(float floor, float full)
		{
			Floor = floor;
			Full = full;
		}

		public float Floor { get; }
		public float Full { get; }
	}

	/// <summary>How a candidate reaches the tracker, which decides how it is withdrawn.</summary>
	internal enum SavedViewSeatPath
	{
		None,
		/// <summary>The body is not tracking: the candidate becomes the retained pose the
		/// host republishes every frame, and withdrawing restores the retained pose it replaced.</summary>
		RetainedPose,
		/// <summary>The body is tracking but held: the candidate is written as the pose to
		/// solve from, and withdrawing re-seeds the seat the hold loop holds.</summary>
		HeldSolve,
	}

	internal enum SavedViewWindowOutcome { None, Passed, Failed }

	/// <summary>Where the candidate of an attempt came from.</summary>
	internal enum SavedViewCandidateSource
	{
		/// <summary>A pose the plugin proposed from a saved view.</summary>
		SavedView,
		/// <summary>A camera-relative pose the body was accepted at before.</summary>
		SeenPose,
	}

	/// <summary>What the driver decided this frame, for the manager to carry out and the host to log.</summary>
	internal struct SavedViewDecision
	{
		public bool Attempted;
		public int Frame;
		public int BankSize;
		public int RowCount;
		public FTVerifiedCandidate[] Rows;
		public float HeldSignal;
		public bool HeldClears;
		public string Refusal;
		public double Milliseconds;

		public bool Seat;
		public FTVerifiedCandidate Candidate;
		public float CandidateSignal;
		public SavedViewSeatPath SeatPath;

		public bool Withdraw;
		public SavedViewSeatPath WithdrawPath;
		public int WithdrawEntryId;
		public int WithdrawSeatFrame;
		public Vector3 RestorePosition;
		public Quaternion RestoreRotation;

		public SavedViewWindowOutcome Window;
		public int WindowEntryId;
		public int WindowSeatFrame;

		/// <summary>Where this attempt's candidate came from.</summary>
		public SavedViewCandidateSource Source;

		/// <summary>Which kept pose a seen-pose attempt scored (start, last, or ring:serial), on the row of that attempt.</summary>
		public string SeenPoseLabel;

		/// <summary>Whether a seat was judged on its refined pose on this step.</summary>
		public bool RefineJudged;

		/// <summary>Whether the refined pose read the judge bar. Meaningful only when <see cref="RefineJudged"/>.</summary>
		public bool RefinePassed;

		/// <summary>The score of the refined pose the seat was judged on.</summary>
		public float RefinedSignal;

		/// <summary>How many completed rows after the seat the judgement was taken.</summary>
		public int RefineRows;

		/// <summary>The frame of the seat that was judged.</summary>
		public int RefineSeatFrame;

		/// <summary>Whether the tracker was refining the seat on the row it was judged. Set by the manager.</summary>
		public bool Refining;

		/// <summary>Whether the distance between the seat and the refined record was measured. Set by the manager.</summary>
		public bool RefineMeasured;

		/// <summary>How far the record judged stands from the pose the seat was made at, in millimetres.</summary>
		public float RefineMillimetres;

		/// <summary>How far the record judged stands from the pose the seat was made at, in degrees.</summary>
		public float RefineDegrees;

		/// <summary>The stage times of the evaluation of each row, in the order of <see cref="Rows"/>, or null when not read.</summary>
		public FTPoseEvidenceTiming[] Timings;

		/// <summary>The split of the start stage of the evaluation of each row, in the order of <see cref="Rows"/>, or null when not read.</summary>
		public FTPoseEvidenceStartTiming[] StartTimings;

		/// <summary>Whether the comparison with the held row was made (a candidate and a held row both existed).</summary>
		public bool MarginKnown;

		/// <summary>
		/// Whether the best candidate beat the held row by the seat margin, which is the test a candidate has to pass to be
		/// seated against a held row. Meaningful only when <see cref="MarginKnown"/>.
		/// </summary>
		public bool MarginPassed;

		/// <summary>Whether a kept-pose attempt that was due on this row was skipped by the cadence of kept-pose attempts.</summary>
		public bool CadenceSkipped;

		/// <summary>How many lost rows had passed since the last kept-pose attempt on a row that the cadence skipped.</summary>
		public int CadenceRowsSinceAttempt;

		/// <summary>How many rows after the seat the window ended on this row. Meaningful only when <see cref="Window"/> is not None.</summary>
		public int WindowRows;
	}

	/// <summary>The driver's memory for one body. Dropped with the body's other per-frame state.</summary>
	internal sealed class SavedViewRecoveryState
	{
		public const int NO_PREVIOUS_ATTEMPT = int.MaxValue / 2;

		public bool Configured;
		public int ConfiguredCapacity = -1;
		public int FramesOutOfTracked;
		public int FramesSinceAttempt = NO_PREVIOUS_ATTEMPT;

		/// <summary>The rows since a kept pose was last taken from the sweep for an attempt.</summary>
		public int FramesSinceKeptAttempt = NO_PREVIOUS_ATTEMPT;

		public int Attempts;
		public int MarkSeatedEntryId;
		public int MarkWindowFailedEntryId;

		public int PendingEntryId;
		public int PendingSeatFrame;
		public SavedViewSeatPath PendingPath;

		/// <summary>Whether the pending seat has been judged on its refined pose already.</summary>
		public bool PendingJudged;
		public Vector3 PreviousPosition;
		public Quaternion PreviousRotation;
	}

	/// <summary>
	/// Decides, once per frame, whether a body that has lost the object is seated
	/// on a pose its saved views propose, and whether a seat that did not start
	/// is taken back.
	/// </summary>
	/// <remarks>
	/// Appearance proposes, geometry verifies, the window decides. No pose enters
	/// the tracker unless the plugin proposed it from a saved view, the verifier
	/// scored it at this frame above the acquire bar and above the pose the body
	/// holds, and the ordinary window then confirms it. A seat the window does not
	/// confirm within the attempt interval is withdrawn: the body goes back to the
	/// pose it held before the seat, and the view that proposed it is marked.
	/// Capacity zero makes no call beyond the one that switches the bank off.
	/// </remarks>
	internal sealed class SavedViewRecoveryDriver
	{
		private readonly ISavedViewRecoveryPort _port;
		private readonly Func<double> _milliseconds;
		private readonly ISeenPoseScorer _seenPoseScorer;

		/// <summary>
		/// How many lost rows apart two kept-pose attempts are at least, counted in rows, or zero for an attempt on every
		/// row that is due. The saved-view cadence is not touched.
		/// </summary>
		public int KeptPoseCadenceRows { get; set; }

		public SavedViewRecoveryDriver(ISavedViewRecoveryPort port, Func<double> milliseconds = null,
			ISeenPoseScorer seenPoseScorer = null)
		{
			_port = port ?? throw new ArgumentNullException(nameof(port));
			_milliseconds = milliseconds ?? (() => 0.0);
			_seenPoseScorer = seenPoseScorer;
		}

		/// <summary>The signal a candidate reads as at the frame it was verified on.</summary>
		/// <remarks>
		/// The verifier and the live status fill compute this from one function, so
		/// a candidate and the pose it would displace are read on the same scale by
		/// construction rather than by agreement. A candidate whose record carries
		/// no reading reads as nothing: it cannot clear a bar and it cannot beat a
		/// held pose.
		/// </remarks>
		public static float SignalOf(in FTVerifiedCandidate candidate) =>
			SignalOf(candidate, TrackerDefaults.MARGIN_RESIDUAL_TERM);

		/// <summary>
		/// The signal a candidate reads as under <paramref name="term"/>, the residual
		/// term the body's own decisions read.
		/// </summary>
		/// <remarks>
		/// A candidate and the pose it would displace are read under the same term, so
		/// they stay on one scale, and the acquire bar a seat must clear means what it
		/// means for the body. A record with no sub-pixel median reads as a record with
		/// no reading. Under the mode that guards the stored reference the reading is the
		/// weaker of the median margin and the published margin.
		/// </remarks>
		public static float SignalOf(in FTVerifiedCandidate candidate,
			TrackerDefaults.MarginResidualTerm term)
		{
			FTRegistrationQualityEvidence record =
				candidate.evidence.registration_quality;
			bool available = (record.availability_mask &
				FTRegistrationQualityEvidence.ScoreAvailableBit) != 0 &&
				record.unavailable_reason == 0 &&
				!float.IsNaN(record.instantaneous_score) &&
				!float.IsInfinity(record.instantaneous_score);
			if (!available)
				return float.NegativeInfinity;
			if (term == TrackerDefaults.MarginResidualTerm.Percentile90)
				return record.instantaneous_score;
			if (!RegistrationQualitySnapshot.TryReadSubpixelMedianMargin(record, out float margin))
				return float.NegativeInfinity;
			// Under the mode that guards the stored reference, candidates are ranked and seated on
			// the reading the stored reference is decided on: the weaker of the median margin and
			// the published margin, which share the alignment term.
			return term == TrackerDefaults.MarginResidualTerm.SubpixelMedianRefineBeforeAcceptGuardedReference
				? Math.Min(margin, record.instantaneous_score)
				: margin;
		}

		/// <summary>
		/// The signal a candidate reads as when decisions read the coverage score: the share of agreeing sites its
		/// own record carries, mapped with <paramref name="scale"/>.
		/// </summary>
		/// <remarks>
		/// The share is the record's own legacy coverage, the share of agreeing sites over every retained site that
		/// the plugin publishes for the released coverage reading. A record the plugin did not write in the layout
		/// this side knows, with no retained site, or with a share that is not a number, reads as negative infinity:
		/// it cannot clear a bar and it cannot beat a held pose.
		/// </remarks>
		internal static float CoverageSignalOf(in FTVerifiedCandidate candidate, CoverageReadScale scale)
		{
			FTRegistrationQualityEvidence record = candidate.evidence.registration_quality;
			bool readable = record.schema_version == 2 && record.model_version == 3 &&
				record.struct_size == FTRegistrationQualityEvidence.StructSize &&
				record.n_retained_sites > 0 &&
				!float.IsNaN(record.legacy_raw_coverage) && !float.IsInfinity(record.legacy_raw_coverage);
			if (!readable)
				return float.NegativeInfinity;
			return Mathf.InverseLerp(scale.Floor, scale.Full, record.legacy_raw_coverage);
		}

		/// <summary>
		/// Advances one body by one completed frame. <paramref name="tracked"/> is the
		/// temporal machine's answer; <paramref name="isTracking"/> and
		/// <paramref name="holdActive"/> say which path a seat would take, and a seat made
		/// while the body is not tracking is on its window until the body is tracking.
		/// <paramref name="marginResidualTerm"/> is the residual term the body's own
		/// decisions read, and every candidate and the held pose are read under it.
		/// <paramref name="seenPoses"/> are the body's kept camera-relative poses. They are
		/// tried only on a row the saved views do not use, while the body is not tracking,
		/// and only when the driver was given a scorer. Without both, nothing here reads them.
		/// <paramref name="refineSteps"/> above zero judges a candidate after the tracker has refined it
		/// instead of before. While the body is not tracking, a candidate is then seated once it is
		/// readable and beats the held pose by the seat margin, without first clearing the acquire bar,
		/// and <paramref name="refinedSignal"/>, the score of the refined pose on the row being stepped,
		/// has to clear <paramref name="judgeBar"/> on the row that many rows after the seat, or the seat
		/// is taken back exactly as a seat that the window did not confirm is. Zero keeps the order in
		/// which a candidate is judged at the pose it was kept at.
		/// <paramref name="coverageScale"/>, when given, reads every candidate and the held pose as the share of
		/// agreeing sites their own record carries, mapped with that scale, instead of as the registration margin.
		/// </summary>
		public SavedViewDecision Step(SavedViewRecoveryState state, string bodyId, int frame,
			int capacity, float rotationDeg, float translationM, float maxProbeResidual,
			float minEdgeCoverage, bool tracked, bool isTracking, bool holdActive,
			Func<float, bool> clearsAcquire, Vector3 heldPosition, Quaternion heldRotation,
			TrackerDefaults.MarginResidualTerm marginResidualTerm =
				TrackerDefaults.MARGIN_RESIDUAL_TERM,
			SeenPoseCandidates seenPoses = null, int refineSteps = 0, float refinedSignal = 0f,
			float judgeBar = 0f, CoverageReadScale? coverageScale = null)
		{
			var decision = new SavedViewDecision { Frame = frame, Refusal = string.Empty };
			bool provisional = refineSteps > 0 && !isTracking;
			if (!state.Configured || state.ConfiguredCapacity != capacity)
			{
				state.Configured = _port.Configure(bodyId, capacity, rotationDeg, translationM,
					maxProbeResidual, minEdgeCoverage);
				state.ConfiguredCapacity = capacity;
			}
			if (capacity <= 0)
				return decision;

			if (state.PendingEntryId > 0)
			{
				// A seat made on the retained pose is confirmed once the body is tracking, and a seat made on the held
				// solve once the hold ends.
				bool passed = state.PendingPath == SavedViewSeatPath.RetainedPose
					? isTracking
					: !holdActive;
				// The refined pose is judged once, on the row the tooling names, and only for a seat made while
				// the body was not tracking that the ordinary start has not confirmed already.
				bool refused = false;
				if (!passed && refineSteps > 0 && state.PendingPath == SavedViewSeatPath.RetainedPose &&
					!state.PendingJudged && frame - state.PendingSeatFrame >= refineSteps)
				{
					state.PendingJudged = true;
					decision.RefineJudged = true;
					decision.RefinedSignal = refinedSignal;
					decision.RefineRows = frame - state.PendingSeatFrame;
					decision.RefineSeatFrame = state.PendingSeatFrame;
					decision.RefinePassed = refinedSignal >= judgeBar;
					refused = !decision.RefinePassed;
				}
				bool timedOut = frame - state.PendingSeatFrame >= TrackerDefaults.RECOVERY_ATTEMPT_INTERVAL_FRAMES;
				if (passed)
				{
					decision.Window = SavedViewWindowOutcome.Passed;
					decision.WindowEntryId = state.PendingEntryId;
					decision.WindowSeatFrame = state.PendingSeatFrame;
					decision.WindowRows = frame - state.PendingSeatFrame;
					state.PendingEntryId = 0;
				}
				else if (refused || timedOut)
				{
					decision.Window = SavedViewWindowOutcome.Failed;
					decision.WindowEntryId = state.PendingEntryId;
					decision.WindowSeatFrame = state.PendingSeatFrame;
					decision.WindowRows = frame - state.PendingSeatFrame;
					decision.Withdraw = true;
					decision.WithdrawPath = state.PendingPath;
					decision.WithdrawEntryId = state.PendingEntryId;
					decision.WithdrawSeatFrame = state.PendingSeatFrame;
					decision.RestorePosition = state.PreviousPosition;
					decision.RestoreRotation = state.PreviousRotation;
					// A kept pose is not the plugin's to mark. It is left out of its own
					// next sweep instead.
					if (state.PendingEntryId >= TrackerDefaults.SEEN_POSE_ENTRY_ID_BASE)
						seenPoses?.MarkWindowFailed(state.PendingEntryId);
					else
						state.MarkWindowFailedEntryId = state.PendingEntryId;
					state.PendingEntryId = 0;
					// The same row counts for the cadence of the kept poses.
					if (state.FramesSinceKeptAttempt < SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT)
						++state.FramesSinceKeptAttempt;
					// The withdrawal takes effect at the next step; proposing against
					// the pose being withdrawn would score the seat it is taking back.
					return decision;
				}
			}

			if (tracked)
			{
				state.FramesOutOfTracked = 0;
				state.FramesSinceAttempt = SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT;
				state.FramesSinceKeptAttempt = SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT;
				seenPoses?.RestartSweep();
				return decision;
			}
			++state.FramesOutOfTracked;
			if (state.FramesSinceAttempt < SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT)
				++state.FramesSinceAttempt;
			if (state.FramesSinceKeptAttempt < SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT)
				++state.FramesSinceKeptAttempt;
			bool sized = _port.TryGetBankSize(bodyId, out int bankSize);
			bool seatDue = sized && TrackedBodyManager.ShouldSeatOnKeptView(bankSize, state.FramesOutOfTracked,
				state.FramesSinceAttempt);
			if (!seatDue)
			{
				if (seenPoses != null && _seenPoseScorer != null)
				{
					AttemptSeenPose(state, ref decision, bodyId, frame, sized ? bankSize : 0, isTracking,
						clearsAcquire, heldPosition, heldRotation, marginResidualTerm, seenPoses, provisional,
						coverageScale);
				}
				return decision;
			}

			double started = _milliseconds();
			var rows = FTVerifiedCandidate.CreateBuffer(bankSize + 1);
			bool proposed = _port.TryPropose(bodyId, state.MarkSeatedEntryId,
				state.MarkWindowFailedEntryId, rows, out int count);
			state.MarkSeatedEntryId = 0;
			state.MarkWindowFailedEntryId = 0;
			++state.Attempts;
			state.FramesSinceAttempt = 0;
			decision.Attempted = true;
			decision.BankSize = bankSize;
			decision.Rows = rows;
			decision.RowCount = proposed ? count : 0;
			decision.Milliseconds = _milliseconds() - started;
			if (!proposed)
			{
				decision.Refusal = "the proposal call failed";
				return decision;
			}
			if (count > 0 && _port is ICandidateTimingSource timingSource)
			{
				var timings = new FTPoseEvidenceTiming[count];
				if (timingSource.TryReadTimings(bodyId, count, timings))
					decision.Timings = timings;
			}
			if (count > 0 && _port is ICandidateStartTimingSource startTimingSource)
			{
				var startTimings = new FTPoseEvidenceStartTiming[count];
				if (startTimingSource.TryReadStartTimings(bodyId, count, startTimings))
					decision.StartTimings = startTimings;
			}

			Choose(state, ref decision, rows, count, frame, isTracking, clearsAcquire, heldPosition,
				heldRotation, marginResidualTerm, markSeated: true, provisional, coverageScale);
			return decision;
		}

		/// <summary>
		/// Picks the best row that is not the held pose, refuses it below the acquire bar,
		/// or when the held pose is live and clears, or when it does not beat the held pose
		/// by the seat margin, and otherwise seats it.
		/// </summary>
		/// <remarks>
		/// Saved views and kept poses go through this one choice, so both meet the same
		/// bar, the same margin and the same seat. Only a saved view is marked seated for
		/// the plugin's bank.
		/// </remarks>
		private void Choose(SavedViewRecoveryState state, ref SavedViewDecision decision,
			FTVerifiedCandidate[] rows, int count, int frame, bool isTracking,
			Func<float, bool> clearsAcquire, Vector3 heldPosition, Quaternion heldRotation,
			TrackerDefaults.MarginResidualTerm marginResidualTerm, bool markSeated,
			bool provisional = false, CoverageReadScale? coverageScale = null)
		{
			// One reading per row, on the scale the body's decisions run on. Under the coverage score it is the
			// share of agreeing sites the row's own record carries.
			float ReadRow(int row) => coverageScale.HasValue
				? CoverageSignalOf(rows[row], coverageScale.Value)
				: SignalOf(rows[row], marginResidualTerm);

			// The best of the proposals, not the first one the bank happens to
			// return. Bank order is the order views were stored in, which carries
			// nothing about how well any of them reads on this frame, so taking the
			// first put every refusal below on a row chosen at random among those
			// offered. An unreadable row reads negative infinity and so is only
			// ever chosen when every row is unreadable, which the refusals handle.
			int held = -1, best = -1;
			float bestSignal = 0f;
			for (int index = 0; index < count; ++index)
			{
				if (rows[index].is_held_pose != 0)
				{
					if (held < 0) held = index;
					continue;
				}
				float signal = ReadRow(index);
				if (best < 0 || signal > bestSignal)
				{
					best = index;
					bestSignal = signal;
				}
			}
			if (held >= 0)
			{
				decision.HeldSignal = ReadRow(held);
				decision.HeldClears = clearsAcquire(decision.HeldSignal);
			}
			if (best < 0)
			{
				decision.Refusal = "no view proposed a pose";
				return;
			}
			decision.Candidate = rows[best];
			decision.CandidateSignal = ReadRow(best);
			// The comparison with the held row is always made and recorded.
			bool marginPassed = true;
			if (held >= 0)
			{
				marginPassed = !(decision.CandidateSignal <= decision.HeldSignal +
					TrackerDefaults.REGISTRATION_MARGIN_SEAT_MARGIN);
				decision.MarginKnown = true;
				decision.MarginPassed = marginPassed;
			}
			// A provisional candidate is judged after the tracker has refined it, so the only thing asked of
			// it here is that it reads at all. It still has to beat the held pose by the seat margin below.
			if (provisional)
			{
				if (float.IsNegativeInfinity(decision.CandidateSignal))
				{
					decision.Refusal = "the best candidate has no reading";
					return;
				}
			}
			else if (!clearsAcquire(decision.CandidateSignal))
			{
				decision.Refusal = "the best candidate is below the acquire bar";
				return;
			}
			if (held >= 0)
			{
				// The ceiling. A live solve that clears the bar on this frame is
				// not a pose to be improved on: the tracker and the re-detector
				// must not compete for a frame the tracker is already winning, and
				// every displacement of a healthy pose so far has come from a
				// candidate that merely read higher.
				if (isTracking && decision.HeldClears)
				{
					decision.Refusal =
						"the pose the body holds is live and clears the acquire bar";
					return;
				}
				// Otherwise the candidate has to beat the held pose by more than
				// the held pose moves on its own from one frame to the next.
				// Beating it by less is inside the noise of the reading.
				if (!marginPassed)
				{
					decision.Refusal = "the best candidate does not beat the pose the "
						+ "body holds by the seat margin";
					return;
				}
			}

			decision.Seat = true;
			decision.SeatPath = isTracking ? SavedViewSeatPath.HeldSolve : SavedViewSeatPath.RetainedPose;
			state.PendingEntryId = rows[best].entry_id;
			state.PendingSeatFrame = frame;
			state.PendingPath = decision.SeatPath;
			state.PendingJudged = false;
			state.PreviousPosition = heldPosition;
			state.PreviousRotation = heldRotation;
			if (markSeated)
				state.MarkSeatedEntryId = rows[best].entry_id;
		}

		/// <summary>
		/// Scores the next kept pose beside the pose the body holds and puts it through
		/// the same choice as a saved view.
		/// </summary>
		/// <remarks>
		/// It runs only while the body is not tracking, after
		/// <see cref="TrackerDefaults.SEEN_POSE_RECOVERY_DELAY_FRAMES"/> rows out of tracked,
		/// and never while a seat is waiting on its window. Each attempt costs exactly two
		/// pose evaluations. It leaves the saved-view counters alone, so the saved views
		/// keep their own cadence.
		/// </remarks>
		private void AttemptSeenPose(SavedViewRecoveryState state, ref SavedViewDecision decision,
			string bodyId, int frame, int bankSize, bool isTracking, Func<float, bool> clearsAcquire,
			Vector3 heldPosition, Quaternion heldRotation,
			TrackerDefaults.MarginResidualTerm marginResidualTerm, SeenPoseCandidates seenPoses,
			bool provisional, CoverageReadScale? coverageScale)
		{
			if (isTracking || state.PendingEntryId > 0 ||
				state.FramesOutOfTracked < TrackerDefaults.SEEN_POSE_RECOVERY_DELAY_FRAMES)
			{
				return;
			}
			// The cadence counts rows. A row it skips evaluates nothing and takes no pose from the sweep.
			if (KeptPoseCadenceRows > 0 && state.FramesSinceKeptAttempt < KeptPoseCadenceRows)
			{
				decision.CadenceSkipped = true;
				decision.CadenceRowsSinceAttempt = state.FramesSinceKeptAttempt;
				decision.Source = SavedViewCandidateSource.SeenPose;
				return;
			}
			if (!seenPoses.TryNext(out SeenPose candidate))
				return;
			state.FramesSinceKeptAttempt = 0;
			double started = _milliseconds();
			var rows = FTVerifiedCandidate.CreateBuffer(2);
			bool scored = _seenPoseScorer.TryScore(bodyId, candidate, rows, out string refusal);
			decision.Attempted = true;
			decision.Source = SavedViewCandidateSource.SeenPose;
			decision.SeenPoseLabel = candidate.Label;
			decision.BankSize = bankSize;
			decision.Rows = rows;
			decision.RowCount = scored ? 2 : 0;
			decision.Milliseconds = _milliseconds() - started;
			if (!scored)
			{
				decision.Refusal = refusal;
				return;
			}
			if (_seenPoseScorer is ICandidateTimingSource timingSource)
			{
				var timings = new FTPoseEvidenceTiming[2];
				if (timingSource.TryReadTimings(bodyId, 2, timings))
					decision.Timings = timings;
			}
			if (_seenPoseScorer is ICandidateStartTimingSource startTimingSource)
			{
				var startTimings = new FTPoseEvidenceStartTiming[2];
				if (startTimingSource.TryReadStartTimings(bodyId, 2, startTimings))
					decision.StartTimings = startTimings;
			}
			Choose(state, ref decision, rows, 2, frame, isTracking, clearsAcquire, heldPosition,
				heldRotation, marginResidualTerm, markSeated: false, provisional, coverageScale);
		}

		/// <summary>
		/// Empties the bank: switches it off so the plugin drops every learned
		/// view, and forgets the configuration so the next frame switches it back on
		/// empty. The reset the host performs on a tracking loss never reaches
		/// here, which is what lets learned views survive it.
		/// </summary>
		public void EmptyBank(SavedViewRecoveryState state, string bodyId, float rotationDeg,
			float translationM, float maxProbeResidual, float minEdgeCoverage)
		{
			_port.Configure(bodyId, 0, rotationDeg, translationM, maxProbeResidual, minEdgeCoverage);
			state.Configured = false;
			state.ConfiguredCapacity = -1;
			state.PendingEntryId = 0;
			state.FramesSinceKeptAttempt = SavedViewRecoveryState.NO_PREVIOUS_ATTEMPT;
			state.MarkSeatedEntryId = 0;
			state.MarkWindowFailedEntryId = 0;
		}
	}
}
