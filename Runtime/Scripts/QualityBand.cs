namespace IV.FormulaTracker
{
	/// <summary>
	/// A reading of a quality scalar against one of the band's thresholds.
	/// </summary>
	/// <remarks>
	/// Each of these reads a quality scalar against a band threshold, and a reading
	/// that is not in this table is a reading nobody can audit.
	/// </remarks>
	internal enum QualityDecision
	{
		/// <summary>Whether tracking may start, or restart, on a body.</summary>
		Acquire,

		/// <summary>Whether tracking is still held rather than declared lost.</summary>
		Hold,

		/// <summary>Whether this frame's solve becomes the body's pose.</summary>
		Accept,
	}

	/// <summary>
	/// Which quality scalar a reading takes.
	/// </summary>
	internal enum QualitySignal
	{
		/// <summary>This frame's quality alone.</summary>
		Instant,

		/// <summary>The running average over the quality window.</summary>
		Averaged,
	}

	/// <summary>
	/// The acquire / hold / accept thresholds a body's decisions run at, and the
	/// signal each decision reads them with.
	/// </summary>
	/// <remarks>
	/// <para>Three decisions read three thresholds through two different scalars.
	/// That is not wrong, but before this type existed it was undeclared: each
	/// reading picked its own scalar at its own call site, and no single place
	/// said which. <see cref="SignalFor"/> is that place, and it is the only one.</para>
	/// <para>A body that tracks by the edge method is built with one structural invariant. Its entry bar is
	/// never below the holding bar, because recovering from a bad start costs
	/// more than keeping good tracking; a configured pair that inverts is
	/// corrected on the way in rather than left to produce a body that starts
	/// inside its own losing region.</para>
	/// </remarks>
	internal readonly struct QualityBand
	{
		private QualityBand(float acquire, float hold, float accept, bool corrected)
		{
			Acquire = acquire;
			Hold = hold;
			Accept = accept;
			WasCorrected = corrected;
		}

		/// <summary>The threshold tracking must clear to start or restart.</summary>
		public float Acquire { get; }

		/// <summary>The threshold below which tracking is no longer held.</summary>
		public float Hold { get; }

		/// <summary>The threshold a frame's solve must clear to be accepted.</summary>
		public float Accept { get; }

		/// <summary>
		/// Whether the configured pair inverted and the entry bar was raised to
		/// satisfy the invariant.
		/// </summary>
		/// <remarks>
		/// This describes the band the caller is holding. It is not surfaced
		/// anywhere: a band is built afresh on every read, so nothing can latch
		/// the flag from here without emitting per read. <see cref="ToString"/>
		/// includes it, and no other reader exists.
		/// </remarks>
		public bool WasCorrected { get; }

		/// <summary>
		/// Builds a band from three configured thresholds, poised so the entry bar
		/// is not below the holding bar.
		/// </summary>
		/// <remarks>
		/// The correction raises the entry bar rather than lowering the holding
		/// bar: raising it admits no frame the configured pair would have admitted,
		/// which is the direction that cannot introduce a start that was previously
		/// refused. How far above the holding bar the entry bar should sit is a
		/// measured quantity, so the constraint stops at equality and claims
		/// nothing further.
		/// </remarks>
		public static QualityBand Poise(float acquire, float hold, float accept)
		{
			bool inverts = acquire < hold;
			return new QualityBand(inverts ? hold : acquire, hold, accept, inverts);
		}

		/// <summary>
		/// Builds a band that reads the three configured thresholds exactly as they are. A body that tracks by the
		/// silhouette method starts on a lower bar than the one it is held to, and that has always been its rule, so its
		/// pair is not corrected.
		/// </summary>
		public static QualityBand Declared(float acquire, float hold, float accept)
		{
			return new QualityBand(acquire, hold, accept, false);
		}

		/// <summary>
		/// The signal a reading takes. This table is the whole of the mapping;
		/// no call site chooses its own scalar.
		/// </summary>
		public static QualitySignal SignalFor(QualityDecision decision)
		{
			switch (decision)
			{
				// Starting is a claim about this frame: a running average would let a
				// body start on evidence it no longer has.
				case QualityDecision.Acquire:
					return QualitySignal.Instant;

				// Losing is a claim about a stretch of frames, so it reads the average
				// and a single bad frame cannot make it.
				case QualityDecision.Hold:
					return QualitySignal.Averaged;

				// Accepting is a claim about this frame's solve, so it reads this
				// frame's quality.
				case QualityDecision.Accept:
					return QualitySignal.Instant;

				default:
					return QualitySignal.Instant;
			}
		}

		/// <summary>The threshold a reading is taken against.</summary>
		public float ThresholdFor(QualityDecision decision)
		{
			switch (decision)
			{
				case QualityDecision.Acquire:
					return Acquire;
				case QualityDecision.Hold:
					return Hold;
				case QualityDecision.Accept:
					return Accept;
				default:
					return Accept;
			}
		}

		/// <summary>
		/// Whether a reading of <paramref name="signalValue"/> clears its threshold.
		/// </summary>
		public bool Clears(QualityDecision decision, float signalValue)
		{
			return signalValue >= ThresholdFor(decision);
		}

		public override string ToString()
		{
			return $"acquire={Acquire:0.###} hold={Hold:0.###} accept={Accept:0.###}"
				+ (WasCorrected ? " (entry bar raised to the holding bar)" : string.Empty);
		}
	}
}
