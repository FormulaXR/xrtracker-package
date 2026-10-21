namespace IV.FormulaTracker
{
	/// <summary>
	/// What the final-row clause of a start read on one row and what it decided. Read-only reporting for the replay record.
	/// </summary>
	internal struct StartFinalRowReading
	{
		/// <summary>Whether the clause was on for this row. When false every other field is its default.</summary>
		public bool Ran;

		/// <summary>The bar the score was read against.</summary>
		public float Bar;

		/// <summary>The coverage score the start bar compares on this row, on the scaled reading of the body.</summary>
		public float Value;

		/// <summary>Whether the score stood at or above the bar.</summary>
		public bool Passed;

		/// <summary>Whether the coverage decision started the body on this row and the clause withheld the start.</summary>
		public bool Withheld;
	}

	/// <summary>
	/// The final-row clause on a start: the row on which a run of start rows completes has to read at or above a bar on the
	/// same scaled coverage score the start bar compares, in addition to clearing the start bar itself.
	/// </summary>
	/// <remarks>
	/// Coverage stays the decision that starts a body, and the clause only withholds a start that coverage would have taken.
	/// It is read on the row the start would fire and on no other row, so the rows before it count exactly as they do
	/// without the clause. A withheld start leaves the count where the coverage check left it. The bar is the confirm
	/// score of the body, which is the start score unless the body sets its own, or the bar a replay states. The clause
	/// applies only where the start margin gate applies and the body reads coverage.
	/// </remarks>
	internal static class StartFinalRow
	{
		/// <summary>The highest bar that is read as a bar. The coverage score cannot read above it.</summary>
		internal const float MAX_BAR = 1f;

		/// <summary>
		/// The bar an explicit request names, or null when the request is null or names a value that is not a number from 0
		/// to <see cref="MAX_BAR"/>. Such a value leaves the start decision as it is without the clause.
		/// </summary>
		internal static float? BarFor(float? requested) =>
			requested.HasValue && !float.IsNaN(requested.Value) && requested.Value >= 0f && requested.Value <= MAX_BAR
				? requested
				: null;

		/// <summary>
		/// The bar the lifecycle reads for one body on one path, or null when the clause does not apply. It applies to a body
		/// that tracks by the edge method, reads the coverage score and takes the frame-hysteresis path, which is where the
		/// start margin gate applies. A body under pose fusion (the stationary path) and a body that tracks by the
		/// silhouette method are never touched.
		/// </summary>
		internal static float? Resolve(float? requested, bool edgeTracking,
			TrackerDefaults.QualitySignalSource source, LifecyclePath path) =>
			edgeTracking && source == TrackerDefaults.QualitySignalSource.Coverage && path == LifecyclePath.Standard
				? BarFor(requested)
				: null;

		/// <summary>Reads the score of one row against a bar. Equal passes, and a score that is not a number does not.</summary>
		internal static StartFinalRowReading Read(float bar, float value)
		{
			return new StartFinalRowReading
			{
				Ran = true,
				Bar = bar,
				Value = value,
				Passed = value >= bar,
			};
		}
	}
}
