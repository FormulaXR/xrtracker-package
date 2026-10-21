namespace IV.FormulaTracker
{
	/// <summary>
	/// What the start margin gate read on one row and what it decided. Read-only reporting for the replay record.
	/// </summary>
	internal struct StartMarginGateReading
	{
		/// <summary>Whether the gate was on for this row. When false every other field is its default.</summary>
		public bool Ran;

		/// <summary>The bar the margin was read against.</summary>
		public float Bar;

		/// <summary>Whether a margin could be read on this row.</summary>
		public bool Read;

		/// <summary>The registration margin with the sub-pixel median term, when <see cref="Read"/>.</summary>
		public float Value;

		/// <summary>Whether the margin was read and stood at or above the bar.</summary>
		public bool Passed;

		/// <summary>Whether the coverage decision started the body on this row and the gate withheld the start.</summary>
		public bool Withheld;
	}

	/// <summary>
	/// The second condition on a start: the registration margin, read with its residual term taken from the median of the
	/// sub-pixel search offsets, has to stand at or above a bar on the row the start would be taken.
	/// </summary>
	/// <remarks>
	/// Coverage stays the decision that starts a body. The gate only withholds a start that coverage would have taken,
	/// and a row the margin cannot be read on withholds it, so the gate never invents a value. The margin is the one the
	/// saved-view verifier reads, whatever residual term the run uses for its own decisions.
	/// </remarks>
	internal static class StartMarginGate
	{
		/// <summary>The highest bar that is read as a bar. The margin cannot reach it on any pose.</summary>
		internal const float MAX_BAR = 2f;

		/// <summary>
		/// The value that states there is no gate. A negative value is not a bar, so it reads as no gate whatever the
		/// shipped default is.
		/// </summary>
		internal const float OFF = -1f;

		/// <summary>
		/// The bar an explicit request names, or null when the request is null or names a value that is not a number
		/// from 0 to <see cref="MAX_BAR"/>. Such a value leaves the start decision to coverage alone.
		/// </summary>
		internal static float? BarFor(float? requested) =>
			requested.HasValue && !float.IsNaN(requested.Value) && requested.Value >= 0f && requested.Value <= MAX_BAR
				? requested
				: null;

		/// <summary>
		/// The margin of one evidence record with the sub-pixel median term. False when the record carries no available
		/// score, no finite median, or no finite angle, or is of a schema this build does not read.
		/// </summary>
		internal static bool TryReadMargin(in RegistrationQualitySnapshot evidence, out float margin)
		{
			if (!evidence.FeatureSupported || !evidence.ScoreAvailable)
			{
				margin = 0f;
				return false;
			}
			return RegistrationQualitySnapshot.TryReadSubpixelMedianMargin(evidence.RawEvidence, out margin);
		}

		/// <summary>Reads the margin of one row against a bar. Equal passes.</summary>
		internal static StartMarginGateReading Read(float bar, in RegistrationQualitySnapshot evidence)
		{
			bool read = TryReadMargin(evidence, out float margin);
			return new StartMarginGateReading
			{
				Ran = true,
				Bar = bar,
				Read = read,
				Value = read ? margin : 0f,
				Passed = read && margin >= bar,
			};
		}
	}
}
