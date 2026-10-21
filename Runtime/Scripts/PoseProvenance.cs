namespace IV.FormulaTracker
{
	/// <summary>
	/// Where a pose the display layer received actually came from.
	/// </summary>
	/// <remarks>
	/// This is the vocabulary of a prioritized fall-through chain: the display
	/// layer takes the highest-priority source that can answer this frame, and the
	/// tag says which one did. The order below is that priority order.
	/// <para>
	/// <b>Scaffold only.</b> Three members are declared and nothing produces them
	/// yet — no source exists behind <see cref="ShortPrediction"/>,
	/// <see cref="WorldCoast"/> or <see cref="RecoveryHypothesis"/>, and this
	/// package contains no chain, no budget and no fall-through logic. They are
	/// declared together because the rule that gives the tag its meaning is a
	/// property of the whole vocabulary: <b>only an
	/// <see cref="Optimizer"/>-provenance pose may update a stored reference.</b>
	/// A predicted or coasted pose that can re-enter the tracker as if it were
	/// evidence makes drift self-reinforcing, and that rule cannot be stated at
	/// all until every source it excludes has a name.
	/// </para>
	/// Recording the tag is off by default. See
	/// <c>TrackedBodyManager.EnablePoseProvenance</c>.
	/// </remarks>
	internal enum PoseProvenance
	{
		/// <summary>
		/// No chain source claimed this pose — including every pose produced while
		/// provenance recording is off, which is the shipped default.
		/// </summary>
		/// <remarks>
		/// The pose shown before tracking starts also carries this value. It is a
		/// real pose from a real source, but that source is the retained
		/// initialization pose, which predates this chain; naming it as one of the
		/// members below would be false, and giving it its own member is a decision
		/// for whoever owns the start gate rather than for this scaffold.
		/// </remarks>
		None = 0,

		/// <summary>The tracker's accepted solve. The only source that may update a stored reference.</summary>
		Optimizer = 1,

		/// <summary>Reserved: extrapolation from motion over a small number of frames. Nothing produces this.</summary>
		ShortPrediction = 2,

		/// <summary>Reserved: the body held fixed in the session's world frame while the camera moves. Nothing produces this.</summary>
		WorldCoast = 3,

		/// <summary>The last accepted pose, held unchanged.</summary>
		LastValid = 4,

		/// <summary>Reserved: a pose proposed by recovery and verified before use. Nothing produces this.</summary>
		RecoveryHypothesis = 5,

		/// <summary>A renderer-only recovery preview. Never assigned to the live tracked body.</summary>
		RecoveryPreview = 6,

		/// <summary>A native-owned candidate installed by an explicit confirmation transaction.</summary>
		RecoveryConfirmed = 7,
	}
}
