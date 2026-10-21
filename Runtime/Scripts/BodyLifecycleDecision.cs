namespace IV.FormulaTracker
{
	/// <summary>
	/// Which processing path took a body's lifecycle decision on a frame.
	/// </summary>
	/// <remarks>
	/// The paths do not run the same checks, and which one ran is the first thing
	/// a reader needs: a check that is absent from a path reads exactly like a
	/// check that ran and passed unless the path is named.
	/// </remarks>
	internal enum LifecyclePath
	{
		/// <summary>No decision was taken for this body on this frame.</summary>
		None,

		/// <summary>The frame-hysteresis path, which starts and loses tracking.</summary>
		Standard,

		/// <summary>The stationary path under pose fusion, which accepts and holds.</summary>
		StationaryFusion,

		/// <summary>A rigid child, whose pose follows its parent.</summary>
		Child,
	}

	/// <summary>Which way a loop with two bars asked its reading.</summary>
	internal enum HoldDirection
	{
		/// <summary>Not a reading a two-bar loop took.</summary>
		None,

		/// <summary>Asked while the body still holds its lock: whether to give it up.</summary>
		GiveUp,

		/// <summary>Asked while the body is held: whether to take the lock back.</summary>
		TakeBack,
	}

	/// <summary>
	/// One reading of a quality scalar against one band threshold, as the decision
	/// that took it saw it.
	/// </summary>
	/// <remarks>
	/// Recorded where the comparison is made rather than recomputed afterwards. A
	/// second reading taken later is a different number from the one that decided,
	/// and the difference between them is invisible in the result.
	/// </remarks>
	internal struct LifecycleComparison
	{
		/// <summary>Whether this comparison was made at all on this frame.</summary>
		public bool Ran;

		/// <summary>The scalar that was compared.</summary>
		public float Value;

		/// <summary>The threshold it was compared against.</summary>
		public float Threshold;

		/// <summary>Which of the two quality scalars <see cref="Value"/> is.</summary>
		public QualitySignal Signal;

		/// <summary>Whether the reading cleared its threshold.</summary>
		public bool Clears;

		/// <summary>Which way a two-bar loop asked this reading. <see cref="Threshold"/>
		/// stays the bar tested, so readers that predate this keep their meaning.</summary>
		public HoldDirection Direction;

		/// <summary>The bar a reading falls under to give the lock up.</summary>
		public float GiveUpBar;

		/// <summary>The bar a reading clears to take the lock back.</summary>
		public float TakeBackBar;

		/// <summary>Records a reading that was made.</summary>
		public static LifecycleComparison Taken(
			float value, float threshold, QualitySignal signal, bool clears)
		{
			return new LifecycleComparison
			{
				Ran = true,
				Value = value,
				Threshold = threshold,
				Signal = signal,
				Clears = clears,
			};
		}
	}

	/// <summary>
	/// What the shipped tracking lifecycle decided about one body on one frame.
	/// </summary>
	/// <remarks>
	/// <para>Read-only reporting. Nothing in this package branches on it, and the
	/// decisions write it after they have already been taken, so a body observed
	/// through it behaves exactly as one that is not.</para>
	/// <para>A comparison whose <see cref="LifecycleComparison.Ran"/> is false was
	/// not made on this frame by this path. That is a statement about the path and
	/// not about the body.</para>
	/// </remarks>
	internal struct BodyLifecycleDecision
	{
		/// <summary>The path that ran.</summary>
		public LifecyclePath Path;

		/// <summary>Whether the body was tracking when the path ran.</summary>
		public bool IsTracking;

		/// <summary>The entry reading, made only while the body is not tracking.</summary>
		public LifecycleComparison Acquire;

		/// <summary>The holding reading, made only by a path that can lose tracking.</summary>
		public LifecycleComparison Hold;

		/// <summary>The acceptance reading, made only while the body is tracking.</summary>
		public LifecycleComparison Accept;

		/// <summary>
		/// The reading that decides whether the lock is kept, made by the stationary
		/// path under pose fusion.
		/// </summary>
		public LifecycleComparison Release;

		/// <summary>Whether the body is held at its seat rather than at its solve.</summary>
		public bool HoldActive;

		/// <summary>Whether the lock was taken back on this frame.</summary>
		public bool ReEntered;

		/// <summary>Whether a re-entry was possible this frame and was not taken.</summary>
		public bool ReEntryRefused;

		/// <summary>Consecutive frames counted toward starting.</summary>
		public int GoodFrameCount;

		/// <summary>Consecutive frames counted toward losing.</summary>
		public int BadFrameCount;

		/// <summary>Frames since tracking last started.</summary>
		public int FramesSinceTrackingStarted;

		/// <summary>Whether the settling window is still open.</summary>
		public bool InWarmup;

		/// <summary>Whether tracking started on this frame.</summary>
		public bool StartFired;

		/// <summary>Whether tracking was given up on this frame.</summary>
		public bool StopFired;

		/// <summary>
		/// What the start margin gate read on this frame, when the tooling switched it on. Its
		/// <see cref="StartMarginGateReading.Ran"/> is false otherwise.
		/// </summary>
		public StartMarginGateReading StartGate;

		/// <summary>
		/// What the final-row clause of a start read on this frame, when the tooling switched it on. Its
		/// <see cref="StartFinalRowReading.Ran"/> is false otherwise.
		/// </summary>
		public StartFinalRowReading FinalRow;

		/// <summary>What the re-check of the predicted pose did on this frame, when the tooling switched it on.</summary>
		public FusionRecheckReading Recheck;
	}
}
