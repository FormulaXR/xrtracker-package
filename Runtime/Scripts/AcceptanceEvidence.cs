namespace IV.FormulaTracker
{
	/// <summary>
	/// The per-frame evidence behind one body's pose, read straight from the
	/// native status block. Emit-only: no decision in this package consumes any
	/// field, and none should until a calibration says which one and against what.
	/// </summary>
	/// <remarks>
	/// The three site counts are a funnel over a single site set, so they only
	/// mean anything read together:
	/// <c>ExpectedVisibleSites &gt;= CandidateSites &gt;= AgreeingSites</c>.
	/// Each step answers a different question. The first is a statement about
	/// visibility — how many of the model's measurement sites project inside the
	/// image at all. The second is a statement about contrast — how many of those
	/// produced a candidate when searched. The third is a statement about
	/// agreement — how many candidates survived the residual and alignment gates.
	/// The shipped quality scalar is <c>AgreeingSites</c> over the *total* site
	/// count, which collapses all three: a body that has left the frame and a body
	/// whose evidence disagrees produce the same number.
	/// </remarks>
	internal readonly struct AcceptanceEvidence
	{
		/// <summary>Whether the edge modality produced this frame's evidence.</summary>
		public bool IsAvailable { get; }

		/// <summary>Total measurement sites carried for this body this frame.</summary>
		public int TotalSites { get; }

		/// <summary>Sites projecting in front of the camera and inside the image.</summary>
		public int ExpectedVisibleSites { get; }

		/// <summary>Of those, sites whose search returned a surviving candidate.</summary>
		public int CandidateSites { get; }

		/// <summary>Of those, sites whose candidate passed the residual and alignment gates.</summary>
		public int AgreeingSites { get; }

		/// <summary>Of the candidate sites, those whose runner-up response rivals the strongest.</summary>
		public int AmbiguousSites { get; }

		/// <summary>Share of the image grid the agreeing sites occupy.</summary>
		/// <remarks>
		/// Normalized by the whole image, so a small object cannot score highly
		/// however evenly it is measured. Read <see cref="SilhouetteCoverageRatio"/>
		/// for the distribution question and this one for apparent size.
		/// </remarks>
		public float ImageCoverageRatio { get; }

		/// <summary>
		/// Share of the projected silhouette's own extent that the agreeing sites span.
		/// </summary>
		/// <remarks>
		/// Normalized by the extent the body actually occupies this frame, so it
		/// answers the distribution question directly: a fit whose agreeing sites
		/// cluster on one edge scores low even when the count is high.
		/// </remarks>
		public float SilhouetteCoverageRatio { get; }

		/// <summary>Share of the edge-orientation bins the agreeing sites occupy.</summary>
		public float OrientationCoverageRatio { get; }

		/// <summary>
		/// Larger eigenvalue of the residual cloud's covariance, in squared pixels.
		/// </summary>
		public float ResidualDispersionMajor { get; }

		/// <summary>
		/// Smaller eigenvalue of the residual cloud's covariance, in squared pixels.
		/// </summary>
		/// <remarks>
		/// A fit that is tight across the edge and loose along it separates the two
		/// eigenvalues; a count-only or average-only reading cannot see that.
		/// </remarks>
		public float ResidualDispersionMinor { get; }

		private AcceptanceEvidence(
			bool isAvailable,
			int totalSites,
			int expectedVisibleSites,
			int candidateSites,
			int agreeingSites,
			int ambiguousSites,
			float imageCoverageRatio,
			float silhouetteCoverageRatio,
			float orientationCoverageRatio,
			float residualDispersionMajor,
			float residualDispersionMinor)
		{
			IsAvailable = isAvailable;
			TotalSites = totalSites;
			ExpectedVisibleSites = expectedVisibleSites;
			CandidateSites = candidateSites;
			AgreeingSites = agreeingSites;
			AmbiguousSites = ambiguousSites;
			ImageCoverageRatio = imageCoverageRatio;
			SilhouetteCoverageRatio = silhouetteCoverageRatio;
			OrientationCoverageRatio = orientationCoverageRatio;
			ResidualDispersionMajor = residualDispersionMajor;
			ResidualDispersionMinor = residualDispersionMinor;
		}

		/// <summary>
		/// Sites that were expected to be visible and produced no candidate at all.
		/// </summary>
		/// <remarks>Derived, not transported — the two terms already cross the boundary.</remarks>
		public int NoCandidateSites =>
			ExpectedVisibleSites > CandidateSites ? ExpectedVisibleSites - CandidateSites : 0;

		/// <summary>
		/// Sites that produced a candidate which then failed the agreement gates.
		/// </summary>
		public int DisagreeingSites =>
			CandidateSites > AgreeingSites ? CandidateSites - AgreeingSites : 0;

		/// <summary>
		/// Sites carried for this body that did not project inside the image.
		/// </summary>
		public int OutOfViewSites =>
			TotalSites > ExpectedVisibleSites ? TotalSites - ExpectedVisibleSites : 0;

		/// <summary>Reads the evidence out of a native status block.</summary>
		public static AcceptanceEvidence From(FTBodyStatus status, bool edgeTrackingEnabled)
		{
			bool available = edgeTrackingEnabled && status.HasEdgeModality;
			if (!available)
				return default;

			return new AcceptanceEvidence(
				true,
				status.n_total_edge_sites,
				status.n_edge_expected_visible_sites,
				status.n_edge_candidate_sites,
				status.n_valid_edge_points,
				status.n_edge_ambiguous_sites,
				status.edge_image_coverage_ratio,
				status.edge_silhouette_coverage_ratio,
				status.edge_orientation_coverage_ratio,
				status.edge_residual_dispersion_major,
				status.edge_residual_dispersion_minor);
		}
	}

	/// <summary>
	/// Conditioning of one body's pose-solve system on the frame's last
	/// optimisation step. Emit-only: no decision in this package consumes it.
	/// </summary>
	/// <remarks>
	/// Residual quality answers "does the pose fit what we measured". This answers
	/// a different question — "did what we measured constrain the pose in every
	/// direction". A frame can fit perfectly and still leave a degree of freedom
	/// unobserved, and only the spectrum of the solve can see that.
	/// <para>
	/// Two spectra are reported. <see cref="DataConditionInverse"/> comes from the
	/// system before the regularisation diagonal is added and carries what the
	/// measurements constrain. <see cref="SolveConditionInverse"/> comes from the
	/// matrix that was actually decomposed; the regulariser bounds its smallest
	/// eigenvalue from below, so on its own it describes the regulariser as much
	/// as the view.
	/// </para>
	/// The matrix is symmetric and positive semi-definite, so its singular values
	/// are its eigenvalues and the inverse condition number is exactly the
	/// smallest over the largest.
	/// </remarks>
	internal readonly struct PoseConditioning
	{
		/// <summary>Whether a solve was assembled for this body this frame.</summary>
		public bool IsAvailable { get; }

		/// <summary>Smallest eigenvalue of the system before regularisation.</summary>
		public float DataSingularMin { get; }

		/// <summary>Largest eigenvalue of the system before regularisation.</summary>
		public float DataSingularMax { get; }

		/// <summary>Smallest over largest, before regularisation. Zero means unconstrained.</summary>
		public float DataConditionInverse { get; }

		/// <summary>Smallest eigenvalue of the system that was decomposed.</summary>
		public float SolveSingularMin { get; }

		/// <summary>Largest eigenvalue of the system that was decomposed.</summary>
		public float SolveSingularMax { get; }

		/// <summary>Smallest over largest, after regularisation.</summary>
		public float SolveConditionInverse { get; }

		private PoseConditioning(
			bool isAvailable,
			float dataSingularMin,
			float dataSingularMax,
			float dataConditionInverse,
			float solveSingularMin,
			float solveSingularMax,
			float solveConditionInverse)
		{
			IsAvailable = isAvailable;
			DataSingularMin = dataSingularMin;
			DataSingularMax = dataSingularMax;
			DataConditionInverse = dataConditionInverse;
			SolveSingularMin = solveSingularMin;
			SolveSingularMax = solveSingularMax;
			SolveConditionInverse = solveConditionInverse;
		}

		/// <summary>Reads the conditioning out of a native status block.</summary>
		public static PoseConditioning From(FTBodyStatus status)
		{
			if (status.pose_conditioning_valid == 0)
				return default;

			return new PoseConditioning(
				true,
				status.pose_data_singular_min,
				status.pose_data_singular_max,
				status.pose_data_condition_inverse,
				status.pose_solve_singular_min,
				status.pose_solve_singular_max,
				status.pose_solve_condition_inverse);
		}
	}
}
