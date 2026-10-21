namespace IV.FormulaTracker
{
	/// <summary>The temporal interpretation of successive registration measurements.</summary>
	internal enum TemporalTrackingState
	{
		Unavailable = 0,
		Acquiring = 1,
		Tracked = 2,
		Critical = 3,
		Lost = 4,
		Recovering = 5,
	}

	/// <summary>Why the temporal state has its current value.</summary>
	internal enum TemporalTrackingReason
	{
		Reset = 0,
		Removed = 1,
		Unsupported = 2,
		MeasurementUnavailable = 3,
		AcquiringEvidence = 4,
		AcquiringWeakEvidence = 5,
		SustainedGoodEvidence = 6,
		WeakEvidence = 7,
		CatastrophicEvidence = 8,
		SustainedBadEvidence = 9,
		RecoveryRequested = 10,
		RecoveryPreview = 11,
		RecoveryConfirmed = 12,
		RecoverySettling = 13,
		RecoveryCancelled = 14,
		Mirrored = 15,
		BelowTrackedEvidence = 16,
	}

	/// <summary>Read-only observation of the explicit recovery workflow.</summary>
	internal enum TemporalRecoveryPhase
	{
		None = 0,
		Requested = 1,
		Preview = 2,
		Confirmed = 3,
		Cancelled = 4,
	}

	/// <summary>Injectable thresholds and consecutive-frame requirements.</summary>
	internal readonly struct TemporalTrackingPolicy
	{
		public TemporalTrackingPolicy(float acquireGood, int acquireFrames,
			float trackedGood, float criticalBoundary, float catastrophicBoundary,
			int lostFrames, int catastrophicFrames, int recoveryFrames,
			bool unavailableImmediately = false,
			bool acquireFailureCanBecomeLost = false)
		{
			AcquireGood = acquireGood;
			AcquireFrames = acquireFrames;
			TrackedGood = trackedGood;
			CriticalBoundary = criticalBoundary;
			CatastrophicBoundary = catastrophicBoundary;
			LostFrames = lostFrames;
			CatastrophicFrames = catastrophicFrames;
			RecoveryFrames = recoveryFrames;
			UnavailableImmediately = unavailableImmediately;
			AcquireFailureCanBecomeLost = acquireFailureCanBecomeLost;
		}

		public float AcquireGood { get; }
		public int AcquireFrames { get; }
		public float TrackedGood { get; }
		public float CriticalBoundary { get; }
		public float CatastrophicBoundary { get; }
		public int LostFrames { get; }
		public int CatastrophicFrames { get; }
		public int RecoveryFrames { get; }
		public bool UnavailableImmediately { get; }
		public bool AcquireFailureCanBecomeLost { get; }

		/// <summary>The first emit-only development policy.</summary>
		internal static TemporalTrackingPolicy V1EmitOnly => new TemporalTrackingPolicy(
			0.75f, 3, 0.55f, 0.35f, 0.10f, 5, 2, 3);

		/// <summary>Calibration selected on the closed synthetic tuning split.</summary>
		internal static TemporalTrackingPolicy V2SyntheticCalibration =>
			new TemporalTrackingPolicy(0.056928087f, 3, 0.056928087f,
				0.03f, 0.01f, 5, 2, 3, true, true);

		/// <summary>V5 product-weighted tuning point; emit-only.</summary>
		internal static TemporalTrackingPolicy V3SyntheticCalibration =>
			new TemporalTrackingPolicy(0.049695376f, 3, 0.049695376f,
				0.024847688f, 0.008f, 5, 2, 3, true, true);

		/// <summary>Initial intuitive confidence semantics for live calibration.</summary>
		public static TemporalTrackingPolicy LiveCalibrationCandidate =>
			new TemporalTrackingPolicy(0.65f, 3, 0.55f,
				0.35f, 0.10f, 5, 2, 3, true, true);

		/// <summary>
		/// Thresholds read as fractions of what the current view normally achieves.
		/// </summary>
		/// <remarks>
		/// Every number here is relative, so one set covers every object, view and
		/// distance: 1.0 is "as good as this view usually is". Acquiring takes five
		/// frames rather than three because the window it belongs to is what
		/// separates a start that settles from one that walks away, and three
		/// frames is not enough of a walk to see.
		/// </remarks>
		public static TemporalTrackingPolicy RelativeHealth =>
			new TemporalTrackingPolicy(0.75f, 5, 0.55f, 0.35f, 0.15f, 5, 2, 5);
	}

	/// <summary>Inputs owned by one completed immutable tracking frame.</summary>
	internal readonly struct TemporalTrackingInput
	{
		public TemporalTrackingInput(int frameIndex, long stepSerial,
			bool isRegistered, RegistrationQualitySnapshot quality,
			TemporalRecoveryPhase recoveryPhase, ulong recoveryToken)
			: this(frameIndex, stepSerial, isRegistered, quality, recoveryPhase,
				recoveryToken, false, false, 0f, true, false, false)
		{
		}

		private TemporalTrackingInput(int frameIndex, long stepSerial,
			bool isRegistered, RegistrationQualitySnapshot quality,
			TemporalRecoveryPhase recoveryPhase, ulong recoveryToken,
			bool hasEvidenceOverride, bool overrideAvailable, float overrideValue,
			bool poseSettled, bool coldStartWindow, bool coldStartSatisfied)
		{
			FrameIndex = frameIndex;
			StepSerial = stepSerial;
			IsRegistered = isRegistered;
			Quality = quality;
			RecoveryPhase = recoveryPhase;
			RecoveryToken = recoveryToken;
			HasEvidenceOverride = hasEvidenceOverride;
			OverrideAvailable = overrideAvailable;
			OverrideValue = overrideValue;
			PoseSettled = poseSettled;
			ColdStartWindow = coldStartWindow;
			ColdStartSatisfied = coldStartSatisfied;
		}

		/// <summary>
		/// The same frame, decided on a relative health value rather than on the
		/// registration score, with the verification window's own two inputs.
		/// </summary>
		/// <param name="healthAvailable">
		/// Whether a health value was measured at all. False is missing evidence,
		/// which is not the same statement as a health of zero.
		/// </param>
		/// <param name="health">The measured health.</param>
		/// <param name="poseSettled">
		/// Whether this frame's pose step is small enough for the frame to count
		/// toward acquisition. A start that is still walking is not acquiring.
		/// </param>
		/// <param name="coldStartWindow">
		/// Whether this view has no expectation yet, in which case health cannot
		/// exist and the window runs on the caller's raw-ratio rule instead.
		/// </param>
		/// <param name="coldStartSatisfied">Whether that rule holds on this frame.</param>
		public static TemporalTrackingInput WithHealth(int frameIndex, long stepSerial,
			bool isRegistered, RegistrationQualitySnapshot quality,
			TemporalRecoveryPhase recoveryPhase, ulong recoveryToken,
			bool healthAvailable, float health, bool poseSettled,
			bool coldStartWindow, bool coldStartSatisfied)
		{
			return new TemporalTrackingInput(frameIndex, stepSerial, isRegistered,
				quality, recoveryPhase, recoveryToken, true, healthAvailable, health,
				poseSettled, coldStartWindow, coldStartSatisfied);
		}

		public int FrameIndex { get; }
		public long StepSerial { get; }
		public bool IsRegistered { get; }
		public RegistrationQualitySnapshot Quality { get; }
		public TemporalRecoveryPhase RecoveryPhase { get; }
		public ulong RecoveryToken { get; }

		/// <summary>Whether the machine reads a supplied scalar instead of the score.</summary>
		public bool HasEvidenceOverride { get; }

		/// <summary>Whether the frame's pose step counts as settled.</summary>
		public bool PoseSettled { get; }

		/// <summary>Whether the acquiring window is running with no expectation yet.</summary>
		public bool ColdStartWindow { get; }

		/// <summary>Whether the caller's cold-start rule holds on this frame.</summary>
		public bool ColdStartSatisfied { get; }

		private bool OverrideAvailable { get; }
		private float OverrideValue { get; }

		/// <summary>Whether this frame carries a usable evidence scalar.</summary>
		public bool EvidenceAvailable =>
			HasEvidenceOverride ? OverrideAvailable : Quality.ScoreAvailable;

		/// <summary>The scalar every threshold in the machine is read against.</summary>
		public float Evidence =>
			HasEvidenceOverride ? OverrideValue : Quality.Score;
	}

	/// <summary>State carried from one completed frame to the next.</summary>
	internal struct TemporalTrackingMemory
	{
		internal bool Initialized;
		internal TemporalTrackingState State;
		internal TemporalTrackingReason Reason;
		internal int AgeFrames;
		internal int TransitionFrameIndex;
		internal long TransitionStepSerial;
		internal int AcquireGoodFrames;
		internal int BelowTrackedFrames;
		internal int CatastrophicFrames;
		internal int RecoveryGoodFrames;
		internal ulong RecoveryToken;
		internal bool RecoveryConfirmed;
	}

	/// <summary>Public immutable temporal status for a body.</summary>
	internal readonly struct TemporalTrackingSnapshot
	{
		internal TemporalTrackingSnapshot(TemporalTrackingState state,
			TemporalTrackingReason reason, int ageFrames, int transitionFrameIndex,
			long transitionStepSerial, int frameIndex, long stepSerial,
			TemporalRecoveryPhase recoveryPhase, ulong recoveryToken,
			string mirroredFromBodyId, float evidence, bool evidenceAvailable,
			bool evidenceIsOverride)
		{
			State = state;
			Reason = reason;
			AgeFrames = ageFrames;
			TransitionFrameIndex = transitionFrameIndex;
			TransitionStepSerial = transitionStepSerial;
			FrameIndex = frameIndex;
			StepSerial = stepSerial;
			RecoveryPhase = recoveryPhase;
			RecoveryToken = recoveryToken;
			MirroredFromBodyId = mirroredFromBodyId ?? string.Empty;
			Evidence = evidence;
			EvidenceAvailable = evidenceAvailable;
			EvidenceIsOverride = evidenceIsOverride;
		}

		public TemporalTrackingState State { get; }
		public TemporalTrackingReason Reason { get; }
		public int AgeFrames { get; }
		public int TransitionFrameIndex { get; }
		public long TransitionStepSerial { get; }
		public int FrameIndex { get; }
		public long StepSerial { get; }
		public TemporalRecoveryPhase RecoveryPhase { get; }
		public ulong RecoveryToken { get; }
		public string MirroredFromBodyId { get; }

		/// <summary>The scalar every threshold in this decision was read against.</summary>
		/// <remarks>
		/// Read-only reporting. This is the value the decision was handed, copied as
		/// the decision was taken and never recomputed afterwards: which of the two
		/// candidate scalars the machine reads depends on a branch outside this
		/// assembly, so a reader that re-derived it could disagree with the machine on
		/// exactly the frames the decision mattered. Nothing in this package branches
		/// on any of the three.
		/// </remarks>
		public float Evidence { get; }

		/// <summary>Whether that scalar was usable on this frame.</summary>
		public bool EvidenceAvailable { get; }

		/// <summary>Which of the two candidate scalars the decision was taken on.</summary>
		public bool EvidenceIsOverride { get; }

		public bool IsMirrored => !string.IsNullOrEmpty(MirroredFromBodyId);
	}

	/// <summary>
	/// Pure emit-only temporal state machine. Nothing in this type can publish a
	/// pose, mutate tracking, or invoke the recovery workflow.
	/// </summary>
	internal static class TemporalTrackingMachine
	{
		public static TemporalTrackingSnapshot Decide(ref TemporalTrackingMemory memory,
			in TemporalTrackingInput input, in TemporalTrackingPolicy policy)
		{
			if (!input.IsRegistered)
			{
				ClearCounters(ref memory);
				return Set(ref memory, input, TemporalTrackingState.Unavailable,
					TemporalTrackingReason.Removed);
			}

			if (!input.Quality.FeatureSupported)
			{
				ClearCounters(ref memory);
				return Set(ref memory, input, TemporalTrackingState.Unavailable,
					TemporalTrackingReason.Unsupported);
			}

			if (!input.EvidenceAvailable && policy.UnavailableImmediately)
			{
				ClearCounters(ref memory);
				return Set(ref memory, input, TemporalTrackingState.Unavailable,
					TemporalTrackingReason.MeasurementUnavailable);
			}

			if (input.RecoveryPhase == TemporalRecoveryPhase.Cancelled)
			{
				ClearCounters(ref memory);
				memory.RecoveryToken = input.RecoveryToken;
				return Set(ref memory, input, TemporalTrackingState.Lost,
					TemporalTrackingReason.RecoveryCancelled);
			}

			bool newRecovery = input.RecoveryToken != 0 &&
				input.RecoveryToken != memory.RecoveryToken;
			if (newRecovery)
			{
				ClearCounters(ref memory);
				memory.RecoveryToken = input.RecoveryToken;
				memory.RecoveryConfirmed = false;
			}

			if (input.RecoveryPhase == TemporalRecoveryPhase.Requested ||
				input.RecoveryPhase == TemporalRecoveryPhase.Preview)
			{
				memory.RecoveryGoodFrames = 0;
				memory.RecoveryConfirmed = false;
				return Set(ref memory, input, TemporalTrackingState.Recovering,
					input.RecoveryPhase == TemporalRecoveryPhase.Requested
						? TemporalTrackingReason.RecoveryRequested
						: TemporalTrackingReason.RecoveryPreview);
			}

			if (input.RecoveryPhase == TemporalRecoveryPhase.Confirmed &&
				(newRecovery || memory.State == TemporalTrackingState.Recovering ||
				 !memory.RecoveryConfirmed))
			{
				memory.RecoveryConfirmed = true;
				// The same two conditions a cold start's window asks. A recovered
				// pose that holds good evidence while still walking is the exact
				// failure the window exists to catch, and it looks no different from
				// a good one in the evidence alone.
				if (input.PoseSettled && input.EvidenceAvailable &&
					input.Evidence >= policy.AcquireGood)
				{
					++memory.RecoveryGoodFrames;
					if (memory.RecoveryGoodFrames >= policy.RecoveryFrames)
					{
						ClearQualityCounters(ref memory);
						return Set(ref memory, input, TemporalTrackingState.Tracked,
							TemporalTrackingReason.SustainedGoodEvidence);
					}
				}
				else
				{
					memory.RecoveryGoodFrames = 0;
				}
				return Set(ref memory, input, TemporalTrackingState.Recovering,
					memory.RecoveryGoodFrames > 0
						? TemporalTrackingReason.RecoverySettling
						: TemporalTrackingReason.RecoveryConfirmed);
			}

			switch (memory.Initialized ? memory.State : TemporalTrackingState.Unavailable)
			{
				case TemporalTrackingState.Tracked:
					return DecideTracked(ref memory, input, policy);
				case TemporalTrackingState.Critical:
					return DecideCritical(ref memory, input, policy);
				case TemporalTrackingState.Lost:
					return DecideLost(ref memory, input, policy);
				case TemporalTrackingState.Recovering:
					return Set(ref memory, input, TemporalTrackingState.Recovering,
						TemporalTrackingReason.RecoverySettling);
				case TemporalTrackingState.Acquiring:
				case TemporalTrackingState.Unavailable:
				default:
					return DecideAcquiring(ref memory, input, policy);
			}
		}

		public static TemporalTrackingSnapshot Reset(ref TemporalTrackingMemory memory,
			int frameIndex, long stepSerial)
		{
			memory = default;
			var input = new TemporalTrackingInput(frameIndex, stepSerial, true,
				RegistrationQualitySnapshot.Unsupported, TemporalRecoveryPhase.None, 0);
			return Set(ref memory, input, TemporalTrackingState.Unavailable,
				TemporalTrackingReason.Reset);
		}

		public static TemporalTrackingSnapshot Mirror(ref TemporalTrackingMemory memory,
			in TemporalTrackingSnapshot parent, string parentBodyId,
			int frameIndex, long stepSerial)
		{
			memory.Initialized = true;
			memory.State = parent.State;
			memory.Reason = parent.Reason;
			memory.AgeFrames = parent.AgeFrames;
			memory.TransitionFrameIndex = parent.TransitionFrameIndex;
			memory.TransitionStepSerial = parent.TransitionStepSerial;
			ClearQualityCounters(ref memory);
			memory.RecoveryToken = parent.RecoveryToken;
			return new TemporalTrackingSnapshot(parent.State, parent.Reason,
				parent.AgeFrames, parent.TransitionFrameIndex,
				parent.TransitionStepSerial, frameIndex, stepSerial,
				parent.RecoveryPhase, parent.RecoveryToken, parentBodyId,
				parent.Evidence, parent.EvidenceAvailable, parent.EvidenceIsOverride);
		}

		private static TemporalTrackingSnapshot DecideAcquiring(
			ref TemporalTrackingMemory memory, in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy)
		{
			if (CountsTowardAcquisition(input, policy))
			{
				memory.BelowTrackedFrames = 0;
				memory.CatastrophicFrames = 0;
				++memory.AcquireGoodFrames;
				if (memory.AcquireGoodFrames >= policy.AcquireFrames)
				{
					ClearQualityCounters(ref memory);
					return Set(ref memory, input, TemporalTrackingState.Tracked,
						TemporalTrackingReason.SustainedGoodEvidence);
				}
				return Set(ref memory, input, TemporalTrackingState.Acquiring,
					TemporalTrackingReason.AcquiringEvidence);
			}

			memory.AcquireGoodFrames = 0;
			if (policy.AcquireFailureCanBecomeLost && input.EvidenceAvailable)
			{
				++memory.BelowTrackedFrames;
				memory.CatastrophicFrames = IsCatastrophic(input, policy)
					? memory.CatastrophicFrames + 1 : 0;
				if (memory.BelowTrackedFrames >= policy.LostFrames ||
					memory.CatastrophicFrames >= policy.CatastrophicFrames)
				{
					return Set(ref memory, input, TemporalTrackingState.Lost,
						TemporalTrackingReason.SustainedBadEvidence);
				}
			}
			return Set(ref memory, input, TemporalTrackingState.Acquiring,
				input.EvidenceAvailable || input.ColdStartWindow
					? TemporalTrackingReason.AcquiringWeakEvidence
					: TemporalTrackingReason.MeasurementUnavailable);
		}

		private static TemporalTrackingSnapshot DecideTracked(
			ref TemporalTrackingMemory memory, in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy)
		{
			if (input.EvidenceAvailable && input.Evidence >= policy.TrackedGood)
			{
				ClearQualityCounters(ref memory);
				return Set(ref memory, input, TemporalTrackingState.Tracked,
					TemporalTrackingReason.SustainedGoodEvidence);
			}

			memory.BelowTrackedFrames = 1;
			memory.CatastrophicFrames = IsCatastrophic(input, policy) ? 1 : 0;
			return Set(ref memory, input, TemporalTrackingState.Critical,
				ReasonForBadInput(input, policy));
		}

		private static TemporalTrackingSnapshot DecideCritical(
			ref TemporalTrackingMemory memory, in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy)
		{
			if (input.EvidenceAvailable && input.Evidence >= policy.TrackedGood)
			{
				ClearQualityCounters(ref memory);
				return Set(ref memory, input, TemporalTrackingState.Tracked,
					TemporalTrackingReason.SustainedGoodEvidence);
			}

			++memory.BelowTrackedFrames;
			memory.CatastrophicFrames = IsCatastrophic(input, policy)
				? memory.CatastrophicFrames + 1
				: 0;
			if (memory.BelowTrackedFrames >= policy.LostFrames ||
				memory.CatastrophicFrames >= policy.CatastrophicFrames)
			{
				return Set(ref memory, input, TemporalTrackingState.Lost,
					TemporalTrackingReason.SustainedBadEvidence);
			}
			return Set(ref memory, input, TemporalTrackingState.Critical,
				ReasonForBadInput(input, policy));
		}

		private static TemporalTrackingSnapshot DecideLost(
			ref TemporalTrackingMemory memory, in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy)
		{
			ClearQualityCounters(ref memory);
			if (!input.EvidenceAvailable && !input.ColdStartWindow)
			{
				return Set(ref memory, input, TemporalTrackingState.Lost,
					TemporalTrackingReason.MeasurementUnavailable);
			}
			return DecideAcquiring(ref memory, input, policy);
		}

		/// <summary>
		/// Whether one frame counts toward the verification window.
		/// </summary>
		/// <remarks>
		/// Two conditions, and both are about this frame rather than about the
		/// pose's history. The evidence has to reach the entry bar, and the pose has
		/// to have stopped moving: a start from a wrong pose can hold better static
		/// evidence than a start from the right one, and what separates them is that
		/// the wrong one keeps walking while the right one settles.
		/// </remarks>
		private static bool CountsTowardAcquisition(in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy)
		{
			if (!input.PoseSettled)
				return false;
			if (input.ColdStartWindow)
				return input.ColdStartSatisfied;
			return input.EvidenceAvailable && input.Evidence >= policy.AcquireGood;
		}

		private static bool IsCatastrophic(in TemporalTrackingInput input,
			in TemporalTrackingPolicy policy) =>
			input.EvidenceAvailable &&
			input.Evidence <= policy.CatastrophicBoundary;

		private static TemporalTrackingReason ReasonForBadInput(
			in TemporalTrackingInput input, in TemporalTrackingPolicy policy)
		{
			if (!input.EvidenceAvailable)
				return TemporalTrackingReason.MeasurementUnavailable;
			if (input.Evidence <= policy.CatastrophicBoundary)
				return TemporalTrackingReason.CatastrophicEvidence;
			return input.Evidence < policy.CriticalBoundary
				? TemporalTrackingReason.WeakEvidence
				: TemporalTrackingReason.BelowTrackedEvidence;
		}

		private static TemporalTrackingSnapshot Set(ref TemporalTrackingMemory memory,
			in TemporalTrackingInput input, TemporalTrackingState state,
			TemporalTrackingReason reason)
		{
			if (!memory.Initialized || memory.State != state)
			{
				memory.Initialized = true;
				memory.State = state;
				memory.AgeFrames = 0;
				memory.TransitionFrameIndex = input.FrameIndex;
				memory.TransitionStepSerial = input.StepSerial;
			}
			else if (memory.AgeFrames < int.MaxValue)
			{
				++memory.AgeFrames;
			}
			memory.Reason = reason;
			return new TemporalTrackingSnapshot(memory.State, reason, memory.AgeFrames,
				memory.TransitionFrameIndex, memory.TransitionStepSerial,
				input.FrameIndex, input.StepSerial, input.RecoveryPhase,
				input.RecoveryToken, string.Empty, input.Evidence,
				input.EvidenceAvailable, input.HasEvidenceOverride);
		}

		private static void ClearCounters(ref TemporalTrackingMemory memory)
		{
			ClearQualityCounters(ref memory);
			memory.RecoveryGoodFrames = 0;
			memory.RecoveryToken = 0;
			memory.RecoveryConfirmed = false;
		}

		private static void ClearQualityCounters(ref TemporalTrackingMemory memory)
		{
			memory.AcquireGoodFrames = 0;
			memory.BelowTrackedFrames = 0;
			memory.CatastrophicFrames = 0;
		}
	}
}
