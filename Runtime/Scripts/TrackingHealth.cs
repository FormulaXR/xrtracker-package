namespace IV.FormulaTracker
{
	/// <summary>Where the expectation a health value was measured against came from.</summary>
	internal enum ViewExpectationSource
	{
		/// <summary>No expectation covers the current view.</summary>
		None = 0,

		/// <summary>Defined by the window the body acquired through.</summary>
		ColdStartWindow = 1,

		/// <summary>Recorded when a view was admitted to the retained bank.</summary>
		SavedView = 2,

		/// <summary>Carried forward by the running average over tracked frames.</summary>
		Learned = 3,
	}

	/// <summary>Which per-frame measurement the health ratio is formed from.</summary>
	/// <remarks>
	/// Both are measurements of the same frame and both are divided by the same
	/// per-view expectation, so the choice changes what the ratio is made of and
	/// nothing about how it is used. The share counts sites and says nothing about
	/// how well each one fitted; the score carries the residual terms as well, so
	/// it can fall on a frame whose sites still agree.
	/// </remarks>
	internal enum HealthNumerator
	{
		/// <summary>Agreeing sites over expected-visible sites.</summary>
		AgreeingShare = 0,

		/// <summary>The frame's own registration-quality score.</summary>
		RegistrationScore = 1,
	}

	/// <summary>
	/// One completed frame's site-funnel ratio, the ratio this view normally
	/// achieves, and the first divided by the second.
	/// </summary>
	/// <remarks>
	/// <para>The raw ratio is agreeing sites over expected-visible sites. It is a
	/// statement about this frame and this view together: a face-on view and an
	/// oblique one reach different ratios while both are tracking perfectly well,
	/// so a single threshold across views is a threshold that is wrong for most of
	/// them. Dividing by what the view normally achieves removes the view from the
	/// number, and what is left is comparable across views, objects and
	/// distances.</para>
	/// <para>Availability is separate from the value at both levels. A frame with
	/// too few expected-visible sites has no ratio to report, and a view with no
	/// stored expectation has no divisor; neither is reported as a low health,
	/// because a low health is a statement the tracker is not in a position to
	/// make.</para>
	/// </remarks>
	internal readonly struct TrackingHealthSnapshot
	{
		private TrackingHealthSnapshot(bool rawAvailable, float raw,
			bool expectationAvailable, float expected,
			ViewExpectationSource expectationSource, bool available, float health,
			float acceptanceHealth, float appearanceGuard)
		{
			RawAvailable = rawAvailable;
			Raw = raw;
			ExpectationAvailable = expectationAvailable;
			Expected = expected;
			ExpectationSource = expectationSource;
			Available = available;
			Health = health;
			AcceptanceHealth = acceptanceHealth;
			_appearanceGuard = appearanceGuard;
		}

		private readonly float _appearanceGuard;

		/// <summary>Whether the frame carried enough expected-visible sites to form a ratio.</summary>
		public bool RawAvailable { get; }

		/// <summary>Agreeing sites over expected-visible sites, or zero when unavailable.</summary>
		public float Raw { get; }

		/// <summary>Whether an expectation covers the current view.</summary>
		public bool ExpectationAvailable { get; }

		/// <summary>The raw ratio this view normally achieves, or zero when unknown.</summary>
		public float Expected { get; }

		/// <summary>Where <see cref="Expected"/> came from.</summary>
		public ViewExpectationSource ExpectationSource { get; }

		/// <summary>Whether <see cref="Health"/> is a measurement.</summary>
		public bool Available { get; }

		/// <summary>
		/// The raw ratio as a fraction of what this view normally achieves, after
		/// the appearance guard. This is the lifecycle's value.
		/// </summary>
		public float Health { get; }

		/// <summary>
		/// The same ratio without the appearance guard. This is what decides
		/// whether a solve may become the body's pose.
		/// </summary>
		/// <remarks>
		/// The two values answer different questions and only one of them may
		/// reach the pose. Whether to keep believing a body is a question about
		/// the body, and appearance evidence belongs in it: a pose that is
		/// edge-coherent on the wrong thing should stop being believed. Whether
		/// this frame's solve is good enough to become the stored reference is a
		/// question about the solve, and a guard that is wrong on one healthy
		/// frame there does not merely mis-state the status - it withholds a good
		/// solve, leaves an older pose standing as the reference every later
		/// comparison is made against, and so degrades the tracking it was meant
		/// to describe. The guard is kept where being wrong costs a status and out
		/// of where being wrong costs the pose.
		/// </remarks>
		public float AcceptanceHealth { get; }

		/// <summary>
		/// What the appearance guard multiplied this frame's health by. One when
		/// the frame carried no appearance evidence, or when the agreement was at
		/// or above the floor.
		/// </summary>
		/// <remarks>
		/// A frame that measured nothing at all is the default snapshot, whose
		/// stored guard is zero. Reporting that as a guard would read as a total
		/// veto rather than as an absence, so it is reported as one: a frame with
		/// no ratio has nothing for a guard to have acted on.
		/// </remarks>
		public float AppearanceGuard => RawAvailable ? _appearanceGuard : 1f;

		/// <summary>Whether the appearance guard reduced this frame's health.</summary>
		public bool AppearanceVetoed => AppearanceGuard < 1f;

		/// <summary>A frame that measured nothing.</summary>
		public static TrackingHealthSnapshot Unavailable => default;

		/// <summary>A frame that produced a ratio but had no expectation to divide it by.</summary>
		public static TrackingHealthSnapshot FromRaw(float raw) =>
			new TrackingHealthSnapshot(true, raw, false, 0f,
				ViewExpectationSource.None, false, 0f, 0f, 1f);

		/// <summary>A frame with both halves of the measurement.</summary>
		public static TrackingHealthSnapshot FromRatio(float raw, float expected,
			ViewExpectationSource source, float ceiling) =>
			FromRatio(raw, expected, source, ceiling, 1f);

		/// <summary>A frame with both halves of the measurement and the guard applied.</summary>
		public static TrackingHealthSnapshot FromRatio(float raw, float expected,
			ViewExpectationSource source, float ceiling, float appearanceGuard)
		{
			if (!(expected > 0f) || float.IsNaN(expected) || float.IsInfinity(expected))
				return FromRaw(raw);
			if (float.IsNaN(appearanceGuard) || float.IsInfinity(appearanceGuard) ||
				appearanceGuard <= 0f || appearanceGuard > 1f)
			{
				appearanceGuard = 1f;
			}
			float acceptanceHealth = raw / expected;
			if (float.IsNaN(acceptanceHealth) || float.IsInfinity(acceptanceHealth))
				return FromRaw(raw);
			float health = appearanceGuard * acceptanceHealth;
			if (float.IsNaN(health) || float.IsInfinity(health))
				return FromRaw(raw);
			return new TrackingHealthSnapshot(true, raw, true, expected, source,
				true, Clamp(health, ceiling), Clamp(acceptanceHealth, ceiling),
				appearanceGuard);
		}

		private static float Clamp(float value, float ceiling)
		{
			if (value < 0f)
				return 0f;
			return value > ceiling ? ceiling : value;
		}

		/// <summary>
		/// What a frame's appearance evidence does to its health.
		/// </summary>
		/// <remarks>
		/// An edge tracker can be perfectly coherent on the wrong object: a keyboard
		/// carries as many straight boundaries as the part does, and the site funnel
		/// cannot tell them apart. What can is whether the image around the sites
		/// still looks like the object a stored view saw. Above the floor the guard
		/// says nothing, and below it health falls to no less than half, so a frame
		/// with no appearance evidence at all is never punished for it.
		/// </remarks>
		/// <param name="available">Whether the frame carried appearance evidence.</param>
		/// <param name="agreement">The agreement it carried.</param>
		/// <param name="floor">Agreement at or above which the guard is one.</param>
		public static float AppearanceGuardFor(bool available, float agreement,
			float floor)
		{
			if (!available || !(floor > 0f))
				return 1f;
			if (float.IsNaN(agreement) || float.IsInfinity(agreement))
				return 1f;
			if (agreement >= floor)
				return 1f;
			float share = agreement <= 0f ? 0f : agreement / floor;
			return 0.5f + 0.5f * share;
		}
	}
}
