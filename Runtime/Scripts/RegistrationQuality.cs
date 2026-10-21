using System;

namespace IV.FormulaTracker
{
	/// <summary>Why an instantaneous registration-quality value is unavailable.</summary>
	internal enum RegistrationQualityUnavailableReason
	{
		Available = 0,
		NoEdge = 1,
		NoRetained = 2,
		NoExpected = 3,
		NoSearched = 4,
		NoMatched = 5,
		NoAgreeing = 6,
		ResidualUnavailable = 7,
		ProjectionUnavailable = 8,
		Nonfinite = 9,
		SpatialUnavailable = 10,
		OrientationUnavailable = 11,
		AbiMismatch = 12,
		Unsupported = 13,
		ScoreUnavailable = 14,
	}

	/// <summary>
	/// One completed frame's behavior-neutral registration-quality readout.
	/// </summary>
	/// <remarks>
	/// Availability is independent of the numeric value. A missing measurement is
	/// never represented by a score of zero, because zero is also a valid and useful
	/// statement about a frame whose evidence actively disagrees.
	/// </remarks>
	internal readonly struct RegistrationQualitySnapshot
	{
		public RegistrationQualitySnapshot(
			int schemaVersion,
			bool featureSupported,
			bool scoreAvailable,
			RegistrationQualityUnavailableReason unavailableReason,
			float score,
			int expectedSupport,
			int searchedSupport,
			int matchedSupport,
			int agreeingSupport,
			bool appearanceEvidenceAvailable,
			FTRegistrationQualityEvidence rawEvidence = default)
		{
			SchemaVersion = schemaVersion;
			FeatureSupported = featureSupported;
			ScoreAvailable = featureSupported && scoreAvailable && IsFinite(score);
			UnavailableReason = ScoreAvailable
				? RegistrationQualityUnavailableReason.Available
				: unavailableReason == RegistrationQualityUnavailableReason.Available
					? RegistrationQualityUnavailableReason.ScoreUnavailable
					: unavailableReason;
			// Passed through unclipped. The score is a margin against a bar: it
			// reads one when the evidence sits on the bar and above one when the
			// evidence is better than it, so a ceiling here would erase exactly
			// the distinction the number exists to make. Zero still stands for
			// "no reading", which <see cref="ScoreAvailable"/> is what to read.
			Score = ScoreAvailable ? score : 0f;
			ExpectedSupport = Math.Max(0, expectedSupport);
			SearchedSupport = Math.Max(0, searchedSupport);
			MatchedSupport = Math.Max(0, matchedSupport);
			AgreeingSupport = Math.Max(0, agreeingSupport);
			AppearanceEvidenceAvailable = appearanceEvidenceAvailable;
			RawEvidence = rawEvidence;
		}

		public int SchemaVersion { get; }
		public bool FeatureSupported { get; }
		public bool ScoreAvailable { get; }
		public RegistrationQualityUnavailableReason UnavailableReason { get; }
		public float Score { get; }
		public int ExpectedSupport { get; }
		public int SearchedSupport { get; }
		public int MatchedSupport { get; }
		public int AgreeingSupport { get; }
		public bool AppearanceEvidenceAvailable { get; }
		/// <summary>
		/// Whether the completed frame carried the separate emit-only
		/// initialization-confidence candidate.
		/// </summary>
		public bool InitializationConfidenceAvailable => FeatureSupported &&
			(RawEvidence.availability_mask &
			 FTRegistrationQualityEvidence.InitializationConfidenceAvailableBit) != 0 &&
			IsFinite(RawEvidence.initialization_confidence);
		/// <summary>
		/// Predicts whether tracking begun from this pose has broad, constrained
		/// support. It does not affect lifecycle, recovery, or pose publication.
		/// </summary>
		public float InitializationConfidence => InitializationConfidenceAvailable
			? Clamp01(RawEvidence.initialization_confidence) : 0f;
		/// <summary>
		/// The complete versioned native record that produced this snapshot.
		/// This is observational data only; it provides development recorders with
		/// the availability flags and raw components behind the public score.
		/// </summary>
		public FTRegistrationQualityEvidence RawEvidence { get; }

		public static RegistrationQualitySnapshot Unsupported =>
			new RegistrationQualitySnapshot(
				0, false, false, RegistrationQualityUnavailableReason.Unsupported,
				0f, 0, 0, 0, 0, false);

		internal static RegistrationQualitySnapshot FromNative(
			in FTRegistrationQualityEvidence evidence)
		{
			bool supported = evidence.schema_version == 2 &&
				evidence.model_version == 3 &&
				evidence.struct_size == FTRegistrationQualityEvidence.StructSize;
			if (!supported)
				return Unsupported;
			bool available = (evidence.availability_mask &
				FTRegistrationQualityEvidence.ScoreAvailableBit) != 0 &&
				evidence.unavailable_reason == 0;
			RegistrationQualityUnavailableReason reason =
				System.Enum.IsDefined(typeof(RegistrationQualityUnavailableReason),
					evidence.unavailable_reason)
					? (RegistrationQualityUnavailableReason)evidence.unavailable_reason
					: RegistrationQualityUnavailableReason.ScoreUnavailable;
			return new RegistrationQualitySnapshot(evidence.schema_version, true,
				available, reason, evidence.instantaneous_score,
				evidence.n_expected_visible_sites, evidence.n_searched_sites,
				evidence.n_matched_sites, evidence.n_agreeing_sites,
				evidence.appearance_available != 0 &&
				(evidence.availability_mask &
				 FTRegistrationQualityEvidence.AppearanceAvailableBit) != 0,
				evidence);
		}

		/// <summary>
		/// The registration margin of one record with its residual term read from the
		/// median of the sub-pixel search offsets.
		/// </summary>
		/// <remarks>
		/// <para>The alignment term is the plugin's, computed from the same published
		/// median angle with the same two constants. The residual term is
		/// <c>(limit - median) / (limit - bar)</c> on
		/// <see cref="TrackerDefaults.REGISTRATION_MARGIN_MEDIAN_LIMIT_PX"/> and
		/// <see cref="TrackerDefaults.REGISTRATION_MARGIN_MEDIAN_BAR_PX"/>, and the
		/// margin is the weaker of the two, as the plugin's is.</para>
		/// <para>The residual term is clamped at zero. The whole-pixel percentile cannot
		/// pass its limit inside the probe's search window, but the median can pass
		/// this one, and a reading below zero would say more than the evidence does.</para>
		/// <para>Returns false when the record carries no sub-pixel median. The caller
		/// reads that exactly as it reads a record that carries no score.</para>
		/// </remarks>
		internal static bool TryReadSubpixelMedianMargin(
			in FTRegistrationQualityEvidence record, out float margin)
		{
			float median = record.residual_subpixel_p50;
			if (!IsFinite(median) || median < 0f)
			{
				margin = 0f;
				return false;
			}
			float alignment =
				(TrackerDefaults.REGISTRATION_MARGIN_ALIGNMENT_LIMIT_DEG - record.projection_error_p50) /
				(TrackerDefaults.REGISTRATION_MARGIN_ALIGNMENT_LIMIT_DEG -
				 TrackerDefaults.REGISTRATION_MARGIN_ALIGNMENT_BAR_DEG);
			float residual =
				(TrackerDefaults.REGISTRATION_MARGIN_MEDIAN_LIMIT_PX - median) /
				(TrackerDefaults.REGISTRATION_MARGIN_MEDIAN_LIMIT_PX -
				 TrackerDefaults.REGISTRATION_MARGIN_MEDIAN_BAR_PX);
			if (residual < 0f)
				residual = 0f;
			margin = Math.Min(alignment, residual);
			if (!IsFinite(margin))
			{
				margin = 0f;
				return false;
			}
			return true;
		}

		private static bool IsFinite(float value) =>
			!float.IsNaN(value) && !float.IsInfinity(value);

		private static float Clamp01(float value) =>
			value < 0f ? 0f : value > 1f ? 1f : value;
	}
}
